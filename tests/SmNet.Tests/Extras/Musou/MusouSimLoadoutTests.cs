using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou": the players' loadout (owner, 2026-10-31). Every life (the first spawn, a death-timer respawn, a round's respawn) starts with the
/// class's pistol alone: no primary (whatever the join asked for), no grenades, the default melee; the break's refill gives back neither; a weapon given at
/// runtime (<see cref="World.MusouGiveWeapon"/>, the entry point of the Nobs' drops) is put in hand with full ammunition, only from the class's own lists, and
/// stays until death. A config the match tuning did not prepare is left alone (the core would give the class primary anyway), and nothing of it reaches a
/// profile without Musou.
/// </summary>
public class MusouSimLoadoutTests
{
    static readonly WeaponSpec Bolter = new("bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Heavy = new("heavy_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 12f, 0.1f, 60, 240, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec BoltPistol = new("bolt_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly WeaponSpec PlasmaPistol = new("plasma_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 25f, 0.4f, 10, 40, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static readonly ProjectileSpec FragBall = new("ebps\\musou\\frag", 20f, 30f, "rigidbody", 0f, [new(5f, 0f), new(10f, 1f)], false, true, false, 1, 1f, 1.5f, 0, null, null);
    static readonly ThrownSpec Frag = new("frag", 3, 3, 1f, 10f, FragBall);

    // Weapons: 0 bolter, 1 heavy bolter, 2 bolt pistol, 3 plasma pistol. Tactical: bolter (heavy not his), both pistols; Devastator: heavy bolter.
    static readonly SimConfig Raw = new()
    {
        Weapons = [Bolter, Heavy, BoltPistol, PlasmaPistol],
        Thrown = [Frag],
        Classes =
        [
            new("sm - tactical", 0, 2, [Light]) { PrimaryChoices = [0], PistolChoices = [2, 3], EquipmentChoices = [0], Side = Team.SpaceMarines, Role = "tactical" },
            new("sm - devastator", 1, 2, [Light]) { PistolChoices = [2], EquipmentChoices = [0], Side = Team.SpaceMarines, Role = "devastator" },
            new("csm - tactical", 0, 2, [Light]) { PrimaryChoices = [0], Side = Team.Chaos, Role = "tactical" },
        ],
        Extras = ContentExtras.Musou,
    };
    static readonly SimConfig Prepared = MusouLoadout.Prepare(Raw with { MusouRules = MusouRules.Default with { PlayerDamageScale = 0f } });

    static World NewWorld(SimConfig config, MatchRules? rules = null)
    {
        var w = new World(config) { Rules = rules ?? MusouSimTests.Endless, StartAreas = MusouDirectorTests.Rooms };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;
        return w;
    }

    static void PistolOnly(SimConfig c, PlayerState p)
    {
        Assert.True(p.Alive);
        Assert.Equal(WeaponSlot.Pistol, p.Slot);
        Assert.Null(Loadouts.Weapon(c, p, WeaponSlot.Ranged));
        Assert.Null(Loadouts.Weapon(c, p, WeaponSlot.Second));
        Assert.Equal("bolt_pistol", Loadouts.InHand(c, p)!.Name);
        Assert.Equal((8, 64), (p.Clip, p.Reserve));
        Assert.Equal((0, 0), (p.OtherClip, p.OtherReserve));
        Assert.Equal(0, p.Grenades);
    }

    [Fact]
    public void Prepare_takes_the_default_primaries_away_and_keeps_the_class_lists()
    {
        Assert.True(MusouLoadout.Prepared(Prepared));
        Assert.All(Prepared.Classes, c => Assert.Equal(-1, c.Primary));
        Assert.Equal([0], Prepared.Classes[0].PrimaryChoices);
        Assert.Equal([1], Prepared.Classes[1].PrimaryChoices);   // the old default, the class's list when it had only that
        Assert.Equal(Prepared.Classes[0].PistolChoices, Raw.Classes[0].PistolChoices);
        // Any other profile: untouched.
        var other = Raw with { Extras = ContentExtras.All };
        Assert.Same(other, MusouLoadout.Prepare(other));
        Assert.False(MusouLoadout.Prepared(Raw));
    }

    [Fact]
    public void Every_life_starts_with_the_class_pistol_alone_in_the_space_marines_room()
    {
        var w = NewWorld(Prepared);
        // A Devastator asking for his heavy bolter and the plasma pistol, a Tactical asking for the bolter.
        w.Spawn(1, w.AssignClass(1), primaryChoice: 0, pistolChoice: 0);
        w.Spawn(2, w.AssignClass(0), primaryChoice: 0, pistolChoice: 1);
        w.Step(_ => default);
        foreach (var p in w.Players.Values)
        {
            PistolOnly(w.Config, p);
            Assert.True(MusouDirectorTests.InRoom(p.Position, MusouDirectorTests.Rooms[0]));
        }
        Assert.Equal(MatchState.Playing, w.State);

        // Death-timer respawns: armed again each time (killed by a Boy kept next to the player), the weapon given in the last life gone.
        int deaths = 0, respawns = 0;
        var hit = new World(Prepared with { RespawnTime = 1f, MusouRules = Prepared.MusouRules! with { PlayerDamageScale = 100f } }) { Rules = MusouSimTests.Endless, StartAreas = MusouDirectorTests.Rooms };
        Assert.True(hit.StartMusou(null));
        hit.MusouDirector!.Hold = true;
        hit.Spawn(5, hit.AssignClass(0), primaryChoice: 0);
        hit.Step(_ => default);
        PistolOnly(hit.Config, hit.Players[5]);
        Assert.True(hit.MusouGiveWeapon(5, 0));
        Assert.Equal("bolter", Loadouts.InHand(hit.Config, hit.Players[5])!.Name);
        for (int t = 0; t < 30 * 30 && respawns < 2; t++)
        {
            if (hit.Musou!.Alive == 0) hit.Musou.Spawn(MusouEnemyType.Boy, hit.Players[5].Position + new Vector3(0f, 1.2f, 0f), 0f, 85f);
            hit.Step(_ => default);
            deaths += hit.LastKills.Count;
            if (hit.LastRespawns.Contains(5))
            {
                respawns++;
                // The respawn tick's match step armed it, in the Space Marines' room; the bolter given last life is gone.
                PistolOnly(hit.Config, hit.Players[5]);
                Assert.True(MusouDirectorTests.InRoom(hit.Players[5].Position, MusouDirectorTests.Rooms[0]));
            }
        }
        Assert.Equal(2, respawns);
        Assert.True(deaths >= 2);
    }

    [Fact]
    public void A_given_weapon_goes_in_hand_with_full_ammunition_and_only_from_the_class_lists()
    {
        var w = NewWorld(Prepared);
        w.Spawn(1, w.AssignClass(0));
        w.Spawn(2, w.AssignClass(1));
        w.Step(_ => default);
        Assert.False(w.MusouGiveWeapon(1, 1));    // the heavy bolter is not a Tactical's
        Assert.False(w.MusouGiveWeapon(1, 9));    // no such weapon
        Assert.False(w.MusouGiveWeapon(7, 0));    // no such player
        PistolOnly(w.Config, w.Players[1]);

        Assert.True(w.MusouGiveWeapon(1, 0));
        var p = w.Players[1];
        Assert.Equal((WeaponSlot.Ranged, "bolter", 30, 270), (p.Slot, Loadouts.InHand(w.Config, p)!.Name, p.Clip, p.Reserve));
        Assert.Equal((8, 64), (p.OtherClip, p.OtherReserve));   // the pistol, holstered as it was
        Assert.True(w.MusouGiveWeapon(2, 1));
        Assert.Equal("heavy_bolter", Loadouts.InHand(w.Config, w.Players[2])!.Name);

        // A pistol of the class's list replaces the holstered one; in hand it replaces the one in hand.
        Assert.True(w.MusouGiveWeapon(1, 3));
        p = w.Players[1];
        Assert.Equal((WeaponSlot.Pistol, "plasma_pistol", 10, 40), (p.Slot, Loadouts.InHand(w.Config, p)!.Name, p.Clip, p.Reserve));
        Assert.Equal("bolter", Loadouts.Weapon(w.Config, p, WeaponSlot.Ranged)!.Name);
        Assert.Equal((30, 270), (p.OtherClip, p.OtherReserve));

        // It stays: later ticks do not strip it.
        for (int t = 0; t < 60; t++) w.Step(_ => default);
        Assert.Equal("bolter", Loadouts.Weapon(w.Config, w.Players[1], WeaponSlot.Ranged)!.Name);

        // Outside Musou the entry point does nothing.
        var plain = new World(Raw with { Extras = ContentExtras.None }) { Rules = MusouSimTests.Endless };
        plain.Spawn(1, plain.AssignClass(0));
        Assert.False(plain.MusouGiveWeapon(1, 0));
    }

    [Fact]
    public void The_break_refills_the_pistol_but_gives_back_no_stripped_weapon_or_grenade()
    {
        var tiny = Prepared.MusouRules! with { Waves = [new(2, 2, [(MusouEnemyType.Boy, 1f)]), new(2, 2, [(MusouEnemyType.Boy, 1f)])], BreakSeconds = 1f };
        var w = NewWorld(Prepared with { MusouRules = tiny });
        w.Spawn(1, w.AssignClass(0));
        w.Spawn(2, w.AssignClass(0));
        w.Step(_ => default);
        Assert.True(w.MusouGiveWeapon(2, 0));
        // Spend pistol ammunition (reloads take from the reserve).
        for (int t = 0; t < 30 * 8; t++) w.Step(id => id == 1 ? new PlayerInput(0, 0f, 0f, 0f, (t & 1) == 0 ? InputButtons.Fire : 0) : default);
        Assert.True(w.Players[1].Reserve < 64);
        w.MusouDirector!.Hold = false;
        var d = w.MusouDirector;
        Span<ushort> ids = stackalloc ushort[16];
        while (d.Phase == MusouPhase.Wave)
        {
            foreach (var id in ids[..w.Musou!.Query(Vector3.Zero, 1e6f, ids, 1e6f)]) w.Musou.Damage(id, 1, 1e6f, Vector3.Zero, out _);
            w.Step(_ => default);
        }
        Assert.Equal(MusouPhase.Break, d.Phase);
        var one = w.Players[1];
        Assert.Equal(64, one.Reserve);                               // the pistol's reserve back
        Assert.Null(Loadouts.Weapon(w.Config, one, WeaponSlot.Ranged));   // no primary given back
        Assert.Equal((0, 0), (one.OtherClip, one.OtherReserve));
        Assert.Equal(0, one.Grenades);
        Assert.Equal("bolter", Loadouts.InHand(w.Config, w.Players[2])!.Name);   // a given weapon stays and is refilled
        Assert.Equal(270, w.Players[2].Reserve);
    }

    [Fact]
    public void An_unprepared_config_is_left_alone()
    {
        var w = NewWorld(Raw with { MusouRules = MusouRules.Default with { PlayerDamageScale = 0f } });
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => default);
        Assert.Equal("bolter", Loadouts.InHand(w.Config, w.Players[1])!.Name);   // the core's class default: nothing to strip it with
    }
}
