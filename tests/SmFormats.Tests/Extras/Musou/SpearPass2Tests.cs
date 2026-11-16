using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using SmContent;
using SmContent.Extras.Musou;
using Xunit.Abstractions;

namespace SmFormats.Tests;

/// <summary>
/// Spear pass 2 (owner, 2026-11-12; docs/claw-spear-animations.md section 3h): the carry of his reference image (the hips squared to the facing at rest, the right arm hanging beside the hip, the
/// blade forward and up) and the three light hews, the F snap cut and the F,F stab re-authored in Blender (<c>tools/blender/spear_pass2_author.py</c>) as compact, accelerating, hip-driven
/// strokes with a brief finish and a quick recovery. Pinned here: every strike keeps the original's identity and timing (length, hit, combo opening, base stance: the adapter refuses
/// anything else), starts and ends on the carry (or, chained, on the stroke before), accelerates into the hit and holds its finish briefly; the rollback switch plays the old clips; the
/// jump pack's flight with the spear is untouched; with the install the in-game overlay agrees with Blender's joints. No original bytes.
/// </summary>
public class SpearPass2Tests(ITestOutputHelper output)
{
    sealed record Case(string File, WeaponPoseClip Authored, WeaponPoseClip Original, float HoldEnd, float CarryAt);

    static Case[] Cases => [
        new("spear_hew1_p2.json", ClawSpearClips.SpearHew1Pass2, ClawSpearClips.SpearHew1Original, 1.15f, 2.10f),
        new("spear_hew2_p2.json", ClawSpearClips.SpearHew2Pass2, ClawSpearClips.SpearHew2Original, 1.15f, 1.95f),
        new("spear_hew3_p2.json", ClawSpearClips.SpearHew3Pass2, ClawSpearClips.SpearHew3Original, 1.25f, 2.05f),
        new("spear_stun_p2.json", ClawSpearClips.SpearStunPass2, ClawSpearClips.SpearStunOriginal, 0.72f, 1.50f),
        new("spear_stab_p2.json", ClawSpearClips.SpearStabPass2, ClawSpearClips.SpearStabOriginal, 1.80f, 3.00f),
    ];

    static float Pelvis(WeaponPoseClip c, float t) => c.Channels.First(x => x.Bone == "RIG_Pelvis" && x.Kind == PoseChannelKind.Turn).Sample(t).Yaw;

    [Fact]
    public void Every_pass_2_stroke_keeps_the_strikes_identity_and_timing_and_is_really_authored()
    {
        foreach (var c in Cases)
        {
            Assert.Equal(c.Original.Name, c.Authored.Name);
            Assert.Equal(c.Original.Duration, c.Authored.Duration, 3);
            Assert.Equal(c.Original.HitTime, c.Authored.HitTime, 3);
            Assert.Equal(c.Original.NextAt, c.Authored.NextAt, 3);
            Assert.Equal(c.Original.BaseClip, c.Authored.BaseClip);
            Assert.Equal(c.Original.Weapon, c.Authored.Weapon);
            Assert.Contains("authored in Blender", c.Authored.Description);
            Assert.NotSame(c.Original, c.Authored);   // the export was found and accepted (a refused or missing one falls back to the original)
        }
    }

    [Fact]
    public void The_switch_picks_between_pass_2_and_the_old_clips()
    {
        bool old = ClawSpearClips.OldPass2, p4 = ClawSpearClips.OldPass4;
        Assert.Equal(old ? ClawSpearClips.SpearHew2Original : p4 ? ClawSpearClips.SpearHew2Pass2 : ClawSpearClips.OldPass4b ? ClawSpearClips.SpearHew2Pass4 : ClawSpearClips.SpearHew2Pass4b, ClawSpearClips.SpearHew2);
        Assert.Equal(old ? ClawSpearClips.SpearHew3Original : p4 ? ClawSpearClips.SpearHew3Pass2 : ClawSpearClips.OldPass4b ? ClawSpearClips.SpearHew3Pass4 : ClawSpearClips.SpearHew3Pass4b, ClawSpearClips.SpearHew3);
        Assert.Equal(old ? ClawSpearClips.SpearStunOriginal : p4 ? ClawSpearClips.SpearStunPass2 : ClawSpearClips.OldPass4b ? ClawSpearClips.SpearStunPass4 : ClawSpearClips.SpearStunPass4b, ClawSpearClips.SpearStun);
        Assert.Equal(old ? ClawSpearClips.SpearStabOriginal : p4 ? ClawSpearClips.SpearStabPass2 : ClawSpearClips.OldPass4b ? ClawSpearClips.SpearStabPass4 : ClawSpearClips.SpearStabPass4b, ClawSpearClips.SpearStab);
        Assert.Equal(old ? ClawSpearClips.SpearRestOld : ClawSpearClips.OldPass3 ? ClawSpearClips.SpearRestPass2 : p4 ? ClawSpearClips.SpearRestPass3 : ClawSpearClips.SpearRestPass4, ClawSpearClips.SpearRest);   // spear pass 3 replaced the carry; its rollback is pass 2's
        Assert.Equal(old ? 65f : SpearHold.Pass2CarryElevation, SpearHold.CarryElevation);
        Assert.Equal(old ? -50f : SpearHold.Pass2CarryAzimuth, SpearHold.CarryAzimuth);
        // the rollback keeps the old carry exactly as it was: no squared hips, no pelvis channel
        Assert.Equal(0f, ClawSpearClips.SpearRestOld.SquareYaw);
        Assert.DoesNotContain(ClawSpearClips.SpearRestOld.Channels, c => c.Bone == "RIG_Pelvis");
        Assert.Equal(ClawSpearClips.CarryFist, ClawSpearClips.SpearRestOld.FistTarget);
    }

    [Fact]
    public void The_carry_hangs_the_right_arm_beside_the_hip_with_the_hips_squared()
    {
        var rest = ClawSpearClips.SpearRestPass2;
        Assert.Equal(ClawSpearClips.IdleHipsTurn, rest.SquareYaw);   // squared to the facing while he stands; the fist solve takes over as he moves
        Assert.True(rest.FacingFrame);
        var up = rest.Channels.First(c => c.Bone == "RIG_R_UpperArm" && c.Kind == PoseChannelKind.Aim).Sample(0.3f);
        var fo = rest.Channels.First(c => c.Bone == "RIG_R_Forearm" && c.Kind == PoseChannelKind.Aim).Sample(0.3f);
        Assert.InRange(up.Elevation, -85f, -60f);   // the arm hangs nearly straight down (owner's reference: "arm hanging nearly straight")
        Assert.InRange(fo.Elevation, -75f, -45f);
        Assert.InRange(up.Azimuth, 0f, 60f);        // a little forward and out from the body, not across it
        // the blade points forward and up, only a little across the body
        Assert.InRange(SpearHold.Pass2CarryElevation, 30f, 75f);
        Assert.InRange(SpearHold.Pass2CarryAzimuth, -60f, 0f);
        Assert.Equal(WeaponPoseClip.CarryGrip, rest.GripPointAt(0.5f));
    }

    [Fact]
    public void Each_stroke_starts_and_ends_on_the_carry_or_the_stroke_before_it()
    {
        foreach (var c in Cases)
        {
            var a = c.Authored;
            float yaw0 = Pelvis(a, 0f), yawEnd = Pelvis(a, a.Duration);
            Assert.True(MathF.Abs(yawEnd - ClawSpearClips.IdleHipsTurn) < 0.5f, $"{a.Name} ends with the hips at {yawEnd}, the squared carry's are {ClawSpearClips.IdleHipsTurn}");
            if (c.File is "spear_hew1_p2.json" or "spear_stun_p2.json")
            {
                Assert.True(MathF.Abs(yaw0 - ClawSpearClips.IdleHipsTurn) < 0.5f, $"{a.Name} starts with the hips at {yaw0}");
                Assert.Equal(0f, a.GripAt(0f), 3);
                Assert.Equal(WeaponPoseClip.CarryGrip, a.GripPointAt(0f), 3);
            }
            Assert.Equal(0f, a.GripAt(a.Duration), 3);   // the left hand has let go
            Assert.Equal(WeaponPoseClip.CarryGrip, a.GripPointAt(a.Duration), 3);   // the shaft is back at the carry grip
            foreach (var ch in a.Channels.Where(x => x.Kind is PoseChannelKind.Move or PoseChannelKind.Foot))
            {
                var e = ch.Sample(a.Duration);
                float drop = ch.Kind == PoseChannelKind.Move ? ClawSpearClips.CarryDrop : 0f;   // the carry's hips are lowered a little (CarryDrop)
                Assert.True(MathF.Abs(e.Forward) + MathF.Abs(e.Right) + MathF.Abs(e.Up - drop) < 2e-3f, $"{a.Name}: {ch.Bone} {ch.Kind} does not end on the carry");
            }
            foreach (var ch in a.Channels.Where(x => x.Kind == PoseChannelKind.Aim))   // the arms end as the carry's arms (the strike's keys are in the stance frame, the carry's in the facing frame)
            {
                var rest = ClawSpearClips.SpearRestPass2.Channels.First(r => r.Bone == ch.Bone && r.Kind == PoseChannelKind.Aim).Sample(0f);
                var end = ch.Sample(a.Duration);
                Assert.True(MathF.Abs(end.Elevation - rest.Elevation) < 0.6f, $"{a.Name}: {ch.Bone} ends at elevation {end.Elevation}, the carry's is {rest.Elevation}");
            }
            // the carry is reached by its time and the clip rests there
            Assert.True(MathF.Abs(Pelvis(a, c.CarryAt + 0.02f) - ClawSpearClips.IdleHipsTurn) < 1f, $"{a.Name} has not reached the carry's hips by {c.CarryAt} s");
        }
    }

    [Fact]
    public void Each_stroke_coils_compactly_accelerates_into_the_hit_and_holds_its_finish_briefly()
    {
        foreach (var c in Cases)
        {
            var a = c.Authored;
            float hit = a.HitTime;
            float Speed(float t) => MathF.Abs(Pelvis(a, t + 0.02f) - Pelvis(a, t - 0.02f)) / 0.04f;   // the hips' yaw rate, degrees a second
            // Compact: the wind-up's apex is where the original's is (the strike's own apex time), and the hips are at their coil there.
            Assert.Equal(c.Original.Anticipation, a.Anticipation, 2);
            // Accelerating: faster through the hit than at any time before the apex, and several times faster than just after the apex (stab and snap are short on yaw: they drive with the shift too).
            float peakBefore = 0f;
            for (float t = 0.04f; t < a.Anticipation - 0.03f; t += 0.02f) peakBefore = MathF.Max(peakBefore, Speed(t));
            output.WriteLine($"{a.Name}: hips {Speed(hit):0} deg/s at the hit, {Speed(a.Anticipation + 0.03f):0} just after the apex, {peakBefore:0} at most before it");
            if (c.File is "spear_hew1_p2.json" or "spear_hew2_p2.json" or "spear_hew3_p2.json") Assert.True(Speed(hit) > 2.5f * Speed(a.Anticipation + 0.03f), $"{a.Name}: no acceleration into the hit");
            else Assert.True(Speed(hit) > 0.9f * Speed(a.Anticipation + 0.03f), $"{a.Name}: the hips slow down into the hit");   // the snap and the stab are driven by the shaft's whip and the lunge, not the hips' turn
            // Brief finish: the hips move under 5 degrees from the end of the follow-through for 0.1 s, then the recovery goes on and has the hips back on the carry within the time.
            float f = a.Follow + 0.03f;
            Assert.True(MathF.Abs(Pelvis(a, f) - Pelvis(a, MathF.Min(c.HoldEnd, f + 0.1f))) < 5f, $"{a.Name}: the finish is not held");
            Assert.True(c.HoldEnd - a.Follow < 0.75f, $"{a.Name}: the finish is held too long");
        }
    }

    [Fact]
    public void The_chained_strokes_start_where_the_stroke_before_them_leaves_the_body()
    {
        // the combo's hand-overs (WeaponPoseState blends 0.14 s from the pose at the combo opening to the next clip's first pose): the hips and the feet must not jump
        void Near(WeaponPoseClip before, WeaponPoseClip after, float yawTol)
        {
            float a = Pelvis(before, before.NextAt), b = Pelvis(after, 0f);
            Assert.True(MathF.Abs(a - b) < yawTol, $"{before.Name} -> {after.Name}: the hips are at {a} when it opens the combo, the next stroke starts at {b}");
        }
        Near(ClawSpearClips.SpearHew1Pass2, ClawSpearClips.SpearHew2Pass2, 12f);
        Near(ClawSpearClips.SpearHew2Pass2, ClawSpearClips.SpearHew3Pass2, 40f);
        Near(ClawSpearClips.SpearStunPass2, ClawSpearClips.SpearStabPass2, 12f);
    }

    [Fact]
    public void The_exports_state_their_units_axes_rest_pose_technique_and_stay_free_of_install_data()
    {
        foreach (var c in Cases)
        {
            using var doc = JsonDocument.Parse(AuthoredPoseClip.EmbeddedJson(c.File));
            var r = doc.RootElement;
            Assert.Equal("metres", r.GetProperty("units").GetProperty("length").GetString());
            Assert.Equal("degrees", r.GetProperty("units").GetProperty("angle").GetString());
            Assert.Contains("Z up", r.GetProperty("axes").GetString());
            Assert.Equal(ClawSpearClips.SpearIdle, r.GetProperty("rest_pose").GetProperty("clip").GetString());
            Assert.Equal(60.0, r.GetProperty("sample_rate_hz").GetDouble());
            Assert.Equal(2, r.GetProperty("authoring").GetProperty("pass").GetInt32());
            Assert.True(r.GetProperty("technique").GetString()!.Length > 40, "the source technique is named");
            Assert.False(r.TryGetProperty("bones", out _));
            Assert.False(r.TryGetProperty("check", out _));
        }
    }

    // ---- with the install: the in-game pose (the overlay's arithmetic over the idle stance) against the joints Blender solved at the same times ----
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);
    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute() { if (Game.Value is null) Skip = "Space Marine install not found (the in-game comparison needs the install's rig; never in CI)"; }
    }

    [InstallFact]
    public void The_in_game_pose_matches_the_joints_Blender_solved_at_the_same_times()
    {
        var rig = CharacterModel.LoadMarineRig(Game.Value!);
        var idle = rig.LoadAnimation(ClawSpearClips.SpearIdle);
        var mirror = new WeaponPoseMirror(rig.Skeleton);
        foreach (var c in Cases)
        {
            string checkFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "spear-authoring", "p2", c.File.Replace(".json", ".check.json"));
            if (!File.Exists(checkFile)) { output.WriteLine($"SKIPPED {c.File}: no private check file (tools/blender/spear_pass2_author.py --check-dir): the in-game comparison did not run"); continue; }
            using var doc = JsonDocument.Parse(File.ReadAllText(checkFile));
            var check = doc.RootElement;
            var times = check.GetProperty("times").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            float worst = 0f; string worstAt = "";
            foreach (var joint in check.GetProperty("joints").EnumerateObject())
            {
                int b = mirror.Bone(joint.Name);
                Assert.True(b >= 0, joint.Name);
                var points = joint.Value.EnumerateArray().Select(p => new Vector3((float)p[0].GetDouble(), (float)p[1].GetDouble(), (float)p[2].GetDouble())).ToArray();
                for (int i = 0; i < times.Length; i++)
                {
                    mirror.Lay(idle, 0f);
                    mirror.Overlay(c.Authored, times[i]);
                    float d = Vector3.Distance(mirror.Origin(b), points[i]);
                    if (d > worst) { worst = d; worstAt = $"{joint.Name} at {times[i]:0.00} s"; }
                }
            }
            output.WriteLine($"{c.Authored.Name}: worst joint distance to Blender's {worst * 100:0.0} cm ({worstAt})");
            Assert.True(worst < 0.04f, $"{c.Authored.Name}: the in-game pose is {worst:0.000} m from Blender's at {worstAt}");
        }
    }
}
