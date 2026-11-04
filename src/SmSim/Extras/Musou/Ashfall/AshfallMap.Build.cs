using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

public static partial class AshfallMap
{
    /// <summary>
    /// The level (deterministic). STUB for the parallel G1 work: a flat 480 m slab with the three points, both start areas and a few spawns at the
    /// design's places; the generator developer replaces this body with the full layout of docs/musou-ashfall-graybox.md.
    /// </summary>
    public static AshfallLevel Build()
    {
        var slab = new AshfallPart(AshfallKind.Ground, Box(new Vector3(-Half, -Half, -2f), new Vector3(Half, Half, 0f)));
        ControlPoint[] points = [new("A", new(-164f, 164f, 0f), 14f, 8f), new("B", Vector3.Zero, 16f, 8f), new("C", new(164f, 150f, 0f), 14f, 8f)];
        StartArea[] areas = [new(Team.SpaceMarines, new(0f, -188f, 0f), new(64f, 48f, 8f), 0f), new(Team.Chaos, new(0f, 138f, 0f), new(40f, 36f, 8f), 0f)];
        Vector3[] spawns = [new(0f, -188f, 0f), new(0f, 138f, 0f), .. points.Select(p => p.Center)];
        return new AshfallLevel([slab], points, areas, spawns, Geometry.Fnv(Geometry.FnvOffset, Version));
    }

    /// <summary>The 12 outward triangles of an axis-aligned box (36 corners).</summary>
    internal static Vector3[] Box(Vector3 min, Vector3 max) => Campaign.TfwMap.Triangles(new Campaign.TfwSolid(min, max));
}
