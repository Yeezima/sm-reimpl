using SmSim.Extras.Warfront.Squad;

namespace SmSim.Extras.Warfront.Retainers;

/// <summary>A roster Marine's <see cref="SquadLoadout"/> (names) as the World's <see cref="LoadoutChoice"/> (indices into the class's lists): what the World player of a retainer is spawned with.</summary>
public static class RetainerLoadout
{
    /// <summary>
    /// The choice for <paramref name="l"/>: its class by name (the first Space Marine class when the config has none of that name), each weapon, the melee wargear and the perks by name in that class's lists;
    /// a name the class does not offer is -1, the class default. Equipment is the class default.
    /// </summary>
    public static LoadoutChoice ChoiceOf(SimConfig c, SquadLoadout l)
    {
        int cls = -1, first = -1;
        for (int i = 0; i < c.Classes.Count; i++)
        {
            if (c.Classes[i].Name == l.ClassId) { cls = i; break; }
            if (first < 0 && c.Classes[i].Side == Team.SpaceMarines) first = i;
        }
        if (cls < 0) cls = Math.Max(0, first);
        if (cls >= c.Classes.Count) return LoadoutChoice.Default;
        var k = c.Classes[cls];
        int Weapon(string name, IReadOnlyList<int> choices)
        {
            if (name.Length == 0) return -1;
            for (int i = 0; i < choices.Count; i++) if (choices[i] >= 0 && choices[i] < c.Weapons.Count && c.Weapons[choices[i]].Name == name) return i;
            return -1;
        }
        int melee = -1;
        for (int i = 0; i < k.MeleeChoices.Count && l.Melee.Length > 0; i++) if (k.MeleeChoices[i].Wargear == l.Melee) { melee = i; break; }
        int Perk(int n)
        {
            if (n >= l.Perks.Count) return -1;
            for (int i = 0; i < k.PerkChoices.Count; i++) if (k.PerkChoices[i] >= 0 && k.PerkChoices[i] < c.Perks.Count && c.Perks[k.PerkChoices[i]].Name == l.Perks[n]) return i;
            return -1;
        }
        return new LoadoutChoice(cls, Weapon(l.Primary, k.PrimaryChoices), Weapon(l.Secondary, k.PistolChoices), melee, -1, Perk(0), Perk(1));
    }
}
