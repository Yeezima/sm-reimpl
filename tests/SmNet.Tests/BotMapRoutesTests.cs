using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Bots A2 (<see cref="BotMapPlan.Routes"/>, <see cref="BotMapPlan.Vantages"/>; test aids, ours). Synthetic yard: a 160 x 80 m floor, the Space Marines' start at the east
/// end, the Chaos start at the west end, two long walls splitting the middle into a left, a middle and a right corridor, and a raised deck (2.5 m, open sides, a ramp up)
/// on the Marines' half that overlooks the middle.
/// </summary>
public class BotMapRoutesTests
{
    static readonly SimConfig Config = new();
    static readonly NavSettings Settings = NavSettings.From(Config);
    static readonly Vector3 MarinesStart = new(70, 0, 0), ChaosStart = new(-70, 0, 0), Centre = new(0, 0, 0);
    static readonly Vector3 ChaosDoor = new(-55, 0, 0);

    static readonly Lazy<(NavGraph Graph, CollisionWorld World)> Yard = new(() =>
    {
        var level = new NavigationTests.Level()
            .Floor(-80, -40, 80, 40)
            .Wall(-30, -14, 30, -14).Wall(-30, 14, 30, 14)
            .Floor(30, -6, 40, 6, 2.5f)                                                               // the deck
            .Quad(new(40, -6, 2.5f), new(52, -6, 0f), new(52, 6, 0f), new(40, 6, 2.5f));              // its ramp, east side
        var mesh = level.Mesh();
        var world = new CollisionWorld(mesh, mesh, null, []);
        return (NavGraph.Build(world.CharacterFor(Team.SpaceMarines), null, Settings), world);
    });

    static BotMapPlan Plan() => BotMapPlan.Build(Yard.Value.Graph, MarinesStart, ChaosStart, ChaosDoor, Centre, Yard.Value.World)!;

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    [Fact]
    public void Routes_fan_out_through_the_side_corridors_and_stay_out_of_the_no_loiter_zone()
    {
        var plan = Plan();
        Assert.InRange(plan.Routes.Count, 2, BotMapPlan.MaxRoutes);
        Assert.InRange(plan.DirectRoute, 0, plan.Routes.Count - 1);
        // The routes' middle points (the middle of the map along x) use at least two corridor bands, a side corridor among them, and lie at least RouteWidth apart.
        var middles = plan.Routes.Select(r => r.OrderBy(p => MathF.Abs(p.X)).First()).ToList();
        Assert.Contains(middles, p => MathF.Abs(p.Y) > 14f);
        Assert.True(middles.Select(p => p.Y > 14f ? 1 : p.Y < -14f ? -1 : 0).Distinct().Count() >= 2, string.Join(" ", middles));
        for (int i = 0; i < middles.Count; i++)
            for (int j = i + 1; j < middles.Count; j++) Assert.True(Flat(middles[i], middles[j]) >= BotMapPlan.RouteWidth - 2f, string.Join(" ", middles));
        foreach (var route in plan.Routes)
        {
            Assert.NotEmpty(route);
            foreach (var p in route)
            {
                Assert.False(plan.InNoLoiterZone(p), $"{p} in the no-loiter zone");
                Assert.True(Flat(p, MarinesStart) >= BotMapPlan.OwnStartClearance - 1e-3f, $"{p} too near the own start");
            }
            for (int i = 1; i < route.Count; i++) Assert.True(Vector3.Distance(route[i], route[i - 1]) <= BotMapPlan.RouteStep + 1.5f, "waypoints follow the path");
        }
    }

    [Fact]
    public void Routes_are_ordered_across_the_map()
    {
        var plan = Plan();
        // Seen from the Marines (east) looking west, "across" grows toward -Y.
        var mean = plan.Routes.Select(r => r.Average(p => p.Y)).ToList();
        for (int i = 1; i < mean.Count; i++) Assert.True(mean[i] < mean[i - 1], string.Join(", ", mean));
    }

    [Fact]
    public void Vantages_take_the_deck_and_see_the_middle()
    {
        var plan = Plan();
        Assert.NotEmpty(plan.Vantages);
        foreach (var v in plan.Vantages)
        {
            Assert.True(v.Sight >= BotMapPlan.VantageMinSight);
            Assert.False(plan.InNoLoiterZone(v.Spot));
            float along = (MarinesStart.X - v.Spot.X) / (MarinesStart.X - ChaosStart.X);
            Assert.InRange(along, BotMapPlan.VantageFrom - 0.02f, BotMapPlan.VantageTo + 0.02f);
        }
        for (int i = 0; i < plan.Vantages.Count; i++)
            for (int j = i + 1; j < plan.Vantages.Count; j++)
                Assert.True(Flat(plan.Vantages[i].Spot, plan.Vantages[j].Spot) >= BotMapPlan.VantageSpacing - 1e-3f);
        Assert.Contains(plan.Vantages, v => v.Spot.Z > 2f && v.Rise > 2f);   // high ground: the deck
        Assert.True(plan.Vantages[0].Spot.Z > 2f, $"best vantage {plan.Vantages[0]}");
    }

    [Fact]
    public void The_plan_without_a_world_has_routes_but_no_vantages()
    {
        var plan = BotMapPlan.Build(Yard.Value.Graph, MarinesStart, ChaosStart, null, Centre)!;
        Assert.NotEmpty(plan.Routes);
        Assert.Empty(plan.Vantages);
    }
}
