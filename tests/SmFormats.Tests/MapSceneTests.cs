using System.Numerics;
using SmContent;
using SmFormats.Bod;
using SmFormats.Dcm;
using SmFormats.Obpk;
using static SmFormats.Bod.SyntheticBodWriter;

namespace SmFormats.Tests;

/// <summary>Map visuals: layer placements, object definitions, LOD choice, and the rotation convention.</summary>
public class MapSceneTests
{
    [Theory]
    // Rotation (degrees) and the quaternion the level collision stores for the same placement (mp_dlc2_map4).
    [InlineData(180f, -90f, -179.999f, 0f, -0.70711f, 0f, 0.70711f)]
    [InlineData(-90f, 0f, 90f, 0.5f, 0.5f, -0.5f, -0.5f)]
    public void Rotations_are_degrees_about_x_then_y_then_z(float rx, float ry, float rz, float qx, float qy, float qz, float qw)
    {
        var q = MapScene.Rotation(rx, ry, rz);
        Assert.True(MathF.Abs(Quaternion.Dot(q, new Quaternion(qx, qy, qz, qw))) > 0.9999f, q.ToString());
    }

    [Fact]
    public void Rotation_applies_x_first()
    {
        // +X: 90° about X leaves it, then 90° about Z takes it to +Y. Z first would give +Y, then X would take that to +Z.
        var v = Vector3.Transform(Vector3.UnitX, MapScene.Rotation(90, 0, 90));
        Assert.True(Vector3.Distance(Vector3.UnitY, v) < 1e-5f, v.ToString());
    }

    static VisualPart Part(string node, int lod) => new("mesh", 0, node, Matrix4x4.Identity, lod, true);

    [Theory]
    // Complementary masks: the high-detail node carries the low bits (bit 0 = nearest).
    [InlineData(new[] { "Pipe_LODH:15", "Pipe_LODL:16" }, new[] { "Pipe_LODH" })]
    [InlineData(new[] { "tank:7", "tank_lod:24" }, new[] { "tank" })]
    // Overlapping masks: the LODL node also has bit 0; the name rule keeps only the high-detail part.
    [InlineData(new[] { "Wall_LODH:31", "Wall_LODL:7" }, new[] { "Wall_LODH" })]
    // Detail parts with smaller masks still draw up close; -1 means every LOD.
    [InlineData(new[] { "body:31", "wheel:15", "gravel:7", "far_only:16" }, new[] { "body", "wheel", "gravel" })]
    [InlineData(new[] { "arch:-1", "trim:31" }, new[] { "arch", "trim" })]
    // Only a low-LOD-named part with bit 0: it is still drawn rather than nothing.
    [InlineData(new[] { "Rock_LODL:31" }, new[] { "Rock_LODL" })]
    public void Nearest_lod_takes_bit_zero_parts_preferring_high_detail_names(string[] parts, string[] expected)
    {
        var definition = new ObjectDefinition("d", parts.Select(p => p.Split(':')).Select(p => Part(p[0], int.Parse(p[1]))).ToList(), []);
        Assert.Equal(expected, definition.NearestLod().Select(v => v.RefNode));
    }

    static Node Placed(string name, string definition, float[] position, float[] rotation, float[] scale, bool disabled = false, params Node[] children) =>
        new Struct("StaticObject",
            ("Name", new S(name)), ("ObjectName", new S(definition)), ("Disabled", new B(disabled)),
            ("Position", new Vec(position)), ("Rotation", new Vec(rotation)), ("Scale", new Vec(scale)),
            ("ChildObjects", new List(children)));

    static byte[] Layer(params Node[] objects) => Document(new Struct("RegionLayer",
        ("ID", new I(1)),
        ("Root", new Ref(new Struct("WorldGroup", ("Name", new S("")), ("Disabled", new B(false)), ("Objects", new List(objects)), ("ChildObjects", new List()))))));

    /// <summary>An object with a near and a far LOD, each hanging on its own node offset 1 m up.</summary>
    static byte[] Definition(string mesh) => Document(new Struct("Object3D",
        ("Skeleton", new Ref(new Struct("Skeleton3D", ("Name", new S("root")), ("Position", new Vec(0, 0, 0)), ("Rotation", new Vec(0, 0, 0)), ("Scale", new Vec(1, 1, 1)),
            ("Children", new List(
                new Struct("Node3D", ("Name", new S("lodh")), ("Position", new Vec(0, 0, 1)), ("Rotation", new Vec(0, 0, 0)), ("Scale", new Vec(1, 1, 1)), ("Children", new List())),
                new Struct("Node3D", ("Name", new S("lodl")), ("Position", new Vec(0, 0, 1)), ("Rotation", new Vec(0, 0, 0)), ("Scale", new Vec(1, 1, 1)), ("Children", new List()))))))),
        ("Visuals", new List(
            new Struct("StaticMeshVisual", ("MeshName", new S(mesh)), ("MeshID", new I(0)), ("RefNode", new S("lodh")), ("LODLevel", new I(31)), ("Renderable", new B(true))),
            new Struct("StaticMeshVisual", ("MeshName", new S(mesh)), ("MeshID", new I(1)), ("RefNode", new S("lodl")), ("LODLevel", new I(15)), ("Renderable", new B(true))))),
        ("Bodies", new List(new Struct("RigidBody", ("NodeName", new S("lodh")))))));

    static byte[] Mesh() => SyntheticDcmWriter.Build(
    [
        new([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2], [new("stone", 0, 3, 0, 3)]),
        new([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2], [new("stone", 0, 3, 0, 3)]),
    ]);

    [Fact]
    public void Placements_definitions_meshes_materials_and_textures_load_from_the_region_archive()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-scene-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data", "chunks"));
        try
        {
            byte[] dds = [0x44, 0x44, 0x53, 0x20, 1, 2, 3];
            File.WriteAllBytes(Path.Combine(dir, "data", "chunks", "mp_test__wrld.oppc"), SyntheticObpkWriter.Build(
            [
                new("Layer", "e_walls", Layer(
                    Placed("wall_01", "wall_a", [10, 0, 0], [0, 0, 90], [2, 2, 2]),
                    Placed("hidden", "wall_a", [0, 0, 0], [0, 0, 0], [1, 1, 1], disabled: true),
                    Placed("group", "wall_a", [0, 5, 0], [0, 0, 90], [2, 2, 2], false,
                        Placed("child", "wall_a", [1, 0, 0], [0, 0, 0], [1, 1, 1])),
                    Placed("hidden_parent", "wall_a", [0, 0, 0], [0, 0, 0], [1, 1, 1], true,
                        Placed("hidden_child", "wall_a", [1, 0, 0], [0, 0, 0], [1, 1, 1])),
                    new Struct("StaticObject", ("Name", new S("no_rotation")), ("ObjectName", new S("wall_a")), ("Position", new Vec(7, 8, 9)))), "\\mp\\mp_test\\wrld\\"),
                new("O3d", "Wall_A", Definition("Wall_A_Mesh")),
                new("O3d", "unused", Definition("unused_mesh")),
                new("dcm", "Wall_A_Mesh", Mesh()),
                new("bmat", "stone", SyntheticBodWriter.Material("stone", "diff", [("DiffuseMap", "stone_dif")])),
                new("dds", "stone_dif", dds),
            ]));

            var scene = MapScene.Load(dir, "mp_test");
            // Disabled objects and everything under them are skipped.
            Assert.Equal(["wall_01", "group", "child", "no_rotation"], scene.Objects.Select(o => o.Name));
            var wall = scene.Objects[0];
            Assert.Equal("e_walls", wall.Layer);
            // Local (1, 0, 0) on the near LOD's node (1 m up): scale 2, turn 90° about Z, move to (10, 0, 0) → (10, 2, 2).
            var def = scene.Definitions[wall.Definition];
            var near = Assert.Single(def.NearestLod());
            Assert.Equal((0, "lodh"), (near.MeshId, near.RefNode));
            var p = Vector3.Transform(new Vector3(1, 0, 0), near.NodeTransform * wall.Transform);
            Assert.True(Vector3.Distance(new Vector3(10, 2, 2), p) < 1e-5f, p.ToString());
            // A child object is placed relative to its parent's full transform (an assumption: no multiplayer map nests
            // objects): (1, 0, 0) scaled by 2, turned 90° about Z → (0, 2, 0), moved by (0, 5, 0) → (0, 7, 0).
            var child = scene.Objects[2];
            Assert.Equal(1, child.Depth);
            Assert.True(Vector3.Distance(new Vector3(0, 7, 0), child.Transform.Translation) < 1e-5f, child.Transform.Translation.ToString());
            // A missing Rotation (or Scale) defaults on its own; the Position still applies.
            Assert.Equal(new Vector3(7, 8, 9), scene.Objects[3].Transform.Translation);
            Assert.Equal(("lodh", Matrix4x4.CreateTranslation(0, 0, 1)), (def.CollisionNodes.Single().Node, def.CollisionNodes.Single().Transform));
            // Only meshes of used definitions are read; materials and the diffuse texture resolve by name, ignoring case.
            Assert.Equal(["Wall_A_Mesh"], scene.Meshes.Keys);
            Assert.Equal(2, scene.Meshes["wall_a_mesh"].Blocks.Count);
            Assert.Equal(dds, scene.Textures["STONE_DIF"]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Missing_definitions_and_meshes_are_noted_not_fatal()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-scene-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data", "chunks"));
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "data", "chunks", "mp_test__r000_main.oppc"), SyntheticObpkWriter.Build(
            [
                new("Layer", "e_walls", Layer(Placed("a", "nowhere", [0, 0, 0], [0, 0, 0], [1, 1, 1]), Placed("b", "meshless", [0, 0, 0], [0, 0, 0], [1, 1, 1]))),
                new("O3d", "meshless", Definition("no_such_mesh")),
                new("Layer", "d_broken", [0x42, 0x4F, 0x44, 0xFD, 2, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0xEE]),
            ]));
            var scene = MapScene.Load(dir, "mp_test");
            Assert.Equal(2, scene.Objects.Count);
            Assert.Contains(scene.Notes, n => n.Contains("'nowhere'"));
            Assert.Contains(scene.Notes, n => n.Contains("'no_such_mesh'"));
            Assert.Contains(scene.Notes, n => n.Contains("d_broken"));
            Assert.Empty(scene.Meshes);
            Assert.Throws<ArgumentException>(() => MapScene.Load(dir, "../x"));
            Assert.Throws<FileNotFoundException>(() => MapScene.Load(dir, "mp_other"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
