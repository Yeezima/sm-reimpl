using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>
/// An author-supplied image a material uses (owner-approved brief 2026-11-25 "Textures and shader nodes"). Content-addressed: the id is the SHA-256 of the file's bytes (64 lowercase hex), so
/// the map's hash covers the picture itself, two maps share one file, and a copy is verified when it is read. The map lists the images it uses (game-visible, in the hash); the bytes live
/// beside the map (<c>images/&lt;id&gt;.&lt;format&gt;</c> next to the <c>.smmap</c>, written by Save) and in the library (<c>library/images/</c>), never inside the <c>.smmap</c> (its 8 MB limit).
/// A shared map is the <c>.smmap</c> plus its <c>images</c> folder, or one <c>.smpack</c> (<see cref="MapPack"/>). The server never reads the bytes.
/// </summary>
public sealed record MapImage(string Id, string Name, string Format, int Width, int Height, long Bytes)
{
    public const long MaxBytes = 32_000_000;
    public const int MaxSide = 8192, MaxImages = 64;

    public string FileName => Id + "." + Format;

    public static bool IsImageId(string? id) => id is { Length: 64 } && id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    public JsonObject ToNode() => new() { ["id"] = Id, ["name"] = Name, ["format"] = Format, ["width"] = Width, ["height"] = Height, ["bytes"] = Bytes };

    public static MapImage FromNode(JsonNode? n, string what, string code = "bad-file")
    {
        var o = Json.Obj(n, what, code);
        var id = Json.ReqStr(o, "id", what, code);
        if (!IsImageId(id)) throw new MapException(code, $"{what}: '{id}' is not an image id (the SHA-256 of the file, 64 lowercase hex).");
        var format = Json.ReqStr(o, "format", what, code);
        if (format is not ("png" or "jpg")) throw new MapException(code, $"{what}.format must be \"png\" or \"jpg\".");
        var name = Json.Str(o["name"], what + ".name", code) ?? "";
        if (name.Length > MapDocument.MaxNameLength) throw new MapException("too-big", $"{what}: the name is too long.");
        int w = Json.Int(o["width"], what + ".width", code), h = Json.Int(o["height"], what + ".height", code);
        long bytes = o["bytes"] is JsonValue bv && bv.TryGetValue<long>(out var b) ? b : throw new MapException(code, $"{what}.bytes must be an integer.");
        if (w < 1 || h < 1 || w > MaxSide || h > MaxSide || bytes < 1 || bytes > MaxBytes) throw new MapException(code, $"{what}: size out of range.");
        return new MapImage(id, name, format, w, h, bytes);
    }

    /// <summary>
    /// Reads an image file's identity without decoding it: PNG or JPEG by its signature, the size from its header (PNG IHDR, JPEG SOF). Limits: <see cref="MaxBytes"/> and
    /// <see cref="MaxSide"/> pixels. <c>bad-image</c> otherwise.
    /// </summary>
    public static MapImage Inspect(byte[] data, string name)
    {
        if (data.Length == 0 || data.LongLength > MaxBytes) throw new MapException("bad-image", $"'{name}': an image must be between 1 byte and {MaxBytes / 1_000_000} MB.");
        string id = Convert.ToHexStringLower(SHA256.HashData(data));
        (string format, int w, int h) = Header(data, name);
        if (w < 1 || h < 1 || w > MaxSide || h > MaxSide) throw new MapException("bad-image", $"'{name}': {w} x {h} pixels is outside 1..{MaxSide}.");
        return new MapImage(id, CleanName(name), format, w, h, data.LongLength);
    }

    static string CleanName(string name)
    {
        var s = Path.GetFileNameWithoutExtension(name).Trim();
        s = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return s.Length > 80 ? s[..80] : s;
    }

    static (string, int, int) Header(byte[] d, string name)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (d.Length >= 24 && d.AsSpan(0, 8).SequenceEqual(png) && d.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            return ("png", (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(16))), (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(20))));
        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                byte marker = d[i + 1];
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
                int len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 2));
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                    return ("jpg", BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 7)), BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 5)));
                if (len < 2) break;
                i += 2 + len;
            }
        }
        throw new MapException("bad-image", $"'{name}' is not a PNG or JPEG image the Mapmaker can read.");
    }
}

/// <summary>Where a map's images are found and how they are carried (docs/mapmaker.md "Materials and images").</summary>
public static class MapImages
{
    /// <summary>The folder beside a map file that holds its images.</summary>
    public static string BesideDir(string mapPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(mapPath))!, "images");

    public static string LibraryDir(MapLibrary lib) => Path.Combine(lib.Root, "library", "images");

    /// <summary>The folders searched for an image, in order: beside the map (when the map has a path), then the library.</summary>
    public static IEnumerable<string> SearchDirs(string? mapPath, MapLibrary lib)
    {
        if (mapPath is not null) yield return BesideDir(mapPath);
        yield return LibraryDir(lib);
    }

    /// <summary>The image's bytes from the first folder that holds a copy whose SHA-256 is its id (a damaged or different copy is skipped), or null.</summary>
    public static byte[]? Read(MapImage image, string? mapPath, MapLibrary lib)
    {
        foreach (var dir in SearchDirs(mapPath, lib))
        {
            var f = Path.Combine(dir, image.FileName);
            try
            {
                if (!File.Exists(f) || new FileInfo(f).Length > MapImage.MaxBytes) continue;
                var data = File.ReadAllBytes(f);
                if (Convert.ToHexStringLower(SHA256.HashData(data)) == image.Id) return data;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>The images of the document no search folder holds (a client lacking one refuses the map plainly: <c>missing-image</c>).</summary>
    public static IReadOnlyList<MapImage> Missing(MapDocument doc, string? mapPath, MapLibrary lib) => doc.Images.Where(i => Read(i, mapPath, lib) is null).ToList();

    /// <summary>Writes <paramref name="data"/> as <paramref name="image"/> into <paramref name="dir"/> unless a verified copy is there. Returns true when it wrote one.</summary>
    public static bool Store(string dir, MapImage image, byte[] data)
    {
        var f = Path.Combine(dir, image.FileName);
        if (File.Exists(f) && new FileInfo(f).Length == data.LongLength && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))) == image.Id) return false;
        Directory.CreateDirectory(dir);
        MapLibrary.WriteAtomic(f, data);
        return true;
    }

    /// <summary>
    /// Copies every image the document uses beside <paramref name="mapPath"/> (from the library or an earlier folder), so the map file and its <c>images</c> folder are the whole map.
    /// Returns the ids it could not find anywhere (Save warns; play would refuse).
    /// </summary>
    public static IReadOnlyList<string> CarryBeside(MapDocument doc, string mapPath, MapLibrary lib, string? previousPath = null)
    {
        var missing = new List<string>();
        var dir = BesideDir(mapPath);
        foreach (var img in doc.Images)
        {
            var data = Read(img, mapPath, lib) ?? (previousPath is not null ? Read(img, previousPath, lib) : null);
            if (data is null) { missing.Add(img.Id); continue; }
            Store(dir, img, data);
        }
        return missing;
    }
}

/// <summary>
/// A shared map in one file (<c>.smpack</c>, a zip): <c>&lt;name&gt;.smmap</c> at the root and <c>images/&lt;id&gt;.&lt;png|jpg&gt;</c> for every image it uses. Unpacking accepts nothing else:
/// no folders other than <c>images/</c>, no <c>..</c> or rooted names, image names that are their own SHA-256 (checked), one map, size limits counted while reading.
/// </summary>
public static class MapPack
{
    public const string Extension = ".smpack";
    public const long MaxUnpacked = 1_000_000_000;

    /// <summary>Packs the map file (and its images, from beside it or the library) into <paramref name="outPath"/>. <c>missing-image</c> when an image is nowhere.</summary>
    public static void Pack(string mapPath, string outPath, MapLibrary lib)
    {
        var text = File.ReadAllText(mapPath);
        var doc = MapDocument.Parse(text);
        var tmp = outPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry(Path.GetFileName(mapPath), CompressionLevel.Optimal);
                using (var w = e.Open()) w.Write(new System.Text.UTF8Encoding(false).GetBytes(text));
                foreach (var img in doc.Images)
                {
                    var data = MapImages.Read(img, mapPath, lib) ?? throw new MapException("missing-image", $"The image '{img.Name}' ({img.Id[..12]}...) is neither beside the map nor in the library.");
                    var ie = zip.CreateEntry("images/" + img.FileName, CompressionLevel.NoCompression);
                    using var iw = ie.Open();
                    iw.Write(data);
                }
            }
            File.Move(tmp, outPath, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    /// <summary>Unpacks a pack into <paramref name="intoDir"/> (the map file and <c>images/</c>); returns the map's path. An existing map file of that name is refused (<c>file-exists</c>).</summary>
    public static string Unpack(string packPath, string intoDir)
    {
        using var zip = ZipFile.OpenRead(packPath);
        if (zip.Entries.Count > MapImage.MaxImages + 1) throw new MapException("bad-pack", "The pack holds more files than a map can use.");
        string? mapEntry = null;
        long total = 0;
        var files = new List<(string Rel, byte[] Data)>();
        foreach (var e in zip.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;   // a folder entry
            bool isMap = !name.Contains('/') && name.EndsWith(".smmap", StringComparison.OrdinalIgnoreCase) && name.Length > 6 && !name.Contains("..") && Path.GetFileName(name) == name;
            bool isImage = name.StartsWith("images/", StringComparison.Ordinal) && name.Count(c => c == '/') == 1
                && Path.GetFileNameWithoutExtension(name[7..]) is var id && MapImage.IsImageId(id) && Path.GetExtension(name) is ".png" or ".jpg";
            if (!isMap && !isImage) throw new MapException("bad-pack", $"The pack holds '{name}', which is not its map or one of its images.");
            if (isMap && mapEntry is not null) throw new MapException("bad-pack", "The pack holds more than one map.");
            long limit = isMap ? CustomMaps.MaxFileBytes : MapImage.MaxBytes;
            using var s = e.Open();
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                ms.Write(buf, 0, n);
                total += n;
                if (ms.Length > limit || total > MaxUnpacked) throw new MapException("too-big", $"'{name}' in the pack is larger than allowed.");
            }
            var data = ms.ToArray();
            if (isImage)
            {
                var info = MapImage.Inspect(data, name);
                if (info.FileName != name[7..]) throw new MapException("bad-pack", $"'{name}' is not the image its name says (its SHA-256 or format differs).");
            }
            else mapEntry = name;
            files.Add((name, data));
        }
        if (mapEntry is null) throw new MapException("bad-pack", "The pack holds no .smmap map.");
        var mapBytes = files.First(f => f.Rel == mapEntry).Data;
        var doc = MapDocument.Parse(new System.Text.UTF8Encoding(false).GetString(mapBytes));   // refuse a bad map before anything is written
        foreach (var img in doc.Images)
            if (files.All(f => f.Rel != "images/" + img.FileName)) throw new MapException("missing-image", $"The pack lacks the image '{img.Name}' that its map uses.");
        var target = Path.Combine(Path.GetFullPath(intoDir), mapEntry);
        if (File.Exists(target)) throw new MapException("file-exists", $"'{target}' already exists; unpack into another folder or remove it first.");
        Directory.CreateDirectory(intoDir);
        foreach (var (rel, data) in files.Where(f => f.Rel != mapEntry))
        {
            var info = MapImage.Inspect(data, rel);
            MapImages.Store(Path.Combine(Path.GetFullPath(intoDir), "images"), info, data);
        }
        MapLibrary.WriteAtomic(target, mapBytes);
        return target;
    }
}
