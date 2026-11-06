using SmSim.Extras.Musou.Ashfall;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall draft 7 (docs/musou-ashfall-graybox.md "Draft 7"): no two placed items overlap (owner, 2026-11-06: "change the generation rules so this doesn't happen on the next musou map draft. there are overlapping objects here.").
/// From the generator alone: no asset bytes, no install.
/// </summary>
public class AshfallDraft7Tests(ITestOutputHelper output)
{
    static readonly AshfallDraft5 D = AshfallMap.BuildDraft5();

    [Fact]
    public void No_two_placed_items_overlap_beyond_the_tolerance()
    {
        var boxes = AshfallOverlap.Boxes(D.Level, D.Dressing);
        var fam = new Dictionary<int, string>();
        foreach (var p in D.Placements) if (p.Item >= 0) fam[p.Item] = p.Family + ":" + p.Group;
        var pairs = AshfallOverlap.Find(boxes, AshfallOverlap.Tolerance, AshfallMap.OverlapExempt(D.Dressing));
        foreach (var p in pairs.OrderByDescending(p => p.Depth).Take(60))
            output.WriteLine($"{p.Depth:0.00} m  {p.A} {D.Dressing.Items[p.A].Asset} ({fam.GetValueOrDefault(p.A)})  x  {p.B} {D.Dressing.Items[p.B].Asset} ({fam.GetValueOrDefault(p.B)})  at ({p.At.X:0}, {p.At.Y:0}, {p.At.Z:0.0})");
        output.WriteLine($"{pairs.Count} overlapping pairs over {AshfallOverlap.Tolerance} m among {boxes.Select(b => b.Item).Distinct().Count()} items with collision");
        Assert.Empty(pairs);
    }

    [Fact]
    public void The_overlap_rule_is_reported_and_keeps_the_stronghold()
    {
        foreach (var f in D.Dressing.Flags.Where(f => f.Contains("overlap"))) output.WriteLine(f);
        var settled = D.Placements.Where(p => p.Outcome.Contains("(overlap)")).ToList();
        var dropped = D.Placements.Where(p => p.Outcome.StartsWith("dropped: overlap")).ToList();
        output.WriteLine($"moved or shrunk: {settled.Count}; left out: {dropped.Count}");
        output.WriteLine($"{D.Dressing.Items.Count} items ({D.Dressing.Items.Count(i => i.Role == AshfallDressRole.Prop)} Prop), {D.Level.Parts.Count} parts, {D.Level.SolidCorners().Length / 3:N0} collision triangles, platforms {D.Dressing.Items.Count(i => i.Asset is "AC-008" or "AC-049" or "AC-050" && i.Role == AshfallDressRole.Prop)}, expansion models used {D.Dressing.Items.Where(i => string.CompareOrdinal(i.Asset, "AC-041") >= 0).Select(i => i.Asset).Distinct().Count()} ({D.Dressing.Items.Count(i => string.CompareOrdinal(i.Asset, "AC-041") >= 0)} placements)");
        foreach (var g in dropped.GroupBy(p => p.Family).OrderBy(g => g.Key)) output.WriteLine($"  left out {g.Key}: {g.Count()} ({string.Join(", ", g.Take(6).Select(p => p.Group))})");
        foreach (var p in settled.Where(p => p.Family.StartsWith("stronghold") || p.Family is "terrace" or "tower" or "platform")) output.WriteLine($"  {p.Group} {p.Asset}: {p.Outcome}");
        foreach (var p in dropped.Where(p => p.Family is not "rubble")) output.WriteLine($"  {p.Group}: {p.Outcome}");
        Assert.Contains(D.Dressing.Flags, f => f.StartsWith("draft 7 overlap rule"));
        // The stronghold's keep, towers and every leg wall stand (the rule moves, shrinks or trims them); of the three stepped terraces, which run into each other, at least two.
        foreach (var name in new[] { "keep", "Keep tower", "Stair tower SW", "Stair tower SE" })
            Assert.Contains(D.Placements, p => p.Group == name && p.Item >= 0);
        Assert.DoesNotContain(dropped, p => p.Family.StartsWith("stronghold"));
        Assert.True(D.Placements.Count(p => p.Group.StartsWith("terrace ") && p.Item >= 0) >= 2);
        // The rule thins the dressing, not the map: at most one placed item in six is left out for it.
        int placed = D.Placements.Count(p => p.Item >= 0);
        Assert.True(dropped.Count * 6 <= placed, $"{dropped.Count} of {placed + dropped.Count} items left out for overlaps");
    }

    [Fact]
    public void Without_the_overlap_rule_the_draft_6_placement_overlaps_and_the_rule_clears_it()
    {
        var before = AshfallMap.RebuildWithoutOverlapRule();
        var pairs = AshfallOverlap.Find(AshfallOverlap.Boxes(before.Level, before.Dressing), AshfallOverlap.Tolerance, AshfallMap.OverlapExempt(before.Dressing));
        output.WriteLine($"without the rule: {pairs.Count} overlapping pairs, deepest {pairs.Select(p => p.Depth).DefaultIfEmpty(0f).Max():0.00} m");
        foreach (var g in pairs.GroupBy(p => (before.Dressing.Items[p.A].Asset, before.Dressing.Items[p.B].Asset)).OrderByDescending(g => g.Count()).Take(15))
            output.WriteLine($"  {g.Key.Item1} x {g.Key.Item2}: {g.Count()} (deepest {g.Max(p => p.Depth):0.00} m at ({g.MaxBy(p => p.Depth).At.X:0}, {g.MaxBy(p => p.Depth).At.Y:0}))");
        Assert.NotEmpty(pairs);   // the rule is what clears them (the test above)
    }

    [Fact]
    public void Fire_spots_are_deterministic_spread_and_clear_of_the_objectives_starts_lanes_and_collision()
    {
        var a = AshfallAtmosphere.FireSpots(D.Level, D.Dressing);
        var r = AshfallMap.RebuildDraft5();
        var b = AshfallAtmosphere.FireSpots(r.Level, r.Dressing);
        Assert.Equal(a, b);
        int plumes = a.Count(s => s.Plume), fires = a.Count(s => !s.Plume);
        output.WriteLine($"{plumes} plumes, {fires} small fires");
        foreach (var s in a) output.WriteLine($"  {(s.Plume ? "plume" : "fire ")} ({s.Position.X:0.0}, {s.Position.Y:0.0}, {s.Position.Z:0.0}) size {s.Size:0.00}");
        Assert.InRange(plumes, 5, AshfallAtmosphere.Plumes);
        Assert.InRange(fires, 24, AshfallAtmosphere.SmallFires);
        var boxes = AshfallOverlap.Boxes(D.Level, D.Dressing);
        foreach (var s in a)
        {
            Assert.True(AshfallAtmosphere.Clear(D.Level, s.Position));
            foreach (var p in D.Level.Points) Assert.True(System.Numerics.Vector2.Distance(new(p.Center.X, p.Center.Y), new(s.Position.X, s.Position.Y)) >= p.Radius + AshfallAtmosphere.ZoneClear);
            if (!s.Plume) Assert.DoesNotContain(boxes, x => AshfallAtmosphere.Inside(x.Box, new(s.Position.X, s.Position.Y), 0f) && x.Box.Z0 < s.Position.Z + 1.5f && x.Box.Z1 > s.Position.Z + 0.1f);
        }
        for (int i = 0; i < a.Count; i++)
            for (int j = i + 1; j < a.Count; j++)
            {
                float d = System.Numerics.Vector2.Distance(new(a[i].Position.X, a[i].Position.Y), new(a[j].Position.X, a[j].Position.Y));
                Assert.True(d >= (a[i].Plume && a[j].Plume ? AshfallAtmosphere.PlumeSpacing : AshfallAtmosphere.FireSpacing) - 1e-3f, $"spots {i} and {j} are {d:0.0} m apart");
            }
        // spread: fires in at least six of the map's nine 160 m blocks
        Assert.True(a.Select(s => ((int)((s.Position.X + 240f) / 160f), (int)((s.Position.Y + 240f) / 160f))).Distinct().Count() >= 6);
    }
}
