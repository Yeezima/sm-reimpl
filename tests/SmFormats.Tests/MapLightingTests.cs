using System.Numerics;
using SmContent;
using SmFormats.Bod;
using SmFormats.Dcm;
using SmFormats.Obpk;
using static SmFormats.Bod.SyntheticBodWriter;

namespace SmFormats.Tests;

/// <summary>The <c>l_lights</c> layer (sun, environment regions, placed lights) and the material parameters the lit look needs.</summary>
public class MapLightingTests
{
    static Node Tex(string name, string value) => new Struct("TextureParameter", ("Value", new S(value)), ("PackageName", new S("")), ("Name", new S(name)));
    static Node FloatParam(string name, float value) => new Struct("FloatParameter", ("Value", new F(value)), ("Name", new S(name)));
    static Node VectorParam(string name, params float[] value) => new Struct("Vector4Parameter", ("Value", new Vec(value)), ("Name", new S(name)));

    [Fact]
    public void Material_reads_float_and_vector_parameters_beside_the_textures()
    {
        var bytes = Document(new Struct("Material", ("Name", new S("rubble")), ("ShaderName", new S("diff_norm_drvspec")),
            ("Parameters", new List(
                Tex("DiffuseMap", "rubble_dif"), Tex("NormalMap", "rubble_nrm"), Tex("EmissiveMap", "rubble_emi"),
                FloatParam("SpecularPower", 20f), VectorParam("SpecularColor", 0.147f, 0.127f, 0.114f, 1f),
                new Struct("Vector4Parameter", ("Value", new Vec()), ("Name", new S("Empty")))))));
        var m = BmatMaterial.From(BodDocument.Read(bytes));
        Assert.Equal("rubble_nrm", m.Textures["NormalMap"]);
        Assert.Equal("rubble_emi", m.Textures["EmissiveMap"]);
        Assert.Equal(20f, m.Floats["SpecularPower"]);
        Assert.Equal(new[] { 0.147f, 0.127f, 0.114f, 1f }, m.Vectors["SpecularColor"]);
        Assert.False(m.Vectors.ContainsKey("Empty"));
        Assert.False(m.Floats.ContainsKey("SpecularColor"));
    }

    [Fact]
    public void Material_without_extra_parameters_has_empty_dictionaries()
    {
        var m = new BmatMaterial("x", "y", new Dictionary<string, string>());
        Assert.Empty(m.Floats);
        Assert.Empty(m.Vectors);
        Assert.Null(m.BlendMode);
    }

    [Fact]
    public void Material_reads_blend_and_cull_modes_and_second_layer_parameters()
    {
        var bytes = Document(new Struct("Material", ("Name", new S("grate")), ("ShaderName", new S("dif2_advvtxbldeg_vtxtint_norm2_drvspec")),
            ("LightingModel", new I(255)), ("BlendMode", new I(8)), ("CullMode", new I(0)),
            ("Parameters", new List(
                Tex("DiffuseMap", "g_dif"), Tex("DiffuseMap2", "g_dif2"), Tex("NormalMap2", "g_nrm2"), Tex("AlphaMaskMap", "g_msk"),
                FloatParam("TileU", 2f), FloatParam("TileV", 3f), VectorParam("VtxBlendParams", 4f, 1f, 25f, 1f)))));
        var m = BmatMaterial.From(BodDocument.Read(bytes));
        Assert.Equal((8, 0), (m.BlendMode, m.CullMode));
        Assert.Equal("g_dif2", m.Textures["DiffuseMap2"]);
        Assert.Equal("g_msk", m.Textures["AlphaMaskMap"]);
        Assert.Equal(new[] { 4f, 1f, 25f, 1f }, m.Vectors["VtxBlendParams"]);
        Assert.Equal((2f, 3f), (m.Floats["TileU"], m.Floats["TileV"]));
        Assert.Contains("DiffuseMap2", MapScene.TextureParameters);
        Assert.Contains("AlphaMaskMap", MapScene.TextureParameters);
    }

    [Fact]
    public void Environment_region_weight_is_zero_outside_and_rises_smoothly_to_one_inside()
    {
        var box = new MapEnvironment("b", 2, new Vector3(100, 0, 0), new Vector3(40, 40, 40), null, null, null, null);
        Assert.Equal(0f, box.Weight(new Vector3(130, 0, 0), 8f));        // outside (10 m past the face at x = 120)
        Assert.Equal(0f, box.Weight(new Vector3(120, 0, 0), 8f));        // on the face
        Assert.Equal(1f, box.Weight(new Vector3(100, 0, 0), 8f));        // centre
        Assert.Equal(1f, box.Weight(new Vector3(112, 0, 0), 8f));        // 8 m in
        float quarter = box.Weight(new Vector3(118, 0, 0), 8f);          // 2 m in: smoothstep(0.25) = 0.15625
        Assert.True(MathF.Abs(quarter - 0.15625f) < 1e-5f, quarter.ToString());
        Assert.True(box.Weight(new Vector3(116, 0, 0), 8f) > quarter);   // monotonic toward the centre
        Assert.Equal(0f, box.Weight(new Vector3(100, 0, 25), 8f));       // outside in Z
        Assert.Equal(1f, box.Weight(new Vector3(119, 0, 0), 0f));        // no feather: full weight anywhere inside
    }

    static Node Gradient(float key, byte r, byte g, byte b, byte a) => new Arr(new F(key), new Arr(new U64(r), new U64(g), new U64(b), new U64(a)));

    static Node Environment(string name, int priority, float sunPower, float[] position, float[] size, float[] lowerAmbient) => new Struct("base/environmentregion",
        ("EnvironmentDescs", new List(
            new Struct("FogDesc",
                ("OutscatterColour", new List(Gradient(0, 60, 60, 69, 0), Gradient(1, 190, 163, 138, 69))),
                ("OutscatterRange", new F(250)), ("GroundFogHeight", new F(0)), ("GroundFogRange", new F(18))),
            new Struct("AmbientDesc", ("LowerAmbientColor", new Vec(lowerAmbient)), ("UpperAmbientColor", new Vec(0.5f, 0.5f, 0.75f)),
                ("AmbientSaturation", new F(0.8f)), ("SSAO_Multiplier", new F(1)), ("SSAO_DisableSSAO", new B(false))),
            new Struct("SunDesc", ("Colour", new Vec(1, 0.9f, 0.7f)), ("Power", new F(sunPower)), ("Rotation", new Vec(-45, 45, 0)), ("NumSunSplits", new I(4))),
            new Struct("HDRDesc", ("BrightThresholdMin", new F(0.4f)), ("BrightThresholdMax", new F(0.8f)), ("BloomIntensity", new F(1)), ("BloomPersistence", new F(0.25f))))),
        ("Position", new Vec(position)), ("Scale", new Vec(size)), ("Rotation", new Vec(0, 0, 0)),
        ("Enabled", new B(true)), ("Priority", new F(priority)), ("Name", new S(name)), ("Disabled", new B(false)));

    static Node Omni(string name, float[] position, float power, float start, float end, bool enabled = true, bool shadows = false) => new Struct("base/omnilight",
        ("Static", new B(false)), ("Power", new F(power)), ("CastShadows", new B(shadows)),
        ("Position", new Vec(position)), ("Rotation", new Vec(0, 0, 0)), ("Scale", new Vec(end, end, end)), ("Color", new Vec(1, 0.8f, 0.6f)),
        ("AttenStart", new F(start)), ("AttenEnd", new F(end)), ("Enabled", new B(enabled)), ("Name", new S(name)), ("Disabled", new B(false)));

    static byte[] LightsLayer() => Document(new Struct("RegionLayer", ("ID", new I(1)),
        ("Root", new Ref(new Struct("WorldGroup", ("Name", new S("")), ("Disabled", new B(false)),
            ("Objects", new List(
                new Struct("base/sunlight", ("NumSunSplits", new I(4)), ("FarSunDistance", new F(400)), ("Position", new Vec(0, 0, 35)),
                    ("Rotation", new Vec(-45, 45, 0)), ("Color", new Vec(1, 0.863f, 0.678f)), ("Power", new F(3)), ("Name", new S("SunLight")), ("Disabled", new B(false))),
                Environment("global", 1, 3, [200, -800, 0], [700, 700, 300], [0.188f, 0.204f, 0.376f]),
                Environment("bloom", 2, 4, [10, 10, 0], [20, 20, 20], [0.23f, 0.26f, 0.47f]),
                Omni("bounce", [1, 2, 3], 1.5f, 5, 12, shadows: true),
                Omni("off", [9, 9, 9], 1, 1, 2, enabled: false),
                new Struct("base/spotlight", ("Power", new F(5)), ("CastShadows", new B(false)), ("FOV", new F(75)),
                    ("Position", new Vec(4, 5, 17)), ("Rotation", new Vec(5, 0, 0)), ("Color", new Vec(0.46f, 0.76f, 0.79f)),
                    ("AttenStart", new F(13)), ("AttenEnd", new F(15)), ("Enabled", new B(true)), ("Name", new S("spot")), ("Disabled", new B(false))),
                // A disabled group hides its lights; an enabled one places its children relative to itself.
                new Struct("WorldGroup", ("Position", new Vec(100, 0, 0)), ("Rotation", new Vec(0, 0, 0)), ("Disabled", new B(true)),
                    ("ChildObjects", new List(Omni("hidden_child", [1, 0, 0], 1, 1, 2)))),
                new Struct("WorldGroup", ("Position", new Vec(0, 100, 0)), ("Rotation", new Vec(0, 0, 0)), ("Disabled", new B(false)),
                    ("ChildObjects", new List(Omni("nested", [1, 0, 0], 1, 1, 2)))))),
            ("ChildObjects", new List()))))));

    [Fact]
    public void Layer_gives_sun_environment_regions_and_placed_lights()
    {
        var lighting = MapLighting.Read(BodDocument.Read(LightsLayer()).Root);

        // Sun: rotation (-45, 45, 0) takes local -Z to (-0.5, -sqrt(0.5), -0.5): travelling toward -X, -Y and down.
        var sun = Assert.IsType<SunLight>(lighting.Sun);
        Assert.True(Vector3.Distance(new Vector3(-0.5f, -MathF.Sqrt(0.5f), -0.5f), sun.Direction) < 1e-5f, sun.Direction.ToString());
        Assert.Equal(new Vector3(1, 0.863f, 0.678f), sun.Colour);
        Assert.Equal(3f, sun.Power);

        // Two regions; the lowest priority number is the map-wide one, the highest wins where a point lies in both.
        Assert.Equal(2, lighting.Environments.Count);
        var global = Assert.IsType<MapEnvironment>(lighting.Global);
        Assert.Equal("global", global.Name);
        Assert.Equal("bloom", lighting.At(new Vector3(10, 10, 5))!.Name);
        Assert.Equal("global", lighting.At(new Vector3(100, 100, 5))!.Name);
        // Fog colours are RGBA bytes: the far stop is (190, 163, 138) with alpha 69/255.
        var fog = Assert.IsType<FogDesc>(global.Fog);
        Assert.Equal(250f, fog.Range);
        Assert.Equal(new Vector4(60, 60, 69, 0) / 255f, fog.NearColour);
        Assert.Equal(new Vector4(190, 163, 138, 69) / 255f, fog.FarColour);
        Assert.Equal(new Vector3(0.188f, 0.204f, 0.376f), global.Ambient!.Lower);
        Assert.Equal(0.8f, global.Ambient.Saturation);
        Assert.Equal(1f, global.Hdr!.BloomIntensity);
        Assert.Equal(3f, global.Sun!.Power);

        // Lights: the disabled light and the disabled group's child are skipped; the nested one moves with its group.
        Assert.Equal(["bounce", "spot", "nested"], lighting.Lights.Select(l => l.Name));
        var bounce = lighting.Lights[0];
        Assert.Equal((MapLightKind.Omni, 1.5f, 5f, 12f, true), (bounce.Kind, bounce.Power, bounce.AttenStart, bounce.AttenEnd, bounce.CastShadows));
        Assert.Equal(new Vector3(1, 2, 3), bounce.Position);
        var spot = lighting.Lights[1];
        Assert.Equal((MapLightKind.Spot, 75f), (spot.Kind, spot.FovDegrees));
        // A spot tilted 5 degrees about X still points almost straight down (local -Z).
        Assert.True(spot.Direction.Z < -0.99f && MathF.Abs(spot.Direction.Y) > 0.05f, spot.Direction.ToString());
        Assert.Equal(new Vector3(1, 100, 0), lighting.Lights[2].Position);
    }

    [Fact]
    public void Layer_without_lights_reads_as_empty_lighting()
    {
        var lighting = MapLighting.Read(BodDocument.Read(Document(new Struct("RegionLayer", ("ID", new I(1))))).Root);
        Assert.Null(lighting.Sun);
        Assert.Null(lighting.Global);
        Assert.Empty(lighting.Lights);
    }

    [Fact]
    public void Scene_loads_the_lights_layer_and_the_normal_specular_and_emissive_textures()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-lighting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data", "chunks"));
        try
        {
            var mesh = SyntheticDcmWriter.Build([new([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2], [new("stone", 0, 3, 0, 3)])]);
            byte[] Dds(byte tag) => [0x44, 0x44, 0x53, 0x20, tag];
            var definition = Document(new Struct("Object3D",
                ("Skeleton", new Ref(new Struct("Skeleton3D", ("Name", new S("root")), ("Children", new List())))),
                ("Visuals", new List(new Struct("StaticMeshVisual", ("MeshName", new S("stone_mesh")), ("MeshID", new I(0)), ("RefNode", new S("root")), ("LODLevel", new I(31)), ("Renderable", new B(true))))),
                ("Bodies", new List())));
            var layer = Document(new Struct("RegionLayer", ("ID", new I(1)), ("Root", new Ref(new Struct("WorldGroup", ("Name", new S("")), ("Disabled", new B(false)),
                ("Objects", new List(new Struct("StaticObject", ("Name", new S("a")), ("ObjectName", new S("stone_a")), ("Position", new Vec(0, 0, 0))))),
                ("ChildObjects", new List()))))));
            var material = Document(new Struct("Material", ("Name", new S("stone")), ("ShaderName", new S("diff_norm_spec")),
                ("Parameters", new List(Tex("DiffuseMap", "s_dif"), Tex("NormalMap", "s_nrm"), Tex("SpecularMap", "s_spc"), Tex("EmissiveMap", "s_emi"), Tex("AlphaMaskMap", "s_msk")))));
            File.WriteAllBytes(Path.Combine(dir, "data", "chunks", "mp_test__wrld.oppc"), SyntheticObpkWriter.Build(
            [
                new("Layer", "e_walls", layer),
                new("Layer", "l_lights", LightsLayer()),
                new("O3d", "stone_a", definition),
                new("dcm", "stone_mesh", mesh),
                new("bmat", "stone", material),
                new("dds", "s_dif", Dds(1)), new("dds", "s_nrm", Dds(2)), new("dds", "s_spc", Dds(3)), new("dds", "s_emi", Dds(4)), new("dds", "s_msk", Dds(5)),
            ]));

            var scene = MapScene.Load(dir, "mp_test");
            Assert.NotNull(scene.Lighting);
            Assert.Equal(3, scene.Lighting!.Lights.Count);
            // The blend mask is loaded too (pass 2); every named second-layer texture would be.
            Assert.Equal(["s_dif", "s_emi", "s_msk", "s_nrm", "s_spc"], scene.Textures.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(Dds(2), scene.Textures["s_nrm"]);
            // The lights layer's objects are still placed objects: none here (no StaticObject in it).
            Assert.Equal(["a"], scene.Objects.Select(o => o.Name));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Static_model_loads_normal_maps_and_infers_the_wargear_normal_by_name()
    {
        var mesh = SyntheticDcmWriter.Build([new([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2],
            [new("named", 0, 3, 0, 3), new("wargear", 0, 0, 0, 3)])]);
        byte[] Dds(byte tag) => [0x44, 0x44, 0x53, 0x20, tag];
        var archive = SyntheticObpkWriter.Build(
        [
            new("dcm", "piece", mesh),
            new("bmat", "named", Document(new Struct("Material", ("Name", new S("named")), ("ShaderName", new S("x")),
                ("Parameters", new List(Tex("DiffuseMap", "n_dif"), Tex("NormalMap", "n_nrm"), Tex("SpecularMap", "n_spc")))))),
            new("bmat", "wargear", Document(new Struct("Material", ("Name", new S("wargear")), ("ShaderName", new S("character_wargear")),
                ("Parameters", new List(Tex("DiffuseMap", "w_dif")))))),
            new("dds", "n_dif", Dds(1)), new("dds", "n_nrm", Dds(2)), new("dds", "n_spc", Dds(3)), new("dds", "w_dif", Dds(4)), new("dds", "piece_nrm", Dds(5)),
        ]);
        string path = Path.Combine(Path.GetTempPath(), "sm-staticmodel-" + Guid.NewGuid().ToString("N") + ".oppc");
        try
        {
            File.WriteAllBytes(path, archive);
            var model = StaticModel.Load(path, "piece");
            Assert.Equal("n_nrm", model.Surfaces[0].Material!.Textures["NormalMap"]);
            Assert.Equal("n_spc", model.Surfaces[0].Material!.Textures["SpecularMap"]);
            // The wargear material names no normal map; the package's "<mesh>_nrm" is used for it.
            Assert.Equal("piece_nrm", model.Surfaces[1].Material!.Textures["NormalMap"]);
            Assert.Equal(Dds(5), model.Textures["piece_nrm"]);
            Assert.Equal(Dds(2), model.Textures["n_nrm"]);
            // Once a renderer has uploaded them the raw bytes can be dropped; the material names stay.
            model.ReleaseTextures();
            Assert.Empty(model.Textures);
            Assert.Equal("piece_nrm", model.Surfaces[1].Material!.Textures["NormalMap"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Vertex_colour_is_bytes_b_g_r_a_and_white_means_no_tint_and_layer_one()
    {
        // Stored little-endian as B, G, R, A: 0xAARRGGBB read as a u32.
        var c = VertexColour.Unpack(0x80FF4020u);
        Assert.Equal(0xFF / 255f, c.X);          // R
        Assert.Equal(0x40 / 255f, c.Y);          // G
        Assert.Equal(0x20 / 255f, c.Z);          // B
        Assert.Equal(0x80 / 255f, c.W);          // A
        Assert.Equal(Vector4.One, VertexColour.Unpack(VertexColour.None));
        Assert.Equal(Vector4.Zero, VertexColour.Unpack(0));
    }

    [Theory]
    // vtxblend: weight of layer 2 = 1 - alpha.
    [InlineData(VertexColour.BlendKind.Vertex, 1f, 0.3f, 4f, 1f, 0f)]
    [InlineData(VertexColour.BlendKind.Vertex, 0f, 0.3f, 4f, 1f, 1f)]
    [InlineData(VertexColour.BlendKind.Vertex, 0.75f, 0.3f, 4f, 1f, 0.25f)]
    // advvtxbld: an exactly mid mask leaves 1 - alpha; contrast pushes values away from 0.5, clamped to 0..1.
    [InlineData(VertexColour.BlendKind.VertexWithNoise, 0.5f, 0.5f, 4f, 1f, 0.5f)]
    [InlineData(VertexColour.BlendKind.VertexWithNoise, 1f, 0.5f, 4f, 1f, 0f)]
    [InlineData(VertexColour.BlendKind.VertexWithNoise, 0f, 0.5f, 4f, 1f, 1f)]
    [InlineData(VertexColour.BlendKind.VertexWithNoise, 0.6f, 0.5f, 2f, 1f, 0.3f)]
    // noise: a bright mask raises the weight (alpha 1, mask 0.75, amplitude 1: ((0 + 0.25) - 0.5) * 4 + 0.5 = -0.5 -> 0).
    [InlineData(VertexColour.BlendKind.VertexWithNoise, 0.5f, 1f, 1f, 1f, 1f)]
    [InlineData(VertexColour.BlendKind.VertexWithNoise, 1f, 0.75f, 4f, 1f, 0f)]
    // mskblend: the mask is the weight.
    [InlineData(VertexColour.BlendKind.Mask, 0.2f, 0.6f, 4f, 1f, 0.6f)]
    public void Layer2_weight_follows_the_blend_kind(VertexColour.BlendKind kind, float alpha, float mask, float contrast, float amplitude, float expected) =>
        Assert.True(MathF.Abs(VertexColour.Layer2Weight(kind, alpha, mask, contrast, amplitude) - expected) < 1e-5f);

    [Fact]
    public void Gradient_stops_accept_float_and_int_colours_and_one_stop_serves_as_both_ends()
    {
        static Node Region(params Node[] stops) => new Struct("base/environmentregion",
            ("EnvironmentDescs", new List(new Struct("FogDesc", ("OutscatterColour", new List(stops)), ("OutscatterRange", new F(100))))),
            ("Position", new Vec(0, 0, 0)), ("Name", new S("r")), ("Disabled", new B(false)));
        byte[] Layer(Node region) => Document(new Struct("RegionLayer", ("ID", new I(1)),
            ("Root", new Ref(new Struct("WorldGroup", ("Name", new S("")), ("Disabled", new B(false)), ("Objects", new List(region)), ("ChildObjects", new List()))))));

        // Float colours (0 to 1) are taken as they are.
        var floats = MapLighting.Read(BodDocument.Read(Layer(Region(
            new Arr(new F(0), new Arr(new F(0.1f), new F(0.2f), new F(0.3f), new F(0f))), new Arr(new F(1), new Arr(new F(0.5f), new F(0.6f), new F(0.7f), new F(0.25f)))))).Root);
        var fog = floats.Environments.Single().Fog!;
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0f), fog.NearColour);
        Assert.Equal(new Vector4(0.5f, 0.6f, 0.7f, 0.25f), fog.FarColour);

        // Int colours are bytes (0 to 255).
        var ints = MapLighting.Read(BodDocument.Read(Layer(Region(
            new Arr(new F(0), new Arr(new I(255), new I(0), new I(51), new I(255)))))).Root);
        // One stop: it is both the near and the far colour.
        var one = ints.Environments.Single().Fog!;
        Assert.Equal(new Vector4(1f, 0f, 0.2f, 1f), one.NearColour);
        Assert.Equal(one.NearColour, one.FarColour);

        // A gradient stop without a four-value colour is skipped; with none left the region has no fog.
        var none = MapLighting.Read(BodDocument.Read(Layer(Region(new Arr(new F(0), new Arr(new F(1), new F(1)))))).Root);
        Assert.Null(none.Environments.Single().Fog);
    }

    [Fact]
    public void Region_without_scale_or_priority_defaults_to_a_unit_box_at_priority_zero()
    {
        var layer = Document(new Struct("RegionLayer", ("ID", new I(1)),
            ("Root", new Ref(new Struct("WorldGroup", ("Name", new S("")), ("Disabled", new B(false)),
                ("Objects", new List(new Struct("base/environmentregion", ("EnvironmentDescs", new List()), ("Position", new Vec(5, 6, 7)), ("Name", new S("bare")), ("Disabled", new B(false))))),
                ("ChildObjects", new List()))))));
        var region = MapLighting.Read(BodDocument.Read(layer).Root).Environments.Single();
        Assert.Equal((0, Vector3.One, new Vector3(5, 6, 7)), (region.Priority, region.Size, region.Centre));
        Assert.Null(region.Fog);
        Assert.Null(region.Ambient);
    }
}
