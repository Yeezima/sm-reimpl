using System.Globalization;
using System.Reflection;
using SmSim;
using SmSim.Extras.Musou;

namespace SmContent.Extras.Musou;

/// <summary>The Musou customizer's top-level entries (owner, 2026-11-10), in the order shown.</summary>
public enum MusouCustomizerEntry { StandardMarines, Librarian, Heretic }

/// <summary>One top-level entry: its button text, its name in lines and cards, whether it can be played now and its tooltip.</summary>
public sealed record MusouEntryInfo(MusouCustomizerEntry Entry, string Label, string Name, bool Available, string Tooltip, int Width);

/// <summary>
/// What a row of the Librarian's page is: real and fixed for him, carried over from the player's Space Marine loadout, a customization that does not exist yet, or (musou/loadout-slots) a
/// choice of his own loadout (<see cref="MusouRow.Action"/> says which).
/// </summary>
public enum MusouRowKind { Fixed, CarriedOver, NotYet, Choice }

/// <summary>
/// A row of the Librarian's page (title, card text, kind). A <see cref="MusouRowKind.Choice"/> row carries its <see cref="Action"/> (<c>melee:</c>, <c>equipment:</c>, <c>perk1:</c>, <c>perk2:</c> or
/// <c>look:</c> and a name; an empty name is the default, <c>look:new</c> a new look of his own) and whether it is the one chosen.
/// </summary>
public sealed record MusouRow(string Title, string Description, MusouRowKind Kind, string? Action = null, bool Selected = false)
{
    /// <summary>The words on the row's right (the creator's own tags: FIXED, SELECTED, and NOT YET for what does not exist).</summary>
    public IReadOnlyList<string> Tags => Kind switch
    {
        MusouRowKind.Fixed => ["FIXED"],
        MusouRowKind.CarriedOver => ["FROM YOUR LOADOUT"],
        MusouRowKind.Choice => Selected ? ["SELECTED"] : [],
        _ => ["NOT YET"],
    };
}

/// <summary>
/// The Librarian's own loadout as his page offers it (musou/loadout-slots): what is chosen (<see cref="MusouLibrarianLoadout"/>), his class's perks (name, title, card text), the class default
/// perk and the profile's Space Marine looks (id, name).
/// </summary>
public sealed record MusouLibrarianPicks(MusouLibrarianLoadout Chosen, IReadOnlyList<(string Name, string Title, string Text)> Perks, string? DefaultPerk,
    IReadOnlyList<(string Id, string Name)> Looks)
{
    /// <summary>The perk in slot 1: the chosen one, else the class default.</summary>
    public string? Perk1 => Chosen.Perk1 ?? DefaultPerk;

    /// <summary>A perk's title (its name when the list lacks it; "none" for null).</summary>
    public string PerkTitle(string? name)
    {
        if (name is null) return "none";
        foreach (var p in Perks) if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Title;
        return name;
    }
}

/// <summary>A heading and its rows on one step of the Librarian's page.</summary>
public sealed record MusouSection(string Heading, IReadOnlyList<MusouRow> Rows);

/// <summary>
/// The Musou layout of the customizer (owner, 2026-11-10, verbatim: "adjust the existing loadout customizer that exists for both classic pvp and musou mode currently - leave classic pvp
/// as it is now and only use this new adjustment for musou mode. the adjustment will refocus the customizer in musou mode only. the top level options to replace faction options are these:
/// standard marines, librarian class, heretic class"). Engine-free (the client's <c>CreatorView.Musou.cs</c> draws it): the three entries, the Librarian's page (read from the data: his class as
/// <see cref="MusouLoadout"/> makes it, <see cref="LibrarianLoadout"/>, and the powers <see cref="LibrarianRules"/> holds, <see cref="LibrarianPowers"/>; what carries over from the Marines' look, what is not there yet) and the Heretic placeholder. Every text is ours. Classic PvP never reaches this: the layout is used
/// only when the customizer's content profile is Musou (<see cref="Uses"/>), and <c>--musou-old-customizer</c> brings back the faction row in Musou too.
/// </summary>
public static class MusouCustomizer
{
    /// <summary>The client flag that restores the shared (Classic) layout in Musou Mode: <c>--musou-old-customizer</c>.</summary>
    public const string OldCustomizerFlag = "musou-old-customizer";

    /// <summary>The client flag that makes Musou's pages edit Classic's loadout slots again (musou/loadout-slots restore switch): <c>--musou-shared-slots</c>.</summary>
    public const string SharedSlotsFlag = "musou-shared-slots";

    /// <summary>The caption in front of the entries (Classic's says "Faction:" or "Preview side:").</summary>
    public const string Caption = "Musou Mode:";

    /// <summary>Whether the customizer shows the Musou layout: the profile it serves is Musou's and the restore flag is not given.</summary>
    public static bool Uses(ContentExtras content, bool oldCustomizer) => content.HasFlag(ContentExtras.Musou) && !oldCustomizer;

    /// <summary>The entries in the order shown. The Heretic is a placeholder (no class, no gameplay): shown so the structure is ready, marked not yet available.</summary>
    public static readonly IReadOnlyList<MusouEntryInfo> Entries =
    [
        new(MusouCustomizerEntry.StandardMarines, "Standard Marines", "Standard Marines", true,
            "Tactical, Assault and Devastator: your Musou loadouts (ten slots of their own, apart from Classic PvP's), wargear, perks and armour (Space Marine side)", 230),
        new(MusouCustomizerEntry.Librarian, "Librarian", "Librarian", true, "The psyker: force sword and psychic powers; his own melee weapon, equipment, perks and look", 190),
        new(MusouCustomizerEntry.Heretic, "Heretic (not yet)", "Heretic", false, "Not yet available: a placeholder for a future class", 230),
    ];

    public static MusouEntryInfo Info(MusouCustomizerEntry entry) => Entries.First(e => e.Entry == entry);

    /// <summary>The Librarian's steps (the creator's five step buttons: class, weapons, his powers and equipment in the equipment's place, perks, look).</summary>
    public static readonly IReadOnlyList<string> LibrarianSteps = ["CLASS", "WEAPONS", "POWERS", "PERKS", "LOOK", "SUPPORT"];

    /// <summary>The step that shows the support ability (owner, 2026-11-12; the same sixth step the marines' page has after PERKS).</summary>
    public const int SupportStep = 5;

    /// <summary>
    /// The SUPPORT step's rows (owner, 2026-11-12; docs/drop-pod-assault.md): what B calls in a battle, one choice per loadout (the artillery strike, the default, and the drop pod assault). A row's action is
    /// <c>support:artillery</c> or <c>support:droppod</c>; <paramref name="chosen"/> is the one chosen.
    /// </summary>
    public static IReadOnlyList<MusouSection> SupportRows(MusouSupport chosen) =>
    [
        new("SUPPORT ABILITY (B)",
        [
            new("Artillery strike", "B marks a point with the laser designator; a walking barrage of shells follows a second later and kills the Orks it touches. 60 s cooldown in a battle (the same cooldown for both support abilities; none at the Firing Range).",
                MusouRowKind.Choice, "support:" + MusouSlots.SupportName(MusouSupport.Artillery), chosen == MusouSupport.Artillery),
            new("Drop pod assault", "B marks a point with the laser designator; a few seconds later a drop pod falls from the sky in fire and smoke and shakes the ground. Its doors open and 4 Tactical Marines fight for 30 seconds, then retreat to the Imperial HQ and vanish. Needs the pod of the campaign's data.",
                MusouRowKind.Choice, "support:" + MusouSlots.SupportName(MusouSupport.DropPod), chosen == MusouSupport.DropPod),
        ]),
    ];

    /// <summary>The step that shows his look (its rows open the appearance editor).</summary>
    public const int LookStep = 4;

    static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// The line under the header on the Librarian's page, from the data: his melee weapon, powers, equipment and guns; <paramref name="lookOf"/> names the active loadout whose look he wears. With
    /// <paramref name="picks"/> (his own loadout, musou/loadout-slots) the chosen melee weapon, equipment, perks and look.
    /// </summary>
    public static string LibrarianSummary(string lookOf, LibrarianLoadout loadout, MusouLibrarianPicks? picks = null)
    {
        var parts = new List<string> { (picks is null ? null : loadout.MeleeTitle(picks.Chosen.Melee)) ?? loadout.Melee ?? "no melee weapon" };
        var powers = LibrarianPowers.Present().Where(p => p.IsPower).Select(p => p.Name).ToList();
        if (powers.Count > 0) parts.Add(string.Join(", ", powers));
        if (picks is null) parts.Add(loadout.Equipment.Count > 0 ? string.Join(" or ", loadout.Equipment) : "no grenades");
        else parts.Add(loadout.EquipmentTitle(picks.Chosen.Equipment) ?? "no grenades");
        parts.Add(loadout.Guns.Count > 0 ? string.Join(", ", loadout.Guns) : "no gun or pistol");
        if (picks is null) return $"LIBRARIAN: {string.Join("; ", parts)}. Look: your active loadout's ({lookOf}).";
        var perks = new[] { picks.Perk1, picks.Chosen.Perk2 }.OfType<string>().Select(picks.PerkTitle).ToList();
        if (perks.Count > 0) parts.Add(string.Join(", ", perks));
        string? own = null;
        foreach (var (id, name) in picks.Looks) if (id == picks.Chosen.Look) own = name;
        return $"LIBRARIAN: {string.Join("; ", parts)}. Look: {own ?? $"your active loadout's ({lookOf})"}.";
    }

    /// <summary>The note under the Librarian's rows on every step (with <c>--musou-shared-slots</c>: he has no loadout).</summary>
    public const string LibrarianNote =
        "The Librarian has no loadout slots of his own. Pick him in the Stormbird: PLAY AS LIBRARIAN (on this page in a match, or in the Esc menu).";

    /// <summary>The note under the Librarian's rows when he has his own loadout (musou/loadout-slots).</summary>
    public const string LibrarianOwnNote =
        "His loadout is saved in your profile with your Musou loadouts (SAVE keeps it). Pick him in the Stormbird: PLAY AS LIBRARIAN (on this page in a match, or in the Esc menu).";

    /// <summary>
    /// The Librarian's rows on step <paramref name="step"/> (0 to 5, <see cref="LibrarianSteps"/>), from the data: <paramref name="loadout"/> (his class as <c>MusouLoadout</c> makes it) and the powers whose
    /// numbers <see cref="LibrarianRules"/> holds (<see cref="LibrarianPowers"/>). <paramref name="lookOf"/> names the active loadout whose look he wears.
    /// </summary>
    public static IReadOnlyList<MusouSection> LibrarianStep(int step, string lookOf, LibrarianLoadout loadout, MusouLibrarianPicks? picks = null, MusouSupport support = MusouSupport.Artillery)
    {
        if (step == SupportStep) return SupportRows(support);
        if (picks is not null && step is >= 1 and <= 4) return LibrarianOwnStep(step, lookOf, loadout, picks);
        var powers = LibrarianPowers.Present();
        int powerCount = powers.Count(p => p.IsPower);
        switch (step)
        {
            case 0:
            {
                var carries = new List<string> { loadout.Melee is { } m ? m.ToLowerInvariant() : "no melee weapon" };
                if (powerCount > 0) carries.Add($"{powerCount} psychic power{(powerCount == 1 ? "" : "s")} on one Psychic Stress scale");
                carries.Add(loadout.Equipment.Count > 0 ? string.Join(" or ", loadout.Equipment).ToLowerInvariant() : "no grenades");
                carries.Add(loadout.Guns.Count > 0 ? string.Join(", ", loadout.Guns).ToLowerInvariant() : "no gun or pistol");
                return [new("CLASS", [new("Librarian", $"A psyker of the Chapter, Musou Mode's own class on the Tactical Marine's body: {string.Join(", ", carries)}.", MusouRowKind.Fixed)])];
            }
            case 1:
            {
                var sections = new List<MusouSection>();
                string relief = LibrarianPowers.Value("MeleeRelief") is { } r ? $" A swing that strikes an enemy takes {N(r)} off his Psychic Stress." : "";
                sections.Add(new("MELEE WEAPON  (fixed for this class)",
                    [loadout.Melee is { } melee ? new MusouRow(melee, "His starting melee weapon: the first of his class's melee choices." + relief, MusouRowKind.Fixed)
                        : new MusouRow("None", "His class has no melee weapon.", MusouRowKind.Fixed)]));
                sections.Add(new("PRIMARY WEAPON AND SIDEARM  (fixed for this class)",
                    loadout.Guns.Count > 0 ? loadout.Guns.Select(g => new MusouRow(g, "A gun his class carries.", MusouRowKind.Fixed)).ToList()
                        : [new MusouRow("None", "His class carries no gun and no pistol.", MusouRowKind.Fixed)]));
                if (loadout.OtherMelee.Count > 0)
                    sections.Add(new("NOT YET", [new("Other melee weapons", $"His class also lists {string.Join(", ", loadout.OtherMelee)}, but nothing can choose one for him yet: he always starts with the first.", MusouRowKind.NotYet)]));
                return sections;
            }
            case 2:
            {
                var sections = new List<MusouSection>();
                var powerRows = powers.Select(p => new MusouRow(p.Title, p.Text, MusouRowKind.Fixed)).ToList();
                if (powerRows.Count > 0) sections.Add(new("PSYCHIC POWERS  (fixed for this class)", powerRows));
                if (loadout.Equipment.Count == 0)
                    sections.Add(new("EQUIPMENT  (fixed for this class)", [new("None", "His class carries no grenades or stimulants.", MusouRowKind.Fixed)]));
                else
                {
                    sections.Add(new("EQUIPMENT  (fixed for this class)", [new(loadout.Equipment[0], "Carried by default: the first of his class's equipment.", MusouRowKind.Fixed)]));
                    var others = loadout.Equipment.Skip(1).Select(e => new MusouRow(e, $"His class lists {e}, but choosing it for him needs a Librarian loadout, which does not exist yet.", MusouRowKind.NotYet)).ToList();
                    if (others.Count > 0) sections.Add(new("NOT YET", others));
                }
                sections.Add(new("NOT YET", [new("Choice of powers", "His powers are fixed: there are no other powers to choose.", MusouRowKind.NotYet)]));
                return sections;
            }
            case 3:
                return
                [
                    new("PERKS", [new("Kept from your current class", "Not chosen here: the perks come with the class you play when you pick him.", MusouRowKind.Fixed)]),
                    new("NOT YET", [new("His own perks", "A perk choice for the Librarian alone needs a Librarian loadout in your profile, which does not exist yet.", MusouRowKind.NotYet)]),
                ];
            default:
                return
                [
                    new("LOOK", [new("Armour paint and pieces", $"He wears your active loadout's Space Marine look ({lookOf}): chapter colours, helmet, left pad, arms, legs, lens and badges. "
                        + "His torso and belt, right pad and psychic hood are the Librarian's own, in that look's colours.", MusouRowKind.CarriedOver)]),
                    new("NOT YET", [new("A look of his own", "His look is always your active loadout's: a separate Librarian look is not stored in your profile yet.", MusouRowKind.NotYet)]),
                ];
        }
    }

    /// <summary>
    /// The steps WEAPONS, POWERS, PERKS and LOOK with his own loadout (musou/loadout-slots): his melee weapon, equipment, two perks and look as choices (the first of a list, or nothing chosen,
    /// is his default); the powers and guns stay fixed.
    /// </summary>
    static IReadOnlyList<MusouSection> LibrarianOwnStep(int step, string lookOf, LibrarianLoadout loadout, MusouLibrarianPicks picks)
    {
        var chosen = picks.Chosen;
        bool Is(string name, string? stored, int index) => stored is null ? index == 0 : name.Equals(stored, StringComparison.OrdinalIgnoreCase);
        switch (step)
        {
            case 1:
            {
                var sections = new List<MusouSection>();
                string relief = LibrarianPowers.Value("MeleeRelief") is { } r ? $" A swing that strikes an enemy takes {N(r)} off his Psychic Stress." : "";
                var melee = loadout.MeleeOptions;
                sections.Add(new("MELEE WEAPON", melee.Count == 0 ? [new MusouRow("None", "His class has no melee weapon.", MusouRowKind.Fixed)]
                    : melee.Select((m, i) => new MusouRow(m.Title, (i == 0 ? "His starting melee weapon (the default)." : "A melee weapon his class carries.") + relief,
                        melee.Count == 1 ? MusouRowKind.Fixed : MusouRowKind.Choice, "melee:" + (i == 0 ? "" : m.Name), Is(m.Name, chosen.Melee, i))).ToList()));
                sections.Add(new("PRIMARY WEAPON AND SIDEARM  (fixed for this class)",
                    loadout.Guns.Count > 0 ? loadout.Guns.Select(g => new MusouRow(g, "A gun his class carries.", MusouRowKind.Fixed)).ToList()
                        : [new MusouRow("None", "His class carries no gun and no pistol.", MusouRowKind.Fixed)]));
                return sections;
            }
            case 2:
            {
                var sections = new List<MusouSection>();
                var powerRows = LibrarianPowers.Present().Select(p => new MusouRow(p.Title, p.Text, MusouRowKind.Fixed)).ToList();
                if (powerRows.Count > 0) sections.Add(new("PSYCHIC POWERS  (fixed for this class)", powerRows));
                var gear = loadout.EquipmentOptions;
                sections.Add(new("EQUIPMENT", gear.Count == 0 ? [new MusouRow("None", "His class carries no grenades or stimulants.", MusouRowKind.Fixed)]
                    : gear.Select((g, i) => new MusouRow(g.Title, i == 0 ? "Carried by default: the first of his class's equipment." : "Equipment his class carries.",
                        gear.Count == 1 ? MusouRowKind.Fixed : MusouRowKind.Choice, "equipment:" + (i == 0 ? "" : g.Name), Is(g.Name, chosen.Equipment, i))).ToList()));
                sections.Add(new("NOT YET", [new("Choice of powers", "His powers are fixed: there are no other powers to choose.", MusouRowKind.NotYet)]));
                return sections;
            }
            case 3:
            {
                if (picks.Perks.Count == 0) return [new("PERKS", [new("None", "His class offers no perks.", MusouRowKind.Fixed)])];
                MusouRow Perk((string Name, string Title, string Text) p, int slot, string? current, string? other)
                {
                    bool taken = other is not null && p.Name.Equals(other, StringComparison.OrdinalIgnoreCase);
                    string text = p.Text + (taken ? $" (Chosen as perk {(slot == 1 ? 2 : 1)}.)" : "")
                        + (slot == 1 && p.Name.Equals(picks.DefaultPerk, StringComparison.OrdinalIgnoreCase) ? " The class default perk." : "");
                    return new MusouRow(p.Title, text, MusouRowKind.Choice, $"perk{slot}:{p.Name}", current is not null && p.Name.Equals(current, StringComparison.OrdinalIgnoreCase));
                }
                var second = new List<MusouRow> { new("None", "No second perk.", MusouRowKind.Choice, "perk2:", chosen.Perk2 is null) };
                second.AddRange(picks.Perks.Select(p => Perk(p, 2, chosen.Perk2, picks.Perk1)));
                return
                [
                    new("PERK 1", picks.Perks.Select(p => Perk(p, 1, picks.Perk1, chosen.Perk2)).ToList()),
                    new("PERK 2", second),
                ];
            }
            default:
            {
                var rows = new List<MusouRow>
                {
                    new("Your active loadout's look", $"He wears your active Standard Marines loadout's Space Marine look ({lookOf}): chapter colours, helmet, left pad, arms, legs, lens and badges. "
                        + "His torso and belt, right pad and psychic hood are the Librarian's own, in that look's colours.", MusouRowKind.Choice, "look:", chosen.Look is null),
                };
                foreach (var (id, name) in picks.Looks)
                    rows.Add(new($"Look: {name}", $"He wears your saved look \"{name}\" (shared with every loadout that wears it: EDIT LOOK changes it for them too). His torso and belt, right pad and psychic hood are his own, in its colours.",
                        MusouRowKind.Choice, "look:" + id, chosen.Look == id));
                rows.Add(new("A new look of his own", "Makes a new look for him alone, starting as a copy of the one he wears now; EDIT LOOK paints it.", MusouRowKind.Choice, "look:new"));
                return [new("LOOK", rows)];
            }
        }
    }

    /// <summary>The Heretic placeholder's line under the header, heading and text.</summary>
    public const string HereticSummary = "HERETIC: a placeholder for a future Musou class.";
    public const string HereticHeading = "HERETIC: NOT YET AVAILABLE";
    public const string HereticText =
        "A placeholder for a future Musou class. There is no Heretic class, weapon, power or look yet: nothing here can be chosen or played.";

    /// <summary>What EDIT LOOK says when the active loadout is a DEFAULTS one (it wears the default look, which cannot be edited).</summary>
    public const string DefaultsLookNote =
        "Your active loadout is a DEFAULTS loadout: he wears the default look. Make a custom loadout active under Standard Marines to paint him.";
}

/// <summary>
/// The Librarian's loadout as the data has it, for the customizer's page: his class as <see cref="MusouLoadout.Prepare"/> appends it to a Musou config (so a change to his class there shows here by
/// itself), with names from the catalogue's display names (the client hands them in).
/// </summary>
/// <param name="Melee">His starting melee weapon (the first melee choice), or null.</param>
/// <param name="OtherMelee">His class's other melee choices.</param>
/// <param name="Guns">Every primary and pistol his class may carry.</param>
/// <param name="Equipment">His class's equipment choices (grenades, stimulants), the default first.</param>
public sealed record LibrarianLoadout(string? Melee, IReadOnlyList<string> OtherMelee, IReadOnlyList<string> Guns, IReadOnlyList<string> Equipment)
{
    /// <summary>What the page shows when the data has no Librarian (no Space Marine Tactical to copy).</summary>
    public static readonly LibrarianLoadout None = new(null, [], [], []);

    /// <summary>His class's melee weapons in choice order (the first his starting one) as (wargear name without its side prefix, title): what his own loadout chooses from (musou/loadout-slots).</summary>
    public IReadOnlyList<(string Name, string Title)> MeleeOptions { get; init; } = [];

    /// <summary>His class's equipment in choice order (the first his default) as (name without its side prefix, title).</summary>
    public IReadOnlyList<(string Name, string Title)> EquipmentOptions { get; init; } = [];

    /// <summary>His class's perks (wargear names) in choice order: the perks his own loadout may take.</summary>
    public IReadOnlyList<string> PerkNames { get; init; } = [];

    /// <summary>The title of a stored melee choice (null: his first); null when his class has none.</summary>
    public string? MeleeTitle(string? name) => Title(MeleeOptions, name);

    /// <summary>The title of a stored equipment choice (null: his first); null when his class has none.</summary>
    public string? EquipmentTitle(string? name) => Title(EquipmentOptions, name);

    static string? Title(IReadOnlyList<(string Name, string Title)> options, string? name)
    {
        if (options.Count == 0) return null;
        if (name is null) return options[0].Title;
        foreach (var o in options) if (o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return o.Title;
        return options[0].Title;   // a name his class does not have: his default is what he gets
    }

    /// <summary>The Librarian of <paramref name="config"/> (any config: it is prepared for Musou here), named by <paramref name="title"/> (a wargear name to its display name), or <see cref="None"/>.</summary>
    public static LibrarianLoadout Of(SimConfig config, Func<string, string> title)
    {
        var prepared = MusouLoadout.Prepare(config with { Extras = ContentExtras.Musou });
        if (prepared.Classes.FirstOrDefault(c => c.Name == LibrarianRules.ClassName) is not { } c) return None;
        string? Weapon(int i) => i >= 0 && i < prepared.Weapons.Count ? title(prepared.Weapons[i].Name) : null;
        var guns = c.PrimaryChoices.Append(c.Primary).Concat(c.PistolChoices).Append(c.Pistol).Select(Weapon).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var equipment = c.EquipmentChoices.Where(i => i >= 0 && i < prepared.Thrown.Count).Select(i => title(prepared.Thrown[i].Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var melee = c.MeleeChoices.Select(m => title(m.Wargear)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int index = prepared.Classes.ToList().IndexOf(c);
        var meleeOptions = c.MeleeChoices.Select(m => (LoadoutIndices.Neutral(m.Wargear), title(m.Wargear))).DistinctBy(o => o.Item1, StringComparer.OrdinalIgnoreCase).ToList();
        var gearOptions = c.EquipmentChoices.Where(i => i >= 0 && i < prepared.Thrown.Count).Select(i => (LoadoutIndices.Neutral(prepared.Thrown[i].Name), title(prepared.Thrown[i].Name)))
            .DistinctBy(o => o.Item1, StringComparer.OrdinalIgnoreCase).ToList();
        return new LibrarianLoadout(melee.FirstOrDefault(), melee.Skip(1).ToList(), guns, equipment)
        {
            MeleeOptions = meleeOptions, EquipmentOptions = gearOptions, PerkNames = LoadoutIndices.Names(prepared, index, "perk").Where(n => n.Length > 0).ToList(),
        };
    }
}

/// <summary>
/// The Librarian's powers as <see cref="LibrarianRules"/> holds them, for the customizer's text. A power is shown only while its numbers are in <see cref="LibrarianRules"/> (looked up by name, so a power
/// taken out there drops from the page and a changed number shows by itself, with no edit here; a power added there needs a line in <see cref="Table"/>). The buttons are the rules' binding today
/// (Fire, Grenade, Reload).
/// </summary>
public static class LibrarianPowers
{
    /// <summary>One power (or the stress scale) as shown: its name, row title, card text and whether it is a power (the stress scale is not).</summary>
    public sealed record Shown(string Name, string Title, string Text, bool IsPower);

    sealed record Clause(string Format, params string[] Constants);
    sealed record Power(string Name, string Button, string Key, bool IsPower, Clause[] Clauses);

    // The key constant decides whether the power exists; a clause shows only when all of its constants exist. "Name*100" shows a share as a percentage.
    static readonly Power[] Table =
    [
        new("Chain lightning", "Fire, hold", "LightningRange", true,
        [
            new("lightning arcs up to {0} m", "LightningRange"), new("jumps up to {0} m from Ork to Ork", "JumpRadius"), new("at most {0} targets", "MaxTargets"),
            new("{0} damage a second to every Ork in the chain", "PrimaryDps"), new("adds {0} stress a second", "LightningStressPerSecond"),
        ]),
        new("Warp Pull", "Grenade", "PullRange", true,
        [
            new("pulls Orks in a {0}-degree cone up to {1} m to {2} m of you", "PullConeDegrees", "PullRange", "PullStopDistance"), new("elites {0} % of the way, bosses not at all", "ElitePullShare*100"),
            new("adds {0} stress", "PullStress"), new("{0} s cooldown", "PullCooldown"),
        ]),
        new("Psychic Shockwave", "Reload", "ShockwaveRadius", true,
        [
            new("everything within {0} m takes {1} damage", "ShockwaveRadius", "ShockwaveDamage"), new("throws ordinary Orks {0} to {1} m", "ThrowMin", "ThrowMax"),
            new("adds {0} stress", "ShockwaveStress"), new("{0} s cooldown", "ShockwaveCooldown"),
        ]),
        new("Psychic Stress", "", "StressMax", false,
        [
            new("0 to {0}, shared by every power", "StressMax"), new("cools {0} a second after {1} s without casting", "CoolPerSecond", "CoolDelay"),
            new("at {0} every power locks until it is back to 0", "StressMax"),
        ]),
    ];

    /// <summary>A number of <see cref="LibrarianRules"/> by its name (a public constant or static field; "Name*100" scaled), or null when there is none.</summary>
    public static double? Value(string name)
    {
        double scale = 1;
        int star = name.IndexOf('*');
        if (star > 0) { scale = double.Parse(name[(star + 1)..], CultureInfo.InvariantCulture); name = name[..star]; }
        var field = typeof(LibrarianRules).GetField(name, BindingFlags.Public | BindingFlags.Static);
        if (field?.GetValue(null) is not { } v) return null;
        try { return Convert.ToDouble(v, CultureInfo.InvariantCulture) * scale; }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException) { return null; }
    }

    /// <summary>The powers (and the stress scale) the rules hold now, in the binding's order, with their numbers.</summary>
    public static IReadOnlyList<Shown> Present()
    {
        var list = new List<Shown>();
        foreach (var p in Table)
        {
            if (Value(p.Key) is null) continue;
            var parts = new List<string>();
            foreach (var clause in p.Clauses)
            {
                var values = clause.Constants.Select(Value).ToList();
                if (values.Any(v => v is null)) continue;
                parts.Add(string.Format(CultureInfo.InvariantCulture, clause.Format, values.Select(v => (object)v!.Value.ToString("0.##", CultureInfo.InvariantCulture)).ToArray()));
            }
            string text = parts.Count == 0 ? "" : char.ToUpperInvariant(parts[0][0]) + parts[0][1..] + string.Concat(parts.Skip(1).Select(x => "; " + x)) + ".";
            list.Add(new Shown(p.Name, p.Button.Length > 0 ? $"{p.Name}  ({p.Button})" : p.Name, text, p.IsPower));
        }
        return list;
    }
}
