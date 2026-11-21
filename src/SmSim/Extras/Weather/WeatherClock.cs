namespace SmSim.Extras.Weather;

/// <summary>Where a preset stands: the phase, the seconds into it, the seconds since the preset began, and the intensity (0 to 1) the presentation scales by.</summary>
public readonly record struct WeatherState(WeatherId Id, WeatherPhase Phase, double PhaseSeconds, double Elapsed, float Intensity);

/// <summary>
/// The weather cycle as a pure function of the seconds since a preset began: Opening once (intensity 0 up to the preset's opening level), then Buildup (opening level to 1), Peak (1) and Recovery (1 back to
/// the opening level) repeating. The server and every client run it, so the phase byte the server sends and a client's own clock agree. <see cref="WeatherId.None"/> has phases but intensity 0 (no added weather).
/// </summary>
public static class WeatherClock
{
    static readonly WeatherPhase[] Cycle = [WeatherPhase.Buildup, WeatherPhase.Peak, WeatherPhase.Recovery];

    public static WeatherState At(WeatherId id, double elapsed)
    {
        var def = WeatherCatalog.Of(id);
        if (!double.IsFinite(elapsed) || elapsed < 0) elapsed = 0;
        double t = elapsed;
        if (t < def.Duration(WeatherPhase.Opening)) return Make(def, WeatherPhase.Opening, t, elapsed);
        t -= def.Duration(WeatherPhase.Opening);
        double length = 0;
        foreach (var p in Cycle) length += def.Duration(p);
        t %= length;
        foreach (var p in Cycle)
        {
            if (t < def.Duration(p)) return Make(def, p, t, elapsed);
            t -= def.Duration(p);
        }
        return Make(def, WeatherPhase.Recovery, def.Duration(WeatherPhase.Recovery), elapsed);
    }

    static WeatherState Make(WeatherDef def, WeatherPhase phase, double into, double elapsed)
    {
        double f = Math.Clamp(into / Math.Max(def.Duration(phase), 1e-6), 0, 1);
        float o = def.OpeningLevel;
        float level = phase switch
        {
            WeatherPhase.Opening => (float)(o * f),
            WeatherPhase.Buildup => (float)(o + (1f - o) * f),
            WeatherPhase.Peak => 1f,
            _ => (float)(1f - (1f - o) * f),
        };
        if (def.Id == WeatherId.None) level = 0f;
        return new WeatherState(def.Id, phase, into, elapsed, level);
    }
}
