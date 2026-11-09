using System.Globalization;
using SmSim;
using SmSim.Extras.Musou;

namespace SmContent.Extras.Musou;

/// <summary>The Musou customizer's top-level entries (owner, 2026-11-10), in the order shown.</summary>
public enum MusouCustomizerEntry { StandardMarines, Librarian, Heretic }

/// <summary>One top-level entry: its button text, its name in lines and cards, whether it can be played now and its tooltip.</summary>
public sealed record MusouEntryInfo(MusouCustomizerEntry Entry, string Label, string Name, bool Available, string Tooltip, int Width);

/// <summary>What a row of the Librarian's page is: real and fixed for him, carried over from the player's Space Marine loadout, or a customization that does not exist yet.</summary>
public enum MusouRowKind { Fixed, CarriedOver, NotYet }

/// <summary>A row of the Librarian's page (title, card text, kind).</summary>
public sealed record MusouRow(string Title, string Description, MusouRowKind Kind)
{
    /// <summary>The words on the row's right (the creator's own tags: FIXED, and NOT YET for what does not exist).</summary>
    public IReadOnlyList<string> Tags => Kind switch
    {
        MusouRowKind.Fixed => ["FIXED"],
        MusouRowKind.CarriedOver => ["FROM YOUR LOADOUT"],
        _ => ["NOT YET"],
    };
}

/// <summary>A heading and its rows on one step of the Librarian's page.</summary>
public sealed record MusouSection(string Heading, IReadOnlyList<MusouRow> Rows);

/// <summary>
/// The Musou layout of the customizer (owner, 2026-11-10, verbatim: "adjust the existing loadout customizer that exists for both classic pvp and musou mode currently - leave classic pvp
/// as it is now and only use this new adjustment for musou mode. the adjustment will refocus the customizer in musou mode only. the top level options to replace faction options are these:
/// standard marines, librarian class, heretic class"). Engine-free (the client's <c>CreatorView.Musou.cs</c> draws it): the three entries, the Librarian's page (his fixed loadout from
/// <see cref="LibrarianRules"/>, what carries over from the Marines' look, what is not there yet) and the Heretic placeholder. Every text is ours. Classic PvP never reaches this: the layout is used
/// only when the customizer's content profile is Musou (<see cref="Uses"/>), and <c>--musou-old-customizer</c> brings back the faction row in Musou too.
/// </summary>
public static class MusouCustomizer
{
    /// <summary>The client flag that restores the shared (Classic) layout in Musou Mode: <c>--musou-old-customizer</c>.</summary>
    public const string OldCustomizerFlag = "musou-old-customizer";

    /// <summary>The caption in front of the entries (Classic's says "Faction:" or "Preview side:").</summary>
    public const string Caption = "Musou Mode:";

    /// <summary>Whether the customizer shows the Musou layout: the profile it serves is Musou's and the restore flag is not given.</summary>
    public static bool Uses(ContentExtras content, bool oldCustomizer) => content.HasFlag(ContentExtras.Musou) && !oldCustomizer;

    /// <summary>The entries in the order shown. The Heretic is a placeholder (no class, no gameplay): shown so the structure is ready, marked not yet available.</summary>
    public static readonly IReadOnlyList<MusouEntryInfo> Entries =
    [
        new(MusouCustomizerEntry.StandardMarines, "Standard Marines", "Standard Marines", true,
            "Tactical, Assault and Devastator: your custom loadouts, wargear, perks and armour (Space Marine side)", 230),
        new(MusouCustomizerEntry.Librarian, "Librarian", "Librarian", true, "The psyker: force sword and three psychic powers; wears your active loadout's look", 190),
        new(MusouCustomizerEntry.Heretic, "Heretic (not yet)", "Heretic", false, "Not yet available: a placeholder for a future class", 230),
    ];

    public static MusouEntryInfo Info(MusouCustomizerEntry entry) => Entries.First(e => e.Entry == entry);

    /// <summary>The Librarian's steps (the creator's five step buttons: class, weapons, his powers in the equipment's place, perks, look).</summary>
    public static readonly IReadOnlyList<string> LibrarianSteps = ["CLASS", "WEAPONS", "POWERS", "PERKS", "LOOK"];

    /// <summary>The step that shows his look (its rows open the appearance editor).</summary>
    public const int LookStep = 4;

    static string N(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The line under the header on the Librarian's page; <paramref name="lookOf"/> names the active loadout whose look he wears.</summary>
    public static string LibrarianSummary(string lookOf) =>
        $"LIBRARIAN: force sword; chain lightning, Warp Pull, Psychic Shockwave; no gun, pistol or grenade. Look: your active loadout's ({lookOf}).";

    /// <summary>The note under the Librarian's rows on every step.</summary>
    public const string LibrarianNote =
        "The Librarian has no loadout slots of his own. Pick him in the Stormbird: PLAY AS LIBRARIAN (on this page in a match, or in the Esc menu).";

    /// <summary>The Librarian's rows on step <paramref name="step"/> (0 to 4, <see cref="LibrarianSteps"/>); <paramref name="lookOf"/> names the active loadout whose look he wears.</summary>
    public static IReadOnlyList<MusouSection> LibrarianStep(int step, string lookOf) => step switch
    {
        0 =>
        [
            new("CLASS", [new("Librarian", "A psyker of the Chapter, Musou Mode's own class: the Tactical Marine's body and perk list, a force sword, and three psychic powers "
                + "(one Psychic Stress scale) in place of a gun, a pistol and grenades.", MusouRowKind.Fixed)]),
        ],
        1 =>
        [
            new("MELEE WEAPON  (fixed for this class)",
                [new("Force sword", $"The power sword's strikes and combo, with a psychic glow. A swing that strikes an enemy takes {N(LibrarianRules.MeleeRelief)} off his Psychic Stress.", MusouRowKind.Fixed)]),
            new("PRIMARY WEAPON AND SIDEARM  (fixed for this class)",
                [new("None", "He carries no gun and no pistol: Fire, Grenade and Reload cast his powers.", MusouRowKind.Fixed)]),
            new("NOT YET",
                [new("Other melee weapons", "His class lists the Tactical Marine's other melee weapons after the sword, but nothing can choose one for him yet: he always starts with the force sword.", MusouRowKind.NotYet)]),
        ],
        2 =>
        [
            new("PSYCHIC POWERS  (fixed for this class)",
            [
                new("Chain lightning  (Fire, hold)", $"Lightning arcs up to {N(LibrarianRules.LightningRange)} m and jumps up to {N(LibrarianRules.JumpRadius)} m from Ork to Ork, at most {LibrarianRules.MaxTargets} targets: "
                    + $"{N(LibrarianRules.PrimaryDps)} damage a second, x{N(LibrarianRules.JumpFalloff)} each jump. Adds {N(LibrarianRules.LightningStressPerSecond)} stress a second.", MusouRowKind.Fixed),
                new("Warp Pull  (Grenade)", $"Orks in a {N(LibrarianRules.PullConeDegrees)}-degree cone up to {N(LibrarianRules.PullRange)} m are pulled to {N(LibrarianRules.PullStopDistance)} m of you; elites come "
                    + $"{N(LibrarianRules.ElitePullShare * 100f)} % of the way, bosses not at all. Adds {N(LibrarianRules.PullStress)} stress; {N(LibrarianRules.PullCooldown)} s cooldown.", MusouRowKind.Fixed),
                new("Psychic Shockwave  (Reload)", $"Everything within {N(LibrarianRules.ShockwaveRadius)} m takes {N(LibrarianRules.ShockwaveDamage)} damage; ordinary Orks are thrown {N(LibrarianRules.ThrowMin)} to "
                    + $"{N(LibrarianRules.ThrowMax)} m, elites and bosses stay. Adds {N(LibrarianRules.ShockwaveStress)} stress; {N(LibrarianRules.ShockwaveCooldown)} s cooldown.", MusouRowKind.Fixed),
                new("Psychic Stress", $"0 to {N(LibrarianRules.StressMax)}. It cools {N(LibrarianRules.CoolPerSecond)} a second after {N(LibrarianRules.CoolDelay)} s without casting; at {N(LibrarianRules.StressMax)} every "
                    + "power locks until it is back to 0.", MusouRowKind.Fixed),
            ]),
            new("NOT YET", [new("Choice of powers", "His three powers are fixed: there are no other powers to choose.", MusouRowKind.NotYet)]),
        ],
        3 =>
        [
            new("PERKS", [new("Kept from your current class", "Not chosen here: when you pick him he keeps the perk slots of the class you are playing (his perk list is the Tactical Marine's).", MusouRowKind.Fixed)]),
            new("NOT YET", [new("His own perks", "A perk choice for the Librarian alone needs a Librarian loadout in your profile, which does not exist yet.", MusouRowKind.NotYet)]),
        ],
        _ =>
        [
            new("LOOK", [new("Armour paint and pieces", $"He wears your active loadout's Space Marine look ({lookOf}): chapter colours, helmet, left pad, arms, legs, lens and badges. "
                + "His torso and belt, right pad and psychic hood are the Librarian's own, in that look's colours.", MusouRowKind.CarriedOver)]),
            new("NOT YET", [new("A look of his own", "His look is always your active loadout's: a separate Librarian look is not stored in your profile yet.", MusouRowKind.NotYet)]),
        ],
    };

    /// <summary>The Heretic placeholder's line under the header, heading and text.</summary>
    public const string HereticSummary = "HERETIC: a placeholder for a future Musou class.";
    public const string HereticHeading = "HERETIC: NOT YET AVAILABLE";
    public const string HereticText =
        "A placeholder for a future Musou class. There is no Heretic class, weapon, power or look yet: nothing here can be chosen or played.";

    /// <summary>What EDIT LOOK says when the active loadout is a DEFAULTS one (it wears the default look, which cannot be edited).</summary>
    public const string DefaultsLookNote =
        "Your active loadout is a DEFAULTS loadout: he wears the default look. Make a custom loadout active under Standard Marines to paint him.";
}
