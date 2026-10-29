using System.Text;
using SmSim;

namespace SmNet;

/// <summary>
/// Display names (names/bot-lore, protocol 41; the owner's request of 2026-10-29: "give the bots lore accurate names instead of the current P1, P2, etc.").
/// The server gives each test bot a name from its race's pool when it joins (<see cref="Pick"/>); the names travel in the scoreboard rows
/// (<see cref="ScoreEntry.Name"/>) and every client shows them in the kill feed, the scoreboard, the name tags, the aimed-enemy text and the
/// end-of-match summary. A player without a name (every human today: no profile name exists yet) shows as "P&lt;id&gt;" (<see cref="Fallback"/>).
/// <para>
/// **The pools are ours**: original names in the style of each faction, none of them an established character of Space Marine (2011) or of the
/// wider Warhammer 40,000 canon (pinned by <c>PlayerNameTests</c> against a list of canon names). ASCII only (the HUD font), at most
/// <see cref="MaxLength"/> characters. The Ork pool belongs to the added content (the Ork Boy); it is used only for a player of
/// <see cref="Race.Orks"/>, which only a match with the Orks has.
/// </para>
/// </summary>
public static class PlayerNames
{
    /// <summary>The longest name on the wire and on screen (characters, Latin-1).</summary>
    public const int MaxLength = 24;

    /// <summary>
    /// Loyalist Space Marines (Ultramarines by default): "Brother" and a Latin / High Gothic style given name; a few "Sergeant" for when the
    /// brothers run low (ours).
    /// </summary>
    public static readonly IReadOnlyList<string> SpaceMarines =
    [
        "Brother Aurex", "Brother Daecus", "Brother Vellius", "Brother Cassor", "Brother Taurex", "Brother Marcan", "Brother Octavus",
        "Brother Sevrin", "Brother Corvan", "Brother Ostiar", "Brother Fulvan", "Brother Valerin", "Brother Justan", "Brother Ostrian",
        "Brother Feronus", "Brother Quintar", "Brother Tavian", "Brother Ancus", "Brother Celsan", "Brother Domitar", "Brother Tervan",
        "Brother Vitrian", "Brother Ardias", "Brother Lysian", "Brother Tiberan", "Brother Novar", "Brother Vespan", "Brother Orestan",
        "Brother Galvarus", "Brother Helvan",
        "Sergeant Albinar", "Sergeant Ducarius", "Sergeant Verrin", "Sergeant Pallax",
    ];

    /// <summary>Chaos Space Marines: harsh names, some with an epithet (ours).</summary>
    public static readonly IReadOnlyList<string> Chaos =
    [
        "Skarne the Unbroken", "Ghorvane", "Draegor the Flayer", "Kasthrax", "Zhorvek", "Malgrith the Hollow", "Ruskarth", "Thexor",
        "Vexhal the Twice-Damned", "Gorvenn", "Ashkaul", "Skarvane", "Kytheron", "Dravek Iron-Tongue", "Sarghul", "Nhorek",
        "Vashkar the Blighted", "Ulkhar", "Zerkan", "Morvax", "Kraegon", "Ghaurek", "Velkhor", "Xarvos", "Brakkus the Red", "Ozrakh",
        "Harrovane", "Zhor of the Burning Eye", "Ilkarn of the Ninth Pyre", "Sathrek", "Kardhul", "Vorsk the Ashen",
    ];

    /// <summary>Orks (the added content's Ork Boy): short, blunt names (ours).</summary>
    public static readonly IReadOnlyList<string> Orks =
    [
        "Skabgob", "Ruttgut", "Dakkrot", "Zogwump", "Snazzrok", "Bruzgit", "Nogglug", "Urrgrak", "Waznob", "Skragga", "Krumpgit", "Dregnob",
        "Gutzog", "Badgob", "Rokkgit", "Zarbruk", "Grubbruk", "Ugnash", "Lugzag", "Braggit", "Fangrot", "Krogsnot", "Muzgob", "Thugrak",
        "Nazzbog", "Drukka", "Gitsmasha", "Snotrukk", "Klawgut", "Skumdakka", "Bonzgob", "Gobbruz",
    ];

    /// <summary>The Tab scoreboard's name column (characters, monospace): a <see cref="MaxLength"/> name, " (you)" and one space before the next column.</summary>
    public const int ScoreboardColumn = MaxLength + 7;

    /// <summary>
    /// <paramref name="text"/> as a left-aligned scoreboard cell of <paramref name="width"/> characters: padded with spaces, or cut so that one space is
    /// left before the next column.
    /// </summary>
    public static string Cell(string text, int width) => text.Length >= width ? text[..(width - 1)] + " " : text.PadRight(width);

    /// <summary>The pool of <paramref name="race"/>: Chaos and Orks their own, anything else the loyalists'.</summary>
    public static IReadOnlyList<string> PoolFor(Race race) => race switch { Race.Chaos => Chaos, Race.Orks => Orks, _ => SpaceMarines };

    /// <summary>The name shown for a player without one: "P&lt;id&gt;" (as before protocol 41).</summary>
    public static string Fallback(int playerId) => $"P{playerId}";

    /// <summary>The name to show: <paramref name="name"/> when it has one, else <see cref="Fallback"/>.</summary>
    public static string Display(int playerId, string? name) => string.IsNullOrEmpty(name) ? Fallback(playerId) : name;

    /// <summary>
    /// A name of <paramref name="race"/>'s pool for the <paramref name="order"/>-th name given in that race (0, 1, ...), that <paramref name="taken"/>
    /// does not hold (compared without case): the pool shuffled by <paramref name="seed"/> and the race, read from position <paramref name="order"/> on
    /// (wrapping), the first free one. The same seed, race, order and taken names give the same name. When the whole pool is taken (more players of
    /// one race than names; 16 players cannot), the pool's names with " II", " III", ... in the same order.
    /// </summary>
    public static string Pick(Race race, uint seed, int order, Func<string, bool> taken)
    {
        var pool = PoolFor(race);
        var shuffled = Shuffled(pool, seed ^ (uint)race * 0x9E3779B9u);
        int start = (int)((uint)Math.Max(0, order) % (uint)shuffled.Length);
        for (int round = 1; ; round++)
        {
            for (int k = 0; k < shuffled.Length; k++)
            {
                string name = shuffled[(start + k) % shuffled.Length];
                if (round > 1) name = Trim($"{name} {Roman(round)}");
                if (!taken(name)) return name;
            }
            if (round > 50) return $"{shuffled[start]} {seed % 1000}";   // never reached with a sane taken set
        }
    }

    /// <summary>
    /// A name as it may travel and be drawn: Latin-1 letters and digits, space, '-', '\'' and '.' kept (anything else dropped: control characters,
    /// BBCode brackets, characters the HUD font may not have), runs of spaces collapsed, trimmed, at most <see cref="MaxLength"/> characters. Null
    /// gives "". Used on every name the server sends and every name a client reads (a future client-supplied name goes through it too).
    /// </summary>
    public static string Sanitise(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(Math.Min(raw.Length, MaxLength));
        foreach (char c in raw)
        {
            if (sb.Length >= MaxLength) break;
            bool keep = c <= 'ÿ' && (char.IsLetterOrDigit(c) || c is '-' or '\'' or '.');
            if (keep) sb.Append(c);
            else if (char.IsWhiteSpace(c) && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    static string Trim(string s) => s.Length <= MaxLength ? s : s[..MaxLength].TrimEnd();

    static string Roman(int n) => n switch { 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>The pool in a seeded order (Fisher-Yates with a SplitMix-style generator: the same on every platform).</summary>
    static string[] Shuffled(IReadOnlyList<string> pool, uint seed)
    {
        var a = pool.ToArray();
        ulong state = (((ulong)seed << 32) | seed) ^ 0xD1B54A32D192ED03UL;
        for (int i = a.Length - 1; i > 0; i--)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            int j = (int)(z % (ulong)(i + 1));
            (a[i], a[j]) = (a[j], a[i]);
        }
        return a;
    }
}
