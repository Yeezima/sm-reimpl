using System.Numerics;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The armies route on ways as wide as a formation (owner, 2026-11-09: "Yes, route on avenues"; <see cref="NavGraph.FindPathWide"/>, <see cref="TfwArmyRules.RouteHalfWidth"/>): a synthetic block with an alley through it, then
/// the war's own routes on Ashfall's city, field and draft-10 maps. A single body's search (<see cref="NavGraph.FindPath"/>) is unchanged.
/// </summary>
public class TfwWideRouteTests(ITestOutputHelper output)
{
    static readonly NavSettings Settings = NavSettings.From(new SimConfig());

    /// <summary>A 100 x 60 m floor with a 60 x 40 m block in the middle cut by a 3 m alley; 10 m streets round it.</summary>
    static NavGraph Block()
    {
        var level = new NavigationTests.Level().Floor(0f, 0f, 100f, 60f);
        level.Box(new Vector3(20f, 10f, 0f), new Vector3(80f, 28.5f, 4f));
        level.Box(new Vector3(20f, 31.5f, 0f), new Vector3(80f, 50f, 4f));
        return NavGraph.Build(level.Mesh(), null, Settings);
    }

    static bool ThroughAlley(NavPath p)
    {
        for (int i = 1; i < p.Waypoints.Count; i++)
        {
            var a = p.Waypoints[i - 1].Position; var b = p.Waypoints[i].Position;
            for (float t = 0f; t <= 1f; t += 0.02f) { var q = Vector3.Lerp(a, b, t); if (q.X > 25f && q.X < 75f && q.Y > 27f && q.Y < 33f) return true; }
        }
        return false;
    }

    [Fact]
    public void A_formation_goes_round_the_block_where_a_single_body_takes_the_alley()
    {
        var g = Block();
        var from = new Vector3(5f, 30f, 0f); var to = new Vector3(95f, 30f, 0f);
        var one = g.FindPath(from, to);
        var wide = g.FindPathWide(from, to, 4f, TfwArmyRules.RoutePenalty);
        Assert.NotNull(one); Assert.NotNull(wide);
        output.WriteLine($"single body {one!.Length:0} m, formation {wide!.Length:0} m");
        Assert.False(one.Partial || wide.Partial);
        Assert.True(ThroughAlley(one), "a single body takes the 3 m alley");
        Assert.False(ThroughAlley(wide), "a formation goes round by a 10 m street");
        Assert.True(wide.Length > one.Length);
        // the clearance: a street's middle has about its half width, the alley's about 1.5 m
        var clear = g.Clearance();
        Assert.InRange(clear[g.Nearest(new Vector3(50f, 5f, 0f), 0.5f, 0.6f)], 3.5f, 6f);
        Assert.InRange(clear[g.Nearest(new Vector3(50f, 30f, 0f), 0.5f, 0.6f)], 0.5f, 2.5f);
    }

    [Fact]
    public void Without_a_wide_way_the_formation_still_gets_through_the_narrow_one()
    {
        // close both streets: the alley is the only way, and the wide search still finds it (a gate stays passable)
        var level = new NavigationTests.Level().Floor(0f, 0f, 100f, 60f);
        level.Box(new Vector3(20f, 0f, 0f), new Vector3(80f, 28.5f, 4f));
        level.Box(new Vector3(20f, 31.5f, 0f), new Vector3(80f, 60f, 4f));
        var g = NavGraph.Build(level.Mesh(), null, Settings);
        var wide = g.FindPathWide(new Vector3(5f, 30f, 0f), new Vector3(95f, 30f, 0f), TfwArmyRules.RouteHalfWidth, TfwArmyRules.RoutePenalty);
        Assert.NotNull(wide);
        Assert.False(wide!.Partial);
    }

    [Theory]
    [InlineData(TfwMap.Name, 20f)]           // the city: every route outside the stronghold on its avenues
    [InlineData(TfwMap.FieldName, 20f)]
    [InlineData(TfwMap.Draft10Name, 13f)]    // draft 10's streets are 14 to 20 m at their narrowest: the widest it has
    public void The_war_s_army_routes_from_the_landing_zone_keep_to_the_wide_ways(string map, float least)
    {
        var data = TfwWorld.Load("", out var problem, map) ?? throw new InvalidOperationException(problem);
        string dir = Path.Combine(Path.GetTempPath(), "sm-wide-route-nav-" + Guid.NewGuid().ToString("N"));   // a folder of its own: never the private cache's nav folder
        NavGraph nav;
        try { nav = TfwWorld.Nav(data.Collision, data.Field, new SimConfig(), out _, dir); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        var grid = new AshfallGrid(AshfallMap.Build(TfwMap.LayoutOf(map)));
        foreach (var p in data.Field.Points)
        {
            var before = nav.FindPath(data.Field.MarineStart, p.Centre, 10f, TfwPatrolRules.RouteExpansions);
            var after = nav.FindPathWide(data.Field.MarineStart, p.Centre, TfwArmyRules.RouteHalfWidth, TfwArmyRules.RoutePenalty, 10f, TfwArmyRules.WideRouteExpansions);
            Assert.NotNull(after);
            Assert.False(after!.Partial);
            float wb = before is null ? 0f : Narrowest(grid, before), wa = Narrowest(grid, after);
            output.WriteLine($"{map} landing zone to {p.Centre}: before {before?.Length:0} m, narrowest {wb:0.0} m; after {after.Length:0} m, narrowest {wa:0.0} m");
            Assert.True(wa >= least, $"{map}: {wa:0.0} m");
            if (wb < least) Assert.True(wa > wb, $"{map}: {wa:0.0} m after, {wb:0.0} m before");   // where the single body squeezed, the formation's way is wider
        }
    }

    static float Narrowest(AshfallGrid grid, NavPath path)
    {
        float min = float.MaxValue;
        for (int i = 1; i < path.Waypoints.Count; i++)
        {
            var a = new Vector2(path.Waypoints[i - 1].Position.X, path.Waypoints[i - 1].Position.Y);
            var b = new Vector2(path.Waypoints[i].Position.X, path.Waypoints[i].Position.Y);
            float len = Vector2.Distance(a, b);
            if (len < 1e-3f) continue;
            var dir = (b - a) / len;
            for (float t = 0f; t <= len; t += 1f)
            {
                var q = a + dir * t;
                if ((q.X > -38f && q.X < 38f && q.Y > -218f && q.Y < -157f) || (q.X > -72f && q.X < 72f && q.Y > 108f && q.Y < 220f)) continue;
                min = MathF.Min(min, grid.WidthAcross(q, dir));
            }
        }
        return min;
    }
}
