using System;
using System.Linq;
using System.Numerics;
using SmContent.Extras.Musou;

namespace SmFormats.Tests;

/// <summary>
/// The lightning claw's and the power spear's strike animations (docs/claw-spear-animations.md): every clip's length and hit time is the borrowed strike's at the design's tempo
/// (the sword's at 1.25x, the hammer's at 0.85x), the keys are in time order within each clip, no clip names a leg bone (the feet stay planted), and the easing curves land on their keys.
/// </summary>
public class ClawSpearClipsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // Spec "Power sword X1..X4" and "Thunder hammer X1..X3" (lengths and hits, s) and smtool melee-steps for the sword's Y backhand.
    [Theory]
    [InlineData("claw_x1", 1.600f, 0.320f, 1.25f)]
    [InlineData("claw_x2", 1.433f, 0.287f, 1.25f)]
    [InlineData("claw_x3", 1.767f, 0.433f, 1.25f)]
    [InlineData("claw_x4", 1.667f, 0.533f, 1.25f)]
    [InlineData("claw_heavy", 1.5f, 0.33f, 1.25f)]
    [InlineData("spear_hew1", 2.667f, 0.604f, 0.85f)]
    [InlineData("spear_hew2", 2.333f, 0.567f, 0.85f)]
    [InlineData("spear_hew3", 2.333f, 0.700f, 0.85f)]
    [InlineData("spear_stun", 1.667f, 0.433f, 0.85f)]   // the hammer's Y interrupt SM_MEL_TH_Ytap_ATK
    [InlineData("spear_stab", 3.0f, 1.1f, 0.85f)]       // the hammer's Y,Y slam SM_MEL_TH_YATK_Displace2
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
    public void The_landing_shows_the_impale_only_on_a_hit_and_otherwise_the_spear_comes_up_beside_him()
    {
        // Owner via the lead, 2026-11-11: the point-down impale only when the landing impaled an Ork; otherwise the plain landing (ours: the impale's own body and recovery hands).
        Assert.Same(ClawSpearClips.SpearImpale, ClawSpearClips.SpearLanding(impaled: true));
        Assert.Same(ClawSpearClips.SpearLand, ClawSpearClips.SpearLanding(impaled: false));
        var land = ClawSpearClips.SpearLand; var impale = ClawSpearClips.SpearImpale;
        Assert.Equal(impale.BaseClip, land.BaseClip);
        Assert.Equal(impale.Duration, land.Duration);
        Assert.True(float.IsNaN(land.HitTime));   // nothing is struck
        // The same landing below the arms: the back and head are the impale's, the hips too until they ease out of the lean before the release.
        foreach (var ch in impale.Channels.Where(c => c.Kind != PoseChannelKind.Aim && c.Bone != "RIG_Pelvis")) Assert.Contains(ch, land.Channels);
        var hips = land.Channels.Single(c => c.Bone == "RIG_Pelvis"); var impaleHips = impale.Channels.Single(c => c.Bone == "RIG_Pelvis");
        Assert.Equal(impaleHips.Sample(ClawSpearClips.ImpaleDrive), hips.Sample(ClawSpearClips.ImpaleDrive));
        Assert.True(hips.Sample(ClawSpearClips.LandRelax).Pitch < impaleHips.Sample(ClawSpearClips.LandRelax).Pitch - 10f, "the plain landing comes out of the lean before the release");
        // The same touch-down (the dive's hands, the hands up the shaft by the drive), but no hold: free by LandFree, the left hand off and the carry's arms by LandRest; the impale holds to 0.85 s.
        Assert.Equal(ClawSpearClips.ImpaleGrip, land.GripPointAt(ClawSpearClips.ImpaleDrive), 3);
        Assert.Equal(WeaponPoseClip.LowGrip, land.GripPointAt(ClawSpearClips.LandFree), 3);
        Assert.Equal(ClawSpearClips.ImpaleGrip, impale.GripPointAt(0.5f), 3);
        Assert.Equal(0f, land.GripAt(ClawSpearClips.LandRest), 3);
        Assert.Equal(1f, impale.GripAt(0.5f), 3);
        Assert.True(ClawSpearClips.LandRest < ClawSpearClips.ImpaleHeld);
        foreach (var ch in land.Channels.Where(c => c.Kind == PoseChannelKind.Aim))
        {
            var dive = ClawSpearClips.SpearDive.Channels.Single(c => c.Bone == ch.Bone).Sample(0f);
            var touch = impale.Channels.Single(c => c.Bone == ch.Bone).Sample(ClawSpearClips.ImpaleDrive);
            Assert.Equal(dive.Elevation, ch.Sample(0f).Elevation, 3);
            Assert.Equal(dive.Azimuth, ch.Sample(0f).Azimuth, 3);
            Assert.Equal(touch.Elevation, ch.Sample(ClawSpearClips.ImpaleDrive).Elevation, 3);
            Assert.Equal(touch.Azimuth, ch.Sample(ClawSpearClips.ImpaleDrive).Azimuth, 3);
        }
    }

    [Fact]
    public void The_jump_packs_flight_with_the_spear_is_left_as_it_was()
    {
        // Owner, 2026-11-11: "The flying when holding w (after holding space bar jump long) animation is the only piece that I don't want touched". No spear clip shows in that flight: the
        // client (NetworkView.SpearPoseOf) lays a spear clip only over the stances (the carry, nothing posed), the pound's dive loop and landing (NetworkView.Jump's DiveClipNames, the
        // SM_JMP_GP_Loop*Degrees clips, and the impact), and a strike; the flight is posed with the SM_JMP_Zero* clips by the vertical angle (JumpFlightPose) and the spear rides W3's own mount
        // there. What the flight reads is pinned here as on main (2026-11-11): the flight pose's numbers, W3's mount points, no spear clip authored over a flight clip, the dive only over the
        // pound's dive loop. The pound's dive itself is not the flight (review of 2026-11-11): its keys are pinned below at the values of the palms pass (they differ from main's by that pass).
        foreach (var clip in ClawSpearClips.All) Assert.DoesNotContain("SM_JMP_Zero", clip.BaseClip, StringComparison.OrdinalIgnoreCase);
        foreach (var clip in ClawSpearClips.All) Assert.DoesNotContain("_Aim_", clip.BaseClip, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ClawSpearClips.SpearDiveLoop, ClawSpearClips.SpearDive.BaseClip);
        Assert.Equal("SM_JMP_TH_GP_Loop_01", ClawSpearClips.SpearDiveLoop);
        var dive = ClawSpearClips.SpearDive.Channels.Where(c => c.Kind == PoseChannelKind.Aim).ToDictionary(c => c.Bone, c => c.Sample(0.5f));
        (string Bone, float El, float Az)[] pinnedDive = [("RIG_R_UpperArm", 9f, 14f), ("RIG_R_Forearm", 46f, -27f), ("RIG_L_UpperArm", -30f, 41f), ("RIG_L_Forearm", 33f, 76f)];
        foreach (var (bone, el, az) in pinnedDive) { Assert.Equal(el, dive[bone].Elevation, 3); Assert.Equal(az, dive[bone].Azimuth, 3); }
        Assert.Equal(-6f, ClawSpearClips.SpearDive.Channels.Single(c => c.Bone == "RIG_Pelvis").Sample(0.5f).Pitch, 3);
        Assert.Equal(1.34f, SpearHold.MountHighFromButt);
        Assert.Equal(0.45f, SpearHold.MountLowFromButt);
        Assert.Equal(1.05f, SpearHold.AxisFromButt);
        float[] got =
        [
            SmContent.JumpFlightPose.Target(8f, 6f), SmContent.JumpFlightPose.Target(0f, 6f), SmContent.JumpFlightPose.Target(-10f, 4f), SmContent.JumpFlightPose.Target(3f, 0f),
            SmContent.JumpFlightPose.Damp(0.2f, 0.8f, 0.05f), SmContent.JumpFlightPose.Time(0.5f, 2f), SmContent.JumpFlightPose.Time(1f, 1.5f),
        ];
        output.WriteLine(string.Join(", ", got.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
        float[] main = FlightPins;
        for (int i = 0; i < main.Length; i++) Assert.Equal(main[i], got[i], 5);
        Assert.True(SmContent.JumpFlightPose.Low(5f, false) == FlightLowPins[0] && SmContent.JumpFlightPose.Low(30f, true) == FlightLowPins[1]);
    }

    /// <summary>The flight pose's numbers on main (2026-11-11), for <see cref="The_jump_packs_flight_with_the_spear_is_left_as_it_was"/>.</summary>
    static readonly float[] FlightPins = [0.20483276f, 0.5f, 0.8788811f, 0f, 0.3700812f, 1f, 1.5f];
    static readonly bool[] FlightLowPins = [true, false];   // within about 13.5 m of the ground the low set; far above it, not

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

    /// <summary>The strokes of spear pass 2 (Blender-authored from the squared hip carry): their start, end and shape are pinned by <c>SpearPass2Tests</c>; the checks here pin the hand-keyed clips of before.</summary>
    static bool Authored(WeaponPoseClip c) => c.Description.Contains("authored in Blender", StringComparison.Ordinal);

    [Fact]
    public void Claw_strikes_return_to_the_stance_at_their_end()
    {
        foreach (var clip in ClawSpearClips.All.Where(c => !float.IsNaN(c.HitTime) && c.Weapon == "claw"))
            foreach (var ch in clip.Channels)
            {
                var end = ch.Sample(clip.Duration);
                if (ch.Kind is PoseChannelKind.Turn or PoseChannelKind.Move or PoseChannelKind.Foot) Assert.True(end.IsZero, $"{clip.Name} {ch.Bone}: a {ch.Kind} left at the end");
                else Assert.True(end.Weight <= 1e-4f, $"{clip.Name} {ch.Bone}: an aim still weighted at the end");
            }
    }

    [Fact]
    public void Spear_sweeps_and_the_impale_return_to_the_carry_at_their_end()
    {
        // The impale too since the no-clipping pass (2026-11-10): the carry follows the pound, so the clip ends on its arms with the left hand off (at the low grip: the landing still crouches him)
        // rather than on the landing's arms.
        // Since the second follow-up pass (2026-11-11) the carry is read in the facing frame and the strikes over the hammer idle in its stance frame, 71 degrees right of the facing: the same
        // arms are the carry's azimuths less ClawSpearClips.IdleHipsTurn.
        Assert.True(ClawSpearClips.SpearRest.FacingFrame);
        var rest = ClawSpearClips.SpearRestOld.Channels.Where(c => c.Kind == PoseChannelKind.Aim).ToDictionary(c => c.Bone, c => { var s = c.Sample(0f); return s with { Yaw = s.Yaw - ClawSpearClips.IdleHipsTurn }; });
        Assert.Equal(4, rest.Count);
        Assert.Equal(0f, ClawSpearClips.SpearImpale.GripAt(ClawSpearClips.SpearImpale.Duration), 3);
        Assert.Equal(WeaponPoseClip.LowGrip, ClawSpearClips.SpearImpale.GripPointAt(ClawSpearClips.SpearImpale.Duration), 3);
        Assert.Equal(0f, ClawSpearClips.SpearLand.GripAt(ClawSpearClips.SpearLand.Duration), 3);
        Assert.Equal(WeaponPoseClip.LowGrip, ClawSpearClips.SpearLand.GripPointAt(ClawSpearClips.SpearLand.Duration), 3);
        // (Spear pass 2: the pound's landings are left as they were and still end on the older carry's arms; the 0.14 s hand-over to the new carry covers the difference, SpearClearanceTests.)
        foreach (var clip in ClawSpearClips.SpearStrikes.Where(c => !Authored(c)).Append(ClawSpearClips.SpearImpale).Append(ClawSpearClips.SpearLand))
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
        foreach (var ch in ClawSpearClips.SpearHew1Original.Channels.Where(c => c.Kind == PoseChannelKind.Aim))
        {
            var start = ch.Sample(0f); var want = rest[ch.Bone];
            Assert.Equal(want.Elevation, start.Elevation, 3);
            Assert.Equal(want.Azimuth, start.Azimuth, 3);
        }
    }

    [Fact]
    public void The_rest_carries_the_spear_held_out_in_the_right_hand_and_the_sweeps_take_the_left_hand_on_and_off()
    {
        // Owner 2026-11-10: "right hand carries, both swing", and (review of the wired stills) "the spear should be held out away from the body at idle and when running": at rest the right
        // upper arm is held out to the side and the forearm forward and out, both below the horizontal (the grip track 0: the spear leans from that hand, SpearHold.Place); the left arm hangs free.
        // (The older carry's contract; spear pass 2's carry is pinned by SpearPass2Tests.)
        var rest = ClawSpearClips.SpearRestOld.Channels.Where(c => c.Kind == PoseChannelKind.Aim).ToDictionary(c => c.Bone, c => c.Sample(0f));
        Assert.InRange(rest["RIG_R_UpperArm"].Azimuth, 30f, 120f);   // 45 since the crosshair pass (2026-11-11): out to the player's right over the side-on hammer idle
        Assert.InRange(rest["RIG_R_UpperArm"].Elevation, -60f, -10f);
        Assert.InRange(rest["RIG_R_Forearm"].Azimuth, 20f, 90f);
        Assert.InRange(rest["RIG_R_Forearm"].Elevation, -45f, 0f);
        Assert.True(rest["RIG_L_UpperArm"].Elevation < -45f && rest["RIG_L_Forearm"].Elevation < -30f);
        Assert.Equal(0f, ClawSpearClips.SpearRest.GripAt(0.7f));
        foreach (var sweep in ClawSpearClips.SpearStrikes)
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
    public void The_spear_keeps_a_straight_back_and_leans_and_turns_from_the_hips()
    {
        // Owner 2026-11-10: "the torso should remain intact and not bend much - straight back the entire time". The spine's own keys stay small; the pelvis carries the lean and the turn.
        foreach (var clip in ClawSpearClips.All.Where(c => c.Weapon == "spear"))
        {
            foreach (var ch in clip.Channels.Where(c => c.Bone is "RIG_SpineA" or "RIG_SpineD"))
                foreach (var k in ch.Keys)
                {
                    Assert.True(MathF.Abs(k.Pitch) <= ClawSpearClips.SpineLimitPitch, $"{clip.Name} {ch.Bone}: spine pitch {k.Pitch} at {k.T}");
                    Assert.True(MathF.Abs(k.Roll) <= ClawSpearClips.SpineLimitRoll, $"{clip.Name} {ch.Bone}: spine roll {k.Roll} at {k.T}");
                    Assert.True(MathF.Abs(k.Yaw) <= ClawSpearClips.SpineLimitYaw, $"{clip.Name} {ch.Bone}: spine yaw {k.Yaw} at {k.T}");
                }
            if (!float.IsNaN(clip.HitTime) && clip.HitTime > 0f)
            {
                var pelvis = clip.Channels.Single(c => c.Kind == PoseChannelKind.Turn && c.Bone == "RIG_Pelvis");
                Assert.True(pelvis.Keys.Max(k => MathF.Abs(k.Yaw)) >= 25f, $"{clip.Name}: the hips barely turn");
                var drop = clip.Channels.Single(c => c.Kind == PoseChannelKind.Move);
                Assert.True(drop.Keys.Min(k => k.Up) <= (clip.Name == "spear_stun" ? -0.15f : -0.25f), $"{clip.Name}: no deep stance");   // the stun is a quick snap cut; the hews and the stab sink deep
            }
        }
    }

    [Fact]
    public void The_shaft_slides_from_the_high_carry_to_the_low_melee_grip_and_back()
    {
        // Owner 2026-11-10: gripped high toward the blade when running, slid low toward the butt for melee.
        Assert.Equal(WeaponPoseClip.CarryGrip, ClawSpearClips.SpearRest.GripPointAt(0.5f));
        // (Spear pass 2: hew 3 is a chained stroke like hew 2, starting with the left hand on the shaft at the melee grip; a start from the carry reaches it in the hand-over.)
        foreach (var (first, melee) in new[] { (ClawSpearClips.SpearHew1, WeaponPoseClip.HewGrip), (ClawSpearClips.SpearHew3, WeaponPoseClip.HewGrip), (ClawSpearClips.SpearStun, WeaponPoseClip.LowGrip) }.Where(x => !Authored(x.Item1) || x.Item1.Name != "spear_hew3"))   // the strikes begun from the carry (hew 3 too since pass 3)
        {
            Assert.Equal(WeaponPoseClip.CarryGrip, first.GripPointAt(0f));
            Assert.Equal(melee, first.GripPointAt(ClawSpearClips.GripSlide), 3);
            Assert.Equal(melee, first.GripPointAt(first.HitTime), 3);
            Assert.Equal(WeaponPoseClip.CarryGrip, first.GripPointAt(first.Duration), 3);
            if (!float.IsNaN(first.NextAt)) Assert.Equal(melee, first.GripPointAt(first.NextAt), 3);   // a chained strike takes over the melee grip
        }
        foreach (var chained in new[] { ClawSpearClips.SpearHew2 }.Concat(Authored(ClawSpearClips.SpearHew3) ? [ClawSpearClips.SpearHew3] : Array.Empty<WeaponPoseClip>()))
        {
            Assert.Equal(WeaponPoseClip.HewGrip, chained.GripPointAt(0f), 3);
            Assert.Equal(WeaponPoseClip.CarryGrip, chained.GripPointAt(chained.Duration), 3);
        }
        Assert.True(ClawSpearClips.SpearStab.GripPointAt(ClawSpearClips.SpearStab.HitTime) < WeaponPoseClip.LowGrip, "the stab holds the shaft at the butt for the reach");
        Assert.Equal(WeaponPoseClip.LowGrip, ClawSpearClips.SpearDive.GripPointAt(0.5f));   // no track: the melee grip
        Assert.Equal(0.627f, ClawSpearClips.SpearStun.NextAt, 3);   // F, F: the hammer's BRANCH_To_Slam window opens at 0.533 s, at the spear's tempo
    }

    [Fact]
    public void Every_spear_sweep_steps_and_moves_the_pelvis_and_the_feet_come_back()
    {
        foreach (var sweep in ClawSpearClips.SpearStrikes)
        {
            Assert.Contains(sweep.Channels, c => c.Kind == PoseChannelKind.Move && c.Bone == "RIG_Pelvis");
            Assert.Contains(sweep.Channels, c => c.Kind == PoseChannelKind.Foot);
            Assert.Contains(sweep.Channels, c => c.Kind == PoseChannelKind.Turn && c.Bone == "RIG_Pelvis");
        }
        // The spin ends the string: both feet are back on their spots at its end.
        foreach (var foot in ClawSpearClips.SpearHew3.Channels.Where(c => c.Kind == PoseChannelKind.Foot))
            Assert.True(foot.Sample(ClawSpearClips.SpearHew3.Duration).IsZero);
    }

    [Fact]
    public void Strikes_snap_onto_the_hit_and_wind_up_before_it()
    {
        foreach (var clip in ClawSpearClips.All.Where(c => c.Weapon is "claw" or "spear" && c.HitTime > 0f))
        {
            var arm = clip.Channels.First(c => c.Kind == PoseChannelKind.Aim && c.Bone.EndsWith("UpperArm"));
            Assert.Contains(arm.Keys, k => MathF.Abs(k.T - clip.HitTime) < 1e-4f);   // a key exactly at the hit
            // A Blender-authored clip (AuthoredPoseClip) is sampled at 60 Hz and read back linearly: its gathering and snap are in the sampled values (SpearAuthoredHewTests pins the
            // acceleration), so its apex key is a key at the right time and its keys ease Linear; the hand-keyed clips gather with an In key.
            bool sampled = Authored(clip);
            Assert.Contains(arm.Keys, k => MathF.Abs(k.T - clip.Anticipation) < 1e-4f && (sampled || k.Ease == PoseEase.In));   // the wind-up gathers
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
    [Fact]
    public void The_grip_points_are_metres_from_the_butt_and_map_onto_W3s_shaft()
    {
        // The grip contract (review of 2026-11-10): a clip says where the right hand holds the shaft in metres from the butt; W3's model puts the hand at that point of its own shaft.
        Assert.Equal(0.95f, WeaponPoseClip.CarryGrip);
        Assert.Equal(0.5f, WeaponPoseClip.LowGrip);
        Assert.Equal(0.3f, WeaponPoseClip.StabGrip);
        Assert.Equal(WeaponPoseClip.CarryGrip, ClawSpearClips.SpearRest.GripPointAt(0.7f), 3);
        Assert.Equal(WeaponPoseClip.HewGrip, ClawSpearClips.SpearHew1.GripPointAt(ClawSpearClips.SpearHew1.HitTime), 3);   // pass 3: near the butt for the wide hews
        Assert.Equal(WeaponPoseClip.StabGrip, ClawSpearClips.SpearStab.GripPointAt(ClawSpearClips.SpearStab.HitTime), 3);
        foreach (var clip in ClawSpearClips.All.Where(c => c.Weapon == "spear"))
            for (float t = 0f; t <= clip.Duration; t += 0.05f)
                Assert.InRange(clip.GripPointAt(t), 0.05f, SpearHold.CollarFromButt);   // always on the shaft, between the butt and the collar
        // W3's axis (its model's origin 1.05 m up the shaft): the clips' points, and its own mount's run and melee grips (WeaponRig.SpearGripHigh 0.29, SpearGripLow -0.60).
        Assert.Equal(-0.10f, SpearHold.AxisAt(WeaponPoseClip.CarryGrip), 3);
        Assert.Equal(-0.55f, SpearHold.AxisAt(WeaponPoseClip.LowGrip), 3);
        Assert.Equal(-0.75f, SpearHold.AxisAt(WeaponPoseClip.StabGrip), 3);
        Assert.Equal(0.29f, SpearHold.AxisAt(SpearHold.MountHighFromButt), 3);
        Assert.Equal(-0.60f, SpearHold.AxisAt(SpearHold.MountLowFromButt), 3);
        // W3's own slide (0 its run grip, 1 its low grip) for a point: where the mount takes over when a clip stops holding the hand.
        Assert.Equal(0f, SpearHold.SlideAt(SpearHold.MountHighFromButt), 3);
        Assert.Equal(1f, SpearHold.SlideAt(SpearHold.MountLowFromButt), 3);
        Assert.Equal((1.34f - 0.95f) / (1.34f - 0.45f), SpearHold.SlideAt(WeaponPoseClip.CarryGrip), 3);
        Assert.Equal(1f, SpearHold.SlideAt(WeaponPoseClip.StabGrip));   // past the low grip: clamped
    }

    [Fact]
    public void The_spear_sits_on_the_hands_with_the_right_hand_at_the_grip_point_and_the_edge_forward()
    {
        var up = Vector3.UnitZ; var front = Vector3.UnitY;
        var right = new Vector3(0.3f, 0.1f, 0.97f); var left = new Vector3(0.1f, 0.6f, 1.25f);
        // Both hands on: the shaft on the line through them toward the left hand (the blade beyond it), the right hand the grip point from the butt, the edge square to the shaft and forward.
        var (butt, shaft, edge) = SpearHold.Place(right, left, 1f, WeaponPoseClip.LowGrip, front, up);
        Assert.Equal(WeaponPoseClip.LowGrip, Vector3.Distance(butt, right), 3);
        Assert.True(Vector3.Dot(shaft, Vector3.Normalize(left - right)) > 0.9999f);
        Assert.Equal(0f, Vector3.Dot(edge, shaft), 3);
        Assert.True(Vector3.Dot(edge, front) > 0f);
        // Carried in the right hand alone at the carry grip (owner's review of 2026-11-10: "held out away from the body", "the blade is facing away from the character model"): the shaft
        // CarryElevation above the horizontal and CarryAzimuth from the front toward the body's right (here front +Y, up +Z: the right is +X), the butt 0.95 m down the shaft, behind and below
        // the hand and not inside it (toward the legs); the edge faces out and forward (EdgeAzimuth), square to the shaft.
        var (carryButt, carry, carryEdge) = SpearHold.Place(right, left, 0f, WeaponPoseClip.CarryGrip, front, up);
        var bodyRight = Vector3.Cross(front, up);
        Assert.Equal(Vector3.UnitX, bodyRight);
        Assert.Equal(SpearHold.CarryElevation, MathF.Asin(Vector3.Dot(carry, up)) * 180f / MathF.PI, 1);
        Assert.Equal(SpearHold.CarryAzimuth, MathF.Atan2(Vector3.Dot(carry, bodyRight), Vector3.Dot(carry, front)) * 180f / MathF.PI, 1);
        Assert.True(Vector3.Dot(carry, front) > 0.2f && carry.Z > 0.85f, "the carried spear leans forward from nearly upright, the blade ahead of the shoulder pad");
        Assert.Equal(WeaponPoseClip.CarryGrip, Vector3.Distance(carryButt, right), 3);
        Assert.True(carryButt.Z < right.Z && Vector3.Dot(carryButt - right, front) < 0f && Vector3.Dot(carryButt - right, bodyRight) >= 0f, "the butt hangs behind and below the hand, not in toward the legs");
        Assert.Equal(0f, Vector3.Dot(carryEdge, carry), 3);
        Assert.True(Vector3.Dot(carryEdge, bodyRight) > 0.8f && Vector3.Dot(carryEdge, front) > 0f, "the carried blade faces away from the body: out and forward");
        // At the low grip with the left hand off, the shaft stands upright; half on, it lies between the carry and the two-hand line.
        var (_, upright, _) = SpearHold.Place(right, left, 0f, WeaponPoseClip.LowGrip, front, up);
        Assert.True(Vector3.Dot(upright, up) > 0.9999f);
        var (_, half, _) = SpearHold.Place(right, left, 0.5f, WeaponPoseClip.LowGrip, front, up);
        Assert.True(Vector3.Dot(half, up) < 0.9999f && Vector3.Dot(half, Vector3.Normalize(left - right)) < 0.9999f);
    }

    [Fact]
    public void Every_strike_of_the_hammer_combo_has_a_spear_clip_shown_on_its_own_hit()
    {
        // W3's combo is the hammer's: light X1..X3, heavies Y, X,Y, X,X,Y, then the Y,Y slam's two sides (MeleeTuning.ThunderHammer).
        Assert.Same(ClawSpearClips.SpearHew1, ClawSpearClips.SpearStrike(false, 1));
        Assert.Same(ClawSpearClips.SpearHew2, ClawSpearClips.SpearStrike(false, 2));
        Assert.Same(ClawSpearClips.SpearHew3, ClawSpearClips.SpearStrike(false, 3));
        Assert.Null(ClawSpearClips.SpearStrike(false, 0));
        Assert.Null(ClawSpearClips.SpearStrike(false, 4));
        foreach (int heavy in new[] { 1, 2, 3 }) Assert.Same(ClawSpearClips.SpearStun, ClawSpearClips.SpearStrike(true, heavy));
        foreach (int slam in new[] { 4, 5 }) Assert.Same(ClawSpearClips.SpearStab, ClawSpearClips.SpearStrike(true, slam));
        Assert.Null(ClawSpearClips.SpearStrike(true, 6));
        // A clip on the strike it was authored for shows at the strike's own time.
        var hew = ClawSpearClips.SpearHew1;
        Assert.Equal(0.4f, hew.TimeAt(0.4f, hew.HitTime), 4);
        Assert.Equal(hew.HitTime, hew.TimeAt(hew.HitTime, hew.HitTime), 4);
        Assert.Equal(hew.Duration, hew.TimeAt(hew.Duration + 2f, hew.HitTime), 4);
        // On another strike's hit (the sweeping attacks with the snap cut): the wind-up fitted so the hits meet, the follow-through at the clip's own rate, never before 0 or past the end.
        var stun = ClawSpearClips.SpearStun;
        const float other = 0.8f;
        Assert.Equal(stun.HitTime, stun.TimeAt(other, other), 4);
        Assert.Equal(stun.HitTime * 0.5f, stun.TimeAt(other * 0.5f, other), 4);
        Assert.Equal(stun.HitTime + 0.3f, stun.TimeAt(other + 0.3f, other), 4);
        Assert.Equal(stun.Duration, stun.TimeAt(100f, other), 4);
        Assert.Equal(0f, stun.TimeAt(-1f, other));
    }
}
