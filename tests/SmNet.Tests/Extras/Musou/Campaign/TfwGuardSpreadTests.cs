using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// musou/guard-spread (the owner, 2026-11-08): "guardsmen need to spread out much more when running to a point. after its captured they splinter and spread out around the point to defend it. on the way to
/// the point guardsmen get stuck on corners and walk in straight lines directly next to walls. they need to spread out." Each check against <see cref="GuardRules.Spread"/> false (the old walk) too.
/// </summary>
public class TfwGuardSpreadTests(ITestOutputHelper output)
{
    static readonly SimConfig Config = new();
    static readonly GuardRules Old = new() { Spread = false };

    static MusouSim Sim(StaticMesh mesh) => new(NavGraph.Build(mesh, null, NavSettings.From(Config))) { TickRate = 30 };

    static TfwGuard[] Men(GuardSim guards)
    {
        var men = new TfwGuard[GuardSim.Capacity];
        int n = guards.CopyTo(men);
        return men.Take(n).Where(m => m.State is (byte)GuardState.Idle or (byte)GuardState.Moving).ToArray();
    }

    static void Run(GuardSim guards, ref uint tick, int ticks, Func<bool>? until = null)
    {
        for (int t = 0; t < ticks && !(until?.Invoke() ?? false); t++, tick++) { guards.BeginTick(tick); guards.Step(false); }
    }

    /// <summary>How wide a squad's men stand across a heading, and their mean distance to their nearest neighbour.</summary>
    static (float Across, float Nearest) Spread(TfwGuard[] men, Vector2 heading)
    {
        var right = new Vector2(heading.Y, -heading.X);
        var across = men.Select(m => Vector2.Dot(new Vector2(m.Position.X, m.Position.Y), right)).ToArray();
        float nearest = (float)men.Average(a => men.Where(b => b.Id != a.Id).Min(b => Vector3.Distance(a.Position, b.Position)));
        return (across.Max() - across.Min(), nearest);
    }

    static (float Across, float Nearest) MidWalk(GuardRules rules)
    {
        var sim = Sim(new NavigationTests.Level().Floor(-60f, -60f, 60f, 60f).Mesh());
        var guards = new GuardSim(sim, rules);
        int s = guards.SpawnSquad(new Vector3(0f, -45f, 0f));
        Assert.True(guards.Order(s, new Vector3(0f, 45f, 0f)));
        uint tick = 1;
        Run(guards, ref tick, 10 * 30);   // half way
        Assert.False(guards.Arrived(s));
        return Spread(Men(guards), Vector2.UnitY);
    }

    [Fact]
    public void A_squad_on_the_move_goes_in_wide_open_order()
    {
        var spread = MidWalk(GuardRules.Default);
        var old = MidWalk(Old);
        output.WriteLine($"across the way: {spread.Across:0.0} m (old {old.Across:0.0}); nearest neighbour {spread.Nearest:0.00} m (old {old.Nearest:0.00})");
        Assert.True(spread.Across >= 12f, $"{spread.Across}");
        Assert.True(spread.Nearest >= 2.5f, $"{spread.Nearest}");
        Assert.True(spread.Across >= 1.8f * old.Across && spread.Nearest >= 1.6f * old.Nearest);
    }

    [Fact]
    public void Squads_bound_for_one_point_walk_in_their_own_lanes()
    {
        var sim = Sim(new NavigationTests.Level().Floor(-60f, -60f, 60f, 60f).Mesh());
        var guards = new GuardSim(sim);
        var point = new Vector3(0f, 45f, 0f);
        int[] squads = [guards.SpawnSquad(new Vector3(0f, -45f, 0f)), guards.SpawnSquad(new Vector3(0f, -49f, 0f)), guards.SpawnSquad(new Vector3(0f, -53f, 0f))];
        foreach (int s in squads) Assert.True(guards.Order(s, point, point, 12f));
        uint tick = 1;
        Run(guards, ref tick, 10 * 30);
        var xs = squads.Select(s => guards.Centre(s).X).OrderBy(x => x).ToArray();
        output.WriteLine($"lanes at x = {string.Join(", ", xs.Select(x => x.ToString("0.0")))}");
        Assert.True(xs[1] - xs[0] >= 4f && xs[2] - xs[1] >= 4f, string.Join(", ", xs));
    }

    static (float MeanRadius, float Arc, int Inside, float Nearest, int Teams) Hold(GuardRules rules)
    {
        var sim = Sim(new NavigationTests.Level().Floor(-60f, -60f, 60f, 60f).Mesh());
        var guards = new GuardSim(sim, rules);
        var point = new Vector3(0f, 20f, 0f);
        const float radius = 12f;
        int s = guards.SpawnSquad(new Vector3(0f, -30f, 0f));
        Assert.True(guards.Order(s, point + new Vector3(0f, -6f, 0f), point, radius));   // its own place on the point: south of the centre
        uint tick = 1;
        Run(guards, ref tick, 40 * 30, () => guards.Arrived(s));
        Assert.True(guards.Arrived(s));
        Run(guards, ref tick, 10 * 30);
        var men = Men(guards);
        var rel = men.Select(m => new Vector2(m.Position.X - point.X, m.Position.Y - point.Y)).ToArray();
        var angles = rel.Select(r => MathF.Atan2(r.X, -r.Y)).OrderBy(a => a).ToArray();   // from the squad's bearing (south of the centre), so the arc never wraps
        // Fire teams: men within 3.5 m of each other chain into one team.
        var team = Enumerable.Range(0, men.Length).ToArray();
        int Root(int a) => team[a] == a ? a : team[a] = Root(team[a]);
        for (int a = 0; a < men.Length; a++)
            for (int b = a + 1; b < men.Length; b++)
                if (Vector3.Distance(men[a].Position, men[b].Position) < 3.5f) team[Root(a)] = Root(b);
        int teams = Enumerable.Range(0, men.Length).Select(Root).Distinct().Count();
        return (rel.Average(r => r.Length()), (angles[^1] - angles[0]) * 180f / MathF.PI, rel.Count(r => r.Length() <= radius), Spread(men, Vector2.UnitY).Nearest, teams);
    }

    [Fact]
    public void After_arriving_to_hold_a_point_the_squad_splits_into_fire_teams_round_it()
    {
        var h = Hold(GuardRules.Default);
        var old = Hold(Old);
        output.WriteLine($"mean distance from the point {h.MeanRadius:0.0} m (old {old.MeanRadius:0.0}), arc {h.Arc:0} degrees (old {old.Arc:0}), inside {h.Inside}, nearest {h.Nearest:0.00} (old {old.Nearest:0.00}), teams {h.Teams} (old {old.Teams})");
        Assert.Equal(GuardSim.SquadSize, h.Inside);
        Assert.InRange(h.MeanRadius, 7f, 11.5f);       // round the edge (0.8 x 12 m), not on the centre
        Assert.InRange(h.Arc, 80f, 200f);                // spread over its arc (120 degrees), not all round
        Assert.Equal(3, h.Teams);
        Assert.True(h.Nearest >= 1.6f, $"{h.Nearest}");
        Assert.Equal(1, old.Teams);                     // the old: one block
    }

    /// <summary>The maze of AllyNavTests: a 52 m wall to go round. How often a man stood right beside a wall on the way, and how many came.</summary>
    static (float Hugging, int Came, float Seconds) RoundTheWall(GuardRules rules)
    {
        var mesh = new NavigationTests.Level().Floor(0f, 0f, 60f, 60f).Wall(30f, 0f, 30f, 52f).Mesh();
        var sim = Sim(mesh);
        var walk = sim.Walk!;
        var guards = new GuardSim(sim, rules);
        int s = guards.SpawnSquad(new Vector3(10f, 10f, 0f));
        var to = new Vector3(50f, 10f, 0f);
        Assert.True(guards.Order(s, to));
        uint tick = 1;
        int samples = 0, beside = 0;
        for (; tick < 60 * 30 && !guards.Arrived(s); tick++)
        {
            guards.BeginTick(tick); guards.Step(false);
            if (tick % 5 != 0) continue;
            foreach (var m in Men(guards))
            {
                int node = walk.Nearest(m.Position);
                if (node < 0) continue;
                samples++;
                if (walk.EdgeDistance(node) < 0.5f) beside++;
            }
        }
        float seconds = tick / 30f;
        Run(guards, ref tick, 10 * 30);
        return (beside / (float)Math.Max(1, samples), guards.CountInside(to, 8f), seconds);
    }

    [Fact]
    public void On_the_way_round_a_wall_the_men_keep_off_it_and_none_is_left_on_the_corner()
    {
        var r = RoundTheWall(GuardRules.Default);
        var old = RoundTheWall(Old);
        output.WriteLine($"beside a wall {r.Hugging:P1} of the samples (old {old.Hugging:P1}); came {r.Came} (old {old.Came}); arrived after {r.Seconds:0.0} s (old {old.Seconds:0.0})");
        Assert.Equal(GuardSim.SquadSize, r.Came);
        Assert.True(r.Hugging <= 0.5f * old.Hugging, $"{r.Hugging} vs {old.Hugging}");
        Assert.True(r.Hugging < 0.1f, $"{r.Hugging}");
    }

    [Fact]
    public void A_man_with_a_wall_in_his_straight_line_takes_a_detour_and_reaches_his_post()
    {
        // A short wall across the middle of the point: the posts on its far side are reached round it.
        var mesh = new NavigationTests.Level().Floor(-40f, -40f, 40f, 40f).Wall(-6f, 4f, 6f, 4f).Mesh();
        var sim = Sim(mesh);
        var guards = new GuardSim(sim);
        var point = new Vector3(0f, 8f, 0f);
        int s = guards.SpawnSquad(new Vector3(0f, -20f, 0f));
        Assert.True(guards.Order(s, point + new Vector3(0f, 6f, 0f), point, 12f));   // its place north of the wall
        uint tick = 1;
        Run(guards, ref tick, 40 * 30, () => guards.Arrived(s));
        Assert.True(guards.Arrived(s));
        Run(guards, ref tick, 15 * 30);
        var men = Men(guards);
        Assert.Equal(GuardSim.SquadSize, men.Length);
        Assert.All(men, m => Assert.True(m.Position.Y > 4.5f, $"{m.Position} is still south of the wall"));
    }

    [Fact]
    public void The_spread_is_deterministic()
    {
        static string Trace()
        {
            var mesh = new NavigationTests.Level().Floor(0f, 0f, 60f, 60f).Wall(30f, 0f, 30f, 52f).Mesh();
            var sim = new MusouSim(NavGraph.Build(mesh, null, NavSettings.From(Config))) { TickRate = 30 };
            var guards = new GuardSim(sim);
            guards.Order(guards.SpawnSquad(new Vector3(10f, 10f, 0f)), new Vector3(50f, 10f, 0f), new Vector3(50f, 10f, 0f), 10f);
            uint tick = 1;
            Run(guards, ref tick, 30 * 30);
            return string.Join(";", Men(guards).Select(m => $"{m.Position.X:0.000},{m.Position.Y:0.000}"));
        }
        Assert.Equal(Trace(), Trace());
    }
}
