using SmSim;

namespace SmNet;

/// <summary>
/// net/responsive (ours; no wire change): the latency readout's feed (<see cref="NetLatencyReadout"/>, off unless asked for) and the round trip measured from
/// snapshot echoes after the map has loaded (<see cref="EchoRoundTrip"/>, always on: the input-lead steering's hold uses it).
/// </summary>
public sealed partial class GameClient
{
    /// <summary>The readout, or null when off (<see cref="ClientOptions.LatencyReadout"/> 0 and no <c>SM_NET_LATENCY</c>).</summary>
    public NetLatencyReadout? Latency { get; private set; }

    /// <summary>
    /// The round trip from snapshot echoes (ms, smoothed; <see cref="RttEstimator"/>): the time from sending the input a snapshot names as the server's newest (its tick plus
    /// the reported lead) to the snapshot's arrival. Measured from the first snapshots on, so a handshake made during the map load does not stand for the link.
    /// -1 before the first sample.
    /// </summary>
    public int EchoRoundTripMs => _echoRtt.Ms;

    readonly RttEstimator _echoRtt = new();
    /// <summary>When each recent tick's input was first sent (index: tick modulo the length; the tick is kept to tell stale entries).</summary>
    readonly (uint Tick, TimeSpan At)[] _sentAt = new (uint, TimeSpan)[256];
    /// <summary>The own predicted shots' ticks and times not yet matched to a hit report (oldest first; for <c>shot-hit</c> and <c>marker</c>).</summary>
    readonly SortedDictionary<uint, TimeSpan> _firedAt = new();
    TimeSpan _lastFiredAt = TimeSpan.MinValue, _lastReportShotAt = TimeSpan.MinValue;
    TimeSpan _snapshotArrivedAt = TimeSpan.MinValue;
    readonly EdgeDrawDetector _edgeDraw = new(), _edgeDrawFar = new(EdgeDrawDetector.FarThreshold);

    void StartLatency()
    {
        double seconds = _options.LatencyReadout > 0 ? _options.LatencyReadout : NetLatencyReadout.EnvSeconds;
        if (seconds > 0 && Latency is null) Latency = new NetLatencyReadout(seconds, _options.LatencyLog);
    }

    /// <summary>From <see cref="StepTick"/>: the tick's input was sent now; <paramref name="fired"/> when its prediction fired a ranged shot.</summary>
    void NoteSent(uint tick, bool fired)
    {
        var now = _clock.Now;
        _sentAt[tick % (uint)_sentAt.Length] = (tick, now);
        if (!fired || Latency is null) return;
        _firedAt[tick] = now;
        _lastFiredAt = now;
        while (_firedAt.Count > 64) _firedAt.Remove(_firedAt.Keys.First());
    }

    /// <summary>From <see cref="OnSnapshot"/>, for every accepted snapshot: the echo round trip, the lead and the arrival gaps.</summary>
    void NoteSnapshotLatency(Snapshot s)
    {
        var now = _clock.Now;
        if (s.InputLead != Protocol.NoLead)
        {
            long newest = (long)s.ServerTick + s.InputLead;
            if (newest > 0 && _sentAt[(uint)newest % (uint)_sentAt.Length] is var (tick, at) && tick == (uint)newest && at != default)
                _echoRtt.Add((now - at).TotalMilliseconds);
            Latency?.Add("lead", s.InputLead);
            Latency?.Add("lead-ms", s.InputLead * _config.TickSeconds * 1000.0);
            if (_echoRtt.Ms >= 0) Latency?.Add("press-apply", _echoRtt.Ms / 2.0 + Math.Max(0, s.InputLead) * _config.TickSeconds * 1000.0);
        }
        if (Latency is not null && _echoRtt.Ms >= 0) Latency.Add("rtt", _echoRtt.Ms);
        if (_snapshotArrivedAt != TimeSpan.MinValue) Latency?.Add("snap-gap", (now - _snapshotArrivedAt).TotalMilliseconds);
        _snapshotArrivedAt = now;
    }

    /// <summary>From <see cref="OnHits"/>: a report of ranged hits arrived in the snapshot of <paramref name="serverTick"/>.</summary>
    void NoteHitsLatency(uint serverTick, bool ranged)
    {
        if (Latency is null || !ranged) return;
        uint? shot = null;
        foreach (var t in _firedAt.Keys) if (t <= serverTick) shot = t; else break;
        if (shot is not { } s) return;
        _lastReportShotAt = _firedAt[s];
        Latency.Add("shot-hit", (_clock.Now - _firedAt[s]).TotalMilliseconds);
        foreach (var t in _firedAt.Keys.Where(t => t <= s).ToList()) _firedAt.Remove(t);
    }

    /// <summary>
    /// The renderer showed a hit marker for the own ranged fire now: <c>marker</c> is the time since the shot it shows. <paramref name="predicted"/>: the client's own trace
    /// of the newest own shot; else the server's report, whose shot is the one <c>shot-hit</c> matched to it (none when that is older than a second).
    /// </summary>
    public void NoteHitMarker(bool predicted)
    {
        var shot = predicted ? _lastFiredAt : _lastReportShotAt;
        if (Latency is null || shot == TimeSpan.MinValue) return;
        double ms = (_clock.Now - shot).TotalMilliseconds;
        if (ms <= 1000) Latency.Add("marker", ms);
        if (!predicted) _lastReportShotAt = TimeSpan.MinValue;
    }

    /// <summary>The renderer's own drawn position this frame (game space), for <c>edge-draw</c>. Cheap; nothing is kept without the readout.</summary>
    public void NoteOwnDrawn(System.Numerics.Vector3 position)
    {
        if (Latency is null) return;
        if (_edgeDraw.Drawn(position, _clock.Now) is { } ms) Latency.Add("edge-draw", ms);
        if (_edgeDrawFar.Drawn(position, _clock.Now) is { } far) Latency.Add("edge-draw-5cm", far);
    }

    /// <summary>Once per <see cref="Update"/> while connected: the input edge, the remote players' delay, and the line when due.</summary>
    void StepLatency(TimeSpan now, bool edge)
    {
        if (Latency is null) return;
        if (edge) { _edgeDraw.Edge(now); _edgeDrawFar.Edge(now); }
        if (_snapshots.Count > 1)
        {
            double serverNow = _latestSnapshotTick + (now - _latestSnapshotAt).TotalSeconds * _config.TickRate;
            double behindMs = (serverNow - RenderTick()) * _config.TickSeconds * 1000.0;
            Latency.Add("interp", CurrentInterpolationDelay.TotalMilliseconds);
            Latency.Add("remote", behindMs + Math.Max(0, _echoRtt.Ms) / 2.0);
        }
        Latency.Step(now);
    }
}
