namespace SmNet;

/// <summary>
/// net/responsive (ours; client presentation, no wire change): the remote players' interpolation delay, adapted to the snapshots that arrive. Over the last
/// <see cref="WindowSeconds"/> of snapshots it takes the spacing of their ticks (the server's snapshot interval: one tick at 30 Hz, 1.5 at 20 Hz), the 95th percentile
/// of the gaps between them (a lost snapshot is a double gap) and the spread of their arrival times against their ticks (jitter, including the wait for the client's
/// next frame: the arrival offset's 98th percentile less its median). The delay wanted is the larger of <see cref="BaseIntervals"/> snapshot intervals and the 95th-percentile gap plus the
/// jitter plus <see cref="MarginSeconds"/>, within [floor, ceiling]. It rises at once and falls with a time constant of <see cref="FallSeconds"/>.
/// With no loss and little jitter it is <see cref="BaseIntervals"/> intervals (the owner, 2026-11-09: as low as it plays well): 50 ms at 30 Hz (75 ms at 20 Hz); a late
/// snapshot is bridged by the client's capped extrapolation (<see cref="ClientOptions.MaxExtrapolationTicks"/>).
/// </summary>
public sealed class InterpolationDelayEstimator
{
    /// <summary>The window of snapshots the estimate looks at (seconds of arrivals).</summary>
    public const double WindowSeconds = 2.0;
    /// <summary>Time constant of a falling delay (seconds; a rising one is taken at once).</summary>
    public const double FallSeconds = 2.0;
    /// <summary>Added to the gap and jitter (seconds; ours).</summary>
    public const double MarginSeconds = 0.005;
    /// <summary>Snapshots needed before the estimate moves from its start.</summary>
    public const int MinSamples = 8;
    /// <summary>Snapshot intervals of delay on a clean link (ours: 1.5, half an interval of room for arrival jitter on top of the one being interpolated).</summary>
    public const double BaseIntervals = 1.5;

    readonly Queue<(uint Tick, double Arrival)> _window = new();
    double _delay = double.NaN;
    double _lastAt = double.NaN;

    /// <summary>The delay in use (seconds); NaN before the first <see cref="Note"/>.</summary>
    public double DelaySeconds => _delay;

    /// <summary>The wanted delay of the last <see cref="Note"/> before smoothing (seconds; diagnostics).</summary>
    public double TargetSeconds { get; private set; } = double.NaN;

    /// <summary>Forgets the window (a reconnect); the next note starts again at <paramref name="startSeconds"/>.</summary>
    public void Reset() { _window.Clear(); _delay = double.NaN; _lastAt = double.NaN; TargetSeconds = double.NaN; }

    /// <summary>
    /// A snapshot of <paramref name="tick"/> arrived (was processed) at <paramref name="arrivalSeconds"/>; returns the delay to use now (seconds).
    /// </summary>
    /// <param name="startSeconds">The delay before enough snapshots have arrived.</param>
    public double Note(uint tick, double arrivalSeconds, double tickSeconds, double floorSeconds, double ceilingSeconds, double startSeconds)
    {
        if (!double.IsFinite(arrivalSeconds) || !(tickSeconds > 0)) return double.IsNaN(_delay) ? startSeconds : _delay;
        ceilingSeconds = Math.Max(ceilingSeconds, floorSeconds);
        _window.Enqueue((tick, arrivalSeconds));
        while (_window.Count > 2 && arrivalSeconds - _window.Peek().Arrival > WindowSeconds) _window.Dequeue();
        double target = _window.Count >= MinSamples ? Required(_window.ToArray(), tickSeconds) : startSeconds;
        target = Math.Clamp(target, floorSeconds, ceilingSeconds);
        TargetSeconds = target;
        double dt = double.IsNaN(_lastAt) ? 0 : Math.Max(0, arrivalSeconds - _lastAt);
        _lastAt = arrivalSeconds;
        if (double.IsNaN(_delay) || target >= _delay) _delay = target;
        else _delay += (target - _delay) * (1 - Math.Exp(-dt / FallSeconds));
        _delay = Math.Clamp(_delay, floorSeconds, ceilingSeconds);
        return _delay;
    }

    /// <summary>The delay a window of (tick, arrival) pairs needs (seconds; see the class summary). Ticks are taken in arrival order; out-of-order ones count as gaps of 0.</summary>
    public static double Required(IReadOnlyList<(uint Tick, double Arrival)> window, double tickSeconds)
    {
        if (window.Count < 2) return double.NaN;
        var ticks = window.Select(w => w.Tick).ToArray();
        uint first = ticks.Min(), last = ticks.Max();
        double interval = (last - first) / (double)(window.Count - 1) * tickSeconds;
        var gaps = new List<double>(window.Count - 1);
        for (int i = 1; i < window.Count; i++) gaps.Add(Math.Max(0, (long)window[i].Tick - window[i - 1].Tick) * tickSeconds);
        var offsets = window.Select(w => w.Arrival - w.Tick * tickSeconds).ToList();
        double jitter = Math.Max(0, NetLatencyReadout.Quantile(offsets, 0.98) - NetLatencyReadout.Quantile(offsets, 0.5));
        double gap = NetLatencyReadout.Quantile(gaps, 0.95);
        return Math.Max(BaseIntervals * interval, gap + jitter + MarginSeconds);
    }
}
