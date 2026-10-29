using System.Numerics;
using SmContent;
using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim;
using static SmFormats.Bod.SyntheticBodWriter;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// Map edges (M18) from a synthetic install: characters collide with layers "2" and "8", shots with "8"; kill regions
/// from the mission layers (kept, skipped with notes), carried into the collision world, and covered by the map hash.
/// </summary>
public class MapEdgesContentTests
{
    static readonly CollisionMesh Floor = new([new(-10, -10, 0), new(10, -10, 0), new(10, 10, 0), new(-10, 10, 0)], [(0, 1, 2), (0, 2, 3)]);
    static readonly CollisionMesh Panel = new([new(-10, 10, 0), new(10, 10, 0), new(10, 10, 3), new(-10, 10, 3)], [(0, 1, 2), (0, 2, 3)]);
    static readonly CollisionPlacement At0 = new(0, Vector3.Zero, Quaternion.Identity, Vector3.One);

    static byte[] Collision() => BuildWorldCollision([new CollisionVariant("2", [Floor], [At0]),
        new CollisionVariant("8", [Floor], [At0])]);

    /// <summary>Layer "8" with the panel: two storages instanced once each.</summary>
    static byte[] CollisionWithPanel() => BuildWorldCollision([new CollisionVariant("2", [Floor], [At0]),
        new CollisionVariant("8", [Floor, Panel], [At0, new(1, Vector3.Zero, Quaternion.Identity, Vector3.One)])]);

    static Node Kill(string name, float[] position, float[] scale, int shape = 0, float[]? rotation = null, bool enable = true, bool exclude = false, bool disabled = false) =>
        new Ref(new Struct("base/KillRegion", ("OneShot", new B(false)), ("Enable", new B(enable)), ("Exclude", new B(exclude)), ("BodyType", new I(11)),
            ("Position", new Vec(position)), ("Rotation", new Vec(rotation ?? [0f, 0f, 0f])), ("Scale", new Vec(scale)), ("Shape", new I(shape)),
            ("Name", new S(name)), ("Disabled", new B(disabled))));

    static byte[] Layer(params Node[] objects) =>
        Document(new Struct("RegionLayer", ("ID", new I(26)), ("Root", new Ref(new Struct("WorldGroup", ("Name", new S("")), ("Objects", new List(objects)))))));

    static string Install(byte[] collision, byte[]? mission)
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-edges-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data", "chunks"));
        var files = new List<SyntheticObpkWriter.File> { new("hkt", "wrld", collision) };
        if (mission is not null) files.Add(new("Layer", "d_mission_objects", mission, "\\mp\\mp_test\\wrld\\"));
        File.WriteAllBytes(Path.Combine(dir, "data", "chunks", "mp_test__wrld.oppc"), SyntheticObpkWriter.Build(files));
        return dir;
    }

    [Fact]
    public void Characters_collide_with_layers_2_and_8_and_shots_with_8()
    {
        string dir = Install(CollisionWithPanel(), null);
        try
        {
            var world = MapContent.Load(dir, "mp_test").ToCollisionWorld();
            Assert.Equal(["2", "8"], MapContent.CharacterLayers);
            Assert.Equal((6, 4), (world.Character.TriangleCount, world.Weapons.TriangleCount));
            Assert.NotNull(world.Character.Raycast(new Vector3(0, 5, 1.5f), Vector3.UnitY, 10f));   // the panel stops characters
            Assert.Empty(world.KillVolumes);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Kill_regions_are_read_as_boxes_and_odd_ones_are_noted_and_skipped()
    {
        string dir = Install(Collision(), Layer(
            Kill("KillRegion", [200f, -800f, -10f], [250f, 250f, 1f]),
            Kill("turned", [0f, 0f, -5f], [10f, 2f, 1f], rotation: [0f, 0f, 90f]),
            Kill("excluded", [0f, 0f, 0f], [1f, 1f, 1f], exclude: true),
            Kill("off", [0f, 0f, 0f], [1f, 1f, 1f], enable: false),
            Kill("sphere", [0f, 0f, 0f], [1f, 1f, 1f], shape: 2),
            Kill("tilted", [0f, 0f, 0f], [1f, 1f, 1f], rotation: [10f, 0f, 0f]),
            Kill("flat", [0f, 0f, 0f], [1f, 0f, 1f]),
            Kill("disabled", [0f, 0f, 0f], [1f, 1f, 1f], disabled: true)));
        try
        {
            var map = MapContent.Load(dir, "mp_test");
            Assert.Equal(["KillRegion", "turned"], map.KillRegions.Select(k => k.Name));
            var world = map.ToCollisionWorld();
            Assert.Equal(new KillVolume(new Vector3(200f, -800f, -10f), new Vector3(125f, 125f, 0.5f), 0f), world.KillVolumes[0]);
            Assert.Equal(MathF.PI / 2, world.KillVolumes[1].Yaw, 5);
            Assert.True(world.KillsPlayer(new Vector3(100f, -700f, -9f), new Vector3(100f, -700f, -12f), 2f));
            foreach (var name in new[] { "excluded", "off", "sphere", "tilted", "flat" })
                Assert.Contains(map.Notes, n => n.Contains($"kill region {name}:", StringComparison.Ordinal));
            Assert.DoesNotContain(map.Notes, n => n.Contains("disabled", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static Node Blocker(string name, float[] position, float[] scale, float[]? rotation = null, bool disabled = false) =>
        new Ref(new Struct("base/MissionMovementBlocker", ("BlockType", new I(0)), ("BlockGroup", new I(3)), ("BlockRace", new I(4)),
            ("Position", new Vec(position)), ("Rotation", new Vec(rotation ?? [0f, 0f, 0f])), ("Scale", new Vec(scale)), ("Shape", new I(1)),
            ("Name", new S(name)), ("Disabled", new B(disabled))));

    [Fact]
    public void Spawn_room_blockers_are_read_per_side_and_odd_ones_are_noted_and_skipped()
    {
        string dir = Install(Collision(), Layer(
            Blocker("marine_blocker_02", [230.426f, -920.404f, 7.173f], [1f, 11f, 15f]),
            Blocker("Chaos_Blocker_03", [208.356f, -713.364f, 4.939f], [1f, 16f, 25f], [0f, 0f, 90f]),
            Blocker("MissionMovementBlocker", [205f, -864f, 15f], [9.3f, 9.3f, 9.3f]),   // not a spawn-room blocker
            Blocker("marine_blocker_09", [0f, 0f, 0f], [1f, 1f, 1f], [5f, 0f, 0f]),
            Blocker("chaos_blocker_09", [0f, 0f, 0f], [1f, 0f, 1f]),
            Blocker("marine_blocker_off", [0f, 0f, 0f], [1f, 1f, 1f], disabled: true)));
        try
        {
            var map = MapContent.Load(dir, "mp_test");
            Assert.Equal(["Chaos_Blocker_03", "marine_blocker_02"], map.Blockers.Select(b => b.Name));
            var sim = map.SimBlockers();
            Assert.Equal((Team.Chaos, MathF.PI / 2), (sim[0].Side, sim[0].Yaw));
            Assert.Equal(new SpawnBlocker(Team.SpaceMarines, new Vector3(230.426f, -920.404f, 7.173f), new Vector3(1f, 11f, 15f), 0f), sim[1]);
            Assert.True(sim[0].Contains(new Vector3(214f, -713f, 1f)) && !sim[0].Contains(new Vector3(208f, -720f, 1f)));   // 16 m along X once turned
            Assert.Contains(map.Notes, n => n.Contains("blocker marine_blocker_09: rotated", StringComparison.Ordinal));
            Assert.Contains(map.Notes, n => n.Contains("blocker chaos_blocker_09: bad scale", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Spawn_room_blockers_go_into_the_collision_world_solid_for_the_other_side_and_the_map_hash_covers_them()
    {
        Node[] Blockers(float x) =>
        [
            Blocker("marine_blocker_01", [x, 0f, 4f], [1f, 10f, 10f]),
            Blocker("chaos_blocker_01", [-5f, 0f, 4f], [1f, 10f, 10f]),
        ];
        string a = Install(Collision(), Layer(Blockers(5f)));
        string b = Install(Collision(), Layer(Blockers(5f)));
        string c = Install(Collision(), Layer(Blockers(5.01f)));
        try
        {
            MapContent A = MapContent.Load(a, "mp_test"), B = MapContent.Load(b, "mp_test"), C = MapContent.Load(c, "mp_test");
            var world = A.ToCollisionWorld();
            // M31: the collision world raises each box to SpawnBlocker.AboveLevel above the level's top (footprint and bottom kept).
            float top = world.Character.Max.Z + SpawnBlocker.AboveLevel;
            Assert.Equal(A.SimBlockers().Select(x => x.ReachingAbove(top)), world.Blockers);
            Assert.All(world.Blockers, x => Assert.Equal(top, x.Center.Z + x.Size.Z / 2f, 4));
            var eye = new Vector3(0, 0, 1.5f);
            // The marine blocker (x 5) stops Chaos, the Chaos blocker (x −5) stops marines; neither stops shots or its own side.
            Assert.Equal(4.5f, world.CharacterFor(Team.Chaos).Raycast(eye, Vector3.UnitX, 9f)!.Value.Distance, 4);
            Assert.Null(world.CharacterFor(Team.Chaos).Raycast(eye, -Vector3.UnitX, 9f));
            Assert.Equal(4.5f, world.CharacterFor(Team.SpaceMarines).Raycast(eye, -Vector3.UnitX, 9f)!.Value.Distance, 4);
            Assert.Null(world.CharacterFor(Team.SpaceMarines).Raycast(eye, Vector3.UnitX, 9f));
            Assert.Null(world.Weapons.Raycast(eye, Vector3.UnitX, 9f));
            Assert.Null(world.Character.Raycast(eye, -Vector3.UnitX, 9f));
            // The map hash (the handshake) and the level's content hash both move with a blocker.
            Assert.Equal(A.Hash, B.Hash);
            Assert.NotEqual(A.Hash, C.Hash);
            Assert.Equal(world.ContentHash, B.ToCollisionWorld().ContentHash);
            Assert.NotEqual(world.ContentHash, C.ToCollisionWorld().ContentHash);
        }
        finally { foreach (var d in new[] { a, b, c }) Directory.Delete(d, recursive: true); }
    }

    [Fact]
    public void A_moved_kill_region_changes_the_map_hash_and_the_level_hash()
    {
        string a = Install(Collision(), Layer(Kill("KillRegion", [200f, -800f, -10f], [250f, 250f, 1f])));
        string b = Install(Collision(), Layer(Kill("KillRegion", [200f, -800f, -10f], [250f, 250f, 1f])));
        string c = Install(Collision(), Layer(Kill("KillRegion", [200f, -800f, -11f], [250f, 250f, 1f])));
        try
        {
            MapContent A = MapContent.Load(a, "mp_test"), B = MapContent.Load(b, "mp_test"), C = MapContent.Load(c, "mp_test");
            Assert.Equal(A.Hash, B.Hash);
            Assert.NotEqual(A.Hash, C.Hash);
            Assert.Equal(A.ToCollisionWorld().ContentHash, B.ToCollisionWorld().ContentHash);
            Assert.NotEqual(A.ToCollisionWorld().ContentHash, C.ToCollisionWorld().ContentHash);
        }
        finally { foreach (var d in new[] { a, b, c }) Directory.Delete(d, recursive: true); }
    }
}
