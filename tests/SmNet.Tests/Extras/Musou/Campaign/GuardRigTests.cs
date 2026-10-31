using System.Numerics;
using SmContent;
using SmContent.Extras.Musou;
using Xunit;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>TF7a: the Guardsman's clip table and the blends the crowd asks of it.</summary>
public class GuardRigTests
{
    [Fact]
    public void The_clip_table_has_a_clip_for_every_index()
    {
        Assert.Equal(GuardRig.ClipCount, GuardRig.ClipNames.Count);
        Assert.Equal(GuardRig.ClipCount, GuardRig.ClipNames.Distinct().Count());
        Assert.Equal("IG_NAV_Idle_AimForward", GuardRig.ClipNames[GuardRig.AimIdle]);
        Assert.Equal("IG_RNG_Fire_S_01", GuardRig.Profile.Clips.RecoilClip);
        for (int d = 0; d < GuardRig.Deaths; d++) Assert.Contains(GuardRig.ClipNames[GuardRig.FirstDeath + d], GuardRig.Clips.DeathClips);
    }

    [Fact]
    public void The_profile_names_the_coop_rig_and_the_lasgun()
    {
        Assert.Equal("data/chunks/ebp_sets__mp_coop_set_sm.oppc", GuardRig.Profile.Archive);
        Assert.Equal("havok_guardsman_a_coop__hkxx", GuardRig.Profile.Entry);
        Assert.Equal(["ig_coop_lasgun"], GuardRig.Profile.DefaultWargear);
        Assert.Empty(GuardRig.Profile.WeaponMounts);   // the wargear names its nodes: read from the data, not a table of ours
    }

    [Theory]
    [InlineData(0f, 5f, 1f, 0f, 0f, 0f)]
    [InlineData(0f, -5f, 0f, 1f, 0f, 0f)]
    [InlineData(-5f, 0f, 0f, 0f, 1f, 0f)]
    [InlineData(5f, 0f, 0f, 0f, 0f, 1f)]
    [InlineData(0f, 0f, 1f, 0f, 0f, 0f)]
    [InlineData(float.NaN, float.PositiveInfinity, 1f, 0f, 0f, 0f)]
    public void Run_weights_follow_the_direction(float x, float y, float f, float b, float l, float r)
    {
        var w = GuardRig.RunWeights(new Vector2(x, y));
        Assert.Equal((f, b, l, r), w);
    }

    [Fact]
    public void Run_weights_sum_to_one_on_a_diagonal()
    {
        var (f, b, l, r) = GuardRig.RunWeights(new Vector2(3f, 3f));
        Assert.Equal(0.5f, f, 3); Assert.Equal(0.5f, r, 3); Assert.Equal(0f, b); Assert.Equal(0f, l);
    }

    [Fact]
    public void Aim_weights_are_clamped_and_sum_to_one()
    {
        Assert.Equal((0f, 1f, 0f), GuardRig.AimWeights(0f));
        Assert.Equal((1f, 0f, 0f), GuardRig.AimWeights(-5f));
        Assert.Equal((0f, 0f, 1f), GuardRig.AimWeights(5f));
        Assert.Equal((0f, 1f, 0f), GuardRig.AimWeights(float.NaN));
        var (down, level, up) = GuardRig.AimWeights(GuardRig.MaxAimPitch / 2f);
        Assert.Equal(1f, down + level + up, 3);
    }

    [Fact]
    public void The_run_rate_follows_the_speed_and_stays_finite()
    {
        Assert.Equal(1f, GuardRig.RunRate(GuardRig.RunClipSpeed), 3);
        Assert.Equal(0.72f, GuardRig.RunRate(4.5f), 3);
        Assert.Equal(1f, GuardRig.RunRate(float.NaN));
        Assert.Equal(0.3f, GuardRig.RunRate(0f));
    }

    [Fact]
    public void An_event_before_the_records_gives_one_body_and_hides_his_late_live_records()
    {
        var deaths = new GuardDeaths();
        Assert.True(deaths.Died(5, 100));
        Assert.True(deaths.IsGone(5, 97));    // a live record behind the event
        Assert.True(deaths.IsGone(5, 103));   // and one after a live record must not clear it
        Assert.False(deaths.Died(5, 101));    // his Dying record
    }

    [Fact]
    public void An_event_for_a_man_never_seen_gives_one_body()
    {
        var deaths = new GuardDeaths();
        Assert.True(deaths.Died(9, 200));
        Assert.False(deaths.Died(9, 205));
        Assert.True(deaths.IsGone(9, 205));
        Assert.False(deaths.IsGone(10, 205));
    }

    [Fact]
    public void An_event_after_his_records_ended_gives_no_second_body()
    {
        var deaths = new GuardDeaths();
        Assert.True(deaths.Died(5, 150));    // the Dying record came first
        Assert.False(deaths.Died(5, 148));   // the event, late, with its earlier tick
    }

    [Fact]
    public void A_new_man_in_the_slot_shows_after_the_hold_and_can_die()
    {
        var deaths = new GuardDeaths();
        deaths.Died(5, 100);
        uint later = 100 + GuardDeaths.HoldTicks;
        Assert.False(deaths.IsGone(5, later));
        deaths.Forget(later);
        Assert.False(deaths.IsGone(5, 100));   // forgotten
        Assert.True(deaths.Died(5, later));
    }

    [Fact]
    public void A_restarted_server_clock_expires_a_death()
    {
        var deaths = new GuardDeaths();
        deaths.Died(5, 90000);
        Assert.False(deaths.IsGone(5, 10));
        Assert.True(deaths.Died(5, 10));
    }

    [Fact]
    public void A_death_clip_is_a_death_and_the_same_on_every_client()
    {
        var seen = new HashSet<int>();
        for (ushort id = 1; id <= 256; id++)
        {
            int clip = GuardRig.DeathClip(id, 1000u + id);
            Assert.InRange(clip, GuardRig.FirstDeath, GuardRig.ClipCount - 1);
            Assert.Equal(clip, GuardRig.DeathClip(id, 1000u + id));
            seen.Add(clip);
        }
        Assert.Equal(GuardRig.Deaths, seen.Count);
    }
}
