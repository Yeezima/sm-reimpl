using System.Diagnostics;
using SmServer;
using SmSim;

namespace SmNet.Tests.Extras.Warfront.Dedicated;

/// <summary>Warfront dedicated server, map warm-up: the supervisor starts the low-priority <c>--warfront-prebuild</c> child once after a stage has run a while (dedicated only), never restarts it, and stops it with the stage; the child's loop builds each map in turn. Synthetic folders, fake children, nothing binds.</summary>
public class WarfrontDedicatedWarmupTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-warm-" + Guid.NewGuid().ToString("N"));

    public WarfrontDedicatedWarmupTests() { Directory.CreateDirectory(_dir); WarfrontRunFiles.NewRun(_dir, 1234UL); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    sealed class Child(string dir, int code, int runMs, bool obeysStopFile) : IWarfrontStageProcess
    {
        readonly Stopwatch _w = Stopwatch.StartNew();
        public bool Killed;
        public int ExitCode => code;
        public bool WaitForExit(int ms)
        {
            var until = Stopwatch.StartNew();
            while (true)
            {
                if (Killed) return true;
                if (obeysStopFile && File.Exists(Path.Combine(dir, WarfrontRunFiles.StopFile))) return true;
                if (runMs >= 0 && _w.ElapsedMilliseconds >= runMs) return true;
                if (ms >= 0 && until.ElapsedMilliseconds >= ms) return false;
                Thread.Sleep(2);
            }
        }
        public void Kill() => Killed = true;
        public void Dispose() { }
    }

    sealed class StageLauncher(string dir, params (int Code, int RunMs)[] script) : IWarfrontStageLauncher
    {
        readonly Queue<(int Code, int RunMs)> _script = new(script);
        public int Starts;
        public IWarfrontStageProcess Start(string map)
        {
            Starts++;
            var next = _script.Count > 0 ? _script.Dequeue() : (Code: 0, RunMs: -1);
            return new Child(dir, next.Code, next.RunMs, obeysStopFile: true);
        }
    }

    sealed class PrebuildLauncher(int code, int runMs) : IWarfrontPrebuildLauncher
    {
        public readonly List<Child> Children = [];
        public IWarfrontStageProcess Start() { var c = new Child(_dirNone, code, runMs, obeysStopFile: false); Children.Add(c); return c; }
        const string _dirNone = "";
    }

    static WarfrontSupervisorTiming Fast() => new()
    {
        Backoff = [TimeSpan.FromMilliseconds(10)], StableAfter = TimeSpan.FromSeconds(10), StopGrace = TimeSpan.FromMilliseconds(300),
        PollMilliseconds = 5, PrebuildAfter = TimeSpan.FromMilliseconds(60),
    };

    [Fact]
    public void The_warmup_starts_once_after_the_first_stage_has_run_a_while_in_dedicated_mode_and_stops_with_the_supervisor()
    {
        var stages = new StageLauncher(_dir);   // one stage that runs until asked
        var pre = new PrebuildLauncher(0, -1);
        var log = new List<string>();
        using var stop = new ManualResetEventSlim();
        var task = Task.Run(() => WarfrontSupervisor.Loop(_dir, stages, true, Fast(), stop, log.Add, pre));
        Assert.True(SpinWait.SpinUntil(() => pre.Children.Count == 1, 5000));
        Thread.Sleep(100);
        Assert.Single(pre.Children);
        Assert.False(pre.Children[0].Killed);
        stop.Set();
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, task.Result);
        Assert.True(pre.Children[0].Killed);   // the supervisor's stop ends it too
        Assert.Contains(log, l => l.Contains("map warm-up started"));
        Assert.Contains(log, l => l.Contains("map warm-up stopped"));
    }

    [Fact]
    public void It_does_not_start_before_a_stage_has_run_long_enough()
    {
        var stages = new StageLauncher(_dir, (0, 10));   // a stage that ends at once
        var pre = new PrebuildLauncher(0, -1);
        using var stop = new ManualResetEventSlim();
        Assert.Equal(0, WarfrontSupervisor.Loop(_dir, stages, true, Fast(), stop, _ => { }, pre));
        Assert.Empty(pre.Children);
    }

    [Fact]
    public void Player_hosted_mode_never_starts_it()
    {
        var stages = new StageLauncher(_dir, (0, 200));
        var pre = new PrebuildLauncher(0, -1);
        using var stop = new ManualResetEventSlim();
        Assert.Equal(0, WarfrontSupervisor.Loop(_dir, stages, dedicated: false, Fast(), stop, _ => { }, pre));
        Assert.Empty(pre.Children);
    }

    [Fact]
    public void A_failed_warmup_is_logged_once_and_not_restarted_while_stages_go_on()
    {
        // stage 1 runs 250 ms (warm-up starts at 60 ms and fails at once), asks for the next stage; stage 2 runs 250 ms and leaves cleanly.
        var stages = new StageLauncher(_dir, (ServerCommandLine.WarfrontStageExit, 250), (0, 250));
        var pre = new PrebuildLauncher(7, 0);
        var log = new List<string>();
        using var stop = new ManualResetEventSlim();
        Assert.Equal(0, WarfrontSupervisor.Loop(_dir, stages, true, Fast(), stop, log.Add, pre));
        Assert.Equal(2, stages.Starts);
        Assert.Single(pre.Children);   // once per supervisor, also not after its failure or on the second stage
        Assert.Single(log, l => l.Contains("code 7") && l.Contains("not restarted"));
    }

    [Fact]
    public void A_finished_warmup_is_logged_and_the_loop_ends_with_the_stage_not_the_warmup()
    {
        var stages = new StageLauncher(_dir, (0, 250));
        var pre = new PrebuildLauncher(0, 0);
        var log = new List<string>();
        using var stop = new ManualResetEventSlim();
        Assert.Equal(0, WarfrontSupervisor.Loop(_dir, stages, true, Fast(), stop, log.Add, pre));
        Assert.Single(log, l => l.Contains("map warm-up finished after"));
    }

    [Fact]
    public void The_child_gets_the_supervisors_arguments_with_the_prebuild_flag_and_no_stage_flags()
    {
        var child = WarfrontSupervisor.PrebuildArguments(["--warfront-dedicated", "--bind", "127.0.0.1", "--map", "x", "--warfront-new", "--warfront-stage"]);
        Assert.Equal(["--warfront-dedicated", "--bind", "127.0.0.1", "--warfront-prebuild"], child);
        Assert.True(ServerCommandLine.TryParse(child.ToArray(), out var a, out string error), error);
        Assert.True(a.WarfrontPrebuild); Assert.False(a.WarfrontStage);
    }

    [Fact]
    public void The_prebuild_flag_needs_dedicated_and_is_not_a_stage()
    {
        Assert.False(ServerCommandLine.TryParse(["--content", "warfront", "--warfront-prebuild"], out _, out string e1));
        Assert.Contains("--warfront-dedicated", e1);
        Assert.False(ServerCommandLine.TryParse(["--warfront-dedicated", "--warfront-stage", "--warfront-prebuild"], out _, out _));
    }

    // ---- the child's loop ----

    [Fact]
    public void The_prebuild_loop_builds_every_map_in_order_one_at_a_time_and_continues_after_a_failure()
    {
        var built = new List<string>();
        var log = new List<string>();
        int code = WarfrontPrebuildRun.Run(["a", "b", "c"], (name, _) =>
        {
            built.Add(name);
            if (name == "b") throw new InvalidOperationException("no data");
            return name + ": ok";
        }, () => false, log.Add, () => 300L * 1024 * 1024);
        Assert.Equal(["a", "b", "c"], built);
        Assert.Equal(1, code);   // one failed
        Assert.Contains(log, l => l.Contains("b failed") && l.Contains("no data"));
        Assert.Contains(log, l => l.Contains("2 of 3 maps, 1 failed") && l.Contains("peak working set 300 MB"));
    }

    [Fact]
    public void The_prebuild_loop_exits_zero_when_all_are_built_and_stops_between_maps_when_asked()
    {
        Assert.Equal(0, WarfrontPrebuildRun.Run(["a", "b"], (n, _) => n, () => false, _ => { }));
        var built = new List<string>();
        var log = new List<string>();
        bool stopNow = false;
        int code = WarfrontPrebuildRun.Run(["a", "b", "c"], (n, _) => { built.Add(n); stopNow = true; return n; }, () => stopNow, log.Add);
        Assert.Equal(["a"], built);
        Assert.Equal(0, code);
        Assert.Contains(log, l => l.Contains("stopped before b"));
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void Concurrent_writers_of_one_cache_file_never_leave_a_reader_a_partial_file()
    {
        // The prebuild child and a stage may build the same map at once: every writer goes through its own temp file and a rename.
        var slot = SmSim.Extras.Warfront.Maps.WarfrontSlots.All[0];
        var build = SmSim.Extras.Warfront.Maps.WarfrontSlots.Build(slot);
        string dir = Path.Combine(Path.GetTempPath(), "sm-wf-warm-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            Func<SmSim.Extras.Warfront.Maps.WarfrontSlot, (SmSim.Extras.Musou.Ashfall.AshfallLevel, SmSim.Extras.Musou.Ashfall.AshfallDressing)> gen = _ => (build.Level, build.Dressing);
            var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
            {
                for (int i = 0; i < 3; i++) SmContent.Extras.Warfront.Maps.WarfrontLayoutCache.LoadOrGenerate(slot, dir, "dotnet", gen, out _);
            })).ToArray();
            Assert.True(Task.WaitAll(writers, TimeSpan.FromSeconds(120)));   // bounded: fails instead of hanging
            string path = SmContent.Extras.Warfront.Maps.WarfrontLayoutCache.PathIn(dir, slot, "dotnet");
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));   // no leftover temp file
            var read = SmContent.Extras.Warfront.Maps.WarfrontSharedCache.TryRead(dir, slot, "dotnet", out string note);
            Assert.NotNull(read);
            Assert.Equal(build.Level.Hash, read!.Value.Level.Hash);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_warmup_covers_every_slot()
    {
        Assert.Equal(11, WarfrontPrebuildRun.Names.Count);
        Assert.Equal(WarfrontPrebuildRun.Names.Count, WarfrontPrebuildRun.Names.Distinct().Count());
    }
}
