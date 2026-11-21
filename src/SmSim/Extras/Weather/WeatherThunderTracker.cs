namespace SmSim.Extras.Weather;

/// <summary>
/// Which thunder to play now (weather W2; presentation only): fed the preset's clock once a frame, it returns each strike's thunder exactly once, strictly in time order. The server's state repeats every two seconds and the
/// client's clock snaps to it, so the clock can step back or forward a little; a step back never replays a strike and never lowers the mark, a jump forward of more than <see cref="JumpSeconds"/> (a join, a stall) plays nothing it skipped.
/// </summary>
public sealed class WeatherThunderTracker
{
    public const double JumpSeconds = 3.0;
    /// <summary>A step back longer than this is a restarted clock (the same preset set again, e.g. an F6 override pressed twice), not the 2 s snap: the tracker starts over from there.</summary>
    public const double RestartSeconds = 2.5;
    double _mark = double.NaN, _lastSound = double.NegativeInfinity;

    /// <summary>The strikes whose sound arrives up to <paramref name="now"/> and were not played yet.</summary>
    public List<WeatherStrike> Step(double now, int span = 0)
    {
        var due = new List<WeatherStrike>();
        if (!double.IsFinite(now)) return due;
        if (double.IsNaN(_mark)) { _mark = now; return due; }
        if (_mark - now > RestartSeconds) { _mark = now; _lastSound = double.NegativeInfinity; return due; }   // a restarted clock: start over
        if (now <= _mark) return due;   // a small step back: nothing new, the mark stays
        double from = _mark;
        _mark = now;
        if (now - from > JumpSeconds) return due;   // a jump: nothing is replayed
        foreach (var s in WeatherPlan.ThunderIn(from, now, span))
            if (s.SoundTime > _lastSound) { due.Add(s); _lastSound = s.SoundTime; }
        return due;
    }
}
