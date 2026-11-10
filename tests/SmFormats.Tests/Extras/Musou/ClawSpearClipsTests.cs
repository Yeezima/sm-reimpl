using System;
using System.Linq;
using SmContent.Extras.Musou;

namespace SmFormats.Tests;

/// <summary>
/// The lightning claw's and the power spear's strike animations (docs/claw-spear-animations.md): every clip's length and hit time is the borrowed strike's at the design's tempo
/// (the sword's at 1.25x, the hammer's at 0.85x), the keys are in time order within each clip, no clip names a leg bone (the feet stay planted), and the easing curves land on their keys.
/// </summary>
public class ClawSpearClipsTests
{
    // Spec "Power sword X1..X4" and "Thunder hammer X1..X3" (lengths and hits, s) and smtool melee-steps for the sword's Y backhand.
    [Theory]
    [InlineData("claw_x1", 1.600f, 0.320f, 1.25f)]
    [InlineData("claw_x2", 1.433f, 0.287f, 1.25f)]
    [InlineData("claw_x3", 1.767f, 0.433f, 1.25f)]
    [InlineData("claw_x4", 1.667f, 0.533f, 1.25f)]
    [InlineData("claw_heavy", 1.5f, 0.33f, 1.25f)]
    [InlineData("spear_forehand", 2.667f, 0.604f, 0.85f)]
    [InlineData("spear_backhand", 2.333f, 0.567f, 0.85f)]
    [InlineData("spear_spin", 2.333f, 0.700f, 0.85f)]
    public void Each_strike_keeps_the_borrowed_timing_at_the_design_tempo(string name, float length, float hit, float tempo)
    {
        var clip = ClawSpearClips.ByName(name) ?? throw new Xunit.Sdk.XunitException($"no clip {name}");
        Assert.Equal(length / tempo, clip.Duration, 4);
        Assert.Equal(hit / tempo, clip.HitTime, 4);
        Assert.True(clip.Anticipation < clip.HitTime && clip.HitTime < clip.Follow && clip.Follow <= clip.Settle && clip.Settle <= clip.Duration, $"{name}: moments out of order");
    }

    [Fact]
    public void The_impale_lands_with_the_landing_and_the_dive_pose_is_held()
    {
        Assert.Equal(0f, ClawSpearClips.SpearImpale.HitTime);
        Assert.Equal(ClawSpearClips.SpearDiveImpact, ClawSpearClips.SpearImpale.BaseClip);
        Assert.Equal(ClawSpearClips.SpearDiveImpactLength, ClawSpearClips.SpearImpale.Duration);
        Assert.True(float.IsNaN(ClawSpearClips.SpearDive.HitTime));
        Assert.Equal(ClawSpearClips.SpearDiveLoop, ClawSpearClips.SpearDive.BaseClip);
    }

    [Fact]
    public void Keys_are_in_time_order_and_within_the_clip()
    {
        foreach (var clip in ClawSpearClips.All)
            foreach (var ch in clip.Channels)
            {
                Assert.True(ch.Keys.Length >= 2, $"{clip.Name} {ch.Bone}: fewer than two keys");
                for (int i = 1; i < ch.Keys.Length; i++) Assert.True(ch.Keys[i].T >= ch.Keys[i - 1].T, $"{clip.Name} {ch.Bone}: key {i} out of order");
                Assert.True(ch.Keys[0].T >= 0f && ch.Keys[^1].T <= clip.Duration + 1e-4f, $"{clip.Name} {ch.Bone}: keys outside the clip");
                if (ch.Kind == PoseChannelKind.Aim) Assert.False(string.IsNullOrEmpty(ch.Toward), $"{clip.Name} {ch.Bone}: an aim channel needs its child bone");
            }
    }

    [Fact]
    public void No_clip_moves_the_legs_or_the_root()
    {
        string[] planted = ["RIG_Root", "RIG_Pelvis", "RIG_L_Thigh", "RIG_R_Thigh", "RIG_L_Calf", "RIG_R_Calf", "RIG_L_Foot", "RIG_R_Foot", "RIG_L_Toe", "RIG_R_Toe"];
        foreach (var clip in ClawSpearClips.All)
            Assert.Empty(clip.Bones.Intersect(planted, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Strikes_return_to_the_stance_at_their_end()
    {
        foreach (var clip in ClawSpearClips.All.Where(c => !float.IsNaN(c.HitTime)))
            foreach (var ch in clip.Channels)
            {
                var end = ch.Sample(clip.Duration);
                if (ch.Kind == PoseChannelKind.Turn) Assert.True(end.IsZero, $"{clip.Name} {ch.Bone}: a turn left at the end");
                else Assert.True(end.Weight <= 1e-4f, $"{clip.Name} {ch.Bone}: an aim still weighted at the end");
            }
    }

    [Fact]
    public void Strikes_snap_onto_the_hit_and_wind_up_before_it()
    {
        foreach (var clip in ClawSpearClips.All.Where(c => c.Weapon is "claw" or "spear" && c.HitTime > 0f))
        {
            var arm = clip.Channels.First(c => c.Kind == PoseChannelKind.Aim && c.Bone.EndsWith("UpperArm"));
            Assert.Contains(arm.Keys, k => MathF.Abs(k.T - clip.HitTime) < 1e-4f);   // a key exactly at the hit
            Assert.Contains(arm.Keys, k => MathF.Abs(k.T - clip.Anticipation) < 1e-4f && k.Ease == PoseEase.In);   // the wind-up gathers
            var hitKey = arm.Keys.First(k => MathF.Abs(k.T - clip.HitTime) < 1e-4f);
            Assert.True(hitKey.Ease is PoseEase.Snap or PoseEase.Linear, $"{clip.Name}: the hit key eases {hitKey.Ease}");
        }
    }

    [Theory]
    [InlineData(PoseEase.Smooth)]
    [InlineData(PoseEase.In)]
    [InlineData(PoseEase.Out)]
    [InlineData(PoseEase.Linear)]
    [InlineData(PoseEase.Snap)]
    [InlineData(PoseEase.Over)]
    [InlineData(PoseEase.Hold)]
    public void Easing_curves_start_at_zero_and_land_on_one(PoseEase ease)
    {
        Assert.Equal(0f, PoseChannel.Ease(ease, 0f), 5);
        Assert.Equal(1f, PoseChannel.Ease(ease, 1f), 5);
        Assert.True(PoseChannel.Ease(PoseEase.Snap, 0.5f) > 0.95f, "the snap has nearly all its travel in the first half");
        Assert.True(PoseChannel.Ease(PoseEase.Over, 0.7f) > 1f, "the follow-through overshoots");
    }

    [Fact]
    public void An_aim_channel_interpolates_its_azimuth_the_short_way_round()
    {
        var ch = new PoseChannel("RIG_R_UpperArm", [PoseKey.Aim(0f, 0f, 170f), PoseKey.Aim(1f, 0f, -170f, PoseEase.Linear)], PoseChannelKind.Aim, "RIG_R_Forearm");
        var mid = ch.Sample(0.5f);
        Assert.True(MathF.Abs(MathF.Abs(mid.Azimuth) - 180f) < 1e-3f, $"azimuth {mid.Azimuth} should pass through 180");
    }
}
