using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SmContent;
using SmContent.Extras.Musou;
using SmFormats.Havok;
using Xunit.Abstractions;

namespace SmFormats.Tests;

/// <summary>
/// Spear pass 3 (owner, 2026-11-12; docs/claw-spear-animations.md section 3i): one carry for standing, walking, running and sprinting in every direction: the spear in the RIGHT hand alone
/// at the side of the hip, the shaft PARALLEL TO THE SPINE (it follows the torso's lean), blade up, butt low; the right arm swings slightly with the stride and the left freely; the hands stay
/// attached to the arms (fixed bone lengths). The strokes are pass 2's. Pinned here: the rollback switch, the carry's data, and, with the install (skipped in CI), the shaft's angle to the
/// spine, the hand's place at the side, the bone lengths of both arms, the swing and the grip on every frame of every hammer stance clip. No original bytes.
/// </summary>
public class SpearPass3Tests(ITestOutputHelper output)
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);
    static readonly System.Globalization.CultureInfo I = System.Globalization.CultureInfo.InvariantCulture;

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the carry's numbers need the install's rig; never in CI)";
        }
    }

    [Fact]
    public void The_switch_picks_between_the_pass_3_carry_and_pass_2s()
    {
        bool oldP2 = ClawSpearClips.OldPass2, oldP3 = ClawSpearClips.OldPass3, oldP4 = ClawSpearClips.OldPass4;
        var expected = oldP2 ? ClawSpearClips.SpearRestOld : oldP3 ? ClawSpearClips.SpearRestPass2 : oldP4 ? ClawSpearClips.SpearRestPass3 : ClawSpearClips.SpearRestPass4;
        Assert.Equal(expected, ClawSpearClips.SpearRest);
        Assert.Equal(!oldP2 && !oldP3, ClawSpearClips.Pass3);
        // the strokes are pass 2's (or pass 4's) in both: the pass 3 rollback changes the carry alone
        Assert.Equal(oldP2 ? ClawSpearClips.SpearHew2Original : oldP4 ? ClawSpearClips.SpearHew2Pass2 : ClawSpearClips.SpearHew2Pass4, ClawSpearClips.SpearHew2);
        // pass 3's rest is pass 2's rest plus the solves: no new keys, the same hand-over channels
        Assert.Equal(ClawSpearClips.SpearRestPass2.Channels.Length, ClawSpearClips.SpearRestPass3.Channels.Length);
        Assert.Null(ClawSpearClips.SpearRestPass2.LeftFistTarget);
        Assert.False(ClawSpearClips.SpearRestPass2.FistAlways);
    }

    [Fact]
    public void The_carry_holds_the_spear_one_handed_at_the_hip_by_a_solve_in_every_stance_and_swings_the_arms()
    {
        var rest = ClawSpearClips.SpearRestPass3;
        Assert.True(rest.FistAlways);
        Assert.True(rest.FistFromPelvis);
        Assert.True(rest.FacingFrame);
        Assert.Equal(1f, rest.FistWeightFor(0f));   // standing too
        Assert.Equal(0f, ClawSpearClips.SpearRestPass2.FistWeightFor(0f));   // the older carry's solve comes in with the speed
        Assert.Equal(0f, rest.GripAt(0.5f));        // the left hand is off the shaft
        Assert.Equal(SpearHold.Pass3CarryGrip, rest.GripPointAt(0.5f));
        Assert.InRange(rest.FistTarget!.Value.X, 0.4f, 0.7f);    // outside the thigh armour (0.34 m from the middle), at the right of the hip
        Assert.InRange(rest.LeftFistTarget!.Value.X, -0.7f, -0.4f);
        Assert.True(rest.FistSwing > 0f && rest.LeftFistSwing > rest.FistSwing, "the right arm swings slightly, the left freely");
        // the right fist swings forward when the left foot is ahead, the left fist the other way, and not at all standing
        Assert.True(rest.SwingForward(0.5f, 1f, left: false) > 0f);
        Assert.True(rest.SwingForward(0.5f, 1f, left: true) < 0f);
        Assert.Equal(0f, rest.SwingForward(0.5f, 0f, left: false));
    }

    [Fact]
    public void The_spear_follows_the_spines_lean_in_the_placement_and_the_old_carry_ignores_it()
    {
        var spine = Vector3.Normalize(new Vector3(0.1f, 0.4f, 0.9f));   // leaning forward (and a little to the side)
        var hand = new Vector3(0.5f, 0.2f, 1.1f);
        float lean(Vector3 v) => MathF.Atan2(v.Y, v.Z) * 180f / MathF.PI;   // forward lean from vertical in the facing's plane
        bool was2 = ClawSpearClips.OldPass2, was3 = ClawSpearClips.OldPass3;
        try
        {
            ClawSpearClips.OldPass2 = false; ClawSpearClips.OldPass3 = false;
            var (butt, shaft, _) = SpearHold.Place(hand, hand + Vector3.UnitY, 0f, SpearHold.Pass3CarryGrip, Vector3.UnitY, Vector3.UnitZ, 0f, spine);
            Assert.InRange(lean(shaft) - lean(spine), -0.01f, 0.01f);   // the fore-and-aft lean is the spine's exactly
            float roll = MathF.Asin(-shaft.X) * 180f / MathF.PI;        // the side lean is fixed, in toward the body's centre line
            Assert.Equal(SpearHold.Pass3CarryRoll, roll, 2);
            Assert.True(shaft.Z > 0f && shaft.Y > 0f, "blade up and forward along the lean");
            Assert.True(Vector3.Distance(butt + shaft * SpearHold.Pass3CarryGrip, hand) < 1e-4f, "the hand is on the shaft at the grip point");
            // a vertical spine: upright but for the side lean
            var (_, up, _) = SpearHold.Place(hand, hand + Vector3.UnitY, 0f, SpearHold.Pass3CarryGrip, Vector3.UnitY, Vector3.UnitZ, 0f, Vector3.UnitZ);
            Assert.InRange(MathF.Acos(Math.Clamp(up.Z, -1f, 1f)) * 180f / MathF.PI, SpearHold.Pass3CarryRoll - 0.01f, SpearHold.Pass3CarryRoll + 0.01f);
            // upright from the low grip, as before
            var (_, upright, _) = SpearHold.Place(hand, hand + Vector3.UnitY, 0f, WeaponPoseClip.LowGrip, Vector3.UnitY, Vector3.UnitZ, 0f, spine);
            Assert.True(upright.Z > 0.9999f);
            // both hands on the shaft: the line through them, whatever the spine
            var (_, held, _) = SpearHold.Place(hand, hand + new Vector3(0f, 0.6f, 0.3f), 1f, WeaponPoseClip.LowGrip, Vector3.UnitY, Vector3.UnitZ, 0f, spine);
            Assert.True(Vector3.Dot(held, Vector3.Normalize(new Vector3(0f, 0.6f, 0.3f))) > 0.9999f);
            ClawSpearClips.OldPass3 = true;
            var (_, older, _) = SpearHold.Place(hand, hand + Vector3.UnitY, 0f, SpearHold.Pass3CarryGrip, Vector3.UnitY, Vector3.UnitZ, 0f, Vector3.UnitZ);   // a vertical spine
            Assert.True(MathF.Abs(lean(older)) > 15f, "the rollback leans the shaft forward as pass 2 did, not along the (vertical) spine");
        }
        finally { ClawSpearClips.OldPass2 = was2; ClawSpearClips.OldPass3 = was3; }
    }

    static List<string> Stances(LocomotionAnimations loco)
    {
        var stances = new List<string> { loco.HammerIdle ?? ClawSpearClips.SpearIdle };
        foreach (var d in loco.Hammer?.Directions ?? []) { if (d.Walk is { } w) stances.Add(w.Clip); if (d.Run is { } r) stances.Add(r.Clip); }
        foreach (var c in loco.SprintFor(SprintFamily.Hammer)?.Clips ?? []) stances.Add(c.Clip);
        Assert.True(stances.Count >= 20, $"the hammer stance's clips not found ({stances.Count})");
        return stances;
    }

    /// <summary>The most the shaft may stand off the spine's direction (degrees; the placement is exact, this guards the wiring).</summary>
    const float ParallelDegrees = 0.5f;

    /// <summary>
    /// Idle, walk, run and sprint in every direction: with the carry laid as in a match (the movement share 0 standing, 1 moving), the shaft is parallel to the spine as seen from the side (its
    /// fore-and-aft lean is the spine's; the side lean is the fixed <see cref="SpearHold.Pass3CarryRoll"/> that keeps the blade out of the crosshair's column), the right hand's grip
    /// socket lies on it at the grip point, the hand is outside the thigh at the side of the hip, and the carried butt stays off the ground.
    /// </summary>
    [InstallFact]
    public void The_shaft_follows_the_spines_lean_with_the_hand_at_the_side_in_every_stance()
    {
        var rig = CharacterModel.LoadMarineRig(Game.Value!);
        var loco = LocomotionAnimations.From(rig);
        var mirror = new WeaponPoseMirror(rig.Skeleton);
        var stances = Stances(loco);
        var failures = new List<string>();
        float worstAngle = 0f, buttLow = float.PositiveInfinity, leanLo = 99f, leanHi = -99f; string buttAt = "";
        foreach (var name in stances)
        {
            var anim = rig.LoadAnimation(name);
            mirror.FistWeight = name == stances[0] ? 0f : 1f;
            float sideLo = 9f, sideHi = -9f, upLo = 9f, upHi = -9f;
            for (float t = 0f; t <= anim.Animation.Duration; t += 1f / 30f)
            {
                mirror.Lay(anim, t);
                mirror.Overlay(ClawSpearClips.SpearRest, t % ClawSpearClips.SpearIdleLength);
                var spine = mirror.Spine();
                var (butt, shaft, _) = SpearHold.Place(mirror.RightHand, mirror.LeftHand, 0f, ClawSpearClips.SpearRest.GripPointAt(t), mirror.SpearFront(ClawSpearClips.SpearRest), Vector3.UnitZ, mirror.FistWeight, spine);
                float angle = MathF.Abs(MathF.Atan2(shaft.Y, shaft.Z) - MathF.Atan2(spine.Y, spine.Z)) * 180f / MathF.PI;   // the fore-and-aft lean, as seen from the side (the side lean is fixed: SpearHold.Pass3CarryRoll)
                worstAngle = MathF.Max(worstAngle, angle);
                leanLo = MathF.Min(leanLo, MathF.Atan2(spine.Y, spine.Z) * 180f / MathF.PI); leanHi = MathF.Max(leanHi, MathF.Atan2(spine.Y, spine.Z) * 180f / MathF.PI);
                float onShaft = SpearClearance.SegmentDistance(mirror.RightHand, mirror.RightHand, butt, butt + shaft * SpearHold.TipFromButt);
                if (onShaft > SpearHold.PalmTolerance) failures.Add($"{name} t {t:0.00}: the right palm is {onShaft:0.000} m off the shaft");
                float h = SpearClearance.ButtHeight(butt, shaft);
                if (h < buttLow) { buttLow = h; buttAt = $"{name} t {t:0.00}"; }
                var pelvis = mirror.Pose("RIG_Pelvis")!.Value.Item1;
                float side = mirror.RightHand.X - pelvis.X, up = mirror.RightHand.Z - pelvis.Z;   // the facing frame is the skeleton's: +X right, Z up
                sideLo = MathF.Min(sideLo, side); sideHi = MathF.Max(sideHi, side); upLo = MathF.Min(upLo, up); upHi = MathF.Max(upHi, up);
            }
            output.WriteLine(string.Format(I, "{0,-34} the hand {1:0.00}..{2:0.00} m right of the pelvis, {3:0.00}..{4:0.00} m above it", name, sideLo, sideHi, upLo, upHi));
            if (sideLo < 0.40f || sideHi > 0.72f) failures.Add($"{name}: the hand is {sideLo:0.00}..{sideHi:0.00} m right of the pelvis (at the side of the hip, outside the thigh: 0.40 to 0.72)");
            if (upLo < -0.25f || upHi > 0.45f) failures.Add($"{name}: the hand is {upLo:0.00}..{upHi:0.00} m above the pelvis (at the hip)");
        }
        output.WriteLine(string.Format(I, "the shaft's worst fore-and-aft angle to the spine {0:0.000} deg (seen from the side); the spine leans {1:0} to {2:0} deg forward over the stances; the carried butt lowest {3:0.000} m ({4})", worstAngle, leanLo, leanHi, buttLow, buttAt));
        Assert.True(worstAngle <= ParallelDegrees, $"the shaft is {worstAngle:0.00} degrees off the spine's lean");
        Assert.True(leanHi - leanLo > 15f, "the stances' spine lean should vary (the check follows the torso)");
        Assert.True(buttLow >= 0.0f, $"the carried butt goes into the ground: {buttLow:0.000} m at {buttAt}");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    /// <summary>The most a bone's length may change from the rest pose (m): the arms are rigid chains; the overlay and the fist solve only turn them.</summary>
    const float BoneLengthTolerance = 0.002f;

    /// <summary>
    /// The hands stay attached to the arms (owner, 2026-11-12: "Ensure the hand is connected to the arm"): over every frame of every stance, laying the carry (the fist solves, the swing, the keys)
    /// changes no bone length in either arm: clavicle to upper arm, upper arm to forearm, forearm to hand and hand to grip socket stay what the stance has (the overlay and the solves only turn the
    /// bones, so a hand cannot be left behind or an arm stretch).
    /// </summary>
    [InstallFact]
    public void The_hands_stay_attached_to_the_arms_with_fixed_bone_lengths_in_every_stance()
    {
        var rig = CharacterModel.LoadMarineRig(Game.Value!);
        var loco = LocomotionAnimations.From(rig);
        var mirror = new WeaponPoseMirror(rig.Skeleton);
        string[][] chains = [["RIG_R_Clavicle", "RIG_R_UpperArm"], ["RIG_R_UpperArm", "RIG_R_Forearm"], ["RIG_R_Forearm", "RIG_R_Hand"], ["RIG_R_Hand", SpearHold.RightGripBone],
                             ["RIG_L_Clavicle", "RIG_L_UpperArm"], ["RIG_L_UpperArm", "RIG_L_Forearm"], ["RIG_L_Forearm", "RIG_L_Hand"], ["RIG_L_Hand", SpearHold.LeftGripBone]];
        mirror.Rest();
        float Len(string a, string b) => Vector3.Distance(mirror.Origin(mirror.Bone(a)), mirror.Origin(mirror.Bone(b)));
        var stances = Stances(loco);
        float worst = 0f; string at = "";
        foreach (var name in stances)
        {
            var anim = rig.LoadAnimation(name);
            mirror.FistWeight = name == stances[0] ? 0f : 1f;
            for (float t = 0f; t <= anim.Animation.Duration; t += 1f / 30f)
            {
                mirror.Lay(anim, t);
                var before = chains.Select(c => Len(c[0], c[1])).ToArray();   // the stance's own lengths (its clip moves the weapon sockets on the hands itself)
                mirror.Overlay(ClawSpearClips.SpearRest, t % ClawSpearClips.SpearIdleLength);
                for (int i = 0; i < chains.Length; i++)
                {
                    float d = MathF.Abs(Len(chains[i][0], chains[i][1]) - before[i]);
                    if (d > worst) { worst = d; at = $"{chains[i][0]} -> {chains[i][1]} in {name} t {t:0.00}"; }
                }
            }
        }
        output.WriteLine(string.Format(I, "the carry changes the arms' bone lengths by at most {0:0.00000} m ({1})", worst, at));
        Assert.True(worst <= BoneLengthTolerance, $"an arm stretches or separates: {worst:0.0000} m ({at})");
    }

    /// <summary>
    /// The arms swing with the stride while he runs (owner, 2026-11-12: "right arm hanging/swinging slightly, spear moving with it; left arm swings freely"): over the forward run the right
    /// hand travels along the facing by at least 4 cm and at most 20 cm, the left hand at least 10 cm and more than the right, and the two swing against each other.
    /// </summary>
    [InstallFact]
    public void The_arms_swing_against_each_other_with_the_stride_in_the_forward_run()
    {
        var rig = CharacterModel.LoadMarineRig(Game.Value!);
        var loco = LocomotionAnimations.From(rig);
        var mirror = new WeaponPoseMirror(rig.Skeleton);
        var run = loco.Hammer!.Directions.OrderBy(d => MathF.Abs(d.AngleDegrees)).First().Run!;
        var anim = rig.LoadAnimation(run.Clip);
        mirror.FistWeight = 1f;
        var rights = new List<float>(); var lefts = new List<float>();
        for (float t = 0f; t < anim.Animation.Duration; t += 1f / 30f)
        {
            mirror.Lay(anim, t);
            mirror.Overlay(ClawSpearClips.SpearRest, t % ClawSpearClips.SpearIdleLength);
            var pelvis = mirror.Pose("RIG_Pelvis")!.Value.Item1;
            rights.Add(mirror.RightHand.Y - pelvis.Y); lefts.Add(mirror.LeftHand.Y - pelvis.Y);
        }
        float rr = rights.Max() - rights.Min(), lr = lefts.Max() - lefts.Min();
        float mr = rights.Average(), ml = lefts.Average();
        float corr = rights.Zip(lefts, (a, b) => (a - mr) * (b - ml)).Sum();
        output.WriteLine(string.Format(I, "forward run: the right hand travels {0:0.000} m, the left {1:0.000} m along the facing; correlation sign {2}", rr, lr, MathF.Sign(corr)));
        Assert.InRange(rr, 0.04f, 0.20f);
        Assert.True(lr >= 0.10f && lr > rr, "the left arm swings freely, more than the right");
        Assert.True(corr < 0f, "the arms swing against each other");
    }
}
