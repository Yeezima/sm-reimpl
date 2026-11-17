using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Fortress;

namespace SmNet.Tests.Extras.Warfront.Fortress;

/// <summary>
/// Warfront W14b: the fortress battle on a stub arena (raw triangles, no Ashfall type, no generated map): the layout from the interface, the final assault with its one leashed Warboss, the stages and the source rules,
/// the win that ends the battle on the tick he dies, and the campaign's fortress rules driven by the real battle's results.
/// </summary>
public class WarfrontFortressBattleTests
{
    /// <summary>An arena inside the test world's walls: landing south, plaza north (radius 25 round (0, 70)), three stages of sources, eight elite posts, three routes.</summary>
    sealed class StubArena : IWarfrontArena
    {
        public string Name => "stub fortress";
        public uint Version => 1;
        public CollisionWorld Collision { get; }
        public ulong Hash => 0x1414_0001UL;
        public Vector3 LandingZone => new(0f, -108f, 0f);
        public Vector3 BossSpot => new(0f, 70f, 0f);
        public float ArenaRadius => 25f;
        public IReadOnlyList<WarfrontArenaSource> Sources { get; }
        public IReadOnlyList<WarfrontArenaPost> Posts { get; }
        public IReadOnlyList<WarfrontArenaRoute> Routes { get; } =
        [
            new("centre", [new(0f, -20f, 0f), new(0f, 10f, 0f)]),
            new("west", [new(-50f, -80f, 0f), new(-60f, -20f, 0f), new(-60f, 30f, 0f)]),
            new("east", [new(20f, -80f, 0f), new(60f, -20f, 0f), new(60f, 30f, 0f)]),
        ];
        public IReadOnlyList<WarfrontArenaDimension> RouteWidths { get; } = [new("pass", 10f, "m")];
        public NavArea WalkArea { get; }

        public StubArena(int sources = 9)
        {
            WarfrontArenaSource[] all =
            [
                new("s0a", new(-40f, -60f, 0f), 0), new("s0b", new(40f, -60f, 0f), 0), new("s0c", new(0f, -40f, 0f), 0),
                new("s1a", new(-60f, 0f, 0f), 1), new("s1b", new(60f, 0f, 0f), 1), new("s1c", new(0f, 10f, 0f), 1),
                new("s2a", new(-15f, 70f, 0f), 2), new("s2b", new(15f, 70f, 0f), 2), new("s2c", new(0f, 92f, 0f), 2),
            ];
            Sources = [.. all.Take(sources)];
            var posts = new List<WarfrontArenaPost>();
            for (int i = 0; i < 8; i++) posts.Add(new($"elite {i}", new(-70f + i * 20f, -30f + (i % 2) * 40f, 0f), "elite"));
            posts.Add(new("landing", new(0f, -108f, 0f), "landing"));
            Posts = posts;
            var v = new List<Vector3>();
            Box(v, -110f, -110f, -2f, 110f, 110f, 0f);
            var corners = v.ToArray();
            int[] idx = Enumerable.Range(0, corners.Length).ToArray();
            Collision = new CollisionWorld(new StaticMesh(corners, idx), new StaticMesh(corners, idx));
            WalkArea = new NavArea(new Vector2(-110f, -110f), new Vector2(110f, 110f), [LandingZone, BossSpot]);
        }

        static void Box(List<Vector3> v, float x0, float y0, float z0, float x1, float y1, float z1)
        {
            Vector3[] c = [new(x0, y0, z0), new(x1, y0, z0), new(x1, y1, z0), new(x0, y1, z0), new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1)];
            int[][] f = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
            foreach (var q in f) { v.AddRange([c[q[0]], c[q[1]], c[q[2]]]); v.AddRange([c[q[0]], c[q[2]], c[q[3]]]); }
        }
    }

    static int Count(World w, Func<MusouEnemy, bool> pick)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        return all.Take(n).Count(e => e.Alive && pick(e));
    }

    /// <summary>A Warfront world, the stub arena's setup on the run's rules, the fronts at the stronghold, deployed: the fortress battle.</summary>
    static (World World, TfwBattle Battle, WarfrontFortressSetup Setup) Fortress(int humans = 1, IWarfrontArena? arena = null)
    {
        var w = WarfrontBattleTests.War(humans);
        var setup = WarfrontFortressSetup.For(arena ?? new StubArena());
        w.Tfw!.Warfront!.Fortress = setup;
        w.Tfw.War.SetFrontsForTests(WarRules.FrontStronghold);
        var battle = TfwRig.Deploy(w);
        return (w, battle, setup);
    }

    static void Stand(World w, int ticks)
    {
        for (int k = 0; k < ticks; k++) w.Step(_ => default);
    }

    // ---- the layout, from the interface alone

    [Fact]
    public void The_layout_comes_from_the_interface_with_waypoints_a_boss_spot_for_hq_and_the_first_four_sources_at_the_battles_four_stages()
    {
        var arena = new StubArena();
        var plan = WarfrontArenaLayout.Plan(arena);
        var layout = plan.Layout;
        Assert.Equal(arena.LandingZone, layout.LandingZone);
        Assert.Equal(arena.BossSpot, layout.BossSpot);
        Assert.Equal(arena.BossSpot, layout.Hq);
        Assert.Equal(3, layout.Positions.Length);
        Assert.Equal(plan.Gate, layout.Positions[1]);     // the layout's "centre": a fresh spawn faces the gate
        Assert.Equal(new Vector3(20f, -80f, 0f), plan.Gate);    // the first stop nearest the landing zone
        Assert.Equal(new Vector3(-50f, -80f, 0f), plan.Breach);  // the next entry at least 8 m from it
        Assert.Equal(new Vector3(0f, 10f, 0f), plan.PlazaGate);  // the last stop nearest the boss spot
        Assert.Equal(arena.Sources.Count, layout.Sources.Length);
        Assert.Equal([0, 0, 1, 2], plan.SourceStage.Take(4));
        Assert.Equal(2, plan.MaxStage);
        Assert.Equal(8, plan.ElitePosts.Count);           // only the "elite" group
        Assert.Equal(2, plan.ApproachPosts.Count);        // one on each of the last two routes
        Assert.Equal(new Vector3(-60f, -20f, 0f), plan.ApproachPosts[0]);
        Assert.Equal(arena.Sources.Select(s => s.Position).OrderBy(p => p.X).ThenBy(p => p.Y), layout.Sources.OrderBy(p => p.X).ThenBy(p => p.Y));   // the same sources, reordered
        Assert.Equal(plan.SourceStage.Length, layout.Sources.Length);
    }

    [Fact]
    public void An_arena_with_fewer_than_four_sources_still_gives_the_battle_four_spawn_points()
    {
        var plan = WarfrontArenaLayout.Plan(new StubArena(sources: 2));
        Assert.True(plan.Layout.Sources.Length >= 4);
        Assert.Equal(plan.Layout.Sources.Length, plan.SourceStage.Length);
        Assert.All(plan.SourceStage, s => Assert.InRange(s, 0, plan.MaxStage));
    }

    [Fact]
    public void The_layout_is_deterministic()
    {
        var a = WarfrontArenaLayout.Plan(new StubArena());
        var b = WarfrontArenaLayout.Plan(new StubArena());
        Assert.Equal(a.SourceStage, b.SourceStage);
        Assert.Equal(a.Layout.Sources, b.Layout.Sources);
        Assert.Equal(a.Layout.Positions, b.Layout.Positions);
    }

    [Fact]
    public void Nothing_in_the_battle_code_names_the_generated_map()
    {
        string root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "SmSim", "Extras", "Warfront", "Fortress"))) root = Path.GetDirectoryName(root)!;
        Assert.NotNull(root);
        foreach (string file in new[] { "TfwBattle.WarfrontFortress.cs", "WarfrontFortressBattleRules.cs", "WarfrontArenaLayout.cs", "TfwRun.WarfrontFortress.cs" })
        {
            string text = File.ReadAllText(Path.Combine(root, "src", "SmSim", "Extras", "Warfront", "Fortress", file));
            Assert.DoesNotContain("WarfrontFortressMap", text);
            Assert.DoesNotContain("AshfallLevel", text);
            Assert.DoesNotContain("WarfrontFortressBuild", text);
        }
    }

    // ---- the battle

    [Fact]
    public void The_battle_is_the_final_assault_with_elite_share_0_6_one_leashed_warboss_at_the_boss_spot_and_the_posts_and_approaches_held()
    {
        var (w, battle, setup) = Fortress();
        var run = w.Tfw!;
        Assert.Equal(TfwRunPhase.FinalAssault, run.Phase);
        Assert.True(battle.IsWarfrontFortress);
        Assert.Equal(0.6f, battle.EliteShare);
        Assert.Equal(setup.Plan.Layout, battle.Layout);
        Assert.Equal(TfwPositionState.Secured, battle.Objectives[0].State);   // waypoints: Secured from the start, never an objective
        Assert.NotEqual((ushort)0, battle.BossId);
        Assert.True(w.Musou!.TryGet(battle.BossId, out var boss));
        Assert.Equal(MusouEnemyType.Warboss, boss.Type);
        Assert.True(Vector3.Distance(boss.Position, setup.Plan.BossSpot) < 1f);
        Assert.True(w.Musou.Leashed(battle.BossId));
        Assert.Equal(1, Count(w, e => e.Type == MusouEnemyType.Warboss));
        Assert.Equal(8, Count(w, e => e.Type == MusouEnemyType.Nob && w.Musou.Leashed(e.Id)));
        Assert.Equal(8, Count(w, e => e.Type == MusouEnemyType.BigShootaNob && w.Musou.Leashed(e.Id) && e.Position.Y < 60f));
        Assert.Equal(2, battle.FortressBossOrksMade);
        Assert.Equal(16, battle.FortressElitesMade);
        Assert.Equal(2, Count(w, e => MusouBossOrks.Is(e.Type)));
    }

    [Fact]
    public void Killing_the_warboss_ends_the_battle_won_on_that_tick_with_no_victory_wave_no_pickup_and_the_result_through_the_node_seam()
    {
        var (w, battle, _) = Fortress();
        var run = w.Tfw!;
        TfwMapResult? ended = null;
        (TfwLane, int, bool)? resolved = null;
        run.Warfront!.NodeEnded = (_, _, r) => ended = r;
        run.Warfront.NodeResolved = (lane, node, won) => resolved = (lane, node, won);
        Stand(w, 60);
        Assert.Null(ended);
        Assert.True(w.Musou!.Damage(battle.BossId, TfwRig.Human, 1e7f, Vector3.Zero, out _));
        w.Step(_ => default);   // the kill's tick
        Assert.Equal(TfwMapResult.Won, ended);
        Assert.Equal(TfwRunPhase.Hub, run.Phase);
        Assert.Null(run.Battle);
        Assert.Equal(0, run.PickupTicks);
        Assert.Equal(0, battle.VictoryWaveSent);
        Assert.Equal(0, battle.VictoryWaveLeft);
        Assert.True(TfwRig.InStormbird(w.Players[TfwRig.Human].Position));   // everyone back aboard the same tick
        Assert.True(resolved?.Item3);
    }

    [Fact]
    public void The_fortress_win_pays_no_capture_xp_for_d_while_a_node_win_still_does()
    {
        var (w, battle, _) = Fortress();
        var progression = w.Tfw!.Warfront!.Progression;
        Assert.Empty(progression.OnBattleChange(battle, TfwBattleChange.Won));
        var node = WarfrontBattleTests.War();
        var nodeBattle = TfwRig.Deploy(node);
        Assert.Equal([3], node.Tfw!.Warfront!.Progression.OnBattleChange(nodeBattle, TfwBattleChange.Won).ToArray());
    }

    [Fact]
    public void A_wipe_ends_the_battle_lost()
    {
        var (w, _, _) = Fortress();
        (TfwLane, int, bool)? resolved = null;
        w.Tfw!.Warfront!.NodeResolved = (lane, node, won) => resolved = (lane, node, won);
        Assert.True(w.TfwKillForTests(TfwRig.Human));
        Stand(w, 750 + 60);
        Assert.Equal(TfwRunPhase.Hub, w.Tfw.Phase);
        Assert.False(resolved?.Item3);
        Assert.NotNull(resolved);
    }

    // ---- the Warboss's leash

    [Fact]
    public void The_warboss_stays_leashed_to_the_plaza_until_a_human_enters_it()
    {
        var (w, battle, setup) = Fortress();
        Stand(w, 90);
        Assert.True(w.Musou!.Leashed(battle.BossId));
        // a human at the gate does not free him
        battle.FortressObserve([setup.Plan.Gate]);
        Assert.True(w.Musou.Leashed(battle.BossId));
        // inside the plaza's radius he is free for good
        battle.FortressObserve([setup.Plan.BossSpot + new Vector3(0f, -20f, 0f)]);
        Assert.False(w.Musou.Leashed(battle.BossId));
        Assert.True(battle.WarfrontFortressFlow!.PlazaEntered);
    }

    // ---- the stages

    [Fact]
    public void Sources_of_stage_1_and_2_stay_closed_until_their_trigger_and_the_stage_never_goes_back()
    {
        var (w, battle, setup) = Fortress();
        var plan = setup.Plan;
        Vector3[] far = [plan.Layout.LandingZone];
        battle.FortressObserve(far);
        Assert.True(battle.SourceActive(0));    // stage 0 from the landing
        Assert.True(battle.SourceActive(1));
        Assert.False(battle.SourceActive(2));   // the strip
        Assert.False(battle.SourceActive(3));   // the plaza
        for (int i = 4; i < plan.SourceStage.Length; i++) Assert.Equal(plan.SourceStage[i] == 0, battle.SourceActive(i));
        battle.FortressObserve([plan.Gate + new Vector3(2f, 0f, 0f)]);   // a human passes the gate
        Assert.Equal(1, battle.WarfrontFortressFlow!.Stage);
        Assert.True(battle.SourceActive(2));
        Assert.False(battle.SourceActive(3));
        battle.FortressObserve(far);
        Assert.Equal(1, battle.WarfrontFortressFlow.Stage);   // nobody at the gate now: still open
        // the breach opens stage 1 too (a fresh battle)
        var (_, other, otherSetup) = Fortress();
        other.FortressObserve([otherSetup.Plan.Breach]);
        Assert.Equal(1, other.WarfrontFortressFlow!.Stage);
        // the plaza
        battle.FortressObserve([plan.BossSpot + new Vector3(0f, -24f, 0f)]);
        Assert.Equal(2, battle.WarfrontFortressFlow.Stage);
        battle.FortressObserve(far);
        Assert.Equal(2, battle.WarfrontFortressFlow.Stage);
        Assert.True(battle.SourceActive(3));
    }

    [Fact]
    public void No_source_sends_within_30_m_of_a_human_and_the_crowds_waypoint_counts_too()
    {
        var (_, battle, setup) = Fortress();
        var plan = setup.Plan;
        var at = plan.Layout.Sources[0];
        battle.FortressObserve([at + new Vector3(29f, 0f, 0f)]);
        Assert.False(battle.SourceActive(0));
        battle.FortressObserve([at + new Vector3(31f, 0f, 0f), plan.Layout.Positions[0]]);   // the source is clear but a human stands on its crowd's waypoint
        Assert.False(battle.SourceActive(0));
        battle.FortressObserve([plan.Layout.LandingZone]);
        Assert.True(battle.SourceActive(0));
        // stage 1 open: the same rule for the strip's sources
        battle.FortressObserve([plan.Gate]);
        var strip = plan.Layout.Sources[2];
        battle.FortressObserve([plan.Gate, strip + new Vector3(0f, 29f, 0f)]);
        Assert.False(battle.SourceActive(2));
        battle.FortressObserve([plan.Gate, strip + new Vector3(0f, 31f, 0f)]);
        Assert.True(battle.SourceActive(2));
    }

    [Fact]
    public void No_stage_2_source_sends_in_sight_of_a_human_standing_in_the_plaza()
    {
        var (_, battle, setup) = Fortress();
        var plan = setup.Plan;
        var human = plan.BossSpot + new Vector3(0f, -22f, 0f);   // the plaza's south edge, 45 m or so from the north source
        int north = Array.FindIndex(plan.Layout.Sources, s => s == new Vector3(0f, 92f, 0f));
        Assert.True(north >= 0 && plan.SourceStage[north] == 2);
        Assert.True(Vector3.Distance(human, plan.Layout.Sources[north]) > 30f);   // the 30 m rule alone would allow it
        battle.FortressObserve([human]);
        Assert.False(battle.SourceActive(north));   // no sight test given: the plaza human sees every plaza source
        setup.Sight = (_, _) => true;
        Assert.False(battle.SourceActive(north));   // a clear line: closed
        setup.Sight = (_, _) => false;
        Assert.True(battle.SourceActive(north));    // behind a wall: open
        setup.Sight = (_, _) => true;
        battle.FortressObserve([plan.Gate]);        // nobody in the plaza any more (the stage stays at 2)
        Assert.True(battle.SourceActive(north));
    }

    // ---- exactly one Warboss

    [Fact]
    public void Exactly_one_warboss_exists_in_a_fortress_battle_over_a_run_and_none_on_a_lane_node()
    {
        var (w, battle, _) = Fortress();
        for (int k = 0; k < 40; k++)
        {
            Stand(w, 15);
            Assert.Equal(1, Count(w, e => e.Type == MusouEnemyType.Warboss));
            Assert.Equal(0, Count(w, e => e.Type == MusouEnemyType.Warboss && e.Id != battle.BossId));
        }
        var node = WarfrontBattleTests.War();
        TfwRig.Deploy(node, TfwLane.B);
        for (int k = 0; k < 40; k++)
        {
            Stand(node, 15);
            Assert.Equal(0, Count(node, e => e.Type == MusouEnemyType.Warboss));
        }
    }

    // ---- the campaign, scripted with forced rolls, ended by the real battle's results

    [Fact]
    public void The_campaign_offers_the_fortress_after_a_cleared_lane_drops_the_lane_on_a_real_wipe_and_wins_on_a_real_warboss_kill()
    {
        var c = new WarfrontCampaign(7, new ForcedRolls(Enumerable.Repeat(false, 20).ToArray()));
        c.Join(101, out _);
        c.Embark();
        for (int i = 0; i < 3; i++) { Assert.True(c.TryDeploy(101, WarfrontTarget.Node(0), out _)); c.BeginBattle(); c.Resolve(WarfrontOutcome.Win); }
        Assert.Contains(WarfrontTarget.Fortress(0), c.AvailableTargets());

        // the first attempt: a real wipe
        Assert.True(c.TryDeploy(101, WarfrontTarget.Fortress(0), out _));
        c.BeginBattle();
        var (w, _, _) = Fortress();
        w.Tfw!.Warfront!.NodeEnded = (_, _, r) => c.Resolve(r == TfwMapResult.Won ? WarfrontOutcome.Win : WarfrontOutcome.Wipe);
        Assert.True(w.TfwKillForTests(TfwRig.Human));
        Stand(w, 750 + 60);
        Assert.Equal(WarfrontPhase.Ship, c.Phase);
        Assert.Equal(3, c.Front(0));                                                     // the cleared lane dropped to node 3
        Assert.DoesNotContain(c.AvailableTargets(), t => t.Kind == WarfrontTargetKind.Fortress);   // and the fortress is locked
        Assert.Equal(4, c.Deployments);

        // clear it again, then a real Warboss kill wins the campaign
        Assert.True(c.TryDeploy(101, WarfrontTarget.Node(0), out _)); c.BeginBattle(); c.Resolve(WarfrontOutcome.Win);
        Assert.Contains(WarfrontTarget.Fortress(0), c.AvailableTargets());
        Assert.True(c.TryDeploy(101, WarfrontTarget.Fortress(0), out _));
        c.BeginBattle();
        var (w2, battle2, _) = Fortress();
        w2.Tfw!.Warfront!.NodeEnded = (_, _, r) => c.Resolve(r == TfwMapResult.Won ? WarfrontOutcome.Win : WarfrontOutcome.Wipe);
        Assert.True(w2.Musou!.Damage(battle2.BossId, TfwRig.Human, 1e7f, Vector3.Zero, out _));
        w2.Step(_ => default);
        Assert.Equal(WarfrontPhase.Won, c.Phase);
    }

    [Fact]
    public void A_human_dropped_inside_the_walls_opens_stage_1_without_passing_an_entry()
    {
        var (_, battle, setup) = Fortress();
        var plan = setup.Plan;
        battle.FortressObserve([new Vector3(30f, -40f, 0f)]);   // mid-compound, far from both entries
        Assert.Equal(1, battle.WarfrontFortressFlow!.Stage);
    }

    [Fact]
    public void A_whole_fortress_battle_pays_no_capture_xp_and_the_progression_returns_nothing()
    {
        var (w, battle, _) = Fortress();
        var progression = w.Tfw!.Warfront!.Progression;
        Assert.Empty(progression.OnBattleChange(battle, TfwBattleChange.PositionSecured | TfwBattleChange.Won));
        Assert.Empty(progression.OnBattleChange(battle, TfwBattleChange.PositionSecured));
    }

    [Fact]
    public void The_fortress_leash_groups_meet_none_of_the_battles_own()
    {
        // The two ranges (W14b review 2): the fortress's leash groups are GroupFirst (200) up to 200 + elite posts + approach posts (the live arena: 8 + 2, so 200 to 209). Musou's own battle groups are 1 to 23 (garrisons 1 to 3, the
        // commander 4, the positions' crowds from 6, the musters 9 to 12, the patrol columns 13 on, the Big Shootas 21 to 23), checked below with a margin of 20; groups from 1000 (the HQ defence's waves, 1000 + 100 w + c; the armies from
        // 100000) are not checked against: an HQ battle and a fortress battle never share a process.
        var own = typeof(TfwBattle).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int) && f.Name.Contains("Group")).Select(f => (int)f.GetRawConstantValue()!).ToArray();
        Assert.NotEmpty(own);
        var plan = WarfrontArenaLayout.Plan(new StubArena());
        int last = WarfrontFortressBattleRules.GroupFirst + plan.ElitePosts.Count + plan.ApproachPosts.Count;
        foreach (int g in own.Where(g => g < 1000)) Assert.True(g + 20 < WarfrontFortressBattleRules.GroupFirst, $"a battle group {g} is near the fortress's");   // patrol columns run from 13 on, the Big Shootas to 23; the HQ defence's wave groups (1000 and up, W13b) never share a battle with the fortress
        Assert.True(last < 1000);   // below the HQ's wave groups, and so below the armies' (100000)
    }

    [Fact]
    public void The_runs_id_list_and_feet_list_have_the_same_length_in_a_fortress_tick()
    {
        var (w, battle, _) = Fortress(humans: 2);
        Stand(w, 5);
        Assert.Equal(w.Players.Count(p => p.Value.Alive), battle.FortressPlayerCount);
    }
}
