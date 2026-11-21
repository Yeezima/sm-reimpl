namespace SmSim.Extras.Weather;

/// <summary>What the sheltered wind loop should do this frame (weather fix 4).</summary>
public enum ClosedWindAction { None, Post, Keep, Stop }

/// <summary>
/// The choice of the install's sheltered wind loop (weather fix 4; presentation only, client only, **ours**; pure, no engine types). A tunnel wind is wanted only when the cover is deep
/// (<see cref="WeatherShelterMix"/> level at least 0.5), the player is covered, the wind is above 0.05 and the shelter is in tunnel mode (a low ceiling); a tall covered hall is covered but not a tunnel,
/// so a loop playing from an earlier tunnel must stop there, not run on until open air. Only one loop plays at a time. A stop fades over <see cref="FadeSeconds"/> to <see cref="FadeFloorDb"/>.
/// </summary>
public static class WeatherClosedWindRule
{
    public const double FadeSeconds = 1.0;
    public const float FadeFloorDb = -60f;
    public const float MinLevel = 0.5f, MinWind = 0.05f;

    /// <summary>Post: start <paramref name="name"/>; Keep: the same loop is wanted (the caller reposts a one-shot); Stop: a loop plays that is no longer wanted; None: nothing.</summary>
    public static ClosedWindAction Decide(bool covered, float level, bool tunnel, float wind, string? playing, System.Collections.Generic.IReadOnlyList<string> tunnelNames, System.Func<string, bool> available, out string? name)
    {
        name = null;
        if (covered && level >= MinLevel && wind > MinWind && tunnel)
            foreach (var c in tunnelNames) if (available(c)) { name = c; break; }
        if (name is null) return playing is null ? ClosedWindAction.None : ClosedWindAction.Stop;
        if (playing == name) return ClosedWindAction.Keep;
        return ClosedWindAction.Post;   // a different loop playing is ended by the caller before the post
    }

    /// <summary>The gain (dB over the loop's own level) <paramref name="seconds"/> into a stop's fade; the stop is final at <see cref="FadeSeconds"/>.</summary>
    public static float FadeDb(double seconds) => FadeFloorDb * (float)System.Math.Clamp(seconds / FadeSeconds, 0.0, 1.0);
}
