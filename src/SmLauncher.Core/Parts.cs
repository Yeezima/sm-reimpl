using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SmLauncher.Core;

/// <summary>
/// A playtest build split into parts (tools/release/release.ps1 with smtool release-parts; docs/releasing.md, "Parts"). The build's folder (the same one
/// the playtest zip holds) is cut into a few zips by path (<see cref="PartRules"/>): the Godot engine, the .NET runtime, the game's own assemblies and
/// pack, the Ashfall kit, the launcher, the source offer and the small notes. Each part zip is named after its content fingerprint, so an unchanged part
/// has the same name in every build and is uploaded once, to the releases repository's stable <see cref="PartsManifest.DefaultPartsTag"/> release. A
/// build's release carries only its manifest (<c>SM-Reimpl-Playtest-&lt;date&gt;-&lt;version&gt;-Windows.parts.json</c>): every file with its SHA-256,
/// size and part, and every part with its zip's SHA-256 and size. The launcher downloads only the parts whose files it does not have already.
/// </summary>
public sealed class PartsManifest
{
    public const string FormatId = "sm-reimpl-parts/1";
    /// <summary>The releases repository's release that holds every part zip (published, never hidden: its assets must stay downloadable).</summary>
    public const string DefaultPartsTag = "parts";
    public const string FileSuffix = ".parts.json";
    public const int MaxManifestBytes = 4 << 20;

    public string Format { get; set; } = FormatId;
    public string Tag { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Commit { get; set; }
    public string? Built { get; set; }
    public string PartsTag { get; set; } = DefaultPartsTag;
    public List<PartInfo> Parts { get; set; } = [];
    public List<PartFile> Files { get; set; } = [];

    /// <summary>The unpacked size of the whole build.</summary>
    public long TotalBytes => Files.Sum(f => f.Size);

    public PartInfo? Part(string name) => Parts.FirstOrDefault(p => p.Name == name);

    public string ToJson() => JsonSerializer.Serialize(this, PartsJson.Default.PartsManifest);

    /// <summary>
    /// Reads and checks a manifest. Everything a launcher later turns into a path, a name or a URL is checked here: the format, the tags, every part's
    /// name, asset name, SHA-256 and size, every file's relative path (<see cref="PackageLayout.SafeRelative"/>; no duplicates, case-insensitively), hash,
    /// size and part; the build must hold the game and its file list. Throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static PartsManifest Parse(string json)
    {
        PartsManifest? m;
        try { m = JsonSerializer.Deserialize(json, PartsJson.Default.PartsManifest); }
        catch (JsonException e) { throw new InvalidDataException("The build's parts list could not be read.", e); }
        if (m is null) throw new InvalidDataException("The build's parts list is empty.");
        m.Validate();
        return m;
    }

    public void Validate()
    {
        static InvalidDataException Bad(string why) => new($"The build's parts list is not valid: {why}.");
        if (Format != FormatId) throw Bad($"unknown format '{Short(Format)}'");
        if (!Names.IsTag(Tag)) throw Bad("the build's tag is not one the launcher accepts");
        if (!Names.IsTag(PartsTag)) throw Bad("the parts release's tag is not one the launcher accepts");
        if (Parts is null || Parts.Count == 0) throw Bad("it names no parts");
        if (Files is null || Files.Count == 0) throw Bad("it names no files");
        if (Files.Count > SafeZip.MaxEntries) throw Bad("it names too many files");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Parts)
        {
            if (p is null || !PartRules.IsPartName(p.Name)) throw Bad($"a part has the unusable name '{Short(p?.Name)}'");
            if (!names.Add(p.Name)) throw Bad($"the part '{p.Name}' is named twice");
            if (!Names.IsPartAsset(p.Asset) || !assets.Add(p.Asset)) throw Bad($"the part '{p.Name}' has an unusable or repeated file name");
            if (!Hashes.IsSha256Hex(p.ZipSha256) || !Hashes.IsSha256Hex(p.ContentId)) throw Bad($"the part '{p.Name}' has no valid SHA-256");
            if (p.ZipSize <= 0 || p.ZipSize > SafeZip.MaxTotalBytes) throw Bad($"the part '{p.Name}' has an impossible size");
            p.ZipSha256 = p.ZipSha256.ToLowerInvariant();
            p.ContentId = p.ContentId.ToLowerInvariant();
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var f in Files)
        {
            if (f is null) throw Bad("a file entry is empty");
            string? safe = PackageLayout.SafeRelative(f.Path ?? "");
            if (safe is null) throw Bad($"a file has the unsafe path '{Short(f.Path)}'");
            f.Path = safe.Replace('\\', '/');
            if (!paths.Add(f.Path)) throw Bad($"the file {Short(f.Path)} is listed twice");
            if (!Hashes.IsSha256Hex(f.Sha256)) throw Bad($"the file {Short(f.Path)} has no valid SHA-256");
            f.Sha256 = f.Sha256.ToLowerInvariant();
            if (f.Size < 0 || f.Size > SafeZip.MaxTotalBytes) throw Bad($"the file {Short(f.Path)} has an impossible size");
            if (!names.Contains(f.Part ?? "")) throw Bad($"the file {Short(f.Path)} names an unknown part");
            total += f.Size;
            if (total > SafeZip.MaxTotalBytes) throw Bad("the build is larger than a playtest build can be");
        }
        foreach (var must in new[] { PackageLayout.GameExe, PackageLayout.ManifestPath })
            if (!paths.Contains(must.Replace('\\', '/'))) throw Bad($"it does not hold {must}");
        // One SHA-256, one size: two entries with the same hash and different sizes cannot both be right.
        foreach (var g in Files.GroupBy(f => f.Sha256)) if (g.Select(f => f.Size).Distinct().Count() > 1) throw Bad("two files share a SHA-256 but not a size");
    }

    static string Short(string? s) => s is null ? "" : s.Length > 60 ? s[..60] + "..." : s;
}

/// <summary>One part: its zip (a release asset of the parts release), the zip's SHA-256 and size, and the fingerprint of what it holds.</summary>
public sealed class PartInfo
{
    public string Name { get; set; } = "";
    /// <summary>sm-reimpl-part-&lt;name&gt;-&lt;first 16 hex digits of the content id&gt;.zip</summary>
    public string Asset { get; set; } = "";
    /// <summary>SHA-256 over the part's entries ("&lt;sha256&gt; &lt;path&gt;\n", sorted by path): equal for equal content, whatever the zip's bytes.</summary>
    public string ContentId { get; set; } = "";
    public string ZipSha256 { get; set; } = "";
    public long ZipSize { get; set; }
    /// <summary>Entries in the zip and their unpacked bytes (for display).</summary>
    public int Entries { get; set; }
    public long Bytes { get; set; }
}

/// <summary>One file of the build: where it goes (relative, forward slashes), its SHA-256 and size, and the part whose zip carries those bytes.</summary>
public sealed class PartFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string Part { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PartsManifest))]
internal sealed partial class PartsJson : JsonSerializerContext;

/// <summary>Which part a file of the build belongs to, by its path. Split so that what rarely changes (engine, runtime, kit, launcher) is apart from
/// what changes with every commit (the game's assemblies and pack, the notes).</summary>
public static class PartRules
{
    static readonly Regex PartName = new(@"^[a-z0-9][a-z0-9-]{0,31}\z", RegexOptions.CultureInvariant);
    /// <summary>The .NET runtime's own files in a self-contained publish (the client's data folder and server\ carry the same ones).</summary>
    static readonly Regex Runtime = new(
        @"^(System\..+|Microsoft\..+|mscorlib|netstandard|WindowsBase|api-ms-win-.+|ucrtbase|hostfxr|hostpolicy|coreclr|clrjit|clrgc.*|clretwrc|mscordaccore.*|mscordbi|mscorrc|msquic|createdump)\.(dll|exe)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The parts in the order they are listed and downloaded.</summary>
    public static readonly string[] Order = ["meta", "launcher", "engine", "dotnet", "client", "media", "ashfall", "server", "source"];

    public static bool IsPartName(string? name) => name is not null && PartName.IsMatch(name);

    /// <summary>The part of a relative path (either slash).</summary>
    public static string PartOf(string relative)
    {
        string p = relative.Replace('\\', '/');
        string[] s = p.Split('/');
        string leaf = s[^1];
        if (s.Length == 1) return leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "launcher" : "meta";
        string top = s[0].ToLowerInvariant();
        switch (top)
        {
            case "source": return "source";
            case "server": return s.Length == 2 && Runtime.IsMatch(leaf) ? "dotnet" : "server";
            case "game":
                if (s.Length == 2)
                {
                    if (leaf.Equals("sm-reimpl.exe", StringComparison.OrdinalIgnoreCase) || leaf.Equals("sm-reimpl.console.exe", StringComparison.OrdinalIgnoreCase)) return "engine";
                    if (leaf.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)) return "client";
                    return "meta";
                }
                string sub = s[1].ToLowerInvariant();
                if (sub == "ashfall-kit") return "ashfall";
                if (sub == "assets") return "media";
                if (sub.StartsWith("data_", StringComparison.Ordinal))
                {
                    if (s.Length == 3 && leaf.Equals("GodotSharp.dll", StringComparison.OrdinalIgnoreCase)) return "engine";
                    return s.Length == 3 && Runtime.IsMatch(leaf) ? "dotnet" : "client";
                }
                return "client";
            default: return "meta";
        }
    }

    public static int Rank(string part) { int i = Array.IndexOf(Order, part); return i < 0 ? Order.Length : i; }
}

/// <summary>Builds the parts of an assembled playtest folder (the release side; smtool release-parts build).</summary>
public static class PartsBuilder
{
    /// <summary>Every zip entry carries this time, so a part with the same files gets the same bytes on every build (with the same zip library).</summary>
    public static readonly DateTimeOffset EntryTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public sealed record Result(PartsManifest Manifest, string ManifestPath, IReadOnlyDictionary<string, string> PartZips);

    /// <summary>
    /// Hashes every file of <paramref name="folder"/>, assigns it to a part (<see cref="PartRules"/>; files with the same bytes are carried once, by the
    /// part of the first of them in path order), writes each part's zip to <paramref name="partsDir"/> and the manifest to <paramref name="manifestPath"/>.
    /// </summary>
    public static Result Build(string folder, string partsDir, string manifestPath, string tag, string version, string? commit, string? built, string partsTag = PartsManifest.DefaultPartsTag)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = Directory.EnumerateFiles(folder, "*", options)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(folder, f).Replace('\\', '/')))
            .OrderBy(f => f.Rel, StringComparer.Ordinal)
            .ToList();
        var manifest = new PartsManifest { Tag = tag, Version = version, Commit = commit, Built = built, PartsTag = partsTag };
        var carrier = new Dictionary<string, string>(StringComparer.Ordinal);                // sha256 -> part
        var carried = new Dictionary<string, List<(string Full, string Rel, string Sha, long Size)>>(StringComparer.Ordinal);
        foreach (var (full, rel) in files)
        {
            if (PackageLayout.SafeRelative(rel) is null) throw new InvalidDataException($"{rel} is not a path a launcher accepts.");
            string sha = Hashes.Sha256File(full);
            long size = new FileInfo(full).Length;
            if (!carrier.TryGetValue(sha, out var part))
            {
                part = PartRules.PartOf(rel);
                carrier[sha] = part;
                if (!carried.TryGetValue(part, out var list)) carried[part] = list = [];
                list.Add((full, rel, sha, size));
            }
            manifest.Files.Add(new PartFile { Path = rel, Sha256 = sha, Size = size, Part = part });
        }
        Directory.CreateDirectory(partsDir);
        var zips = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in carried.Keys.OrderBy(PartRules.Rank).ThenBy(n => n, StringComparer.Ordinal))
        {
            var entries = carried[name];
            string contentId = ContentId(entries.Select(e => (e.Rel, e.Sha)));
            string asset = AssetName(name, contentId);
            string zip = Path.Combine(partsDir, asset);
            WriteZip(zip, entries.Select(e => (e.Full, e.Rel)));
            manifest.Parts.Add(new PartInfo
            {
                Name = name, Asset = asset, ContentId = contentId, ZipSha256 = Hashes.Sha256File(zip), ZipSize = new FileInfo(zip).Length,
                Entries = entries.Count, Bytes = entries.Sum(e => e.Size),
            });
            zips[name] = zip;
        }
        manifest.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
        File.WriteAllText(manifestPath, manifest.ToJson() + "\n", new UTF8Encoding(false));
        return new Result(manifest, manifestPath, zips);
    }

    /// <summary>The content fingerprint: SHA-256 of "&lt;sha256&gt; &lt;path&gt;\n" per entry, sorted by path (ordinal).</summary>
    public static string ContentId(IEnumerable<(string Path, string Sha256)> entries)
    {
        var sb = new StringBuilder();
        foreach (var (path, sha) in entries.OrderBy(e => e.Path, StringComparer.Ordinal)) sb.Append(sha).Append(' ').Append(path).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static string AssetName(string part, string contentId) => $"sm-reimpl-part-{part}-{contentId[..16]}.zip";

    static void WriteZip(string zipPath, IEnumerable<(string Full, string Rel)> entries)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (full, rel) in entries.OrderBy(e => e.Rel, StringComparer.Ordinal))
        {
            var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
            entry.LastWriteTime = EntryTime;
            using var src = File.OpenRead(full);
            using var dst = entry.Open();
            src.CopyTo(dst);
        }
    }
}

/// <summary>Which parts a launcher must download: those carrying a file whose bytes it does not have yet.</summary>
public sealed record PartsPlan(IReadOnlyList<PartInfo> Download, IReadOnlyList<PartInfo> Reuse)
{
    public long DownloadBytes => Download.Sum(p => p.ZipSize);
    public long ReuseBytes => Reuse.Sum(p => p.ZipSize);

    /// <summary>
    /// A part is downloaded when one of the files it carries has a SHA-256 that <paramref name="have"/> does not contain (the hashes of the installed
    /// versions' files); every other part is assembled from the installed files.
    /// </summary>
    public static PartsPlan Select(PartsManifest manifest, IReadOnlySet<string> have)
    {
        var need = new HashSet<string>(manifest.Files.Where(f => !have.Contains(f.Sha256)).Select(f => f.Part), StringComparer.Ordinal);
        var ordered = manifest.Parts.OrderBy(p => PartRules.Rank(p.Name)).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
        return new PartsPlan(ordered.Where(p => need.Contains(p.Name)).ToList(), ordered.Where(p => !need.Contains(p.Name)).ToList());
    }

    /// <summary>The SHA-256 of every file an installed version's own file list (notes\CONTENTS-SHA256.txt) names, with one path for each.</summary>
    public static Dictionary<string, string> InstalledHashes(params string?[] versionDirs)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in versionDirs)
        {
            if (dir is null) continue;
            string list = Path.Combine(dir, PackageLayout.ManifestPath);
            if (!File.Exists(list)) continue;
            var (entries, _) = Manifest.Parse(File.ReadAllText(list));
            foreach (var (rel, hash) in entries)
            {
                string? full = PackageLayout.Inside(dir, rel);
                if (full is not null) result.TryAdd(hash, full);
            }
        }
        return result;
    }
}

/// <summary>A part zip already on the releases repository's parts release: its name, size and GitHub's SHA-256 (the asset's digest), if any.</summary>
public sealed record RemotePart(string Name, long Size, string? Sha256);

/// <summary>What a ship uploads: the parts whose zip is not on the parts release yet. The others are reused with the zip that is there.</summary>
public sealed record PartsUpload(IReadOnlyList<PartInfo> Upload, IReadOnlyList<PartInfo> Reuse, IReadOnlyList<string> MissingDigest)
{
    public long UploadBytes => Upload.Sum(p => p.ZipSize);

    /// <summary>
    /// Compares the build's parts with the assets of the parts release (the release side; smtool release-parts plan). A part whose asset name (its content
    /// fingerprint) is there already is reused: the manifest then takes that zip's SHA-256 and size (its bytes may differ from this build's zip, its files
    /// do not). An existing asset without a digest is listed in <see cref="MissingDigest"/> unless <paramref name="knownDigests"/> gives its hash (the caller
    /// downloads it and hashes it). Nothing is ever uploaded over an existing asset: older manifests name its hash.
    /// </summary>
    public static PartsUpload Plan(PartsManifest manifest, IEnumerable<RemotePart> remote, IReadOnlyDictionary<string, string>? knownDigests = null)
    {
        var byName = new Dictionary<string, RemotePart>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in remote) byName.TryAdd(r.Name, r);
        var upload = new List<PartInfo>();
        var reuse = new List<PartInfo>();
        var missing = new List<string>();
        foreach (var p in manifest.Parts)
        {
            if (!byName.TryGetValue(p.Asset, out var r)) { upload.Add(p); continue; }
            string? sha = r.Sha256 ?? (knownDigests is not null && knownDigests.TryGetValue(r.Name, out var k) ? k : null);
            if (sha is null || !Hashes.IsSha256Hex(sha)) { missing.Add(r.Name); continue; }
            if (r.Size <= 0) { missing.Add(r.Name); continue; }
            p.ZipSha256 = sha.ToLowerInvariant();
            p.ZipSize = r.Size;
            reuse.Add(p);
        }
        return new PartsUpload(upload, reuse, missing);
    }

    /// <summary>The assets of a release as GitHub's API lists them (GET /repos/{repo}/releases/tags/{tag}: its "assets" array, or the array itself).</summary>
    public static List<RemotePart> ParseAssets(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("assets", out var a)) root = a;
        var result = new List<RemotePart>();
        if (root.ValueKind != JsonValueKind.Array) return result;
        foreach (var e in root.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            string? name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (name is null) continue;
            if (e.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() != "uploaded") continue;
            long size = e.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0;
            string? digest = e.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            result.Add(new RemotePart(name, size, Hashes.FromDigest(digest)));
        }
        return result;
    }
}
