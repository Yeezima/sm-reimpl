using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Seize Ground (M16): capture and neutralise timing at the tick boundaries, contested points, unwinding the other side's
/// partial progress, regaining hold, presence (inside the cylinder, alive), scoring per interval, deaths not scoring, the
/// score and time limits, restart resetting the points, warm-up, start-area spawns, Annihilation unaffected (controls),
/// protocol 11 bounds and refusals, and the server end to end. Synthetic classes, weapons and points only (no game data).
/// </summary>
public class SeizeGroundTests(ITestOutputHelper output)
{
    const int Marine = 0, ChaosClass = 1;
    static readonly WeaponSpec Rifle = new("test_rifle", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.2f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly WeaponSpec Sidearm = new("test_sidearm", WeaponSlot.Pistol, TriggerKind.SemiAuto, 10f, 0.2f, 10, 40, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static ClassLoadout Tac(string name, Team side) => new(name, 0, 1, SimConfig.PlaceholderMeleeSteps) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };

    /// <summary>Two rifle hits kill; no shield; a long respawn so the dead stay dead.</summary>
    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Sidearm], Classes = [Tac("sm - tactical", Team.SpaceMarines), Tac("csm - tactical", Team.Chaos)],
        HealthMax = 40f, ShieldMax = 0f, HealthRegenDelay = 1e6f, RespawnTime = 60f,
    };
    static readonly MatchRules Seize = MatchRules.For(GameMode.SeizeGround) with { TimeLimit = 0f };
    const int Full = 600, Hold = 300, Interval = 30;   // 20 s, 10 s, 1 s at 30 Hz

    static readonly ControlPoint A = new("mkr_objective_1", new Vector3(0, 0, 0), 5f, 10f);
    static readonly ControlPoint B = new("mkr_objective_2", new Vector3(40, 0, 0), 5f, 10f);
    static readonly Vector3 Far = new(-60, 0, 0), Far2 = new(-60, 30, 0);

    /// <summary>A world whose sides spawn at the given points (start areas of 1 x 1 m: every spawn at the centre).</summary>
    static World Arena(MatchRules? rules, Vector3 marines, Vector3 chaos, params ControlPoint[] points) =>
        new(Config) { Rules = rules, ControlPoints = points, StartAreas = [Area(Team.SpaceMarines, marines, 0f), Area(Team.Chaos, chaos, MathF.PI)] };

    static StartArea Area(Team side, Vector3 at, float yaw) => new(side, at, new Vector3(1, 1, 2), yaw);

    static Func<int, PlayerInput> Idle(World w) => id => new PlayerInput(0, 0, 0, w.Players[id].Yaw, InputButtons.None);
    static void Run(World w, int ticks, Func<int, PlayerInput>? input = null) { for (int i = 0; i < ticks; i++) w.Step(input ?? Idle(w)); }

    /// <summary>Spawns a marine (id 1) and a Chaos player (id 2), then one tick: warm-up ends at its close, capture counts from the next.</summary>
    static World Started(MatchRules? rules, Vector3 marines, Vector3 chaos, params ControlPoint[] points)
    {
        var w = Arena(rules, marines, chaos, points);
        w.Spawn(1, Marine); w.Spawn(2, ChaosClass);
        Run(w, 1);
        Assert.Equal(MatchState.Playing, w.State);
        return w;
    }

    // ---------------------------------------------------------------- capture

    [Fact]
    public void One_side_alone_on_a_neutral_point_owns_it_after_exactly_the_capture_time()
    {
        var w = Started(Seize, A.Center, Far, A, B);
        Assert.Equal(new PointState(Team.None, Team.None, 0, false), w.Points[0]);
        Run(w, Full - 1);
        Assert.Equal(new PointState(Team.None, Team.SpaceMarines, Full - 1, false), w.Points[0]);
        Assert.Empty(w.LastCaptures);
        Assert.Equal(0, w.OwnedBy(Team.SpaceMarines));
        Run(w, 1);
        Assert.Equal(new PointState(Team.SpaceMarines, Team.SpaceMarines, Hold, false), w.Points[0]);
        var e = Assert.Single(w.LastCaptures);
        Assert.Equal((0, CaptureKind.Captured, Team.SpaceMarines, w.Tick), (e.Point, e.Kind, e.Side, e.Tick));
        Assert.Equal(1f, w.ProgressOf(0));
        Assert.Equal(default, w.Points[1]);                                  // nobody on B
        Run(w, 1);
        Assert.Empty(w.LastCaptures);                                        // once per change
        Assert.Equal(new PointState(Team.SpaceMarines, Team.SpaceMarines, Hold, false), w.Points[0]);   // hold stays full
    }

    [Fact]
    public void A_contested_point_freezes_and_resumes()
    {
        // Progress frozen: the marine is 100 ticks in when the Chaos player (12 m away, facing -Y) walks onto the point.
        var w = Started(Seize, A.Center, new Vector3(0, 12, 0), A);
        Run(w, 100);
        Assert.Equal(100, w.Points[0].Progress);
        Func<int, PlayerInput> chaosComes = id => new PlayerInput(0, 0, id == 2 && !A.Contains(w.Players[2].Position) ? 1 : 0, w.Players[id].Yaw, InputButtons.None);
        int before = 0;
        do { w.Step(chaosComes); before++; } while (!A.Contains(w.Players[2].Position));
        Assert.Equal(new PointState(Team.None, Team.SpaceMarines, 100 + before - 1, true), w.Points[0]);
        Run(w, 200, chaosComes);
        Assert.Equal(new PointState(Team.None, Team.SpaceMarines, 100 + before - 1, true), w.Points[0]);

        // Both on it from the start: contested at zero; when the Chaos player walks off, the marine's capture starts.
        var w2 = Started(Seize, A.Center, A.Center, A);
        Run(w2, 250);
        Assert.Equal(new PointState(Team.None, Team.None, 0, true), w2.Points[0]);
        Assert.Empty(w2.LastCaptures);
        // The Chaos player walks off (faces -Y, forward): the marine's capture starts from where it stood (zero).
        Func<int, PlayerInput> chaosLeaves = id => new PlayerInput(0, 0, id == 2 ? 1 : 0, w2.Players[id].Yaw, InputButtons.None);
        int ticks = 0;
        while (A.Contains(w2.Players[2].Position)) { w2.Step(chaosLeaves); ticks++; Assert.True(ticks < 200); }
        Assert.False(w2.Points[0].Contested);
        Assert.Equal((Team.SpaceMarines, 1), (w2.Points[0].Side, w2.Points[0].Progress));   // the tick it left already counts for the marine
        output.WriteLine($"the Chaos player left the 5 m point after {ticks} ticks");
    }

    [Fact]
    public void An_owned_point_is_neutralised_after_the_neutralise_time_then_captured_after_the_capture_time()
    {
        // Marines own A; the marine walks off to -X (yaw pi/2), then the Chaos player (spawned 12 m up, facing -Y) walks onto it.
        var w = Started(Seize, A.Center, new Vector3(0, 12, 0), A);
        Run(w, Full);
        Assert.Equal(Team.SpaceMarines, w.Points[0].Owner);
        Func<int, PlayerInput> marineLeaves = id => new PlayerInput(0, 0, id == 1 ? 1 : 0, id == 1 ? MathF.PI / 2 : w.Players[id].Yaw, InputButtons.None);
        while (A.Contains(w.Players[1].Position)) w.Step(marineLeaves);
        Run(w, 30, marineLeaves);
        Assert.Equal(new PointState(Team.SpaceMarines, Team.SpaceMarines, Hold, false), w.Points[0]);   // nobody on it: unchanged
        Func<int, PlayerInput> chaosComes = id => new PlayerInput(0, 0, id == 2 && !A.Contains(w.Players[2].Position) ? 1 : 0, w.Players[id].Yaw, InputButtons.None);
        do w.Step(chaosComes); while (!A.Contains(w.Players[2].Position));
        // The tick it entered counts.
        Assert.Equal(Hold - 1, w.Points[0].Progress);
        Run(w, Hold - 2, chaosComes);
        Assert.Equal((Team.SpaceMarines, 1), (w.Points[0].Owner, w.Points[0].Progress));
        Run(w, 1, chaosComes);
        Assert.Equal(default, w.Points[0]);
        var e = Assert.Single(w.LastCaptures);
        Assert.Equal((CaptureKind.Neutralised, Team.Chaos), (e.Kind, e.Side));
        Run(w, Full - 1, chaosComes);
        Assert.Equal(new PointState(Team.None, Team.Chaos, Full - 1, false), w.Points[0]);
        Run(w, 1, chaosComes);
        Assert.Equal(new PointState(Team.Chaos, Team.Chaos, Hold, false), w.Points[0]);
        Assert.Equal((CaptureKind.Captured, Team.Chaos), (w.LastCaptures[0].Kind, w.LastCaptures[0].Side));
    }

    [Fact]
    public void Partial_progress_unwinds_before_the_other_side_captures_and_an_owner_regains_hold()
    {
        // Marine 100 ticks into a capture walks off; the Chaos player then needs 100 ticks to unwind and 600 to capture.
        var w = Started(Seize, A.Center, new Vector3(0, 12, 0), A);
        Run(w, 100);
        Func<int, PlayerInput> marineLeaves = id => new PlayerInput(0, 0, id == 1 ? 1 : 0, id == 1 ? MathF.PI / 2 : w.Players[id].Yaw, InputButtons.None);
        while (A.Contains(w.Players[1].Position)) w.Step(marineLeaves);
        int marine = w.Points[0].Progress;
        Assert.Equal(Team.SpaceMarines, w.Points[0].Side);
        Func<int, PlayerInput> chaosComes = id => new PlayerInput(0, 0, id == 2 && !A.Contains(w.Players[2].Position) ? 1 : 0, w.Players[id].Yaw, InputButtons.None);
        do w.Step(chaosComes); while (!A.Contains(w.Players[2].Position));
        Assert.Equal((Team.SpaceMarines, marine - 1), (w.Points[0].Side, w.Points[0].Progress));
        Run(w, marine - 1, chaosComes);
        Assert.Equal(default, w.Points[0]);                                  // unwound: untouched again
        Run(w, 1, chaosComes);
        Assert.Equal((Team.Chaos, 1), (w.Points[0].Side, w.Points[0].Progress));
        Assert.Empty(w.LastCaptures);

        // Regaining hold: marines own A; the marine walks off (-X), the Chaos player walks through (-Y) wearing the hold
        // down, then the marine walks back (+X) and refills it at one tick per tick.
        var o = Started(Seize, A.Center, new Vector3(0, 12, 0), A);
        Run(o, Full);
        Assert.Equal((Team.SpaceMarines, Hold), (o.Points[0].Owner, o.Points[0].Progress));
        Func<int, PlayerInput> away = id => new PlayerInput(0, 0, id == 1 ? 1 : 0, id == 1 ? MathF.PI / 2 : o.Players[id].Yaw, InputButtons.None);
        while (A.Contains(o.Players[1].Position)) o.Step(away);
        Func<int, PlayerInput> through = id => new PlayerInput(0, 0, id == 2 ? 1 : 0, o.Players[id].Yaw, InputButtons.None);
        do o.Step(through); while (!A.Contains(o.Players[2].Position));
        do o.Step(through); while (A.Contains(o.Players[2].Position));
        int worn = o.Points[0].Progress;
        Assert.Equal(Team.SpaceMarines, o.Points[0].Owner);
        Assert.InRange(worn, 1, Hold - 50);
        Func<int, PlayerInput> back = id => new PlayerInput(0, 0, id == 1 ? 1 : 0, id == 1 ? -MathF.PI / 2 : o.Players[id].Yaw, InputButtons.None);
        do o.Step(back); while (!A.Contains(o.Players[1].Position));
        Assert.Equal(worn + 1, o.Points[0].Progress);
        Run(o, 5);
        Assert.Equal(worn + 6, o.Points[0].Progress);
        Run(o, Hold);
        Assert.Equal(Hold, o.Points[0].Progress);                            // capped at full
    }

    [Fact]
    public void Presence_needs_a_living_player_with_feet_inside_the_cylinder()
    {
        var p = new ControlPoint("p", new Vector3(10, 10, 5), 3f, 2f);
        Assert.True(p.Contains(new Vector3(13, 10, 5)));                     // on the rim
        Assert.False(p.Contains(new Vector3(13.01f, 10, 5)));
        Assert.True(p.Contains(new Vector3(10, 10, 3)) && p.Contains(new Vector3(10, 10, 7)));
        Assert.False(p.Contains(new Vector3(10, 10, 2.99f)) || p.Contains(new Vector3(10, 10, 7.01f)));

        // A dead marine on the point does not count: the Chaos player (on the point too) shoots it, then captures alone.
        var w = Started(Seize, A.Center, A.Center + new Vector3(0, 4, 0), A);
        // Marine faces +Y (yaw 0), Chaos faces -Y: Chaos fires at the marine until it dies.
        Func<int, PlayerInput> chaosFires = id => new PlayerInput(0, 0, 0, w.Players[id].Yaw, id == 2 && w.Tick % 2 == 0 ? InputButtons.Fire : InputButtons.None);
        int guard = 0; bool contested = false;
        while (w.Players[1].Alive) { contested = w.Points[0].Contested; w.Step(chaosFires); Assert.True(++guard < 120); }
        Assert.True(contested);                                              // both stood on it until the marine died
        Assert.False(w.Points[0].Contested);                                 // the tick it died, it no longer counts
        Assert.Equal((Team.Chaos, 1), (w.Points[0].Side, w.Points[0].Progress));
        Assert.Equal((0, 0), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));   // the kill scored nothing
        Assert.Equal(new PlayerScore(1, 0), w.Scores[2]);                    // personal kills still count
        Assert.Equal(new PlayerScore(0, 1), w.Scores[1]);
    }

    // ---------------------------------------------------------------- scoring and limits

    [Fact]
    public void Owned_points_score_every_interval_and_the_score_limit_ends_the_match()
    {
        // Marines on A, Chaos on B: both capture at the same tick, then each scores 2 per second.
        var w = Started(Seize with { ScoreLimit = 20 }, A.Center, B.Center, A, B);
        Run(w, Full - 1);
        Assert.Equal((0, 0), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
        Run(w, 1);                                                           // tick 600 of play: captures, then the award of that second
        Assert.Equal(2, w.LastCaptures.Count);
        Assert.Equal((2, 2), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
        Run(w, Interval - 1);
        Assert.Equal((2, 2), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
        Run(w, 1);
        Assert.Equal((4, 4), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
        Run(w, 8 * Interval - 1);
        Assert.Equal(MatchState.Playing, w.State);
        Run(w, 1);                                                           // 20 each at the same award: equal scores, a draw
        Assert.Equal((MatchState.Ended, Team.None, 20, 20), (w.State, w.Winner, w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
        // After the end the points freeze and nothing scores.
        var frozen = w.Points.ToArray();
        Run(w, 3 * Interval);
        Assert.Equal(frozen, w.Points);
        Assert.Equal((20, 20), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));

        // Two points against none: twice the rate, and a winner.
        var two = Started(Seize with { ScoreLimit = 10 }, A.Center, Far, A, new ControlPoint("near", A.Center + new Vector3(1, 0, 0), 5f, 10f));
        Run(two, Full);
        Assert.Equal((2, 4), (two.OwnedBy(Team.SpaceMarines), two.ScoreOf(Team.SpaceMarines)));
        Run(two, 2 * Interval - 1);
        Assert.Equal(MatchState.Playing, two.State);
        Run(two, 1);
        Assert.Equal((MatchState.Ended, Team.SpaceMarines, 12), (two.State, two.Winner, two.ScoreOf(Team.SpaceMarines)));
    }

    [Fact]
    public void The_time_limit_ends_with_the_higher_score_and_the_restart_resets_points_and_scores()
    {
        var rules = Seize with { TimeLimit = 25f, RestartDelay = 1f };
        var w = Started(rules, A.Center, Far, A);
        Assert.Equal((uint)(25 * 30), w.TicksLeft);
        Run(w, 25 * 30 - 1);
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Equal(Team.SpaceMarines, w.Points[0].Owner);
        Assert.Equal(2 * 5, w.ScoreOf(Team.SpaceMarines));                  // owned from play tick 600: awards at 600, 630, 660, 690, 720
        Run(w, 1);
        Assert.Equal((MatchState.Ended, Team.SpaceMarines, 12), (w.State, w.Winner, w.ScoreOf(Team.SpaceMarines)));   // the award at 750 comes first
        int guard = 0;
        while (w.State != MatchState.Playing) { w.Step(Idle(w)); Assert.True(++guard <= 31); }
        // The restart tick: points neutral and untouched, scores zero, everyone back in the start areas.
        Assert.Equal((0, 0), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
        Assert.Equal(default, w.Points[0]);
        Assert.Equal(A.Center, w.Players[1].Position);
        Run(w, 1);
        Assert.Equal(new PointState(Team.None, Team.SpaceMarines, 1, false), w.Points[0]);
    }

    [Fact]
    public void An_ended_round_that_goes_back_to_warm_up_clears_its_scores_and_points()
    {
        var w = Started(Seize with { ScoreLimit = 2, RestartDelay = 1f }, A.Center, Far, A);
        Run(w, Full);
        Assert.Equal((MatchState.Ended, 2, Team.SpaceMarines), (w.State, w.ScoreOf(Team.SpaceMarines), w.Points[0].Owner));
        w.Remove(2);                                                         // a side empties: no new round
        Run(w, 31);
        Assert.Equal(MatchState.Warmup, w.State);
        Assert.Equal((0, 0), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
        Assert.Equal(default, w.Points[0]);
        Assert.Equal(new MatchView(MatchState.Warmup, Team.None, 0, 0, 0, GameMode.SeizeGround), GameServer.MatchOf(w));
    }

    [Fact]
    public void Leaving_warm_up_puts_everyone_in_their_start_areas_in_Seize_Ground_only()
    {
        // A lone marine walks from its start area onto A during warm-up; when Chaos joins, it is sent back.
        var w = Arena(Seize, new Vector3(0, -20, 0), Far, A);
        w.Spawn(1, Marine);
        Func<int, PlayerInput> walk = id => new PlayerInput(0, 0, 1, 0f, InputButtons.None);
        while (!A.Contains(w.Players[1].Position)) w.Step(walk);
        w.Spawn(2, ChaosClass);
        Run(w, 1);
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Equal(new Vector3(0, -20, 0), w.Players[1].Position);
        Assert.Equal(Far, w.Players[2].Position);
        Run(w, 1);
        Assert.Equal(default, w.Points[0]);

        // Annihilation plays on where everyone stands (M15 behavior).
        var an = new World(Config, null, [new SpawnPoint(new Vector3(0, -20, 0), 0f), new SpawnPoint(Far, 0f)]) { Rules = new MatchRules { TimeLimit = 0f } };
        an.Spawn(1, Marine);
        Run(an, 20, id => new PlayerInput(0, 0, 1, 0f, InputButtons.None));
        var before = an.Players[1].Position;
        an.Spawn(2, ChaosClass);
        an.Step(Idle(an));
        Assert.Equal(MatchState.Playing, an.State);
        Assert.True(Vector3.Distance(before, an.Players[1].Position) < 1f);
    }

    [Fact]
    public void Nothing_is_captured_in_warm_up()
    {
        var w = Arena(Seize, A.Center, Far, A);
        w.Spawn(1, Marine);
        Run(w, Full + 10);
        Assert.Equal(MatchState.Warmup, w.State);
        Assert.Equal(default, w.Points[0]);
        Assert.Equal(0, w.ScoreOf(Team.SpaceMarines));
    }

    // ---------------------------------------------------------------- spawns

    [Fact]
    public void Seize_Ground_spawns_each_side_in_its_start_area()
    {
        // 6 x 4 m box rotated 90 degrees: spots on a 2 m grid 1 m in from the edge, nearest the centre first.
        var area = new StartArea(Team.SpaceMarines, new Vector3(10, 20, 0), new Vector3(6, 4, 2), MathF.PI / 2);
        var w = new World(Config) { Rules = Seize, ControlPoints = [A], StartAreas = [area, Area(Team.Chaos, Far, MathF.PI)] };
        var first = w.Spawn(1, Marine);
        // M22: without a level or blockers the facing is the area's rotation (out of the room toward its exit otherwise: SpawnRoomTests).
        Assert.Equal((new Vector3(10, 20, 0), MathF.PI / 2), (first.Position, first.Yaw));
        var spots = Enumerable.Range(2, 3).Select(id => w.Spawn(id, Marine).Position).ToList();
        // Local (+-2, 0) rotated by 90 degrees: (0, +-2); local (0, +-2) is outside 4 - 2 x 1 m. Then the centre again (all taken).
        Assert.Equal([new Vector3(10, 18, 0), new Vector3(10, 22, 0)], spots.Take(2).Select(p => new Vector3(MathF.Round(p.X, 4), MathF.Round(p.Y, 4), p.Z)));
        Assert.All(w.Players.Values.Where(p => p.Team == Team.SpaceMarines), p => Assert.Equal(MathF.PI / 2, p.Yaw));
        var chaos = w.Spawn(9, ChaosClass);
        Assert.Equal((Far, MathF.PI), (chaos.Position, chaos.Yaw));

        // With a level: spots drop onto the ground below them (a floor at z 2 under an area centred at z 3).
        var floor = new StaticMesh([new(-50, -50, 2), new(50, -50, 2), new(50, 50, 2), new(-50, 50, 2)], [0, 1, 2, 0, 2, 3]);
        var level = new World(Config, new CollisionWorld(floor, floor)) { Rules = Seize, ControlPoints = [A], StartAreas = [Area(Team.SpaceMarines, new Vector3(5, 5, 3), 0f)] };
        Assert.Equal(new Vector3(5, 5, 2), level.Spawn(1, Marine).Position);

        // A gantry (z 4.2) over part of the area and a roof (z 4.8) over another part, both inside the area's height (z 1..5)
        // and below the casting height: nobody spawns on them, the floor under them is used; 8 per side get 8 distinct spots.
        var mesh = new StaticMesh(
            [new(-50, -50, 2), new(50, -50, 2), new(50, 50, 2), new(-50, 50, 2),
             new(4.5f, 1, 4.2f), new(9, 1, 4.2f), new(9, 9, 4.2f), new(4.5f, 9, 4.2f),
             new(-9, -9, 4.8f), new(-4.5f, -9, 4.8f), new(-4.5f, -1, 4.8f), new(-9, -1, 4.8f)],
            [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 8, 9, 10, 8, 10, 11]);
        var covered = new World(Config, new CollisionWorld(mesh, mesh))
        {
            Rules = Seize, ControlPoints = [A],
            StartAreas = [new StartArea(Team.SpaceMarines, new Vector3(0, 0, 3), new Vector3(18, 18, 4), 0f), new StartArea(Team.Chaos, new Vector3(30, 30, 3), new Vector3(10, 8, 4), 0f)],
        };
        var floorSpots = covered.StartAreaSpots(0);
        Assert.All(floorSpots, q => Assert.Equal(2f, q.Z));
        Assert.Contains(floorSpots, q => q.X >= 4.5f && q.Y >= 1f);          // under the gantry: the floor
        Assert.Contains(floorSpots, q => q.X <= -4.5f && q.Y <= -1f);        // under the roof (2.8 m up): the floor, not the roof
        for (int i = 1; i <= 16; i++) covered.Spawn(i, i <= 8 ? Marine : ChaosClass);
        foreach (var side in new[] { Team.SpaceMarines, Team.Chaos })
        {
            var at = covered.Players.Values.Where(p => p.Team == side).Select(p => p.Position).ToList();
            Assert.Equal(8, at.Count);
            Assert.Equal(8, at.Distinct().Count());
            Assert.All(at, q => Assert.Equal(2f, q.Z));
        }

        // M22: Annihilation uses the start areas too; a side without one falls back to the shared markers (and their facing).
        var annihilation = new World(Config, null, [new SpawnPoint(new Vector3(7, 7, 0), 1f)]) { Rules = new MatchRules(), StartAreas = [area] };
        Assert.Equal(new Vector3(10, 20, 0), annihilation.Spawn(1, Marine).Position);
        Assert.Equal((new Vector3(7, 7, 0), 1f), (annihilation.Spawn(2, ChaosClass).Position, annihilation.Players[2].Yaw));
        // Without rules (free-for-all) the start areas are not used.
        Assert.Equal(new Vector3(7, 7, 0), new World(Config, null, [new SpawnPoint(new Vector3(7, 7, 0), 1f)]) { StartAreas = [area] }.Spawn(1, Marine).Position);
    }

    // ---------------------------------------------------------------- Annihilation unaffected

    [Fact]
    public void Annihilation_ignores_the_points_and_still_scores_deaths()
    {
        var w = new World(Config, null, [new SpawnPoint(A.Center, 0f), new SpawnPoint(A.Center + new Vector3(0, 4, 0), MathF.PI)])
        { Rules = new MatchRules { TimeLimit = 0f }, ControlPoints = [A] };
        w.Spawn(1, Marine); w.Spawn(2, ChaosClass);
        Run(w, 1);
        Assert.Equal(MatchState.Playing, w.State);
        Func<int, PlayerInput> chaosFires = id => new PlayerInput(0, 0, 0, w.Players[id].Yaw, id == 2 && w.Tick % 2 == 0 ? InputButtons.Fire : InputButtons.None);
        int guard = 0;
        while (w.Players[1].Alive) { w.Step(chaosFires); Assert.True(++guard < 120); }
        Run(w, Full + 30);
        Assert.Equal(default, w.Points[0]);
        Assert.Equal((0, 1), (w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos)));
        Assert.Equal(GameMode.Annihilation, GameServer.MatchOf(w).Mode);
        Assert.Empty(GameServer.PointsOf(w));
        Assert.Null(GameServer.SeizeOf(w));
    }

    [Fact]
    public void Worlds_refuse_too_many_or_malformed_points_and_servers_refuse_Seize_Ground_without_points()
    {
        var eight = Enumerable.Range(0, World.MaxControlPoints).Select(i => A with { Name = $"p{i}" }).ToArray();
        Assert.Equal(8, new World(Config) { ControlPoints = eight }.Points.Count);
        Assert.Throws<ArgumentException>(() => new World(Config) { ControlPoints = [.. eight, A] });
        Assert.Throws<ArgumentException>(() => new World(Config) { ControlPoints = [A with { Radius = 0f }] });
        Assert.Throws<ArgumentException>(() => new World(Config) { ControlPoints = [A with { Center = new Vector3(float.NaN, 0, 0) }] });
        Assert.Throws<ArgumentException>(() => new World(Config) { StartAreas = [Area(Team.Chaos, A.Center, 0f) with { Size = Vector3.Zero }] });
        var net = new LoopbackNetwork(new ManualClock(), new LinkConditions(TimeSpan.Zero, TimeSpan.Zero, 0), 1);
        Assert.Throws<ArgumentException>(() => new GameServer(Config, 1, net.Server, new ManualClock(), new ServerOptions { Match = Seize }));
        var floor = new StaticMesh([new(-50, -50, 0), new(50, -50, 0), new(50, 50, 0)], [0, 1, 2]);
        var badName = new GameMap("test", new CollisionWorld(floor, floor), [], 1, [A with { Name = "bad name" }]);
        Assert.Throws<ArgumentException>(() => new GameServer(Config, 1, net.Server, new ManualClock(), new ServerOptions { Match = Seize }, badName));
        Assert.Equal((1000, 20f, 10f, 2, 1f), (Seize.ScoreLimit, Seize.CaptureTime, Seize.NeutraliseTime, Seize.PointScore, Seize.ScoreInterval));
        Assert.Equal(41, MatchRules.For(GameMode.Annihilation).ScoreLimit);
    }

    // ---------------------------------------------------------------- protocol 11

    static byte[] Patch(byte[] bytes, int at, int value) { var copy = (byte[])bytes.Clone(); copy[at] = (byte)value; return copy; }

    [Fact]
    public void Protocol_11_carries_the_mode_and_the_points_within_the_budget_and_refuses_malformed_ones()
    {
        Assert.Equal(48, Protocol.Version);
        Assert.Equal(1194, Protocol.MaxSnapshotBytes);   // protocol 21: + 1 ready-tick byte; protocol 12: + the 12-byte hit block; protocol 13: + 18 evade bytes − 1 projectile record; 16: − 3 projectile records + 5 shot records; 17: + 4 reaction bytes − 1 shot record
        Assert.Equal(8, Protocol.MaxSnapshotPoints);
        ControlPointView[] points =
        [
            new(Team.None, Team.None, 0f, false), new(Team.SpaceMarines, Team.SpaceMarines, 1f, false),
            new(Team.None, Team.Chaos, 0.5f, true), new(Team.Chaos, Team.Chaos, 0.25f, true),
        ];
        var match = new MatchView(MatchState.Playing, Team.None, 99, 1000, 65535, GameMode.SeizeGround);
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, [], Match: match, Points: points));
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal(match, back.Match);
        Assert.Equal(points.Select(Protocol.Quantize), back.Points!);
        Assert.Equal(128 / 255f, back.Points![2].Progress);                 // 1/255 steps
        Assert.Equal(1 + 1 + 4 * 2 + Protocol.HitBlockBytes + 1, bytes.Length - (13 + 1 + 1 + 9));   // kill count, point count, 4 points, the hit block, the shot count (16)
        // Normalised on the way out: an owned point's side is its owner; no side, no progress; NaN and out of range clamp.
        var odd = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [], Points:
            [new(Team.Chaos, Team.SpaceMarines, 2f, false), new(Team.None, Team.None, 0.7f, false), new(Team.None, (Team)7, float.NaN, false), new((Team)3, Team.Chaos, -1f, false)]))));
        Assert.Equal([new(Team.Chaos, Team.Chaos, 1f, false), new(Team.None, Team.None, 0f, false), new(Team.None, Team.None, 0f, false), new(Team.None, Team.Chaos, 0f, false)], odd.Points!);
        // At most 8 travel.
        var many = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [], Points: [.. Enumerable.Repeat(points[1], 12)]))));
        Assert.Equal(8, many.Points!.Length);

        // Refusals: count 9, unknown flag bits, owner or side 3, an owned point whose side differs, progress without a side.
        int count = bytes.Length - 1 - Protocol.HitBlockBytes - 1 - 4 * 2;
        Assert.Equal(4, bytes[count]);
        Assert.Null(Protocol.Read(Patch(bytes, count, 9)));
        int flags = count + 1 + 2 * 1;                                       // the second point (Space Marines owned)
        Assert.Equal(1 | (1 << 2), bytes[flags]);
        Assert.NotNull(Protocol.Read(Patch(bytes, flags, 1 | (1 << 2) | 16)));   // control: contested
        Assert.Null(Protocol.Read(Patch(bytes, flags, 1 | (1 << 2) | 32)));
        Assert.Null(Protocol.Read(Patch(bytes, flags, 3 | (3 << 2))));
        Assert.Null(Protocol.Read(Patch(bytes, flags, 1 | (2 << 2))));
        Assert.Equal((0, 0), (bytes[count + 1], bytes[count + 2]));         // the first point: neutral, untouched
        Assert.Null(Protocol.Read(Patch(bytes, count + 2, 5)));              // progress without a side
        // The mode: 1 is Seize Ground, 2 and 3 are refused.
        int state = 13 + 1 + 1;
        Assert.Equal((int)MatchState.Playing | (1 << 4), bytes[state]);
        Assert.Null(Protocol.Read(Patch(bytes, state, (int)MatchState.Playing | (2 << 4))));
        for (int n = 0; n < bytes.Length; n++) Assert.Null(Protocol.Read(bytes.AsSpan(0, n)));
    }

    [Fact]
    public void The_scoreboard_carries_the_Seize_Ground_rules_and_layout_and_refuses_malformed_ones()
    {
        var seize = new SeizeGroundInfo(20f, 10f, 2, 1f, [new("mkr_objective_1", new Vector3(110, -785, 4), 15f, 10f), new("mkr_cp_02", new Vector3(-1, 2, -3), 13.5f, 2.5f)]);
        var board = new Scoreboard(5, true, 1000, 600f, false, [new ScoreEntry(1, Team.SpaceMarines, 0, 1, 2)], seize);
        var bytes = Protocol.Write(board);
        var back = Assert.IsType<Scoreboard>(Protocol.Read(bytes));
        Assert.Equal((GameMode.SeizeGround, 1000, 600f), (back.Mode, back.ScoreLimit, back.TimeLimit));
        Assert.Equal((20f, 10f, 2, 1f), (back.Seize!.CaptureTime, back.Seize.NeutraliseTime, back.Seize.PointScore, back.Seize.ScoreInterval));
        Assert.Equal(seize.Points, back.Seize.Points);
        Assert.Equal(board.Players, back.Players);
        int layout = 1 + 4 + 1 + 2 + 4 + 4 + 4 + 2 + 4 + 1;
        Assert.Equal(layout + (1 + 15 + 20) + (1 + 9 + 20) + 1 + 14, bytes.Length);   // protocol 22: 12-byte rows; protocol 40: 13; protocol 41: 14 (an empty name)
        Assert.Equal(GameMode.Annihilation, Assert.IsType<Scoreboard>(Protocol.Read(Protocol.Write(board with { Seize = null }))).Mode);

        Assert.Null(Protocol.Read(Patch(bytes, layout - 1, 9)));            // 9 points
        Assert.Null(Protocol.Read(Patch(bytes, layout, 25)));               // a name of 25 bytes
        Assert.Null(Protocol.Read(Patch(bytes, layout + 1, (byte)' ')));    // not an identifier
        Assert.Null(Protocol.Read(Patch(bytes, layout + 1 + 15 + 12 + 3, 0xFF)));   // radius NaN (0xFF in the top byte)
        var zero = Protocol.Write(board with { Seize = seize with { Points = [] } });
        Assert.Empty(Assert.IsType<Scoreboard>(Protocol.Read(zero)).Seize!.Points);
        // Writers refuse what readers would refuse.
        Assert.Throws<ArgumentException>(() => Protocol.Write(board with { Seize = seize with { CaptureTime = 0f } }));
        Assert.Throws<ArgumentException>(() => Protocol.Write(board with { Seize = seize with { Points = [seize.Points[0] with { Name = "no spaces" }] } }));
        Assert.Throws<ArgumentException>(() => Protocol.Write(board with { Seize = seize with { Points = [.. Enumerable.Repeat(seize.Points[0], 9)] } }));
        for (int n = 0; n < bytes.Length; n++) Assert.Null(Protocol.Read(bytes.AsSpan(0, n)));
    }

    [Fact]
    public void A_sixteen_player_Seize_Ground_snapshot_with_every_record_full_fits_the_budget()
    {
        var players = Enumerable.Range(1, 16).Select(i => new PlayerState(i, new Vector3(i, -i, 1e6f), Vector3.One, 1f, true) { Alive = true, Team = Team.Chaos }).ToArray();
        var projectiles = Enumerable.Range(1, 20).Select(i => new ProjectileView(i, 0, 0, new Vector3(1e5f, -1e5f, 1e4f), ProjectileFlags.Stuck)).ToArray();
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, players, Self: 7, Projectiles: projectiles, Match: new MatchView(MatchState.Ended, Team.Chaos, 1, 65535, 65535, GameMode.SeizeGround),
            Kills: [.. Enumerable.Range(0, 4).Select(k => new KillView(k, 1, 2, KillCause.Weapon, 0))], Points: [.. Enumerable.Repeat(new ControlPointView(Team.Chaos, Team.Chaos, 0.5f, true), 8)], Shots: ShotTests.ManyShots(players, Protocol.MaxSnapshotShots)));
        output.WriteLine($"16 players, 20 projectiles, 4 kills, 8 points: {bytes.Length} bytes");
        Assert.Equal(Protocol.MaxSnapshotBytes, bytes.Length);
        Assert.True(bytes.Length <= UdpServerTransport.MaxPacket && bytes.Length <= 1200);
        Assert.Equal(8, Assert.IsType<Snapshot>(Protocol.Read(bytes)).Points!.Length);
    }

    // ---------------------------------------------------------------- end to end

    [Fact]
    public void Clients_see_the_layout_the_points_and_the_mode_and_predict_exactly()
    {
        var floor = new StaticMesh([new(-100, -100, 0), new(100, -100, 0), new(100, 100, 0), new(-100, 100, 0)], [0, 1, 2, 0, 2, 3]);
        var collision = new CollisionWorld(floor, floor);
        var map = new GameMap("test_seize", collision, [], floor.ContentHash, [A, B],
            [Area(Team.SpaceMarines, A.Center, 0f), Area(Team.Chaos, new Vector3(-30, 0, 0), MathF.PI)]);
        var options = new ClientOptions { LoadMap = _ => new LoadedMap(collision, floor.ContentHash) };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(5), 0.05);
        var h = new Harness(link, 5, 0, Config, new ServerOptions { Match = Seize with { CaptureTime = 2f } }, map: map, clientOptions: options);
        var a = h.AddClient(nonce: 100, options: options with { RequestedClass = Marine });
        var b = h.AddClient(nonce: 101, options: options with { RequestedClass = ChaosClass });
        var captures = new List<CaptureEvent>();
        h.Server.Stepped += w => captures.AddRange(w.LastCaptures);
        h.Run(TimeSpan.FromSeconds(4));
        var world = h.Server.World;
        Assert.Equal(MatchState.Playing, world.State);
        var capture = Assert.Single(captures);
        Assert.Equal((0, CaptureKind.Captured, Team.SpaceMarines), (capture.Point, capture.Kind, capture.Side));
        Assert.Equal(Team.SpaceMarines, world.Points[0].Owner);
        foreach (var c in new[] { a, b })
        {
            Assert.Equal(GameMode.SeizeGround, c.Match.Mode);
            Assert.Equal(2, c.Points.Count);
            Assert.Equal(new ControlPointView(Team.SpaceMarines, Team.SpaceMarines, 1f, false), c.Points[0]);
            Assert.Equal(default, c.Points[1]);
            Assert.True(c.Match.MarineScore >= 2 && c.Match.ChaosScore == 0, $"score {c.Match.MarineScore}-{c.Match.ChaosScore}");
            var board = Assert.IsType<Scoreboard>(c.Scoreboard);
            Assert.Equal(GameMode.SeizeGround, board.Mode);
            Assert.Equal([new ControlPointInfo(A.Name, A.Center, A.Radius, A.HalfHeight), new ControlPointInfo(B.Name, B.Center, B.Radius, B.HalfHeight)], board.Seize!.Points);
            Assert.Equal((2f, 10f, 2, 1f, 1000), (board.Seize.CaptureTime, board.Seize.NeutraliseTime, board.Seize.PointScore, board.Seize.ScoreInterval, board.ScoreLimit));
        }
        // Prediction stays exact (capture state is server-only).
        h.Run(TimeSpan.FromSeconds(4), Harness.Scripted);
        foreach (var c in h.Clients)
        {
            var e = c.CorrectionErrors;
            output.WriteLine($"player {c.PlayerId}: {e.Count} corrections, max {e.Max():F6} m");
            Assert.True(e.Max() < 1e-4f, $"max correction {e.Max()}");
        }
    }
}
