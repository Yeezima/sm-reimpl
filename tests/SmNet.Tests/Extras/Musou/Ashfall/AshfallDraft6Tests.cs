using System.Numerics;
using SmSim.Extras.Musou.Ashfall;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall draft 6 (docs/musou-ashfall-graybox.md "Draft 6"): the owner's notes on draft 5. Bridges rest on a supporting structure at both narrow ends (or stand on their own legs), more AC-008 shooting platforms on free ground,
/// every placed object 1.5x its draft-5 size. From the generator alone: no asset bytes, no install. The level's hash is pinned in <see cref="AshfallDraft5Tests.PinnedLevelHash"/>.
/// </summary>
public class AshfallDraft6Tests(ITestOutputHelper output)
{
    static readonly AshfallDraft5 D = AshfallMap.BuildDraft5();
    static AshfallLevel Level => D.Level;
    static AshfallDressing Dressing => D.Dressing;

    static (Vector3 Min, Vector3 Max) Bounds(AshfallPart p) => (p.Corners.Aggregate(Vector3.Min), p.Corners.Aggregate(Vector3.Max));

    static bool IsGround(AshfallKind k) => k is AshfallKind.Ground or AshfallKind.Road or AshfallKind.Paving or AshfallKind.Curb or AshfallKind.Boundary or AshfallKind.Objective;

    // ---------------------------------------------------------------- bridges (owner: "never floating or not have support under it or directly connected to either narrow end")

    [Fact]
    public void Every_bridge_rests_on_a_structure_at_its_deck_at_both_narrow_ends_or_stands_on_its_own_legs()
    {
        var owner = new Dictionary<int, int>();
        for (int i = 0; i < Dressing.Items.Count; i++) { var (f, n) = Dressing.ItemParts[i]; for (int p = f; p < f + n; p++) owner[p] = i; }
        var others = Level.Parts.Select((p, k) => (p, k)).Where(x => x.p.Solid && !IsGround(x.p.Kind) && owner.TryGetValue(x.k, out int o) && Dressing.Items[o].Asset != "AC-009").Select(x => Bounds(x.p)).ToList();
        static bool Holds((Vector3 Min, Vector3 Max) b, Vector2 q) => q.X >= b.Min.X - 0.01f && q.X <= b.Max.X + 0.01f && q.Y >= b.Min.Y - 0.01f && q.Y <= b.Max.Y + 0.01f;
        int bridges = 0;
        for (int i = 0; i < Dressing.Items.Count; i++)
        {
            var it = Dressing.Items[i];
            if (it.Role != AshfallDressRole.Prop || it.Asset != "AC-009") continue;
            bridges++;
            var (first, count) = Dressing.ItemParts[i];
            var mine = Enumerable.Range(first, count).Select(p => Bounds(Level.Parts[p])).ToList();
            Assert.NotEmpty(mine);
            var dir = new Vector2(MathF.Cos(it.Yaw), MathF.Sin(it.Yaw));   // the item's local X: its length; the narrow ends at +-Size.X / 2
            var across = new Vector2(-dir.Y, dir.X);
            var centre = new Vector2(it.Position.X, it.Position.Y);
            float deck = mine.Where(b => Holds(b, centre)).Select(b => b.Max.Z).DefaultIfEmpty(float.NaN).Max();
            Assert.False(float.IsNaN(deck), $"bridge {i}: no deck over its centre");
            foreach (int side in new[] { -1, 1 })
            {
                var end = centre + dir * (side * it.Size.X / 2f);
                var samples = new List<Vector2>();
                foreach (float t in new[] { -1f, 0f, 1f }) foreach (float u in new[] { -it.Size.Y / 3f, 0f, it.Size.Y / 3f }) samples.Add(end + dir * t + across * u);
                // a placed structure's collision top within the 0.4 m step of the deck at this end
                bool rests = samples.Any(q => others.Any(b => Holds(b, q) && MathF.Abs(b.Max.Z - deck) <= 0.4f + 1e-3f));
                // or a leg of its own, in this end's half, standing on the ground
                bool leg = mine.Any(b => Vector2.Dot(new Vector2((b.Min.X + b.Max.X) / 2f, (b.Min.Y + b.Max.Y) / 2f) - centre, dir) * side > it.Size.X / 4f && b.Min.Z <= AshfallMap.SurfaceAt(end.X, end.Y) + 0.05f);
                output.WriteLine($"bridge {i} at ({it.Position.X:0}, {it.Position.Y:0}), deck {deck:0.0} m, end {side}: {(rests ? "rests on a structure" : leg ? "stands on its own leg" : "NO SUPPORT")}");
                Assert.True(rests || leg, $"bridge {i}: its {(side < 0 ? "first" : "second")} narrow end has nothing under it at the {deck:0.0} m deck");
            }
        }
        // Each v5 bridge is placed or dropped with a flag.
        foreach (var name in new[] { "twin bridge", "keep bridge" })
        {
            var log = D.Placements.Single(l => l.Group == name);
            if (log.Item < 0) Assert.Contains(Dressing.Flags, f => f.StartsWith(name + ": dropped"));
            output.WriteLine($"{name}: {log.Outcome}");
        }
        output.WriteLine($"{bridges} AC-009 items");
    }

    // ---------------------------------------------------------------- shooting platforms (owner: "more of the shooting platform objects")

    [Fact]
    public void There_are_at_least_45_shooting_platforms_spread_over_the_map_on_free_level_ground()
    {
        var platforms = Dressing.Items.Select((it, i) => (it, i)).Where(x => x.it.Role == AshfallDressRole.Prop && x.it.Asset == "AC-008").ToList();
        output.WriteLine($"{platforms.Count} AC-008 items (draft 5: 29)");
        Assert.True(platforms.Count >= 45, $"{platforms.Count} shooting platforms");
        Assert.True(platforms.Count <= AshfallMap.Draft6PlatformTarget);
        var added = platforms.Where(x => D.Placements.First(l => l.Item == x.i && l.Asset == "AC-008").Group.StartsWith("extra platform")).ToList();
        Assert.True(added.Count >= 16, $"{added.Count} platforms added");
        int regions = platforms.Select(x => ((int)MathF.Floor((x.it.Position.X + 240f) / 160f), (int)MathF.Floor((x.it.Position.Y + 240f) / 160f))).Distinct().Count();
        output.WriteLine($"{added.Count} added; the platforms stand in {regions} of the map's nine 160 m blocks");
        Assert.True(regions >= 8);
        foreach (var (it, i) in added)
        {
            // on a block (the roads stay open), 24 m from every other platform; its own collision only (the clear-places check of AshfallDraft5Tests covers lanes, zones, spawns, start areas and the boundary)
            Assert.Equal(AshfallMap.BlockTop, it.Position.Z);
            foreach (var (o, j) in platforms.Where(o => o.i != i))
                Assert.True(Vector2.Distance(new(o.Position.X, o.Position.Y), new(it.Position.X, it.Position.Y)) >= 24f - 1e-3f, $"platform {i} stands within 24 m of platform {j}");
            output.WriteLine($"  added at ({it.Position.X:0}, {it.Position.Y:0}): {it.Size.X:0.0} x {it.Size.Y:0.0} x {it.Size.Z:0.0} m, {D.Placements.First(l => l.Item == i && l.Asset == "AC-008").Outcome}");
        }
        Assert.Contains(Dressing.Flags, f => f.StartsWith("draft 6: ") && f.Contains("AC-008 shooting platforms added"));
    }

    // ---------------------------------------------------------------- collision = the drawn model's fitted boxes (the lead's check of the owner's Shift+F8 screenshot)

    /// <summary>
    /// Every Prop item's collision parts (ramp wedges aside) are its model's fitted boxes moved by the item's draw transform (<see cref="AshfallDressItem.ToWorld"/>, the mapping the client's
    /// <c>AshfallKitView.Fit</c> draws the model with), one part per fitted box except the dust under 0.15 m: no other scale on any axis, no union, no extra box. The merged families (covers, rubble, collapsed wall pieces:
    /// six slabs along the run) are the deliberate exception; the air their slabs add is measured and reported.
    /// </summary>
    [Fact]
    public void Every_items_collision_is_its_models_fitted_boxes_under_the_draw_transform_except_the_merged_families()
    {
        int exact = 0;
        double mergedParts = 0, mergedFitted = 0;
        var merged = new HashSet<string> { "cover", "rubble", "wallpiece" };
        for (int i = 0; i < Dressing.Items.Count; i++)
        {
            var it = Dressing.Items[i];
            if (it.Role != AshfallDressRole.Prop) continue;
            var (first, count) = Dressing.ItemParts[i];
            var family = D.Placements.First(l => l.Item == i && l.Asset == it.Asset).Family;
            if (family == "backdrop") { Assert.Equal(0, count); continue; }   // outside the boundary: drawn, no collision
            var expected = new List<(Vector3 Min, Vector3 Max)>();
            foreach (var (mn, mx) in AshfallKitCollision.Boxes(it.Asset))
            {
                var corners = new List<Vector3>();
                foreach (float x in new[] { mn.X, mx.X }) foreach (float y in new[] { mn.Y, mx.Y }) foreach (float z in new[] { mn.Z, mx.Z }) corners.Add(it.ToWorld(new Vector3(x, y, z)));
                expected.Add((corners.Aggregate(Vector3.Min), corners.Aggregate(Vector3.Max)));
            }
            var parts = Enumerable.Range(first, count).Where(p => Level.Parts[p].Kind != AshfallKind.Stair).Select(p => Bounds(Level.Parts[p])).ToList();
            static double Vol((Vector3 Min, Vector3 Max) b) { var e = b.Max - b.Min; return (double)e.X * e.Y * e.Z; }
            if (merged.Contains(family))
            {
                Assert.True(parts.Count <= 6, $"item {i} ({family}): {parts.Count} slabs");
                mergedParts += parts.Sum(Vol); mergedFitted += expected.Sum(Vol);
                continue;
            }
            var m = AshfallKit.Get(it.Asset);
            int dust = AshfallKitCollision.Boxes(it.Asset).Count(b => (b.Max.X - b.Min.X) / m.RawSize.X * it.Size.X < 0.15f && (b.Max.Z - b.Min.Z) / m.RawSize.Z * it.Size.Y < 0.15f && (b.Max.Y - b.Min.Y) / m.RawSize.Y * it.Size.Z < 0.15f);
            Assert.True(expected.Count - dust == parts.Count, $"item {i} ({it.Asset}, {family}): {parts.Count} parts for {expected.Count} fitted boxes, {dust} of them dust");
            foreach (var p in parts)
                Assert.True(expected.Any(e => Vector3.Distance(e.Min, p.Min) < 0.02f && Vector3.Distance(e.Max, p.Max) < 0.02f), $"item {i} ({it.Asset}, {family}): a collision part {p.Min}..{p.Max} is not one of its fitted boxes under the draw transform");
            exact++;
        }
        Assert.True(exact > 200, $"{exact} items checked");
        output.WriteLine($"{exact} items: collision = fitted boxes under the draw transform; merged families: slab volume {mergedParts:N0} m3 against {mergedFitted:N0} m3 of fitted boxes (sum, overlaps counted twice): {mergedParts / Math.Max(mergedFitted, 1):0.00}x");
    }

    // ---------------------------------------------------------------- the growth (owner: 1.5x the current size)

    [Fact]
    public void Every_object_grew_1_5x_and_what_could_not_fit_is_logged_and_flagged()
    {
        Assert.Equal(1.5f, AshfallMap.Draft6Scale);
        var growth = Assert.Single(Dressing.Flags, f => f.StartsWith("draft 6 growth"));
        output.WriteLine(growth);
        foreach (var l in D.Placements.Where(l => l.Outcome.StartsWith("dropped: grown"))) Assert.Contains(l.Group, growth);
        foreach (var l in D.Placements.Where(l => l.Outcome.Contains("moved"))) output.WriteLine($"{l.Group} ({l.Family}): {l.Asset} {l.Outcome}");
        // The surface tiles are draft 5's (the ground, roads and plaza do not grow): the same 220 tiles, first in the item list.
        Assert.Equal(220, Dressing.Items.Count(i => i.Role == AshfallDressRole.Surface));
        // The stronghold's leg walls and gate keep their length along the leg (a breach never closes): never longer than their v5 segment.
        var structureOf = new Dictionary<int, AshfallStructure>();
        int k = 0;
        for (int i = 0; i < Dressing.Items.Count; i++) if (Dressing.Items[i].Role == AshfallDressRole.Prop) structureOf[i] = Dressing.Structures![k++];
        var walls = D.Placements.Where(l => l.Family is "stronghold-wall" or "stronghold-gate" && l.Item >= 0).ToList();
        Assert.True(walls.Count >= 10);
        foreach (var l in walls)
        {
            var st = structureOf[l.Item]; var w = Dressing.Items[l.Item];
            float along = MathF.Max(st.Max.X - st.Min.X, st.Max.Y - st.Min.Y);
            Assert.True(w.Size.X <= along + 0.01f, $"{l.Group}: {w.Size.X:0.0} m along a {along:0.0} m segment");
        }
    }
}
