using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall draft 7 (ours, placeholder demonstration; docs/musou-ashfall-graybox.md "Draft 7"): no overlapping objects. After an item has grown and settled (draft 6), its collision boxes (fitted to the model, so they
/// stand for the drawn mesh) are checked against every box placed before it; where two different items meet deeper than <see cref="AshfallOverlap.Tolerance"/> the later item yields: it is moved out along the
/// shallowest separating axis (never into a protected place it did not already touch, never adding a narrow gap), else its footprint shrinks (to 0.85, 0.7 of it; the stronghold's pieces to 0.55) and moves again, else the
/// pool's next model is tried, else it is left out and named in the overlap flag. Bridges are exempt (their ends rest on what carries them, an intended attachment); they are placed last against what stands.
/// </summary>
internal static partial class AshfallDraft5Pass
{
    static AshfallBox AsBox(WBox b) => new(b.Centre.X, b.Centre.Y, b.Centre.Z - b.Half.Z, b.Centre.Z + b.Half.Z, b.Half.X * 2f, b.Half.Y * 2f, b.YawStep);

    static bool Dust(WBox b) { var e = b.Half * 2f; return e.X < MinBox && e.Y < MinBox && e.Z < MinBox; }

    /// <summary>The deepest meeting of these boxes with a placed box of another item (bridges skipped), with that box; depth 0 or less when nothing meets beyond the tolerance.</summary>
    static (float Depth, WBox Mine, WBox Other, int Item) Deepest(Ctx c, List<WBox> boxes)
    {
        (float, WBox, WBox, int) best = (0f, default, default, -1);
        foreach (var b in boxes)
        {
            if (Dust(b)) continue;
            var ab = AsBox(b);
            var r = b.Rect;
            for (int gx = (int)MathF.Floor(r.X / OverlapCell); gx <= (int)MathF.Floor(r.Z / OverlapCell); gx++)
                for (int gy = (int)MathF.Floor(r.Y / OverlapCell); gy <= (int)MathF.Floor(r.W / OverlapCell); gy++)
                {
                    if (!c.Solid.TryGetValue((gx, gy), out var l)) continue;
                    foreach (var (oi, o) in l)
                    {
                        var q = o.Rect;
                        if (r.X >= q.Z || q.X >= r.Z || r.Y >= q.W || q.Y >= r.W) continue;
                        float d = AshfallOverlap.Depth(ab, AsBox(o));
                        if (d > AshfallOverlap.Tolerance && d > best.Item1) best = (d, b, o, oi);
                    }
                }
        }
        return best;
    }

    const float OverlapCell = 16f;

    /// <summary>Registers a placed box for the overlap rule (bridges are not registered: nothing yields to them, they come last).</summary>
    static void AddSolid(Ctx c, int item, WBox b)
    {
        var r = b.Rect;
        for (int gx = (int)MathF.Floor(r.X / OverlapCell); gx <= (int)MathF.Floor(r.Z / OverlapCell); gx++)
            for (int gy = (int)MathF.Floor(r.Y / OverlapCell); gy <= (int)MathF.Floor(r.W / OverlapCell); gy++)
            {
                if (!c.Solid.TryGetValue((gx, gy), out var l)) c.Solid[(gx, gy)] = l = [];
                l.Add((item, b));
            }
    }

    /// <summary>The move that takes box <paramref name="a"/> out of <paramref name="o"/> along their shallowest horizontal separating axis, to within half the tolerance (they still touch: no slot opens).</summary>
    static Vector2 Separate(WBox a, WBox o) => Separate(a, o, AshfallOverlap.Tolerance);

    static Vector2 Separate(WBox a, WBox o, float tol)
    {
        var aa = AsBox(a); var ob = AsBox(o);
        var delta = new Vector2(aa.Cx - ob.Cx, aa.Cy - ob.Cy);
        Vector2 bestAxis = Vector2.UnitX; float bestOver = float.MaxValue;
        foreach (var ax in new[] { aa.AxisX, aa.AxisY, ob.AxisX, ob.AxisY })
        {
            float ra = MathF.Abs(Vector2.Dot(aa.AxisX, ax)) * aa.Length / 2f + MathF.Abs(Vector2.Dot(aa.AxisY, ax)) * aa.Depth / 2f;
            float rb = MathF.Abs(Vector2.Dot(ob.AxisX, ax)) * ob.Length / 2f + MathF.Abs(Vector2.Dot(ob.AxisY, ax)) * ob.Depth / 2f;
            float over = ra + rb - MathF.Abs(Vector2.Dot(delta, ax));
            if (over < bestOver) { bestOver = over; bestAxis = Vector2.Dot(delta, ax) >= 0f ? ax : -ax; }
        }
        return bestAxis * (bestOver - tol / 2f);
    }

    /// <summary>
    /// Draft 7: settles a grown item so it meets no placed item deeper than the tolerance (moves, then smaller footprints); false when nothing within reach clears it. <paramref name="centre"/> and
    /// <paramref name="size"/> come back changed only on success.
    /// </summary>
    static bool Unoverlap(Ctx c, Spec s, string id, int yaw, float bottom, ref Vector2 centre, ref Vector3 size, out string note)
    {
        note = "";
        if (s.NoCollision) return true;
        AshfallDressItem Item(Vector2 at, Vector3 sz) => new(id, new Vector3(at.X, at.Y, bottom), yaw * (MathF.PI / 12f), sz, false, AshfallDressRole.Prop, -1);
        var start = CoarseBoxes(s, Item(centre, size), yaw, id);
        var first = Conflict(c, s, id, Item(centre, size), yaw, start);
        if (first.Excess <= 0f) return true;
        bool draft9 = first.What != "boxes";
        note = first.Item >= 0 && first.Item < c.Items.Count ? $"{c.Items[first.Item].Asset} item {first.Item}, {first.Excess + first.Tol:0.0} m deep" + (draft9 ? $" (draft 9: {first.What})" : "") : "";
        var allowed = new HashSet<int>();
        foreach (var b in start) foreach (int z in Zones(c, b)) allowed.Add(z);
        int narrow0 = CountNarrow(c, start);
        bool ring = s.Family.StartsWith("stronghold") || s.Family == "terrace" || s.Group.Contains(" breach ");
        float[] shrink = ring ? [1f, 0.85f, 0.7f, 0.55f] : [1f, 0.85f, 0.7f];
        float reach = MathF.Max(ring ? 2f * OverlapReach : OverlapReach, 0.25f * MathF.Max(size.X, size.Y));
        if (s.KeepLength)
        {
            // A leg wall keeps its place along the leg (no breach may close): it is trimmed at the end that meets the other piece (a corner tower, the gate), the far end staying put, by at most a third of its length.
            var (sn, cs) = AshfallBuilder.Trig(yaw);
            var axis = new Vector2(cs, sn);
            var at = centre; var sz = size;
            for (int k = 0; k < 6; k++)
            {
                var boxes = CoarseBoxes(s, Item(at, sz), yaw, id);
                var k2 = Conflict(c, s, id, Item(at, sz), yaw, boxes);
                var other = k2.Other;
                if (k2.Excess <= 0f && OffRoad(c, boxes))
                {
                    note = $"trimmed {size.X - sz.X:0.00} m at its end ({(draft9 ? "draft 9" : "overlap")})";
                    centre = at; size = sz;
                    return true;
                }
                float side = MathF.Sign(Vector2.Dot(new Vector2(other.Centre.X, other.Centre.Y) - at, axis));
                if (side == 0f) side = 1f;
                float trim = k2.Excess + k2.Tol / 2f;
                if (size.X - sz.X + trim > size.X / 3f) break;
                sz.X -= trim; at -= axis * (side * trim / 2f);
            }
            return false;
        }
        foreach (float f in shrink)
        {
            var sz = new Vector3(size.X * f, size.Y * f, size.Z);
            var off = Vector2.Zero;
            for (int k = 0; k < 24; k++)
            {
                var boxes = CoarseBoxes(s, Item(centre + off, sz), yaw, id);
                var k2 = Conflict(c, s, id, Item(centre + off, sz), yaw, boxes);
                bool zonesOk = boxes.All(b => Zones(c, b).All(allowed.Contains));
                if (k2.Excess <= 0f)
                {
                    if (zonesOk && (ring || CountNarrow(c, boxes) <= narrow0) && OffRoad(c, boxes))   // draft 8: never back onto a roadway
                    {
                        note = (f < 1f ? $"shrunk to {f:0.##} of its footprint" : "") + (off.Length() > 0.01f ? (f < 1f ? ", " : "") + $"moved {off.Length():0.00} m" : "") + (draft9 ? " (draft 9)" : " (overlap)");
                        centre += off; size = sz;
                        return true;
                    }
                    break;
                }
                off += Separate(k2.Mine, k2.Other, k2.Tol);
                if (off.Length() > reach) break;
            }
        }
        return false;
    }

    /// <summary>The farthest the overlap rule moves an item (or a quarter of its longer side, if more; ours).</summary>
    const float OverlapReach = 4f;
}
