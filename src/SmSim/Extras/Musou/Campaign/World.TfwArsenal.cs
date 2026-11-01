using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// A player's arms in Musou Mode (owner's items 3, 9, 11, 25 and 26; design section F, whose "MusouArm strips to the pistol" item 25 replaces). Every life starts with the full chosen loadout and the run's kit on top
/// (<see cref="TfwArmAll"/>), never the 5-wave run's strip. A picked-up weapon never replaces the primary: it goes in an extra slot, keys 3, 4 and 5. <see cref="PlayerState"/> keeps its three slots, so the core,
/// the snapshots and the prediction stay as they are: an extra in hand stands in the primary's or the pistol's field, by the list it comes from, and the player's own weapon of that field waits here in its bank
/// (<see cref="Arsenal"/>), with its ammunition. Keys 1 and 2 bring the own weapons back. Only a war reaches this file (<see cref="_tfw"/>; everything else goes through <c>World.MusouLoadout.cs</c> as before).
/// </summary>
public sealed partial class World
{
    /// <summary>What one weapon has left when it is not in the player's fields.</summary>
    readonly record struct Ammo(int Clip, int Reserve, float ReloadLeft, float Heat, bool Overheated, float SinceHeat);

    sealed class Arsenal
    {
        public readonly Ammo[] Extras = new Ammo[TfwRewardRules.ExtraSlots];
        public int HomePrimary = -1, HomePistol = -1;
        public Ammo PrimaryBank, PistolBank;
        /// <summary>The extra slot standing in each field (-1: the player's own weapon).</summary>
        public int LoadedPrimary = -1, LoadedPistol = -1;
        /// <summary>The extra slot last in hand (-1: none), and a key waiting for the end of a strike (0: none).</summary>
        public int LastInHand = -1, PendingKey;
    }

    readonly Dictionary<int, Arsenal> _tfwArsenals = new();
    long _tfwBoxesSeen;

    Arsenal ArsenalOf(int id)
    {
        if (!_tfwArsenals.TryGetValue(id, out var a)) _tfwArsenals[id] = a = new Arsenal();
        return a;
    }

    /// <summary>The player as its own loadout stands, with no extra in its fields.</summary>
    PlayerState TfwHome(int id, PlayerState p)
    {
        if (!_tfwArsenals.TryGetValue(id, out var a)) return p;
        if (a.LoadedPrimary >= 0) p = p with { Primary = a.HomePrimary };
        if (a.LoadedPistol >= 0) p = p with { Pistol = a.HomePistol };
        return p;
    }

    /// <summary>The whole loadout as a fresh life has it, with the kit on top: the class's default primary (the match config has taken it away for the 5-wave run), the won equipment, melee and perk.</summary>
    PlayerState TfwArmed(int id, PlayerState p)
    {
        var a = ArsenalOf(id);
        var kit = _tfw!.Rewards.Kit(id, p.Class);
        // An extra left in a field goes home; a field that holds something else took a new customizer choice at this respawn, which stands.
        if (a.LoadedPrimary >= 0 && p.Primary == kit.Weapons[a.LoadedPrimary]) p = p with { Primary = a.HomePrimary };
        if (a.LoadedPistol >= 0 && p.Pistol == kit.Weapons[a.LoadedPistol]) p = p with { Pistol = a.HomePistol };
        int primary = Loadouts.WeaponIndex(Config, p, WeaponSlot.Ranged);
        if (primary < 0) primary = MusouLoadout.DefaultPrimary(Config, p.Class);
        p = p with { Primary = primary };
        if (kit.Equipment >= 0) p = p with { Equipment = kit.Equipment };
        if (kit.Melee >= 0) p = p with { MeleeChoice = kit.Melee };
        p = Combat.Respawned(TfwRewards.WithPerk(Config, p, kit.Perk), Config);
        a.HomePrimary = p.Primary;
        a.HomePistol = p.Pistol;
        a.LoadedPrimary = a.LoadedPistol = -1;
        a.PendingKey = 0;
        for (int k = 0; k < kit.Weapons.Length; k++) a.Extras[k] = kit.Weapons[k] >= 0 ? TfwFull(p, kit.Weapons[k]) : default;
        return p;
    }

    /// <summary>A full clip and reserve of weapon <paramref name="weapon"/> as the player's perks change it.</summary>
    Ammo TfwFull(PlayerState p, int weapon)
    {
        var spec = Config.Weapons[weapon];
        if (spec.Variants.Count > 0) spec = spec.With(Loadouts.Upgrades(Config, p));
        return new Ammo(spec.ClipSize, spec.ReserveSize, 0f, 0f, false, 0f);
    }

    /// <summary>Every human alive and not armed this life is armed (in id order); the dead are forgotten so their next life is armed again. The AI Marines keep their class loadout.</summary>
    void TfwArmAll()
    {
        _musouArmIds.Clear();
        foreach (int id in _players.Keys) _musouArmIds.Add(id);
        _musouArmIds.Sort();
        foreach (int id in _musouArmIds)
        {
            if (TfwContracts.IsPuppet(id)) continue;
            var p = _players[id];
            if (!p.Alive) { _musouArmed.Remove(id); continue; }
            if (_musouArmed.Add(id)) _players[id] = TfwArmed(id, p);
        }
        _musouArmed.RemoveWhere(_musouGone ??= id => !_players.ContainsKey(id));
    }

    /// <summary>A new run: each player's own weapons back in its fields, the arsenals forgotten (the kits went with the run).</summary>
    void TfwForgetArsenals()
    {
        foreach (var id in _tfwArsenals.Keys) if (_players.TryGetValue(id, out var p)) _players[id] = TfwHome(id, p);
        _tfwArsenals.Clear();
    }

    // ---------------------------------------------------------------- ammunition by slot

    static Ammo AmmoOf(PlayerState s, WeaponSlot slot)
    {
        if (s.Slot == slot) return new Ammo(s.Clip, s.Reserve, s.ReloadLeft, s.Heat, s.Overheated, s.SinceHeat);
        if (Loadouts.Other(s.Slot) == slot) return new Ammo(s.OtherClip, s.OtherReserve, s.OtherReloadLeft, s.OtherHeat, s.OtherOverheated, s.OtherSinceHeat);
        return new Ammo(s.ThirdClip, s.ThirdReserve, s.ThirdReloadLeft, s.ThirdHeat, s.ThirdOverheated, s.ThirdSinceHeat);
    }

    static PlayerState WithAmmo(PlayerState s, WeaponSlot slot, Ammo a)
    {
        if (s.Slot == slot) return s with { Clip = a.Clip, Reserve = a.Reserve, ReloadLeft = a.ReloadLeft, Heat = a.Heat, Overheated = a.Overheated, SinceHeat = a.SinceHeat };
        if (Loadouts.Other(s.Slot) == slot) return s with { OtherClip = a.Clip, OtherReserve = a.Reserve, OtherReloadLeft = a.ReloadLeft, OtherHeat = a.Heat, OtherOverheated = a.Overheated, OtherSinceHeat = a.SinceHeat };
        return s with { ThirdClip = a.Clip, ThirdReserve = a.Reserve, ThirdReloadLeft = a.ReloadLeft, ThirdHeat = a.Heat, ThirdOverheated = a.Overheated, ThirdSinceHeat = a.SinceHeat };
    }

    /// <summary>The player with <paramref name="hand"/> in hand and each weapon's ammunition in the fields that slot asks for.</summary>
    static PlayerState Rehand(PlayerState s, WeaponSlot hand, Ammo primary, Ammo pistol, Ammo second)
    {
        var q = MusouLoadout.Calm(s) with { Slot = hand };
        return WithAmmo(WithAmmo(WithAmmo(q, WeaponSlot.Ranged, primary), WeaponSlot.Pistol, pistol), WeaponSlot.Second, second);
    }

    // ---------------------------------------------------------------- the keys

    /// <summary>The field an extra of <paramref name="weapon"/> stands in: the primary's when the class lists it there, else the pistol's.</summary>
    WeaponSlot FieldOf(int playerClass, int weapon) => Loadouts.ValidWeapon(Config, playerClass, WeaponSlot.Ranged, weapon) ? WeaponSlot.Ranged : WeaponSlot.Pistol;

    /// <summary>A weapon key (1 to 5; a client's SelectSlot): acts now, or when the strike under way ends (<see cref="MusouLoadout.Busy"/>). Key 3 of a player with a second primary stays the core's.</summary>
    internal void TfwSelectSlot(int id, byte key)
    {
        if (key is < 1 or > 5 || !_players.TryGetValue(id, out var p) || !p.Alive) return;
        if (MusouLoadout.Busy(p)) { ArsenalOf(id).PendingKey = key; return; }
        TfwApplyKey(id, p, key);
    }

    void TfwApplyKey(int id, PlayerState p, int key)
    {
        var a = ArsenalOf(id);
        a.PendingKey = 0;
        if (key <= 2) { _players[id] = TfwHomeInHand(a, p, key == 2 ? WeaponSlot.Ranged : WeaponSlot.Pistol); return; }
        var kit = _tfw!.Rewards.Kit(id, p.Class);
        int slot = key - TfwRewardRules.FirstExtraKey;
        if (kit.Weapons[slot] >= 0) _players[id] = TfwLoad(a, p, slot, kit.Weapons[slot]);
    }

    /// <summary>The player's own weapon of <paramref name="field"/> back in its field and in hand (nothing when no extra stands there: the core's press did it).</summary>
    PlayerState TfwHomeInHand(Arsenal a, PlayerState p, WeaponSlot field)
    {
        bool primary = field == WeaponSlot.Ranged;
        if ((primary ? a.LoadedPrimary : a.LoadedPistol) < 0) return p;
        return TfwUnload(a, p, field, hand: field);
    }

    /// <summary>The extra in <paramref name="field"/> goes to its slot and the own weapon comes back; <paramref name="hand"/> ends in hand.</summary>
    static PlayerState TfwUnload(Arsenal a, PlayerState p, WeaponSlot field, WeaponSlot hand)
    {
        Ammo primary = AmmoOf(p, WeaponSlot.Ranged), pistol = AmmoOf(p, WeaponSlot.Pistol), second = AmmoOf(p, WeaponSlot.Second);
        if (field == WeaponSlot.Ranged)
        {
            a.Extras[a.LoadedPrimary] = primary;
            primary = a.PrimaryBank;
            a.LoadedPrimary = -1;
            p = p with { Primary = a.HomePrimary };
        }
        else
        {
            a.Extras[a.LoadedPistol] = pistol;
            pistol = a.PistolBank;
            a.LoadedPistol = -1;
            p = p with { Pistol = a.HomePistol };
        }
        return Rehand(p, hand, primary, pistol, second);
    }

    /// <summary>Extra <paramref name="slot"/> (holding <paramref name="weapon"/>) into its field and in hand, what stood there going to its bank.</summary>
    PlayerState TfwLoad(Arsenal a, PlayerState p, int slot, int weapon)
    {
        var field = FieldOf(p.Class, weapon);
        Ammo primary = AmmoOf(p, WeaponSlot.Ranged), pistol = AmmoOf(p, WeaponSlot.Pistol), second = AmmoOf(p, WeaponSlot.Second);
        if (field == WeaponSlot.Ranged && a.LoadedPrimary != slot)
        {
            if (a.LoadedPrimary >= 0) a.Extras[a.LoadedPrimary] = primary; else a.PrimaryBank = primary;
            primary = a.Extras[slot];
            a.LoadedPrimary = slot;
            p = p with { Primary = weapon };
        }
        else if (field == WeaponSlot.Pistol && a.LoadedPistol != slot)
        {
            if (a.LoadedPistol >= 0) a.Extras[a.LoadedPistol] = pistol; else a.PistolBank = pistol;
            pistol = a.Extras[slot];
            a.LoadedPistol = slot;
            p = p with { Pistol = weapon };
        }
        a.LastInHand = slot;
        return Rehand(p, field, primary, pistol, second);
    }

    /// <summary>A key pressed during a strike, once the strike is over.</summary>
    void TfwSlotsStep()
    {
        foreach (var (id, a) in _tfwArsenals)
            if (a.PendingKey != 0 && _players.TryGetValue(id, out var p) && p.Alive && !MusouLoadout.Busy(p)) TfwApplyKey(id, p, a.PendingKey);
    }

    /// <summary>The slot of the extra the player holds now, or -1.</summary>
    static int ExtraInHand(Arsenal a, PlayerState p) => p.Slot == WeaponSlot.Ranged ? a.LoadedPrimary : p.Slot == WeaponSlot.Pistol ? a.LoadedPistol : -1;

    /// <summary>What tag 15 tells player <paramref name="id"/>.</summary>
    public TfwArsenalView TfwArsenalOf(int id)
    {
        if (_tfw is null || !_players.TryGetValue(id, out var p)) return TfwArsenalView.Empty;
        var w = _tfw.Rewards.Kit(id, p.Class).Weapons;
        _tfwArsenals.TryGetValue(id, out var a);
        static byte Key(int slot) => slot >= 0 ? (byte)(slot + TfwRewardRules.FirstExtraKey) : (byte)0;
        return new TfwArsenalView((short)w[0], (short)w[1], (short)w[2], Key(a?.LoadedPrimary ?? -1), Key(a?.LoadedPistol ?? -1));
    }

    // ---------------------------------------------------------------- taking a weapon

    /// <summary>
    /// A weapon found (a Nob's drop, a crate, a card): it never touches the hand or the primary; it goes in the first free extra slot. A weapon the player owns already refills. All slots full: it replaces the extra
    /// in hand, else the one last in hand, and stays in hand when the old one was. A melee item changes the combo, as in the 5-wave run. False when it cannot be given now (the drop stays).
    /// </summary>
    bool TfwTake(int id, ushort item)
    {
        if (!_players.TryGetValue(id, out var p) || !p.Alive) return false;
        var kit = _tfw!.Rewards.Kit(id, p.Class);
        if (MusouDropItem.IsMelee(item))
        {
            if (MusouLoadout.GiveItem(Config, p, item) is not { } given) return false;
            kit.Melee = MusouDropItem.Index(item);
            _players[id] = given;
            return true;
        }
        int weapon = MusouDropItem.Index(item);
        if (weapon >= Config.Weapons.Count || !(Loadouts.ValidWeapon(Config, p.Class, WeaponSlot.Ranged, weapon) || Loadouts.ValidWeapon(Config, p.Class, WeaponSlot.Pistol, weapon))) return false;
        var a = ArsenalOf(id);
        if (weapon == a.HomePrimary || weapon == a.HomePistol || weapon == Loadouts.SecondIndex(Config, p) || kit.Holds(weapon)) { TfwRefill(id, p, a, kit, weapon); return true; }
        int first = Loadouts.SecondIndex(Config, p) >= 0 ? 1 : 0;
        int inHand = ExtraInHand(a, p);
        int slot = TfwRewards.SlotFor(kit, first, inHand, a.LastInHand);
        bool full = kit.Weapons[slot] >= 0, replacesHand = full && slot == inHand;
        if (replacesHand && MusouLoadout.Busy(p)) return false;
        if (full && slot == a.LoadedPrimary) p = TfwUnload(a, p, WeaponSlot.Ranged, p.Slot);
        else if (full && slot == a.LoadedPistol) p = TfwUnload(a, p, WeaponSlot.Pistol, p.Slot);
        kit.Weapons[slot] = weapon;
        a.Extras[slot] = TfwFull(p, weapon);
        _players[id] = replacesHand ? TfwLoad(a, p, slot, weapon) : p;
        return true;
    }

    /// <summary>A found copy of a weapon owned: its clip and reserve full, wherever it is.</summary>
    void TfwRefill(int id, PlayerState p, Arsenal a, TfwKit kit, int weapon)
    {
        var full = TfwFull(p, weapon);
        for (int k = 0; k < kit.Weapons.Length; k++) if (kit.Weapons[k] == weapon && k != a.LoadedPrimary && k != a.LoadedPistol) a.Extras[k] = full;
        if (weapon == a.HomePrimary && a.LoadedPrimary >= 0) a.PrimaryBank = full;
        if (weapon == a.HomePistol && a.LoadedPistol >= 0) a.PistolBank = full;
        foreach (var slot in new[] { WeaponSlot.Ranged, WeaponSlot.Pistol, WeaponSlot.Second })
            if (Loadouts.WeaponIndex(Config, p, slot) == weapon) p = WithAmmo(p, slot, AmmoOf(p, slot) with { Clip = full.Clip, Reserve = full.Reserve });
        _players[id] = p;
    }

    // ---------------------------------------------------------------- crates and boxes

    /// <summary>A battle's weapon crates (owner's item 9): each site holds one roll per human from its class's list, seen by that human alone. A human with nothing left to find gets none.</summary>
    internal void TfwPlaceCrates(IReadOnlyList<Vector3> sites, IReadOnlyList<int> humans)
    {
        foreach (int id in humans)
        {
            if (!_players.TryGetValue(id, out var p)) continue;
            var pool = TfwRewards.Pool(Config, TfwHome(id, p), _tfw!.Rewards.Kit(id, p.Class));
            for (int k = 0; k < sites.Count; k++)
                if (TfwRewards.Pick(pool, Pickups.Seed(Tick, id * 31 + k)) is { } item) MusouSpawnDrop(id, sites[k], item, crate: true);
        }
    }

    /// <summary>A KO may leave an ammunition or equipment box (owner's item 3): the core's own pickup, drawn and collected by its own rules; only the chance is ours (<see cref="TfwRewardRules"/>).</summary>
    void TfwDropBox(MusouKill kill)
    {
        if (Config.Pickups is not { } spec || !PickupsActive || kill.Cause == MusouDeathKind.World || !TfwRewardRules.Boxes(kill.Type, Tick, kill.Enemy)) return;
        if (_musou!.TryGet(kill.Enemy, out var enemy)) DropPickup(spec, TfwRewardRules.BoxDropperBase + kill.Enemy, enemy.Position);
    }

    /// <summary>The core's <see cref="Pickups.Collect"/> fills the three slots in the player's fields: an ammunition box also reaches what waits in the bank, by the same amounts and caps.</summary>
    void TfwBoxAmmo()
    {
        long total = PickupEventsTotal;
        int fresh = (int)Math.Min(total - _tfwBoxesSeen, _pickupEventLog.Count);
        _tfwBoxesSeen = total;
        if (fresh <= 0 || Config.Pickups is not { } spec) return;
        for (int i = _pickupEventLog.Count - fresh; i < _pickupEventLog.Count; i++)
        {
            var e = _pickupEventLog[i];
            if (e.End != PickupEnd.Collected || !_tfwArsenals.TryGetValue(e.Collector, out var a) || !_players.TryGetValue(e.Collector, out var p)) continue;
            var type = spec.Types[e.Type];
            var kit = _tfw!.Rewards.Kit(e.Collector, p.Class);
            for (int k = 0; k < kit.Weapons.Length; k++)
                if (kit.Weapons[k] >= 0 && k != a.LoadedPrimary && k != a.LoadedPistol) a.Extras[k] = TfwTopUp(p, kit.Weapons[k], a.Extras[k], type);
            if (a.LoadedPrimary >= 0 && a.HomePrimary >= 0) a.PrimaryBank = TfwTopUp(p, a.HomePrimary, a.PrimaryBank, type);
            if (a.LoadedPistol >= 0 && a.HomePistol >= 0) a.PistolBank = TfwTopUp(p, a.HomePistol, a.PistolBank, type);
        }
    }

    /// <summary>The amount of <paramref name="type"/> for a weapon in the bank: into the reserve, or the clip of a weapon without one, capped (the rule of <see cref="Pickups.Collect"/>).</summary>
    Ammo TfwTopUp(PlayerState p, int weapon, Ammo ammo, PickupType type)
    {
        int amount = type.WeaponAmount(weapon);
        if (amount <= 0) return ammo;
        var full = TfwFull(p, weapon);
        return full.Reserve > 0
            ? ammo with { Reserve = Math.Clamp(ammo.Reserve + amount, ammo.Reserve, Math.Max(ammo.Reserve, full.Reserve)) }
            : ammo with { Clip = Math.Clamp(ammo.Clip + amount, ammo.Clip, Math.Max(ammo.Clip, full.Clip)) };
    }

    // ---------------------------------------------------------------- the cards

    /// <summary>After a won map: each human gets three cards (tag 14; it reaches the client in the Stormbird).</summary>
    internal void TfwOfferRewards(IReadOnlyList<int> humans)
    {
        foreach (int id in humans)
            if (_players.TryGetValue(id, out var p)) _tfw!.Rewards.Roll(Config, TfwHome(id, p), _tfw.Rewards.Kit(id, p.Class), Tick, id);
    }

    /// <summary>
    /// A client's pick (ChooseReward): only for the offer standing now. A weapon goes in a slot, an equipment, perk or melee choice on the kit, and the next tick arms the player afresh: the kit lies on top of its loadout.
    /// </summary>
    internal void TfwChooseReward(int id, TfwCommandRecord c)
    {
        var rewards = _tfw!.Rewards;
        if (!_players.TryGetValue(id, out var p) || rewards.Pick(id, c.Offer, c.Argument) is not { } card) return;
        var kit = rewards.Kit(id, p.Class);
        switch (card.Kind)
        {
            case TfwRewardKind.Weapon:
                if (MusouDropItem.IsMelee(card.Item)) kit.Melee = MusouDropItem.Index(card.Item);
                else
                {
                    var a = ArsenalOf(id);
                    int slot = TfwRewards.SlotFor(kit, Loadouts.SecondIndex(Config, p) >= 0 ? 1 : 0, ExtraInHand(a, p), a.LastInHand);
                    kit.Weapons[slot] = MusouDropItem.Index(card.Item);
                }
                break;
            case TfwRewardKind.Equipment: kit.Equipment = card.Item; break;
            case TfwRewardKind.Perk: kit.Perk = card.Item; break;
        }
        _musouArmed.Remove(id);
    }
}
