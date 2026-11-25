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
    /// <summary>Decoded-pixel budget over all of a map's images (about 256 MB of RGBA before mipmaps): a shared map cannot make every client decode gigabytes.</summary>
    public const long MaxTotalPixels = 64L * 1024 * 1024;

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
                if (Convert.ToHexStringLower(SHA256.HashData(data)) != image.Id) continue;
                var info = MapImage.Inspect(data, image.FileName);   // the same bytes, but the list in the map must also say what they are
                if (info.Format == image.Format && info.Width == image.Width && info.Height == image.Height) return data;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            catch (MapException) { }   // a copy that is not a readable image is skipped like a damaged one
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
    public static IReadOnlyList<string> CarryBeside(MapDocument doc, string mapPath, MapLibrary lib, string? previousPath = null, List<string>? failures = null)
    {
        var missing = new List<string>();
        var dir = BesideDir(mapPath);
        foreach (var img in doc.Images)
        {
            var data = Read(img, mapPath, lib) ?? (previousPath is not null ? Read(img, previousPath, lib) : null);
            if (data is null) { missing.Add(img.Id); continue; }
            // one picture that cannot be copied (a full disk, a locked file) is reported, never the reason the map itself is not saved
            try { Store(dir, img, data); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failures?.Add($"The image '{img.Name}' could not be copied beside the map: {e.Message}"); }
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

    /// <summary>Packs the map file (and its images, from beside it or the library) into <paramref name="outPath"/>. <c>missing-image</c> when an image is nowhere.</summary>
    public static void Pack(string mapPath, string outPath, MapLibrary lib)
    {
        var text = File.ReadAllText(mapPath);
        var entryName = EntryName(Path.GetFileName(mapPath));   // the name Unpack accepts (a file called "Nick's arena #1.smmap" packs as "Nick_s arena _1.smmap")
        var doc = MapDocument.Parse(text);
        var tmp = outPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry(entryName, CompressionLevel.Optimal);
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

    /// <summary>
    /// Unpacks a pack into <paramref name="intoDir"/> (the map file and <c>images/</c>); returns the map's path. Checked before anything is written: one map entry whose name is plain (letters,
    /// digits, space, <c>_ . - ( )</c>, no path, no <c>:</c>), every other entry <c>images/&lt;sha256&gt;.png|jpg</c> (no backslash, no deeper folder, no repeat), the map readable, every image it
    /// lists present, no map file of that name in the folder (<c>file-exists</c>). Then each listed image is read alone (one in memory at a time, its size counted as it is read, its SHA-256,
    /// format and size checked against the map's list) and stored; images the map does not list are skipped; the map file is written last, atomically, so a failed unpack never leaves a map
    /// that names a missing picture. <c>bad-pack</c> for anything else (a file that is not a zip too).
    /// </summary>
    public static string Unpack(string packPath, string intoDir)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(packPath); }
        catch (InvalidDataException e) { throw new MapException("bad-pack", $"'{Path.GetFileName(packPath)}' is not a .smpack (zip) file: {e.Message}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { throw new MapException("bad-pack", $"Cannot read the pack: {e.Message}"); }
        using (zip)
        {
            try { return UnpackFrom(zip, intoDir); }
            catch (InvalidDataException e) { throw new MapException("bad-pack", $"The pack is damaged: {e.Message}"); }
        }
    }

    static readonly string[] DeviceNames = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>The map entry name rule (Pack writes only such names, Unpack accepts only such names): letters, digits, space, <c>_ . - ( )</c>, ending <c>.smmap</c>, at most 120 characters, not a Windows device name.</summary>
    public static bool IsPlainMapName(string name) =>
        name.Length is > 6 and <= 120 && name.EndsWith(".smmap", StringComparison.OrdinalIgnoreCase) && !name.StartsWith('.') && !name.StartsWith(' ') && !name.Contains("..")
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '.' or '-' or '(' or ')')
        && !DeviceNames.Contains(name.Split('.')[0].TrimEnd(' '), StringComparer.OrdinalIgnoreCase);

    /// <summary>A map file name made plain for a pack: other characters become '_', a device name or an empty stem gets "map", long names are cut.</summary>
    public static string EntryName(string fileName)
    {
        var stem = fileName.EndsWith(".smmap", StringComparison.OrdinalIgnoreCase) ? fileName[..^6] : fileName;
        var clean = new string(stem.Select(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-' or '(' or ')' ? c : '_').ToArray()).Trim(' ');
        if (clean.Length > 100) clean = clean[..100].TrimEnd(' ');
        if (clean.Length == 0 || DeviceNames.Contains(clean, StringComparer.OrdinalIgnoreCase)) clean += "map";
        var name = clean + ".smmap";
        return IsPlainMapName(name) ? name : "map.smmap";
    }

    /// <summary>The most bytes one unpack reads in all (map and images).</summary>
    public const long MaxUnpacked = 1_000_000_000;

    static string UnpackFrom(ZipArchive zip, string intoDir)
    {
        if (zip.Entries.Count > 4 * MapImage.MaxImages + 1) throw new MapException("bad-pack", "The pack holds more files than a map can use.");
        ZipArchiveEntry? mapEntry = null;
        var images = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var e in zip.Entries)
        {
            var name = e.FullName;
            if (name.EndsWith('/') && e.Length == 0 && name == "images/") continue;   // the folder entry itself
            if (name.Contains('\\')) throw new MapException("bad-pack", $"The pack holds '{name}', a name with a backslash.");
            bool isImage = name.StartsWith("images/", StringComparison.Ordinal) && name.Count(c => c == '/') == 1
                && Path.GetFileNameWithoutExtension(name[7..]) is var id && MapImage.IsImageId(id) && Path.GetExtension(name) is ".png" or ".jpg";
            if (isImage) { if (!images.TryAdd(name[7..], e)) throw new MapException("bad-pack", $"The pack holds '{name}' twice."); continue; }
            if (!IsPlainMapName(name)) throw new MapException("bad-pack", $"The pack holds '{name}', which is not its map or one of its images.");
            if (mapEntry is not null) throw new MapException("bad-pack", "The pack holds more than one map.");
            mapEntry = e;
        }
        if (mapEntry is null) throw new MapException("bad-pack", "The pack holds no .smmap map.");
        var mapBytes = ReadEntry(mapEntry, CustomMaps.MaxFileBytes);
        var doc = MapDocument.Parse(new System.Text.UTF8Encoding(false).GetString(mapBytes));   // refuse a bad map before anything is written
        foreach (var img in doc.Images)
            if (!images.ContainsKey(img.FileName)) throw new MapException("missing-image", $"The pack lacks the image '{img.Name}' that its map uses.");
        var dir = Path.GetFullPath(intoDir);
        var target = Path.Combine(dir, mapEntry.FullName);
        if (File.Exists(target)) throw new MapException("file-exists", $"'{target}' already exists; unpack into another folder or remove it first.");
        long total = mapBytes.LongLength + doc.Images.Sum(i => Math.Max(images[i.FileName].Length, 0));
        if (total > MaxUnpacked) throw new MapException("too-big", $"The pack holds more than {MaxUnpacked / 1_000_000} MB.");
        var written = new List<string>();   // images this unpack added: removed again if it fails, so a refused pack leaves nothing behind
        bool dirExisted = Directory.Exists(dir), imagesExisted = Directory.Exists(Path.Combine(dir, "images"));
        try
        {
            foreach (var img in doc.Images)   // only the images the map lists; one in memory at a time
            {
                var data = ReadEntry(images[img.FileName], MapImage.MaxBytes);
                var info = MapImage.Inspect(data, img.FileName);
                if (info.FileName != img.FileName || info.Width != img.Width || info.Height != img.Height)
                    throw new MapException("bad-pack", $"'images/{img.FileName}' is not the image the map lists (its SHA-256, format or size differs).");
                if (MapImages.Store(Path.Combine(dir, "images"), img, data)) written.Add(Path.Combine(dir, "images", img.FileName));
            }
            MapLibrary.WriteAtomic(target, mapBytes);
        }
        catch (Exception failure)
        {
            foreach (var f in written) try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            // folders this unpack created go too, when they are empty
            try
            {
                var imgDir = Path.Combine(dir, "images");
                if (!imagesExisted && Directory.Exists(imgDir) && !Directory.EnumerateFileSystemEntries(imgDir).Any()) Directory.Delete(imgDir);
                if (!dirExisted && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            // a disk or permission problem is a plain refusal like every other (callers handle MapException)
            if (failure is IOException or UnauthorizedAccessException) throw new MapException("bad-pack", $"The pack could not be unpacked into '{dir}': {failure.Message}");
            throw;
        }
        return target;
    }

    /// <summary>An entry's bytes, read into one buffer and counted as read (an entry's declared size can lie): <c>too-big</c> past <paramref name="limit"/>.</summary>
    static byte[] ReadEntry(ZipArchiveEntry e, long limit)
    {
        if (e.Length > limit) throw new MapException("too-big", $"'{e.FullName}' in the pack is larger than allowed.");
        var buf = new byte[e.Length];
        using var s = e.Open();
        int got = 0, n;
        while (got < buf.Length && (n = s.Read(buf, got, buf.Length - got)) > 0) got += n;
        if (got != buf.Length || s.ReadByte() >= 0) throw new MapException("bad-pack", $"'{e.FullName}' in the pack is not the size it declares.");
        return buf;
    }
}
