using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall draft 8 (ours, placeholder demonstration; docs/musou-ashfall-graybox.md "Draft 8 roadways"): no placed 3D item stands in a roadway (<see cref="AshfallRoadway"/>). After an item has grown (draft 6) and before
/// the overlap rule (draft 7) settles it, its collision boxes are checked against the roadway strips grown by <see cref="AshfallRoadway.Margin"/>; an item in one is moved off it along the shallowest separating axis
/// (across the road), at most <see cref="RoadReach"/> m or a quarter of its longer side, never into a protected place it did not already touch, never adding a narrow gap under 2.4 m; else its footprint shrinks
/// (0.85, 0.7; the stronghold's pieces 0.55) and moves again; else the pool's next model is tried; else it is left out and named in the roadway flag. The overlap rule's moves never put an item back on a road.
/// A bridge may span a road when none of its boxes reaches below <see cref="AshfallRoadway.Headroom"/> over it; an added shooting platform's candidate in a roadway is skipped.
/// </summary>
internal static partial class AshfallDraft5Pass
{
    /// <summary>The farthest the roadway rule moves an item (ours: half an avenue's 24 m, the margin and a step, so a piece in the middle of an avenue reaches its kerb).</summary>
    const float RoadReach = 14f;

    /// <summary>A leg band's keep-out across the leg (ours: the leg walls grow to 6 m thick, so 4 m each side keeps a piece pushed off a road from taking the ring's place).</summary>
    const float RingBand = 4f;

    /// <summary>Whether a box (below 8 m) meets the band of a stronghold leg (the leg's line, its corners included, widened by <see cref="RingBand"/>): a piece pushed off a road never lands where the ring stands.</summary>
    static bool InRingBand(WBox b)
    {
        if (b.Centre.Z - b.Half.Z >= 8f) return false;
        var r = b.Rect;
        foreach (var (_, alongX, t0, t1, lo, hi, _, _) in Legs)
        {
            var z = alongX ? new Vector4(lo - 10f, t0 - RingBand, hi + 10f, t1 + RingBand) : new Vector4(t0 - RingBand, lo - 10f, t1 + RingBand, hi + 10f);
            if (r.X < z.Z && r.Z > z.X && r.Y < z.W && r.W > z.Y) return true;
        }
        return false;
    }

    /// <summary>The deepest meeting of these boxes with a roadway strip (dust skipped), with the box and the strip; depth 0 or less when every box is clear (or the rule is off).</summary>
    static (float Depth, WBox Mine, AshfallBox Strip) RoadDeepest(Ctx c, List<WBox> boxes, bool bridge)
    {
        (float, WBox, AshfallBox) best = (0f, default, default);
        if (!c.RoadwayRule) return best;
        foreach (var b in boxes)
        {
            if (Dust(b)) continue;
            var ab = AsBox(b);
            foreach (var s in c.Roads)
            {
                float d = AshfallRoadway.Depth(ab, s, bridge);
                if (d > 1e-3f && d > best.Item1) best = (d, b, s);
            }
        }
        return best;
    }

    /// <summary>Whether none of these boxes stands in a roadway (always true with the rule off).</summary>
    static bool OffRoad(Ctx c, List<WBox> boxes, bool bridge = false) => RoadDeepest(c, boxes, bridge).Depth <= 0f;

    /// <summary>The move that takes box <paramref name="a"/> out of the strip along their shallowest horizontal separating axis, plus 5 cm.</summary>
    static Vector2 SeparateFromRoad(WBox a, AshfallBox o)
    {
        var aa = AsBox(a);
        var delta = new Vector2(aa.Cx - o.Cx, aa.Cy - o.Cy);
        Vector2 bestAxis = Vector2.UnitX; float bestOver = float.MaxValue;
        foreach (var ax in new[] { aa.AxisX, aa.AxisY, o.AxisX, o.AxisY })
        {
            float ra = MathF.Abs(Vector2.Dot(aa.AxisX, ax)) * aa.Length / 2f + MathF.Abs(Vector2.Dot(aa.AxisY, ax)) * aa.Depth / 2f;
            float rb = MathF.Abs(Vector2.Dot(o.AxisX, ax)) * o.Length / 2f + MathF.Abs(Vector2.Dot(o.AxisY, ax)) * o.Depth / 2f;
            float over = ra + rb - MathF.Abs(Vector2.Dot(delta, ax));
            if (over < bestOver) { bestOver = over; bestAxis = Vector2.Dot(delta, ax) >= 0f ? ax : -ax; }
        }
        return bestAxis * (bestOver + 0.05f);
    }

    /// <summary>
    /// Draft 8: settles a grown item off every roadway (moves, then smaller footprints); false when nothing within reach clears it. <paramref name="centre"/> and <paramref name="size"/> come back changed only on success.
    /// </summary>
    static bool Unroad(Ctx c, Spec s, string id, int yaw, float bottom, ref Vector2 centre, ref Vector3 size, out string note)
    {
        note = "";
        if (s.NoCollision || !c.RoadwayRule) return true;
        AshfallDressItem Item(Vector2 at, Vector3 sz) => new(id, new Vector3(at.X, at.Y, bottom), yaw * (MathF.PI / 12f), sz, false, AshfallDressRole.Prop, -1);
        var start = Coarse(s, Boxes(Item(centre, size), yaw, id));
        var first = RoadDeepest(c, start, false);
        if (first.Depth <= 0f) return true;
        note = $"{first.Depth:0.0} m into a roadway";
        var allowed = new HashSet<int>();
        foreach (var b in start) foreach (int z in Zones(c, b)) allowed.Add(z);
        int narrow0 = CountNarrow(c, start);
        bool ring = s.Family.StartsWith("stronghold") || s.Family is "terrace" or "outerwall" || s.Group.Contains(" breach ");   // the stronghold's pieces (its outer walls too: they stand on the avenue's kerb)
        float[] shrink = ring ? [1f, 0.85f, 0.7f, 0.55f] : [1f, 0.85f, 0.7f];
        float reach = MathF.Max(RoadReach, 0.25f * MathF.Max(size.X, size.Y));
        foreach (float f in shrink)
        {
            // a leg wall keeps its length (no breach may close): only its depth shrinks
            var sz = new Vector3(s.KeepLength ? size.X : size.X * f, size.Y * f, size.Z);
            var off = Vector2.Zero;
            for (int k = 0; k < 24; k++)
            {
                var boxes = Coarse(s, Boxes(Item(centre + off, sz), yaw, id));
                var (depth, mine, strip) = RoadDeepest(c, boxes, false);
                if (depth <= 0f)
                {
                    bool zonesOk = boxes.All(b => Zones(c, b).All(allowed.Contains));
                    if (zonesOk && (ring || CountNarrow(c, boxes) <= narrow0) && (ring || !boxes.Any(b => InRingBand(b))))
                    {
                        note = (f < 1f ? $"shrunk to {f:0.##} of its footprint" : "") + (off.Length() > 0.01f ? (f < 1f ? ", " : "") + $"moved {off.Length():0.00} m" : "") + " (roadway)";
                        centre += off; size = sz;
                        return true;
                    }
                    break;
                }
                off += SeparateFromRoad(mine, strip);
                if (off.Length() > reach) break;
            }
        }
        return false;
    }
}
