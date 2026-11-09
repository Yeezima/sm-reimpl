using System.Numerics;
using SmContent;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall draft 11, the battlefield layout (owner, 2026-11-09; docs/ashfall-layout-brief.md, docs/musou-ashfall-graybox.md "Draft 11 battlefield layout"): a separate generator path selected by the map name
/// <c>tfw_war_field</c>, draft 10 kept as the rollback (map <c>tfw_war</c>, its hash pinned in <see cref="AshfallDraft5Tests.PinnedLevelHash"/>). From the generator alone (no asset bytes, no install).
/// </summary>
public class AshfallDraft11Tests(ITestOutputHelper output)
{
    /// <summary>The field level's pin (field version 1, default parameters). After a refit of the kit's collision, take the new value from this test's failure.</summary>
    public const ulong PinnedFieldHash = 0x94488BF0597BA823UL;

    static readonly AshfallDraft5 F = AshfallMap.BuildDraft5(AshfallLayout.Field);
    static readonly Lazy<AshfallBattlefield> Field = new(() => AshfallBattlefield.Measure(AshfallLayout.Field));

    // ---------------------------------------------------------------- the switch and the rollback

    [Fact]
    public void The_field_layout_is_its_own_map_with_its_own_hash_and_draft_10_stays_as_it_was()
    {
        var d10 = AshfallMap.BuildDraft5();
        Assert.Equal(AshfallDraft5Tests.PinnedLevelHash, d10.Level.Hash);   // the rollback path: draft 10 bit for bit
        Assert.Equal(AshfallLayout.Draft10, d10.Level.Layout);
        Assert.Same(d10, AshfallMap.BuildDraft5(AshfallLayout.Draft10));
        Assert.Equal(AshfallLayout.Field, F.Level.Layout);
        Assert.NotEqual(d10.Level.Hash, F.Level.Hash);
        output.WriteLine($"field hash {F.Level.Hash:X16}");
        if (PinnedFieldHash != 0UL) Assert.Equal(PinnedFieldHash, F.Level.Hash);
        // map names: tfw_war_draft10 is draft 10 (tfw_war is the draft-12 city since draft 12), tfw_war_field the field; all are Ashfall and war maps
        Assert.Equal(AshfallLayout.Draft10, TfwMap.LayoutOf(TfwMap.Draft10Name));
        Assert.Equal(AshfallLayout.Field, TfwMap.LayoutOf(TfwMap.FieldName));
        Assert.True(TfwMap.IsWarMap(TfwMap.FieldName) && TfwMap.IsAshfall(TfwMap.FieldName) && TfwMap.IsAshfall(TfwMap.Name) && !TfwMap.IsAshfall(TfwMap.ManufactorumName));
        // the composed worlds differ in their hash, so a client on the other layout is told apart at the handshake
        var a = TfwWorld.Load("", out var pa, TfwMap.Name); var b = TfwWorld.Load("", out var pb, TfwMap.FieldName);
        Assert.Null(pa); Assert.Null(pb);
        Assert.NotEqual(a!.Hash, b!.Hash);
        Assert.Equal(AshfallContent.ToMapContent(F.Level).Hash, F.Level.Hash);
    }

    [Fact]
    public void The_hosted_server_gets_the_field_map_only_when_asked()
    {
        var plain = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 4000, true, "", Content: "musou"));
        Assert.DoesNotContain("--map", plain);
        var field = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 4000, true, "", Content: "musou", Map: TfwMap.FieldName));
        int i = field.IndexOf("--map");
        Assert.True(i >= 0 && field[i + 1] == TfwMap.FieldName);
    }

    [Fact]
    public void The_field_layout_is_deterministic()
    {
        var again = AshfallMap.RebuildField(AshfallFieldParams.Default);
        Assert.Equal(F.Level.Hash, again.Level.Hash);
        Assert.Equal(F.Dressing.Items.Count, again.Dressing.Items.Count);
    }

    [Fact]
    public void The_surface_seed_changes_only_the_look_and_the_dressing_seed_never_the_corridors()
    {
        var surface = AshfallMap.RebuildField(AshfallFieldParams.Default with { SurfaceSeed = 7 });
        Assert.Equal(F.Level.Hash, surface.Level.Hash);   // collision untouched
        var tiles = F.Dressing.Items.Where(i => i.Role == AshfallDressRole.Surface).Select(i => i.Asset + i.Yaw).ToList();
        var tiles2 = surface.Dressing.Items.Where(i => i.Role == AshfallDressRole.Surface).Select(i => i.Asset + i.Yaw).ToList();
        Assert.NotEqual(tiles, tiles2);
        var dress = AshfallMap.RebuildField(AshfallFieldParams.Default with { DressSeed = 9 });
        var grid = new AshfallGrid(dress.Level);
        foreach (var k in AshfallField.Default.Corridors)
            for (float t = 0f; t <= k.Length; t += 2f)
            {
                var q = k.A + Vector2.Normalize(k.B - k.A) * t;
                Assert.False(grid.Blocked[grid.Index(q)], $"dressing seed 9 blocks corridor {k.Name} at ({q.X:0}, {q.Y:0})");
            }
    }

    // ---------------------------------------------------------------- the ruleset

    [Fact]
    public void Every_constraint_of_the_ruleset_passes_on_the_field_layout()
    {
        var bf = Field.Value;
        output.WriteLine(bf.Report());
        Assert.Empty(bf.Constraints.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Value} (needs {c.Threshold})"));
        Assert.True(bf.Constraints.Count >= 60);
    }

    [Fact]
    public void Army_routes_are_20_m_wide_at_their_narrowest_point_after_all_collision()
    {
        var bf = Field.Value;
        foreach (var l in bf.Links)
        {
            Assert.True(l.Width >= 20f, $"{l.From} to {l.To}: {l.Width:0.0} m at ({l.Pinch.X:0}, {l.Pinch.Y:0})");
            Assert.NotNull(l.ArmyLength);
        }
    }

    [Fact]
    public void Every_point_has_two_distinct_formation_capable_approaches()
    {
        foreach (var a in Field.Value.Approaches) Assert.True(a.Count >= 2, $"{a.Point}: {a.Count} ({string.Join(", ", a.Bearings)})");
        // and the plan reaches each point by at least two corridors from different sides
        foreach (var p in AshfallField.Default.Points)
        {
            var dirs = AshfallField.Default.ApproachesOf(p).Select(k => { var c = new Vector2(p.Center.X, p.Center.Y); var far = Vector2.Distance(k.A, c) > Vector2.Distance(k.B, c) ? k.A : k.B; return Vector2.Normalize(far - c); }).ToList();
            Assert.Contains(dirs, d => dirs.Any(e => Vector2.Dot(d, e) < 0.5f));
        }
    }

    [Fact]
    public void Protected_places_assembly_areas_and_corridors_hold_no_collision()
    {
        foreach (var (name, blocked, cells) in Field.Value.ProtectedBlocked) Assert.True(blocked == 0, $"{name}: {blocked} of {cells} cells");
        Assert.Empty(AshfallRoadway.Find(F.Level, F.Dressing));   // the corridors are the draft-8 roadways
    }

    [Fact]
    public void Every_standing_placement_rule_holds_on_the_field_layout()
    {
        // no overlaps (draft 7)
        var boxes = AshfallOverlap.Boxes(F.Level, F.Dressing);
        Assert.Empty(AshfallOverlap.Find(boxes, exempt: AshfallMap.OverlapExempt(F.Dressing)));
        // walkable stairs only, foot and landing open (drafts 9 and 10)
        var stairs = AshfallStairs.All(F.Level, F.Dressing);
        Assert.NotEmpty(stairs);
        var solids = AshfallStairs.Solids.Of(F.Level);
        var bad = new List<string>();
        foreach (var r in stairs)
        {
            var foot = AshfallStairs.Measure(r, solids, top: false, 1.5f); var land = AshfallStairs.Measure(r, solids, top: true, 1.0f);
            if (!foot.Open || !land.Open) bad.Add($"stair {r.Part} of item {r.Item}: foot {foot.Open}, landing {land.Open}");
        }
        Assert.Empty(bad);
        // the stair-access rule refused nothing it should have kept, and the keep is authored
        Assert.Contains(F.Dressing.Flags, f => f.StartsWith("draft 10 stair access"));
        Assert.Contains(F.Placements, p => p.Group == "keep" && p.Asset == "AC-044");
        // bridges only on supports (the keep bridge is the only bridge): no bridge model outside the stronghold
        Assert.All(F.Dressing.Items.Where(i => AshfallRoadway.IsBridge(i.Asset)), i => Assert.True(i.Position.Y > 96f && MathF.Abs(i.Position.X) < 84f));
    }

    [Fact]
    public void The_stronghold_and_the_Imperial_compound_are_draft_10s()
    {
        var d10 = AshfallMap.BuildDraft5();
        static bool Fort(string g) => g.StartsWith("ring ") || g is "keep" or "Keep tower" or "Stair tower SW" or "Stair tower SE" or "keep bridge" or "Twin tower west" or "Twin tower east" or "twin bridge" || g.StartsWith("terrace") || g.StartsWith("east outer wall") || g.StartsWith("compound ");
        var a = d10.Placements.Where(p => Fort(p.Group) && p.Item >= 0).ToDictionary(p => p.Group, p => d10.Dressing.Items[p.Item]);
        var b = F.Placements.Where(p => Fort(p.Group) && p.Item >= 0).ToDictionary(p => p.Group, p => F.Dressing.Items[p.Item]);
        var diff = new List<string>();
        foreach (var (g, it) in a)
        {
            if (!b.TryGetValue(g, out var o)) { diff.Add($"{g}: missing"); continue; }
            if (o.Asset != it.Asset || Vector3.Distance(o.Position, it.Position) > 0.05f || MathF.Abs(o.Yaw - it.Yaw) > 1e-3f) diff.Add($"{g}: {it.Asset} at {it.Position} -> {o.Asset} at {o.Position}");
        }
        foreach (var d in diff) output.WriteLine(d);
        Assert.Empty(diff);
        // the same collision bits: every part of every fortress item
        foreach (var (g, it) in a)
        {
            int ia = d10.Placements.First(p => p.Group == g && p.Item >= 0).Item, ib = F.Placements.First(p => p.Group == g && p.Item >= 0).Item;
            var (fa, ca) = d10.Dressing.ItemParts![ia]; var (fb, cb) = F.Dressing.ItemParts![ib];
            Assert.Equal(ca, cb);
            for (int k = 0; k < ca; k++) Assert.Equal(d10.Level.Parts[fa + k].Corners, F.Level.Parts[fb + k].Corners);
        }
        Assert.True(a.Count >= 50, $"{a.Count} fortress pieces");
        Assert.Equal(d10.Level.Areas, F.Level.Areas);
    }

    [Fact]
    public void Districts_are_mostly_open_with_islands_and_clusters()
    {
        var plan = AshfallField.Default;
        Assert.Equal(["cathedral quarter", "manufactorum district", "no-man's-land", "hab-block ruins"], plan.Districts.Select(d => d.Name));
        foreach (var d in plan.Districts) Assert.Contains(plan.Structures, s => s.District == d.Name);
        // clusters: some islands hold several buildings near each other
        // an island's structures are named "<district> island <n> <family> <k>": the island is the name less its last two words
        static string Island(string name) { int last = name.LastIndexOf(' '); int prev = last > 0 ? name.LastIndexOf(' ', last - 1) : -1; return name.Contains(" island ") && prev > 0 ? name[..prev] : name; }
        Assert.Contains(plan.Structures.GroupBy(s => Island(s.Name)), g => g.Count(s => s.Family is "shell" or "facade") >= 3);
        foreach (var (d, cover) in Field.Value.Density) { output.WriteLine($"{d}: {cover:P1}"); Assert.True(cover < 0.3f, d); }
        // only kit models
        Assert.All(F.Dressing.Items, i => Assert.NotNull(AshfallKit.Get(i.Asset)));
    }

    // ---------------------------------------------------------------- clashes

    [Fact]
    public void A_200_a_side_clash_meets_in_a_broad_front_on_the_field_layout()
    {
        var r = AshfallClash.Run(new AshfallClash.Setup(AshfallLayout.Field, "B", 200, AshfallClash.ClashAi.Shipped));
        output.WriteLine(AshfallClash.Result.Header); output.WriteLine(r.Row);
        Assert.True(r.FirstContact > 0f);
        Assert.False(r.SingleFile);
        Assert.True(r.FrontageSteady >= 15f, $"frontage {r.FrontageSteady:0.0} m");
        Assert.True(r.StuckEver <= 10, $"{r.StuckEver} stuck");
        Assert.True(r.NarrowestOnRoute >= 20f, $"narrowest {r.NarrowestOnRoute:0.0} m");
    }

    [Theory]
    [Trait("Speed", "Slow")]   // 500 and 1,000 a side (about 10 and 30 s each)
    [InlineData(500)]
    [InlineData(1000)]
    public void Larger_clashes_keep_a_front_and_retreat_on_the_field_layout(int perSide)
    {
        var r = AshfallClash.Run(new AshfallClash.Setup(AshfallLayout.Field, "B", perSide, AshfallClash.ClashAi.Shipped));
        output.WriteLine(AshfallClash.Result.Header); output.WriteLine(r.Row);
        Assert.False(r.SingleFile);
        Assert.True(r.FrontageSteady >= 25f, $"frontage {r.FrontageSteady:0.0} m");
        Assert.True(r.RetreatSeconds > 0f && r.RetreatSeconds < 90f, $"retreat {r.RetreatSeconds:0} s");
        Assert.True(r.StuckEver <= perSide / 20, $"{r.StuckEver} stuck");
    }
}
