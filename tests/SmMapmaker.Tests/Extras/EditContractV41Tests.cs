using System.Numerics;
using SmMapmaker;
using SmSim;
using Xunit;

namespace SmMapmaker.Tests;

/// <summary>Contract v4.1: leaning-door push-out, tilted caps in the outline, raising a partly high shape, a NaN radius.</summary>
public sealed class EditContractV41Tests
{
    static MeshVolume Box(Vector3 size, Vector3 at, Func<int, Vector3, Vector3>? bend = null)
    {
        var (v, i, _) = EditMesh.Stock(MeshTopology.Box).Triangulate();
        return new MeshVolume("box", v.Select((p, k) => { var q = p * size + at; return bend is null ? q : bend(k, q); }).ToArray(), i);
    }

    [Fact]
    public void A_leaning_door_pushes_a_capsule_clear_of_its_leaning_face_by_the_radius()
    {
        // 6 x 1 x 3, its top sheared 0.3 m toward +Y: the +Y face leans out from y = 0.5 at the foot to 0.8 at the top
        var door = Box(new Vector3(6, 1, 3), Vector3.Zero, (k, p) => (k & 4) != 0 ? p + new Vector3(0, 0.3f, 0) : p);
        foreach (var vol in new[] { door, door.ReachingAbove(25f) })
        {
            var p = vol.PushOut(new Vector3(0, 0.45f, 0), 0.4f, 1.9f);
            Assert.Equal(0.8f + 0.41f, p.Y, 3);   // past the top edge, not 0.41 m past the foot (which left 0.11 m to the leaning face)
            Assert.Equal(0f, p.X, 4);
            Assert.False(vol.Crosses(p, p + new Vector3(0, 0, 1.9f)));
        }
    }

    [Fact]
    public void A_tilted_edited_capture_point_has_only_its_real_edges_as_outline()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        Assert.True(s.Apply("""{"op":"place","piece":{"kind":"capture-point"},"transform":{"position":[13.37,-7.11,0.73],"rotation":[20,0,37],"scale":[8,6,4]}}""").Ok);
        Assert.True(s.Apply("""{"op":"edit-mesh","id":"o1","mode":"face","ids":[1],"move":[0,0,0.5]}""").Ok);
        var vol = CustomMap.Compile(s.Document).Volumes["mkr_objective_1"];
        Assert.Equal(48, vol.FeatureEdges().Count);   // 16 bottom, 16 top, 16 upright; no cap fan diagonals
    }

    [Fact]
    public void A_shape_whose_highest_corner_reaches_the_height_still_raises_its_lower_parts()
    {
        var door = Box(new Vector3(6, 1, 3), Vector3.Zero, (k, p) => k == 7 ? p with { Z = 30 } : p);   // one top corner at 30 m, the others at 3 m
        var tall = door.ReachingAbove(25f);
        Assert.NotSame(door, tall);
        Assert.Equal(30f, tall.Max.Z);
        Assert.True(tall.Contains(new Vector3(-2.9f, -0.4f, 20)));   // above a low corner
        Assert.Same(tall, tall.ReachingAbove(25f));                   // already raised that high
        var flat = Box(new Vector3(6, 1, 30), Vector3.Zero);
        Assert.Same(flat, flat.ReachingAbove(25f));                   // every part already reaches it
    }

    [Fact]
    public void A_nan_radius_or_height_is_read_as_none()
    {
        var door = Box(new Vector3(6, 1, 3), Vector3.Zero);
        var p = door.PushOut(new Vector3(0, 0.2f, 1), float.NaN, float.NaN);
        Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y));
        Assert.Equal(0.51f, p.Y, 3);
        var q = door.PushOut(new Vector3(0, 0.2f, 1), 0.4f, float.NaN);
        Assert.Equal(0.91f, q.Y, 3);
    }
}
