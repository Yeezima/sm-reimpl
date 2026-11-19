using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;

namespace SmServer;

/// <summary>A running stage child, as the supervisor loop sees it (a real process, or a fake in the tests).</summary>
interface IWarfrontStageProcess : IDisposable
{
    /// <summary>True once the child has exited, waiting at most <paramref name="milliseconds"/> (-1: without limit).</summary>
    bool WaitForExit(int milliseconds);
    int ExitCode { get; }
    void Kill();
}

/// <summary>Starts the stage child for a map (the tests inject a fake; no real server is spawned).</summary>
interface IWarfrontStageLauncher
{
    IWarfrontStageProcess Start(string map);
}

/// <summary>Starts the map warm-up child (<c>--warfront-prebuild</c>, low priority; the tests inject a fake).</summary>
interface IWarfrontPrebuildLauncher
{
    IWarfrontStageProcess Start();
}

/// <summary>The supervisor's timing (docs/warfront.md "Dedicated mode"; all kind=ours in spec/behavior.md).</summary>
sealed record WarfrontSupervisorTiming
{
    /// <summary>Waits before the 1st, 2nd, 3rd and later restarts of a crashed stage (the last one repeats).</summary>
    public TimeSpan[] Backoff { get; init; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
    /// <summary>A child that ran this long before it crashed was stable: the backoff starts again at its first step.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>After SIGTERM: how long the child gets to save and leave (it checks once a second) before it is killed.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>This many failures in a row, each before <see cref="StableAfter"/>, log a "child keeps crashing" line on every further cycle (the retries go on).</summary>
    public int CrashLoopAfter { get; init; } = 5;
    /// <summary>How often the loop looks at the stop request while the child runs.</summary>
    public int PollMilliseconds { get; init; } = 250;
    /// <summary>The map warm-up starts once one stage child has run this long without ending (it has loaded the game data and listens: "ready"); it starts once per supervisor, dedicated mode only.</summary>
    public TimeSpan PrebuildAfter { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>The lock on a run folder: <c>supervisor.lock</c> opened with <c>FileShare.None</c> and held for the supervisor's whole life (the OS releases it when the process dies, however it dies: flock on Linux, a sharing violation on Windows). A second supervisor on the same folder is refused. The file's text (the PID) is informational only; a leftover file nobody holds is simply taken.</summary>
sealed class WarfrontSupervisorLock : IDisposable
{
    public const string FileName = "supervisor.lock";
    /// <summary>Exit code of a supervisor that found a live one on its run folder.</summary>
    public const int ExitCode = 5;

    readonly FileStream _stream;
    WarfrontSupervisorLock(FileStream stream) { _stream = stream; }

    /// <summary>Takes the lock, or says why not (null).</summary>
    public static WarfrontSupervisorLock? TryAcquire(string dir, out string? refusal)
    {
        refusal = null;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, FileName);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            refusal = $"another Warfront supervisor already holds the run folder's lock ({path}); stop it first";
            return null;
        }
        stream.SetLength(0);
        var bytes = System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        stream.Write(bytes); stream.Flush();
        return new WarfrontSupervisorLock(stream);
    }

    /// <summary>Releases the lock. The file stays (deleting it would race a new holder); the next supervisor reuses it.</summary>
    public void Dispose()
    {
        try { _stream.Dispose(); } catch (IOException) { }
    }
}

/// <summary>
/// The Warfront supervisor (slice W5a; modelled on <see cref="RotationSupervisor"/>): reads <c>run.json</c> (creates a run with <c>--warfront-new</c> or when none exists), starts the stage child on the right map
/// (the same arguments plus <c>--map &lt;name&gt; --warfront-stage</c>), waits, and starts the next stage when the child exits with <see cref="ServerCommandLine.WarfrontStageExit"/>; any other exit code ends the
/// supervisor with it. It binds nothing itself. Closing the supervisor (Ctrl+C) kills the running child; the run on disk is whatever the last save wrote, and the next start resumes from it.
/// <para>Dedicated mode (<c>--warfront-dedicated</c>, S1): the run folder is locked (<see cref="WarfrontSupervisorLock"/>, exit 5 when taken); a stage child that ends with any code other than 0, 1, 2, 3 and 12
/// (clean leave, no game install or run unreadable, command-line error, port taken: all of which a restart cannot fix) is restarted from the last save after a backoff; SIGTERM (or Ctrl+C) writes <c>stop.request</c> so the child
/// saves and leaves, waits <see cref="WarfrontSupervisorTiming.StopGrace"/> (then kills it), and exits 0. Every supervisor log line starts with a UTC ISO timestamp.</para>
/// </summary>
static class WarfrontSupervisor
{
    /// <summary>The child's arguments: the supervisor's own without <c>--warfront-new</c>, <c>--map</c> and its value, plus <c>--map name --warfront-stage</c>.</summary>
    public static List<string> ChildArguments(IReadOnlyList<string> args, string map)
    {
        var child = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] is "--warfront-new" or "--warfront-stage") continue;
            if (args[i] == "--map") { i++; continue; }
            child.Add(args[i]);
        }
        child.Add("--map"); child.Add(map); child.Add("--warfront-stage");
        return child;
    }

    /// <summary>Exit codes a restart cannot fix: the supervisor ends with them (0 clean leave; 1 no install, bad run or map; 2 command line; 3 port taken). 12 is the next stage.</summary>
    public static bool EndsSupervisor(int code) => code is 0 or 1 or 2 or 3;

    /// <summary>One log line with a UTC ISO timestamp, the form the server's own lines use.</summary>
    public static string Stamp(string message) => $"[{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}] warfront: {message}";

    /// <summary>The warm-up child's arguments: the supervisor's own without <c>--warfront-new</c>, <c>--warfront-stage</c> and <c>--map</c>, plus <c>--warfront-prebuild</c>.</summary>
    public static List<string> PrebuildArguments(IReadOnlyList<string> args)
    {
        var child = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] is "--warfront-new" or "--warfront-stage" or "--warfront-prebuild") continue;
            if (args[i] == "--map") { i++; continue; }
            child.Add(args[i]);
        }
        child.Add("--warfront-prebuild");
        return child;
    }

    sealed class RealLauncher(string exe, string[] host, IReadOnlyList<string> args) : IWarfrontStageLauncher, IWarfrontPrebuildLauncher
    {
        sealed class Real(Process p) : IWarfrontStageProcess
        {
            public bool WaitForExit(int milliseconds) => p.WaitForExit(milliseconds);
            public int ExitCode => p.ExitCode;
            public void Kill() { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            public void Dispose() => p.Dispose();
        }

        public IWarfrontStageProcess Start(string map)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            info.Environment[WarfrontRunFiles.SupervisorPidVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            foreach (var h in host) info.ArgumentList.Add(h);
            foreach (var a in ChildArguments(args, map)) info.ArgumentList.Add(a);
            return new Real(Process.Start(info) ?? throw new InvalidOperationException("the Warfront stage did not start"));
        }

        IWarfrontStageProcess IWarfrontPrebuildLauncher.Start()
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            info.Environment[WarfrontRunFiles.SupervisorPidVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            foreach (var h in host) info.ArgumentList.Add(h);
            foreach (var a in PrebuildArguments(args)) info.ArgumentList.Add(a);
            var p = Process.Start(info) ?? throw new InvalidOperationException("the Warfront map warm-up did not start");
            // Low priority (nice on Linux): the warm-up must never compete with the stage that serves the players.
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { }
            return new Real(p);
        }
    }

    /// <summary>The warm-up child while the loop runs: started at most once, logged once when it ends, killed when the supervisor stops or the loop ends.</summary>
    sealed class PrebuildHold
    {
        public bool Started;
        public IWarfrontStageProcess? Child;
        DateTime _began;

        public void Start(IWarfrontPrebuildLauncher launcher, Action<string> log)
        {
            Started = true;
            try { Child = launcher.Start(); _began = DateTime.UtcNow; log(Stamp("map warm-up started (low priority, one map at a time; players can join meanwhile)")); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            { log(Stamp($"map warm-up did not start ({e.Message}); the first deployment to each map builds it")); }
        }

        /// <summary>Notices the child's end (logged once, never restarted).</summary>
        public void Poll(Action<string> log)
        {
            if (Child is not { } c) return;
            bool ended;
            try { ended = c.WaitForExit(0); } catch (InvalidOperationException) { ended = true; }
            if (!ended) return;
            int code = SafeExitCode(c);
            double s = (DateTime.UtcNow - _began).TotalSeconds;
            log(Stamp(code == 0 ? $"map warm-up finished after {s:0} s" : $"map warm-up ended with code {code} after {s:0} s; not restarted (the first deployment to a map it missed builds it)"));
            c.Dispose(); Child = null;
        }

        public void Stop(Action<string> log)
        {
            if (Child is not { } c) return;
            c.Kill(); try { c.WaitForExit(5000); } catch (InvalidOperationException) { }
            log(Stamp("map warm-up stopped"));
            c.Dispose(); Child = null;
        }
    }

    static volatile IWarfrontStageProcess? _running;

    public static int Run(IReadOnlyList<string> args, ServerArguments cli)
    {
        string dir = WarfrontRunFiles.Dir;
        WarfrontSupervisorLock? runLock = null;
        if (cli.WarfrontDedicated)
        {
            try
            {
                runLock = WarfrontSupervisorLock.TryAcquire(dir, out string? refusal);
                if (runLock is null) { Console.Error.WriteLine(Stamp($"smserver: {refusal}")); return WarfrontSupervisorLock.ExitCode; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine(Stamp($"smserver: the run folder is not usable: {e.Message}")); return 1; }
        }
        using var lockHold = runLock;
        try
        {
            if (cli.WarfrontNew && WarfrontSave.Exists(dir))
            {
                var old = WarfrontSave.Load(dir);
                if (old is not null) Console.WriteLine(Stamp($"--warfront-new: the run on disk is archived ({(old.IsOver ? "finished" : "unfinished, as abandoned")}): {Path.GetFileName(WarfrontSave.Archive(dir, old, DateTime.UtcNow))}"));
                try { File.Delete(Path.Combine(dir, WarfrontRunFiles.StageFile)); } catch (IOException) { }
            }
            if (!WarfrontSave.Exists(dir))
            {
                var fresh = WarfrontRunFiles.NewRun(dir);
                Console.WriteLine(Stamp($"a new run (seed {fresh.RunSeed:X}) on the ship"));
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(Stamp($"smserver: the Warfront run could not be read or created: {e.Message}"));
            return 1;
        }

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        string[] host = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? [Environment.GetCommandLineArgs()[0]] : [];
        var launcher = new RealLauncher(exe, host, args);
        using var stop = new ManualResetEventSlim(false);
        PosixSignalRegistration? term = null;
        if (cli.WarfrontDedicated)
        {
            // SIGTERM (systemd stop, docker stop) and Ctrl+C: a graceful stop (the child saves and leaves, see Loop).
            try { term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Set(); }); }
            catch (Exception e) when (e is PlatformNotSupportedException or NotSupportedException) { }
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        }
        else
        {
            // Player-hosted: closing the supervisor kills the running child (unchanged).
            Console.CancelKeyPress += (_, e) => { try { _running?.Kill(); } catch (InvalidOperationException) { } };
        }
        using (term)
            return Loop(dir, launcher, cli.WarfrontDedicated, new WarfrontSupervisorTiming(), stop, Console.WriteLine, cli.WarfrontDedicated ? launcher : null);
    }

    /// <summary>The supervisor's loop: injectable launcher, stop event and log, so the tests run it with a fake child and a short timing.</summary>
    public static int Loop(string dir, IWarfrontStageLauncher launcher, bool dedicated, WarfrontSupervisorTiming timing, ManualResetEventSlim stop, Action<string> log, IWarfrontPrebuildLauncher? prebuild = null)
    {
        var hold = new PrebuildHold();
        try { return LoopCore(dir, launcher, dedicated, timing, stop, log, dedicated ? prebuild : null, hold); }
        finally { hold.Stop(log); }   // whatever ends the loop ends the warm-up child too
    }

    static int LoopCore(string dir, IWarfrontStageLauncher launcher, bool dedicated, WarfrontSupervisorTiming timing, ManualResetEventSlim stop, Action<string> log, IWarfrontPrebuildLauncher? prebuild, PrebuildHold hold)
    {
        string stopFile = Path.Combine(dir, WarfrontRunFiles.StopFile);
        try { File.Delete(stopFile); } catch (IOException) { }   // a stop request left by an earlier supervisor must not stop this one's first child
        string? lastShip = null;
        int stage = 1, restarts = 0, rapid = 0;
        while (true)
        {
            if (stop.IsSet) { log(Stamp("stop requested: no stage started, exiting 0")); return 0; }
            WarfrontCampaign? run;
            try { run = WarfrontSave.Load(dir); }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { Console.Error.WriteLine(Stamp($"smserver: run.json is unreadable: {e.Message}")); return 1; }
            string map = WarfrontStagePlan.NextMap(run, lastShip ?? WarfrontRunFiles.ReadStage(dir)?.Ship);
            if (WarfrontStagePlan.HasShip(map)) lastShip = map;
            log(Stamp($"stage {stage} on {map} ({(run is null ? "no run" : run.Phase.ToString())})"));
            var watch = Stopwatch.StartNew();
            using var child = launcher.Start(map);
            if (!dedicated) _running = child;
            bool stopping = false;
            TimeSpan deadline = TimeSpan.Zero;
            while (!child.WaitForExit(dedicated ? timing.PollMilliseconds : Timeout.Infinite))
            {
                if (prebuild is not null && !stop.IsSet)
                {
                    if (!hold.Started && watch.Elapsed >= timing.PrebuildAfter) hold.Start(prebuild, log);   // this stage has run long enough to count as ready
                    hold.Poll(log);
                }
                if (!stop.IsSet) continue;
                if (!stopping)
                {
                    hold.Stop(log);   // nothing to save there (cache writes are atomic): it goes at once, the stage gets the grace
                    stopping = true; deadline = watch.Elapsed + timing.StopGrace;
                    log(Stamp("stop requested: the stage is asked to save and leave"));
                    try { File.WriteAllText(stopFile, "stop"); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log(Stamp($"the stop request could not be written ({e.Message}); the stage is killed")); deadline = watch.Elapsed; }
                }
                if (watch.Elapsed >= deadline)
                {
                    log(Stamp("the stage did not leave in time: killed"));
                    child.Kill(); child.WaitForExit(5000);
                    break;
                }
            }
            _running = null;
            int code = SafeExitCode(child);
            TimeSpan ran = watch.Elapsed;
            log(Stamp($"stage {stage} ended with code {code} after {ran.TotalSeconds:0.#} s"));
            if (stopping || stop.IsSet) { try { File.Delete(stopFile); } catch (IOException) { } log(Stamp("stopped: exiting 0")); return 0; }
            if (code == ServerCommandLine.WarfrontStageExit) { stage++; restarts = 0; continue; }
            if (!dedicated || EndsSupervisor(code)) return code;
            if (ran >= timing.StableAfter) { restarts = 0; rapid = 0; } else rapid++;
            TimeSpan wait = timing.Backoff[Math.Min(restarts, timing.Backoff.Length - 1)];
            restarts++;
            log(Stamp($"stage {stage} ended unexpectedly (code {code}): restart {restarts} from the last save in {wait.TotalSeconds:0.#} s"));
            if (rapid >= timing.CrashLoopAfter) log(Stamp($"CRASH LOOP: the stage child has failed {rapid} times in a row without running {timing.StableAfter.TotalSeconds:0.#} s; still retrying every {wait.TotalSeconds:0.#} s (look at the stage's own log)"));
            if (stop.Wait(wait)) { log(Stamp("stop requested while waiting: exiting 0")); return 0; }
        }
    }

    static int SafeExitCode(IWarfrontStageProcess p)
    {
        try { return p.ExitCode; } catch (InvalidOperationException) { return -1; }
    }
}
