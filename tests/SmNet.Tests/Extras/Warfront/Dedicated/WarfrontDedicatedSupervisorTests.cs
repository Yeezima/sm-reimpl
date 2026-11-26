using System.Diagnostics;
using SmServer;
using SmSim;

namespace SmNet.Tests.Extras.Warfront.Dedicated;

/// <summary>Warfront dedicated server S1: the <c>--warfront-dedicated</c> flag, the run-folder lock, the restart loop, the stop request and the log timestamps. Synthetic temp run folders, a fake stage child (no server is spawned, nothing binds).</summary>
public class WarfrontDedicatedSupervisorTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-ded-" + Guid.NewGuid().ToString("N"));

    public WarfrontDedicatedSupervisorTests() { Directory.CreateDirectory(_dir); WarfrontRunFiles.NewRun(_dir, 1234UL); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static ServerArguments Parse(params string[] args)
    {
        Assert.True(ServerCommandLine.TryParse(args, out var a, out string error), error);
        return a;
    }

    // ---- flag ----

    [Fact]
    public void Dedicated_implies_warfront_and_four_humans()
    {
        var a = Parse("--warfront-dedicated");
        Assert.True(a.WarfrontDedicated);
        Assert.True(a.Content.HasFlag(ContentExtras.Warfront));
        Assert.Equal(4, a.WarfrontMaxHumans);
    }

    [Fact]
    public void Dedicated_without_content_runs_the_profile_the_online_row_joins_with()
    {
        // 2026-11-20: the VM ran the default profile plus Warfront (config CFF57F93...), so every ONLINE WARFRONT join was refused as ConfigMismatch.
        Assert.Equal(ContentProfiles.WarfrontProfile, Parse("--warfront-dedicated").Content);
        Assert.Equal(Parse("--content", "warfront").Content, Parse("--warfront-dedicated").Content);
        Assert.Equal(ContentProfiles.WarfrontProfile, Parse("--warfront-dedicated", "--content", "warfront").Content);
    }

    [Fact]
    public void Dedicated_keeps_a_given_human_cap_and_content_order_does_not_matter()
    {
        var a = Parse("--warfront-dedicated", "--warfront-max-humans", "2", "--bind", "127.0.0.1");
        Assert.Equal(2, a.WarfrontMaxHumans);
        var b = Parse("--bind", "127.0.0.1", "--warfront-max-humans", "3", "--warfront-dedicated", "--content", "warfront");
        Assert.Equal(3, b.WarfrontMaxHumans);
        Assert.True(b.Content.HasFlag(ContentExtras.Warfront));
    }

    [Fact]
    public void Dedicated_refuses_a_new_run()
    {
        Assert.False(ServerCommandLine.TryParse(["--warfront-dedicated", "--warfront-new"], out _, out string error));
        Assert.Contains("--warfront-new", error);
    }

    [Fact]
    public void Dedicated_still_refuses_maps_and_the_flag_is_off_by_default()
    {
        Assert.False(ServerCommandLine.TryParse(["--warfront-dedicated", "--maps", "a,b"], out _, out _));
        Assert.False(Parse("--content", "warfront").WarfrontDedicated);
    }

    [Fact]
    public void The_stage_child_gets_the_dedicated_flag_and_loses_new_and_map()
    {
        var child = WarfrontSupervisor.ChildArguments(["--warfront-dedicated", "--bind", "127.0.0.1", "--map", "x", "--warfront-new"], "wf_b1");
        Assert.Equal(["--warfront-dedicated", "--bind", "127.0.0.1", "--map", "wf_b1", "--warfront-stage"], child);
        Assert.True(Parse(child.ToArray()).WarfrontStage);   // the child's own command line parses, dedicated too
    }

    // ---- lock (an OS lock held on the open file, not a PID check) ----

    [Fact]
    public void A_second_holder_is_refused_while_the_first_holds_the_lock_and_released_after_dispose()
    {
        using (var first = WarfrontSupervisorLock.TryAcquire(_dir, out string? r1))
        {
            Assert.NotNull(first); Assert.Null(r1);
            var second = WarfrontSupervisorLock.TryAcquire(_dir, out string? r2);
            Assert.Null(second);
            Assert.False(string.IsNullOrEmpty(r2));
        }
        using var again = WarfrontSupervisorLock.TryAcquire(_dir, out _);
        Assert.NotNull(again);
    }

    [Fact]
    public void A_leftover_lock_file_without_a_holder_is_taken_whatever_it_says()
    {
        // Even this process's own PID, a reused PID or junk: the PID text is informational only.
        foreach (string text in new[] { "2147483000", Environment.ProcessId.ToString(), "1", "not a pid", "" })
        {
            File.WriteAllText(Path.Combine(_dir, WarfrontSupervisorLock.FileName), text);
            using var l = WarfrontSupervisorLock.TryAcquire(_dir, out string? refusal);
            Assert.NotNull(l); Assert.Null(refusal);
        }
    }

    [Fact]
    public void The_lock_exit_code_is_distinct_from_the_server_codes()
    {
        Assert.DoesNotContain(WarfrontSupervisorLock.ExitCode, new[] { 0, 1, 2, 3, 4, 11, 12 });
    }

    // ---- loop with a fake child ----

    sealed class FakeLauncher(string dir, params (int Code, int RunMs)[] script) : IWarfrontStageLauncher
    {
        readonly Queue<(int Code, int RunMs)> _script = new(script);
        public readonly List<string> Maps = [];
        public readonly List<FakeChild> Children = [];

        public IWarfrontStageProcess Start(string map)
        {
            Maps.Add(map);
            // An exhausted script: a child that runs until it is asked to stop.
            var next = _script.Count > 0 ? _script.Dequeue() : (Code: 0, RunMs: -1);
            var c = new FakeChild(dir, next.Code, next.RunMs);
            Children.Add(c);
            return c;
        }
    }

    sealed class FakeChild(string dir, int code, int runMs) : IWarfrontStageProcess
    {
        readonly Stopwatch _w = Stopwatch.StartNew();
        public bool Killed, ObeysStop = true;
        public int ExitCode => code;
        public bool SawStopFile;
        public bool WaitForExit(int ms)
        {
            var until = Stopwatch.StartNew();
            while (true)
            {
                if (Killed) return true;
                if (ObeysStop && File.Exists(Path.Combine(dir, WarfrontRunFiles.StopFile))) { SawStopFile = true; return true; }
                if (runMs >= 0 && _w.ElapsedMilliseconds >= runMs) return true;
                if (ms >= 0 && until.ElapsedMilliseconds >= ms) return false;
                Thread.Sleep(5);
            }
        }
        public void Kill() => Killed = true;
        public void Dispose() { }
    }

    static WarfrontSupervisorTiming Fast(int stableMs = 10_000) => new()
    {
        Backoff = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40)],
        StableAfter = TimeSpan.FromMilliseconds(stableMs), StopGrace = TimeSpan.FromMilliseconds(300), PollMilliseconds = 10,
    };

    [Fact]
    public void A_crashed_stage_restarts_from_the_last_save_with_growing_backoff_and_each_restart_is_logged()
    {
        var launcher = new FakeLauncher(_dir, (4, 0), (139, 0), (-1, 0), (0, 0));   // fatal, a signal, a lost process, then a clean leave
        var log = new List<string>();
        using var stop = new ManualResetEventSlim();
        int code = WarfrontSupervisor.Loop(_dir, launcher, dedicated: true, Fast(), stop, log.Add);
        Assert.Equal(0, code);
        Assert.Equal(4, launcher.Maps.Count);
        Assert.All(launcher.Maps, m => Assert.Equal(launcher.Maps[0], m));   // the same stage from the same save
        var restarts = log.Where(l => l.Contains("restart ")).ToList();
        Assert.Equal(3, restarts.Count);
        Assert.Contains("restart 1 ", restarts[0]); Assert.Contains("code 4", restarts[0]);
        Assert.Contains("restart 3 ", restarts[2]); Assert.Contains("in 0 s", restarts[0]);
    }

    [Fact]
    public void A_stable_run_resets_the_backoff()
    {
        var launcher = new FakeLauncher(_dir, (4, 0), (4, 80), (0, 0));
        var log = new List<string>();
        using var stop = new ManualResetEventSlim();
        Assert.Equal(0, WarfrontSupervisor.Loop(_dir, launcher, true, Fast(stableMs: 50), stop, log.Add));
        var restarts = log.Where(l => l.Contains("restart ")).ToList();
        Assert.Equal(2, restarts.Count);
        Assert.Contains("restart 1 ", restarts[0]);
        Assert.Contains("restart 1 ", restarts[1]);   // the second crash came after a stable stretch: back to step one
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Errors_a_restart_cannot_fix_end_the_supervisor_with_the_same_code(int exit)
    {
        var launcher = new FakeLauncher(_dir, (exit, 0));
        using var stop = new ManualResetEventSlim();
        Assert.Equal(exit, WarfrontSupervisor.Loop(_dir, launcher, true, Fast(), stop, _ => { }));
        Assert.Single(launcher.Maps);
    }

    [Fact]
    public void The_next_stage_code_starts_the_next_stage_without_a_restart_wait()
    {
        var launcher = new FakeLauncher(_dir, (ServerCommandLine.WarfrontStageExit, 0), (0, 0));
        var log = new List<string>();
        using var stop = new ManualResetEventSlim();
        Assert.Equal(0, WarfrontSupervisor.Loop(_dir, launcher, true, Fast(), stop, log.Add));
        Assert.Equal(2, launcher.Maps.Count);
        Assert.DoesNotContain(log, l => l.Contains("restart "));
        Assert.Contains(log, l => l.Contains("stage 2 on "));
    }

    [Fact]
    public void Player_hosted_mode_still_ends_on_any_other_code()
    {
        var launcher = new FakeLauncher(_dir, (4, 0));
        using var stop = new ManualResetEventSlim();
        Assert.Equal(4, WarfrontSupervisor.Loop(_dir, launcher, dedicated: false, Fast(), stop, _ => { }));
        Assert.Single(launcher.Maps);
    }

    [Fact]
    public void A_stop_request_writes_the_stop_file_waits_for_the_child_and_exits_zero()
    {
        var launcher = new FakeLauncher(_dir);   // one child that runs until asked
        var log = new List<string>();
        using var stop = new ManualResetEventSlim();
        var task = Task.Run(() => WarfrontSupervisor.Loop(_dir, launcher, true, Fast(), stop, log.Add));
        Assert.True(SpinWait.SpinUntil(() => launcher.Children.Count > 0, TimeSpan.FromSeconds(60)));   // under load a fixed sleep could stop the loop before the child started (flake seen in a full run); bounded, so it fails instead of hanging
        stop.Set();
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, task.Result);
        Assert.True(launcher.Children[0].SawStopFile);
        Assert.False(launcher.Children[0].Killed);
        Assert.False(File.Exists(Path.Combine(_dir, WarfrontRunFiles.StopFile)));   // cleaned up
        Assert.Single(launcher.Maps);   // no restart
    }

    [Fact]
    public void A_child_that_ignores_the_stop_request_is_killed_after_the_grace()
    {
        var launcher = new FakeLauncher(_dir);
        var log = new List<string>();
        using var stop = new ManualResetEventSlim();
        var task = Task.Run(() => WarfrontSupervisor.Loop(_dir, launcher, true, Fast(), stop, log.Add));
        Assert.True(SpinWait.SpinUntil(() => launcher.Children.Count > 0, TimeSpan.FromSeconds(60)));
        launcher.Children[0].ObeysStop = false;
        stop.Set();
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, task.Result);
        Assert.True(launcher.Children[0].Killed);
        Assert.Contains(log, l => l.Contains("killed"));
    }

    [Fact]
    public void A_stop_during_the_restart_wait_ends_without_another_child()
    {
        var launcher = new FakeLauncher(_dir, (4, 0));
        var log = new List<string>();
        using var stop = new ManualResetEventSlim();
        var timing = Fast() with { Backoff = [TimeSpan.FromSeconds(30)] };
        var task = Task.Run(() => WarfrontSupervisor.Loop(_dir, launcher, true, timing, stop, log.Add));
        Assert.True(SpinWait.SpinUntil(() => log.Any(l => l.Contains("restart 1 ")), TimeSpan.FromSeconds(60)));
        stop.Set();
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, task.Result);
        Assert.Single(launcher.Maps);
    }

    [Fact]
    public void A_stale_stop_file_does_not_stop_the_next_start()
    {
        File.WriteAllText(Path.Combine(_dir, WarfrontRunFiles.StopFile), "stop");
        var launcher = new FakeLauncher(_dir, (0, 30));
        using var stop = new ManualResetEventSlim();
        Assert.Equal(0, WarfrontSupervisor.Loop(_dir, launcher, true, Fast(), stop, _ => { }));
        Assert.False(launcher.Children[0].SawStopFile);   // it ended by its own code, the old request was deleted at start
    }

    [Fact]
    public void Repeated_rapid_failures_log_a_crash_loop_line_each_cycle_and_keep_retrying()
    {
        var launcher = new FakeLauncher(_dir, (4, 0), (4, 0), (4, 0), (4, 0), (4, 0), (4, 0), (4, 0), (0, 0));
        var log = new List<string>();
        using var stop = new ManualResetEventSlim();
        var timing = Fast() with { CrashLoopAfter = 3 };
        Assert.Equal(0, WarfrontSupervisor.Loop(_dir, launcher, true, timing, stop, log.Add));
        Assert.Equal(8, launcher.Maps.Count);
        Assert.Equal(5, log.Count(l => l.Contains("CRASH LOOP")));   // failures 3 to 7
    }

    [Fact]
    public void A_child_that_exits_while_a_stop_is_pending_is_logged_as_stopping_not_as_a_crash()
    {
        var launcher = new FakeLauncher(_dir, (4, 0));
        var log = new List<string>();
        using var stop = new ManualResetEventSlim();
        stop.Set();   // the signal came just as the child died
        // the loop checks the stop first: no child at all
        Assert.Equal(0, WarfrontSupervisor.Loop(_dir, launcher, true, Fast(), stop, log.Add));
        Assert.DoesNotContain(log, l => l.Contains("unexpectedly"));
    }

    // ---- timestamps ----

    [Fact]
    public void Supervisor_log_lines_start_with_a_utc_iso_timestamp()
    {
        var launcher = new FakeLauncher(_dir, (4, 0), (0, 0));
        var log = new List<string>();
        using var stop = new ManualResetEventSlim();
        WarfrontSupervisor.Loop(_dir, launcher, true, Fast(), stop, log.Add);
        Assert.NotEmpty(log);
        Assert.All(log, l => Assert.Matches(@"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z\] warfront: ", l));
        // the player menu's status reader still finds the line
        Assert.StartsWith("warfront: stage", SmContent.Extras.Warfront.WarfrontLocal.LastStatus(log.Take(1)));
    }
}
