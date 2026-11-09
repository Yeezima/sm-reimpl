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

    /// <summary>
    /// A kit model's ramp wedge (draft 5: a <see cref="AshfallKind.Stair"/> part with a sloped face, laid over a stair run so walkers climb it smoothly). Movement only (the window pass, 2026-11-06):
    /// shots meet the steps' own boxes, and a wedge's flat bottom would fill the arch under a stair flight. The v5 graybox's stair blocks are boxes and stay in both meshes.
    /// </summary>
    public bool MovementOnly => Kind == AshfallKind.Stair && Sloped(Corners);

    static bool Sloped(Vector3[] c)
    {
        for (int k = 0; k + 2 < c.Length; k += 3)
        {
            var n = Vector3.Cross(c[k + 1] - c[k], c[k + 2] - c[k]);
            float len = n.Length();
            if (len < 1e-9f) continue;
            n /= len;
            float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
            if (MathF.Max(ax, MathF.Max(ay, az)) < 0.9999f) return true;
        }
        return false;
    }
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

    /// <summary>The weapon mesh's corners: <see cref="SolidCorners"/> without the movement-only ramp wedges (<see cref="AshfallPart.MovementOnly"/>; the window pass).</summary>
    public Vector3[] WeaponCorners() => Parts.Where(p => p.Solid && !p.MovementOnly).SelectMany(p => p.Corners).ToArray();
}

/// <summary>The generator's entry points. Contract shared by the simulation, content, server and client work of G1; <see cref="Build"/> is deterministic.</summary>
public static partial class AshfallMap
{
    /// <summary>Raised with any change of the generated geometry, points, areas or spawns. 17: draft 10 (lanes to every point and between them, a 3 m clear zone at every stair's foot, narrow and blocked stairs made walkable); 16: draft 9, late batch F (stairs from the meshes, walked; clear stair feet and landings; no stacked models; the urban rules); 15: the openings pass (rubble, cover and wall pieces with through-openings keep their fitted boxes instead of six slabs); 14: draft 8 with the window pass (no objects in the roadways, collision boxes carved out of every window opening); 13: either alone; 12: draft 7 (no overlapping objects); 9: draft 5 (one asset per structure, collision from the assets); 10: draft 6 (every object 1.5x, more shooting platforms, bridges only on supports); 11: draft 6 with the Meshy expansion placed; <see cref="GrayboxVersion"/> is the v5 graybox it starts from.</summary>
    public const uint Version = 17;

    /// <summary>The graybox generator's version (<see cref="BuildGraybox"/>, hash <c>0xAE096E323BB5A4D3</c>, pinned).</summary>
    public const uint GrayboxVersion = 5;

    /// <summary>The seed of the repeated surfaces' variation (road and plaza tiles here; the client's ground and boundary textures use it too). Fixed and configurable by editing this constant (ours).</summary>
    public const uint VariationSeed = 1;

    /// <summary>The playable square's half side (metres): x and y in [-<see cref="Half"/>, <see cref="Half"/>].</summary>
    public const float Half = 240f;
}
