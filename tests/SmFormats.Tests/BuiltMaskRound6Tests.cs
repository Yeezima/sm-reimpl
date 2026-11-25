using SmContent.Customization;
using SmFormats.Dds;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// armour-pieces-1125 round 6 (the round-5 re-gate's per-piece fixes): the per-piece shading speckle clamp, the shape helpers (the Blood Ravens chevron, the Corvus
/// laurel bands), the havoc pads' hue split, crisp regions and the brightness entries. Synthetic textures only (no original bytes).
/// </summary>
[Collection(BuiltMaskStatics.Name)]
public sealed class BuiltMaskRound6Tests
{
    const int W = 64, H = 64;

    static PaintMaskBuilder.MaskOnly AllPrimary(int w = W, int h = H)
    {
        var m = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) m[i * 4] = 255;
        return new PaintMaskBuilder.MaskOnly(w, h, m, 100f, 80f, 62f, 1f, 0f, 0f);
    }

    static DdsImage Grey(byte v, int w = W, int h = H) => new(w, h, Enumerable.Range(0, w * h * 4).Select(i => i % 4 == 3 ? (byte)255 : v).ToArray());

    static List<MaskRegionOverride> Regions(string package) => BuiltMaskOverrides.For(package, null).SelectMany(o => o.Regions).ToList();

    [Fact]
    public void A_smaller_speckle_share_holds_a_thin_pale_line_closer_to_the_plate_and_the_default_is_unchanged()
    {
        // A plate at luminance 100 with one pale column (150): a crackle line one texel wide.
        var dif = Grey(100);
        for (int y = 0; y < H; y++) { int i = (y * W + 30) * 4; dif.Rgba[i] = dif.Rgba[i + 1] = dif.Rgba[i + 2] = 150; }
        // Region statistics on (the clamp runs only with them): one group per channel at the plate's median.
        var medians = Enumerable.Repeat(float.NaN, 3 * PaintMaskBuilder.StatGroups).ToArray();
        medians[0] = 100f;
        var m = AllPrimary() with { Group = new byte[W * H], GroupMedians = medians };
        var t = new PaintMaskBuilder.Targets(100f, 100f, 62f);
        int Lum(BuiltPaintMask b, int x) => b.Diffuse[(32 * W + x) * 4];
        var general = PaintMaskBuilder.WithGrey(dif, m, t);
        var tight = PaintMaskBuilder.WithGrey(dif, m, t with { Speckle = 0.06f });
        Assert.True(Lum(general, 30) > Lum(general, 20) + 15, $"general line {Lum(general, 30)} plate {Lum(general, 20)}");
        Assert.True(Lum(tight, 30) < Lum(general, 30) - 10, $"tight line {Lum(tight, 30)} general {Lum(general, 30)}");
        Assert.True(Lum(tight, 30) <= Lum(tight, 20) + 8, $"tight line {Lum(tight, 30)} plate {Lum(tight, 20)}");
        // Speckle 0 is the general clamp: the same bytes.
        Assert.Equal(general.Diffuse, PaintMaskBuilder.WithGrey(dif, m, t with { Speckle = 0f }).Diffuse);
    }

    [Fact]
    public void The_round_6_entries_stay_in_bounds_and_carry_their_brightness()
    {
        Assert.All(BuiltMaskOverrides.All, e => Assert.InRange(e.Speckle, 0f, 0.2f));
        Assert.All(BuiltMaskOverrides.All, e => Assert.InRange(e.Brightness, 0.5f, 1.5f));
        Assert.All(BuiltMaskOverrides.All.SelectMany(e => e.Regions), r =>
        {
            Assert.InRange(r.U0, 0f, 1f); Assert.InRange(r.U1, 0f, 1f); Assert.InRange(r.V0, 0f, 1f); Assert.InRange(r.V1, 0f, 1f);
            Assert.True(r.U0 < r.U1 && r.V0 < r.V1, $"empty rectangle {r}");
        });
        static float Scale(string p) => BuiltMaskOverrides.For(p, null).Aggregate(1f, (a, o) => a * o.Brightness);
        static float Contrast(string p) => BuiltMaskOverrides.For(p, null).Aggregate(1f, (a, o) => a * o.Contrast);
        Assert.Equal(1.4f, Scale("csm_armour_raptor_torso"), 3);
        Assert.Equal(0.82f, Scale("csm_armour_basic_shldrl"), 3);
        Assert.Equal(1.08f, Scale("sm_armour_blacktemp_backpack"), 3);
        Assert.Equal(1.08f, Scale("sm_armour_black_temp_backpack"), 3);
        Assert.Equal(0.5f, Contrast("sm_armour_mp_dlc2_set2_bp"), 2);
        Assert.Equal(0.5f, Contrast("sm_armour_mp_dlc2_set2_shldrr"), 2);
        // The passing berserker's gilded left pad is untouched by the basic pad's entry.
        Assert.Equal(1f, Scale("csm_armour_berserker_shldrl"), 3);
    }

    [Fact]
    public void The_blood_ravens_chevron_is_tertiary_by_shape_including_its_light_scratches()
    {
        // A bone dome (luminance 200, primary) whose every texel is light: only the arrow's core rectangles (no filter) take it.
        var applied = BuiltMaskOverrides.Apply(AllPrimary(256, 512), Grey(200, 256, 512), Regions("sm_armour_blood_ravens_shldrr"));
        byte T(float u, float v) => applied.Mask[((int)(v * 512) * 256 + (int)(u * 256)) * 4 + 2];
        Assert.Equal(255, T(0.508f, 0.15f));    // the head's middle
        Assert.Equal(255, T(0.508f, 0.25f));    // the stem
        Assert.Equal(255, T(0.40f, 0.19f));     // near the head's base corner
        Assert.Equal(0, T(0.40f, 0.12f));       // beside the apex: the bone field
        Assert.Equal(0, T(0.20f, 0.25f));       // far from the arrow
    }

    [Fact]
    public void The_corvus_laurel_bands_follow_their_diagonals()
    {
        var applied = BuiltMaskOverrides.Apply(AllPrimary(512, 512), Grey(120, 512, 512), Regions("sm_armour_corvus_helmet").Where(r => r.Filter is null).ToList());
        byte T(float u, float v) => applied.Mask[((int)(v * 512) * 512 + (int)(u * 512)) * 4 + 2];
        Assert.Equal(255, T(0.50f, 0.15f));     // the left band's middle (from U 0.39 at the top to U 0.61 at the bottom)
        Assert.Equal(255, T(0.81f, 0.15f));     // the right band's middle
        Assert.Equal(0, T(0.65f, 0.15f));       // between the bands
        Assert.Equal(0, T(0.40f, 0.25f));       // left of the left band's lower end
    }

    [Fact]
    public void The_havoc_pads_split_their_dome_by_hue_gold_tertiary_red_primary()
    {
        // Left half olive-gold (warm yellow), right half dark red; below the dome untouched.
        var dif = Grey(0);
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4;
            (dif.Rgba[i], dif.Rgba[i + 1], dif.Rgba[i + 2]) = x < W / 2 ? ((byte)150, (byte)140, (byte)60) : ((byte)70, (byte)25, (byte)25);
        }
        var m = AllPrimary();
        for (int i = 0; i < W * H; i++) { m.Mask[i * 4] = 128; m.Mask[i * 4 + 2] = 127; }   // a blend everywhere
        var applied = BuiltMaskOverrides.Apply(m, dif, Regions("csm_armour_havoc_shldrl"));
        Assert.Equal(255, applied.Mask[(5 * W + 10) * 4 + 2]);     // gold: tertiary, wholly
        Assert.Equal(0, applied.Mask[(5 * W + 10) * 4]);
        Assert.Equal(255, applied.Mask[(5 * W + 50) * 4]);         // red: primary, wholly
        Assert.Equal(0, applied.Mask[(5 * W + 50) * 4 + 2]);
    }

    [Fact]
    public void The_salamanders_right_pad_is_crisp_soft_outlines_go_whole()
    {
        var m = AllPrimary();
        m.Mask[(10 * W + 10) * 4] = 170;   // a soft chip outline texel: two thirds painted
        var applied = BuiltMaskOverrides.Apply(m, Grey(90), Regions("sm_armour_mp_dlc2_set2_shldrr"));
        Assert.Equal(255, applied.Mask[(10 * W + 10) * 4]);
    }
}
