using System.Numerics;
using SmSim.Extras.Musou.Ashfall;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// The Ashfall kit collision fit (draft 4) on synthetic triangles only (the owner's models are not on CI): solid boxes, openings kept open, the cap, determinism,
/// and the committed per-model box sets against the catalogue's raw bounds.
/// </summary>
public class AshfallKitFitTests
{
    /// <summary>Triangles of an axis-aligned box (12 triangles, both windings irrelevant to the fit).</summary>
    static void AddBox(List<Vector3> pos, List<int> idx, Vector3 min, Vector3 max)
    {
        int b = pos.Count;
        for (int k = 0; k < 8; k++) pos.Add(new((k & 1) == 0 ? min.X : max.X, (k & 2) == 0 ? min.Y : max.Y, (k & 4) == 0 ? min.Z : max.Z));
        foreach (var q in new[] { (0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5) })
        {
            idx.AddRange([b + q.Item1, b + q.Item2, b + q.Item3, b + q.Item1, b + q.Item3, b + q.Item4]);
        }
    }

    static AshfallKitFitResult FitBoxes(IEnumerable<(Vector3 Min, Vector3 Max)> solids, int res, int maxBoxes = 32, float fill = 0.15f)
    {
        var pos = new List<Vector3>(); var idx = new List<int>();
        foreach (var (mn, mx) in solids) AddBox(pos, idx, mn, mx);
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var p in pos) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        return AshfallKitFit.Fit(pos, idx, lo, hi, res, maxBoxes, fill, res);
    }

    static bool Covered(AshfallKitFitResult r, Vector3 p) => r.Boxes.Any(b => p.X >= b.Min.X && p.X <= b.Max.X && p.Y >= b.Min.Y && p.Y <= b.Max.Y && p.Z >= b.Min.Z && p.Z <= b.Max.Z);

    [Fact]
    public void A_cube_is_one_box_with_full_overlap()
    {
        var r = FitBoxes([(Vector3.Zero, new Vector3(4, 4, 4))], 8);
        Assert.Single(r.Boxes);
        Assert.Equal(1.0, r.Metrics.Iou, 6);
        Assert.Equal(Vector3.Zero, r.Boxes[0].Min);
        Assert.Equal(new Vector3(4, 4, 4), r.Boxes[0].Max);
    }

    [Fact]
    public void An_arch_keeps_its_opening()
    {
        // 9 wide, 2 deep, 7 tall: piers 3 wide, lintel 2 tall over a 3 wide opening 5 tall.
        var r = FitBoxes([(new(0, 0, 0), new(3, 7, 2)), (new(6, 0, 0), new(9, 7, 2)), (new(3, 5, 0), new(6, 7, 2))], 9);
        Assert.False(Covered(r, new Vector3(4.5f, 2.5f, 1f)), "a point inside the opening is in a box");
        Assert.True(Covered(r, new Vector3(1.5f, 3.5f, 1f)));
        Assert.True(Covered(r, new Vector3(4.5f, 6f, 1f)));
        // Even when the cap asks for fewer boxes, the opening survives.
        var capped = FitBoxes([(new(0, 0, 0), new(3, 7, 2)), (new(6, 0, 0), new(9, 7, 2)), (new(3, 5, 0), new(6, 7, 2))], 9, maxBoxes: 2);
        Assert.False(Covered(capped, new Vector3(4.5f, 2.5f, 1f)));
    }

    [Fact]
    public void An_L_shape_is_two_boxes()
    {
        var r = FitBoxes([(new(0, 0, 0), new(8, 4, 4)), (new(0, 4, 0), new(4, 8, 4))], 8);
        Assert.Equal(2, r.Boxes.Count);
        Assert.Equal(1.0, r.Metrics.Iou, 6);
    }

    [Fact]
    public void A_hollow_closed_box_is_filled_solid()
    {
        var r = FitBoxes([(Vector3.Zero, new Vector3(6, 6, 6))], 6);
        // The six faces alone leave an interior; the fill makes it one solid box.
        Assert.Single(r.Boxes);
        Assert.Equal(6 * 6 * 6, r.Metrics.SolidCells);
    }

    [Fact]
    public void A_stair_keeps_one_step_tall_boxes()
    {
        // Four steps, each 2 deep, rising 2 each, 8 wide.
        // Solids are overlapping prisms from the floor up to each tread; the union is the stair.
        var solids = Enumerable.Range(0, 4).Select(i => (new Vector3(0, 0, i * 2), new Vector3(8, (i + 1) * 2, 8))).ToList();
        var r = FitBoxes(solids, 8, fill: 0.15f);
        Assert.Equal(4, r.Boxes.Count);
        // No box over the open space above the lower treads: the point above step 1's tread, in step 3's depth, is empty.
        Assert.False(Covered(r, new Vector3(4f, 3.5f, 1f)));
        Assert.True(Covered(r, new Vector3(4f, 3.5f, 5f)));
        Assert.All(r.Boxes, b => Assert.True(b.Max.Y - b.Min.Y <= 8f));
        Assert.True(r.Metrics.Iou > 0.99);
    }

    [Fact]
    public void The_same_input_gives_the_same_boxes()
    {
        var solids = new[] { (new Vector3(0, 0, 0), new Vector3(5, 3, 2)), (new Vector3(1, 3, 0), new Vector3(3, 6, 2)), (new Vector3(6, 0, 1), new Vector3(9, 2, 4)) };
        var a = FitBoxes(solids, 18); var b = FitBoxes(solids, 18);
        Assert.Equal(a.Boxes, b.Boxes);
        Assert.Equal(a.Metrics.Iou, b.Metrics.Iou);
    }

    [Fact]
    public void The_cap_is_respected_when_merging_is_allowed()
    {
        // Eight bars 3 wide and 2 tall with 2 cell gaps (too low to be an opening): merging neighbours adds some empty volume.
        var bars = Enumerable.Range(0, 8).Select(i => (new Vector3(i * 5, 0, 0), new Vector3(i * 5 + 3, 2, 3))).ToList();
        var uncapped = FitBoxes(bars, 38, maxBoxes: 32);
        Assert.Equal(8, uncapped.Boxes.Count);
        var capped = FitBoxes(bars, 38, maxBoxes: 4, fill: 0.5f);
        Assert.True(capped.Boxes.Count <= 4, $"{capped.Boxes.Count} boxes");
        Assert.True(capped.Metrics.CapReached);
        Assert.False(capped.Metrics.OverCap);
    }

    [Fact]
    public void Committed_collision_covers_every_kit_model_inside_its_bounds()
    {
        foreach (var m in AshfallKit.Models)
        {
            var boxes = AshfallKitCollision.Boxes(m.Id);
            Assert.NotEmpty(boxes);
            float cell = AshfallKitCollision.Metrics(m.Id).CellSize;
            var lo = m.RawMin - new Vector3(cell); var hi = m.RawMax + new Vector3(cell);
            foreach (var (mn, mx) in boxes)
            {
                Assert.True(mn.X <= mx.X && mn.Y <= mx.Y && mn.Z <= mx.Z, $"{m.Id}: inverted box");
                Assert.True(mn.X >= lo.X && mn.Y >= lo.Y && mn.Z >= lo.Z && mx.X <= hi.X && mx.Y <= hi.Y && mx.Z <= hi.Z, $"{m.Id}: box outside the raw bounds");
            }
        }
        Assert.Equal(AshfallKit.Models.Count, AshfallKitCollision.Ids.Count());
    }
}
