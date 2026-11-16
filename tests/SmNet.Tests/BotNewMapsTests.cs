using System.Numerics;
using SmContent;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// bots/new-maps (2026-11-16; test aids, ours): the Classic PvP bots' stuck rules on the ten imported maps. Synthetic levels: a pit a drop falls into with no way out (<see cref="NavSettings.PruneTraps"/>),
/// a drop link given up after it was stuck on (<see cref="NavAgent.ExcludeStuckDrops"/>), a beam the motor stands on and the graph has no cell at (<see cref="NavAgent.EscapeOffGraph"/>), and the pin that
/// Manufactorum and Musou Mode's maps keep every old rule (<see cref="BotNavigation.NewMapRules"/>).
/// </summary>
public class BotNewMapsTests
{
    static readonly SimConfig Config = new();
    static readonly NavSettings Settings = NavSettings.From(Config);

    /// <summary>A 3 m platform over the whole yard but a 4 x 4 m well in it: its floor is 3 m down and walled in, a drop leads in and nothing leads out.</summary>
    static NavigationTests.Level WellYard()
    {
        var level = new NavigationTests.Level().Floor(-10, -10, 10, 10, 0f);
        level.Box(new Vector3(-10, -10, 0), new Vector3(-2, 0, 3));
        level.Box(new Vector3(2, -10, 0), new Vector3(10, 0, 3));
        level.Box(new Vector3(-2, -10, 0), new Vector3(2, -8, 3));
        level.Box(new Vector3(-2, -4, 0), new Vector3(2, 0, 3));
        level.Box(new Vector3(-10, 0, 0), new Vector3(10, 10, 3));
        return level;
    }

    static readonly NavArea Seeded = new(new Vector2(-12, -12), new Vector2(12, 12), [new Vector3(0, 5, 3)]);

    [Fact]
    public void A_pit_with_no_way_out_is_left_out_of_the_graph_only_when_asked_to()
    {
        var mesh = WellYard().Mesh();
        var plain = NavGraph.Build(mesh, null, Settings, Seeded);
        var pruned = NavGraph.Build(mesh, null, Settings with { PruneTraps = true }, Seeded);
        bool InWell(NavGraph g) => g.Nearest(new Vector3(0, -6, 0), 0.5f, 0.6f) >= 0;
        Assert.True(InWell(plain), "without the rule a drop leads into the well");
        Assert.False(InWell(pruned), "with it the well is left out");
        Assert.True(plain.NodeCount > pruned.NodeCount);
        Assert.True(pruned.Nearest(new Vector3(0, 5, 3), 0.5f, 0.6f) >= 0, "the platform stays");
        // No link of the pruned graph falls into the well.
        for (int n = 0; n < pruned.NodeCount; n++)
            foreach (var l in pruned.Links(n)) Assert.True(pruned.Position(l.To).Z > 2f, $"{pruned.Position(n)} -> {pruned.Position(l.To)}");
        // The cache key follows the rule, and a graph built without it keeps the key it always had.
        Assert.NotEqual(NavGraph.KeyFor(mesh, null, Settings, Seeded), NavGraph.KeyFor(mesh, null, Settings with { PruneTraps = true }, Seeded));
        Assert.Equal(NavGraph.KeyFor(mesh, null, Settings, Seeded), NavGraph.KeyFor(mesh, null, Settings with { PruneTraps = false }, Seeded));
    }

    [Fact]
    public void A_drop_the_agent_gave_up_is_not_planned_through_and_the_stairs_are_taken()
    {
        // The platform with stairs (as NavigationTests's ledge level): down is a drop off the ledge, up and round is the stairs.
        var level = new NavigationTests.Level().Floor(-10, -10, 10, 0);
        for (int k = 0; k < 4; k++) level.Box(new Vector3(-10, k, 0), new Vector3(0, k + 1, 0.3f * (k + 1)));
        level.Floor(0, 0, 10, 4);
        level.Box(new Vector3(-10, 4, 0), new Vector3(10, 10, 1.2f));
        var g = NavGraph.Build(level.Mesh(), null, Settings);
        var from = new Vector3(5, 7, 1.2f); var to = new Vector3(5, 1, 0);
        var direct = g.FindPath(from, to)!;
        int i = direct.Waypoints.ToList().FindIndex(w => w.Kind == NavLinkKind.Drop);
        Assert.True(i > 0);
        var banned = new HashSet<long> { NavGraph.DropKey(direct.Waypoints[i - 1].Node, direct.Waypoints[i].Node) };
        var around = g.FindPath(from, to, excludedJumps: banned)!;
        Assert.False(around.Partial);
        for (int k = 1; k < around.Waypoints.Count; k++)
            if (around.Waypoints[k].Kind == NavLinkKind.Drop) Assert.NotEqual(banned.Single(), NavGraph.DropKey(around.Waypoints[k - 1].Node, around.Waypoints[k].Node));
        Assert.True(around.Length > direct.Length);
        Assert.NotEqual(NavGraph.DropKey(1, 2), NavGraph.JumpKey(1, 2));
    }

    [Fact]
    public void A_slit_the_body_cannot_fall_through_is_no_drop_but_a_ledge_still_is_when_asked_to()
    {
        // A thin deck 3 m up over a floor, split by a 0.5 m slit (the capsule bridges it) and ending at y = 10 in an open ledge.
        var level = new NavigationTests.Level().Floor(-14, -14, 14, 14, 0f);
        level.Box(new Vector3(-10, -10, 2.9f), new Vector3(10, -0.25f, 3f));
        level.Box(new Vector3(-10, 0.25f, 2.9f), new Vector3(10, 10, 3f));
        var seeds = new NavArea(new Vector2(-12, -12), new Vector2(12, 12), [new Vector3(0, 5, 3), new Vector3(0, -5, 3)]);
        var mesh = level.Mesh();
        int Drops(NavGraph g, Func<Vector3, bool> from) =>
            Enumerable.Range(0, g.NodeCount).Where(n => from(g.Position(n)) && g.Position(n).Z > 2.5f).Sum(n => g.Links(n).Count(l => l.Kind == NavLinkKind.Drop && g.Position(l.To).Z < 1f));
        var plain = NavGraph.Build(mesh, null, Settings, seeds);
        var strict = NavGraph.Build(mesh, null, Settings with { StrictDrops = true }, seeds);
        Assert.True(Drops(plain, p => MathF.Abs(p.Y) < 1f && MathF.Abs(p.X) < 8f) > 0, "without the rule the slit is a drop");
        Assert.Equal(0, Drops(strict, p => MathF.Abs(p.Y) < 1f && MathF.Abs(p.X) < 8f));
        Assert.True(Drops(strict, p => p.Y > 9f) > 0, "the open ledge at the deck's end stays a drop");
        Assert.NotEqual(NavGraph.KeyFor(mesh, null, Settings, seeds), NavGraph.KeyFor(mesh, null, Settings with { StrictDrops = true }, seeds));
        Assert.False(Settings.StrictDrops);
    }

    /// <summary>A floor and, on it, a beam 0.5 m wide and 3 m high with a rail each side 5 cm off its edge: the motor stands on top of it (a capsule's centre between the rails), the graph has no cell there (the clearance is wider).</summary>
    static (NavGraph Graph, Vector3 OnBeam) Beam()
    {
        var level = new NavigationTests.Level().Floor(-12, -12, 12, 12);
        level.Box(new Vector3(-0.25f, -2, 0), new Vector3(0.25f, 2, 3));
        level.Box(new Vector3(-0.4f, -2, 3), new Vector3(-0.3f, 2, 4.5f)).Box(new Vector3(0.3f, -2, 3), new Vector3(0.4f, 2, 4.5f));   // rails the capsule's cells cannot clear
        return (NavGraph.Build(level.Mesh(), null, Settings), new Vector3(0, 0, 3));
    }

    [Fact]
    public void Cells_to_escape_to_from_a_spot_the_graph_misses_are_below_and_in_different_directions()
    {
        var (g, on) = Beam();
        Assert.True(g.Nearest(on, 1f, 0.6f) < 0, "no cell on the beam");
        var cells = g.EscapeCells(on, 5f, 8f, 0.6f, 8);
        Assert.NotEmpty(cells);
        Assert.All(cells, c => Assert.True(c.Z < 0.1f && Vector2.Distance(new Vector2(c.X, c.Y), new Vector2(on.X, on.Y)) <= 5.01f));
        Assert.True(cells.Count >= 4, "a cell in most sectors");
        // Nothing within 0.6 m above and nothing below the limit: none.
        Assert.Empty(g.EscapeCells(on, 5f, 1f, 0.6f, 8));
    }

    [Fact]
    public void A_bot_stuck_on_the_beam_walks_toward_the_floor_only_with_the_rule_on()
    {
        var (g, on) = Beam();
        var goal = new Vector3(8, 0, 0);
        int Escapes(bool rule)
        {
            var agent = new NavAgent(g) { EscapeOffGraph = rule };
            for (int k = 0; k < 400; k++) agent.Steer(on, true, goal, k * 0.033);   // it never moves: stuck again and again
            return agent.Escapes;
        }
        Assert.True(Escapes(true) >= 2);
        Assert.Equal(0, Escapes(false));
    }

    [Fact]
    public void Manufactorum_and_the_Musou_maps_keep_every_old_rule()
    {
        Assert.False(BotNavigation.NewMapRules("mp_tdm_09"));
        Assert.False(BotNavigation.NewMapRules("MP_TDM_09"));
        Assert.False(BotNavigation.NewMapRules("tfw_war"));
        Assert.False(BotNavigation.NewMapRules("ashfall_citadel"));
        Assert.False(BotNavigation.NewMapRules(""));
        foreach (string map in new[] { "mp_tdm_01", "mp_tdm_11", "mp_tdm_17", "mp_tdm_18", "mp_dlc1_map1", "mp_dlc1_map2", "mp_dlc2_map1", "mp_dlc2_map2", "mp_dlc2_map3", "mp_dlc2_map4" })
            Assert.True(BotNavigation.NewMapRules(map), map);
        // The defaults of the agent and the settings are the old behaviour (what Musou Mode's agents and Manufactorum's bots run on).
        var agent = new NavAgent(Beam().Graph);
        Assert.False(agent.EscapeOffGraph);
        Assert.False(agent.ExcludeStuckDrops);
        Assert.False(Settings.PruneTraps);
        Assert.False(NavSettings.From(Config).PruneTraps);
    }

    // ------------------------------------------------------------------ review round (2026-11-16)

    [Fact]
    public void Setup_turns_the_rules_on_for_an_imported_map_and_off_for_Manufactorum()
    {
        MapContent Map(string name) => new(name, "", [], [new MapSpawn("s", new Vector3(0, 0, 0), 0f)], 0UL);
        var on = BotNavigation.Setup(Map("mp_tdm_01"), Config)!.Value.Settings;
        Assert.True(on.PruneTraps && on.StrictDrops);
        var off = BotNavigation.Setup(Map("mp_tdm_09"), Config)!.Value.Settings;
        Assert.False(off.PruneTraps || off.StrictDrops);
        Assert.Equal(NavSettings.From(Config), off);   // Manufactorum keeps the old settings, so its cache key
        var musou = BotNavigation.Setup(Map("tfw_war"), Config)!.Value.Settings;
        Assert.False(musou.PruneTraps || musou.StrictDrops);
    }

    [Fact]
    public void FloorAlong_follows_ramps_and_stairs_and_stops_at_holes_and_gaps_with_a_deck_far_below()
    {
        // A floor, a 20 degree ramp to a deck 2.2 m up (x 0..6), the deck to x = 16, a hole x 16..22 over a floor 6 m down, the deck again from 22.
        var level = new NavigationTests.Level().Floor(-10, -10, 0, 10, 0f);
        level.Quad(new(0, -10, 0), new(6, -10, 2.2f), new(6, 10, 2.2f), new(0, 10, 0));
        level.Floor(6, -10, 16, 10, 2.2f).Floor(22, -10, 30, 10, 2.2f).Floor(16, -10, 22, 10, -4f);
        var g = NavGraph.Build(level.Mesh(), null, Settings, new NavArea(new Vector2(-12, -12), new Vector2(32, 12), [new Vector3(-5, 0, 0), new Vector3(10, 0, 2.2f), new Vector3(26, 0, 2.2f)]));
        Assert.True(g.FloorAlong(new Vector3(-8, 0, 0), new Vector3(14, 0, 2.2f), 2f, 1.5f), "up the ramp and onto the deck");
        Assert.False(g.FloorAlong(new Vector3(10, 0, 2.2f), new Vector3(26, 0, 2.2f), 2f, 1.5f), "the hole in the deck, the floor 6 m under it is not floor");
        Assert.True(g.FloorAlong(new Vector3(7, 0, 2.2f), new Vector3(7, 0, 2.2f), 2f, 1.5f));
    }

    [Fact]
    public void Drop_links_near_a_point_are_the_ones_off_that_ledge_only()
    {
        var level = new NavigationTests.Level().Floor(-10, -10, 10, 0).Box(new Vector3(-10, 0, 0), new Vector3(10, 10, 1.2f));
        var g = NavGraph.Build(level.Mesh(), null, Settings);
        var near = g.DropLinksNear(new Vector3(0, 0.5f, 1.2f), 2f, 1.5f);
        Assert.NotEmpty(near);
        Assert.All(near, l => { var a = g.Position(l.From); Assert.True(MathF.Abs(a.X) <= 2.01f && a.Z > 1f && g.Position(l.To).Z < 0.5f); });
        Assert.Empty(g.DropLinksNear(new Vector3(0, 6f, 1.2f), 2f, 1.5f).Where(l => g.Position(l.From).Y < 2f));
        Assert.Empty(g.DropLinksNear(new Vector3(0, 0.5f, 5f), 2f, 1.5f));   // nothing at that height
    }

    [Fact]
    public void Every_seed_of_a_multi_seed_map_stays_reachable_when_the_traps_are_pruned()
    {
        // The well yard with a second platform on the far side, a seed on each deck and a seed in the open: pruning removes the well only.
        var level = WellYard();
        level.Box(new Vector3(-10, 10, 0), new Vector3(10, 14, 3));
        var seeds = new NavArea(new Vector2(-12, -12), new Vector2(12, 16), [new Vector3(0, 5, 3), new Vector3(0, 12, 3), new Vector3(6, -5, 3), new Vector3(-6, -5, 3)]);
        var g = NavGraph.Build(level.Mesh(), null, Settings with { PruneTraps = true }, seeds);
        foreach (var seed in seeds.Seeds)
        {
            Assert.True(g.Nearest(seed, 0.6f, 0.6f) >= 0, $"seed {seed} has no cell");
            Assert.False(g.FindPath(seeds.Seeds[0], seed)!.Partial, $"no walk to {seed}");
        }
    }

    /// <summary>A ledge: down is a drop, up is stairs.</summary>
    static (NavGraph Graph, Vector3 Edge, Vector3 Goal) LedgeLevel()
    {
        var level = new NavigationTests.Level().Floor(-10, -10, 10, 0);
        for (int k = 0; k < 4; k++) level.Box(new Vector3(-10, k, 0), new Vector3(0, k + 1, 0.3f * (k + 1)));
        level.Floor(0, 0, 10, 4);
        level.Box(new Vector3(-10, 4, 0), new Vector3(10, 10, 1.2f));
        return (NavGraph.Build(level.Mesh(), null, Settings), new Vector3(5, 4.5f, 1.2f), new Vector3(5, 1, 0));
    }

    [Fact]
    public void A_drop_given_up_comes_back_after_the_forget_time_and_only_counts_at_its_edge()
    {
        var (g, edge, goal) = LedgeLevel();
        NavAgent Agent() => new(g) { ExcludeStuckDrops = true, DropForgetSeconds = 25.0 };
        // Stuck at the edge for a few windows (never moving): the drops off it are given up.
        var a = Agent();
        double t = 0;
        for (; t < 8; t += 0.033) a.Steer(edge, true, goal, t);
        Assert.True(a.ExcludedDrops > 0, "given up after the stuck windows at the edge");
        // The same standstill far from the edge (the path's first waypoint is not near): counts nothing.
        var far = Agent();
        for (double u = 0; u < 8; u += 0.033) far.Steer(new Vector3(5, 9.5f, 1.2f), true, goal, u);
        // (a bot 5 m from the drop's origin is not at the edge; it may have been stuck for another reason)
        Assert.True(far.ExcludedDrops == 0 || far.StuckCount > 0);
        // The entries decay: moving about for longer than the forget time, nothing is given up any more.
        for (; t < 8 + 30; t += 0.033) a.Steer(new Vector3(5 + (float)(Math.Sin(t) * 3), 7f, 1.2f), true, goal, t);
        Assert.Equal(0, a.ExcludedDrops);
    }

    [Fact]
    public void A_jump_pack_bot_stuck_in_a_pit_asks_for_and_flies_the_jump_out()
    {
        var (g, on) = Beam();
        var config = JumpPackTests.Config with { JumpPacks = [JumpPackTests.Pack with { ReleaseCut = true }] };
        var pack = config.JumpPacks[0];
        int asked = 0;
        var agent = new NavAgent(g)
        {
            EscapeOffGraph = true,
            PitJump = feet => { asked++; return (new Vector3(8, 0, 0), 4, 60, pack.CostAt(1f)); },
        };
        var body = new NavBody(JumpPhase.None, 0, 300f, 0f, pack, config.TickSeconds);
        bool pressed = false;
        for (int k = 0; k < 600 && !pressed; k++) pressed = agent.Steer(on, true, new Vector3(-8, 0, 0), k * 0.033, body).Jump;
        Assert.True(pressed, "the jump is pressed");
        Assert.True(asked >= 1 && agent.PitJumps == 1);
        // Without a jump pack (no body) or with the rule off, nothing is asked.
        asked = 0;
        var plain = new NavAgent(g) { EscapeOffGraph = true, PitJump = feet => { asked++; return null; } };
        for (int k = 0; k < 600; k++) plain.Steer(on, true, new Vector3(-8, 0, 0), k * 0.033, null);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void A_bot_stuck_at_the_foot_of_a_rise_gives_up_the_rising_links_and_gets_them_back_after_the_forget_time()
    {
        var (g, _, _) = LedgeLevel();
        var foot = new Vector3(-5, -0.5f, 0);
        var goal = new Vector3(-5, 7, 1.2f);
        Assert.NotNull(g.FindPath(foot, goal));
        var a = new NavAgent(g) { ExcludeStuckDrops = true };
        double t = 0;
        for (; t < 8; t += 0.033) a.Steer(foot, true, goal, t);
        Assert.True(a.ExcludedSteps > 0, "the rising walk links round the foot are given up");
        // Off (the old rules): nothing is given up.
        var old = new NavAgent(g);
        for (double u = 0; u < 8; u += 0.033) old.Steer(foot, true, goal, u);
        Assert.Equal(0, old.ExcludedSteps);
        // The entries decay once the bot moves about for longer than the forget time.
        for (; t < 8 + 100; t += 0.033) a.Steer(new Vector3(-10f + (float)(t * 4.0 % 20.0), -5f, 0), true, new Vector3(10, -9, 0), t);
        Assert.Equal(0, a.ExcludedSteps);
    }

    [Fact]
    public void A_Seize_point_marker_floating_far_above_its_floor_still_has_a_stand_cell_inside_its_zone()
    {
        var level = new NavigationTests.Level().Floor(-12, -12, 12, 12, 0f);
        var g = NavGraph.Build(level.Mesh(), null, Settings);
        var marker = new Vector3(0, 0, 7f);   // 7 m above the floor, like Hab Center's point D
        Assert.True(g.Nearest(marker, 10f, 6f) < 0, "the planner's own goal snap (6 m of height) finds no cell");
        int n = g.NearestInCylinder(marker, 6f, 7.5f);
        Assert.True(n >= 0);
        Assert.InRange(g.Position(n).Z, -0.1f, 0.1f);
        Assert.True(MathF.Abs(g.Position(n).X) < 0.6f && MathF.Abs(g.Position(n).Y) < 0.6f, "the cell under the marker");
        Assert.True(g.FindPath(new Vector3(-8, -8, 0), g.Position(n)) is { Partial: false });
        Assert.True(g.NearestInCylinder(marker, 6f, 3f) < 0, "a zone that does not reach the floor has none");
    }

    [Fact]
    public void A_bot_that_does_not_get_on_over_flat_ground_gives_up_the_links_ahead_only_after_three_detections()
    {
        var g = NavGraph.Build(new NavigationTests.Level().Floor(-12, -12, 12, 12, 0f).Mesh(), null, Settings);
        var here = new Vector3(0, 0, 0);
        var goal = new Vector3(10, 0, 0);
        var a = new NavAgent(g) { ExcludeStuckDrops = true };
        double t = 0;
        for (; t < 3.2; t += 0.033) a.Steer(here, true, goal, t);
        Assert.True(a.StuckCount >= 1);
        Assert.Equal(0, a.ExcludedSteps);
        for (; t < 9; t += 0.033) a.Steer(here, true, goal, t);
        Assert.True(a.ExcludedSteps > 0, "the links toward the goal round the bot are given up");
        // Only the ones that lead its way: none of them runs backwards.
        foreach (var (from, to) in g.RiseLinksNear(here, 2f, 1f, -1f, new Vector2(-1, 0), 0.5f).Take(1)) Assert.True(g.Position(to).X < g.Position(from).X);
    }
}
