using SmContent;

namespace SmTool;

/// <summary>
/// <c>smtool scan-package [--against &lt;dir&gt;]... [--against-install &lt;game-dir|auto&gt;] [--secrets &lt;config.json&gt;]... [--secrets-env]
/// [--require-install] [--require-secrets] [--require-pack] [--profile binary] [--source-archive &lt;entry prefix&gt;]... &lt;zip|tar.gz|folder|file&gt;...</c>
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
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--against" when i + 1 < args.Length:
                        Console.WriteLine($"guard: {scanner.AddForbiddenTree(args[++i], skipSourceText: true)} private file(s) indexed");
                        break;
                    case "--against-install" when i + 1 < args.Length:
                        string? dir = args[++i] is var given && !given.Equals("auto", StringComparison.OrdinalIgnoreCase) ? given : null;
                        try
                        {
                            string install = GameInstall.Locate(dir);
                            int indexed = scanner.AddForbiddenTree(install);
                            Console.WriteLine($"guard: {indexed} game file(s) indexed (read only)");
                            scanner.AddSecret(install);
                            installIndexed = indexed > 0;
                        }
                        catch (DirectoryNotFoundException) { Console.WriteLine("guard: game install not found, its files are not compared"); }
                        break;
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
            Console.Error.WriteLine("usage: smtool scan-package [--against <dir>]... [--against-install <game-dir|auto>] [--secrets <config.json>]... [--secrets-env] [--require-install] [--require-secrets] [--require-pack] [--profile binary] [--source-archive <entry prefix>]... <zip|tar.gz|folder|file>...");
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
        return bad == 0 ? 0 : 1;
    }
}
