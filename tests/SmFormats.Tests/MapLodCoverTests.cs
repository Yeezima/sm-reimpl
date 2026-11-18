using System.Numerics;
using SmContent;
using SmFormats.Bod;
using SmFormats.Dcm;
using static SmFormats.Bod.SyntheticBodWriter;

namespace SmFormats.Tests;

/// <summary>
/// hab-center-floor (2026-11-19): the LOD-suffix family rule dropped Hab Center's floor sections (<c>_LodL</c> next to a small <c>_LODM</c> patch); <see cref="MapScene.DrawnParts(ObjectDefinition, bool, bool)"/>
/// draws a dropped member again when the better members' box does not cover it. Synthetic data only (a built scene with generated meshes).
/// </summary>
public class MapLodCoverTests
{
    /// <summary>A block whose positions span the box from the origin to <paramref name="size"/> (a flat triangle pair is enough: only the box matters).</summary>
    static DcmMesh Mesh(params (float X, float Y, float Z)[] sizes)
    {
        var blocks = sizes.Select(s => new SyntheticDcmWriter.Block(
            [new(0, 0, 0), new(s.X, 0, 0), new(0, s.Y, 0), new(s.X, s.Y, s.Z)], [0, 1, 2, 1, 3, 2], [new("stone", 0, 6, 0, 4)])).ToList();
        return DcmMesh.Read(SyntheticDcmWriter.Build(blocks));
    }

    static VisualPart Part(string node, int block, int mask, Vector3? at = null) => new("m", block, node, Matrix4x4.CreateTranslation(at ?? Vector3.Zero), mask, true);

    static MapScene Scene(DcmMesh mesh) => new()
    {
        Map = "mp_test", Objects = [], Definitions = new Dictionary<string, ObjectDefinition>(), Meshes = new Dictionary<string, DcmMesh> { ["m"] = mesh },
        Materials = new Dictionary<string, BmatMaterial>(), Textures = new Dictionary<string, byte[]>(), Notes = [],
    };

    [Fact]
    public void A_lower_member_the_better_ones_do_not_cover_is_drawn_again_and_a_coarser_copy_is_not()
    {
        // Hab Center: Floor_LodL is 58 x 90 x 26 m, Floor_LODM a 38 x 23 x 7 m patch (4.5 % of it); Pipe_LODL is a coarser copy of Pipe_LODH (same box within a metre).
        var scene = Scene(Mesh((58, 90, 26), (38, 23, 7), (10, 10, 1), (10.5f, 10.2f, 1.1f)));
        var def = new ObjectDefinition("hab", [Part("Floor_LodL", 0, 31), Part("Floor_LODM", 1, 7), Part("Pipe_LODH", 2, 7), Part("Pipe_LODL", 3, 31)], []);

        Assert.Equal(["Floor_LODM", "Pipe_LODH"], def.HighestLod().Select(v => v.RefNode));   // the family rule alone: the floor is gone
        var drawn = scene.DrawnParts(def, oldRule: false, keepUncovered: true).Select(v => v.RefNode).ToList();
        Assert.Contains("Floor_LodL", drawn);        // the floor is back
        Assert.Contains("Floor_LODM", drawn);
        Assert.Contains("Pipe_LODH", drawn);
        Assert.DoesNotContain("Pipe_LODL", drawn);   // still one member of a real LOD pair
    }

    [Fact]
    public void Without_the_switch_or_with_the_old_rule_the_parts_are_the_earlier_ones()
    {
        var scene = Scene(Mesh((58, 90, 26), (38, 23, 7)));
        var def = new ObjectDefinition("hab", [Part("Floor_LodL", 0, 31), Part("Floor_LODM", 1, 7)], []);
        Assert.Equal(["Floor_LODM"], scene.DrawnParts(def, oldRule: false, keepUncovered: false).Select(v => v.RefNode));
        Assert.Equal(def.NearestLod().Select(v => v.RefNode), scene.DrawnParts(def, oldRule: true, keepUncovered: true).Select(v => v.RefNode));
    }

    [Fact]
    public void An_added_member_covers_the_next_coarser_copy_and_the_parts_keep_the_definitions_order()
    {
        // H is a small patch; M the whole piece; L a coarser copy of M: M is added, L stays dropped.
        var scene = Scene(Mesh((6, 6, 2), (40, 40, 10), (40.5f, 40.2f, 10.1f)));
        var def = new ObjectDefinition("d", [Part("Wing_LodL", 2, 31), Part("Wing_LodM", 1, 15), Part("Wing_LodH", 0, 7)], []);
        Assert.Equal(["Wing_LodM", "Wing_LodH"], scene.DrawnParts(def, false, true).Select(v => v.RefNode));
    }

    [Fact]
    public void A_dropped_member_whose_mesh_is_missing_or_a_better_member_without_a_mesh_stays_dropped()
    {
        var scene = Scene(Mesh((58, 90, 26)));
        var def = new ObjectDefinition("d", [Part("Floor_LodL", 0, 31), new VisualPart("nowhere", 0, "Floor_LODM", Matrix4x4.Identity, 7, true)], []);
        Assert.Equal(["Floor_LODM"], scene.DrawnParts(def, false, true).Select(v => v.RefNode));
    }

    [Fact]
    public void The_box_of_a_part_follows_its_node_transform()
    {
        // The same two meshes, but the near patch hangs 100 m away: the far piece is not covered by it either way; a copy at the same place is.
        var scene = Scene(Mesh((10, 10, 1), (10, 10, 1)));
        var apart = new ObjectDefinition("d", [Part("Rib_LODH", 0, 7), Part("Rib_LODL", 1, 31, new Vector3(100, 0, 0))], []);
        var together = new ObjectDefinition("d", [Part("Rib_LODH", 0, 7, new Vector3(5, 5, 0)), Part("Rib_LODL", 1, 31, new Vector3(5.2f, 5, 0))], []);
        Assert.Equal(["Rib_LODL", "Rib_LODH"], scene.DrawnParts(apart, false, true).Select(v => v.RefNode).OrderByDescending(n => n));
        Assert.Equal(["Rib_LODH"], scene.DrawnParts(together, false, true).Select(v => v.RefNode));
    }
}
