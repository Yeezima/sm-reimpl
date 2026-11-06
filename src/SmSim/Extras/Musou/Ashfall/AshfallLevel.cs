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
    /// <summary>Raised with any change of the generated geometry, points, areas or spawns. 13: draft 8 (no objects in the roadways); 12: draft 7 (no overlapping objects); 9: draft 5 (one asset per structure, collision from the assets); 10: draft 6 (every object 1.5x, more shooting platforms, bridges only on supports); 11: draft 6 with the Meshy expansion placed; <see cref="GrayboxVersion"/> is the v5 graybox it starts from.</summary>
    public const uint Version = 13;

    /// <summary>The graybox generator's version (<see cref="BuildGraybox"/>, hash <c>0xAE096E323BB5A4D3</c>, pinned).</summary>
    public const uint GrayboxVersion = 5;

    /// <summary>The seed of the repeated surfaces' variation (road and plaza tiles here; the client's ground and boundary textures use it too). Fixed and configurable by editing this constant (ours).</summary>
    public const uint VariationSeed = 1;

    /// <summary>The playable square's half side (metres): x and y in [-<see cref="Half"/>, <see cref="Half"/>].</summary>
    public const float Half = 240f;
}
