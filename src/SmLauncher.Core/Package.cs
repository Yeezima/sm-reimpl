using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace SmLauncher.Core;

/// <summary>The playtest zip's own layout, as tools/release/release.ps1 builds it.</summary>
public static class PackageLayout
{
    /// <summary>notes\CONTENTS-SHA256.txt: "&lt;sha256&gt;  &lt;relative path&gt;" for every file of the unpacked folder but itself.</summary>
    public const string ManifestPath = @"notes\CONTENTS-SHA256.txt";
    public const string VersionPath = @"notes\VERSION.txt";
    /// <summary>A version folder must hold the game to be playable.</summary>
    public const string GameExe = @"game\sm-reimpl.exe";
    /// <summary>The launcher inside the zip (top level, first in the list).</summary>
    public const string BundledLauncher = "0 - SM Reimpl Launcher.exe";

    /// <summary>A numbered entry point at the top of a version folder ("2 - Play.cmd" for 2), by its number, so a renamed title still works.</summary>
    public static string? EntryPoint(string versionDir, int number)
    {
        if (!Directory.Exists(versionDir)) return null;
        return Directory.EnumerateFiles(versionDir, $"{number} - *.cmd").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>
    /// A relative path from a zip entry or a manifest line, made safe: forward or back slashes, no empty, "." or ".." segment, no drive, root, colon
    /// (alternate streams), control or wildcard characters, no trailing dot or space. Returns the Windows form, or null when it is not safe.
    /// </summary>
    static readonly Regex ReservedDevice = new(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? SafeRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 400) return null;
        var parts = path.Replace('\\', '/').Split('/');
        foreach (var p in parts)
        {
            if (p.Length == 0 || p == "." || p == "..") return null;
            if (p.EndsWith('.') || p.EndsWith(' ')) return null;
            foreach (char c in p) if (c < 32 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|') return null;
            if (ReservedDevice.IsMatch(p)) return null;
        }
        return string.Join('\\', parts);
    }

    /// <summary>The full path of <paramref name="relative"/> under <paramref name="root"/>, or null when it would land outside it.</summary>
    public static string? Inside(string root, string relative)
    {
        string? safe = SafeRelative(relative);
        if (safe is null) return null;
        string full = Path.GetFullPath(Path.Combine(root, safe));
        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>The version a folder holds, from notes\VERSION.txt, and the tag its release has (the rule of release.ps1).</summary>
    public static VersionFile? ReadVersion(string folder)
    {
        string path = Path.Combine(folder, VersionPath);
        if (!File.Exists(path)) return null;
        return VersionFile.Parse(File.ReadAllText(path));
    }
}

/// <summary>notes\VERSION.txt of a playtest folder.</summary>
public sealed record VersionFile(string Version, string? Commit, string? Built)
{
    public static VersionFile? Parse(string text)
    {
        string? version = null, commit = null, built = null;
        foreach (var raw in text.Split('\n'))
        {
            string l = raw.Trim();
            if (l.StartsWith("sm-reimpl ", StringComparison.Ordinal)) version = l["sm-reimpl ".Length..].Trim();
            else if (l.StartsWith("commit:", StringComparison.Ordinal)) commit = l["commit:".Length..].Trim();
            else if (l.StartsWith("built:", StringComparison.Ordinal)) built = Regex.Match(l, @"\d{4}-\d{2}-\d{2}").Value;
        }
        return string.IsNullOrEmpty(version) ? null : new VersionFile(version, commit, string.IsNullOrEmpty(built) ? null : built);
    }

    /// <summary>
    /// The release tag (release.ps1): a bare commit hash gives tester-&lt;date&gt;-&lt;short sha&gt;; a describe that starts with v and a digit stays; anything
    /// else gets a v in front. Null when that is not a valid tag.
    /// </summary>
    public string? Tag
    {
        get
        {
            string? tag;
            if (Regex.IsMatch(Version, "^[0-9a-f]{7,40}$"))
            {
                if (Built is null) return null;
                string sha = Commit is { Length: >= 7 } c && Regex.IsMatch(c, "^[0-9a-f]+$") ? c[..7] : Version[..7];
                tag = $"tester-{Built}-{sha}";
            }
            else tag = Regex.IsMatch(Version, @"^v\d") ? Version : "v" + Version;
            return Names.IsTag(tag) ? tag : null;
        }
    }
}

/// <summary>The result of checking a folder against its manifest.</summary>
public sealed record ManifestReport(bool HasManifest, int Checked, IReadOnlyList<string> Missing, IReadOnlyList<string> Changed, IReadOnlyList<string> Unsafe,
    IReadOnlyList<string>? UnlistedPrograms = null)
{
    /// <summary>Program files (.exe, .dll, .cmd, ...) in the folder that its file list does not name.</summary>
    public IReadOnlyList<string> Unlisted => UnlistedPrograms ?? [];
    public bool Ok => HasManifest && Missing.Count == 0 && Changed.Count == 0 && Unsafe.Count == 0 && Unlisted.Count == 0 && Checked > 0;

    public string Describe()
    {
        if (!HasManifest) return "the folder has no file list (notes\\CONTENTS-SHA256.txt) to check against";
        if (Ok) return $"all {Checked} files are intact";
        var sb = new StringBuilder();
        if (Missing.Count > 0) sb.Append($"{Missing.Count} missing ({string.Join(", ", Missing.Take(3))}{(Missing.Count > 3 ? ", ..." : "")}) ");
        if (Changed.Count > 0) sb.Append($"{Changed.Count} changed or damaged ({string.Join(", ", Changed.Take(3))}{(Changed.Count > 3 ? ", ..." : "")}) ");
        if (Unsafe.Count > 0) sb.Append($"{Unsafe.Count} unsafe names in the list ");
        if (Unlisted.Count > 0) sb.Append($"{Unlisted.Count} program files that do not belong to the build ({string.Join(", ", Unlisted.Take(3))}{(Unlisted.Count > 3 ? ", ..." : "")}) ");
        if (Checked == 0 && sb.Length == 0) sb.Append("the file list is empty");
        return sb.ToString().Trim();
    }
}

public static class Manifest
{
    /// <summary>Parses CONTENTS-SHA256.txt into (relative path, hash) pairs; unsafe or malformed lines are returned as unsafe.</summary>
    public static (List<(string Path, string Hash)> Entries, List<string> Unsafe) Parse(string text)
    {
        var entries = new List<(string, string)>();
        var bad = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var m = Regex.Match(line, @"^([0-9a-fA-F]{64}) [ *](.+)$");
            string? rel = m.Success ? PackageLayout.SafeRelative(m.Groups[2].Value.TrimStart(' ', '*')) : null;
            if (rel is null) { bad.Add(line.Length > 80 ? line[..80] : line); continue; }
            entries.Add((rel, m.Groups[1].Value.ToLowerInvariant()));
        }
        return (entries, bad);
    }

    /// <summary>The set of relative paths the manifest lists (case-insensitive), or null without a manifest.</summary>
    public static HashSet<string>? ListedFiles(string folder)
    {
        string path = Path.Combine(folder, PackageLayout.ManifestPath);
        if (!File.Exists(path)) return null;
        var (entries, _) = Parse(File.ReadAllText(path));
        return new HashSet<string>(entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Hashes every file the folder's manifest lists. <paramref name="progress"/> gets (files done, files listed).</summary>
    public static ManifestReport Verify(string folder, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        string path = Path.Combine(folder, PackageLayout.ManifestPath);
        if (!File.Exists(path)) return new ManifestReport(false, 0, [], [], []);
        var (entries, bad) = Parse(File.ReadAllText(path));
        var missing = new List<string>();
        var changed = new List<string>();
        int done = 0;
        foreach (var (rel, hash) in entries)
        {
            ct.ThrowIfCancellationRequested();
            string? full = PackageLayout.Inside(folder, rel);
            if (full is null) bad.Add(rel);
            else if (!File.Exists(full)) missing.Add(rel);
            else
            {
                try { if (!string.Equals(Hashes.Sha256File(full), hash, StringComparison.Ordinal)) changed.Add(rel); }
                catch (IOException) { changed.Add(rel); }
                catch (UnauthorizedAccessException) { changed.Add(rel); }
            }
            progress?.Report((++done, entries.Count));
        }
        var listed = new HashSet<string>(entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase) { PackageLayout.ManifestPath };
        var unlisted = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var f in Directory.EnumerateFiles(folder, "*", options))
        {
            string rel = Path.GetRelativePath(folder, f);
            if (ProgramExtensions.Contains(Path.GetExtension(rel)) && !listed.Contains(rel)) unlisted.Add(rel);
        }
        return new ManifestReport(true, entries.Count, missing, changed, bad, unlisted);
    }

    /// <summary>Program files that a version folder may only hold when its file list names them (anything else was put there by something else).</summary>
    public static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".cmd", ".bat", ".ps1", ".com", ".scr", ".msi" };

    /// <summary>The hash the folder's manifest gives for <paramref name="relative"/>, or null when it does not list it.</summary>
    public static string? ExpectedHash(string folder, string relative)
    {
        string path = Path.Combine(folder, PackageLayout.ManifestPath);
        if (!File.Exists(path)) return null;
        var (entries, _) = Parse(File.ReadAllText(path));
        return entries.Where(e => string.Equals(e.Path, relative, StringComparison.OrdinalIgnoreCase)).Select(e => e.Hash).FirstOrDefault();
    }

    /// <summary>The file is listed in the folder's manifest and matches its hash.</summary>
    public static bool FileMatches(string folder, string relative)
    {
        string? expected = ExpectedHash(folder, relative);
        string? full = PackageLayout.Inside(folder, relative);
        return expected is not null && full is not null && File.Exists(full) && string.Equals(Hashes.Sha256File(full), expected, StringComparison.Ordinal);
    }
}

/// <summary>Unpacks a verified zip. Zip-slip safe: every entry must land inside the target folder.</summary>
public static class SafeZip
{
    public const int MaxEntries = 100_000;
    public const long MaxTotalBytes = 8L << 30;

    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="dest"/> (which must not exist yet). Refuses the whole zip, before writing anything, when an
    /// entry name is unsafe (absolute, "..", a drive or stream colon), two entries map to one file, or the sizes are implausible; checks while writing that
    /// no entry inflates beyond its stated size. Each file is flushed to disk, so a version switched to after this survives a power cut.
    /// </summary>
    public static int Extract(string zipPath, string dest, IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default, Func<string, long>? freeBytes = null)
    {
        try { return ExtractChecked(zipPath, dest, progress, ct, freeBytes); }
        catch (IOException e) when (DiskSpace.IsDiskFull(e)) { throw DiskSpace.Wrap(e, "unpack the update"); }
    }

    static int ExtractChecked(string zipPath, string dest, IProgress<(long Done, long Total)>? progress, CancellationToken ct, Func<string, long>? freeBytes)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > MaxEntries) throw new VerificationException("The zip holds too many files to be a playtest build.");
        var plan = new List<(ZipArchiveEntry Entry, string Target)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var e in zip.Entries)
        {
            string name = e.FullName;
            bool isDir = name.EndsWith('/') || name.EndsWith('\\');
            string trimmed = name.TrimEnd('/', '\\');
            if (trimmed.Length == 0) continue;
            string? target = PackageLayout.Inside(dest, trimmed);
            if (target is null) throw new VerificationException($"The zip holds an unsafe path ({Show(name)}); nothing was extracted.");
            if (isDir) continue;
            if (!seen.Add(target)) throw new VerificationException($"The zip holds {Show(name)} twice; nothing was extracted.");
            if (e.Length < 0) throw new VerificationException("The zip states an impossible size.");
            total += e.Length;
            if (total > MaxTotalBytes) throw new VerificationException("The zip unpacks to more than a playtest build can be.");
            plan.Add((e, target));
        }
        if (Directory.Exists(dest)) throw new IOException($"{dest} exists already.");
        DiskSpace.Require(dest, total, "unpack the update", freeBytes);      // the declared total, before anything is written
        Directory.CreateDirectory(dest);
        long done = 0;
        var buffer = new byte[1 << 16];
        foreach (var (entry, target) in plan)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var src = entry.Open())
            using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                long written = 0;
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += n;
                    if (written > entry.Length) throw new VerificationException($"{Show(entry.FullName)} inflates beyond its stated size; the zip is refused.");
                    dst.Write(buffer, 0, n);
                }
                if (written != entry.Length) throw new VerificationException($"{Show(entry.FullName)} is shorter than its stated size; the zip is refused.");
                dst.Flush(flushToDisk: true);
            }
            done += entry.Length;
            progress?.Report((done, total));
        }
        return plan.Count;
    }

    static string Show(string name) => name.Length > 80 ? name[..80] + "..." : name;
}

/// <summary>A server address for Join, as "4 - Join a server by address.cmd" accepts it: host or host:port (1..65535); the default port 27015 is added.</summary>
public static class ServerAddress
{
    public static string? Normalise(string? text)
    {
        string s = (text ?? "").Trim();
        var m = Regex.Match(s, @"^[A-Za-z0-9.\-]{1,253}(:([0-9]{1,5}))?$");
        if (!m.Success) return null;
        if (!m.Groups[2].Success) return s + ":27015";
        int port = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        return port is >= 1 and <= 65535 ? s : null;
    }
}
