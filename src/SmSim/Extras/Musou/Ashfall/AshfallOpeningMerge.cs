using System.Collections.Concurrent;
using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// The openings pass (2026-11-06, ours): rubble, cover and collapsed-wall models that have through-openings (gaps in a debris pile, a window in a wall piece; <see cref="AshfallKitCollision.Openings"/>)
/// are reduced in the model's raw frame by an opening-aware merge instead of the level pass's six slabs (<c>AshfallDraft5.Coarse</c>, which fills every gap). Boxes are merged greedily, the pair whose union wastes
/// the least volume first, down to <see cref="MaxBoxes"/> boxes, and a merge is refused when the union would reach into an opening's core (the centre ray's tunnel thickened by <see cref="CoreShare"/> of the opening's
/// width and height) further than its two parts did. The result is cached per model; the level pass scales and turns it like the fitted boxes.
/// </summary>
public static class AshfallOpeningMerge
{
    /// <summary>Boxes a model is merged down to (the slab merge's six where nothing is in the way).</summary>
    public const int MaxBoxes = 6;

    /// <summary>Share of an opening's width and height (each side of its centre ray) that stays free of merged boxes.</summary>
    public const float CoreShare = 0.3f;

    static readonly ConcurrentDictionary<string, IReadOnlyList<(Vector3 Min, Vector3 Max)>> Cache = new();

    /// <summary>The model's boxes after the opening-aware merge (raw frame).</summary>
    public static IReadOnlyList<(Vector3 Min, Vector3 Max)> Reduced(string id) => Cache.GetOrAdd(id, Build);

    static IReadOnlyList<(Vector3 Min, Vector3 Max)> Build(string id)
    {
        var boxes = AshfallKitCollision.Boxes(id).ToList();
        var cores = AshfallKitCollision.Openings(id).Select(Core).ToList();
        while (boxes.Count > MaxBoxes)
        {
            int bi = -1, bj = -1; float best = float.MaxValue;
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    var u = (Vector3.Min(boxes[i].Min, boxes[j].Min), Vector3.Max(boxes[i].Max, boxes[j].Max));
                    float waste = Vol(u) - Vol(boxes[i]) - Vol(boxes[j]);
                    if (waste >= best || !Allowed(u, boxes[i], boxes[j], cores)) continue;
                    best = waste; bi = i; bj = j;
                }
            if (bi < 0) break;
            boxes[bi] = (Vector3.Min(boxes[bi].Min, boxes[bj].Min), Vector3.Max(boxes[bi].Max, boxes[bj].Max));
            boxes.RemoveAt(bj);
        }
        return boxes;
    }

    /// <summary>The part of an opening that must stay free: along its axis the whole tunnel, across it and up <see cref="CoreShare"/> of the opening's width and height around the centre ray.</summary>
    static (Vector3 Min, Vector3 Max) Core(AshfallKitOpening o)
    {
        float across = o.Width * CoreShare, up = o.Height * CoreShare;
        return o.Axis == 0
            ? (new Vector3(o.DepthMin, o.Centre.Y - up, o.Centre.Z - across), new Vector3(o.DepthMax, o.Centre.Y + up, o.Centre.Z + across))
            : (new Vector3(o.Centre.X - across, o.Centre.Y - up, o.DepthMin), new Vector3(o.Centre.X + across, o.Centre.Y + up, o.DepthMax));
    }

    static float Vol((Vector3 Min, Vector3 Max) b) { var e = b.Max - b.Min; return e.X * e.Y * e.Z; }

    static float Overlap((Vector3 Min, Vector3 Max) a, (Vector3 Min, Vector3 Max) b)
    {
        var e = Vector3.Min(a.Max, b.Max) - Vector3.Max(a.Min, b.Min);
        return e.X <= 0f || e.Y <= 0f || e.Z <= 0f ? 0f : e.X * e.Y * e.Z;
    }

    static bool Allowed((Vector3 Min, Vector3 Max) union, (Vector3 Min, Vector3 Max) a, (Vector3 Min, Vector3 Max) b, List<(Vector3 Min, Vector3 Max)> cores)
    {
        foreach (var c in cores)
        {
            float u = Overlap(union, c);
            if (u > 1e-7f && u > Overlap(a, c) + Overlap(b, c) + 1e-7f) return false;
        }
        return true;
    }
}
