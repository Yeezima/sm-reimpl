using System;
using System.Collections.Generic;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// classic-maps-fidelity fix 4: an environment region's colour grade (<c>ColourGradeEffectDesc</c>; Dome Mechanicus' <c>cg_interior_warmer</c>), a 32x32x32 look-up volume (a DDS volume texture, 32 bits per
/// texel) turned into Godot's 3D colour-correction texture (<c>Environment.AdjustmentColorCorrection</c>). **Interpretation** (medium): the volume is indexed (red, green, blue) = (x, y, z) as a standard
/// 3D grade look-up is, applied after the tone mapping; the mix with the ungraded image by <c>strength</c> is how a blend is drawn (the data names only a blend duration).
/// </summary>
public sealed class MapGradeLut
{
    public const int Size = ColourGradeVolume.Size;
    readonly ColourGradeVolume _volume;
    readonly Dictionary<int, ImageTexture3D> _built = new();   // by strength step (rebuilds are cached: a blend in, out and in again builds each step once)

    MapGradeLut(ColourGradeVolume volume) => _volume = volume;

    /// <summary>Reads a DDS volume of 32 x 32 x 32 uncompressed 32-bit texels (<see cref="ColourGradeVolume.Read"/>); null when it is anything else.</summary>
    public static MapGradeLut? Read(byte[] dds) => ColourGradeVolume.Read(dds) is { } v ? new MapGradeLut(v) : null;

    /// <summary>The graded colour of (r, g, b) in 0..1 (trilinear).</summary>
    public (float R, float G, float B) Sample(float r, float g, float b) => _volume.Sample(r, g, b);

    /// <summary>The cached texture of strength step <paramref name="step"/> (of <paramref name="steps"/>), built on first use (see <see cref="Build"/>).</summary>
    public ImageTexture3D Get(int step, int steps, Gradient? curve)
    {
        if (!_built.TryGetValue(step, out var texture)) _built[step] = texture = Build(step / (float)steps, curve);
        return texture;
    }

    /// <summary>
    /// The 3D colour-correction texture for a blend of <paramref name="strength"/> (0 = the image as it is, 1 = fully graded): per texel the grade of the colour that <paramref name="curve"/>
    /// (the look's own per-channel curve, null: none) leaves, mixed with the curve's output by <paramref name="strength"/>. The curve is sampled once per axis step, not per texel.
    /// </summary>
    public ImageTexture3D Build(float strength, Gradient? curve)
    {
        var axisR = new float[Size]; var axisG = new float[Size]; var axisB = new float[Size];
        for (int i = 0; i < Size; i++)
        {
            float v = i / (float)(Size - 1);
            if (curve is null) { axisR[i] = axisG[i] = axisB[i] = v; }
            else { var c = curve.Sample(v); axisR[i] = c.R; axisG[i] = c.G; axisB[i] = c.B; }
        }
        var layers = new Godot.Collections.Array<Image>();
        var row = new byte[Size * Size * 3];
        for (int z = 0; z < Size; z++)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float r = axisR[x], g = axisG[y], b = axisB[z];
                    var (gr, gg, gb) = _volume.Sample(r, g, b);
                    int i = (y * Size + x) * 3;
                    row[i] = Byte(Lerp(r, gr, strength)); row[i + 1] = Byte(Lerp(g, gg, strength)); row[i + 2] = Byte(Lerp(b, gb, strength));
                }
            layers.Add(Image.CreateFromData(Size, Size, false, Image.Format.Rgb8, (byte[])row.Clone()));
        }
        var texture = new ImageTexture3D();
        texture.Create(Image.Format.Rgb8, Size, Size, Size, false, layers);
        return texture;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;
    static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
