using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall draft 6 (ours, placeholder demonstration; docs/musou-ashfall-graybox.md "Draft 6"): the owner's notes on draft 5. Every placed object is 1.5x its draft-5 size in all three axes (its foot stays on the ground,
/// its collision is transformed with it), moved back from a protected place it would grow into or left out; more AC-008 shooting platforms stand on free ground; a bridge is kept only when both narrow ends rest on
/// something at its deck height or it stands on its own legs, else it is lowered to the height its ends reach, else dropped. Deterministic like draft 5.
/// </summary>
internal static partial class AshfallDraft5Pass
{
    /// <summary>The owner's growth (2026-11-06: "scale to 1.5x the current size"): every placed object, all three axes.</summary>
    const float Scale6 = AshfallMap.Draft6Scale;

    /// <summary>The draft-6 total of AC-008 shooting platforms (owner: "more of the shooting platform objects"; the lead's target at least 45; draft 5 had 29).</summary>
    const int PlatformTarget = AshfallMap.Draft6PlatformTarget;

    /// <summary>A new platform keeps this much free ground to every other collision box below 8 m and to the other new platforms (ours: wider than the 2.4 m route rule, so no route closes).</summary>
    const float PlatformClear = 3f;

    /// <summary>The least distance between two AC-008 platforms' centres (ours; v5 placed its platforms 36 m apart, draft 6 adds them between).</summary>
    const float PlatformSpacing = 24f;

    static float DeckTop(IReadOnlyList<AshfallPart> parts, List<int> idx)
    {
        float top = float.MinValue, low = float.MaxValue;
        foreach (int i in idx)
        {
            foreach (var v in parts[i].Corners) low = MathF.Min(low, v.Z);
            if (parts[i].Kind == AshfallKind.Floor) foreach (var v in parts[i].Corners) top = MathF.Max(top, v.Z);
        }
        return top > float.MinValue ? top : low + 0.5f;
    }

    // ---------------------------------------------------------------- the growth

    /// <summary>
    /// Grows a draft-5 item 1.5x about its centre (its foot stays). When a collision box would stand in a protected place the item did not already touch at its draft-5 size, it is moved back along the shortest way out
    /// (at most the growth plus 1.5 m); false when that does not clear it (the item is left out).
    /// </summary>
    static bool Grow6(Ctx c, Spec s, string id, int yaw, float bottom, List<WBox> d5Boxes, ref Vector2 centre, ref Vector3 size, out float moved, bool final = true)
    {
        moved = 0f;
        var size6 = size * Scale6;
        if (s.KeepLength) size6.X = size.X;
        if (s.KeepWalkCell && Walkable.Contains(id))
        {
            float limit = StepHeight / AshfallKitCollision.Metrics(id).CellSize * AshfallKit.Get(id).RawSize.Y * 0.98f;   // a hair under the step
            if (size6.Z > limit) size6.Z = MathF.Max(limit, size.Z);
        }
        if (s.NoCollision)
        {
            if (s.Family == "backdrop")
            {
                // A backdrop block keeps its inner face (it grows away from the boundary).
                var e0 = new WBox(Vector3.Zero, size / 2f, yaw).Extent; var e1 = new WBox(Vector3.Zero, size6 / 2f, yaw).Extent;
                if (MathF.Abs(centre.X) >= MathF.Abs(centre.Y)) centre.X += MathF.Sign(centre.X) * (e1.X - e0.X); else centre.Y += MathF.Sign(centre.Y) * (e1.Y - e0.Y);
            }
            size = size6;
            return true;
        }
        var allowed = new HashSet<int>();
        foreach (var b in d5Boxes) foreach (int z in Zones(c, b)) if (z < 3000) allowed.Add(z);   // a breach is never allowed to a piece outside the ring, even one that touched it at its draft-5 size (phase 2: the new Twin tower east did)
        if (s.Family.StartsWith("stronghold") || s.Group.Contains(" breach ")) for (int i = 0; i < c.BreachRects.Count; i++) allowed.Add(3000 + i);   // the ring's own pieces and its breach rubble
        float cap = MathF.Max((Scale6 - 1f) / 2f * MathF.Max(size.X, size.Y), MinGap) + 1.5f;   // the growth, or room to open a gap, plus 1.5 m
        bool spread = !(s.Family.StartsWith("stronghold") || s.Group.Contains(" breach "));   // the ring's pieces and breach rubble keep their places along the leg
        Vector2? firstClear = null, bestClear = null;
        int bestNarrow = int.MaxValue;
        bool settled = false, gapPushed = false;
        var off = Vector2.Zero;
        // Two tries: open every narrow gap (move away from the neighbour); then, from the first clear place, the shorter of opening it or closing it (move until the boxes touch: no slot left between them).
        // A small piece (rubble, cover, a wall piece) has a third: close every narrow gap.
        int modes = s.Family is "rubble" or "cover" or "wallpiece" ? 3 : 2;
        for (int mode = 0; mode < modes && !settled; mode++)
        {
            if (mode > 0) { if (firstClear is not { } f0) break; off = f0; }
            for (int k = 0; k < 32; k++)
            {
                var at = centre + off;
                var item = new AshfallDressItem(id, new Vector3(at.X, at.Y, bottom), yaw * (MathF.PI / 12f), size6, false, AshfallDressRole.Prop, -1);
                var boxes = Coarse(s, Boxes(item, yaw, id));
                var push = Push(c, boxes, allowed);
                if (push is null)
                {
                    firstClear ??= off;
                    var gp = spread ? GapPush(c, boxes, close: mode) : null;
                    if (gp is null) { settled = true; break; }
                    int narrow = CountNarrow(c, boxes);
                    if (narrow < bestNarrow) { bestNarrow = narrow; bestClear = off; }
                    gapPushed = true;
                    push = gp;
                }
                off += push.Value;
                if (off.Length() > cap) break;
            }
        }
        if (firstClear is not { } first) return false;
        var done = settled ? off : bestClear ?? first;
        c.NarrowDrop = false;
        if (!settled && !final) return false;   // phase 2: another model of the pool may open every gap (the caller tries it; the last one keeps its fewest-gaps place)
        // Phase 2: a small piece (cover, rubble, a collapsed wall piece; not the breach rubble) that cannot open or close every narrow gap is left out: it never leaves a slot under 2.4 m (the route rule).
        if (!settled && modes == 3 && spread) { c.NarrowDrop = true; return false; }
        if (!settled) c.NarrowKept++;   // no place within reach clears every gap: of the places clear of the protected ones it visited, the one with the fewest narrow gaps is kept (counted in the growth flag)
        else if (gapPushed) c.Spread++;
        centre += done; size = size6; moved = done.Length();
        return true;
    }

    /// <summary>The narrow-gap rule's width (the lead's route rule: no passage under 2.4 m): two different items' boxes in the walking band either touch or leave this much between them.</summary>
    const float MinGap = 2.4f;

    static void AddLow(Ctx c, int item, WBox b)
    {
        if (b.Centre.Z - b.Half.Z >= 2.5f || b.Centre.Z + b.Half.Z <= 0.4f) return;
        var r = b.Rect;
        for (int gx = (int)MathF.Floor(r.X / 8f); gx <= (int)MathF.Floor(r.Z / 8f); gx++)
            for (int gy = (int)MathF.Floor(r.Y / 8f); gy <= (int)MathF.Floor(r.W / 8f); gy++)
            {
                if (!c.Low.TryGetValue((gx, gy), out var l)) c.Low[(gx, gy)] = l = [];
                l.Add((item, r));
            }
    }

    /// <summary>
    /// The move that opens the first gap between one of these boxes (in the walking band) and a placed box of another item that is wider than nothing and narrower than <see cref="MinGap"/>: away from that box,
    /// to <see cref="MinGap"/> plus 5 cm (<paramref name="close"/> 1: a gap under half of it is closed instead, towards the box until they touch; 2: every narrow gap is closed). Boxes that touch or overlap are left as they are (no slot between them).
    /// Null when every gap is open.
    /// </summary>
    static int CountNarrow(Ctx c, List<WBox> boxes)
    {
        int n = 0;
        foreach (var b in boxes)
        {
            if (b.Centre.Z - b.Half.Z >= 2.5f || b.Centre.Z + b.Half.Z <= 0.4f) continue;
            var r = b.Rect;
            var seen = new HashSet<Vector4>();
            for (int gx = (int)MathF.Floor((r.X - MinGap) / 8f); gx <= (int)MathF.Floor((r.Z + MinGap) / 8f); gx++)
                for (int gy = (int)MathF.Floor((r.Y - MinGap) / 8f); gy <= (int)MathF.Floor((r.W + MinGap) / 8f); gy++)
                    if (c.Low.TryGetValue((gx, gy), out var l))
                        foreach (var (_, q) in l)
                        {
                            float dx = MathF.Max(q.X - r.Z, r.X - q.Z), dy = MathF.Max(q.Y - r.W, r.Y - q.W);
                            if (dx <= 0f && dy <= 0f) continue;
                            float gap = dx > 0f && dy > 0f ? MathF.Sqrt(dx * dx + dy * dy) : MathF.Max(dx, dy);
                            if (gap < MinGap && seen.Add(q)) n++;
                        }
        }
        return n;
    }

    static Vector2? GapPush(Ctx c, List<WBox> boxes, int close = 0)
    {
        foreach (var b in boxes)
        {
            if (b.Centre.Z - b.Half.Z >= 2.5f || b.Centre.Z + b.Half.Z <= 0.4f) continue;
            var r = b.Rect;
            for (int gx = (int)MathF.Floor((r.X - MinGap) / 8f); gx <= (int)MathF.Floor((r.Z + MinGap) / 8f); gx++)
                for (int gy = (int)MathF.Floor((r.Y - MinGap) / 8f); gy <= (int)MathF.Floor((r.W + MinGap) / 8f); gy++)
                {
                    if (!c.Low.TryGetValue((gx, gy), out var l)) continue;
                    foreach (var (_, q) in l)
                    {
                        float dx = MathF.Max(q.X - r.Z, r.X - q.Z), dy = MathF.Max(q.Y - r.W, r.Y - q.W);
                        if (dx <= 0f && dy <= 0f) continue;   // touching or overlapping
                        float gap = dx > 0f && dy > 0f ? MathF.Sqrt(dx * dx + dy * dy) : MathF.Max(dx, dy);
                        if (gap >= MinGap) continue;
                        float sx = (r.X + r.Z) / 2f < (q.X + q.Z) / 2f ? -1f : 1f, sy = (r.Y + r.W) / 2f < (q.Y + q.W) / 2f ? -1f : 1f;
                        float need = close == 2 || close == 1 && gap < MinGap / 2f ? -(gap + 0.05f) : MinGap - gap + 0.05f;   // closing: towards the neighbour until they overlap by 5 cm
                        if (dx > 0f && dy > 0f) return Vector2.Normalize(new Vector2(sx * dx, sy * dy)) * need;
                        return dx > 0f ? new Vector2(sx * need, 0f) : new Vector2(0f, sy * need);
                    }
                }
        }
        return null;
    }

    /// <summary>The protected places a box stands in: banned rectangle i, banned disc 1000 + i, the boundary's 2 m 2000 (the same places as <see cref="Banned"/>; above 8 m only the boundary counts), a stronghold breach 3000 + i.</summary>
    static IEnumerable<int> Zones(Ctx c, WBox b)
    {
        var r = b.Rect;
        if (r.X < -238f || r.Y < -238f || r.Z > 238f || r.W > 238f) yield return 2000;
        if (b.Centre.Z - b.Half.Z >= 8f) yield break;
        for (int i = 0; i < c.BannedRects.Count; i++) { var z = c.BannedRects[i]; if (r.X < z.Z && r.Z > z.X && r.Y < z.W && r.W > z.Y) yield return i; }
        for (int i = 0; i < c.BannedDiscs.Count; i++)
        {
            var d = c.BannedDiscs[i];
            float dx = MathF.Max(MathF.Max(r.X - d.X, d.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - d.Y, d.Y - r.W), 0f);
            if (dx * dx + dy * dy < d.Z * d.Z) yield return 1000 + i;
        }
        for (int i = 0; i < c.BreachRects.Count; i++) { var z = c.BreachRects[i]; if (r.X < z.Z && r.Z > z.X && r.Y < z.W && r.W > z.Y) yield return 3000 + i; }
    }

    /// <summary>The shortest move (plus 5 cm) that takes the first offending box out of the first protected place it stands in that is not <paramref name="allowed"/>; null when none.</summary>
    static Vector2? Push(Ctx c, List<WBox> boxes, HashSet<int> allowed)
    {
        const float eps = 0.05f;
        foreach (var b in boxes)
        {
            var r = b.Rect;
            foreach (int z in Zones(c, b))
            {
                if (allowed.Contains(z)) continue;
                if (z == 2000)
                {
                    float px = MathF.Max(-238f - r.X, 0f) - MathF.Max(r.Z - 238f, 0f), py = MathF.Max(-238f - r.Y, 0f) - MathF.Max(r.W - 238f, 0f);
                    return new Vector2(px == 0f ? 0f : px + MathF.Sign(px) * eps, py == 0f ? 0f : py + MathF.Sign(py) * eps);
                }
                if (z < 1000 || z >= 3000)
                {
                    var q = z < 1000 ? c.BannedRects[z] : c.BreachRects[z - 3000];
                    (float Amount, Vector2 Dir)[] ways = [(r.Z - q.X, -Vector2.UnitX), (q.Z - r.X, Vector2.UnitX), (r.W - q.Y, -Vector2.UnitY), (q.W - r.Y, Vector2.UnitY)];
                    var best = ways[0];
                    foreach (var w in ways) if (w.Amount < best.Amount) best = w;
                    return best.Dir * (best.Amount + eps);
                }
                var d = c.BannedDiscs[z - 1000];
                var near = new Vector2(Math.Clamp(d.X, r.X, r.Z), Math.Clamp(d.Y, r.Y, r.W));
                var v = near - new Vector2(d.X, d.Y);
                float len = v.Length();
                if (len > 1e-3f) return v / len * (d.Z - len + eps);
                var away = new Vector2((r.X + r.Z) / 2f - d.X, (r.Y + r.W) / 2f - d.Y);
                if (away.LengthSquared() < 1e-6f) away = Vector2.UnitY;
                return Vector2.Normalize(away) * (d.Z + eps);
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- more shooting platforms

    /// <summary>
    /// Adds AC-008 shooting platforms on free ground until the level has <see cref="PlatformTarget"/>: candidates on a 4 m grid in a seeded order, those near an objective (10..40 m outside its capture radius) first,
    /// then those beside a route (8..30 m from an avenue's or the paved road's centre line); each on level ground, outside every protected place, <see cref="PlatformClear"/> m from every other collision box below 8 m,
    /// <see cref="PlatformSpacing"/> m from every other platform. The size is one of the draft-6 platforms' sizes (seeded pick), lowered uniformly where its scaled cell would exceed the 0.4 m step (the deck stays reachable where the model allows).
    /// </summary>
    static void ExtraPlatforms(Ctx c)
    {
        int have = PlatformIds.Sum(id => c.Uses.GetValueOrDefault(id));
        if (have >= PlatformTarget) return;
        // Draft 6, phase 2: the added platforms are the expansion's low and medium firing platforms (AC-049, AC-050) at their design size (smaller, their stairs are too narrow to walk: the graph test reached none of the 3/4 and 3/5 sizes), and AC-008 at the sizes draft 6 added it with
        // (12.5 x 4.3 x 2.3 m to 21.0 x 7.2 x 6.0 m); the two new models first (the seeded one, then the other), AC-008 (larger size first) only where neither fits. Every size is lowered uniformly where the model's
        // scaled cell would exceed the step (AC-050 at its design scale: 0.42 m, so it is drawn at about 10.1 m/unit).
        var sizesOf = new Dictionary<string, List<Vector3>>();
        foreach (var pid in PlatformIds)
        {
            var pm = AshfallKit.Get(pid);
            float limit = StepHeight / AshfallKitCollision.Metrics(pid).CellSize * pm.RawSize.Y;   // tallest the model may be drawn under the step
            var design = new Vector3(pm.RawSize.X, pm.RawSize.Z, pm.RawSize.Y) * pm.DesignScale;
            List<Vector3> raw = pid == "AC-008" ? [new(21.0f, 7.2f, 6.0f), new(16.5f, 5.7f, 3.5f), new(12.5f, 4.3f, 2.3f)] : [design];
            sizesOf[pid] = [.. raw.Select(sz => sz.Z > limit ? sz * (limit / sz.Z) : sz)];
        }
        string[] pickOrder = ["AC-049", "AC-050", "AC-008"];
        var points = c.BannedDiscs.Take(3).ToList();   // the capture zones' rings come first in BuildBanned (centre, radius + 6)
        float[] lanes = [-92f, 92f, -232f, 232f];
        var cands = new List<(uint Key, float X, float Y)>();
        uint n = 0;
        for (float x = -220f; x <= 220f; x += 4f)
            for (float y = -220f; y <= 220f; y += 4f)
            {
                uint key = AshfallBuilder.Mix(c.Seed ^ 0x9A7F0u, n++);
                float objective = points.Min(p => Vector2.Distance(new Vector2(x, y), new Vector2(p.X, p.Y)) - (p.Z - 6f));
                float route = lanes.Min(v => MathF.Min(MathF.Abs(x - v), MathF.Abs(y - v)));
                if (y > -160f && y < 80f) route = MathF.Min(route, MathF.Abs(x));
                uint tier = objective >= 10f && objective <= 40f ? 0u : route >= 8f && route <= 30f ? 1u : 2u;
                cands.Add(((tier << 30) | (key >> 2), x, y));
            }
        cands.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        var low = c.AllBoxes.Where(x => x.Box.Centre.Z - x.Box.Half.Z < 8f).Select(x => x.Box.Rect).ToList();
        var platforms = c.Items.Where(i => i.Role == AshfallDressRole.Prop && PlatformIds.Contains(i.Asset)).Select(i => new Vector2(i.Position.X, i.Position.Y)).ToList();
        static int Region(Vector2 p) => Math.Clamp((int)((p.X + 240f) / 160f), 0, 2) * 3 + Math.Clamp((int)((p.Y + 240f) / 160f), 0, 2);   // the map's 3 x 3 blocks of 160 m
        var perRegion = new int[9];
        foreach (var p in platforms) perRegion[Region(p)]++;
        // Every candidate that fits on its own (ground, protected places, the breaches, the clearance to what stands), in the seeded order.
        var valid = new List<(uint Key, Vector2 At, string Id, Vector3 Size, int Yaw, float Ground, Vector4 Rect)>();
        foreach (var (key, x, y) in cands)
        {
            var at = new Vector2(x, y);
            if (platforms.Any(p => Vector2.Distance(p, at) < PlatformSpacing)) continue;
            uint h = AshfallBuilder.Mix(key, 0x51A7u);
            float ground = AshfallMap.SurfaceAt(x, y);
            if (ground != AshfallMap.BlockTop) continue;   // on a block: the roads stay open
            // The new models first (seeded order), AC-008 last; each model's larger size first (a smaller platform where a larger one does not fit).
            if (Fit() is { } v) valid.Add(v);

            (uint, Vector2, string, Vector3, int, float, Vector4)? Fit()
            {
                int yaw = 6 * (int)((h >> 8) % 4u);
                for (int t = 0; t < pickOrder.Length; t++)
                {
                    string pid = t == 2 ? pickOrder[2] : pickOrder[(int)(((h >> 16) + (uint)t) % 2u)];
                    foreach (var size in sizesOf[pid])
                    {
                        var rect = new WBox(new Vector3(x, y, ground + size.Z / 2f), size / 2f, yaw).Rect;
                        if (rect.X < -222f || rect.Y < -222f || rect.Z > 222f || rect.W > 222f || !Level(rect, ground)) continue;
                        var grown = new Vector4(rect.X - PlatformClear, rect.Y - PlatformClear, rect.Z + PlatformClear, rect.W + PlatformClear);
                        if (c.BreachRects.Any(r => r.X < grown.Z && r.Z > grown.X && r.Y < grown.W && r.W > grown.Y)) continue;
                        if (low.Any(r => r.X < grown.Z && r.Z > grown.X && r.Y < grown.W && r.W > grown.Y)) continue;
                        var item = new AshfallDressItem(pid, new Vector3(x, y, ground), yaw * (MathF.PI / 12f), size, false, AshfallDressRole.Prop, -1);
                        var pboxes = Boxes(item, yaw, pid);
                        if (pboxes.Any(b => Banned(c, b))) continue;
                        if (!OffRoad(c, pboxes)) { c.RoadPlatformsSkipped++; continue; }   // draft 8: the roads stay open
                        return (key, at, pid, size, yaw, ground, rect);
                    }
                }
                return null;
            }
        }
        // Spread: each new platform goes to the 160 m block that has the fewest so far (ties: the seeded order), keeping the spacing and the clearance to the platforms added before it.
        int added = 0;
        var spec = new Spec("extra platform", "platform", Vector3.Zero, Vector3.Zero, null, PlatformIds);
        var mine = new List<Vector4>();
        while (have + added < PlatformTarget)
        {
            int pick = -1;
            foreach (int region in Enumerable.Range(0, 9).OrderBy(r => perRegion[r]).ThenBy(r => r))
            {
                pick = valid.FindIndex(v => Region(v.At) == region && !platforms.Any(p => Vector2.Distance(p, v.At) < PlatformSpacing)
                    && !mine.Any(r => r.X < v.Rect.Z + PlatformClear && r.Z > v.Rect.X - PlatformClear && r.Y < v.Rect.W + PlatformClear && r.W > v.Rect.Y - PlatformClear));
                if (pick >= 0) break;
            }
            if (pick < 0) break;
            var (key, at, pid, size, yaw, ground, rect) = valid[pick];
            valid.RemoveAt(pick);
            var item = new AshfallDressItem(pid, new Vector3(at.X, at.Y, ground), yaw * (MathF.PI / 12f), size, false, AshfallDressRole.Prop, -1);
            var s = spec with { Group = $"extra platform {added}", Min = new Vector3(rect.X, rect.Y, ground), Max = new Vector3(rect.Z, rect.W, ground + size.Z) };
            Emit(c, s, item, yaw, pid, Boxes(item, yaw, pid), $"placed, added in draft 6 ({(key >> 30 == 0 ? "near an objective" : key >> 30 == 1 ? "beside a route" : "open ground")})", at);
            mine.Add(rect);
            platforms.Add(at);
            perRegion[Region(at)]++;
            added++;
        }
        c.Flags.Add($"draft 6: {added} shooting platforms added on free ground (AC-049 x{c.Items.Skip(c.Items.Count - added).Count(i => i.Asset == "AC-049")}, AC-050 x{c.Items.Skip(c.Items.Count - added).Count(i => i.Asset == "AC-050")}, AC-008 x{c.Items.Skip(c.Items.Count - added).Count(i => i.Asset == "AC-008")}; {have} from draft 5 after the growth, {have + added} in total; target {PlatformTarget}; bare edges, nothing added to them)");
    }

    /// <summary>Whether the ground under a footprint (and 1 m around) is one level <paramref name="ground"/>: no kerb, crater or rim under a platform.</summary>
    static bool Level(Vector4 r, float ground)
    {
        for (float x = r.X - 1f; x <= r.Z + 1.001f; x += 1f)
            for (float y = r.Y - 1f; y <= r.W + 1.001f; y += 1f)
                if (AshfallMap.SurfaceAt(x, y) != ground) return false;
        return true;
    }

    // ---------------------------------------------------------------- bridges

    /// <summary>
    /// One AC-009 over a v5 bridge's span (x <paramref name="mn"/>..<paramref name="mx"/>, its narrow ends at the two x ends), placed after everything else. Kept when (a) both narrow ends rest on a placed structure
    /// whose collision top is within the 0.4 m step of the deck (the item spans end to end, cross-section 1.5x its draft-5 size), or (b) it stands on its own legs on the ground with the deck at the v5 height;
    /// else (c) lowered to the highest height both ends reach (2 m or more), else dropped and flagged. Never through a structure mid-span, never in a protected place below 8 m.
    /// </summary>
    static void Bridge6(Ctx c, string name, Vector3 mn, Vector3 mx, float deck)
    {
        // Draft 6, phase 2: the expansion's stone bridge (AC-045, on arches, open ends) first, then AC-009; the first that rests or stands is kept.
        var why = new List<string>();
        foreach (var id in BridgeIds)
        {
            string? fail = Bridge6(c, name, mn, mx, deck, id);
            if (fail is null) return;
            why.Add($"{id}: {fail}");
        }
        c.Log.Add(new(name, "bridge", "", -1, "dropped: neither end rests on a structure at its deck, its legs do not stand clear, and the ends reach no common height"));
        c.Flags.Add($"{name}: dropped, its narrow ends do not both rest on a structure at its {deck:0.0} m deck ({string.Join("; ", why)})");
    }

    /// <summary>One bridge model over the span; null when it was placed, else why not.</summary>
    static string? Bridge6(Ctx c, string name, Vector3 mn, Vector3 mx, float deck, string id)
    {
        var m = AshfallKit.Get(id);
        var r = m.RawSize;
        float span = mx.X - mn.X, width = mx.Y - mn.Y, h = mx.Z - mn.Z, yc = (mn.Y + mx.Y) / 2f, xc = (mn.X + mx.X) / 2f;
        // The draft-5 cross-section (the placer's stretch rule for this span), grown 1.5x.
        float kx = span / r.X, kh = h / r.Y, kd = width / r.Z, sc = MathF.Sqrt(kx * kh);
        float Cl(float k) => MathF.Min(MathF.Max(k, 0.8f * sc), 1.25f * sc);
        float depth = MathF.Min(Cl(kd) * r.Z, width + 2f * Grow) * Scale6, tall = MathF.Min(Cl(kh) * r.Y, h) * Scale6;
        var spec = new Spec(name, "bridge", mn, mx, null, [id]) { UseNet = false };

        AshfallDressItem At(float bottom, Vector3 size) => new(id, new Vector3(xc, yc, bottom), 0f, size, false, AshfallDressRole.Prop, -1);
        float DeckOf(AshfallDressItem it) => DeckRel(Boxes(it, 0, id), it.Position.Z, xc, yc);

        // (a) resting on both ends at the v5 deck.
        var sizeA = new Vector3(span, depth, tall);
        var probe = At(0f, sizeA);
        float relA = DeckOf(probe);
        bool TryRest(float level, string why)
        {
            var item = At(level - relA, sizeA);
            var boxes = Boxes(item, 0, id);
            if (!Supported(c, mn.X, level, yc, width) || !Supported(c, mx.X, level, yc, width)) return false;
            if (boxes.Any(b => Banned(c, b)) || ThroughMidSpan(c, boxes, mn.X + 2f, mx.X - 2f, level) || !OffRoad(c, boxes, bridge: true)) return false;
            Emit(c, spec, item, 0, id, boxes, why, new Vector2(xc, yc));
            return true;
        }
        if (TryRest(deck, $"placed, both ends rest on structures at its deck {deck:0.0} m")) return null;

        // (b) on its own legs, the deck at the v5 height.
        float ground = MathF.Max(AshfallMap.SurfaceAt(mn.X, yc), AshfallMap.SurfaceAt(mx.X, yc));
        string legsWhy = "the model has no deck over its centre";
        {
            var unit = At(ground, new Vector3(span, depth, 1f));
            float rel = DeckOf(unit);   // the deck's height per metre of the item's height
            if (rel > 0.05f)
            {
                var item = At(ground, new Vector3(span, depth, (deck - ground) / rel));
                var boxes = Boxes(item, 0, id);
                bool legs = boxes.Count(b => b.Centre.Z - b.Half.Z <= ground + 0.05f) >= 2 && LegsAtBothEnds(boxes, ground, xc);
                bool banned = boxes.Any(b => Banned(c, b)) || !OffRoad(c, boxes, bridge: true), meets = Overlaps(c, boxes, mn.X + 0.3f * span, mx.X - 0.3f * span);
                legsWhy = !legs ? "no legs at both ends" : banned ? "a leg or a low box stands in a protected place" : meets ? "it meets a structure between its end thirds" : "";
                if (legs && !banned && !meets)
                {
                    Emit(c, spec, item, 0, id, boxes, $"placed, stands on its own legs, deck {deck:0.0} m", new Vector2(xc, yc));
                    c.Flags.Add($"{name}: {id} stands on its own legs (its ends do not reach the {deck:0.0} m deck)");
                    return null;
                }
            }
        }

        // (c) lowered to the highest height both ends reach.
        var ea = Surfaces(c, mn.X, yc, width); var eb = Surfaces(c, mx.X, yc, width);
        var common = new List<float>();
        foreach (float a in ea) foreach (float b in eb) if (MathF.Abs(a - b) <= StepHeight && MathF.Max(a, b) >= 2f) common.Add(MathF.Max(a, b));
        foreach (float level in common.Distinct().OrderByDescending(v => v))
            if (level < deck - StepHeight && TryRest(level, $"placed, lowered from {deck:0.0} m to {level:0.0} m where both ends rest"))
            {
                c.Flags.Add($"{name}: {id} lowered from {deck:0.0} m to {level:0.0} m, the height both its ends reach");
                return null;
            }
        return $"the ends reach {Describe(ea)} and {Describe(eb)}, no common height of 2 m or more that stays clear, and on its own legs {legsWhy}";
    }

    static string Describe(List<float> tops) => tops.Count == 0 ? "nothing" : string.Join("/", tops.OrderBy(t => t).Select(t => t.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))) + " m";

    /// <summary>The deck's height above the item's foot: the highest top of the boxes over the item's centre (the railings stand at the sides).</summary>
    static float DeckRel(List<WBox> boxes, float foot, float x, float y)
    {
        float top = float.MinValue;
        foreach (var b in boxes) { var r = b.Rect; if (x >= r.X && x <= r.Z && y >= r.Y && y <= r.W) top = MathF.Max(top, b.Centre.Z + b.Half.Z); }
        if (top == float.MinValue) foreach (var b in boxes) { var r = b.Rect; if (y >= r.Y && y <= r.W) top = MathF.Max(top, b.Centre.Z + b.Half.Z); }
        return top == float.MinValue ? 0f : top - foot;
    }

    /// <summary>The standable tops at a narrow end (x = <paramref name="x"/>, 1 m either side, three points across the width): the top of every placed box there with 1.8 m of headroom above it.</summary>
    static List<float> Surfaces(Ctx c, float x, float y, float width)
    {
        var tops = new List<float>();
        foreach (float sx in new[] { x - 1f, x, x + 1f })
            foreach (float sy in new[] { y - width / 3f, y, y + width / 3f })
            {
                var col = c.AllBoxes.Where(q => !BridgeIds.Contains(c.Items[q.Item].Asset)).Select(q => q.Box).Where(b => { var r = b.Rect; return sx >= r.X && sx <= r.Z && sy >= r.Y && sy <= r.W; }).ToList();
                foreach (var b in col)
                {
                    float t = b.Centre.Z + b.Half.Z;
                    if (col.Any(o => o.Centre.Z - o.Half.Z < t + 1.8f && o.Centre.Z + o.Half.Z > t + 0.05f)) continue;
                    tops.Add(MathF.Round(t, 2));
                }
            }
        return [.. tops.Distinct()];
    }

    static bool Supported(Ctx c, float x, float level, float y, float width) => Surfaces(c, x, y, width).Any(t => MathF.Abs(t - level) <= StepHeight);

    /// <summary>Whether a placed box stands in the bridge's way between its end zones (from 0.4 m under its deck up to 1.9 m over it).</summary>
    static bool ThroughMidSpan(Ctx c, List<WBox> bridge, float x0, float x1, float level)
    {
        float y0 = bridge.Min(b => b.Rect.Y), y1 = bridge.Max(b => b.Rect.W);
        foreach (var (_, b) in c.AllBoxes)
        {
            var r = b.Rect;
            if (r.X < x1 && r.Z > x0 && r.Y < y1 && r.W > y0 && b.Centre.Z - b.Half.Z < level + 1.9f && b.Centre.Z + b.Half.Z > level + StepHeight) return true;
        }
        return false;
    }

    static bool LegsAtBothEnds(List<WBox> boxes, float ground, float xc)
    {
        var feet = boxes.Where(b => b.Centre.Z - b.Half.Z <= ground + 0.05f).ToList();
        return feet.Any(b => b.Centre.X < xc) && feet.Any(b => b.Centre.X > xc);
    }

    /// <summary>Whether any of the boxes meets a placed box (more than 0.3 m deep on every axis) between x <paramref name="x0"/> and <paramref name="x1"/> (the legs may lean on what stands at the ends).</summary>
    static bool Overlaps(Ctx c, List<WBox> boxes, float x0, float x1)
    {
        foreach (var b in boxes)
            foreach (var (_, o) in c.AllBoxes)
            {
                var ro = o.Rect;
                if (ro.Z <= x0 || ro.X >= x1) continue;
                var e1 = b.Extent; var e2 = o.Extent;
                if (MathF.Abs(b.Centre.X - o.Centre.X) < e1.X + e2.X - 0.3f && MathF.Abs(b.Centre.Y - o.Centre.Y) < e1.Y + e2.Y - 0.3f && MathF.Abs(b.Centre.Z - o.Centre.Z) < b.Half.Z + o.Half.Z - 0.3f) return true;
            }
        return false;
    }
}
