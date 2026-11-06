using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Draft 8's roadway rule (owner, 2026-11-06: "we need to ensure another parameter for procedural generation is followed: no 3d objects should be placed in roadways."; ours, placeholder demonstration;
/// docs/musou-ashfall-graybox.md "Draft 8 roadways"). A roadway is a paved strip the level lays out: the avenues (24 m) and the perimeter road (16 m) between the blocks, the strips the road tiles cover
/// (<c>AshfallDressPass.RoadLines</c>, the whole map's length, junctions included), and the level's own <see cref="AshfallKind.Road"/> parts (the paved road from the compound's gate to the plaza). The plaza
/// (<see cref="AshfallKind.Paving"/>) is objective B's square, not a road. No placed item's collision box may meet a roadway grown by <see cref="Margin"/>; a bridge (AC-045, AC-009) may span one when none of its
/// boxes reaches below <see cref="Headroom"/> over the road surface (nothing of it stands on the road).
/// </summary>
public static class AshfallRoadway
{
    /// <summary>The clear margin kept beside a roadway's edge (ours: a kerb's width, so no piece leans over the paving).</summary>
    public const float Margin = 0.5f;

    /// <summary>A bridge's box whose bottom is this high over the road surface spans the road, it does not stand on it (ours: the walking band's top, the narrow-gap rule's 2.5 m).</summary>
    public const float Headroom = 2.5f;

    /// <summary>The roadway strips as boxes on the ground plane from the road surface (0) up, each grown by <paramref name="margin"/> on every side; <paramref name="top"/> is the strips' top.</summary>
    public static List<AshfallBox> Strips(AshfallLevel level, float margin = Margin, float top = 1000f)
    {
        var list = new List<AshfallBox>();
        float len = 2f * AshfallMap.Half;
        foreach (var (c, w) in AshfallDressPass.RoadLines)
        {
            list.Add(new AshfallBox(c, 0f, -2f, top, w + 2f * margin, len, 0));   // north-south
            list.Add(new AshfallBox(0f, c, -2f, top, len, w + 2f * margin, 0));   // east-west
        }
        foreach (var p in level.Parts)
            if (p.Kind == AshfallKind.Road && AshfallBox.Of(p) is { } b)
                list.Add(b with { Z0 = -2f, Z1 = top, Length = b.Length + 2f * margin, Depth = b.Depth + 2f * margin });
        return list;
    }

    /// <summary>One item in a roadway: its index, the deepest meeting of its boxes with a strip (metres, the margin included) and that box's centre.</summary>
    public readonly record struct Hit(int Item, float Depth, Vector3 At);

    /// <summary>Whether this asset is a bridge (judged by <see cref="Headroom"/>: it may span a road).</summary>
    public static bool IsBridge(string asset) => AshfallDraft5Pass.BridgeIds.Contains(asset);

    /// <summary>The meeting depth of a box with a strip (0 or less when apart). A bridge's box counts only below <see cref="Headroom"/> over the road surface.</summary>
    public static float Depth(AshfallBox box, AshfallBox strip, bool bridge) =>
        AshfallOverlap.Depth(box, bridge ? strip with { Z1 = MathF.Min(strip.Z1, Headroom) } : strip);

    /// <summary>Every placed item with a collision box in a roadway (grown by <paramref name="margin"/>), the deepest per item.</summary>
    public static List<Hit> Find(AshfallLevel level, AshfallDressing dressing, float margin = Margin)
    {
        var strips = Strips(level, margin);
        var best = new Dictionary<int, Hit>();
        foreach (var (item, box) in AshfallOverlap.Boxes(level, dressing))
        {
            bool bridge = IsBridge(dressing.Items[item].Asset);
            foreach (var s in strips)
            {
                float d = Depth(box, s, bridge);
                if (d <= 1e-3f) continue;
                if (best.TryGetValue(item, out var old) && old.Depth >= d) continue;
                best[item] = new Hit(item, d, new Vector3(box.Cx, box.Cy, box.Z0));
            }
        }
        return [.. best.Values.OrderBy(h => h.Item)];
    }
}
