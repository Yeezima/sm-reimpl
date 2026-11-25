using SmContent.Customization;
using SmFormats.Bod;
using SmFormats.Dds;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// armour-pieces-1125 round 5 (the round-4 re-gate's per-piece defects): placeholder normal maps, the per-piece island pass and broad-shading removal, the region
/// data's bounds and the raptor left pad's own entry. Synthetic textures only (no original bytes).
/// </summary>
[Collection(BuiltMaskStatics.Name)]
public sealed class BuiltMaskRound5Tests
{
    const int W = 32, H = 32;

    static byte[] Solid(byte r, byte g, byte b, Func<int, int, int>? noise = null)
    {
        var rgba = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4, n = noise?.Invoke(x, y) ?? 0;
            rgba[i] = (byte)Math.Clamp(r + n, 0, 255); rgba[i + 1] = (byte)Math.Clamp(g + n, 0, 255); rgba[i + 2] = b; rgba[i + 3] = 255;
        }
        return DdsImage.EncodeRgba8WithMips(W, H, rgba);
    }

    [Fact]
    public void A_constant_tilted_normal_map_is_a_placeholder_and_a_flat_or_detailed_one_is_not()
    {
        Assert.True(BakedArmourPaint.IsPlaceholderNormal(Solid(200, 200, 200)));                     // the campaign raptor and berserker maps: one grey
        Assert.False(BakedArmourPaint.IsPlaceholderNormal(Solid(128, 128, 255)));                    // flat: harmless, kept
        Assert.False(BakedArmourPaint.IsPlaceholderNormal(Solid(128, 128, 255, (x, y) => (x * 7 + y * 3) % 40 - 20)));   // real detail
        Assert.False(BakedArmourPaint.IsPlaceholderNormal([1, 2, 3]));                                // undecodable
    }

    [Fact]
    public void A_built_material_marked_flat_is_painted_without_its_normal_map_unless_the_round_4_look_is_asked_for()
    {
        var tex = new Dictionary<string, string> { ["DiffuseMap"] = "d", ["NormalMap"] = "n", [BakedArmourPaint.PaintDiffuseKey] = "g", ["PaintMask"] = "m", [BakedArmourPaint.PaintFlatNormalKey] = "1" };
        var built = new BmatMaterial("m", "character_wargear", tex);
        var plain = new BmatMaterial("m", "character_wargear", new Dictionary<string, string> { ["DiffuseMap"] = "d", ["NormalMap"] = "n" });
        try
        {
            BakedArmourPaint.OldRound4Look = false; BakedArmourPaint.OldRegionStats = false;
            Assert.True(BakedArmourPaint.PaintsWithoutNormalMap(built));
            Assert.False(BakedArmourPaint.PaintsWithoutNormalMap(plain));   // the baked (full set) material keeps its map
            BakedArmourPaint.OldRound4Look = true;
            Assert.False(BakedArmourPaint.PaintsWithoutNormalMap(built));
        }
        finally { BakedArmourPaint.OldRound4Look = false; }
    }

    static PaintMaskBuilder.MaskOnly AllPrimaryWith(params (int X0, int Y0, int Size)[] tertiary)
    {
        var m = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) m[i * 4] = 255;
        foreach (var (x0, y0, size) in tertiary)
            for (int y = y0; y < y0 + size; y++)
            for (int x = x0; x < x0 + size; x++) { int o = (y * W + x) * 4; m[o] = 0; m[o + 2] = 255; }
        return new PaintMaskBuilder.MaskOnly(W, H, m, 80f, 80f, 62f, 1f, 0f, 0f);
    }

    [Fact]
    public void Islands_smaller_than_the_bound_join_the_region_round_them_and_larger_ones_stay()
    {
        var m = AllPrimaryWith((2, 2, 3), (16, 16, 10));   // a 9-texel fleck and a 100-texel emblem
        var merged = PaintMaskBuilder.MergeIslands(m, null, 20);
        Assert.Equal(255, merged.Mask[(3 * W + 3) * 4]);            // the fleck is plate now
        Assert.Equal(0, merged.Mask[(3 * W + 3) * 4 + 2]);
        Assert.Equal(255, merged.Mask[(20 * W + 20) * 4 + 2]);      // the emblem stays tertiary
        Assert.Same(m, PaintMaskBuilder.MergeIslands(m, null, 0));  // no bound: unchanged
        // Only some regions: the tertiary fleck is kept when only baked islands may merge.
        Assert.Equal(255, PaintMaskBuilder.MergeIslands(m, null, 20, [MaskRegion.Baked]).Mask[(3 * W + 3) * 4 + 2]);
    }

    [Fact]
    public void Flattening_moves_a_broad_dark_patch_toward_the_median_and_keeps_a_scratch()
    {
        // A plate at luminance 100 with a broad dark patch (luminance 50) over its left third and one light scratch column (130) in the plate's right part.
        var dif = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4;
            byte v = (byte)(x < 11 ? 50 : x == 24 ? 130 : 100);
            dif[i] = dif[i + 1] = dif[i + 2] = v; dif[i + 3] = 255;
        }
        var image = new DdsImage(W, H, dif);
        var m = AllPrimaryWith();
        var t = new PaintMaskBuilder.Targets(100f, 100f, 62f);
        var plain = PaintMaskBuilder.WithGrey(image, m, t);
        var flat = PaintMaskBuilder.WithGrey(image, m, t with { Flatten = 1f });
        int Lum(BuiltPaintMask b, int x, int y) => b.Diffuse[(y * W + x) * 4];
        // The middle of the dark patch: far below the target before, near it after.
        Assert.True(Lum(plain, 2, 16) < 70, $"plain {Lum(plain, 2, 16)}");
        Assert.True(Lum(flat, 2, 16) > 85, $"flat {Lum(flat, 2, 16)}");
        // The scratch stays lighter than the plate beside it.
        Assert.True(Lum(flat, 24, 16) > Lum(flat, 28, 16) + 10, $"scratch {Lum(flat, 24, 16)} plate {Lum(flat, 28, 16)}");
    }

    [Fact]
    public void The_round_5_region_data_stays_in_bounds_and_the_raptor_left_pad_has_its_own_disc()
    {
        Assert.All(BuiltMaskOverrides.All, e => Assert.InRange(e.MinIsland, 0, 400));
        Assert.All(BuiltMaskOverrides.All, e => Assert.InRange(e.Flatten, 0f, 1f));
        // The raptor's left pad: the disc is primary (the gold filter no longer takes its gilded plate).
        var raptor = BuiltMaskOverrides.For("csm_armour_raptor_shldrl", null).SelectMany(o => o.Regions).ToList();
        Assert.Contains(raptor, r => r.To == MaskRegion.Primary && r.Ellipse && r.Filter is null);
        Assert.DoesNotContain(raptor, r => r.To == MaskRegion.Tertiary && r.Filter is { MinChroma: 7f, MinHue: 0.9f });
        // A disc pad, synthetic: the rim ring tertiary, the inside primary, outside untouched (the berserker pad: round 7 dropped the ring on the raptor and basic pads).
        var dif = new DdsImage(W, H, Enumerable.Repeat((byte)90, W * H * 4).ToArray());
        var m = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) m[i * 4 + 1] = 255;   // all secondary to start
        var applied = BuiltMaskOverrides.Apply(new PaintMaskBuilder.MaskOnly(W, H, m, 80f, 80f, 62f, 0f, 1f, 0f), dif,
            BuiltMaskOverrides.For("csm_armour_berserker_shldrl", null).SelectMany(o => o.Regions).Where(r => r.Filter is null || r.Filter.MinLum == 4f).ToList());
        Assert.Equal(255, applied.Mask[(13 * W + 15) * 4]);        // the disc's middle: primary
        Assert.Equal(255, applied.Mask[(13 * W + 0) * 4 + 2]);     // the rim at the disc's left edge (U 0.0 to 0.045): tertiary
        Assert.Equal(255, applied.Mask[(31 * W + 0) * 4 + 1]);     // outside the disc: as it was
    }
}
