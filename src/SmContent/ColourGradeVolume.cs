namespace SmContent;

/// <summary>
/// classic-maps-fidelity fix 4: a colour-grade look-up volume read from a DDS (32 x 32 x 32 uncompressed 32-bit texels). **Interpretation** (medium): the volume is indexed
/// (red, green, blue) = (x, y, z) as a standard 3D grade look-up is. Engine-neutral so it can be tested without the client.
/// </summary>
public sealed class ColourGradeVolume
{
    public const int Size = 32;
    readonly float[] _lut;   // r, g, b per texel, index ((z * Size + y) * Size + x) * 3

    ColourGradeVolume(float[] lut) => _lut = lut;

    /// <summary>Reads a DDS volume of 32 x 32 x 32 uncompressed 32-bit texels; null when it is anything else (wrong size, bit count, channel masks, or truncated).</summary>
    public static ColourGradeVolume? Read(byte[] dds)
    {
        if (dds.Length < 128 + Size * Size * Size * 4 || BitConverter.ToUInt32(dds, 0) != 0x20534444) return null;
        uint height = BitConverter.ToUInt32(dds, 12), width = BitConverter.ToUInt32(dds, 16), depth = BitConverter.ToUInt32(dds, 24), bits = BitConverter.ToUInt32(dds, 88);
        if (width != Size || height != Size || depth != Size || bits != 32) return null;
        uint[] masks = [BitConverter.ToUInt32(dds, 92), BitConverter.ToUInt32(dds, 96), BitConverter.ToUInt32(dds, 100)];   // red, green, blue
        int[] at = new int[3];
        for (int c = 0; c < 3; c++)
        {
            at[c] = masks[c] switch { 0x000000FF => 0, 0x0000FF00 => 1, 0x00FF0000 => 2, 0xFF000000 => 3, _ => -1 };
            if (at[c] < 0) return null;
        }
        var lut = new float[Size * Size * Size * 3];
        for (int i = 0; i < Size * Size * Size; i++)
            for (int c = 0; c < 3; c++) lut[i * 3 + c] = dds[128 + i * 4 + at[c]] / 255f;
        return new ColourGradeVolume(lut);
    }

    /// <summary>The value of one texel channel (0 red, 1 green, 2 blue).</summary>
    public float Texel(int x, int y, int z, int channel) => _lut[((z * Size + y) * Size + x) * 3 + channel];

    /// <summary>The graded colour of (r, g, b) in 0..1 (trilinear). No allocation.</summary>
    public (float R, float G, float B) Sample(float r, float g, float b)
    {
        float fx = Math.Clamp(r, 0f, 1f) * (Size - 1), fy = Math.Clamp(g, 0f, 1f) * (Size - 1), fz = Math.Clamp(b, 0f, 1f) * (Size - 1);
        int x0 = (int)fx, y0 = (int)fy, z0 = (int)fz, x1 = Math.Min(x0 + 1, Size - 1), y1 = Math.Min(y0 + 1, Size - 1), z1 = Math.Min(z0 + 1, Size - 1);
        float tx = fx - x0, ty = fy - y0, tz = fz - z0;
        return (Channel(0, x0, y0, z0, x1, y1, z1, tx, ty, tz), Channel(1, x0, y0, z0, x1, y1, z1, tx, ty, tz), Channel(2, x0, y0, z0, x1, y1, z1, tx, ty, tz));
    }

    float Channel(int c, int x0, int y0, int z0, int x1, int y1, int z1, float tx, float ty, float tz)
    {
        float V(int x, int y, int z) => _lut[((z * Size + y) * Size + x) * 3 + c];
        float a = Lerp(Lerp(V(x0, y0, z0), V(x1, y0, z0), tx), Lerp(V(x0, y1, z0), V(x1, y1, z0), tx), ty);
        float d = Lerp(Lerp(V(x0, y0, z1), V(x1, y0, z1), tx), Lerp(V(x0, y1, z1), V(x1, y1, z1), tx), ty);
        return Lerp(a, d, tz);
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
