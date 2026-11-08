using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// The owner's second bot pass of 2026-11-08 (<see cref="BotRetreat"/>, <see cref="BotCover"/>, <see cref="BotPosts"/>, the safe approach and the under-fire rule of
/// <see cref="BotPush"/>; test aids, ours). The yard is <see cref="BotApproachTests"/>'s: the enemy at the origin, an L of walls north-east of it.
/// </summary>
public class BotCoverTests
{
    static readonly SimConfig Config = new();
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

    static Vector3 EyeAt(Vector3 feet) => feet + new Vector3(0, 0, Config.EyeHeight);

    [Fact]
    public void A_retreat_starts_at_60_percent_health_and_ends_at_90_percent_health_and_armour_or_on_timeout()
    {
        var r = new BotRetreat();
        Assert.False(r.Step(0, 0.7f, 0f));
        Assert.True(r.Step(1, 0.6f, 0f));
        Assert.True(r.Step(5, 0.95f, 0.5f));    // armour not back yet
        Assert.True(r.Step(6, 0.85f, 1f));      // health not back yet
        Assert.False(r.Step(7, 0.9f, 0.9f));
        Assert.Equal(1, r.Retreats);
        Assert.True(r.Step(10, 0.1f, 0f));
        Assert.True(r.Step(10 + BotRetreat.Timeout - 1, 0.2f, 0f));
        Assert.False(r.Step(10 + BotRetreat.Timeout + 1, 0.2f, 0f));
        Assert.True(r.Step(10 + BotRetreat.Timeout + 2, 0.2f, 0f));   // still low: a new retreat
        Assert.Equal(3, r.Retreats);
    }

    [Fact]
    public void Cover_is_a_near_cell_the_threat_cannot_see_and_a_hidden_bot_stays_put()
    {
        var (graph, world) = Yard.Value;
        var eyes = new[] { EyeAt(Vector3.Zero) };
        var me = new Vector3(20, -2, 0);   // in the open yard, seen
        Assert.False(BotCover.Hidden(world, eyes, me));
        var cover = BotCover.Find(graph, world, me, eyes);
        Assert.NotNull(cover);
        Assert.True(BotCover.Hidden(world, eyes, cover!.Value));
        Assert.InRange(Vector2.Distance(new(cover.Value.X, cover.Value.Y), new(me.X, me.Y)), BotCover.MinDistance, BotCover.MaxDistance);
        Assert.True(cover.Value.Y > 4f, $"{cover}");   // behind the long wall
        var hidden = new Vector3(20, 10, 0);
        Assert.Equal(hidden, BotCover.Find(graph, world, hidden, eyes));
        Assert.Equal(me, BotCover.Find(graph, world, me, []));
        // Open ground: nothing hides the bot.
        var (openGraph, openWorld) = Open.Value;
        Assert.Null(BotCover.Find(openGraph, openWorld, me, eyes));
        // The excluded zone is never chosen.
        var away = BotCover.Find(graph, world, me, eyes, p => p.X > 15f);
        Assert.True(away is null || away.Value.X <= 15f, $"{away}");
    }

    [Fact]
    public void A_push_goes_ahead_only_on_a_hidden_approach()
    {
        var (graph, world) = Yard.Value;
        var safe = BotApproach.Plan(graph, world, new Vector3(36, 10, 0), Vector3.Zero, Config.EyeHeight);
        Assert.True(safe.Safe, $"lead {safe.Lead:P0}");
        Assert.InRange(safe.Lead, 0f, BotApproach.SafeExposure);
        var (openGraph, openWorld) = Open.Value;
        var open = BotApproach.Plan(openGraph, openWorld, new Vector3(30, 0, 0), Vector3.Zero, Config.EyeHeight);
        Assert.False(open.Safe);
        // A hidden point reached across open ground is not safe either.
        Assert.False(new ApproachPlan([new Vector3(5, 5, 0)], 0.8f, 1f, 0.6f).Safe);
    }

    [Fact]
    public void No_push_starts_within_4_s_of_damage_and_a_hit_ends_one_that_has_not_closed_in()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            var hurt = new BotPush(seed);
            for (double t = 0; t < 120; t += 0.5) Assert.False(hurt.Step(t, "assault", 2, 20f, true, false, sinceDamage: 3.5));
        }
        BotPush Started(out double at)
        {
            for (int seed = 0; ; seed++)
            {
                var p = new BotPush(seed);
                for (double t = 0; t <= BotPush.RollMax; t += 0.5)
                    if (p.Step(t, "assault", 3, 20f, true, false)) { at = t; return p; }
            }
        }
        var push = Started(out double t0);
        Assert.False(push.Step(t0 + 1, "assault", 3, 20f, true, false, sinceDamage: 0.5));
        Assert.Equal("under fire", push.Ended);
        var close = Started(out t0);
        Assert.True(close.Step(t0 + 1, "assault", 3, 12f, true, false, sinceDamage: 0.5, closedIn: true));
        Assert.True(close.Step(t0 + 2, "assault", 3, 12f, true, false, sinceDamage: 2));
    }

    [Fact]
    public void The_balcony_goes_to_the_lowest_id_heavy_and_the_ramp_to_the_lowest_id_tactical_or_else_a_non_heavy()
    {
        (int, string)[] team = [(5, "assault"), (3, "heavy"), (7, "tactical"), (4, "heavy"), (9, "tactical")];
        Assert.Equal(BotPosts.Duty.Balcony, BotPosts.DutyOf(3, team));
        Assert.Equal(BotPosts.Duty.None, BotPosts.DutyOf(4, team));
        Assert.Equal(BotPosts.Duty.Ramp, BotPosts.DutyOf(7, team));
        Assert.Equal(BotPosts.Duty.None, BotPosts.DutyOf(9, team));
        Assert.Equal(BotPosts.Duty.None, BotPosts.DutyOf(5, team));
        (int, string)[] noTactical = [(6, "heavy"), (8, "assault"), (2, "assault")];
        Assert.Equal(BotPosts.Duty.Ramp, BotPosts.DutyOf(2, noTactical));
        Assert.Equal(BotPosts.Duty.Balcony, BotPosts.DutyOf(6, noTactical));
        (int, string)[] noHeavy = [(1, "assault"), (2, "tactical")];
        Assert.Equal(BotPosts.Duty.Ramp, BotPosts.DutyOf(2, noHeavy));
        Assert.Equal(BotPosts.Duty.None, BotPosts.DutyOf(1, noHeavy));
    }

    [Fact]
    public void Manufactorum_has_two_ramp_tops_and_two_balconies_mirrored_about_B_and_other_maps_none()
    {
        var anchors = BotPosts.Anchors("mp_tdm_09");
        var b = new Vector2(200, -800);
        foreach (var kind in new[] { BotPosts.Duty.Ramp, BotPosts.Duty.Balcony })
        {
            var two = anchors.Where(a => a.Kind == kind).ToList();
            Assert.Equal(2, two.Count);
            var mirrored = 2 * b - new Vector2(two[0].At.X, two[0].At.Y);
            Assert.True(Vector2.Distance(mirrored, new Vector2(two[1].At.X, two[1].At.Y)) <= 5f, $"{kind}: {two[0].At} {two[1].At}");
        }
        Assert.Empty(BotPosts.Anchors("tfw_war"));
    }
}
