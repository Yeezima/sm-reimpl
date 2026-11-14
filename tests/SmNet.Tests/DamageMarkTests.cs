using System.Numerics;
using SmClient.ImpactRules;
using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>onhit-damage-mark (protocol 71, owner 2026-11-13: "any sort of damage should trigger the sparks and glows"): every player's record carries a damage counter and the last damage's shield and health bits.</summary>
public class DamageMarkTests
{
    static PlayerState Player(int id) => new(id, new Vector3(id, 0, 1), Vector3.Zero, 0f, true) { Alive = true, Health = 100f, Shield = 100f, Team = Team.Chaos };

    [Fact]
    public void Every_damage_in_a_tick_raises_the_counter_once_and_the_bits_of_the_tick_are_ored()
    {
        var c = new SimConfig();
        var s = Player(3);
        var (hit, shield, health) = Combat.ApplyDamage(s, 10f, 1f, 1f, c);
        Assert.True(shield > 0f && health == 0f);
        Assert.Equal((1, true, false, true), ((int)hit.DamageCount, hit.DamageShield, hit.DamageHealth, hit.DamageFresh));
        // A second damage in the same tick (many pellets, many sources): the counter stays, the bits are ORed.
        var again = Combat.ApplyDamage(hit with { Shield = 0f }, 10f, 1f, 1f, c).After;
        Assert.Equal((1, true, true), ((int)again.DamageCount, again.DamageShield, again.DamageHealth));
        // The next tick (Combat.StepPlayer clears the fresh flag): a new count, only that tick's bits.
        var next = Combat.ApplyDamage(again with { DamageFresh = false }, 10f, 1f, 1f, c).After;
        Assert.Equal((2, false, true), ((int)next.DamageCount, next.DamageShield, next.DamageHealth));
        // A shield-breaking blow that spills into health: both bits at once.
        var both = Combat.ApplyDamage(Player(3) with { Shield = 4f }, 30f, 1f, 1f, c).After;
        Assert.True(both.DamageShield && both.DamageHealth);
    }

    [Fact]
    public void Many_damages_in_one_tick_cannot_wrap_the_counter_and_a_damage_that_took_nothing_leaves_the_mark()
    {
        var c = new SimConfig();
        var s = Player(3) with { Shield = 1000f, Health = 1000f };
        for (int pellet = 0; pellet < 20; pellet++) s = Combat.ApplyDamage(s, 1f, 1f, 1f, c).After;   // one tick
        Assert.Equal(1, s.DamageCount);
        var zero = Combat.ApplyDamage(Player(3) with { DamageCount = 4 }, 10f, 0f, 0f, c);   // zero multipliers: nothing taken
        Assert.Equal((0f, 0f, 4, false), (zero.ShieldDamage, zero.HealthDamage, (int)zero.After.DamageCount, zero.After.DamageFresh));
    }

    [Fact]
    public void The_counter_wraps_modulo_eight_and_no_damage_changes_nothing()
    {
        var c = new SimConfig();
        var s = Player(3) with { Shield = 1000f, Health = 1000f };
        for (int k = 0; k < 9; k++) s = Combat.ApplyDamage(s with { DamageFresh = false }, 1f, 1f, 1f, c).After;   // nine ticks
        Assert.Equal(1, s.DamageCount);
        var unchanged = Combat.ApplyDamage(s, 0f, 1f, 1f, c).After;
        Assert.Equal(s.DamageCount, unchanged.DamageCount);
        Assert.Equal(s.DamageCount, Combat.ApplyDamage(s with { Alive = false }, 5f, 1f, 1f, c).After.DamageCount);   // the dead take none
    }

    [Fact]
    public void The_mark_travels_in_every_record_and_unknown_bits_are_refused()
    {
        var a = Player(4) with { DamageCount = 5, DamageShield = true, DamageHealth = false };
        var b = Player(5) with { DamageCount = 2, DamageShield = false, DamageHealth = true };
        var own = Player(6) with { DamageCount = 7, DamageShield = true, DamageHealth = true };
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, [a, b, own], Self: 6));
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal((5, true, false), ((int)back.Players[0].DamageCount, back.Players[0].DamageShield, back.Players[0].DamageHealth));
        Assert.Equal((2, false, true), ((int)back.Players[1].DamageCount, back.Players[1].DamageShield, back.Players[1].DamageHealth));
        Assert.Equal((7, true, true), ((int)back.Players[2].DamageCount, back.Players[2].DamageShield, back.Players[2].DamageHealth));
        Assert.Equal(Protocol.Slim(a), back.Players[0]);

        // The mark is the byte after the evade byte of a slim record: 25 from the end of a one-record snapshot (24 bytes of blocks follow it); bits 6..7 are refused.
        var one = Protocol.Write(new Snapshot(1, 1, 1, [a]));
        Assert.Equal(5 | 8, one[^25]);
        var unknown = (byte[])one.Clone(); unknown[^25] |= 64;
        Assert.Null(Protocol.Read(unknown));
    }

    [Fact]
    public void The_protocol_is_71_the_slim_record_one_byte_longer_and_the_fullest_snapshot_still_fits()
    {
        Assert.Equal(72, Protocol.Version);
        Assert.Equal(48, Protocol.SlimRecordBytes);
        Assert.True(Protocol.MaxSnapshotBytes <= Protocol.SnapshotBudget);
        Assert.Equal(4, Protocol.MinSnapshotShots);   // the guaranteed shots stay; one projectile record went instead
        Assert.Equal(12, Protocol.MaxSnapshotProjectiles);
    }

    [Fact]
    public void The_client_rules_show_a_glow_for_a_change_on_a_living_known_player_only()
    {
        Assert.True(PlayerHitFxRules.MarkShowsGlow(known: true, wasAlive: true, nowAlive: true, wasCount: 3, nowCount: 4));
        Assert.True(PlayerHitFxRules.MarkShowsGlow(true, true, true, 7, 0));    // wrapped
        Assert.False(PlayerHitFxRules.MarkShowsGlow(true, true, true, 3, 3));   // no change
        Assert.False(PlayerHitFxRules.MarkShowsGlow(false, false, true, 0, 5)); // first sight
        Assert.False(PlayerHitFxRules.MarkShowsGlow(true, false, true, 6, 0));  // the record before was a corpse: a respawn (the counter is kept; the alive flags tell)
        Assert.False(PlayerHitFxRules.MarkShowsGlow(true, true, false, 3, 4));  // the killing blow: a corpse shows none
        Assert.False(PlayerHitFxRules.ArmourUpOfMark(shield: true, health: true));   // the shield broke and health took the rest: red
        Assert.True(PlayerHitFxRules.ArmourUpOfMark(true, false));
        Assert.False(PlayerHitFxRules.ArmourUpOfMark(false, true));
        Assert.Null(PlayerHitFxRules.ArmourUpOfMark(false, false));
    }

    [Fact]
    public void A_light_melee_hit_in_the_world_marks_its_victim_and_nobody_else()
    {
        // Player 1 swings at player 2 (nearest, 2 m ahead); player 3 stands behind him, player 4 beside the swing but out of the cone.
        var c = new SimConfig
        {
            MeleeSteps = [new MeleeStep(30.5f / 30f, 6.5f / 30f, 12.5f / 30f, 18.5f / 30f, 15.5f / 30f, 21.5f / 30f, 10f, 5f, 3.5f)],
            MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f, ShieldBodyMultiplier = 1f, BodyMultiplier = 1f,
        };
        var at = new[] { Vector3.Zero, new Vector3(0, 2, 0), new Vector3(0, -2, 0), new Vector3(3.6f, 1.5f, 0) };
        var world = new World(c, null, at.Select(p => new SpawnPoint(p, 0f)).ToArray());
        for (int i = 0; i < at.Length; i++) world.Spawn(i + 1);
        bool freshAtHit = false;
        for (int i = 0; i < 12; i++)
        {
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, i == 0 ? InputButtons.Melee : InputButtons.None, 0, 0) : new PlayerInput(0, 0, 0, 0, InputButtons.None));
            if (world.LastHits.Count > 0) freshAtHit = world.Players[2].DamageFresh;
        }
        Assert.Equal((1, true, false), ((int)world.Players[2].DamageCount, world.Players[2].DamageShield, world.Players[2].DamageHealth));   // a hit that only flinches (no reaction needed) is marked
        Assert.All(new[] { 1, 3, 4 }, id => Assert.Equal(0, world.Players[id].DamageCount));
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, world.Players.Values.OrderBy(p => p.PlayerId).ToArray()))));
        Assert.Equal(1, back.Players.Single(p => p.PlayerId == 2).DamageCount);
        Assert.True(freshAtHit);                                  // marked on the hit's tick
        Assert.False(world.Players[2].DamageFresh);               // Combat.StepPlayer cleared it at the start of a later tick
        Assert.Equal(1, world.Players[2].DamageCount);            // the counter stays
    }
}
