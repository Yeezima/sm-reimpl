using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M22: in a team match (Annihilation and Seize Ground) every spawn of a side's player (first spawn, respawn after death,
/// round start and restart) is in that side's start area (spawn room), facing out of the room toward its exit (or the marker's
/// rotation without blockers). A synthetic layout with
/// Manufactorum's plan (start-area and point marker positions, sizes and rotations as recorded in docs/discovery.md; heights
/// flattened to a floor at z 0) and shared markers, some deliberately inside the other side's room.
/// </summary>
public class SpawnRoomTests(ITestOutputHelper output)
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 20f, 0.15f, 30, 270, 0.5f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static ClassLoadout Tac(string name, Team side) => new(name, 0, -1, SimConfig.PlaceholderMeleeSteps) { Side = side, Role = "tactical" };
    const int Marine = 0, ChaosClass = 1;

    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle], Classes = [Tac("sm - tactical", Team.SpaceMarines), Tac("csm - tactical", Team.Chaos)], RespawnTime = 0.5f,
    };

    static readonly StartArea MarineArea = new(Team.SpaceMarines, new Vector3(245f, -937.377f, 0.5f), new Vector3(10, 8, 5), 0f);
    static readonly StartArea ChaosArea = new(Team.Chaos, new Vector3(154.493f, -664.391f, 0.5f), new Vector3(10, 8, 5), MathF.PI);
    static readonly ControlPoint[] Points =
    [
        new("mkr_objective_1", new Vector3(110, -785, 4), 15f, 10f),
        new("mkr_objective_2", new Vector3(200, -800, 0), 13.5f, 10f),
        new("mkr_objective_3", new Vector3(290, -815, 4), 15f, 10f),
    ];
    /// <summary>Shared markers: one in each room (as tdm_mkr_spawn_03 / _04 lie next to the rooms) and two in the middle.</summary>
    static readonly SpawnPoint[] Shared =
    [
        new(new Vector3(154, -665, 0), 0f), new(new Vector3(245, -937, 0), MathF.PI), new(new Vector3(200, -810, 0), 1f), new(new Vector3(200, -790, 0), 2f),
    ];

    /// <summary>A floor at z 0; kill volumes across each side's way to the middle, 25 m out of its room (walking into one kills).</summary>
    static CollisionWorld Level()
    {
        var floor = new StaticMesh([new(0, -1100, 0), new(400, -1100, 0), new(400, -500, 0), new(0, -500, 0)], [0, 1, 2, 0, 2, 3]);
        return new CollisionWorld(floor, floor,
        [
            new KillVolume(new Vector3(200, -905, 1), new Vector3(150, 3, 5), 0f),
            new KillVolume(new Vector3(200, -697, 1), new Vector3(150, 3, 5), 0f),
        ]);
    }

    static World Arena(MatchRules rules) => new(Config, Level(), Shared) { Rules = rules, ControlPoints = Points, StartAreas = [MarineArea, ChaosArea] };

    static bool InArea(StartArea a, Vector3 p)
    {
        var d = p - a.Center;
        float c = MathF.Cos(-a.Yaw), s = MathF.Sin(-a.Yaw);
        var local = new Vector2(d.X * c - d.Y * s, d.X * s + d.Y * c);
        return MathF.Abs(local.X) <= a.Size.X / 2f + 1e-3f && MathF.Abs(local.Y) <= a.Size.Y / 2f + 1e-3f;
    }

    /// <summary>
    /// The player stands in its own side's area (not the other one) and faces as the area says: these worlds have no
    /// blockers, so the marker's rotation (the exit rule is tested below).
    /// </summary>
    static void AssertSpawned(World w, PlayerState p, string when)
    {
        var own = p.Team == Team.SpaceMarines ? MarineArea : ChaosArea;
        var other = p.Team == Team.SpaceMarines ? ChaosArea : MarineArea;
        Assert.True(InArea(own, p.Position), $"{when}: player {p.PlayerId} ({p.Team}) at {p.Position} is not in its start area");
        Assert.False(InArea(other, p.Position), $"{when}: player {p.PlayerId} in the other side's area");
        int index = w.StartAreas.ToList().FindIndex(a => a.Side == p.Team);
        Assert.Equal(SpawnFacingSource.MarkerRotation, w.StartAreaFacings(index).Source);
        Assert.Equal(own.Yaw, p.Yaw);
    }

    [Fact]
    public void The_middle_is_the_points_centroid_and_without_blockers_spawns_keep_the_marker_rotation()
    {
        var w = Arena(new MatchRules());
        var middle = w.MapMiddle!.Value;
        Assert.Equal(200f, middle.X, 3); Assert.Equal(-800f, middle.Y, 3);   // point B's centre in plan (kept for comparison)
        Assert.Equal((0f, MathF.PI), (w.StartAreaFacing(0, MarineArea.Center), w.StartAreaFacing(1, ChaosArea.Center)));
        Assert.Contains("no blockers", w.StartAreaFacings(0).Note);
        // Without a level: also the marker rotation.
        var flat = new World(Config) { Rules = new MatchRules(), StartAreas = [MarineArea with { Yaw = 1f }], SpawnBlockers = [new SpawnBlocker(Team.SpaceMarines, new Vector3(245, -925, 2), new Vector3(10, 1, 6), 0f)] };
        Assert.Equal((SpawnFacingSource.MarkerRotation, 1f), (flat.StartAreaFacings(0).Source, flat.Spawn(1, Marine).Yaw));
        Assert.Contains("no level", flat.StartAreaFacings(0).Note);
        // Without points: the start areas' midpoint; with one area and no points: none.
        var noPoints = new World(Config) { Rules = new MatchRules(), StartAreas = [MarineArea, ChaosArea] };
        Assert.Equal((MarineArea.Center + ChaosArea.Center) / 2f, noPoints.MapMiddle);
        Assert.Null(new World(Config) { Rules = new MatchRules(), StartAreas = [MarineArea] }.MapMiddle);
    }

    // ---------------------------------------------------------------- facing out of the room (M22, user's decision)

    static (Vector3, Vector3, Vector3, Vector3) Quad(float x0, float y0, float x1, float y1, float z) =>
        (new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z));
    static (Vector3, Vector3, Vector3, Vector3) WallQuad(float x0, float y0, float x1, float y1) =>
        (new(x0, y0, -1f), new(x1, y1, -1f), new(x1, y1, 4f), new(x0, y0, 4f));

    static CollisionWorld Rooms(params (Vector3 A, Vector3 B, Vector3 C, Vector3 D)[] walls)
    {
        var quads = walls.Prepend(Quad(-40, -40, 40, 40, 0f)).ToList();
        var v = new List<Vector3>(); var idx = new List<int>();
        foreach (var (a, b, c, d) in quads) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    static readonly StartArea RoomArea = new(Team.SpaceMarines, new Vector3(0, 0, 1), new Vector3(6, 6, 2), MathF.PI);   // marker faces south (a wall)

    static float Clear(World w, Vector3 spot, float yaw) =>
        w.Collision!.Character.Raycast(spot + new Vector3(0, 0, w.Config.EyeHeight), new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0f), 100f)?.Distance ?? 100f;

    [Fact]
    public void In_an_L_shaped_room_spawns_face_along_the_first_leg_toward_the_exit()
    {
        // A 10 x 10 m room (x, y -5..5) open on the north wall at x 2..5; a corridor north (x 2..5) to y 20, then west to
        // x -20 (y 15..20), where the blocker closes it. The marker faces south, into a wall.
        var level = Rooms(
            WallQuad(-5, -5, 5, -5), WallQuad(-5, -5, -5, 5), WallQuad(5, -5, 5, 20), WallQuad(-5, 5, 2, 5),   // room (north wall to x 2)
            WallQuad(2, 5, 2, 15), WallQuad(5, 20, -20, 20), WallQuad(2, 15, -20, 15));                         // corridor north, then west
        var w = new World(Config, level) { Rules = new MatchRules(), StartAreas = [RoomArea],
            SpawnBlockers = [new SpawnBlocker(Team.SpaceMarines, new Vector3(-15, 17.5f, 2), new Vector3(1, 5, 6), 0f), new SpawnBlocker(Team.Chaos, new Vector3(0, -30, 2), new Vector3(5, 1, 6), 0f)] };
        var f = w.StartAreaFacings(0);
        var spots = w.StartAreaSpots(0);
        output.WriteLine($"L room: {f.Source} ({f.Note}), exit {f.Exit}; facings {string.Join(" ", f.Yaws.Select(y => $"{y * 180f / MathF.PI:0.#}"))}; clear {string.Join(" ", spots.Select((q, k) => $"{Clear(w, q, f.Yaws[k]):0.#}"))}");
        Assert.Equal(SpawnFacingSource.Exit, f.Source);
        Assert.NotNull(f.Exit);
        Assert.True(f.Exit!.Value.X < -13f && f.Exit.Value.Y is > 15f and < 20f, $"exit {f.Exit}");   // next to the Space Marines' blocker
        for (int k = 0; k < spots.Count; k++)
        {
            var dir = new Vector2(-MathF.Sin(f.Yaws[k]), MathF.Cos(f.Yaws[k]));
            Assert.True(dir.Y > 0.3f, $"spot {spots[k]}: facing {f.Yaws[k]} does not go north, toward the opening");   // the first leg
            Assert.True(Clear(w, spots[k], f.Yaws[k]) >= World.MinClearView, $"spot {spots[k]}: a wall ahead");
        }
        var p = w.Spawn(1, Marine);
        Assert.Equal(w.StartAreaFacing(0, p.Position), p.Yaw);
        Assert.NotEqual(RoomArea.Yaw, p.Yaw);
    }

    [Fact]
    public void In_a_straight_room_spawns_face_the_exit_and_without_blockers_the_marker_rotation()
    {
        // A room x -5..5, y -5..15 with the blocker across its north end: everyone faces north (yaw 0), shared.
        var level = Rooms(WallQuad(-5, -5, 5, -5), WallQuad(-5, -5, -5, 30), WallQuad(5, -5, 5, 30));
        var blocker = new SpawnBlocker(Team.SpaceMarines, new Vector3(0, 15, 2), new Vector3(10, 1, 6), 0f);
        var w = new World(Config, level) { Rules = new MatchRules(), StartAreas = [RoomArea], SpawnBlockers = [blocker] };
        var f = w.StartAreaFacings(0);
        output.WriteLine($"straight room: {f.Source} ({f.Note}), exit {f.Exit}; facings {string.Join(" ", f.Yaws.Select(y => $"{y * 180f / MathF.PI:0.#}"))}");
        Assert.Equal(SpawnFacingSource.Exit, f.Source);
        Assert.StartsWith("shared", f.Note);
        Assert.All(f.Yaws, y => Assert.True(MathF.Abs(y) < 0.15f, $"facing {y} rad"));
        Assert.All(f.Yaws, y => Assert.Equal(f.Yaws[0], y));
        Assert.InRange(f.Exit!.Value.Y, 13.5f, 14.6f);
        // The same room without blockers: the marker's rotation.
        var bare = new World(Config, level) { Rules = new MatchRules(), StartAreas = [RoomArea] };
        Assert.Equal((SpawnFacingSource.MarkerRotation, MathF.PI), (bare.StartAreaFacings(0).Source, bare.Spawn(1, Marine).Yaw));
        // Blockers out of reach (a closed room): the marker's rotation too.
        var closed = Rooms(WallQuad(-5, -5, 5, -5), WallQuad(-5, -5, -5, 10), WallQuad(5, -5, 5, 10), WallQuad(-5, 10, 5, 10));
        var shut = new World(Config, closed) { Rules = new MatchRules(), StartAreas = [RoomArea], SpawnBlockers = [blocker] };
        Assert.Equal(SpawnFacingSource.MarkerRotation, shut.StartAreaFacings(0).Source);
    }

    [Theory]
    [InlineData(GameMode.Annihilation)]
    [InlineData(GameMode.SeizeGround)]
    public void Every_spawn_is_in_the_own_spawn_room(GameMode mode)
    {
        // A low score limit (Annihilation: deaths) and a short restart, so the run covers deaths, respawns, an end and a restart.
        var rules = MatchRules.For(mode) with { ScoreLimit = mode == GameMode.Annihilation ? 6 : 40, TimeLimit = 0f, RestartDelay = 1f };
        var w = Arena(rules);
        for (int id = 1; id <= 16; id++)
        {
            var p = w.Spawn(id, id % 2 == 1 ? Marine : ChaosClass);
            AssertSpawned(w, p, "first spawn");
        }
        Assert.Equal(16, w.Players.Values.Select(p => p.Position).Distinct().Count());   // 15 spots per 10 x 8 m area, 8 used

        int respawns = 0, restarts = 0, roundStarts = 0;
        var before = w.Players.ToDictionary(kv => kv.Key, kv => kv.Value);
        var state = w.State;
        for (int t = 0; t < 30 * 90; t++)
        {
            // Everyone walks the way it faces (toward the middle), into the kill volume in front of its room; some stand still.
            w.Step(id => new PlayerInput(0, 0, id % 4 == 0 && t % 600 < 300 ? 0f : 1f, w.Players[id].Yaw, InputButtons.Sprint));
            bool roundStarted = state != MatchState.Playing && w.State == MatchState.Playing && mode == GameMode.SeizeGround || state == MatchState.Ended && w.State == MatchState.Playing;
            if (roundStarted)
            {
                if (state == MatchState.Ended) restarts++; else roundStarts++;
                foreach (var p in w.Players.Values) AssertSpawned(w, p, $"round start at tick {w.Tick}");
            }
            foreach (var (id, p) in w.Players)
                if (!before[id].Alive && p.Alive)
                {
                    respawns++;
                    AssertSpawned(w, p, $"respawn at tick {w.Tick}");
                }
            state = w.State;
            before = w.Players.ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        output.WriteLine($"{mode}: {respawns} respawns, {roundStarts} round starts from warm-up, {restarts} restarts; team scores {w.ScoreOf(Team.SpaceMarines)} - {w.ScoreOf(Team.Chaos)}");
        Assert.True(respawns >= 100, $"only {respawns} respawns");
        if (mode == GameMode.Annihilation) Assert.True(restarts >= 2, $"only {restarts} restarts");
        else Assert.Equal(1, roundStarts);
    }

    [Fact]
    public void A_side_without_a_start_area_uses_the_shared_markers()
    {
        var w = new World(Config, null, Shared) { Rules = new MatchRules(), ControlPoints = Points, StartAreas = [MarineArea] };
        var marine = w.Spawn(1, Marine);
        AssertSpawned(w, marine, "marine");
        var chaos = w.Spawn(2, ChaosClass);
        Assert.Contains(Shared, q => q.Position == chaos.Position && q.Yaw == chaos.Yaw);   // a marker, with its own facing
    }

    [Fact]
    public void Without_rules_the_start_areas_are_not_used()
    {
        var w = new World(Config, null, Shared) { ControlPoints = Points, StartAreas = [MarineArea, ChaosArea] };
        for (int id = 1; id <= 4; id++)
        {
            var p = w.Spawn(id, id % 2 == 1 ? Marine : ChaosClass);
            Assert.Contains(Shared, q => q.Position == p.Position && q.Yaw == p.Yaw);
        }
    }

    [Fact]
    public void With_all_15_spots_taken_a_spawn_still_lands_in_the_own_area()
    {
        var w = new World(Config) { Rules = new MatchRules { MaxPerSide = 32 }, ControlPoints = Points, StartAreas = [MarineArea, ChaosArea] };
        Assert.Equal(15, w.StartAreaSpots(0).Count);
        for (int id = 1; id <= 17; id++) AssertSpawned(w, w.Spawn(id, Marine), $"marine {id}");
        var at = w.Players.Values.Select(p => p.Position).ToList();
        Assert.Equal(15, at.Distinct().Count());   // 15 free spots first, then the rotation goes on over taken ones
    }

    [Fact]
    public void A_single_start_area_is_used_by_its_side()
    {
        var withPoints = new World(Config) { Rules = MatchRules.For(GameMode.SeizeGround), ControlPoints = Points, StartAreas = [ChaosArea] };
        AssertSpawned(withPoints, withPoints.Spawn(1, ChaosClass), "single area with points");
        var bare = new World(Config) { Rules = new MatchRules(), StartAreas = [ChaosArea] };
        var p = bare.Spawn(1, ChaosClass);
        Assert.Equal((ChaosArea.Center, MathF.PI), (p.Position, p.Yaw));
    }
}
