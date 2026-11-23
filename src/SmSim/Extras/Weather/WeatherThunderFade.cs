namespace SmSim.Extras.Weather;

/// <summary>
/// The thunder's fade on a switch to None or Clear (weather fix 5, the weather finish; presentation only, client only, **ours**, an estimate for a playtest; pure, no engine types). When the effective weather becomes None (the server's state,
/// the F6 None, or Follow Match resolving to None) the weather's own thunder and warning voices (the rig's procedural rumbles and the installed one-shots the weather code posted) fade to <see cref="FloorDb"/> over
/// <see cref="Seconds"/> of real frame time, then stop. Since the weather finish a switch to Clear fades them too (Clear is the map's authored sound); any other switch (a Thunderstorm to Rain, say) lets the rolls finish naturally. Sounds not posted by the weather code are never part of it.
/// </summary>
public sealed class WeatherThunderFade
{
    public const double Seconds = 0.5;
    public const float FloorDb = -60f;

    double _t;
    public bool Active { get; private set; }
    /// <summary>True on the step that reached the end: the voices are to be stopped.</summary>
    public bool Done { get; private set; }

    /// <summary>True when a change of the effective weather from <paramref name="previous"/> to <paramref name="next"/> fades the thunder: a change to None, or (the weather finish, 2026-11-23: Clear has no weather audio) to Clear.</summary>
    public static bool Fades(WeatherId previous, WeatherId next) => Silent(next) && !Silent(previous);

    /// <summary>The presets with no weather audio of their own: None and Clear (the map's authored sound).</summary>
    public static bool Silent(WeatherId id) => id is WeatherId.None or WeatherId.Clear;

    public void Start() { Active = true; Done = false; _t = 0; }
    public void Cancel() { Active = false; Done = false; _t = 0; }

    /// <summary>The gain (dB over the voices' own level) <paramref name="seconds"/> into the fade.</summary>
    public static float Db(double seconds) => FloorDb * (float)System.Math.Clamp(seconds / Seconds, 0.0, 1.0);

    public float Step(double dt)
    {
        Done = false;
        if (!Active) return 0f;
        if (dt > 0 && !double.IsInfinity(dt)) _t += dt;
        if (_t >= Seconds) { Active = false; Done = true; _t = 0; return FloorDb; }
        return Db(_t);
    }
}
