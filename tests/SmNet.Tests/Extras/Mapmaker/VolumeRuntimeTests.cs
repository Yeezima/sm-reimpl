using System.Numerics;
using SmSim;
using SmSim.Extras.Mapmaker;

namespace SmNet.Tests;

/// <summary>
/// Mapmaker edited volumes in the simulation (added content, docs/mapmaker.md "Edit mode in play", plan M2): a capture point, start area, spawn door or kill zone with a
/// <see cref="MeshVolume"/> uses its exact shape; without one everything is bit for bit as before (the stock fingerprints below were pinned on the code before the volumes
/// were wired in). Synthetic geometry only.
/// </summary>
public class VolumeRuntimeTests
{
    static readonly SimConfig Config = new();

    static ulong Fnv(ulong h, uint v) { for (int i = 0; i < 4; i++) { h ^= (v >> (8 * i)) & 0xFF; h *= 0x100000001B3UL; } return h; }
    static ulong Fnv(ulong h, float f) => Fnv(h, BitConverter.SingleToUInt32Bits(f));
    static ulong Fnv(ulong h, Vector3 v) => Fnv(Fnv(Fnv(h, v.X), v.Y), v.Z);
    const ulong Offset = 0xCBF29CE484222325UL;

    /// <summary>A 60 x 60 m floor at z 0 with a 1 m step (x 10..20) and a wall at y = 12 (x -10..10, 4 m tall).</summary>
    static StaticMesh Level()
    {
        var v = new List<Vector3> { new(-30, -30, 0), new(30, -30, 0), new(30, 30, 0), new(-30, 30, 0) };
        var i = new List<int> { 0, 1, 2, 0, 2, 3 };
        int b = v.Count;
        v.AddRange([new(10, -5, 1), new(20, -5, 1), new(20, 5, 1), new(10, 5, 1)]);
        i.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        b = v.Count;
        v.AddRange([new(-10, 12, 0), new(10, 12, 0), new(10, 12, 4), new(-10, 12, 4)]);
        i.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        return new StaticMesh([.. v], [.. i]);
    }

    static readonly KillVolume StockKill = new(new Vector3(-15, -15, 1), new Vector3(3, 4, 1), 0.3f);
    static readonly SpawnBlocker StockMarineDoor = new(Team.SpaceMarines, new Vector3(0, 8, 3), new Vector3(6, 1, 6), 0.2f);
    static readonly SpawnBlocker StockChaosDoor = new(Team.Chaos, new Vector3(5, -8, 3), new Vector3(6, 1, 6), -0.4f);
    static readonly ControlPoint StockPoint = new("mkr_objective_1", new Vector3(15, 0, 3), 4f, 2f);
    static readonly StartArea StockArea = new(Team.SpaceMarines, new Vector3(0, 0, 1.5f), new Vector3(12, 10, 3), 0.25f);

    static IEnumerable<Vector3> Grid(Vector3 lo, Vector3 hi, float step)
    {
        for (float x = lo.X; x <= hi.X; x += step)
            for (float y = lo.Y; y <= hi.Y; y += step)
                for (float z = lo.Z; z <= hi.Z; z += step)
                    yield return new Vector3(x, y, z);
    }

    // ---- the stock behaviour, unchanged (pinned before M2) ----

    [Fact]
    public void Stock_collision_world_hash_side_meshes_and_box_tests_are_unchanged()
    {
        var walls = new[] { WorldBlocker.Box("w", Matrix4x4.CreateScale(2, 2, 3) * Matrix4x4.CreateTranslation(-20, 20, 1.5f)) };
        var world = new CollisionWorld(Level(), Level(), [StockKill], [StockMarineDoor, StockChaosDoor], null, walls);
        ulong h = Offset;
        h = Fnv(h, (uint)(world.ContentHash & 0xFFFFFFFF)); h = Fnv(h, (uint)(world.ContentHash >> 32));
        foreach (var side in new[] { Team.SpaceMarines, Team.Chaos, Team.None })
        {
            var m = world.CharacterFor(side);
            h = Fnv(h, (uint)(m.ContentHash & 0xFFFFFFFF)); h = Fnv(h, (uint)(m.ContentHash >> 32));
        }
        foreach (var p in Grid(new Vector3(-20, -20, -1), new Vector3(20, 20, 7), 0.7f))
        {
            h = Fnv(h, StockKill.Contains(p) ? 1u : 0u);
            h = Fnv(h, StockKill.Crosses(p, p + new Vector3(1.3f, -0.6f, 0.9f)) ? 1u : 0u);
            h = Fnv(h, world.KillsPlayer(p, p + new Vector3(0.4f, 0, -0.8f), Config.CapsuleHeight) ? 1u : 0u);
            h = Fnv(h, StockMarineDoor.Contains(p) ? 1u : 0u);
            h = Fnv(h, StockChaosDoor.PushOut(p, Config.CapsuleRadius, Config.CapsuleHeight));
            h = Fnv(h, StockPoint.Contains(p) ? 1u : 0u);
        }
        foreach (var b in world.Blockers) foreach (var c in b.Corners()) h = Fnv(h, c);
        Assert.Equal(StockFingerprint, h);
    }

    const ulong StockFingerprint = 13912869646446837435UL;

    [Fact]
    public void Stock_start_area_spots_facings_and_capture_are_unchanged()
    {
        var level = new CollisionWorld(Level(), Level(), null, [StockMarineDoor, StockChaosDoor]);
        var world = new World(Config, level, [new SpawnPoint(Vector3.Zero, 0f)]) { StartAreas = [StockArea], ControlPoints = [StockPoint] };
        ulong h = Offset;
        var spots = world.StartAreaSpots(0);
        foreach (var s in spots) h = Fnv(h, s);
        var f = world.StartAreaFacings(0);
        h = Fnv(h, (uint)f.Source);
        foreach (var y in f.Yaws) h = Fnv(h, y);
        var flat = new World(Config) { StartAreas = [StockArea] };
        foreach (var s in flat.StartAreaSpots(0)) h = Fnv(h, s);
        Assert.Equal(StockSpotsFingerprint, h);
    }

    const ulong StockSpotsFingerprint = 8447509927339958168UL;

    // ---- edited shapes ----

    /// <summary>A vertical prism over a convex polygon (counter-clockwise in plan) from <paramref name="z0"/> to <paramref name="z1"/>, as a closed triangle mesh.</summary>
    static ExactMeshVolume Prism(string name, Vector2[] plan, float z0, float z1)
    {
        int n = plan.Length;
        var v = new List<Vector3>();
        foreach (var p in plan) v.Add(new Vector3(p.X, p.Y, z0));
        foreach (var p in plan) v.Add(new Vector3(p.X, p.Y, z1));
        var i = new List<int>();
        for (int k = 1; k + 1 < n; k++) { i.AddRange([0, k + 1, k]); i.AddRange([n, n + k, n + k + 1]); }
        for (int k = 0; k < n; k++) { int j = (k + 1) % n; i.AddRange([k, j, n + j, k, n + j, n + k]); }
        return new ExactMeshVolume(new MeshVolume(name, [.. v], [.. i]));
    }

    /// <summary>A right-triangle wedge over x 0..10, y 0..10 (the corner at +X +Y is cut away), z 0..3: (2, 2) is inside, (8, 8) is in the box only.</summary>
    static ExactMeshVolume Wedge(float dx = 0, float dy = 0) => Prism("wedge", [new(dx, dy), new(dx + 10, dy), new(dx, dy + 10)], 0, 3);

    static readonly WeaponSpec Rifle = new("test_rifle", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.2f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static ClassLoadout Tac(string name, Team side) => new(name, 0, -1, SimConfig.PlaceholderMeleeSteps) { PrimaryChoices = [0], Side = side, Role = "tactical" };
    static readonly SimConfig TeamConfig = new() { Weapons = [Rifle], Classes = [Tac("sm - tactical", Team.SpaceMarines), Tac("csm - tactical", Team.Chaos)], RespawnTime = 60f };

    [Fact]
    public void An_edited_capture_point_is_held_inside_its_shape_and_not_inside_only_its_box()
    {
        var shape = Wedge();
        var point = new ControlPoint("mkr_objective_1", new Vector3(5, 5, 1.5f), 5f, 1.5f) { Volume = shape };
        Assert.True(point.Contains(new Vector3(2, 2, 0)));     // feet on the floor at the shape's bottom: lifted into it
        Assert.False(point.Contains(new Vector3(8, 8, 0)));    // inside the box (and the old cylinder of radius 5 round 5,5), outside the wedge
        Assert.True(new ControlPoint("stock", new Vector3(5, 5, 1.5f), 5f, 1.5f).Contains(new Vector3(8, 8, 0)));   // the box rule would have counted it
        Assert.False(point.Contains(new Vector3(2, 2, 3.5f)));  // above

        foreach (var (spot, captures) in new[] { (new Vector3(2, 2, 0), true), (new Vector3(8, 8, 0), false) })
        {
            var rules = MatchRules.For(GameMode.SeizeGround) with { TimeLimit = 0f };
            var w = new World(TeamConfig) { Rules = rules, ControlPoints = [point],
                StartAreas = [new StartArea(Team.SpaceMarines, spot, new Vector3(1, 1, 2), 0f), new StartArea(Team.Chaos, new Vector3(-60, 0, 0), new Vector3(1, 1, 2), 0f)] };
            w.Spawn(1, 0); w.Spawn(2, 1);
            for (int t = 0; t < 40; t++) w.Step(id => new PlayerInput(0, 0, 0, w.Players[id].Yaw, InputButtons.None));
            Assert.Equal(captures ? Team.SpaceMarines : Team.None, w.Points[0].Side);
            Assert.Equal(captures, w.Points[0].Progress > 0);
        }
    }

    [Fact]
    public void An_edited_kill_volume_kills_inside_its_shape_and_not_inside_only_its_box()
    {
        var kill = new KillVolume(new Vector3(5, 5, 1.5f), new Vector3(5, 5, 1.5f), 0f) { Shape = Wedge() };
        Assert.True(kill.Contains(new Vector3(2, 2, 1)));
        Assert.False(kill.Contains(new Vector3(8, 8, 1)));
        Assert.True(kill.Crosses(new Vector3(-5, 2, 1), new Vector3(-1, 2, 1)) is false);
        Assert.True(kill.Crosses(new Vector3(-5, 2, 1), new Vector3(15, 2, 1)));     // passes right through the wedge: both ends outside
        Assert.False(kill.Crosses(new Vector3(8, 15, 1), new Vector3(8, 6, 1)));      // ends in the box's cut-away corner
        var world = new CollisionWorld(Level(), Level(), [kill]);
        Assert.True(world.KillsPlayer(new Vector3(2, 2, 5), new Vector3(2, 2, 0.5f), Config.CapsuleHeight));
        Assert.False(world.KillsPlayer(new Vector3(8, 8, 5), new Vector3(8, 8, 0.5f), Config.CapsuleHeight));
        // the shape is in the collision hash; a box-only list hashes as before
        Assert.NotEqual(new CollisionWorld(Level(), Level(), [kill with { Shape = null }]).ContentHash, world.ContentHash);
    }

    [Fact]
    public void An_edited_spawn_door_stops_the_other_side_at_its_edited_boundary_only()
    {
        var level = Level();
        var door = new SpawnBlocker(Team.SpaceMarines, new Vector3(5, 5, 1.5f), new Vector3(10, 10, 3), 0f) { Volume = Wedge() };
        var world = new CollisionWorld(level, level, null, [door]);
        var raised = Assert.Single(world.Blockers);
        Assert.True(raised.Volume!.Max.Z > level.Max.Z + SpawnBlocker.AboveLevel - 0.01f);   // stretched above the level like the box
        Assert.Equal(door.Volume!.Min.Z, raised.Volume.Min.Z);
        Assert.True(raised.Contains(new Vector3(2, 2, 20)) && !raised.Contains(new Vector3(8, 8, 20)));   // the footprint stays the wedge all the way up
        // Chaos walks into the wedge's slanted face, not the box's +Y face: a ray down -Y at x = 8 from y = 11 meets the slope at y = 2 (9 m), the box would stop it at y = 10 (1 m)
        var hit = world.CharacterFor(Team.Chaos).Raycast(new Vector3(8, 11, 1), -Vector3.UnitY, 30f);
        Assert.NotNull(hit);
        Assert.Equal(9f, hit!.Value.Distance, 3);
        Assert.Null(world.CharacterFor(Team.SpaceMarines).Raycast(new Vector3(8, 11, 1), -Vector3.UnitY, 30f));   // its own side passes
        Assert.Null(world.Weapons.Raycast(new Vector3(8, 11, 1), -Vector3.UnitY, 30f));   // shots pass
        // pushed out only from inside the shape
        Assert.Equal(new Vector3(8, 8, 0), raised.PushOut(new Vector3(8, 8, 0), 0.5f, 1.9f));
        Assert.NotEqual(new Vector3(2, 2, 0), raised.PushOut(new Vector3(2, 2, 0), 0.5f, 1.9f));
        Assert.NotEqual(new CollisionWorld(level, level, null, [door with { Volume = null }]).ContentHash, world.ContentHash);
    }

    [Fact]
    public void An_edited_start_area_spawns_only_inside_its_shape()
    {
        // the wedge moved to x -15..-5, y -15..-5 (on the flat floor, away from the step)
        var shape = Wedge(-15, -15);
        var area = new StartArea(Team.SpaceMarines, new Vector3(-10, -10, 1.5f), new Vector3(10, 10, 3), 0f) { Volume = shape };
        var level = new CollisionWorld(Level(), Level());
        var edited = new World(Config, level, [new SpawnPoint(Vector3.Zero, 0f)]) { StartAreas = [area] }.StartAreaSpots(0);
        var stock = new World(Config, level, [new SpawnPoint(Vector3.Zero, 0f)]) { StartAreas = [area with { Volume = null }] }.StartAreaSpots(0);
        Assert.NotEmpty(edited);
        Assert.True(edited.Count < stock.Count, $"{edited.Count} vs {stock.Count}");
        Assert.All(edited, s => Assert.True(area.HoldsSpot(s), $"{s}"));
        Assert.All(edited, s => Assert.Contains(s, stock));                      // the same grid, filtered
        Assert.Contains(stock, s => shape.Contains(s + new Vector3(0, 0, 0.1f)) is false);   // the box alone spawns where the shape does not reach
        var flat = new World(Config) { StartAreas = [area] }.StartAreaSpots(0);
        Assert.All(flat, s => Assert.True(shape.Contains(s)));
    }
}
