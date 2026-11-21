namespace SmSim.Extras.Weather;

/// <summary>
/// Weather for Classic PvP hosting (weather W3; added content, owner 2026-11-21): the rules that are not the catalog's. Classic weather is the content flag <c>weather</c> (in <c>extended</c>, or alone as <c>--content weather</c>):
/// <see cref="Carries"/> is the one gate. The host picks a preset (None by default) or Random; the server fits its cycle to the match's length and restarts it with every map and round. Presentation only: nothing here reaches
/// damage, movement, weapons or the AI. Every number is ours, an estimate for a playtest (spec/behavior.md "Weather (Classic PvP, W3)").
/// </summary>
public static class WeatherClassic
{
    /// <summary>The cycle of a match with no time limit is fitted to this many seconds (the match length the server defaults to, <c>MatchRules.TimeLimit</c> 600), and goes on repeating after it.</summary>
    public const int DefaultSpan = 600;
    /// <summary>The shortest and the longest span (seconds). A time limit outside them is clamped: the wire's span is 16 bits and a match shorter than a minute is a test, not a weather.</summary>
    public const int MinSpan = 60, MaxSpan = 65535;

    /// <summary>Classic PvP carries weather: any profile with the weather flag (<c>extended</c>, <c>weather</c>, <c>orks,weather</c>), never Musou or Warfront (they have their own).</summary>
    public static bool Carries(ContentExtras extras) => extras.HasFlag(ContentExtras.Weather) && !extras.HasFlag(ContentExtras.Musou) && !extras.HasFlag(ContentExtras.Warfront);

    /// <summary>The cycle's span for a match of <paramref name="timeLimitSeconds"/> (0 or less: none, <see cref="DefaultSpan"/>), clamped to <see cref="MinSpan"/> and <see cref="MaxSpan"/>.</summary>
    public static int SpanFor(float timeLimitSeconds)
    {
        if (!float.IsFinite(timeLimitSeconds) || timeLimitSeconds <= 0f) return DefaultSpan;
        return (int)Math.Clamp(Math.Round(timeLimitSeconds), MinSpan, MaxSpan);
    }

    /// <summary>The key that draws a preset per map load (<c>--weather random</c>, the Host panel's RANDOM).</summary>
    public const string RandomKey = "random";

    /// <summary>One choice of the host's selector: the key the server flag takes and the words on the row.</summary>
    public readonly record struct Choice(string Key, string Label);

    /// <summary>The host's selector, in its order: None (the default), the six presets, then Random.</summary>
    public static IReadOnlyList<Choice> HostChoices { get; } =
    [
        new("none", "NONE"), new("clear", "CLEAR"), new("low_fog", "LOW FOG"), new("rain", "RAIN"), new("thunderstorm", "THUNDERSTORM"),
        new("ash_squall", "ASH SQUALL"), new("ember_wind", "EMBER WIND"), new(RandomKey, "RANDOM"),
    ];

    /// <summary>The key a server flag or host row may carry: a preset's stable key or <see cref="RandomKey"/> (case, spaces and dashes ignored); null for anything else.</summary>
    public static string? NormalizeKey(string? text)
    {
        if (text is null) return null;
        string k = text.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return HostChoices.Any(c => c.Key == k) ? k : null;
    }

    /// <summary>The keys for messages.</summary>
    public static string Expected => string.Join("|", HostChoices.Select(c => c.Key));

    /// <summary>
    /// The preset a match plays: <paramref name="key"/> as given, or for Random the draw of this map load (<paramref name="draw"/>, 0 for the first match of a server, counting up with every map of a rotation).
    /// A missing or unknown key is None. Pure in (<paramref name="seed"/>, <paramref name="draw"/>), so a rotation's order is reproducible from its seed.
    /// </summary>
    public static WeatherId Resolve(string? key, uint seed, int draw) => NormalizeKey(key) switch
    {
        null => WeatherId.None,
        RandomKey => Draw(seed, draw),
        var k => WeatherCatalog.ByKey(k)?.Id ?? WeatherId.None,
    };

    /// <summary>
    /// The Random draw: one of the seven presets (None too: the owner, 2026-11-21), each map load a different one from the load before it (the first is uniform; each later one is uniform among the other six).
    /// </summary>
    public static WeatherId Draw(uint seed, int draw)
    {
        const int Presets = 7;   // the whole catalog: None, Clear, Low Fog, Ash Squall, Rain, Thunderstorm, Ember Wind (None may be drawn: the map as it is is a fair draw)
        int last = -1;
        for (int i = 0; i <= Math.Max(0, draw); i++)
        {
            ulong h = Mix(((ulong)seed << 20) ^ ((ulong)i * 0x9E3779B97F4A7C15UL) ^ 0xC1A55C0DE0000001UL);
            last = last < 0 ? (int)(h % Presets) : (last + 1 + (int)(h % (Presets - 1))) % Presets;
        }
        return (WeatherId)last;
    }

    static ulong Mix(ulong x)
    {
        x ^= x >> 33; x *= 0xFF51AFD7ED558CCDUL; x ^= x >> 33; x *= 0xC4CEB9FE1A85EC53UL; x ^= x >> 33;
        return x;
    }
}
