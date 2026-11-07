using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// The owner's Classic PvP bot behaviour of 2026-11-07 (<see cref="BotMapPlan"/>, <see cref="BotRoamer"/>; test aids, ours). Synthetic level: a 160 x 80 m yard with
/// the Space Marines' start at the east end, the Chaos start at the west end, a cross of walls round the centre (cover for the holds) and three corridors' worth of
/// open floor between, so lanes on the left, middle and right exist.
/// </summary>
public class BotMapPlanTests
{
    static readonly SimConfig Config = new();
    static readonly NavSettings Settings = NavSettings.From(Config);
    static readonly Vector3 MarinesStart = new(70, 0, 0), ChaosStart = new(-70, 0, 0), Centre = new(0, 0, 0);
    static readonly Vector3 ChaosDoor = new(-55, 0, 0);

    static readonly Lazy<(NavGraph Graph, CollisionWorld World)> Yard = new(() =>
    {
        var level = new NavigationTests.Level()
            .Floor(-80, -40, 80, 40)
            .Wall(-3, 6, 3, 6).Wall(-3, -6, 3, -6).Wall(6, -3, 6, 3).Wall(-6, -3, -6, 3)   // cover round the centre
            .Wall(-30, -14, 30, -14).Wall(-30, 14, 30, 14);                                 // two long walls: a left, a middle and a right corridor
        var mesh = level.Mesh();
        var world = new CollisionWorld(mesh, mesh, null, []);
        return (NavGraph.Build(world.CharacterFor(Team.SpaceMarines), null, Settings), world);
    });

    static BotMapPlan Plan() => BotMapPlan.Build(Yard.Value.Graph, MarinesStart, ChaosStart, ChaosDoor, Centre, Yard.Value.World)!;

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    [Fact]
    public void Holds_ring_the_centre_one_per_sector_and_none_is_in_the_no_loiter_zone()
    {
        var plan = Plan();
        Assert.True(plan.CentreHolds.Count >= 6, plan.Note);
        foreach (var h in plan.CentreHolds)
        {
            Assert.InRange(Flat(h, Centre), BotMapPlan.HoldRingMin, BotMapPlan.HoldRingMax);
            Assert.False(plan.InNoLoiterZone(h));
        }
        // One per sector: every pair differs in angle about the centre.
        var angles = plan.CentreHolds.Select(h => MathF.Atan2(h.Y, h.X)).ToList();
        Assert.Equal(angles.Count, angles.Select(a => (int)((a + MathF.PI) / (2 * MathF.PI) * BotMapPlan.HoldSectors) % BotMapPlan.HoldSectors).Distinct().Count());
    }

    [Fact]
    public void No_hold_roam_or_lane_point_is_near_the_enemy_spawn()
    {
        var plan = Plan();
        foreach (var p in plan.CentreHolds.Concat(plan.RoamPoints).Concat(plan.Lanes))
        {
            Assert.True(Flat(p, ChaosDoor) >= BotMapPlan.NoLoiterRadius, $"{p} is {Flat(p, ChaosDoor):F1} m from the door");
            Assert.True(Flat(p, ChaosStart) >= BotMapPlan.NoLoiterRadius, $"{p} is {Flat(p, ChaosStart):F1} m from the start");
        }
        Assert.True(plan.InNoLoiterZone(ChaosDoor + new Vector3(10, 5, 0)));
        Assert.False(plan.InNoLoiterZone(Centre));
        Assert.True(plan.RoamPoints.Count >= 6, plan.Note);
        for (int i = 0; i < plan.RoamPoints.Count; i++)
            for (int j = i + 1; j < plan.RoamPoints.Count; j++) Assert.True(Vector3.Distance(plan.RoamPoints[i], plan.RoamPoints[j]) >= BotMapPlan.RoamSpacing - 0.01f);
    }

    [Fact]
    public void Lanes_cross_the_map_from_one_side_to_the_other()
    {
        var plan = Plan();
        Assert.Equal(3, plan.Lanes.Count);
        // Along: between a quarter and nearly half the way from the own start (x 70) toward the enemy's (x -70).
        foreach (var l in plan.Lanes) Assert.InRange(l.X, 70 - 140 * BotMapPlan.LaneTo - 0.5f, 70 - 140 * BotMapPlan.LaneFrom + 0.5f);
        // Across: one each side of the long walls and one between them.
        var ys = plan.Lanes.Select(l => l.Y).OrderBy(y => y).ToList();
        Assert.True(ys[0] < -14f && ys[2] > 14f && MathF.Abs(ys[1]) < 14f, string.Join(", ", ys));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(12)]
    public void Spread_gives_each_rank_its_own_index(int count)
    {
        var picks = Enumerable.Range(0, count).Select(r => BotMapPlan.Spread(r, count)).ToList();
        Assert.Equal(count, picks.Distinct().Count());
        Assert.All(picks, p => Assert.InRange(p, 0, count - 1));
        Assert.Equal(-1, BotMapPlan.Spread(0, 0));
    }

    [Fact]
    public void Six_bots_of_a_side_hold_six_different_centre_spots_after_a_wipe()
    {
        var plan = Plan();
        var spots = new List<Vector3>();
        for (int rank = 0; rank < 6; rank++)
        {
            var roamer = new BotRoamer(rank);
            var goal = roamer.IdleGoal(plan, rank, MarinesStart, 0, out bool holding);
            Assert.NotNull(goal);
            Assert.False(holding);
            Assert.Contains(goal!.Value, plan.CentreHolds);
            spots.Add(goal.Value);
            // Arrived: it holds there.
            Assert.Equal(goal, roamer.IdleGoal(plan, rank, goal.Value, 5, out holding));
            Assert.True(holding);
            Assert.True(roamer.Holding);
        }
        Assert.Equal(6, spots.Distinct().Count());
    }

    [Fact]
    public void A_held_spot_is_left_for_a_roam_point_and_the_bot_comes_back()
    {
        var plan = Plan();
        var roamer = new BotRoamer(42);
        var hold = roamer.IdleGoal(plan, 2, MarinesStart, 0, out _)!.Value;
        roamer.IdleGoal(plan, 2, hold, 1, out bool holding);
        Assert.True(holding);
        Assert.True(roamer.IdleGoal(plan, 2, hold, 1 + BotRoamer.HoldMin - 0.5, out holding) == hold && holding);   // still holding before the shortest hold ends
        var roam = roamer.IdleGoal(plan, 2, hold, 1 + BotRoamer.HoldMax + 0.5, out holding)!.Value;
        Assert.False(holding);
        Assert.Equal(BotRoamer.Activity.Roam, roamer.Doing);
        Assert.Contains(roam, plan.RoamPoints);
        Assert.False(plan.InNoLoiterZone(roam));
        // At the roam point: back to its own hold.
        var back = roamer.IdleGoal(plan, 2, roam, 30, out _)!.Value;
        Assert.Equal(hold, back);
        Assert.Equal(1, roamer.Visits);
    }

    [Fact]
    public void Bots_leave_the_spawn_by_different_lanes_and_change_lane_each_life()
    {
        var plan = Plan();
        var first = Enumerable.Range(0, 3).Select(rank => new BotRoamer(rank).LaneGoal(plan, rank, 0, MarinesStart, 0)!.Value).ToList();
        Assert.Equal(3, first.Distinct().Count());
        var roamer = new BotRoamer(7);
        var lives = Enumerable.Range(0, 3).Select(life => roamer.LaneGoal(plan, 0, life, MarinesStart, life * 100)!.Value).ToList();
        Assert.Equal(3, lives.Distinct().Count());
        // Reached, timed out or ended by a fight: no lane until the next life.
        Assert.Null(roamer.LaneGoal(plan, 0, 2, lives[2], 200.5));
        var other = new BotRoamer(8);
        Assert.NotNull(other.LaneGoal(plan, 1, 0, MarinesStart, 0));
        Assert.Null(other.LaneGoal(plan, 1, 0, MarinesStart, BotRoamer.LaneTimeout + 1));
        var fought = new BotRoamer(9);
        Assert.NotNull(fought.LaneGoal(plan, 1, 0, MarinesStart, 0));
        fought.EndLane();
        Assert.Null(fought.LaneGoal(plan, 1, 0, MarinesStart, 1));
    }

    [Fact]
    public void Long_chases_bend_left_middle_and_right_by_rank()
    {
        var plan = Plan();
        var target = new Vector3(-35, 0, 0);   // out of the zone, in the middle corridor
        var vias = Enumerable.Range(0, 3).Select(rank => plan.ViaFor(MarinesStart, target, rank)).ToList();
        Assert.All(vias, v => Assert.NotNull(v));
        Assert.Equal(3, vias.Distinct().Count());
        float direct = Flat(MarinesStart, target);
        foreach (var v in vias)
        {
            Assert.True(Flat(MarinesStart, v!.Value) + Flat(v.Value, target) <= BotMapPlan.ViaDetour * direct + 0.01f);
            Assert.False(plan.InNoLoiterZone(v.Value));
        }
        // The ranks go across the line: rank 0 one side, rank 2 the other.
        Assert.True(vias[0]!.Value.Y * vias[2]!.Value.Y < 0f || MathF.Abs(vias[0]!.Value.Y - vias[2]!.Value.Y) > 10f);
        Assert.Null(plan.ViaFor(MarinesStart, MarinesStart + new Vector3(-20, 0, 0), 0));   // short chases go straight
    }

    [Fact]
    public void Without_a_door_the_zone_is_the_enemy_start_alone_and_an_off_graph_start_gives_no_plan()
    {
        var plan = BotMapPlan.Build(Yard.Value.Graph, MarinesStart, ChaosStart, null, Centre)!;
        Assert.Null(plan.EnemyDoor);
        Assert.True(plan.InNoLoiterZone(ChaosStart + new Vector3(10, 0, 0)));
        Assert.False(plan.InNoLoiterZone(ChaosDoor + new Vector3(30, 0, 0)));
        Assert.Null(BotMapPlan.Build(Yard.Value.Graph, new Vector3(500, 500, 0), ChaosStart, null, Centre));
    }
}
