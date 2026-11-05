using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Musou.Ashfall;

public static partial class AshfallMap
{
    /// <summary>The dressing pass's version (raised with any change of the items). 1: the first draft (docs/musou-ashfall-graybox.md, "Kit dressing (draft 1)").</summary>
    public const uint DressVersion = 1;

    /// <summary>
    /// The level with its kit dressing (deterministic; the same items in the same order on every machine). Server and client both call it, so the Prop items'
    /// collision proxies are part of the level and its hash (<see cref="Build"/> returns the same level).
    /// </summary>
    public static (AshfallLevel Level, AshfallDressing Dressing) BuildWithDressing()
    {
        var built = BuildCore(true);
        return (built.Build.Level, built.Dressing);
    }
}

/// <summary>
/// The kit dressing pass (ours, placeholder demonstration; docs/musou-ashfall-graybox.md "Kit dressing (draft 1)"): skins over the graybox's boxes, road and plaza tiles, walk-over scatter and
/// props with their own collision proxies. Deterministic: integer hashes (<see cref="AshfallBuilder.Mix"/>), total-order sorts, no random numbers. Files: this one (entry and the box reader),
/// <c>AshfallDress.Skins.cs</c>, <c>AshfallDress.Surfaces.cs</c>, <c>AshfallDress.Props.cs</c>.
/// </summary>
internal static partial class AshfallDressPass
{
    /// <summary>How far an item may stick out of its part's box (metres): what you see is what you hit.</summary>
    public const float SkinGrow = 0.15f;

    /// <summary>The tallest a walk-over Decoration item may be after fitting (metres).</summary>
    public const float DecorationMaxHeight = 0.35f;

    static float Yaw(int step) => (((step % 24) + 24) % 24) * (MathF.PI / 12f);

    /// <summary>Runs the pass: adds the props' proxies to <paramref name="b"/> (always) and builds the items (only when <paramref name="withItems"/>).</summary>
    public static AshfallDressing Run(AshfallBuilder b, ControlPoint[] points, StartArea[] areas, bool withItems)
    {
        int grayCount = b.Parts.Count;
        var placer = new AshfallPlacer(b);
        AshfallMap.Bans(placer, b, points);
        // The stronghold block (ring, keep, towers, outerworks) is no place for props: nothing may become a foothold on the ring or narrow the gate approach.
        placer.Ban(-84f, 96f, 84f, 224f);
        var props = Props(b, placer);
        if (!withItems) return new AshfallDressing([], [], AshfallMap.DressVersion);
        var items = new List<AshfallDressItem>();
        var hidden = new List<int>();
        Skins(b.Parts, grayCount, items, hidden);
        Surfaces(b.Parts, grayCount, items);
        Decoration(placer, areas, items);
        items.AddRange(props);
        hidden.Sort();
        return new AshfallDressing(items, hidden, AshfallMap.DressVersion);
    }
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
