using System.Numerics;
using SmContent;
using SmFormats.Bod;
using static SmFormats.Bod.SyntheticBodWriter;

namespace SmFormats.Tests;

/// <summary>classic-maps-fidelity fixes 1 and 4: the static level decal definitions and an environment region's colour-grade description. Synthetic documents only.</summary>
public class MapDecalAndGradeTests
{
    [Fact]
    public void A_screen_space_decal_description_reads_its_textures_blend_and_size()
    {
        var bytes = Document(new Struct("ScreenSpaceDecalDescription", ("DiffuseMap", new S("d_dif")), ("NormalMap", new S("")), ("SpecularMap", new S("d_spc")),
            ("EmissiveMap", new S("d_emi")), ("AdditiveBlend", new B(true)), ("Color", new Vec(0.5f, 0.25f, 1f)), ("EmissiveColor", new Vec(1f, 1f, 1f)),
            ("Opacity", new F(0.75f)), ("NormalThreshold", new F(45f)), ("Width", new F(1f)), ("height", new F(2f)), ("Depth", new F(0.1f))));
        var def = MapDecalDefinition.Read("d", BodDocument.Read(bytes).Root)!;
        Assert.Equal(("d_dif", "", "d_spc", "d_emi", true), (def.DiffuseMap, def.NormalMap, def.SpecularMap, def.EmissiveMap, def.AdditiveBlend));
        Assert.Equal(new Vector3(0.5f, 0.25f, 1f), def.Colour);
        Assert.Equal((0.75f, 45f, 1f, 2f, 0.1f), (def.Opacity, def.NormalThreshold, def.Width, def.Height, def.Depth));
        Assert.Equal(["d_dif", "d_spc", "d_emi"], def.TextureNames);
    }

    [Fact]
    public void Another_struct_is_not_a_decal_description()
    {
        var bytes = Document(new Struct("Material", ("Name", new S("x"))));
        Assert.Null(MapDecalDefinition.Read("x", BodDocument.Read(bytes).Root));
    }

    [Fact]
    public void An_ortho_light_reads_its_box_mask_and_upward_direction_when_turned_over()
    {
        var light = new Struct("base/OrthoLight", ("Power", new F(6f)), ("Width", new F(10f)), ("Height", new F(20f)), ("MaskName", new S("light_mask_soft_square_dif")),
            ("Position", new Vec(1f, 2f, 3f)), ("Rotation", new Vec(-180f, 0f, 0f)), ("Scale", new Vec(20f, 20f, 20f)), ("Color", new Vec(1f, 0.5f, 0f)), ("AttenEnd", new F(20f)));
        var off = new Struct("base/OrthoLight", ("Enabled", new B(false)), ("MaskName", new S("m")), ("Position", new Vec(0f, 0f, 0f)));
        var maskless = new Struct("base/OrthoLight", ("MaskName", new S("")), ("Position", new Vec(0f, 0f, 0f)));
        var lighting = MapLighting.Read(BodDocument.Read(Document(new Struct("RegionLayer", ("Objects", new List(light, off, maskless))))).Root);
        var o = Assert.Single(lighting.OrthoLights);
        Assert.Equal(("light_mask_soft_square_dif", 10f, 20f, 20f, 6f), (o.MaskName, o.Width, o.Height, o.Depth, o.Power));
        Assert.Equal(new Vector3(1f, 2f, 3f), o.Position);
        Assert.True(Vector3.Distance(o.Forward, Vector3.UnitZ) < 1e-4f);   // local -Z turned half a circle about X: up
        Assert.True(Vector3.Distance(o.Right, Vector3.UnitX) < 1e-4f);
        Assert.Empty(MapLighting.Combine([lighting, new MapLighting()]).OrthoLights.Skip(1));
    }

    [Fact]
    public void An_environment_region_reads_its_colour_grade()
    {
        var region = new Struct("base/environmentregion", ("Name", new S("r")), ("Priority", new I(1)), ("Scale", new Vec(600f, 600f, 300f)), ("Position", new Vec(0f, 0f, 0f)),
            ("EnvironmentDescs", new List(new Struct("ColourGradeEffectDesc", ("BlendDuration", new F(3f)), ("PackageName", new S("cg")), ("MaterialName", new S("cg"))))));
        var lighting = MapLighting.Read(BodDocument.Read(Document(new Struct("RegionLayer", ("Objects", new List(region))))).Root);
        var grade = Assert.Single(lighting.Environments).Grade;
        Assert.Equal(new ColourGradeDesc("cg", 3f), grade);
    }

    [Fact]
    public void A_region_without_a_grade_has_none()
    {
        var region = new Struct("base/environmentregion", ("Name", new S("r")), ("Priority", new I(1)), ("Scale", new Vec(1f, 1f, 1f)), ("Position", new Vec(0f, 0f, 0f)), ("EnvironmentDescs", new List()));
        var lighting = MapLighting.Read(BodDocument.Read(Document(new Struct("RegionLayer", ("Objects", new List(region))))).Root);
        Assert.Null(Assert.Single(lighting.Environments).Grade);
    }
}
