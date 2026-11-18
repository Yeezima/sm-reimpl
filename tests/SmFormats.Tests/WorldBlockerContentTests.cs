using System.Numerics;
using SmContent;
using SmFormats.Obpk;
using SmSim;
using static SmFormats.Bod.SyntheticBodWriter;

namespace SmFormats.Tests;

/// <summary>
/// The map's invisible movement walls (classic-maps-fidelity gameplay, 2026-11-18) from a synthetic install: the
/// <c>d_blockers</c> layer's and the mission layers' group-2 <c>MissionMovementBlocker</c>s are read (boxes and cylinders, any
/// rotation), spawn-room blockers and odd ones are not, and they reach the collision world solid for every side and the map hash.
/// </summary>
public class WorldBlockerContentTests
{
    static Node Blocker(string name, float[] position, float[] scale, int group = 2, int race = 1, int shape = 1, float[]? rotation = null, bool disabled = false) =>
        new Ref(new Struct("base/MissionMovementBlocker", ("BlockType", new I(0)), ("BlockGroup", new I(group)), ("BlockRace", new I(race)),
            ("Position", new Vec(position)), ("Rotation", new Vec(rotation ?? [0f, 0f, 0f])), ("Scale", new Vec(scale)), ("Shape", new I(shape)),
            ("Name", new S(name)), ("Disabled", new B(disabled))));

    static string Install(Node[] blockers, Node[]? mission = null)
    {
        var layers = new List<(string, byte[])> { ("d_blockers", SeizeGroundContentTests.Layer(blockers)) };
        if (mission is not null) layers.Add(("d_mission", SeizeGroundContentTests.Layer(mission)));
        return SeizeGroundContentTests.Install("mp_test", null, [.. layers]);
    }

    [Fact]
    public void Group_2_boxes_and_cylinders_of_the_blockers_and_mission_layers_are_walls_and_the_rest_is_noted_or_left_to_the_rooms()
    {
        string dir = Install(
            [
                Blocker("MissionMovementBlocker_02", [55f, 137f, 17.6f], [50f, 8f, 80f]),
                Blocker("MissionMovementBlocker_06", [0f, 0f, 21f], [10f, 10f, 31.4f], race: 2, shape: 3),
                Blocker("MissionMovementBlocker_sphere", [0f, 0f, 15f], [9.3f, 9.3f, 9.3f], shape: 2),
                Blocker("MissionMovementBlocker_shape5", [0f, 0f, 15f], [1f, 1f, 1f], shape: 5),
                Blocker("MissionMovementBlocker_room", [0f, 0f, 0f], [1f, 1f, 1f], group: 3, race: 4),
                Blocker("MissionMovementBlocker_race", [0f, 0f, 0f], [1f, 1f, 1f], race: 3),
                Blocker("MissionMovementBlocker_flat", [0f, 0f, 0f], [1f, 0f, 1f]),
                Blocker("MissionMovementBlocker_off", [0f, 0f, 0f], [1f, 1f, 1f], disabled: true),
                Blocker("marine_blocker_07", [9f, 9f, 9f], [1f, 1f, 1f]),   // a room's: group 2 by mistake still belongs to the rooms
            ],
            [
                Blocker("everyone_blocker_01", [-38.8f, -102.9f, -4.9f], [2f, 49.6f, 15f], race: 2, rotation: [0f, -44f, 0f]),
                Blocker("chaos_blocker_01", [1f, 1f, 1f], [1f, 1f, 1f], group: 3, race: 3),
            ]);
        try
        {
            var map = MapContent.Load(dir, "mp_test");
            Assert.Equal(["MissionMovementBlocker_02", "MissionMovementBlocker_06", "MissionMovementBlocker_sphere", "everyone_blocker_01"], map.Walls.Select(w => w.Name));
            Assert.Equal([1, 3, 2, 1], map.Walls.Select(w => w.Shape));
            Assert.Equal([1, 2, 1, 2], map.Walls.Select(w => w.Race));
            Assert.Equal(new Vector3(0f, -44f, 0f), map.Walls[3].RotationDegrees);
            Assert.Contains(map.Notes, n => n.Contains("MissionMovementBlocker_shape5: shape 5", StringComparison.Ordinal));
            Assert.Contains(map.Notes, n => n.Contains("MissionMovementBlocker_room: group 3", StringComparison.Ordinal));
            Assert.Contains(map.Notes, n => n.Contains("MissionMovementBlocker_race: race 3", StringComparison.Ordinal));
            Assert.Contains(map.Notes, n => n.Contains("MissionMovementBlocker_flat: bad scale", StringComparison.Ordinal));
            Assert.DoesNotContain(map.Notes, n => n.Contains("_off", StringComparison.Ordinal) || n.Contains("marine_blocker_07", StringComparison.Ordinal));
            // The rooms' blockers are read as before (only by name, not by group); the layer's group is not theirs to decide.
            Assert.Equal(["chaos_blocker_01", "marine_blocker_07"], map.Blockers.Select(b => b.Name));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_wall_turned_about_Z_is_the_same_box_as_a_spawn_blocker_of_that_yaw()
    {
        string dir = Install([Blocker("MissionMovementBlocker", [10f, -4f, 3f], [1f, 16f, 25f], rotation: [0f, 0f, 90f])]);
        try
        {
            var wall = MapContent.Load(dir, "mp_test").Walls[0].ToSim();
            var box = new SpawnBlocker(Team.None, new Vector3(10f, -4f, 3f), new Vector3(1f, 16f, 25f), MathF.PI / 2);
            Assert.Equal(8, wall.Vertices.Length);
            Assert.Equal(12, wall.Indices.Length / 3);
            for (int k = 0; k < 8; k++) Assert.True(Vector3.Distance(wall.Vertices[k], box.Corners()[k]) < 1e-3f, $"corner {k}: {wall.Vertices[k]} against {box.Corners()[k]}");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Walls_go_into_the_collision_world_solid_for_every_side_and_not_for_shots_and_the_map_hash_covers_them()
    {
        string a = Install([Blocker("MissionMovementBlocker", [5f, 0f, 4f], [1f, 10f, 10f])]);
        string b = Install([Blocker("MissionMovementBlocker", [5f, 0f, 4f], [1f, 10f, 10f])]);
        string c = Install([Blocker("MissionMovementBlocker", [5.01f, 0f, 4f], [1f, 10f, 10f])]);
        string none = Install([]);
        try
        {
            MapContent A = MapContent.Load(a, "mp_test"), B = MapContent.Load(b, "mp_test"), C = MapContent.Load(c, "mp_test"), N = MapContent.Load(none, "mp_test");
            Assert.Equal(A.Hash, B.Hash);
            Assert.NotEqual(A.Hash, C.Hash);
            Assert.NotEqual(A.Hash, N.Hash);
            var world = A.ToCollisionWorld();
            Assert.Single(world.Walls);
            var across = new Vector3(0f, 0f, 1f);
            foreach (var team in new[] { Team.SpaceMarines, Team.Chaos, Team.None })
                Assert.NotNull(world.CharacterFor(team).Raycast(across, Vector3.UnitX, 20f));
            Assert.Null(world.Character.Raycast(across, Vector3.UnitX, 20f));   // the level alone has no wall there
            Assert.Null(world.Weapons.Raycast(across, Vector3.UnitX, 20f));     // shots pass through
            var bare = N.ToCollisionWorld();
            Assert.NotEqual(world.ContentHash, bare.ContentHash);
            Assert.Empty(bare.Walls);
            Assert.Same(bare.Character, bare.CharacterFor(Team.None));   // no walls, no blockers: the level itself, as before
        }
        finally { foreach (var x in new[] { a, b, c, none }) Directory.Delete(x, recursive: true); }
    }

    [Fact]
    public void A_non_cubic_wall_turned_about_Y_is_tilted_as_the_map_scene_turns_objects()
    {
        // Shattered Bridge's everyone_blocker_01: 2 x 49.6 x 15 m turned -44 degrees about Y. MapScene.Transform (degrees about X, then Y, then Z, right-handed,
        // checked against the level collision's quaternions in MapSceneTests) is what places it: its top leans toward -X (the outside of the bridge) and the
        // 2 m thick plane is tipped 44 degrees from vertical, so its face is 46 degrees from the horizontal.
        string dir = Install([Blocker("MissionMovementBlocker", [-38.8f, -102.9f, -4.9f], [2f, 49.6f, 15f], race: 2, rotation: [0f, -44f, 0f])]);
        try
        {
            var wall = MapContent.Load(dir, "mp_test").Walls[0].ToSim();
            var q = MapScene.Rotation(0f, -44f, 0f);
            foreach (var v in wall.Vertices)
            {
                var local = Vector3.Transform(v - new Vector3(-38.8f, -102.9f, -4.9f), Quaternion.Inverse(q));
                Assert.True(MathF.Abs(MathF.Abs(local.X) - 1f) < 1e-3f && MathF.Abs(MathF.Abs(local.Y) - 24.8f) < 1e-3f && MathF.Abs(MathF.Abs(local.Z) - 7.5f) < 1e-3f, $"{v} -> {local}");
            }
            // Top centre (local +Z) leans to -X, 7.5 m up: x offset 7.5 sin(-44), z offset 7.5 cos(44).
            var top = wall.Vertices.Where(v => v.Z > -4.9f + 1f).Aggregate(Vector3.Zero, (a, v) => a + v) / 4f;
            Assert.True(MathF.Abs(top.X - (-38.8f - 7.5f * MathF.Sin(44f * MathF.PI / 180f))) < 0.05f, top.ToString());
            Assert.True(MathF.Abs(top.Z - (-4.9f + 7.5f * MathF.Cos(44f * MathF.PI / 180f))) < 0.05f, top.ToString());
            // A 90 degree turn about Y swaps the X and Z extents of a 2 x 10 x 6 box.
            var box = new MapWall("b", Vector3.Zero, new Vector3(0f, 90f, 0f), new Vector3(2f, 10f, 6f), 1, 2, 1).ToSim();
            Assert.True(Vector3.Distance(box.Max - box.Min, new Vector3(6f, 10f, 2f)) < 1e-3f, (box.Max - box.Min).ToString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_sphere_wall_is_a_polyhedron_that_contains_the_true_sphere_and_is_not_much_bigger()
    {
        var w = WorldBlocker.Sphere("s", Matrix4x4.CreateScale(9.3f) * Matrix4x4.CreateTranslation(205.7f, -864.7f, 15.3f));
        Assert.Equal(80, w.Indices.Length / 3);
        var c = new Vector3(205.7f, -864.7f, 15.3f);
        foreach (var v in w.Vertices) Assert.InRange(Vector3.Distance(v, c), 4.65f, 4.65f * 1.08f);
        var mesh = new StaticMesh(w.Vertices, w.Indices);
        for (int k = 0; k < 40; k++)
        {
            var d = Vector3.Normalize(new Vector3(MathF.Sin(k * 1.7f), MathF.Cos(k * 2.3f), MathF.Sin(k * 0.9f) + 0.1f));
            var hit = mesh.Raycast(c + d * 12f, -d, 20f);
            Assert.NotNull(hit);
            Assert.InRange(hit.Value.Distance, 12f - 4.65f * 1.08f, 12f - 4.65f + 1e-3f);
        }
    }
}
