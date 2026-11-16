using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// musou/guard-vantage (owner, 2026-11-09): "make small squads of guardsmen position themselves up in buildings randomly at times around the map - they should only position on high ground vantage points with
/// good lines of fire around them within 50m of a friendly captured point. if there is no friendly captured point then they still defend the stronghold/imperial base but also venture out in front of their home
/// base to take up high ground vantage point positions around the imperial base." Synthetic levels only.
/// </summary>
public class GuardVantageTests(ITestOutputHelper output)
{
    static readonly SimConfig Config = new();
    static NavGraph Graph(StaticMesh mesh) => NavGraph.Build(mesh, null, NavSettings.From(Config));

    /// <summary>A platform 4 m up (8 x 8 m), reached by a ramp from the south whose top is at its south edge (y0).</summary>
    static NavigationTests.Level Platform(NavigationTests.Level l, float x0, float y0, bool rampSouth = true)
    {
        l.Box(new Vector3(x0, y0, 0f), new Vector3(x0 + 8f, y0 + 8f, 4f));
        return rampSouth
            ? l.Quad(new(x0 + 2f, y0 - 8f, 0f), new(x0 + 6f, y0 - 8f, 0f), new(x0 + 6f, y0, 4f), new(x0 + 2f, y0, 4f))
            : l.Quad(new(x0 + 6f, y0 + 16f, 0f), new(x0 + 2f, y0 + 16f, 0f), new(x0 + 2f, y0 + 8f, 4f), new(x0 + 6f, y0 + 8f, 4f));
    }

    /// <summary>
    /// The finder's level: ground at 0 (60 m each way), a platform with a ramp at (10, 10) to (18, 18); a pit 4 m deep at (-40, -40) to (-30, -30); a block at (30, -20) to (40, -10), 4 m high, with a closed room on it
    /// (walls 3 m, a roof) whose one door faces the ramp up from the south.
    /// </summary>
    static StaticMesh FinderLevel()
    {
        var l = new NavigationTests.Level()
            .Floor(-60f, -30f, 60f, 60f).Floor(-60f, -60f, 60f, -40f).Floor(-60f, -40f, -40f, -30f).Floor(-30f, -40f, 60f, -30f)
            .Floor(-40f, -40f, -30f, -30f, -4f)
            .Quad(new(-40f, -40f, -4f), new(-30f, -40f, -4f), new(-30f, -40f, 0f), new(-40f, -40f, 0f))
            .Quad(new(-40f, -30f, -4f), new(-30f, -30f, -4f), new(-30f, -30f, 0f), new(-40f, -30f, 0f))
            .Quad(new(-40f, -40f, -4f), new(-40f, -30f, -4f), new(-40f, -30f, 0f), new(-40f, -40f, 0f))
            .Quad(new(-30f, -40f, -4f), new(-30f, -30f, -4f), new(-30f, -30f, 0f), new(-30f, -40f, 0f));
        Platform(l, 10f, 10f);
        l.Box(new Vector3(30f, -20f, 0f), new Vector3(40f, -10f, 4f))
            .Wall(30f, -10f, 40f, -10f, 4f, 3f).Wall(30f, -20f, 30f, -10f, 4f, 3f).Wall(40f, -20f, 40f, -10f, 4f, 3f)
            .Wall(30f, -20f, 34f, -20f, 4f, 3f).Wall(36f, -20f, 40f, -20f, 4f, 3f)
            .Floor(30f, -20f, 40f, -10f, 7f)
            .Quad(new(34f, -28f, 0f), new(36f, -28f, 0f), new(36f, -20f, 4f), new(34f, -20f, 4f));
        return l.Mesh();
    }

    static string Show(IEnumerable<GuardVantagePoint> points) => string.Join("; ", points.Select(v => $"({v.Position.X:0.0}, {v.Position.Y:0.0}, {v.Position.Z:0.0}) h {v.Height:0.0} open {v.Open:0.00} posts {v.Posts.Length}"));

    [Fact]
    public void The_finder_takes_a_raised_platform_with_open_lines_and_not_a_pit_or_a_closed_room()
    {
        var mesh = FinderLevel();
        var walk = new MusouNav(Graph(mesh));
        int home = walk.Nearest(new Vector3(0f, -50f, 0f));
        Assert.True(home >= 0);
        var found = GuardVantage.Find(walk, mesh, home, GuardVantageRules.Default);
        output.WriteLine($"{found.Length} vantage points of {GuardVantage.LastCandidates} candidates, {GuardVantage.LastRays} rays: {Show(found)}");
        Assert.Contains(found, v => v.Position.X is > 9.5f and < 18.5f && v.Position.Y is > 9.5f and < 18.5f && MathF.Abs(v.Position.Z - 4f) < 0.5f);
        var platform = found.First(v => v.Position.X is > 9.5f and < 18.5f && v.Position.Y is > 9.5f and < 18.5f);
        Assert.True(platform.Open >= 0.5f && platform.Posts.Length >= GuardVantageRules.Default.MinPosts, Show([platform]));
        Assert.All(platform.Posts, p => Assert.True(MathF.Abs(p.Z - 4f) < 0.5f, $"post {p}"));
        Assert.DoesNotContain(found, v => v.Position.Z < -1f);                                                                     // the pit
        Assert.DoesNotContain(found, v => v.Position.X is > 30f and < 40f && v.Position.Y is > -18f and < -10f && v.Position.Z < 6f);   // inside the room
        Assert.DoesNotContain(found, v => v.Position.Z > 6f);                                                                        // its roof: no way up
        Assert.DoesNotContain(found, v => v.Height < GuardVantageRules.Default.MinHeight);                                           // the ground
        Assert.Equal(Show(found), Show(GuardVantage.Find(walk, mesh, home, GuardVantageRules.Default)));                           // deterministic
    }

    // The battle's level: ground 120 x 140 m; a platform near position 0, one in front of the base (towards the HQ) and one behind it.
    static readonly Vector3 Landing = new(0f, -55f, 0f), Hq = new(0f, 56f, 0f);
    static readonly Vector3[] PositionsAt = [new(14f, 40f, 0f), new(-48f, 40f, 0f), new(48f, 40f, 0f)];

    static StaticMesh BattleLevel()
    {
        var l = new NavigationTests.Level().Floor(-60f, -80f, 60f, 60f);
        Platform(l, 10f, 10f);                 // near position 0 (26 m)
        Platform(l, 8f, -42f);                 // in front of the base (about 21 m)
        Platform(l, -16f, -76f, rampSouth: false);   // behind the base (about 21 m)
        return l.Mesh();
    }

    static (MusouSim Sim, GuardSim Guards, TfwBattle Battle) Battle(StaticMesh mesh, GuardVantageRules? rules = null)
    {
        var sim = new MusouSim(Graph(mesh)) { TickRate = 30, Frozen = true };
        var guards = new GuardSim(sim) { Sight = new CollisionWorld(mesh, mesh) };
        var layout = new TfwLayout(Landing, PositionsAt, Hq, [new(-50f, 50f, 0f), new(50f, 50f, 0f), new(-50f, 0f, 0f), Hq]);
        var battle = new TfwBattle(sim, guards, layout, 100.0, 1, false, default, TfwBattleRules.EliteBase, guardsmen: true)
        {
            Trickle = false, Patrols = false, Armies = false, BossOrksOn = false, StormboysOn = false, MeksOn = false, WeirdboyzOn = false, BigShootasOn = false, RokkitsOn = false, VictoryWave = false,
            VantageRules = rules ?? GuardVantageRules.Default,
        };
        battle.Start();
        return (sim, guards, battle);
    }

    static readonly Vector3[] Nobody = [new(-55f, -75f, 0f)];

    /// <summary>Runs the battle and its Guardsmen; the largest step any living man took in one tick (a teleport would show).</summary>
    static float Run(TfwBattle battle, GuardSim guards, ref uint tick, int ticks)
    {
        var before = new TfwGuard[GuardSim.Capacity];
        var after = new TfwGuard[GuardSim.Capacity];
        float most = 0f;
        for (int t = 0; t < ticks; t++, tick++)
        {
            int nb = guards.CopyTo(before);
            battle.Step(Nobody);
            guards.BeginTick(tick);
            guards.Step(false);
            int na = guards.CopyTo(after);
            var was = new Dictionary<ushort, Vector3>();
            for (int k = 0; k < nb; k++) was[before[k].Id] = before[k].Position;
            for (int k = 0; k < na; k++)
                if (was.TryGetValue(after[k].Id, out var p)) most = MathF.Max(most, Vector3.Distance(p, after[k].Position));
        }
        return most;
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    [Fact]
    public void With_a_point_secured_teams_take_only_spots_within_50_m_of_it_and_climb_the_ramp()
    {
        var (_, guards, battle) = Battle(BattleLevel());
        output.WriteLine(Show(battle.VantagePoints));
        Assert.True(battle.VantagePoints.Count >= 3, Show(battle.VantagePoints));
        battle.ClearAndSecureForTests(0);
        uint tick = 1;
        Run(battle, guards, ref tick, 40 * 30);   // the squads reach the point
        for (int k = 0; k < 4; k++) battle.VantageDepartForTests();
        var teams = battle.VantageTeamsForTests();
        Assert.NotEmpty(teams);
        foreach (var t in teams)
        {
            var spot = battle.VantagePoints[t.Spot].Position;
            Assert.True(Flat(spot, PositionsAt[0]) <= 50f, $"spot {spot}");
            Assert.InRange(guards.AliveIn(t.Squad), GuardVantageRules.Default.MenMin, GuardVantageRules.Default.MenMax);
        }
        Assert.Equal(teams.Length, teams.Select(t => t.Spot).Distinct().Count());   // one team a spot
        float step = Run(battle, guards, ref tick, 30 * 30);
        var men = new TfwGuard[GuardSim.Capacity];
        int n = guards.CopyTo(men);
        foreach (var t in battle.VantageTeamsForTests())
        {
            Assert.True(t.Holding, "the team holds its spot");
            var spot = battle.VantagePoints[t.Spot].Position;
            int up = men.Take(n).Count(m => (m.Id - 1) / GuardSim.SquadSize == t.Squad && MathF.Abs(m.Position.Z - spot.Z) < 0.6f && Flat(m.Position, spot) < 4f);
            output.WriteLine($"team {t.Squad}: {up} of {guards.AliveIn(t.Squad)} men on the spot at {spot}");
            Assert.Equal(guards.AliveIn(t.Squad), up);
        }
        Assert.True(step <= guards.Rules.Speed * 2f / 30f + 0.05f, $"a man moved {step:0.00} m in one tick");   // walked, never put there
    }

    [Fact]
    public void With_no_point_secured_the_base_keeps_its_squads_and_teams_go_up_only_in_front_of_it()
    {
        var (_, guards, battle) = Battle(BattleLevel());
        uint tick = 1;
        Run(battle, guards, ref tick, 15 * 30);
        for (int k = 0; k < 4; k++) battle.VantageDepartForTests();
        var teams = battle.VantageTeamsForTests();
        Assert.NotEmpty(teams);
        foreach (var t in teams)
        {
            var spot = battle.VantagePoints[t.Spot].Position;
            Assert.True(Flat(spot, Landing) <= 50f && spot.Y > Landing.Y, $"spot {spot}");   // in front, towards the HQ
        }
        for (int s = 0; s < GuardSim.MaxSquads; s++)
            if (guards.AliveIn(s) > 0 && !teams.Any(t => t.Squad == s)) Assert.True(guards.AliveIn(s) >= GuardVantageRules.Default.ParentKeeps, $"squad {s} kept {guards.AliveIn(s)}");
        Assert.True(battle.SquadPost(teams[0].Squad) < 0);   // a team takes no post of the field's
        Run(battle, guards, ref tick, 30 * 30);
        Assert.All(battle.VantageTeamsForTests(), t => Assert.True(t.Holding));
    }

    [Fact]
    public void A_level_with_nothing_raised_near_its_base_sends_the_teams_out_to_the_far_radius()
    {
        var l = new NavigationTests.Level().Floor(-60f, -80f, 60f, 60f);
        Platform(l, 10f, 10f);   // about 70 m in front of the base, nothing nearer
        var (_, guards, battle) = Battle(l.Mesh());
        Assert.Equal(GuardVantageRules.Default.BaseRadiusFar, battle.VantageBaseRadius);
        uint tick = 1;
        Run(battle, guards, ref tick, 15 * 30);
        battle.VantageDepartForTests();
        var team = Assert.Single(battle.VantageTeamsForTests());
        Assert.InRange(Flat(battle.VantagePoints[team.Spot].Position, Landing), 50f, 100f);
        Assert.Equal(GuardVantageRules.Default.BaseRadius, Battle(BattleLevel()).Battle.VantageBaseRadius);   // a spot within 50 m: the near radius
    }

    [Fact]
    public void A_team_whose_point_falls_goes_back_and_rejoins_its_squad()
    {
        var (_, guards, battle) = Battle(BattleLevel());
        battle.ClearAndSecureForTests(0);
        uint tick = 1;
        Run(battle, guards, ref tick, 40 * 30);
        battle.VantageDepartForTests();
        Assert.Single(battle.VantageTeamsForTests());
        int alive = guards.Alive;
        Run(battle, guards, ref tick, 20 * 30);
        battle.LosePointForTests(0);
        Run(battle, guards, ref tick, 2 * 30);
        // No point is secured now: the base front's spot counts, so the team may go there, or back.
        var left = battle.VantageTeamsForTests();
        Assert.All(left, t => Assert.True(t.Returning || battle.VantagePoints[t.Spot].Position.Y < 0f));
        Run(battle, guards, ref tick, 60 * 30);
        Assert.Equal(alive, guards.Alive);   // nobody lost on the way
    }

    [Fact]
    public void A_team_holds_its_time_then_merges_back_into_its_squad()
    {
        var quick = GuardVantageRules.Default with { HoldMin = 5f, HoldMax = 5f, IntervalMin = 1e5f, IntervalMax = 1e5f };   // only the test's own team
        var (_, guards, battle) = Battle(BattleLevel(), quick);
        battle.ClearAndSecureForTests(0);
        uint tick = 1;
        Run(battle, guards, ref tick, 40 * 30);
        int squadsBefore = guards.SquadsAlive;
        battle.VantageDepartForTests();
        Assert.Equal(squadsBefore + 1, guards.SquadsAlive);
        Run(battle, guards, ref tick, 90 * 30);
        Assert.Equal(1, battle.VantageTeamsSent);
        Assert.Equal(0, battle.VantageTeamsOut);              // it went back
        Assert.Equal(squadsBefore, guards.SquadsAlive);       // and merged into its squad
    }

    [Fact]
    public void Off_no_team_ever_goes()
    {
        var (_, guards, battle) = Battle(BattleLevel(), GuardVantageRules.Default with { Enabled = false });
        battle.ClearAndSecureForTests(0);
        uint tick = 1;
        Run(battle, guards, ref tick, 120 * 30);
        battle.VantageDepartForTests();
        Assert.Equal(0, battle.VantageTeamsSent);
        Assert.Empty(battle.VantagePoints);
    }

    [Fact]
    public void The_teams_cost_little_on_the_tick()
    {
        double Cost(GuardVantageRules rules, out int sent)
        {
            var (_, guards, battle) = Battle(BattleLevel(), rules with { IntervalMin = 2f, IntervalMax = 4f });
            battle.ClearAndSecureForTests(0);
            uint tick = 1;
            Run(battle, guards, ref tick, 30 * 30);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int t = 0; t < 60 * 30; t++, tick++) { battle.Step(Nobody); guards.BeginTick(tick); guards.Step(false); }
            sent = battle.VantageTeamsSent;
            return watch.Elapsed.TotalMilliseconds / (60 * 30);
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var mesh = BattleLevel();
        var walk = new MusouNav(Graph(mesh));
        double graph = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        GuardVantage.Find(walk, mesh, walk.Nearest(Landing), GuardVantageRules.Default);
        double find = watch.Elapsed.TotalMilliseconds;
        double on = Cost(GuardVantageRules.Default, out int sent), off = Cost(GuardVantageRules.Default with { Enabled = false }, out _);
        output.WriteLine($"finder {find:0.0} ms on {walk.NodeCount} nodes ({GuardVantage.LastCandidates} candidates, {GuardVantage.LastRays} rays; graph {graph:0} ms); tick {on:0.000} ms with teams ({sent} sent) vs {off:0.000} ms without");
        Assert.True(sent >= 1);
        Assert.True(on < off + 1.0, $"{on} vs {off}");
    }
}
