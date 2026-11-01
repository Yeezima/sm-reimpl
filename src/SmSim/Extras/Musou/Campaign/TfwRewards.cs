namespace SmSim.Extras.Musou.Campaign;

/// <summary>The numbers of Musou Mode's rewards (design section F; the chances and the slot rules are ours, tagged in the spec).</summary>
public static class TfwRewardRules
{
    /// <summary>The reward cards after a won map: switched off by the owner (2026-11-01: "temporarily disable the end of run rewards"). While false no offer is rolled or sent and the client has nothing to wait for; the code stays. Set it true to bring the cards back. The score panel is separate and stays.</summary>
    public static readonly bool Enabled = false;

    /// <summary>The chance a KO leaves an ammunition or equipment box (ours: the owner said "a chance"; the original's drop on every player death would bury the map at 100 Orks).</summary>
    public const float FodderBoxChance = 0.08f, EliteBoxChance = 0.40f;
    /// <summary>Extra primary slots (keys 3, 4, 5; owner's item 26).</summary>
    public const int ExtraSlots = 3, FirstExtraKey = 3;
    /// <summary>A box's dropper id: above every player id, so the log tells an enemy's box from a player's.</summary>
    public const int BoxDropperBase = 0x10000;

    /// <summary>Whether a KO of <paramref name="type"/> counts as an elite for the boxes.</summary>
    public static bool Elite(MusouEnemyType type) => type is MusouEnemyType.ArdBoy or MusouEnemyType.Nob or MusouEnemyType.ChaosMarine or MusouEnemyType.Warboss;

    /// <summary>Whether this KO leaves a box: a roll of its own (a hash of the tick and the enemy, not the core draw's seed), deterministic.</summary>
    public static bool Boxes(MusouEnemyType type, uint tick, ushort enemy)
    {
        float roll = (Pickups.Seed(tick, ~(int)enemy) >> 40) / (float)(1UL << 24);
        return roll < (Elite(type) ? EliteBoxChance : FodderBoxChance);
    }
}

/// <summary>
/// What a player has won this run with one class (design section F: "stored per run and per class"): the weapons in keys 3, 4 and 5 (weapon indices, -1 empty), and the equipment, perk and melee choices that
/// lie on top of what the customizer set (-1: none won). The customizer still sets the base.
/// </summary>
public sealed class TfwKit
{
    public readonly int[] Weapons = [-1, -1, -1];
    public int Equipment = -1, Perk = -1, Melee = -1;

    public bool Holds(int weapon) => Array.IndexOf(Weapons, weapon) >= 0;
}

/// <summary>
/// The reward cards and the kits of a run (owner's items 9, 11, 25, 26). The rules that need no World are here: what a crate or a card can hold for a player, and which slot a weapon goes to.
/// <see cref="World"/> applies them (<c>World.TfwArsenal.cs</c>).
/// </summary>
public sealed class TfwRewards
{
    /// <summary>Whether a won map offers cards (starts as <see cref="TfwRewardRules.Enabled"/>; tests of the cards turn it on).</summary>
    public bool Offering { get; set; } = TfwRewardRules.Enabled;
    readonly Dictionary<(int Player, int Class), TfwKit> _kits = new();
    readonly Dictionary<int, TfwRewardOffer> _offers = new();
    readonly Dictionary<int, int> _offerClass = new();
    ushort _sequence;

    /// <summary>A new run: no kit and no offer. Offer numbers run on, so a pick from the last run answers nothing.</summary>
    public void NewRun()
    {
        _kits.Clear();
        _offers.Clear();
        _offerClass.Clear();
    }

    /// <summary>The player's kit with <paramref name="playerClass"/>.</summary>
    public TfwKit Kit(int player, int playerClass)
    {
        if (!_kits.TryGetValue((player, playerClass), out var kit)) _kits[(player, playerClass)] = kit = new TfwKit();
        return kit;
    }

    /// <summary>The offer waiting for <paramref name="player"/>'s pick, if any.</summary>
    public TfwRewardOffer? Offer(int player) => _offers.TryGetValue(player, out var offer) ? offer : null;

    /// <summary>
    /// What a crate or a weapon card can hold for <paramref name="home"/> (a player as its own loadout stands): <see cref="MusouDropRules.Pool"/>, the class's own list, without what it owns already: its primary, its
    /// pistol, its second primary and the kit's weapons.
    /// </summary>
    public static List<ushort> Pool(SimConfig c, PlayerState home, TfwKit kit)
    {
        var pool = MusouDropRules.Pool(c, home);
        int primary = Loadouts.WeaponIndex(c, home, WeaponSlot.Ranged), pistol = Loadouts.WeaponIndex(c, home, WeaponSlot.Pistol), second = Loadouts.WeaponIndex(c, home, WeaponSlot.Second);
        int melee = Loadouts.MeleeChoiceOf(c, home);
        pool.RemoveAll(item => MusouDropItem.IsMelee(item) ? MusouDropItem.Index(item) == melee
            : MusouDropItem.Index(item) is var w && (w == primary || w == pistol || w == second || kit.Holds(w)));
        return pool;
    }

    /// <summary>One of <paramref name="pool"/> by <paramref name="roll"/>; null when it is empty.</summary>
    public static ushort? Pick(List<ushort> pool, ulong roll) => pool.Count > 0 ? pool[(int)(roll % (ulong)pool.Count)] : null;

    /// <summary>The perks a card can offer: the class's list without the two the player has and without Weapon Versatility, whose second primary is a customizer choice.</summary>
    static List<int> PerkPool(SimConfig c, PlayerState home)
    {
        var perks = new List<int>();
        int one = Loadouts.PerkIndex(c, home, 1), two = Loadouts.PerkIndex(c, home, 2);
        foreach (int p in Loadouts.PerkChoices(c, home.Class))
            if (p >= 0 && p < c.Perks.Count && p != one && p != two && !string.Equals(c.Perks[p].Name, Loadouts.SecondWeaponPerk, StringComparison.OrdinalIgnoreCase)) perks.Add(p);
        return perks;
    }

    /// <summary>
    /// A new offer for <paramref name="player"/>: one card of each kind (weapon, equipment, perk; a kind with nothing to give stays empty), rolled from a hash of the tick, the player and the offer's number. It replaces one
    /// still waiting. Null, and no offer, when no kind has anything to give.
    /// </summary>
    public TfwRewardOffer? Roll(SimConfig c, PlayerState home, TfwKit kit, uint tick, int player)
    {
        ushort next = (ushort)(_sequence == ushort.MaxValue ? 1 : _sequence + 1);
        ulong h = Pickups.Seed(tick, player * 31 + next);
        TfwRewardCard Card(TfwRewardKind kind, ushort? item) => item is { } i ? new TfwRewardCard(kind, i) : default;

        var weapon = Card(TfwRewardKind.Weapon, Pick(Pool(c, home, kit), h & 0xFFFF));
        var equipmentPool = Loadouts.EquipmentChoices(c, home.Class).Where(e => e != Loadouts.EquipmentIndex(c, home) && Loadouts.ValidEquipment(c, home.Class, e)).Select(e => (ushort)e).ToList();
        var equipment = Card(TfwRewardKind.Equipment, Pick(equipmentPool, (h >> 16) & 0xFFFF));
        var perk = Card(TfwRewardKind.Perk, Pick(PerkPool(c, home).Select(p => (ushort)p).ToList(), (h >> 32) & 0xFFFF));
        _offers.Remove(player);
        if (weapon.Kind == TfwRewardKind.None && equipment.Kind == TfwRewardKind.None && perk.Kind == TfwRewardKind.None) return null;
        _sequence = next;
        var offer = new TfwRewardOffer(next, weapon, equipment, perk);
        _offers[player] = offer;
        _offerClass[player] = home.Class;
        return offer;
    }

    /// <summary>
    /// The card a pick names: only for the offer standing now, so a late command for an older offer does nothing (M0's review). The offer is then gone. <paramref name="playerClass"/>: the class it was rolled for, whose kit
    /// takes it, whatever class the player has changed to in the Stormbird since: a card holds that class's weapon.
    /// </summary>
    public TfwRewardCard? Pick(int player, ushort offer, byte card, out int playerClass)
    {
        playerClass = _offerClass.GetValueOrDefault(player);
        if (!_offers.TryGetValue(player, out var open) || open.Sequence != offer) return null;
        var chosen = card switch { 0 => open.First, 1 => open.Second, 2 => open.Third, _ => default };
        if (chosen.Kind == TfwRewardKind.None) return null;
        _offers.Remove(player);
        return chosen;
    }

    /// <summary>The player with the kit's perk on top: it fills an empty second slot, else replaces the second, else (the second being Weapon Versatility, whose weapon must not vanish) the first.</summary>
    public static PlayerState WithPerk(SimConfig c, PlayerState s, int perk)
    {
        if (perk < 0) return s;
        int two = Loadouts.PerkIndex(c, s, 2);
        bool keepSecond = two >= 0 && string.Equals(c.Perks[two].Name, Loadouts.SecondWeaponPerk, StringComparison.OrdinalIgnoreCase);
        return keepSecond ? s with { Perk1 = perk } : s with { Perk2 = perk };
    }

    /// <summary>
    /// The extra slot (0 to 2, for keys 3 to 5) a new weapon takes: the first empty one, else the extra in hand (<paramref name="inHand"/>, -1 none), else the one last in hand (<paramref name="last"/>, -1 none), else the
    /// first. <paramref name="first"/>: a player with a second primary keeps key 3 for it, as the core does, so its slots start at 1.
    /// </summary>
    public static int SlotFor(TfwKit kit, int first, int inHand, int last)
    {
        for (int k = first; k < TfwRewardRules.ExtraSlots; k++) if (kit.Weapons[k] < 0) return k;
        if (inHand >= first) return inHand;
        return last >= first ? last : first;
    }
}
