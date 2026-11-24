using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

namespace SmNet.Tests.Extras.Warfront.WalkOver;

/// <summary>
/// Warfront's walk-over pickups (owner 2026-11-25: "make a change so that pickups are picked up just by walking over them"; "Warfront only", "Weapons stay on E"): a living human takes a powerup, a crate and an
/// ammunition box by standing over it, with no press and no hold; a weapon drop is not taken by walking and E still takes it; an AI Marine (a retainer) takes nothing; a box that would add nothing stays.
/// Musou Mode keeps the press and the hold.
/// </summary>
public class WarfrontWalkOverTests
{
    const int Human = TfwRig.Human;

    /// <summary>Clip-only weapons (no reserve: Musou Mode's endless ammunition fills a reserve every tick, so only a clip can want a box) and a box that adds rounds to both.</summary>
    static SimConfig WithBoxes(SimConfig c) => c with
    {
        Weapons = [c.Weapons[0] with { ReserveSize = 0 }, c.Weapons[1] with { ReserveSize = 0 }],
        Pickups = new PickupSpec([new PickupType("pickup_mp_drop_ammo", PickupKind.Ammo, 1, 4f, 25f, [30, 8], [])]),
    };

    static SimConfig NoWarfront(SimConfig c) => c with { Extras = c.Extras & ~ContentExtras.Warfront };

    static World Battle(bool warfront)
    {
        var config = WithBoxes(WarfrontBattleTests.WarfrontConfig);
        var w = TfwRig.War(null, warfront ? config : NoWarfront(config));
        TfwRig.Steps(w, 5);
        TfwRig.Deploy(w);
        TfwRig.Seconds(w, 1f);
        w.Musou!.Frozen = true;
        return w;
    }

    static Vector3 Feet(World w, int id = Human) => w.Players[id].Position;

    static PlayerInput Press => new(0, 0f, 0f, 0f, InputButtons.Interact);

    static MusouDropState StateOf(World w, ushort id) => w.MusouDrops.First(d => d.Id == id).State;

    static void Set(World w, int id, Func<PlayerState, PlayerState> change)
    {
        var players = (Dictionary<int, PlayerState>)typeof(World).GetField("_players", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(w)!;
        players[id] = change(players[id]);
    }

    static ushort Powerup => MusouPowerupRules.Item(MusouPowerup.DoubleDamage);

    [Fact]
    public void The_rules_are_ours_and_only_weapons_stay_on_the_press()
    {
        Assert.Equal(1.0f, WarfrontPickupRules.WalkOverMetres);
        Assert.Equal(1.0f, WarfrontPickupRules.WalkOverHeight);
        foreach (var kind in MusouPowerupRules.All) Assert.True(WarfrontPickupRules.WalksOver(MusouPowerupRules.Item(kind), crate: false));
        Assert.True(WarfrontPickupRules.WalksOver(0, crate: true));
        Assert.False(WarfrontPickupRules.WalksOver(0, crate: false));                      // a weapon
        Assert.False(WarfrontPickupRules.WalksOver(MusouDropItem.Melee(1), crate: false)); // a melee weapon
        Assert.True(WarfrontPickupRules.Over(new Vector3(0.9f, 0.3f, 0.8f), Vector3.Zero));
        Assert.False(WarfrontPickupRules.Over(new Vector3(1.1f, 0f, 0f), Vector3.Zero));
        Assert.False(WarfrontPickupRules.Over(new Vector3(0f, 0f, 1.2f), Vector3.Zero));
    }

    [Fact]
    public void In_Warfront_walking_over_a_powerup_takes_it_without_a_press_or_a_hold()
    {
        var w = Battle(warfront: true);
        var drop = w.MusouSpawnDrop(Human, Feet(w), Powerup)!.Value;
        w.Step(_ => default);
        Assert.Equal(MusouDropState.Picked, StateOf(w, drop.Id));
        Assert.True(w.MusouBuffActive(Human, MusouPowerup.DoubleDamage));
    }

    [Fact]
    public void In_Warfront_a_press_on_a_powerup_in_reach_but_not_underfoot_begins_no_hold_and_walking_on_takes_it()
    {
        var w = Battle(warfront: true);
        var drop = w.MusouSpawnDrop(Human, Feet(w) + new Vector3(1.3f, 0f, 0f), Powerup)!.Value;   // within Musou's 1.5 m press reach, outside the 1 m walk-over
        for (int k = 0; k < MusouPowerupRules.HoldTicks(w.Config.TickRate) + 5; k++) w.Step(_ => Press);
        Assert.Null(w.MusouHoldOf(Human));
        Assert.Equal(MusouDropState.Live, StateOf(w, drop.Id));
        Assert.True(w.TfwRespawnAt(Human, drop.Position + new Vector3(0.2f, 0f, 0f), 0f));
        w.Step(_ => default);
        Assert.Equal(MusouDropState.Picked, StateOf(w, drop.Id));
    }

    [Fact]
    public void In_Warfront_walking_over_a_crate_takes_it()
    {
        var w = Battle(warfront: true);
        var crate = w.MusouSpawnDrop(Human, Feet(w), 0, crate: true)!.Value;   // the class's own rifle: a found copy refills
        w.Step(_ => default);
        Assert.Equal(MusouDropState.Picked, StateOf(w, crate.Id));
    }

    [Fact]
    public void In_Warfront_a_weapon_drop_is_not_taken_by_walking_and_E_still_takes_it()
    {
        var w = Battle(warfront: true);
        var drop = w.MusouSpawnDrop(Human, Feet(w), 0)!.Value;
        TfwRig.Seconds(w, 1f);
        Assert.Equal(MusouDropState.Live, StateOf(w, drop.Id));
        w.Step(_ => Press);
        Assert.Equal(MusouDropState.Picked, StateOf(w, drop.Id));
    }

    [Fact]
    public void In_Warfront_walking_over_a_box_takes_it_when_it_adds_something_and_leaves_it_when_full()
    {
        var w = Battle(warfront: true);
        var box = w.SpawnPickup(0, Feet(w))!.Value;
        TfwRig.Steps(w, 10);
        Assert.Contains(w.LivePickups, p => p.Id == box.Id);   // full: nothing to add, it stays on the floor
        Set(w, Human, p => p with { Clip = 2 });   // the weapon in hand wants rounds
        long before = w.PickupEventsTotal;
        w.Step(_ => default);
        Assert.DoesNotContain(w.LivePickups, p => p.Id == box.Id);
        var e = Assert.Single(w.PickupEventLog.Skip(w.PickupEventLog.Count - (int)(w.PickupEventsTotal - before)));
        Assert.Equal((PickupEnd.Collected, Human), (e.End, e.Collector));
        Assert.True(e.Gain.Any);
    }

    [Fact]
    public void In_Warfront_an_AI_Marine_walking_over_a_powerup_or_a_box_takes_nothing()
    {
        var w = Battle(warfront: true);
        int puppet = TfwContracts.PuppetIdFirst + TfwContracts.PuppetIds - 1;
        w.Spawn(puppet, w.AssignClass(0));
        TfwRig.Steps(w, 2);
        Assert.True(w.Players[puppet].Alive);
        Set(w, puppet, p => p with { Clip = 2 });
        var drop = w.MusouSpawnDrop(puppet, Feet(w, puppet), Powerup)!.Value;
        var box = w.SpawnPickup(0, Feet(w, puppet))!.Value;
        long before = w.PickupEventsTotal;
        w.Step(_ => default);
        Assert.Equal(MusouDropState.Live, StateOf(w, drop.Id));
        Assert.False(w.MusouBuffActive(puppet, MusouPowerup.DoubleDamage));
        Assert.DoesNotContain(w.PickupEventLog.Skip(w.PickupEventLog.Count - (int)(w.PickupEventsTotal - before)), e => e.Collector == puppet);
        Assert.True(w.LivePickups.Any(p => p.Id == box.Id) || w.PickupEventLog.Any(e => e.Id == box.Id && e.Collector != puppet));
    }

    [Fact]
    public void In_Musou_Mode_walking_takes_nothing_and_the_hold_and_the_press_are_unchanged()
    {
        var w = Battle(warfront: false);
        var drop = w.MusouSpawnDrop(Human, Feet(w), Powerup)!.Value;
        Set(w, Human, p => p with { Clip = 2 });   // the weapon in hand wants rounds
        var box = w.SpawnPickup(0, Feet(w))!.Value;
        TfwRig.Steps(w, 10);
        Assert.Equal(MusouDropState.Live, StateOf(w, drop.Id));
        Assert.Contains(w.LivePickups, p => p.Id == box.Id);
        int need = MusouPowerupRules.HoldTicks(w.Config.TickRate);
        for (int k = 0; k < need - 1; k++) w.Step(_ => Press);
        Assert.Equal(MusouDropState.Live, StateOf(w, drop.Id));   // the hold has not run its time
        Assert.NotNull(w.MusouHoldOf(Human));
        w.Step(_ => Press);
        Assert.Equal(MusouDropState.Picked, StateOf(w, drop.Id));
        w.Step(_ => default);
        w.Step(_ => Press);   // a fresh press with no drop in reach takes the box (the core's rule)
        Assert.DoesNotContain(w.LivePickups, p => p.Id == box.Id);
    }
}
