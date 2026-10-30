using System;
using System.Collections.Generic;
using System.Linq;
using SmContent;
using SmContent.Customization;
using SmSim;

namespace SmClient.Customizer.Model;

/// <summary>One choice in a list of the creator (a weapon, a grenade, a perk).</summary>
/// <param name="Id">The neutral wargear name (side prefix stripped; perks as they are): what a <see cref="CustomClass"/> stores.</param>
/// <param name="Fixed">The class carries this item and the player cannot change it ("Fixed").</param>
/// <param name="NotSimulated">The simulation does not run it yet: selectable and stored, but the server gets the default.</param>
/// <param name="Lock">Level / challenge / needs-perk state; <see cref="Unlock.Open"/> when free.</param>
/// <param name="Note">One line under the title (why it is fixed or unavailable, a warning); empty when none.</param>
public sealed record OptionRow(string Id, string Title, string Description, bool Selected, bool Fixed, bool NotSimulated, Unlock Lock, string Note)
{
    public bool Selectable => !Fixed && Lock.IsOpen;

    /// <summary>The words that mark the row's state (colour is never the only signal): SELECTED, FIXED, LOCKED, NOT SIMULATED YET.</summary>
    public IEnumerable<string> Tags
    {
        get
        {
            if (Selected) yield return Fixed ? "FIXED" : "SELECTED";
            else if (Fixed) yield return "FIXED";
            if (!Lock.IsOpen) yield return Lock.Tag;
            if (NotSimulated) yield return "NOT SIMULATED YET";
        }
    }
}

/// <param name="Key">"primary", "pistol", "melee" or "equipment".</param>
public sealed record OptionSection(string Key, string Heading, bool Selectable, IReadOnlyList<OptionRow> Rows, string? Hint);

/// <summary>
/// A perk row: <see cref="Slot"/> is 1 or 2 when the perk sits in that slot, else 0. <see cref="Status"/> is the simulation's own answer (<c>Perks.StatusOf</c>: Active,
/// Partial with the unsimulated part in its note, Inert = "NOT ACTIVE YET").
/// </summary>
public sealed record PerkRow(string Name, string Title, string Description, bool WeaponPerk, int Slot, PerkStatus Status, Unlock Lock, string Warning)
{
    /// <summary>Some of the perk's effect is simulated.</summary>
    public bool Active => Status.Activity != PerkActivity.Inert;

    /// <summary>The word the row carries for its simulation state: "" when fully active, "PARTLY ACTIVE", or "NOT ACTIVE YET".</summary>
    public string ActivityTag => Status.Activity switch { PerkActivity.Inert => PerkStatus.NotActiveLabel, PerkActivity.Partial => "PARTLY ACTIVE", _ => "" };

    /// <summary>One line for the card: what is not simulated (empty when fully active).</summary>
    public string ActivityNote => Status.Activity switch
    {
        PerkActivity.Inert => $"{PerkStatus.NotActiveLabel}: you can choose it, but nothing of it is simulated" + (Status.Note.Length > 0 ? $" ({Status.Note})." : "."),
        PerkActivity.Partial => $"Partly active: {Status.Note}.",
        _ => "",
    };
}

/// <summary>
/// The creator's lists, derived from the catalogue and a custom class (docs/customizer-design.md F5, engine-free). Every state is a word as well as a
/// colour. Items the class does not offer never appear; items the simulation does not run (stims, three melee weapons) are listed "NOT SIMULATED YET".
/// </summary>
public static class LoadoutRows
{
    public static readonly string[] SlotKeys = ["primary", "pistol", "melee", "equipment"];

    /// <summary>The class options of a role on the side shown (the item names carry that side's prefix). The Ork Boy's are the same on every side (<see cref="OrkLoadout.Options"/>).</summary>
    public static ClassOptions? Options(CustomizationCatalog catalog, string role, string race) =>
        OrkLoadout.IsOrk(role) ? OrkLoadout.Options(catalog) : catalog.Perks.ForRole(role, race);

    /// <summary>The item in effect for a slot: the custom class's choice (neutral name), else the class default.</summary>
    public static LoadoutItem? Effective(CustomizationCatalog catalog, ClassOptions options, string slot, CustomClass? custom)
    {
        var choices = options.For(slot);
        string? chosen = slot switch { "primary" => custom?.Primary, "pistol" => custom?.Pistol, "melee" => custom?.Melee, "equipment" => custom?.Equipment, _ => null };
        // By name, else the counterpart on this side (a class that stored Chaos's "chain_axe" shows the Space Marine's "power_axe" while the preview is on that side).
        if (catalog.Perks.Resolve(options, slot, chosen) is { } item) return item;
        return choices.Default;
    }

    /// <summary>The neutral names of the weapons the class carries (primary, pistol, melee in effect).</summary>
    public static IReadOnlyList<string> CarriedWeapons(CustomizationCatalog catalog, ClassOptions options, CustomClass? custom) =>
        new[] { "primary", "pistol", "melee" }.Select(s => Effective(catalog, options, s, custom)).OfType<LoadoutItem>().Select(i => ProfileWire.Neutral(i.Name)).ToList();

    public static string PrimaryHeading(string role) => role == "heavy" ? "HEAVY WEAPON" : "PRIMARY WEAPON";

    /// <summary>The weapon step: primary (or heavy), sidearm and melee.</summary>
    public static IReadOnlyList<OptionSection> Weapons(CustomizationCatalog catalog, string role, string race, CustomClass? custom, UnlockPolicy policy)
    {
        var sections = new List<OptionSection>();
        if (Options(catalog, role, race) is not { } options) return sections;
        var pistolPerks = custom is null ? [options.DefaultPerk?.Name] : new[] { custom.Perk(0) ?? options.DefaultPerk?.Name, custom.Perk(1) };   // slot 1 unset is the class default perk
        foreach (var key in new[] { "primary", "pistol", "melee" })
        {
            var choices = options.For(key);
            string heading = key switch { "primary" => PrimaryHeading(role), "pistol" => "SIDEARM", _ => "MELEE WEAPON" };
            if (choices.Items.Count == 0)
            {
                // The Assault class has no primary; the Devastator no melee weapon: say so instead of leaving a gap.
                string none = key == "primary" ? "None (jump pack class)" : "None";
                sections.Add(new OptionSection(key, heading, false, [new OptionRow("", none, "", true, true, false, Unlock.Open, "")], null));
                continue;
            }
            var effective = Effective(catalog, options, key, custom);
            var rows = new List<OptionRow>();
            for (int i = 0; i < choices.Items.Count; i++)
            {
                var item = choices.Items[i];
                var gate = Gate(item, catalog, policy, i, pistolPerks);
                rows.Add(new OptionRow(ProfileWire.Neutral(item.Name), item.DisplayName, item.Description, effective is not null && item.Name == effective.Name,
                    !choices.Selectable, !item.Simulated, gate, !item.Simulated ? "Sent to the server as the default until it is simulated" : ""));
            }
            sections.Add(new OptionSection(key, heading, choices.Selectable, rows, null));
        }
        // Protocol 44 (owner's decision of 2026-10-30): a Tactical that carries Weapon Versatility picks a second primary (key 3 in play); None is the default.
        if (role.Equals("tactical", StringComparison.OrdinalIgnoreCase) && custom is not null && LoadoutEdits.PerkIn(custom, options, SmSim.Loadouts.SecondWeaponPerk) && options.For("primary") is { Items.Count: > 0 } primaries)
        {
            var secondRows = new List<OptionRow> { new("", "None", "No second primary: key 3 does nothing.", custom.Second is null, false, false, Unlock.Open, "") };
            for (int i = 0; i < primaries.Items.Count; i++)
            {
                var item = primaries.Items[i];
                secondRows.Add(new OptionRow(ProfileWire.Neutral(item.Name), item.DisplayName, item.Description,
                    custom.Second is not null && ProfileWire.Neutral(item.Name).Equals(custom.Second, StringComparison.OrdinalIgnoreCase), false, !item.Simulated,
                    policy.Evaluate(i, item.Level, null), !item.Simulated ? "Sent to the server as no second primary until it is simulated" : ""));
            }
            sections.Add(new OptionSection("second", "SECOND PRIMARY (WEAPON VERSATILITY, KEY 3)", true, secondRows, null));
        }
        return sections;
    }

    /// <summary>The equipment step: grenades (frag, blind) and the stims (shown, not simulated yet).</summary>
    public static OptionSection Equipment(CustomizationCatalog catalog, string role, string race, CustomClass? custom, UnlockPolicy policy)
    {
        if (Options(catalog, role, race) is not { } options) return new OptionSection("equipment", "EQUIPMENT", false, [], null);
        var choices = options.Equipment;
        var effective = Effective(catalog, options, "equipment", custom);
        var rows = new List<OptionRow>();
        for (int i = 0; i < choices.Items.Count; i++)
        {
            var item = choices.Items[i];
            string title = EquipmentTitle(item);
            rows.Add(new OptionRow(ProfileWire.Neutral(item.Name), title, item.Description, effective is not null && item.Name == effective.Name,
                !choices.Selectable, !item.Simulated, Gate(item, catalog, policy, i, []), !item.Simulated ? "Sent to the server as the default until it is simulated" : ""));
        }
        return new OptionSection("equipment", "EQUIPMENT", choices.Selectable, rows, null);
    }

    /// <summary>The player-facing name of a thrown item: the blind grenade is "krak" in the files.</summary>
    public static string EquipmentTitle(LoadoutItem item) =>
        item.Name.Contains("krak", StringComparison.OrdinalIgnoreCase) && item.DisplayName.Equals(item.Name, StringComparison.OrdinalIgnoreCase) ? "Blind Grenades" : item.DisplayName;

    static Unlock Gate(LoadoutItem item, CustomizationCatalog catalog, UnlockPolicy policy, int index, IReadOnlyList<string?> perks)
    {
        if (item.RequiredPerk is { } need && !perks.Any(p => p is not null && p.Equals(need, StringComparison.OrdinalIgnoreCase)))
            return new Unlock(UnlockKind.NeedsPerk, 0, catalog.Perks.Perk(need)?.DisplayName ?? need);
        return policy.Evaluate(index, item.Level, null);
    }

    // ---- perks ----

    /// <summary>The perk lists of a role: class perks then weapon perks, each with the slot it sits in and a warning when its weapon is not carried.</summary>
    public static IReadOnlyList<PerkRow> Perks(CustomizationCatalog catalog, string role, string race, CustomClass? custom, UnlockPolicy policy, IReadOnlyDictionary<string, PerkStatus>? status = null)
    {
        if (Options(catalog, role, race) is not { } options) return [];
        var carried = CarriedWeapons(catalog, options, custom);
        var rows = new List<PerkRow>();
        int index = 0;
        foreach (var p in options.Perks.OrderBy(p => p.Kind == PerkKind.Weapon ? 1 : 0))
        {
            int slot = custom is null ? (options.DefaultPerk is { } d && Same(d.Name, p.Name) ? 1 : 0)
                : Same(custom.Perk(0) ?? options.DefaultPerk?.Name, p.Name) ? 1 : Same(custom.Perk(1), p.Name) ? 2 : 0;
            string warning = "";
            if (p.Kind == PerkKind.Weapon && PerkWeapons.Needs(p.Name) is { } needs && !needs.Any(n => carried.Contains(n, StringComparer.OrdinalIgnoreCase)))
                warning = $"Needs {PerkWeapons.Describe(needs, options)}; no effect without it";
            var activity = status is null ? new PerkStatus(PerkActivity.Active, "")   // no config yet: the row says nothing about it
                : status.TryGetValue(p.Name, out var known) ? known : new PerkStatus(PerkActivity.Inert, "the simulation has no such perk");   // a perk the config lacks does nothing
            // perks/finish: the locale's text, then the values line from the simulation's own specs (PerkValues; "Not active yet." when inert).
            string values = activity.Values is { Length: > 0 } v ? v : status is not null && activity.Activity == PerkActivity.Inert ? PerkValues.NotActive : "";
            rows.Add(new PerkRow(p.Name, p.DisplayName, PerkValues.Describe(p.Description, values), p.Kind == PerkKind.Weapon, slot, activity,
                policy.Evaluate(index++, p.Level, p.ChallengeText), warning));
        }
        return rows;
    }

    static bool Same(string? a, string? b) => a is not null && b is not null && ProfileWire.Neutral(a).Equals(ProfileWire.Neutral(b), StringComparison.OrdinalIgnoreCase);

    // ---- text ----

    /// <summary>"Tactical Marine - Bolter - Frag Grenades - Perk A, Perk B": the summary line of a slot.</summary>
    public static string Summary(CustomizationCatalog catalog, CustomClass custom, string race)
    {
        if (Options(catalog, custom.Role, race) is not { } options) return custom.Role;
        var parts = new List<string> { options.DisplayName };
        if (custom.Second is not null && options.For("primary").Items.FirstOrDefault(i => ProfileWire.Neutral(i.Name).Equals(custom.Second, StringComparison.OrdinalIgnoreCase)) is { } second)
            parts.Add("2nd " + second.DisplayName);
        foreach (var key in new[] { "primary", "pistol", "melee", "equipment" })
            if ((options.For(key).Selectable || OrkLoadout.IsOrk(custom.Role)) && Effective(catalog, options, key, custom) is { } item) parts.Add(key == "equipment" ? EquipmentTitle(item) : item.DisplayName);   // the Ork's fixed gear is all he has: named too
        var perks = new[] { custom.Perk(0) ?? options.DefaultPerk?.Name, custom.Perk(1) }.OfType<string>()
            .Select(n => catalog.Perks.Perk(n)?.DisplayName ?? n).ToList();
        if (perks.Count > 0) parts.Add(string.Join(", ", perks));
        return string.Join(" - ", parts);
    }

    /// <summary>The summary of a DEFAULTS loadout (the role's class defaults).</summary>
    public static string DefaultSummary(CustomizationCatalog catalog, string role, string race) =>
        Summary(catalog, LoadoutEdits.New(0, role, catalog, "Defaults"), race);

    /// <summary>The role's tab label with both sides' class names ("Tactical Marine / Chaos Space Marine").</summary>
    public static string RoleLabel(CustomizationCatalog catalog, string role)
    {
        string a = Options(catalog, role, CustomizationCatalog.SpaceMarine)?.DisplayName ?? role;
        string b = Options(catalog, role, CustomizationCatalog.Chaos)?.DisplayName ?? role;
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ? a : $"{a} / {b}";
    }

    public static string ClassName(CustomizationCatalog catalog, string role, string race) => Options(catalog, role, race)?.DisplayName ?? role;
}

/// <summary>
/// Whether each perk does anything in the simulation, from the same config the client plays with (<see cref="Perks.StatusOf"/>: Active, Partial with the unsimulated
/// part named, Inert = "NOT ACTIVE YET"). Before the perks of M43 every row said NOT ACTIVE YET, from an empty table that nobody filled when the perks came in; since
/// fix/customizer-weapons-perks the creator reads the real answer. Without a status (no config yet) a row says nothing about it.
/// </summary>
public static class PerkStatuses
{
    /// <summary>The status of every perk of <paramref name="config"/>, by wargear name.</summary>
    public static IReadOnlyDictionary<string, PerkStatus> Of(SimConfig config)
    {
        var map = new Dictionary<string, PerkStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var perk in config.Perks) map[perk.Name] = SmSim.Perks.StatusOf(config, perk) with { Values = PerkValues.Text(config, perk) };   // perks/finish
        return map;
    }
}

/// <summary>Which weapon a weapon perk belongs to (<c>mp_perk_weap_&lt;token&gt;_...</c>; docs/customizer-and-perks-data.md section 2.2).</summary>
public static class PerkWeapons
{
    static readonly (string Prefix, string[] Weapons)[] Table =
    [
        ("mp_perk_weap_bolt_pistol", ["mp_pistol_bolt_pistol", "mp_pistol_bolt_pistol_devastator"]),
        ("mp_perk_weap_plasma_pistol", ["mp_pistol_plasma_pistol"]),
        ("mp_perk_weap_chainsword", ["mp_melee_chainsword", "mp_melee_chainsword_gold"]),
        ("mp_perk_weap_power_axe", ["mp_melee_power_axe", "mp_melee_chain_axe"]),
        ("mp_perk_weap_power_sword", ["mp_melee_powersword"]),
        ("mp_perk_weap_thunder_hammer", ["mp_melee_2hand_thunder_hammer", "mp_melee_2hand_power_mace"]),
        ("mp_perk_weap_storm_bolter", ["mp_ranged_storm_bolter"]),
        ("mp_perk_weap_bolter", ["mp_ranged_bolter", "mp_ranged_bolter_gold"]),
        ("mp_perk_weap_stalker", ["mp_ranged_sniper_rifle"]),
        ("mp_perk_weap_gren_launcher", ["mp_ranged_grenade_launcher"]),
        ("mp_perk_weap_melta_gun", ["mp_ranged_melta"]),
        ("mp_perk_weap_plasma_gun", ["mp_ranged_plasma_gun"]),
        ("mp_perk_weap_heavy_bolter", ["mp_exotic_heavy_bolter"]),
        ("mp_perk_weap_las_cannon", ["mp_exotic_las_cannon"]),
        ("mp_perk_weap_plasma_cannon", ["mp_exotic_plasma_cannon"]),
    ];

    /// <summary>The neutral weapon names a weapon perk acts on; null for a class perk or an unknown one.</summary>
    public static string[]? Needs(string perkName)
    {
        foreach (var (prefix, weapons) in Table)
            if (perkName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase) || perkName.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return weapons;
        return null;
    }

    /// <summary>The display name of the first of <paramref name="neutralNames"/> the class lists, else a readable form of the first name.</summary>
    public static string Describe(string[] neutralNames, ClassOptions options)
    {
        foreach (var key in new[] { "primary", "pistol", "melee" })
            if (options.For(key).Items.FirstOrDefault(i => neutralNames.Contains(ProfileWire.Neutral(i.Name), StringComparer.OrdinalIgnoreCase)) is { } item) return item.DisplayName;
        string n = neutralNames[0];
        return n[(n.LastIndexOf('_') + 1)..];
    }
}
