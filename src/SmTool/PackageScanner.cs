using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SmTool;

/// <summary>One thing the content guard refused. <see cref="Detail"/> never repeats a secret, a token or a user name.</summary>
public sealed record ScanFinding(string Package, string Entry, string Rule, string Detail)
{
    public override string ToString() => $"{Package}: {Entry}: [{Rule}] {Detail}";
}

/// <summary>
/// The release content guard (tools/release/release.ps1): decides whether a package (zip or folder) may leave the machine.
/// It refuses original game data (by extension, by file magic, by identical content with files of the install or the private
/// cache), the private cache and the secrets config, absolute user paths, Discord webhook URLs and bot tokens, and the exact
/// secret values of the given config files. Engine-free; findings never print matched secret text.
/// </summary>
public sealed class PackageScanner
{
    /// <summary>Extensions of the game's own files (and of the formats read from it). A Godot-embedded .pck ("GDPC" magic) is the one exception.</summary>
    public static readonly string[] ForbiddenExtensions =
    [
        ".oppc", ".bnk", ".wem", ".pck", ".hkt", ".hkx", ".dcm", ".bmat", ".dds", ".fx", ".baf", ".o3d", ".layer", ".ppk", ".bin",
        ".bod", ".attr_pc", ".ucs", ".usm", ".object-manifest",
    ];

    /// <summary>Folder names that hold private data or test output; nothing under them goes into a package.</summary>
    public static readonly string[] ForbiddenFolders = ["cache", "discovery", "screenshots", "recordings", "captures", "demos", ".claude", ".godot"];

    /// <summary>File names that hold secrets or local configuration.</summary>
    public static readonly string[] ForbiddenNames = ["local.json", ".env", "roles-seen.json", "discord.json"];

    /// <summary>The first bytes of the game's container formats: OBPK (.oppc), AKPK (Wwise package), BKHD (Wwise bank), DDS, a Havok packfile.</summary>
    static readonly (string Name, byte[] Magic)[] Magics =
    [
        ("OBPK archive", "OBPK"u8.ToArray()),
        ("Wwise package (AKPK)", "AKPK"u8.ToArray()),
        ("Wwise bank (BKHD)", "BKHD"u8.ToArray()),
        ("DDS texture", "DDS "u8.ToArray()),
        ("Havok packfile", [0x57, 0xE0, 0xE0, 0x57, 0x10, 0xC0, 0xC0, 0x10]),
    ];

    const int ChunkBytes = 4 * 1024 * 1024;
    const int OverlapBytes = 1024;
    const int MinHashedSize = 4096;

    /// <summary>The size of one read of a file's content (a value that straddles the end of a read is still found: the next read starts with an overlap).</summary>
    internal const int ReadBufferBytes = ChunkBytes + OverlapBytes;

    /// <summary>
    /// When set, a package entry whose extension is not in this set (case-insensitive; "" = no extension, allowed only for the names in
    /// <see cref="AllowedBareNames"/>) is refused as an unexpected type. Used for the binary packages (client, servers), whose contents are known.
    /// </summary>
    public HashSet<string>? AllowedExtensions { get; set; }

    /// <summary>
    /// When set, a package must contain at least one Godot pack that parsed and lists the client's own entries (an executable with an embedded pack, or a .pck);
    /// otherwise the package is refused ("no-verified-pack"). Used for the client zip.
    /// </summary>
    public bool RequireVerifiedPack { get; set; }

    int _verifiedPacks;

    /// <summary>
    /// The GPL source offer inside a binary package: entry paths (forward slashes, case-insensitive). An item ending in <c>.zip</c> names exactly one entry
    /// (the release passes <c>source/sm-reimpl-source-&lt;ver&gt;.zip</c>); any other item is a folder prefix, normalised to end with a slash. A matching zip is
    /// allowed in a binary package once per package (a second one is refused), and its own entries are scanned with every rule except the type allowlist, as
    /// the stand-alone source zip is; inside it, archives and binaries other than <see cref="SourceMediaExtensions"/> are refused. Every other archive in a
    /// package is scanned too (zips by their PK magic, whatever the name), with the package's profile; gzip, 7z and RAR archives cannot be looked into and
    /// are refused.
    /// </summary>
    public List<string> SourceArchivePrefixes { get; } = [];

    /// <summary>Binary files the source offer may hold (the added content's own sounds and images); every other non-text file in it is refused.</summary>
    public static readonly string[] SourceMediaExtensions = [".ogg", ".png", ".jpg", ".jpeg", ".webp", ".ttf", ".otf", ".ico"];

    /// <summary>Nested zips are opened at most this deep (a zip in a zip in the package); deeper is refused.</summary>
    const int MaxNestDepth = 2;
    /// <summary>A nested zip is inflated into memory to be opened; one that inflates to more than this (whatever its stated size) is refused as unverifiable.</summary>
    internal static long MaxNestedBytes = 512L * 1024 * 1024;
    int _nestDepth;
    bool _inSourceArchive;
    int _sourceArchivesSeen;

    static readonly (string Name, byte[] Magic)[] ArchiveMagics =
    [
        ("zip", [0x50, 0x4B, 0x03, 0x04]), ("zip", [0x50, 0x4B, 0x05, 0x06]),
        ("gzip", [0x1F, 0x8B]), ("7z", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]), ("RAR", [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07]),
    ];

    bool IsSourceArchive(string name)
    {
        foreach (var raw in SourceArchivePrefixes)
        {
            string item = raw.Replace('\\', '/').TrimStart('/');
            if (item.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name.Equals(item, StringComparison.OrdinalIgnoreCase)
                : name.StartsWith(item.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Extensionless file names the binary packages may hold (the Linux server executable and its helper, licences).</summary>
    public static readonly string[] AllowedBareNames = ["LICENSE", "SmServer", "createdump", "COPYRIGHT"];


    static readonly Regex UserPath = new(@"[A-Za-z]:[\\/]+Users[\\/]+(?<name>[^\\/\s""'<>|*?:\x00-\x1f]{1,64})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex HomePath = new(@"(?<![\w.])/(?:home|Users)/(?<name>[A-Za-z_][A-Za-z0-9_.-]{0,31})/", RegexOptions.Compiled);
    // A webhook is a snowflake id and a 60+ character token in reality; the shortest shape accepted here is 17 digits and 30 characters,
    // so test fixtures such as "webhooks/123/abc" are not flagged.
    static readonly Regex Webhook = new(@"discord(?:app)?\.com/api(?:/v\d+)?/webhooks/(?<id>\d{17,20})/(?<token>[A-Za-z0-9_-]{30,})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex BotToken = new(@"(?<![A-Za-z0-9_-])[MNO][A-Za-z0-9_-]{23,25}\.[A-Za-z0-9_-]{6}\.[A-Za-z0-9_-]{27,}", RegexOptions.Compiled);

    static readonly Regex Email = new(@"(?<![A-Za-z0-9._%+-])[A-Za-z0-9._%+-]+@(?<domain>[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,})", RegexOptions.Compiled);

    /// <summary>Addresses that may appear in a public package: GitHub's noreply addresses, the commit trailer's noreply@anthropic.com, reserved example domains.</summary>
    static bool IsPublicEmailDomain(string domain) =>
        domain.Equals("users.noreply.github.com", StringComparison.OrdinalIgnoreCase) || domain.Equals("noreply.github.com", StringComparison.OrdinalIgnoreCase) ||
        domain.Equals("anthropic.com", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) ||
        domain.Equals("example.com", StringComparison.OrdinalIgnoreCase) || domain.Equals("example.org", StringComparison.OrdinalIgnoreCase) || domain.Equals("example.net", StringComparison.OrdinalIgnoreCase);

    static readonly HashSet<string> PlaceholderUsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "default", "default user", "all users", "user", "username", "name", "you", "me", "your", "yourname", "someone", "example", "test", "runner", "nobody",
    };

    // .zip: its compressed bytes are not text (its entries are scanned one by one, ScanNestedZip).
    static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase) { ".dll", ".exe", ".so", ".dylib", ".pdb", ".a", ".o", ".pck", ".node", ".zip" };

    readonly Dictionary<long, List<string>> _forbiddenBySize = new();
    readonly Dictionary<string, string> _forbiddenHashes = new();
    readonly List<string> _secrets = [];

    static readonly HashSet<string> SourceTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".md", ".txt", ".json", ".ps1", ".py", ".csproj", ".cmd", ".sh", ".cfg", ".tscn", ".props", ".xml", ".yml", ".yaml", ".slnx", ".gd", ".log", ".csv", ".html", ".js",
    };

    /// <summary>
    /// Remembers every file under <paramref name="folder"/> (read-only: sizes only) so a package file with identical content to one of them is refused.
    /// <paramref name="skipSourceText"/>: leave out plain source and document files (the private cache holds scratch copies of this repository's own source).
    /// </summary>
    public int AddForbiddenTree(string folder, bool skipSourceText = false)
    {
        // Fail closed: a folder that is not there, or a file that cannot be sized, is an error and never an empty index.
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("the folder to compare against does not exist");
        int count = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            long size = new FileInfo(file).Length;
            if (size < MinHashedSize) continue;
            if (skipSourceText && SourceTextExtensions.Contains(Path.GetExtension(file))) continue;
            if (!_forbiddenBySize.TryGetValue(size, out var list)) _forbiddenBySize[size] = list = [];
            list.Add(file);
            count++;
        }
        return count;
    }

    /// <summary>A secret value (a webhook URL, a token, a local game path): any package that contains it, as ASCII or UTF-16, is refused. Values under 8 characters are ignored.</summary>
    public void AddSecret(string value)
    {
        if (value.Length >= 8 && !_secrets.Contains(value)) _secrets.Add(value);
    }

    /// <summary>
    /// Reads the string values of discord.* (objects and arrays) and gameDir from a config/local.json or a settings.json as secrets. Never prints them.
    /// Throws when the file cannot be read or parsed (fail closed: an unreadable secrets file must not make the guard weaker).
    /// </summary>
    public int AddSecretsFromConfig(string path)
    {
        int before = _secrets.Count;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        // Every string under "discord" and the "gameDir" value (also with backslashes) are secrets.
        void Walk(System.Text.Json.JsonElement e, bool secret, bool isPath)
        {
            switch (e.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String when e.GetString() is { } s:
                    if (secret || isPath) AddSecret(s);
                    if (isPath) AddSecret(s.Replace('/', '\\'));
                    break;
                case System.Text.Json.JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject()) Walk(p.Value, secret, isPath);
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) Walk(item, secret, isPath);
                    break;
            }
        }
        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var p in doc.RootElement.EnumerateObject()) Walk(p.Value, p.Name == "discord", p.Name == "gameDir");
        return _secrets.Count - before;
    }

    /// <summary>The values of the environment variables SMREIMPL_DISCORD_* (the webhook URLs and the bot token the tools read instead of the config file).</summary>
    public int AddSecretsFromEnvironment(Func<string, string?> getEnv, IEnumerable<string> names)
    {
        int before = _secrets.Count;
        foreach (var name in names)
            if (name.StartsWith("SMREIMPL_DISCORD_", StringComparison.OrdinalIgnoreCase) && getEnv(name) is { } value) AddSecret(value.Trim());
        return _secrets.Count - before;
    }

    /// <summary>Scans a zip file or a folder; entry names in findings are relative with forward slashes.</summary>
    public List<ScanFinding> ScanPackage(string path)
    {
        var findings = new List<ScanFinding>();
        string package = Path.GetFileName(path.TrimEnd('\\', '/'));
        _verifiedPacks = 0;
        _sourceArchivesSeen = 0;
        if (Directory.Exists(path))
        {
            string root = Path.GetFullPath(path);
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                string entry = Path.GetRelativePath(root, file).Replace('\\', '/');
                ScanEntry(package, entry, new FileInfo(file).Length, () => File.OpenRead(file), findings);
            }
            // An empty folder named like private data is still a leak of its name; folders are checked through their files only.
        }
        else if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using var gz = new System.IO.Compression.GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            using var tar = new System.Formats.Tar.TarReader(gz);
            while (tar.GetNextEntry(copyData: true) is { } e)
            {
                if (e.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile) || e.DataStream is null) continue;
                var bytes = new MemoryStream();
                e.DataStream.CopyTo(bytes);
                var data = bytes.ToArray();
                ScanEntry(package, e.Name, data.Length, () => new MemoryStream(data, writable: false), findings);
            }
        }
        else if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // A single file (a release attachment such as a markdown document).
            ScanEntry(package, package, new FileInfo(path).Length, () => File.OpenRead(path), findings);
        }
        else
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                var entry = e;
                ScanEntry(package, e.FullName, e.Length, () => entry.Open(), findings);
            }
        }
        if (RequireVerifiedPack && _verifiedPacks == 0)
            findings.Add(new ScanFinding(package, "(package)", "no-verified-pack", "the package holds no parseable Godot pack listing the client's own files (project.binary), so it is not the client"));
        return findings;
    }

    /// <summary>Applies every rule to one package entry. <paramref name="open"/> opens a fresh stream each time.</summary>
    public void ScanEntry(string package, string entryName, long length, Func<Stream> open, List<ScanFinding> findings)
    {
        string name = entryName.Replace('\\', '/').TrimStart('/');
        string[] parts = name.Split('/');
        string file = parts[^1];
        string ext = Path.GetExtension(file).ToLowerInvariant();
        void Add(string rule, string detail) => findings.Add(new ScanFinding(package, name, rule, detail));

        byte[] head = new byte[8];
        int headLength = 0;
        using (var s = open())
        {
            while (headLength < head.Length) { int n = s.Read(head, headLength, head.Length - headLength); if (n <= 0) break; headLength += n; }
        }

        bool godotPack = ext == ".pck" && headLength >= 4 && head[0] == (byte)'G' && head[1] == (byte)'D' && head[2] == (byte)'P' && head[3] == (byte)'C';
        if (ForbiddenExtensions.Contains(ext) && !godotPack) Add("forbidden-extension", $"'{ext}' is a game-content extension");
        foreach (var folder in parts[..^1])
            if (ForbiddenFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) Add("forbidden-folder", $"under a private folder ('{folder}')");
        if (ForbiddenNames.Contains(file, StringComparer.OrdinalIgnoreCase) || file.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
            Add("forbidden-file", "holds local configuration or secrets");
        foreach (var (magicName, magic) in Magics)
            if (headLength >= magic.Length && head.AsSpan(0, magic.Length).SequenceEqual(magic)) Add("game-format-magic", magicName);

        if (ext == ".exe" || godotPack) CheckGodotPack(file, ext, length, open, Add);

        string? archive = null;
        foreach (var (archiveName, magic) in ArchiveMagics)
            if (headLength >= magic.Length && head.AsSpan(0, magic.Length).SequenceEqual(magic)) { archive = archiveName; break; }
        bool zipLike = archive == "zip" || (archive is null && ext == ".zip");     // a .zip without the magic is opened anyway: a broken one is a refusal
        bool sourceArchive = zipLike && _nestDepth == 0 && IsSourceArchive(name);
        if (sourceArchive && ++_sourceArchivesSeen > 1) { Add("duplicate-source-archive", "the package holds the source offer more than once"); sourceArchive = false; }
        if (archive is not null and not "zip") Add("unscannable-archive", $"a {archive} archive cannot be looked into, so it is refused");
        if (_inSourceArchive)
        {
            if (archive is not null || ext == ".zip") Add("archive-in-source", "an archive inside the source offer");
            else if (!SourceMediaExtensions.Contains(ext) && IsBinary(open)) Add("binary-in-source", $"a non-text file ('{(ext.Length > 0 ? ext : "no extension")}') inside the source offer");
        }
        if (AllowedExtensions is { } allowed && !sourceArchive)
        {
            bool bare = ext.Length == 0;
            if (bare ? !AllowedBareNames.Contains(file, StringComparer.OrdinalIgnoreCase) : !allowed.Contains(ext))
                Add("unexpected-type", bare ? "a file without extension that the binary packages do not contain" : $"'{ext}' is not a type this package may contain");
        }

        if (_forbiddenBySize.TryGetValue(length, out var candidates) && length >= MinHashedSize)
        {
            string hash = Hash(open);
            foreach (var candidate in candidates)
            {
                string other;
                try { other = HashOfForbidden(candidate); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Add("unreadable-private-file", "a private file of the same size could not be read, so the comparison failed (fail closed)");
                    continue;
                }
                if (string.Equals(other, hash, StringComparison.Ordinal)) { Add("copy-of-private-file", "identical to a file of the game install or the private cache"); break; }
            }
        }

        ScanContent(name, ext, IsLicenceText(parts), open, Add);

        // Nothing hides inside a zip in the package: its entries are scanned as "outer.zip!/inner/path" (an unreadable one throws: the command refuses).
        if (zipLike) ScanNestedZip(package, name, length, open, sourceArchive, findings);
    }

    /// <summary>A NUL byte in the first 8 KB: not text.</summary>
    static bool IsBinary(Func<Stream> open)
    {
        var buffer = new byte[8192];
        using var s = open();
        int total = 0;
        while (total < buffer.Length) { int n = s.Read(buffer, total, buffer.Length - total); if (n <= 0) break; total += n; }
        return Array.IndexOf(buffer, (byte)0, 0, total) >= 0;
    }

    void ScanNestedZip(string package, string name, long length, Func<Stream> open, bool sourceArchive, List<ScanFinding> findings)
    {
        if (_nestDepth >= MaxNestDepth) { findings.Add(new ScanFinding(package, name, "nested-too-deep", "a zip nested this deep is not checked, so it is refused")); return; }
        if (length > MaxNestedBytes) { findings.Add(new ScanFinding(package, name, "nested-unverifiable", "the nested zip is too large to check")); return; }
        // Inflate with a bound on the bytes actually read: a stated size proves nothing.
        var bytes = new MemoryStream();
        using (var s = open())
        {
            var buffer = new byte[81920];
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bytes.Length + n > MaxNestedBytes) { findings.Add(new ScanFinding(package, name, "nested-unverifiable", "the nested zip inflates beyond the size this guard checks")); return; }
                bytes.Write(buffer, 0, n);
            }
        }
        bytes.Position = 0;
        var profile = AllowedExtensions;
        bool wasInSource = _inSourceArchive;
        if (sourceArchive) { AllowedExtensions = null; _inSourceArchive = true; }
        _nestDepth++;
        try
        {
            using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                var entry = e;
                ScanEntry(package, name + "!/" + e.FullName, e.Length, () => entry.Open(), findings);
            }
        }
        finally { _nestDepth--; AllowedExtensions = profile; _inSourceArchive = wasInSource; }
    }

    string HashOfForbidden(string path)
    {
        if (_forbiddenHashes.TryGetValue(path, out var h)) return h;
        h = Hash(() => File.OpenRead(path));      // throws on an unreadable file: the caller reports it, never treats it as "no match"
        return _forbiddenHashes[path] = h;
    }

    static string Hash(Func<Stream> open)
    {
        using var s = open();
        return Convert.ToHexString(SHA256.HashData(s));
    }

    static readonly Regex ImportedName = new(@"\.(?<ext>[A-Za-z0-9_]+)-[0-9a-fA-F]{32}\.[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>
    /// A file name stored in a Godot pack that must not be there: a game-content extension (not .bin: Godot's own uid cache is one) or a private folder
    /// (not .godot: the exported imports live there). A ".remap" or ".import" suffix and the "name.ext-&lt;md5&gt;.ctex" form of imported files are looked through.
    /// </summary>
    internal static string? PackNameProblem(string name)
    {
        string n = name.StartsWith("res://", StringComparison.Ordinal) ? name["res://".Length..] : name;
        string[] parts = n.Split('/');
        string file = parts[^1];
        if (file.EndsWith(".remap", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".import", StringComparison.OrdinalIgnoreCase)) file = file[..file.LastIndexOf('.')];
        var imported = ImportedName.Match(file);
        string ext = imported.Success ? "." + imported.Groups["ext"].Value.ToLowerInvariant() : Path.GetExtension(file).ToLowerInvariant();
        if (ext != ".bin" && ForbiddenExtensions.Contains(ext)) return $"the Godot pack lists a '{ext}' file";
        foreach (var folder in parts[..^1])
            if (!folder.Equals(".godot", StringComparison.OrdinalIgnoreCase) && ForbiddenFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return $"the Godot pack lists a file under a private folder ('{folder}')";
        return null;
    }

    /// <summary>The directory of an embedded or standalone Godot pack, parsed from its length-prefixed names (no text sniffing), or why it cannot be trusted.</summary>
    internal sealed record PackListing(bool Ok, string Error, List<string> Names);

    /// <summary>
    /// Parses a Godot pack of format version 3 or 4 (Godot 4.4 and later; 4.7.2 writes 4): the header (flags: an encrypted directory is refused), the directory at
    /// <c>dir_offset</c> from the pack start, and every entry (path_len, padded path, offset, size, md5, flags; an encrypted file is refused). In an executable the
    /// pack ends with its size (u64) and "GDPC". Anything that does not fit is refused: the caller fails closed.
    /// </summary>
    internal static PackListing ParsePack(byte[] data, bool embedded)
    {
        static PackListing Bad(string why) => new(false, why, []);
        long end = data.Length, start = 0;
        if (embedded)
        {
            if (data.Length < 12 || !(data[^4] == 'G' && data[^3] == 'D' && data[^2] == 'P' && data[^1] == 'C')) return Bad("no pack trailer (GDPC) at the end of the file");
            ulong size = BitConverter.ToUInt64(data, data.Length - 12);
            if (size < 104 || size > (ulong)(data.Length - 12)) return Bad("the pack size in the trailer is impossible");
            end = data.Length - 12;
            start = end - (long)size;
        }
        if (end - start < 104 || !(data[start] == 'G' && data[start + 1] == 'D' && data[start + 2] == 'P' && data[start + 3] == 'C')) return Bad("no GDPC header where the pack should start");
        uint version = BitConverter.ToUInt32(data, (int)start + 4);
        if (version is not (3 or 4)) return Bad($"pack format version {version} is not one this guard can read");
        uint flags = BitConverter.ToUInt32(data, (int)start + 20);
        if ((flags & 1) != 0) return Bad("the pack directory is encrypted, so its files cannot be checked");
        ulong dirOffset = BitConverter.ToUInt64(data, (int)start + 32);
        if (dirOffset < 40 || dirOffset > (ulong)(end - start - 4)) return Bad("the directory offset lies outside the pack");
        long pos = start + (long)dirOffset;
        uint count = BitConverter.ToUInt32(data, (int)pos);
        pos += 4;
        if (count == 0 || count > 200_000) return Bad($"implausible file count {count}");
        var names = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            if (pos + 4 > end) return Bad("the directory runs past the end of the pack");
            uint len = BitConverter.ToUInt32(data, (int)pos);
            pos += 4;
            if (len == 0 || len > 4096 || pos + len + 36 > end) return Bad("a directory entry is malformed");
            names.Add(Encoding.UTF8.GetString(data, (int)pos, (int)len).TrimEnd('\0'));
            pos += len;
            uint fileFlags = BitConverter.ToUInt32(data, (int)pos + 32);
            if ((fileFlags & 1) != 0) return Bad("a packed file is encrypted, so it cannot be checked");
            pos += 36;
        }
        return new PackListing(true, "", names);
    }

    /// <summary>The names every real client pack has; a pack without them is not the client's.</summary>
    static readonly string[] ExpectedPackEntries = ["project.binary"];

    void CheckGodotPack(string fileName, string ext, long length, Func<Stream> open, Action<string, string> add)
    {
        bool isClientExe = fileName.Equals("sm-reimpl.exe", StringComparison.OrdinalIgnoreCase);
        bool standalone = ext == ".pck";
        if (!isClientExe && !standalone && ext != ".exe") return;
        if (length > int.MaxValue - 64) { add("pack-unverifiable", "the file is too large to verify"); return; }
        var data = new byte[length];
        using (var s = open()) s.ReadExactly(data);
        bool looksLikePack = standalone || (data.Length >= 4 && data[^4] == 'G' && data[^3] == 'D' && data[^2] == 'P' && data[^1] == 'C');
        if (!looksLikePack && !isClientExe) return;       // some other executable: nothing embedded to check
        var listing = ParsePack(data, embedded: !standalone);
        if (!listing.Ok) { add("pack-unverifiable", $"the Godot pack cannot be verified: {listing.Error}"); return; }
        foreach (var expected in ExpectedPackEntries)
            if (!listing.Names.Contains(expected)) { add("pack-unverifiable", $"the Godot pack lacks '{expected}', so it is not the client's"); return; }
        _verifiedPacks++;
        var problems = listing.Names.Select(PackNameProblem).Where(p => p is not null).Distinct().ToList();
        foreach (var p in problems.Take(5)) add("game-content-in-pack", p!);
    }

    /// <summary>Licence and notice texts of third parties name their authors by e-mail address; the personal-address rule skips them.</summary>
    static bool IsLicenceText(string[] parts)
    {
        string file = parts[^1];
        return parts[..^1].Contains("licenses", StringComparer.OrdinalIgnoreCase) ||
               new[] { "LICENSE", "THIRD", "NOTICE", "COPYING" }.Any(p => file.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    void ScanContent(string name, string ext, bool licenceText, Func<Stream> open, Action<string, string> add)
    {
        bool userPath = false, homePath = false, webhook = false, token = false, email = false;
        var secretHit = new HashSet<int>();
        bool binary = BinaryExtensions.Contains(ext);
        var buffer = new byte[ReadBufferBytes];
        var chars = new char[buffer.Length];
        using var stream = open();
        int carry = 0;
        while (true)
        {
            int read = 0;
            while (carry + read < buffer.Length) { int n = stream.Read(buffer, carry + read, buffer.Length - carry - read); if (n <= 0) break; read += n; }
            int total = carry + read;
            if (total == 0) break;
            // Latin-1 with the zero bytes dropped: ASCII stays as it is and UTF-16LE text of ASCII letters becomes plain ASCII too.
            int len = 0;
            for (int i = 0; i < total; i++) { byte b = buffer[i]; if (b != 0) chars[len++] = (char)b; }
            var text = new string(chars, 0, len);

            if (!userPath && text.Contains("Users", StringComparison.OrdinalIgnoreCase))
                foreach (Match m in UserPath.Matches(text))
                    if (!IsPlaceholderUser(m.Groups["name"].Value)) { userPath = true; break; }
            if (!homePath && !binary && (text.Contains("/home/", StringComparison.Ordinal) || text.Contains("/Users/", StringComparison.Ordinal)))
                foreach (Match m in HomePath.Matches(text))
                    if (!IsPlaceholderUser(m.Groups["name"].Value)) { homePath = true; break; }
            if (!email && !binary && !licenceText && text.Contains('@'))
                foreach (Match m in Email.Matches(text))
                    if (!IsPublicEmailDomain(m.Groups["domain"].Value)) { email = true; break; }
            if (!webhook && text.Contains("webhooks/", StringComparison.OrdinalIgnoreCase) && Webhook.IsMatch(text)) webhook = true;
            if (!token && text.Contains('.') && BotToken.IsMatch(text)) token = true;
            for (int i = 0; i < _secrets.Count; i++)
                if (!secretHit.Contains(i) && text.Contains(_secrets[i], StringComparison.Ordinal)) secretHit.Add(i);

            if (read == 0) break;
            carry = Math.Min(OverlapBytes, total);
            Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
        }
        if (userPath) add("absolute-user-path", "contains an absolute user path (C:\\Users\\<name>\\...; name not printed)");
        if (homePath) add("absolute-user-path", "contains an absolute home path (/home/<name>/ or /Users/<name>/; name not printed)");
        if (email) add("personal-email", "contains an e-mail address that is not a GitHub/Anthropic noreply or example address (not printed)");
        if (webhook) add("discord-webhook", "contains a Discord webhook URL (not printed)");
        if (token) add("discord-bot-token", "contains a string shaped like a Discord bot token (not printed)");
        if (secretHit.Count > 0) add("local-secret-value", $"contains {secretHit.Count} value(s) read from the local config (not printed)");
    }

    static bool IsPlaceholderUser(string name) =>
        PlaceholderUsers.Contains(name) || name.StartsWith('<') || name.StartsWith('%') || name.StartsWith('{') || name.StartsWith('$') || name.StartsWith('[') || name.StartsWith('*') || name.StartsWith("...", StringComparison.Ordinal);
}
