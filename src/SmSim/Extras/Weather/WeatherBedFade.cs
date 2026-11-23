namespace SmSim.Extras.Weather;

/// <summary>
/// The weather beds' stop (weather finish repair 2; presentation only, client only, **ours**; pure, no engine types): when a preset no longer wants its rain hiss or its wind bed (a switch to Clear, None or a preset without that bed),
/// the loop fades linearly in dB from the level it had to <see cref="FloorDb"/> over <see cref="Seconds"/> of real frame time, then stops; the same length as the thunder's fade (<see cref="WeatherThunderFade"/>), so a switch to
/// Clear or None takes every weather sound down together in half a second instead of cutting the beds at once. A bed wanted again during the fade cancels it and plays at its level.
/// </summary>
public sealed class WeatherBedFade
{
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
