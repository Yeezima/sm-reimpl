using System.Numerics;
using SmContent;
using SmFormats.Bod;
using static SmFormats.Bod.SyntheticBodWriter;

namespace SmFormats.Tests;

/// <summary>classic-maps-fidelity: the LOD-suffix family rule (the default) and the extended lighting reading (the default; passed as a parameter, no shared state). Synthetic data only.</summary>
public class MapFidelityPass2Tests
{
    static VisualPart Part(string node, int mask = 31) => new("mesh_" + node, 0, node, Matrix4x4.Identity, mask, true);
    static List<string> Highest(params string[] nodes) => new ObjectDefinition("d", nodes.Select(n => Part(n)).ToList(), []).HighestLod().Select(v => v.RefNode).ToList();

    [Fact]
    public void HighestLod_keeps_a_LodL_part_that_has_no_other_family_member_and_one_member_of_each_family()
    {
        var def = new ObjectDefinition("hab", [
            Part("Walls_B_LodL"), Part("Walls_C_LodL"),           // real walls named LodL: no other member
            Part("Stairs_LodH", 15), Part("Stairs_LodM", 7),       // one family: only H stays
            Part("Pipe_LODH"), Part("Pipe_LODL", 15),
            Part("Plain"),
        ], []);
        var current = def.NearestLod().Select(v => v.RefNode).ToList();
        var highest = def.HighestLod().Select(v => v.RefNode).ToList();
        Assert.DoesNotContain("Walls_B_LodL", current);      // before: the name rule drops them
        Assert.Contains("Stairs_LodM", current);              // before: H and M together
        Assert.Contains("Walls_B_LodL", highest);
        Assert.Contains("Walls_C_LodL", highest);
        Assert.Contains("Stairs_LodH", highest);
        Assert.DoesNotContain("Stairs_LodM", highest);
        Assert.Contains("Pipe_LODH", highest);
        Assert.DoesNotContain("Pipe_LODL", highest);
        Assert.Contains("Plain", highest);
    }

    [Fact]
    public void Families_ignore_case_and_rank_H_then_M_then_C_then_L()
    {
        Assert.Equal(["Roof_LodH"], Highest("Roof_LODC", "Roof_lodh", "Roof_LodM", "Roof_LodL").Select(n => n == "Roof_lodh" ? "Roof_LodH" : n));
        Assert.Equal(["Roof_LodM"], Highest("Roof_LODC", "Roof_LodM", "Roof_LodL"));
        Assert.Equal(["Roof_LODC"], Highest("Roof_LODC", "Roof_LodL"));
        Assert.Equal(["Roof_LodL"], Highest("Roof_LodL"));
    }

    [Fact]
    public void Bare_nodes_without_a_stem_form_one_family()
    {
        Assert.Equal(["lodh"], Highest("lodh", "lodl"));            // the empty stem, as in MapSceneTests
        Assert.Equal(["LODL"], Highest("LODL"));
    }

    [Theory]
    [InlineData("Enviro_Light_small_cyan_LODH01", "Enviro_Light_small_cyan_LODL")]   // a number after the kind
    [InlineData("Pipe_LODH2", "Pipe_lod_l")]                                         // an underscore inside the suffix
    [InlineData("Metal_Scraps_Floor_B_LODH", "Metal_Scraps_Floor_B_LOD")]            // a bare _LOD is the low one
    [InlineData("Patch_LODH", "Patch_LODL_b")]                                       // a variant tag after the kind
    [InlineData("Fan_Body_LODH", "Fan_Body_LODLB")]
    public void Numbered_underscored_bare_and_tagged_variants_join_their_family(string high, string low)
    {
        Assert.Equal([high], Highest(high, low));
    }

    [Fact]
    public void Variant_tags_keep_equal_kinds_together()
    {
        Assert.Equal(["Body_LODH_A", "Body_LODH_B", "Body_LODH_interior_B"], Highest("Body_LODH_A", "Body_LODH_B", "Body_LODH_interior_B"));
    }

    [Fact]
    public void LodFamily_reads_stem_and_rank_and_leaves_other_names_alone()
    {
        Assert.Equal(("pipe", 0), ObjectDefinition.LodFamily("Pipe_LODH"));
        Assert.Equal(("", 3), ObjectDefinition.LodFamily("LODL"));
        Assert.Equal(("pipe", 3), ObjectDefinition.LodFamily("Pipe_LOD"));
        Assert.Null(ObjectDefinition.LodFamily("Golden_Skull"));
        Assert.Null(ObjectDefinition.LodFamily("Brick_wall_A"));
    }

    static byte[] OneLight(string cls) => Document(new Struct("WorldGroup", ("Objects", new List(
        new Struct(cls, ("Position", new Vec(1f, 2f, 3f)), ("Rotation", new Vec(0f, 0f, 0f)), ("Scale", new Vec(10f, 10f, 10f)),
            ("Power", new F(2f)), ("Color", new Vec(1f, 0.5f, 0.2f)), ("AttenStart", new F(1f)), ("AttenEnd", new F(5f)), ("FOV", new F(45f)))))));

    [Fact]
    public void Extended_reading_is_the_default_and_reads_capitalised_light_classes_unless_switched_off()
    {
        var root = BodDocument.Read(OneLight("base/OmniLight")).Root;
        Assert.Empty(MapLighting.Read(root, extended: false).Lights);
        var light = Assert.Single(MapLighting.Read(root).Lights);
        Assert.Equal(new Vector3(1f, 2f, 3f), light.Position);
        Assert.Equal(MapLightKind.Omni, light.Kind);
    }

    [Theory]
    [InlineData("base/SpotLight")]
    [InlineData("base/spotlight")]
    public void A_spot_light_keeps_its_cone_in_either_spelling(string cls)
    {
        var light = Assert.Single(MapLighting.Read(BodDocument.Read(OneLight(cls)).Root).Lights);
        Assert.Equal(MapLightKind.Spot, light.Kind);
        Assert.Equal(45f, light.FovDegrees);
    }

    [Fact]
    public void Combine_joins_the_regions_and_lights_and_keeps_the_first_sun()
    {
        var a = new MapLighting { Sun = new SunLight(-Vector3.UnitZ, Vector3.One, 1f, 4, 100f) };
        var b = new MapLighting { Sun = new SunLight(Vector3.UnitX, Vector3.One, 2f, 4, 100f), Environments = [new MapEnvironment("e", 1, Vector3.Zero, Vector3.One, null, null, null, null)] };
        var c = MapLighting.Combine([a, b]);
        Assert.Equal(1f, c.Sun!.Power);
        Assert.Single(c.Environments);
    }
}
