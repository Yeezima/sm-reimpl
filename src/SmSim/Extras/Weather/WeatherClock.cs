namespace SmSim.Extras.Weather;

/// <summary>
/// Where a preset stands: the phase, the seconds into it, the seconds since the preset began, and the intensity (0 to 1) the presentation scales by. <paramref name="Span"/> is the match length (seconds) the cycle was fitted
/// to (Classic PvP, W3; 0: the catalog's own lengths, Warfront and the developer-tools override). With a span, <paramref name="PhaseSeconds"/> is in catalog seconds (the fitted clock's virtual time) and <paramref name="Elapsed"/> stays real.
/// </summary>
public readonly record struct WeatherState(WeatherId Id, WeatherPhase Phase, double PhaseSeconds, double Elapsed, float Intensity, int Span = 0);

/// <summary>
/// The weather cycle as a pure function of the seconds since a preset began: Opening once, then Buildup, Peak and Recovery repeating. Since the weather finish the intensity starts at 1 and stays within 0.5 to 1
/// (<see cref="Make"/>: the opening eases from 1 to the preset's lull, the buildup back to 1, the peak holds 1, the recovery eases to the lull). The server and every client run it, so the phase byte the server sends and a client's own clock agree. <see cref="WeatherId.None"/> has phases but intensity 0 (no added weather).
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

    /// <summary>
    /// The cycle fitted to a match of <paramref name="span"/> seconds (Classic PvP, W3): one pass of Opening, Buildup, Peak and Recovery at the catalog's proportions lasts <paramref name="span"/> seconds, so the
    /// weather builds up, peaks about the middle of the match and eases before its end (spec/behavior.md "Weather (Classic PvP, W3)"). A span of 0 or less is <see cref="At(WeatherId, double)"/>. Past the span the
    /// catalog's Buildup, Peak and Recovery go on repeating at the fitted pace (a match with no time limit), never back to the opening.
    /// </summary>
    public static WeatherState At(WeatherId id, double elapsed, int span)
    {
        if (span <= 0) return At(id, elapsed);
        var def = WeatherCatalog.Of(id);
        double total = def.Duration(WeatherPhase.Opening) + def.Duration(WeatherPhase.Buildup) + def.Duration(WeatherPhase.Peak) + def.Duration(WeatherPhase.Recovery);
        if (!double.IsFinite(elapsed) || elapsed < 0) elapsed = 0;
        var s = At(id, elapsed * total / span);
        return s with { Elapsed = elapsed, Span = span };
    }

    /// <summary>
    /// Weather finish (owner brief 2026-11-23-series-36-weather-finish, item 3): the active lifecycle. An active preset starts at full strength (<see cref="ActiveFull"/>) the moment it begins and afterwards moves smoothly
    /// between <see cref="ActiveFloor"/> and full: the opening eases from full down to the preset's lull, the buildup back up, the peak holds full, the recovery eases down to the lull again. Every join is a smoothstep, so the level
    /// never steps and never reaches zero; there is no expiry (the cycle repeats until the preset is cleared or replaced, or the match ends). The lull is <c>ActiveFloor + (1 - ActiveFloor) x OpeningLevel</c>, so the catalog's
    /// levels keep their order (Clear's lull is the highest, the Ash Squall's the lowest). None stays at 0 (no added weather).
    /// </summary>
    public const float ActiveFloor = 0.5f, ActiveFull = 1f;

    /// <summary>The level an active preset eases down to between its peaks (0.5 to 1).</summary>
    public static float Lull(WeatherDef def) => ActiveFloor + (ActiveFull - ActiveFloor) * Math.Clamp(float.IsFinite(def.OpeningLevel) ? def.OpeningLevel : 0f, 0f, 1f);

    static WeatherState Make(WeatherDef def, WeatherPhase phase, double into, double elapsed)
    {
        float f = Smooth((float)Math.Clamp(into / Math.Max(def.Duration(phase), 1e-6), 0, 1));
        float low = Lull(def);
        float level = phase switch
        {
            WeatherPhase.Opening => ActiveFull - (ActiveFull - low) * f,
            WeatherPhase.Buildup => low + (ActiveFull - low) * f,
            WeatherPhase.Peak => ActiveFull,
            _ => ActiveFull - (ActiveFull - low) * f,
        };
        if (def.Id == WeatherId.None) level = 0f;
        return new WeatherState(def.Id, phase, into, elapsed, level);
    }

    static float Smooth(float x) => x * x * (3f - 2f * x);
}
