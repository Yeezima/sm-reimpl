using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Ashfall;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// The kit's collision quality gate (draft 6, docs/musou-ashfall-graybox.md "Draft 6: collision quality gate"): every used model's committed fit must pass the thresholds in <see cref="AshfallKitFit.Gate"/>,
/// so a model added without a good fit fails here. The measuring itself is checked on synthetic meshes only (no asset bytes).
/// </summary>
public class AshfallKitCollisionGateTests(ITestOutputHelper output)
{
    static void AddBox(List<Vector3> pos, List<int> idx, Vector3 min, Vector3 max)
    {
        int b = pos.Count;
        for (int k = 0; k < 8; k++) pos.Add(new((k & 1) == 0 ? min.X : max.X, (k & 2) == 0 ? min.Y : max.Y, (k & 4) == 0 ? min.Z : max.Z));
        foreach (var q in new[] { (0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5) })
            idx.AddRange([b + q.Item1, b + q.Item2, b + q.Item3, b + q.Item1, b + q.Item3, b + q.Item4]);
    }

    static (List<Vector3> Pos, List<int> Idx, Vector3 Min, Vector3 Max) Mesh(IEnumerable<(Vector3 Min, Vector3 Max)> solids)
    {
        var pos = new List<Vector3>(); var idx = new List<int>();
        foreach (var (mn, mx) in solids) AddBox(pos, idx, mn, mx);
        return (pos, idx, pos.Aggregate(Vector3.Min), pos.Aggregate(Vector3.Max));
    }

    /// <summary>An open frame 8 wide, 8 tall, 1 deep: two posts and a lintel (a doorway 6 x 7 under it), the shape that draft 5 filled on AC-010.</summary>
    static readonly (Vector3 Min, Vector3 Max)[] Frame = [(new(0, 0, 0), new(1, 8, 1)), (new(7, 0, 0), new(8, 8, 1)), (new(1, 7, 0), new(7, 8, 1))];

    [Fact]
    public void Every_used_model_s_committed_collision_passes_the_gate()
    {
        var failures = new List<string>();
        foreach (var m in AshfallKit.Models)
        {
            if (AshfallKitFit.Gate.ReportOnlyIds.Contains(m.Id)) continue;
            var q = AshfallKitCollision.Quality(m.Id);
            Assert.Equal(AshfallKitFit.MeasureResolution, q.FineResolution);
            Assert.True(q.FineSolidCells > 0, $"{m.Id}: not measured");
            foreach (var f in q.Failures()) failures.Add($"{m.Id}: {f}");
        }
        Assert.True(failures.Count == 0, "collision fits that fail the gate (rerun smtool ashfall-kit-collision and fix the fit):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_Meshy_expansion_model_has_a_committed_fit_that_passes_the_gate()
    {
        // Draft 6, the 36 models of 2026-11-05 (AC-041..076): none is report-only, so each must be fitted, measured and pass; the traversal pieces at the walkable 48 cells.
        var expansion = AshfallKit.Models.Where(m => m.File.StartsWith(AshfallKit.ExpansionFolder + "/", StringComparison.Ordinal)).ToList();
        Assert.Equal(36, expansion.Count);
        var ids = AshfallKitCollision.Ids.ToHashSet();
        foreach (var m in expansion)
        {
            Assert.Contains(m.Id, ids);
            Assert.DoesNotContain(m.Id, AshfallKitFit.Gate.ReportOnlyIds);
            var q = AshfallKitCollision.Quality(m.Id);
            Assert.True(q.FineSolidCells > 0, $"{m.Id}: not measured");
            Assert.True(q.Passes, $"{m.Id}: {string.Join("; ", q.Failures())}");
            if (AshfallKitFit.WalkableIds.Contains(m.Id)) Assert.Equal(AshfallKitFit.WalkableResolution, AshfallKitCollision.Settings(m.Id).Resolution);
        }
    }

    [Fact]
    public void At_their_design_scale_the_walkable_expansion_models_keep_the_step_but_the_medium_platform()
    {
        // The fit's cell is the height quantum of its boxes: scaled by the design scale it must stay within the 0.4 m step. AC-050 (08M, 0.25 m risers give 10.6 m/unit) is over
        // (0.42 m): the placement draws it at most at AshfallKitFit.MaxWalkScale (about 10.1 m/unit) or relies on its ramp wedge.
        var over = AshfallKit.Models.Where(m => m.DesignScale > 0f && AshfallKitFit.WalkableIds.Contains(m.Id))
            .Where(m => AshfallKitCollision.Metrics(m.Id).CellSize * m.DesignScale > AshfallKitFit.StepHeight + 1e-3f).Select(m => m.Id);
        Assert.Equal(["AC-050"], over);
        Assert.True(AshfallKitFit.MaxWalkScale(AshfallKitCollision.Metrics("AC-050").CellSize) > 10f);
    }

    [Fact]
    public void The_level_s_collision_stays_within_its_triangle_budget()
    {
        // Draft 5 had 181,772 collision triangles (v5: 69,202); the gated refit may cost some, not a blow-up. Ours: 220 k.
        var level = AshfallMap.BuildDraft5().Level;
        int triangles = level.Parts.Sum(p => p.Corners.Length / 3);
        output.WriteLine($"collision triangles: {triangles}, parts: {level.Parts.Count}");
        Assert.InRange(triangles, 69_202, 220_000);
    }

    [Fact]
    public void The_gate_names_the_report_only_models_and_its_thresholds_are_sane()
    {
        Assert.Equal(["AC-015", "AC-016", "AC-026"], AshfallKitFit.Gate.ReportOnlyIds);
        Assert.All(AshfallKitFit.Gate.ReportOnlyIds, id => Assert.True(AshfallKit.Get(id).Review));
        Assert.InRange(AshfallKitFit.Gate.MaxFarAirPercent, 0.1, 10);
        Assert.InRange(AshfallKitFit.Gate.MaxPhantomSilhouettePercent, 0.1, 5);
        Assert.InRange(AshfallKitFit.Gate.MaxHoleSilhouettePercent, 0.1, 5);
        Assert.InRange(AshfallKitFit.Gate.MaxPocketPercent, 0.5, 10);
        Assert.InRange(AshfallKitFit.Gate.MaxDeepAirViewPercent, 1, 20);
    }

    [Fact]
    public void Boxes_that_match_the_mesh_pass()
    {
        var (pos, idx, lo, hi) = Mesh(Frame);
        var q = AshfallKitFit.Measure(pos, idx, lo, hi, Frame, 8f / 48f);
        Assert.True(q.Passes, string.Join("; ", q.Failures()));
        Assert.True(q.FineIou > 0.95, $"IoU {q.FineIou}");
        Assert.Equal(0, q.FarAirPercent, 3);
        Assert.Equal(0, q.HoleSilhouettePercent, 3);
    }

    [Fact]
    public void One_box_over_an_open_frame_fails_on_air_and_on_the_see_through_view()
    {
        var (pos, idx, lo, hi) = Mesh(Frame);
        var q = AshfallKitFit.Measure(pos, idx, lo, hi, [(lo, hi)], 8f / 48f);
        Assert.False(q.Passes);
        Assert.True(q.FarAirPercent > 50, $"far air {q.FarAirPercent}");
        Assert.True(q.PhantomSilhouettePercent > 50, $"phantom {q.PhantomSilhouettePercent}");
        Assert.True(q.OverhangAirPercent > 50, $"overhang {q.OverhangAirPercent}");   // the doorway is under the lintel
        Assert.True(q.LargestPocketPercent > AshfallKitFit.Gate.MaxPocketPercent);
        Assert.True(q.DeepAirViewPercent > 50, $"deep air view {q.DeepAirViewPercent}");
    }

    [Fact]
    public void A_missing_post_fails_as_a_hole()
    {
        var (pos, idx, lo, hi) = Mesh(Frame);
        var q = AshfallKitFit.Measure(pos, idx, lo, hi, Frame.Skip(1).ToArray(), 8f / 48f);
        Assert.False(q.Passes);
        Assert.True(q.HoleSilhouettePercent > 10, $"holes {q.HoleSilhouettePercent}");
        Assert.True(q.MissedPercent > 10, $"missed {q.MissedPercent}");
    }

    [Fact]
    public void A_hollow_shell_with_a_hairline_crack_is_measured_solid_but_a_doorway_stays_open()
    {
        // A 6 m cube made of six 0.25 m slabs, the top slab short by 0.02 m (a crack): measured as one solid, so a box over the whole cube is no air.
        float t = 0.25f;
        (Vector3, Vector3)[] shell =
        [
            (new(0, 0, 0), new(6, t, 6)), (new(0, 6 - t, 0), new(5.98f, 6, 6)),
            (new(0, t, 0), new(t, 6 - t, 6)), (new(6 - t, t, 0), new(6, 6 - t, 6)),
            (new(t, t, 0), new(6 - t, 6 - t, t)), (new(t, t, 6 - t), new(6 - t, 6 - t, 6)),
        ];
        var (pos, idx, lo, hi) = Mesh(shell);
        var q = AshfallKitFit.Measure(pos, idx, lo, hi, [(lo, hi)], 6f / 48f);
        Assert.True(q.FarAirPercent < 1, $"far air {q.FarAirPercent}");
        // The same shell with a 2 x 3 m doorway in the front wall: the room is open, so one box over all of it is mostly air.
        (Vector3, Vector3)[] room =
        [
            (new(0, 0, 0), new(6, t, 6)), (new(0, 6 - t, 0), new(6, 6, 6)),
            (new(0, t, 0), new(t, 6 - t, 6)), (new(6 - t, t, 0), new(6, 6 - t, 6)),
            (new(t, t, 6 - t), new(6 - t, 6 - t, 6)),
            (new(t, t, 0), new(2, 6 - t, t)), (new(4, t, 0), new(6 - t, 6 - t, t)), (new(2, 3 + t, 0), new(4, 6 - t, t)),
        ];
        var r = Mesh(room);
        var q2 = AshfallKitFit.Measure(r.Pos, r.Idx, r.Min, r.Max, [(r.Min, r.Max)], 6f / 48f);
        Assert.True(q2.FarAirPercent > 40, $"far air {q2.FarAirPercent}");
    }

    [Fact]
    public void A_thin_wall_fitted_fine_is_kept_whole_not_dropped_as_dust()
    {
        // Draft 5 dropped one-cell-thick boxes under 0.2 % of the solid: at 48 cells a thin wall became strips and vanished (holes through AC-010). A 12 m wall 0.2 m thick beside a 2 m block.
        var (pos, idx, lo, hi) = Mesh([(new(0, 0, 0), new(12, 6, 0.2f)), (new(0, 0, 4), new(2, 2, 6))]);
        var fit = AshfallKitFit.Fit(pos, idx, lo, hi, 48, 64, 0.15f, 48);
        var q = AshfallKitFit.Measure(pos, idx, lo, hi, fit.Boxes, fit.Metrics.CellSize);
        Assert.Equal(0, fit.Metrics.DroppedBoxes);
        Assert.True(q.Passes, string.Join("; ", q.Failures()));
    }

    [Fact]
    public void The_gated_fit_of_an_open_frame_passes_and_keeps_the_doorway_open()
    {
        var (pos, idx, lo, hi) = Mesh(Frame);
        var g = AshfallKitFit.FitGated(pos, idx, lo, hi, walkable: false);
        Assert.True(g.Quality.Passes, string.Join("; ", g.Quality.Failures()));
        Assert.DoesNotContain(g.Fit.Boxes, b => b.Min.X < 4f && b.Max.X > 4f && b.Min.Y < 3f && b.Max.Y > 3f);
    }
}
