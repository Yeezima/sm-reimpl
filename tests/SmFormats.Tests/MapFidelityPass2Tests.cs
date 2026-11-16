using System.Numerics;
using SmContent;
using SmFormats.Bod;
using static SmFormats.Bod.SyntheticBodWriter;

namespace SmFormats.Tests;

/// <summary>classic-maps-fidelity pass 2 test aids: the highest-LOD family rule and the extended lighting reading (both default off; synthetic data only).</summary>
public class MapFidelityPass2Tests
{
    static VisualPart Part(string node, int mask) => new("mesh_" + node, 0, node, Matrix4x4.Identity, mask, true);

    [Fact]
    public void HighestLod_keeps_a_LodL_part_that_has_no_other_family_member_and_one_member_of_each_family()
    {
        var def = new ObjectDefinition("hab", [
            Part("Walls_B_LodL", 31), Part("Walls_C_LodL", 31),           // real walls named LodL: no other member
            Part("Stairs_LodH", 15), Part("Stairs_LodM", 7),               // one family: only H stays
            Part("Pipe_LODH", 31), Part("Pipe_LODL", 15),
            Part("Plain", 31),
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
    public void ExtendedReading_is_on_by_default_and_reads_capitalised_light_classes_unless_switched_off()
    {
        var bytes = Document(new Struct("WorldGroup", ("Objects", new List(
            new Struct("base/OmniLight", ("Position", new Vec(1f, 2f, 3f)), ("Rotation", new Vec(0f, 0f, 0f)), ("Scale", new Vec(10f, 10f, 10f)),
                ("Power", new F(2f)), ("Color", new Vec(1f, 0.5f, 0.2f)), ("AttenStart", new F(1f)), ("AttenEnd", new F(5f)))))));
        var root = BodDocument.Read(bytes).Root;
        Assert.True(MapLighting.ExtendedReading);
        try
        {
            MapLighting.ExtendedReading = false;
            Assert.Empty(MapLighting.Read(root).Lights);
            MapLighting.ExtendedReading = true;
            var light = Assert.Single(MapLighting.Read(root).Lights);
            Assert.Equal(new Vector3(1f, 2f, 3f), light.Position);
        }
        finally { MapLighting.ExtendedReading = true; }
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
