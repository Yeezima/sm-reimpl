using SmContent.Customization;
using SmFormats.Dds;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// armour-pieces-1125 round 4 (the round-3 re-gate's code findings): region statistics from the final mask, highlight ceilings, the one-sided hue filter and the
/// package check. Synthetic textures only (no original bytes).
/// </summary>
[Collection(BuiltMaskStatics.Name)]
public sealed class BuiltMaskRound4Tests
{
    const int W = 32, H = 32;
    static readonly bool[] All = Enumerable.Repeat(true, W * H).ToArray();

    /// <summary>
    /// A "pad": the top half a black plate (luminance about 14, a little noise), the bottom half a bone dome (about 190). The builder's mask (given here) puts
    /// the black plate in primary and leaves the bone baked; region data then moves the bone into primary too.
    /// </summary>
    static (DdsImage Diffuse, PaintMaskBuilder.MaskOnly Builder) BlackPlateBoneDome()
    {
        var dif = new byte[W * H * 4]; var mask = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4, n = (x * 5 + y * 3) % 7;
            byte v = (byte)(y < H / 2 ? 12 + n : 186 + n);
            dif[i] = dif[i + 1] = dif[i + 2] = v; dif[i + 3] = 255;
            if (y < H / 2) mask[i] = 255;
        }
        return (new DdsImage(W, H, dif), new PaintMaskBuilder.MaskOnly(W, H, mask, 14f, 14f, 62f, 0.5f, 0f, 0f));
    }

    static int Lum(BuiltPaintMask m, int x, int y) => m.Diffuse[(y * W + x) * 4];

    [Fact]
    public void Texels_moved_by_region_data_are_shaded_against_their_own_median_not_the_builders()
    {
        // Finding 1: bone texels moved into a black plate's primary were shaded against the black median and hit the dark-plate clamp (flat, 1.5 x the target).
        var (dif, builder) = BlackPlateBoneDome();
        var final = BuiltMaskOverrides.Apply(builder, dif, [new MaskRegionOverride(0f, 0.5f, 1f, 1f, MaskRegion.Primary)], All);
        var targets = new PaintMaskBuilder.Targets(98f, 55f, 62f);
        var stale = PaintMaskBuilder.WithGrey(dif, final, targets);
        var fixedStats = PaintMaskBuilder.WithRegionStats(builder, builder, final, dif);
        var fresh = PaintMaskBuilder.WithGrey(dif, fixedStats, targets);
        // Before: the whole dome at the clamp (147), flat.
        Assert.All(Enumerable.Range(H / 2, H / 2), y => Assert.Equal(147, Lum(stale, 5, y)));
        // After: the dome's median maps to the target and its shading is kept; the black plate still maps to the target as before.
        var dome = Enumerable.Range(H / 2, H / 2).SelectMany(y => Enumerable.Range(0, W).Select(x => Lum(fresh, x, y))).ToList();
        Assert.InRange(dome.Order().ElementAt(dome.Count / 2), 94, 102);
        Assert.True(dome.Max() - dome.Min() >= 2, "the dome keeps its shading");
        Assert.InRange(Lum(fresh, 5, 2), 85, 112);
        // The shares are counted on the final mask (finding 4): every texel primary now.
        Assert.Equal(1f, fixedStats.Primary, 2);
        Assert.Equal(0.5f, final.Primary, 2);   // the region data alone kept the builder's share
    }

    [Fact]
    public void Black_texels_moved_into_a_bright_region_are_not_drawn_near_black()
    {
        // Finding 1, the other way: black rim texels put in a bone secondary were shaded against the bone median (near-black rims).
        var (dif, _) = BlackPlateBoneDome();
        var m = new byte[W * H * 4];
        for (int i = W * H / 2; i < W * H; i++) m[i * 4 + 1] = 255;   // the builder: bone secondary, black baked
        var builder = new PaintMaskBuilder.MaskOnly(W, H, m, 60f, 188f, 62f, 0f, 0.5f, 0f);
        var final = BuiltMaskOverrides.Apply(builder, dif, [new MaskRegionOverride(0f, 0f, 1f, 0.5f, MaskRegion.Secondary)], All);
        var targets = new PaintMaskBuilder.Targets(98f, 55f, 62f);
        Assert.True(Lum(PaintMaskBuilder.WithGrey(dif, final, targets), 5, 2) < 12, "before: near-black");
        var fresh = PaintMaskBuilder.WithGrey(dif, PaintMaskBuilder.WithRegionStats(builder, builder, final, dif), targets);
        Assert.InRange(Lum(fresh, 5, 2), 40, 70);
        Assert.InRange(Lum(fresh, 5, H - 3), 45, 65);
    }

    [Fact]
    public void Two_builder_regions_merged_into_one_channel_keep_their_own_brightness()
    {
        // Finding 3: Permute merged the builder's secondary and tertiary into tertiary keeping only the larger one's median.
        var dif = new byte[W * H * 4]; var m = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4;
            byte v = (byte)(x < 8 ? 40 + (x + y) % 5 : x < 24 ? 120 + (x + y) % 5 : 200 + (x + y) % 5);
            dif[i] = dif[i + 1] = dif[i + 2] = v; dif[i + 3] = 255;
            m[i + (x < 8 ? 1 : x < 24 ? 0 : 2)] = 255;   // dark trim secondary, plate primary, light ornament tertiary
        }
        var diffuse = new DdsImage(W, H, dif);
        var builder = new PaintMaskBuilder.MaskOnly(W, H, m, 122f, 42f, 202f, 0.5f, 0.25f, 0.25f);
        var permuted = PaintMaskBuilder.Permute(builder, [0, 2, 2]);
        var targets = new PaintMaskBuilder.Targets(60f, 60f, 70f);
        var old = PaintMaskBuilder.WithGrey(diffuse, permuted, targets);
        Assert.True(Math.Abs(Lum(old, 2, 5) - Lum(old, 28, 5)) > 30, "before: one median for both (they shade apart)");
        var fresh = PaintMaskBuilder.WithGrey(diffuse, PaintMaskBuilder.WithRegionStats(builder, permuted, permuted, diffuse), targets);
        Assert.InRange(Lum(fresh, 2, 5), 64, 76);
        Assert.InRange(Lum(fresh, 28, 5), 64, 76);
    }

    [Fact]
    public void A_tiny_group_falls_back_to_its_channels_median()
    {
        var (dif, builder) = BlackPlateBoneDome();
        // Only 4 bone texels moved: under MinGroupTexels, so they share the channel's median.
        var final = BuiltMaskOverrides.Apply(builder, dif, [new MaskRegionOverride(0f, 0.5f, 2f / W, 0.5f + 2f / H, MaskRegion.Primary)], All);
        var stats = PaintMaskBuilder.WithRegionStats(builder, builder, final, dif);
        Assert.True(float.IsNaN(stats.GroupMedians![0 * PaintMaskBuilder.StatGroups + 4 + (int)MaskRegion.Baked]));
        Assert.False(float.IsNaN(stats.GroupMedians![0 * PaintMaskBuilder.StatGroups + 0]));
    }

    [Fact]
    public void The_highlight_shoulder_never_passes_the_ceiling_and_leaves_values_under_the_knee()
    {
        // Finding 2: a soft ceiling at the regular piece's 99th percentile.
        Assert.Equal(90f, PaintMaskBuilder.Shoulder(90f, 98f, 121f));
        float knee = 98f + (121f - 98f) * PaintMaskBuilder.ShoulderKnee;
        Assert.Equal(knee, PaintMaskBuilder.Shoulder(knee, 98f, 121f), 3);
        Assert.True(PaintMaskBuilder.Shoulder(294f, 98f, 121f) < 121f);
        Assert.True(PaintMaskBuilder.Shoulder(150f, 98f, 121f) > PaintMaskBuilder.Shoulder(130f, 98f, 121f), "monotonic");
        Assert.Equal(200f, PaintMaskBuilder.Shoulder(200f, 98f, 0f));   // no ceiling: unchanged
        // A bright, flat dome (ratio up to 3 x) with the Space Marine pad targets stays under the ceiling; the round-3 targets (no ceiling) clipped.
        var dif = new byte[W * H * 4]; var m = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) { byte v = (byte)(60 + i % 120); dif[i * 4] = dif[i * 4 + 1] = dif[i * 4 + 2] = v; dif[i * 4 + 3] = 255; m[i * 4] = 255; }
        var mask = new PaintMaskBuilder.MaskOnly(W, H, m, 60f, 60f, 62f, 1f, 0f, 0f);
        var pad = BakedArmourPaint.LuminanceTargets("sm_armour_mp_master_002_shldrr");
        Assert.Equal(121f, pad.PrimaryCeiling);
        var capped = PaintMaskBuilder.WithGrey(new DdsImage(W, H, dif), mask, pad);
        var round3 = PaintMaskBuilder.WithGrey(new DdsImage(W, H, dif), mask, new PaintMaskBuilder.Targets(pad.Primary, pad.Secondary, pad.Tertiary));
        Assert.True(Enumerable.Range(0, W * H).Max(i => capped.Diffuse[i * 4]) <= 121);
        Assert.True(Enumerable.Range(0, W * H).Max(i => round3.Diffuse[i * 4]) >= 250);
        // The per-piece brightness scale scales the ceilings too; the restore switch has none.
        Assert.Equal(pad.PrimaryCeiling * 0.8f, pad.Scaled(0.8f).PrimaryCeiling, 3);
        try
        {
            BakedArmourPaint.OldBrightness = true;
            Assert.Equal(0f, BakedArmourPaint.LuminanceTargets("sm_armour_mp_master_002_shldrr").PrimaryCeiling);
        }
        finally { BakedArmourPaint.OldBrightness = false; }
        Assert.Equal(106f, BakedArmourPaint.LuminanceTargets("csm_armour_basic_shldrl").Primary);
    }

    [Fact]
    public void Specks_join_the_region_around_them_and_thin_trims_stay()
    {
        // A primary plate with a 2 x 2 tertiary speck, a single baked hole, and a 2-texel secondary trim line across it.
        var m = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int o = (y * W + x) * 4;
            if (y is 20 or 21) m[o + 1] = 255;                              // trim
            else if (x is 8 or 9 && y is 8 or 9) m[o + 2] = 255;            // speck
            else if (x == 24 && y == 6) { }                                 // hole (baked)
            else m[o] = 255;
        }
        var mask = new PaintMaskBuilder.MaskOnly(W, H, m, 80f, 80f, 60f, 0.9f, 0.06f, 0.004f);
        var d = PaintMaskBuilder.Despeckle(mask, All);
        byte At(int x, int y, int c) => d.Mask[(y * W + x) * 4 + c];
        Assert.Equal((255, 0, 0), (At(8, 8, 0), At(8, 8, 1), At(8, 8, 2)));
        Assert.Equal(255, At(24, 6, 0));
        Assert.Equal((0, 255), (At(5, 20, 0), At(5, 20, 1)));            // the trim is kept
        Assert.Equal(255, At(5, 5, 0));
        Assert.Same(d, PaintMaskBuilder.Despeckle(d, All));                // nothing left to change
    }

    [Fact]
    public void The_padding_past_the_uv_islands_takes_the_nearest_covered_texel()
    {
        // A covered island x < 10 painted primary with grey 90; outside it the baked yellow padding.
        var mask = new byte[W * H * 4]; var dif = new byte[W * H * 4];
        var covered = new bool[W * H];
        for (int i = 0; i < W * H; i++)
        {
            bool inside = i % W < 10;
            covered[i] = inside;
            if (inside) { mask[i * 4] = 255; dif[i * 4] = dif[i * 4 + 1] = dif[i * 4 + 2] = 90; }
            else { dif[i * 4] = 230; dif[i * 4 + 1] = 190; dif[i * 4 + 2] = 20; }
            dif[i * 4 + 3] = 255;
        }
        var built = new BuiltPaintMask(W, H, mask, dif, 0.3f, 0f, 0f);
        var padded = PaintMaskBuilder.PadOutside(built, covered);
        int Px(int x, int c) => padded.Diffuse[(5 * W + x) * 4 + c];
        int last = 10 + PaintMaskBuilder.PadTexels - 1;
        Assert.Equal((90, 90), (Px(last, 0), Px(last, 2)));
        Assert.Equal(255, padded.Mask[(5 * W + last) * 4]);
        Assert.Equal(230, Px(10 + PaintMaskBuilder.PadTexels, 0));                   // past the padding: unchanged
        Assert.Equal(built.Diffuse, PaintMaskBuilder.PadOutside(built, null).Diffuse); // no coverage known: unchanged
    }

    [Fact]
    public void A_contrast_scale_brings_the_shading_toward_the_target()
    {
        var dif = new byte[W * H * 4]; var m = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) { byte v = (byte)(60 + i % 40); dif[i * 4] = dif[i * 4 + 1] = dif[i * 4 + 2] = v; dif[i * 4 + 3] = 255; m[i * 4] = 255; }
        var mask = new PaintMaskBuilder.MaskOnly(W, H, m, 80f, 80f, 62f, 1f, 0f, 0f);
        var full = PaintMaskBuilder.WithGrey(new DdsImage(W, H, dif), mask, new PaintMaskBuilder.Targets(80f, 80f, 62f));
        var half = PaintMaskBuilder.WithGrey(new DdsImage(W, H, dif), mask, new PaintMaskBuilder.Targets(80f, 80f, 62f) { Contrast = 0.5f });
        int i0 = 0;   // luminance 60: grey 60 before, 70 at half contrast
        Assert.InRange((int)full.Diffuse[i0 * 4], 59, 61);
        Assert.InRange((int)half.Diffuse[i0 * 4], 69, 71);
        Assert.All(BuiltMaskOverrides.All, e => Assert.InRange(e.Contrast, 0.3f, 1f));
        // Round 5: the Black Templars legs keep 0.7 (with the broad-shading removal; was 0.5 alone).
        Assert.Equal(0.7f, BuiltMaskOverrides.For("sm_armour_blacktemp_legl", null).Aggregate(1f, (a, o) => a * o.Contrast), 3);
    }

    [Fact]
    public void A_hue_filter_with_one_bound_is_a_plain_bound()
    {
        // Finding 5: one of MinHue / MaxHue alone was ignored.
        Assert.False(new TexelFilter(MinHue: 1f).Matches(100f, 0.5f, 20f));
        Assert.True(new TexelFilter(MinHue: 1f).Matches(100f, 1.5f, 20f));
        Assert.False(new TexelFilter(MaxHue: -1f).Matches(100f, 0f, 20f));
        Assert.True(new TexelFilter(MaxHue: -1f).Matches(100f, -2f, 20f));
        Assert.True(new TexelFilter(MinHue: 2.6f, MaxHue: -1f).Matches(100f, 3f, 20f));   // a band through pi still wraps
        Assert.False(new TexelFilter(MinHue: 2.6f, MaxHue: -1f).Matches(100f, 0f, 20f));
    }

    [Fact]
    public void Region_data_naming_a_missing_package_is_reported()
    {
        // Finding 6.
        var all = BuiltMaskOverrides.All.Select(o => o.Package).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Empty(BuiltMaskOverrides.MissingPackages(_ => true));
        Assert.Equal(all.Count, BuiltMaskOverrides.MissingPackages(_ => false).Count);
        Assert.Equal([all[0]], BuiltMaskOverrides.MissingPackages(p => !p.Equals(all[0], StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void The_region_stats_switch_reads_the_command_line_or_the_environment()
    {
        // Round 4: built materials are painted with the regular armour's specular strength; a full set's plain material keeps its own.
        var builtMat = new SmFormats.Bod.BmatMaterial("m", "character_wargear", new Dictionary<string, string> { ["DiffuseMap"] = "d", ["PaintMask"] = "p", [BakedArmourPaint.PaintDiffuseKey] = "g" });
        var plainMat = builtMat with { Textures = new Dictionary<string, string> { ["DiffuseMap"] = "d" } };
        Assert.True(BakedArmourPaint.UsesRegularSpecular(builtMat));
        Assert.False(BakedArmourPaint.UsesRegularSpecular(plainMat));
        try
        {
            BakedArmourPaint.OldRegionStats = true;
            Assert.True(BakedArmourPaint.OldRegionStats);
            BakedArmourPaint.OldRegionStats = false;
            Assert.False(BakedArmourPaint.OldRegionStats);
        }
        finally { BakedArmourPaint.OldRegionStats = false; }
        Assert.Equal(5, BakedArmourPaint.BuilderVersion);
    }
}
