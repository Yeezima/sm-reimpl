using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Godot;

namespace SmClient;

/// <summary>
/// M40-T9 test aid (<c>--frame-profile [--frame-profile-warmup s]</c>): a frame-time profile by subsystem for the networked client.
/// <see cref="FrameWatchdog.Phase"/> (the <c>Ph(...)</c> calls of the frame) and <see cref="FrameWatchdog.Frame"/> feed it, so the time between two
/// phase marks is charged to the phase that was entered first; the time after the client's <c>_Process</c> ends until it begins again is the engine
/// ("between frames": the other nodes' processing, physics and the renderer). Every 5 s it prints one line with the frame-time percentiles and
/// the phase table (mean and worst ms), and each frame over <see cref="HitchMs"/> with the phase that took the most; at the end a summary of
/// all frames after the warm-up (the first <c>warmup</c> seconds, where the one-off loads sit, are reported separately). Reads time only; nothing
/// of the game state is touched. Not used by the game itself.
/// </summary>
sealed class FrameProfile
{
    /// <summary>A frame longer than this (ms) is a hitch (the T9 criterion).</summary>
    public const double HitchMs = 33.0;
    const double WindowSeconds = 5.0;
    /// <summary>The time outside the client's <c>_Process</c> (rendering, physics, the other nodes): the engine's share of a frame.</summary>
    public const string EnginePhase = "between frames";

    readonly string _name;
    readonly double _warmupSeconds;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _lastMark = Stopwatch.GetTimestamp();
    long _lastAlloc = GC.GetAllocatedBytesForCurrentThread();
    long _frameStart = Stopwatch.GetTimestamp();
    string _phase = "start";
    readonly Dictionary<string, double> _thisFrame = new();
    readonly Dictionary<string, double> _thisAlloc = new();   // bytes allocated by the main thread per phase this frame
    // The window and the overall (after warm-up) tables.
    readonly Table _window = new(), _after = new(), _warm = new();
    double _windowStart;
    int _frameGc0, _frameGc1, _frameGc2;
    long _framePause;
    int _gcLast2, _gcLast0;
    long _gcPauseLast;

    public FrameProfile(string name, double warmupSeconds)
    {
        _name = name;
        _warmupSeconds = warmupSeconds;
        _gcLast2 = GC.CollectionCount(2); _gcLast0 = GC.CollectionCount(0);
        _gcPauseLast = (long)GC.GetTotalPauseDuration().TotalMilliseconds;
    }

    sealed class Table
    {
        public readonly List<float> Frames = new();
        /// <summary>The same frames without the engine's share ("between frames"): the time the client's own code took.</summary>
        public readonly List<float> Client = new();
        public readonly Dictionary<string, double> Sum = new();
        public readonly Dictionary<string, double> Worst = new();
        public readonly Dictionary<string, double> Alloc = new();
        public int Hitches;
        public double WorstFrame;
        public string WorstPhase = "";

        public void Add(double frameMs, Dictionary<string, double> phases, Dictionary<string, double> alloc)
        {
            foreach (var (k, v) in alloc) Alloc[k] = Alloc.GetValueOrDefault(k) + v;
            Frames.Add((float)frameMs);
            Client.Add((float)(frameMs - phases.GetValueOrDefault(EnginePhase)));
            foreach (var (k, v) in phases)
            {
                Sum[k] = Sum.GetValueOrDefault(k) + v;
                if (v > Worst.GetValueOrDefault(k)) Worst[k] = v;
            }
            if (frameMs > HitchMs) Hitches++;
            if (frameMs > WorstFrame) { WorstFrame = frameMs; WorstPhase = phases.Count == 0 ? "" : phases.MaxBy(p => p.Value).Key; }
        }

        public double Percentile(double p, bool client = false)
        {
            var list = client ? Client : Frames;
            if (list.Count == 0) return 0;
            var sorted = list.OrderBy(x => x).ToArray();
            int i = Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1);
            return sorted[i];
        }

        public string Summary()
        {
            if (Frames.Count == 0) return "no frames";
            double mean = Frames.Average();
            return $"{Frames.Count} frames, mean {mean:F1} ms ({1000 / mean:F0} fps), p50 {Percentile(0.5):F1} p99 {Percentile(0.99):F1} p99.9 {Percentile(0.999):F1} worst {WorstFrame:F1} ms (in '{WorstPhase}'), {Hitches} over {HitchMs:F0} ms; client code only: p99 {Percentile(0.99, true):F1} p99.9 {Percentile(0.999, true):F1} worst {Client.Max():F1} ms, {Client.Count(x => x > HitchMs)} over {HitchMs:F0} ms";
        }

        public string PhaseTable()
        {
            var sb = new StringBuilder();
            foreach (var (k, sum) in Sum.OrderByDescending(p => p.Value).Take(14))
                sb.Append($"\n    {k,-34} mean {sum / Frames.Count,6:F2} ms  worst {Worst.GetValueOrDefault(k),6:F1} ms  alloc {Alloc.GetValueOrDefault(k) / Frames.Count / 1024,7:F1} KB/frame");
            return sb.ToString();
        }
    }

    /// <summary>Main thread: a phase begins (charges the time since the last mark to the phase that was running).</summary>
    public void Phase(string name)
    {
        long now = Stopwatch.GetTimestamp();
        Charge(now);
        _phase = name;
    }

    void Charge(long now)
    {
        double ms = Stopwatch.GetElapsedTime(_lastMark, now).TotalMilliseconds;
        _lastMark = now;
        _thisFrame[_phase] = _thisFrame.GetValueOrDefault(_phase) + ms;
        long alloc = GC.GetAllocatedBytesForCurrentThread();
        _thisAlloc[_phase] = _thisAlloc.GetValueOrDefault(_phase) + (alloc - _lastAlloc);
        _lastAlloc = alloc;
    }

    /// <summary>Main thread: the client's part of the frame is over; what follows until the next <see cref="Phase"/> is the engine's.</summary>
    public void Frame()
    {
        long now = Stopwatch.GetTimestamp();
        Charge(now);
        double frameMs = Stopwatch.GetElapsedTime(_frameStart, now).TotalMilliseconds;
        _frameStart = now;
        double t = _clock.Elapsed.TotalSeconds;
        var target = t < _warmupSeconds ? _warm : _after;
        target.Add(frameMs, _thisFrame, _thisAlloc);
        _window.Add(frameMs, _thisFrame, _thisAlloc);
        int c0 = GC.CollectionCount(0), c1 = GC.CollectionCount(1), c2 = GC.CollectionCount(2);
        long pauseNow = (long)GC.GetTotalPauseDuration().TotalMilliseconds;
        if (frameMs > HitchMs)
            GD.Print($"profile {_name}: hitch {frameMs:F1} ms at {t:F1} s (client {frameMs - _thisFrame.GetValueOrDefault(EnginePhase):F1}); " + string.Join(", ", _thisFrame.OrderByDescending(p => p.Value).Take(3).Select(p => $"{p.Key} {p.Value:F1}"))
                     + $"; gc this frame gen0 +{c0 - _frameGc0} gen1 +{c1 - _frameGc1} gen2 +{c2 - _frameGc2} pause +{pauseNow - _framePause} ms");
        _frameGc0 = c0; _frameGc1 = c1; _frameGc2 = c2; _framePause = pauseNow;
        _thisFrame.Clear(); _thisAlloc.Clear();
        _phase = EnginePhase;
        if (t - _windowStart >= WindowSeconds)
        {
            int g0 = GC.CollectionCount(0), g2 = GC.CollectionCount(2); long pause = (long)GC.GetTotalPauseDuration().TotalMilliseconds;
            GD.Print($"profile {_name}: {t:F0} s window: {_window.Summary()}; gc gen0 +{g0 - _gcLast0} gen2 +{g2 - _gcLast2} pause +{pause - _gcPauseLast} ms{_window.PhaseTable()}");
            _gcLast0 = g0; _gcLast2 = g2; _gcPauseLast = pause;
            _windowStart = t;
            _window.Frames.Clear(); _window.Client.Clear(); _window.Sum.Clear(); _window.Worst.Clear(); _window.Alloc.Clear(); _window.Hitches = 0; _window.WorstFrame = 0; _window.WorstPhase = "";
        }
    }

    /// <summary>The summary printed when the view ends: warm-up frames, then the frames after it.</summary>
    public void PrintSummary()
    {
        GD.Print($"profile {_name}: SUMMARY warm-up (first {_warmupSeconds:F0} s: the join and the one-off loads): {_warm.Summary()}{_warm.PhaseTable()}");
        GD.Print($"profile {_name}: SUMMARY after warm-up: {_after.Summary()}{_after.PhaseTable()}");
    }
}
