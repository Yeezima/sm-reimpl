using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>What a part of the graybox is, for drawing and checks (docs/musou-ashfall-graybox.md). Objective parts are floor markers, never solid.</summary>
public enum AshfallKind : byte
{
    Ground, Road, Paving, Curb, Wall, Cover, Rubble, Floor, Stair, Perch, Objective, Stronghold, Boundary, Backdrop,
}

/// <summary>One part of the level: its kind and its triangles (three corners a triangle, outward winding, simulation frame: X east, Y north, Z up, metres).</summary>
public sealed record AshfallPart(AshfallKind Kind, Vector3[] Corners)
{
    /// <summary>Whether the part collides (every kind but <see cref="AshfallKind.Objective"/>).</summary>
    public bool Solid => Kind != AshfallKind.Objective;
}

/// <summary>
/// Ashfall Citadel, Musou Mode's generated battlefield (placeholder demonstration, primitive graybox; every place and size is ours): its parts, the three capture points
/// A, B, C in that order, a start area for each side (Space Marines: the spawn compound; Chaos: the stronghold's courtyard, where the commander stands), the spawn markers
/// that seed the enemies' graph, and the hash of the whole (FNV over the solid corners, salted with <see cref="AshfallMap.Version"/>).
/// </summary>
public sealed record AshfallLevel(IReadOnlyList<AshfallPart> Parts, IReadOnlyList<ControlPoint> Points, IReadOnlyList<StartArea> Areas, IReadOnlyList<Vector3> Spawns, ulong Hash)
{
    /// <summary>The solid triangles' corners, three a triangle, in part order: the character and the weapon mesh of the level.</summary>
    public Vector3[] SolidCorners() => Parts.Where(p => p.Solid).SelectMany(p => p.Corners).ToArray();
}

/// <summary>The generator's entry points. Contract shared by the simulation, content, server and client work of G1; <see cref="Build"/> is deterministic.</summary>
public static partial class AshfallMap
{
    /// <summary>Raised with any change of the generated geometry, points, areas or spawns.</summary>
    public const uint Version = 5;

    /// <summary>The playable square's half side (metres): x and y in [-<see cref="Half"/>, <see cref="Half"/>].</summary>
    public const float Half = 240f;
}
