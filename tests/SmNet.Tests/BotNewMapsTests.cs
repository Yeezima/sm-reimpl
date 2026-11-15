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
}
