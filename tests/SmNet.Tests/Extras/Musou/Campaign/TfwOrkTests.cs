using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>The Orks (pass 2's M3 and the owner's changes of 2026-11-01): the spawn points' crowds, when they swarm and when they stop, retaking, the elite curve, the count, the war without Guardsmen, and the crowd.</summary>
public class TfwOrkTests
{
    static (MusouSim Sim, TfwBattle Battle, TfwLayout Layout) Frozen(bool guardsmen = false, float elite = TfwBattleRules.EliteBase, bool final = false)
    {
        var sim = new MusouSim(null) { Frozen = true };
        var layout = TfwTestArena.Layout();
        var battle = new TfwBattle(sim, new GuardSim(sim), layout, 100.0, 1, final, default, elite, guardsmen);
        battle.Start();
        return (sim, battle, layout);
    }

    static int OnPoint(MusouSim sim, TfwLayout layout, int k)
    {
        var ids = new ushort[MusouSim.Capacity];
        return sim.Query(layout.Positions[k], TfwBattleRules.SpawnDisc + 1f, ids);
    }

    static void ClearPoint(MusouSim sim, TfwLayout layout, int k)
    {
        var ids = new ushort[MusouSim.Capacity];
        int n = sim.Query(layout.Positions[k], layout.Radius(k) + 2f, ids);
        for (int i = 0; i < n; i++) sim.Damage(ids[i], 1, 1e7f, Vector3.Zero, out _);
    }

    /// <summary>Secures position <paramref name="k"/> with a human on it.</summary>
    static void Capture(MusouSim sim, TfwBattle battle, TfwLayout layout, int k)
    {
        Vector3[] at = [layout.Positions[k]];
        for (int t = 0; t < 30 * 30 && battle.Objectives[k].State != TfwPositionState.Secured; t++) { ClearPoint(sim, layout, k); battle.Step(at); }
        Assert.Equal(TfwPositionState.Secured, battle.Objectives[k].State);
    }

    [Fact]
    public void Each_point_starts_with_its_garrison_and_a_held_point_adds_one_to_its_crowd_a_second()
    {
        var (sim, battle, layout) = Frozen();
        Assert.Equal(TfwTestArena.StartAlive, sim.Alive);
        Assert.Equal(3 * TfwBattleRules.Garrison, sim.Alive);
        Assert.Equal([12, 12, 12], Enumerable.Range(0, 3).Select(battle.Garrisoned));
        Assert.Equal([0, 0, 0, 0], Enumerable.Range(0, 4).Select(battle.Pooled));   // the crowds start from none: no stagger
        Assert.Equal(50, TfwBattleRules.CrowdSize);
        Assert.All(Enumerable.Range(0, 3), k => Assert.True(battle.SourceActive(k)));
        Assert.False(battle.SourceActive(3));                                 // the HQ waits for the commander
        for (int t = 0; t < 10 * 30; t++) battle.Step([]);
        Assert.Equal(TfwTestArena.StartAlive + 30, sim.Alive);                // one a second from each point
        Assert.Equal([10, 10, 10], Enumerable.Range(0, 3).Select(battle.Pooled));
        Assert.Equal([12, 12, 12], Enumerable.Range(0, 3).Select(battle.Garrisoned));   // and the garrisons are never rebuilt
        Assert.All(Enumerable.Range(0, 3), k => Assert.True(OnPoint(sim, layout, k) >= 22));
    }

    [Fact]
    public void A_garrison_is_never_rebuilt_even_when_it_is_killed_and_the_point_goes_on_filling_its_crowd()
    {
        var (sim, battle, layout) = Frozen();
        battle.OnSwarm = s => { foreach (ushort id in s.Ids) sim.Damage(id, 1, 1e7f, Vector3.Zero, out _); };   // a player who kills each swarm
        foreach (ushort id in battle.GarrisonIds(0)) sim.Damage(id, 1, 1e7f, Vector3.Zero, out _);
        battle.Step([]);
        Assert.Equal(0, battle.Garrisoned(0));
        for (int t = 0; t < 60 * 30; t++) battle.Step([]);
        Assert.Equal(0, battle.Garrisoned(0));                                // 60 s later: still none
        Assert.InRange(battle.Pooled(0), 9, 11);                              // 50 swarmed at 50 s, the next crowd has 10
    }

    [Fact]
    public void The_three_crowds_swarm_at_fifty_each_on_its_own_and_start_a_new_crowd()
    {
        var (sim, battle, _) = Frozen();
        var swarms = new List<(int Tick, TfwSwarm Swarm)>();
        int tick = 0;
        battle.OnSwarm = s =>
        {
            swarms.Add((tick, s));
            Assert.All(s.Ids, id => Assert.False(sim.Leashed(id)));            // free for good, every one of them
            foreach (ushort id in s.Ids) sim.Damage(id, 1, 1e7f, Vector3.Zero, out _);   // a player who kills each swarm
        };
        for (tick = 1; tick <= 160 * 30; tick++) battle.Step([]);
        Assert.Equal(9, swarms.Count);                                        // three rounds of three, 50 s a crowd
        Assert.All(swarms, s => { Assert.Equal(TfwSwarmWhy.Full, s.Swarm.Why); Assert.Equal(TfwBattleRules.CrowdSize, s.Swarm.Size); });
        Assert.Equal([0, 1, 2], swarms.Take(3).Select(s => s.Swarm.Point).Order());
        Assert.Equal(3, swarms.Select(s => s.Tick).Distinct().Count());       // no stagger: the three points swarm together
        for (int round = 0; round < 3; round++) Assert.Equal(50 * 30 * (round + 1), swarms[round * 3].Tick);
        Assert.Equal(9, battle.Swarms);
        Assert.Equal(3 * TfwBattleRules.Garrison, sim.Alive - battle.Pooled(0) - battle.Pooled(1) - battle.Pooled(2));   // the garrisons stood the whole time
    }

    [Fact]
    public void A_hurt_crowd_ork_wakes_its_whole_crowd_not_the_garrison_and_the_point_starts_again_from_none()
    {
        var (sim, battle, _) = Frozen();
        for (int t = 0; t < 10 * 30; t++) battle.Step([]);
        TfwSwarm? seen = null;
        battle.OnSwarm = s => seen = s;
        var crowd = battle.CrowdIds(1);
        Assert.Equal(10, crowd.Length);
        sim.Damage(crowd[0], 1, 1f, Vector3.Zero, out _);                     // one hurt: MusouSim frees its group
        battle.Step([]);
        Assert.NotNull(seen);
        Assert.Equal((1, TfwSwarmWhy.Woken, 10), (seen!.Value.Point, seen.Value.Why, seen.Value.Size));
        Assert.Equal(0, battle.Pooled(1));
        Assert.All(seen.Value.Ids, id => Assert.False(sim.Leashed(id)));
        Assert.Equal(12, battle.Garrisoned(1));                               // the garrison stands
        Assert.Equal(10, battle.Pooled(0));                                   // so do the other crowds
        for (int t = 0; t < 10 * 30; t++) battle.Step([]);
        Assert.InRange(battle.Pooled(1), 9, 11);                              // and the point grows a new one
    }

    [Fact]
    public void A_hurt_garrison_ork_wakes_its_garrison_only()
    {
        var (sim, battle, _) = Frozen();
        for (int t = 0; t < 5 * 30; t++) battle.Step([]);
        sim.Damage(battle.GarrisonIds(2)[0], 1, 1f, Vector3.Zero, out _);
        battle.Step([]);
        Assert.Equal(0, battle.Garrisoned(2));
        Assert.Equal(12, battle.Garrisoned(1));
        Assert.Equal(5, battle.Pooled(2));                                    // the crowd keeps pooling
    }

    [Fact]
    public void A_target_near_a_crowd_wakes_all_of_it_at_once_and_one_out_of_reach_wakes_none()
    {
        var sim = new MusouSim(null) { Swarm = MusouSwarmRules.Default };
        var layout = TfwTestArena.Layout();
        var battle = new TfwBattle(sim, new GuardSim(sim), layout, 100.0, 1, false, default, TfwBattleRules.EliteBase, false);
        battle.Start();
        var attacks = new List<MusouAttack>();
        var swarms = new List<TfwSwarm>();
        battle.OnSwarm = swarms.Add;
        uint t = 0;
        void Tick(Vector3 at)
        {
            sim.BeginTick(++t);
            battle.Step([at]);
            sim.Step([new MusouTarget(1, at, true)], null, false, attacks);
        }
        for (int k = 0; k < 8 * 30; k++) Tick(layout.LandingZone);            // a standoff 55 m or more from every crowd
        Assert.Empty(swarms);
        Assert.Equal(8, battle.Pooled(2));
        var near = layout.Positions[2] + new Vector3(TfwBattleRules.SpawnDisc + TfwBattleRules.CrowdAggro - 6f, 0f, 0f);   // near the closest of the disc, far from most of it
        for (int k = 0; k < 3 * 30; k++) Tick(near);
        var swarm = swarms.First(s => s.Point == 2);   // a fresh crowd of 1 to 3 may wake again at once with the player still near (CrowdAggro 30)
        Assert.Equal(TfwSwarmWhy.Woken, swarm.Why);
        Assert.InRange(swarm.Size, 8, 12);
        Assert.All(swarm.Ids, id => Assert.False(sim.Leashed(id)));            // the whole crowd, not the few the target was near
        Assert.InRange(battle.Pooled(2), 0, 3);                                // and the point has begun a new crowd
    }

    [Fact]
    public void A_capture_pauses_the_point_and_it_sends_again_on_the_tick_the_capture_stops()
    {
        var (sim, battle, layout) = Frozen();
        Vector3[] at = [layout.Positions[0]], away = [layout.LandingZone];
        ClearPoint(sim, layout, 0);
        for (int t = 0; t < 5 * 30; t++)
        {
            battle.Step(at);
            Assert.False(battle.SourceActive(0));
            Assert.Equal(0, OnPoint(sim, layout, 0));                         // nothing joins its crowd
        }
        Assert.Equal(TfwPositionState.Securing, battle.Objectives[0].State);
        float progress = battle.Objectives[0].Progress;
        Assert.True(progress is > 0f and < 1f);
        battle.Step(away);                                              // the capture stops
        Assert.Equal(TfwPositionState.Held, battle.Objectives[0].State);
        Assert.True(battle.SourceActive(0));
        Assert.True(OnPoint(sim, layout, 0) >= 1, "one more of its crowd on that very tick");
    }

    [Fact]
    public void Stepping_on_and_off_a_point_sends_no_faster_than_holding_it()
    {
        var (sim, battle, layout) = Frozen();
        ClearPoint(sim, layout, 0);
        Vector3[] at = [layout.Positions[0]], away = [layout.LandingZone];
        for (int t = 0; t < 10 * 30; t++) battle.Step(t % 2 == 0 ? at : away);
        int sent = OnPoint(sim, layout, 0);
        Assert.InRange(sent, 8, 11);                                          // about 10 in 10 s as when holding it, not one a tick of the Held ones
    }

    [Fact]
    public void A_late_human_raises_the_cap()
    {
        var (sim, battle, _) = Frozen();
        for (int t = 0; t < 200 * 30; t++) battle.Step([]);
        Assert.Equal(TfwBattleRules.AliveCap(1) + TfwTestArena.StartAlive, sim.Alive);   // the garrisons stand outside the cap
        for (int t = 0; t < 120 * 30; t++) battle.Step([], 2);
        Assert.Equal(TfwBattleRules.AliveCap(2) + TfwTestArena.StartAlive, sim.Alive);
    }

    [Fact]
    public void An_imperial_fighting_on_the_point_pauses_it_too()
    {
        var (sim, battle, layout) = Frozen();
        Vector3[] at = [layout.Positions[0]];
        int before = OnPoint(sim, layout, 0);
        for (int t = 0; t < 5 * 30; t++) battle.Step(at);
        Assert.Equal(TfwPositionState.Clearing, battle.Objectives[0].State);  // its crowd still stands there
        Assert.Equal(before, OnPoint(sim, layout, 0));
        Assert.Equal(0f, battle.Objectives[0].Progress);
    }

    [Fact]
    public void Only_a_full_capture_stops_the_point_and_a_retake_sends_again_on_that_tick()
    {
        var (sim, battle, layout) = Frozen();
        Capture(sim, battle, layout, 0);
        Vector3[] away = [layout.LandingZone];
        for (int t = 0; t < 10 * 30; t++) battle.Step(away);
        Assert.False(battle.SourceActive(0));
        Assert.Equal(0, OnPoint(sim, layout, 0));                             // nobody on it, and nobody sent

        ushort ork = sim.Spawn(MusouEnemyType.Boy, layout.Positions[0], 0f, 85f);
        int ticks = 0;
        while (battle.Objectives[0].State == TfwPositionState.Secured && ticks < 40 * 30)
        {
            Assert.False(battle.SourceActive(0));                             // a falling point sends nothing until it has fallen
            battle.Step(away);
            ticks++;
        }
        Assert.Equal(TfwPositionState.Held, battle.Objectives[0].State);
        Assert.Equal(20f, ticks / 30f, 0.1f);
        Assert.Equal(0, battle.Secured);
        Assert.True(battle.SourceActive(0));
        Assert.True(OnPoint(sim, layout, 0) >= 2, "the Ork that took it, then one of its crowd on that very tick");
        Assert.True(sim.TryGet(ork, out _));
    }

    [Fact]
    public void An_imperial_coming_back_holds_a_falling_point()
    {
        var (sim, battle, layout) = Frozen();
        Capture(sim, battle, layout, 0);
        Vector3[] at = [layout.Positions[0]], away = [layout.LandingZone];
        sim.Spawn(MusouEnemyType.Boy, layout.Positions[0], 0f, 85f);
        for (int t = 0; t < 10 * 30; t++) battle.Step(away);
        Assert.InRange(battle.Objectives[0].Progress, 0.45f, 0.55f);
        for (int t = 0; t < 12 * 30; t++) battle.Step(at);
        Assert.Equal(TfwPositionState.Secured, battle.Objectives[0].State);
        Assert.Equal(1f, battle.Objectives[0].Progress);
    }

    [Fact]
    public void The_commander_comes_once_each_point_has_fallen_even_with_one_retaken()
    {
        var (sim, battle, layout) = Frozen();
        Capture(sim, battle, layout, 0);
        Capture(sim, battle, layout, 1);
        Vector3[] away = [layout.LandingZone];
        sim.Spawn(MusouEnemyType.Boy, layout.Positions[0], 0f, 85f);
        for (int t = 0; t < 21 * 30; t++) battle.Step(away);
        Assert.Equal(TfwPositionState.Held, battle.Objectives[0].State);
        Assert.Equal((ushort)0, battle.BossId);
        Capture(sim, battle, layout, 2);
        Assert.NotEqual((ushort)0, battle.BossId);                            // a battle never hangs on holding all three at once
        Assert.True(battle.SourceActive(3));
    }

    [Theory]
    [InlineData(1, 150)]
    [InlineData(2, 230)]
    [InlineData(4, 390)]
    [InlineData(8, 460)]
    public void The_count_is_the_owners_and_stays_inside_the_pool(int humans, int cap)
    {
        Assert.Equal(cap, TfwBattleRules.AliveCap(humans));
        Assert.True(TfwBattleRules.AliveCap(humans) + 3 * TfwBattleRules.Garrison < MusouSim.Capacity);
    }

    [Fact]
    public void Three_crowds_of_fifty_are_the_cap_and_swarms_nobody_kills_hold_the_refilling_back()
    {
        Assert.Equal(3 * TfwBattleRules.CrowdSize, TfwBattleRules.AliveCapSolo);
        var (sim, battle, _) = Frozen();
        var swarms = new List<TfwSwarm>();
        battle.OnSwarm = swarms.Add;
        for (int t = 0; t < 300 * 30; t++) battle.Step([]);
        Assert.Equal(TfwBattleRules.AliveCapSolo + TfwTestArena.StartAlive, sim.Alive);   // never over: 150 and the garrisons
        Assert.Equal(3, swarms.Count);                                        // one round only: the free Orks hold the next crowds at nothing
        Assert.All(swarms, s => Assert.Equal(TfwBattleRules.CrowdSize, s.Size));
    }

    [Fact]
    public void With_the_cap_full_of_free_orks_a_crowd_waits_at_its_size_and_never_swarms_below_fifty()
    {
        // Free Orks fill the cap while the crowds are part-grown (swarms nobody kills): the crowds wait, they are not let go small; as the free Orks fall they fill again and swarm at 50.
        var (sim, battle, layout) = Frozen();
        var swarms = new List<TfwSwarm>();
        battle.OnSwarm = swarms.Add;
        for (int t = 0; t < 20 * 30; t++) battle.Step([]);
        var free = new List<ushort>();
        while (sim.Alive - TfwTestArena.StartAlive < TfwBattleRules.AliveCapSolo) free.Add(sim.Spawn(MusouEnemyType.Boy, layout.LandingZone, 0f, 85f));   // the cap is reached by Orks that stay
        for (int t = 0; t < 60 * 30; t++) battle.Step([]);
        Assert.Empty(swarms);
        Assert.Equal([20, 20, 20], Enumerable.Range(0, 3).Select(battle.Pooled));   // held at what they had
        foreach (ushort id in free) sim.Damage(id, 1, 1e7f, Vector3.Zero, out _);
        for (int t = 0; t < 40 * 30; t++) battle.Step([]);
        Assert.Equal(3, swarms.Count);
        Assert.All(swarms, s => { Assert.Equal(TfwSwarmWhy.Full, s.Why); Assert.Equal(TfwBattleRules.CrowdSize, s.Size); });
    }

    [Fact]
    public void The_commander_brings_a_crowd_of_the_hqs_source_and_it_grows_and_swarms_like_the_rest()
    {
        var (sim, battle, layout) = Frozen();
        var swarms = new List<TfwSwarm>();
        battle.OnSwarm = swarms.Add;
        Capture(sim, battle, layout, 0);
        Capture(sim, battle, layout, 1);
        Capture(sim, battle, layout, 2);
        Assert.True(battle.SourceActive(3));
        Assert.Equal(0, battle.Pooled(3));                                    // it starts from none
        swarms.Clear();
        Vector3[] away = [layout.LandingZone];
        for (int t = 0; t < 52 * 30; t++) battle.Step(away);
        var swarm = Assert.Single(swarms, s => s.Point == 3);
        Assert.Equal((TfwSwarmWhy.Full, TfwBattleRules.CrowdSize), (swarm.Why, swarm.Size));
        Assert.Equal(0, swarms.Count(s => s.Point < 3 && s.Why == TfwSwarmWhy.Full));   // the three positions are secured: no crowd grows there
    }

    [Fact]
    public void The_final_assault_has_four_crowds_from_none_and_no_garrison_and_the_warboss()
    {
        var (sim, battle, _) = Frozen(final: true);
        Assert.Equal([0, 0, 0, 0], Enumerable.Range(0, 4).Select(battle.Pooled));
        Assert.Equal([0, 0, 0], Enumerable.Range(0, 3).Select(battle.Garrisoned));
        Assert.Equal(1, sim.Alive);
        Assert.All(Enumerable.Range(0, 4), k => Assert.True(battle.SourceActive(k)));
    }

    [Theory]
    [InlineData(0f, 0.10f)]
    [InlineData(0.5f, 0.35f)]
    [InlineData(1f, 0.60f)]
    public void The_elite_share_follows_the_run_and_the_mix_its_shares(float progress, float share)
    {
        float elite = TfwBattleRules.EliteShare(progress);
        Assert.Equal(share, elite, 3);
        var (sim, battle, _) = Frozen(elite: elite);
        for (int t = 0; t < 200 * 30; t++) battle.Step([]);
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = sim.CopyTo(all);
        float Share(MusouEnemyType type) => all.Take(n).Count(e => e.Type == type) / (float)n;
        Assert.InRange(Share(MusouEnemyType.ArdBoy) + Share(MusouEnemyType.Nob), share - 0.02f, share + 0.02f);
        Assert.InRange(Share(MusouEnemyType.ArdBoy) / (Share(MusouEnemyType.ArdBoy) + Share(MusouEnemyType.Nob)), 0.6f, 0.9f);
        Assert.True(Share(MusouEnemyType.Boy) > Share(MusouEnemyType.ShootaBoy) && Share(MusouEnemyType.ShootaBoy) > Share(MusouEnemyType.Gretchin));
    }

    [Theory]
    [InlineData(0, 2, false, 0f)]
    [InlineData(1, 2, false, 0.5f)]
    [InlineData(5, 2, false, 1f)]
    [InlineData(0, 3, true, 1f)]
    public void The_runs_progress_is_its_wins_and_the_final_assault_is_one(int wins, int needed, bool final, float progress) =>
        Assert.Equal(progress, TfwBattleRules.RunProgress(wins, needed, final), 3);

    [Fact]
    public void A_win_raises_the_elites_of_the_next_battle()
    {
        var w = TfwRig.War();
        Assert.Equal(0, w.Tfw!.Wins);
        var first = TfwRig.Deploy(w, TfwLane.A);
        Assert.Equal(TfwBattleRules.EliteBase, first.EliteShare, 3);
        TfwRig.KillAll(w);
        typeof(TfwRun).GetProperty(nameof(TfwRun.Wins))!.SetValue(w.Tfw, 1);
        TfwRig.Command(w, TfwCommand.RequestPickup);
        TfwRig.Seconds(w, TfwBattleRules.PickupSeconds + 0.2f);
        var second = TfwRig.Deploy(w, TfwLane.A);
        Assert.Equal(TfwBattleRules.EliteShare(1f / TfwRun.WinsNeeded(TfwLane.A)), second.EliteShare, 3);
    }

    // ---------------------------------------------------------------- without Guardsmen

    [Fact]
    public void The_guardsmen_switch_is_off_and_a_run_takes_it()
    {
        Assert.False(TfwBattleRules.GuardsmenEnabled);
        var w = TfwRig.War();
        w.Tfw!.Guardsmen = TfwBattleRules.GuardsmenEnabled;
        var battle = TfwRig.Deploy(w);
        Assert.Equal(0, battle.Squads);
        Assert.Equal(0, battle.Reserves);
        Assert.Equal(0, w.TfwGuards!.Count);
        var targets = new MusouTarget[GuardSim.MaxSquads];
        Assert.Equal(0, w.TfwGuards.AppendTargets(targets));
    }

    [Fact]
    public void Without_guardsmen_a_human_takes_all_three_points_and_wins_the_battle()
    {
        var w = TfwRig.War();
        w.Tfw!.Guardsmen = false;
        var battle = TfwRig.Deploy(w);
        for (int k = 0; k < 3; k++)
        {
            int ticks = 0;
            TfwRig.Teleport(w, battle.Layout.Positions[k]);
            for (; ticks < 40 * 30 && battle.Objectives[k].State != TfwPositionState.Secured; ticks++) { TfwRig.KillAll(w); w.Step(_ => default); }
            Assert.True(ticks / 30f <= TfwBattleRules.SecureSeconds + 2f, $"{ticks / 30f} s");   // the full rate (a lord may be there first)
        }
        Assert.NotEqual((ushort)0, battle.BossId);
        TfwRig.KillAll(w);
        TfwRig.Steps(w, 2);
        Assert.True(battle.Won);
        Assert.Equal(0, w.TfwGuards!.Lost);
    }

    [Fact]
    public void An_ai_marine_alone_takes_a_point_at_the_full_rate_without_guardsmen_and_at_half_with_them()
    {
        var (sim, without, layout) = Frozen(guardsmen: false);
        ClearPoint(sim, layout, 0);
        Vector3[] marine = [layout.Positions[0]];
        for (int t = 0; t < 26 * 30; t++) { ClearPoint(sim, layout, 0); without.Step(marine); }
        Assert.Equal(TfwPositionState.Secured, without.Objectives[0].State);

        var (other, with, place) = Frozen(guardsmen: true);
        for (int t = 0; t < 26 * 30; t++) { ClearPoint(other, place, 0); with.Step(marine); }
        Assert.Equal(TfwPositionState.Securing, with.Objectives[0].State);    // never stuck at rate 0
        Assert.InRange(with.Objectives[0].Progress, 0.5f, 0.55f);
    }

    [Fact]
    public void A_run_with_nobody_left_still_ends_without_guardsmen()
    {
        var w = TfwRig.War();
        w.Tfw!.Guardsmen = false;
        TfwRig.Deploy(w);
        Assert.True(w.TfwKillForTests(TfwRig.Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 0.5f);
        Assert.Equal(TfwRunPhase.Hub, w.Tfw.Phase);
    }

    [Fact]
    public void The_hud_says_nothing_of_guardsmen_there_are_none_of()
    {
        Assert.Equal("", SmNet.Extras.Musou.Campaign.TfwHudModel.GuardsText(default));
        Assert.StartsWith("GUARDS 10", SmNet.Extras.Musou.Campaign.TfwHudModel.GuardsText(default(TfwOwnView) with { Guards = 10, Squads = 1 }));
    }

    // ---------------------------------------------------------------- the crowd

    static MusouSim Crowd()
    {
        var mesh = new NavigationTests.Level().Floor(0f, 0f, 200f, 120f).Mesh();
        return new MusouSim(NavGraph.Build(mesh, null, NavSettings.From(new SimConfig()))) { Swarm = MusouSwarmRules.Default };
    }

    static void Run(MusouSim sim, MusouTarget[] targets, int ticks, ref uint tick, List<MusouAttack>? attacks = null)
    {
        attacks ??= new List<MusouAttack>();
        for (int k = 0; k < ticks; k++) { sim.BeginTick(++tick); sim.Step(targets, null, true, attacks); }
    }

    /// <summary>The spread across the way: the standard deviation of y of a pack walking along x.</summary>
    static float Across(MusouSim sim)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = sim.CopyTo(all);
        float mean = all.Take(n).Average(e => e.Position.Y);
        return MathF.Sqrt(all.Take(n).Average(e => (e.Position.Y - mean) * (e.Position.Y - mean)));
    }

    [Fact]
    public void A_pack_fans_out_on_the_way_and_without_the_swarm_it_does_not()
    {
        float Walk(bool swarm)
        {
            var sim = Crowd();
            if (!swarm) sim.Swarm = null;
            for (int k = 0; k < 40; k++) sim.Spawn(MusouEnemyType.Boy, new Vector3(10f + k % 8, 56f + k / 8, 0f), 0f, 85f);
            uint tick = 0;
            Run(sim, [new MusouTarget(1, new Vector3(190f, 60f, 0f), true)], 15 * 30, ref tick);
            return Across(sim);
        }
        float plain = Walk(false), fanned = Walk(true);
        Assert.True(fanned > plain * 2f && fanned > 6f, $"across {fanned:F1} m, the plain pack {plain:F1} m");
    }

    [Fact]
    public void Tokens_are_six_on_a_player_twelve_on_a_squad_and_the_rest_wait_without_orbiting()
    {
        foreach (var (target, most) in new[] { (new MusouTarget(1, new Vector3(100f, 60f, 0f), true), 6), (new MusouTarget(TfwContracts.GuardTargetId(0), new Vector3(100f, 60f, 0f), true, 2.5f), 12) })
        {
            var sim = Crowd();
            for (int k = 0; k < 40; k++) sim.Spawn(MusouEnemyType.Boy, new Vector3(100f + MathF.Cos(k) * 12f, 60f + MathF.Sin(k) * 12f, 0f), 0f, 85f);
            uint tick = 0;
            MusouTarget[] targets = [target];
            var attacks = new List<MusouAttack>();
            Run(sim, targets, 8 * 30, ref tick, attacks);
            Assert.Equal(most, sim.Tally().Melee);
            Assert.True(attacks.Count >= most, $"{attacks.Count} blows");
            var at = new MusouEnemy[MusouSim.Capacity];
            int n = sim.CopyTo(at);
            var before = at.Take(n).Where(e => e.AttackToken == 0).ToDictionary(e => e.Id, e => e.Position);
            Run(sim, targets, 60, ref tick, attacks);
            sim.CopyTo(at);
            float moved = before.Average(p => sim.TryGet(p.Key, out var e) ? Vector3.Distance(e.Position, p.Value) : 0f);
            Assert.True(moved < 1.5f, $"the waiting moved {moved:F2} m in 2 s");   // the old ring walked about 4.7 m
        }
    }

    [Fact]
    public void A_melee_token_goes_only_to_those_who_can_strike_and_comes_back_when_they_cannot()
    {
        var sim = Crowd();
        for (int k = 0; k < 12; k++) sim.Spawn(MusouEnemyType.Boy, new Vector3(100f + MathF.Cos(k) * 4f, 60f + MathF.Sin(k) * 4f, 0f), 0f, 85f);
        uint tick = 0;
        Run(sim, [new MusouTarget(1, new Vector3(100f, 60f, 2f), true)], 3 * 30, ref tick);
        Assert.Equal(0, sim.Tally().Melee);                                   // 2 m above: in the old gap, no blow could land
        Run(sim, [new MusouTarget(1, new Vector3(100f, 60f, 0f), true)], 3 * 30, ref tick);
        Assert.Equal(6, sim.Tally().Melee);
        Run(sim, [new MusouTarget(1, new Vector3(100f, 60f, 2f), true)], 3 * 30, ref tick);   // a blow under way finishes first
        Assert.Equal(0, sim.Tally().Melee);                                   // six can no longer block the player's melee from there
    }

    [Fact]
    public void A_woken_defender_never_walks_home()
    {
        var sim = Crowd();
        ushort id = sim.SpawnLeashed(MusouEnemyType.Boy, new Vector3(100f, 60f, 0f), 0f, 85f, 1, 30f);
        uint tick = 0;
        Run(sim, [new MusouTarget(1, new Vector3(120f, 60f, 0f), true)], 30, ref tick);
        Assert.False(sim.Leashed(id));
        Run(sim, [new MusouTarget(1, new Vector3(190f, 60f, 0f), true)], 120, ref tick);
        Assert.True(sim.TryGet(id, out var e) && e.Position.X > 110f, $"{e.Position}");
    }

    [Fact]
    public void A_stuck_unreachable_Ork_and_an_off_centre_shooter_do_not_keep_a_point_from_being_taken()
    {
        var (sim, battle, layout) = Frozen();
        ClearPoint(sim, layout, 0);
        var at = layout.Positions[0];
        ushort stuck = sim.Spawn(MusouEnemyType.Boy, at + new Vector3(8f, 0f, 0f), 0f, 85f);      // frozen: it never moves, attacks nothing
        ushort shooter = sim.Spawn(MusouEnemyType.ShootaBoy, at + new Vector3(-10f, 3f, 0f), 0f, 85f);
        Vector3[] me = [at];
        for (int t = 0; t < 4 * 30; t++) battle.Step(me);
        Assert.Equal(TfwPositionState.Clearing, battle.Objectives[0].State);    // not yet: they have not stood idle for long
        Assert.True(sim.TryGet(stuck, out var e) && e.Alive);
        for (int t = 0; t < 12 * 30; t++) battle.Step(me);
        Assert.False(sim.TryGet(stuck, out e) && e.Alive);
        Assert.False(sim.TryGet(shooter, out e) && e.Alive);
        for (int t = 0; t < 40 * 30 && battle.Objectives[0].State != TfwPositionState.Secured; t++) battle.Step(me);
        Assert.Equal(TfwPositionState.Secured, battle.Objectives[0].State);
    }

    [Fact]
    public void A_shooter_holding_its_band_stays_out_of_a_point_being_taken_and_without_one_it_holds_where_it_is()
    {
        // The capture bug's cause: a shooter holds 12 m or more from its target, so with the player on the centre of a 13.5 m ring the idle shooters stood just inside it and the point stayed Clearing for as long as they lived.
        float Held(bool keepOut)
        {
            var sim = Crowd();
            var centre = new Vector3(100f, 60f, 0f);
            ushort id = sim.Spawn(MusouEnemyType.ShootaBoy, centre + new Vector3(12.5f, 0f, 0f), 0f, 85f);
            if (keepOut) sim.KeepOut.Add((centre, 13.5f, 10f));
            uint tick = 0;
            Run(sim, [new MusouTarget(1, centre, true)], 8 * 30, ref tick);
            Assert.True(sim.TryGet(id, out var e) && e.Alive);
            return new Vector2(e.Position.X - centre.X, e.Position.Y - centre.Y).Length();
        }
        Assert.True(Held(false) < 13.5f, "without the keep-out it holds inside the ring");
        Assert.True(Held(true) >= 13.5f, "with it, outside");
    }

    [Fact]
    public void The_battle_keeps_shooters_out_of_a_position_only_while_it_is_being_taken()
    {
        var sim = new MusouSim(null) { Frozen = true };
        var layout = TfwTestArena.Layout();
        var battle = new TfwBattle(sim, new GuardSim(sim), layout, 100.0, 1, false, default, TfwBattleRules.EliteBase, false);
        battle.Start();
        battle.Step([]);
        Assert.Empty(sim.KeepOut);                                            // Held: nobody on it
        battle.Step([layout.Positions[0]]);
        var place = Assert.Single(sim.KeepOut);                               // an Imperial on it with its garrison there: Clearing
        Assert.Equal(layout.Positions[0], place.Centre);
        Assert.Equal(layout.Radius(0), place.Radius);
        battle.Step([layout.LandingZone]);
        Assert.Empty(sim.KeepOut);
    }

    [Fact]
    public void A_goal_draws_the_orks_and_nobody_strikes_it()
    {
        var sim = Crowd();
        ushort id = sim.Spawn(MusouEnemyType.Boy, new Vector3(100f, 60f, 0f), 0f, 85f);
        var goal = new Vector3(120f, 60f, 0f);
        uint tick = 0;
        var attacks = new List<MusouAttack>();
        Run(sim, [new MusouTarget(TfwContracts.PointTargetId(0), goal, true, 0f, true)], 8 * 30, ref tick, attacks);
        Assert.Empty(attacks);
        Assert.True(sim.TryGet(id, out var e) && Vector3.Distance(e.Position, goal) <= MusouSwarmRules.Default.GoalStand + 1f, $"{e.Position}");
    }

    [Fact]
    public void A_goal_wakes_no_garrison()
    {
        var sim = Crowd();
        ushort id = sim.SpawnLeashed(MusouEnemyType.Boy, new Vector3(100f, 60f, 0f), 0f, 85f, 1, 30f);
        uint tick = 0;
        Run(sim, [new MusouTarget(TfwContracts.PointTargetId(0), new Vector3(110f, 60f, 0f), true, 0f, true)], 60, ref tick);
        Assert.True(sim.Leashed(id));
    }

    [Fact]
    public void A_war_gets_the_crowd_and_the_five_wave_run_does_not()
    {
        var w = TfwRig.War();
        Assert.NotNull(w.Musou!.Swarm);
        Assert.Null(new MusouSim(null).Swarm);
    }
}
