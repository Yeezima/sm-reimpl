using System.Text;
using SmLauncher.Core;

namespace SmLauncher.Tests;

/// <summary>The self-update helper's every way out: a working launcher is left (and started with --window), the old one put back on any failure.</summary>
public class SelfUpdateHelperTests
{
    sealed class FakeProc : IStartedProcess
    {
        public bool HasExited { get; set; }
        public int ExitCode { get; set; }
        public void Dispose() { }
    }

    sealed class FakeProcesses : ILauncherProcesses
    {
        public bool OldGone { get; set; } = true;
        public int SelfTestExit { get; set; }
        public Func<string, IReadOnlyList<string>, IStartedProcess?> OnStart { get; set; } = (_, _) => new FakeProc { HasExited = true };
        public List<(string Exe, string[] Args)> Starts { get; } = [];
        public List<string[]> Headless { get; } = [];
        public bool WaitForExit(int pid, long? start, TimeSpan wait) => OldGone;
        public int RunHeadless(string exe, IReadOnlyList<string> args, TimeSpan wait) { Headless.Add([.. args]); return SelfTestExit; }
        public IStartedProcess? Start(string exe, IReadOnlyList<string> args) { Starts.Add((exe, [.. args])); return OnStart(exe, args); }
    }

    sealed class Setup : IDisposable
    {
        public readonly TempDir T = new();
        public readonly InstallLayout Layout;
        public readonly string Own;
        public readonly List<string> Log = [];
        public Setup()
        {
            Layout = new InstallLayout(T["root"]);
            Directory.CreateDirectory(Layout.Root);
            File.WriteAllText(Layout.LauncherExe, "OLD");
            var files = Builds.Files("abc1234");
            files[PackageLayout.BundledLauncher] = Encoding.ASCII.GetBytes("NEW");
            string versionDir = Layout.VersionDir("tester-2026-10-29-abc1234");
            Builds.WriteFolder(versionDir, files);
            Own = Path.Combine(versionDir, PackageLayout.BundledLauncher);
        }
        public HelperRequest Request(bool window = true, string? target = null, string? own = null) => new()
        {
            Target = target ?? Layout.LauncherExe, Root = Layout.Root, OwnPath = own ?? Own, OldPid = 1234, OldStartFileTimeUtc = 5678, Window = window,
            Forward = ["--offline"], HandshakeWait = TimeSpan.FromSeconds(3), LockWait = TimeSpan.FromMilliseconds(200), Poll = TimeSpan.FromMilliseconds(10),
        };
        public HelperOutcome Run(HelperRequest r, FakeProcesses p, Func<string, string, string?>? place = null, bool newer = true) =>
            SelfUpdateHelper.Run(r, p, s => { lock (Log) Log.Add(s); }, place, (_, _) => newer);
        public string Target => File.ReadAllText(Layout.LauncherExe);
        public void Dispose() => T.Dispose();
    }

    /// <summary>A new window that finishes its first update check: it writes the .ok sign and keeps running.</summary>
    static IStartedProcess? Signals(string exe, IReadOnlyList<string> args)
    {
        if (args.Contains("--self-updated")) File.WriteAllText(SelfUpdateHelper.OkMarker(exe), "");
        return new FakeProc { HasExited = false };
    }

    [Fact]
    public void The_new_launcher_is_placed_checked_started_and_proves_itself_with_the_ok_sign()
    {
        using var s = new Setup();
        var p = new FakeProcesses { OnStart = Signals };
        Assert.Equal(HelperOutcome.Updated, s.Run(s.Request(), p));
        Assert.Equal("NEW", s.Target);
        Assert.Equal(["--selftest", "--rules"], p.Headless.Single());                                      // the light self-test
        var start = p.Starts.Single();
        Assert.Equal(s.Layout.LauncherExe, start.Exe);
        Assert.Equal("--self-updated", start.Args[0]);
        Assert.Equal("OLD", File.ReadAllText(start.Args[1]));                                            // the backup the window removes
        Assert.Contains("--offline", start.Args);
        Assert.DoesNotContain("--root", start.Args);                                                     // only with ForwardRoot
        Assert.False(File.Exists(SelfUpdateHelper.OkMarker(s.Layout.LauncherExe)));                      // the sign was taken
    }

    [Fact]
    public void A_new_launcher_that_fails_its_self_test_is_replaced_by_the_old_one_which_is_started_with_a_notice()
    {
        using var s = new Setup();
        var p = new FakeProcesses { SelfTestExit = 1 };
        Assert.Equal(HelperOutcome.PutBack, s.Run(s.Request(), p));
        Assert.Equal("OLD", s.Target);
        Assert.Contains(SelfUpdateHelper.FailedFlag, p.Starts.Single().Args);
        Assert.Empty(Directory.EnumerateFiles(s.Layout.Root, "*.old"));
    }

    [Fact]
    public void A_new_window_that_crashes_before_its_sign_puts_the_old_one_back()
    {
        using var s = new Setup();
        var p = new FakeProcesses { OnStart = (_, args) => new FakeProc { HasExited = true, ExitCode = args.Contains("--self-updated") ? -532462766 : 0 } };
        Assert.Equal(HelperOutcome.PutBack, s.Run(s.Request(), p));
        Assert.Equal("OLD", s.Target);
        Assert.Equal(2, p.Starts.Count);
        Assert.Contains(SelfUpdateHelper.FailedFlag, p.Starts[1].Args);
    }

    [Fact]
    public void A_crash_sign_counts_even_with_exit_code_zero()
    {
        using var s = new Setup();
        var p = new FakeProcesses
        {
            OnStart = (exe, args) =>
            {
                if (args.Contains("--self-updated")) File.WriteAllText(SelfUpdateHelper.CrashMarker(exe), "x");
                return new FakeProc { HasExited = true, ExitCode = 0 };
            },
        };
        Assert.Equal(HelperOutcome.PutBack, s.Run(s.Request(), p));
        Assert.Equal("OLD", s.Target);
        Assert.False(File.Exists(SelfUpdateHelper.CrashMarker(s.Layout.LauncherExe)));
    }

    [Fact]
    public void A_new_window_closed_normally_before_its_sign_is_kept()
    {
        using var s = new Setup();
        var p = new FakeProcesses { OnStart = (_, _) => new FakeProc { HasExited = true, ExitCode = 0 } };
        Assert.Equal(HelperOutcome.Updated, s.Run(s.Request(), p));
        Assert.Equal("NEW", s.Target);
        Assert.Single(p.Starts);
        Assert.Empty(Directory.EnumerateFiles(s.Layout.Root, "*.old"));                                    // the backup is not needed any more
    }

    [Fact]
    public void A_failing_place_leaves_the_old_launcher_and_starts_it()
    {
        using var s = new Setup();
        var p = new FakeProcesses { OnStart = Signals };
        Assert.Equal(HelperOutcome.PutBack, s.Run(s.Request(), p, place: (_, _) => throw new IOException("disk said no")));
        Assert.Equal("OLD", s.Target);
        Assert.Contains(SelfUpdateHelper.FailedFlag, p.Starts.Single().Args);
        Assert.Empty(p.Headless);
    }

    [Fact]
    public void A_place_that_failed_half_way_is_undone_from_the_backup()
    {
        using var s = new Setup();
        var p = new FakeProcesses();
        string? Half(string source, string target)
        {
            File.Move(target, target + ".old");                                                           // moved aside, then failed
            throw new IOException("failed after the rename");
        }
        Assert.Equal(HelperOutcome.PutBack, s.Run(s.Request(), p, place: Half));
        Assert.Equal("OLD", s.Target);
    }

    [Fact]
    public void Nothing_changes_while_the_old_launcher_is_verifiably_running()
    {
        using var s = new Setup();
        var p = new FakeProcesses { OldGone = false };
        Assert.Equal(HelperOutcome.OldStillRunning, s.Run(s.Request(), p));
        Assert.Equal("OLD", s.Target);
        Assert.Empty(p.Starts);
    }

    [Fact]
    public void A_launcher_that_is_not_newer_is_left_and_started()
    {
        using var s = new Setup();
        var p = new FakeProcesses();
        Assert.Equal(HelperOutcome.NotNewer, s.Run(s.Request(), p, newer: false));
        Assert.Equal("OLD", s.Target);
        Assert.DoesNotContain(SelfUpdateHelper.FailedFlag, p.Starts.Single().Args);
    }

    [Fact]
    public void Without_a_window_nothing_is_started_and_the_backup_goes()
    {
        using var s = new Setup();
        var p = new FakeProcesses();
        Assert.Equal(HelperOutcome.Updated, s.Run(s.Request(window: false), p));
        Assert.Equal("NEW", s.Target);
        Assert.Empty(p.Starts);
        Assert.Empty(Directory.EnumerateFiles(s.Layout.Root, "*.old"));
    }

    [Fact]
    public void A_target_other_than_the_install_folders_launcher_is_refused_and_nothing_started()
    {
        using var s = new Setup();
        File.WriteAllText(s.T["other.exe"], "OTHER");
        var p = new FakeProcesses();
        Assert.Equal(HelperOutcome.Refused, s.Run(s.Request(target: s.T["other.exe"]), p));
        Assert.Equal("OTHER", File.ReadAllText(s.T["other.exe"]));
        Assert.Empty(p.Starts);
    }

    [Fact]
    public void A_helper_that_is_not_a_verified_build_launcher_is_refused_and_the_old_launcher_started()
    {
        using var s = new Setup();
        File.WriteAllText(s.T["elsewhere.exe"], "NEW");
        var p = new FakeProcesses();
        Assert.Equal(HelperOutcome.Refused, s.Run(s.Request(own: s.T["elsewhere.exe"]), p));
        File.WriteAllText(s.Own, "ALTERED");                                                              // in the build folder, but not its listed hash
        Assert.Equal(HelperOutcome.Refused, s.Run(s.Request(), p));
        Assert.Equal("OLD", s.Target);
        Assert.All(p.Starts, x => Assert.Contains(SelfUpdateHelper.FailedFlag, x.Args));
    }

    [Fact]
    public void A_second_helper_is_refused()
    {
        using var s = new Setup();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var other = new Thread(() =>
        {
            using var m = new Mutex(false, SelfUpdateHelper.MutexName(s.Layout.Root));
            m.WaitOne();
            held.Set();
            release.Wait();
            m.ReleaseMutex();
        });
        other.Start();
        held.Wait();
        try
        {
            var p = new FakeProcesses();
            Assert.Equal(HelperOutcome.Refused, s.Run(s.Request(), p));
            Assert.Equal("OLD", s.Target);
            Assert.Empty(p.Starts);
        }
        finally { release.Set(); other.Join(); }
    }

    [Fact]
    public void An_install_folder_held_by_another_launcher_is_not_touched()
    {
        using var s = new Setup();
        using var held = RootLock.TryAcquire(s.Layout, TimeSpan.Zero);
        var p = new FakeProcesses();
        Assert.Equal(HelperOutcome.Refused, s.Run(s.Request(), p));
        Assert.Equal("OLD", s.Target);
    }

    [Fact]
    public void The_root_is_passed_on_only_when_asked()
    {
        using var s = new Setup();
        var p = new FakeProcesses { OnStart = Signals };
        var r = s.Request();
        Assert.Equal(HelperOutcome.Updated, s.Run(new HelperRequest
        {
            Target = r.Target, Root = r.Root, OwnPath = r.OwnPath, Window = true, ForwardRoot = true,
            HandshakeWait = r.HandshakeWait, LockWait = r.LockWait, Poll = r.Poll,
        }, p));
        Assert.Contains("--root", p.Starts.Single().Args);
    }
}

public class LauncherLeftoverTests
{
    [Theory]
    [InlineData("SM Reimpl Launcher.exe.old", true)]
    [InlineData("SM Reimpl Launcher.exe.ab12cd.old", true)]
    [InlineData("SM Reimpl Launcher.exe", false)]
    [InlineData("current.json", false)]
    [InlineData("other.old", false)]
    [InlineData(@"sub\SM Reimpl Launcher.exe.old", false)]
    [InlineData(@"..\SM Reimpl Launcher.exe.old", false)]
    public void Only_the_launchers_own_backup_beside_it_is_accepted(string backup, bool ok)
    {
        using var t = new TempDir();
        Assert.Equal(ok, LauncherVersions.IsOwnBackup(t["SM Reimpl Launcher.exe"], Path.Combine(t.Path, backup)));
    }

    [Fact]
    public void Clean_old_removes_backups_the_stale_copy_and_with_markers_the_signs()
    {
        using var t = new TempDir();
        string exe = t["SM Reimpl Launcher.exe"];
        foreach (var f in new[] { exe, exe + ".old", exe + ".x1.old", exe + ".new", SelfUpdateHelper.OkMarker(exe), SelfUpdateHelper.CrashMarker(exe), t["keep.txt"] })
            File.WriteAllText(f, "x");
        Assert.True(LauncherVersions.CleanOld(exe, markers: false));
        Assert.True(File.Exists(SelfUpdateHelper.OkMarker(exe)));                                          // a helper may still be reading it
        Assert.False(File.Exists(exe + ".old"));
        Assert.False(File.Exists(exe + ".x1.old"));
        Assert.False(File.Exists(exe + ".new"));
        Assert.True(LauncherVersions.CleanOld(exe));
        Assert.False(File.Exists(SelfUpdateHelper.OkMarker(exe)));
        Assert.False(File.Exists(SelfUpdateHelper.CrashMarker(exe)));
        Assert.True(File.Exists(exe));
        Assert.True(File.Exists(t["keep.txt"]));
    }
}
