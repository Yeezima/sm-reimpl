using System.Diagnostics;
using System.Numerics;
using System.Text;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Ashfall;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall draft 5 (docs/musou-ashfall-graybox.md "Draft 5"): one asset per graybox structure, the level's collision from the assets. Checks the pinned hashes, the tagging of the v5 generator, the collision transform (the item contract),
/// the places that stay clear, the stronghold's breaches, the walkable risers and (slow) the enemies' graph with the report of every v5 walkable place as kept, changed or lost. From the generator alone: no asset bytes, no install.
/// </summary>
public class AshfallDraft5Tests(ITestOutputHelper output)
{
    static readonly AshfallDraft5 D = AshfallMap.BuildDraft5();
    static AshfallLevel Level => D.Level;
    static AshfallDressing Dressing => D.Dressing;

    /// <summary>
    /// The level's hash (version 11, draft 6 with the Meshy expansion placed; version 10, draft 6 before it, was 0xC1D1D8A1118A55AF; draft 5's version 9 was 0x6A803A7703280536). A change of the pass, the pools, the fitted boxes or the generator without a new version fails here, not at a client's handshake.
    /// The one level pin: after a refit of the collision (<c>AshfallKitCollision.g.cs</c>) take the new value from this test's failure.
    /// </summary>
    public const ulong PinnedLevelHash = 0xA47761F67B43DC1CUL;   // version 17: draft 10 (lanes to every point, a 3 m clear zone at every stair's foot, walkable stairs, no building with a stair without access; 0xD20B0CBDD8445C42 before the stair-access rule, 0xA22539A7248E21E1 draft 9); version 16: draft 9, late batch F (stairs from the meshes, clear stair feet and landings, no stacked models, the urban rules; 0x058B350D285DE351 before); version 15: the openings pass (opening-aware merge for debris with openings, 0x2BB88406D096C7EF before); version 14: draft 8 (roadways, 0xB2335BB6CF43E872 alone) with the window pass (0x2D90FDEF0587A5AA alone); draft 7 was 0x29D435A1D94CD3CF

    /// <summary>The v5 graybox's hash (pinned since pass 3, and again by the draft 3 and 5 work).</summary>
    const ulong PinnedGrayboxHash = 0xAE096E323BB5A4D3UL;

    static bool IsGround(AshfallKind k) => k is AshfallKind.Ground or AshfallKind.Road or AshfallKind.Paving or AshfallKind.Curb or AshfallKind.Boundary or AshfallKind.Objective;

    static (Vector3 Min, Vector3 Max) Bounds(AshfallPart p) => (p.Corners.Aggregate(Vector3.Min), p.Corners.Aggregate(Vector3.Max));

    // ---------------------------------------------------------------- pins

    [Fact]
    public void The_graybox_is_v5_exactly_and_the_draft_6_level_has_its_own_pinned_hash()
    {
        var gb = AshfallMap.BuildGraybox();
        Assert.Equal(PinnedGrayboxHash, gb.Hash);
        Assert.Equal(5u, AshfallMap.GrayboxVersion);
        Assert.Equal(17u, AshfallMap.Version);
        Assert.NotEqual(gb.Hash, Level.Hash);
        Assert.Equal(PinnedLevelHash, Level.Hash);
        Assert.Equal(AshfallContent.ToMapContent(Level).Hash, Level.Hash);
        // The ground kinds and the boundary are v5's, bit for bit and in order.
        var ground = gb.Parts.Where(p => IsGround(p.Kind)).ToList();
        for (int i = 0; i < ground.Count; i++) { Assert.Equal(ground[i].Kind, Level.Parts[i].Kind); Assert.Equal(ground[i].Corners, Level.Parts[i].Corners); }
        // draft 10: then the lanes' Road parts (AshfallLanes), then nothing of the ground's kinds
        var lanes = Level.Parts.Skip(ground.Count).Take(AshfallLanes.Connectors.Length).ToList();
        Assert.All(lanes, p => Assert.Equal(AshfallKind.Road, p.Kind));
        Assert.All(Level.Parts.Skip(ground.Count + lanes.Count), p => Assert.False(IsGround(p.Kind)));
        Assert.Equal(1u, AshfallMap.VariationSeed);
    }

    [Fact]
    public void Every_non_ground_graybox_part_is_tagged_and_every_tagged_structure_has_one_item_or_a_logged_reason()
    {
        var gb = D.Graybox;
        var parts = gb.Level.Parts;
        for (int i = 0; i < parts.Count; i++)
            if (!IsGround(parts[i].Kind)) Assert.True(gb.PartGroup![i] >= 0, $"part {i} ({parts[i].Kind}) has no structure group");
        var tagged = gb.PartGroup!.Where(g => g >= 0).Distinct().Select(g => gb.Groups![g]).ToList();
        var logged = D.Placements.ToList();
        int stronghold = 0;
        foreach (var g in tagged)
        {
            var mine = logged.Where(l => l.Group == g.Name || l.Group.StartsWith(g.Name + " ")).ToList();
            Assert.True(mine.Count > 0, $"structure '{g.Name}' ({g.Family}) was neither placed nor dropped with a reason");
            if (g.Family.StartsWith("stronghold-leg")) { stronghold++; Assert.True(mine.Count(l => l.Item >= 0) >= 2, g.Name + " is not a line of items"); }
            else Assert.All(mine, l => Assert.True(l.Item >= 0 || l.Outcome.StartsWith("dropped"), l.Group + ": " + l.Outcome));
        }
        Assert.Equal(5, stronghold);   // the five ring legs
        output.WriteLine($"{tagged.Count} tagged structures, {logged.Count(l => l.Item >= 0)} items placed, {logged.Count(l => l.Item < 0)} dropped");
        // Tags are not part of the graybox's hash: a second build of the graybox gives the same hash and groups.
        Assert.Equal(PinnedGrayboxHash, AshfallMap.BuildGrayboxWithFeatures().Level.Hash);
    }

    // ---------------------------------------------------------------- the item contract and the collision transform

    [Fact]
    public void The_item_transform_maps_a_known_raw_box_the_way_the_client_draws_it()
    {
        // AC-001 raw bounds (-1, -0.4396, -0.2479)..(1, 0.4396, 0.2479); placed at (10, 20) on z 1, size 8 x 2 x 4, turned 90 degrees.
        var m = AshfallKit.Get("AC-001");
        var it = new AshfallDressItem("AC-001", new Vector3(10f, 20f, 1f), MathF.PI / 2f, new Vector3(8f, 2f, 4f), false, AshfallDressRole.Prop, -1);
        // raw min: local x -4, local y = (0.5 - 0) * 2 = +1 (glTF -Z is local Y), up 0 -> turned: (10 - 1, 20 - 4)
        var lo = it.ToWorld(m.RawMin);
        Assert.Equal(9f, lo.X, 1e-4f); Assert.Equal(16f, lo.Y, 1e-4f); Assert.Equal(1f, lo.Z, 1e-4f);
        var hi = it.ToWorld(m.RawMax);
        Assert.Equal(11f, hi.X, 1e-4f); Assert.Equal(24f, hi.Y, 1e-4f); Assert.Equal(5f, hi.Z, 1e-4f);
        // the model's front (glTF +Z) faces local -Y: at yaw 0 the raw +Z face lies on the south side
        var flat = it with { Yaw = 0f };
        Assert.True(flat.ToWorld(new Vector3(0f, 0f, m.RawMax.Z)).Y < flat.ToWorld(new Vector3(0f, 0f, m.RawMin.Z)).Y);
        // swapped: glTF Z is local X and glTF X is local Y
        var sw = flat with { SwapXZ = true };
        Assert.Equal(10f - 4f, sw.ToWorld(new Vector3(0f, 0f, m.RawMin.Z)).X, 1e-4f);
        Assert.Equal(20f - 1f, sw.ToWorld(new Vector3(m.RawMin.X, 0f, 0f)).Y, 1e-4f);
    }

    [Fact]
    public void An_items_collision_boxes_are_the_models_fitted_boxes_moved_by_that_transform()
    {
        // A tower is not coarsened (only covers and rubble are): every fitted box of the model, minus dust under 0.15 m, is a part whose centre is the transformed raw box centre.
        int checkedItems = 0;
        for (int i = 0; i < Dressing.Items.Count; i++)
        {
            var it = Dressing.Items[i];
            if (it.Role != AshfallDressRole.Prop || it.Asset != "AC-010") continue;
            var (first, count) = Dressing.ItemParts[i];
            var expected = new List<Vector3>();
            foreach (var (mn, mx) in AshfallKitCollision.Boxes(it.Asset))
            {
                var a = it.ToWorld(mn); var b = it.ToWorld(mx);
                float dx = (mx.X - mn.X) / AshfallKit.Get(it.Asset).RawSize.X * it.Size.X, dy = (mx.Z - mn.Z) / AshfallKit.Get(it.Asset).RawSize.Z * it.Size.Y, dz = (mx.Y - mn.Y) / AshfallKit.Get(it.Asset).RawSize.Y * it.Size.Z;
                if (dx < 0.15f && dy < 0.15f && dz < 0.15f) continue;
                expected.Add((a + b) / 2f);
            }
            var actual = Enumerable.Range(first, count).Where(p => Level.Parts[p].Kind != AshfallKind.Stair).Select(p => { var (mn, mx) = Bounds(Level.Parts[p]); return (mn + mx) / 2f; }).ToList();
            Assert.Equal(expected.Count, actual.Count);
            foreach (var c in expected) Assert.Contains(actual, a => Vector3.Distance(a, c) < 0.02f);
            checkedItems++;
        }
        Assert.True(checkedItems > 0);
    }

    [Fact]
    public void Each_items_collision_lies_inside_its_transformed_model_box()
    {
        const float eps = 0.02f;
        for (int i = 0; i < Dressing.Items.Count; i++)
        {
            var it = Dressing.Items[i];
            if (it.Role != AshfallDressRole.Prop) continue;
            var (first, count) = Dressing.ItemParts[i];
            float s = MathF.Sin(it.Yaw), c = MathF.Cos(it.Yaw);
            for (int p = first; p < first + count; p++)
                foreach (var v in Level.Parts[p].Corners)
                {
                    float dx = v.X - it.Position.X, dy = v.Y - it.Position.Y;
                    float lx = c * dx + s * dy, ly = -s * dx + c * dy;   // into the item's local frame
                    Assert.True(MathF.Abs(lx) <= it.Size.X / 2f + eps && MathF.Abs(ly) <= it.Size.Y / 2f + eps && v.Z >= it.Position.Z - eps && v.Z <= it.Position.Z + it.Size.Z + eps,
                        $"item {i} ({it.Asset}) part {p} corner {v} is outside the item's box");
                }
        }
    }

    /// <summary>
    /// Draft 6 (deliberate change of draft 5's "within the structure's bounds grown by 1 m"): every item grew 1.5x about its centre with its foot kept, so it stays within its structure's bounds scaled 1.5x about their
    /// centre and grown by 1.5 m (draft 5's 1 m, grown), plus the distance it was moved back from a protected place (logged). Bridges follow their ends and backdrop blocks grow away from the boundary: not checked here.
    /// The walkable models' cell rule (a scaled cell at most the 0.4 m step) no longer holds for every item after the growth: it is reported, and it holds for every platform draft 6 adds.
    /// </summary>
    [Fact]
    public void Items_stay_within_their_structures_bounds_grown_1_5x_and_the_walkable_cell_rule_is_reported()
    {
        var props = Dressing.Items.Select((it, i) => (it, i)).Where(x => x.it.Role == AshfallDressRole.Prop).ToList();
        Assert.Equal(Dressing.Structures!.Count, props.Count);
        int checkedItems = 0;
        for (int k = 0; k < props.Count; k++)
        {
            var it = props[k].it; var st = Dressing.Structures[k];
            var log = D.Placements.First(l => l.Item == props[k].i && l.Asset == it.Asset);   // (the plinth's line names the A dais' item)
            if (log.Family is "bridge" or "backdrop") continue;
            float moved = 0f;   // every logged move adds up (draft 7: the overlap rule's move is logged beside the growth's)
            for (int at = log.Outcome.IndexOf("moved ", StringComparison.Ordinal); at >= 0; at = log.Outcome.IndexOf("moved ", at + 6, StringComparison.Ordinal))
                moved += float.Parse(log.Outcome[(at + 6)..].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            float s = MathF.Sin(it.Yaw), c = MathF.Cos(it.Yaw);
            // the placer's 1 m margin is along the item's own axes (grown 1.5x): turned by the yaw it reaches |cos| + |sin| times as far along the world axes (phase 2: a square model on a small turned pile showed it)
            float tol = 1.5f * (MathF.Abs(c) + MathF.Abs(s)) + moved + 0.02f;
            var mid = (st.Min + st.Max) / 2f; var half = (st.Max - st.Min) / 2f * 1.5f;
            float ex = MathF.Abs(c) * it.Size.X / 2f + MathF.Abs(s) * it.Size.Y / 2f, ey = MathF.Abs(s) * it.Size.X / 2f + MathF.Abs(c) * it.Size.Y / 2f;
            Assert.True(it.Position.X - ex >= mid.X - half.X - tol && it.Position.X + ex <= mid.X + half.X + tol && it.Position.Y - ey >= mid.Y - half.Y - tol && it.Position.Y + ey <= mid.Y + half.Y + tol,
                $"{it.Asset} item {props[k].i} ({log.Group}) leaves its structure's bounds grown 1.5x");
            Assert.True(it.Position.Z >= st.Min.Z - 0.01f && it.Position.Z + it.Size.Z <= st.Min.Z + 1.5f * (st.Max.Z - st.Min.Z) + 0.01f, $"{it.Asset} item {props[k].i} ({log.Group}): foot or height off");
            checkedItems++;
        }
        Assert.True(checkedItems > 450, $"{checkedItems} items checked");   // draft 9: lone debris, stacked models and pieces that kept a narrow gap left out   // draft 7: 677 Props (the overlap rule left 37 out); draft 8: the roadway rule leaves out what stood in the roads
        // The walkable models' cell, scaled up, against the 0.4 m step (a riser no thicker than a cell): reported for the grown items, held by the added platforms.
        var walkable = AshfallKitFit.WalkableIds;
        int over = 0, total = 0;
        foreach (var (it, i) in props.Where(x => walkable.Contains(x.it.Asset)))
        {
            float cell = AshfallKitCollision.Metrics(it.Asset).CellSize * it.Size.Z / AshfallKit.Get(it.Asset).RawSize.Y;
            total++;
            bool added = D.Placements.First(l => l.Item == i && l.Asset == it.Asset).Group.StartsWith("extra platform");
            if (added) Assert.True(cell <= 0.4f + 1e-3f, $"added platform {i}: cell {cell:0.00} m after scaling");
            if (cell > 0.4f + 1e-3f) { over++; output.WriteLine($"over the step: {it.Asset} item {i} at ({it.Position.X:0}, {it.Position.Y:0}): cell {cell:0.00} m"); }
        }
        output.WriteLine($"walkable-model items: {total}, scaled cell over the 0.4 m step: {over}");
    }

    // ---------------------------------------------------------------- places that stay clear

    [Fact]
    public void Start_areas_spawns_lanes_capture_zones_and_the_boundarys_edge_stay_clear_of_collision()
    {
        var solids = Level.Parts.Where(p => p.Solid && !IsGround(p.Kind)).Select(Bounds).Where(b => b.Min.Z < 8f).ToList();
        void NoneIn(string what, Func<(Vector3 Min, Vector3 Max), bool> hit)
        {
            var bad = solids.Where(hit).ToList();
            Assert.True(bad.Count == 0, $"{what}: {bad.Count} collision boxes, first {(bad.Count > 0 ? bad[0].Min + " .. " + bad[0].Max : "")}");
        }
        foreach (var a in Level.Areas)
        {
            var lo = new Vector2(a.Center.X - a.Size.X / 2f, a.Center.Y - a.Size.Y / 2f); var hi = new Vector2(a.Center.X + a.Size.X / 2f, a.Center.Y + a.Size.Y / 2f);
            NoneIn("start area " + a.Side, b => b.Min.X < hi.X && b.Max.X > lo.X && b.Min.Y < hi.Y && b.Max.Y > lo.Y);
        }
        foreach (var p in Level.Points)
            NoneIn("capture zone " + p.Name + " plus 6 m", b => DiscHits(b, p.Center.X, p.Center.Y, p.Radius + 6f));
        foreach (var s in Level.Spawns)
            NoneIn("spawn " + s, b => DiscHits(b, s.X, s.Y, 4f));
        foreach (float v in new[] { -92f, 92f, -232f, 232f })
        {
            // (the small monument stands on the west avenue's centre, y -5..5, as in v5: the corridor check of v5 skips |y| < 16 there too)
            NoneIn("north-south lane " + v, b => b.Min.X < v + 4f && b.Max.X > v - 4f && !(MathF.Abs(v) == 92f && b.Min.Y > -16f && b.Max.Y < 16f));
            NoneIn("east-west lane " + v, b => b.Min.Y < v + 4f && b.Max.Y > v - 4f);
        }
        NoneIn("the paved road", b => b.Min.X < 6f && b.Max.X > -6f && b.Min.Y < 60f && b.Max.Y > -160f);
        NoneIn("the plaza's axes", b => (b.Min.Y < 4f && b.Max.Y > -4f && b.Min.X < 60f && b.Max.X > -60f) || (b.Min.X < 4f && b.Max.X > -4f && b.Min.Y < 60f && b.Max.Y > -60f));
        NoneIn("B's centre", b => DiscHits(b, 0f, 0f, 16f));
        NoneIn("the boundary's inner 2 m", b => b.Min.X < -238f || b.Min.Y < -238f || b.Max.X > 238f || b.Max.Y > 238f);
        // High places (a bridge over an avenue) may cross a lane, but nothing stands under them in it: the lane is clear to 8 m.
        var all = Level.Parts.Where(p => p.Solid && !IsGround(p.Kind)).Select(Bounds).ToList();
        foreach (var hi in all.Where(b => b.Min.Z >= 8f))
        {
            bool inLane = new[] { -92f, 92f, -232f, 232f }.Any(v => (hi.Min.X < v + 4f && hi.Max.X > v - 4f) || (hi.Min.Y < v + 4f && hi.Max.Y > v - 4f));
            if (!inLane) continue;
            Assert.DoesNotContain(all, lo => lo.Min.Z < hi.Min.Z && lo.Max.Z < 8f && lo.Min.X < hi.Max.X && lo.Max.X > hi.Min.X && lo.Min.Y < hi.Max.Y && lo.Max.Y > hi.Min.Y
                && new[] { -92f, 92f, -232f, 232f }.Any(v => (lo.Min.X < v + 4f && lo.Max.X > v - 4f) || (lo.Min.Y < v + 4f && lo.Max.Y > v - 4f)) && !(lo.Min.Y > -16f && lo.Max.Y < 16f));
        }
    }

    static bool DiscHits((Vector3 Min, Vector3 Max) b, float cx, float cy, float r)
    {
        float dx = MathF.Max(MathF.Max(b.Min.X - cx, cx - b.Max.X), 0f), dy = MathF.Max(MathF.Max(b.Min.Y - cy, cy - b.Max.Y), 0f);
        return dx * dx + dy * dy < r * r;
    }

    [Fact]
    public void Objective_A_stands_on_the_ground_and_its_plinth_is_gone()
    {
        var a = Level.Points[0];
        Assert.Equal(AshfallMap.BlockTop, a.Center.Z);
        Assert.Contains(a.Center, Level.Spawns);
        Assert.DoesNotContain(Level.Spawns, s => s.Z == 3f);
        Assert.Equal(3f, D.Graybox.Level.Points[0].Center.Z);   // v5 had it on the plinth
        Assert.DoesNotContain(Dressing.Flags, f => f.Contains("A obelisk") && f.Contains("dropped"));
        Assert.Contains(Dressing.Flags, f => f.StartsWith("A: the plinth"));
    }

    // ---------------------------------------------------------------- the stronghold

    [Fact]
    public void The_stronghold_is_a_breached_enclosure_with_an_8_m_breach_on_every_leg_and_one_asset_for_each_piece()
    {
        // The five legs: along-axis, the band across, the span (between the corner pieces), as generated.
        (string Name, bool AlongX, float T0, float T1, float Lo, float Hi)[] legs =
        [
            ("ring south leg west", true, 112f, 116f, -66f, -12f), ("ring south leg east", true, 112f, 116f, 12f, 66f), ("ring west leg", false, -72f, -68f, 118f, 214f),
            ("ring north leg", true, 216f, 220f, -66f, 66f), ("ring east leg", false, 68f, 72f, 118f, 214f),
        ];
        var solids = Level.Parts.Where(p => p.Solid && !IsGround(p.Kind)).Select(Bounds).Where(b => b.Min.Z < 2.5f).ToList();
        foreach (var (name, alongX, t0, t1, lo, hi) in legs)
        {
            var spans = solids.Where(b => alongX ? (b.Min.Y < t1 && b.Max.Y > t0) : (b.Min.X < t1 && b.Max.X > t0))
                .Select(b => alongX ? (A: b.Min.X, B: b.Max.X) : (A: b.Min.Y, B: b.Max.Y)).Where(s => s.B > lo && s.A < hi).OrderBy(s => s.A).ToList();
            float widest = 0f, at = lo;
            foreach (var (a, b) in spans) { widest = MathF.Max(widest, a - at); at = MathF.Max(at, b); }
            widest = MathF.Max(widest, hi - at);
            output.WriteLine($"{name}: widest opening {widest:0.0} m");
            Assert.True(widest >= 8f - 1e-3f, $"{name}: widest opening {widest:0.0} m");
            Assert.Equal(1, Dressing.Flags.Count(f => f.StartsWith(name + " breach 0:")));
        }
        // One asset each: gate, four corners, keep, keep tower, two stair towers, two or three terraces (draft 7: the third ran into the second and was left out by the overlap rule; draft 8: the roadway rule moves
        // the terraces north off the avenue and all three fit), one or two east outer walls (draft 8: both stood on the avenue; moved north off it, the second runs into the first and is left out by the overlap rule).
        var log = D.Placements;
        Assert.Single(log, l => l.Group == "ring gate" && l.Item >= 0 && l.Asset == "AC-042");   // draft 6, phase 2: the expansion's gatehouse
        Assert.Equal(4, log.Count(l => l.Group.StartsWith("ring corner") && l.Item >= 0 && l.Asset == "AC-003"));
        Assert.Single(log, l => l.Group == "keep" && l.Item >= 0);
        Assert.Single(log, l => l.Group == "Keep tower" && l.Item >= 0);
        Assert.Equal(2, log.Count(l => l.Group.StartsWith("Stair tower") && l.Item >= 0 && l.Asset == "AC-046"));   // the expansion's freestanding stair
        Assert.InRange(log.Count(l => l.Group.StartsWith("terrace") && l.Item >= 0 && l.Asset is "AC-008" or "AC-049" or "AC-050"), 2, 3);
        Assert.InRange(log.Count(l => l.Group.StartsWith("east outer wall") && l.Item >= 0 && l.Asset is "AC-041" or "AC-001"), 1, 2);
        Assert.Single(log, l => l.Group == "keep" && l.Asset == "AC-044");   // the expansion's gothic hall keep: the library's first keep asset
        Assert.Contains(Dressing.Flags, f => f.StartsWith("the keep: the expansion's gothic hall keep"));
        // The curtain wall (AC-041, intact ends) is never a breach end: on a leg it is only ever a wall's middle, between broken pieces where the wall meets a breach.
        foreach (var l in log.Where(l => l.Family == "stronghold-wall" && l.Asset == "AC-041")) Assert.EndsWith(" curtain", l.Group);
        foreach (var l in log.Where(l => l.Family == "stronghold-wall" && (l.Group.EndsWith(" end") || l.Group.EndsWith(" end 2")))) Assert.NotEqual("AC-041", l.Asset);
        // Each leg's walls are 14..28 m of standalone ruins (the last may be longer).
        foreach (var l in log.Where(l => l.Family == "stronghold-wall")) Assert.True(l.Item >= 0);
        // The gate segment: the free width through it is reported (the fit decides).
        var gate = solids.Where(b => b.Min.Y < 116f && b.Max.Y > 112f && b.Min.X < 12f && b.Max.X > -12f && b.Max.Z > 0.4f + 1e-3f).OrderBy(b => b.Min.X).ToList();   // a floor or sill under the 0.4 m step is walked over
        float free = 0f, edge = -12f;
        foreach (var b in gate) { free = MathF.Max(free, b.Min.X - edge); edge = MathF.Max(edge, b.Max.X); }
        free = MathF.Max(free, 12f - edge);
        output.WriteLine($"gate segment: widest opening through x -12..12 is {free:0.0} m");
    }

    [Fact]
    public void Narrow_gaps_between_cover_and_rubble_items_are_reported_and_few()
    {
        // The v5 band check, adapted: the gap between two different items' low pieces (cover and rubble parts) is either nothing (they touch or overlap) or wide enough for a 1.2 m squad.
        var owner = new Dictionary<int, int>();
        for (int i = 0; i < Dressing.Items.Count; i++) { var (f, n) = Dressing.ItemParts[i]; for (int p = f; p < f + n; p++) owner[p] = i; }
        var low = Level.Parts.Select((p, i) => (p, i)).Where(x => x.p.Kind is AshfallKind.Cover or AshfallKind.Rubble or AshfallKind.Wall or AshfallKind.Floor && owner.ContainsKey(x.i) && Bounds(x.p).Min.Z < 2.5f)
            .Select(x => (Owner: owner[x.i], B: Bounds(x.p))).ToList();
        int bad = 0, pairs = 0;
        for (int i = 0; i < low.Count; i++)
            for (int j = i + 1; j < low.Count; j++)
            {
                if (low[i].Owner == low[j].Owner) continue;
                var a = low[i].B; var b = low[j].B;
                float dx = MathF.Max(MathF.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X), 0f), dy = MathF.Max(MathF.Max(a.Min.Y - b.Max.Y, b.Min.Y - a.Max.Y), 0f);
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d > 1e-3f && d < 8f) { pairs++; if (d < 1.2f - 1e-3f) bad++; }
            }
        output.WriteLine($"cover/rubble pairs of different items within 8 m: {pairs}, narrower than 1.2 m: {bad}");
        Assert.True(bad < pairs / 10, $"{bad} of {pairs} gaps are narrower than 1.2 m");   // a finding, not a v5 guarantee: items grow up to 1 m past their structures, so v5 gaps of 1.2 m can close
    }

    [Fact]
    public void Every_ramp_wedge_has_its_top_plane_within_half_a_riser_of_its_items_stepped_box_tops()
    {
        // Draft 9 replaced these wedges (stairs found in the meshes, the boxes cut under them: AshfallDraft9Tests); the draft-5 wedges are checked on the pass without draft 9's rules.
        var d8 = AshfallMap.RebuildWithoutDraft9();
        var Level = d8.Level; var Dressing = d8.Dressing;
        int ramps = 0, samples = 0, outliers = 0;
        for (int i = 0; i < Dressing.Items.Count; i++)
        {
            var it = Dressing.Items[i];
            var (first, count) = Dressing.ItemParts[i];
            if (it.Role != AshfallDressRole.Prop || count == 0) continue;
            float s = MathF.Sin(it.Yaw), c = MathF.Cos(it.Yaw);
            float cellY = AshfallKitCollision.Metrics(it.Asset).CellSize * it.Size.Z / AshfallKit.Get(it.Asset).RawSize.Y;   // the fit's vertical quantum, scaled
            Vector3 Local(Vector3 v) { float dx = v.X - it.Position.X, dy = v.Y - it.Position.Y; return new Vector3(c * dx + s * dy, -s * dx + c * dy, v.Z); }
            var boxes = Enumerable.Range(first, count).Where(p => Level.Parts[p].Kind != AshfallKind.Stair)
                .Select(p => { var l = Level.Parts[p].Corners.Select(Local).ToArray(); return (Min: l.Aggregate(Vector3.Min), Max: l.Aggregate(Vector3.Max)); }).ToList();
            foreach (int p in Enumerable.Range(first, count).Where(p => Level.Parts[p].Kind == AshfallKind.Stair))
            {
                ramps++;
                var cs = Level.Parts[p].Corners;
                // the top face: the two triangles that face up
                var tris = Enumerable.Range(0, 12).Select(t => (A: Local(cs[t * 3]), B: Local(cs[t * 3 + 1]), C: Local(cs[t * 3 + 2]))).Where(t => Vector3.Cross(t.B - t.A, t.C - t.A).Z > 1e-3f).ToList();
                Assert.NotEmpty(tris);
                var pts = new List<Vector3>();
                foreach (var t in tris) { var cen = (t.A + t.B + t.C) / 3f; pts.Add(cen); foreach (var v in new[] { t.A, t.B, t.C }) pts.Add(cen + (v - cen) * 0.5f); }
                var tops = new List<float>(); var rampZ = new List<(float Z, float Top)>();
                foreach (var q in pts)
                {
                    var cover = boxes.Where(b => q.X >= b.Min.X - 0.01f && q.X <= b.Max.X + 0.01f && q.Y >= b.Min.Y - 0.01f && q.Y <= b.Max.Y + 0.01f).ToList();
                    if (cover.Count == 0) continue;
                    float top = cover.Max(b => b.Max.Z);
                    tops.Add(top); rampZ.Add((q.Z, top)); samples++;
                }
                if (rampZ.Count == 0) continue;
                var distinct = tops.OrderBy(t => t).Aggregate(new List<float>(), (l, t) => { if (l.Count == 0 || t - l[^1] > 0.05f) l.Add(t); return l; });
                float riser = distinct.Count < 2 ? 0f : Enumerable.Range(1, distinct.Count - 1).Max(k => distinct[k] - distinct[k - 1]);
                float tol = MathF.Max(riser / 2f, 1.5f * cellY) + 0.05f;
                var diffs = rampZ.Select(x => MathF.Abs(x.Z - x.Top)).OrderBy(x => x).ToList();
                outliers += diffs.Count(d => d > tol);
                // the typical sample lies within the tolerance (a dropped dust box or a merged box can leave single samples off)
                Assert.True(diffs[diffs.Count / 2] <= tol, $"item {i} ({it.Asset}): median distance of the ramp plane to the box tops {diffs[diffs.Count / 2]:0.00} m, tolerance {tol:0.00} m");
            }
        }
        Assert.True(ramps > 50);
        output.WriteLine($"{ramps} ramp wedges, {samples} samples against the stepped box tops, {outliers} samples beyond the tolerance");
    }

    // ---------------------------------------------------------------- flags and the review list

    [Fact]
    public void The_flags_list_the_review_items()
    {
        foreach (var f in Dressing.Flags) output.WriteLine(f);
        // Draft 6: every used model passes the collision quality gate, so a clean-up flag appears only when a used model still needs one (a walkable model without a top, say).
        bool anyCleanup = Dressing.Items.Where(i => i.Role == AshfallDressRole.Prop).Select(i => i.Asset).Distinct().Any(AshfallKitCollision.NeedsCleanup);
        Assert.Equal(anyCleanup, Dressing.Flags.Any(f => f.Contains("manual clean-up")));
        Assert.DoesNotContain(Dressing.Flags, f => f.StartsWith("untagged"));
        // Every model used whose collision needs a clean-up is named.
        foreach (var id in Dressing.Items.Where(i => i.Role == AshfallDressRole.Prop).Select(i => i.Asset).Distinct().Where(AshfallKitCollision.NeedsCleanup))
            Assert.Contains(Dressing.Flags, f => f.StartsWith(id + " used"));
    }

    // ---------------------------------------------------------------- the report and the enemies' graph (slow)

    sealed record V5Place(string Name, Vector3 At);

    static List<V5Place> V5Places()
    {
        var f = D.Graybox.Features;
        var list = new List<V5Place>();
        foreach (var (k, v) in f.Points!.OrderBy(k => k.Key, StringComparer.Ordinal)) list.Add(new("point: " + k, v));
        foreach (var t in f.Towers) for (int i = 0; i < t.Floors.Count; i++) list.Add(new($"tower floor: {t.Name} {i + 1}", t.Floors[i]));
        foreach (var r in f.Ruins.Where(r => r.FloorPoint != null)) list.Add(new($"ruin floor: {r.Kind} at {(r.Footprint.X + r.Footprint.Z) / 2f:0},{(r.Footprint.Y + r.Footprint.W) / 2f:0}", r.FloorPoint!.Value));
        foreach (var p in f.Platforms) list.Add(new($"platform deck at {p.Top.X:0},{p.Top.Y:0}", p.Top));
        list.AddRange([
            new("A plinth top", new(-170f, 160f, 3f)), new("small monument plinth", new(-92f, 3f, 2f)), new("C first floor", new(160f, 184f, 6f)), new("E ruin first floor", new(170f, 10f, 6f)),
            new("small ruin floor", new(136f, -48f, 6f)), new("bunker roof", new(170f, -164f, 6f)), new("artillery platform", new(-164f, -164f, 3f)),
            new("keep roof", new(0f, 190f, 18f)), new("keep stair deck", new(34f, 210f, 18f)), new("west L wall top (ramparts)", new(-70f, 150f, 12f)), new("east L wall top (ramparts, 18 m)", new(70f, 150f, 18f)),
        ]);
        return list;
    }

    [Fact]
    [Trait("Speed", "Slow")]   // builds the war's whole walking graph on the draft-6 level (about a minute or two)
    public void The_enemies_graph_reaches_the_places_and_reports_every_v5_walkable_place_as_kept_changed_or_lost()
    {
        var sw = Stopwatch.StartNew();
        var data = TfwWorld.Load("unused", out _)!;
        string dir = Path.Combine(Path.GetTempPath(), "ashfall-d5-nav-" + Guid.NewGuid().ToString("N"));
        NavGraph graph;
        try { graph = TfwWorld.Nav(data.Collision, data.Field, new SimConfig(), out _, dir); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        output.WriteLine($"nav build {sw.Elapsed.TotalSeconds:0.0} s, {graph.NodeCount} nodes");

        var start = Level.Areas[0].Center;
        int s0 = graph.Nearest(start, 2f, 1.5f);
        Assert.True(s0 >= 0, "no node at the Space Marines' start");
        // Everything walked to from the start (walk and drop links, no jump packs).
        var seen = new bool[graph.NodeCount];
        var queue = new Queue<int>(); queue.Enqueue(s0); seen[s0] = true;
        while (queue.Count > 0)
        {
            int n = queue.Dequeue();
            foreach (var (to, _, kind) in graph.Links(n))
                if (kind is NavLinkKind.Walk or NavLinkKind.Drop && !seen[to]) { seen[to] = true; queue.Enqueue(to); }
        }
        var reach = Enumerable.Range(0, graph.NodeCount).Where(n => seen[n]).Select(graph.Position).ToList();
        var grid = new Dictionary<(int, int), List<Vector3>>();
        foreach (var p in reach) { var key = ((int)MathF.Floor(p.X / 4f), (int)MathF.Floor(p.Y / 4f)); if (!grid.TryGetValue(key, out var l)) grid[key] = l = []; l.Add(p); }
        List<Vector3> Near(Vector3 at, float r)
        {
            var res = new List<Vector3>();
            for (int gx = (int)MathF.Floor((at.X - r) / 4f); gx <= (int)MathF.Floor((at.X + r) / 4f); gx++)
                for (int gy = (int)MathF.Floor((at.Y - r) / 4f); gy <= (int)MathF.Floor((at.Y + r) / 4f); gy++)
                    if (grid.TryGetValue((gx, gy), out var l)) res.AddRange(l.Where(p => Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(at.X, at.Y)) <= r));
            return res;
        }
        bool Reaches(Vector3 p, float r = 2f) => Near(p, r).Any(q => MathF.Abs(q.Z - p.Z) <= 1f);

        // The places the game needs: the capture points, the other side's start, the spawn markers.
        Assert.True(Reaches(Level.Points[0].Center), "A is not reached on foot");
        Assert.True(Reaches(Level.Points[1].Center), "B is not reached on foot");
        Assert.True(Reaches(Level.Points[2].Center), "C is not reached on foot");
        Assert.True(Reaches(Level.Areas[1].Center), "the Chaos start (the stronghold's courtyard) is not reached on foot");
        var unreached = Level.Spawns.Where(s => !Reaches(s, 3f)).ToList();
        output.WriteLine($"spawn markers not reached on foot: {unreached.Count} of {Level.Spawns.Count}{(unreached.Count > 0 ? ": " + string.Join(" ", unreached) : "")}");

        // Reachable tops: clusters of reached nodes above 2 m, by 4 m cell.
        var tops = reach.Where(p => p.Z >= 2f).GroupBy(p => ((int)MathF.Floor(p.X / 12f), (int)MathF.Floor(p.Y / 12f))).Select(g => (g.Key, N: g.Count(), Min: g.Min(p => p.Z), Max: g.Max(p => p.Z), C: new Vector2(g.Average(p => p.X), g.Average(p => p.Y)))).OrderBy(t => t.Key).ToList();
        var report = new StringBuilder();
        report.AppendLine($"Reachable tops on foot from the Space Marines' start: {reach.Count(p => p.Z >= 2f)} nodes at 2 m or higher in {tops.Count} 12 m cells; highest {(reach.Count == 0 ? 0f : reach.Max(p => p.Z)):0.0} m");
        foreach (var t in tops) report.AppendLine($"  top near ({t.C.X:0}, {t.C.Y:0}): {t.N} nodes, z {t.Min:0.0}..{t.Max:0.0}");

        var places = V5Places();
        int kept = 0, changed = 0, lost = 0;
        report.AppendLine("v5 walkable places:");
        foreach (var pl in places)
        {
            string what;
            if (Reaches(pl.At)) { kept++; what = "kept"; }
            else
            {
                var heights = Near(pl.At, 2f).Select(q => q.Z).Distinct().OrderBy(z => z).Take(4).ToList();
                if (heights.Count > 0) { changed++; what = "changed (ground at " + string.Join(", ", heights.Select(z => z.ToString("0.0"))) + " m instead)"; }
                else { lost++; what = "lost"; }
            }
            report.AppendLine($"  {pl.Name} ({pl.At.X:0}, {pl.At.Y:0}, {pl.At.Z:0.0}): {what}");
        }
        report.AppendLine($"v5 walkable places: {places.Count}, kept {kept}, changed {changed}, lost {lost}");
        // Draft 6: the decks of the shooting platforms (AC-008, and since phase 2 the expansion's AC-049 and AC-050; the added ones listed): a reached node over the item's footprint at 1 m or more above its foot.
        int decks = 0, deckTotal = 0, addedDecks = 0, addedTotal = 0;
        for (int i = 0; i < Dressing.Items.Count; i++)
        {
            var it = Dressing.Items[i];
            if (it.Role != AshfallDressRole.Prop || it.Asset is not ("AC-008" or "AC-049" or "AC-050")) continue;
            bool added = D.Placements.First(l => l.Item == i && l.Asset == it.Asset).Group.StartsWith("extra platform");
            float r = MathF.Min(it.Size.X, it.Size.Y) / 2f;
            var on = Near(it.Position, r).Where(q => q.Z >= it.Position.Z + 1f).ToList();
            deckTotal++; if (on.Count > 0) decks++;
            if (added) { addedTotal++; if (on.Count > 0) addedDecks++; }
            { report.AppendLine($"  {(added ? "added" : "grown")} {it.Asset} at ({it.Position.X:0}, {it.Position.Y:0}), {it.Size.Z:0.0} m tall: deck {(on.Count > 0 ? $"reached at {on.Max(q => q.Z):0.0} m" : "not reached")}"); }
        }
        report.AppendLine($"shooting platforms with a deck reached on foot: {decks} of {deckTotal} (added in draft 6: {addedDecks} of {addedTotal})");
        // Route clearance: the free width across the walk graph's routes from the Space Marines' start to A, B, C and the Chaos courtyard.
        var obstacles = Level.Parts.Where(p => p.Solid && !IsGround(p.Kind)).Select(Bounds).ToList();
        float Free(Vector3 at, Vector2 dir)
        {
            float best = 12f;
            foreach (var b in obstacles)
            {
                if (b.Max.Z < at.Z + 0.4f || b.Min.Z > at.Z + 1.9f) continue;
                float t0 = 0f, t1 = best; bool hit = true;
                foreach (var (o, d, lo, hi) in new[] { (at.X, dir.X, b.Min.X, b.Max.X), (at.Y, dir.Y, b.Min.Y, b.Max.Y) })
                {
                    if (MathF.Abs(d) < 1e-6f) { if (o < lo || o > hi) { hit = false; break; } continue; }
                    float ta = (lo - o) / d, tb = (hi - o) / d;
                    t0 = MathF.Max(t0, MathF.Min(ta, tb)); t1 = MathF.Min(t1, MathF.Max(ta, tb));
                    if (t0 > t1) { hit = false; break; }
                }
                if (hit && t0 > 0.01f) best = MathF.Min(best, t0);   // a box whose bounds already hold the sample is a turned or sloped part's hull: the route runs beside it, not through it
            }
            return best;
        }
        report.AppendLine("Route clearance (free width across the route, 2 m samples):");
        // The window pass (2026-11-06): a placed model's own doorway or arch is open to walkers too; a route through one is a door, not a squeeze between objects (reported, not asserted).
        var doors = new List<(Vector3 Min, Vector3 Max, string What)>();
        foreach (var (it, i) in Dressing.Items.Select((it, i) => (it, i)))
        {
            if (it.Role != AshfallDressRole.Prop || Dressing.ItemParts[i].Count == 0) continue;
            foreach (var o in AshfallKitCollision.Openings(it.Asset))
            {
                float span = o.DepthMax - o.DepthMin;
                var lo = o.At(o.Centre, o.DepthMin - span); var hi = o.At(o.Centre, o.DepthMax + span);
                var half = o.Axis == 0 ? new Vector3(0f, o.Height, o.Width) : new Vector3(o.Width, o.Height, 0f);
                var rmin = Vector3.Min(lo, hi) - half; var rmax = Vector3.Max(lo, hi) + half;
                var c = Enumerable.Range(0, 8).Select(k => it.ToWorld(new Vector3((k & 1) == 0 ? rmin.X : rmax.X, (k & 2) == 0 ? rmin.Y : rmax.Y, (k & 4) == 0 ? rmin.Z : rmax.Z))).ToList();
                doors.Add((c.Aggregate(Vector3.Min), c.Aggregate(Vector3.Max), $"{it.Asset} item {i}"));
            }
        }
        string? Door0(Vector3 q) => doors.Where(d => q.X >= d.Min.X && q.X <= d.Max.X && q.Y >= d.Min.Y && q.Y <= d.Max.Y && q.Z + 1f >= d.Min.Z && q.Z + 1f <= d.Max.Z).Select(d => d.What).FirstOrDefault();
        // Draft 9: a stair (a placed model's ramp, 1 m wide or more) is a passage of its own width by design: a sample on its ramp, foot or landing (the stair's own width plus 0.5 m a side, along the stair and its approaches or inside its own model; the draft-9 zone rule keeps that clear) is reported, not asserted against the 2.4 m rule; a gap beside a stair is still asserted.
        var stairs = AshfallStairs.All(Level, AshfallMap.BuildDraft5().Dressing);
        static bool InDrawn(AshfallDressItem it, Vector2 p)
        {
            var d = p - new Vector2(it.Position.X, it.Position.Y);
            float s = MathF.Sin(it.Yaw), c = MathF.Cos(it.Yaw);
            return MathF.Abs(c * d.X + s * d.Y) <= it.Size.X / 2f && MathF.Abs(-s * d.X + c * d.Y) <= it.Size.Y / 2f;
        }
        string? Door(Vector3 q)
        {
            if (Door0(q) is { } d) return d;
            var p = new Vector2(q.X, q.Y);
            foreach (var r in stairs)
            {
                float along = Vector2.Dot(p - r.Low, r.Dir), across = MathF.Abs(Vector2.Dot(p - (r.Low + r.High) / 2f, r.Across));
                bool inLine = across <= r.Width / 2f + 0.5f;
                bool inModel = r.Item >= 0 && InDrawn(AshfallMap.BuildDraft5().Dressing.Items[r.Item], p);   // between the model's own cheeks: not a gap between objects
                if (inLine && ((along >= -AshfallStairs.Approach && along <= r.Run + AshfallStairs.Approach) || inModel)) return $"a stair of {(r.Item >= 0 ? AshfallMap.BuildDraft5().Dressing.Items[r.Item].Asset : "?")}";
            }
            return null;
        }
        float worst = 12f;
        foreach (var (name, goal) in new[] { ("A", Level.Points[0].Center), ("B", Level.Points[1].Center), ("C", Level.Points[2].Center), ("Chaos courtyard", Level.Areas[1].Center) })
        {
            var path = graph.FindPath(start, goal, goalSnap: 3f, maxExpanded: 8_000_000);
            if (path is not { Partial: false } pp) { report.AppendLine($"  {name}: no complete path"); continue; }
            var widths = new List<(Vector3 At, float W)>();
            for (int k = 1; k < pp.Waypoints.Count; k++)
            {
                var a = pp.Waypoints[k - 1].Position; var b = pp.Waypoints[k].Position;
                var flat = new Vector2(b.X - a.X, b.Y - a.Y);
                float len = flat.Length();
                if (len < 0.5f) continue;
                var heading = flat / len;
                var perp = new Vector2(-heading.Y, heading.X);
                for (float t = 0f; t < len; t += 2f)
                {
                    var w = Vector3.Lerp(a, b, t / len);
                    widths.Add((w, Free(w, perp) + Free(w, -perp)));
                }
            }
            if (widths.Count == 0) continue;
            int under24 = widths.Count(x => x.W < 2.4f), under12 = widths.Count(x => x.W < 1.2f), under08 = widths.Count(x => x.W < 0.8f);
            var between = widths.Where(x => x.W >= 2.4f || Door(x.At) == null).ToList();
            if (between.Count > 0) worst = MathF.Min(worst, between.Min(x => x.W));
            report.AppendLine($"  {name}: {widths.Count} samples, narrowest {widths.Min(x => x.W):0.0} m, under 2.4 m {under24} ({100f * under24 / widths.Count:0.0} %), under 1.2 m {under12} ({100f * under12 / widths.Count:0.0} %), under 0.8 m {under08}");
            foreach (var x in widths.Where(x => x.W < 2.4f).Take(12)) report.AppendLine($"    tight {x.W:0.0} m at ({x.At.X:0}, {x.At.Y:0}, {x.At.Z:0.0}){(Door(x.At) is { } dw ? $" ({(dw.StartsWith("a stair") ? dw : "a doorway of " + dw)}: not asserted)" : "")}");
        }
        output.WriteLine(report.ToString());
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "ashfall-draft6-walkable-report.txt"), report.ToString()); } catch (IOException) { }
        Assert.True(worst >= 2.4f, $"a route squeezes through {worst:0.0} m");   // draft 6 (the lead's brief): no passage under 2.4 m on the main routes (draft 5 asserted 0.8 m and measured 3.2 m)
        Assert.True(kept + changed + lost == places.Count);
        Assert.True(reach.Count > graph.NodeCount / 2, "less than half of the graph is walked from the start");
    }
}
