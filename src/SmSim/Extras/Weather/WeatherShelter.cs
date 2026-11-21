namespace SmSim.Extras.Weather;

/// <summary>
/// Whether the listener stands under cover (weather W4; presentation only, client only, **ours**; pure, no engine types). The client casts a few upward rays at a low rate and hands
/// <see cref="Sample"/> the covered share (0 to 1) and the nearest ceiling's height; <see cref="Step"/> runs every frame. A Schmitt trigger (enter at <see cref="EnterCover"/>, leave at <see cref="LeaveCover"/>) plus a
/// minimum hold between flips keeps a doorway from flickering, and <see cref="Level"/> (0 open air, 1 fully sheltered) slides toward the state over <see cref="FadeInSeconds"/> and <see cref="FadeOutSeconds"/>,
/// so every look and sound that follows it crossfades. Nothing here allocates.
/// </summary>
public sealed class WeatherShelter
{
    /// <summary>The covered share that turns shelter on and the lower one that turns it off (rays that hit a roof, of the rays cast).</summary>
    public const float EnterCover = 0.6f, LeaveCover = 0.3f;
    /// <summary>Seconds for <see cref="Level"/> to rise from 0 to 1 and to fall from 1 to 0 (the owner: about 0.5 to 1 s).</summary>
    public const double FadeInSeconds = 0.7, FadeOutSeconds = 0.9;
    /// <summary>The shortest time between two flips of <see cref="Covered"/>, in seconds.</summary>
    public const double MinHoldSeconds = 0.4;
    /// <summary>A ceiling this close (m) makes the cover a tunnel or corridor; it stops being one above <see cref="TunnelLeaveM"/>.</summary>
    public const float TunnelEnterM = 5f, TunnelLeaveM = 8f;
    /// <summary>The rays reach this far up (m); a roof higher than this is not cover.</summary>
    public const float RayReachM = 40f;

    double _sinceFlip = 1e9;

    /// <summary>The smoothed state wants shelter.</summary>
    public bool Covered { get; private set; }
    /// <summary>The cover is low (a tunnel or corridor), so the tunnel wind is the better bed.</summary>
    public bool Tunnel { get; private set; }
    /// <summary>0 in the open, 1 fully sheltered, moving linearly between.</summary>
    public float Level { get; private set; }

    /// <summary>One sampling tick: the share of rays that found a roof (0 to 1) and the nearest ceiling's distance above the player (m, <see cref="RayReachM"/> or more for none).</summary>
    public void Sample(float cover, float ceilingM)
    {
        if (!float.IsFinite(cover)) cover = 0f;
        if (!float.IsFinite(ceilingM)) ceilingM = RayReachM;
        bool want = Covered ? cover > LeaveCover : cover >= EnterCover;
        if (want != Covered && _sinceFlip >= MinHoldSeconds) { Covered = want; _sinceFlip = 0; }
        if (!Covered) Tunnel = false;
        else Tunnel = Tunnel ? ceilingM < TunnelLeaveM : ceilingM <= TunnelEnterM;
    }

    /// <summary>Once a frame.</summary>
    public void Step(double dt)
    {
        if (!(dt > 0) || double.IsInfinity(dt)) return;
        _sinceFlip += dt;
        float target = Covered ? 1f : 0f;
        float stepSize = (float)(dt / (Covered ? FadeInSeconds : FadeOutSeconds));
        Level = Level < target ? System.Math.Min(target, Level + stepSize) : System.Math.Max(target, Level - stepSize);
    }

    /// <summary>Forget everything (the rig is gone).</summary>
    public void Reset() { Covered = false; Tunnel = false; Level = 0f; _sinceFlip = 1e9; }
}

/// <summary>
/// What the shelter level does to the weather's looks and sounds (weather W4; **ours**, estimates for a playtest): a muffled indoor mix (the weather bus's low-pass slides from open to <see cref="MuffledHz"/>, the rain
/// hiss and wind are quieter, thunder stays audible but duller and a little quieter) and which particle layers fade. All pure functions of the shelter level; every level only ever lowers a loudness, so the W2 caps
/// (rain -22 dB, wind -20 dB, thunder -14 dB) still hold.
/// </summary>
public static class WeatherShelterMix
{
    /// <summary>The low-pass cutoff (Hz) of the weather bus in the open (no audible filtering) and fully sheltered.</summary>
    public const float OpenHz = 20000f, MuffledHz = 1100f;
    /// <summary>The extra cut (dB, at full shelter) of the rain hiss, the procedural wind bed and thunder; the install's closed-wind sound's own gain.</summary>
    public const float RainCutDb = -7f, WindCutDb = -4f, ThunderCutDb = -3f, InstallClosedWindDb = -10f;
    /// <summary>Under cover the far rain layer keeps this share of its alpha and rains only outside a hole of <see cref="FarHoleRadiusM"/> (m) round the player.</summary>
    public const float FarShareAtFull = 0.55f, FarHoleRadiusM = 14f;
    /// <summary>The shelter level above which the far layer's emission has its hole, and above which the near layers have stopped emitting.</summary>
    public const float HoleAbove = 0.5f, NearOffAbove = 0.98f;

    static float Clamp01(float v) => float.IsFinite(v) ? System.Math.Clamp(v, 0f, 1f) : 0f;

    /// <summary>The low-pass cutoff of the weather bus: logarithmic between the open and muffled values.</summary>
    public static float CutoffHz(float level) => OpenHz * System.MathF.Pow(MuffledHz / OpenHz, Clamp01(level));

    /// <summary>The install's own far thunder and warning rumble and the ash grit loop (posted through the sound system, not on the weather bus) are cut by this many dB at full shelter, scaled by the level when posted; the grit is stopped once the level passes <see cref="GritStopAbove"/>.</summary>
    public const float InstallThunderCutDb = -7f, InstallGritCutDb = -9f, GritStopAbove = 0.5f;
    public static float InstallThunderDb(float level) => InstallThunderCutDb * Clamp01(level);
    public static float InstallGritDb(float level) => InstallGritCutDb * Clamp01(level);

    /// <summary>
    /// The Warfront battle's own open-air wind bed (<c>play_wind_open</c>, a started loop with no gain control of its own) under a roof (weather W5, owner 2026-11-21): the sound system adds this live cut (dB, ours, an estimate) and
    /// low-pass (Wwise 0..100 units: 59.3 is the 1.1 kHz of <see cref="MuffledHz"/> on the sound system's curve, 20 kHz x (150/20000)^(u/100)) at full shelter, scaled by the level. The same in the authored look (None) and in every preset.
    /// </summary>
    public const float BedWindCutDb = -6f, BedWindLpfAtFull = 59.3f;
    public static float BedWindDb(float level) => BedWindCutDb * Clamp01(level);
    public static float BedWindLpf(float level) => BedWindLpfAtFull * Clamp01(level);

    public static float RainDb(float level) => RainCutDb * Clamp01(level);
    public static float WindDb(float level) => WindCutDb * Clamp01(level);
    public static float ThunderDb(float level) => ThunderCutDb * Clamp01(level);

    /// <summary>The alpha factor of the near layers (near rain, splashes, mist, ash, embers): none left at full shelter.</summary>
    public static float NearAlpha(float level) => 1f - Clamp01(level);
    /// <summary>The alpha factor of the far rain layer.</summary>
    public static float FarAlpha(float level) => 1f - (1f - FarShareAtFull) * Clamp01(level);
}
