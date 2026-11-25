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

    /// <summary>
    /// A synthetic black-schemed piece: near-black plates (specular 60) with faint noise, a near-black rubber band at the bottom (specular 10), a light grey trim
    /// band (rows 20 to 25, specular 120) and 3 x 3 light scratches (specular 40) spread over the plate.
    /// </summary>
    static (DdsImage Diffuse, DdsImage Specular) SyntheticBlack()
    {
        var dif = new byte[W * H * 4]; var spc = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4;
            bool scratch = y < 48 && (x % 12) is >= 4 and < 7 && (y % 12) is >= 4 and < 7 && !(y is >= 18 and < 28);
            (byte v, byte s) = y >= 52 ? ((byte)7, (byte)10)
                : y is >= 20 and < 26 ? ((byte)95, (byte)120)
                : scratch ? ((byte)90, (byte)40)
                : ((byte)(5 + (x * 7 + y * 3) % 4), (byte)60);
            dif[i] = dif[i + 1] = v; dif[i + 2] = (byte)(v + 1); dif[i + 3] = 255;
            spc[i] = spc[i + 1] = spc[i + 2] = s; spc[i + 3] = 255;
        }
        return (new DdsImage(W, H, dif), new DdsImage(W, H, spc));
    }

    [Fact]
    public void Black_plates_paint_their_plate_and_trim_keep_scratches_and_leave_rubber_baked()
    {
        var (dif, spc) = SyntheticBlack();
        var m = PaintMaskBuilder.Build(dif, spc, null);
        Assert.True(M(m, 34, 46, 0) > 200, "black plate is primary");
        Assert.True(M(m, 30, 22, 2) > 150, "light specular trim band is tertiary (painted like regular trim)");
        Assert.True(M(m, 5, 5, 0) < 90, "centre of a scratch stays baked");
        Assert.True(M(m, 30, 58, 0) + M(m, 30, 58, 1) + M(m, 30, 58, 2) < 40, "low-specular rubber stays baked");
        // The plate's grey shading follows the texture's small differences without blotches: within the regular plate brightness band.
        for (int x = 8; x < 60; x += 5) Assert.InRange((int)m.Diffuse[(46 * W + x) * 4], 60, 110);
        Assert.Equal(m.Mask, PaintMaskBuilder.Build(dif, spc, null).Mask);   // deterministic
        Assert.Equal(m.Diffuse, PaintMaskBuilder.Build(dif, spc, null).Diffuse);
    }

    [Fact]
    public void The_old_dark_plates_switch_restores_the_earlier_masks()
    {
        var (dif, spc) = SyntheticBlack();
        var now = PaintMaskBuilder.Build(dif, spc, null);
        BuiltPaintMask old;
        try { PaintMaskBuilder.OldDarkPlates = true; old = PaintMaskBuilder.Build(dif, spc, null); }
        finally { PaintMaskBuilder.OldDarkPlates = false; }
        Assert.True(M(old, 30, 22, 2) < 60, "before: the trim was not tertiary");
        Assert.NotEqual(now.Mask, old.Mask);
        // Coloured plates are not affected by the dark-plate rules.
        var (red, redSpc) = Synthetic();
        var a = PaintMaskBuilder.Build(red, redSpc, null);
        try { PaintMaskBuilder.OldDarkPlates = true; Assert.Equal(a.Mask, PaintMaskBuilder.Build(red, redSpc, null).Mask); }
        finally { PaintMaskBuilder.OldDarkPlates = false; }
    }

    [Fact]
    public void Matte_black_plates_with_an_empty_specular_map_are_painted_where_the_mesh_covers_them()
    {
        // Repair attempt 1: luminance and specular under 4 read as unused background, and the specular gates dropped matte plates as undersuit.
        var dif = new byte[W * H * 4]; var spc = new byte[W * H * 4]; var used = new bool[W * H];
        for (int i = 0; i < W * H; i++)
        {
            byte v = (byte)(3 + i % 3);
            dif[i * 4] = dif[i * 4 + 1] = dif[i * 4 + 2] = v; dif[i * 4 + 3] = 255;
            spc[i * 4] = spc[i * 4 + 1] = spc[i * 4 + 2] = 1; spc[i * 4 + 3] = 255;
            used[i] = i % W < 48;
        }
        var m = PaintMaskBuilder.Build(new DdsImage(W, H, dif), new DdsImage(W, H, spc), null, used);
        Assert.True(M(m, 20, 30, 0) > 200, "a covered matte black plate texel is primary");
        Assert.Equal(0, M(m, 60, 30, 0));   // not covered by the mesh: left alone
        BuiltPaintMask old;
        try { PaintMaskBuilder.OldMaskRules = true; old = PaintMaskBuilder.Build(new DdsImage(W, H, dif), new DdsImage(W, H, spc), null, used); }
        finally { PaintMaskBuilder.OldMaskRules = false; }
        Assert.True(M(old, 20, 30, 0) < 50, "before (old-mask-rules): left baked");
    }

    [Fact]
    public void Darker_paint_of_the_plates_hue_joins_the_plate()
    {
        // Repair attempt 1: shadowed, dark-edged paint of the plate's hue kept its baked colour and showed through the new paint as blotches.
        var dif = new byte[W * H * 4]; var spc = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4;
            bool dark = x is >= 20 and < 30 && y is >= 20 and < 44;   // a large darker band of the same green, connected to the plate
            (byte r, byte g, byte b) = dark ? ((byte)12, (byte)44, (byte)16) : ((byte)40, (byte)(140 + (x + y) % 9), (byte)50);
            dif[i] = r; dif[i + 1] = g; dif[i + 2] = b; dif[i + 3] = 255;
            spc[i] = spc[i + 1] = spc[i + 2] = 60; spc[i + 3] = 255;
        }
        var m = PaintMaskBuilder.Build(new DdsImage(W, H, dif), new DdsImage(W, H, spc), null);
        Assert.True(M(m, 25, 32, 0) > 200, "the darker band of the plate's green is primary");
        Assert.True(M(m, 5, 5, 0) > 200);
    }

    [Fact]
    public void A_glossy_black_field_stays_primary_and_strong_flames_on_it_are_secondary()
    {
        // Repair attempt 2 (owner: Legion of the Damned flames "Paint them secondary"): black field (glossy as the flames) primary, orange flames secondary.
        var dif = new byte[W * H * 4]; var spc = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4;
            bool flame = y > 36 && (x % 10) < 6 + (y - 36) / 6;
            (byte r, byte g, byte b) = flame ? ((byte)(220 + x % 8), (byte)(110 + y % 12), (byte)30) : ((byte)(14 + (x + y) % 4), (byte)14, (byte)15);
            dif[i] = r; dif[i + 1] = g; dif[i + 2] = b; dif[i + 3] = 255;
            spc[i] = spc[i + 1] = spc[i + 2] = 60; spc[i + 3] = 255;
        }
        var m = PaintMaskBuilder.Build(new DdsImage(W, H, dif), new DdsImage(W, H, spc), null);
        Assert.True(M(m, 30, 10, 0) > 200, "the black field is primary");
        Assert.True(M(m, 2, 60, 1) > 200, "a flame is secondary");
    }

    [Fact]
    public void Built_chaos_legs_and_pads_put_their_plates_in_the_regular_armours_channel()
    {
        // Repair attempt 2, measured on the install's masks and the rendered regular armour: Chaos legs secondary, everything else primary.
        Assert.Equal(1, BakedArmourPaint.PlateChannel("csm_armour_mp_dlc3_set3_legl"));
        Assert.Equal(1, BakedArmourPaint.PlateChannel("csm_armour_basic_legr"));
        Assert.Equal(0, BakedArmourPaint.PlateChannel("csm_armour_mp_dlc2_set1_shldrl"));   // pads draw in their primary colour
        Assert.Equal(0, BakedArmourPaint.PlateChannel("csm_armour_mp_dlc2_set1_torso"));
        Assert.Equal(0, BakedArmourPaint.PlateChannel("csm_armour_mp_dlc2_set1_arml"));
        Assert.Equal(0, BakedArmourPaint.PlateChannel("sm_armour_mp_dlc1_set1_legl"));
        Assert.Equal(0, BakedArmourPaint.PlateChannel(null));
        var (dif, spc) = Synthetic();
        var mask = PaintMaskBuilder.BuildMask(dif, spc, null);
        var legs = PaintMaskBuilder.Remap(mask, 1);
        int plate = (40 * W + 10) * 4;
        Assert.Equal(mask.Mask[plate], legs.Mask[plate + 1]);
        Assert.Equal(mask.Mask[plate + 1], legs.Mask[plate]);
        Assert.Equal((mask.PlateMedian, mask.Primary), (legs.SecondaryMedian, legs.Secondary));
        // The grey diffuse is the same whichever channel carries the plate.
        Assert.Equal(PaintMaskBuilder.WithGrey(dif, mask).Diffuse, PaintMaskBuilder.WithGrey(dif, legs).Diffuse);
        Assert.Same(mask, PaintMaskBuilder.Remap(mask, 0));
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
