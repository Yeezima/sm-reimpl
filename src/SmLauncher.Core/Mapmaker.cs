using System.Text.RegularExpressions;

namespace SmLauncher.Core;

/// <summary>
/// One official download the Mapmaker button may need on first use: the Godot editor (MIT licence, godotengine/godot GitHub release) or, when the PC has no
/// .NET 10 runtime, Microsoft's .NET runtime zip. The URL, exact size and SHA-512 are pinned here; for Godot the release's own SHA512-SUMS.txt must say the
/// same, or nothing is downloaded. Nothing else is trusted: a file with another hash is deleted.
/// </summary>
public sealed record ToolPackage(string Id, string Title, string Url, long Size, string Sha512, string? SumsUrl, string? SumsName, string[] Required)
{
    public string AssetName => Url[(Url.LastIndexOf('/') + 1)..];
    /// <summary>The folder under tools\ the package is unpacked into (Id and version, so a newer pin is a new folder).</summary>
    public string DirName { get; init; } = Id;
}

public static class MapmakerTools
{
    /// <summary>Godot 4.7.2-stable .NET, Windows 64 bit: the editor and its console build (same pin as client.cmd, tools/get-godot.ps1 and release.ps1).</summary>
    public static readonly ToolPackage Godot = new(
        "godot", "the Godot editor",
        "https://github.com/godotengine/godot/releases/download/4.7.2-stable/Godot_v4.7.2-stable_mono_win64.zip",
        116_582_061,
        "79229fd112b0c9cbeab82363a4ef7be18ea70f1caf86bf912789335b136fbe7e01db0053a33461438c5da1c680c17bbb10040bd09bedc51221cd4423d0367757",
        "https://github.com/godotengine/godot/releases/download/4.7.2-stable/SHA512-SUMS.txt",
        "Godot_v4.7.2-stable_mono_win64.zip",
        [@"Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe", @"Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe", @"Godot_v4.7.2-stable_mono_win64\GodotSharp\Api"])
    { DirName = "godot-4.7.2" };

    /// <summary>The .NET 10 runtime (not the SDK), win-x64, from Microsoft's release metadata (builds.dotnet.microsoft.com, release 10.0.15). Used only when no .NET 10 runtime is installed.</summary>
    public static readonly ToolPackage DotNet = new(
        "dotnet", "the .NET runtime",
        "https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.15/dotnet-runtime-10.0.15-win-x64.zip",
        36_980_907,
        "06f0bedd59ae4ac722ada805a6d6bbf605bcdc790c71f59b7003878072902b5cde5dec8c40c0baa81192660d4748e9d79afdbdcb0c0ea9d6b821d0a044c4c6dd",
        null, null,
        [@"host\fxr", @"shared\Microsoft.NETCore.App"])
    { DirName = "dotnet-10.0.15" };

    public static string GodotExe(string godotDir, bool console = false) =>
        Path.Combine(godotDir, "Godot_v4.7.2-stable_mono_win64", console ? "Godot_v4.7.2-stable_mono_win64_console.exe" : "Godot_v4.7.2-stable_mono_win64.exe");
}

/// <summary>How the editor is started: the program, its arguments (one string each, no shell), the folder and the environment variables to add.</summary>
public sealed record MapmakerLaunch(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>The command as one line for logs and the self-check (quotes where needed).</summary>
    public string Describe() => string.Join(' ', new[] { FileName }.Concat(Arguments).Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
}

/// <summary>What the MAPMAKER button still has to do before the editor can open.</summary>
public sealed record MapmakerPlan(bool ShippedOk, string? ShippedProblem, bool NeedGodot, bool NeedDotNet, bool NeedSync, bool NeedImport)
{
    public bool NeedsDownload => NeedGodot || NeedDotNet;
    public long DownloadBytes => (NeedGodot ? MapmakerTools.Godot.Size : 0) + (NeedDotNet ? MapmakerTools.DotNet.Size : 0);
}

/// <summary>
/// The launcher's MAPMAKER button (docs/mapmaker.md, docs/releasing.md "The Mapmaker"). The playtest build ships <c>mapmaker\</c>: the Godot project with the
/// Mapmaker plugin (<c>project.godot</c>, <c>Maps\</c>, <c>addons\sm_mapmaker\</c>) and its compiled assemblies (<c>bin\</c>); no game data of any kind (the plugin
/// reads the player's own Space Marine install at run time, found as the game finds it). The Godot editor is not in the build: it is downloaded on first
/// use into <c>&lt;install&gt;\tools\</c> (shared by every version) with its SHA-512 checked, and so is a private .NET runtime when the PC has none. The
/// shipped project is copied to <c>&lt;install&gt;\mapmaker-project\</c> (the editor writes its import cache there, never into the build's folder).
/// </summary>
public sealed class MapmakerSetup(InstallLayout layout)
{
    public const string ShippedDir = "mapmaker";
    public const string BinDir = "bin";
    public static readonly string[] Needed = ["project.godot", @"Maps\Empty.tscn", @"addons\sm_mapmaker\plugin.cfg", @"addons\sm_mapmaker\MapmakerPlugin.cs", @"bin\Mapmaker.dll", @"bin\SmMapmaker.dll", @"bin\SmContent.dll"];
    /// <summary>Where Godot's editor looks for the project's compiled assembly.</summary>
    public static readonly string EditorAssemblyDir = Path.Combine(".godot", "mono", "temp", "bin", "Debug");

    /// <summary>The environment, replaceable in tests: the folders a .NET runtime may be in.</summary>
    public Func<IEnumerable<string>> RuntimeRoots { get; init; } = DefaultRuntimeRoots;

    public string ToolsDir => Path.Combine(layout.Root, "tools");
    public string GodotDir => Path.Combine(ToolsDir, MapmakerTools.Godot.DirName);
    public string DotNetDir => Path.Combine(ToolsDir, MapmakerTools.DotNet.DirName);
    public string WorkProject => Path.Combine(layout.Root, "mapmaker-project");
    /// <summary>.sm-build: the build id on the first line, then every file (relative to the project) a sync copied: only those are ever deleted by a later sync or by removal.</summary>
    public string BuildMarker => Path.Combine(WorkProject, ".sm-build");
    /// <summary>Written only after the first-time import exited with code 0 (<see cref="MarkImported"/>); deleted when a sync changed files.</summary>
    public string ImportMarker => Path.Combine(WorkProject, ".sm-imported");
    public void MarkImported() { Directory.CreateDirectory(WorkProject); File.WriteAllText(ImportMarker, DateTime.UtcNow.ToString("O")); }

    public static string Shipped(string versionDir) => Path.Combine(versionDir, ShippedDir);

    static IEnumerable<string> DefaultRuntimeRoots()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } r) yield return r;
        foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") })
            if (!string.IsNullOrEmpty(pf)) yield return Path.Combine(pf, "dotnet");
    }

    /// <summary>The root of a .NET runtime of major version 10 on this PC (DOTNET_ROOT, then Program Files), or null. Godot 4.7 needs 10.x.</summary>
    public string? FindRuntime()
    {
        foreach (var root in RuntimeRoots())
        {
            string shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
            if (!Directory.Exists(shared) || !Directory.Exists(Path.Combine(root, "host", "fxr"))) continue;
            try { if (Directory.EnumerateDirectories(shared, "10.*").Any()) return root; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public const string VerifiedMarker = ".sm-verified";

    /// <summary>Installed and verified: the files are there and the folder carries the marker written after the pinned SHA-512 matched. The same pin is never downloaded again; a new pin (another folder name or another hash) is.</summary>
    public static bool Installed(ToolPackage pkg, string dir)
    {
        string marker = Path.Combine(dir, VerifiedMarker);
        return HasAll(dir, pkg.Required) && File.Exists(marker) && File.ReadAllText(marker).Trim() == pkg.Sha512;
    }

    public bool GodotInstalled => Installed(MapmakerTools.Godot, GodotDir);
    public bool PrivateRuntimeInstalled => Installed(MapmakerTools.DotNet, DotNetDir);

    static bool HasAll(string dir, string[] required) => required.All(r => File.Exists(Path.Combine(dir, r)) || Directory.Exists(Path.Combine(dir, r)));

    /// <summary>Every file of the shipped project folder, as relative paths with backslashes (the plugin, the scene, the project file and the assemblies).</summary>
    public static List<string> ShippedFiles(string shipped) =>
        Directory.Exists(shipped)
            ? Directory.EnumerateFiles(shipped, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(shipped, f)).OrderBy(f => f, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>Checks the shipped project: the files the editor and the plugin need are there. Null when fine, else a plain sentence.</summary>
    public static string? CheckShipped(string versionDir)
    {
        string shipped = Shipped(versionDir);
        if (!Directory.Exists(shipped)) return "This build has no Mapmaker folder. Update to the newest build, or use Check files.";
        foreach (var n in Needed)
            if (!File.Exists(Path.Combine(shipped, n))) return $"The Mapmaker is incomplete in this build ({n} is missing). Use Check files.";
        return null;
    }

    public MapmakerPlan Plan(string versionDir)
    {
        string? problem = CheckShipped(versionDir);
        bool needDotNet = FindRuntime() is null && !PrivateRuntimeInstalled;
        return new MapmakerPlan(problem is null, problem, !GodotInstalled, needDotNet, NeedSync(versionDir), !File.Exists(ImportMarker));
    }

    static string BuildId(string versionDir) => Path.GetFileName(Path.TrimEndingDirectorySeparator(versionDir));

    bool NeedSync(string versionDir) =>
        !File.Exists(BuildMarker) || File.ReadLines(BuildMarker).FirstOrDefault()?.Trim() != BuildId(versionDir) || !File.Exists(Path.Combine(WorkProject, EditorAssemblyDir, "Mapmaker.dll"));

    /// <summary>The files an earlier sync copied (the lines after the build id of .sm-build), relative to the project.</summary>
    List<string> TrackedFiles() => File.Exists(BuildMarker) ? File.ReadLines(BuildMarker).Skip(1).Where(l => l.Length > 0).ToList() : [];

    /// <summary>
    /// Copies the shipped project into <see cref="WorkProject"/>: files that are missing or differ (size, SHA-256) are copied, the assemblies go to where the
    /// editor loads them (<see cref="EditorAssemblyDir"/>). Only files an earlier sync copied (listed in .sm-build) and this build no longer ships are deleted:
    /// a tester's own files (saved scenes in Maps\, anything else) and the editor's .godot cache are never touched. A sync that changed files clears the
    /// import marker. Returns the number of files copied or removed.
    /// </summary>
    public int SyncProject(string versionDir)
    {
        if (CheckShipped(versionDir) is { } problem) throw new FileNotFoundException(problem);
        string shipped = Shipped(versionDir);
        int copied = 0;
        var expected = new List<string>();
        foreach (var rel in ShippedFiles(shipped))
        {
            bool isBin = rel.StartsWith(BinDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            string targetRel = isBin ? Path.Combine(EditorAssemblyDir, rel[(BinDir.Length + 1)..]) : rel;
            expected.Add(targetRel);
            if (CopyIfDifferent(Path.Combine(shipped, rel), Path.Combine(WorkProject, targetRel))) copied++;
        }
        var keep = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase);
        foreach (var old in TrackedFiles().Where(f => !keep.Contains(f)))
        {
            string? full = PackageLayout.Inside(WorkProject, old);
            if (full is not null && File.Exists(full)) { try { File.Delete(full); copied++; } catch (IOException) { } }
        }
        if (copied > 0) { try { File.Delete(ImportMarker); } catch (IOException) { } }
        File.WriteAllLines(BuildMarker, new[] { BuildId(versionDir) }.Concat(expected));
        return copied;
    }

    /// <summary>
    /// The Mapmaker's part of the launcher's uninstall (owner, 2026-11-17: "Remove it too"): deletes <c>tools\</c> (the Godot editor and the private runtime) and, in
    /// the project copy, only the files a sync copied plus the editor's .godot cache and the launcher's markers; folders left empty go too. A tester's own
    /// files in mapmaker-project\ stay (their maps in the library folder are outside the install and never touched).
    /// </summary>
    public void Remove()
    {
        if (Directory.Exists(ToolsDir)) Directory.Delete(ToolsDir, true);
        if (!Directory.Exists(WorkProject)) return;
        foreach (var rel in TrackedFiles())
            if (PackageLayout.Inside(WorkProject, rel) is { } full && File.Exists(full)) File.Delete(full);
        string cache = Path.Combine(WorkProject, ".godot");
        if (Directory.Exists(cache)) Directory.Delete(cache, true);
        foreach (var f in new[] { BuildMarker, ImportMarker }) if (File.Exists(f)) File.Delete(f);
        PruneEmpty(WorkProject);
    }

    static void PruneEmpty(string dir)
    {
        foreach (var d in Directory.EnumerateDirectories(dir)) PruneEmpty(d);
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }

    static bool CopyIfDifferent(string from, string to)
    {
        var fi = new FileInfo(from);
        if (File.Exists(to) && new FileInfo(to).Length == fi.Length && Hashes.Sha256File(to) == Hashes.Sha256File(from)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
        return true;
    }

    /// <summary>
    /// The editor's command line: the Godot editor on the work project, visible. <paramref name="safeMode"/> adds the Compatibility renderer like the game's
    /// Old GPU mode. <paramref name="headlessImport"/> is the first-time import: the official one-shot <c>--headless --import --path</c> (console build, no window; exits by itself when done).
    /// DOTNET_ROOT is set only when the private runtime is used.
    /// </summary>
    public MapmakerLaunch EditorCommand(bool safeMode = false, bool headlessImport = false)
    {
        var args = new List<string>();
        if (headlessImport) { args.Add("--headless"); args.Add("--import"); }
        else args.Add("--editor");
        args.Add("--path"); args.Add(WorkProject);
        if (safeMode && !headlessImport) args.AddRange(SafeModePolicy.EngineArgs);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (FindRuntime() is null) env["DOTNET_ROOT"] = DotNetDir;
        return new MapmakerLaunch(MapmakerTools.GodotExe(GodotDir, console: headlessImport), args, WorkProject, env);
    }

    // ---- downloads ----------------------------------------------------------------------------------------------------------

    /// <summary>Downloads and unpacks what <paramref name="plan"/> lacks (the editor, then the runtime). Safe to repeat: a half download resumes, a finished folder is kept.</summary>
    public async Task EnsureToolsAsync(MapmakerPlan plan, SafeHttp http, IProgress<UpdateStatus>? progress, CancellationToken ct, DownloadOptions? options = null)
    {
        if (plan.NeedGodot) await InstallToolAsync(MapmakerTools.Godot, GodotDir, http, progress, ct, options);
        if (plan.NeedDotNet) await InstallToolAsync(MapmakerTools.DotNet, DotNetDir, http, progress, ct, options);
    }

    public async Task InstallToolAsync(ToolPackage pkg, string dest, SafeHttp http, IProgress<UpdateStatus>? progress, CancellationToken ct, DownloadOptions? options = null)
    {
        if (Installed(pkg, dest)) return;
        // The published checksum must agree with the pin before a byte of the zip is fetched.
        if (pkg.SumsUrl is not null)
        {
            progress?.Report(new UpdateStatus(UpdatePhase.Verifying, $"Checking the published checksum of {pkg.Title}", null));
            string published = await FetchPublishedSha512Async(pkg, http, ct).ConfigureAwait(false);
            if (!string.Equals(published, pkg.Sha512, StringComparison.OrdinalIgnoreCase))
                throw new VerificationException($"The published checksum of {pkg.Title} is not the one this launcher expects, so it was not downloaded. Update the launcher and try again.");
        }
        string part = Path.Combine(ToolsDir, "downloads", pkg.AssetName + ".part");
        var downloader = new Downloader(http, options);
        var dp = new ActionProgress<DownloadProgress>(d => progress?.Report(new UpdateStatus(UpdatePhase.Downloading, $"Downloading {pkg.Title}", d.Fraction, d)));
        progress?.Report(new UpdateStatus(UpdatePhase.Downloading, $"Downloading {pkg.Title} (once)", 0));
        await downloader.DownloadAsync(new Uri(pkg.Url), part, pkg.Size, dp, ct).ConfigureAwait(false);
        var hp = new ActionProgress<long>(b => progress?.Report(new UpdateStatus(UpdatePhase.Verifying, $"Checking {pkg.Title}", (double)b / pkg.Size)));
        string actual = await Hashes.Sha512FileAsync(part, hp, ct).ConfigureAwait(false);
        if (!string.Equals(actual, pkg.Sha512, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(part); } catch (IOException) { }
            throw new VerificationException($"The downloaded file for {pkg.Title} is damaged (its SHA-512 does not match). It was deleted; press MAPMAKER to download it again.");
        }
        string staging = dest + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        progress?.Report(new UpdateStatus(UpdatePhase.Extracting, $"Unpacking {pkg.Title}", 0));
        var xp = new ActionProgress<(long Done, long Total)>(x => progress?.Report(new UpdateStatus(UpdatePhase.Extracting, $"Unpacking {pkg.Title}", x.Total > 0 ? (double)x.Done / x.Total : null)));
        await Task.Run(() => SafeZip.Extract(part, staging, xp, ct), ct).ConfigureAwait(false);
        if (!HasAll(staging, pkg.Required))
        {
            Directory.Delete(staging, true);
            throw new VerificationException($"The downloaded file for {pkg.Title} does not hold what was expected, so it was not installed.");
        }
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.Move(staging, dest);
        File.WriteAllText(Path.Combine(dest, VerifiedMarker), pkg.Sha512);
        try { File.Delete(part); } catch (IOException) { }
    }

    /// <summary>The SHA-512 the release's SHA512-SUMS.txt lists for the package's zip.</summary>
    public static async Task<string> FetchPublishedSha512Async(ToolPackage pkg, SafeHttp http, CancellationToken ct)
    {
        using var response = await http.SendAsync(u => new HttpRequestMessage(HttpMethod.Get, u), new Uri(pkg.SumsUrl!), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} for the checksum list of {pkg.Title}.");
        if (response.Content.Headers.ContentLength is > 1 << 20) throw new VerificationException("The checksum list is far larger than expected.");
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseSums(text, pkg.SumsName!) ?? throw new VerificationException($"The release's checksum list does not name {pkg.SumsName}.");
    }

    /// <summary>One "&lt;sha512 hex&gt;  &lt;file name&gt;" line per file; null when <paramref name="name"/> is not listed or its hash is not 128 hex digits.</summary>
    public static string? ParseSums(string text, string name)
    {
        foreach (var line in text.Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^([0-9A-Fa-f]{128})\s+\*?(\S+)$", RegexOptions.CultureInvariant);
            if (m.Success && m.Groups[2].Value == name) return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }
}
