using System.Numerics;
using SmContent;
using SmContent.Customization;
using SmFormats.Dcm;
using SmFormats.Obpk;
using Xunit;
using static SmFormats.Bod.SyntheticBodWriter;

namespace SmFormats.Tests;

/// <summary>
/// fix/armour-pieces (a tester's report of 2026-10-29: the Salamanders Veteran set drew no torso, the Emperor's Children set no backpack): the armour's
/// type-2 cloth blocks, a package whose mesh is named otherwise, and cloth that hangs under no bone. Synthetic fixtures only; the install check is
/// <see cref="ArmourAuditInstallTests"/>.
/// </summary>
public class ArmourPiecesTests
{
    static SyntheticDcmWriter.Block Tri(float z, string material = "torso", int? simulation = null, bool shortTrailer = false) =>
        new([new(0, 0, z), new(1, 0, z), new(0, 1, z)], [0, 1, 2], [new(material, 0, 3, 0, 3)], SimulationBytes: simulation, ShortTrailer: shortTrailer);

    [Fact]
    public void The_armours_short_type_2_trailer_reads_and_the_next_block_follows_its_range()
    {
        // The Salamanders Veteran torso's layout: two static blocks, the cloth (type 2, short trailer), then a skinned block.
        var skinned = new SyntheticDcmWriter.Block([new(0, 0, 3), new(1, 0, 3), new(0, 1, 3)], [0, 1, 2], [new("torso", 0, 3, 0, 3)],
            Skinning: new([new("RIG_SpineD", Matrix4x4.Identity)], [Vector4.UnitX, Vector4.UnitX, Vector4.UnitX], new byte[12]));
        var mesh = DcmMesh.Read(SyntheticDcmWriter.Build([Tri(0), Tri(1), Tri(2, "cloth", simulation: 41, shortTrailer: true), skinned]));
        Assert.Equal(4, mesh.Blocks.Count);
        var cloth = mesh.Blocks[2];
        Assert.Equal(2, cloth.Flags);
        Assert.Equal(1u, cloth.Materials.Single().Unknown6);
        var sim = Assert.IsType<DcmSimulationData>(cloth.Simulation);
        Assert.Equal((0u, 1u, 41, DcmMesh.ShortSimulationTrailerSize), (sim.Unknown, sim.Count, sim.Size, sim.TrailerSize));
        Assert.Equal(0, sim.Offset % 16);
        Assert.Equal(new Vector3(1, 0, 2), cloth.Positions[1]);
        Assert.NotNull(mesh.Blocks[3].Skin);
        Assert.Equal(new Vector3(0, 1, 3), mesh.Blocks[3].Positions[2]);
    }

    [Fact]
    public void The_weapons_long_trailer_still_reads_and_says_its_size()
    {
        var sim = DcmMesh.Read(SyntheticDcmWriter.Build([Tri(0, "cable", simulation: 20)])).Blocks[0].Simulation!;
        Assert.Equal((0u, 1u, 20, DcmMesh.SimulationTrailerSize), (sim.Unknown, sim.Count, sim.Size, sim.TrailerSize));
    }

    [Fact]
    public void A_short_trailer_whose_range_does_not_follow_the_streams_stays_not_supported()
    {
        var bytes = SyntheticDcmWriter.Build([Tri(0, "cloth", simulation: 20, shortTrailer: true)]);
        int headerSize = BitConverter.ToInt32(bytes, 0);
        BitConverter.GetBytes(BitConverter.ToInt32(bytes, headerSize - 8) + 64).CopyTo(bytes, headerSize - 8);   // offset, size: the last 8 header bytes
        Assert.Throws<NotSupportedException>(() => DcmMesh.Read(bytes));
    }

    [Fact]
    public void A_short_trailer_needs_the_material_records_one()
    {
        // The same bytes with the record's last u32 put back to 0: neither reading fits.
        var bytes = SyntheticDcmWriter.Build([Tri(0, "cloth", simulation: 20, shortTrailer: true)]);
        int headerSize = BitConverter.ToInt32(bytes, 0);
        int last = headerSize - DcmMesh.ShortSimulationTrailerSize - 4;
        Assert.Equal(1, BitConverter.ToInt32(bytes, last));
        BitConverter.GetBytes(0).CopyTo(bytes, last);
        Assert.Throws<NotSupportedException>(() => DcmMesh.Read(bytes));
    }

    [Fact]
    public void Nearest_bone_to_the_top_skips_the_helpers_and_cloth_follows_the_pelvis()
    {
        var bones = new Dictionary<string, Vector3>
        {
            ["RIG_Root"] = new(0, 0, 1.3f), ["RIG_Pelvis"] = new(0, 0, 1.2f), ["RIG_SpineA"] = new(0, 0, 1.4f), ["RIG_Prop_M_Chest"] = new(0, 0, 1.36f),
        };
        Vector3[] tabard = [new(0, 0.2f, 0.6f), new(0.1f, 0.2f, 1.35f), new(-0.1f, 0.2f, 1.37f)];
        Assert.Equal("RIG_SpineA", CharacterModel.NearestBoneToTop(tabard, bones));
        Assert.Equal("RIG_Pelvis", CharacterModel.ClothBone(tabard, bones));
        bones.Remove("RIG_Pelvis");
        Assert.Equal("RIG_SpineA", CharacterModel.ClothBone(tabard, bones));
        Assert.Null(CharacterModel.NearestBoneToTop([], bones));
        Assert.Null(CharacterModel.NearestBoneToTop(tabard, new Dictionary<string, Vector3> { ["RIG_Root"] = Vector3.Zero }));
    }

    [Fact]
    public void A_package_with_one_mesh_named_otherwise_loads_and_root_cloth_follows_the_pelvis()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-armour-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Block 0 hangs under RIG_SpineA (1 m up); block 1 (cloth) sits at the tree's root, its vertices in model space; block 2 is a LOD.
            var mesh = SyntheticDcmWriter.Build([Tri(0, "pack"), Tri(0.9f, "cloth", simulation: 16, shortTrailer: true), Tri(0, "pack")]);
            var skeleton = new Struct("Skeleton3D", ("Name", new S("pack")), ("Children", new List(
                Node3D("RIG_SpineA", [0, 0, 1], [0, 0, 0], Node3D("GEO_pack", [0, 0.5f, 0], [0, 0, 0])),
                Node3D("GEO_cloth_sim", [0, 0, 0], [0, 0, 0]),
                Node3D("GEO_cloth", [0, 0, 0], [0, 0, 0]),
                Node3D("GEO_pack_LOD", [0, 0, 0], [0, 0, 0]))));
            var o3d = Document(new Struct("Object3D", ("Skeleton", new Ref(skeleton)), ("Visuals", new List(
                Visual("GEO_pack", "csm_test_backpack", 0), Visual("GEO_cloth", "csm_test_backpack", 1), Visual("GEO_pack_LOD", "csm_test_backpack", 2)))));
            string path = Path.Combine(dir, "csm_test_bp.oppc");
            File.WriteAllBytes(path, SyntheticObpkWriter.Build([new("dcm", "csm_test_backpack", mesh), new("O3d", "csm_test_backpack", o3d)]));
            Assert.Equal("csm_test_backpack", CharacterModel.MeshNameOf(path));

            var names = new HashSet<string>(["RIG_Root", "RIG_Pelvis", "RIG_SpineA"], StringComparer.Ordinal);
            var positions = new Dictionary<string, Vector3> { ["RIG_Root"] = new(0, 0, 1.2f), ["RIG_Pelvis"] = new(0, 0, 1.2f), ["RIG_SpineA"] = new(0, 0, 1f) };
            var piece = CharacterModel.LoadPiece(names, path, [], objectTreePlacement: true, bonePositions: positions);
            Assert.Equal(3, piece.Model.Mesh.Blocks.Count);
            Assert.Equal(new RigidBlock("RIG_SpineA", Matrix4x4.CreateTranslation(0, 0.5f, 0)), piece.Rigid![0]);
            var cloth = piece.Rigid[1];
            Assert.Equal(("RIG_Pelvis", true, "GEO_cloth"), (cloth.Bone, cloth.ModelSpace, cloth.Node));
            Assert.Equal(Matrix4x4.Identity, cloth.Local);
            Assert.Contains(2, piece.Hidden!);

            // Without the bones' positions the cloth is left out with a note, as before.
            var notes = new List<string>();
            var old = CharacterModel.LoadPiece(names, path, notes, objectTreePlacement: true);
            Assert.False(old.Rigid!.ContainsKey(1));
            Assert.Contains(notes, n => n.Contains("hangs under no skeleton bone"));

            // The audit counts the same: both visible blocks drawn, the cloth reported as free-hanging on the pelvis.
            Directory.CreateDirectory(Path.Combine(dir, "data", "packages"));
            File.Copy(path, Path.Combine(dir, "data", "packages", "csm_test_bp.oppc"));
            var check = new ArmourAudit(dir, names, positions).Package("csm_test_bp");
            Assert.Null(check.Problem);
            Assert.Equal((3, 2), (check.Surfaces, check.Drawn));
            Assert.Equal(["block 1 ('GEO_cloth') on RIG_Pelvis"], check.FreeHanging);
            Assert.True(new ArmourAudit(dir, names, positions).Package("csm_missing").Missing);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static Struct Node3D(string name, float[] position, float[] rotation, params Node[] children) => new("Node3D",
        ("Name", new S(name)), ("Position", new Vec(position)), ("Rotation", new Vec(rotation)), ("Scale", new Vec(1, 1, 1)), ("Children", new List(children)));

    static Struct Visual(string node, string mesh, int id) => new("StaticMeshVisual",
        ("RefNode", new S(node)), ("MeshName", new S(mesh)), ("MeshID", new I(id)), ("Renderable", new B(true)));
}

/// <summary>
/// fix/armour-pieces: every class's default look, every armour set worn whole and every offered piece of both races resolve and draw on the player's install
/// (<see cref="ArmourAudit"/>, as <c>smtool armour-audit</c>); the only gaps left are the three DLC torsos' belt packages, which the install does not have at all.
/// Skipped when no install is found (CI). No original bytes.
/// </summary>
public class ArmourAuditInstallTests
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the audit needs the player's install; never in CI)";
        }
    }

    [InstallFact]
    public void Every_set_and_piece_of_both_races_draws()
    {
        string game = Game.Value!;
        var catalog = CustomizationCatalog.Load(game);
        var audit = ArmourAudit.ForInstall(game);
        var cases = audit.Run(catalog);
        Assert.NotEmpty(cases);
        Assert.Contains(cases, c => c.Subject.StartsWith("set dlc2_set2 ", StringComparison.Ordinal));
        Assert.Contains(cases, c => c.Subject.StartsWith("set emperors_children_dlc ", StringComparison.Ordinal));
        var failing = cases.Where(c => c.Problems.Count > 0).Select(c => $"{c.ClassKey} | {c.Subject}: {string.Join("; ", c.Problems)}").ToList();
        Assert.True(failing.Count == 0, string.Join("\n", failing));
        var gaps = cases.SelectMany(c => c.Gaps).Select(g => g[(g.IndexOf("package ", StringComparison.Ordinal) + 8)..]).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["sm_armour_dlc1_set1_belt: not in the install", "sm_armour_dlc2_set1_belt: not in the install", "sm_armour_dlc3_set1_belt: not in the install"], gaps);
    }
}
