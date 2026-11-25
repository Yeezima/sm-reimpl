using SmContent.Customization;
using SmFormats.Dds;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// armour-pieces-1125 round 7 (the round-6 re-gate's five fixes, region data only): the Black Templars rear shoulder-blade plates, the Corvus crown plate before
/// its laurel bands, and the campaign raptor and basic left pads (no rim ring, a solid skull emblem). Synthetic textures only (no original bytes).
/// </summary>
[Collection(BuiltMaskStatics.Name)]
public sealed class BuiltMaskRound7Tests
{
    /// <summary>Every texel a primary + tertiary blend that is mostly tertiary (the purple patches in test_rgb).</summary>
    static PaintMaskBuilder.MaskOnly Blend(int w, int h)
    {
        var m = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { m[i * 4] = 60; m[i * 4 + 2] = 190; }
        return new PaintMaskBuilder.MaskOnly(w, h, m, 100f, 80f, 62f, 0.25f, 0f, 0.75f);
    }

    static DdsImage Grey(byte v, int w, int h) => new(w, h, Enumerable.Range(0, w * h * 4).Select(i => i % 4 == 3 ? (byte)255 : v).ToArray());

    static List<MaskRegionOverride> Regions(string package) => BuiltMaskOverrides.For(package, null).SelectMany(o => o.Regions).ToList();

    static (byte P, byte S, byte T) At(PaintMaskBuilder.MaskOnly m, float u, float v)
    {
        int o = ((int)(v * m.Height) * m.Width + (int)(u * m.Width)) * 4;
        return (m.Mask[o], m.Mask[o + 1], m.Mask[o + 2]);
    }

    [Theory]
    [InlineData("sm_armour_blacktemp_torso")]
    [InlineData("sm_armour_black_temp_torso")]
    public void The_black_templars_rear_shoulder_blade_plates_are_wholly_plate(string package)
    {
        var applied = BuiltMaskOverrides.Apply(Blend(512, 512), Grey(30, 512, 512), Regions(package));
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(applied, 0.63f, 0.10f));   // the right blade's patch
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(applied, 0.78f, 0.10f));   // the left blade's
        Assert.Equal(((byte)60, (byte)0, (byte)190), At(applied, 0.72f, 0.20f));  // elsewhere on the back: untouched
    }

    [Fact]
    public void The_corvus_crown_plate_loses_its_tertiary_and_the_laurel_bands_keep_theirs()
    {
        // A grey (colourless) crown, so the colour strips take every texel in reach: bands tertiary, the crown between and around them primary, wholly.
        var applied = BuiltMaskOverrides.Apply(Blend(512, 512), Grey(120, 512, 512), Regions("sm_armour_corvus_helmet"));
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(applied, 0.49f, 0.15f));   // the left band (u = 0.368 + 0.829 v)
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(applied, 0.81f, 0.15f));   // the right band (u = 0.934 - 0.834 v)
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(applied, 0.65f, 0.20f));   // the filigree between the bands
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(applied, 0.66f, 0.33f));   // the crown's foot
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(applied, 0.33f, 0.20f));   // outside the left band
        Assert.Equal(((byte)60, (byte)0, (byte)190), At(applied, 0.17f, 0.30f));  // the side vox stud (not the crown): untouched
    }

    [Theory]
    [InlineData("csm_armour_raptor_shldrl")]
    [InlineData("csm_armour_basic_shldrl")]
    public void The_campaign_chaos_left_pads_have_no_rim_ring_and_a_solid_emblem(string package)
    {
        // A dark dome (luminance 20: the colour filters take nothing), so only the shapes count.
        var applied = BuiltMaskOverrides.Apply(Blend(256, 512), Grey(20, 256, 512), Regions(package));
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(applied, 0.02f, 0.43f));   // the old ring at the disc's outer edge: plate
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(applied, 0.95f, 0.30f));   // the dome's run past the ellipse at the crest (the raptor's grey fragment)
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(applied, 0.60f, 0.20f));   // the dome
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(applied, 0.394f, 0.396f)); // the upper skull, its dark sockets included
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(applied, 0.368f, 0.478f)); // the lower skull
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(applied, 0.70f, 0.43f));   // the blade
        Assert.Equal(((byte)60, (byte)0, (byte)190), At(applied, 0.90f, 0.05f));  // a spike above the dome (a separate piece): untouched
    }

    [Fact]
    public void The_berserker_left_pad_keeps_its_round_5_gilded_disc()
    {
        Assert.DoesNotContain(BuiltMaskOverrides.For("csm_armour_berserker_shldrl", null), o => o.Why.Contains("round 7", StringComparison.OrdinalIgnoreCase) || o.Why.Contains("emblem solid"));
        Assert.Equal(0.82f, BuiltMaskOverrides.For("csm_armour_basic_shldrl", null).Aggregate(1f, (a, o) => a * o.Brightness), 3);
    }
}
