using SmLauncher.Core;

namespace SmLauncher;

/// <summary>The headless modes: --selftest (release smoke test) and the support / test commands (see <see cref="Program"/>).</summary>
static class Cli
{
    public static int Run(Args a)
    {
        if (a.Has("layout-check")) return LayoutCheck.Run(a["layout-check"]);
        if (a.Has("selftest")) return SelfTest(a);
        if (a.Has("install-self"))
        {
            AppLog? helperLog = a["root"] is { } hr ? new AppLog(new InstallLayout(hr)) : null;      // --root is required: the helper's log goes there
            return SelfUpdate.RunHelper(a, s => { Console.WriteLine(s); helperLog?.Write(s); });
        }
        string? root = RootFinder.Find(a);
        if (root is null) { Console.Error.WriteLine("No install folder: pass --root <dir>."); return 2; }
        var layout = new InstallLayout(root);
        var log = new AppLog(layout);
        using var rootLock = RootLock.TryAcquire(layout, TimeSpan.FromSeconds(10));
        if (rootLock is null) { Console.Error.WriteLine("Another launcher is working on this install folder."); return 3; }
        using var http = new SafeHttp(NetPolicy.GitHub);
        var updater = new Updater(layout, new Downloader(http), s => { log.Write(s); });
        var recovered = updater.Recover();
        foreach (var n in recovered.Notes) Console.WriteLine("recover: " + n);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        switch (a.Command)
        {
            case "recover":
            case "status":
                PrintStatus(updater);
                return 0;
            case "check":
            {
                var newest = new ReleaseSource(http).GetNewestAsync(cts.Token).GetAwaiter().GetResult();
                PrintStatus(updater);
                if (newest is null) { Console.WriteLine("newest: none published"); return 0; }
                Console.WriteLine($"newest: {newest.Tag} published {newest.PublishedAt:yyyy-MM-dd HH:mm} UTC, {newest.Asset.Name} {newest.Asset.Size} bytes, api sha256 {newest.Asset.ApiSha256 ?? "-"}, description sha256 {newest.BodySha256 ?? "-"}");
                Console.WriteLine(updater.ReadPointer().Current == newest.Tag ? "up to date" : "update available");
                Console.WriteLine("what's new:\n" + newest.WhatsNew);
                return 0;
            }
            case "install-latest":
            {
                var newest = new ReleaseSource(http).GetNewestAsync(cts.Token).GetAwaiter().GetResult() ?? throw new InvalidOperationException("No release is published.");
                string tag = updater.InstallAsync(newest, ConsoleProgress(), cts.Token).GetAwaiter().GetResult();
                Console.WriteLine($"installed: {tag}");
                PrintStatus(updater);
                return 0;
            }
            case "resume":
            {
                string? tag = updater.ResumeAsync(ConsoleProgress(), cts.Token).GetAwaiter().GetResult();
                Console.WriteLine(tag is null ? "nothing to resume" : $"installed: {tag}");
                PrintStatus(updater);
                return 0;
            }
            case "verify":
            {
                var p = updater.ReadPointer();
                if (p.Current is null) { Console.WriteLine("nothing installed"); return 1; }
                var r = updater.CheckFiles(p.Current);
                Console.WriteLine($"verify {p.Current}: {r.Describe()}");
                return r.Ok ? 0 : 1;
            }
            case "rollback":
                Console.WriteLine($"rolled back to {updater.Rollback()}");
                PrintStatus(updater);
                return 0;
            case "update-launcher":
            {
                SelfUpdate.CleanOld();
                string? newer = SelfUpdate.NewerBundled(updater.CurrentDir(), layout.LauncherExe);
                Console.WriteLine($"this launcher: {LauncherInfo.Version} ({LauncherInfo.ExePath}); in the install folder: {SelfUpdate.FileVersionOf(layout.LauncherExe)?.ToString() ?? "none"}");
                if (newer is null) { Console.WriteLine("the active build carries no newer launcher (or its launcher does not match the build's file list)"); return 0; }
                var outcome = SelfUpdate.Install(newer, layout.LauncherExe);
                if (outcome == SelfUpdate.Outcome.NeedsHelper)
                {
                    // This running launcher is the one to replace: the new one does it once this process has exited, then checks itself (--selftest).
                    rootLock.Dispose();
                    SelfUpdate.StartHelper(newer, layout.LauncherExe, layout.Root, window: false, forwardRoot: false, []);
                    Console.WriteLine($"the new launcher {SelfUpdate.FileVersionOf(newer)} replaces this one when it exits (log: {layout.LogFile})");
                    return 0;
                }
                Console.WriteLine(outcome == SelfUpdate.Outcome.NotNewer ? "not newer than the installed launcher: nothing changed"
                    : $"installed launcher {SelfUpdate.FileVersionOf(newer)} at {layout.LauncherExe}");
                return 0;
            }
            case "adopt":
            {
                string from = a["adopt"] ?? throw new ArgumentException("--adopt needs a folder.");
                string tag = updater.AdoptAsync(from, ConsoleProgress(), cts.Token).GetAwaiter().GetResult();
                Console.WriteLine($"adopted: {tag}");
                PrintStatus(updater);
                return 0;
            }
        }
        return 2;
    }

    static void PrintStatus(Updater updater)
    {
        var p = updater.ReadPointer();
        Console.WriteLine($"root: {updater.Layout.Root}");
        Console.WriteLine($"current: {p.Current ?? "-"}  previous: {p.Previous ?? "-"}");
        Console.WriteLine($"versions on disk: {string.Join(", ", updater.InstalledTags())}");
        var s = updater.ReadState();
        if (s is not null)
        {
            long have = s.AssetName is { } n && File.Exists(updater.Layout.PartFile(n)) ? new FileInfo(updater.Layout.PartFile(n)).Length : 0;
            Console.WriteLine($"pending update: {s.Tag}, phase {s.Phase}, {have} of {s.Size} bytes downloaded");
        }
    }

    /// <summary>Progress lines at most once a second (and every phase change).</summary>
    static IProgress<UpdateStatus> ConsoleProgress()
    {
        long last = 0;
        UpdatePhase? lastPhase = null;
        return new ActionProgress<UpdateStatus>(s =>
        {
            long now = Environment.TickCount64;
            if (s.Phase == lastPhase && now - last < 1000 && s.Download?.Note is null) return;
            last = now;
            lastPhase = s.Phase;
            Console.WriteLine(s.Download is { } d ? $"{s.Phase}: {Format.Download(d)}" : $"{s.Phase}: {s.Message}{(s.Fraction is { } f ? $" {f:P0}" : "")}");
        });
    }

    /// <summary>
    /// The release smoke test's check: the build this launcher came with (its own folder when that is an unpacked playtest zip; else the active
    /// version of the install folder) is checked against its file list. No network, nothing written. Exit 0 = ok.
    /// </summary>
    static int SelfTest(Args a)
    {
        var problems = new List<string>();
        if (a.Has("rules"))
        {
            // The light check the self-update helper runs on a new launcher: it starts, and its rules hold (no build is hashed; the window's first
            // update check is the real proof).
            RuleChecks(problems);
            Console.WriteLine(problems.Count == 0 ? $"launcher selftest: ok (rules, launcher {LauncherInfo.VersionText})" : $"launcher selftest: FAILED: {string.Join("; ", problems)}");
            return problems.Count == 0 ? 0 : 1;
        }
        string? target = null;
        if (a["root"] is null && File.Exists(Path.Combine(LauncherInfo.ExeDir, PackageLayout.VersionPath))) target = LauncherInfo.ExeDir;
        else if (RootFinder.Find(a) is { } root) target = new Updater(new InstallLayout(root), null).CurrentDir();
        if (target is null) { Console.WriteLine("launcher selftest: FAILED: no version to check (not inside an unpacked build, no install folder)"); return 1; }

        RuleChecks(problems);
        var v = PackageLayout.ReadVersion(target);
        var report = Manifest.Verify(target);
        if (!report.Ok) problems.Add("files: " + report.Describe());
        string tag = v?.Tag ?? "(no version)";
        if (problems.Count > 0)
        {
            Console.WriteLine($"launcher selftest: FAILED ({tag}): {string.Join("; ", problems)}");
            return 1;
        }
        Console.WriteLine($"launcher selftest: ok ({tag}, {report.Checked} files verified, launcher {LauncherInfo.VersionText})");
        return 0;
    }

    /// <summary>The engine-free rules this build relies on, on fixed inputs.</summary>
    static void RuleChecks(List<string> problems)
    {
        if (!Names.IsTag("tester-2026-10-29-d211613") || Names.IsTag("..") || Names.IsTag(@"a\b") || Names.IsTag("v1.")) problems.Add("tag rule");
        if (PackageLayout.SafeRelative(@"..\evil.txt") is not null || PackageLayout.SafeRelative("C:/x") is not null || PackageLayout.SafeRelative("game/ok.txt") is null) problems.Add("zip path rule");
        if (RangeResume.Decide(206, 10, 100, new System.Net.Http.Headers.ContentRangeHeaderValue(10, 99, 100), 90).Action != RangeAction.Append) problems.Add("range rule");
        const string sample = """[{"draft":true,"tag_name":"d","published_at":"2026-10-30T00:00:00Z","assets":[]},{"draft":false,"prerelease":true,"tag_name":"tester-2026-10-29-abcdef1","published_at":"2026-10-29T00:00:00Z","assets":[{"name":"x.txt","size":1,"browser_download_url":"https://github.com/x"},{"name":"SM-Reimpl-Playtest-2026-10-29-abcdef1-Windows.zip","size":5,"browser_download_url":"https://github.com/y","digest":"sha256:0000000000000000000000000000000000000000000000000000000000000000"}]}]""";
        if (ReleaseFeed.SelectNewest(sample) is not { Tag: "tester-2026-10-29-abcdef1", Asset.ApiSha256: not null }) problems.Add("release selection rule");
        if (!NetPolicy.GitHub.IsAllowed(new Uri("https://api.github.com/x")) || NetPolicy.GitHub.IsAllowed(new Uri("http://github.com/x")) || NetPolicy.GitHub.IsAllowed(new Uri("https://example.com/x")))
            problems.Add("network policy");
    }
}

static class Format
{
    public static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    public static string Download(DownloadProgress d)
    {
        if (d.Note is not null) return d.Note;
        string s = $"{d.Fraction:P0} · {Mb(d.Done)} of {Mb(d.Total)}";
        if (d.BytesPerSecond > 0) s += $" · {d.BytesPerSecond / 1048576.0:0.0} MB/s";
        if (d.Remaining is { } r) s += $" · {Duration(r)} left";
        return s;
    }

    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(1, (int)t.TotalSeconds)} s" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s" : $"{(int)t.TotalHours} h {t.Minutes:00} min";
}
