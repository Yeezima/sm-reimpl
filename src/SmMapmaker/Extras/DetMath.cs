using System.Numerics;

namespace SmMapmaker;

/// <summary>
/// Placement maths that gives the same bits on every machine (docs/mapmaker.md "SM1 pieces in play"): the Linux server and the Windows clients must build bit-identical collision from the same
/// map, so nothing here calls the platform's libm (<c>MathF.Sin</c>, <c>Math.Cos</c> and friends may differ in the last bit between operating systems) or a float matrix routine that may be fused.
/// Everything is <c>double</c> and uses only IEEE + - * / (and <c>Math.Floor</c>, <c>Math.Round</c> and <c>Math.Sqrt</c>, which IEEE defines exactly). Rotation angles are snapped to 1/1000 degree first.
/// A 4 x 4 matrix is 16 doubles, row-major, for row vectors (p' = p M) like <see cref="Matrix4x4"/>.
/// </summary>
public static class DetMath
{
    /// <summary>Rotation angles are rounded to this many steps per degree before they are used.</summary>
    public const double AngleSteps = 1000.0;
    /// <summary>Largest magnitude of a position, rotation or scale component the decoration maths accepts.</summary>
    public const double Limit = 1_000_000.0;

    const double DegToRad = 0.017453292519943295769236907684886;   // pi / 180

    /// <summary>The snapped angle in degrees, reduced to [-180, 180).</summary>
    public static double SnapDegrees(double degrees)
    {
        double d = Math.Round(degrees * AngleSteps, MidpointRounding.ToEven) / AngleSteps;
        d -= 360.0 * Math.Floor((d + 180.0) / 360.0);
        return d;
    }

    /// <summary>Sine and cosine of an angle in degrees (snapped first), by argument reduction to a quarter turn and a Taylor series; within 1e-15 of the true values.</summary>
    public static (double Sin, double Cos) SinCosDegrees(double degrees)
    {
        double d = SnapDegrees(degrees);
        double q = Math.Round(d / 90.0, MidpointRounding.ToEven);
        double r = (d - 90.0 * q) * DegToRad;                      // |r| <= pi/4
        double s = SinSeries(r), c = CosSeries(r);
        switch (((long)q % 4 + 4) % 4)
        {
            case 0: return (s, c);
            case 1: return (c, -s);
            case 2: return (-s, -c);
            default: return (-c, s);
        }
    }

    static double SinSeries(double x)
    {
        double x2 = x * x;
        // x (1 - x^2/3! (1 - x^2/(4*5) ...)): coefficients 1/(2k+1)!, Horner from the highest term (|x| <= pi/4: the next term is below 1e-19)
        double[] inv = [1.0, 1.0 / 6.0, 1.0 / 120.0, 1.0 / 5040.0, 1.0 / 362880.0, 1.0 / 39916800.0, 1.0 / 6227020800.0, 1.0 / 1307674368000.0, 1.0 / 355687428096000.0, 1.0 / 121645100408832000.0];
        double sum = inv[9];
        for (int k = 8; k >= 0; k--) sum = inv[k] - x2 * sum;
        return x * sum;
    }

    static double CosSeries(double x)
    {
        double x2 = x * x;
        // coefficients (-1)^k / (2k)!
        double[] inv = [1.0, 1.0 / 2.0, 1.0 / 24.0, 1.0 / 720.0, 1.0 / 40320.0, 1.0 / 3628800.0, 1.0 / 479001600.0, 1.0 / 87178291200.0, 1.0 / 20922789888000.0, 1.0 / 6402373705728000.0];
        double sum = inv[9];
        for (int k = 8; k >= 0; k--) sum = inv[k] - x2 * sum;
        return sum;
    }

    public static double[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    public static double[] FromFloat(Matrix4x4 m) =>
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    public static Matrix4x4 ToFloat(double[] m) =>
        new((float)m[0], (float)m[1], (float)m[2], (float)m[3], (float)m[4], (float)m[5], (float)m[6], (float)m[7], (float)m[8], (float)m[9], (float)m[10], (float)m[11], (float)m[12], (float)m[13], (float)m[14], (float)m[15]);

    /// <summary>a then b (row vectors: p' = p a b).</summary>
    public static double[] Mul(double[] a, double[] b)
    {
        var r = new double[16];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                double s = a[i * 4] * b[j];
                s += a[i * 4 + 1] * b[4 + j];
                s += a[i * 4 + 2] * b[8 + j];
                s += a[i * 4 + 3] * b[12 + j];
                r[i * 4 + j] = s;
            }
        return r;
    }

    /// <summary>Scale, then rotate about X, then Y, then Z (fixed axes, angles in degrees snapped to 1/1000), then translate: the same placement as <see cref="MapScene.Transform"/>.</summary>
    /// <exception cref="MapException"><c>bad-transform</c> when a component is not finite or beyond <see cref="Limit"/>.</exception>
    public static double[] Local(MapTransform t)
    {
        double px = Check(t.Position.X), py = Check(t.Position.Y), pz = Check(t.Position.Z);
        double sx = Check(t.Scale.X), sy = Check(t.Scale.Y), sz = Check(t.Scale.Z);
        double rx = Check(t.Rotation.X), ry = Check(t.Rotation.Y), rz = Check(t.Rotation.Z);
        var (sinX, cosX) = SinCosDegrees(rx);
        var (sinY, cosY) = SinCosDegrees(ry);
        var (sinZ, cosZ) = SinCosDegrees(rz);
        // row-vector rotation matrices
        double[] Rx = [1, 0, 0, 0, 0, cosX, sinX, 0, 0, -sinX, cosX, 0, 0, 0, 0, 1];
        double[] Ry = [cosY, 0, -sinY, 0, 0, 1, 0, 0, sinY, 0, cosY, 0, 0, 0, 0, 1];
        double[] Rz = [cosZ, sinZ, 0, 0, -sinZ, cosZ, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        double[] S = [sx, 0, 0, 0, 0, sy, 0, 0, 0, 0, sz, 0, 0, 0, 0, 1];
        double[] T = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, px, py, pz, 1];
        return Mul(Mul(Mul(Mul(S, Rx), Ry), Rz), T);
    }

    static double Check(double v)
    {
        if (!double.IsFinite(v) || Math.Abs(v) > Limit) throw new MapException("bad-transform", $"A transform component ({v}) is not a finite number within ±{Limit:N0}.");
        return v;
    }

    /// <summary>The point moved by the matrix (p' = p M, w = 1), in double.</summary>
    public static (double X, double Y, double Z) Transform(double x, double y, double z, double[] m) =>
        (x * m[0] + y * m[4] + z * m[8] + m[12], x * m[1] + y * m[5] + z * m[9] + m[13], x * m[2] + y * m[6] + z * m[10] + m[14]);

    /// <summary>The point on the 1/4096 m grid as floats (every grid point is exactly representable, so the cast loses nothing).</summary>
    public static Vector3 Grid(double x, double y, double z)
    {
        const double g = DecorGeometry.Grid;
        return new Vector3((float)(Math.Round(x * g, MidpointRounding.ToEven) / g), (float)(Math.Round(y * g, MidpointRounding.ToEven) / g), (float)(Math.Round(z * g, MidpointRounding.ToEven) / g));
    }
}
