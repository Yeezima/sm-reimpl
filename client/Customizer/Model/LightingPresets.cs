using System;
using System.Collections.Generic;

namespace SmClient.Customizer.Model;

/// <summary>The lighting preset of OPTIONS, GRAPHICS (ui/lighting-presets, the owner's request of 2026-11-06: ten looks, named by him). <see cref="FaithfulRemaster"/> is today's look.</summary>
public enum LightingPresetChoice
{
    FaithfulRemaster, NaturalCinema, PaleAshfall, GoldenEpic, BlueHourSteel, Stormbreak, GothicChiaroscuro, ForgeAmber, BleachBypassWarfilm, AshColorBalance,
}

/// <summary>
/// One preset as changes to the look the map's lighting already produced (client only, cosmetic; **ours**: every value picked by eye from the preset's name, the owner gave names only).
/// Factors multiply the value the lighting code set (1 = unchanged); a tint is a colour the value is moved toward by its amount (0 = unchanged) or, for the sun, multiplied by.
/// Nothing here switches a costly feature on: SSAO and volumetric fog are only scaled where the renderer and the lighting preset already run them.
/// </summary>
public sealed record LightingGrade(
    float Exposure = 1f, float Contrast = 1f, float Saturation = 1f, float Brightness = 1f,
    float SunEnergy = 1f, float SunR = 1f, float SunG = 1f, float SunB = 1f,
    float Ambient = 1f, float FogDensity = 1f, float FogR = 0f, float FogG = 0f, float FogB = 0f, float FogTint = 0f,
    float Glow = 1f, float Ssao = 1f)
{
    /// <summary>No change: the look as the lighting code made it.</summary>
    public bool IsIdentity => this == Identity;

    public static readonly LightingGrade Identity = new();
}

/// <summary>The ten presets in the menu's order, their labels and their grades (engine-free; <c>LightingGradeView</c> applies them).</summary>
public static class LightingPresets
{
    /// <summary>The labels in the menu's order, as the owner named them.</summary>
    public static readonly IReadOnlyList<string> Labels =
    [
        "Faithful Remaster", "Natural Cinema", "Pale Ashfall", "Golden Epic", "Blue-Hour Steel",
        "Stormbreak", "Gothic Chiaroscuro", "Forge Amber", "Bleach-Bypass Warfilm", "Ash + Color Balance",
    ];

    public static string Label(LightingPresetChoice choice) => Labels[(int)choice];

    /// <summary>
    /// The grades (**ours**, estimates by eye). Limits kept on purpose so no preset hides players: exposure 0.95 to 1.08, ambient (the light in the shade) at least 0.9,
    /// fog density at most 1.35 times the map's, contrast at most 1.16.
    /// </summary>
    public static LightingGrade For(LightingPresetChoice choice) => choice switch
    {
        // Today's look exactly.
        LightingPresetChoice.FaithfulRemaster => LightingGrade.Identity,
        // Neutral and filmic: a little less warmth and saturation, thinner haze, softer bloom, slightly lifted shade.
        LightingPresetChoice.NaturalCinema => new(Exposure: 1.0f, Contrast: 0.97f, Saturation: 0.92f, SunR: 0.98f, SunG: 1.0f, SunB: 1.04f,
            Ambient: 1.05f, FogDensity: 0.85f, FogR: 0.55f, FogG: 0.55f, FogB: 0.55f, FogTint: 0.25f, Glow: 0.85f),
        // Pale ash in the air: thicker grey-white haze, washed colours, a softer sun and brighter shade.
        LightingPresetChoice.PaleAshfall => new(Exposure: 1.05f, Contrast: 0.93f, Saturation: 0.74f, SunEnergy: 0.88f, SunR: 0.97f, SunG: 0.98f, SunB: 1.02f,
            Ambient: 1.12f, FogDensity: 1.3f, FogR: 0.80f, FogG: 0.79f, FogB: 0.77f, FogTint: 0.6f, Glow: 1.0f),
        // Warm golden-hour light, gold haze, richer colour and a warmer bloom.
        LightingPresetChoice.GoldenEpic => new(Exposure: 1.04f, Contrast: 1.04f, Saturation: 1.08f, SunEnergy: 1.1f, SunR: 1.07f, SunG: 0.98f, SunB: 0.80f,
            Ambient: 1.0f, FogDensity: 1.0f, FogR: 0.86f, FogG: 0.66f, FogB: 0.36f, FogTint: 0.35f, Glow: 1.25f),
        // Dusk: a cool, weaker sun, blue-steel haze, cooler colour; the shade is lifted so it stays readable.
        LightingPresetChoice.BlueHourSteel => new(Exposure: 1.0f, Contrast: 1.06f, Saturation: 0.9f, SunEnergy: 0.82f, SunR: 0.82f, SunG: 0.91f, SunB: 1.1f,
            Ambient: 1.1f, FogDensity: 1.05f, FogR: 0.42f, FogG: 0.50f, FogB: 0.62f, FogTint: 0.5f, Glow: 1.1f),
        // Storm clouds breaking: grey-slate haze, muted colour, more contrast and a brighter sun breaking through.
        LightingPresetChoice.Stormbreak => new(Exposure: 0.97f, Contrast: 1.1f, Saturation: 0.84f, SunEnergy: 1.15f, SunR: 1.0f, SunG: 0.98f, SunB: 0.95f,
            Ambient: 0.95f, FogDensity: 1.25f, FogR: 0.45f, FogG: 0.48f, FogB: 0.52f, FogTint: 0.55f, Glow: 1.15f),
        // Hard light and deep shade (gothic painting): strong sun, less fill, thin dark-warm haze, stronger contact shadows.
        LightingPresetChoice.GothicChiaroscuro => new(Exposure: 1.0f, Contrast: 1.14f, Saturation: 0.9f, SunEnergy: 1.18f, SunR: 1.02f, SunG: 0.98f, SunB: 0.92f,
            Ambient: 0.9f, FogDensity: 0.85f, FogR: 0.30f, FogG: 0.26f, FogB: 0.22f, FogTint: 0.3f, Glow: 1.1f, Ssao: 1.2f),
        // The forge: amber haze, a warm sun, a hotter bloom on the lamps and molten metal.
        LightingPresetChoice.ForgeAmber => new(Exposure: 1.02f, Contrast: 1.05f, Saturation: 1.06f, SunEnergy: 1.0f, SunR: 1.06f, SunG: 0.92f, SunB: 0.76f,
            Ambient: 1.0f, FogDensity: 1.15f, FogR: 0.76f, FogG: 0.46f, FogB: 0.20f, FogTint: 0.45f, Glow: 1.3f),
        // Bleach bypass war film: most of the colour gone, hard contrast, neutral grey haze, little bloom.
        LightingPresetChoice.BleachBypassWarfilm => new(Exposure: 1.03f, Contrast: 1.16f, Saturation: 0.6f, SunEnergy: 1.0f,
            Ambient: 0.95f, FogDensity: 1.0f, FogR: 0.5f, FogG: 0.5f, FogB: 0.5f, FogTint: 0.3f, Glow: 0.8f),
        // Ash haze with a neutral white balance: the smog turns ash grey, the orange cast is taken out of the sun, colour slightly muted.
        LightingPresetChoice.AshColorBalance => new(Exposure: 1.03f, Contrast: 1.03f, Saturation: 0.9f, SunEnergy: 1.0f, SunR: 0.97f, SunG: 1.0f, SunB: 1.06f,
            Ambient: 1.06f, FogDensity: 1.2f, FogR: 0.62f, FogG: 0.61f, FogB: 0.60f, FogTint: 0.5f, Glow: 1.0f),
        _ => LightingGrade.Identity,
    };

    /// <summary>A command-line name (<c>--lighting-preset</c>): the 1-based number, the enum name or the label, any case; null when it names none.</summary>
    public static LightingPresetChoice? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (int.TryParse(text, out int n)) return n >= 1 && n <= Labels.Count ? (LightingPresetChoice)(n - 1) : null;
        if (Enum.TryParse<LightingPresetChoice>(text, true, out var e) && Enum.IsDefined(e)) return e;
        string Key(string s) => new string(Array.FindAll(s.ToCharArray(), char.IsLetterOrDigit)).ToLowerInvariant();
        for (int i = 0; i < Labels.Count; i++) if (Key(Labels[i]) == Key(text)) return (LightingPresetChoice)i;
        return null;
    }
}
