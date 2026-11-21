using System;
using SmClient.Customizer.Model;
using SmSim.Extras.Weather;

namespace SmClient.Extras.Weather;

/// <summary>
/// The look of a weather preset as a <see cref="LightingGrade"/> (client only, cosmetic, presentation only; **ours**, estimates by eye for a playtest to refine; spec/behavior.md "Weather look"): the same
/// multipliers and fog tint the OPTIONS lighting presets use, so the weather is applied by <c>LightingGradeView</c> on top of whatever the map's lighting wrote (the Ashfall storm included), in every renderer,
/// and "None" is exactly the identity: nothing is touched and every base is written back. Limits kept on purpose so no weather hides players, objectives or the beacon (the lighting presets' own rule): the shade is never
/// darker than the map's (ambient at least 1), exposure stays within 0.94 to 1.13, fog density at most 3.5 times the map's (a street 40 m long stays about 40 % clear at the peak of Low Fog).
/// </summary>
public static class WeatherGrade
{
    /// <summary>Fog density at most this many times the map's (the readability limit above).</summary>
    public const float MaxFogFactor = 3.5f;
    /// <summary>Exposure bounds.</summary>
    public const float MinExposure = 0.94f, MaxExposure = 1.13f;

    /// <summary>The grade of <paramref name="id"/> at <paramref name="intensity"/> (0 to 1). None and an unknown id give the identity.</summary>
    public static LightingGrade For(WeatherId id, float intensity)
    {
        float i = Math.Clamp(float.IsFinite(intensity) ? intensity : 0f, 0f, 1f);
        if (i <= 0f) return LightingGrade.Identity;
        return id switch
        {
            // sunlight: a higher, brighter, warm-white sun, more colour, a thin pale haze
            WeatherId.Clear => new(Exposure: 1f + 0.08f * i, Contrast: 1f + 0.04f * i, Saturation: 1f + 0.30f * i, SunEnergy: 1f + 0.55f * i, SunR: 1f + 0.04f * i, SunG: 1f + 0.02f * i, SunB: 1f - 0.06f * i,
                Ambient: 1f + 0.20f * i, FogDensity: 1f - 0.50f * i, FogR: 0.62f, FogG: 0.66f, FogB: 0.72f, FogTint: 0.55f * i, Glow: 1f - 0.25f * i),
            // street-level haze: a dim flat sun, the shade lifted by scatter, grey-blue fog 3.4 times the map's at the peak
            WeatherId.LowFog => new(Exposure: 1f + 0.02f * i, Contrast: 1f - 0.08f * i, Saturation: 1f - 0.25f * i, SunEnergy: 1f - 0.40f * i, SunR: 1f - 0.06f * i, SunG: 1f - 0.02f * i,
                Ambient: 1f + 0.18f * i, FogDensity: 1f + 2.4f * i, FogR: 0.60f, FogG: 0.63f, FogB: 0.67f, FogTint: 0.85f * i, Glow: 1f + 0.10f * i),
            // overcast rain: a weak cool sun, desaturated, a cold grey haze, a touch more bloom on wet highlights
            WeatherId.Rain => new(Exposure: 1f - 0.05f * i, Contrast: 1f + 0.03f * i, Saturation: 1f - 0.25f * i, SunEnergy: 1f - 0.55f * i, SunR: 1f - 0.15f * i, SunG: 1f - 0.08f * i,
                Ambient: 1f, FogDensity: 1f + 0.9f * i, FogR: 0.38f, FogG: 0.42f, FogB: 0.48f, FogTint: 0.70f * i, Glow: 1f + 0.15f * i),
            // thunderstorm (W2): a darkening sky, a dim cold sun, desaturated, dark slate haze 2.4 times the map's at the peak; the shade is never darker and the exposure stays at its floor (never a flash: the glow is a sky-only layer in the rig)
            WeatherId.Thunderstorm => new(Exposure: 1f - 0.06f * i, Contrast: 1f + 0.05f * i, Saturation: 1f - 0.30f * i, SunEnergy: 1f - 0.58f * i, SunR: 1f - 0.18f * i, SunG: 1f - 0.10f * i,
                Ambient: 1f, FogDensity: 1f + 1.4f * i, FogR: 0.30f, FogG: 0.33f, FogB: 0.40f, FogTint: 0.75f * i, Glow: 1f + 0.12f * i),
            // ash squall (W2): on top of the Ashfall storm, a dimmer ochre sun and a warm brown haze up to 2.8 times the map's at the peak, colour drained
            WeatherId.AshSquall => new(Exposure: 1f - 0.03f * i, Contrast: 1f, Saturation: 1f - 0.22f * i, SunEnergy: 1f - 0.40f * i, SunR: 1f - 0.04f * i, SunG: 1f - 0.14f * i, SunB: 1f - 0.30f * i,
                Ambient: 1.05f, FogDensity: 1f + 1.8f * i, FogR: 0.52f, FogG: 0.40f, FogB: 0.28f, FogTint: 0.80f * i, Glow: 1f + 0.05f * i),
            // ember wind (W2, preview): a faintly warmer, slightly dimmer sun and a thin warm haze; the embers are the show
            WeatherId.EmberWind => new(Exposure: 1f, Contrast: 1f, Saturation: 1f + 0.04f * i, SunEnergy: 1f - 0.12f * i, SunR: 1f + 0.05f * i, SunB: 1f - 0.10f * i,
                Ambient: 1f, FogDensity: 1f + 0.3f * i, FogR: 0.55f, FogG: 0.36f, FogB: 0.24f, FogTint: 0.35f * i, Glow: 1f + 0.10f * i),
            _ => LightingGrade.Identity,
        };
    }

    /// <summary>The player's lighting preset and the weather together: factors multiply; the fog tints blend by their amounts; the readability limits hold on the product.</summary>
    public static LightingGrade Combine(LightingGrade user, LightingGrade weather)
    {
        if (weather.IsIdentity) return user;
        float ut = user.FogTint, wt = weather.FogTint, t = ut + wt;
        float r = t > 0f ? (user.FogR * ut + weather.FogR * wt) / t : 0f, g = t > 0f ? (user.FogG * ut + weather.FogG * wt) / t : 0f, b = t > 0f ? (user.FogB * ut + weather.FogB * wt) / t : 0f;
        return new LightingGrade(
            Exposure: Math.Clamp(user.Exposure * weather.Exposure, MinExposure, MaxExposure), Contrast: user.Contrast * weather.Contrast, Saturation: user.Saturation * weather.Saturation, Brightness: user.Brightness * weather.Brightness,
            SunEnergy: user.SunEnergy * weather.SunEnergy, SunR: user.SunR * weather.SunR, SunG: user.SunG * weather.SunG, SunB: user.SunB * weather.SunB,
            Ambient: Math.Max(1f, user.Ambient * weather.Ambient), FogDensity: Math.Min(user.FogDensity * weather.FogDensity, MaxFogFactor), FogR: r, FogG: g, FogB: b, FogTint: Math.Min(1f, Math.Max(ut, wt)),
            Glow: user.Glow * weather.Glow, Ssao: user.Ssao * weather.Ssao);
    }
}
