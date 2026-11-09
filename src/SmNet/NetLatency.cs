using System.Globalization;

namespace SmNet;

/// <summary>
/// net/responsive (ours; a test aid, off by default): the client's latency readout. With <c>SM_NET_LATENCY=&lt;s&gt;</c> in the client's environment, or
/// <see cref="ClientOptions.LatencyReadout"/> (the Godot client's <c>--net-latency [s]</c>), <see cref="GameClient"/> keeps per-stage samples and prints one
/// <c>NETLAT</c> line every that many seconds (count, mean, median, 90th percentile, worst of each stage in the window). Stages:
/// <list type="bullet">
/// <item><c>edge-draw</c>: an input edge (a game key pressed or let go, <c>Update(.., tickNow: true)</c>) to the first frame the own drawn position leaves the path it was
/// on (<see cref="GameClient.NoteOwnDrawn"/>, fed by the renderer; only edges that change the movement count; 1 cm off the extrapolated path); <c>edge-draw-5cm</c>: to 5 cm.</item>
/// <item><c>lead</c> (ticks) and <c>lead-ms</c>: the input lead the server reports in each snapshot (its newest input's tick minus the tick it just ran): how long an input
/// waits on the server before the tick that applies it.</item>
/// <item><c>rtt</c>: the round trip from snapshot echoes after the map has loaded: the time from sending the input the server names as its newest (snapshot tick plus lead) to
/// the snapshot's arrival. An upper bound: it includes the server's wait for its next snapshot send and the client's wait for its next frame (about 17 to 25 ms at 60 fps).</item>
/// <item><c>press-apply</c>: the estimate of a sampled input's way to the server's tick: half the round trip plus the lead.</item>
/// <item><c>shot-hit</c>: an own ranged shot (the prediction's fired tick) to the server's hit report on it (the newest own fired tick at or before the snapshot's).</item>
/// <item><c>marker</c>: an own ranged shot to the hit marker shown (the renderer's <see cref="GameClient.NoteHitMarker"/>: the predicted one, or the server's).</item>
/// <item><c>interp</c>: the remote players' interpolation delay in use; <c>snap-gap</c>: the time between two snapshots' arrivals; <c>remote</c>: how far behind the server's
/// newest tick remote players are drawn (the newest snapshot's tick plus its age, less the render tick) plus half the round trip: the age of what is seen.</item>
/// </list>
/// Engine-free; nothing in the simulation or on the wire depends on it.
/// </summary>
public sealed class NetLatencyReadout(double intervalSeconds, Action<string>? log = null)
{
    /// <summary>The seconds between two lines from <c>SM_NET_LATENCY</c> (0: off).</summary>
    public static double EnvSeconds { get; } =
        double.TryParse(Environment.GetEnvironmentVariable("SM_NET_LATENCY"), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0 ? s : 0;

    /// <summary>The stages in the order the line prints them.</summary>
    public static readonly string[] Stages = ["edge-draw", "edge-draw-5cm", "lead", "lead-ms", "rtt", "press-apply", "shot-hit", "marker", "interp", "snap-gap", "remote"];

    /// <summary>Samples kept per stage over the whole run (tests and the final report); the oldest go first.</summary>
    public const int MaxKept = 100_000;

    readonly Dictionary<string, List<double>> _window = new(), _all = new();
    TimeSpan _since = TimeSpan.MinValue;

    public double IntervalSeconds { get; } = intervalSeconds;

    /// <summary>Adds one sample of <paramref name="stage"/>; non-finite samples are dropped.</summary>
    public void Add(string stage, double value)
    {
        if (!double.IsFinite(value)) return;
        (_window.TryGetValue(stage, out var w) ? w : _window[stage] = new()).Add(value);
        var all = _all.TryGetValue(stage, out var a) ? a : _all[stage] = new();
        all.Add(value);
        if (all.Count > MaxKept) all.RemoveRange(0, all.Count - MaxKept);
    }

    /// <summary>Every sample of a stage so far (oldest first; empty when none).</summary>
    public IReadOnlyList<double> All(string stage) => _all.TryGetValue(stage, out var a) ? a : [];

    /// <summary>Prints a line when the interval has passed since the last (the first call starts the window).</summary>
    public void Step(TimeSpan now)
    {
        if (_since == TimeSpan.MinValue) { _since = now; return; }
        if ((now - _since).TotalSeconds < IntervalSeconds) return;
        (log ?? Console.WriteLine)("NETLAT " + Line(_window));
        foreach (var list in _window.Values) list.Clear();
        _since = now;
    }

    /// <summary>The whole run's line (every sample kept).</summary>
    public string Summary() => Line(_all);

    static string Line(Dictionary<string, List<double>> samples) =>
        string.Join("; ", Stages.Where(s => samples.TryGetValue(s, out var v) && v.Count > 0).Select(s => $"{s} {Stats(samples[s])}"));

    /// <summary>"n 12 mean 3.4 p50 3.0 p90 5.0 max 7.1" (invariant culture; "none" for no samples).</summary>
    public static string Stats(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return "none";
        var sorted = values.OrderBy(v => v).ToArray();
        double At(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return string.Create(CultureInfo.InvariantCulture, $"n {sorted.Length} mean {sorted.Average():F1} p50 {At(0.5):F1} p90 {At(0.9):F1} max {sorted[^1]:F1}");
    }

    /// <summary>The <paramref name="p"/> quantile of the values (nearest rank; NaN for none).</summary>
    public static double Quantile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}

/// <summary>
/// net/responsive: <c>edge-draw</c>'s detector (see <see cref="NetLatencyReadout"/>). The own drawn position is followed frame by frame; at an input edge its position and
/// velocity are kept, and the first later frame whose drawn position is more than <see cref="Threshold"/> off the straight continuation of that motion ends the sample.
/// An edge with no visible change within <see cref="Timeout"/> (a key that does not move the marine) gives none.
/// </summary>
public sealed class EdgeDrawDetector(float threshold = EdgeDrawDetector.Threshold)
{
    /// <summary>Metres off the extrapolated path that count as a visible change (ours).</summary>
    public const float Threshold = 0.01f;
    /// <summary><c>edge-draw-5cm</c>: the change grown to 5 cm (how fast it builds, not only when it starts).</summary>
    public const float FarThreshold = 0.05f;
    /// <summary>An edge with no change for this long gives no sample.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(0.5);

    System.Numerics.Vector3 _last, _velocity, _edgePosition;
    TimeSpan _lastAt = TimeSpan.MinValue, _edgeAt = TimeSpan.MinValue;
    bool _open;

    /// <summary>An input edge seen at <paramref name="now"/> (a second edge before the first showed restarts the sample).</summary>
    public void Edge(TimeSpan now)
    {
        if (_lastAt == TimeSpan.MinValue) return;
        _open = true; _edgeAt = now; _edgePosition = _last;
    }

    /// <summary>The drawn position this frame; returns the milliseconds from the edge when this frame shows its change, else null.</summary>
    public double? Drawn(System.Numerics.Vector3 position, TimeSpan now)
    {
        double? result = null;
        if (_open)
        {
            var expected = _edgePosition + _velocity * (float)(now - _edgeAt).TotalSeconds;
            if (System.Numerics.Vector3.Distance(expected, position) > threshold) { result = (now - _edgeAt).TotalMilliseconds; _open = false; }
            else if (now - _edgeAt > Timeout) _open = false;
        }
        if (!_open && _lastAt != TimeSpan.MinValue && now > _lastAt)
            _velocity = (position - _last) / (float)(now - _lastAt).TotalSeconds;
        _last = position; _lastAt = now;
        return result;
    }
}
