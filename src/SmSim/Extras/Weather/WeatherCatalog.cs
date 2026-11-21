namespace SmSim.Extras.Weather;

/// <summary>The four phases of a weather preset's cycle (owner brief 2026-11-21). The match starts in <see cref="Opening"/> once; then Buildup, Peak and Recovery repeat.</summary>
public enum WeatherPhase : byte { Opening = 0, Buildup = 1, Peak = 2, Recovery = 3 }

/// <summary>The stable weather preset ids; the byte is the wire value and the save value. <see cref="None"/> is "no added weather": the map's own look (Warfront's Ashfall storm).</summary>
public enum WeatherId : byte { None = 0, Clear = 1, LowFog = 2, AshSquall = 3, Rain = 4, Thunderstorm = 5, EmberWind = 6 }

/// <summary>
/// One preset of the catalog. Weather is presentation only (owner, 2026-11-21): nothing here reaches damage, movement, weapons or AI. Every number is **ours**, an estimate for a playtest to refine
/// (spec/behavior.md "Weather", the refine register).
/// </summary>
/// <param name="Real">Built in the client as a real look (W1: Clear, Low Fog, Rain; W2: Ash Squall, Thunderstorm, Ember Wind). A preset that is not built draws the map's default look until its milestone; since W2 none is left.</param>
/// <param name="OpeningLevel">The intensity (0 to 1) the opening phase ends at, and the buildup starts and the recovery ends at.</param>
/// <param name="Seconds">The length of each phase, in the order Opening, Buildup, Peak, Recovery, in seconds.</param>
public sealed record WeatherDef(WeatherId Id, string Key, string Name, string Short, bool Real, float OpeningLevel, double[] Seconds)
{
    /// <summary>The duration of <paramref name="phase"/> in seconds.</summary>
    public double Duration(WeatherPhase phase) => Seconds[(int)phase];
}

/// <summary>The one weather catalog (stable ids; docs/added-content.md "Weather"). Warfront uses it from W1; Classic hosting reuses it in W3.</summary>
public static class WeatherCatalog
{
    /// <summary>Raised with any change to a preset's id, key, phase lengths or levels.</summary>
    public const int Version = 2;   // 2: W2 builds Ash Squall, Thunderstorm and Ember Wind (their ids, keys, phase lengths and levels are unchanged)

    public static IReadOnlyList<WeatherDef> All { get; } =
    [
        new(WeatherId.None, "none", "Default look", "STORM", true, 0f, [60, 60, 120, 60]),
        new(WeatherId.Clear, "clear", "Clear", "CLEAR", true, 0.5f, [60, 60, 240, 60]),
        new(WeatherId.LowFog, "low_fog", "Low fog", "FOG", true, 0.35f, [45, 60, 240, 90]),
        new(WeatherId.AshSquall, "ash_squall", "Ash squall", "SQUALL", true, 0.1f, [60, 45, 90, 60]),
        new(WeatherId.Rain, "rain", "Rain", "RAIN", true, 0.3f, [45, 60, 180, 75]),
        new(WeatherId.Thunderstorm, "thunderstorm", "Thunderstorm", "THUNDER", true, 0.2f, [45, 75, 150, 75]),
        new(WeatherId.EmberWind, "ember_wind", "Ember wind", "EMBERS", true, 0.3f, [45, 60, 180, 60]),
    ];

    /// <summary>The preset with this wire value, or null.</summary>
    public static WeatherDef? Of(byte wire) => wire < All.Count ? All[wire] : null;

    public static WeatherDef Of(WeatherId id) => All[(int)id];

    /// <summary>The preset with this stable key ("low_fog"), or null.</summary>
    public static WeatherDef? ByKey(string? key)
    {
        if (key is null) return null;
        foreach (var d in All) if (string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    public static bool IsKnown(byte wire) => wire < All.Count;
}
