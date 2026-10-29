using System;
using System.Diagnostics;
using System.Threading;
using Godot;

namespace SmClient;

/// <summary>
/// Test-bot diagnostic: a background thread that reports when the main thread has not finished a frame for
/// <see cref="StallSeconds"/>. The main thread calls <see cref="Frame"/> at the end of every frame and <see cref="Phase"/>
/// at the start of each part of it, so a report names the part it is in, how long it has been there, and what the process
/// did meanwhile (CPU seconds used, garbage collections and their pause, thread-pool queue), which separates a hang
/// (a part that never returns: no CPU) from starvation (CPU use but slow) from a loop (full CPU). Reads and prints only; it
/// never touches the game state.
/// </summary>
sealed class FrameWatchdog : IDisposable
{
    /// <summary>Seconds without a finished frame after which a report is printed (and every this long again while it lasts).</summary>
    public const double StallSeconds = 2.0;

    readonly Thread _thread;
    readonly Process _process = Process.GetCurrentProcess();
    volatile bool _stop;
    volatile string _phase = "start";
    long _phaseAt = Stopwatch.GetTimestamp();
    long _frameAt = Stopwatch.GetTimestamp();
    long _frames;
    readonly string _name;
    readonly string? _file;

    /// <param name="file">Optional file the reports and a 5 s heartbeat are also appended to, independent of the engine's console (test aid).</param>
    public FrameWatchdog(string name, string? file = null)
    {
        _name = name;
        _file = file;
        _thread = new Thread(Run) { IsBackground = true, Name = "FrameWatchdog" };
        _thread.Start();
    }

    /// <summary>M40-T9 (<c>--frame-profile</c>): the per-subsystem frame-time profile fed by the same phase marks (main thread only).</summary>
    public FrameProfile? Profile { get; set; }

    /// <summary>Main thread: the part of the frame being entered.</summary>
    public void Phase(string name)
    {
        Profile?.Phase(name);
        _phase = name;
        Interlocked.Exchange(ref _phaseAt, Stopwatch.GetTimestamp());
    }

    /// <summary>The longest time between two finished frames so far, seconds (the first frames, which load the map, included).</summary>
    public double LongestFrameSeconds => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _longest)).TotalSeconds;
    long _longest;

    /// <summary>Main thread: a frame is finished.</summary>
    public void Frame()
    {
        Profile?.Frame();
        _phase = "between frames";
        long now = Stopwatch.GetTimestamp();
        long gap = now - Interlocked.Read(ref _frameAt);
        if (gap > _longest) Interlocked.Exchange(ref _longest, gap);
        Interlocked.Exchange(ref _phaseAt, now);
        Interlocked.Exchange(ref _frameAt, now);
        Interlocked.Increment(ref _frames);
    }

    void Run()
    {
        double lastReportAt = 0, lastCpu = CpuSeconds();
        long lastGcPause = (long)GC.GetTotalPauseDuration().TotalMilliseconds;
        while (!_stop)
        {
            Thread.Sleep(250);
            long now = Stopwatch.GetTimestamp();
            if (_file is not null && Stopwatch.GetElapsedTime(_lastBeat, now).TotalSeconds >= 5)
            {
                _lastBeat = now;
                Write($"{DateTime.Now:HH:mm:ss} heartbeat {_name}: frame {Interlocked.Read(ref _frames)}, last frame {Stopwatch.GetElapsedTime(Interlocked.Read(ref _frameAt), now).TotalSeconds:F1} s ago, phase '{_phase}' for {Stopwatch.GetElapsedTime(Interlocked.Read(ref _phaseAt), now).TotalSeconds:F1} s, cpu {CpuSeconds():F0} s");
            }
            double silent = Stopwatch.GetElapsedTime(Interlocked.Read(ref _frameAt), now).TotalSeconds;
            if (silent < StallSeconds) { lastReportAt = 0; lastCpu = CpuSeconds(); lastGcPause = (long)GC.GetTotalPauseDuration().TotalMilliseconds; continue; }
            if (silent - lastReportAt < StallSeconds) continue;
            lastReportAt = silent;
            double inPhase = Stopwatch.GetElapsedTime(Interlocked.Read(ref _phaseAt), now).TotalSeconds;
            double cpu = CpuSeconds();
            long pause = (long)GC.GetTotalPauseDuration().TotalMilliseconds;
            string report = $"watchdog {_name}: no frame finished for {silent:F1} s (frame {Interlocked.Read(ref _frames)}); main thread in '{_phase}' for {inPhase:F1} s; " +
                     $"process cpu +{cpu - lastCpu:F1} s since the stall began; gc pause +{pause - lastGcPause} ms (gen0/1/2 {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}); " +
                     $"heap {GC.GetTotalMemory(false) / 1048576} MB, thread pool {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} queued";
            Write($"{DateTime.Now:HH:mm:ss} {report}");
            GD.Print(report);
        }
    }

    long _lastBeat = Stopwatch.GetTimestamp();

    void Write(string line)
    {
        if (_file is null) return;
        try { System.IO.File.AppendAllText(_file, line + System.Environment.NewLine); }
        catch (Exception) { }   // a bad SM_WATCHDOG_FILE must never take the bot down from this thread
    }

    double CpuSeconds()
    {
        try { _process.Refresh(); return _process.TotalProcessorTime.TotalSeconds; }
        catch (InvalidOperationException) { return 0; }
    }

    public void Dispose() => _stop = true;
}
