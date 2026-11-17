using System.IO.Compression;
using System.Numerics;
using System.Text.RegularExpressions;

namespace SmMapmaker;

/// <summary>Which file of a download the importer opens, and which LODs it left out.</summary>
public sealed record ModelChoice(string Path, string Format, int Lod, IReadOnlyList<string> SkippedLods);

/// <summary>
/// The pure (no Godot) half of the Quixel / Fab import: what a downloaded archive or folder holds. The Godot half (loading FBX and glTF, building
/// materials, writing the .glb) is the editor plugin's AssetImporter; docs/mapmaker.md "Custom assets".
/// </summary>
public static class ModelImport
{
    public static readonly string[] ModelExtensions = [".glb", ".gltf", ".fbx"];
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".tga", ".webp", ".bmp"];
    static readonly string[] CompanionExtensions = [".bin"];

    /// <summary>The import's limits against a hostile or accidentally huge archive (a zip bomb stops at the byte count, not at the headers' word).</summary>
    public sealed record Limits(int MaxEntries = 20_000, long MaxTotalBytes = 2L << 30, long MaxEntryBytes = 1L << 30);

    public static bool IsModel(string path) => ModelExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    static bool Wanted(string path) => IsModel(path) || IsImage(path) || CompanionExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // ---- archives ----

    /// <summary>
    /// A zip entry name as a safe relative path, or null for a directory entry. Backslashes count as separators; a rooted path, a drive or stream
    /// colon, a NUL or any ".." segment is a zip-slip attempt and throws <c>bad-archive</c>.
    /// </summary>
    public static string? SafeEntryPath(string entryName)
    {
        string name = entryName.Replace('\\', '/');
        if (name.Length == 0 || name.EndsWith('/')) return null;
        if (name.StartsWith('/') || name.Contains(':') || name.Contains('\0'))
            throw new MapException("bad-archive", $"The archive has an entry '{entryName}' that is not a plain relative path.");
        var segments = name.Split('/');
        if (segments.Any(s => s is ".." or "" ))
            throw new MapException("bad-archive", $"The archive has an entry '{entryName}' that climbs out of its folder.");
        return string.Join('/', segments.Where(s => s != "."));
    }

    /// <summary>
    /// Unpacks the model, image and .bin files of <paramref name="zipPath"/> into the (new or empty) folder <paramref name="destination"/> and returns their
    /// relative paths. Anything else in the archive is left unread. Throws <c>bad-archive</c> for zip-slip names, too many entries or too many bytes.
    /// </summary>
    public static IReadOnlyList<string> ExtractZip(string zipPath, string destination, Limits? limits = null)
    {
        limits ??= new Limits();
        if (!File.Exists(zipPath)) throw new MapException("bad-file", $"No file '{zipPath}'.");
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var written = new List<string>();
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count > limits.MaxEntries) throw new MapException("bad-archive", $"The archive has {zip.Entries.Count} entries (limit {limits.MaxEntries}).");
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (SafeEntryPath(entry.FullName) is not { } rel) continue;
                if (!Wanted(rel)) continue;
                string target = Path.GetFullPath(Path.Combine(destination, rel));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new MapException("bad-archive", $"The archive entry '{entry.FullName}' would be written outside the import folder.");
                if (entry.Length > limits.MaxEntryBytes) throw new MapException("bad-archive", $"'{entry.FullName}' is larger than {limits.MaxEntryBytes >> 20} MB when unpacked.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = File.Create(target);
                var buffer = new byte[81920];
                long entryBytes = 0;
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    entryBytes += n; total += n;
                    if (entryBytes > limits.MaxEntryBytes || total > limits.MaxTotalBytes)
                        throw new MapException("bad-archive", $"The archive unpacks to more than {limits.MaxTotalBytes >> 20} MB; not importing it.");
                    output.Write(buffer, 0, n);
                }
                written.Add(rel);
            }
        }
        catch (InvalidDataException e) { throw new MapException("bad-archive", $"'{Path.GetFileName(zipPath)}' is not a readable zip archive: {e.Message}"); }
        return written;
    }

    // ---- choosing the model ----

    static readonly Regex LodPattern = new(@"(?:^|[^a-z0-9])lod[ _\-.]?(\d+)(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The LOD number a file name states (<c>Rock_LOD0</c>, <c>rock.lod_2</c>, <c>LOD3</c>), or null when it states none.</summary>
    public static int? LodOf(string fileNameOrPath) =>
        LodPattern.Match(Path.GetFileNameWithoutExtension(fileNameOrPath)) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : null;

    /// <summary>
    /// The model file to import from a list of relative paths: the lowest LOD first (a name without a LOD counts as LOD 0), then glb over gltf over
    /// fbx (Godot's glTF reader is the most faithful), then the shallowest and alphabetically first path. Variation folders therefore resolve to the
    /// first one. Null when the list holds no model.
    /// </summary>
    public static ModelChoice? PickModel(IEnumerable<string> relativePaths)
    {
        var models = relativePaths.Select(p => p.Replace('\\', '/')).Where(IsModel)
            .Where(p => !p.Split('/').Any(s => s.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase) || s.StartsWith("._"))).ToList();
        if (models.Count == 0) return null;
        static int Rank(string p) => Path.GetExtension(p).ToLowerInvariant() switch { ".glb" => 0, ".gltf" => 1, _ => 2 };
        var ordered = models.OrderBy(p => LodOf(p) ?? 0).ThenBy(Rank).ThenBy(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal).ToList();
        var best = ordered[0];
        int lod = LodOf(best) ?? 0;
        // The LODs left out: models of the same format and stem family with a higher number.
        var skipped = ordered.Skip(1).Where(p => (LodOf(p) ?? 0) > lod).ToList();
        return new ModelChoice(best, Path.GetExtension(best).TrimStart('.').ToLowerInvariant(), lod, skipped);
    }

    /// <summary>All the relative paths of the files an import cares about under <paramref name="folder"/> (models, images, .bin), skipping links.</summary>
    public static IReadOnlyList<string> ListFolder(string folder, int maxFiles = 20_000)
    {
        string root = Path.GetFullPath(folder);
        var list = new List<string>();
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (list.Count >= maxFiles) throw new MapException("bad-archive", $"The folder has more than {maxFiles} files; not importing it.");
            if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) continue;
            string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (Wanted(rel)) list.Add(rel);
        }
        return list;
    }

    /// <summary>SHA-256 of the main file followed by each companion, the way the library's import hashes an asset (so the caller can see "already imported").</summary>
    public static string ContentSha(string path) => Batch.HashOf(path, GltfReader.Inspect(path));

    // ---- textures ----

    public enum TextureRole { Unknown, Albedo, Normal, Roughness, Metallic, Occlusion, Orm, Opacity, Emissive, Displacement }

    public enum NormalConvention { Unknown, OpenGL, DirectX }

    /// <summary>One texture file: what it is for, which material set it belongs to, the resolution its name states (0 when none) and, for a normal map, its convention.</summary>
    public sealed record TextureInfo(string File, TextureRole Role, string Set, int Resolution, NormalConvention Convention);

    /// <summary>
    /// A normal map with no convention in its name: Megascans names (<c>id_4K_Normal</c>) are read as DirectX, the owner's note of 2026-11-17 ("Megascans
    /// normals are often DX"), so their green channel is flipped; any other plain "Normal" is taken as OpenGL, which is what Godot expects. Ours; a refine item.
    /// </summary>
    public static bool UnknownNormalIsDirectX(TextureInfo t) => t.Convention == NormalConvention.Unknown && t.Resolution > 0;

    public static bool NormalNeedsGreenFlip(TextureInfo t) => t.Convention == NormalConvention.DirectX || UnknownNormalIsDirectX(t);

    static readonly Regex Resolution = new(@"^(\d{1,2})k$|^(512|1024|2048|4096|8192)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static int ResolutionOf(string token)
    {
        var m = Resolution.Match(token);
        if (!m.Success) return 0;
        if (m.Groups[1].Success) return int.Parse(m.Groups[1].Value) * 1024;
        return int.Parse(m.Groups[2].Value);
    }

    /// <summary>Reads Megascans, Fab / Unreal ("T_Rock_BC", "_N", "_ORM") and Blender-style ("BaseColor", "Roughness") texture names. Null for a file that is no image.</summary>
    public static TextureInfo? ClassifyTexture(string path)
    {
        if (!IsImage(path)) return null;
        string stem = Path.GetFileNameWithoutExtension(path);
        var tokens = Regex.Split(Regex.Replace(Regex.Replace(stem, "directx|d3d", "_dx", RegexOptions.IgnoreCase), @"(?<=[a-z])(?=[A-Z])", "_"), @"[_\-. ]+").Where(t => t.Length > 0).ToList();
        int role = -1, res = 0;
        TextureRole found = TextureRole.Unknown;
        var convention = NormalConvention.Unknown;
        for (int i = 0; i < tokens.Count; i++)
        {
            string t = tokens[i].ToLowerInvariant();
            if (ResolutionOf(t) is > 0 and var r) { res = Math.Max(res, r); if (role < 0) continue; }
            TextureRole? hit = t switch
            {
                "albedo" or "basecolor" or "diffuse" or "diff" or "color" or "colour" or "col" or "bc" or "bco" => TextureRole.Albedo,
                "base" when i + 1 < tokens.Count && tokens[i + 1].Equals("color", StringComparison.OrdinalIgnoreCase) => TextureRole.Albedo,
                "normal" or "normals" or "nrm" or "norm" or "nor" or "nm" => TextureRole.Normal,
                "n" when i == tokens.Count - 1 || tokens.Skip(i + 1).All(x => x.Length <= 7 && (x is "dx" or "gl" or "opengl" or "directx")) => TextureRole.Normal,
                "roughness" or "rough" or "rgh" or "r" when i == tokens.Count - 1 || t != "r" => TextureRole.Roughness,
                "metallic" or "metalness" or "metal" or "mtl" or "m" when i == tokens.Count - 1 || t != "m" => TextureRole.Metallic,
                "ao" or "occlusion" or "ambientocclusion" or "ambient" when t != "ambient" || (i + 1 < tokens.Count && tokens[i + 1].Equals("occlusion", StringComparison.OrdinalIgnoreCase)) => TextureRole.Occlusion,
                "orm" or "arm" or "rma" => TextureRole.Orm,
                "opacity" or "alpha" or "mask" or "transparency" => TextureRole.Opacity,
                "emissive" or "emission" or "emit" => TextureRole.Emissive,
                "displacement" or "disp" or "height" or "bump" or "heightmap" => TextureRole.Displacement,
                _ => null,
            };
            if (hit is { } h && found == TextureRole.Unknown) { found = h; role = i; }
            else if (t is "dx" or "directx" or "d3d") convention = NormalConvention.DirectX;
            else if (t is "gl" or "opengl" or "ogl") convention = NormalConvention.OpenGL;
        }
        if (found == TextureRole.Unknown) return new TextureInfo(path, TextureRole.Unknown, "", res, NormalConvention.Unknown);
        // The set: the name's tokens before the role word, minus the resolution and a T_ prefix.
        string set = string.Join("_", tokens.Take(role).Where(t => ResolutionOf(t) == 0 && !t.Equals("T", StringComparison.OrdinalIgnoreCase))).ToLowerInvariant();
        if (found != TextureRole.Normal) convention = NormalConvention.Unknown;
        return new TextureInfo(path, found, set, res, convention);
    }

    /// <summary>The textures of a download grouped into material sets (by the name before the role word); per role the highest resolution wins. Displacement is dropped.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<TextureRole, TextureInfo>> GroupTextures(IEnumerable<string> files)
    {
        var sets = new SortedDictionary<string, Dictionary<TextureRole, TextureInfo>>(StringComparer.Ordinal);
        foreach (var info in files.Select(ClassifyTexture).OfType<TextureInfo>().Where(t => t.Role is not (TextureRole.Unknown or TextureRole.Displacement)))
        {
            if (!sets.TryGetValue(info.Set, out var roles)) sets[info.Set] = roles = new();
            if (!roles.TryGetValue(info.Role, out var have) || info.Resolution > have.Resolution
                || (info.Resolution == have.Resolution && string.CompareOrdinal(info.File, have.File) < 0)) roles[info.Role] = info;
        }
        return sets.Values.Where(r => r.Count > 0).Select(r => (IReadOnlyDictionary<TextureRole, TextureInfo>)r).ToList();
    }

    // ---- units ----

    /// <summary>The scale to apply to a model whose largest side is <paramref name="largestSide"/> (as loaded): 1 when it already reads as metres.</summary>
    public sealed record UnitGuess(float Scale, string Why);

    /// <summary>
    /// Map props are about 5 cm to 30 m. A larger model is read as centimetres (Megascans FBX; x0.01) and, when that still leaves it too big, as millimetres
    /// (x0.001). A model that is too small stays (we cannot know). Ours; the scale is stored in asset.json.
    /// </summary>
    public static UnitGuess GuessUnits(float largestSide)
    {
        const float MaxPlausible = 30f, MinPlausible = 0.05f;
        if (!float.IsFinite(largestSide) || largestSide <= MaxPlausible) return new UnitGuess(1f, "metres");
        foreach (var (scale, name) in new[] { (0.01f, "centimetres"), (0.001f, "millimetres") })
            if (largestSide * scale <= MaxPlausible && largestSide * scale >= MinPlausible) return new UnitGuess(scale, name);
        return new UnitGuess(0.001f, "millimetres");
    }

    /// <summary>The largest side of a box.</summary>
    public static float LargestSide(Vector3 size) => Math.Max(size.X, Math.Max(size.Y, size.Z));
}
