using System.Numerics;
using SmClient.Extras.Warfront.Dress;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Fortress;
using static SmSim.Extras.Warfront.Fortress.WarfrontFortressMap;

namespace SmNet.Tests.Extras.Warfront.Maps;

/// <summary>
/// Warfront dressing part 1 (slice "dress"): the engine-free plans. A part of the fortress level with no dressing rule fails here (a later map change cannot bring a grey box back); no piece or kit item stands in the breach's 8 m gap, the gate's
/// span or the arena's openings; the far massing keeps the avenue clear and grows denser and taller toward the spire; the plans are deterministic. Visual only: nothing here touches the level, its hash or the wire. Every size is ours.
/// </summary>
public class WarfrontDressTests
{
    static readonly Lazy<WarfrontFortressBuild> Build_ = new(WarfrontFortressMap.Build);
    static WarfrontFortressBuild Fortress => Build_.Value;
    static readonly Lazy<DressPlan> Plan_ = new(() => WarfrontFortressDressPlan.Build(Fortress.Level, Fortress.Posts));
    static DressPlan Plan => Plan_.Value;

    [Fact]
    public void Every_part_of_the_fortress_has_a_dressing_rule()
    {
        var bare = new List<string>();
        for (int i = 0; i < Fortress.Level.Parts.Count; i++)
            if (WarfrontFortressDressPlan.Classify(Fortress.Level.Parts[i]) is null) bare.Add($"part {i} ({Fortress.Level.Parts[i].Kind})");
        Assert.True(bare.Count == 0, "parts with no dressing rule (drawn as default grey): " + string.Join(", ", bare));
        Assert.Equal(Fortress.Level.Parts.Count, Plan.Rules.Count);
    }

    [Fact]
    public void The_kit_table_uses_every_rule_and_hides_exactly_the_plated_parts()
    {
        foreach (var rule in Enum.GetValues<DressRule>().Where(r => r is not (DressRule.NotDressed or DressRule.ImperialWall or DressRule.Parapet or DressRule.ParapetStair or DressRule.CourtCover)))
            Assert.Contains(rule, Plan.Rules);   // a rule no part uses is a dead row: the table and the level agree
        for (int i = 0; i < Plan.Rules.Count; i++)
        {
            bool kitDraws = Plan.Rules[i] is DressRule.Ground or DressRule.Boundary;
            Assert.Equal(!kitDraws, Plan.HiddenParts.Contains(i));
        }
    }

    [Fact]
    public void Rules_follow_the_levels_own_shapes()
    {
        var rules = Fortress.Level.Parts.Select(WarfrontFortressDressPlan.Classify).ToList();
        Assert.Equal(1, rules.Count(r => r == DressRule.GateLintel));
        Assert.Equal(4, rules.Count(r => r == DressRule.ScrapTower));
        Assert.Equal(8, rules.Count(r => r == DressRule.StreetBlock));
        Assert.Equal(4, rules.Count(r => r == DressRule.Boundary));
        Assert.Equal(2, rules.Count(r => r == DressRule.RockCliff));
    }

    // ---- nothing in the openings
    static (Vector2 Min, Vector2 Max) Foot(DressPiece p) { var (mn, mx) = p.Bounds(); return (new(mn.X, mn.Y), new(mx.X, mx.Y)); }
    static bool Meets((Vector2 Min, Vector2 Max) a, float x0, float y0, float x1, float y1) => a.Min.X < x1 && a.Max.X > x0 && a.Min.Y < y1 && a.Max.Y > y0;

    [Fact]
    public void Nothing_stands_in_the_breachs_gap()
    {
        const float m = WarfrontFortressDressPlan.OpeningMargin;
        foreach (var p in Plan.Pieces)
            Assert.False(Meets(Foot(p), BreachX0 - m, GateSouth - m, BreachX1 + m, GateNorth + m), $"{p.Shape} {p.Mat} at {p.Base} meets the breach's gap");
        foreach (var it in Plan.KitItems)
        {
            float r = (MathF.Abs(it.Size.X) + MathF.Abs(it.Size.Y)) / 2f;   // a generous turned footprint
            Assert.False(it.Position.X + r > BreachX0 - m && it.Position.X - r < BreachX1 + m && it.Position.Y + r > GateSouth - m && it.Position.Y - r < GateNorth + m && it.Position.Z < WallTop,
                $"{it.Asset} at {it.Position} meets the breach's gap");
        }
    }

    [Fact]
    public void Nothing_low_stands_in_the_gate_or_under_the_lintel()
    {
        foreach (var p in Plan.Pieces.Where(p => p.Base.Z < GateClear))
            Assert.False(Meets(Foot(p), -GateWidth / 2f, GateSouth, GateWidth / 2f, GateNorth), $"{p.Shape} {p.Mat} at {p.Base} stands in the gate");
    }

    [Fact]
    public void Nothing_stands_in_the_arenas_openings()
    {
        float so = SouthOpening / 2f, sd = SideOpening / 2f;
        foreach (var p in Plan.Pieces.Where(p => p.Base.Z < ShellTop))
        {
            var f = Foot(p);
            Assert.False(Meets(f, -so, ShellSouth, so, ShellSouth + ShellWall), $"{p.Shape} {p.Mat} at {p.Base} stands in the arena's south opening");
            foreach (float sx in new[] { -1f, 1f })
            {
                float x0 = MathF.Min(sx * ShellHalf, sx * (ShellHalf - ShellWall)), x1 = MathF.Max(sx * ShellHalf, sx * (ShellHalf - ShellWall));
                Assert.False(Meets(f, x0, PlazaCentreY - sd, x1, PlazaCentreY + sd), $"{p.Shape} {p.Mat} at {p.Base} stands in a side opening");
            }
        }
    }

    [Fact]
    public void Nothing_hangs_over_an_arena_opening()
    {
        // the openings have no lintel (open to the sky): no piece spans them above, either
        float so = SouthOpening / 2f, sd = SideOpening / 2f;
        foreach (var p in Plan.Pieces)
        {
            var f = Foot(p);
            Assert.False(Meets(f, -so + 0.1f, ShellSouth, so - 0.1f, ShellSouth + ShellWall) && p.Shape == DressShape.Box && p.Size.X > 2f * so, $"{p.Mat} at {p.Base} bridges the south opening");
            Assert.False(Meets(f, ShellHalf - ShellWall, PlazaCentreY - sd + 0.1f, ShellHalf, PlazaCentreY + sd - 0.1f) && p.Base.Z > 5f, $"{p.Mat} at {p.Base} hangs in the east opening");
        }
    }

    [Fact]
    public void Kit_items_are_catalogued_decoration_without_collision_parts()
    {
        foreach (var it in Plan.KitItems)
        {
            Assert.Equal(AshfallDressRole.Decoration, it.Role);
            var model = AshfallKit.Get(it.Asset);   // throws for a model the catalogue does not hold
            Assert.True(model.RawSize.X > 0f);
        }
        Assert.Empty(Plan.KitItems.Where(i => i.Part >= 0));
    }

    [Fact]
    public void Posts_that_hold_elites_get_a_sigil_and_a_totem()
    {
        var elites = Fortress.Posts.Where(p => p.Group == "elite").ToList();
        Assert.NotEmpty(elites);
        foreach (var post in elites)
            Assert.Contains(Plan.Pieces, p => p.Shape == DressShape.Disc && MathF.Abs(p.Base.X - post.Position.X) < 0.01f && MathF.Abs(p.Base.Y - post.Position.Y) < 0.01f);
    }

    [Fact]
    public void The_plan_is_deterministic()
    {
        var again = WarfrontFortressDressPlan.Build(Fortress.Level, Fortress.Posts);
        Assert.Equal(Plan.Pieces.Count, again.Pieces.Count);
        for (int i = 0; i < Plan.Pieces.Count; i++) Assert.Equal(Plan.Pieces[i], again.Pieces[i]);
        Assert.Equal(WarfrontFortressDressPlan.Outside().Count, WarfrontFortressDressPlan.Outside().Count);
    }

    [Fact]
    public void The_outside_stands_clear_of_the_map_and_behind_the_arena()
    {
        foreach (var p in WarfrontFortressDressPlan.Outside())
        {
            var (mn, mx) = p.Bounds();
            Assert.True(mn.X > HalfWidth + 4f || mx.X < -HalfWidth - 4f || mn.Y > NorthEdge + 4f || mx.Y < SouthEdge - 4f, $"{p.Shape} at {p.Base} stands inside the map's box");
        }
        Assert.Contains(WarfrontFortressDressPlan.Outside(), p => p.Shape == DressShape.Spike && p.Base.Y > NorthEdge && MathF.Abs(p.Base.X) < 1f);   // the spire, directly behind the arena
    }

    // ---- the far massing
    [Theory]
    [InlineData(794f, 2050f, 1u)]
    [InlineData(764f, 1530f, 2u)]
    [InlineData(304f, 1010f, 3u)]
    public void Far_massing_keeps_the_avenue_clear_and_grows_toward_the_spire(float y0, float y1, uint seed)
    {
        var blocks = WarfrontFarMassingPlan.Blocks(y0, y1, seed);
        Assert.NotEmpty(blocks);
        foreach (var b in blocks.Where(b => b.SizeX < 30f))   // the processional line stands at the avenue's side (inside AvenueClear + its own half width)
            Assert.True(MathF.Abs(b.X) - b.SizeX / 2f >= WarfrontFarMassingPlan.AvenueClear - 0.5f, $"a block at {b.X},{b.Y} reaches into the avenue");
        float third = (y1 - y0) / 3f;
        var first = blocks.Where(b => b.Y < y0 + third).ToList(); var last = blocks.Where(b => b.Y > y1 - third).ToList();
        Assert.True(last.Count > first.Count, $"density grows toward the spire ({first.Count} blocks in the first third, {last.Count} in the last)");
        Assert.True(last.Average(b => b.Height) > first.Average(b => b.Height), "the blocks grow taller toward the spire");
        Assert.All(blocks, b => Assert.InRange(b.Y, y0 - 20f, y1 + 20f));
        Assert.Equal(blocks, WarfrontFarMassingPlan.Blocks(y0, y1, seed));   // deterministic
    }

    [Fact]
    public void Far_massing_range_starts_past_the_neighbour_copy_and_ends_short_of_the_spire()
    {
        var (a0, a1) = WarfrontFarMassingPlan.Range(true, 520f, 244f, 2190f);
        Assert.Equal(794f, a0); Assert.Equal(2190f - WarfrontFarMassingPlan.SpireClearance, a1);
        var (c0, _) = WarfrontFarMassingPlan.Range(false, 520f, 244f, 1150f);
        Assert.Equal(304f, c0);   // node 3 has no neighbour to the north: the massing starts past the band's own edge
    }

    // ---- the HQ exterior (part 2)
    static readonly Lazy<SmSim.Extras.Warfront.Hq.WarfrontHqBuild> Hq_ = new(SmSim.Extras.Warfront.Hq.WarfrontHqMap.Build);
    static readonly Lazy<DressPlan> HqPlan_ = new(() => WarfrontHqDressPlan.Build(Hq_.Value.Level));

    [Fact]
    public void Every_part_of_the_hq_has_a_dressing_rule_and_the_interior_is_left_alone()
    {
        var level = Hq_.Value.Level;
        var bare = new List<string>();
        for (int i = 0; i < level.Parts.Count; i++)
            if (WarfrontHqDressPlan.Classify(level.Parts[i]) is null) bare.Add($"part {i} ({level.Parts[i].Kind})");
        Assert.True(bare.Count == 0, "HQ parts with no dressing rule: " + string.Join(", ", bare));
        // the keep, the tunnels, the cross passage and the chamber (y at or over 40 m, W13d's) are not dressed: nothing of the plan stands there but the outside
        for (int i = 0; i < level.Parts.Count; i++)
        {
            var (mn, _) = WarfrontFortressDressPlan.Aabb(level.Parts[i]);
            if (level.Parts[i].Kind is AshfallKind.Stronghold or AshfallKind.Floor || (level.Parts[i].Kind == AshfallKind.Cover && mn.Y >= SmSim.Extras.Warfront.Hq.WarfrontHqMap.KeepSouth))
                Assert.Equal(DressRule.NotDressed, HqPlan_.Value.Rules[i]);
        }
        Assert.Contains(DressRule.ImperialWall, HqPlan_.Value.Rules); Assert.Contains(DressRule.Parapet, HqPlan_.Value.Rules);
        Assert.Contains(DressRule.ParapetStair, HqPlan_.Value.Rules); Assert.Contains(DressRule.CourtCover, HqPlan_.Value.Rules); Assert.Contains(DressRule.RockCliff, HqPlan_.Value.Rules);
        Assert.Equal(HqPlan_.Value.KitItems.Count(i => i.Asset == WarfrontHqDressPlan.CoverModel), HqPlan_.Value.Rules.Count(r => r == DressRule.CourtCover));   // one kit barrier a cover wall
    }

    [Fact]
    public void Nothing_stands_within_forty_metres_of_the_ramps_foot_or_on_the_ramp_or_in_the_gate()
    {
        var plan = HqPlan_.Value;
        var foot = new Vector2(0f, SmSim.Extras.Warfront.Hq.WarfrontHqMap.RampSouth);
        const float rampHalf = SmSim.Extras.Warfront.Hq.WarfrontHqMap.RampWidth / 2f;
        foreach (var p in plan.Pieces.Where(p => p.Base.Z < 20f && p.Base.Y < SmSim.Extras.Warfront.Hq.WarfrontHqMap.RampNorth - 8f))   // everything on the south side of the wall (the wall's own skin, buttresses and banners stand at its face)
        {
            var f = Foot(p);
            var nearest = new Vector2(Math.Clamp(foot.X, f.Min.X, f.Max.X), Math.Clamp(foot.Y, f.Min.Y, f.Max.Y));
            Assert.True(Vector2.Distance(nearest, foot) >= WarfrontHqDressPlan.FootClear - 0.01f, $"{p.Shape} {p.Mat} at {p.Base} is within 40 m of the ramp's foot");
            Assert.False(Meets(f, -rampHalf, SmSim.Extras.Warfront.Hq.WarfrontHqMap.RampSouth, rampHalf, SmSim.Extras.Warfront.Hq.WarfrontHqMap.RampNorth), $"{p.Shape} {p.Mat} at {p.Base} stands on the ramp");
        }
        foreach (var it in plan.KitItems.Where(i => i.Asset != WarfrontHqDressPlan.CoverModel && i.Position.Y < SmSim.Extras.Warfront.Hq.WarfrontHqMap.RampNorth))
        {
            float r = (it.Size.X + it.Size.Y) / 2f;
            Assert.True(Vector2.Distance(new Vector2(it.Position.X, it.Position.Y), foot) - r >= WarfrontHqDressPlan.FootClear - 0.01f, $"{it.Asset} at {it.Position} is within 40 m of the ramp's foot");
        }
        foreach (var p in plan.Pieces.Where(p => p.Base.Z < SmSim.Extras.Warfront.Hq.WarfrontHqMap.PlateauZ + SmSim.Extras.Warfront.Hq.WarfrontHqMap.GateHeight))
            Assert.False(Meets(Foot(p), -SmSim.Extras.Warfront.Hq.WarfrontHqMap.GateWidth / 2f, SmSim.Extras.Warfront.Hq.WarfrontHqMap.WallSouth, SmSim.Extras.Warfront.Hq.WarfrontHqMap.GateWidth / 2f, SmSim.Extras.Warfront.Hq.WarfrontHqMap.WallNorth), $"{p.Shape} {p.Mat} at {p.Base} stands in the gate");
    }

    [Fact]
    public void The_hq_plan_is_deterministic_and_its_kit_models_are_catalogued()
    {
        var again = WarfrontHqDressPlan.Build(Hq_.Value.Level);
        Assert.Equal(HqPlan_.Value.Pieces.Count, again.Pieces.Count);
        Assert.All(HqPlan_.Value.KitItems, it => Assert.True(AshfallKit.Get(it.Asset).RawSize.X > 0f));
        foreach (var p in WarfrontHqDressPlan.Outside())
        {
            var (mn, mx) = p.Bounds();
            Assert.True(mn.X > SmSim.Extras.Warfront.Hq.WarfrontHqMap.HalfWidth + 4f || mx.X < -SmSim.Extras.Warfront.Hq.WarfrontHqMap.HalfWidth - 4f || mn.Y > SmSim.Extras.Warfront.Hq.WarfrontHqMap.NorthEdge + 4f || mx.Y < SmSim.Extras.Warfront.Hq.WarfrontHqMap.SouthEdge - 4f, $"{p.Shape} at {p.Base} stands inside the map's box");
        }
    }

    // ---- second pass: the game's own pieces and install props
    [Fact]
    public void The_fortress_has_a_hundred_or_more_kit_pieces_along_its_streets_and_blocks()
    {
        Assert.True(Plan.KitItems.Count >= 100, $"kit pieces: {Plan.KitItems.Count}");
        Assert.True(Plan.KitItems.Select(i => i.Asset).Distinct().Count() >= 8);
    }

    [Fact]
    public void Install_props_are_known_keys_with_fallbacks_where_they_carry_a_look_and_stand_clear_of_the_openings()
    {
        var all = Plan.Props.Concat(HqPlan_.Value.Props).ToList();
        Assert.NotEmpty(Plan.Props); Assert.NotEmpty(HqPlan_.Value.Props);
        Assert.All(all, p => Assert.Contains(p.Key, DressPropKeys.All));
        foreach (var key in new[] { DressPropKeys.Waaagh, DressPropKeys.Junkpile }) Assert.True(Plan.Fallbacks.ContainsKey(key) && Plan.Fallbacks[key].Count > 0, key);
        Assert.True(HqPlan_.Value.Fallbacks.ContainsKey(DressPropKeys.SmBanner));
        foreach (var p in Plan.Props)
        {
            Assert.False(p.Base.X > BreachX0 - 1.5f && p.Base.X < BreachX1 + 1.5f && p.Base.Y > GateSouth - 1.5f && p.Base.Y < GateNorth + 1.5f, $"{p.Key} at {p.Base} meets the breach's gap");
            Assert.False(MathF.Abs(p.Base.X) < GateWidth / 2f + 1.5f && p.Base.Y > GateSouth - 1.5f && p.Base.Y < GateNorth + 1.5f && p.Base.Z < GateClear, $"{p.Key} at {p.Base} stands in the gate");
            Assert.False(MathF.Abs(p.Base.X) < SouthOpening / 2f + 1f && p.Base.Y > ShellSouth - 1f && p.Base.Y < ShellSouth + ShellWall + 1f && p.Base.Z < ShellTop, $"{p.Key} at {p.Base} stands in the south opening");
            Assert.False(MathF.Abs(p.Base.X) > ShellHalf - ShellWall - 1f && MathF.Abs(p.Base.X) < ShellHalf + 1f && MathF.Abs(p.Base.Y - PlazaCentreY) < SideOpening / 2f + 1f && p.Base.Z < ShellTop, $"{p.Key} at {p.Base} stands in a side opening");
        }
        var foot = new Vector2(0f, SmSim.Extras.Warfront.Hq.WarfrontHqMap.RampSouth);
        foreach (var p in HqPlan_.Value.Props.Where(p => p.Base.Y < SmSim.Extras.Warfront.Hq.WarfrontHqMap.RampNorth))
        {
            Assert.True(Vector2.Distance(new(p.Base.X, p.Base.Y), foot) >= WarfrontHqDressPlan.FootClear + 6f, $"{p.Key} at {p.Base} is within 40 m of the ramp's foot");
            Assert.False(MathF.Abs(p.Base.X) < SmSim.Extras.Warfront.Hq.WarfrontHqMap.RampWidth / 2f + 3f && p.Base.Y > SmSim.Extras.Warfront.Hq.WarfrontHqMap.RampSouth - 20f, $"{p.Key} at {p.Base} stands on the ramp");
        }
    }

    [Fact]
    public void The_hq_rock_masses_get_a_broken_skyline_of_rock_pieces_and_kit_boulders_at_their_feet()
    {
        Assert.True(HqPlan_.Value.Pieces.Count(p => p.Mat == DressMat.Rock) >= 40);
        Assert.Contains(HqPlan_.Value.KitItems, i => i.Asset == "AC-030");
        var foot = new Vector2(0f, SmSim.Extras.Warfront.Hq.WarfrontHqMap.RampSouth);
        foreach (var it in HqPlan_.Value.KitItems.Where(i => i.Asset != WarfrontHqDressPlan.CoverModel && i.Position.Y < SmSim.Extras.Warfront.Hq.WarfrontHqMap.RampNorth))
            Assert.True(Vector2.Distance(new(it.Position.X, it.Position.Y), foot) - (it.Size.X + it.Size.Y) / 2f >= WarfrontHqDressPlan.FootClear - 0.01f, $"{it.Asset} at {it.Position}");
    }
}
