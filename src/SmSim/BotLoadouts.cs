namespace SmSim;

/// <summary>
/// One of the owner's fixed Classic PvP bot loadouts (2026-11-07; **ours**: test bots are not the original's AI). Items are side-free wargear stems: a weapon,
/// melee combo or thrown item matches when its wargear name ends with <c>_mp_</c> + the stem (<c>sm_mp_ranged_sniper_rifle</c>, <c>csm_mp_ranged_sniper_rifle</c>),
/// so the same preset names the Chaos side's own item. A stem may list alternatives with <c>|</c> (the Space Marine name first, the Chaos one after:
/// the Thunder Hammer is the Raptor's Daemon Maul). Perks are perk file names (the same on both sides). Null: the class's fixed item (Tactical's and
/// Devastator's bolt pistol, which they do not choose).
/// </summary>
/// <param name="Role">The class role the preset is for (<see cref="ClassLoadout.Role"/>: tactical, assault, heavy).</param>
/// <param name="Key">The preset's letter in the owner's list (a, b, c).</param>
/// <param name="Text">The owner's words for it (logs and docs).</param>
public sealed record BotPreset(string Role, string Key, string Text, string? Primary, string? Second, string? Pistol, string? Melee, string Perk1, string Perk2, string Equipment);

/// <summary>A preset resolved against a class: choice-list indices as <c>ClientOptions</c> sends them (-1: the class default) and what could not be matched.</summary>
public sealed record BotLoadoutPick(BotPreset Preset, int PlayerClass, int Primary, int Second, int Pistol, int Melee, int Equipment, int Perk1, int Perk2, IReadOnlyList<string> Problems)
{
    /// <summary>Every item matched and the result is legal for the class (<see cref="BotLoadouts.Check"/>).</summary>
    public bool Legal => Problems.Count == 0;
}

/// <summary>
/// The owner's fixed bot loadouts (2026-11-07, verbatim in <c>docs/changes-from-original.md</c>): each Classic PvP bot picks one of its class role's presets at
/// random <b>once per match</b> (at its connect: the loadout goes in the join, so no protocol change and no re-send per life). Ours, a test aid.
/// </summary>
public static class BotLoadouts
{
    /// <summary>The owner's list. "Twin Link" (owner: "twink link") is Improved Twin-Link (<c>mp_perk_weap_storm_bolter_hip_fire</c>); combat stimulants are equipment.</summary>
    public static readonly IReadOnlyList<BotPreset> Presets =
    [
        new("tactical", "a", "stalker bolter, melta gun, Weapon Versatility, Larraman's Blessing, combat stimulants",
            "ranged_sniper_rifle", "ranged_melta", null, null, "mp_perk_secondary_weapon", "mp_perk_shield_recharge", "eq_stimpack"),
        new("tactical", "b", "plasma gun, bolter, Weapon Versatility, Bolter Targeter, frag grenades",
            "ranged_plasma_gun", "ranged_bolter", null, null, "mp_perk_secondary_weapon", "mp_perk_weap_bolter_accuracy", "eq_frag_grenade"),
        new("tactical", "c", "storm bolter, Twin Link, Weapon Versatility, melta gun, frag grenades",
            "ranged_storm_bolter", "ranged_melta", null, null, "mp_perk_weap_storm_bolter_hip_fire", "mp_perk_secondary_weapon", "eq_frag_grenade"),
        new("assault", "a", "power sword, bolt pistol, Air Cooled Thrusters, Impenetrable, frag grenades",
            null, null, "pistol_bolt_pistol", "melee_powersword", "mp_perk_jump_pack_reduced_cooldown", "mp_perk_ranged_damage_resistance", "eq_frag_grenade"),
        new("assault", "b", "chainsword, plasma pistol, Swordsman's Zeal, Impenetrable, combat stimulants",
            null, null, "pistol_plasma_pistol", "melee_chainsword", "mp_perk_weap_chainsword_heal_on_hit", "mp_perk_ranged_damage_resistance", "eq_stimpack"),
        new("assault", "c", "thunder hammer, plasma pistol, Air Cooled Thrusters, Impenetrable, frag grenades",
            null, null, "pistol_plasma_pistol", "melee_2hand_thunder_hammer|melee_2hand_power_mace", "mp_perk_jump_pack_reduced_cooldown", "mp_perk_ranged_damage_resistance", "eq_frag_grenade"),
        new("heavy", "a", "lascannon, bolt pistol, Iron Halo, Artificer Armour, frag grenades",
            "exotic_las_cannon", null, null, null, "mp_perk_iron_halo", "mp_perk_shield_regen_delay", "eq_frag_grenade"),
        new("heavy", "b", "plasma cannon, bolt pistol, Iron Halo, Artificer Armour, frag grenades",
            "exotic_plasma_cannon", null, null, null, "mp_perk_iron_halo", "mp_perk_shield_regen_delay", "eq_frag_grenade"),
    ];

    /// <summary>The presets of a role (empty for an unknown one, e.g. the Ork Boy's).</summary>
    public static IReadOnlyList<BotPreset> For(string role) => Presets.Where(p => string.Equals(p.Role, role, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Whether wargear <paramref name="name"/> is the item <paramref name="stem"/> names (one of its <c>|</c> alternatives, after <c>_mp_</c>).</summary>
    public static bool Matches(string name, string stem) =>
        stem.Split('|').Any(s => name.EndsWith("_mp_" + s, StringComparison.OrdinalIgnoreCase));

    /// <summary>The preset index for a draw <paramref name="draw"/> (any integer, e.g. from <see cref="Random.Next()"/>) over <paramref name="count"/> presets.</summary>
    public static int PickIndex(int draw, int count) => count <= 0 ? -1 : (int)((uint)draw % (uint)count);

    /// <summary>A random preset of the class's role resolved against <paramref name="c"/>; null when the role has none.</summary>
    public static BotLoadoutPick? Pick(SimConfig c, int playerClass, Random random)
    {
        if (Loadouts.ClassOf(c, playerClass) is not { } loadout) return null;
        var list = For(loadout.Role);
        if (list.Count == 0) return null;
        return Resolve(c, playerClass, list[PickIndex(random.Next(), list.Count)]);
    }

    /// <summary>
    /// <paramref name="preset"/> as choice indices of class <paramref name="playerClass"/>: each named item looked up in the class's own lists, and the
    /// result checked (<see cref="Check"/>). An item the class does not offer is a problem (never replaced by another one silently).
    /// </summary>
    public static BotLoadoutPick Resolve(SimConfig c, int playerClass, BotPreset preset)
    {
        var problems = new List<string>();
        var loadout = Loadouts.ClassOf(c, playerClass);
        if (loadout is null) problems.Add($"no class {playerClass}");
        else if (!string.Equals(loadout.Role, preset.Role, StringComparison.OrdinalIgnoreCase)) problems.Add($"class {loadout.Name} is role '{loadout.Role}', the preset is for '{preset.Role}'");
        int Weapon(WeaponSlot slot, string? stem, string what)
        {
            if (stem is null) return -1;
            var list = Loadouts.Choices(c, playerClass, slot);
            for (int i = 0; i < list.Count; i++) if (Matches(c.Weapons[list[i]].Name, stem)) return i;
            problems.Add($"{what} '{stem}' is not among the class's {slot} choices ({string.Join(", ", list.Select(k => c.Weapons[k].Name))})");
            return -1;
        }
        int primary = Weapon(WeaponSlot.Ranged, preset.Primary, "primary");
        int second = Weapon(WeaponSlot.Second, preset.Second, "second primary");
        int pistol = Weapon(WeaponSlot.Pistol, preset.Pistol, "pistol");
        int melee = -1;
        if (preset.Melee is { } m)
        {
            var list = loadout?.MeleeChoices ?? [];
            for (int i = 0; i < list.Count && melee < 0; i++) if (Matches(list[i].Wargear, m)) melee = i;
            if (melee < 0) problems.Add($"melee '{m}' is not among the class's melee choices ({string.Join(", ", list.Select(x => x.Wargear))})");
        }
        int equipment = -1;
        var items = Loadouts.EquipmentChoices(c, playerClass);
        for (int i = 0; i < items.Count && equipment < 0; i++) if (items[i] >= 0 && items[i] < c.Thrown.Count && Matches(c.Thrown[items[i]].Name, preset.Equipment)) equipment = i;
        if (equipment < 0) problems.Add($"equipment '{preset.Equipment}' is not among the class's equipment ({string.Join(", ", items.Select(k => k >= 0 && k < c.Thrown.Count ? c.Thrown[k].Name : "?"))})");
        int Perk(string name)
        {
            var list = Loadouts.PerkChoices(c, playerClass);
            for (int i = 0; i < list.Count; i++) if (string.Equals(c.Perks[list[i]].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            problems.Add($"perk '{name}' is not among the class's perks");
            return -1;
        }
        int perk1 = Perk(preset.Perk1), perk2 = Perk(preset.Perk2);
        var pick = new BotLoadoutPick(preset, playerClass, primary, second, pistol, melee, equipment, perk1, perk2, problems);
        if (problems.Count == 0) problems.AddRange(Check(c, pick));
        return pick;
    }

    /// <summary>
    /// What the server would change about <paramref name="pick"/> (<see cref="Loadouts.Choose"/>, the rule applied at every spawn): a perk dropped (two the
    /// same), the second primary refused (not a Tactical, no Weapon Versatility, the same as the primary), a pistol the perks forbid, or a fixed pistol that is
    /// not a bolt pistol. Empty: legal as asked.
    /// </summary>
    public static IReadOnlyList<string> Check(SimConfig c, BotLoadoutPick pick)
    {
        var problems = new List<string>();
        var s = Loadouts.Choose(c, new PlayerState(0, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, 0f, true) { Class = pick.PlayerClass }, pick.Primary, pick.Pistol, pick.Melee, pick.Equipment,
            pick.Perk1, pick.Perk2, pick.Second);
        var perks = Loadouts.PerkChoices(c, pick.PlayerClass);
        if (pick.Perk1 >= 0 && s.Perk1 != perks[pick.Perk1]) problems.Add($"perk 1 {c.Perks[perks[pick.Perk1]].Name} not kept");
        if (pick.Perk2 >= 0 && s.Perk2 != perks[pick.Perk2]) problems.Add($"perk 2 {c.Perks[perks[pick.Perk2]].Name} not kept");
        if (pick.Second >= 0)
        {
            int wanted = Loadouts.Choices(c, pick.PlayerClass, WeaponSlot.Second)[pick.Second];
            if (s.Second != wanted) problems.Add($"second primary {c.Weapons[wanted].Name} refused (Weapon Versatility, Tactical only)");
            else if (s.Second == s.Primary) problems.Add($"second primary {c.Weapons[wanted].Name} is the primary");
        }
        if (pick.Pistol >= 0 && s.Pistol != Loadouts.Choices(c, pick.PlayerClass, WeaponSlot.Pistol)[pick.Pistol]) problems.Add("pistol refused by the perks");
        if (pick.Preset.Pistol is null && pick.Preset.Text.Contains("bolt pistol", StringComparison.OrdinalIgnoreCase)
            && !(s.Pistol >= 0 && s.Pistol < c.Weapons.Count && c.Weapons[s.Pistol].Name.Contains("bolt_pistol", StringComparison.OrdinalIgnoreCase)))
            problems.Add("the class's fixed pistol is not a bolt pistol");
        return problems;
    }

    /// <summary>One line naming what a pick asks for, by wargear name (logs, <c>smtool bot-loadouts</c>).</summary>
    public static string Describe(SimConfig c, BotLoadoutPick pick)
    {
        string W(WeaponSlot slot, int i) => i < 0 ? "-" : c.Weapons[Loadouts.Choices(c, pick.PlayerClass, slot)[i]].Name;
        var loadout = Loadouts.ClassOf(c, pick.PlayerClass);
        var perks = Loadouts.PerkChoices(c, pick.PlayerClass);
        var items = Loadouts.EquipmentChoices(c, pick.PlayerClass);
        string fixedPistol = Loadouts.Weapon(c, pick.PlayerClass, WeaponSlot.Pistol)?.Name ?? "-";
        return $"{loadout?.Name ?? "?"} preset {pick.Preset.Key}: primary {W(WeaponSlot.Ranged, pick.Primary)}, second {W(WeaponSlot.Second, pick.Second)}, "
            + $"pistol {(pick.Pistol < 0 ? fixedPistol + " (fixed)" : W(WeaponSlot.Pistol, pick.Pistol))}, melee {(pick.Melee < 0 || loadout is null ? "default" : loadout.MeleeChoices[pick.Melee].Wargear)}, "
            + $"perks {(pick.Perk1 < 0 ? "-" : c.Perks[perks[pick.Perk1]].Name)} + {(pick.Perk2 < 0 ? "-" : c.Perks[perks[pick.Perk2]].Name)}, "
            + $"equipment {(pick.Equipment < 0 ? "-" : c.Thrown[items[pick.Equipment]].Name)}{(pick.Legal ? "" : "; PROBLEMS: " + string.Join("; ", pick.Problems))}";
    }
}
