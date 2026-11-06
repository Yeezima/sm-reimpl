using SmSim.Extras.Musou.Ashfall;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall draft 8 (docs/musou-ashfall-graybox.md "Draft 8 roadways"): no placed 3D item stands in a roadway (owner, 2026-11-06: "we need to ensure another parameter for procedural generation is followed:
/// no 3d objects should be placed in roadways."). From the generator alone: no asset bytes, no install.
/// </summary>
public class AshfallDraft8Tests(ITestOutputHelper output)
{
    static readonly AshfallDraft5 D = AshfallMap.BuildDraft5();

    void Print(AshfallDraft5 d, List<AshfallRoadway.Hit> hits)
    {
        var fam = new Dictionary<int, string>();
        foreach (var p in d.Placements) if (p.Item >= 0) fam[p.Item] = p.Family + ":" + p.Group;
        foreach (var h in hits.OrderByDescending(h => h.Depth).Take(80))
            output.WriteLine($"{h.Depth:0.00} m  {h.Item} {d.Dressing.Items[h.Item].Asset} ({fam.GetValueOrDefault(h.Item)}) at ({h.At.X:0}, {h.At.Y:0}, {h.At.Z:0.0})");
        output.WriteLine($"{hits.Count} items in a roadway (margin {AshfallRoadway.Margin} m)");
    }

    [Fact]
    public void No_placed_item_stands_in_a_roadway()
    {
        var hits = AshfallRoadway.Find(D.Level, D.Dressing);
        Print(D, hits);
        Assert.Empty(hits);
    }

    [Fact]
    public void The_roadway_rule_is_reported_and_keeps_the_stronghold()
    {
        foreach (var f in D.Dressing.Flags.Where(f => f.Contains("roadway"))) output.WriteLine(f);
        var settled = D.Placements.Where(p => p.Outcome.Contains("(roadway)")).ToList();
        var dropped = D.Placements.Where(p => p.Outcome.StartsWith("dropped: roadway")).ToList();
        output.WriteLine($"moved or shrunk off a roadway: {settled.Count}; left out: {dropped.Count}");
        output.WriteLine($"{D.Dressing.Items.Count} items ({D.Dressing.Items.Count(i => i.Role == AshfallDressRole.Prop)} Prop), {D.Level.Parts.Count} parts, {D.Level.SolidCorners().Length / 3:N0} collision triangles, hash 0x{D.Level.Hash:X16}");
        foreach (var g in settled.GroupBy(p => p.Family).OrderBy(g => g.Key)) output.WriteLine($"  moved {g.Key}: {g.Count()}");
        foreach (var g in dropped.GroupBy(p => p.Family).OrderBy(g => g.Key)) output.WriteLine($"  left out {g.Key}: {g.Count()} ({string.Join(", ", g.Take(8).Select(p => p.Group))})");
        foreach (var p in settled.Where(p => p.Family.StartsWith("stronghold") || p.Family is "terrace" or "outerwall" or "tower" or "monument" or "arch")) output.WriteLine($"  {p.Group} {p.Asset}: {p.Outcome}");
        foreach (var p in dropped.Where(p => p.Family is not ("rubble" or "cover"))) output.WriteLine($"  {p.Group}: {p.Outcome}");
        Assert.Contains(D.Dressing.Flags, f => f.StartsWith("draft 8 roadway rule ("));
        Assert.NotEmpty(settled);   // the rule moved what stood in the roads (the test below: without it they stand there)
        foreach (var name in new[] { "keep", "Keep tower", "Stair tower SW", "Stair tower SE", "ring gate" })
            Assert.Contains(D.Placements, p => p.Group == name && p.Item >= 0);
        Assert.DoesNotContain(dropped, p => p.Family.StartsWith("stronghold"));
        Assert.True(D.Placements.Count(p => p.Group.StartsWith("terrace ") && p.Item >= 0) >= 2);
        // The rule clears the roads, not the map: at most one placed item in eight is left out for it.
        int placed = D.Placements.Count(p => p.Item >= 0);
        Assert.True(dropped.Count * 8 <= placed, $"{dropped.Count} of {placed + dropped.Count} items left out for roadways");
    }

    [Fact]
    public void Without_the_roadway_rule_items_stand_in_the_roads_and_the_rule_clears_them()
    {
        var before = AshfallMap.RebuildWithoutRoadwayRule();
        Assert.Contains(before.Dressing.Flags, f => f.StartsWith("draft 8 roadway rule OFF"));
        var hits = AshfallRoadway.Find(before.Level, before.Dressing);
        output.WriteLine($"without the rule: {hits.Count} items in a roadway, deepest {hits.Select(h => h.Depth).DefaultIfEmpty(0f).Max():0.00} m");
        foreach (var g in hits.GroupBy(h => before.Dressing.Items[h.Item].Asset).OrderByDescending(g => g.Count()).Take(12)) output.WriteLine($"  {g.Key}: {g.Count()}");
        Assert.NotEmpty(hits);   // the rule is what clears them (No_placed_item_stands_in_a_roadway)
    }

    [Fact]
    public void Roadway_strips_come_from_the_level()
    {
        var strips = AshfallRoadway.Strips(D.Level, 0f);
        // the four road lines both ways (avenues 24 m, the perimeter road 16 m) and the paved road's three Road parts
        Assert.Equal(8 + D.Level.Parts.Count(p => p.Kind == AshfallKind.Road), strips.Count);
        Assert.Equal(3, D.Level.Parts.Count(p => p.Kind == AshfallKind.Road));
        // a point in the middle of an avenue, on the paved road, and in the middle of a block
        static bool In(AshfallBox b, float x, float y) => AshfallAtmosphere.Inside(b, new(x, y), 0f);
        Assert.Contains(strips, b => In(b, 92f, 30f));
        Assert.Contains(strips, b => In(b, 0f, -120f));
        Assert.DoesNotContain(strips, b => In(b, 160f, 160f));
        Assert.DoesNotContain(strips, b => In(b, 30f, 30f));   // the plaza is not a road
    }
}
