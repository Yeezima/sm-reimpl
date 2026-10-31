using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou" (MU-D, owner 2026-10-31): the golden weapon drops in the simulation. A Nob's KO makes one drop spot where it fell; each living player
/// gets its own roll from its class's pool (<see cref="MusouDropRules.Pool"/>: the class's primaries, its other pistols, an Assault's other melee weapons;
/// never the spawn pistol); E within 1.5 m takes the nearest own drop and equips it (<see cref="World.MusouGiveItem"/>, the perks' clip and reserve); out of
/// reach nothing happens, another player's drop is untouched; a drop lies 60 s; nothing else drops; rolls are deterministic. Also the review's fixes: a join
/// is stripped at once (<see cref="World.MusouJoined"/>), a gift to a player not armed yet strips first, a gift's ammunition counts the perks.
/// </summary>
public class MusouDropsTests
{
    static readonly WeaponSpec BolterBase = new("bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Bolter = BolterBase with { Variants = [new(["mag"], BolterBase with { ClipSize = 40, ReserveSize = 320 })] };
    static readonly WeaponSpec Heavy = new("heavy_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 12f, 0.1f, 60, 240, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec BoltPistol = new("bolt_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly WeaponSpec PlasmaPistol = new("plasma_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 25f, 0.4f, 10, 40, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly WeaponSpec PlasmaGun = new("plasma_gun", WeaponSlot.Ranged, TriggerKind.SemiAuto, 30f, 0.3f, 20, 80, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);

    // Weapons: 0 bolter (a "mag" perk variant: 40 + 320), 1 heavy bolter, 2 bolt pistol, 3 plasma pistol, 4 plasma gun.
    // Classes: 0 Tactical (bolter, plasma gun; both pistols; the perk), 1 Devastator (heavy bolter; the bolt pistol only), 2 Assault (no primary; both
    // pistols; chainsword, hammer, axe), 3 a Chaos Tactical.
    static readonly SimConfig Raw = new()
    {
        Weapons = [Bolter, Heavy, BoltPistol, PlasmaPistol, PlasmaGun],
        Perks = [new PerkSpec("mp_perk_mag", "mag", PerkKind.Weapon, 1)],
        Classes =
        [
            new("sm - tactical", 0, 2, [Light]) { PrimaryChoices = [0, 4], PistolChoices = [2, 3], PerkChoices = [0], Side = Team.SpaceMarines, Role = "tactical" },
            new("sm - devastator", 1, 2, [Light]) { PistolChoices = [2], Side = Team.SpaceMarines, Role = "devastator" },
            new("sm - assault marine", -1, 2, [Light])
            {
                PistolChoices = [2, 3], Side = Team.SpaceMarines, Role = "assault",
                MeleeChoices = [new("melee_chainsword", [Light]), new("melee_2hand_thunder_hammer", [Light]), new("melee_power_axe", [Light])],
            },
            new("csm - tactical", 0, 2, [Light]) { PrimaryChoices = [0], Side = Team.Chaos, Role = "tactical" },
        ],
        Extras = ContentExtras.Musou,
    };
    static readonly SimConfig Prepared = MusouLoadout.Prepare(Raw with { MusouRules = MusouRules.Default with { PlayerDamageScale = 0f } });

    static World NewWorld(SimConfig? config = null)
    {
        var w = new World(config ?? Prepared) { Rules = MusouSimTests.Endless, StartAreas = MusouDirectorTests.Rooms };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;
        w.Musou!.Frozen = true;
        return w;
    }

    static PlayerInput Press => new(0, 0f, 0f, 0f, InputButtons.Interact);

    static List<MusouDrop> DropsOf(World w, int id)
    {
        var list = new List<MusouDrop>();
        w.MusouDropsOf(id, list);
        return list;
    }

    /// <summary>A Nob at <paramref name="at"/> killed by player 1 between ticks, then one tick.</summary>
    static ushort KillAt(World w, MusouEnemyType type, Vector3 at)
    {
        ushort id = w.Musou!.Spawn(type, at, 0f, 10f);
        Assert.NotEqual(0, id);
        Assert.True(w.Musou.Damage(id, 1, 1e6f, Vector3.Zero, out bool killed));
        Assert.True(killed);
        w.Step(_ => default);
        return id;
    }

    [Fact]
    public void A_nob_kill_makes_one_drop_spot_with_a_class_roll_for_every_living_player()
    {
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(0));
        w.Spawn(2, w.AssignClass(1));
        w.Spawn(3, w.AssignClass(2));
        w.Step(_ => default);
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Empty(w.MusouDrops);

        var spot = new Vector3(5f, 3f, 0f);
        ushort nob = KillAt(w, MusouEnemyType.Nob, spot);
        Assert.All(w.MusouDrops, d => Assert.Equal((spot, nob, MusouDropState.Live), (d.Position, d.Enemy, d.State)));
        // The Tactical: a primary of its list or the plasma pistol (never the bolt pistol it spawned with).
        Assert.Contains(Assert.Single(DropsOf(w, 1)).Item, new ushort[] { 0, 4, 3 });
        // The Devastator: its heavy bolter (its only pistol is the spawn pistol).
        Assert.Equal(1, Assert.Single(DropsOf(w, 2)).Item);
        // The Assault Marine: the plasma pistol or a melee weapon other than the default chainsword.
        Assert.Contains(Assert.Single(DropsOf(w, 3)).Item, new ushort[] { 3, MusouDropItem.Melee(1), MusouDropItem.Melee(2) });
        Assert.Equal(3, w.MusouDrops.Count);
        Assert.Equal(3, w.MusouDrops.Select(d => d.Id).Distinct().Count());

        // The pools as the rule lists them (the item carried in that slot left out when something else is left).
        var c = w.Config;
        Assert.Equal(new ushort[] { 0, 4, 3 }, MusouDropRules.Pool(c, w.Players[1]));
        Assert.Equal(new ushort[] { 1 }, MusouDropRules.Pool(c, w.Players[2]));
        Assert.Equal(new ushort[] { 3, MusouDropItem.Melee(1), MusouDropItem.Melee(2) }, MusouDropRules.Pool(c, w.Players[3]));
        Assert.True(w.MusouGiveWeapon(1, 4));
        Assert.Equal(new ushort[] { 0, 3 }, MusouDropRules.Pool(c, w.Players[1]));   // the plasma gun in hand: left out
        Assert.True(w.MusouGiveWeapon(2, 1));
        Assert.Equal(new ushort[] { 1 }, MusouDropRules.Pool(c, w.Players[2]));   // nothing else: the heavy bolter again

    }

    [Fact]
    public void Kills_of_other_enemies_never_drop()
    {
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => default);
        foreach (var type in new[] { MusouEnemyType.Boy, MusouEnemyType.ShootaBoy, MusouEnemyType.ArdBoy, MusouEnemyType.Gretchin, MusouEnemyType.ChaosMarine })
            KillAt(w, type, new Vector3(4f, 0f, 0f));
        for (int t = 0; t < 10; t++) w.Step(_ => default);
        Assert.Empty(w.MusouDrops);
        Assert.Equal([MusouEnemyType.Nob], MusouDropRules.DropFrom);
        // A Nob the stranded-enemy failsafe removes (a KO for nobody, by the world) drops nothing either.
        ushort id = w.Musou!.Spawn(MusouEnemyType.Nob, new Vector3(4f, 0f, 0f), 0f, 10f);
        w.Musou.Damage(id, 0, 1e6f, Vector3.Zero, out _, marker: 0, cause: MusouDeathKind.World);
        w.Step(_ => default);
        Assert.Empty(w.MusouDrops);
    }

    [Fact]
    public void E_in_reach_takes_the_own_drop_and_equips_it_with_the_perks_ammunition()
    {
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(0), perk1Choice: 0);   // the "mag" perk: the bolter's clip 40, reserve 320
        w.Spawn(2, w.AssignClass(0));
        w.Step(_ => default);
        var me = w.Players[1];
        Assert.Equal(WeaponSlot.Pistol, me.Slot);
        var drop = w.MusouSpawnDrop(1, me.Position + new Vector3(1.2f, 0.5f, 0f), 0)!.Value;
        var theirs = w.MusouSpawnDrop(2, me.Position + new Vector3(0.5f, 0f, 0f), 4)!.Value;   // the other player's, nearer: never mine
        uint version = w.MusouDropVersion(1);

        w.Step(id => id == 1 ? Press : default);
        me = w.Players[1];
        Assert.Equal((WeaponSlot.Ranged, "bolter", 40, 320), (me.Slot, Loadouts.InHand(w.Config, me)!.Name, me.Clip, me.Reserve));
        Assert.Equal((8, 64), (me.OtherClip, me.OtherReserve));   // the bolt pistol, holstered as it was
        Assert.Equal(MusouDropState.Picked, Assert.Single(DropsOf(w, 1)).State);
        Assert.Equal(drop.Id, DropsOf(w, 1)[0].Id);
        Assert.NotEqual(version, w.MusouDropVersion(1));
        Assert.Equal(theirs with { }, Assert.Single(DropsOf(w, 2)));   // untouched
        // The picked drop is listed a second for the wire, then goes.
        for (int t = 0; t < 31; t++) w.Step(_ => default);
        Assert.Empty(DropsOf(w, 1));
        Assert.Equal(MusouDropState.Live, Assert.Single(DropsOf(w, 2)).State);
        // A second press with nothing of its own in reach changes nothing (player 2's plasma gun lies 0.5 m away).
        w.Step(id => id == 1 ? Press : default);
        Assert.Equal("bolter", Loadouts.InHand(w.Config, w.Players[1])!.Name);
        Assert.Equal(MusouDropState.Live, Assert.Single(DropsOf(w, 2)).State);
    }

    [Fact]
    public void Out_of_reach_or_without_a_press_nothing_is_taken()
    {
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => default);
        var at = w.Players[1].Position;
        w.MusouSpawnDrop(1, at + new Vector3(1.6f, 0f, 0f), 0);   // 1.6 m flat
        w.MusouSpawnDrop(1, at + new Vector3(0f, 0f, 2f), 4);       // above, out of the 1.5 m height
        for (int t = 0; t < 5; t++) w.Step(_ => default);           // in reach of nothing, no press
        w.Step(id => Press);
        Assert.Equal(WeaponSlot.Pistol, w.Players[1].Slot);
        Assert.All(DropsOf(w, 1), d => Assert.Equal(MusouDropState.Live, d.State));
        Assert.True(World.MusouInReach(at + new Vector3(1.5f, 0f, 0f), at));
        Assert.False(World.MusouInReach(at + new Vector3(1.51f, 0f, 0f), at));
        // Held E does not take again: the press edge only (the core's interact rule).
        w.MusouSpawnDrop(1, at + new Vector3(0.5f, 0f, 0f), 4);
        w.Step(id => Press);   // still held from the last tick: no edge
        Assert.Equal(WeaponSlot.Pistol, w.Players[1].Slot);
        w.Step(_ => default);
        w.Step(id => Press);
        Assert.Equal("plasma_gun", Loadouts.InHand(w.Config, w.Players[1])!.Name);
    }

    [Fact]
    public void A_drop_lies_sixty_seconds_then_expires()
    {
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => default);
        var drop = w.MusouSpawnDrop(1, new Vector3(30f, 0f, 0f), 0)!.Value;
        Assert.Equal(drop.SpawnTick + 60u * (uint)w.Config.TickRate, drop.EndTick);
        while (w.Tick < drop.EndTick - 1) w.Step(_ => default);
        Assert.Equal(MusouDropState.Live, Assert.Single(DropsOf(w, 1)).State);
        w.Step(_ => default);
        Assert.Equal(MusouDropState.Expired, Assert.Single(DropsOf(w, 1)).State);
        for (int t = 0; t < 31; t++) w.Step(_ => default);
        Assert.Empty(w.MusouDrops);
        // An expired drop cannot be taken.
        var late = w.MusouSpawnDrop(1, w.Players[1].Position, 0)!.Value;
        while (w.Tick < late.EndTick) w.Step(_ => default);
        w.Step(id => Press);
        Assert.Equal(WeaponSlot.Pistol, w.Players[1].Slot);
    }

    [Fact]
    public void A_melee_drop_changes_an_assault_marines_combo()
    {
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(2));
        w.Step(_ => default);
        Assert.True(w.MusouGiveItem(1, MusouDropItem.Melee(1)));
        Assert.Equal(1, w.Players[1].MeleeChoice);
        Assert.Equal("melee_2hand_thunder_hammer", MusouDropItem.Wargear(w.Config, 2, MusouDropItem.Melee(1)));
        Assert.False(w.MusouGiveItem(1, MusouDropItem.Melee(9)));   // no such melee weapon
        Assert.False(w.MusouGiveItem(1, 0));                          // the bolter is not an Assault's
        // Mid-strike it waits (the drop stays).
        var striking = MusouLoadout.GiveItem(w.Config, w.Players[1] with { MeleeStep = 1 }, MusouDropItem.Melee(2));
        Assert.Null(striking);
        // Through E: the drop goes to the pistol slot or the melee slot.
        w.MusouSpawnDrop(1, w.Players[1].Position, MusouDropItem.Melee(2));
        w.Step(id => Press);
        Assert.Equal(2, w.Players[1].MeleeChoice);
        // A new life: back to the default.
        Assert.Equal(-1, MusouLoadout.Stripped(w.Config, w.Players[1]).MeleeChoice);
    }

    [Fact]
    public void Rolls_are_deterministic_and_independent_per_player()
    {
        static List<ushort> Run()
        {
            var w = NewWorld();
            w.Spawn(1, w.AssignClass(0));
            w.Spawn(2, w.AssignClass(0));
            w.Step(_ => default);
            for (int k = 0; k < 12; k++) KillAt(w, MusouEnemyType.Nob, new Vector3(k, 2f, 0f));
            return w.MusouDrops.OrderBy(d => d.Id).Select(d => d.Item).ToList();
        }
        var a = Run();
        Assert.Equal(a, Run());
        Assert.Equal(24, a.Count);
        var c = Prepared;
        var s = new PlayerState(1, default, default, 0f, true) { Alive = true, Class = 0, Team = Team.SpaceMarines, Primary = -1, Pistol = 2 };
        Assert.Equal(MusouDropRules.Roll(c, s, 100, 5, 1), MusouDropRules.Roll(c, s, 100, 5, 1));
        // Over many KOs every item of the pool comes up, and two players' rolls differ for some (independent).
        var seen = new HashSet<ushort>();
        int differ = 0;
        for (ushort e = 1; e <= 200; e++)
        {
            ushort one = MusouDropRules.Roll(c, s, 1000u + e, e, 1)!.Value, two = MusouDropRules.Roll(c, s, 1000u + e, e, 2)!.Value;
            seen.Add(one);
            if (one != two) differ++;
        }
        Assert.Equal(new HashSet<ushort> { 0, 4, 3 }, seen);
        Assert.InRange(differ, 80, 180);
    }

    [Fact]
    public void A_join_is_stripped_at_once_and_a_gift_before_the_strip_strips_first()
    {
        // The join gap: the server calls MusouJoined as a player joins; no tick runs with the class loadout.
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(0), primaryChoice: 1);
        Assert.NotNull(Loadouts.Weapon(w.Config, w.Players[1], WeaponSlot.Ranged));   // the core's spawn: the class loadout
        w.MusouJoined(1);
        var p = w.Players[1];
        Assert.Equal((WeaponSlot.Pistol, -1, 0), (p.Slot, Loadouts.WeaponIndex(w.Config, p, WeaponSlot.Ranged), p.Grenades));
        Assert.Equal("bolt_pistol", Loadouts.InHand(w.Config, p)!.Name);
        // A held trigger in the first tick fires the pistol, never a primary.
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire));
        Assert.All(w.LastShots, s => Assert.Equal(2, s.Weapon));

        // A gift to a player not armed yet (no tick, no MusouJoined): the strip comes first.
        var u = NewWorld();
        u.Spawn(2, u.AssignClass(0));
        u.Step(_ => default);   // the round is under way (a round's start arms everyone afresh)
        Assert.Equal(MatchState.Playing, u.State);
        u.Spawn(1, u.AssignClass(0), pistolChoice: 1);   // a mid-match join, asked for the plasma pistol
        Assert.True(u.MusouGiveWeapon(1, 4));
        p = u.Players[1];
        Assert.Equal("plasma_gun", Loadouts.InHand(u.Config, p)!.Name);
        Assert.Equal("bolt_pistol", Loadouts.Weapon(u.Config, p, WeaponSlot.Pistol)!.Name);   // the class pistol, not the requested one
        Assert.Equal((8, 64, 0), (p.OtherClip, p.OtherReserve, p.Grenades));
        u.Step(_ => default);
        Assert.Equal("plasma_gun", Loadouts.InHand(u.Config, u.Players[1])!.Name);   // and the next tick's arm keeps it

        // Outside Musou nothing.
        var plain = new World(Raw with { Extras = ContentExtras.None }) { Rules = MusouSimTests.Endless };
        plain.Spawn(1, plain.AssignClass(0));
        plain.MusouJoined(1);
        Assert.Equal("bolter", Loadouts.InHand(plain.Config, plain.Players[1])!.Name);
        Assert.Null(plain.MusouSpawnDrop(1, Vector3.Zero, 0));
    }

    [Fact]
    public void A_new_round_clears_the_drops_and_a_leaver_loses_its_own()
    {
        var w = NewWorld();
        w.Spawn(1, w.AssignClass(0));
        w.Spawn(2, w.AssignClass(0));
        w.Step(_ => default);
        w.MusouSpawnDrop(1, new Vector3(30f, 0f, 0f), 0);
        w.MusouSpawnDrop(2, new Vector3(30f, 0f, 0f), 0);
        w.Remove(2);
        w.Step(_ => default);
        Assert.Single(w.MusouDrops);
        Assert.Equal(1, w.MusouDrops[0].Player);
        // The cap: one over 16 live expires the oldest.
        for (int k = 0; k < MusouDropRules.MaxLivePerPlayer; k++) w.MusouSpawnDrop(1, new Vector3(30f, k, 0f), 0);
        Assert.Equal(MusouDropRules.MaxLivePerPlayer, DropsOf(w, 1).Count(d => d.State == MusouDropState.Live));
        Assert.Equal(MusouDropState.Expired, DropsOf(w, 1)[0].State);
    }
}
