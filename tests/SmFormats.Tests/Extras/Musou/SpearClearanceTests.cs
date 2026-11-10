using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using SmContent;
using SmContent.Extras.Musou;
using SmFormats.Havok;
using Xunit.Abstractions;

namespace SmFormats.Tests;

/// <summary>
/// The power spear never passes through the marine (owner, 2026-11-10: "Make a pass at all the animations to ensure the spear isn't going through the player models body at any time
/// (clipping)"; docs/claw-spear-animations.md section 3b). Every spear clip is sampled over its whole time on the install's rig (the keys laid over the clip they lie on in a match: the
/// hammer's idle under the strikes, the dive loop and the landing under the pound, the hammer stance's idle, walk, run and sprint under the carry), with the hand-overs between them, by
/// <see cref="WeaponPoseMirror"/> (the client's overlay in System.Numerics); the spear is placed as in a match (<see cref="SpearHold.Place"/>) and measured against the body's capsules
/// (<see cref="SpearClearance"/>). No pose may pass more than <see cref="SpearClearance.Tolerance"/> into the body (one allowance: <see cref="LandingHandOverBudget"/>), and the carried butt
/// stays off the ground. Before the no-clipping pass (the W3b clips and placement) the worst was 0.34 m (hew 3's overhead loop through the jump pack), the carry 0.23 m into the right shin
/// and every strike 0.18 to 0.34 m deep; the mirror agreed with the stage's own overlay (<c>--claw-spear-test --clearance</c>) to 0.01 to 0.02 m on every strike. Skipped without the install
/// (CI). No original bytes.
/// </summary>
public class SpearClearanceTests(ITestOutputHelper output)
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the spear's clearance needs the install's rig; never in CI)";
        }
    }

    static readonly Lazy<(HavokCharacterFile Rig, LocomotionAnimations Loco)> Data = new(() =>
    {
        var rig = CharacterModel.LoadMarineRig(Game.Value!);
        return (rig, LocomotionAnimations.From(rig));
    });

    /// <summary>The carried butt's least height above the ground over the stances (m; owner, 2026-11-10: "right at the point where the butt of the spear is just above the ground" and "the butt shouldn't drag").</summary>
    const float ButtMinHeight = 0.05f;

    const float Step = 1f / 60f;
    /// <summary>The hammer idle's loop phases a strike is measured from (s).</summary>
    static readonly float[] IdlePhases = [0f, 0.25f, 0.5f, 0.75f, 1.0f, 1.25f];
    static readonly CultureInfo I = CultureInfo.InvariantCulture;

    sealed record Worst(string Scenario, float Time, SpearClearanceResult Result);

    sealed class Run(WeaponPoseMirror mirror)
    {
        public readonly Dictionary<string, Worst> ByClip = new();
        public float LowestButt = float.PositiveInfinity, LowestLandingButt = float.PositiveInfinity;
        public string LowestButtAt = "", LowestLandingButtAt = "";

        /// <summary>Lays <paramref name="clip"/> (faded from <paramref name="from"/>) over the pose the mirror holds and measures the spear placed on the hands.</summary>
        public SpearClearanceResult Measure(string key, string scenario, float time, WeaponPoseClip clip, float clipTime, WeaponPoseClip? from = null, float fromTime = 0f, float share = 0f, bool carry = false, bool land = false)
        {
            mirror.Overlay(clip, clipTime, from, fromTime, share);
            float grip = clip.GripAt(clipTime), point = clip.GripPointAt(clipTime);
            if (from is not null && share > 0f) { grip += (from.GripAt(fromTime) - grip) * share; point += (from.GripPointAt(fromTime) - point) * share; }
            var (butt, shaft, edge) = SpearHold.Place(mirror.RightHand, mirror.LeftHand, grip, point, mirror.FrontNow(), Vector3.UnitZ);
            var r = SpearClearance.Measure(mirror.Pose, butt, shaft, edge);
            if (!float.IsFinite(r.Clearance)) throw new InvalidOperationException($"a clearance that is not a finite number: {key} at {clipTime:0.000} s");   // a NaN would never count as the worst
            if (!ByClip.TryGetValue(key, out var worst) || r.Clearance < worst.Result.Clearance) ByClip[key] = new Worst(scenario, time, r);
            if (carry)
            {
                float h = SpearClearance.ButtHeight(butt, shaft);
                if (h < LowestButt) { LowestButt = h; LowestButtAt = $"{scenario} t {time:0.000}"; }
            }
            if (land)
            {
                float h = SpearClearance.ButtHeight(butt, shaft);   // the plain landing holds the spear butt down: never into the ground
                if (h < LowestLandingButt) { LowestLandingButt = h; LowestLandingButtAt = $"{scenario} t {time:0.000}"; }
            }
            return r;
        }
    }

    Run Sweep()
    {
        var (rig, loco) = Data.Value;
        var mirror = new WeaponPoseMirror(rig.Skeleton);
        var run = new Run(mirror);
        var idle = rig.LoadAnimation(ClawSpearClips.SpearIdle);
        // The carry over the hammer stance (standing, walking, running and sprinting in every direction the stance has): the stance itself is the pose under the keys (no base laid).
        var stances = new List<string> { loco.HammerIdle ?? ClawSpearClips.SpearIdle };
        foreach (var d in loco.Hammer?.Directions ?? []) { if (d.Walk is { } w) stances.Add(w.Clip); if (d.Run is { } r) stances.Add(r.Clip); }
        foreach (var c in loco.SprintFor(SprintFamily.Hammer)?.Clips ?? []) stances.Add(c.Clip);
        Assert.True(stances.Count >= 20, $"the hammer stance's clips not found ({stances.Count}; the install has twenty: the idle, the walk and run directions and the sprint)");
        foreach (var name in stances)
        {
            var stance = rig.LoadAnimation(name);
            for (float t = 0f; t <= stance.Animation.Duration; t += Step)
            {
                mirror.Lay(stance, t);
                run.Measure("spear_rest (carry)", name, t, ClawSpearClips.SpearRest, t % ClawSpearClips.SpearIdleLength, carry: true);
            }
        }
        // The strikes over the hammer's idle (in a match the idle's loop clock runs on under them, so a strike starts at any phase of its 1.5 s loop: six of them).
        foreach (float phase in IdlePhases)
            foreach (var clip in ClawSpearClips.SpearStrikes)
                for (float t = 0f; t <= clip.Duration + 1e-4f; t += Step)
                {
                    mirror.Lay(idle, (phase + t) % ClawSpearClips.SpearIdleLength);
                    run.Measure(clip.Name, $"over the idle +{phase:0.00}", t, clip, t);
                }
        // The pound: the dive over the dive loop, the impale over the landing.
        var dive = rig.LoadAnimation(ClawSpearClips.SpearDiveLoop);
        var impact = rig.LoadAnimation(ClawSpearClips.SpearDiveImpact);
        for (float t = 0f; t <= ClawSpearClips.SpearDiveLoopLength; t += Step) { mirror.Lay(dive, t); run.Measure("spear_dive", "over the dive loop", t, ClawSpearClips.SpearDive, t); }
        for (float t = 0f; t <= ClawSpearClips.SpearDiveImpactLength; t += Step) { mirror.Lay(impact, t); run.Measure("spear_impale", "over the landing", t, ClawSpearClips.SpearImpale, t); }
        for (float t = 0f; t <= ClawSpearClips.SpearDiveImpactLength; t += Step) { mirror.Lay(impact, t); run.Measure("spear_land", "over the landing", t, ClawSpearClips.SpearLand, t, land: true); }
        // The hand-overs (WeaponPoseState.BlendSeconds): the carry into each strike (a Musou light press may start any hew), the chains at their windows, each strike's end back into the
        // carry, the dive into the impale; the new clip runs on from its start while the old one's pose fades.
        var handovers = new List<(string Key, HavokBoundAnimation Base, WeaponPoseClip From, float FromTime, WeaponPoseClip To, float ToStart, HavokBoundAnimation ToBase)>();
        foreach (var strike in ClawSpearClips.SpearStrikes) handovers.Add(($"carry -> {strike.Name}", idle, ClawSpearClips.SpearRest, 0.5f, strike, 0f, idle));
        handovers.Add(("hew1 -> hew2", idle, ClawSpearClips.SpearHew1, ClawSpearClips.SpearHew1.NextAt, ClawSpearClips.SpearHew2, 0f, idle));
        handovers.Add(("hew2 -> hew3", idle, ClawSpearClips.SpearHew2, ClawSpearClips.SpearHew2.NextAt, ClawSpearClips.SpearHew3, 0f, idle));
        handovers.Add(("hew1 -> hew3", idle, ClawSpearClips.SpearHew1, ClawSpearClips.SpearHew1.NextAt, ClawSpearClips.SpearHew3, 0f, idle));
        handovers.Add(("stun -> stab", idle, ClawSpearClips.SpearStun, ClawSpearClips.SpearStun.NextAt, ClawSpearClips.SpearStab, 0f, idle));
        foreach (var strike in ClawSpearClips.SpearStrikes) handovers.Add(($"{strike.Name} -> carry", idle, strike, strike.Duration, ClawSpearClips.SpearRest, 0f, idle));
        handovers.Add(("dive -> impale", impact, ClawSpearClips.SpearDive, 1.0f, ClawSpearClips.SpearImpale, 0f, impact));
        handovers.Add(("spear_impale -> carry", idle, ClawSpearClips.SpearImpale, ClawSpearClips.SpearImpale.Duration, ClawSpearClips.SpearRest, 0f, idle));
        // The plain landing (2026-11-11: the impale only on a hit): from the dive, into the carry, and the client's change of mind while the choice is open (its guess, then the server's word).
        handovers.Add(("dive -> land", impact, ClawSpearClips.SpearDive, 1.0f, ClawSpearClips.SpearLand, 0f, impact));
        handovers.Add(("spear_land -> carry", idle, ClawSpearClips.SpearLand, ClawSpearClips.SpearLand.Duration, ClawSpearClips.SpearRest, 0f, idle));
        foreach (float at in new[] { 0.1f, 0.2f, 0.25f })   // up to NetworkView.SpearLandingLock (0.25 s); the two share their touch-down to ClawSpearClips.ImpaleDrive
        {
            handovers.Add(($"impale -> land at {at:0.0}", impact, ClawSpearClips.SpearImpale, at, ClawSpearClips.SpearLand, at, impact));
            handovers.Add(($"land -> impale at {at:0.0}", impact, ClawSpearClips.SpearLand, at, ClawSpearClips.SpearImpale, at, impact));
        }
        foreach (var h in handovers)
            for (float e = 0f; e <= WeaponPoseState_BlendSeconds + 1e-4f; e += Step)
            {
                float share = 1f - Smooth(Math.Clamp(e / WeaponPoseState_BlendSeconds, 0f, 1f));
                float t = h.ToStart + e;
                mirror.Lay(h.ToBase, h.ToBase == idle ? t % ClawSpearClips.SpearIdleLength : t);
                run.Measure($"hand-over {h.Key}", h.ToBase == idle ? "over the idle" : "over the landing", e, h.To, t, h.From, h.FromTime, share);
            }
        return run;
    }

    /// <summary>
    /// The one allowance: the dive's pose fading into the impale over the landing's first frames, while the landing clip slams the body about a metre down in 0.07 s; the dive's held hands meet
    /// the landing body's right arm and leg for one frame (0.07 to 0.09 m at 0.033 s for every dive pose tried; the impale alone and the rest of the hand-over are clear). Open, for a refinement.
    /// </summary>
    const float LandingHandOverBudget = 0.10f;   // for the whole dive -> impale hand-over (0.14 s), not one frame: its worst frame measured 0.085 m (lead review, 2026-11-10)

    /// <summary>The client's <c>WeaponPoseState.BlendSeconds</c> (client code, not referenced here).</summary>
    const float WeaponPoseState_BlendSeconds = 0.14f;
    /// <summary>The client's <c>CharacterViewer.Smooth</c> (smoothstep).</summary>
    static float Smooth(float u) => u * u * (3f - 2f * u);

    /// <summary>Which vertices make each part (by the bone they are skinned to most, or rigid on) and how many capsules it is split into.</summary>
    static readonly (string Part, string Bone, string[] Sources, int Clusters)[] FitPlan =
    [
        ("head", "RIG_Head", ["RIG_Head"], 2),
        ("torso", "RIG_SpineD", ["RIG_SpineD"], 6),
        ("waist", "RIG_SpineA", ["RIG_SpineA"], 1),
        ("pelvis", "RIG_Pelvis", ["RIG_Pelvis"], 3),
        ("jump pack", "RIG_SpineD", ["jump pack"], 6),
        ("left pad", "RIG_L_ShoulderPad", ["RIG_L_ShoulderPad"], 3),
        ("right pad", "RIG_R_ShoulderPad", ["RIG_R_ShoulderPad"], 3),
        ("left upper arm", "RIG_L_UpperArm", ["RIG_L_UpperArm"], 1),
        ("right upper arm", "RIG_R_UpperArm", ["RIG_R_UpperArm"], 1),
        ("left thigh", "RIG_L_Thigh", ["RIG_L_Thigh"], 1),
        ("right thigh", "RIG_R_Thigh", ["RIG_R_Thigh"], 1),
        ("left knee", "RIG_L_KneePad", ["RIG_L_KneePad"], 1),
        ("right knee", "RIG_R_KneePad", ["RIG_R_KneePad"], 1),
        ("left shin", "RIG_L_Calf", ["RIG_L_Calf"], 1),
        ("right shin", "RIG_R_Calf", ["RIG_R_Calf"], 1),
        ("left foot", "RIG_L_Foot", ["RIG_L_Foot", "RIG_L_Toe"], 1),
        ("right foot", "RIG_R_Foot", ["RIG_R_Foot", "RIG_R_Toe"], 1),
    ];

    /// <summary>The share of a part's vertices each capsule covers (the rest stand out of it by a little: the armour's ridges and trims).</summary>
    const float FitCover = 0.88f;

    /// <summary>
    /// The body's fitted capsules (<see cref="SpearClearance.Fitted"/>) are the install's armour: the basic multiplayer armour and the Assault's jump pack in the bind pose, each part's vertices (by the
    /// bone they are skinned to most, or rigid on) split into clusters and a capsule fitted to each (<see cref="FitPlan"/>); the committed numbers match this fit to a centimetre.
    /// </summary>
    [InstallFact]
    public void Body_capsules_fit_the_armour()
    {
        var fitted = FitBody(out var report);
        foreach (var line in report) output.WriteLine(line);
        foreach (var c in fitted)
            output.WriteLine(string.Format(I, "        new(\"{0}\", \"{1}\", new({2:0.000}f, {3:0.000}f, {4:0.000}f), new({5:0.000}f, {6:0.000}f, {7:0.000}f), {8:0.000}f),", c.Part, c.Bone, c.A.X, c.A.Y, c.A.Z, c.B.X, c.B.Y, c.B.Z, c.Radius));
        Assert.Equal(fitted.Count, SpearClearance.Fitted.Count);
        for (int i = 0; i < fitted.Count; i++)
        {
            var (f, c) = (fitted[i], SpearClearance.Fitted[i]);
            Assert.Equal(f.Part, c.Part); Assert.Equal(f.Bone, c.Bone);
            Assert.True(Vector3.Distance(f.A, c.A) < 0.01f && Vector3.Distance(f.B, c.B) < 0.01f && MathF.Abs(f.Radius - c.Radius) < 0.01f, $"capsule {i} ({c.Part}) differs from the fit");
        }
    }

    static List<BodyCapsule> FitBody(out List<string> report)
    {
        report = new();
        var model = CharacterModel.LoadMarine(Game.Value!);
        var sk = model.Rig.Skeleton;
        var ms = sk.ModelSpace(sk.ReferencePose);
        var names = sk.BoneNames.ToList();
        var byBone = new Dictionary<string, List<Vector3>>();
        void Add(string bone, Vector3 p) { if (!byBone.TryGetValue(bone, out var l)) byBone[bone] = l = new(); l.Add(p); }
        foreach (var piece in model.Pieces)
        {
            if (piece.Name.EndsWith("_backpack", StringComparison.OrdinalIgnoreCase)) continue;   // the Assault's jump pack takes the back (WeaponRig hides the backpack under it)
            for (int b = 0; b < piece.Model.Mesh.Blocks.Count; b++)
            {
                if (piece.Hidden?.Contains(b) == true) continue;
                var block = piece.Model.Mesh.Blocks[b];
                if (block.Skin is { } skin)
                    for (int v = 0; v < block.Positions.Length; v++)
                    {
                        var w = skin.Weights[v]; float[] ws = [w.X, w.Y, w.Z, w.W];
                        int best = 0; for (int k = 1; k < 4; k++) if (ws[k] > ws[best]) best = k;
                        Add(skin.Bones[skin.BoneIndices[v * 4 + best]].Name, block.Positions[v]);
                    }
                else if (piece.Rigid is not null && piece.Rigid.TryGetValue(b, out var placed))
                {
                    var toModel = placed.ModelSpace ? placed.Local : placed.Local * ms[names.IndexOf(placed.Bone)];
                    foreach (var p in block.Positions) Add(placed.Bone, Vector3.Transform(p, toModel));
                }
            }
        }
        var notes = new List<string>();
        var weapons = WeaponModels.Load(Game.Value!, notes, ["sm_mp_abil_jumppack"], names);
        foreach (var worn in weapons.Worn("sm_mp_abil_jumppack"))
        {
            var pack = WeaponModel.Load(Game.Value!, worn, notes);
            foreach (var part in pack.Parts.Where(p => p.Visible && !p.Node.Contains("strap", StringComparison.OrdinalIgnoreCase)))   // the straps lie on the chest, inside the torso's capsules
                if (pack.WornPlacement(part, n => names.Contains(n)) is { } on)
                    foreach (var p in pack.Model.Mesh.Blocks[part.BlockIndex].Positions) Add("jump pack", Vector3.Transform(p, on.PartToBone * ms[names.IndexOf(on.Bone)]));
        }
        var result = new List<BodyCapsule>();
        foreach (var (part, bone, sources, k) in FitPlan)
        {
            var pts = sources.SelectMany(s => byBone.GetValueOrDefault(s) ?? []).ToList();
            Assert.True(pts.Count > 20, $"{part}: {pts.Count} vertices");
            Matrix4x4.Invert(ms[names.IndexOf(bone)], out var toBone);
            var local = pts.Select(p => Vector3.Transform(p, toBone)).ToList();
            var min = pts.Aggregate(new Vector3(float.MaxValue), Vector3.Min); var max = pts.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            var capsules = KMeans(local, k).Select(cluster => FitCapsule(cluster, FitCover)).Select(c => new BodyCapsule(part, bone, c.A, c.B, c.R)).ToList();
            float covered = local.Count(p => capsules.Any(c => SpearClearance.SegmentDistance(p, p, c.A, c.B) <= c.Radius + SpearClearance.Tolerance)) / (float)local.Count;
            report.Add(string.Format(I, "{0,-16} {1,5} vertices, bind-pose box {2:0.00} {3:0.00} {4:0.00} .. {5:0.00} {6:0.00} {7:0.00}; {8:0.0} % inside its capsules (+ the tolerance)", part, pts.Count, min.X, min.Y, min.Z, max.X, max.Y, max.Z, covered * 100f));
            result.AddRange(capsules);
        }
        return result;
    }

    /// <summary>k-means (farthest-point start, fixed iterations, so the split is the same every run).</summary>
    static List<List<Vector3>> KMeans(List<Vector3> pts, int k)
    {
        if (k <= 1) return [pts];
        var centres = new List<Vector3> { pts[0] };
        while (centres.Count < k) centres.Add(pts.OrderByDescending(p => centres.Min(c => Vector3.DistanceSquared(p, c))).First());
        var assign = new int[pts.Count];
        for (int it = 0; it < 40; it++)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                int best = 0;
                for (int c = 1; c < k; c++) if (Vector3.DistanceSquared(pts[i], centres[c]) < Vector3.DistanceSquared(pts[i], centres[best])) best = c;
                assign[i] = best;
            }
            for (int c = 0; c < k; c++)
            {
                var members = pts.Where((_, i) => assign[i] == c).ToList();
                if (members.Count > 0) centres[c] = members.Aggregate(Vector3.Zero, (a, x) => a + x) / members.Count;
            }
        }
        return Enumerable.Range(0, k).Select(c => pts.Where((_, i) => assign[i] == c).ToList()).Where(l => l.Count > 0).ToList();
    }

    /// <summary>A capsule along the points' principal axis: of the half-lengths tried, the one with the least volume whose radius covers <paramref name="cover"/> of the points.</summary>
    static (Vector3 A, Vector3 B, float R) FitCapsule(List<Vector3> pts, float cover)
    {
        var c = pts.Aggregate(Vector3.Zero, (a, x) => a + x) / pts.Count;
        var cov = new float[3, 3];
        foreach (var x in pts) { var d = x - c; float[] e = [d.X, d.Y, d.Z]; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) cov[i, j] += e[i] * e[j]; }
        var axis = new Vector3(1f, 0.7f, 0.3f);
        for (int it = 0; it < 60; it++)
            axis = Vector3.Normalize(new Vector3(cov[0, 0] * axis.X + cov[0, 1] * axis.Y + cov[0, 2] * axis.Z, cov[1, 0] * axis.X + cov[1, 1] * axis.Y + cov[1, 2] * axis.Z, cov[2, 0] * axis.X + cov[2, 1] * axis.Y + cov[2, 2] * axis.Z));
        var proj = pts.Select(x => Vector3.Dot(x - c, axis)).OrderBy(x => x).ToList();
        float span = proj[^1] - proj[0], centre = (proj[0] + proj[^1]) * 0.5f;
        (Vector3, Vector3, float) best = default; float bestVolume = float.MaxValue;
        for (int s = 0; s <= 40; s++)
        {
            float half = span * 0.5f * s / 40f;
            var a = c + axis * (centre - half); var b = c + axis * (centre + half);
            var d = pts.Select(x => SpearClearance.SegmentDistance(x, x, a, b)).OrderBy(x => x).ToList();
            float r = d[(int)((d.Count - 1) * cover)];
            float volume = MathF.PI * r * r * 2f * half + 4f / 3f * MathF.PI * r * r * r;
            if (volume < bestVolume) { bestVolume = volume; best = (a, b, r); }
        }
        return best;
    }

    /// <summary>The client's exploration camera distance factor (<c>NetworkView.CameraBackScale</c>, client code: the data's rig times 1.3, owner 2026-10-30).</summary>
    const float CameraBackScale = 1.3f;

    /// <summary>The own third-person camera at <paramref name="pitch"/> (radians) in the skeleton's space (the client's character frame: +X right, +Y the facing, Z up, the feet at 0): the install's exploration rig (the rifle's set, which every weapon but the scoped ones uses), at the default field of view (no shift to the right).</summary>
    static Vector3 OwnCamera(float pitch)
    {
        var tuning = CameraTuning.Load(Game.Value!);
        var rig = CameraRig.Load(Game.Value!);
        var set = tuning.For("sm_mp_ranged_bolter")!.Exploration;
        var at = rig.OffsetAtPitch(set.AnimationCurve, pitch)!.Value.Camera;
        return at with { Y = at.Y * CameraBackScale };
    }

    /// <summary>
    /// The carried blade stays out of the crosshair's column in the own view at idle (owner via the lead, 2026-11-11: the blade crossed it): over the whole hammer idle the spear's head (collar,
    /// crossguard, blade) stands at least <see cref="SpearClearance.CrosshairColumnDegrees"/> to one side of the screen's centre column, the view level; the walk, run and sprint are printed
    /// (not held to it).
    /// </summary>
    [InstallFact]
    public void The_carried_blade_stays_out_of_the_crosshair_column_at_idle()
    {
        var (rig, loco) = Data.Value;
        var mirror = new WeaponPoseMirror(rig.Skeleton);
        var level = OwnCamera(0f);
        output.WriteLine(string.Format(I, "own camera (level): {0:0.00} right, {1:0.00} along the facing, {2:0.00} up", level.X, level.Y, level.Z));
        float Least(string stance, Vector3 camera, float pitch)
        {
            var clip = rig.LoadAnimation(stance);
            float least = float.PositiveInfinity;
            for (float t = 0f; t <= clip.Animation.Duration; t += Step)
            {
                mirror.Lay(clip, t);
                mirror.Overlay(ClawSpearClips.SpearRest, t % ClawSpearClips.SpearIdleLength);
                var (butt, shaft, edge) = SpearHold.Place(mirror.RightHand, mirror.LeftHand, 0f, WeaponPoseClip.CarryGrip, mirror.FrontNow(), Vector3.UnitZ);
                float a = SpearClearance.HeadColumnAngle(butt, shaft, edge, camera, pitch);
                if (MathF.Abs(a) < MathF.Abs(least)) least = a;
            }
            return least;
        }
        string idle = loco.HammerIdle ?? ClawSpearClips.SpearIdle;
        float atIdle = Least(idle, level, 0f);
        output.WriteLine(string.Format(I, "{0,-36} the head's least angle from the column {1,6:0.0} deg (level view)", idle, atIdle));
        foreach (float pitch in new[] { -0.4f, 0.4f })
            output.WriteLine(string.Format(I, "{0,-36} {1,6:0.0} deg at pitch {2:0.0}", idle, Least(idle, OwnCamera(pitch), pitch), pitch));
        foreach (float shift in new[] { 0.1f, 0.2f, 0.35f })   // a wider field of view moves the camera right (FovMath.CameraShift, up to 0.35 m)
            output.WriteLine(string.Format(I, "{0,-36} {1,6:0.0} deg with the camera {2:0.00} m further right", idle, Least(idle, level with { X = level.X + shift }, 0f), shift));
        foreach (var d in loco.Hammer?.Directions ?? [])
            foreach (var s in new[] { d.Walk, d.Run })
                if (s is { } moving) output.WriteLine(string.Format(I, "{0,-36} {1,6:0.0} deg", moving.Clip, Least(moving.Clip, level, 0f)));
        foreach (var c in loco.SprintFor(SprintFamily.Hammer)?.Clips ?? []) output.WriteLine(string.Format(I, "{0,-36} {1,6:0.0} deg", c.Clip, Least(c.Clip, level, 0f)));
        Assert.True(MathF.Abs(atIdle) >= SpearClearance.CrosshairColumnDegrees, $"the carried blade comes within {MathF.Abs(atIdle):0.0} degrees of the crosshair's column at idle (at least {SpearClearance.CrosshairColumnDegrees} wanted)");
    }

    [InstallFact]
    public void The_spear_never_passes_into_the_body_in_any_clip_or_hand_over()
    {
        var run = Sweep();
        foreach (var (clip, w) in run.ByClip.OrderBy(k => k.Value.Result.Clearance))
            output.WriteLine(string.Format(I, "{0,-32} least clearance {1,6:0.000} m  ({2} by the {3}, {4} t {5:0.000})", clip, w.Result.Clearance, w.Result.Part, w.Result.SpearPiece, w.Scenario, w.Time));
        output.WriteLine(string.Format(I, "carried butt lowest {0:0.000} m ({1}); the plain landing's butt lowest {2:0.000} m ({3})", run.LowestButt, run.LowestButtAt, run.LowestLandingButt, run.LowestLandingButtAt));
        var clipping = run.ByClip.Where(k => k.Value.Result.Clearance < -(k.Key is "hand-over dive -> impale" or "hand-over dive -> land" ? LandingHandOverBudget : SpearClearance.Tolerance)).Select(k => $"{k.Key}: {k.Value.Result.Clearance:0.000} m into the {k.Value.Result.Part} ({k.Value.Result.SpearPiece}, {k.Value.Scenario} t {k.Value.Time:0.000})").ToList();
        Assert.True(clipping.Count == 0, "the spear passes into the body:\n" + string.Join("\n", clipping));
        Assert.True(run.LowestButt >= ButtMinHeight, $"the carried butt drags: {run.LowestButt:0.000} m at {run.LowestButtAt}");
        Assert.True(run.LowestLandingButt >= 0f, $"the plain landing drives the butt into the ground: {run.LowestLandingButt:0.000} m at {run.LowestLandingButtAt}");
        Assert.Contains(run.ByClip.Keys, k => k.StartsWith("spear_rest", StringComparison.Ordinal));
        foreach (var clip in ClawSpearClips.All.Where(c => c.Weapon == "spear")) Assert.Contains(run.ByClip.Keys, k => k.StartsWith(clip.Name, StringComparison.Ordinal));
    }
}
