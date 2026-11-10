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
        Assert.True(float.IsNaN(ClawSpearClips.SpearRest.HitTime));
        Assert.Equal(ClawSpearClips.SpearIdle, ClawSpearClips.SpearRest.BaseClip);
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
    public void The_legs_are_only_ever_placed_by_their_feet_and_the_root_is_never_moved()
    {
        // Third pass: the pelvis may turn and move and a foot may step (a Foot channel on the foot bone); the thighs, calves and toes are solved, never keyed; the root stays the simulation's.
        string[] solved = ["RIG_L_Thigh", "RIG_R_Thigh", "RIG_L_Calf", "RIG_R_Calf", "RIG_L_Toe", "RIG_R_Toe"];
        foreach (var clip in ClawSpearClips.All)
            foreach (var ch in clip.Channels)
            {
                Assert.NotEqual("RIG_Root", ch.Bone);
                Assert.DoesNotContain(ch.Bone, solved);
                if (ch.Bone is "RIG_L_Foot" or "RIG_R_Foot") Assert.Equal(PoseChannelKind.Foot, ch.Kind);
                if (ch.Kind == PoseChannelKind.Foot) Assert.True(ch.Bone is "RIG_L_Foot" or "RIG_R_Foot", $"{clip.Name}: a foot channel on {ch.Bone}");
                if (ch.Kind == PoseChannelKind.Move) Assert.Equal("RIG_Pelvis", ch.Bone);
            }
    }

    [Fact]
    public void Feet_never_go_below_the_ground_and_every_step_comes_back_to_the_stance()
    {
        foreach (var clip in ClawSpearClips.All)
            foreach (var ch in clip.Channels.Where(c => c.Kind == PoseChannelKind.Foot))
            {
                foreach (var k in ch.Keys) Assert.True(k.Up >= 0f, $"{clip.Name} {ch.Bone}: a foot key below the ground at {k.T}");
                for (float t = 0f; t <= clip.Duration; t += 0.01f) Assert.True(ch.Sample(t).Up >= -1e-3f, $"{clip.Name} {ch.Bone}: the foot dips below the ground at {t:0.00}");
                var end = ch.Sample(clip.Duration);
                Assert.True(MathF.Abs(end.Forward) + MathF.Abs(end.Right) + MathF.Abs(end.Up) < 1e-3f, $"{clip.Name} {ch.Bone}: the foot is not back on its spot at the end");
            }
        foreach (var clip in ClawSpearClips.All.Where(c => c.Weapon == "claw"))
        {
            var pelvis = clip.Channels.Single(c => c.Kind == PoseChannelKind.Move);
            var end = pelvis.Sample(clip.Duration);
            Assert.True(MathF.Abs(end.Forward) + MathF.Abs(end.Right) + MathF.Abs(end.Up) < 1e-3f, $"{clip.Name}: the pelvis is not back at the end");
            Assert.Contains(clip.Channels, c => c.Kind == PoseChannelKind.Foot);   // every claw strike steps
        }
    }

    [Fact]
    public void Chained_claw_strikes_start_with_the_feet_where_the_one_before_planted_them()
    {
        var pairs = new[] { (ClawSpearClips.ClawX1, ClawSpearClips.ClawX2), (ClawSpearClips.ClawX2, ClawSpearClips.ClawX3), (ClawSpearClips.ClawX3, ClawSpearClips.ClawX4) };
        foreach (var (first, next) in pairs)
            foreach (string foot in new[] { "RIG_L_Foot", "RIG_R_Foot" })
            {
                var a = first.Channels.FirstOrDefault(c => c.Bone == foot)?.Sample(first.NextAt) ?? default;
                var b = next.Channels.FirstOrDefault(c => c.Bone == foot)?.Sample(0f) ?? default;
                Assert.True(MathF.Abs(a.Forward - b.Forward) < 1e-3f && MathF.Abs(a.Right - b.Right) < 1e-3f && MathF.Abs(a.Up - b.Up) < 1e-3f, $"{first.Name} -> {next.Name}: {foot} jumps at the chain");
            }
    }

    [Fact]
    public void Claw_strikes_and_the_impale_return_to_the_stance_at_their_end()
    {
        foreach (var clip in ClawSpearClips.All.Where(c => !float.IsNaN(c.HitTime) && !ClawSpearClips.SpearSweeps.Contains(c)))
            foreach (var ch in clip.Channels)
            {
                var end = ch.Sample(clip.Duration);
                if (ch.Kind is PoseChannelKind.Turn or PoseChannelKind.Move or PoseChannelKind.Foot) Assert.True(end.IsZero, $"{clip.Name} {ch.Bone}: a {ch.Kind} left at the end");
                else Assert.True(end.Weight <= 1e-4f, $"{clip.Name} {ch.Bone}: an aim still weighted at the end");
            }
    }

    [Fact]
    public void Spear_sweeps_return_to_the_upright_rest_at_their_end()
    {
        var rest = ClawSpearClips.SpearRest.Channels.Where(c => c.Kind == PoseChannelKind.Aim).ToDictionary(c => c.Bone, c => c.Sample(0f));
        Assert.Equal(4, rest.Count);
        foreach (var clip in ClawSpearClips.SpearSweeps)
            foreach (var ch in clip.Channels)
            {
                var end = ch.Sample(clip.Duration);
                if (ch.Kind is PoseChannelKind.Turn or PoseChannelKind.Move or PoseChannelKind.Foot) { Assert.True(end.IsZero, $"{clip.Name} {ch.Bone}: a {ch.Kind} left at the end"); continue; }
                var want = rest[ch.Bone];
                Assert.Equal(want.Elevation, end.Elevation, 3);
                Assert.Equal(want.Azimuth, end.Azimuth, 3);
                Assert.Equal(1f, end.Weight, 3);
            }
        // The forehand starts from the rest too (the backhand and spin start from the sweep before them).
        foreach (var ch in ClawSpearClips.SpearForehand.Channels.Where(c => c.Kind == PoseChannelKind.Aim))
        {
            var start = ch.Sample(0f); var want = rest[ch.Bone];
            Assert.Equal(want.Elevation, start.Elevation, 3);
            Assert.Equal(want.Azimuth, start.Azimuth, 3);
        }
    }

    [Fact]
    public void The_rest_carries_the_spear_upright_in_the_right_hand_and_the_sweeps_take_the_left_hand_on_and_off()
    {
        // Owner 2026-11-10: "right hand carries, both swing". At rest the right arm hangs with the shaft (the grip track 0: the stand-in stands the shaft upright from that hand) and the left arm hangs free.
        var rest = ClawSpearClips.SpearRest.Channels.Where(c => c.Kind == PoseChannelKind.Aim).ToDictionary(c => c.Bone, c => c.Sample(0f));
        Assert.True(rest["RIG_R_UpperArm"].Elevation < -60f && rest["RIG_R_Forearm"].Elevation < -60f);
        Assert.True(rest["RIG_L_UpperArm"].Elevation < -45f && rest["RIG_L_Forearm"].Elevation < -30f);
        Assert.Equal(0f, ClawSpearClips.SpearRest.GripAt(0.7f));
        foreach (var sweep in ClawSpearClips.SpearSweeps)
        {
            Assert.Equal(1f, sweep.GripAt(sweep.HitTime), 3);          // both hands on the shaft through the hit
            Assert.Equal(1f, sweep.GripAt(sweep.Anticipation), 3);     // the left hand is on by the wind-up's apex
            Assert.Equal(0f, sweep.GripAt(sweep.Duration), 3);         // and off again at the end
            if (!float.IsNaN(sweep.NextAt)) Assert.Equal(1f, sweep.GripAt(sweep.NextAt), 3);   // a chained sweep starts two-handed
        }
        Assert.Equal(1f, ClawSpearClips.SpearDive.GripAt(0.5f));        // the dive and the impale are two-handed
        Assert.Equal(1f, ClawSpearClips.ClawX1.GripAt(0.5f));           // the claw has no grip track
    }

    [Fact]
    public void Every_spear_sweep_steps_and_moves_the_pelvis_and_the_feet_come_back()
    {
        foreach (var sweep in ClawSpearClips.SpearSweeps)
        {
            Assert.Contains(sweep.Channels, c => c.Kind == PoseChannelKind.Move && c.Bone == "RIG_Pelvis");
            Assert.Contains(sweep.Channels, c => c.Kind == PoseChannelKind.Foot);
            Assert.Contains(sweep.Channels, c => c.Kind == PoseChannelKind.Turn && c.Bone == "RIG_Pelvis");
        }
        // The spin ends the string: both feet are back on their spots at its end.
        foreach (var foot in ClawSpearClips.SpearSpin.Channels.Where(c => c.Kind == PoseChannelKind.Foot))
            Assert.True(foot.Sample(ClawSpearClips.SpearSpin.Duration).IsZero);
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
            if (clip.Weapon == "claw")   // the claw's apex is held two frames before the snap (owner's review: the cocked pose must read)
            {
                var held = arm.Keys.First(k => MathF.Abs(k.T - clip.Anticipation - ClawSpearClips.ClawApexHold) < 1e-4f);
                Assert.Equal(PoseEase.Hold, held.Ease);
                Assert.Equal(arm.Sample(clip.Anticipation), arm.Sample(clip.Anticipation + ClawSpearClips.ClawApexHold * 0.5f));
            }
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
