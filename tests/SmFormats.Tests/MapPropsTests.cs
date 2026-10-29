using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmFormats.Obpk;
using SmFormats.Ppk;
using SmSim;
using static SmFormats.Bod.SyntheticBodWriter;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// M39 readers: physics shape meshes (<c>ppk</c>), the movable props a region archive places (moving rigid bodies whose shape is
/// a <c>ppk</c> entry), carried into the collision world and the map hash, and the weapons' <c>penetration</c>. Synthetic files only.
/// </summary>
public class MapPropsTests
{
    /// <summary>A crate-like box: 1.2 × 3 × 1.5 m, centred on X and Y, standing on z = 0.</summary>
    static readonly Vector3[] CrateCorners = Enumerable.Range(0, 8)
        .Select(i => new Vector3((i & 1) == 0 ? -0.6f : 0.6f, (i & 2) == 0 ? -1.5f : 1.5f, (i & 4) == 0 ? 0f : 1.5f)).ToArray();
    static readonly int[] BoxIndices = [0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5];

    [Fact]
    public void A_physics_shape_mesh_round_trips_and_odd_ones_are_refused()
    {
        var bytes = SyntheticPpkWriter.Build(CrateCorners, BoxIndices);
        Assert.True(PpkMesh.HasMagic(bytes));
        var mesh = PpkMesh.Read(bytes);
        Assert.Equal(CrateCorners, mesh.Vertices);
        Assert.Equal(BoxIndices, mesh.Indices);
        Assert.Equal(12, mesh.TriangleCount);

        static byte[] Patch(byte[] b, int at, uint value) { var copy = (byte[])b.Clone(); BitConverter.GetBytes(value).CopyTo(copy, at); return copy; }
        Assert.Throws<InvalidDataException>(() => PpkMesh.Read(Patch(bytes, 0x00, 0x67)));          // not the magic
        Assert.Throws<InvalidDataException>(() => PpkMesh.Read(Patch(bytes, 0x04, 2)));             // a multi-part mesh
        Assert.Throws<InvalidDataException>(() => PpkMesh.Read(Patch(bytes, 0x18, 1_000_000)));     // too many triangles
        Assert.Throws<InvalidDataException>(() => PpkMesh.Read(Patch(bytes, 0x18, 1000)));          // triangles past the end
        Assert.Throws<InvalidDataException>(() => PpkMesh.Read(Patch(bytes, 0x10, 4)));             // an index past the vertices
        Assert.Throws<InvalidDataException>(() => PpkMesh.Read(Patch(bytes, 0x1C, 0x10)));          // triangles over the vertices
        Assert.Throws<InvalidDataException>(() => PpkMesh.Read(Patch(bytes, 0x50, 0x7FC00000)));    // a NaN vertex
        Assert.Throws<InvalidDataException>(() => PpkMesh.Read(bytes.AsSpan(0, 0x40)));
    }

    static Node Placed(string name, string definition, float[] position, float[] rotation, bool noBullets = false, params Node[] children) =>
        new Struct("StaticObject",
            ("Name", new S(name)), ("ObjectName", new S(definition)), ("Disabled", new B(false)),
            ("Position", new Vec(position)), ("Rotation", new Vec(rotation)), ("Scale", new Vec(1, 1, 1)),
            ("DisableCollision", new B(false)), ("DisableBulletCollision", new B(noBullets)), ("DisableCharacterCollision", new B(false)),
            ("ChildObjects", new List(children)));

    static byte[] Layer(params Node[] objects) => Document(new Struct("RegionLayer", ("ID", new I(1)),
        ("Root", new Ref(new Struct("WorldGroup", ("Name", new S("")), ("Objects", new List(objects)))))));

    /// <summary>An object definition with one rigid body on a node 0.5 m up; <paramref name="motion"/> 0 is a static body.</summary>
    static byte[] Definition(string shape, int motion, int material = 4) => Document(new Struct("Object3D",
        ("Skeleton", new Ref(new Struct("Skeleton3D", ("Name", new S("root")), ("Position", new Vec(0, 0, 0)), ("Rotation", new Vec(0, 0, 0)), ("Scale", new Vec(1, 1, 1)),
            ("Children", new List(new Struct("Node3D", ("Name", new S("crate_COL")), ("Position", new Vec(0, 0, 0.5f)), ("Rotation", new Vec(0, 0, 0)), ("Scale", new Vec(1, 1, 1)), ("Children", new List()))))))),
        ("Visuals", new List()),
        ("Bodies", new List(
            new Struct("RigidBody", ("MotionType", new I(motion)), ("Mass", new F(motion == 0 ? 0f : 70f)), ("NodeName", new S("crate_COL")),
                ("Shape", new Ref(new Struct("CShapeMesh", ("MeshName", new S(shape)), ("MeshID", new I(0)), ("MaterialID", new I(material))))), ("BodyType", new I(3))),
            // A second body on the same node and shape (the door button's BodyType 2 and 8): one prop.
            new Struct("RigidBody", ("MotionType", new I(motion)), ("Mass", new F(70f)), ("NodeName", new S("crate_COL")),
                ("Shape", new Ref(new Struct("CShapeMesh", ("MeshName", new S(shape)), ("MeshID", new I(0)), ("MaterialID", new I(material))))), ("BodyType", new I(8)))))));

    static string Install(byte[] crateShape, bool withCrates = true)
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-props-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data", "chunks"));
        var floor = new CollisionMesh([new(-10, -10, 0), new(10, -10, 0), new(10, 10, 0), new(-10, 10, 0)], [(0, 1, 2), (0, 2, 3)]);
        var at0 = new CollisionPlacement(0, Vector3.Zero, Quaternion.Identity, Vector3.One);
        var files = new List<SyntheticObpkWriter.File>
        {
            new("hkt", "wrld", BuildWorldCollision([new CollisionVariant("2", [floor], [at0]), new CollisionVariant("8", [floor], [at0])])),
            new("O3d", "Ammo_Crate_A", Definition("ammo_crate_A__d", 2)),
            new("O3d", "static_crate", Definition("static_crate__s", 0)),
            new("O3d", "ghost", Definition("ghost__d", 2)),
            new("ppk", "ammo_crate_A__d", crateShape),
            new("ppk", "static_crate__s", SyntheticPpkWriter.Build(CrateCorners, BoxIndices)),
        };
        if (withCrates)
            files.Add(new("Layer", "e_cover", Layer(
                Placed("crate_1", "ammo_crate_a", [5f, 0f, 0f], [0f, 0f, 90f]),
                Placed("crate_static", "static_crate", [0f, 5f, 0f], [0f, 0f, 0f]),
                Placed("crate_ghost", "ghost", [0f, -5f, 0f], [0f, 0f, 0f]),
                Placed("crate_no_bullets", "ammo_crate_a", [-5f, 0f, 0f], [0f, 0f, 0f], noBullets: true),
                Placed("stack", "static_crate", [0f, 0f, 0f], [0f, 0f, 0f], false, Placed("crate_on_top", "ammo_crate_a", [0f, 0f, 2f], [0f, 0f, 0f]))), "\\mp\\mp_test\\wrld\\"));
        File.WriteAllBytes(Path.Combine(dir, "data", "chunks", "mp_test__wrld.oppc"), SyntheticObpkWriter.Build(files));
        return dir;
    }

    [Fact]
    public void Moving_bodies_with_a_physics_shape_become_props_placed_as_their_objects()
    {
        string dir = Install(SyntheticPpkWriter.Build(CrateCorners, BoxIndices));
        try
        {
            var map = MapContent.Load(dir, "mp_test");
            Assert.Equal(["crate_1", "crate_on_top"], map.Props.Select(p => p.Name));   // the static body and the switched-off placement are not props
            var crate = map.Props[0];
            Assert.Equal(("ammo_crate_a", "e_cover", 4), (crate.Definition, crate.Layer, crate.Surface));
            // Local (0.6, 1.5, 1.5) on the body node (0.5 m up), turned 90° about Z, moved to (5, 0, 0) → (3.5, 0.6, 2).
            Assert.Contains(crate.Vertices, v => Vector3.Distance(v, new Vector3(3.5f, 0.6f, 2f)) < 1e-4f);
            Assert.Contains(map.Props[1].Vertices, v => Vector3.Distance(v, new Vector3(0.6f, 1.5f, 4f)) < 1e-4f);   // a child: relative to its parent
            Assert.Contains(map.Notes, n => n.Contains("crate_no_bullets") && n.Contains("DisableBulletCollision"));
            Assert.Contains(map.Notes, n => n.Contains("crate_ghost") && n.Contains("ghost__d"));

            var world = map.ToCollisionWorld();
            Assert.Equal((4 + 24, 2 + 24), (world.Character.TriangleCount, world.Weapons.TriangleCount));
            Assert.Equal(2, world.Props.Count);
            Assert.Equal(4, world.Weapons.MaterialOf(2));
            var hit = world.Weapons.Raycast(new Vector3(0f, 0f, 1f), Vector3.UnitX, 10f)!.Value;
            Assert.Equal((3.5f, 0), (MathF.Round(hit.Distance, 3), world.PropOfShotTriangle(hit.Triangle)));
            Assert.NotNull(world.CharacterFor(SmSim.Team.Chaos).Raycast(new Vector3(0f, 0f, 1f), Vector3.UnitX, 10f));

            // The map hash covers the props' sources and the loader's rules.
            ulong hash = map.Hash;
            string moved = Install(SyntheticPpkWriter.Build(CrateCorners.Select(v => v * 1.01f).ToArray(), BoxIndices));
            string none = Install(SyntheticPpkWriter.Build(CrateCorners, BoxIndices), withCrates: false);
            try
            {
                Assert.NotEqual(hash, MapContent.Load(moved, "mp_test").Hash);
                var bare = MapContent.Load(none, "mp_test");
                Assert.Empty(bare.Props);
                Assert.NotEqual(hash, bare.Hash);
                Assert.Equal(hash, MapContent.Load(dir, "mp_test").Hash);
            }
            finally { Directory.Delete(moved, true); Directory.Delete(none, true); }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void An_unreadable_shape_is_noted_and_its_props_are_left_out()
    {
        var bytes = SyntheticPpkWriter.Build(CrateCorners, BoxIndices);
        BitConverter.GetBytes(3u).CopyTo(bytes, 4);   // three parts: not read
        string dir = Install(bytes);
        try
        {
            var map = MapContent.Load(dir, "mp_test");
            Assert.Empty(map.Props);
            Assert.Contains(map.Notes, n => n.Contains("ammo_crate_A__d") && n.Contains("3 parts"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---------------------------------------------------------------- penetration

    static WeaponSpec? Gun(PierceFixture? pierce, List<string> notes) =>
        WeaponTuning.Read("gun", BafDocument.Read(SyntheticBafWriter.RangedWeapon(100f, 1f, 1, 10, 2f, 500f, [], 1f, 1f, 1f, 1f, pierce: pierce)), notes);

    [Fact]
    public void The_default_penetration_entry_is_read_with_its_exit_cone()
    {
        var notes = new List<string>();
        var las = Gun(new PierceFixture(10f, 1f), notes)!.Pierce!;   // the lascannon's numbers
        Assert.Equal((10f, 1f, true), (las.Points, las.PropCost, las.ExitHitsAllies));
        Assert.Equal((15f, 1f, 0f, 0f), (las.Exit!.Length, las.Exit.Angle, las.Exit.StartLength, las.Exit.Offset));
        Assert.Equal((4f, 3f), (las.BodyCost, las.HeadCost));   // players: default_body, default_head
        // The perk's entry (the fixture: 2 points, props 1, bodies 4, heads 3) is read beside the default one.
        var perk = Assert.Single(Gun(new PierceFixture(10f, 1f), notes)!.PerkPierces);
        Assert.Equal(("some_upgrade", 2f, 1f, 4f, 3f, 16f, true), (perk.Upgrade, perk.Spec.Points, perk.Spec.PropCost, perk.Spec.BodyCost, perk.Spec.HeadCost, perk.Spec.Exit!.Damage.Damage, perk.Spec.ExitHitsAllies));
        Assert.Equal(new DamageSpec(50f, 0f, 1f, 1f, 1f, 1f, false), las.Exit.Damage);
        var stalker = Gun(new PierceFixture(10f, 5f, 20f, 10f, 3f, "target_enemies_and_neutrals"), notes)!.Pierce!;
        Assert.Equal((5f, false, 10f, 3f, 20f), (stalker.PropCost, stalker.ExitHitsAllies, stalker.Exit!.Length, stalker.Exit.Angle, stalker.Exit.Damage.Damage));
        Assert.DoesNotContain(notes, n => n.Contains("penetration"));

        // No pierce: only the perk's entry (the bolter's Kraken Bolts: read as a perk entry only), 0 points (the heavy bolter), no table.
        var bolter = Gun(new PierceFixture(10f, 1f, WithDefault: false), notes)!;
        Assert.Equal((null, 1), (bolter.Pierce, bolter.PerkPierces.Count));
        Assert.Null(Gun(new PierceFixture(0f, 0f), notes)!.Pierce);
        Assert.Null(Gun(null, notes)!.Pierce);
        Assert.Empty(Gun(null, notes)!.PerkPierces);
        // A prop cost above the points is read (no prop is passed; players still are).
        Assert.False(Gun(new PierceFixture(10f, 20f), notes)!.Pierce!.CanPierce(10f));
        Assert.DoesNotContain(notes, n => n.Contains("penetration"));
        // A cost of 0 is read (free; the charged lascannon); an exit without a cone or without an exit action pierces with nothing past it.
        Assert.Equal(0f, Gun(new PierceFixture(10f, 0f), notes)!.Pierce!.PropCost);
        Assert.Null(Gun(new PierceFixture(10f, 1f, ExitShape: "_base_templates\\shape\\sphere"), notes)!.Pierce!.Exit);
        Assert.Contains(notes, n => n.Contains("not a cone"));
        Assert.Null(Gun(new PierceFixture(10f, 1f, ExitDamage: 0f), notes)!.Pierce!.Exit);
        Assert.Contains(notes, n => n.Contains("without an exit area of effect"));
    }
}
