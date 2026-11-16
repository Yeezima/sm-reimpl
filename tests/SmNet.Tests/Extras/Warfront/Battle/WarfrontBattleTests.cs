using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

namespace SmNet.Tests.Extras.Warfront.Battle;

/// <summary>Warfront W3: a node battle (D as a capture point, no Warboss, no victory wave), the leader-only deploy, the fallen human's spectator time and the wipe rule, the Ork scaling.</summary>
public class WarfrontBattleTests
{
    internal static readonly SimConfig WarfrontConfig = TfwRig.Config with { Extras = ContentProfiles.WarfrontProfile };

    internal static World War(int humans = 1)
    {
        var w = TfwRig.War(config: WarfrontConfig);
        for (int id = 2; id <= humans; id++) w.Spawn(id, w.AssignClass(0));
        TfwRig.Steps(w, 5);
        return w;
    }

    static int Count(World w, Func<MusouEnemy, bool> pick)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        return all.Take(n).Count(e => e.Alive && pick(e));
    }

    static void Stand(World w, int ticks)
    {
        for (int k = 0; k < ticks; k++) w.Step(_ => default);
    }

    /// <summary>Takes a position as TfwRig.Secure does, checking at every tick that no Warboss stands.</summary>
    static void Capture(World w, Vector3 at, Func<bool> done)
    {
        TfwRig.Teleport(w, at);
        for (int t = 0; t < 120 * 30 && !done(); t++)
        {
            TfwRig.KillAll(w);
            w.Step(_ => default);
            Assert.Equal(0, Count(w, e => e.Type == MusouEnemyType.Warboss));
        }
        Assert.True(done());
    }

    [Fact]
    public void The_run_carries_the_warfront_rules_and_the_battle_is_a_node()
    {
        var w = War();
        Assert.NotNull(w.Tfw!.Warfront);
        var battle = TfwRig.Deploy(w);
        Assert.True(battle.WarfrontNode);
        Assert.True(w.Tfw.Warfront!.InNode);
    }

    [Fact]
    public void A_node_is_A_B_C_then_D_and_securing_D_wins_it_with_no_warboss_no_wave_and_no_costs()
    {
        var w = War();
        var run = w.Tfw!;
        int transport = run.Transport, supplies = run.Supplies;
        (TfwLane, int, bool)? resolved = null;
        run.Warfront!.NodeResolved = (lane, node, won) => resolved = (lane, node, won);
        var battle = TfwRig.Deploy(w, TfwLane.B);
        Assert.Equal(transport, run.Transport);   // no deploy cost
        for (int k = 0; k < 3; k++)
        {
            Assert.Equal(TfwPositionState.Locked, battle.Objectives[TfwBattle.CommanderObjective].State);   // locked until the third
            int p = k;
            Capture(w, battle.Layout.Positions[p], () => battle.Objectives[p].State == TfwPositionState.Secured);
        }
        Assert.Equal(transport, run.Transport);    // the depot (B) pays nothing
        Assert.Equal(supplies, run.Supplies);
        Assert.Equal(TfwPositionState.Held, battle.Objectives[TfwBattle.CommanderObjective].State);   // D is capturable now
        Assert.Equal(0, battle.BossId);
        Assert.Equal(1, Count(w, e => e.Type == MusouEnemyType.BossNob && w.Musou!.Leashed(e.Id)));   // the garrison's Boss Nob
        Assert.True(Count(w, e => e.Type == MusouEnemyType.Nob && w.Musou!.Leashed(e.Id)) >= 2);
        Assert.False(battle.Won);
        Capture(w, battle.Layout.Hq, () => run.Phase == TfwRunPhase.Hub);
        Assert.Null(run.Battle);
        Assert.True(TfwRig.InStormbird(w.Players[TfwRig.Human].Position));   // back in the hub the same tick, no pickup phase
        Assert.Equal(0, run.PickupTicks);
        Assert.Equal(transport, run.Transport);
        Assert.Equal(supplies, run.Supplies);
        Assert.Equal((TfwLane.B, resolved?.Item2 ?? 0, true), resolved);
        Assert.True(resolved?.Item2 > 0);
        Assert.Equal(0, battle.VictoryWaveSent);   // no victory wave
    }

    [Fact]
    public void Only_the_leader_deploys_and_the_pickup_commands_do_nothing()
    {
        var w = War(humans: 2);
        var run = w.Tfw!;
        Assert.True(run.Enqueue(2, new TfwCommandRecord(1, TfwCommand.Deploy, (byte)TfwLane.A)));
        TfwRig.Seconds(w, TfwBattleRules.TravelSeconds + 0.5f);
        Assert.Equal(TfwRunPhase.Hub, run.Phase);   // a non-leader's deploy is dropped
        Assert.Null(run.Battle);
        var battle = TfwRig.Deploy(w);
        int supplies = run.Supplies, transport = run.Transport;
        ushort seq = 10;
        foreach (var command in new[] { TfwCommand.RequestPickup, TfwCommand.CancelPickup, TfwCommand.Redeploy, TfwCommand.ReturnToShip })
            Assert.True(run.Enqueue(TfwRig.Human, new TfwCommandRecord(seq++, command, 0)));
        TfwRig.Seconds(w, 1f);
        Assert.Equal(TfwRunPhase.Battle, run.Phase);
        Assert.Equal(0, run.PickupTicks);
        Assert.False(run.Recalling);
        Assert.Same(battle, run.Battle);
        Assert.Equal(supplies, run.Supplies);
        Assert.Equal(transport, run.Transport);
    }

    [Fact]
    public void The_war_clock_does_not_run()
    {
        var w = War();
        double clock = w.Tfw!.War.Clock;
        TfwRig.Seconds(w, 5f);
        Assert.Equal(clock, w.Tfw.War.Clock);
    }

    [Fact]
    public void No_puppet_retainer_lands()
    {
        var w = War();
        TfwRig.Deploy(w);
        Assert.Null(w.Tfw!.RetainerOf(TfwRig.Human));
    }

    static World Down(int humans, out TfwBattle battle, bool retainer = false)
    {
        var w = War(humans);
        w.Tfw!.Warfront!.AnyRetainerStanding = () => retainer;
        battle = TfwRig.Deploy(w);
        w.Musou!.Frozen = true;
        return w;
    }

    const int Window = 750, Spectate = 1800;

    [Fact]
    public void A_fallen_human_revived_inside_the_window_stands_again_and_never_spectates()
    {
        var w = Down(2, out _);
        Assert.True(w.TfwKillForTests(1));
        Stand(w, 300);
        Assert.Equal(WarfrontDownPhase.Downed, w.Tfw!.WarfrontDownState(1).Phase);
        Assert.True(w.Tfw.WarfrontRevive(w, 1));
        Assert.True(w.Players[1].Alive);
        Stand(w, Window + Spectate);
        Assert.True(w.Players[1].Alive);
        Assert.Equal(WarfrontDownPhase.None, w.Tfw.WarfrontDownState(1).Phase);
        Assert.False(w.Tfw.WarfrontRevive(w, 1));   // alive: nothing to revive
    }

    [Fact]
    public void A_fallen_human_not_revived_spectates_sixty_seconds_then_returns_to_the_landing_zone_while_a_teammate_stands()
    {
        var w = Down(2, out var battle);
        Assert.True(w.TfwKillForTests(1));
        Stand(w, Window - 20);
        Assert.Equal(WarfrontDownPhase.Downed, w.Tfw!.WarfrontDownState(1).Phase);
        Stand(w, 40);
        var state = w.Tfw.WarfrontDownState(1);
        Assert.Equal(WarfrontDownPhase.Spectator, state.Phase);
        Assert.InRange(state.SecondsLeft, 58f, 60f);
        Assert.False(w.Players[1].Alive);                 // no body in play
        Assert.False(w.Tfw.WarfrontRevive(w, 1));         // the window is over
        Stand(w, Spectate - 100);
        Assert.False(w.Players[1].Alive);
        Stand(w, 140);
        Assert.True(w.Players[1].Alive);
        Assert.Equal(TfwRunPhase.Battle, w.Tfw.Phase);
        Assert.True(Vector3.Distance(w.Players[1].Position, battle.Layout.LandingZone) < 12f);
        Assert.Equal(WarfrontDownPhase.None, w.Tfw.WarfrontDownState(1).Phase);
    }

    [Fact]
    public void A_solo_human_with_only_a_retainer_standing_spectates_and_returns()
    {
        var w = Down(1, out _, retainer: true);
        Assert.True(w.TfwKillForTests(1));
        Stand(w, Window + 40);
        Assert.Equal(WarfrontDownPhase.Spectator, w.Tfw!.WarfrontDownState(1).Phase);
        Assert.Equal(TfwRunPhase.Battle, w.Tfw.Phase);   // not a wipe: a retainer stands
        Stand(w, Spectate);
        Assert.True(w.Players[1].Alive);
        Assert.Equal(TfwRunPhase.Battle, w.Tfw.Phase);
    }

    [Fact]
    public void A_solo_human_with_no_retainer_is_wiped_when_the_window_closes()
    {
        var w = Down(1, out _);
        (TfwLane, int, bool)? resolved = null;
        w.Tfw!.Warfront!.NodeResolved = (lane, node, won) => resolved = (lane, node, won);
        int node = w.Tfw.Warfront.Node;
        Assert.True(w.TfwKillForTests(1));
        Stand(w, Window - 30);
        Assert.Equal(TfwRunPhase.Battle, w.Tfw.Phase);   // the window is still open: no wipe yet
        Stand(w, 60);
        Assert.Equal(TfwRunPhase.Hub, w.Tfw.Phase);
        Assert.Null(w.Tfw.Battle);
        Assert.True(w.Players[1].Alive);
        Assert.True(TfwRig.InStormbird(w.Players[1].Position));   // everyone to the hub
        Assert.Equal((TfwLane.A, node, false), resolved);
    }

    [Fact]
    public void Two_humans_down_with_no_retainer_are_wiped_when_the_last_window_closes_and_the_pending_respawn_is_dropped()
    {
        var w = Down(2, out _);
        Assert.True(w.TfwKillForTests(1));
        Stand(w, 300);
        Assert.True(w.TfwKillForTests(2));                // the first is 300 ticks into its window when the second falls
        Stand(w, Window - 300 + 40);                      // the first window has closed (spectator), the second is open
        Assert.Equal(WarfrontDownPhase.Spectator, w.Tfw!.WarfrontDownState(1).Phase);
        Assert.Equal(TfwRunPhase.Battle, w.Tfw.Phase);
        Stand(w, 300);                                    // the second window closes: nobody stands, nobody has a window: wipe; the first timer (about 55 s left) is discarded
        Assert.Equal(TfwRunPhase.Hub, w.Tfw.Phase);
        Assert.Equal(WarfrontDownPhase.None, w.Tfw.WarfrontDownState(1).Phase);
        Assert.True(w.Players[1].Alive && w.Players[2].Alive);
    }

    [Fact]
    public void Orks_have_twenty_percent_more_health_for_each_human_after_the_first()
    {
        Assert.Equal(1f, WarfrontBattleRules.PlayerScale(1));
        Assert.Equal(1.6f, WarfrontBattleRules.PlayerScale(4), 4);
        var w = War(humans: 4);
        var battle = TfwRig.Deploy(w);
        var rules = w.Tfw!.Warfront!;
        ushort a = w.Musou!.Spawn(MusouEnemyType.Boy, battle.Layout.LandingZone + new Vector3(30f, 0f, 0f), 0f, 100f);
        Assert.True(w.Musou.TryGet(a, out var boy));
        Assert.Equal(160f, boy.Health, 2);
        Assert.Equal(1f, rules.DamageScaleOf(a));
        rules.OrkLevelScale = () => (2f, 3f);   // the node's level changes mid-battle
        ushort b = w.Musou.Spawn(MusouEnemyType.Boy, battle.Layout.LandingZone + new Vector3(32f, 0f, 0f), 0f, 100f);
        Assert.True(w.Musou.TryGet(a, out boy));
        Assert.Equal(160f, boy.Health, 2);          // the living keep theirs
        Assert.Equal(1f, rules.DamageScaleOf(a));
        Assert.True(w.Musou.TryGet(b, out var later));
        Assert.Equal(320f, later.Health, 2);        // the new one takes the new level on top of the humans' 1.6
        Assert.Equal(3f, rules.DamageScaleOf(b));
        rules.OrkLevelScale = () => (1f, 1f);
        ushort boss = w.Musou.SpawnLeashed(MusouEnemyType.BossNob, battle.Layout.LandingZone + new Vector3(34f, 0f, 0f), 0f, 3000f, 1, 10f);   // a boss, by the leashed path: the same one multiplier
        Assert.True(w.Musou.TryGet(boss, out var bossNob));
        Assert.Equal(4800f, bossNob.Health, 2);
        Assert.Equal(1f, rules.DamageScaleOf(boss));   // a reused id is overwritten at each spawn
    }

    [Fact]
    public void The_damage_multiplier_reaches_the_player_through_the_Ork_that_took_it()
    {
        float Lost(float damageScale)
        {
            var w = War();
            var battle = TfwRig.Deploy(w);
            w.Tfw!.Warfront!.OrkLevelScale = () => (1f, damageScale);
            var p = w.Players[TfwRig.Human];
            float before = p.Health + p.Shield;
            w.Musou!.Spawn(MusouEnemyType.Boy, p.Position + new Vector3(0f, 1.2f, 0f), 0f, 100000f);
            Stand(w, 150);
            p = w.Players[TfwRig.Human];
            return before - (p.Health + p.Shield);
        }
        float one = Lost(1f), three = Lost(3f);
        Assert.True(one > 0f);
        Assert.True(three > one * 2f, $"{one} against {three}");
    }

    [Fact]
    public void A_garrison_Boss_Nob_at_four_humans_has_one_point_six_times_his_health_not_two_point_five_six()
    {
        var w = War(humans: 4);
        var battle = TfwRig.Deploy(w);
        for (int k = 0; k < 3; k++)
        {
            int p = k;
            Capture(w, battle.Layout.Positions[p], () => battle.Objectives[p].State == TfwPositionState.Secured);
        }
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        var boss = all.Take(n).Single(e => e.Alive && e.Type == MusouEnemyType.BossNob);
        Assert.Equal(w.Musou.Rules.Of(MusouEnemyType.BossNob).Health * 1.6f, boss.Health, 1);
    }

    [Fact]
    public void A_spectators_timer_that_ends_with_nobody_standing_waits_and_never_respawns_into_nothing()
    {
        var w = Down(2, out _);
        Assert.True(w.TfwKillForTests(1));
        Stand(w, Window + Spectate - 200);   // the first is a spectator, 200 ticks from its return
        Assert.True(w.TfwKillForTests(2));   // the only one standing falls: its window is open (750 ticks)
        Stand(w, 300);                       // the first one's timer ran out meanwhile: nobody stands, so it waits
        Assert.False(w.Players[1].Alive);
        Assert.Equal(WarfrontDownPhase.Spectator, w.Tfw!.WarfrontDownState(1).Phase);
        Assert.Equal(TfwRunPhase.Battle, w.Tfw.Phase);
        Stand(w, 600);                       // the second window closes: a wipe
        Assert.Equal(TfwRunPhase.Hub, w.Tfw.Phase);
    }

    [Fact]
    public void A_battle_left_by_every_human_reports_Left_not_a_loss_to_the_seam_and_the_level_scale_is_the_nodes_alone()
    {
        var w = War();
        var rules = w.Tfw!.Warfront!;
        rules.OrkLevelScale = () => (2f, 1f);
        ushort hub = w.Musou!.Spawn(MusouEnemyType.Boy, TfwTestArena.Hub.Spawns[0].Position, 0f, 100f);
        Assert.True(w.Musou.TryGet(hub, out var e));
        Assert.Equal(100f, e.Health, 2);   // the hub's Orks (the Firing Range) take no level scale
        TfwRig.Deploy(w);
        TfwMapResult? ended = null;
        rules.NodeEnded = (_, _, result) => ended = result;
        w.Remove(TfwRig.Human);   // the last human goes
        Stand(w, 3);
        Assert.Equal(TfwMapResult.Left, ended);
    }

    [Fact]
    public void One_human_scales_nothing_and_the_hub_scales_nothing()
    {
        var w = War();
        ushort hub = w.Musou!.Spawn(MusouEnemyType.Boy, TfwTestArena.Hub.Spawns[0].Position, 0f, 100f);
        Assert.True(w.Musou.TryGet(hub, out var e));
        Assert.Equal(100f, e.Health, 2);
        var battle = TfwRig.Deploy(w);
        ushort id = w.Musou.Spawn(MusouEnemyType.Boy, battle.Layout.LandingZone + new Vector3(30f, 0f, 0f), 0f, 100f);
        Assert.True(w.Musou.TryGet(id, out e));
        Assert.Equal(100f, e.Health, 2);
    }
}
