using SmNet;

namespace SmNet.Tests;

/// <summary>
/// feel/jump-pack-2 (the owner: "the air slash landing ... plays the beginning of the animation correctly, then performs the air slash backwards"):
/// the slash clip's time through the dive and the landing (<see cref="SlashPose"/>), with the install's crops (the flight plays the clip 0 to
/// 0.4667 s, the impact from 0.4667 s; clip 1.7333 s). The clip time never runs backwards from the dive's start to the clip's end.
/// </summary>
public class SlashPoseTests
{
    const float Crop = 0.4667f, Length = 1.7333f;

    [Fact]
    public void The_dive_plays_the_wind_up_fitted_to_the_dive_and_the_landing_goes_on_from_its_end()
    {
        // 6.5 m above the ground at 25.7 m/s down: about 0.25 s of dive, the wind-up at about 1.84x.
        float expected = SlashPose.ExpectedDive(6.5f, -25.7f);
        Assert.Equal(0.253f, expected, 3);
        Assert.Equal(Crop / expected, SlashPose.DiveRate(expected, Crop), 3);
        Assert.Equal(0f, SlashPose.DiveTime(0f, expected, Crop));
        Assert.Equal(Crop, SlashPose.DiveTime(expected, expected, Crop), 3);   // at the expected landing: the wind-up's end
        Assert.Equal(Crop, SlashPose.DiveTime(1f, expected, Crop));            // a longer dive holds it
        Assert.Equal(Crop, SlashPose.ImpactTime(0f, Crop, Length));            // the landing starts where the wind-up ended
        Assert.Equal(Crop + 0.133f, SlashPose.ImpactTime(0.133f, Crop, Length), 4);   // the hit: clip 0.60 s, the strike coming down
        Assert.Equal(Length, SlashPose.ImpactTime(5f, Crop, Length));
        Assert.Equal(Length - Crop, SlashPose.AfterImpact(Crop, Length), 4);
    }

    [Fact]
    public void Under_the_entry_fade_the_wind_up_runs_at_the_clips_rate_and_the_rest_is_fitted_to_the_rest_of_the_dive()
    {
        const float Fade = 0.125f;
        float expected = 0.25f;
        Assert.Equal(0.1f, SlashPose.DiveTime(0.1f, expected, Crop, Fade), 5);          // under the fade: the clip's own rate
        Assert.Equal(Fade, SlashPose.DiveTime(Fade, expected, Crop, Fade), 5);
        Assert.Equal(Crop, SlashPose.DiveTime(expected, expected, Crop, Fade), 3);       // at the expected landing: the wind-up's end
        float rate = (Crop - Fade) / (expected - Fade);
        Assert.Equal(Fade + 0.02f * rate, SlashPose.DiveTime(Fade + 0.02f, expected, Crop, Fade), 4);
        Assert.Equal(Crop, SlashPose.DiveTime(0.3f, 0.1f, Crop, Fade), 5);               // a dive shorter than the fade: the fastest rate after it, then held
    }

    [Fact]
    public void A_dive_that_lands_before_its_wind_up_ends_eases_to_the_strike_over_the_graphs_slo_mo_blend()
    {
        const float Landed = 0.38f;
        Assert.Equal(Landed, SlashPose.ImpactTime(0f, Crop, Length, Landed), 5);                  // no pop at the landing
        Assert.Equal(Crop + SlashPose.ImpactCatchUp, SlashPose.ImpactTime(SlashPose.ImpactCatchUp, Crop, Length, Landed), 5);   // on the strike's time after 1/30 s
        Assert.Equal(Crop + 0.133f, SlashPose.ImpactTime(0.133f, Crop, Length, Landed), 4);       // the hit unchanged
        float last = Landed;
        for (float t = 0f; t < 0.2f; t += 1f / 240f) { float c = SlashPose.ImpactTime(t, Crop, Length, Landed); Assert.True(c >= last - 1e-6f); last = c; }
        Assert.Equal(Crop, SlashPose.ImpactTime(0f, Crop, Length, Crop + 0.1f));                   // a wind-up at or past the start: no catch-up
    }

    [Theory]
    [InlineData(0.1f)]   // a dive shorter than expected: the landing jumps ahead, never back
    [InlineData(0.25f)]
    [InlineData(0.6f)]
    public void The_clip_time_never_decreases_from_the_dive_to_the_end(float diveLength)
    {
        float expected = SlashPose.ExpectedDive(6.5f, -25.7f), last = -1f;
        for (float t = 0f; t < diveLength; t += 1f / 60f) { float c = SlashPose.DiveTime(t, expected, Crop); Assert.True(c >= last); last = c; }
        for (float t = 0f; t < 2f; t += 1f / 60f) { float c = SlashPose.ImpactTime(t, Crop, Length); Assert.True(c >= last); last = c; }
    }

    [Fact]
    public void Limits_and_the_old_rule_without_crops()
    {
        Assert.Equal(float.PositiveInfinity, SlashPose.ExpectedDive(float.PositiveInfinity, -30f));
        Assert.Equal(1f, SlashPose.DiveRate(float.PositiveInfinity, Crop));
        Assert.Equal(SlashPose.MaxDiveRate, SlashPose.DiveRate(0.01f, Crop));
        Assert.Equal(1f, SlashPose.DiveRate(2f, Crop));
        // No crops read: the dive holds frame 0 and the landing plays from 0 (the feel/jump-pack rule).
        Assert.Equal(0f, SlashPose.DiveTime(0.2f, 0.25f, 0f));
        Assert.Equal(0.2f, SlashPose.ImpactTime(0.2f, 0f, Length));
    }

    [Fact]
    public void The_smooth_wind_up_starts_and_ends_at_the_clip_rate_and_never_steps()
    {
        // anim/feel-pass: a 0.2 s dive (the snappy 40 m/s slash from about 8 m) over the 0.4667 s wind-up.
        const float dive = 0.2f, dt = 1f / 240f;
        Assert.Equal(0f, SlashPose.SmoothDiveTime(0f, dive, Crop));
        Assert.Equal(Crop, SlashPose.SmoothDiveTime(dive, dive, Crop), 4);   // at the expected landing: the wind-up's end
        Assert.Equal(Crop, SlashPose.SmoothDiveTime(1f, dive, Crop));        // a longer dive holds it
        float rateStart = SlashPose.SmoothDiveTime(dt, dive, Crop) / dt;
        float rateEnd = (Crop - SlashPose.SmoothDiveTime(dive - dt, dive, Crop)) / dt;
        Assert.InRange(rateStart, 0.95f, 1.1f);   // the clip's own rate at the dive's start (the flight pose's motion carries on) ...
        Assert.InRange(rateEnd, 0.9f, 1.1f);      // ... and at the landing, where the strike goes on at the clip's rate
        float last = 0f, lastRate = rateStart, maxRate = 0f, maxStep = 0f;
        for (float t = dt; t <= dive; t += dt)
        {
            float x = SlashPose.SmoothDiveTime(t, dive, Crop), rate = (x - last) / dt;
            Assert.True(x >= last - 1e-5f);   // never backwards
            maxRate = MathF.Max(maxRate, rate); maxStep = MathF.Max(maxStep, MathF.Abs(rate - lastRate));
            last = x; lastRate = rate;
        }
        Assert.True(maxRate <= 4f);      // at most MaxDiveRate in the middle
        Assert.True(maxStep < 0.2f);     // the rate changes by small amounts from one 1/240 s step to the next (DiveTime stepped from 1 to 4 at once)
        // A very short dive is fitted over a third of the wind-up (the rest is the landing's catch-up); no ground: the clip's rate.
        Assert.True(SlashPose.SmoothDiveTime(0.05f, 0.05f, Crop) < Crop);
        Assert.Equal(0.1f, SlashPose.SmoothDiveTime(0.1f, float.PositiveInfinity, Crop), 4);
        Assert.Equal(0f, SlashPose.SmoothDiveTime(0.1f, dive, 0f));
    }
}
