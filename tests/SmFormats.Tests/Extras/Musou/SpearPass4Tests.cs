using System;
using System.Linq;
using SmContent;
using SmContent.Extras.Musou;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// Spear pass 4 (2026-11-12, claude/spear-pass-4): the carry's left arm from the bolt pistol's idle and a runner's arm pump, the fingers' grip, and the five strokes re-authored for weight (a
/// compact coil, a higher-power acceleration, the hips carried through after the hit). Presentation only: every timing is the strike's (the adapter refuses otherwise), pinned here too.
/// The overlay's install-only parts (the pistol idle's local rotations, the fingers' curl) run in the client; they were checked on the preview stage and in a match (docs/claw-spear-animations.md section 3j).
/// </summary>
public class SpearPass4Tests
{
    static float PelvisYaw(WeaponPoseClip c, float t) => c.Channels.First(k => k.Bone == "RIG_Pelvis" && k.Kind == PoseChannelKind.Turn).Sample(t).Yaw;

    static (string Name, WeaponPoseClip P4, WeaponPoseClip P2)[] Strokes() =>
    [
        ("hew1", ClawSpearClips.SpearHew1Pass4, ClawSpearClips.SpearHew1Pass2), ("hew2", ClawSpearClips.SpearHew2Pass4, ClawSpearClips.SpearHew2Pass2),
        ("hew3", ClawSpearClips.SpearHew3Pass4, ClawSpearClips.SpearHew3Pass2), ("stun", ClawSpearClips.SpearStunPass4, ClawSpearClips.SpearStunPass2),
        ("stab", ClawSpearClips.SpearStabPass4, ClawSpearClips.SpearStabPass2),
    ];

    [Fact]
    public void The_switch_picks_pass_4_or_the_rollback()
    {
        bool oldP4 = ClawSpearClips.OldPass4, oldP3 = ClawSpearClips.OldPass3, oldP2 = ClawSpearClips.OldPass2;
        if (!oldP2 && !oldP3) Assert.Same(oldP4 ? ClawSpearClips.SpearRestPass3 : ClawSpearClips.SpearRestPass4, ClawSpearClips.SpearRest);
        if (!oldP2) Assert.Same(oldP4 ? ClawSpearClips.SpearStabPass2 : ClawSpearClips.SpearStabPass4, ClawSpearClips.SpearStab);
        Assert.True(ClawSpearClips.Pass3 || oldP2 || oldP3);   // pass 4 keeps pass 3's solves (the flag stays on)
    }

    [Fact]
    public void The_carry_takes_the_left_arm_from_the_pistol_idle_while_standing_and_pumps_it_when_running()
    {
        var rest = ClawSpearClips.SpearRestPass4;
        Assert.Equal(RigClipTable.PistolRelaxedIdle, rest.PistolArmFrom);
        Assert.True(rest.LeftFistWithSpeed);
        Assert.Equal(0f, rest.LeftFistWeightFor(0f));          // standing: the pistol idle's arm shows
        Assert.Equal(1f, rest.LeftFistWeightFor(1f));          // running: the pump
        Assert.Equal(1f, rest.FistWeightFor(0f));              // the right fist is held at the hip in every stance, as pass 3
        // the keys leave the left arm to the layer: weight 0 on both left arm aims (every stroke still fades them in by its own hand-over)
        foreach (var ch in rest.Channels.Where(c => c.Bone is "RIG_L_UpperArm" or "RIG_L_Forearm")) Assert.All(ch.Keys, k => Assert.Equal(0f, k.Weight));
        // pass 3's carry is untouched (the rollback)
        Assert.Null(ClawSpearClips.SpearRestPass3.PistolArmFrom);
        Assert.False(ClawSpearClips.SpearRestPass3.LeftFistWithSpeed);
    }

    [Fact]
    public void The_arms_pump_a_runner_s_arc_the_hand_rises_as_it_swings_forward()
    {
        var rest = ClawSpearClips.SpearRestPass4;
        float forward = rest.SwingForward(-0.8f, 1f, left: true), back = rest.SwingForward(0.8f, 1f, left: true);
        Assert.True(forward > 0.15f && back < -0.15f, $"the left hand swings {forward:F2} / {back:F2} m");
        Assert.True(rest.SwingUp(forward, left: true) > 0.05f);    // up as it comes forward
        Assert.True(rest.SwingUp(back, left: true) < -0.05f);      // down as it goes back
        Assert.Equal(0f, rest.SwingForward(0.8f, 0f, left: true)); // standing: no swing
        Assert.True(MathF.Abs(rest.SwingForward(-0.8f, 1f, left: false)) < MathF.Abs(forward));   // the spear hand carries the shaft: it swings less
    }

    [Fact]
    public void The_re_authored_strokes_keep_every_timing_of_the_strike_and_start_and_end_as_pass_2_did()
    {
        foreach (var (name, p4, p2) in Strokes())
        {
            Assert.NotSame(p2, p4);
            Assert.Equal(p2.Name, p4.Name);
            Assert.Equal(p2.Duration, p4.Duration, 3);
            Assert.Equal(p2.HitTime, p4.HitTime, 3);
            Assert.Equal(p2.NextAt, p4.NextAt);
            Assert.Equal(p2.BaseClip, p4.BaseClip);
            Assert.Contains(p4.Channels.First(c => c.Bone == "RIG_R_UpperArm").Keys, k => MathF.Abs(k.T - p4.HitTime) < 1e-4f);
            // the carry hand-overs: the arms start and end where pass 2's did (the 0.14 s fade to the carry needs the same ends)
            foreach (var ch in p2.Channels.Where(c => c.Kind == PoseChannelKind.Aim))
            {
                var other = p4.Channels.First(c => c.Bone == ch.Bone && c.Kind == ch.Kind);
                var a = ch.Sample(0f); var b = other.Sample(0f);
                Assert.True(MathF.Abs(a.Elevation - b.Elevation) < 1.5f, $"{name} {ch.Bone} starts {a.Elevation} vs {b.Elevation}");
                a = ch.Sample(p2.Duration); b = other.Sample(p4.Duration);
                Assert.True(MathF.Abs(a.Elevation - b.Elevation) < 1.5f && MathF.Abs(a.Azimuth - b.Azimuth) < 1.5f, $"{name} {ch.Bone} ends");
            }
        }
    }

    [Fact]
    public void The_coil_is_more_compact_and_the_hips_go_through_after_the_hit()
    {
        foreach (var (name, p4, p2) in Strokes().Where(s => s.Name != "stab"))
        {
            Assert.True(p4.Anticipation > p2.Anticipation + 0.01f, $"{name}: the apex {p4.Anticipation:F3} s is not nearer the hit than pass 2's {p2.Anticipation:F3} s");   // a shorter wind-up (the coil's apex nearer the hit)
            float after4 = PelvisYaw(p4, p4.HitTime + 0.25f) - PelvisYaw(p4, p4.HitTime), after2 = PelvisYaw(p2, p2.HitTime + 0.25f) - PelvisYaw(p2, p2.HitTime);
            float travel = PelvisYaw(p4, p4.HitTime) - PelvisYaw(p4, p4.Anticipation);
            Assert.True(MathF.Abs(after4) >= MathF.Abs(after2) - 0.5f && MathF.Sign(after4) == MathF.Sign(after2), $"{name}: the hips turn {after4:F1} deg after the hit (pass 2: {after2:F1})");
            Assert.True(MathF.Abs(travel) > 8f, $"{name}: the hips' stroke is {travel:F1} degrees");
        }
    }

    [Fact]
    public void The_strokes_accelerate_harder_into_the_hit_than_pass_2s()
    {
        // The hips' angular speed in the 0.06 s before the hit against the average over the stroke: pass 4's ratio is higher (the stroke is slower out of the coil and faster at the hit).
        foreach (var (name, p4, p2) in Strokes().Where(s => s.Name is "hew1" or "hew3" or "hew2"))
        {
            static float Ratio(WeaponPoseClip c)
            {
                float rateHit = MathF.Abs(PelvisYaw(c, c.HitTime) - PelvisYaw(c, c.HitTime - 0.06f)) / 0.06f;
                float avg = MathF.Abs(PelvisYaw(c, c.HitTime) - PelvisYaw(c, c.Anticipation)) / (c.HitTime - c.Anticipation);
                return rateHit / MathF.Max(avg, 1e-3f);
            }
            Assert.True(Ratio(p4) > Ratio(p2) * 0.95f && Ratio(p4) > 1.3f, $"{name}: speed at the hit over the stroke's average {Ratio(p4):F2} (pass 2: {Ratio(p2):F2})");
        }
    }
}
