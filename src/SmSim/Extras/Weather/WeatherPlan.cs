namespace SmSim.Extras.Weather;

/// <summary>One thunderstorm strike: when its flash is (seconds since the preset began), a seed for the rumble, whether it is a near one (a short, loud crack) and how long after the flash the sound arrives.</summary>
public readonly record struct WeatherStrike(double Time, uint Seed, bool Near, double SoundDelay)
{
    public double SoundTime => Time + SoundDelay;
}

/// <summary>
/// The W2 presets' timelines as pure functions of the seconds since the preset began (weather W2; presentation only, **ours**, every number an estimate for a playtest; spec/behavior.md "Weather (W2)"). The server
/// and every client derive the same strikes, gusts and wind direction from the same clock, so a reconnecting client sees the storm where it is. Nothing here reaches damage, movement, weapons or the AI.
/// <list type="bullet">
/// <item><b>Thunderstorm</b>: no strike while the intensity is below <see cref="StrikeFromIntensity"/>); then at most one strike per <see cref="StrikeSlot"/>-second slot, the strike
/// <see cref="StrikeOffsetMin"/> to <see cref="StrikeOffsetMax"/> seconds into its slot, so two strikes are always at least <see cref="MinStrikeGap"/> seconds apart (rate at most 0.15 a second). The chance of a strike in a slot rises with the intensity to
/// <see cref="StrikeChanceAtPeak"/>. A strike is a cloud-glow pulse (<see cref="CloudGlow"/>, a smooth rise of <see cref="GlowRise"/> s and fall of <see cref="GlowFall"/> s, small and in the sky only) and a thunder sound
/// <see cref="SoundDelay"/> seconds later. **Reduced flash is the only mode**: the pulse never touches the screen's exposure or the map's lighting.</item>
/// <item><b>Ash Squall</b>: the ash follows the active level (full at once, 0.5 to 1 after); the last seconds of the buildup carry a short "intensifying" cue.</item>
/// <item><b>Ember Wind</b>: sparse embers (<see cref="EmberDensity"/>) on a steady wind.</item>
/// </list>
/// </summary>
public static class WeatherPlan
{
    // ---- thunderstorm strikes and the cloud glow ----

    /// <summary>The slot a strike lives in (s), the strike's offset range inside it (s): two strikes are at least <see cref="MinStrikeGap"/> apart.</summary>
    public const double StrikeSlot = 9.0, StrikeOffsetMin = 0.5, StrikeOffsetMax = 2.5;
    public const double MinStrikeGap = StrikeSlot - (StrikeOffsetMax - StrikeOffsetMin);
    /// <summary>No strike below this intensity (the buildup's first half is darkening and wind only); the chance of a strike in a slot at the peak.</summary>
    public const float StrikeFromIntensity = 0.55f, StrikeChanceAtPeak = 0.6f;
    /// <summary>The cloud glow pulse: the rise and the fall (s). A pulse at its fullest is <see cref="GlowMaxAlpha"/> of a sky-only additive layer (the client).</summary>
    public const double GlowRise = 0.35, GlowFall = 1.2;
    /// <summary>The strongest the sky glow layer gets (additive alpha, 0 to 1): a pale blue-white film on the sky behind the world, never on the screen.</summary>
    public const float GlowMaxAlpha = 0.06f;
    /// <summary>Thunder arrives after the flash: a far one after <see cref="FarDelayMin"/> to <see cref="FarDelayMax"/> s (the owner's "distant thunder"), a near one (about one in <see cref="NearOneIn"/>) after <see cref="NearDelayMin"/> to <see cref="NearDelayMax"/> s.</summary>
    public const double FarDelayMin = 1.5, FarDelayMax = 6.5, NearDelayMin = 0.6, NearDelayMax = 1.2;
    public const int NearOneIn = 5;

    /// <summary>The strike of a slot, or null when the slot has none.</summary>
    public static WeatherStrike? StrikeOfSlot(long slot, int span = 0)
    {
        if (slot < 0) return null;
        ulong h = Mix((ulong)slot * 0x9E3779B97F4A7C15UL + 0x7B0DE5ACE5UL);
        double offset = StrikeOffsetMin + Unit(h, 0) * (StrikeOffsetMax - StrikeOffsetMin);
        double time = slot * StrikeSlot + offset;
        float intensity = WeatherClock.At(WeatherId.Thunderstorm, time, span).Intensity;
        if (intensity < StrikeFromIntensity) return null;
        float chance = StrikeChanceAtPeak * Math.Clamp((intensity - StrikeFromIntensity) / (1f - StrikeFromIntensity), 0f, 1f);
        if (Unit(h, 1) >= chance) return null;
        bool near = h % NearOneIn == 0;
        double delay = near ? NearDelayMin + Unit(h, 2) * (NearDelayMax - NearDelayMin) : FarDelayMin + Unit(h, 2) * (FarDelayMax - FarDelayMin);
        return new WeatherStrike(time, (uint)(h >> 32), near, delay);
    }

    /// <summary>The strikes whose flash lies in (<paramref name="from"/>, <paramref name="to"/>] seconds into a Thunderstorm.</summary>
    public static List<WeatherStrike> StrikesIn(double from, double to, int span = 0)
    {
        var list = new List<WeatherStrike>();
        if (!double.IsFinite(from) || !double.IsFinite(to) || to <= from) return list;
        for (long slot = (long)Math.Floor(Math.Max(0, from) / StrikeSlot); slot * StrikeSlot <= to; slot++)
            if (StrikeOfSlot(slot, span) is { } s && s.Time > from && s.Time <= to) list.Add(s);
        return list;
    }

    /// <summary>The strikes whose sound arrives in (<paramref name="from"/>, <paramref name="to"/>]: a strike's sound is at most 6.5 s after its flash, so the slots before are looked at too.</summary>
    public static List<WeatherStrike> ThunderIn(double from, double to, int span = 0)
    {
        var list = new List<WeatherStrike>();
        if (!double.IsFinite(from) || !double.IsFinite(to) || to <= from) return list;
        foreach (var s in StrikesIn(from - FarDelayMax - 1, to, span)) if (s.SoundTime > from && s.SoundTime <= to) list.Add(s);
        return list;
    }

    /// <summary>The cloud glow (0 to 1) at <paramref name="elapsed"/> seconds into a Thunderstorm: the pulses of the strikes in the last seconds, a smoothstep rise then fall, the strongest if two overlap. Never above 1.</summary>
    public static float CloudGlow(double elapsed, int span = 0)
    {
        if (!double.IsFinite(elapsed) || elapsed < 0) return 0f;
        float best = 0f;
        foreach (var s in StrikesIn(elapsed - GlowRise - GlowFall, elapsed + GlowRise, span))
        {
            double t = elapsed - s.Time + GlowRise;   // the pulse starts one rise before the flash's peak
            if (t < 0 || t > GlowRise + GlowFall) continue;
            float v = t < GlowRise ? Smooth(t / GlowRise) : Smooth(1.0 - (t - GlowRise) / GlowFall);
            if (v > best) best = v;
        }
        return best;
    }

    // ---- the full-flash setting (client option; the default is reduced) ----

    /// <summary>
    /// Full flash (the owner's setting, 2026-11-21; a client-only choice, never on the wire): the same strikes (so the same spacing: at least <see cref="MinStrikeGap"/> s apart, never three in ten seconds), each ONE brighter
    /// pulse, a quick rise of <see cref="FlashRise"/> s and a fast fall of <see cref="FlashFall"/> s (no second pulse: no strobing). It lifts the exposure by at most <see cref="FlashExposure"/>, the sun by <see cref="FlashSun"/>
    /// and the shade by <see cref="FlashAmbient"/> at its fullest, and the sky glow to <see cref="FullGlowMaxAlpha"/>. One frame at 60 Hz moves it by at most 0.32 of its peak.
    /// </summary>
    public const double FlashRise = 0.08, FlashFall = 0.45;
    public const float FlashExposure = 0.09f, FlashSun = 1.0f, FlashAmbient = 0.20f, FullGlowMaxAlpha = 0.30f;

    /// <summary>The full-flash pulse (0 to 1) at <paramref name="elapsed"/> seconds into a Thunderstorm: peaks at the strike's own time.</summary>
    public static float FullFlash(double elapsed, int span = 0)
    {
        if (!double.IsFinite(elapsed) || elapsed < 0) return 0f;
        float best = 0f;
        foreach (var s in StrikesIn(elapsed - FlashFall, elapsed + FlashRise, span))
        {
            double t = elapsed - s.Time + FlashRise;
            if (t < 0 || t > FlashRise + FlashFall) continue;
            float v = t < FlashRise ? Smooth(t / FlashRise) : Smooth(1.0 - (t - FlashRise) / FlashFall);
            if (v > best) best = v;
        }
        return best;
    }

    // ---- wind ----

    /// <summary>The wind's heading (radians, the direction the air moves toward, a flat angle in the world's x-z plane) per preset: fixed, so every client and every frame agree, the same for the ash, the embers and the rain's slant.</summary>
    public static float WindYaw(WeatherId id) => id switch
    {
        WeatherId.AshSquall => 0.9f,
        WeatherId.EmberWind => 0.9f,
        WeatherId.Thunderstorm => 2.2f,
        _ => 0.4f,
    };

    /// <summary>The wind's strength (0 to 1) at <paramref name="intensity"/> (the phase's) with slow gusts: the preset's own curve, a gust factor of 0.85 to 1.15 with a period of about 11 s. The audio's loudness and the particles' speed follow it.</summary>
    public static float Wind(WeatherId id, float intensity, double elapsed)
    {
        float i = Math.Clamp(float.IsFinite(intensity) ? intensity : 0f, 0f, 1f);
        float baseLevel = id switch
        {
            WeatherId.AshSquall => i,                // the weather finish: the crosswind follows the active level (0.5 to 1; was intensity squared)
            WeatherId.Thunderstorm => 0.15f + 0.85f * i,   // wind rises through the buildup
            WeatherId.EmberWind => 0.25f + 0.25f * i,
            _ => 0f,
        };
        if (baseLevel <= 0f) return 0f;
        if (!double.IsFinite(elapsed)) elapsed = 0;
        float gust = 1f + 0.15f * (float)(Math.Sin(elapsed * 0.57) * 0.6 + Math.Sin(elapsed * 1.31 + 1.7) * 0.4);
        return Math.Clamp(baseLevel * gust, 0f, 1f);
    }

    // ---- what is drawn, by preset ----

    /// <summary>Falling rain's share (0 to 1) of the rain rig's full count: Rain and the Thunderstorm follow their intensity (the weather finish: full at once, 0.5 to 1 after; the storm's rain no longer waits for the buildup).</summary>
    public static float RainShare(WeatherId id, float intensity)
    {
        float i = Math.Clamp(float.IsFinite(intensity) ? intensity : 0f, 0f, 1f);
        return id switch
        {
            WeatherId.Rain => i,
            WeatherId.Thunderstorm => i,
            _ => 0f,
        };
    }

    /// <summary>The Ash Squall's flecks (0 to 1 of the full count): the weather finish makes them follow the active level (full at once, 0.5 to 1 after: no near-empty warning or opening any more).</summary>
    public static float AshShare(WeatherState s)
    {
        if (s.Id != WeatherId.AshSquall) return 0f;
        return Math.Clamp(float.IsFinite(s.Intensity) ? s.Intensity : 0f, 0f, 1f);
    }

    /// <summary>
    /// A loop's gain (dB) that follows a visual share (the weather finish: "weather audio follows the visual intensity"): the amplitude is proportional to the share, so half the rain sounds 6 dB under the full rain, not
    /// near silence; <paramref name="loudestDb"/> at a share of 1, <paramref name="silentDb"/> at 0 and never below it.
    /// </summary>
    public static float FollowDb(float share, float loudestDb, float silentDb)
    {
        float v = Math.Clamp(float.IsFinite(share) ? share : 0f, 0f, 1f);
        if (v <= 0f) return silentDb;
        return Math.Max(silentDb, loudestDb + 20f * MathF.Log10(v));
    }

    /// <summary>Ember Wind's embers (0 to 1 of a deliberately small full count): sparse and steady, a little more with the phase's intensity.</summary>
    public static float EmberDensity(WeatherId id, float intensity)
        => id == WeatherId.EmberWind ? 0.5f + 0.5f * Math.Clamp(float.IsFinite(intensity) ? intensity : 0f, 0f, 1f) : 0f;

    /// <summary>The Ash Squall's intensifying cue is on: the last <see cref="WarningSeconds"/> seconds of its buildup only (it ends when the peak begins; the client shows a short text cue beside the visible and audible ones).</summary>
    public const double WarningSeconds = 30.0;
    public static bool Warning(WeatherState s)
        => s.Id == WeatherId.AshSquall && s.Phase == WeatherPhase.Buildup && s.PhaseSeconds >= WeatherCatalog.Of(WeatherId.AshSquall).Duration(WeatherPhase.Buildup) - WarningSeconds;

    // ---- helpers ----

    static float Smooth(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }
    static float Smooth(double x) => Smooth((float)x);

    /// <summary>A uniform number in [0, 1) from a hash and a lane.</summary>
    static double Unit(ulong h, int lane) => (Mix(h + (ulong)lane * 0xD6E8FEB86659FD93UL) >> 11) * (1.0 / (1UL << 53));

    static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
