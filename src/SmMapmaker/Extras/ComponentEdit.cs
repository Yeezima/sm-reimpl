using System.Numerics;

namespace SmMapmaker;

/// <summary>Which of a transform's three vectors a spin box edits.</summary>
public enum TransformProp { Position, Rotation, Scale }

/// <summary>
/// Single-component transform edits for the dock's Object and Prefab spin boxes (owner 2026-11-19: moving X must leave Y, Z, rotation and scale
/// 100 % static). Every edit starts from the stored transform, replaces exactly one component (one axis of one property) and copies everything else
/// bit for bit. Pure maths; the dock only feeds it the typed value.
/// </summary>
public static class ComponentEdit
{
    public static float Get(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    public static Vector3 WithAxis(Vector3 v, int axis, float value) => axis switch { 0 => v with { X = value }, 1 => v with { Y = value }, _ => v with { Z = value } };

    public static Vector3 Of(MapTransform t, TransformProp p) => p switch { TransformProp.Position => t.Position, TransformProp.Rotation => t.Rotation, _ => t.Scale };

    public static MapTransform With(MapTransform t, TransformProp p, Vector3 v) => p switch
    {
        TransformProp.Position => t with { Position = v },
        TransformProp.Rotation => t with { Rotation = v },
        _ => t with { Scale = v },
    };

    /// <summary>Local space: the one component replaced, all others identical.</summary>
    public static MapTransform Local(MapTransform current, TransformProp p, int axis, float value) =>
        With(current, p, WithAxis(Of(current, p), axis, value));

    /// <summary>Tolerance (degrees, metres, scale) under which a recomputed axis counts as unchanged and the stored value is kept.</summary>
    public const float KeepTolerance = 1e-4f;

    /// <summary>Scale ratios are meaningless below this first-object component; <see cref="Delta"/> sets the value absolutely instead.</summary>
    public const float MinRatioBase = 1e-3f;

    /// <summary>True when the parent matrix is only a translation and a positive axis-aligned scale; only a rotated parent can couple the axes.</summary>
    public static bool IsUnrotated(Matrix4x4 m, out Vector3 translation, out Vector3 scale)
    {
        translation = new Vector3(m.M41, m.M42, m.M43);
        scale = new Vector3(m.M11, m.M22, m.M33);
        const float e = 1e-6f;
        return MathF.Abs(m.M12) < e && MathF.Abs(m.M13) < e && MathF.Abs(m.M21) < e && MathF.Abs(m.M23) < e && MathF.Abs(m.M31) < e && MathF.Abs(m.M32) < e
            && scale.X > 0 && scale.Y > 0 && scale.Z > 0;
    }

    static bool Near(float a, float b, bool angle)
    {
        float d = a - b;
        if (angle) d = ((d + 180f) % 360f + 360f) % 360f - 180f;
        return MathF.Abs(d) <= KeepTolerance;
    }

    /// <summary>Axes other than <paramref name="axis"/> that came out within <see cref="KeepTolerance"/> of the stored value take the stored value back.</summary>
    static Vector3 KeepStored(Vector3 stored, Vector3 recomputed, int axis, bool angle)
    {
        var r = recomputed;
        for (int a = 0; a < 3; a++)
            if (a != axis && Near(Get(stored, a), Get(recomputed, a), angle)) r = WithAxis(r, a, Get(stored, a));
        return r;
    }

    /// <summary>
    /// World space: <paramref name="value"/> is the new world component of <paramref name="current"/> (stored under <paramref name="parentWorld"/>, null for a root).
    /// Under an unrotated parent (translation and axis-aligned scale) only the edited local axis is computed and every other value is the stored one, bit for bit:
    /// position (world - parent position) / parent scale, rotation = the world angle, scale = world / parent scale. Only a rotated parent couples the axes: then
    /// the edited property is recovered from the matrix (rounded to <see cref="TransformMath.Decimals"/>), any other axis that comes out within
    /// <see cref="KeepTolerance"/> of its stored value keeps it exactly (no Euler flips, no extra decimals), and the other two properties stay as stored.
    /// A turned parent with non-uniform scale can still refuse with <c>bad-transform</c>.
    /// </summary>
    public static MapTransform World(MapTransform current, Matrix4x4? parentWorld, TransformProp p, int axis, float value)
    {
        if (parentWorld is not { } pw) return Local(current, p, axis, value);
        if (IsUnrotated(pw, out var pos, out var scale))
        {
            float local = p switch
            {
                TransformProp.Position => (value - Get(pos, axis)) / Get(scale, axis),
                TransformProp.Rotation => value,
                _ => value / Get(scale, axis),
            };
            return Local(current, p, axis, local);
        }
        var world = TransformMath.Decompose(current.ToMatrix() * pw);
        var edited = WithAxis(Of(world, p), axis, value);
        if (!Matrix4x4.Invert(pw, out var inv)) throw new MapException("bad-transform", "The parent's matrix cannot be inverted.");
        var stored = Of(current, p);
        if (p == TransformProp.Position)
            return current with { Position = KeepStored(stored, Vector3.Transform(edited, inv), axis, false) };
        var recovered = Of(TransformMath.Decompose(With(world, p, edited).ToMatrix() * inv), p);
        return With(current, p, KeepStored(stored, recovered, axis, p == TransformProp.Rotation));
    }

    /// <summary>True when a multi-selection scale edit cannot use a ratio because the first object's component is (near) zero.</summary>
    public static bool RatioUnusable(TransformProp p, float firstNow) => p == TransformProp.Scale && MathF.Abs(firstNow) < MinRatioBase;

    /// <summary>
    /// Multi-selection: the edited component of each object moves by the same delta (position, rotation) or ratio (scale) as the first object's
    /// component did; everything else on every object is copied unchanged. <paramref name="firstNow"/> is the first object's component before the edit.
    /// When the first object's scale component is below <see cref="MinRatioBase"/> there is no sensible ratio: the typed value is set absolutely on each object.
    /// </summary>
    public static MapTransform Delta(MapTransform current, TransformProp p, int axis, float firstNow, float value)
    {
        float cur = Get(Of(current, p), axis);
        float next = p == TransformProp.Scale ? (RatioUnusable(p, firstNow) ? value : cur * (value / firstNow)) : cur + (value - firstNow);
        return Local(current, p, axis, next);
    }

    /// <summary>Pivot shift: an axis the user touched takes the typed value; an untouched axis stays at the baseline (no shift).</summary>
    public static Vector3 Pivot(Vector3 baseline, bool[] touched, Vector3 typed) =>
        new(touched[0] ? typed.X : baseline.X, touched[1] ? typed.Y : baseline.Y, touched[2] ? typed.Z : baseline.Z);
}
