using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>How a Musou weapon drop ends (wire tag 6's state byte): it lies there, a player picked it up, or its time ran out.</summary>
public enum MusouDropState : byte { Live = 0, Picked = 1, Expired = 2 }

/// <summary>
/// One weapon drop of one player (owner, 2026-10-31: "each player sees weapons when 'boss' enemies die"; <see cref="World.MusouDrops"/>): its id, the player
/// it is for (only that player sees it and can pick it up), where it lies, what it holds (<see cref="MusouDropItem"/>), its state and its ticks.
/// </summary>
public readonly record struct MusouDrop(ushort Id, int Player, Vector3 Position, ushort Item, MusouDropState State, uint SpawnTick, uint EndTick, ushort Enemy = 0);

/// <summary>
/// The Musou drop rules (owner's request 2026-10-31, the lead's design; Musou only). Every value here is ours (kind: ours, to refine in a playtest).
/// </summary>
public static class MusouDropRules
{
    /// <summary>
    /// The enemies whose death drops a weapon: the owner's "boss" enemies, the Nobs and (the owner, 2026-10-31) the Chaos Marines; the Warboss's death
    /// ends the run.
    /// </summary>
    public static readonly IReadOnlyList<MusouEnemyType> DropFrom = [MusouEnemyType.Nob, MusouEnemyType.ChaosMarine];

    /// <summary>Whether a KO of <paramref name="type"/> drops (see <see cref="DropFrom"/>).</summary>
    public static bool Drops(MusouEnemyType type)
    {
        foreach (var t in DropFrom) if (t == type) return true;
        return false;
    }

    /// <summary>Pickup reach (ours, the lead: about 1.5 m): feet to the drop, horizontally; and at most <see cref="ReachHeight"/> above or below.</summary>
    public const float ReachMetres = 1.5f, ReachHeight = 1.5f;
    /// <summary>Seconds a drop lies there before it goes (ours, the lead: 60 s).</summary>
    public const float LifetimeSeconds = 60f;
    /// <summary>Seconds an ended drop (picked or expired) is still listed for the wire, so a lost message does not hide the end (ours).</summary>
    public const float EndedListedSeconds = 1f;
    /// <summary>Most live drops one player has at once (ours; one over expires the oldest).</summary>
    public const int MaxLivePerPlayer = 16;

    /// <summary>
    /// The items a drop for <paramref name="s"/> can hold (its class's carryable pool): the class's primaries, its pistols other than the class's own
    /// spawn pistol (and a pistol-slot bolter only with the perk that allows it), and its melee weapons other than the default (only a class that chooses
    /// among several, the Assault Marine and the Raptor). The item the player carries now in that slot is left out when anything else is left; empty when
    /// the class has nothing to give.
    /// </summary>
    public static List<ushort> Pool(SimConfig c, PlayerState s)
    {
        var all = new List<ushort>();
        var other = new List<ushort>();
        if (Loadouts.ClassOf(c, s.Class) is not { } loadout) return all;
        int primary = Loadouts.WeaponIndex(c, s, WeaponSlot.Ranged);
        foreach (int w in Loadouts.Choices(c, s.Class, WeaponSlot.Ranged))
        {
            if (w < 0 || w >= c.Weapons.Count || w >= MusouDropItem.MeleeFlag || !Loadouts.ValidWeapon(c, s.Class, WeaponSlot.Ranged, w)) continue;
            all.Add((ushort)w);
            if (w != primary) other.Add((ushort)w);
        }
        int spawnPistol = Loadouts.WeaponIndex(c, s.Class, WeaponSlot.Pistol);
        int pistol = Loadouts.WeaponIndex(c, s, WeaponSlot.Pistol);
        foreach (int w in Loadouts.Choices(c, s.Class, WeaponSlot.Pistol))
        {
            if (w < 0 || w >= c.Weapons.Count || w >= MusouDropItem.MeleeFlag || w == spawnPistol || !Loadouts.PistolPerkRule(c, s, w)) continue;
            all.Add((ushort)w);
            if (w != pistol) other.Add((ushort)w);
        }
        if (loadout.MeleeChoices.Count > 1)
        {
            int melee = Loadouts.MeleeChoiceOf(c, s);
            for (int k = 1; k < loadout.MeleeChoices.Count && k < MusouDropItem.MeleeFlag; k++)
            {
                all.Add(MusouDropItem.Melee(k));
                if (k != melee) other.Add(MusouDropItem.Melee(k));
            }
        }
        return other.Count > 0 ? other : all;
    }

    /// <summary>
    /// The item of the drop for <paramref name="player"/> from enemy <paramref name="enemy"/>'s death on <paramref name="tick"/>: one of
    /// <see cref="Pool"/>, uniformly (a hash of the three numbers: deterministic, independent per player); null when the pool is empty.
    /// </summary>
    public static ushort? Roll(SimConfig c, PlayerState s, uint tick, ushort enemy, int player)
    {
        var pool = Pool(c, s);
        if (pool.Count == 0) return null;
        ulong h = Mix(Mix(tick) ^ ((ulong)enemy * 0xD6E8FEB86659FD93UL) ^ ((ulong)(uint)player * 0xA0761D6478BD642FUL));
        return pool[(int)(h % (ulong)pool.Count)];
    }

    /// <summary>SplitMix64's finaliser.</summary>
    static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}

/// <summary>
/// A drop's item as one 16-bit number (wire tag 6): a weapon index (into <see cref="SimConfig.Weapons"/>) below <see cref="MeleeFlag"/>, else a melee
/// weapon, <see cref="MeleeFlag"/> plus its index in the class's <see cref="ClassLoadout.MeleeChoices"/>.
/// </summary>
public static class MusouDropItem
{
    public const ushort MeleeFlag = 0x8000;
    public static ushort Melee(int choice) => (ushort)(MeleeFlag | choice);
    public static bool IsMelee(ushort item) => (item & MeleeFlag) != 0;
    /// <summary>The weapon index, or the melee choice of a melee item.</summary>
    public static int Index(ushort item) => item & ~MeleeFlag & 0xFFFF;

    /// <summary>The item's wargear file name for a player of <paramref name="playerClass"/> (the weapon's name, or the melee combo's wargear), or null.</summary>
    public static string? Wargear(SimConfig c, int playerClass, ushort item)
    {
        int i = Index(item);
        if (!IsMelee(item)) return i < c.Weapons.Count ? c.Weapons[i].Name : null;
        return Loadouts.ClassOf(c, playerClass) is { } loadout && i < loadout.MeleeChoices.Count ? loadout.MeleeChoices[i].Wargear : null;
    }
}
