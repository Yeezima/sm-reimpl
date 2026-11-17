using SmContent;

namespace SmFormats.Tests;

/// <summary>classic-maps-fidelity fix 4: the colour-grade look-up volume reader. Synthetic DDS bytes only.</summary>
public class ColourGradeVolumeTests
{
    const int N = ColourGradeVolume.Size;

    /// <summary>A 32 x 32 x 32 DDS volume with 32-bit texels; the texel at (x, y, z) is (x*8, y*8, z*8) in the given channel byte order (red mask given).</summary>
    static byte[] Volume(uint redMask = 0xFF, uint greenMask = 0xFF00, uint blueMask = 0xFF0000, int width = N, int bits = 32, int truncate = 0)
    {
        var d = new byte[128 + N * N * N * 4];
        BitConverter.GetBytes(0x20534444u).CopyTo(d, 0);
        BitConverter.GetBytes((uint)N).CopyTo(d, 12);
        BitConverter.GetBytes((uint)width).CopyTo(d, 16);
        BitConverter.GetBytes((uint)N).CopyTo(d, 24);
        BitConverter.GetBytes((uint)bits).CopyTo(d, 88);
        BitConverter.GetBytes(redMask).CopyTo(d, 92);
        BitConverter.GetBytes(greenMask).CopyTo(d, 96);
        BitConverter.GetBytes(blueMask).CopyTo(d, 100);
        int[] at(uint m) => [m == 0xFF ? 0 : m == 0xFF00 ? 1 : m == 0xFF0000 ? 2 : 3];
        for (int z = 0; z < N; z++)
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int o = 128 + ((z * N + y) * N + x) * 4;
                    d[o + at(redMask)[0]] = (byte)(x * 8); d[o + at(greenMask)[0]] = (byte)(y * 8); d[o + at(blueMask)[0]] = (byte)(z * 8);
                }
        return truncate > 0 ? d[..^truncate] : d;
    }

    [Fact]
    public void A_32_cubed_volume_reads_with_red_green_blue_as_x_y_z()
    {
        var v = ColourGradeVolume.Read(Volume())!;
        Assert.Equal(8 / 255f, v.Texel(1, 2, 3, 0));
        Assert.Equal(16 / 255f, v.Texel(1, 2, 3, 1));
        Assert.Equal(24 / 255f, v.Texel(1, 2, 3, 2));
    }

    [Fact]
    public void Swapped_channel_masks_are_honoured()
    {
        var v = ColourGradeVolume.Read(Volume(redMask: 0xFF0000, blueMask: 0xFF))!;
        Assert.Equal(8 / 255f, v.Texel(1, 2, 3, 0));
        Assert.Equal(24 / 255f, v.Texel(1, 2, 3, 2));
    }

    [Fact]
    public void A_wrong_size_bit_count_mask_or_truncated_file_is_refused()
    {
        Assert.Null(ColourGradeVolume.Read(Volume(width: 16)));
        Assert.Null(ColourGradeVolume.Read(Volume(bits: 24)));
        Assert.Null(ColourGradeVolume.Read(Volume(redMask: 0x00FF00FF)));
        Assert.Null(ColourGradeVolume.Read(Volume(truncate: 4)));
        Assert.Null(ColourGradeVolume.Read(new byte[10]));
        var bad = Volume(); bad[0] = 0;
        Assert.Null(ColourGradeVolume.Read(bad));
    }

    [Fact]
    public void Sampling_is_trilinear_between_texels_and_clamps()
    {
        var v = ColourGradeVolume.Read(Volume())!;
        var (r, g, b) = v.Sample(0.5f / (N - 1), 0f, 1f);   // half a texel along x
        Assert.Equal(4 / 255f, r, 4);
        Assert.Equal(0f, g, 4);
        Assert.Equal(248 / 255f, b, 4);
        Assert.Equal(v.Sample(1f, 1f, 1f), v.Sample(5f, 5f, 5f));
    }
}
