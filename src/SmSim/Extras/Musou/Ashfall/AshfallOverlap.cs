using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Draft 7's overlap rule (owner, 2026-11-06: "change the generation rules so this doesn't happen on the next musou map draft. there are overlapping objects here."; ours, placeholder demonstration):
/// two different placed items' collision boxes (the boxes fitted to their models, so they stand for the drawn meshes) may touch or meet by at most <see cref="Tolerance"/> metres of penetration
/// (the depth along the box pair's shallowest separating axis). Intended attachments are exempt: a bridge resting on what carries its ends. docs/musou-ashfall-graybox.md "Draft 7".
/// </summary>
public static class AshfallOverlap
{
    /// <summary>The deepest two different items' boxes may meet (ours: the narrow-gap rule closes slots by 5 cm, the fitted boxes stand up to a cell proud of the mesh).</summary>
    public const float Tolerance = 0.3f;

    /// <summary>One overlapping pair of items: their indices (A placed before B), the deepest penetration of any of their box pairs (metres) and the first box pair's overlap centre.</summary>
    public readonly record struct Pair(int A, int B, float Depth, Vector3 At);

    /// <summary>The penetration depth of two boxes: the least overlap along Z and the four horizontal axes of the two boxes (0 or less when they are apart).</summary>
    public static float Depth(AshfallBox a, AshfallBox b)
    {
        float d = MathF.Min(a.Z1, b.Z1) - MathF.Max(a.Z0, b.Z0);
        if (d <= 0f) return d;
        var delta = new Vector2(b.Cx - a.Cx, b.Cy - a.Cy);
        Span<Vector2> axes = [a.AxisX, a.AxisY, b.AxisX, b.AxisY];
        foreach (var ax in axes)
        {
            float ra = MathF.Abs(Vector2.Dot(a.AxisX, ax)) * a.Length / 2f + MathF.Abs(Vector2.Dot(a.AxisY, ax)) * a.Depth / 2f;
            float rb = MathF.Abs(Vector2.Dot(b.AxisX, ax)) * b.Length / 2f + MathF.Abs(Vector2.Dot(b.AxisY, ax)) * b.Depth / 2f;
            d = MathF.Min(d, ra + rb - MathF.Abs(Vector2.Dot(delta, ax)));
            if (d <= 0f) return d;
        }
        return d;
    }

    /// <summary>The boxes of each item (box parts only: ramp wedges are part of their item's own boxes' tops).</summary>
    public static List<(int Item, AshfallBox Box)> Boxes(AshfallLevel level, AshfallDressing dressing)
    {
        var all = new List<(int, AshfallBox)>();
        for (int i = 0; i < dressing.Items.Count && i < dressing.ItemParts.Count; i++)
        {
            var (first, count) = dressing.ItemParts[i];
            for (int p = first; p < first + count; p++)
                if (level.Parts[p].Kind != AshfallKind.Stair && AshfallBox.Of(level.Parts[p]) is { } b) all.Add((i, b));
        }
        return all;
    }

    /// <summary>Every pair of different items whose boxes meet deeper than <paramref name="tolerance"/>, the deepest per pair; <paramref name="exempt"/> skips intended attachments.</summary>
    public static List<Pair> Find(IReadOnlyList<(int Item, AshfallBox Box)> boxes, float tolerance = Tolerance, Func<int, int, bool>? exempt = null)
    {
        const float cell = 16f;
        var grid = new Dictionary<(int, int), List<int>>();
        var rects = new Vector4[boxes.Count];
        for (int k = 0; k < boxes.Count; k++)
        {
            var b = boxes[k].Box;
            var (s, c) = AshfallBuilder.Trig(b.YawStep);
            float ex = MathF.Abs(c) * b.Length / 2f + MathF.Abs(s) * b.Depth / 2f, ey = MathF.Abs(s) * b.Length / 2f + MathF.Abs(c) * b.Depth / 2f;
            rects[k] = new Vector4(b.Cx - ex, b.Cy - ey, b.Cx + ex, b.Cy + ey);
            for (int gx = (int)MathF.Floor(rects[k].X / cell); gx <= (int)MathF.Floor(rects[k].Z / cell); gx++)
                for (int gy = (int)MathF.Floor(rects[k].Y / cell); gy <= (int)MathF.Floor(rects[k].W / cell); gy++)
                {
                    if (!grid.TryGetValue((gx, gy), out var l)) grid[(gx, gy)] = l = [];
                    l.Add(k);
                }
        }
        var best = new Dictionary<(int, int), Pair>();
        foreach (var l in grid.Values)
            for (int x = 0; x < l.Count; x++)
                for (int y = x + 1; y < l.Count; y++)
                {
                    int p = l[x], q = l[y];
                    int ia = boxes[p].Item, ib = boxes[q].Item;
                    if (ia == ib) continue;
                    var ra = rects[p]; var rb = rects[q];
                    if (ra.X >= rb.Z || rb.X >= ra.Z || ra.Y >= rb.W || rb.Y >= ra.W) continue;
                    var key = ia < ib ? (ia, ib) : (ib, ia);
                    if (exempt is not null && exempt(key.Item1, key.Item2)) continue;
                    float d = Depth(boxes[p].Box, boxes[q].Box);
                    if (d <= tolerance) continue;
                    if (best.TryGetValue(key, out var old) && old.Depth >= d) continue;
                    var a = boxes[p].Box; var b = boxes[q].Box;
                    best[key] = new Pair(key.Item1, key.Item2, d, new Vector3((a.Cx + b.Cx) / 2f, (a.Cy + b.Cy) / 2f, (MathF.Max(a.Z0, b.Z0) + MathF.Min(a.Z1, b.Z1)) / 2f));
                }
        return [.. best.Values.OrderBy(v => v.A).ThenBy(v => v.B)];
    }
}

public static partial class AshfallMap
{
    /// <summary>Draft 7: the intended attachments the overlap rule skips: a bridge (AC-045, AC-009) and what carries its ends.</summary>
    public static Func<int, int, bool> OverlapExempt(AshfallDressing dressing) =>
        (a, b) => AshfallDraft5Pass.BridgeIds.Contains(dressing.Items[a].Asset) || AshfallDraft5Pass.BridgeIds.Contains(dressing.Items[b].Asset);
}
