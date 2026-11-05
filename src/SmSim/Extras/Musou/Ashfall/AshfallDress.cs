using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

public static partial class AshfallMap
{
    /// <summary>The dressing pass's version (raised with any change of the items). 7: draft 5, one asset per graybox structure with collision from the assets (docs/musou-ashfall-graybox.md "Draft 5"); 5 was draft 3's scaled replacements, 1..4 drafts 1 and 2.</summary>
    public const uint DressVersion = 7;

    static readonly Lazy<AshfallDraft5> Draft5Cache = new(AshfallDraft5Pass.Run, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The draft-5 result in full: level, dressing, the v5 graybox it started from and the placement log (built once, shared; treat as read-only).</summary>
    public static AshfallDraft5 BuildDraft5() => Draft5Cache.Value;

    /// <summary>A second, uncached run of the pass (for the determinism checks).</summary>
    public static AshfallDraft5 RebuildDraft5() => AshfallDraft5Pass.Run();

    /// <summary>
    /// The level (deterministic): Ashfall draft 5, <see cref="Version"/> 9 (docs/musou-ashfall-graybox.md "Draft 5"). The v5 graybox's ground kinds and boundary plus the collision boxes of the one asset that replaces each graybox structure,
    /// transformed with the placement. Every place and size is ours (placeholder demonstration). The same parts, in the same order, with the same bits, on every machine.
    /// </summary>
    public static AshfallLevel Build() => BuildDraft5().Level;

    /// <summary>The draft-5 level together with the features of the v5 graybox it started from (the places v5 had, for the reports; draft 5 keeps only what its assets allow).</summary>
    public static AshfallBuild BuildWithFeatures() => new(BuildDraft5().Level, BuildDraft5().Graybox.Features);

    /// <summary>The draft-5 level with its dressing: one <see cref="AshfallDressRole.Prop"/> per placed asset (its collision parts in <see cref="AshfallDressing.ItemParts"/>) and the <see cref="AshfallDressRole.Surface"/> tiles.</summary>
    public static (AshfallLevel Level, AshfallDressing Dressing) BuildWithDressing() => (BuildDraft5().Level, BuildDraft5().Dressing);
}

/// <summary>Namespace holder of the surface tiles pass (see <c>AshfallDress.Surfaces.cs</c>); the structures are placed by <c>AshfallDraft5Pass</c>.</summary>
internal static partial class AshfallDressPass
{
    static float Yaw(int step) => (((step % 24) + 24) % 24) * (MathF.PI / 12f);
}

/// <summary>A graybox part read as a box on the ground plane: centre, bottom and top, <see cref="Length"/> along the local X (the longer horizontal side) and <see cref="Depth"/> along the local Y, turned by <see cref="YawStep"/> x 15 degrees.</summary>
public readonly record struct AshfallBox(float Cx, float Cy, float Z0, float Z1, float Length, float Depth, int YawStep)
{
    public float Height => Z1 - Z0;

    /// <summary>The yaw in radians (a step of the 15 degree table times a constant: the same bits on every machine).</summary>
    public float Yaw => YawStep * (MathF.PI / 12f);

    public Vector2 AxisX { get { var (s, c) = AshfallBuilder.Trig(YawStep); return new Vector2(c, s); } }

    public Vector2 AxisY { get { var (s, c) = AshfallBuilder.Trig(YawStep); return new Vector2(-s, c); } }

    /// <summary>Reads a part as a box (null for anything else: more or fewer than 12 triangles, a pitched box, a convex roof, a heightfield, a floor disc).</summary>
    public static AshfallBox? Of(AshfallPart part)
    {
        var c = part.Corners;
        if (c.Length != 36 || part.Kind == AshfallKind.Objective) return null;
        float z0 = float.MaxValue, z1 = float.MinValue;
        foreach (var v in c) { z0 = MathF.Min(z0, v.Z); z1 = MathF.Max(z1, v.Z); }
        if (z1 - z0 < 1e-3f) return null;
        foreach (var v in c) if (MathF.Abs(v.Z - z0) > 1e-4f && MathF.Abs(v.Z - z1) > 1e-4f) return null;
        for (int k = 0; k < 12; k++)
        {
            var (s, co) = AshfallBuilder.Trig(k);
            float u0 = float.MaxValue, u1 = float.MinValue, w0 = float.MaxValue, w1 = float.MinValue;
            foreach (var v in c)
            {
                float u = v.X * co + v.Y * s, w = -v.X * s + v.Y * co;
                u0 = MathF.Min(u0, u); u1 = MathF.Max(u1, u); w0 = MathF.Min(w0, w); w1 = MathF.Max(w1, w);
            }
            bool fits = true;
            foreach (var v in c)
            {
                float u = v.X * co + v.Y * s, w = -v.X * s + v.Y * co;
                if ((MathF.Abs(u - u0) > 2e-3f && MathF.Abs(u - u1) > 2e-3f) || (MathF.Abs(w - w0) > 2e-3f && MathF.Abs(w - w1) > 2e-3f)) { fits = false; break; }
            }
            if (!fits) continue;
            float cu = (u0 + u1) / 2f, cw = (w0 + w1) / 2f, lu = u1 - u0, lw = w1 - w0;
            float cx = cu * co - cw * s, cy = cu * s + cw * co;
            return lw > lu * 1.001f ? new AshfallBox(cx, cy, z0, z1, lw, lu, (k + 6) % 24) : new AshfallBox(cx, cy, z0, z1, lu, lw, k);
        }
        return null;
    }

    /// <summary>Whether the item's box lies inside this box grown by <paramref name="grow"/> metres on every side.</summary>
    public bool Holds(AshfallDressItem item, float grow)
    {
        var ax = AxisX; var ay = AxisY;
        float sy = MathF.Sin(item.Yaw), cy = MathF.Cos(item.Yaw);
        var ix = new Vector2(cy, sy); var iy = new Vector2(-sy, cy);
        const float eps = 2e-3f;
        if (item.Position.Z < Z0 - grow - eps || item.Position.Z + item.Size.Z > Z1 + grow + eps) return false;
        foreach (float a in new[] { -0.5f, 0.5f })
            foreach (float b in new[] { -0.5f, 0.5f })
            {
                var p = new Vector2(item.Position.X, item.Position.Y) + ix * (a * item.Size.X) + iy * (b * item.Size.Y) - new Vector2(Cx, Cy);
                if (MathF.Abs(Vector2.Dot(p, ax)) > Length / 2f + grow + eps || MathF.Abs(Vector2.Dot(p, ay)) > Depth / 2f + grow + eps) return false;
            }
        return true;
    }
}
