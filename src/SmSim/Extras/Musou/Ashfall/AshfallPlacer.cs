using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// The rule-driven placer of pass 2 (docs/musou-ashfall-graybox.md): the claims of everything already built, the banned rectangles and discs (roads' lanes, start areas, capture zones and their 6 m ring,
/// the plaza's open centre, spawn markers) and the checks a candidate must pass (distance to every claim, banned places, flat ground). Deterministic: no random numbers.
/// </summary>
internal sealed class AshfallPlacer
{
    public readonly AshfallBuilder B;
    public readonly List<Vector4> Claims = [];
    readonly List<Vector4> _banned = [];
    readonly List<Vector3> _discs = [];
    public readonly List<AshfallPlatform> Platforms = [];
    public readonly List<AshfallTower> Towers = [];
    public readonly List<AshfallRuin> Ruins = [];
    public int WallSegments, StaggerWalls;
    public float WallLength;

    static bool Structural(AshfallKind k) => k is AshfallKind.Wall or AshfallKind.Cover or AshfallKind.Rubble or AshfallKind.Floor or AshfallKind.Stair or AshfallKind.Perch or AshfallKind.Stronghold;

    public AshfallPlacer(AshfallBuilder b)
    {
        B = b;
        foreach (var part in b.Parts)
        {
            if (!Structural(part.Kind)) continue;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var c in part.Corners) { x0 = MathF.Min(x0, c.X); y0 = MathF.Min(y0, c.Y); x1 = MathF.Max(x1, c.X); y1 = MathF.Max(y1, c.Y); }
            Claims.Add(new Vector4(x0, y0, x1, y1));
        }
    }

    public void Ban(float x0, float y0, float x1, float y1) => _banned.Add(new Vector4(x0, y0, x1, y1));

    public void BanDisc(float x, float y, float r) => _discs.Add(new Vector3(x, y, r));

    public static float Gap(Vector4 a, Vector4 b)
    {
        float dx = MathF.Max(MathF.Max(a.X - b.Z, b.X - a.Z), 0f), dy = MathF.Max(MathF.Max(a.Y - b.W, b.Y - a.W), 0f);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Whether <paramref name="r"/> stays out of every banned place and at least <paramref name="gap"/> m from every claim.</summary>
    public bool Clear(Vector4 r, float gap)
    {
        foreach (var z in _banned)
            if (r.X < z.Z && r.Z > z.X && r.Y < z.W && r.W > z.Y) return false;
        foreach (var d in _discs)
        {
            float dx = MathF.Max(MathF.Max(r.X - d.X, d.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - d.Y, d.Y - r.W), 0f);
            if (dx * dx + dy * dy < d.Z * d.Z) return false;
        }
        foreach (var c in Claims)
            if (Gap(r, c) < gap) return false;
        return true;
    }

    /// <summary>Why <see cref="Clear"/> says no (for the hand-placed buildings' error message).</summary>
    public string Why(Vector4 r, float gap)
    {
        for (int i = 0; i < _banned.Count; i++) { var z = _banned[i]; if (r.X < z.Z && r.Z > z.X && r.Y < z.W && r.W > z.Y) return "banned rectangle " + z; }
        foreach (var d in _discs)
        {
            float dx = MathF.Max(MathF.Max(r.X - d.X, d.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - d.Y, d.Y - r.W), 0f);
            if (dx * dx + dy * dy < d.Z * d.Z) return "disc " + d;
        }
        foreach (var c in Claims) if (Gap(r, c) < gap) return "claim " + c;
        return Flat(r) ? "nothing" : "not flat";
    }

    /// <summary>Whether the ground under <paramref name="r"/> (and 2 m around) is level: a road (0) or a block's flat top; no crater, no rim.</summary>
    public static bool Flat(Vector4 r)
    {
        for (float x = r.X - 2f; x <= r.Z + 2.001f; x += 1f)
            for (float y = r.Y - 2f; y <= r.W + 2.001f; y += 1f)
            {
                float s = AshfallMap.SurfaceAt(x, y);
                if (s != 0f && s != AshfallMap.BlockTop) return false;
            }
        return true;
    }

    public static bool Inside(Vector4 r, float x0, float y0, float x1, float y1) => r.X >= x0 && r.Y >= y0 && r.Z <= x1 && r.W <= y1;
}
