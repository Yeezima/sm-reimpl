using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// The owner's melee push of 2026-11-08 (<see cref="BotPush"/>, <see cref="BotApproach"/>, <see cref="BotHopper"/>; test aids, ours). Synthetic level: an 80 x 40 m
/// yard with the enemy at the origin and an L of walls north-east of it (along y = 4 from x = 6 to 40, and along x = 6 from y = 4 to 15), so the strip north of the
/// long wall is hidden from the enemy's eye while the open yard south of it is not.
/// </summary>
public class BotApproachTests
{
    static readonly SimConfig Config = new();
    static readonly Vector3 Enemy = Vector3.Zero;
    static Vector3 Eye => Enemy + new Vector3(0, 0, Config.EyeHeight);

    static readonly Lazy<(NavGraph Graph, CollisionWorld World)> Yard = new(() => Build(true));
    static readonly Lazy<(NavGraph Graph, CollisionWorld World)> Open = new(() => Build(false));

    static (NavGraph, CollisionWorld) Build(bool walls)
    {
        var level = new NavigationTests.Level().Floor(-40, -20, 40, 20);
        if (walls) level.Wall(6, 4, 40, 4).Wall(6, 4, 6, 15);
        var mesh = level.Mesh();
        var world = new CollisionWorld(mesh, mesh, null, []);
        return (NavGraph.Build(world.CharacterFor(Team.SpaceMarines), null, NavSettings.From(Config)), world);
    }

    [Fact]
    public void The_enemy_sees_the_open_yard_but_not_the_strip_behind_the_walls()
    {
        var world = Yard.Value.World;
        Assert.True(BotApproach.Seen(world, Eye, new Vector3(20, -5, 0)));
        Assert.False(BotApproach.Seen(world, Eye, new Vector3(20, 10, 0)));
        Assert.False(BotApproach.Seen(world, Eye, new Vector3(7, 8, 0)));
        Assert.Equal(0f, BotApproach.Exposure(world, Eye, new Vector3(30, 10, 0), new Vector3(15, 10, 0)));
        Assert.Equal(1f, BotApproach.Exposure(world, Eye, new Vector3(30, -5, 0), new Vector3(15, -5, 0)));
    }

    [Fact]
    public void A_far_push_goes_through_hidden_cover_and_a_hidden_staging_cell_and_is_seen_less_than_the_straight_line()
    {
        var (graph, world) = Yard.Value;
        var me = new Vector3(36, 10, 0);
        var plan = BotApproach.Plan(graph, world, me, Enemy, Config.EyeHeight);
        Assert.Equal(2, plan.Waypoints.Count);
        var cover = plan.Waypoints[0]; var stage = plan.Waypoints[1];
        Assert.InRange(new Vector2(cover.X, cover.Y).Length(), BotApproach.CoverMin, BotApproach.CoverMax);
        Assert.InRange(new Vector2(stage.X, stage.Y).Length(), BotApproach.StageMin, BotApproach.StageMax);
        Assert.All(plan.Waypoints, p => Assert.False(BotApproach.Seen(world, Eye, p)));
        Assert.True(plan.Exposure < plan.DirectExposure, $"seen {plan.Exposure:P0}, straight {plan.DirectExposure:P0}");
        // The cover is in the hidden strip, the staging cell just behind the short wall.
        Assert.True(cover.Y > 4f && stage.X > 6f && stage.Y > 4f, $"{cover} {stage}");
    }

    [Fact]
    public void A_near_push_needs_only_the_staging_cell_and_open_ground_gives_no_waypoint()
    {
        var (graph, world) = Yard.Value;
        var near = BotApproach.Plan(graph, world, new Vector3(20, 10, 0), Enemy, Config.EyeHeight);
        Assert.Single(near.Waypoints);
        var (openGraph, openWorld) = Open.Value;
        var open = BotApproach.Plan(openGraph, openWorld, new Vector3(30, 0, 0), Enemy, Config.EyeHeight);
        Assert.Empty(open.Waypoints);
        Assert.Equal(1f, open.DirectExposure);
    }

    [Fact]
    public void Pushes_come_every_15_to_30_s_and_jump_pack_bots_push_most()
    {
        int Starts(string role)
        {
            int starts = 0;
            for (int seed = 0; seed < 200; seed++)
            {
                var push = new BotPush(seed);
                for (double t = 0; t < BotPush.RollMax + 0.5; t += 0.5)
                    if (push.Step(t, role, 7, 30f, true, false)) { starts++; Assert.True(t >= BotPush.RollMin, $"rolled at {t}"); break; }
            }
            return starts;
        }
        int assault = Starts("assault"), tactical = Starts("tactical"), heavy = Starts("heavy");
        Assert.InRange(assault, 125, 175);   // 75 % of 200
        Assert.InRange(tactical, 45, 95);    // 35 %
        Assert.InRange(heavy, 10, 50);       // 15 %
        Assert.True(assault > tactical && tactical > heavy);
    }

    [Fact]
    public void A_push_ends_on_the_target_s_death_after_melee_and_on_timeout_and_never_starts_far_or_without_a_target()
    {
        BotPush Started(out double at)
        {
            for (int seed = 0; ; seed++)
            {
                var p = new BotPush(seed);
                for (double t = 0; t <= BotPush.RollMax; t += 0.5)
                    if (p.Step(t, "assault", 3, 20f, true, false)) { at = t; return p; }
            }
        }
        var dead = Started(out double t0);
        Assert.Equal(3, dead.Target);
        Assert.False(dead.Step(t0 + 1, "assault", 3, 20f, false, false));
        Assert.False(dead.Active);
        var melee = Started(out t0);
        Assert.True(melee.Step(t0 + 1, "assault", 3, 2f, true, true));
        Assert.True(melee.Step(t0 + 1 + BotPush.MeleeLinger - 0.5, "assault", 3, 2f, true, true));
        Assert.False(melee.Step(t0 + 1 + BotPush.MeleeLinger + 0.5, "assault", 3, 2f, true, true));
        var slow = Started(out t0);
        Assert.True(slow.Step(t0 + BotPush.Timeout - 1, "assault", 3, 20f, true, false));
        Assert.False(slow.Step(t0 + BotPush.Timeout + 1, "assault", 3, 20f, true, false));
        for (int seed = 0; seed < 50; seed++)
        {
            var never = new BotPush(seed);
            for (double t = 0; t < 120; t += 0.5)
            {
                Assert.False(never.Step(t, "assault", -1, float.MaxValue, false, false));
                Assert.False(never.Step(t + 0.25, "assault", 4, BotPush.StartRange + 5f, true, false));
            }
        }
    }

    [Fact]
    public void A_jump_pack_bot_alternates_dashes_and_hops_keeps_its_reserve_and_stops_hopping_within_jump_range()
    {
        var hopper = new BotHopper(1);
        var moves = new List<BotHopper.Move>();
        double last = double.NegativeInfinity;
        for (double t = 0; t < 20; t += 1.0 / 30)
        {
            var m = hopper.Next(t, true, true, 100f, 10f, 40f, 30f);
            if (m == BotHopper.Move.None) continue;
            Assert.True(t - last >= BotHopper.SpacingMin - 1e-6, $"{t - last:F2} s apart");
            last = t; moves.Add(m);
        }
        Assert.True(moves.Count >= 12, $"{moves.Count} moves");
        for (int i = 1; i < moves.Count; i++) Assert.NotEqual(moves[i - 1], moves[i]);
        // Too little power for a hop and the reserve: only dashes.
        var low = new BotHopper(2);
        for (double t = 0; t < 10; t += 1.0 / 30) Assert.NotEqual(BotHopper.Move.Hop, low.Next(t, true, true, 45f, 10f, 40f, 30f));
        Assert.True(low.Dashes > 4);
        // Within the jump-onto range, in the air, or with the dash cooling and no power: nothing.
        var near = new BotHopper(3);
        for (double t = 0; t < 10; t += 0.1)
        {
            Assert.Equal(BotHopper.Move.None, near.Next(t, true, true, 100f, 10f, 40f, BotHopper.JumpInRange - 1f));
            Assert.Equal(BotHopper.Move.None, near.Next(t, true, false, 100f, 10f, 40f, 30f));
            Assert.Equal(BotHopper.Move.None, near.Next(t, false, true, 0f, 10f, 40f, 30f));
        }
    }
}
