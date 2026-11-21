namespace SmSim.Extras.Weather;

/// <summary>
/// The weather's particle auto-trim (weather W4; presentation only, client only, **ours**, estimates for a playtest; pure, no engine types). Fed each frame's length, it steps the particle counts down one level when frames
/// run long and back up when there is headroom, with hysteresis so it does not oscillate: it steps down when the mean frame over <see cref="DownWindowSeconds"/> is above <see cref="DownMs"/> (about 45 frames a second),
/// steps up only after <see cref="UpHoldSeconds"/> of frames averaging under <see cref="UpMs"/> (about 57 a second, a wide gap below the down line), and the up wait doubles (to <see cref="MaxUpHoldSeconds"/>) each time a down
/// step follows an up step within <see cref="BurnSeconds"/>, so a count that does not fit stops being retried. A single hitch is capped at <see cref="HitchCapMs"/>; the first <see cref="WarmupSeconds"/> (shader and mesh builds)
/// are ignored. The counts never go below the layer's floor (<see cref="Count"/>); the preset's own scale is applied first, so the floor is the preset's floor count.
/// </summary>
public sealed class WeatherTrim
{
    public const double DownMs = 22.0, UpMs = 17.5, HitchCapMs = 100.0;
    public const double DownWindowSeconds = 1.5, UpHoldSeconds = 8.0, MaxUpHoldSeconds = 128.0, WarmupSeconds = 2.0, BurnSeconds = 20.0, CalmSeconds = 300.0;
    /// <summary>The share of the preset's count kept at each level (level 0 is no trim).</summary>
    public static readonly float[] Steps = [1f, 0.8f, 0.62f, 0.46f];
    public static int MaxLevel => Steps.Length - 1;

    double _warm, _downSum, _downTime, _upSum, _upTime, _sinceChange = 1e9, _sinceDown = 1e9, _upHold = UpHoldSeconds;
    int _downN, _upN;
    bool _lastWasUp;

    public int Level { get; private set; }
    /// <summary>The frame length (ms) a frame cap or the display's refresh already explains (1000 over the cap, or the vsync interval; 0 none): a window whose mean is within <see cref="CapBand"/> of it is the cap, not weather cost, so it does not step down.</summary>
    public double ExpectedFrameMs { get; set; }
    public const double CapBand = 0.10;
    /// <summary>How many times the level has changed since the start.</summary>
    public int Changes { get; private set; }
    /// <summary>The seconds the next step up waits for (grows when a step up is undone).</summary>
    public double UpHold => _upHold;

    /// <summary>The count of a layer at <paramref name="level"/>: its full count (already scaled by the preset) times the level's share, never below <paramref name="floor"/> (nor above the full count).</summary>
    public static int Count(int full, int floor, int level)
    {
        int l = System.Math.Clamp(level, 0, MaxLevel);
        int trimmed = (int)(full * Steps[l]);
        return System.Math.Min(full, System.Math.Max(floor, trimmed));
    }

    /// <summary>One frame of <paramref name="dt"/> seconds; true when the level changed on this frame.</summary>
    public bool Frame(double dt)
    {
        if (!(dt > 0) || double.IsInfinity(dt)) return false;
        _sinceChange += dt; _sinceDown += dt;
        if (_warm < WarmupSeconds) { _warm += dt; return false; }
        double ms = System.Math.Min(dt * 1000.0, HitchCapMs);
        if (_sinceDown > CalmSeconds) _upHold = UpHoldSeconds;   // a long quiet stretch: the next attempt is cheap again
        _downSum += ms; _downN++; _downTime += dt;
        _upSum += ms; _upN++; _upTime += dt;
        bool changed = false;
        if (_downTime >= DownWindowSeconds)
        {
            double mean = _downSum / _downN;
            _downSum = 0; _downN = 0; _downTime = 0;
            if (mean > DownMs && Level < MaxLevel && !(ExpectedFrameMs > 0 && System.Math.Abs(mean - ExpectedFrameMs) <= ExpectedFrameMs * CapBand))
            {
                if (_lastWasUp && _sinceChange < BurnSeconds) _upHold = System.Math.Min(MaxUpHoldSeconds, _upHold * 2);   // that step up did not hold
                Level++; Changes++; _sinceChange = 0; _sinceDown = 0; _lastWasUp = false; changed = true;
                _upSum = 0; _upN = 0; _upTime = 0;
            }
        }
        if (!changed && Level > 0 && _upTime >= _upHold)
        {
            double mean = _upSum / _upN;
            _upSum = 0; _upN = 0; _upTime = 0;
            if (mean < UpMs) { Level--; Changes++; _sinceChange = 0; _lastWasUp = true; changed = true; }
        }
        else if (Level == 0) { _upSum = 0; _upN = 0; _upTime = 0; }
        return changed;
    }

    /// <summary>Back to no trim and a fresh warm-up (the rig was rebuilt).</summary>
    public void Reset() { Level = 0; Changes = 0; _warm = 0; _downSum = _downTime = _upSum = _upTime = 0; _downN = _upN = 0; _sinceChange = _sinceDown = 1e9; _upHold = UpHoldSeconds; _lastWasUp = false; }
}
