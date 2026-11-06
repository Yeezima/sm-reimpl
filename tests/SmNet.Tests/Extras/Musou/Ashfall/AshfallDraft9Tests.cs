using System.Numerics;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall draft 9, late batch F (owner, 2026-11-06; docs/musou-ashfall-graybox.md "Draft 9 layout"): no stacked 3D objects; an opening at the foot of every stair; no stair ends in an object; every stair walks up and down
/// smoothly; the layout follows the city's streets. From the generator alone (the committed fit and stair numbers): no asset bytes, no install. The walks use the game's own movement (<see cref="Movement.Step"/>) on the whole level.
/// </summary>
public class AshfallDraft9Tests(ITestOutputHelper output)
{
    static readonly AshfallDraft5 D = AshfallMap.BuildDraft5();
    static readonly Lazy<CollisionWorld> World = new(() =>
    {
        var c = D.Level.SolidCorners(); var w = D.Level.WeaponCorners();
        return new CollisionWorld(new StaticMesh(c, Enumerable.Range(0, c.Length).ToArray()), new StaticMesh(w, Enumerable.Range(0, w.Length).ToArray()));
    });

    string Name(int item) => D.Placements.FirstOrDefault(p => p.Item == item) is { } p ? $"{p.Group} ({p.Asset})" : $"item {item}";

    static AshfallBox Drawn(AshfallDressItem it)
    {
        int ys = (((int)MathF.Round(it.Yaw / (MathF.PI / 12f))) % 24 + 24) % 24;
        return new AshfallBox(it.Position.X, it.Position.Y, it.Position.Z, it.Position.Z + it.Size.Z, it.Size.X, it.Size.Y, ys);
    }

    static bool Ring(AshfallPlacement? p) => p is not null && (p.Family is "stronghold-wall" or "stronghold-corner" or "stronghold-gate" || p.Group.Contains(" breach "));

    // ---------------------------------------------------------------- no stacked objects

    [Fact]
    public void No_two_placed_models_are_drawn_into_or_on_top_of_each_other()
    {
        var place = D.Placements.Where(p => p.Item >= 0).GroupBy(p => p.Item).ToDictionary(g => g.Key, g => g.Last());
        var props = Enumerable.Range(0, D.Dressing.Items.Count).Where(i => D.Dressing.Items[i].Role == AshfallDressRole.Prop && D.Dressing.ItemParts[i].Count > 0
            && !AshfallRoadway.IsBridge(D.Dressing.Items[i].Asset)).ToList();
        var bad = new List<string>();
        foreach (int i in props)
            foreach (int j in props)
            {
                if (j <= i || (Ring(place.GetValueOrDefault(i)) && Ring(place.GetValueOrDefault(j)))) continue;
                var a = Drawn(D.Dressing.Items[i]); var b = Drawn(D.Dressing.Items[j]);
                if (MathF.Abs(a.Cx - b.Cx) > 120f || MathF.Abs(a.Cy - b.Cy) > 120f) continue;
                float d = AshfallOverlap.Depth(a, b);
                if (d > AshfallOverlap.Tolerance + 1e-3f) bad.Add($"{Name(i)} / {Name(j)}: {d:0.00} m");
            }
        foreach (var b in bad.Take(40)) output.WriteLine(b);
        output.WriteLine($"{props.Count} models with collision, {bad.Count} drawn into each other");
        Assert.Empty(bad);
    }

    [Fact]
    public void No_placed_model_rests_on_top_of_another()
    {
        // every model stands on the ground (its foot at the block or road), and no box of one sits on a box top of another (bridges on their supports excepted)
        var bad = new List<string>();
        for (int i = 0; i < D.Dressing.Items.Count; i++)
        {
            var it = D.Dressing.Items[i];
            if (it.Role != AshfallDressRole.Prop || D.Dressing.ItemParts[i].Count == 0) continue;
            float g = AshfallMap.SurfaceAt(it.Position.X, it.Position.Y);
            if (it.Position.Z > g + AshfallOverlap.Tolerance) bad.Add($"{Name(i)} stands {it.Position.Z - g:0.00} m over the ground");
        }
        var boxes = AshfallOverlap.Boxes(D.Level, D.Dressing).Where(x => !AshfallRoadway.IsBridge(D.Dressing.Items[x.Item].Asset)).ToList();
        var grid = new Dictionary<(int, int), List<int>>();
        for (int k = 0; k < boxes.Count; k++)
        {
            var key = ((int)MathF.Floor(boxes[k].Box.Cx / 16f), (int)MathF.Floor(boxes[k].Box.Cy / 16f));
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = [];
            l.Add(k);
        }
        var pairs = new HashSet<(int, int)>();
        foreach (var (ib, b) in boxes)
        {
            if (b.Z0 < AshfallOverlap.Tolerance + 0.3f) continue;   // stands on the ground
            int gx = (int)MathF.Floor(b.Cx / 16f), gy = (int)MathF.Floor(b.Cy / 16f);
            for (int dx = -3; dx <= 3; dx++)
                for (int dy = -3; dy <= 3; dy++)
                    if (grid.TryGetValue((gx + dx, gy + dy), out var l))
                        foreach (int k in l)
                        {
                            var (ia, a) = boxes[k];
                            if (ia == ib || MathF.Abs(b.Z0 - a.Z1) > 0.35f || b.Z0 < a.Z0 + 0.3f) continue;
                            if (AshfallOverlap.Depth(a with { Z0 = -1f, Z1 = 1f }, b with { Z0 = -1f, Z1 = 1f }) > AshfallOverlap.Tolerance) pairs.Add((ia, ib));
                        }
        }
        foreach (var (a, b) in pairs) bad.Add($"{Name(b)} rests on {Name(a)}");
        foreach (var b in bad.Take(40)) output.WriteLine(b);
        Assert.Empty(bad);
    }

    // ---------------------------------------------------------------- stairs

    [Fact]
    public void Every_stair_has_an_opening_at_its_foot_and_a_clear_landing_at_its_top()
    {
        var ramps = AshfallStairs.All(D.Level, D.Dressing);
        var solids = AshfallStairs.Solids.Of(D.Level);
        var bad = new List<string>();
        foreach (var r in ramps)
        {
            var foot = AshfallStairs.Measure(r, solids, top: false);
            var top = AshfallStairs.Measure(r, solids, top: true);
            if (!foot.Open) bad.Add($"{Name(r.Item)} at ({r.Low.X:0.0}, {r.Low.Y:0.0}): foot clear {foot.ClearRun:0.0} m of {foot.Need:0.0}");
            if (!top.Open) bad.Add($"{Name(r.Item)} at ({r.High.X:0.0}, {r.High.Y:0.0}): landing clear {top.ClearRun:0.0} m of {top.Need:0.0}");
        }
        foreach (var b in bad) output.WriteLine(b);
        output.WriteLine($"{ramps.Count} stairs");
        Assert.True(ramps.Count >= 30, $"{ramps.Count} stairs (the platforms and stair towers have theirs)");
        Assert.Empty(bad);
    }

    [Fact]
    public void Every_stair_walks_up_and_down_smoothly_in_the_level()
    {
        var cfg = new SimConfig();
        float speed = cfg.RunSpeed / cfg.TickRate;
        var solids = AshfallStairs.Solids.Of(D.Level);
        var bad = new List<string>();
        var ramps = AshfallStairs.All(D.Level, D.Dressing);
        foreach (var r in ramps)
        {
            string? Walk(Vector2 from, float fromZ, Vector2 dir, Func<PlayerState, bool> arrived, string what)
            {
                var s = new PlayerState(1, new Vector3(from.X, from.Y, fromZ + 0.02f), Vector3.Zero, 0f, true);
                float yaw = MathF.Atan2(-dir.X, dir.Y);
                int ticks = (int)((r.Run + 4f) / speed) + 40, slow = 0;
                float lastZ = s.Position.Z;
                for (uint t = 0; t < ticks; t++)
                {
                    var prev = s.Position;
                    s = Movement.Step(s, new PlayerInput(t, 0f, 1f, yaw, InputButtons.None), cfg, World.Value);
                    if (arrived(s)) return null;
                    if (t > 12 && Vector2.Distance(new(prev.X, prev.Y), new(s.Position.X, s.Position.Y)) < 0.4f * speed && ++slow > 2) return $"{what} stalls at ({s.Position.X:0.0}, {s.Position.Y:0.0}, {s.Position.Z:0.0})";
                    // smooth: on the way up no sudden lift of more than a step in one tick
                    if (s.Position.Z - lastZ > AshfallStairs.Step + 0.05f) return $"{what} jumps {s.Position.Z - lastZ:0.00} m in one tick at ({s.Position.X:0.0}, {s.Position.Y:0.0})";
                    lastZ = s.Position.Z;
                }
                return $"{what} does not arrive";
            }
            var foot = r.Low - r.Dir * AshfallStairs.Approach;
            var land = r.High + r.Dir * 1.0f;
            var up = Walk(foot, solids.FloorUnder(foot, r.LowZ + AshfallStairs.Step), r.Dir,
                s => s.Grounded && Vector2.Dot(new Vector2(s.Position.X, s.Position.Y) - r.High, r.Dir) > 0.5f && MathF.Abs(s.Position.Z - r.HighZ) < AshfallStairs.Step, "up");
            var down = Walk(land, solids.FloorUnder(land, r.HighZ + AshfallStairs.Step), -r.Dir,
                s => s.Grounded && Vector2.Dot(new Vector2(s.Position.X, s.Position.Y) - r.Low, -r.Dir) > 0.5f && s.Position.Z < r.LowZ + AshfallStairs.Step, "down");
            foreach (var w in new[] { up, down }) if (w is not null) bad.Add($"{Name(r.Item)} {r.LowZ:0.0} -> {r.HighZ:0.0} m, {r.Degrees:0} degrees: {w}");
        }
        foreach (var b in bad) output.WriteLine(b);
        output.WriteLine($"{ramps.Count} stairs walked up and down");
        Assert.Empty(bad);
    }

    [Fact]
    public void Every_stair_part_is_a_ramp_no_steeper_than_the_rule_and_wide_enough()
    {
        var stairs = D.Level.Parts.Where(p => p.Kind == AshfallKind.Stair).ToList();
        var ramps = AshfallStairs.All(D.Level, D.Dressing);
        Assert.Equal(stairs.Count, ramps.Count);   // no box stairs, no leftover draft-5 wedges
        Assert.All(ramps, r => Assert.True(r.Degrees <= 50.5f && r.Width >= 0.99f && r.HighZ - r.LowZ >= 0.79f, $"{Name(r.Item)}: {r.Degrees:0} degrees, {r.Width:0.0} m wide"));
        Assert.All(ramps, r => Assert.True(r.Item >= 0, "a stair belongs to a placed model"));
    }

    [Fact]
    public void The_stair_detector_finds_a_stepped_stair_and_not_a_smooth_slope_or_a_wall_top()
    {
        // synthetic meshes, raw frame (Y up): a 5-step stair 2 wide rising toward +X onto a deck; a smooth ramp; a stepped broken wall top 0.2 wide
        static void Box(List<Vector3> p, List<int> idx, Vector3 mn, Vector3 mx)
        {
            Vector3 P(int x, int y, int z) => new(x == 0 ? mn.X : mx.X, y == 0 ? mn.Y : mx.Y, z == 0 ? mn.Z : mx.Z);
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int i = p.Count; p.AddRange([a, b, c, d]); idx.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]); }
            Quad(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1));   // the top is what the detector reads
            Quad(P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1));
        }
        var pos = new List<Vector3>(); var idx = new List<int>();
        for (int k = 0; k < 5; k++) Box(pos, idx, new(k * 0.4f, 0f, 0f), new(k * 0.4f + 0.4f, (k + 1) * 0.2f, 2f));
        Box(pos, idx, new(2f, 0f, 0f), new(4f, 1.2f, 2f));   // the deck, one riser above the last tread
        var stairs = AshfallStairDetect.Find(pos, idx, new Vector3(-0.4f, 0f, 0f), new Vector3(4f, 1.2f, 2f));
        Assert.Single(stairs);
        var s = stairs[0];
        Assert.Equal(0, s.Dir);
        Assert.InRange(s.YLow, -0.01f, 0.01f);
        Assert.InRange(s.YHigh, 1.19f, 1.21f);   // carried to the deck
        Assert.InRange(s.Max.Z - s.Min.Z, 1.8f, 2.01f);

        var slope = new List<Vector3> { new(0, 0, 0), new(4, 2, 0), new(4, 2, 2), new(0, 0, 2) };
        Assert.Empty(AshfallStairDetect.Find(slope, [0, 1, 2, 0, 2, 3], Vector3.Zero, new Vector3(4f, 2f, 2f)));

        var wall = new List<Vector3>(); var widx = new List<int>();
        for (int k = 0; k < 6; k++) Box(wall, widx, new(k * 0.5f, 0f, 0f), new(k * 0.5f + 0.5f, 2f + k * 0.3f, 0.02f));
        Assert.Empty(AshfallStairDetect.Find(wall, widx, Vector3.Zero, new Vector3(3f, 3.5f, 0.02f)));   // one line, not a stair's width
    }

    // ---------------------------------------------------------------- urban logic, flags, isolation

    [Fact]
    public void The_layout_follows_the_streets()
    {
        foreach (var f in D.Dressing.Flags.Where(f => f.StartsWith("draft 9"))) output.WriteLine(f);
        var place = D.Placements.Where(p => p.Item >= 0).GroupBy(p => p.Item).ToDictionary(g => g.Key, g => g.Last());
        // debris lies at the foot of a building: every placed rubble pile (not the ring's breach rubble) within reach of a building model
        var buildings = place.Values.Where(p => p.Family is "shell" or "shell2" or "corner" or "facade" or "tower" or "bunker" || p.Family.StartsWith("stronghold")).Select(p => Drawn(D.Dressing.Items[p.Item])).ToList();
        var lonely = new List<string>();
        foreach (var p in place.Values.Where(p => p.Family is "rubble" or "wallpiece" && !p.Group.Contains(" breach ")))
        {
            var it = D.Dressing.Items[p.Item];
            var me = new Vector2(it.Position.X, it.Position.Y);
            float best = buildings.Min(b => MathF.Max(0f, Vector2.Distance(me, new Vector2(b.Cx, b.Cy)) - 0.5f * MathF.Sqrt(b.Length * b.Length + b.Depth * b.Depth)));
            if (best > 8f + 1.5f * 12f) lonely.Add($"{p.Group} {best:0} m from any building");
        }
        foreach (var l in lonely) output.WriteLine(l);
        Assert.Empty(lonely);
        Assert.Contains(D.Placements, p => p.Outcome.StartsWith("dropped: urban"));
        // nothing but rubble keeps an off-grid turn: built pieces stand square to the streets
        var offGrid = place.Values.Where(p => p.Family is "cover" or "wall" or "wallrun" or "facade" or "shell" or "shell2" or "tower")
            .Where(p => { int ys = Drawn(D.Dressing.Items[p.Item]).YawStep; return ys % 6 != 0; }).Select(p => p.Group).ToList();
        foreach (var g in offGrid) output.WriteLine("off the street grid: " + g);
        Assert.Empty(offGrid);
    }

    [Fact]
    public void The_draft9_rules_are_reported_and_keep_the_stronghold_and_the_platforms()
    {
        Assert.Contains(D.Dressing.Flags, f => f.StartsWith("draft 9 stairs ("));
        Assert.Contains(D.Dressing.Flags, f => f.StartsWith("draft 9 placement ("));
        Assert.Contains(D.Dressing.Flags, f => f.StartsWith("draft 9 urban rules:"));
        foreach (var name in new[] { "keep", "Keep tower", "Stair tower SW", "Stair tower SE", "ring gate" })
            Assert.Contains(D.Placements, p => p.Group == name && p.Item >= 0);
        Assert.DoesNotContain(D.Placements, p => p.Family.StartsWith("stronghold") && p.Item < 0);
        Assert.DoesNotContain(D.Dressing.Items, i => i.Asset == "AC-049" && i.Role == AshfallDressRole.Prop);   // its stair ends in its own wall
        int platforms = D.Dressing.Items.Count(i => i.Role == AshfallDressRole.Prop && i.Asset is "AC-008" or "AC-050");
        output.WriteLine($"{platforms} platforms; {D.Dressing.Items.Count} items, {D.Level.Parts.Count} parts, {D.Level.SolidCorners().Length / 3:N0} collision triangles, hash 0x{D.Level.Hash:X16}");
        Assert.True(platforms >= 40, $"{platforms} platforms");
    }

    [Fact]
    public void The_pass_gives_the_same_bits_whatever_ran_before_it()
    {
        // the stair planner caches by the exact placement; a run after another run (the report aid without draft 9 between) gives the pinned level
        _ = AshfallMap.RebuildWithoutDraft9();
        var again = AshfallMap.RebuildDraft5();
        Assert.Equal(AshfallDraft5Tests.PinnedLevelHash, again.Level.Hash);
        Assert.Equal(D.Level.Hash, again.Level.Hash);
    }

    [Fact]
    public void Without_draft9_models_stack_and_stairs_break()
    {
        var before = AshfallMap.RebuildWithoutDraft9();
        var ramps = AshfallStairs.All(before.Level, before.Dressing);
        var solids = AshfallStairs.Solids.Of(before.Level);
        int blocked = ramps.Count(r => !AshfallStairs.Measure(r, solids, false).Open || !AshfallStairs.Measure(r, solids, true).Open);
        output.WriteLine($"without draft 9: {ramps.Count} ramp wedges, {blocked} with a blocked foot or landing");
        Assert.True(blocked > ramps.Count / 4);
    }
}
