using System;
using System.Linq;
using SmContent.Extras.Musou;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// Spear pass 4b (2026-11-12, claude/spear-pass-4b): the second iteration on the strokes (a still more compact coil, a higher-power acceleration, a deeper carry-through), the carry's torso
/// lean and the rollback switch. Presentation only: every gameplay timing is the strike's, pinned here against pass 4's. The client's hand work (the grip point in the palm, the wrist, the per-finger
/// fit, the left hand brought onto the shaft) needs the install's rig and was checked on the preview stage and in a match (docs/claw-spear-animations.md section 3k).
/// </summary>
public class SpearPass4bTests
{
    static (string Name, WeaponPoseClip P4b, WeaponPoseClip P4)[] Strokes() =>
    [
        ("hew1", ClawSpearClips.SpearHew1Pass4b, ClawSpearClips.SpearHew1Pass4), ("hew2", ClawSpearClips.SpearHew2Pass4b, ClawSpearClips.SpearHew2Pass4),
        ("hew3", ClawSpearClips.SpearHew3Pass4b, ClawSpearClips.SpearHew3Pass4), ("stun", ClawSpearClips.SpearStunPass4b, ClawSpearClips.SpearStunPass4),
        ("stab", ClawSpearClips.SpearStabPass4b, ClawSpearClips.SpearStabPass4),
    ];

    [Fact]
    public void The_switch_picks_pass_4b_or_pass_4()
    {
        bool old = ClawSpearClips.OldPass4b, oldP4 = ClawSpearClips.OldPass4, oldP2 = ClawSpearClips.OldPass2;
        if (!oldP2 && !oldP4) Assert.Same(old ? ClawSpearClips.SpearStabPass4 : ClawSpearClips.SpearStabPass4b, ClawSpearClips.SpearStab);
        if (!oldP2 && !oldP4) Assert.Same(old ? ClawSpearClips.SpearHew1Pass4 : ClawSpearClips.SpearHew1Pass4b, ClawSpearClips.SpearHew1);
    }

    [Fact]
    public void The_strokes_keep_every_gameplay_timing_of_pass_4()
    {
        foreach (var (name, p4b, p4) in Strokes())
        {
            Assert.Equal(p4.Duration, p4b.Duration, 3);
            Assert.Equal(p4.HitTime, p4b.HitTime, 3);
            Assert.Equal(p4.NextAt, p4b.NextAt, 3);
            Assert.Equal(p4.BaseClip, p4b.BaseClip);
            Assert.Equal(p4.Weapon, p4b.Weapon);
            Assert.NotSame(p4, p4b);   // the export was found and accepted (a refused or missing one falls back to pass 4's)
            Assert.True(p4b.Anticipation > p4.Anticipation, $"{name}: the coil's apex is not nearer the hit");
        }
    }

    [Fact]
    public void The_strokes_carry_the_hips_further_through_than_pass_4()
    {
        foreach (var (name, p4b, p4) in Strokes().Where(s => s.Name != "stab"))
        {
            float PelvisAt(WeaponPoseClip c, float t) => c.Channels.First(k => k.Bone == "RIG_Pelvis" && k.Kind == PoseChannelKind.Turn).Sample(t).Yaw;
            float t = p4.Follow + 0.1f;
            Assert.True(MathF.Abs(PelvisAt(p4b, t) - PelvisAt(p4b, p4b.HitTime)) >= MathF.Abs(PelvisAt(p4, t) - PelvisAt(p4, p4.HitTime)) - 1f, $"{name}: the carry-through is not deeper");
        }
    }

    [Fact]
    public void The_carry_leans_the_torso_a_little_forward_and_the_rollback_keeps_pass_4s()
    {
        if (ClawSpearClips.OldPass4b) return;
        float Lean(WeaponPoseClip c) => c.Channels.First(k => k.Bone == "RIG_SpineD" && k.Kind == PoseChannelKind.Turn).Sample(0.5f).Pitch;
        Assert.True(Lean(ClawSpearClips.SpearRestPass4) < Lean(ClawSpearClips.SpearRestPass3) - 1f);   // a negative pitch leans the upper spine forward
        Assert.Equal(ClawSpearClips.Pass4bCarryLean, Lean(ClawSpearClips.SpearRestPass4), 2);
        // the 13 degree side lean that keeps the blade off the crosshair is untouched
        Assert.Equal(13f, SpearHold.Pass3CarryRoll);
    }

    [Fact]
    public void Setting_the_pass_4b_rollback_after_start_up_restores_the_upright_carry()
    {
        float Lean(WeaponPoseClip c) => c.Channels.First(k => k.Bone == "RIG_SpineD" && k.Kind == PoseChannelKind.Turn).Sample(0.5f).Pitch;
        bool was = ClawSpearClips.OldPass4b;
        try
        {
            ClawSpearClips.OldPass4b = false;
            Assert.True(Lean(ClawSpearClips.SpearRestPass4) < -1f);
            ClawSpearClips.OldPass4b = true;
            Assert.Equal(Lean(ClawSpearClips.SpearRestPass3), Lean(ClawSpearClips.SpearRestPass4), 2);
        }
        finally { ClawSpearClips.OldPass4b = was; }
    }
}
