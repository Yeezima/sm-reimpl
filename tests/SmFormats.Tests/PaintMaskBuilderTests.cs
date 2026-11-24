using SmContent.Customization;
using SmFormats.Dds;
using Xunit;

namespace SmFormats.Tests;

/// <summary>armour-pieces-1125: the built paint masks for baked armour, on synthetic textures only (no original bytes).</summary>
public sealed class PaintMaskBuilderTests
{
    const int W = 64, H = 64;

    /// <summary>
    /// A synthetic "baked" piece: left half red plates with a little shading noise, a light bone ornament square in the middle, a black undersuit band
    /// at the bottom (no specular), background black in the top rows.
    /// </summary>
    static (DdsImage Diffuse, DdsImage Specular) Synthetic()
    {
        var dif = new byte[W * H * 4]; var spc = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4;
            (byte r, byte g, byte b, byte s) = y < 4 ? ((byte)0, (byte)0, (byte)0, (byte)0)
                : y >= 52 ? ((byte)14, (byte)14, (byte)16, (byte)0)
                : x is >= 26 and < 38 && y is >= 22 and < 34 ? ((byte)205, (byte)190, (byte)150, (byte)90)
                : ((byte)(150 + (x * 7 + y * 3) % 20), (byte)(22 + (x + y) % 6), (byte)20, (byte)70);
            dif[i] = r; dif[i + 1] = g; dif[i + 2] = b; dif[i + 3] = 255;
            spc[i] = spc[i + 1] = spc[i + 2] = s; spc[i + 3] = 255;
        }
        return (new DdsImage(W, H, dif), new DdsImage(W, H, spc));
    }

    static byte M(BuiltPaintMask m, int x, int y, int c) => m.Mask[(y * W + x) * 4 + c];

    [Fact]
    public void Plates_become_primary_ornaments_tertiary_and_undersuit_and_background_stay_unpainted()
    {
        var (dif, spc) = Synthetic();
        var m = PaintMaskBuilder.Build(dif, spc, null);
        Assert.True(M(m, 10, 40, 0) > 200, "red plate pixel is primary");
        Assert.True(M(m, 32, 28, 2) > 200, "bone ornament pixel is tertiary");
        Assert.True(M(m, 10, 58, 0) + M(m, 10, 58, 1) + M(m, 10, 58, 2) < 20, "undersuit stays baked");
        Assert.Equal(0, M(m, 10, 1, 0) + M(m, 10, 1, 1) + M(m, 10, 1, 2));
        Assert.All(Enumerable.Range(0, W * H), i => Assert.Equal(0, m.Mask[i * 4 + 3]));   // no dirt channel
        // The painted plate's grey diffuse is neutral (R = G = B) and near the regular armour's plate brightness; unpainted pixels keep their colour.
        int p = (40 * W + 10) * 4;
        Assert.InRange(Math.Abs(m.Diffuse[p] - m.Diffuse[p + 1]), 0, 3);
        Assert.InRange((int)m.Diffuse[p], 50, 130);
        int u = (58 * W + 10) * 4;
        Assert.Equal((dif.Rgba[u], dif.Rgba[u + 1], dif.Rgba[u + 2]), (m.Diffuse[u], m.Diffuse[u + 1], m.Diffuse[u + 2]));
    }

    [Fact]
    public void The_builder_is_deterministic()
    {
        var (dif, spc) = Synthetic();
        var a = PaintMaskBuilder.Build(dif, spc, null);
        var b = PaintMaskBuilder.Build(dif, spc, null);
        Assert.Equal(a.Mask, b.Mask);
        Assert.Equal(a.Diffuse, b.Diffuse);
    }

    [Fact]
    public void The_light_property_reflection_mask_marks_ornaments()
    {
        var (dif, spc) = Synthetic();
        var lp = new byte[W * H * 4];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i = (y * W + x) * 4; lp[i] = 239; lp[i + 2] = (byte)(x < 8 && y is > 8 and < 50 ? 200 : 0); lp[i + 3] = 255; }
        var m = PaintMaskBuilder.Build(dif, spc, new DdsImage(W, H, lp));
        Assert.True(M(m, 3, 30, 2) > 200);
        Assert.True(M(m, 3, 30, 0) < 40);
    }

    [Fact]
    public void Dds_round_trip_keeps_the_first_level_and_writes_a_full_mip_chain()
    {
        var (dif, _) = Synthetic();
        var bytes = DdsImage.EncodeRgba8WithMips(W, H, dif.Rgba);
        Assert.Equal(7, BitConverter.ToInt32(bytes, 28));   // 64, 32, ..., 1
        var back = DdsImage.TryDecode(bytes);
        Assert.NotNull(back);
        Assert.Equal(dif.Rgba, back!.Rgba);
        Assert.Null(DdsImage.TryDecode(new byte[10]));
    }

    [Fact]
    public void Bc1_blocks_decode()
    {
        // One 4x4 DXT1 block: colour 0 pure red (0xF800), colour 1 pure blue (0x001F), all indices 0 -> every pixel red.
        var dds = new byte[DdsImage.HeaderBytes + 8];
        "DDS "u8.CopyTo(dds);
        BitConverter.GetBytes(124).CopyTo(dds, 4);
        BitConverter.GetBytes(4).CopyTo(dds, 12); BitConverter.GetBytes(4).CopyTo(dds, 16);
        BitConverter.GetBytes(4u).CopyTo(dds, 80);
        "DXT1"u8.CopyTo(dds.AsSpan(84));
        dds[128] = 0x00; dds[129] = 0xF8; dds[130] = 0x1F; dds[131] = 0x00;
        var img = DdsImage.TryDecode(dds)!;
        Assert.Equal((255, 0, 0, 255), (img.Rgba[0], img.Rgba[1], img.Rgba[2], img.Rgba[3]));
    }
}
