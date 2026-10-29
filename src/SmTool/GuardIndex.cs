using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmTool;

/// <summary>
/// The content guard's remembered fingerprints of one comparison tree (the game install or the private cache), so a release does not hash the same
/// unchanged files again on every run (<c>smtool scan-package --index-cache &lt;dir&gt;</c>; release.ps1 passes <c>%LOCALAPPDATA%\sm-reimpl\cache\guard</c>).
/// One JSON file per tree, named by the tree's full path; each file of the tree is keyed by its relative path, size and last-write time (UTC ticks), and
/// for the install also by the Steam build id. A SHA-256 is reused only when the path, the size and the time all match; anything new or changed is hashed
/// again when the guard needs it, and files no longer in the tree are dropped when the index is saved. A missing, unreadable, corrupt or foreign index
/// file is ignored and rebuilt: the cache can only save time, never change the guard's answer. The index file is never written inside the tree it
/// describes when that tree is the game install (the command refuses such a cache folder).
/// </summary>
public sealed class GuardIndex
{
    /// <summary>The index file format; a file of another version is ignored and rebuilt.</summary>
    public const int FormatVersion = 1;

    /// <summary>One file of the tree as it was seen: size, last-write time (UTC ticks) and its SHA-256 (upper-case hex) when it was ever needed.</summary>
    public readonly record struct Entry(long Size, long WriteTicks, string? Hash);

    static readonly Regex HashShape = new("^[0-9A-F]{64}$", RegexOptions.Compiled);
    static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    readonly Dictionary<string, Entry> _entries;

    /// <summary>The index file this tree's fingerprints are kept in.</summary>
    public string File { get; }
    /// <summary>The tree's full path.</summary>
    public string Root { get; }
    /// <summary>The Steam build id of the install (null for the private cache, or when the manifest is not there); a different id discards the index.</summary>
    public string? BuildId { get; }
    public bool SkipSourceText { get; }
    /// <summary>How the index was loaded: "new", "loaded", or why the stored one was discarded ("corrupt", "other version", ...).</summary>
    public string Status { get; }
    /// <summary>Hashes taken from the index during this run (set by the scanner).</summary>
    public int Reused { get; internal set; }

    GuardIndex(string file, string root, string? buildId, bool skipSourceText, Dictionary<string, Entry> entries, string status)
    {
        File = file; Root = root; BuildId = buildId; SkipSourceText = skipSourceText; _entries = entries; Status = status;
    }

    /// <summary>The index file of a tree inside the cache folder: <c>&lt;label&gt;-&lt;16 hex of the tree's path&gt;.json</c> (the label is "install" or "private").</summary>
    public static string FileFor(string cacheDir, string label, string root, bool skipSourceText)
    {
        string key = Path.GetFullPath(root).TrimEnd('\\', '/');
        if (OperatingSystem.IsWindows()) key = key.ToUpperInvariant();
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key + "|" + (skipSourceText ? "skip-source-text" : "all")))).Substring(0, 16);
        return Path.Combine(cacheDir, $"{label}-{id}.json");
    }

    /// <summary>Reads the index of <paramref name="root"/>; never throws: anything wrong with the stored file means an empty index (and a status saying why).</summary>
    public static GuardIndex Load(string file, string root, string? buildId, bool skipSourceText)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
        var entries = new Dictionary<string, Entry>(PathComparer);
        string status;
        if (!System.IO.File.Exists(file)) status = "new";
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllBytes(file));
                var r = doc.RootElement;
                var storedBuild = r.GetProperty("buildId");
                string? storedBuildId = storedBuild.ValueKind == JsonValueKind.Null ? null : storedBuild.GetString();
                if (r.GetProperty("version").GetInt32() != FormatVersion) status = "other version, rebuilt";
                else if (!PathComparer.Equals(r.GetProperty("root").GetString(), fullRoot)) status = "other folder, rebuilt";
                else if (r.GetProperty("skipSourceText").GetBoolean() != skipSourceText) status = "other options, rebuilt";
                else if (storedBuildId != buildId) status = "other Steam build, rebuilt";
                else
                {
                    foreach (var p in r.GetProperty("files").EnumerateObject())
                    {
                        long size = p.Value.GetProperty("s").GetInt64();
                        long ticks = p.Value.GetProperty("t").GetInt64();
                        string? hash = p.Value.TryGetProperty("h", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                        if (hash is not null && !HashShape.IsMatch(hash)) hash = null;      // a malformed hash is hashed again, never trusted
                        if (size < 0 || p.Name.Length == 0) continue;
                        entries[p.Name] = new Entry(size, ticks, hash);
                    }
                    status = "loaded";
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                entries.Clear();
                status = "corrupt, rebuilt";
            }
        }
        return new GuardIndex(file, fullRoot, buildId, skipSourceText, entries, status);
    }

    /// <summary>The stored hash of a file, only when its size and last-write time are exactly what was stored.</summary>
    public bool TryGetHash(string relativePath, long size, long writeTicks, out string hash)
    {
        if (_entries.TryGetValue(relativePath, out var e) && e.Size == size && e.WriteTicks == writeTicks && e.Hash is { } h) { hash = h; return true; }
        hash = "";
        return false;
    }

    /// <summary>The stored entries (tests).</summary>
    public IReadOnlyDictionary<string, Entry> Entries => _entries;

    /// <summary>
    /// Writes the tree as it was indexed in this run (files no longer there are dropped), with every hash known: written to a temporary file in the same
    /// folder and moved over the old one. The temporary name ends in .json so that a concurrent index of the private cache skips it as text.
    /// </summary>
    public void Save(IEnumerable<(string RelativePath, long Size, long WriteTicks, string? Hash)> files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(File))!);
        string temp = File + "." + Guid.NewGuid().ToString("N") + ".tmp.json";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteNumber("version", FormatVersion);
                w.WriteString("root", Root);
                if (BuildId is null) w.WriteNull("buildId"); else w.WriteString("buildId", BuildId);
                w.WriteBoolean("skipSourceText", SkipSourceText);
                w.WriteStartObject("files");
                foreach (var (rel, size, ticks, hash) in files)
                {
                    w.WriteStartObject(rel);
                    w.WriteNumber("s", size);
                    w.WriteNumber("t", ticks);
                    if (hash is not null) w.WriteString("h", hash);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            System.IO.File.Move(temp, File, overwrite: true);
        }
        finally
        {
            if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp);
        }
    }

    /// <summary>
    /// The Steam build id of an install, read-only from <c>appmanifest_55150.acf</c> two folders above it (steamapps/common/&lt;name&gt;); null when it is not there.
    /// </summary>
    public static string? SteamBuildId(string install)
    {
        try
        {
            string manifest = Path.GetFullPath(Path.Combine(install, "..", "..", "appmanifest_55150.acf"));
            if (!System.IO.File.Exists(manifest)) return null;
            using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var match = Regex.Match(reader.ReadToEnd(), "\"buildid\"\\s+\"(\\d{1,12})\"");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies under it.</summary>
    public static bool IsInside(string path, string folder)
    {
        string p = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string f = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return p.StartsWith(f, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
