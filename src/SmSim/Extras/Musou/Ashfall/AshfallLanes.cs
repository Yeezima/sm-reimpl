using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Draft 10's lanes (owner, 2026-11-09: "make the lanes to each point from the stronghold, and between each point. there should be clear pathways between them all."; ours, placeholder demonstration;
/// docs/musou-ashfall-graybox.md "Draft 10 lanes and stairs"). The avenues (24 m) and the perimeter road (16 m) already run between the blocks and stay clear (draft 8); the capture points A and C stand inside
/// their blocks and the plaza (B) and the stronghold's gate stopped short of the avenues. Each lane here is a paved connector <see cref="Width"/> wide (the paved road's width) from a block's edge to a point's ring or to
/// the gate, laid as a <see cref="AshfallKind.Road"/> part: it is drawn as road (the road tiles) and it is a roadway, so the draft-8 rule keeps every object's collision out of it plus its margin.
/// <see cref="Routes"/> are the pathways the lanes and the avenues make between the stronghold, the Imperial base and every point (walked by the checks).
/// </summary>
public static class AshfallLanes
{
    /// <summary>A connector's width (metres; the paved road from the compound's gate to the plaza is 12 m; ours).</summary>
    public const float Width = 12f;

    /// <summary>The connectors: name and plan rectangle (x0, y0, x1, y1), each inside one block (its surface at <see cref="AshfallMap.BlockTop"/>).</summary>
    public static readonly (string Name, Vector4 Rect)[] Connectors =
    [
        ("stronghold gate", new(-6f, 104f, 6f, 110f)),   // the north avenue to the gatehouse's foot
        ("plaza north", new(-6f, 60f, 6f, 80f)),         // the plaza's edge to the north avenue (the south one is the paved road)
        ("plaza west", new(-80f, -6f, -60f, 6f)),
        ("plaza east", new(60f, -6f, 80f, 6f)),
        ("A south", new(-170f, 104f, -158f, 150f)),      // the north avenue to A's ring (A: (-164, 164), 14 m)
        ("A east", new(-150f, 158f, -104f, 170f)),       // A's ring to the west avenue
        ("C south", new(158f, 104f, 170f, 136f)),        // the north avenue to C's ring (C: (164, 150), 14 m)
        ("C west", new(104f, 144f, 150f, 156f)),         // the east avenue to C's ring
    ];

    /// <summary>The connectors as paved road parts (a flush overlay 2 cm over the block, like the paved road).</summary>
    internal static List<AshfallPart> Parts()
    {
        var b = new AshfallBuilder();
        foreach (var (_, r) in Connectors) b.Box(AshfallKind.Road, r.X, r.Y, AshfallMap.BlockTop, r.Z, r.W, AshfallMap.BlockTop + 0.02f);
        return b.Parts;
    }

    /// <summary>Whether a plan point lies on a connector, grown by <paramref name="grow"/>.</summary>
    public static bool On(Vector2 p, float grow = 0f)
    {
        foreach (var (_, r) in Connectors)
            if (p.X >= r.X - grow && p.X <= r.Z + grow && p.Y >= r.Y - grow && p.Y <= r.W + grow) return true;
        return false;
    }

    static readonly Vector2 Stronghold = new(0f, 138f), Base = new(0f, -188f), A = new(-164f, 164f), B = new(0f, 0f), C = new(164f, 150f);

    /// <summary>
    /// The pathways (plan waypoints along lane and avenue centrelines): from the stronghold's courtyard (through its gate) and from the Imperial base (through the compound's gate) to every point, and between every pair of points.
    /// </summary>
    public static readonly (string Name, Vector2[] Path)[] Routes =
    [
        ("stronghold to A", [Stronghold, new(0f, 92f), new(-164f, 92f), A]),
        ("stronghold to B", [Stronghold, new(0f, 92f), B]),
        ("stronghold to C", [Stronghold, new(0f, 92f), new(164f, 92f), C]),
        ("A to B", [A, new(-92f, 164f), new(-92f, 0f), B]),
        ("B to C", [B, new(92f, 0f), new(92f, 150f), C]),
        ("A to C", [A, new(-164f, 92f), new(164f, 92f), C]),
        ("base to B", [Base, B]),
        ("base to A", [Base, new(0f, -92f), new(-92f, -92f), new(-92f, 164f), A]),
        ("base to C", [Base, new(0f, -92f), new(92f, -92f), new(92f, 150f), C]),
    ];
}
