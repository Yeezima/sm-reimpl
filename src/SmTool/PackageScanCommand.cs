using SmContent;

namespace SmTool;

/// <summary>
/// <c>smtool scan-package [--against &lt;dir&gt;]... [--against-install &lt;game-dir|auto&gt;] [--secrets &lt;config.json&gt;]... [--secrets-env]
/// [--require-install] [--require-secrets] [--require-pack] [--profile binary] [--source-archive &lt;entry prefix&gt;]... [--index-cache &lt;dir&gt; [--rehash]] &lt;zip|tar.gz|folder|file&gt;...</c>
/// Runs the release content guard (<see cref="PackageScanner"/>). Zips inside a package are scanned entry by entry; <c>--source-archive source/</c> marks the
/// nested zips under that prefix as the GPL source offer (allowed in a binary package, their entries scanned without the type allowlist). Exit 0 = clean, 1 = findings, 2 = usage error or an input that could not be used.
/// The guard fails closed: a missing --against folder, an install that was asked for (--require-install) but not indexed, a secrets file that is
/// unreadable or yields no values (--require-secrets), or a forbidden file that cannot be read are errors, never an empty comparison.
/// </summary>
public static class PackageScanCommand
{
    /// <summary>Extensions the binary packages (client, servers) may contain; anything else is an unexpected type (--profile binary).</summary>
    public static readonly string[] BinaryProfileExtensions =
    [
        ".exe", ".dll", ".so", ".json", ".cmd", ".ps1", ".sh", ".txt", ".md",
        ".jpg",   // the owner's menu background and hub planet, copied beside the exe by release.ps1
    ];

    /// <summary>Test hook: called with each package path right before it is scanned (a test makes it throw to prove an unexpected crash exits 2).</summary>
    internal static Action<string>? BeforeScanHook;

    /// <summary>Any exception that escapes the scan is an exit code 2 (never a pass): the caller treats every non-zero code as a refusal.</summary>
    public static int Run(string[] args)
    {
        try { return RunCore(args); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"scan-package: unexpected {e.GetType().Name}; refusing (the guard did not finish)");
            return 2;
        }
    }

    static int RunCore(string[] args)
    {
        var scanner = new PackageScanner();
        var packages = new List<string>();
        bool requireInstall = false, requireSecrets = false, installIndexed = false, binaryProfile = false;
        var secretFiles = new List<string>();
        var againstDirs = new List<string>();
        var installArgs = new List<string>();
        string? indexCache = null;
        bool rehash = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--against" when i + 1 < args.Length:
                        againstDirs.Add(args[++i]);
                        break;
                    case "--against-install" when i + 1 < args.Length:
                        installArgs.Add(args[++i]);
                        break;
                    case "--index-cache" when i + 1 < args.Length:
                        indexCache = Path.GetFullPath(args[++i]);
                        break;
                    case "--rehash": rehash = true; break;
                    case "--secrets" when i + 1 < args.Length:
                        secretFiles.Add(args[++i]);
                        break;
                    case "--secrets-env":
                        var names = Environment.GetEnvironmentVariables().Keys.Cast<string>().ToList();
                        Console.WriteLine($"guard: {scanner.AddSecretsFromEnvironment(Environment.GetEnvironmentVariable, names)} secret value(s) loaded from SMREIMPL_DISCORD_* (not printed)");
                        break;
                    case "--require-install": requireInstall = true; break;
                    case "--require-secrets": requireSecrets = true; break;
                    case "--require-pack": scanner.RequireVerifiedPack = true; break;
                    case "--source-archive" when i + 1 < args.Length:
                        scanner.SourceArchivePrefixes.Add(args[++i].Replace('\\', '/').TrimStart('/'));
                        break;
                    case "--profile" when i + 1 < args.Length:
                        if (args[++i] != "binary") { Console.Error.WriteLine("scan-package: --profile knows 'binary' only"); return 2; }
                        binaryProfile = true;
                        break;
                    case var flag when flag.StartsWith("--", StringComparison.Ordinal):
                        Console.Error.WriteLine($"scan-package: unknown or incomplete option '{flag}'");
                        return 2;
                    default:
                        packages.Add(args[i]);
                        break;
                }
            }

            // The comparison trees, after every option is known (--index-cache may come after them). The fingerprint index only saves hashing time:
            // the trees are enumerated in full on every run, and a file is reused only when its path, size and last-write time are unchanged.
            var installs = new List<string>();
            foreach (var given in installArgs)
            {
                string? dir = given.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : given;
                try { installs.Add(GameInstall.Locate(dir)); }
                catch (DirectoryNotFoundException) { Console.WriteLine("guard: game install not found, its files are not compared"); }
            }
            // Never write inside the install or inside a comparison tree (links and junctions resolved), except the private cache's own guard folder:
            // an index folder anywhere else is ignored and the guard runs without stored hashes.
            if (indexCache is not null)
            {
                string? problem;
                try
                {
                    problem = installs.Select(i => GuardIndex.LocationProblem(indexCache, againstDirs.Where(Directory.Exists), i)).FirstOrDefault(p => p is not null)
                              ?? GuardIndex.LocationProblem(indexCache, againstDirs.Where(Directory.Exists), null);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problem = $"its location could not be resolved ({e.GetType().Name})"; }
                if (problem is not null) { Console.WriteLine($"guard: the --index-cache folder is ignored: {problem}; nothing is written there"); indexCache = null; }
            }
            foreach (var folder in againstDirs)
            {
                GuardIndex? index = null;
                if (indexCache is not null && Directory.Exists(folder))
                    index = GuardIndex.Load(GuardIndex.FileFor(indexCache, "private", folder, skipSourceText: true), folder, null, skipSourceText: true);
                if (index is not null) index.TrustStoredHashes = !rehash;
                int indexed = scanner.AddForbiddenTree(folder, skipSourceText: true, index);
                Console.WriteLine($"guard: {indexed} private file(s) indexed{IndexNote(index)}");
            }
            foreach (var install in installs)
            {
                GuardIndex? index = null;
                if (indexCache is not null)
                    index = GuardIndex.Load(GuardIndex.FileFor(indexCache, "install", install, skipSourceText: false), install, GuardIndex.SteamBuildId(install), skipSourceText: false);
                if (index is not null) index.TrustStoredHashes = !rehash;
                try
                {
                    int indexed = scanner.AddForbiddenTree(install, skipSourceText: false, index);
                    Console.WriteLine($"guard: {indexed} game file(s) indexed (read only){IndexNote(index)}");
                    scanner.AddSecret(install);
                    installIndexed = installIndexed || indexed > 0;
                }
                catch (DirectoryNotFoundException) { Console.WriteLine("guard: game install not found, its files are not compared"); }
            }
            if (againstDirs.Count + installArgs.Count > 0) Console.WriteLine($"guard: comparison trees indexed in {clock.Elapsed.TotalSeconds:F1} s");

            foreach (var file in secretFiles)
            {
                if (!File.Exists(file)) { Console.Error.WriteLine($"scan-package: secrets file '{Path.GetFileName(file)}' not found"); return 2; }
                int loaded = scanner.AddSecretsFromConfig(file);
                Console.WriteLine($"guard: {loaded} secret value(s) loaded from {Path.GetFileName(file)} (not printed)");
                // Required of the secrets config (local.json); other sources (the remembered game folder in settings.json) may legitimately hold nothing.
                if (requireSecrets && loaded == 0 && Path.GetFileName(file).Equals("local.json", StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine($"scan-package: --require-secrets, but {Path.GetFileName(file)} yielded no secret values"); return 2; }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"scan-package: an input could not be used ({e.GetType().Name}); refusing to scan with a weaker guard");
            return 2;
        }
        // --require-secrets: a secrets config (local.json) must have been given and loaded. A machine with no config/local.json at all cannot vouch that
        // nothing is leaked, so it does not release.
        if (requireSecrets && !secretFiles.Any(f => Path.GetFileName(f).Equals("local.json", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("scan-package: --require-secrets, but no local.json was given (--secrets <config/local.json>)");
            return 2;
        }
        if (requireInstall && !installIndexed)
        {
            Console.Error.WriteLine("scan-package: --require-install, but the game install was not found or had no files to compare");
            return 2;
        }
        if (binaryProfile) scanner.AllowedExtensions = new HashSet<string>(BinaryProfileExtensions, StringComparer.OrdinalIgnoreCase);
        if (packages.Count == 0)
        {
            Console.Error.WriteLine("usage: smtool scan-package [--against <dir>]... [--against-install <game-dir|auto>] [--secrets <config.json>]... [--secrets-env] [--require-install] [--require-secrets] [--require-pack] [--profile binary] [--source-archive <entry prefix>]... [--index-cache <dir> [--rehash]] <zip|tar.gz|folder|file>...");
            return 2;
        }

        int bad = 0;
        foreach (var package in packages)
        {
            if (!File.Exists(package) && !Directory.Exists(package)) { Console.Error.WriteLine($"scan-package: '{package}' not found"); return 2; }
            // The loose release documents (.md) are text: the binary profile is for the packages only.
            bool isPackage = Directory.Exists(package) || package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || package.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);
            var profile = scanner.AllowedExtensions;
            if (!isPackage) scanner.AllowedExtensions = null;
            List<ScanFinding> findings;
            try { BeforeScanHook?.Invoke(package); findings = scanner.ScanPackage(package); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Console.Error.WriteLine($"scan-package: '{Path.GetFileName(package)}' could not be read completely ({e.GetType().Name})");
                return 2;
            }
            finally { scanner.AllowedExtensions = profile; }
            if (findings.Count == 0) Console.WriteLine($"PASS {Path.GetFileName(package)}");
            else
            {
                bad++;
                Console.WriteLine($"FAIL {Path.GetFileName(package)}: {findings.Count} finding(s)");
                foreach (var f in findings.Take(50)) Console.WriteLine("  " + f);
                if (findings.Count > 50) Console.WriteLine($"  ... and {findings.Count - 50} more");
            }
        }
        Console.WriteLine($"guard: done in {clock.Elapsed.TotalSeconds:F1} s; {scanner.ForbiddenHashesComputed} file(s) of the comparison trees hashed in this run");
        // The fingerprints are a cache: a failure to write them costs time on the next run, never the guard's answer.
        try { scanner.SaveIndexes(); }
        // ArgumentException / InvalidOperationException: Utf8JsonWriter refuses a name it cannot encode (an odd file name, e.g. a lone surrogate).
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        { Console.WriteLine($"guard: the fingerprint index was not saved ({e.GetType().Name}); the next run hashes again"); }
        return bad == 0 ? 0 : 1;
    }

    static string IndexNote(GuardIndex? index) => index is null ? "" :
        index.TrustStoredHashes ? $"; fingerprints {index.Status}, {index.Reused} reused" : $"; fingerprints {index.Status}, not trusted (--rehash): every needed file hashed again";
}
