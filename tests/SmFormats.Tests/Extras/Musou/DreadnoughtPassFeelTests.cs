using SmContent.Extras.Musou;
using Xunit;

namespace SmFormats.Tests;

/// <summary>The Dreadnought pass of 2026-11-17 (owner): a screen shake on his light melee hits and on the cannon's sustained fire, both scaled by the Camera shake option and off with Reduce motion.</summary>
public class DreadnoughtPassFeelTests
{
    [Fact]
    public void A_light_swings_hit_shakes_the_own_camera_for_a_fifth_of_a_second_and_is_a_smaller_blow_than_the_stomp()
    {
        var hit = DreadnoughtFeel.ArmHit;
        Assert.Equal(0.1f, hit.Duration);
        Assert.InRange(hit.Total, 0.2f, 0.25f);
        Assert.Contains(hit, DreadnoughtFeel.Shakes);
        Assert.True(hit.Pitch.Amplitude < DreadnoughtFeel.Stomp.Pitch.Amplitude && hit.Vertical.Amplitude < DreadnoughtFeel.Stomp.Vertical.Amplitude);
        Assert.Equal(1f, hit.Gain(0f));   // the own camera
        var (offset, degrees) = hit.Sample(0.05f, 1f);
        Assert.True(offset.Length() > 0f || degrees.Length() > 0f);
        Assert.Equal((System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero), hit.Sample(hit.Total + 0.1f, 1f));
    }

    [Fact]
    public void The_shakes_follow_the_Camera_shake_option_and_Reduce_motion()
    {
        var one = new[] { (DreadnoughtFeel.ArmHit, 0.05f, 0f, false) };
        var full = DreadnoughtFeel.Mix(one, 1f, false);
        var half = DreadnoughtFeel.Mix(one, 0.5f, false);
        Assert.True(full.Degrees.Length() > 0f);
        Assert.Equal(full.Degrees.Length() * 0.5f, half.Degrees.Length(), 4);
        Assert.Equal(System.Numerics.Vector3.Zero, DreadnoughtFeel.Mix(one, 0f, false).Degrees);
        Assert.Equal(System.Numerics.Vector3.Zero, DreadnoughtFeel.Mix(one, 1f, true).Degrees);
        var sustained = DreadnoughtFeel.CannonSustained.Sample(1.234f, 1f * DreadnoughtFeel.Scale(0f, false));
        Assert.Equal((System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero), sustained);
    }

    [Fact]
    public void The_sustained_fire_builds_over_0_6_s_of_rounds_and_falls_in_0_3_s_after_the_last()
    {
        float k = 0f;
        for (int i = 0; i < 30; i++) k = DreadnoughtFeel.SustainRamp(k, true, 1f / 60f);   // half a second at 60 frames
        Assert.InRange(k, 0.8f, 0.85f);
        for (int i = 0; i < 8; i++) k = DreadnoughtFeel.SustainRamp(k, true, 1f / 60f);
        Assert.InRange(k, 0.999f, 1f);
        for (int i = 0; i < 10; i++) k = DreadnoughtFeel.SustainRamp(k, false, 1f / 30f);
        Assert.Equal(0f, k);
        Assert.Equal(0f, DreadnoughtFeel.SustainRamp(0.5f, false, 0f) - 0.5f);   // no time, no change
        Assert.InRange(DreadnoughtFeel.SustainGapSeconds, 0.2f, 0.3f);   // the cannon's rounds come 0.1 s apart: two missed rounds end the run
    }

    [Fact]
    public void The_sustained_shake_is_a_steady_rumble_that_never_ends_and_stays_small_next_to_the_stomp()
    {
        var s = DreadnoughtFeel.CannonSustained;
        Assert.Equal(1f, s.Envelope(0f)); Assert.Equal(1f, s.Envelope(500f));
        float peak = 0f, peakPitch = 0f;
        for (int i = 0; i < 600; i++) { var (o, d) = s.Sample(i / 60f, 1f); peak = MathF.Max(peak, o.Length()); peakPitch = MathF.Max(peakPitch, MathF.Abs(d.X)); }
        Assert.True(peak > 0.005f && peakPitch > 0.1f, $"peak offset {peak}, pitch {peakPitch}");
        Assert.True(peakPitch <= DreadnoughtFeel.Stomp.Pitch.Amplitude / 2f && peak < DreadnoughtFeel.Stomp.Vertical.Amplitude / 4f);
        Assert.Equal(1f, s.Gain(30f));   // own camera, wherever
    }
}
