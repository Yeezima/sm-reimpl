using SmNet;

namespace SmNet.Tests;

/// <summary>anim/melee-flow: the clip foot lock's decisions (<see cref="ClipFootLockRules"/>) and the slash's eased catch-up (<see cref="SlashPose"/>).</summary>
public class ClipFootLockRulesTests
{
    [Theory]
    [InlineData(false, 0.01f, 0.2f, true)]     // toe and ankle down: put down
    [InlineData(false, 0.04f, 0.2f, false)]    // the toe not low enough to start a hold
    [InlineData(true, 0.04f, 0.2f, true)]      // ... but low enough to keep one (hysteresis)
    [InlineData(true, 0.05f, 0.2f, false)]     // lifted
    [InlineData(false, 0.01f, 0.25f, false)]   // the heel up: not put down (the probes' flat-foot entry, 0.24 m)
    [InlineData(true, 0.01f, 0.26f, true)]     // a held foot keeps through a small heel lift
    [InlineData(true, 0.01f, 0.28f, false)]
    [InlineData(true, float.NaN, 0.2f, false)]
    public void A_foot_is_down_by_its_toe_and_ankle_with_hysteresis(bool was, float toe, float ankle, bool expected) =>
        Assert.Equal(expected, ClipFootLockRules.Planted(was, toe, ankle));

    [Fact]
    public void A_let_go_foot_reaches_the_clip_on_the_smooth_curve()
    {
        Assert.Equal(1f, ClipFootLockRules.ReleaseHold(0f));
        Assert.Equal(1f, ClipFootLockRules.ReleaseHold(-1f));
        Assert.Equal(0.5f, ClipFootLockRules.ReleaseHold(ClipFootLockRules.ReleaseSeconds / 2f), 4);
        Assert.Equal(0f, ClipFootLockRules.ReleaseHold(ClipFootLockRules.ReleaseSeconds));
        Assert.Equal(0.5f, ClipFootLockRules.ReleaseHold(0.15f, 0.3f), 4);
        Assert.Equal(0f, ClipFootLockRules.ReleaseHold(0.1f, 0f));
        float last = 1f;
        for (float t = 0f; t <= 0.3f; t += 0.005f)
        {
            float h = ClipFootLockRules.ReleaseHold(t, 0.3f);
            Assert.True(h <= last + 1e-6f);
            last = h;
        }
    }

    [Fact]
    public void The_farther_a_held_foot_is_from_the_clips_the_longer_it_takes()
    {
        Assert.Equal(ClipFootLockRules.ReleaseSeconds, ClipFootLockRules.ReleaseDuration(0.05f));   // close: the shortest release
        Assert.Equal(0.25f, ClipFootLockRules.ReleaseDuration(0.5f), 4);                             // 0.5 m at 2 m/s
        Assert.Equal(ClipFootLockRules.MaxReleaseSeconds, ClipFootLockRules.ReleaseDuration(3f));    // capped
        Assert.Equal(ClipFootLockRules.ReleaseSeconds, ClipFootLockRules.ReleaseDuration(float.NaN));
    }

    [Fact]
    public void Only_a_foot_still_in_the_authored_world_starts_a_hold()
    {
        // In the in-place clip a planted foot moves back at the root's speed: still.
        Assert.True(ClipFootLockRules.AuthoredStill(0.1f, -5.9f, 6f));
        // A foot dragged back along the ground while the root hardly moves (the power sword's second strike): not still.
        Assert.False(ClipFootLockRules.AuthoredStill(-3f, -25f, 1.5f));
        // Without a root speed every foot down qualifies; a speed that is not a number does not.
        Assert.True(ClipFootLockRules.AuthoredStill(10f, 10f, float.NaN));
        Assert.False(ClipFootLockRules.AuthoredStill(float.NaN, 0f, 1f));
    }

    [Fact]
    public void A_held_ankle_out_of_reach_is_dragged_on_the_edge_of_the_reach()
    {
        Assert.Equal(0.6f, ClipFootLockRules.Reach(0.6f, 0.9f, out bool inReach));
        Assert.False(inReach);
        Assert.Equal(0.9f * ClipFootLockRules.ReachFraction, ClipFootLockRules.Reach(1.4f, 0.9f, out bool dragged), 5);
        Assert.True(dragged);
        Assert.Equal(0f, ClipFootLockRules.Reach(float.NaN, float.NaN, out bool bad));
        Assert.True(bad);
    }

    [Fact]
    public void The_slash_landing_catches_up_eased_and_never_backwards()
    {
        const float Crop = 0.4667f, Length = 1.733f, Landed = 0.30f;
        Assert.Equal(Landed, SlashPose.ImpactTime(0f, Crop, Length, Landed, SlashPose.EasedCatchUp), 5);   // no pop at the landing
        Assert.Equal(Crop + SlashPose.EasedCatchUp, SlashPose.ImpactTime(SlashPose.EasedCatchUp, Crop, Length, Landed, SlashPose.EasedCatchUp), 5);
        Assert.Equal(Crop + 0.133f, SlashPose.ImpactTime(0.133f, Crop, Length, Landed, SlashPose.EasedCatchUp), 4);   // the hit unchanged
        float LargestStep(float catchUp)
        {
            float last = -1f, step = 0f;
            for (float t = 0f; t < 0.2f; t += 1f / 60f)
            {
                float c = SlashPose.ImpactTime(t, Crop, Length, Landed, catchUp);
                Assert.True(c >= last - 1e-6f);
                if (last >= 0f) step = MathF.Max(step, c - last);
                last = c;
            }
            return step;
        }
        // The graph's 1/30 s catch-up played 0.2 s of the clip in two frames; eased over 0.1 s, at most about 0.067 s of the clip a frame.
        float eased = LargestStep(SlashPose.EasedCatchUp), graph = LargestStep(SlashPose.ImpactCatchUp);
        Assert.True(eased < 0.07f && eased < graph * 0.7f, $"largest step {eased} (graph's {graph})");
        // The graph's own catch-up is unchanged (linear over 1/30 s).
        Assert.Equal(SlashPose.ImpactTime(0.02f, Crop, Length, Landed), SlashPose.ImpactTime(0.02f, Crop, Length, Landed, SlashPose.ImpactCatchUp));
    }
}
