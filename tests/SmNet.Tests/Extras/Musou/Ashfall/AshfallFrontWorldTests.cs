using System.Numerics;
using SmContent.Extras.Musou;
using SmNet;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// The Musou front pilot's composed world (F2; docs/musou-ashfall-graybox.md "Front pilot"): the three bands side by side in one world under the map <c>tfw_war_front</c>, split back into bands for drawing, one battlefield per band
/// in the war's world, and the run fighting each node on its band. From the generator alone (no asset bytes, no install).
/// </summary>
public class AshfallFrontWorldTests(ITestOutputHelper output)
{
    /// <summary>The composed world's level hash (front version 1, the default seed; after F5). Re-pin with the band pins after a generator change.</summary>
    public const ulong PinnedComposedHash = 0x85544EA11A1D9794UL;

    [Fact]
    [Trait("Speed", "Slow")]   // builds the default bands (cached in the process) and one other seed's wasteland
    public void A_built_bands_frame_is_read_back_from_the_level_for_any_seed()
    {
        // review of 2026-11-16: the readers of a built level (roadways, the storm's clear ground, the stair-access start) take the band's own frame from the level, not the default seed's plan
        var cases = Enumerable.Range(0, 3).Select(b => (AshfallMap.BuildFrontBand(b).Level, AshfallFront.Default(b))).ToList();
        var other = AshfallFront.Plan(0, 2u);   // seed 2's wasteland took gate A retries: other lines than the default's
        cases.Add((AshfallMap.BuildFrontFrom(other).Level, other));
        foreach (var (level, plan) in cases)
        {
            var frame = level.PlanOf()!;
            Assert.Same(frame, level.PlanOf());   // read once, kept
            Assert.Equal(plan.Skeleton! with { Attempt = -1 }, frame.Skeleton);
            Assert.Equal(plan.Corridors, frame.Corridors);
            Assert.Equal(plan.Assembly, frame.Assembly);
            Assert.Equal(plan.Aprons, frame.Aprons);
        }
        Assert.NotEqual(cases[0].Item2.Corridors, cases[3].Item2.Corridors);
        Assert.Null(AshfallMap.BuildDraft5(AshfallLayout.Heights).Level.PlanOf()!.Skeleton);   // other layouts: their plan as before
    }

    // ---------------------------------------------------------------- the composed world and the run's bands (F2)

    [Fact]
    [Trait("Speed", "Slow")]   // the three bands and the composed world (about a minute and a half on a cold build)
    public void Tfw_war_front_composes_the_three_bands_side_by_side_and_the_run_fights_each_node_on_its_band()
    {
        Assert.Equal(AshfallLayout.Front, TfwMap.LayoutOf(TfwMap.FrontName));
        Assert.True(TfwMap.IsWarMap(TfwMap.FrontName) && TfwMap.IsAshfall(TfwMap.FrontName) && Protocol.IsMapName(TfwMap.FrontName));
        Assert.Equal(AshfallLayout.Front, AshfallLayouts.Parse("front"));
        Assert.Equal("front", AshfallLayout.Front.Key());
        Assert.Null(AshfallLayout.Front.PlanOf());
        var whole = AshfallMap.BuildDraft5(AshfallLayout.Front);
        var level = whole.Level;
        Assert.Equal(AshfallLayout.Front, level.Layout);
        Assert.Equal(9, level.Points.Count);
        Assert.Equal(6, level.Areas.Count);
        Assert.Equal(Enumerable.Range(0, 3).Sum(b => AshfallMap.BuildFrontBand(b).Level.Parts.Count), level.Parts.Count);
        for (int k = 0; k < 3; k++) Assert.All(level.Points.Skip(3 * k).Take(3), p => Assert.Equal(k, AshfallFront.BandAt(p.Center.X)));
        Assert.Equal(PinnedComposedHash, level.Hash);
        output.WriteLine($"composed: hash {level.Hash:X16}, {level.Parts.Count} parts, {level.SolidCorners().Length / 3} collision triangles, {whole.Dressing.Items.Count} items");
        // split back into its bands: the same parts, items and points as each band's own build
        var split = AshfallMap.SplitFront(level, whole.Dressing);
        Assert.Equal(3, split.Count);
        for (int k = 0; k < 3; k++)
        {
            var own = AshfallMap.BuildFrontBand(k);
            Assert.Equal(own.Level.Parts.Count, split[k].Level.Parts.Count);
            Assert.Equal(own.Dressing.Items.Count, split[k].Dressing.Items.Count);
            Assert.Equal(own.Dressing.HiddenParts.Count, split[k].Dressing.HiddenParts.Count);
            Assert.Equal(AshfallFront.LayoutOf(k), split[k].Level.Layout);
            for (int i = 0; i < 3; i++) Assert.True(Vector3.Distance(own.Level.Points[i].Center, split[k].Level.Points[i].Center) < 1e-3f);
        }
        // the war's world: one battlefield per band, each its own points, start and bounds inside its square; the run's band follows the node
        var data = TfwMap.ComposeFront(AshfallContent.ToMapContent(level).ToCollisionWorld(), level.Points, level.Areas, level.Spawns, AshfallFront.BandCount, AshfallFront.BandAt);
        var bands = data.Field.Bands!;
        Assert.Equal(3, bands.Count);
        for (int k = 0; k < 3; k++)
        {
            float off = AshfallFront.OffsetOf(k);
            Assert.True(bands[k].Bounds.Min.X >= off - 280f && bands[k].Bounds.Max.X <= off + 280f, $"band {k} bounds {bands[k].Bounds.Min} {bands[k].Bounds.Max}");
            Assert.All(bands[k].Points, p => Assert.Equal(k, AshfallFront.BandAt(p.Centre.X)));
            Assert.Equal(k, AshfallFront.BandAt(bands[k].MarineStart.X));
            Assert.True(bands[k].BaseLanding);
            Assert.Same(data.Field.Hub, bands[k].Hub);
        }
        var run = new TfwRun(data.Field);
        Assert.Same(bands[0], run.Field);
        Assert.Same(data.Field, run.WholeField);
        Assert.Equal(0, TfwRun.BandForNode(1, 3)); Assert.Equal(1, TfwRun.BandForNode(2, 3)); Assert.Equal(2, TfwRun.BandForNode(3, 3)); Assert.Equal(0, TfwRun.BandForNode(3, 0));
        // any other map: no bands, the run's field is the map's own
        var heights = TfwWorld.Load("", out var problem, TfwMap.HeightsName);
        Assert.Null(problem);
        Assert.Null(heights!.Field.Bands);
        Assert.Same(heights.Field, new TfwRun(heights.Field).Field);
    }
}
