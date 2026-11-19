namespace SmContent;

/// <summary>
/// A 4 x 4 matrix of doubles (row-major, for row vectors) with value equality, so a record that carries one keeps its value semantics.
/// </summary>
public sealed class DetMatrix(double[] values) : IEquatable<DetMatrix>
{
    public double[] Values { get; } = values;
    public bool Equals(DetMatrix? other) => other is not null && Values.AsSpan().SequenceEqual(other.Values);
    public override bool Equals(object? obj) => obj is DetMatrix m && Equals(m);
    public override int GetHashCode() { var h = new HashCode(); foreach (var v in Values) h.Add(v); return h.ToHashCode(); }
}

/// <summary>
/// Placement maths that gives the same bits on every machine, used by the Mapmaker's custom-map collision (docs/mapmaker.md "SM1 pieces in play"): double precision, rotation angles snapped to
/// 1/1000 degree, and a sin/cos made only of IEEE + - * / (never the platform's libm). It is a separate path: <see cref="MapScene.Transform"/> and everything the Classic maps use are unchanged.
/// Matrices are 16 doubles, row-major, for row vectors (p' = p M) like <see cref="System.Numerics.Matrix4x4"/>.
/// </summary>
public static class DetTransform
{
    /// <summary>Rotation angles are rounded to this many steps per degree before they are used.</summary>
    public const double AngleSteps = 1000.0;
    public const double DegToRad = 0.017453292519943295769236907684886;   // pi / 180

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

    static readonly double[] SinCoefficients = [1.0, 1.0 / 6.0, 1.0 / 120.0, 1.0 / 5040.0, 1.0 / 362880.0, 1.0 / 39916800.0, 1.0 / 6227020800.0, 1.0 / 1307674368000.0, 1.0 / 355687428096000.0, 1.0 / 121645100408832000.0];
    static readonly double[] CosCoefficients = [1.0, 1.0 / 2.0, 1.0 / 24.0, 1.0 / 720.0, 1.0 / 40320.0, 1.0 / 3628800.0, 1.0 / 479001600.0, 1.0 / 87178291200.0, 1.0 / 20922789888000.0, 1.0 / 6402373705728000.0];

    static double SinSeries(double x)
    {
        double x2 = x * x, sum = SinCoefficients[9];
        for (int k = 8; k >= 0; k--) sum = SinCoefficients[k] - x2 * sum;
        return x * sum;
    }

    static double CosSeries(double x)
    {
        double x2 = x * x, sum = CosCoefficients[9];
        for (int k = 8; k >= 0; k--) sum = CosCoefficients[k] - x2 * sum;
        return sum;
    }

    public static double[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

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

    /// <summary>Scale, then rotate about X, then Y, then Z (fixed axes, degrees, snapped to 1/1000), then translate: the placement <see cref="MapScene.Transform"/> makes, bit-for-bit the same on every OS.</summary>
    public static double[] Local(double px, double py, double pz, double rxDegrees, double ryDegrees, double rzDegrees, double sx, double sy, double sz)
    {
        var (sinX, cosX) = SinCosDegrees(rxDegrees);
        var (sinY, cosY) = SinCosDegrees(ryDegrees);
        var (sinZ, cosZ) = SinCosDegrees(rzDegrees);
        double[] Rx = [1, 0, 0, 0, 0, cosX, sinX, 0, 0, -sinX, cosX, 0, 0, 0, 0, 1];
        double[] Ry = [cosY, 0, -sinY, 0, 0, 1, 0, 0, sinY, 0, cosY, 0, 0, 0, 0, 1];
        double[] Rz = [cosZ, sinZ, 0, 0, -sinZ, cosZ, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        double[] S = [sx, 0, 0, 0, 0, sy, 0, 0, 0, 0, sz, 0, 0, 0, 0, 1];
        double[] T = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, px, py, pz, 1];
        return Mul(Mul(Mul(Mul(S, Rx), Ry), Rz), T);
    }
}
