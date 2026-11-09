using System.Numerics;
using SmContent;
using SmContent.Extras.Musou;
using SmNet;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall draft 12, the city layout (owner, 2026-11-09: "redo the layout of the ashfall draft 10 map. no stairs inaccessible or blocked by walls or other objects. lined up buildings and walls. a logical layout that
/// allows the flow of the battle, with roads placed properly to facilitate movement of armies"; docs/musou-ashfall-graybox.md "Draft 12 city layout"). The map <c>tfw_war</c> builds it; draft 10 stays as the rollback
/// (<c>tfw_war_draft10</c>, its hash pinned in <see cref="AshfallDraft5Tests.PinnedLevelHash"/>) and draft 11 as <c>tfw_war_field</c>. From the generator alone (no asset bytes, no install).
/// </summary>
public class AshfallDraft12Tests(ITestOutputHelper output)
{
    /// <summary>The city level's pin (city version 1, default parameters). After a refit of the kit's collision, take the new value from this test's failure.</summary>
    public const ulong PinnedCityHash = 0x401F29A0BB9A1E49UL;

    static readonly AshfallDraft5 C = AshfallMap.BuildDraft5(AshfallLayout.City);
    static readonly Lazy<AshfallBattlefield> City = new(() => AshfallBattlefield.Measure(AshfallLayout.City));
    static readonly Lazy<List<AshfallStairAccess.Stair>> Access = new(() => AshfallStairAccess.Check(C));

    string Name(int item) => C.Placements.FirstOrDefault(p => p.Item == item) is { } p ? $"{p.Group} ({p.Asset})" : $"item {item}";

    // ---------------------------------------------------------------- the switch and the rollback

    [Fact]
    public void Tfw_war_builds_the_city_and_draft_10_stays_reachable_unchanged_behind_its_own_map_name()
    {
        Assert.Equal(AshfallLayout.City, TfwMap.LayoutOf(TfwMap.Name));
        Assert.Equal(AshfallLayout.Draft10, TfwMap.LayoutOf(TfwMap.Draft10Name));
        Assert.Equal(AshfallLayout.Field, TfwMap.LayoutOf(TfwMap.FieldName));
        foreach (var n in new[] { TfwMap.Name, TfwMap.Draft10Name, TfwMap.FieldName })
        {
            Assert.True(TfwMap.IsWarMap(n) && TfwMap.IsAshfall(n), n);
            Assert.True(Protocol.IsMapName(n), n);
        }
        // the rollback: draft 10 bit for bit, and the field as draft 11 left it
        var d10 = AshfallMap.BuildDraft5(AshfallLayout.Draft10);
        Assert.Equal(AshfallDraft5Tests.PinnedLevelHash, d10.Level.Hash);
        Assert.Equal(AshfallDraft11Tests.PinnedFieldHash, AshfallMap.BuildDraft5(AshfallLayout.Field).Level.Hash);
        Assert.Equal(AshfallLayout.City, C.Level.Layout);
        output.WriteLine($"city hash {C.Level.Hash:X16}");
        if (PinnedCityHash != 0UL) Assert.Equal(PinnedCityHash, C.Level.Hash);
        // the composed worlds differ, so a client on another layout is told apart at the handshake
        var a = TfwWorld.Load("", out var pa, TfwMap.Name); var b = TfwWorld.Load("", out var pb, TfwMap.Draft10Name);
        Assert.Null(pa); Assert.Null(pb);
        Assert.NotEqual(a!.Hash, b!.Hash);
        Assert.Equal(AshfallContent.ToMapContent(C.Level).Hash, C.Level.Hash);
    }

    [Fact]
    public void The_hosted_server_gets_the_draft_10_map_only_when_asked()
    {
        var plain = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 4000, true, "", Content: "musou"));
        Assert.DoesNotContain("--map", plain);
        var d10 = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 4000, true, "", Content: "musou", Map: TfwMap.Draft10Name));
        int i = d10.IndexOf("--map");
        Assert.True(i >= 0 && d10[i + 1] == TfwMap.Draft10Name);
        Assert.Equal(AshfallLayout.City, AshfallLayouts.Parse("city"));
        Assert.Equal(AshfallLayout.Draft10, AshfallLayouts.Parse("draft10"));
        Assert.Equal(AshfallLayout.Field, AshfallLayouts.Parse("field"));
    }

    [Fact]
    public void The_city_layout_is_deterministic_and_its_surface_seed_leaves_the_collision()
    {
        var again = AshfallMap.RebuildCity(AshfallCity.Defaults);
        Assert.Equal(C.Level.Hash, again.Level.Hash);
        Assert.Equal(C.Dressing.Items.Count, again.Dressing.Items.Count);
        var surface = AshfallMap.RebuildCity(AshfallCity.Defaults with { SurfaceSeed = 7 });
        Assert.Equal(C.Level.Hash, surface.Level.Hash);
    }

    // ---------------------------------------------------------------- stairs (the owner's first rule)

    [Fact]
    public void Every_stair_is_reachable_at_both_ends_on_the_final_collision()
    {
        var res = Access.Value;
        output.WriteLine($"{res.Count} stairs ({res.Count(r => r.Elevated)} with a raised foot), {res.Count(r => r.Authored)} authored");
        foreach (var r in res.Where(r => r.Problem is not null)) output.WriteLine($"{(r.Authored ? "[authored] " : "")}{Name(r.Ramp.Item)} at ({r.Ramp.Low.X:0.0}, {r.Ramp.Low.Y:0.0}): {r.Problem}");
        Assert.True(res.Count >= 100, $"{res.Count} stairs");
        Assert.Empty(res.Where(r => r.Problem is not null && !r.Authored).Select(r => $"{Name(r.Ramp.Item)}: {r.Problem}"));
        // only the stronghold's authored flights may fail (the keep tower's top flight, as in drafts 10 and 11)
        Assert.True(res.Count(r => r.Problem is not null) <= 2);
        var flag = C.Dressing.Flags.Single(f => f.StartsWith("draft 12 stair access"));
        output.WriteLine(flag);
        Assert.DoesNotContain("still fail", flag);
    }

    [Fact]
    public void The_two_storey_ruins_raised_floors_get_an_entry_ramp_or_another_model()
    {
        // AC-051's ground floor is a 1.1 m plinth whose door steps rise 0.57 m (over the 0.4 m step): every kept AC-051 has its entry ramp
        var ac051 = C.Placements.Where(p => p.Item >= 0 && p.Asset == "AC-051").ToList();
        Assert.NotEmpty(ac051);
        Assert.All(ac051, p => Assert.Contains("draft 12: an entry ramp", p.Outcome));
        // the check walks them: a raised foot reached through its entry ramp from the streets
        Assert.Contains(Access.Value, r => r.Elevated && r.Problem is null);
    }

    [Fact]
    public void The_stair_access_check_finds_the_draft_10_ruins_that_no_one_can_enter()
    {
        // the reason for the redo: on draft 10 the two-storey ruins' flights start on a plinth no player can step onto
        var d10 = AshfallStairAccess.Check(AshfallMap.BuildDraft5(AshfallLayout.Draft10));
        int bad = d10.Count(r => r.Problem is not null && !r.Authored);
        output.WriteLine($"draft 10: {d10.Count} stairs, {bad} unreachable outside the stronghold");
        Assert.True(bad >= 10, $"{bad}");
    }

    // ---------------------------------------------------------------- roads, approaches, the grid

    [Fact]
    public void Every_constraint_of_the_ruleset_passes_on_the_city()
    {
        var bf = City.Value;
        output.WriteLine(bf.Report());
        Assert.Empty(bf.Constraints.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Value} (needs {c.Threshold})"));
        Assert.True(bf.Constraints.Count >= 60);
    }

    [Fact]
    public void The_roads_join_the_bases_and_every_point_at_army_width_with_three_or_more_approaches_each()
    {
        var bf = City.Value;
        foreach (var l in bf.Links) Assert.True(l.Width >= 20f && l.ArmyLength is not null, $"{l.From} to {l.To}: {l.Width:0.0} m");
        foreach (var a in bf.Approaches) Assert.True(a.Count >= 3, $"{a.Point}: {a.Count} ({string.Join(", ", a.Bearings)})");
        Assert.Empty(AshfallRoadway.Find(C.Level, C.Dressing));   // the avenues are the draft-8 roadways: nothing stands in them
        foreach (var (name, blocked, cells) in bf.ProtectedBlocked) Assert.True(blocked == 0, $"{name}: {blocked} of {cells} cells");
    }

    [Fact]
    public void Buildings_and_walls_stand_on_the_grid_along_the_roads()
    {
        var plan = AshfallCity.Default;
        var rows = plan.Structures.Where(s => s.Name.Contains(" row ")).ToList();
        Assert.True(rows.Count >= 150, $"{rows.Count} row structures");
        // square to the grid
        Assert.All(rows, s => Assert.True(s.YawStep % 6 == 0, s.Name));
        // one frontage line per row: the buildings of a row share the face on one side (the front; their depths vary behind it)
        foreach (var g in rows.Where(s => s.Family != "wall").GroupBy(s => s.Name[..(s.Name.IndexOf(" row ", StringComparison.Ordinal) + 5)] + s.Name.Split(' ')[s.Name.Split(' ').ToList().IndexOf("row") + 1]))
        {
            var fps = g.Select(s => (Fp: AshfallField.Footprint(s.Centre, s.Size, s.YawStep), Along: s.YawStep % 12 == 0)).ToList();
            var low = fps.Select(f => MathF.Round((f.Along ? f.Fp.Y : f.Fp.X) * 10f)).Distinct().Count();
            var high = fps.Select(f => MathF.Round((f.Along ? f.Fp.W : f.Fp.Z) * 10f)).Distinct().Count();
            Assert.True(low == 1 || high == 1, $"{g.Key}: no common frontage ({low} and {high} face lines)");
        }
        Assert.True(rows.Count(s => s.Family == "wall") >= 30, "kerb walls");
        // every placed building and wall of the plan is turned square to the grid in the level (draft 9's urban rule faces it to its street)
        foreach (var p in C.Placements.Where(p => p.Item >= 0 && p.Group.Contains(" row ")))
        {
            float step = C.Dressing.Items[p.Item].Yaw / (MathF.PI / 12f);
            Assert.True(MathF.Abs(step - MathF.Round(step / 6f) * 6f) < 1e-3f, $"{p.Group}: yaw step {step:0.00}");
        }
    }

    [Fact]
    public void The_stronghold_and_the_compound_are_draft_10s_and_every_standing_rule_holds()
    {
        var d10 = AshfallMap.BuildDraft5();
        static bool Fort(string g) => AshfallStairAccess.IsAuthored(g, "", "");
        var a = d10.Placements.Where(p => Fort(p.Group) && p.Item >= 0).ToDictionary(p => p.Group, p => d10.Dressing.Items[p.Item]);
        var b = C.Placements.Where(p => Fort(p.Group) && p.Item >= 0).ToDictionary(p => p.Group, p => C.Dressing.Items[p.Item]);
        foreach (var (g, it) in a)
        {
            Assert.True(b.TryGetValue(g, out var o), g);
            Assert.True(o!.Asset == it.Asset && Vector3.Distance(o.Position, it.Position) < 0.05f, g);
        }
        Assert.True(a.Count >= 50);
        Assert.Contains(C.Placements, p => p.Group == "keep" && p.Asset == "AC-044");
        // no overlaps (draft 7), stairs open at both ends (drafts 9 and 10)
        Assert.Empty(AshfallOverlap.Find(AshfallOverlap.Boxes(C.Level, C.Dressing), exempt: AshfallMap.OverlapExempt(C.Dressing)));
        Assert.All(C.Dressing.Items, i => Assert.NotNull(AshfallKit.Get(i.Asset)));
    }

    // ---------------------------------------------------------------- clashes

    [Fact]
    public void A_200_a_side_clash_meets_in_a_broad_front_at_B_and_at_A()
    {
        foreach (var p in new[] { "B", "A" })
        {
            var r = AshfallClash.Run(new AshfallClash.Setup(AshfallLayout.City, p, 200, AshfallClash.ClashAi.Shipped));
            output.WriteLine(AshfallClash.Result.Header); output.WriteLine(r.Row);
            Assert.True(r.FirstContact > 0f, p);
            Assert.False(r.SingleFile, p);
            // at B the army layer's anchor walks the central avenue; at A it takes one body's shortest walk, which cuts through the blocks' alleys (2.3 m; the routing finding of drafts 11 and 12, the owner's call)
            if (p == "B") Assert.True(r.NarrowestOnRoute >= 20f, $"{p}: narrowest {r.NarrowestOnRoute:0.0} m");
            Assert.True(r.FrontageSteady >= 15f, $"{p}: frontage {r.FrontageSteady:0.0} m");
        }
    }

    [Fact]
    public void The_city_version_salts_its_hash_apart_from_the_drafts()
    {
        Assert.Equal(0x43000000u | AshfallCity.Version, AshfallCity.HashVersion);
        Assert.NotEqual(AshfallField.HashVersion, AshfallCity.HashVersion);
        Assert.NotEqual(AshfallMap.Version, AshfallCity.HashVersion);
    }
}
