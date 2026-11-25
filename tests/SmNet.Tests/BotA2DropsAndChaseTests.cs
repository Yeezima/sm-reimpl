using System.Numerics;
using SmContent;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Bots A2 review (2026-11-25; test aids, ours): the 12 m drop limit per map (<see cref="BotNavigation.ClassicMaxDrop"/>, <c>--bot-old-drops</c> as a <see cref="BotNavigation.Setup"/>
/// parameter), a drop link between 8 and 12 m on a synthetic ledge, and <see cref="BotMapPlan.RouteChase"/> / <see cref="BotMapPlan.Near"/> on a straight and a stacked route.
/// </summary>
public class BotA2DropsAndChaseTests
{
    static readonly SimConfig Config = new();
    static MapContent Map(string name) => new(name, "", [], [new MapSpawn("s", new Vector3(0, 0, 0), 0f)], 0UL);

    [Fact]
    public void The_12_m_drop_limit_is_on_for_imported_Classic_maps_only_and_old_drops_restores_8_m()
    {
        var imported = BotNavigation.Setup(Map("mp_tdm_01"), Config)!.Value.Settings;
        Assert.Equal(BotNavigation.ClassicMaxDrop, imported.MaxDrop);
        Assert.True(BotNavigation.HigherDrops("mp_dlc2_map4"));
        var old = BotNavigation.Setup(Map("mp_tdm_01"), Config, oldDrops: true)!.Value.Settings;
        Assert.Equal(new NavSettings().MaxDrop, old.MaxDrop);
        Assert.Equal(imported with { MaxDrop = old.MaxDrop }, old);   // only the drop limit differs
        Assert.Equal(new NavSettings().MaxDrop, BotNavigation.Setup(Map("mp_tdm_09"), Config)!.Value.Settings.MaxDrop);   // Manufactorum unchanged
        Assert.Equal(NavSettings.From(Config), BotNavigation.Setup(Map("tfw_war"), Config)!.Value.Settings);               // Musou Mode unchanged
        Assert.False(BotNavigation.HigherDrops("cm_test"));                                                                  // Mapmaker maps unchanged
    }

    [Fact]
    public void The_drop_limit_changes_the_graph_key_and_a_10_m_ledge_gets_a_drop_link_only_with_12_m()
    {
        var level = new NavigationTests.Level()
            .Floor(-20, -10, 30, 10)        // the ground
            .Box(new Vector3(-20, -10, 0), new Vector3(0, 10, 10));   // a 10 m block with an open edge at x = 0 (its top is the deck)
        var mesh = level.Mesh();
        var s8 = NavSettings.From(Config);
        var s12 = s8 with { MaxDrop = BotNavigation.ClassicMaxDrop };
        Assert.NotEqual(NavGraph.KeyFor(mesh, null, s8), NavGraph.KeyFor(mesh, null, s12));
        var g8 = NavGraph.Build(mesh, null, s8);
        var g12 = NavGraph.Build(mesh, null, s12);
        Assert.Equal(0, CountDropsFrom(g8, 9f));
        Assert.True(CountDropsFrom(g12, 9f) > 0, "the 10 m edge has drop links with the 12 m limit");
    }

    static int CountDropsFrom(NavGraph g, float minZ)
    {
        int n = 0;
        for (int i = 0; i < g.NodeCount; i++)
        {
            if (g.Position(i).Z < minZ) continue;
            foreach (var (to, _, kind) in g.Links(i)) if (kind == NavLinkKind.Drop && g.Position(to).Z < 1f) n++;
        }
        return n;
    }

    static readonly List<Vector3> Straight = Enumerable.Range(0, 6).Select(i => new Vector3(i * 10f, 0, 0)).ToList();

    [Fact]
    public void RouteChase_walks_the_route_toward_the_point_nearest_the_enemy()
    {
        var (cost, next, target) = BotMapPlan.RouteChase(Straight, new Vector3(0, 2, 0), new Vector3(52, 3, 0), 5f);
        Assert.Equal(5, target);
        Assert.Equal(Straight[1], next);
        Assert.Equal(2f + 50f + new Vector2(2, 3).Length(), cost, 3);
        // Backwards along the route too.
        var back = BotMapPlan.RouteChase(Straight, new Vector3(50, 0, 0), new Vector3(1, 0, 0), 5f);
        Assert.Equal(0, back.Target);
        Assert.Equal(Straight[4], back.Next);
        // At the point nearest the enemy already: no route chase.
        var at = BotMapPlan.RouteChase(Straight, new Vector3(20, 0, 0), new Vector3(21, 5, 0), 5f);
        Assert.True(float.IsPositiveInfinity(at.Cost));
        Assert.Null(at.Next);
        Assert.True(float.IsPositiveInfinity(BotMapPlan.RouteChase([], Vector3.Zero, Vector3.One, 5f).Cost));
    }

    [Fact]
    public void Near_tells_stacked_floors_apart_so_a_point_overhead_is_not_the_nearest()
    {
        // A route that climbs to a walkway straight above its start: points 0..2 on the ground, 3..5 on the walkway 6 m up over the same ground.
        var route = new List<Vector3> { new(0, 0, 0), new(10, 0, 0), new(20, 0, 0), new(20, 0, 6), new(10, 0, 6), new(0, 0, 6) };
        Assert.True(BotMapPlan.Near(route[5], route[0]) >= 15f);
        var (_, next, target) = BotMapPlan.RouteChase(route, new Vector3(1, 0, 0), new Vector3(1, 0, 6), 5f);   // the bot on the ground, the enemy on the walkway above it
        Assert.Equal(5, target);
        Assert.Equal(route[1], next);   // along the ground first, not "already there" by flat distance
    }
}
