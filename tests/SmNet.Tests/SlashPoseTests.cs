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

    // ---- anim/air-slash-character: one curve from the dive's start to the strike at the hit ----
    const float Hit = 0.1333f;

    [Fact]
    public void The_strike_curve_reaches_the_strike_at_the_hit_and_never_runs_backwards()
    {
        foreach (float dive in new[] { 0.13f, 0.2f, 0.267f, 0.3f })
        {
            float sync = SlashPose.StrikeSync(Crop, Hit), prev = -1f;
            Assert.False(SlashPose.DataPlayback(dive, Crop, Crop, Hit));
            Assert.Equal(0f, SlashPose.StrikeDiveTime(0f, dive, Crop, Crop, Hit));
            for (float t = 0f; t <= dive + 1e-4f; t += 1f / 120f)
            {
                float c = SlashPose.StrikeDiveTime(t, dive, Crop, Crop, Hit);
                Assert.True(c >= prev - 1e-5f && c <= Crop + 1e-5f, $"dive {dive}: {c} at {t}");
                prev = c;
            }
            // The landing as expected: the clip goes on from where the dive left it, at its rate, to the sync point at the hit, then at the clip's rate.
            float landed = SlashPose.StrikeDiveTime(dive, dive, Crop, Crop, Hit), rate = SlashPose.StrikeDiveRate(dive, dive, Crop, Crop, Hit);
            Assert.True(landed < Crop, $"dive {dive}: the sword is still rising at the landing ({landed})");
            Assert.Equal(landed, SlashPose.StrikeImpactTime(0f, landed, rate, Crop, Hit, Length), 4);
            Assert.Equal(sync, SlashPose.StrikeImpactTime(Hit, landed, rate, Crop, Hit, Length), 4);
            Assert.Equal(sync + 0.2f, SlashPose.StrikeImpactTime(Hit + 0.2f, landed, rate, Crop, Hit, Length), 4);
            Assert.Equal(Length, SlashPose.StrikeImpactTime(5f, landed, rate, Crop, Hit, Length));
            for (float s = 0f; s <= Hit + 0.05f; s += 1f / 120f)
            {
                float c = SlashPose.StrikeImpactTime(s, landed, rate, Crop, Hit, Length);
                Assert.True(c >= prev - 1e-5f, $"dive {dive}: back to {c} at impact+{s}");
                prev = c;
            }
            // The same curve across the landing: no step in time or rate (the first 1/60 s after it moves as the dive did).
            float step = SlashPose.StrikeImpactTime(1f / 60f, landed, rate, Crop, Hit, Length) - landed;
            Assert.InRange(step * 60f, rate * 0.8f, rate * 1.2f + 0.05f);
        }
    }

    [Fact]
    public void The_strike_curve_plays_the_wind_up_slower_than_the_feel_pass_did()
    {
        // A 0.2 s dive: the feel pass fitted the whole 0.4667 s wind-up into it (a middle rate near 3); the strike curve spreads it to the hit (at most about 2).
        float dive = 0.2f, peakOld = 0f, peakNew = 0f, dt = 1f / 240f;
        for (float t = 0f; t < dive; t += dt)
        {
            peakOld = MathF.Max(peakOld, (SlashPose.SmoothDiveTime(t + dt, dive, Crop) - SlashPose.SmoothDiveTime(t, dive, Crop)) / dt);
            peakNew = MathF.Max(peakNew, (SlashPose.StrikeDiveTime(t + dt, dive, Crop, Crop, Hit) - SlashPose.StrikeDiveTime(t, dive, Crop, Crop, Hit)) / dt);
        }
        Assert.True(peakOld > 2.8f, $"feel pass peak {peakOld}");
        Assert.True(peakNew < 2.3f, $"strike curve peak {peakNew}");
    }

    [Fact]
    public void A_dive_long_enough_plays_the_data_way()
    {
        // dive + hit at least the sync point (0.6 s): the wind-up at the clip's rate, held at its end; the landing from the impact's crop start (the data's sequencers).
        Assert.True(SlashPose.DataPlayback(0.5f, Crop, Crop, Hit));
        Assert.Equal(0.3f, SlashPose.StrikeDiveTime(0.3f, 0.5f, Crop, Crop, Hit), 4);
        Assert.Equal(Crop, SlashPose.StrikeDiveTime(0.49f, 0.5f, Crop, Crop, Hit), 4);
        Assert.Equal(Crop + 0.05f, SlashPose.StrikeImpactTime(0.05f, Crop, 0f, Crop, Hit, Length), 4);
        // No crops or no hit time: the data's playback too.
        Assert.True(SlashPose.DataPlayback(0.2f, 0f, Crop, Hit));
        Assert.True(SlashPose.DataPlayback(0.2f, Crop, Crop, 0f));
        Assert.True(SlashPose.DataPlayback(float.PositiveInfinity, Crop, Crop, Hit));
        Assert.Equal(0f, SlashPose.StrikeDiveTime(0.1f, 0.2f, 0f, Crop, Hit));
    }

    [Fact]
    public void A_landing_sooner_than_expected_catches_up_by_the_hit()
    {
        // Expected 0.3 s, landed at 0.15 s: the strike still meets the hit, never runs backwards.
        float landed = SlashPose.StrikeDiveTime(0.15f, 0.3f, Crop, Crop, Hit), rate = SlashPose.StrikeDiveRate(0.15f, 0.3f, Crop, Crop, Hit), prev = landed;
        for (float s = 0f; s <= Hit; s += 1f / 120f)
        {
            float c = SlashPose.StrikeImpactTime(s, landed, rate, Crop, Hit, Length);
            Assert.True(c >= prev - 1e-5f); prev = c;
        }
        Assert.Equal(SlashPose.StrikeSync(Crop, Hit), SlashPose.StrikeImpactTime(Hit, landed, rate, Crop, Hit, Length), 4);
    }
}
