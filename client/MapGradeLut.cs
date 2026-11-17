using System;
using Godot;

namespace SmClient;

/// <summary>
/// classic-maps-fidelity fix 4: an environment region's colour grade (<c>ColourGradeEffectDesc</c>; Dome Mechanicus' <c>cg_interior_warmer</c>), a 32x32x32 look-up volume (a DDS volume texture, 32 bits per
/// texel) turned into Godot's 3D colour-correction texture (<c>Environment.AdjustmentColorCorrection</c>). **Interpretation** (medium): the volume is indexed (red, green, blue) = (x, y, z) as a standard
/// 3D grade look-up is, applied after the tone mapping; the mix with the ungraded image by <c>strength</c> is how a blend is drawn (the data names only a blend duration).
/// </summary>
public sealed class MapGradeLut
{
    public const int Size = 32;
    readonly float[] _lut;   // r, g, b per texel, index ((z * Size + y) * Size + x) * 3

    MapGradeLut(float[] lut) => _lut = lut;

    /// <summary>Reads a DDS volume of 32 x 32 x 32 uncompressed 32-bit texels; null when it is anything else.</summary>
    public static MapGradeLut? Read(byte[] dds)
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
        return new MapGradeLut(lut);
    }

    /// <summary>The graded colour of (r, g, b) in 0..1 (trilinear).</summary>
    public (float R, float G, float B) Sample(float r, float g, float b)
    {
        float fx = Math.Clamp(r, 0f, 1f) * (Size - 1), fy = Math.Clamp(g, 0f, 1f) * (Size - 1), fz = Math.Clamp(b, 0f, 1f) * (Size - 1);
        int x0 = (int)fx, y0 = (int)fy, z0 = (int)fz, x1 = Math.Min(x0 + 1, Size - 1), y1 = Math.Min(y0 + 1, Size - 1), z1 = Math.Min(z0 + 1, Size - 1);
        float tx = fx - x0, ty = fy - y0, tz = fz - z0;
        var o = new float[3];
        for (int c = 0; c < 3; c++)
        {
            float V(int x, int y, int z) => _lut[((z * Size + y) * Size + x) * 3 + c];
            float a = Lerp(Lerp(V(x0, y0, z0), V(x1, y0, z0), tx), Lerp(V(x0, y1, z0), V(x1, y1, z0), tx), ty);
            float d = Lerp(Lerp(V(x0, y0, z1), V(x1, y0, z1), tx), Lerp(V(x0, y1, z1), V(x1, y1, z1), tx), ty);
            o[c] = Lerp(a, d, tz);
        }
        return (o[0], o[1], o[2]);
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// The 3D colour-correction texture for a blend of <paramref name="strength"/> (0 = the image as it is, 1 = fully graded): per texel the grade of the colour that <paramref name="curve"/>
    /// (the look's own per-channel curve, null: none) leaves, mixed with the curve's output by <paramref name="strength"/>.
    /// </summary>
    public ImageTexture3D Build(float strength, Gradient? curve)
    {
        var layers = new Godot.Collections.Array<Image>();
        var row = new byte[Size * Size * 3];
        for (int z = 0; z < Size; z++)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float r = x / (float)(Size - 1), g = y / (float)(Size - 1), b = z / (float)(Size - 1);
                    if (curve is not null) { r = curve.Sample(r).R; g = curve.Sample(g).G; b = curve.Sample(b).B; }
                    var (gr, gg, gb) = Sample(r, g, b);
                    int i = (y * Size + x) * 3;
                    row[i] = Byte(Lerp(r, gr, strength)); row[i + 1] = Byte(Lerp(g, gg, strength)); row[i + 2] = Byte(Lerp(b, gb, strength));
                }
            layers.Add(Image.CreateFromData(Size, Size, false, Image.Format.Rgb8, (byte[])row.Clone()));
        }
        var texture = new ImageTexture3D();
        texture.Create(Image.Format.Rgb8, Size, Size, Size, false, layers);
        return texture;
    }

    static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
