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

    /// <summary>
    /// World space: <paramref name="value"/> is the new world component of <paramref name="current"/> (stored under <paramref name="parentWorld"/>, null for a root).
    /// A position edit moves only the position through the parent's inverse and keeps local rotation and scale exactly. A rotation or scale edit changes the
    /// world matrix, so that one property is recovered from the matrix (rounded to <see cref="TransformMath.Decimals"/>) while the other two stay exactly as stored;
    /// under a parent with non-uniform scale and a turn the matrix may not be representable, and then the edit throws <c>bad-transform</c>.
    /// </summary>
    public static MapTransform World(MapTransform current, Matrix4x4? parentWorld, TransformProp p, int axis, float value)
    {
        if (parentWorld is not { } pw) return Local(current, p, axis, value);
        var world = TransformMath.Decompose(current.ToMatrix() * pw);
        var edited = WithAxis(Of(world, p), axis, value);
        if (!Matrix4x4.Invert(pw, out var inv)) throw new MapException("bad-transform", "The parent's matrix cannot be inverted.");
        if (p == TransformProp.Position)
            return current with { Position = Vector3.Transform(edited, inv) };
        var local = TransformMath.Decompose(With(world, p, edited).ToMatrix() * inv);
        return With(current, p, Of(local, p));
    }

    /// <summary>
    /// Multi-selection: the edited component of each object moves by the same delta (position, rotation) or ratio (scale) as the first object's
    /// component did; everything else on every object is copied unchanged. <paramref name="firstNow"/> is the first object's component before the edit.
    /// </summary>
    public static MapTransform Delta(MapTransform current, TransformProp p, int axis, float firstNow, float value)
    {
        float cur = Get(Of(current, p), axis);
        float next = p == TransformProp.Scale ? (firstNow == 0 ? cur : cur * (value / firstNow)) : cur + (value - firstNow);
        return Local(current, p, axis, next);
    }
}
