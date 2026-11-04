using System.Numerics;
using SmContent;

namespace SmMapmaker;

/// <summary>Snapping is applied by the caller (the editor), so a typed value is never re-snapped.</summary>
public static class Snap
{
    /// <summary>The nearest multiple of <paramref name="step"/> (metres); a step of 0 or less returns the value.</summary>
    public static float Grid(float value, float step) => step > 0 ? MathF.Round(value / step, MidpointRounding.AwayFromZero) * step : value;

    public static Vector3 Grid(Vector3 value, float step) => new(Grid(value.X, step), Grid(value.Y, step), Grid(value.Z, step));

    /// <summary>The nearest multiple of <paramref name="stepDegrees"/>.</summary>
    public static float Angle(float degrees, float stepDegrees) => Grid(degrees, stepDegrees);

    public static Vector3 Angle(Vector3 degrees, float stepDegrees) => Grid(degrees, stepDegrees);
}

/// <summary>Matrix and component maths consistent with <see cref="MapScene.Transform"/>.</summary>
public static class TransformMath
{
    /// <summary>Decimals kept when components are recovered from a matrix (positions and scales in metres; the angles are in degrees).</summary>
    public const int Decimals = 5;

    /// <summary>
    /// Position, rotation (Euler degrees in <see cref="MapScene.Rotation"/> order) and scale of a matrix without shear, rounded to <see cref="Decimals"/>.
    /// Throws <c>bad-transform</c> when the matrix cannot be decomposed or its recomposition differs by more than 1e-3 (shear, zero scale).
    /// </summary>
    public static MapTransform Decompose(Matrix4x4 m)
    {
        var pos = new Vector3(m.M41, m.M42, m.M43);
        var scale = new Vector3(new Vector3(m.M11, m.M12, m.M13).Length(), new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length());
        if (m.GetDeterminant() < 0) scale.X = -scale.X;   // a mirrored matrix keeps its mirror on X
        // Euler angles for R = Rz(c)Ry(b)Rx(a) (MapScene.Rotation), read from the matrix's own rotation rows in double precision (better conditioned near +-90 than the quaternion).
        double sx = scale.X, sy = scale.Y, sz = scale.Z;
        if (sx == 0 || sy == 0 || sz == 0) throw new MapException("bad-transform", "The matrix has a zero scale.");
        double r11 = m.M11 / sx, r21 = m.M12 / sx, r31 = m.M13 / sx, r12 = m.M21 / sy, r22 = m.M22 / sy, r32 = m.M23 / sy, r33 = m.M33 / sz;
        double ex, ey, ez, cosB = Math.Sqrt(r11 * r11 + r21 * r21);
        if (cosB > 1e-6)
        {
            ey = Math.Atan2(-r31, cosB);
            ex = Math.Atan2(r32, r33);
            ez = Math.Atan2(r21, r11);
        }
        else
        {
            ey = r31 < 0 ? Math.PI / 2 : -Math.PI / 2;
            ex = 0;
            ez = Math.Atan2(-r12, r22);
        }
        const double deg = 180.0 / Math.PI;
        var result = new MapTransform(Round(pos), Round(new Vector3((float)(ex * deg), (float)(ey * deg), (float)(ez * deg))), Round(scale));
        if (result.Scale.X == 0 || result.Scale.Y == 0 || result.Scale.Z == 0) throw new MapException("bad-transform", "The matrix has a zero scale.");
        if (!Close(result.ToMatrix(), m, 1e-3f)) throw new MapException("bad-transform", "The matrix cannot be stored as position, rotation and scale (shear?).");
        return result;
    }

    public static bool Close(Matrix4x4 a, Matrix4x4 b, float tolerance) =>
        MathF.Abs(a.M11 - b.M11) <= tolerance && MathF.Abs(a.M12 - b.M12) <= tolerance && MathF.Abs(a.M13 - b.M13) <= tolerance && MathF.Abs(a.M14 - b.M14) <= tolerance
        && MathF.Abs(a.M21 - b.M21) <= tolerance && MathF.Abs(a.M22 - b.M22) <= tolerance && MathF.Abs(a.M23 - b.M23) <= tolerance && MathF.Abs(a.M24 - b.M24) <= tolerance
        && MathF.Abs(a.M31 - b.M31) <= tolerance && MathF.Abs(a.M32 - b.M32) <= tolerance && MathF.Abs(a.M33 - b.M33) <= tolerance && MathF.Abs(a.M34 - b.M34) <= tolerance
        && MathF.Abs(a.M41 - b.M41) <= tolerance && MathF.Abs(a.M42 - b.M42) <= tolerance && MathF.Abs(a.M43 - b.M43) <= tolerance && MathF.Abs(a.M44 - b.M44) <= tolerance;

    static float R(float v) => v == 0 ? 0 : (float)Math.Round((double)v, Decimals);
    public static Vector3 Round(Vector3 v) => new(R(v.X), R(v.Y), R(v.Z));
}
