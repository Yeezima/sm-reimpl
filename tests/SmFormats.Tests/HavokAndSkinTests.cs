using System.IO.Compression;
using System.Numerics;
using SmContent;
using SmFormats.Bod;
using SmFormats.Dcm;
using SmFormats.Havok;
using SmFormats.Obpk;

namespace SmFormats.Tests;

public class HavokTests
{
    static readonly SyntheticHavokWriter.Bone[] Bones =
    [
        new("RIG_Root", -1, Vector3.Zero, Quaternion.Identity),
        new("RIG_Pelvis", 0, new Vector3(0, 0, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)),
        new("RIG_L_Thigh", 1, new Vector3(0.2f, 0, 0), Quaternion.Identity),
    ];

    static readonly Quaternion Turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 0.7f);

    static byte[] Sample() => SyntheticHavokWriter.Build(Bones,
    [
        new("Walk", 1f, 31, [
            new(new Vector3(0, 0, 0), Quaternion.Identity, (0f, 3f)),
            new(new Vector3(0, 0, 1), Turn),
            new(new Vector3(0.2f, 0, 0), Quaternion.Identity)]),
        new("Idle", 2f, 61, [
            new(Vector3.Zero, Quaternion.Identity),
            new(new Vector3(0, 0, 1), Quaternion.Identity),
            new(new Vector3(0.2f, 0, 0), Quaternion.Inverse(Turn))]),
    ]);

    [Fact]
    public void Reads_sections_objects_and_skeleton()
    {
        var pack = new HavokPackfile(Sample());
        Assert.Equal("hk_2010.2.0-r1", pack.ContentsVersion);
        Assert.Equal(["__classnames__", "__types__", "__data__"], pack.Sections.Select(s => s.Tag));
        Assert.Equal(2, pack.OfClass("hkaSplineCompressedAnimation").Count());
        var skeleton = HavokSkeleton.Read(pack, pack.OfClass("hkaSkeleton").Single().Offset);
        Assert.Equal(["RIG_Root", "RIG_Pelvis", "RIG_L_Thigh"], skeleton.BoneNames);
        Assert.Equal(new short[] { -1, 0, 1 }, skeleton.Parents);
        var model = skeleton.ModelSpace(skeleton.ReferencePose);
        // The thigh's +X offset is turned by the pelvis's 90° about Z: it ends up on +Y.
        Assert.Equal(0.2f, model[2].Translation.Y, 4);
        Assert.Equal(1f, model[2].Translation.Z, 4);
    }

    [Fact]
    public void Names_animations_through_the_combiner_toc_and_decodes_them()
    {
        var file = new HavokCharacterFile(Sample());
        Assert.Equal(["Walk", "Idle"], file.AnimationNames);
        var walk = file.LoadAnimation("Walk");
        Assert.Equal(new short[] { 0, 1, 2 }, walk.TrackToBone);
        Assert.Equal(0f, walk.Animation.Sample(0f)[0].Translation.X, 3);
        Assert.Equal(1.5f, walk.Animation.Sample(0.5f)[0].Translation.X, 3);
        Assert.Equal(3f, walk.Animation.Sample(1f)[0].Translation.X, 3);
        var turn = walk.Animation.Sample(0.25f)[1].Rotation;
        Assert.True(MathF.Abs(Quaternion.Dot(turn, Turn)) > 0.99999f);
        Assert.Equal(new Vector3(0, 0, 1), walk.Animation.Sample(0.25f)[1].Translation);
        Assert.Throws<KeyNotFoundException>(() => file.LoadAnimation("Run"));
    }

    [Fact]
    public void ThreeComp40_round_trips_within_quantization()
    {
        var random = new Random(7);
        for (int i = 0; i < 500; i++)
        {
            var q = Quaternion.Normalize(new Quaternion((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f));
            var d = HavokSplineAnimation.DecodeQuaternion(SyntheticHavokWriter.EncodeThreeComp40(q), 1);
            Assert.True(MathF.Abs(Vector4.Dot(d, new Vector4(q.X, q.Y, q.Z, q.W))) > 0.99999f);
        }
    }

    [Fact]
    public void Polar32_decodes_its_documented_extremes()
    {
        Assert.Equal(new Vector4(0, 0, 0, 1), HavokSplineAnimation.DecodeQuaternion(BitConverter.GetBytes(0u), 0));
        Assert.Equal(new Vector4(0, 0, 0, -1), HavokSplineAnimation.DecodeQuaternion(BitConverter.GetBytes(0x80000000u), 0));
        var z = HavokSplineAnimation.DecodeQuaternion(BitConverter.GetBytes(0x3FFu << 18), 0);   // radius term 0, angles 0
        Assert.Equal(1f, z.Z, 5);
        Assert.Equal(0f, z.W, 5);
    }

    [Fact]
    public void Bad_parent_index_is_rejected()
    {
        var bad = Bones.ToArray();
        bad[1] = bad[1] with { Parent = 2 };
        var pack = new HavokPackfile(SyntheticHavokWriter.Build(bad, []));
        Assert.Throws<InvalidDataException>(() => HavokSkeleton.Read(pack, pack.OfClass("hkaSkeleton").Single().Offset));
    }

    /// Patches a 32-bit field of the first object of a class (offset relative to the object).
    static byte[] Patch(string cls, int field, int value)
    {
        var bytes = Sample();
        var pack = new HavokPackfile(bytes);
        var data = pack.Sections[pack.DataSectionIndex];
        BitConverter.GetBytes(value).CopyTo(bytes, data.Absolute + pack.OfClass(cls).First().Offset + field);
        return bytes;
    }

    [Fact]
    public void Zero_frame_duration_is_rejected()
    {
        var file = new HavokCharacterFile(Patch("hkaSplineCompressedAnimation", 64, 0));
        Assert.Throws<InvalidDataException>(() => file.LoadAnimation("Walk"));
    }

    [Fact]
    public void Binding_with_fewer_track_entries_than_tracks_is_rejected()
    {
        var file = new HavokCharacterFile(Patch("hkaAnimationBinding", 16 + 4, 2));
        Assert.Throws<InvalidDataException>(() => file.LoadAnimation("Walk"));
    }

    [Fact]
    public void Array_running_past_the_section_is_rejected()
    {
        var pack = new HavokPackfile(Patch("hkaSkeleton", 36 + 4, 1_000_000));
        Assert.Throws<InvalidDataException>(() => HavokSkeleton.Read(pack, pack.OfClass("hkaSkeleton").Single().Offset));
    }

    [Fact]
    public void Truncated_packfiles_fail_cleanly()
    {
        var bytes = Sample();
        for (int len = 0; len < bytes.Length; len += 7)
        {
            var cut = bytes.AsSpan(0, len).ToArray();
            var ex = Record.Exception(() => new HavokCharacterFile(cut).LoadAnimation("Walk"));
            Assert.True(ex is InvalidDataException or KeyNotFoundException, $"length {len}: {ex?.GetType().Name}");
        }
    }
}

public class SkinnedDcmTests
{
    static SyntheticDcmWriter.Block SkinnedQuad(byte firstBone = 0) => new(
        [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)], [0, 1, 2, 0, 2, 3], [new("sm_torso", 0, 6, 0, 4)],
        Skinning: new SyntheticDcmWriter.Skin(
            [new("RIG_Pelvis", Matrix4x4.CreateTranslation(0, 0, -1)), new("RIG_SpineA", Matrix4x4.Identity)],
            [new(1, 0, 0, 0), new(0.75f, 0.25f, 0, 0), new(0.5f, 0.5f, 0, 0), new(0, 1, 0, 0)],
            [firstBone, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0]));

    [Fact]
    public void Reads_skinned_block_bones_weights_and_streams()
    {
        var mesh = DcmMesh.Read(SyntheticDcmWriter.Build([SkinnedQuad()]));
        var b = Assert.Single(mesh.Blocks);
        var skin = Assert.IsType<DcmSkin>(b.Skin);
        Assert.Equal(["RIG_Pelvis", "RIG_SpineA"], skin.Bones.Select(x => x.Name));
        Assert.Equal(-1f, skin.Bones[0].InverseBind.M43);
        Assert.Equal(new Vector4(0.75f, 0.25f, 0, 0), skin.Weights[1]);
        Assert.Equal(new byte[] { 0, 1, 0, 0 }, skin.BoneIndices[4..8]);
        Assert.Equal(new Vector3(1, 1, 0), b.Positions[2]);
        Assert.Equal(new Vector3(0, 0, 1), b.Normals[3]);
        Assert.Equal(new Vector2(1, 1), b.Uv0[2]);
    }

    [Fact]
    public void Static_and_skinned_blocks_mix_in_one_mesh()
    {
        var rigid = new SyntheticDcmWriter.Block([new(5, 5, 5), new(6, 5, 5), new(5, 6, 5)], [0, 1, 2], [new("blade", 0, 3, 0, 3)]);
        var mesh = DcmMesh.Read(SyntheticDcmWriter.Build([SkinnedQuad(), rigid]));
        Assert.NotNull(mesh.Blocks[0].Skin);
        Assert.Null(mesh.Blocks[1].Skin);
        Assert.Equal(new Vector3(6, 5, 5), mesh.Blocks[1].Positions[1]);
    }

    [Fact]
    public void Weighted_bone_outside_the_block_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => DcmMesh.Read(SyntheticDcmWriter.Build([SkinnedQuad(firstBone: 2)])));
    }

    [Fact]
    public void Unknown_block_type_is_not_supported()
    {
        var bytes = SyntheticDcmWriter.Build([SkinnedQuad()]);
        bytes[12] = 2;
        Assert.Throws<NotSupportedException>(() => DcmMesh.Read(bytes));
    }

    [Fact]
    public void Every_truncation_fails_cleanly()
    {
        var bytes = SyntheticDcmWriter.Build([SkinnedQuad()]);
        for (int len = 0; len < bytes.Length; len++)
            Assert.Throws<InvalidDataException>(() => DcmMesh.Read(bytes.AsSpan(0, len)));
    }

    // Two materials with their own bone palettes (the Ork boy layout): six vertices, three bones A, B, C.
    // Material 0 (vertices 0-2) palette [C, A]; material 1 (vertices 3-5) palette [B, C, A].
    static SyntheticDcmWriter.Block TwoMaterialSkinned(int[][]? palettes = null, byte[]? slots = null, int firstMaterialVertices = 3)
    {
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(2, 0, 0), new(3, 0, 0), new(2, 1, 0)];
        Vector4[] weights = [new(1, 0, 0, 0), new(0.5f, 0.5f, 0, 0), new(0.25f, 0.75f, 0, 0), new(1, 0, 0, 0), new(0.5f, 0.5f, 0, 0), new(0.5f, 0.5f, 0, 0)];
        slots ??= [0, 0, 0, 0,  1, 0, 0, 0,  1, 1, 0, 0,  2, 0, 0, 0,  1, 2, 0, 0,  0, 1, 0, 0];
        return new SyntheticDcmWriter.Block(positions, [0, 1, 2, 3, 4, 5],
            [new("mat_a", 0, 3, 0, firstMaterialVertices), new("mat_b", 3, 3, 3, 3)],
            Skinning: new SyntheticDcmWriter.Skin(
                [new("A", Matrix4x4.Identity), new("B", Matrix4x4.Identity), new("C", Matrix4x4.Identity)], weights, slots,
                palettes ?? [[2, 0], [1, 2, 0]]));
    }

    [Fact]
    public void Multi_material_block_reads_each_ranges_palette_and_maps_the_bone_slots()
    {
        var mesh = DcmMesh.Read(SyntheticDcmWriter.Build([TwoMaterialSkinned()]));
        var block = Assert.Single(mesh.Blocks);
        Assert.Equal(new[] { 2, 0 }, block.Materials[0].BonePalette);
        Assert.Equal(new[] { 1, 2, 0 }, block.Materials[1].BonePalette);
        Assert.Equal(2u, block.Materials[0].Unknown6);
        Assert.Equal(3u, block.Materials[1].Unknown6);
        var skin = block.Skin!;
        // Stored slots 0,0,0,0 | 1,0,0,0 | 1,1,0,0 | 2,0,0,0 | 1,2,0,0 | 0,1,0,0 become block bone indices through the vertex's own palette.
        Assert.Equal(new byte[] { 2, 2, 2, 2,  0, 2, 2, 2,  0, 0, 2, 2,  0, 1, 1, 1,  2, 0, 1, 1,  1, 2, 1, 1 }, skin.BoneIndices);
        Assert.Equal(new[] { 2, 0, 1 }, skin.BoneList);   // distinct union, first use first
        Assert.Equal(new Vector4(0.25f, 0.75f, 0, 0), skin.Weights[2]);
        Assert.Equal(new Vector3(2, 1, 0), block.Positions[5]);
    }

    [Fact]
    public void Single_material_block_has_the_identity_palette_and_unchanged_bone_indices()
    {
        var b = Assert.Single(DcmMesh.Read(SyntheticDcmWriter.Build([SkinnedQuad()])).Blocks);
        Assert.Equal(new[] { 0, 1 }, b.Materials[0].BonePalette);
        Assert.Equal(new[] { 0, 1 }, b.Skin!.BoneList);
        Assert.Equal(new byte[] { 0, 0, 0, 0,  0, 1, 0, 0,  0, 1, 0, 0,  1, 0, 0, 0 }, b.Skin.BoneIndices);
    }

    [Fact]
    public void Static_blocks_have_no_palette()
    {
        var rigid = new SyntheticDcmWriter.Block([new(5, 5, 5), new(6, 5, 5), new(5, 6, 5)], [0, 1, 2], [new("blade", 0, 3, 0, 3)]);
        var mesh = DcmMesh.Read(SyntheticDcmWriter.Build([TwoMaterialSkinned(), rigid]));
        Assert.Null(mesh.Blocks[1].Materials[0].BonePalette);
        Assert.Equal(0u, mesh.Blocks[1].Materials[0].Unknown6);
    }

    [Fact]
    public void Block_without_materials_keeps_the_stand_alone_bone_list()
    {
        var quad = SkinnedQuad();
        var noMaterials = quad with { Materials = [], Indices = [] };
        var b = Assert.Single(DcmMesh.Read(SyntheticDcmWriter.Build([noMaterials])).Blocks);
        Assert.Equal(new[] { 0, 1 }, b.Skin!.BoneList);
    }

    [Fact]
    public void Weighted_slot_past_the_ranges_palette_is_rejected()
    {
        // Slot 2 is valid for material 1 (palette of 3) but not for material 0 (palette of 2).
        byte[] slots = [2, 0, 0, 0,  1, 0, 0, 0,  1, 1, 0, 0,  2, 0, 0, 0,  1, 2, 0, 0,  0, 1, 0, 0];
        Assert.Throws<InvalidDataException>(() => DcmMesh.Read(SyntheticDcmWriter.Build([TwoMaterialSkinned(slots: slots)])));
    }

    [Fact]
    public void Palette_entry_outside_the_block_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => DcmMesh.Read(SyntheticDcmWriter.Build([TwoMaterialSkinned([[2, 3], [1, 2, 0]])])));
    }

    [Fact]
    public void Palette_longer_than_the_block_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => DcmMesh.Read(SyntheticDcmWriter.Build([TwoMaterialSkinned([[2, 0, 1, 0], [1, 2, 0]])])));
    }

    [Fact]
    public void Ranges_sharing_a_vertex_must_share_its_palette()
    {
        // Material 0 reaches vertices 0-3, material 1 vertices 3-5: vertex 3 lies in both.
        Assert.Throws<InvalidDataException>(() => DcmMesh.Read(SyntheticDcmWriter.Build([TwoMaterialSkinned(firstMaterialVertices: 4)])));
        var same = DcmMesh.Read(SyntheticDcmWriter.Build([TwoMaterialSkinned([[1, 2, 0], [1, 2, 0]], firstMaterialVertices: 4)]));
        Assert.Single(same.Blocks);
    }

    [Fact]
    public void Zero_weight_slot_may_name_a_bone_past_the_palette_and_the_block()
    {
        // Vertex 0: slot 0 weighted (-> bone C), slot 1 is 200 with weight 0; nothing consumes an unweighted slot.
        byte[] slots = [0, 200, 0, 0,  1, 0, 0, 0,  1, 1, 0, 0,  2, 0, 0, 0,  1, 2, 0, 0,  0, 1, 0, 0];
        var skin = Assert.Single(DcmMesh.Read(SyntheticDcmWriter.Build([TwoMaterialSkinned(slots: slots)])).Blocks).Skin!;
        Assert.Equal((byte)2, skin.BoneIndices[0]);
        Assert.Equal((byte)200, skin.BoneIndices[1]);
        Assert.Equal(new Vector4(1, 0, 0, 0), skin.Weights[0]);

        // The same on a single-material block (identity palette of 2): slot 200 with weight 0.
        var quad = SkinnedQuad();
        byte[] quadSlots = [0, 200, 0, 0,  0, 1, 0, 0,  0, 1, 0, 0,  1, 0, 0, 0];
        var single = DcmMesh.Read(SyntheticDcmWriter.Build([quad with { Skinning = quad.Skinning! with { BoneIndices = quadSlots } }]));
        Assert.Equal((byte)200, single.Blocks[0].Skin!.BoneIndices[1]);
    }

    [Fact]
    public void Weighted_vertex_outside_every_range_is_rejected_on_multi_material_blocks()
    {
        // Material 1 only covers vertices 3-4 (two indices), so vertex 5 lies in no range but keeps its weights.
        var block = TwoMaterialSkinned();
        var shortRanges = block with { Materials = [new("mat_a", 0, 3, 0, 3), new("mat_b", 3, 2, 3, 2)] };
        Assert.Throws<InvalidDataException>(() => DcmMesh.Read(SyntheticDcmWriter.Build([shortRanges])));

        // An unweighted vertex outside the ranges is harmless.
        var weights = block.Skinning!.Weights.ToArray();
        weights[5] = Vector4.Zero;
        var unweighted = shortRanges with { Skinning = shortRanges.Skinning! with { Weights = weights } };
        var mesh = DcmMesh.Read(SyntheticDcmWriter.Build([unweighted]));
        Assert.Equal(Vector4.Zero, mesh.Blocks[0].Skin!.Weights[5]);
    }

    [Fact]
    public void Index_outside_its_materials_vertex_range_is_rejected_on_multi_material_blocks()
    {
        // Material 1 draws indices 3, 4 and 0, but its vertices are 3-5.
        var crossing = TwoMaterialSkinned() with { Indices = [0, 1, 2, 3, 4, 0] };
        Assert.Throws<InvalidDataException>(() => DcmMesh.Read(SyntheticDcmWriter.Build([crossing])));
    }

    [Fact]
    public void Every_truncation_of_a_multi_material_block_fails_cleanly()
    {
        var bytes = SyntheticDcmWriter.Build([TwoMaterialSkinned()]);
        for (int len = 0; len < bytes.Length; len++)
            Assert.Throws<InvalidDataException>(() => DcmMesh.Read(bytes.AsSpan(0, len)));
    }
}

public class CompressedBodTests
{
    static byte[] Compress(byte[] uncompressed)
    {
        var payload = uncompressed[10..];
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(payload);
        var output = new MemoryStream();
        output.Write(uncompressed.AsSpan(0, 10));
        output.Write(BitConverter.GetBytes(payload.Length));
        output.Write(z.ToArray());
        var bytes = output.ToArray();
        bytes[5] = 1;
        return bytes;
    }

    [Fact]
    public void Compressed_document_reads_like_the_uncompressed_one()
    {
        var plain = SyntheticBodWriter.Material("sm_torso", "diff_norm", [("DiffuseMap", "sm_armour_basic_001_torso_dif")]);
        var m = BmatMaterial.From(BodDocument.Read(Compress(plain)));
        Assert.Equal("sm_armour_basic_001_torso_dif", m.Textures["DiffuseMap"]);
        Assert.Contains("RIG_L_ShoulderPad", BodDocument.ReadStrings(Compress(SyntheticBodWriter.Material("RIG_L_ShoulderPad", "x", []))));
    }

    [Fact]
    public void ReadStrings_rejects_unknown_compression_flags_like_Read()
    {
        var bytes = SyntheticBodWriter.Material("a", "b", []);
        bytes[5] = 2;
        Assert.Throws<NotSupportedException>(() => BodDocument.ReadStrings(bytes));
    }

    [Fact]
    public void Short_compressed_payload_is_rejected()
    {
        var bytes = Compress(SyntheticBodWriter.Material("a", "b", []));
        BitConverter.GetBytes(100_000).CopyTo(bytes, 10);
        Assert.Throws<InvalidDataException>(() => BodDocument.Read(bytes));
    }
}

public class CharacterModelTests
{
    [Fact]
    public void Assembles_rig_skinned_and_rigid_pieces_from_archives()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-character-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var rig = SyntheticHavokWriter.Build(
                [new("RIG_Root", -1, Vector3.Zero, Quaternion.Identity), new("RIG_L_ShoulderPad", 0, new Vector3(0, 0, 2), Quaternion.Identity)],
                [new("Idle", 1f, 31, [new(Vector3.Zero, Quaternion.Identity), new(new Vector3(0, 0, 2), Quaternion.Identity)])]);
            File.WriteAllBytes(Path.Combine(dir, "rig.oppc"), SyntheticObpkWriter.Build([new("hkxx", "havok_test__hkxx", rig)]));

            var skinned = SyntheticDcmWriter.Build([new([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2], [new("arm", 0, 3, 0, 3)],
                Skinning: new([new("RIG_Root", Matrix4x4.Identity)], [Vector4.UnitX, Vector4.UnitX, Vector4.UnitX], new byte[12]))]);
            File.WriteAllBytes(Path.Combine(dir, "test_arm.oppc"), SyntheticObpkWriter.Build([new("dcm", "test_arm", skinned)]));

            var pad = SyntheticDcmWriter.Build([new([new(0, 0, 2), new(1, 0, 2), new(0, 1, 2)], [0, 1, 2], [new("pad", 0, 3, 0, 3)])]);
            // The rigid piece's object tree names its bone; a material-shaped document is enough to carry that string.
            var o3d = SyntheticBodWriter.Material("GEO_L_ShoulderPad", "RIG_L_ShoulderPad", []);
            File.WriteAllBytes(Path.Combine(dir, "test_pad.oppc"), SyntheticObpkWriter.Build([new("dcm", "test_pad", pad), new("O3d", "test_pad", o3d)]));

            var notes = new List<string>();
            var model = CharacterModel.Load(Path.Combine(dir, "rig.oppc"), "havok_test__hkxx",
                [Path.Combine(dir, "test_arm.oppc"), Path.Combine(dir, "test_pad.oppc")], notes);
            Assert.Equal(["RIG_Root", "RIG_L_ShoulderPad"], model.Rig.Skeleton.BoneNames);
            Assert.Equal(["Idle"], model.Rig.AnimationNames);
            Assert.Null(model.Pieces[0].RigidBone);
            Assert.Equal("RIG_L_ShoulderPad", model.Pieces[1].RigidBone);
            Assert.DoesNotContain(notes, n => n.Contains("not in the skeleton"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
