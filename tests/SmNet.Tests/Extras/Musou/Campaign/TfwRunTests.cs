using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>The run (TF4, design sections D and F): scripted through the real World, the AI Marines faked by <see cref="TfwRun.RetainerUp"/>.</summary>
public class TfwRunTests
{
    const int Human = TfwRig.Human;

    [Fact]
    public void A_run_starts_with_5_transport_and_4_supplies_and_shows_nothing_until_a_deploy()
    {
        var w = TfwRig.War();
        Assert.Equal((5, 4), (w.Tfw!.Transport, w.Tfw.Supplies));
        Assert.Equal(TfwRunPhase.Hub, w.Tfw.Phase);
        Assert.Equal(default, w.Musou!.Director);
    }

    [Fact]
    public void A_deploy_costs_1_takes_5_s_and_lands_a_fresh_pistol_life_at_the_landing_zone()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = 0, Primary = 0 }));
        TfwRig.Command(w, TfwCommand.Deploy, TfwLane.B);
        TfwRig.Steps(w, 2);
        Assert.Equal(TfwRunPhase.Travel, run.Phase);
        Assert.Equal(4, run.Transport);
        Assert.Equal(MusouPhase.Campaign, w.Musou!.Director.Phase);
        TfwRig.Seconds(w, TfwBattleRules.TravelSeconds - 0.3f);
        Assert.Equal(TfwRunPhase.Travel, run.Phase);
        TfwRig.Seconds(w, 0.4f);
        Assert.Equal(TfwRunPhase.Battle, run.Phase);
        Assert.Equal(TfwLane.B, run.War.BattleLane);
        Assert.Null(w.NextLoadout(Human));                                  // the customizer's Setup applied
        var p = w.Players[Human];
        Assert.True(Vector3.Distance(p.Position with { Z = 0f }, TfwMap.LandingZone) < 3f, $"{p.Position}");
        Assert.Equal((-1, WeaponSlot.Pistol), (p.Primary, p.Slot));
        Assert.Equal(6, w.TfwGuards!.SquadsAlive);                          // 7 at Imperial 100, at most 6 in the field
        Assert.Equal(run.Battle!.Layout.Positions[0], run.ActivePosition);
    }

    [Theory]
    [InlineData(TfwCommand.Deploy, 3)]   // no such lane
    [InlineData(TfwCommand.RequestPickup, 0)]
    [InlineData(TfwCommand.CancelPickup, 0)]
    public void Commands_out_of_place_do_nothing(TfwCommand command, byte argument)
    {
        var w = TfwRig.War();
        Assert.True(w.Tfw!.Enqueue(Human, new TfwCommandRecord(1, command, argument)));
        TfwRig.Steps(w, 2);
        Assert.Equal((TfwRunPhase.Hub, 5, 0), (w.Tfw.Phase, w.Tfw.Transport, w.Tfw.QueuedCommands));
    }

    [Fact]
    public void Orders_need_a_battle_and_their_points_stay_in_the_arena()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var seen = new List<TfwCommandRecord>();
        run.OrderSeen = (_, c) => seen.Add(c);
        run.Enqueue(Human, new TfwCommandRecord(1, TfwCommand.Hold, 0, new Vector3(1f, 2f, 0f)));
        TfwRig.Steps(w, 2);
        Assert.Empty(seen);                                                  // the hub: refused
        TfwRig.Deploy(w);
        run.Enqueue(Human, new TfwCommandRecord(2, TfwCommand.Attack, 0, new Vector3(3277f, -3277f, -3277f)));
        run.Enqueue(TfwContracts.PuppetIdFirst, new TfwCommandRecord(3, TfwCommand.Attack, 0, Vector3.Zero));   // nobody in the field
        TfwRig.Steps(w, 2);
        var order = Assert.Single(seen);
        Assert.Equal(new Vector3(TfwMap.ArenaHalf, -TfwMap.ArenaHalf, 0f), order.Point);
    }

    [Fact]
    public void A_flood_of_deploys_is_drained_each_tick_and_starves_no_order()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var seen = new List<TfwCommandRecord>();
        run.OrderSeen = (_, c) => seen.Add(c);
        TfwRig.Deploy(w);
        for (int k = 0; k < TfwRun.MaxQueuedCommands; k++) Assert.True(run.Enqueue(Human, new TfwCommandRecord((ushort)k, TfwCommand.Deploy, 1)));
        Assert.False(run.Enqueue(Human, new TfwCommandRecord(100, TfwCommand.Follow, 0)));   // full
        TfwRig.Steps(w, 1);
        Assert.Equal(0, run.QueuedCommands);
        Assert.True(run.Enqueue(Human, new TfwCommandRecord(101, TfwCommand.Follow, 0)));
        TfwRig.Steps(w, 1);
        Assert.Single(seen);
        Assert.Equal(TfwLane.A, run.War.BattleLane);
    }

    [Fact]
    public void A_deploy_in_the_field_or_from_outside_the_stormbird_is_refused()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        TfwRig.Deploy(w);
        TfwRig.Command(w, TfwCommand.Deploy, TfwLane.B);
        TfwRig.Steps(w, 2);
        Assert.Equal((TfwRunPhase.Battle, TfwLane.A, 4), (run.Phase, run.War.BattleLane, run.Transport));
        var fresh = TfwRig.War();
        TfwRig.Teleport(fresh, TfwMap.LandingZone);
        TfwRig.Command(fresh, TfwCommand.Deploy);
        TfwRig.Steps(fresh, 2);
        Assert.Equal((TfwRunPhase.Hub, 5), (fresh.Tfw!.Phase, fresh.Tfw.Transport));
    }

    [Fact]
    public void A_battle_goes_p1_p2_p3_the_sources_stop_the_commander_falls_and_the_battle_is_won()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var battle = TfwRig.Deploy(w);
        Assert.All(Enumerable.Range(0, 4), s => Assert.True(battle.SourceActive(s)));

        TfwRig.Secure(w, battle, 0);
        Assert.False(battle.SourceActive(0));
        Assert.True(battle.SourceActive(1));
        Assert.NotEqual(TfwPositionState.Locked, battle.Objectives[1].State);
        Assert.Equal(TfwPositionState.Locked, battle.Objectives[2].State);
        Assert.Equal((4, 4), (run.Transport, run.Supplies));

        TfwRig.Secure(w, battle, 1);
        Assert.Equal((6, 5), (run.Transport, run.Supplies));                 // the depot
        Assert.False(battle.SourceActive(1));

        Assert.Equal(0, battle.BossId);
        TfwRig.Secure(w, battle, 2);
        Assert.False(battle.SourceActive(2));
        Assert.True(battle.SourceActive(3));                                 // the HQ's stops only at victory
        ushort commander = battle.BossId;
        Assert.NotEqual(0, commander);
        Assert.Equal(battle.Layout.Hq, run.ActivePosition);

        sbyte front = run.War.LaneState(TfwLane.A).Front;
        double ork = run.War.LaneState(TfwLane.A).Ork;
        w.Musou!.Damage(commander, Human, 1e6f, Vector3.Zero, out bool killed);
        Assert.True(killed);
        TfwRig.Steps(w, 2);
        Assert.True(battle.Won);
        Assert.False(battle.SourceActive(3));
        Assert.Equal(front + 1, run.War.LaneState(TfwLane.A).Front);
        Assert.Equal(ork * WarRules.WinOrkFactor, run.War.LaneState(TfwLane.A).Ork, 3);
        int arrivals = battle.Arrivals;
        TfwRig.Seconds(w, 20f);
        Assert.Equal(arrivals, battle.Arrivals);                             // nothing comes any more
    }

    [Fact]
    public void A_pickup_takes_20_s_costs_1_and_leaving_applies_the_formula()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var battle = TfwRig.Deploy(w);
        TfwRig.Secure(w, battle, 0);
        var guards = w.TfwGuards!;
        int squad = guards.NearestSquad(TfwMap.LandingZone);
        for (int k = 0; k < 3; k++) guards.Hit(squad, 1e6f, Vector3.Zero);
        TfwRig.KillAll(w);
        TfwRig.Steps(w, 1);
        int lost = guards.Lost, kills = battle.OrksKilled;
        Assert.True(kills >= TfwBattleRules.Defenders, $"{kills}");
        Assert.True(lost >= 3);

        TfwRig.Command(w, TfwCommand.RequestPickup);
        TfwRig.Steps(w, 1);
        Assert.Equal(TfwRunPhase.Pickup, run.Phase);
        Assert.Equal(599, run.PickupTicks);
        Assert.Equal(4, run.Transport);                                      // charged on the way out
        while (run.PickupTicks > 1) w.Step(_ => default);
        var before = run.War.LaneState(TfwLane.A);
        kills = battle.OrksKilled;
        lost = guards.Lost;
        // The last tick: the war second may fall on it, so a shadow of that lane's reinforcements is within the tolerance.
        w.Step(_ => default);
        Assert.Equal(TfwRunPhase.Hub, run.Phase);
        Assert.Equal(3, run.Transport);
        var after = run.War.LaneState(TfwLane.A);
        Assert.Equal((float)WarRules.LeftProgress(1), after.Progress, 4);
        Assert.Equal(before.Ork - 0.05f * kills, after.Ork, 0.1f);
        Assert.Equal(before.Imperial - 0.1f * lost, after.Imperial, 0.1f);
        Assert.True(TfwRig.InStormbird(w.Players[Human].Position), $"{w.Players[Human].Position}");
        Assert.Equal(0, w.Musou!.Count);
        Assert.Equal(0, w.TfwGuards!.Count);
        Assert.Null(run.Battle);
        Assert.Null(run.ActivePosition);
    }

    [Fact]
    public void A_pickup_is_free_after_a_win_and_counts_no_leaving()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        TfwRig.WinBattle(w, TfwLane.A);
        Assert.Equal(6, run.Transport);                                      // 5 - 1 + the depot's 2, no pickup fee
        Assert.Equal(2, run.War.LaneState(TfwLane.A).Front);
    }

    [Fact]
    public void A_pickup_can_be_cancelled_and_a_death_cancels_it()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        TfwRig.Deploy(w);
        TfwRig.Command(w, TfwCommand.RequestPickup);
        TfwRig.Steps(w, 2);
        TfwRig.Command(w, TfwCommand.CancelPickup);
        TfwRig.Steps(w, 2);
        Assert.Equal((TfwRunPhase.Battle, 0), (run.Phase, run.PickupTicks));
        TfwRig.Command(w, TfwCommand.RequestPickup);
        TfwRig.Steps(w, 2);
        Assert.Equal(TfwRunPhase.Pickup, run.Phase);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Steps(w, 2);
        Assert.Equal((TfwRunPhase.Battle, 0), (run.Phase, run.PickupTicks));
        Assert.Equal(4, run.Transport);
    }

    [Fact]
    public void An_extraction_costs_2_supplies_after_3_s_and_counts_as_leaving()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var battle = TfwRig.Deploy(w);
        TfwRig.Secure(w, battle, 0);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds - 0.3f);
        Assert.False(w.Players[Human].Alive);
        Assert.Equal(TfwRunPhase.Battle, run.Phase);
        Assert.True(run.ExtractionTicks > 0);
        TfwRig.Seconds(w, 0.5f);
        Assert.Equal(TfwRunPhase.Hub, run.Phase);
        Assert.Equal(2, run.Supplies);
        Assert.Equal(4, run.Transport);                                      // no pickup fee
        Assert.True(w.Players[Human].Alive);
        Assert.True(TfwRig.InStormbird(w.Players[Human].Position));
        Assert.Equal((float)WarRules.LeftProgress(1), run.War.LaneState(TfwLane.A).Progress, 4);
    }

    [Fact]
    public void An_unpaid_extraction_ends_the_run_and_a_new_run_follows_the_delay()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        for (int k = 0; k < 2; k++)
        {
            TfwRig.Deploy(w);
            Assert.True(w.TfwKillForTests(Human));
            TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.3f);
            Assert.Equal(TfwRunPhase.Hub, run.Phase);
        }
        Assert.Equal(0, run.Supplies);
        TfwRig.Deploy(w);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.3f);
        Assert.Equal(TfwRunPhase.Lost, run.Phase);
        Assert.Equal(MatchState.Ended, w.State);
        Assert.Equal(Team.Chaos, w.Winner);
        Assert.False(w.Players[Human].Alive);                                // nobody returns on the end screen
        TfwRig.Seconds(w, w.Rules!.RestartDelay + w.Rules.CountdownTime + 0.5f);
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Equal(TfwRunPhase.Hub, run.Phase);
        Assert.Equal((5, 4), (run.Transport, run.Supplies));
        Assert.True(w.Players[Human].Alive);
        Assert.Equal(0, w.Musou!.Count);
    }

    [Fact]
    public void With_a_retainer_the_fallen_wait_25_s_for_a_revive_and_then_extract()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        run.RetainerUp = _ => true;
        TfwRig.Deploy(w);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Steps(w, 2);
        Assert.True(run.ReviveWanted(Human));
        TfwRig.Seconds(w, 10f);                                              // past the core's 2 s respawn: still down
        Assert.False(w.Players[Human].Alive);
        Assert.True(w.TfwRevive(Human));
        TfwRig.Steps(w, 2);
        Assert.False(run.ReviveWanted(Human));
        Assert.Equal(TfwRunPhase.Battle, run.Phase);
        Assert.Equal(4, run.Supplies);

        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ReviveWindowSeconds - 0.5f);
        Assert.True(run.ReviveWanted(Human));
        Assert.Equal(0, run.ExtractionTicks);
        TfwRig.Seconds(w, 1f);
        Assert.False(run.ReviveWanted(Human));
        Assert.True(run.ExtractionTicks > 0);
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds);
        Assert.Equal((TfwRunPhase.Hub, 2), (run.Phase, run.Supplies));
    }

    [Fact]
    public void A_retainer_lost_closes_the_window()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        bool up = true;
        run.RetainerUp = _ => up;
        TfwRig.Deploy(w);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Steps(w, 2);
        up = false;
        TfwRig.Steps(w, 2);
        Assert.False(run.ReviveWanted(Human));
        Assert.True(run.ExtractionTicks > 0);
    }

    [Fact]
    public void The_hq_falling_ends_the_run()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        for (int k = 0; k < 20000 && run.War.Phase != TfwRunPhase.Lost; k++) run.War.Step();
        Assert.Equal(TfwRunPhase.Lost, run.Phase);
        TfwRig.Steps(w, 2);
        Assert.Equal(MatchState.Ended, w.State);
        Assert.Equal(Team.Chaos, w.Winner);
    }

    [Fact]
    public void The_hq_gives_a_transport_every_10_war_minutes()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        while (run.War.Clock < 599.0) run.War.Step();
        Assert.Equal(5, run.Transport);
        TfwRig.Seconds(w, 8f);
        Assert.Equal(6, run.Transport);
    }

    [Fact]
    public void A_new_battle_clears_the_guardsmen_of_the_last_and_the_drops()
    {
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        Assert.NotNull(w.MusouSpawnDrop(Human, TfwMap.LandingZone, 0));
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.3f);
        Assert.Equal(0, w.TfwGuards!.Alive);
        Assert.DoesNotContain(w.MusouDrops, d => d.State == MusouDropState.Live);
        TfwRig.Deploy(w);
        Assert.Equal(60, w.TfwGuards.Alive);
    }

    [Fact]
    public void The_war_keeps_time_by_the_scale_and_freezes_the_battles_lane()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        double clock = run.War.Clock;
        TfwRig.Seconds(w, 8f);
        Assert.Equal(2.0, run.War.Clock - clock, 1);                         // the hub: a quarter
        TfwRig.Deploy(w);
        var lane = run.War.LaneState(TfwLane.A);
        clock = run.War.Clock;
        TfwRig.Seconds(w, 10f);
        Assert.Equal(10.0, run.War.Clock - clock, 1);
        Assert.Equal(lane.Progress, run.War.LaneState(TfwLane.A).Progress);
        Assert.Equal(1f, run.WarTimeScale);
    }

    static (ulong Hash, int Tick) WholeRun()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        TfwRig.WinBattle(w, TfwLane.A);
        TfwRig.WinBattle(w, TfwLane.A);
        Assert.Equal(TfwLane.A, run.War.FinalLane);

        var battle = TfwRig.Deploy(w);
        Assert.Equal(TfwRunPhase.FinalAssault, run.Phase);
        Assert.True(w.Musou!.TryGet(battle.BossId, out var boss));
        Assert.Equal(MusouEnemyType.Warboss, boss.Type);
        var bonus = run.War.FinalBonus(TfwLane.A);
        Assert.Equal(w.Musou.Rules.BossHealth * bonus.BossHealthFactor, battle.BossMaxHealth);
        Assert.Equal(TfwBattleRules.SquadsAt(run.War.LaneState(TfwLane.A).Imperial) + bonus.ExtraSquads, battle.Squads);
        double clock = run.War.Clock;
        TfwRig.Seconds(w, 5f);
        Assert.Equal(clock, run.War.Clock);                                  // the war waits for the assault

        TfwRig.KillAll(w);
        TfwRig.Steps(w, 2);
        Assert.Equal(TfwRunPhase.Won, run.Phase);
        Assert.Equal(MatchState.Ended, w.State);
        Assert.Equal(Team.SpaceMarines, w.Winner);
        return (run.War.StateHash(), (int)w.Tick);
    }

    [Fact]
    public void A_whole_scripted_run_reaches_the_final_assault_and_wins_the_same_way_twice() => Assert.Equal(WholeRun(), WholeRun());

    [Fact]
    public void The_server_feeds_the_wire_from_the_run_each_guardsman_once()
    {
        string? name = null;
        var map = Assert.IsType<GameMap>(SmServer.GameServer.GeneratedMap(ContentExtras.Musou, ref name, out _));
        var h = new Harness(LinkConditions.Perfect, 5, 1, TfwRig.Config, new SmServer.ServerOptions { Match = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } }, map: map,
            clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        h.Run(TimeSpan.FromSeconds(1));
        var run = h.Server.World.Tfw!;
        Assert.Same(run.War, h.Server.TfwFeed.War);                          // set from the hub on
        Assert.True(run.Enqueue(h.Clients[0].PlayerId, new TfwCommandRecord(1, TfwCommand.Deploy, 0)));
        h.Clients[0].TakeExtras();
        h.Run(TimeSpan.FromSeconds(TfwBattleRules.TravelSeconds + 1f));
        Assert.Equal(TfwRunPhase.Battle, run.Phase);
        var feed = h.Server.TfwFeed;
        Assert.Equal(run.Objectives.Count, feed.Objectives.Count);
        Assert.Equal(60, feed.GuardCount);
        Assert.Equal(60, feed.Guards.Take(feed.GuardCount).Select(g => g.Id).Distinct().Count());
        Assert.Equal(4, feed.Transport);
        var tags = h.Clients[0].TakeExtras().Select(s => s.Tag).ToHashSet();
        Assert.Contains(TfwWire.WarTag, tags);
        Assert.Contains(TfwWire.ObjectivesTag, tags);
        Assert.Contains(TfwWire.GuardsTag, tags);
    }

    [Fact]
    public void A_position_is_secured_on_the_real_walking_graph()
    {
        string? name = null;
        var map = Assert.IsType<GameMap>(SmServer.GameServer.GeneratedMap(ContentExtras.Musou, ref name, out _));
        var h = new Harness(LinkConditions.Perfect, 5, 0, TfwRig.Config, new SmServer.ServerOptions { Match = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } }, map: map);
        var nav = Assert.IsType<NavGraph>(h.Server.World.Musou!.Nav);
        var w = TfwRig.War(nav);
        var battle = TfwRig.Deploy(w);
        Assert.Equal(60, w.Musou!.Alive);
        TfwRig.Secure(w, battle, 0);
        TfwRig.Secure(w, battle, 1);
        Assert.Equal((6, 5), (w.Tfw!.Transport, w.Tfw.Supplies));
    }

    [Fact]
    public void Every_human_gone_sends_the_war_back_to_warmup_with_a_new_run()
    {
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        w.Remove(Human);
        TfwRig.Steps(w, 2);
        Assert.Equal(MatchState.Warmup, w.State);
        Assert.Equal(0, w.Musou!.Count);
        Assert.Equal(5, w.Tfw!.Transport);
    }
}
