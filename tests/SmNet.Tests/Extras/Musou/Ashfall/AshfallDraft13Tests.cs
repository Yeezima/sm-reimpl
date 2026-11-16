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
/// Ashfall draft 13, the high-ground layout (owner, 2026-11-14: "another draft of the ashfall map. include large scaled up buildings again, and ensure that there are no dead end stairs, no stairs with blocked entrances, and no
/// weird things like a wall in front of a door ... devastator and tactical marines to have strong high ground positions to play from, and the ability for assault to leverage its ability to jump to gain an advantage on the high
/// ground"; docs/musou-ashfall-graybox.md "Draft 13 heights"). The map <c>tfw_war_heights</c> builds it; <c>tfw_war</c> stays the city (draft 12), <c>tfw_war_draft10</c> and <c>tfw_war_field</c> as they were.
/// From the generator alone (no asset bytes, no install).
/// </summary>
public class AshfallDraft13Tests(ITestOutputHelper output)
{
    /// <summary>The heights level's pin (heights version 1, default parameters). After a change of the generator or a refit of the kit's collision, take the new value from this test's failure.</summary>
    public const ulong PinnedHeightsHash = 0x0B35BFB12EF8FD3DUL;

    static readonly AshfallDraft5 H = AshfallMap.BuildDraft5(AshfallLayout.Heights);
    static readonly Lazy<AshfallBattlefield> Field = new(() => AshfallBattlefield.Measure(AshfallLayout.Heights));
    static readonly Lazy<List<AshfallStairAccess.Stair>> Access = new(() => AshfallStairAccess.Check(H));
    static readonly Lazy<AshfallHighGround.Result> High = new(() => AshfallHighGround.Analyse(H.Level, H.Dressing, Access.Value, new AshfallGrid(H.Level), H.Level.Points));
    static readonly Lazy<List<AshfallDoorClearance.Door>> Doors = new(() => AshfallDoorClearance.Find(H.Level, H.Dressing));

    static string Name(AshfallDraft5 d, int item) => d.Placements.FirstOrDefault(p => p.Item == item) is { } p ? $"{p.Group} ({p.Asset})" : $"item {item}";

    static HashSet<int> Authored(AshfallDraft5 d)
    {
        var auth = new HashSet<int>();
        foreach (var p in d.Placements) if (p.Item >= 0 && AshfallStairAccess.IsAuthored(p.Group, p.Family, p.Asset)) auth.Add(p.Item);
        return auth;
    }

    // ---------------------------------------------------------------- the switch and the rollback

    [Fact]
    public void Tfw_war_heights_builds_the_heights_layout_and_the_other_maps_stay_as_they_were()
    {
        Assert.Equal(AshfallLayout.Heights, TfwMap.LayoutOf(TfwMap.HeightsName));
        Assert.Equal(AshfallLayout.Heights, TfwMap.LayoutOf(TfwMap.Name));   // the 2026-11-14 ship: the default Musou map is draft 13
        Assert.Equal(AshfallLayout.City, TfwMap.LayoutOf(TfwMap.CityName));
        Assert.True(TfwMap.IsWarMap(TfwMap.CityName) && TfwMap.IsAshfall(TfwMap.CityName) && Protocol.IsMapName(TfwMap.CityName));
        Assert.Equal(AshfallLayout.Heights, AshfallLayouts.Parse("heights"));
        Assert.Equal("heights", AshfallLayout.Heights.Key());
        Assert.True(TfwMap.IsWarMap(TfwMap.HeightsName) && TfwMap.IsAshfall(TfwMap.HeightsName) && Protocol.IsMapName(TfwMap.HeightsName));
        // the rollbacks: the city (draft 12), draft 10 and the field, bit for bit
        Assert.Equal(AshfallDraft12Tests.PinnedCityHash, AshfallMap.BuildDraft5(AshfallLayout.City).Level.Hash);
        Assert.Equal(AshfallDraft5Tests.PinnedLevelHash, AshfallMap.BuildDraft5(AshfallLayout.Draft10).Level.Hash);
        Assert.Equal(AshfallDraft11Tests.PinnedFieldHash, AshfallMap.BuildDraft5(AshfallLayout.Field).Level.Hash);
        Assert.Equal(AshfallLayout.Heights, H.Level.Layout);
        output.WriteLine($"heights hash {H.Level.Hash:X16}");
        if (PinnedHeightsHash != 0UL) Assert.Equal(PinnedHeightsHash, H.Level.Hash);
        var a = TfwWorld.Load("", out var pa, TfwMap.HeightsName); var b = TfwWorld.Load("", out var pb, TfwMap.CityName);
        Assert.Null(pa); Assert.Null(pb);
        Assert.NotEqual(a!.Hash, b!.Hash);
        Assert.Equal(AshfallContent.ToMapContent(H.Level).Hash, H.Level.Hash);
        var hosted = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 4000, true, "", Content: "musou", Map: TfwMap.HeightsName));
        int i = hosted.IndexOf("--map");
        Assert.True(i >= 0 && hosted[i + 1] == TfwMap.HeightsName);
    }

    [Fact]
    public void The_heights_layout_is_deterministic_and_its_surface_seed_leaves_the_collision()
    {
        var again = AshfallMap.RebuildHeights(AshfallHeights.Defaults);
        Assert.Equal(H.Level.Hash, again.Level.Hash);
        Assert.Equal(H.Dressing.Items.Count, again.Dressing.Items.Count);
        Assert.Equal(H.Level.Hash, AshfallMap.RebuildHeights(AshfallHeights.Defaults with { SurfaceSeed = 7 }).Level.Hash);
        Assert.Equal(0x48000000u | AshfallHeights.Version, AshfallHeights.HashVersion);
        Assert.NotEqual(AshfallCity.HashVersion, AshfallHeights.HashVersion);
    }

    // ---------------------------------------------------------------- the owner's stair and door rules

    [Fact]
    public void Every_stair_is_walkable_up_and_down_and_open_at_both_ends()
    {
        var res = Access.Value;
        output.WriteLine($"{res.Count} stairs ({res.Count(r => r.Elevated)} with a raised foot), {res.Count(r => r.Authored)} authored");
        foreach (var r in res.Where(r => r.Problem is not null)) output.WriteLine($"{(r.Authored ? "[authored] " : "")}{Name(H, r.Ramp.Item)} at ({r.Ramp.Low.X:0.0}, {r.Ramp.Low.Y:0.0}): {r.Problem}");
        Assert.True(res.Count >= 100, $"{res.Count} stairs");
        Assert.Empty(res.Where(r => r.Problem is not null && !r.Authored).Select(r => $"{Name(H, r.Ramp.Item)}: {r.Problem}"));
        Assert.True(res.Count(r => r.Problem is not null) <= 2, "only the stronghold's authored flights may fail");
        var solids = AshfallStairs.Solids.Of(H.Level);
        // no stair with a blocked entrance: the foot and the landing are measured open on every solid, for every flight outside the authored pieces
        foreach (var r in res.Where(r => !r.Authored))
        {
            Assert.True(AshfallStairs.Measure(r.Ramp, solids, top: false, AshfallStairs.Approach).Open, $"{Name(H, r.Ramp.Item)}: foot blocked");
            Assert.True(AshfallStairs.Measure(r.Ramp, solids, top: true, AshfallStairs.LandingDepth).Open, $"{Name(H, r.Ramp.Item)}: landing blocked");
        }
        var flag = H.Dressing.Flags.Single(f => f.StartsWith("draft 12 stair access"));
        Assert.DoesNotContain("still fail", flag);
    }

    [Fact]
    public void No_flight_is_a_dead_end_outside_the_authored_stronghold()
    {
        var res = Access.Value; var high = High.Value;
        foreach (var (s, why) in high.DeadEnds) output.WriteLine($"{(res[s].Authored ? "[authored] " : "")}{Name(H, res[s].Ramp.Item)} at ({res[s].Ramp.High.X:0.0}, {res[s].Ramp.High.Y:0.0}): {why}");
        Assert.Empty(high.DeadEnds.Where(d => !res[d.Stair].Authored).Select(d => $"{Name(H, res[d.Stair].Ramp.Item)}: {d.Why}"));
        output.WriteLine($"{res.Count} flights checked, {high.DeadEnds.Count} dead ends (all authored)");
        Assert.True(res.Count(r => !r.Authored) >= 100);
        Assert.DoesNotContain("still open", H.Dressing.Flags.Single(f => f.StartsWith("draft 13 high ground")));
    }

    [Fact]
    public void No_item_stands_in_front_of_a_door()
    {
        var doors = Doors.Value; var auth = Authored(H);
        output.WriteLine($"{doors.Count} doors, {doors.Count(d => d.Blocker >= 0)} with another item within {AshfallDoorClearance.Depth:0} m in front");
        foreach (var d in doors.Where(d => d.Blocker >= 0)) output.WriteLine($"{(auth.Contains(d.Item) ? "[authored] " : "")}{Name(H, d.Item)} door at ({d.Centre.X:0.0}, {d.Centre.Y:0.0}), {d.Width:0.0} x {d.Height:0.0} m: {Name(H, d.Blocker)} in front");
        Assert.True(doors.Count >= 50, $"{doors.Count} doors");
        Assert.Empty(doors.Where(d => d.Blocker >= 0 && !(auth.Contains(d.Item) && auth.Contains(d.Blocker))).Select(d => $"{Name(H, d.Item)} door at ({d.Centre.X:0.0}, {d.Centre.Y:0.0}): {Name(H, d.Blocker)} in front"));
    }

    [Fact]
    public void The_door_check_finds_what_draft_12_left_standing_in_front_of_a_door()
    {
        // draft 12's city has the stronghold's ring pieces with a curtain wall in front of their doors (authored, copied from draft 10)
        var city = AshfallMap.BuildDraft5(AshfallLayout.City);
        var found = AshfallDoorClearance.Find(city.Level, city.Dressing).Where(d => d.Blocker >= 0).ToList();
        output.WriteLine($"draft 12: {found.Count} doors with an item in front");
        Assert.NotEmpty(found);
        Assert.All(found, d => Assert.True(d.Width >= AshfallDoorClearance.MinWidth && d.Height >= AshfallDoorClearance.MinHeight));
    }

    // ---------------------------------------------------------------- the high ground

    [Fact]
    public void Every_high_ground_position_is_reachable_on_foot_and_from_an_assault_jump_point()
    {
        var high = High.Value;
        Assert.True(high.Decks.Count >= 40, $"{high.Decks.Count} decks");
        foreach (var d in high.Decks.OrderByDescending(d => d.Height))
            output.WriteLine($"{d.Source} ({d.Centre.X:0}, {d.Centre.Y:0}) {d.Height:0.0} m over the ground, {d.Area:0} m2, sees {(d.Sees.Length == 0 ? "-" : d.Sees)}; on foot {(d.OnFoot ? "yes" : "NO")}; jump from ground {(d.JumpFromGround ? $"yes ({d.JumpTier}, from ({d.JumpFrom.X:0}, {d.JumpFrom.Y:0}))" : "NO")}; from {d.JumpFromDecks.Count} decks{(d.Authored ? " [authored]" : "")}");
        output.WriteLine($"{high.Decks.Count} high-ground positions (floor of at least {AshfallHighGround.MinArea:0} m2, {AshfallHighGround.MinRise:0} m or more over the ground); {high.Decks.Count(d => d.OnFoot)} on foot; {high.Decks.Count(d => d.JumpFromGround)} by an Assault jump from the ground; {high.Decks.Count(d => d.JumpFromDecks.Count > 0)} also from another deck");
        Assert.Empty(high.Decks.Where(d => !d.OnFoot).Select(d => $"{d.Source} ({d.Centre.X:0}, {d.Centre.Y:0}): not reached on foot"));
        Assert.Empty(high.Decks.Where(d => !d.JumpFromGround && !d.Authored).Select(d => $"{d.Source} ({d.Centre.X:0}, {d.Centre.Y:0}): no Assault jump point"));
    }

    [Fact]
    public void Devastators_and_Tacticals_have_strong_high_ground_over_every_point_and_Assault_can_jump_between_roofs()
    {
        var high = High.Value;
        // a strong position: a deck of at least 100 m2, 5 m or more up, that sees a point's centre; no deck sees all three (the brief: avoid one position dominating every objective)
        foreach (var pt in new[] { "A", "B", "C" })
        {
            var strong = high.Decks.Where(d => d.Area >= 100f && d.Height >= 5f && d.Sees.Contains(pt)).ToList();
            output.WriteLine($"point {pt}: {strong.Count} strong positions ({string.Join(", ", strong.Select(d => $"{d.Source} ({d.Centre.X:0}, {d.Centre.Y:0}) {d.Height:0.0} m"))})");
            Assert.True(strong.Count >= 3, $"{pt}: {strong.Count}");
        }
        Assert.DoesNotContain(high.Decks, d => d.Sees.Length == 3);
        // the overwatch terraces round each point: a medium firing platform each
        var terraces = H.Placements.Where(p => p.Group.Contains(" overwatch terrace ") && p.Item >= 0).ToList();
        output.WriteLine($"{terraces.Count} overwatch terraces");
        Assert.True(terraces.Count >= 9, $"{terraces.Count}");
        Assert.All(terraces, t => Assert.Equal("AC-050", t.Asset));
        // roof to roof: decks that an Assault jump from another deck reaches
        int roofs = high.Decks.Count(d => d.JumpFromDecks.Count > 0);
        output.WriteLine($"{roofs} decks reached by a jump from another deck");
        Assert.True(roofs >= 20, $"{roofs}");
    }

    [Fact]
    public void The_assault_jump_model_matches_the_spec_s_apexes_and_reaches_a_deck_from_the_ground()
    {
        Assert.Equal([3.79f, 11.63f, 17.54f], AssaultJump.Tiers.Select(t => t.Apex));
        foreach (var t in AssaultJump.Tiers)
        {
            float top = 0f;
            for (float d = 0f; d < 80f; d += 0.1f) top = MathF.Max(top, t.RiseAt(d));
            Assert.InRange(top, t.Apex - 0.2f, t.Apex + 0.2f);   // the sampled path peaks at the apex
        }
        // a 5.4 m deck is within the half hold: it lands beyond the apex, further out than 20 m and nearer than 45
        float landing = AssaultJump.Tiers[1].Landing(5.4f);
        Assert.InRange(landing, 20f, 45f);
        Assert.Equal(17.54f, AssaultJump.Reach);
    }

    // ---------------------------------------------------------------- the ruleset as on the city

    [Fact]
    public void Every_constraint_of_the_ruleset_passes_on_the_heights_layout()
    {
        var bf = Field.Value;
        output.WriteLine(bf.Report());
        Assert.Empty(bf.Constraints.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Value} (needs {c.Threshold})"));
        Assert.True(bf.Constraints.Count >= 60);
        foreach (var l in bf.Links) Assert.True(l.Width >= 20f && l.ArmyLength is not null, $"{l.From} to {l.To}: {l.Width:0.0} m");
        foreach (var a in bf.Approaches) Assert.True(a.Count >= 3, $"{a.Point}: {a.Count}");
        Assert.Empty(AshfallRoadway.Find(H.Level, H.Dressing));
    }

    [Fact]
    public void The_buildings_are_larger_than_the_city_s_and_stand_square_to_the_grid()
    {
        var plan = AshfallHeights.Default; var city = AshfallCity.Default;
        static double Mean(AshfallFieldPlan p) => p.Structures.Where(s => s.Name.Contains(" row ") && s.Family is "shell" or "bunker").Average(s => s.Size.X * s.Size.Y);
        output.WriteLine($"mean building footprint: heights {Mean(plan):0} m2, city {Mean(city):0} m2");
        Assert.True(Mean(plan) > Mean(city) * 1.2);
        Assert.All(plan.Structures.Where(s => s.Name.Contains(" row ")), s => Assert.True(s.YawStep % 6 == 0, s.Name));
    }
}
