using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>A shooting platform of pass 2 (docs/musou-ashfall-graybox.md): <see cref="Top"/> is the middle of its walkable top.</summary>
public sealed record AshfallPlatform(Vector3 Top, float Height, int Ramps, bool Parapet);

/// <summary>A tall building of pass 2: footprint (min x, min y, max x, max y), storeys, whether a stair core reaches every floor, and a point on each upper floor (6 m steps, bottom first).</summary>
public sealed record AshfallTower(string Name, Vector4 Footprint, int Storeys, bool Core, IReadOnlyList<Vector3> Floors);

/// <summary>A ruin of pass 2: its template, footprint, storeys, and a point on its walkable first floor (null when the floor is jump-only or absent).</summary>
public sealed record AshfallRuin(string Kind, Vector4 Footprint, int Storeys, Vector3? FloorPoint);

/// <summary>What pass 2 placed, for the checks (never part of the wire contract).</summary>
public sealed record AshfallFeatures(IReadOnlyList<AshfallPlatform> Platforms, IReadOnlyList<AshfallTower> Towers, IReadOnlyList<AshfallRuin> Ruins, int WallSegments, float WallLength, int StaggerWalls);

/// <summary>The level together with its features.</summary>
public sealed record AshfallBuild(AshfallLevel Level, AshfallFeatures Features);

/// <summary>A structure's local frame: (u, v) on the 4 m grid turned by <c>rot</c> quarter turns about the origin (ox, oy) (exact in floats, so every machine builds the same bits).</summary>
internal readonly struct AshfallFrame
{
    readonly AshfallBuilder? _b;
    readonly float _ox, _oy;
    readonly int _rot;

    public AshfallFrame(AshfallBuilder? b, float ox, float oy, int rot) { _b = b; _ox = ox; _oy = oy; _rot = rot & 3; }

    public Vector2 Map(float u, float v) => _rot switch { 0 => new(_ox + u, _oy + v), 1 => new(_ox - v, _oy + u), 2 => new(_ox - u, _oy - v), _ => new(_ox + v, _oy - u) };

    public (int X, int Y) Dir(int du, int dv) => _rot switch { 0 => (du, dv), 1 => (-dv, du), 2 => (-du, -dv), _ => (dv, -du) };

    public Vector4 Rect(float u0, float v0, float u1, float v1)
    {
        var a = Map(u0, v0); var c = Map(u1, v1);
        return new(MathF.Min(a.X, c.X), MathF.Min(a.Y, c.Y), MathF.Max(a.X, c.X), MathF.Max(a.Y, c.Y));
    }

    public void Box(AshfallKind kind, float u0, float v0, float z0, float u1, float v1, float z1)
    {
        if (u1 - u0 < 1e-3f || v1 - v0 < 1e-3f || z1 - z0 < 1e-3f) return;   // an empty piece (a hole at the plate's edge)
        var r = Rect(u0, v0, u1, v1);
        _b!.Box(kind, r.X, r.Y, z0, r.Z, r.W, z1);
    }

    public void Stair(float su, float sv, int du, int dv, float width, float z0, float rise, int skip = 0)
    {
        var p = Map(su, sv);
        var (dx, dy) = Dir(du, dv);
        _b!.Stair(p.X, p.Y, dx, dy, width, z0, rise, skip);
    }

    /// <summary>The frame whose world rectangle of the local box (<paramref name="u0"/>, <paramref name="v0"/>)..(<paramref name="u1"/>, <paramref name="v1"/>) starts at (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    public static AshfallFrame At(AshfallBuilder b, int rot, float u0, float v0, float u1, float v1, float cx, float cy, out Vector4 world)
    {
        var r0 = new AshfallFrame(null, 0f, 0f, rot).Rect(u0, v0, u1, v1);
        world = new Vector4(cx, cy, cx + (r0.Z - r0.X), cy + (r0.W - r0.Y));
        return new AshfallFrame(b, cx - r0.X, cy - r0.Y, rot);
    }
}
