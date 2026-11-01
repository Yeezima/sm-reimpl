using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>The Orks (pass 2's M3 and the owner's changes of 2026-11-01): garrisons, the tide and when it stops, retaking, the elite curve, the count, the war without Guardsmen, and the crowd.</summary>
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
    public void Each_point_starts_with_its_garrison_and_a_held_point_sends_the_tide()
    {
        var (sim, battle, layout) = Frozen();
        Assert.Equal(3 * TfwBattleRules.Garrison, sim.Alive);
        Assert.All(Enumerable.Range(0, 3), k => Assert.True(battle.SourceActive(k)));
        Assert.False(battle.SourceActive(3));                                 // the HQ waits for the commander
        for (int t = 0; t < 10 * 30; t++) battle.Step([]);
        Assert.Equal(3 * TfwBattleRules.Garrison + 30, sim.Alive);            // one a second from each point; the garrisons are whole
        Assert.All(Enumerable.Range(0, 3), k => Assert.True(OnPoint(sim, layout, k) >= TfwBattleRules.Garrison + 10));
    }

    [Fact]
    public void A_garrison_is_rebuilt_at_its_rate_while_the_point_is_held()
    {
        var (sim, battle, layout) = Frozen();
        ClearPoint(sim, layout, 0);
        int before = sim.Alive;
        battle.Step([]);
        for (int t = 1; t < 8 * 30; t++) battle.Step([]);
        int sent = battle.Arrivals;
        Assert.InRange(sim.Alive - before, 24 + 2, 24 + 3);                   // the tide of three points, and the garrison's two at 4 s
        Assert.True(sent >= 24);
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
            Assert.Equal(0, OnPoint(sim, layout, 0));                         // neither tide nor garrison
        }
        Assert.Equal(TfwPositionState.Securing, battle.Objectives[0].State);
        float progress = battle.Objectives[0].Progress;
        Assert.True(progress is > 0f and < 1f);
        battle.Step(away);                                              // the capture stops
        Assert.Equal(TfwPositionState.Held, battle.Objectives[0].State);
        Assert.True(battle.SourceActive(0));
        Assert.True(OnPoint(sim, layout, 0) >= 2, "tide and garrison on that very tick");
    }

    [Fact]
    public void Stepping_on_and_off_a_point_sends_no_faster_than_holding_it()
    {
        var (sim, battle, layout) = Frozen();
        ClearPoint(sim, layout, 0);
        Vector3[] at = [layout.Positions[0]], away = [layout.LandingZone];
        for (int t = 0; t < 10 * 30; t++) battle.Step(t % 2 == 0 ? at : away);
        int sent = OnPoint(sim, layout, 0);
        Assert.InRange(sent, 8, 13);                                          // 10 of the tide and 2 or 3 of the garrison, not 15 a second
    }

    [Fact]
    public void A_late_human_raises_the_cap()
    {
        var (sim, battle, _) = Frozen();
        for (int t = 0; t < 200 * 30; t++) battle.Step([]);
        Assert.Equal(TfwBattleRules.AliveCap(1), sim.Alive);
        for (int t = 0; t < 40 * 30; t++) battle.Step([], 2);
        Assert.Equal(TfwBattleRules.AliveCap(2), sim.Alive);
    }

    [Fact]
    public void An_imperial_fighting_on_the_point_pauses_it_too()
    {
        var (sim, battle, layout) = Frozen();
        Vector3[] at = [layout.Positions[0]];
        int before = OnPoint(sim, layout, 0);
        for (int t = 0; t < 5 * 30; t++) battle.Step(at);
        Assert.Equal(TfwPositionState.Clearing, battle.Objectives[0].State);  // its garrison still stands there
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
        Assert.True(OnPoint(sim, layout, 0) >= 3, "the Ork that took it, then tide and garrison on that very tick");
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
    [InlineData(1, 200)]
    [InlineData(2, 280)]
    [InlineData(4, 440)]
    [InlineData(8, 460)]
    public void The_count_is_the_owners_and_stays_inside_the_pool(int humans, int cap)
    {
        Assert.Equal(cap, TfwBattleRules.AliveCap(humans));
        Assert.True(TfwBattleRules.AliveCap(humans) < MusouSim.Capacity);
    }

    [Fact]
    public void The_cap_holds_with_every_point_sending()
    {
        var (sim, battle, _) = Frozen();
        for (int t = 0; t < 200 * 30; t++) battle.Step([]);
        Assert.Equal(TfwBattleRules.AliveCapSolo, sim.Alive);
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
