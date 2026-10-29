using System.Buffers.Binary;

namespace SmFormats.Audio;

/// <summary>One point of an attenuation curve: distance (x), value (y) and the shape of the segment that starts here.</summary>
public readonly record struct CurvePoint(float X, float Y, uint Interpolation);

/// <summary>
/// An attenuation curve (v56): <see cref="Scaling"/> (2 = dB for the volume curves, interpretation) and its points, sorted
/// by distance as the bank stores them.
/// </summary>
public sealed record AttenuationCurve(byte Scaling, IReadOnlyList<CurvePoint> Points)
{
    /// <summary>Wwise's "constant" segment shape (holds the start value; Wwise convention, interpretation).</summary>
    public const uint Constant = 9;

    public float MaxX => Points.Count > 0 ? Points[^1].X : 0f;

    /// <summary>
    /// The curve's value at <paramref name="x"/>: the first point's value before it, the last one's after it, and between
    /// two points a straight line, or the start value for a <see cref="Constant"/> segment. Wwise's other segment shapes
    /// (logarithmic, S-curves, exponential) are drawn as straight lines: a **provisional approximation**.
    /// </summary>
    public float Evaluate(float x)
    {
        if (Points.Count == 0 || !float.IsFinite(x)) return 0f;
        if (x <= Points[0].X) return Points[0].Y;
        for (int i = 1; i < Points.Count; i++)
        {
            var a = Points[i - 1];
            var b = Points[i];
            if (x > b.X) continue;
            if (a.Interpolation == Constant || b.X <= a.X) return a.Y;
            return a.Y + (b.Y - a.Y) * ((x - a.X) / (b.X - a.X));
        }
        return Points[^1].Y;
    }
}

/// <summary>
/// An attenuation object (HIRC type 14, v56), read from its payload: <c>u8 coneEnabled</c> (with a cone: 4 × f32 inside
/// angle, outside angle, outside volume, low-pass), <c>5 × s8 curveToUse</c> (dry volume, wet volume, LFE, low-pass, spread;
/// -1 none), <c>u8 curveCount</c>, curves <c>{u8 scaling, u16 pointCount, pointCount × {f32 x, f32 y, u32 interpolation}}</c>.
/// What follows (RTPCs) is not read. Checked on the install: the bolter's (0x10b21f17) dry volume curve is 0 dB at 0,
/// -66.9 dB at 103.9 and -96.3 dB at 300 (docs/discovery.md, "Audio (Wwise)").
/// </summary>
public sealed record WwiseAttenuation(uint Id, bool ConeEnabled, IReadOnlyList<sbyte> CurveToUse, IReadOnlyList<AttenuationCurve> Curves)
{
    /// <summary>The dry (direct) volume curve, distance → dB, or null.</summary>
    public AttenuationCurve? DryVolume => CurveToUse.Count > 0 && CurveToUse[0] >= 0 && CurveToUse[0] < Curves.Count ? Curves[CurveToUse[0]] : null;

    /// <summary>Where the dry volume curve ends (game units; metres assumed): beyond it the sound keeps the last value.</summary>
    public float MaxDistance => DryVolume?.MaxX ?? 0f;

    /// <summary>The dry volume (dB) at <paramref name="distance"/>; 0 without a curve.</summary>
    public float VolumeAt(float distance) => DryVolume?.Evaluate(distance) ?? 0f;

    /// <summary>
    /// The low-pass curve (the fourth slot of <c>curveToUse</c>), distance → LPF value 0..100 (scaling 0, linear values): 203 of
    /// the 232 attenuation objects of the multiplayer banks have one (e.g. the bolter's: 0 up to 92 m, 59 at 110 m). Interpretation
    /// of the slot order (dry volume, wet volume, LFE, LPF, spread), supported by the values.
    /// </summary>
    public AttenuationCurve? LowPass => CurveToUse.Count > 3 && CurveToUse[3] >= 0 && CurveToUse[3] < Curves.Count ? Curves[CurveToUse[3]] : null;

    /// <summary>The low-pass value (0..100) at <paramref name="distance"/>; 0 without a curve.</summary>
    public float LpfAt(float distance) => LowPass?.Evaluate(distance) ?? 0f;

    /// <summary>Reads an attenuation object kept raw by <see cref="SoundBank"/>; null with the reason when it does not fit the layout.</summary>
    public static WwiseAttenuation? TryRead(HircObject obj, out string? error)
    {
        error = null;
        if (obj.Type != HircType.Attenuation) { error = $"{obj.Type} is not an attenuation"; return null; }
        var d = obj.Payload.Span;
        int p = 0;
        try
        {
            bool cone = U8(d, ref p) != 0;
            if (cone) Need(d, ref p, 16);
            var use = new sbyte[5];
            for (int i = 0; i < 5; i++) use[i] = (sbyte)U8(d, ref p);
            int count = U8(d, ref p);
            if (count > 16) throw new InvalidDataException($"{count} curves");
            var curves = new AttenuationCurve[count];
            for (int c = 0; c < count; c++)
            {
                byte scaling = U8(d, ref p);
                int n = BinaryPrimitives.ReadUInt16LittleEndian(Need(d, ref p, 2));
                if (n > 256) throw new InvalidDataException($"{n} curve points");
                var points = new CurvePoint[n];
                for (int i = 0; i < n; i++)
                {
                    var s = Need(d, ref p, 12);
                    float x = BinaryPrimitives.ReadSingleLittleEndian(s), y = BinaryPrimitives.ReadSingleLittleEndian(s[4..]);
                    if (!float.IsFinite(x) || !float.IsFinite(y)) throw new InvalidDataException("a curve point is not finite");
                    if (i > 0 && x < points[i - 1].X) throw new InvalidDataException("curve points are not sorted by distance");
                    points[i] = new CurvePoint(x, y, BinaryPrimitives.ReadUInt32LittleEndian(s[8..]));
                }
                curves[c] = new AttenuationCurve(scaling, points);
            }
            foreach (var u in use)
                if (u >= count) throw new InvalidDataException($"curve index {u} of {count}");
            return new WwiseAttenuation(obj.Id, cone, use, curves);
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    static byte U8(ReadOnlySpan<byte> d, ref int p) => Need(d, ref p, 1)[0];

    static ReadOnlySpan<byte> Need(ReadOnlySpan<byte> d, ref int p, int n)
    {
        if (n < 0 || p + n > d.Length) throw new InvalidDataException("attenuation truncated");
        var s = d.Slice(p, n);
        p += n;
        return s;
    }
}
