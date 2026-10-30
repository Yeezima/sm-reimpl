using SmSim;

namespace SmContent;

/// <summary>
/// Wire indices for a loadout, by name (protocol 22). <b>The rule</b>: the indices a Hello or Setup carries index the simulation's lists of the
/// class (<see cref="Loadouts.Choices"/> for primary and pistol, <see cref="ClassLoadout.MeleeChoices"/>, <see cref="ClassLoadout.EquipmentChoices"/>,
/// <see cref="ClassLoadout.PerkChoices"/> of <see cref="SimConfig.Classes"/>), resolved by the item's wargear name; never positions in the
/// customizer's catalogue (<see cref="PerkCatalog"/>), which also lists items the simulation drops (unsimulated equipment or melee weapons), so its
/// positions differ. A client sends indices for the class it plays now on its current side; the server maps a class of the other side by role
/// and carries the indices over by position (<see cref="World.ClassForSide"/>).
/// </summary>
public static class LoadoutIndices
{
    /// <summary>The index of the class named <paramref name="key"/> in <see cref="SimConfig.Classes"/> (case-insensitive), or -1.</summary>
    public static int Class(SimConfig c, string key)
    {
        for (int i = 0; i < c.Classes.Count; i++) if (string.Equals(c.Classes[i].Name, key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// The index of the item named <paramref name="name"/> (a wargear file name, with or without its side prefix: <see cref="Neutral"/>) in the class's
    /// simulation list for <paramref name="slot"/> ("ranged" or "primary", "pistol", "melee", "equipment", "perk"); -1 (the default on the wire) when the
    /// name is null, unknown, not simulated or not the class's. The one way names become choice indices (the profile's <c>ProfileWire.ToLoadout</c> uses it).
    /// </summary>
    public static int Choice(SimConfig c, int playerClass, string slot, string? name)
    {
        if (name is null) return -1;
        var names = Names(c, playerClass, slot);
        string wanted = Neutral(name);
        for (int i = 0; i < names.Count; i++) if (string.Equals(Neutral(names[i]), wanted, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The wargear names of the class's simulation list for <paramref name="slot"/>, in choice order (empty for an unknown class).</summary>
    public static IReadOnlyList<string> Names(SimConfig c, int playerClass, string slot)
    {
        if (Loadouts.ClassOf(c, playerClass) is not { } loadout) return [];
        return slot switch
        {
            "ranged" or "primary" => Loadouts.Choices(c, playerClass, WeaponSlot.Ranged).Select(i => i >= 0 && i < c.Weapons.Count ? c.Weapons[i].Name : "").ToList(),
            "second" => Loadouts.Choices(c, playerClass, WeaponSlot.Second).Select(i => i >= 0 && i < c.Weapons.Count ? c.Weapons[i].Name : "").ToList(),   // protocol 44
            "pistol" => Loadouts.Choices(c, playerClass, WeaponSlot.Pistol).Select(i => i >= 0 && i < c.Weapons.Count ? c.Weapons[i].Name : "").ToList(),
            "melee" => loadout.MeleeChoices.Select(m => m.Wargear).ToList(),
            "equipment" => loadout.EquipmentChoices.Select(i => i >= 0 && i < c.Thrown.Count ? c.Thrown[i].Name : "").ToList(),
            "perk" => loadout.PerkChoices.Select(i => i >= 0 && i < c.Perks.Count ? c.Perks[i].Name : "").ToList(),
            _ => throw new ArgumentException($"Unknown slot '{slot}'.", nameof(slot)),
        };
    }

    /// <summary>
    /// A wargear name without its side prefix ("sm_mp_ranged_bolter" and "csm_mp_ranged_bolter" → "mp_ranged_bolter"): the form a custom class stores, so
    /// one class serves both sides. Names with neither prefix (perks) are unchanged.
    /// </summary>
    public static string Neutral(string wargear)
    {
        if (wargear.StartsWith("csm_", StringComparison.OrdinalIgnoreCase)) return wargear[4..];
        if (wargear.StartsWith("sm_", StringComparison.OrdinalIgnoreCase)) return wargear[3..];
        return wargear;
    }

    /// <summary>A whole loadout by names (null: the default) for the class named <paramref name="classKey"/> (unknown: -1, the server's choice).</summary>
    public static LoadoutChoice ToChoice(SimConfig c, string classKey, string? primary, string? pistol, string? melee, string? equipment, string? perk1, string? perk2)
    {
        int k = Class(c, classKey);
        return new LoadoutChoice(k, Choice(c, k, "ranged", primary), Choice(c, k, "pistol", pistol), Choice(c, k, "melee", melee),
            Choice(c, k, "equipment", equipment), Choice(c, k, "perk", perk1), Choice(c, k, "perk", perk2));
    }
}
