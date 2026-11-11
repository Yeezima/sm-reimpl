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
/// The authored-animation pilot (musou/spear-authored, 2026-11-12; docs/claw-spear-animations.md section 3g): hew 1 is keyed in Blender (<c>tools/blender/spear_hew_author.py</c>), exported to
/// <c>Authored/spear_hew1.json</c> and read back by <see cref="AuthoredPoseClip"/> into the overlay's own <see cref="WeaponPoseClip"/>. Pinned here: the adapter keeps the strike's timing
/// (the hit lands when it did), refuses data that disagrees, is deterministic, keeps the clip's shape (compact wind-up, a swing that accelerates into the hit, a brief finish hold that is
/// over before the next legal action matters), the rollback is the original clip, and, with the install, the in-game pose matches Blender's joints at the same times. No original bytes.
/// </summary>
public class SpearAuthoredHewTests(ITestOutputHelper output)
{
    static readonly WeaponPoseClip Authored = ClawSpearClips.SpearHew1Authored, Original = ClawSpearClips.SpearHew1Original;
    static float Pelvis(float t, int field) { var s = Authored.Channels.First(c => c.Bone == "RIG_Pelvis" && c.Kind == PoseChannelKind.Turn).Sample(t); return field == 0 ? s.Yaw : field == 1 ? s.Pitch : s.Roll; }

    [Fact]
    public void The_authored_hew_keeps_the_strikes_timing_and_identity()
    {
        Assert.Equal(Original.Name, Authored.Name);
        Assert.Equal(Original.Duration, Authored.Duration, 3);
        Assert.Equal(Original.HitTime, Authored.HitTime, 3);   // the blow lands when it did
        Assert.Equal(Original.NextAt, Authored.NextAt, 3);     // the combo opens when it did
        Assert.Equal(Original.BaseClip, Authored.BaseClip);
        Assert.Equal(Original.Weapon, Authored.Weapon);
        Assert.Contains("authored in Blender", Authored.Description);
        Assert.Equal(ClawSpearClips.OldHew1 ? Original : ClawSpearClips.OldPass2 ? Authored : ClawSpearClips.SpearHew1Pass2, ClawSpearClips.SpearHew1);   // the switches pick one of the three (--old-spear-hew: the original; --old-spear-pass2: this pilot clip)
        Assert.Equal(WeaponPoseClip.HewGrip, Authored.GripPointAt(Authored.HitTime), 3);
        Assert.Equal(1f, Authored.GripAt(Authored.HitTime), 3);
        Assert.Equal(0f, Authored.GripAt(0f), 3);   // starts from the one-handed carry
        Assert.Equal(WeaponPoseClip.CarryGrip, Authored.GripPointAt(0f), 3);
        Assert.Equal(0f, Authored.GripAt(Authored.Duration), 3);   // and ends in it
        Assert.Equal(WeaponPoseClip.CarryGrip, Authored.GripPointAt(Authored.Duration), 3);
    }

    [Fact]
    public void The_authored_hew_moves_the_same_bones_the_original_does_and_ends_on_the_stance()
    {
        var wanted = Original.Channels.Select(c => (c.Bone, c.Kind)).ToHashSet();
        var got = Authored.Channels.Select(c => (c.Bone, c.Kind)).ToHashSet();
        Assert.True(wanted.IsSubsetOf(got), "the authored clip lacks: " + string.Join(", ", wanted.Except(got)));
        // The one-piece rule: the pelvis carries the spine; the clip never keys the root (the simulation owns translation).
        Assert.DoesNotContain(Authored.Channels, c => c.Bone is "RIG_Root" or "RIG_Interaction_Root");
        foreach (var c in Authored.Channels.Where(c => c.Kind is PoseChannelKind.Turn or PoseChannelKind.Move or PoseChannelKind.Foot))
        {
            var end = c.Sample(Authored.Duration); var start = c.Sample(0f);
            Assert.True(end.IsZero, $"{c.Bone} {c.Kind} does not end on the stance");
            Assert.True(start.IsZero, $"{c.Bone} {c.Kind} does not start on the stance (the hand-over from the carry would pop)");
        }
        foreach (var c in Authored.Channels.Where(c => c.Kind == PoseChannelKind.Aim))   // the arms start and end as the carry's arms
        {
            var carry = ClawSpearClips.SpearRestOld.Channels.First(r => r.Bone == c.Bone && r.Kind == PoseChannelKind.Aim);
            var a = c.Sample(0f); var b = carry.Sample(0f);
            // the carry is read in the facing frame, the strike's keys in the stance frame (71 degrees turned): the same direction, so the elevations match
            Assert.True(MathF.Abs(a.Elevation - b.Elevation) < 0.6f, $"{c.Bone} starts at elevation {a.Elevation}, the carry's is {b.Elevation}");
            Assert.True(MathF.Abs(c.Sample(Authored.Duration).Elevation - b.Elevation) < 0.6f, $"{c.Bone} ends off the carry");
        }
    }

    [Fact]
    public void The_authored_hew_gathers_compactly_accelerates_into_the_hit_and_holds_its_finish_briefly()
    {
        float hit = Authored.HitTime;
        float Speed(float t) => MathF.Abs(Pelvis(t + 0.02f, 0) - Pelvis(t - 0.02f, 0)) / 0.04f;   // the hips' yaw rate (degrees a second)
        // Compact: the coil is complete by the wind-up apex, 0.19 s before the hit, and the swing from the apex to the hit is the whole acceleration.
        Assert.Equal(hit - 0.19f, Authored.Anticipation, 2);
        Assert.True(Pelvis(hit - 0.19f, 0) < 0f, "the hips are coiled away at the apex");
        // Accelerating: the hips' yaw rate keeps rising to the hit; it is faster there than anywhere in the wind-up before the apex, and several times faster than just after the apex.
        Assert.True(Speed(hit) > 4f * Speed(hit - 0.17f), $"swing speed at the hit {Speed(hit):0} deg/s vs {Speed(hit - 0.17f):0} just after the apex");
        Assert.True(Speed(hit) > 250f, $"the hips turn at only {Speed(hit):0} deg/s through the hit");
        for (float t = 0.05f; t < hit - 0.2f; t += 0.02f) Assert.True(Speed(t) < Speed(hit), $"the wind-up is faster than the hit at {t:0.00}");
        Assert.InRange(Pelvis(hit, 0), 45f, 80f);   // the hips are square to the target (the stance's front is 71 degrees right of it) as the blade passes
        // The finish: reached shortly after the hit and held briefly, the hips moving less than 4 degrees over 0.2 s, then the recovery goes on (the combo opens at NextAt).
        Assert.True(MathF.Abs(Pelvis(0.95f, 0) - Pelvis(1.15f, 0)) < 4f);
        Assert.True(Pelvis(0.95f, 0) > 85f, "the finish pose is past square");
        Assert.True(Pelvis(1.6f, 0) < Pelvis(1.18f, 0) - 20f, "the recovery has begun by 1.6 s");
        Assert.True(Authored.NextAt > 1.0f && Authored.NextAt < 1.18f + 0.01f, "the finish hold covers the opening of the combo window");
    }

    [Fact]
    public void The_adapter_refuses_data_that_would_move_the_hit_or_change_the_strike()
    {
        string json = AuthoredPoseClip.EmbeddedJson("spear_hew1.json");
        Assert.Equal(Authored.Channels.Length, AuthoredPoseClip.FromJson(json, Original).Channels.Length);   // deterministic: the same data, the same clip
        Assert.Equal(Authored.Channels[3].Keys, AuthoredPoseClip.FromJson(json, Original).Channels[3].Keys);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        void Refuses(Action<System.Text.Json.Nodes.JsonNode> change, string why)
        {
            var copy = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            change(copy);
            Assert.Throws<InvalidDataException>(() => AuthoredPoseClip.FromJson(copy.ToJsonString(), Original));
            output.WriteLine("refused: " + why);
        }
        Refuses(n => n["timing"]!["hit_time"] = Original.HitTime + 0.05, "a later hit");
        Refuses(n => n["timing"]!["duration"] = Original.Duration + 0.2, "a longer clip");
        Refuses(n => n["rest_pose"]!["clip"] = "SM_OTHER_IDLE", "another base stance");
        Refuses(n => n["name"] = "spear_hew2", "another strike's name");
        Refuses(n => n["format"] = "something/2", "an unknown format");
        Assert.NotNull(node);
    }

    [Fact]
    public void A_refused_authored_export_falls_back_to_the_original_clip()
    {
        var bad = AuthoredPoseClip.FromJsonOrTemplate(() => "{ not json", Original, out string? p1);
        Assert.Same(Original, bad); Assert.NotNull(p1);
        string json = AuthoredPoseClip.EmbeddedJson("spear_hew1.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["timing"]!["hit_time"] = 0.9;
        var later = AuthoredPoseClip.FromJsonOrTemplate(() => node.ToJsonString(), Original, out string? p2);
        Assert.Same(Original, later); Assert.Contains("hit", p2);
        var missing = AuthoredPoseClip.FromJsonOrTemplate(() => AuthoredPoseClip.EmbeddedJson("nothing.json"), Original, out string? p3);
        Assert.Same(Original, missing); Assert.NotNull(p3);
        var good = AuthoredPoseClip.FromJsonOrTemplate(() => json, Original, out string? p4);
        Assert.Null(p4); Assert.NotSame(Original, good);
    }

    [Fact]
    public void The_private_output_guard_checks_the_output_path_not_the_working_directory()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "sm-guard-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(tmp, "checkout"), inRepo = Path.Combine(repo, "private", "x"), sibling = Path.Combine(tmp, "checkout-other"), install = Path.Combine(tmp, "Game");
        Directory.CreateDirectory(Path.Combine(repo, ".git")); Directory.CreateDirectory(sibling); Directory.CreateDirectory(install);
        try
        {
            Assert.NotNull(PrivateOutputGuard.Refuses(inRepo, install));          // under a checkout, whatever the working directory is
            Assert.NotNull(PrivateOutputGuard.Refuses(repo, install));
            Assert.Null(PrivateOutputGuard.Refuses(sibling, install));            // a sibling that merely starts with the same letters is outside
            Assert.NotNull(PrivateOutputGuard.Refuses(Path.Combine(install, "out"), install));
            Assert.Null(PrivateOutputGuard.Refuses(install + "-copy", install));   // "Game-copy" is not inside "Game"
            Assert.True(PrivateOutputGuard.IsInside(Path.Combine(install, "A") + Path.DirectorySeparatorChar, install.ToUpperInvariant()));
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public void The_export_states_its_units_axes_rest_pose_bone_map_and_sample_rate()
    {
        using var doc = JsonDocument.Parse(AuthoredPoseClip.EmbeddedJson("spear_hew1.json"));
        var r = doc.RootElement;
        Assert.Equal("metres", r.GetProperty("units").GetProperty("length").GetString());
        Assert.Equal("degrees", r.GetProperty("units").GetProperty("angle").GetString());
        Assert.Contains("Z up", r.GetProperty("axes").GetString());
        Assert.Equal(ClawSpearClips.SpearIdle, r.GetProperty("rest_pose").GetProperty("clip").GetString());
        Assert.Equal("RIG_Prop_RHand", r.GetProperty("bone_map").GetProperty("grip_sockets")[0].GetString());
        Assert.Equal("RIG_Prop_LHand", r.GetProperty("bone_map").GetProperty("grip_sockets")[1].GetString());
        Assert.Equal(60.0, r.GetProperty("sample_rate_hz").GetDouble());
        Assert.StartsWith("Blender 4.5", r.GetProperty("authoring").GetProperty("tool").GetString());
        // No install data in a committed file: no bone transforms of the skeleton and no solved joint positions (those go to a private check file), only authored channels.
        Assert.False(r.TryGetProperty("bones", out _));
        Assert.False(r.TryGetProperty("check", out _));
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
        string checkFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "spear-authoring", "spear_hew1.check.json");
        if (!File.Exists(checkFile)) { output.WriteLine("SKIPPED: no private check file (run tools/blender/spear_hew_author.py with --check, see docs/spear-authored-pilot.md): the in-game comparison did not run"); return; }
        using var doc = JsonDocument.Parse(File.ReadAllText(checkFile));
        var check = doc.RootElement;
        var times = check.GetProperty("times").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        float worst = 0f; string worstAt = "";
        foreach (var joint in check.GetProperty("joints").EnumerateObject())
        {
            int b = mirror.Bone(joint.Name);
            Assert.True(b >= 0, joint.Name);
            var points = joint.Value.EnumerateArray().Select(p => new Vector3((float)p[0].GetDouble(), (float)p[1].GetDouble(), (float)p[2].GetDouble())).ToArray();
            float jointWorst = 0f;
            for (int i = 0; i < times.Length; i++)
            {
                mirror.Lay(idle, 0f);
                mirror.Overlay(Authored, times[i]);
                float d = Vector3.Distance(mirror.Origin(b), points[i]);
                jointWorst = MathF.Max(jointWorst, d);
                if (d > worst) { worst = d; worstAt = $"{joint.Name} at {times[i]:0.00} s"; }
            }
            output.WriteLine($"{joint.Name,-16} worst {jointWorst * 100:0.0} cm");
        }
        output.WriteLine($"worst of all: {worst * 100:0.0} cm ({worstAt})");
        Assert.True(worst < 0.04f, $"the in-game pose is {worst:0.000} m from Blender's at {worstAt}");
    }
}
