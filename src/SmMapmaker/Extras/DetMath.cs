using System.Numerics;
using SmContent;

namespace SmMapmaker;

/// <summary>
/// Placement maths that gives the same bits on every machine (docs/mapmaker.md "SM1 pieces in play"): the Linux server and the Windows clients must build bit-identical collision from the same
/// map, so nothing here calls the platform's libm (<c>MathF.Sin</c>, <c>Math.Cos</c>, <c>Math.Atan2</c> may differ in the last bit between operating systems) or a float matrix routine that may be fused.
/// Everything is <c>double</c> and uses only IEEE + - * / (and <c>Math.Floor</c>, <c>Math.Round</c> and <c>Math.Sqrt</c>, which IEEE defines exactly). Rotation angles are snapped to 1/1000 degree first.
/// The maths itself is <see cref="DetTransform"/> (SmContent, so the SM1 node transforms can use it); this adds the checks of the custom-map path. A 4 x 4 matrix is 16 doubles, row-major, for row vectors.
/// </summary>
public static class DetMath
{
    /// <summary>Rotation angles are rounded to this many steps per degree before they are used.</summary>
    public const double AngleSteps = DetTransform.AngleSteps;
    /// <summary>Largest magnitude of a position, rotation or scale component, and of a collision vertex, the maths accepts.</summary>
    public const double Limit = 1_000_000.0;

    public static double SnapDegrees(double degrees) => DetTransform.SnapDegrees(degrees);
    public static (double Sin, double Cos) SinCosDegrees(double degrees) => DetTransform.SinCosDegrees(degrees);
    public static double[] Identity() => DetTransform.Identity();
    public static double[] Mul(double[] a, double[] b) => DetTransform.Mul(a, b);

    public static double[] FromFloat(Matrix4x4 m) =>
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    /// <summary>The matrix as floats (for drawing). <exception cref="MapException"><c>bad-transform</c> when an entry does not fit a float (nested scales can reach that).</exception></summary>
    public static Matrix4x4 ToFloat(double[] m)
    {
        foreach (var v in m) if (!float.IsFinite((float)v)) throw new MapException("bad-transform", "A placement's nested transforms grow beyond what a float can hold.");
        return new((float)m[0], (float)m[1], (float)m[2], (float)m[3], (float)m[4], (float)m[5], (float)m[6], (float)m[7], (float)m[8], (float)m[9], (float)m[10], (float)m[11], (float)m[12], (float)m[13], (float)m[14], (float)m[15]);
    }

    /// <summary>Scale, then rotate about X, then Y, then Z (fixed axes, angles in degrees snapped to 1/1000), then translate: the same placement as <see cref="MapScene.Transform"/>.</summary>
    /// <exception cref="MapException"><c>bad-transform</c> when a component is not finite or beyond <see cref="Limit"/>.</exception>
    public static double[] Local(MapTransform t) =>
        DetTransform.Local(Check(t.Position.X), Check(t.Position.Y), Check(t.Position.Z), Check(t.Rotation.X), Check(t.Rotation.Y), Check(t.Rotation.Z), Check(t.Scale.X), Check(t.Scale.Y), Check(t.Scale.Z));

    static double Check(double v)
    {
        if (!double.IsFinite(v) || Math.Abs(v) > Limit) throw new MapException("bad-transform", $"A transform component ({v}) is not a finite number within ±{Limit:N0}.");
        return v;
    }

    /// <summary>The point moved by the matrix (p' = p M, w = 1), in double.</summary>
    public static (double X, double Y, double Z) Transform(double x, double y, double z, double[] m) =>
        (x * m[0] + y * m[4] + z * m[8] + m[12], x * m[1] + y * m[5] + z * m[9] + m[13], x * m[2] + y * m[6] + z * m[10] + m[14]);

    /// <summary>
    /// The point rounded to the 1/4096 m grid, as floats. The grid point is exact in a float only up to about 4096 m from the origin (24 bits of mantissa); beyond that the cast rounds, which is still
    /// deterministic (a cast of a double is IEEE). A result that is not finite or lies beyond <see cref="Limit"/> is refused.
    /// </summary>
    /// <exception cref="MapException"><c>bad-transform</c>.</exception>
    public static Vector3 Grid(double x, double y, double z)
    {
        const double g = DecorGeometry.Grid;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) || Math.Abs(x) > Limit || Math.Abs(y) > Limit || Math.Abs(z) > Limit)
            throw new MapException("bad-transform", "A placed piece lies beyond ±1,000,000 m (or its transform is not finite); the map is refused.");
        return new Vector3((float)(Math.Round(x * g, MidpointRounding.ToEven) / g), (float)(Math.Round(y * g, MidpointRounding.ToEven) / g), (float)(Math.Round(z * g, MidpointRounding.ToEven) / g));
    }

    /// <summary>A unit-cylinder ring point (angle <paramref name="step"/> of <paramref name="sides"/>), from the deterministic sin/cos.</summary>
    public static (double Cos, double Sin) Ring(int step, int sides)
    {
        var (s, c) = SinCosDegrees(step * 360.0 / sides);
        return (c, s);
    }
}
