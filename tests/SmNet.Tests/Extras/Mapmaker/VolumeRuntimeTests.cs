using System.Numerics;
using SmSim;

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
}
