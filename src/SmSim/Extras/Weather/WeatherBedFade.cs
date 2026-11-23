namespace SmSim.Extras.Weather;

/// <summary>
/// The weather beds' stop (weather finish repair 2; presentation only, client only, **ours**; pure, no engine types): when a preset no longer wants its rain hiss or its wind bed (a switch to Clear, None or a preset without that bed),
/// the loop fades linearly in dB from the level it had to <see cref="FloorDb"/> over <see cref="Seconds"/> of real frame time, then stops; the same length as the thunder's fade (<see cref="WeatherThunderFade"/>), so a switch to
/// Clear or None takes every weather sound down together in half a second instead of cutting the beds at once. A bed wanted again during the fade cancels it and plays at its level.
/// </summary>
public sealed class WeatherBedFade
{
    /// <summary>The longest frame step a fade takes (s): a stall never jumps a fade to its end.</summary>
    public const double MaxStep = 0.25;

    public const double Seconds = 0.5;
    public const float FloorDb = -60f;

    double _t;
    float _from;
    public bool Active { get; private set; }

    /// <summary>Starts the fade from <paramref name="fromDb"/> (ignored while one runs).</summary>
    public void Start(float fromDb)
    {
        if (Active) return;
        Active = true; _t = 0; _from = float.IsFinite(fromDb) ? fromDb : FloorDb;
    }

    public void Cancel() { Active = false; _t = 0; }

    /// <summary>One step of <paramref name="dt"/> real seconds: the level now (dB) and whether the fade has ended (the loop is then to be stopped).</summary>
    public float Step(double dt, out bool done)
    {
        done = false;
        if (!Active) return _from;
        if (dt > 0 && double.IsFinite(dt)) _t += dt;
        if (_t >= Seconds) { Active = false; done = true; return FloorDb; }
        return _from + (FloorDb - _from) * (float)(_t / Seconds);
    }
}

/// <summary>
/// Repair 3 (review): the real seconds of each frame, sampled once every frame by its owner (the rig samples it at the top of every update, fade or not), so the first step of a fade is one frame long. Sampled only while a fade ran,
/// a second fade in one rig's life began with the whole time since the first (capped at <see cref="WeatherBedFade.MaxStep"/>): a 19 dB drop in one frame.
/// </summary>
public sealed class WeatherFrameDt
{
    long _lastMs = -1;

    /// <summary>The real seconds since the last sample (0 at the first), capped at <see cref="WeatherBedFade.MaxStep"/>.</summary>
    public double Step(long nowMs)
    {
        double dt = _lastMs < 0 || nowMs < _lastMs ? 0 : System.Math.Min(WeatherBedFade.MaxStep, (nowMs - _lastMs) / 1000.0);
        _lastMs = nowMs;
        return dt;
    }
}
