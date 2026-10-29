using System.Numerics;
using SmFormats.Bod;
using SmFormats.Dcm;

namespace SmFormats.Tests;

public class DcmMeshTests
{
    static readonly Vector3[] Quad = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)];

    static SyntheticDcmWriter.Block TwoMaterialQuad() => new(
        Quad, [0, 1, 2, 0, 2, 3],
        [new("mat_a", 0, 3, 0, 3), new("mat_b", 3, 3, 0, 4)]);

    [Fact]
    public void Reads_blocks_streams_and_draw_ranges()
    {
        var mesh = DcmMesh.Read(SyntheticDcmWriter.Build([TwoMaterialQuad()]));
        var b = Assert.Single(mesh.Blocks);
        Assert.Equal(Quad, b.Positions);
        Assert.Equal(new ushort[] { 0, 1, 2, 0, 2, 3 }, b.Indices);
        Assert.Equal(new Vector3(0, 0, 0), b.BoundsMin);
        Assert.Equal(new Vector3(1, 1, 0), b.BoundsMax);
        Assert.Equal(["mat_a", "mat_b"], b.Materials.Select(m => m.Name));
        Assert.Equal((3, 3), (b.Materials[1].StartIndex, b.Materials[1].IndexCount));
        Assert.Equal((0, 4), (b.Materials[1].FirstVertex, b.Materials[1].VertexCount));
        Assert.Equal(new Vector3(0, 0, 1), b.Normals[2]);
        Assert.Equal(new Vector4(1, 0, 0, 1), b.Tangents[2]);
        Assert.Equal(new Vector2(1, 1), b.Uv0[2]);
        Assert.Equal(new Vector2(1, -1), b.Uv1[2]);
        Assert.Equal(0xFF808080u, b.Colors[0]);
    }

    [Fact]
    public void Second_block_follows_the_first_in_the_body()
    {
        var second = new SyntheticDcmWriter.Block([new(5, 5, 5), new(6, 5, 5), new(5, 6, 5)], [0, 1, 2], [new("mat_c", 0, 3, 0, 3)]);
        var mesh = DcmMesh.Read(SyntheticDcmWriter.Build([TwoMaterialQuad(), second]));
        Assert.Equal(2, mesh.Blocks.Count);
        Assert.Equal(new Vector3(6, 5, 5), mesh.Blocks[1].Positions[1]);
        Assert.Equal("mat_c", mesh.Blocks[1].Materials.Single().Name);
    }

    [Fact]
    public void Skinned_layout_is_rejected_as_not_supported()
    {
        var bytes = SyntheticDcmWriter.Build([TwoMaterialQuad()]);
        BitConverter.GetBytes(76).CopyTo(bytes, 12 + 6);   // block stride field
        Assert.Throws<NotSupportedException>(() => DcmMesh.Read(bytes));
    }

    [Fact]
    public void Out_of_range_index_is_rejected()
    {
        var bytes = SyntheticDcmWriter.Build([TwoMaterialQuad()]);
        int headerSize = BitConverter.ToInt32(bytes, 0);
        BitConverter.GetBytes((ushort)4).CopyTo(bytes, headerSize);   // first index -> vertex 4 of 4
        Assert.Throws<InvalidDataException>(() => DcmMesh.Read(bytes));
    }

    [Fact]
    public void Draw_range_outside_the_block_is_rejected()
    {
        var bad = TwoMaterialQuad() with { Materials = [new("mat_a", 3, 6, 0, 4)] };
        Assert.Throws<InvalidDataException>(() => DcmMesh.Read(SyntheticDcmWriter.Build([bad])));
    }

    [Fact]
    public void Every_truncation_fails_cleanly()
    {
        var bytes = SyntheticDcmWriter.Build([TwoMaterialQuad()]);
        for (int len = 0; len < bytes.Length; len++)
            Assert.Throws<InvalidDataException>(() => DcmMesh.Read(bytes.AsSpan(0, len)));
    }
}

public class BodDocumentTests
{
    static byte[] Sample() => SyntheticBodWriter.Material("hazzard", "diff_norm",
        [("DiffuseMap", "painted_hazzard_large_dif"), ("NormalMap", "painted_hazzard_large_nrm")], tileU: 2f);

    [Fact]
    public void Reads_string_table_and_value_tree()
    {
        var doc = BodDocument.Read(Sample());
        var root = Assert.IsType<BodStruct>(doc.Root);
        Assert.Equal("Material", root.Class.Text);
        Assert.Equal("hazzard", root["Name"]?.ToString());
        var list = Assert.IsType<BodList>(root["Parameters"]);
        Assert.Equal(3, list.Items.Count);
        var tile = Assert.IsType<BodStruct>(list.Items[2].Value);
        Assert.Equal(2f, Assert.IsType<BodFloat>(tile["Value"]).Value);
        Assert.Equal(4, Assert.IsType<BodArray>(tile["Extra"]).Items.Count);
        Assert.True(Assert.IsType<BodString>(list.Items[0].Key).IsKey);
    }

    [Fact]
    public void Layer_values_references_keyless_lists_bools_nulls_and_identifiers_parse_to_the_end()
    {
        var doc = BodDocument.Read(SyntheticBodWriter.Layer([("tdm_mkr_spawn_01", [1f, 2f, 3f], [0f, 0f, 90f]), ("other", [4f, 5f, 6f], [0f, 0f, 0f])]));
        var layer = Assert.IsType<BodStruct>(doc.Root);
        Assert.Equal("RegionLayer", layer.Class.Text);
        Assert.Equal(new BodBool(false), layer["IsNavMeshCover"]);
        var nav = Assert.IsType<BodStruct>(Assert.IsType<BodRef>(layer["NavMesh"]).Value);
        Assert.Equal(new BodUInt64(0x43E8ECA5F7F9E2F2UL), nav["GUID"]);
        var root = Assert.IsType<BodStruct>(Assert.IsType<BodRef>(layer["Root"]).Value);
        var objects = Assert.IsType<BodList>(root["Objects"]);
        Assert.Equal(0, objects.Flag);
        Assert.Equal(2, objects.Items.Count);
        Assert.IsType<BodNull>(objects.Items[0].Key);
        Assert.Equal(new BodIndex(3), objects.Items[1].Index);
        var marker = Assert.IsType<BodStruct>(objects.Items[0].Value);
        Assert.Equal("tdm_mkr_spawn_01", marker["Name"]?.ToString());
        Assert.IsType<BodNull>(marker["Unknown"]);
        Assert.Equal(90f, Assert.IsType<BodFloat>(Assert.IsType<BodArray>(marker["Rotation"]).Items[2]).Value);
    }

    [Fact]
    public void Material_exposes_shader_and_textures()
    {
        var m = BmatMaterial.From(BodDocument.Read(Sample()));
        Assert.Equal("hazzard", m.Name);
        Assert.Equal("diff_norm", m.ShaderName);
        Assert.Equal("painted_hazzard_large_dif", m.Textures["DiffuseMap"]);
        Assert.Equal("painted_hazzard_large_nrm", m.Textures["NormalMap"]);
    }

    [Fact]
    public void Wargear_material_reads_texture_names_from_the_struct_fields()
    {
        var bytes = SyntheticBodWriter.Material("Standard_2", "character_wargear",
            [("DiffuseMap", "chaos_piece_dif"), ("SpecularMap", "chaos_piece_spc"), ("PaintMask", "chaos_piece_pnt")], wargear: true);
        var m = BmatMaterial.From(BodDocument.Read(bytes));
        Assert.Equal("character_wargear", m.ShaderName);
        Assert.Equal("chaos_piece_dif", m.Textures["DiffuseMap"]);
        Assert.Equal("chaos_piece_spc", m.Textures["SpecularMap"]);
        Assert.Equal("chaos_piece_pnt", m.Textures["PaintMask"]);
    }

    [Fact]
    public void Wargear_material_reads_the_dirt_colour_into_the_vector_parameters()
    {
        var bytes = SyntheticBodWriter.Material("Standard_2", "character_wargear", [("PaintMask", "p_pnt")], wargear: true, dirtColour: [0.25f, 0.5f, 0.75f, 1f]);
        var m = BmatMaterial.From(BodDocument.Read(bytes));
        Assert.Equal([0.25f, 0.5f, 0.75f, 1f], m.Vectors["DirtColour"]);
        Assert.False(BmatMaterial.From(BodDocument.Read(SyntheticBodWriter.Material("m", "character_wargear", [("PaintMask", "p")], wargear: true))).Vectors.ContainsKey("DirtColour"));
    }

    [Fact]
    public void Empty_list_still_has_its_flag_byte()
    {
        var m = BmatMaterial.From(BodDocument.Read(SyntheticBodWriter.Material("shadow", "shadow", [])));
        Assert.Empty(m.Textures);
    }

    [Fact]
    public void Unknown_compression_flag_is_reported_as_not_supported()
    {
        var bytes = Sample();
        bytes[5] = 2;
        Assert.Throws<NotSupportedException>(() => BodDocument.Read(bytes));
    }

    [Fact]
    public void Unknown_value_type_is_reported_as_not_supported()
    {
        var bytes = Sample();
        // The ShaderName field's type byte follows the last occurrence of its hash (the first is in the string table).
        int at = bytes.AsSpan().LastIndexOf(BitConverter.GetBytes(SyntheticBodWriter.Hash("ShaderName"))) + 8;
        Assert.Equal(0x0F, bytes[at]);
        bytes[at] = 0x7E;
        Assert.Throws<NotSupportedException>(() => BodDocument.Read(bytes));
    }

    [Fact]
    public void Every_truncation_fails_cleanly()
    {
        var bytes = Sample();
        for (int len = 0; len < bytes.Length; len++)
            Assert.Throws<InvalidDataException>(() => BodDocument.Read(bytes.AsSpan(0, len)));
    }
}
