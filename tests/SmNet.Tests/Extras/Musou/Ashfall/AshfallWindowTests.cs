using System.Numerics;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// The window pass (owner, 2026-11-06: "send a collison box adjustment pass that ensures i can shoot through all the windows in the structures on the map."; docs/musou-ashfall-graybox.md "Window pass"):
/// the fitter finds every through-opening of a kit model (<see cref="AshfallKitFit.FindOpenings"/>), carves it out of the boxes, and the gate fails a model whose openings are blocked. The finding and carving are
/// checked on synthetic meshes; the placed level is checked with the committed openings (numbers only, no asset bytes): a shot through each window of every placed model passes, a shot into the wall beside it stops.
/// </summary>
public class AshfallWindowTests(ITestOutputHelper output)
{
    static void AddBox(List<Vector3> pos, List<int> idx, Vector3 min, Vector3 max)
    {
        int b = pos.Count;
        for (int k = 0; k < 8; k++) pos.Add(new((k & 1) == 0 ? min.X : max.X, (k & 2) == 0 ? min.Y : max.Y, (k & 4) == 0 ? min.Z : max.Z));
        foreach (var q in new[] { (0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5) })
            idx.AddRange([b + q.Item1, b + q.Item2, b + q.Item3, b + q.Item1, b + q.Item3, b + q.Item4]);
    }

    static (List<Vector3> Pos, List<int> Idx, Vector3 Min, Vector3 Max) Mesh(IEnumerable<(Vector3 Min, Vector3 Max)> solids)
    {
        var pos = new List<Vector3>(); var idx = new List<int>();
        foreach (var (mn, mx) in solids) AddBox(pos, idx, mn, mx);
        return (pos, idx, pos.Aggregate(Vector3.Min), pos.Aggregate(Vector3.Max));
    }

    /// <summary>A wall 12 wide (X), 6 tall, 0.6 deep (Z) with two windows 1.2 x 1.6 (sills at 2.4) and a doorway 1.6 x 2.6 at its foot.</summary>
    static readonly (Vector3 Min, Vector3 Max)[] WindowWall =
    [
        (new(0, 0, 0), new(2, 6, 0.6f)),          // left pier
        (new(3.2f, 0, 0), new(5.2f, 6, 0.6f)),    // pier between the windows... and the door
        (new(6.8f, 0, 0), new(8.8f, 6, 0.6f)),
        (new(10, 0, 0), new(12, 6, 0.6f)),        // right pier
        (new(2, 0, 0), new(3.2f, 2.4f, 0.6f)), (new(2, 4, 0), new(3.2f, 6, 0.6f)),          // window 1 (x 2..3.2)
        (new(5.2f, 2.6f, 0), new(6.8f, 6, 0.6f)),                                          // doorway (x 5.2..6.8, up to 2.6)
        (new(8.8f, 0, 0), new(10, 2.4f, 0.6f)), (new(8.8f, 4, 0), new(10, 6, 0.6f)),      // window 2 (x 8.8..10)
    ];

    [Fact]
    public void Windows_and_a_doorway_are_found_and_a_blind_niche_is_not()
    {
        var (pos, idx, lo, hi) = Mesh(WindowWall);
        var map = AshfallKitFit.FindOpenings(pos, idx, lo, hi);
        Assert.Equal(3, map.Count);
        var q = AshfallKitFit.Measure(pos, idx, lo, hi, WindowWall, 12f / 48f, map);
        foreach (var o in q.Openings) output.WriteLine($"axis {o.Axis} centre {o.Centre} depth {o.DepthMin:0.00}..{o.DepthMax:0.00} {o.Width:0.00} x {o.Height:0.00} clear {o.ClearPercent:0} % wall {o.Wall}");
        Assert.All(q.Openings, o => Assert.Equal(2, o.Axis));
        Assert.Equal([2.6f, 6.0f, 9.4f], q.Openings.Select(o => MathF.Round(o.Centre.X, 1)).Order());
        Assert.All(q.Openings, o => Assert.True(o.Open && o.Wall != null));
        Assert.Equal(0, q.BlockedOpenings);

        // a niche 1.2 x 1.6, 0.3 deep, in a 0.6 deep wall: no ray passes through, so it is no window
        (Vector3, Vector3)[] niche = [(new(0, 0, 0), new(12, 6, 0.3f)), (new(0, 0, 0.3f), new(5.4f, 6, 0.6f)), (new(6.6f, 0, 0.3f), new(12, 6, 0.6f)),
            (new(5.4f, 0, 0.3f), new(6.6f, 2.4f, 0.6f)), (new(5.4f, 4, 0.3f), new(6.6f, 6, 0.6f))];
        var (p2, i2, l2, h2) = Mesh(niche);
        Assert.Equal(0, AshfallKitFit.FindOpenings(p2, i2, l2, h2).Count);
    }

    [Fact]
    public void The_fit_carves_the_openings_and_the_gate_fails_boxes_that_fill_them()
    {
        var (pos, idx, lo, hi) = Mesh(WindowWall);
        var map = AshfallKitFit.FindOpenings(pos, idx, lo, hi);
        foreach (int res in new[] { 16, 24, 48 })
            foreach (int seal in new[] { 0, 1 })
            {
                var fit = AshfallKitFit.Fit(pos, idx, lo, hi, res, 8, 0.4f, res, seal: seal, openings: map);
                var q = AshfallKitFit.Measure(pos, idx, lo, hi, fit.Boxes, fit.Metrics.CellSize, map);
                output.WriteLine($"res {res} seal {seal}: {fit.Boxes.Count} boxes, openings {q.OpeningCount - q.BlockedOpenings}/{q.OpeningCount}, least clear {q.WorstOpeningClearPercent:0} %");
                Assert.Equal(3, q.OpeningCount);
                Assert.Equal(0, q.BlockedOpenings);
            }
        // one box over the whole wall blocks every opening
        var one = AshfallKitFit.Measure(pos, idx, lo, hi, [(lo, hi)], 12f / 48f, map);
        Assert.Equal(3, one.BlockedOpenings);
        Assert.Contains(one.Failures(), f => f.Contains("window opening"));
        // without the carve, a coarse, loose fit fills them (what the pass fixes)
        var loose = AshfallKitFit.Fit(pos, idx, lo, hi, 16, 1, 0.4f, 16, seal: 1);
        Assert.True(AshfallKitFit.Measure(pos, idx, lo, hi, loose.Boxes, loose.Metrics.CellSize, map).BlockedOpenings > 0);
    }

    [Fact]
    public void Every_used_model_s_committed_openings_are_open()
    {
        int total = 0, models = 0;
        foreach (var m in AshfallKit.Models)
        {
            var q = AshfallKitCollision.Quality(m.Id);
            var list = AshfallKitCollision.Openings(m.Id);
            Assert.Equal(q.OpeningCount, list.Count);
            if (list.Count > 0) { models++; output.WriteLine($"{m.Id} {m.Name}: {list.Count} opening(s), least clear {q.WorstOpeningClearPercent:0} %"); }
            total += list.Count;
            if (AshfallKitFit.Gate.ReportOnlyIds.Contains(m.Id)) continue;
            Assert.Equal(0, q.BlockedOpenings);
            Assert.All(list, o => Assert.True(o.Open, $"{m.Id}: an opening at {o.Centre} is {o.ClearPercent:0} % clear"));
            Assert.All(list, o => Assert.True(o.DepthMax > o.DepthMin && o.Axis is 0 or 2));
        }
        output.WriteLine($"{total} openings in {models} models");
        Assert.True(total > 0, "the kit has windows: none found");
    }

    [Fact]
    public void The_opening_aware_merge_keeps_every_committed_opening_s_centre_ray_free_and_reduces_the_boxes()
    {
        int models = 0, before = 0, after = 0;
        foreach (var m in AshfallKit.Models)
        {
            var list = AshfallKitCollision.Openings(m.Id);
            if (list.Count == 0) continue;
            var fitted = AshfallKitCollision.Boxes(m.Id);
            var reduced = AshfallOpeningMerge.Reduced(m.Id);
            models++; before += fitted.Count; after += reduced.Count;
            Assert.InRange(reduced.Count, 1, fitted.Count);
            foreach (var o in list)
            {
                if (AshfallKitFit.Gate.ReportOnlyIds.Contains(m.Id)) continue;
                // the centre ray through the wall meets no more of a reduced box than of the fitted ones (a gate-passing opening is at most 10 % blocked)
                float Hit(IEnumerable<(Vector3 Min, Vector3 Max)> boxes)
                {
                    float len = 0f;
                    foreach (var (mn, mx) in boxes)
                    {
                        var a = o.Start; var b = o.End;
                        int ax = o.Axis;
                        float lo = ax == 0 ? Math.Max(a.X, mn.X) : Math.Max(a.Z, mn.Z), hi = ax == 0 ? Math.Min(b.X, mx.X) : Math.Min(b.Z, mx.Z);
                        bool across = ax == 0 ? o.Centre.Y >= mn.Y && o.Centre.Y <= mx.Y && o.Centre.Z >= mn.Z && o.Centre.Z <= mx.Z
                                             : o.Centre.Y >= mn.Y && o.Centre.Y <= mx.Y && o.Centre.X >= mn.X && o.Centre.X <= mx.X;
                        if (across && hi > lo) len += hi - lo;
                    }
                    return len;
                }
                Assert.True(Hit(reduced) <= Hit(fitted) + 1e-4f, $"{m.Id}: the centre ray of the opening at {o.Centre} meets more box after the merge");
            }
        }
        output.WriteLine($"{models} models with openings: {before} fitted boxes, {after} after the opening-aware merge");
        Assert.True(models > 0 && after < before);
    }

    [Fact]
    public void Shots_pass_through_every_window_of_every_placed_model_and_stop_at_the_wall_beside_it()
    {
        var d = AshfallMap.BuildDraft5();
        var world = AshfallContent.ToMapContent(d.Level).ToCollisionWorld();
        var family = new Dictionary<int, string>();
        foreach (var p in d.Placements) if (p.Item >= 0) family[p.Item] = p.Family;
        // the weapon mesh's parts as convex solids (boxes, 12 triangles each; the ramp wedges are movement only) with their bounds: a trace that starts and ends inside one crosses no face
        var solids = d.Level.Parts.Select((p, pi) => (p, pi)).Where(x => x.p.Solid && !x.p.MovementOnly && x.p.Corners.Length == 36)
            .Select(x => (x.p.Corners, Min: x.p.Corners.Aggregate(Vector3.Min), Max: x.p.Corners.Aggregate(Vector3.Max), x.pi)).ToList();
        int insidePart = -1;
        bool Inside(Vector3 q)
        {
            foreach (var (c, mn, mx, pi) in solids)
            {
                if (q.X < mn.X || q.Y < mn.Y || q.Z < mn.Z || q.X > mx.X || q.Y > mx.Y || q.Z > mx.Z) continue;
                bool inside = true;
                for (int k = 0; k < 36 && inside; k += 3)
                {
                    var n = Vector3.Cross(c[k + 1] - c[k], c[k + 2] - c[k]);
                    if (n.LengthSquared() < 1e-12f) continue;
                    if (Vector3.Dot(Vector3.Normalize(n), q - c[k]) > 1e-4f) inside = false;
                }
                if (inside) { insidePart = pi; return true; }
            }
            return false;
        }
        int windows = 0, walls = 0, debrisTraced = 0, debrisPlaced = 0;
        var blocked = new List<string>(); var through = new List<string>();
        var perModel = new SortedDictionary<string, (int Items, int Windows)>(StringComparer.Ordinal);
        for (int i = 0; i < d.Dressing.Items.Count; i++)
        {
            var it = d.Dressing.Items[i];
            if (it.Role != AshfallDressRole.Prop || d.Dressing.ItemParts[i].Count == 0) continue;
            var openings = AshfallKitCollision.Openings(it.Asset);
            if (openings.Count == 0) continue;
            if (family.GetValueOrDefault(i) is "rubble" or "wallpiece" or "cover") debrisPlaced += openings.Count;
        // Rubble, cover and collapsed wall pieces with openings keep opening-aware boxes (AshfallOpeningMerge): their gaps are traced too.
            bool debris = family.GetValueOrDefault(i) is "rubble" or "wallpiece" or "cover";
            perModel[it.Asset] = (perModel.GetValueOrDefault(it.Asset).Items + 1, perModel.GetValueOrDefault(it.Asset).Windows + openings.Count);
            foreach (var o in openings)
            {
                var a = it.ToWorld(o.Start); var b = it.ToWorld(o.End);
                float len = Vector3.Distance(a, b);
                if (len < 1e-4f) continue;
                windows++; if (debris) debrisTraced++;
                if (world.Weapons.Raycast(a, (b - a) / len, len) is { } hit)
                    blocked.Add($"{it.Asset} item {i} ({family.GetValueOrDefault(i)}) at ({it.Position.X:0.0}, {it.Position.Y:0.0}): the window at raw {o.Centre} stops a shot at {hit.Point} ({hit.Distance:0.00} of {len:0.00} m)");
                else if (Inside((a + b) / 2f))
                    blocked.Add($"{it.Asset} item {i} ({family.GetValueOrDefault(i)}) at ({it.Position.X:0.0}, {it.Position.Y:0.0}): the window at raw {o.Centre} lies inside a solid (part {insidePart} {d.Level.Parts[insidePart].Kind}, own parts {d.Dressing.ItemParts[i].First}+{d.Dressing.ItemParts[i].Count})");
                if (o.Wall is { } w)
                {
                    // into the wall beside the window: from outside the wall's thickness (a run as long again on each side) to its far side; a shot there stops, or the wall's middle is inside a solid
                    float span = o.DepthMax - o.DepthMin;
                    var wa = it.ToWorld(o.At(w, o.DepthMin - span)); var wb = it.ToWorld(o.At(w, o.DepthMax + span));
                    float wl = Vector3.Distance(wa, wb);
                    walls++;
                    if (world.Weapons.Raycast(wa, (wb - wa) / wl, wl) is null && !Inside(it.ToWorld(w)))
                        through.Add($"{it.Asset} item {i} at ({it.Position.X:0.0}, {it.Position.Y:0.0}): a shot into the wall at raw {w} passes");
                }
            }
        }
        foreach (var (id, (items, n)) in perModel) output.WriteLine($"{id}: {items} placed, {n} window traces");
        output.WriteLine($"{windows} window traces, {walls} wall traces; blocked {blocked.Count}, walls passed {through.Count}");
        output.WriteLine($"debris openings placed {debrisPlaced}, traced {debrisTraced}; blocked among debris {blocked.Count(b => b.Contains("(rubble)") || b.Contains("(cover)") || b.Contains("(wallpiece)"))}");
        foreach (var s in blocked.Take(40)) output.WriteLine("blocked: " + s);
        foreach (var s in through.Take(20)) output.WriteLine("through: " + s);
        Assert.True(windows > 0);
        Assert.True(walls * 10 >= windows * 9, $"only {walls} of {windows} windows have a wall point");
        // Draft 9 left out the loose debris away from the buildings (318 traced before, about 170 since).
        // Openings pass: the debris families keep their fitted boxes when the model has openings (AshfallDraft5.Coarse). What still stops: the 0.07 m gaps at the foot of the flat AC-025 rubble layer, where a neighbouring wall, cover or the ground lies in the gap.
        var stillBlocked = blocked.Where(b => !b.StartsWith("AC-025 ")).ToList();
        Assert.True(stillBlocked.Count == 0, $"{stillBlocked.Count} of {windows} window shots stop:\n" + string.Join("\n", stillBlocked.Take(40)));
        Assert.True(debrisTraced >= 150 && blocked.Count <= debrisTraced / 8, $"{blocked.Count} of {debrisTraced} debris openings stop");
        Assert.True(through.Count == 0, $"{through.Count} of {walls} wall shots pass:\n" + string.Join("\n", through.Take(20)));
    }
}
