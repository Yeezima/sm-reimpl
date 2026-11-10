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
        /// <summary>Per clip, the farthest a holding hand's grip socket is from the shaft's axis (m) and where.</summary>
        public readonly Dictionary<string, (float Off, string At)> Palm = new();
        /// <summary>Per clip, while both hands hold the shaft: the closest the two grip sockets come (m), the farthest up the shaft the left one sits (m from the butt), and where.</summary>
        public readonly Dictionary<string, (float Spacing, string SpacingAt, float LeftAt, string LeftAtWhere)> Hands = new();
        public float LowestButt = float.PositiveInfinity, LowestLandingButt = float.PositiveInfinity;
        public string LowestButtAt = "", LowestLandingButtAt = "";

        /// <summary>Lays <paramref name="clip"/> (faded from <paramref name="from"/>) over the pose the mirror holds and measures the spear placed on the hands.</summary>
        public SpearClearanceResult Measure(string key, string scenario, float time, WeaponPoseClip clip, float clipTime, WeaponPoseClip? from = null, float fromTime = 0f, float share = 0f, bool carry = false, bool land = false)
        {
            mirror.Overlay(clip, clipTime, from, fromTime, share);
            float grip = clip.GripAt(clipTime), point = clip.GripPointAt(clipTime);
            if (from is not null && share > 0f) { grip += (from.GripAt(fromTime) - grip) * share; point += (from.GripPointAt(fromTime) - point) * share; }
            var (butt, shaft, edge) = SpearHold.Place(mirror.RightHand, mirror.LeftHand, grip, point, mirror.SpearFront(clip), Vector3.UnitZ);
            var r = SpearClearance.Measure(mirror.Pose, butt, shaft, edge);
            if (!float.IsFinite(r.Clearance)) throw new InvalidOperationException($"a clearance that is not a finite number: {key} at {clipTime:0.000} s");   // a NaN would never count as the worst
            if (!ByClip.TryGetValue(key, out var worst) || r.Clearance < worst.Result.Clearance) ByClip[key] = new Worst(scenario, time, r);
            // The grip (owner, 2026-11-11: "the spear is always attached to the inside palm of the hand"): the shaft's axis through the right hand's grip socket on every frame, and through the
            // left's whenever the left hand holds the shaft (its grip track at 1); the sockets read here by name, apart from the placement.
            // The palms are on the shaft by construction (SpearHold.Place builds the axis through the two sockets; review of 2026-11-11): what the arms decide is how far apart the hands are and
            // where the left one sits on the shaft, so both are held here: the hands never closer than HandSpacingMin, the left on the spear (LeftHandMaxUp).
            if (grip >= 0.999f)
            {
                float spacing = Vector3.Distance(mirror.RightHand, mirror.LeftHand), leftAt = point + spacing;
                var h = Hands.TryGetValue(key, out var hv) ? hv : (float.PositiveInfinity, "", 0f, "");
                if (spacing < h.Item1) h = (spacing, $"{scenario} t {time:0.000}", h.Item3, h.Item4);
                if (leftAt > h.Item3) h = (h.Item1, h.Item2, leftAt, $"{scenario} t {time:0.000}");
                Hands[key] = h;
            }
            foreach (var (socket, holds) in new[] { (SpearHold.RightGripBone, true), (SpearHold.LeftGripBone, grip >= 0.999f) })
            {
                if (!holds || mirror.Pose(socket) is not { } palm) continue;
                float off = SpearClearance.SegmentDistance(palm.Item1, palm.Item1, butt, butt + shaft * SpearHold.TipFromButt);
                if (!Palm.TryGetValue(key, out var p) || off > p.Off) Palm[key] = (off, $"{socket} {scenario} t {time:0.000}");
            }
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
            mirror.FistWeight = name == stances[0] ? 0f : 1f;   // the carry's fist solve comes in with the speed (WeaponPoseState.FistWeight): off standing, on in the walk, run and sprint
            for (float t = 0f; t <= stance.Animation.Duration; t += Step)
            {
                mirror.Lay(stance, t);
                run.Measure("spear_rest (carry)", name, t, ClawSpearClips.SpearRest, t % ClawSpearClips.SpearIdleLength, carry: true);
            }
        }
        mirror.FistWeight = 0f;
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
    /// The carried blade stays out of the crosshair's column in the own view (owner via the lead, 2026-11-11: the blade crossed it): over the whole hammer idle and every walk and run direction
    /// (pass 3, 2026-11-11: the run too, with the carry's fist solve laid as the speed lays it in a match) the spear's head (collar, crossguard, blade) stands at least
    /// <see cref="SpearClearance.CrosshairColumnDegrees"/> to one side of the screen's centre column, the view level; the sprints are printed (the right strafe sprint's arm cannot reach in far
    /// enough: open).
    /// </summary>
    [InstallFact]
    public void The_carried_blade_stays_out_of_the_crosshair_column_standing_walking_and_running()
    {
        var (rig, loco) = Data.Value;
        var mirror = new WeaponPoseMirror(rig.Skeleton);
        var level = OwnCamera(0f);
        output.WriteLine(string.Format(I, "own camera (level): {0:0.00} right, {1:0.00} along the facing, {2:0.00} up", level.X, level.Y, level.Z));
        string idleName = loco.HammerIdle ?? ClawSpearClips.SpearIdle;
        float Least(string stance, Vector3 camera, float pitch)
        {
            var clip = rig.LoadAnimation(stance);
            mirror.FistWeight = stance == idleName ? 0f : 1f;
            float least = float.PositiveInfinity;
            for (float t = 0f; t <= clip.Animation.Duration; t += Step)
            {
                mirror.Lay(clip, t);
                mirror.Overlay(ClawSpearClips.SpearRest, t % ClawSpearClips.SpearIdleLength);
                var (butt, shaft, edge) = SpearHold.Place(mirror.RightHand, mirror.LeftHand, 0f, WeaponPoseClip.CarryGrip, mirror.SpearFront(ClawSpearClips.SpearRest), Vector3.UnitZ);
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
        var failures = new List<string>();
        if (MathF.Abs(atIdle) < SpearClearance.CrosshairColumnDegrees) failures.Add($"idle: {atIdle:0.0} deg");
        int moving = 0;
        foreach (var d in loco.Hammer?.Directions ?? [])
            foreach (var s in new[] { d.Walk, d.Run })
                if (s is { } m)
                {
                    float a = Least(m.Clip, level, 0f); moving++;
                    output.WriteLine(string.Format(I, "{0,-36} {1,6:0.0} deg", m.Clip, a));
                    if (MathF.Abs(a) < SpearClearance.CrosshairColumnDegrees) failures.Add($"{m.Clip}: {a:0.0} deg");
                }
        foreach (var c in loco.SprintFor(SprintFamily.Hammer)?.Clips ?? []) output.WriteLine(string.Format(I, "{0,-36} {1,6:0.0} deg (sprint: printed)", c.Clip, Least(c.Clip, level, 0f)));
        Assert.True(moving >= 16, $"the hammer stance's walk and run directions not found ({moving})");
        Assert.True(failures.Count == 0, $"the carried blade comes within {SpearClearance.CrosshairColumnDegrees} degrees of the crosshair's column:\n" + string.Join("\n", failures));
    }

    /// <summary>The least horizontal travel of a light hew's blade tip round him from its wind-up apex to its follow-through (degrees; pass 3, owner 2026-11-11: "the spear properly swinging out wide").</summary>
    const float HewArcMin = 180f;

    /// <summary>
    /// The blade tip's horizontal travel round the pelvis's vertical line (degrees, unwrapped, the greatest less the least) from the clip's wind-up apex to 0.25 s past its follow-through, over the
    /// hammer idle at phase 0, and the tip's least and greatest reach from that line (m).
    /// </summary>
    static (float Arc, float MinReach, float MaxReach) TipArc(WeaponPoseMirror mirror, HavokBoundAnimation idle, WeaponPoseClip clip)
    {
        float prev = float.NaN, u = 0f, lo = float.PositiveInfinity, hi = float.NegativeInfinity, minR = float.PositiveInfinity, maxR = 0f;
        for (float t = clip.Anticipation; t <= clip.Follow + 0.25f + 1e-4f; t += 1f / 120f)
        {
            mirror.Lay(idle, t % ClawSpearClips.SpearIdleLength);
            mirror.Overlay(clip, t);
            var (butt, shaft, _) = SpearHold.Place(mirror.RightHand, mirror.LeftHand, clip.GripAt(t), clip.GripPointAt(t), mirror.SpearFront(clip), Vector3.UnitZ);
            var tip = butt + shaft * SpearHold.TipFromButt;
            var pelvis = mirror.Pose("RIG_Pelvis")!.Value.Item1;
            var rel = new Vector2(tip.X - pelvis.X, tip.Y - pelvis.Y);
            float az = MathF.Atan2(rel.X, rel.Y) * 180f / MathF.PI;
            if (float.IsNaN(prev)) u = az; else { float d = az - prev; while (d > 180f) d -= 360f; while (d < -180f) d += 360f; u += d; }
            prev = az; lo = MathF.Min(lo, u); hi = MathF.Max(hi, u); minR = MathF.Min(minR, rel.Length()); maxR = MathF.Max(maxR, rel.Length());
        }
        return (hi - lo, minR, maxR);
    }

    /// <summary>
    /// Each light hew swings the blade out wide (owner, 2026-11-11: "most of the light attack swings don't feature the spear properly swinging out wide"; pass 3): its tip travels at least
    /// <see cref="HewArcMin"/> degrees round him from the wind-up to the follow-through (before pass 3: 105, 140 and 176 degrees by this measure), at full reach.
    /// </summary>
    [InstallFact]
    public void Each_light_hew_swings_the_blade_out_wide()
    {
        var (rig, _) = Data.Value;
        var mirror = new WeaponPoseMirror(rig.Skeleton);
        var idle = rig.LoadAnimation(ClawSpearClips.SpearIdle);
        var failures = new List<string>();
        foreach (var clip in new[] { ClawSpearClips.SpearHew1, ClawSpearClips.SpearHew2, ClawSpearClips.SpearHew3, ClawSpearClips.SpearStun, ClawSpearClips.SpearStab })
        {
            var (arc, minR, maxR) = TipArc(mirror, idle, clip);
            output.WriteLine(string.Format(I, "{0,-12} the tip travels {1,6:0.0} deg round him, {2:0.00} to {3:0.00} m out", clip.Name, arc, minR, maxR));
            if (clip.Name.StartsWith("spear_hew", StringComparison.Ordinal) && (arc < HewArcMin || minR < 1.8f)) failures.Add($"{clip.Name}: {arc:0.0} deg, the tip {minR:0.00} m out at the least");
        }
        Assert.True(failures.Count == 0, "a light hew does not swing out wide:\n" + string.Join("\n", failures));
    }

    /// <summary>How far the simulation's lunge carries the body in this test (m, along the facing, from the wind-up apex to 0.1 s past the hit; a stand-in for the step's root motion and lock-on lunge).</summary>
    const float TestLunge = 0.8f;

    /// <summary>
    /// In a match the planted feet stay put while the lunge moves the body (pass 3, owner 2026-11-11: no sliding): each strike over the hammer idle with the body carried <see cref="TestLunge"/> along
    /// the facing, the overlay's legs through the client's world-space foot pin (<see cref="FootPin"/>, the mirror as <c>WeaponPoseOverlay</c> runs it): a foot the clip has on the ground and the
    /// pin holds moves at most <see cref="FootSlide"/> a frame in the world; without the pin the same foot slides with the body (printed: the test sees the slide).
    /// </summary>
    [InstallFact]
    public void Planted_feet_hold_their_spots_in_the_world_while_the_lunge_moves_the_body()
    {
        var (rig, _) = Data.Value;
        var mirror = new WeaponPoseMirror(rig.Skeleton);
        var idle = rig.LoadAnimation(ClawSpearClips.SpearIdle);
        string[] feet = ["RIG_L_Foot", "RIG_R_Foot"];
        var failures = new List<string>();
        foreach (var clip in ClawSpearClips.SpearStrikes)
        {
            float worstPinned = 0f, worstFree = 0f; string at = ""; int pinnedFrames = 0;
            foreach (bool pinOn in new[] { true, false })
            {
                var pin = new FootPin();
                var last = new Vector3?[2];
                for (float t = 0f; t <= clip.Duration + 1e-4f; t += Step)
                {
                    float u = Math.Clamp((t - clip.Anticipation) / (clip.HitTime + 0.1f - clip.Anticipation), 0f, 1f);
                    var toWorld = Matrix4x4.CreateTranslation(0f, TestLunge * u * u * (3f - 2f * u), 0f);
                    mirror.Lay(idle, t % ClawSpearClips.SpearIdleLength);
                    mirror.Overlay(clip, t, null, 0f, 0f, 1f, pinOn ? pin : null, toWorld, Step, pinActive: true);
                    for (int i = 0; i < 2; i++)
                    {
                        var ch = clip.Channels.FirstOrDefault(c => c.Bone == feet[i] && c.Kind == PoseChannelKind.Foot);
                        bool planted = ch is null || ch.Sample(t).Up < FootPin.PlantedBelow;
                        bool held = pinOn ? pin.IsPinned(i) : planted;
                        var world = Vector3.Transform(mirror.Pose(feet[i])!.Value.Item1, toWorld);
                        if (held && planted && last[i] is { } before)
                        {
                            float moved = Vector3.Distance(world, before);
                            if (pinOn) { pinnedFrames++; if (moved > worstPinned) { worstPinned = moved; at = $"{feet[i]} t {t:0.000}"; } }
                            else worstFree = MathF.Max(worstFree, moved);
                        }
                        last[i] = held && planted ? world : null;
                    }
                }
            }
            output.WriteLine(string.Format(I, "{0,-12} a pinned planted foot moves at most {1:0.0000} m a frame in the world ({2}; {3} frames held); without the pin {4:0.000} m a frame", clip.Name, worstPinned, at, pinnedFrames, worstFree));
            if (worstPinned > FootSlide) failures.Add($"{clip.Name}: a pinned foot slides {worstPinned:0.0000} m in a frame ({at})");
            if (pinnedFrames < 30) failures.Add($"{clip.Name}: the pin held a planted foot for only {pinnedFrames} frames");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>How far a planted foot may stand from its spot (m; ours): the leg reaches it, so the foot does not float.</summary>
    const float FootReach = 0.02f;
    /// <summary>How far a planted foot may drift in a frame beyond the stance's own (m; ours): it does not slide.</summary>
    const float FootSlide = 0.004f;

    /// <summary>
    /// The spear's steps plant the feet (owner, 2026-11-11: "Ensure the step forward has proper leg movement to match it with no floating"): over every strike and the landings, sampled every
    /// 1/60 s on the install's rig, a foot whose step key is on the ground (its lift 0) stands on its spot (the stance's plus the step's offset, within <see cref="FootReach"/>: the leg reaches
    /// it), does not move while its offset holds (within <see cref="FootSlide"/> a frame beyond the stance's own motion), and no foot ever goes below the stance's ground.
    /// </summary>
    [InstallFact]
    public void The_spears_steps_plant_the_feet_without_floating_or_sliding()
    {
        var (rig, _) = Data.Value;
        var mirror = new WeaponPoseMirror(rig.Skeleton);
        var idle = rig.LoadAnimation(ClawSpearClips.SpearIdle);
        var impact = rig.LoadAnimation(ClawSpearClips.SpearDiveImpact);
        string[] feet = ["RIG_L_Foot", "RIG_R_Foot"];
        var failures = new List<string>();
        foreach (var clip in ClawSpearClips.SpearStrikes.Append(ClawSpearClips.SpearImpale).Append(ClawSpearClips.SpearLand))
        {
            var under = clip.BaseClip == ClawSpearClips.SpearDiveImpact ? impact : idle;
            float worstReach = 0f, worstSlide = 0f, worstDip = 0f; string reachAt = "", slideAt = "", dipAt = "";
            var last = new (Vector3 Actual, Vector3 Base, Vector3 Offset, bool Planted)[2];
            bool have = false;
            for (float t = 0f; t <= clip.Duration + 1e-4f; t += Step)
            {
                mirror.Lay(under, under == idle ? t % ClawSpearClips.SpearIdleLength : t);
                var across = mirror.Pose("RIG_R_Thigh")!.Value.Item1 - mirror.Pose("RIG_L_Thigh")!.Value.Item1; across.Z = 0f;
                var right = Vector3.Normalize(across); var front = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, right));
                var bases = feet.Select(f => mirror.Pose(f)!.Value.Item1).ToArray();
                mirror.Overlay(clip, t);
                for (int i = 0; i < 2; i++)
                {
                    var ch = clip.Channels.FirstOrDefault(c => c.Bone == feet[i] && c.Kind == PoseChannelKind.Foot);
                    var k = ch?.Sample(t) ?? default;
                    var offset = ch is null ? Vector3.Zero : front * k.Forward + right * k.Right + Vector3.UnitZ * k.Up;
                    bool planted = ch is null || k.Up < 1e-3f;
                    var actual = mirror.Pose(feet[i])!.Value.Item1;
                    float dip = bases[i].Z - actual.Z;
                    if (dip > worstDip) { worstDip = dip; dipAt = $"{feet[i]} t {t:0.000}"; }
                    if (planted)
                    {
                        float off = Vector3.Distance(actual, bases[i] + offset);
                        if (off > worstReach) { worstReach = off; reachAt = $"{feet[i]} t {t:0.000}"; }
                        if (have && last[i].Planted && Vector3.Distance(offset, last[i].Offset) < 1e-4f)
                        {
                            float slide = Vector3.Distance(actual - last[i].Actual, bases[i] - last[i].Base);
                            if (slide > worstSlide) { worstSlide = slide; slideAt = $"{feet[i]} t {t:0.000}"; }
                        }
                    }
                    last[i] = (actual, bases[i], offset, planted);
                }
                have = true;
            }
            output.WriteLine(string.Format(I, "{0,-14} planted feet off their spots {1:0.000} m ({2}), sliding {3:0.0000} m a frame ({4}), below the ground {5:0.000} m ({6})", clip.Name, worstReach, reachAt, worstSlide, slideAt, worstDip, dipAt));
            if (worstReach > FootReach) failures.Add($"{clip.Name}: a planted foot floats {worstReach:0.000} m off its spot ({reachAt})");
            if (worstSlide > FootSlide) failures.Add($"{clip.Name}: a planted foot slides {worstSlide:0.0000} m in a frame ({slideAt})");
            if (worstDip > 0.01f) failures.Add($"{clip.Name}: a foot goes {worstDip:0.000} m below the ground ({dipAt})");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// The least distance between the two grip sockets while both hands hold the shaft (m; ours: the sockets sit in the palms, so two gauntlets touching along the shaft keep them about this far
    /// apart). Measured (pass 3): 0.07 m at the closest, between the hews' keys, where the left hand slides down the shaft to the right hand.
    /// </summary>
    const float HandSpacingMin = 0.06f;
    /// <summary>
    /// The farthest up the shaft the left hand may sit (m from the butt): on the spear, short of the blade's last 0.1 m. Measured (pass 3): 1.4 m in the hews; 2.67 m in the impale's drive and
    /// the dive's hand-over into it, past the collar (1.89 m) onto the blade's fittings, as approved in pass 2 (open: docs/claw-spear-animations.md section 3f).
    /// </summary>
    const float LeftHandMaxUp = SpearHold.TipFromButt - 0.1f;

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
        var offPalm = run.Palm.Where(k => k.Value.Off > SpearHold.PalmTolerance).Select(k => $"{k.Key}: {k.Value.Off:0.000} m from the palm ({k.Value.At})").ToList();
        output.WriteLine(string.Format(I, "the shaft's farthest from a holding palm: {0:0.0000} m", run.Palm.Values.Max(p => p.Off)));
        Assert.True(offPalm.Count == 0, "the shaft leaves the palm:\n" + string.Join("\n", offPalm));
        foreach (var (clip, h) in run.Hands.OrderBy(k => k.Key))
            output.WriteLine(string.Format(I, "{0,-32} hands at least {1:0.00} m apart ({2}); the left hand at most {3:0.00} m up the shaft ({4})", clip, h.Spacing, h.SpacingAt, h.LeftAt, h.LeftAtWhere));
        var badHands = run.Hands.Where(k => k.Value.Spacing < HandSpacingMin || k.Value.LeftAt > LeftHandMaxUp)
            .Select(k => $"{k.Key}: hands {k.Value.Spacing:0.00} m apart ({k.Value.SpacingAt}), the left hand {k.Value.LeftAt:0.00} m up the shaft ({k.Value.LeftAtWhere})").ToList();
        Assert.True(badHands.Count == 0, "a hand off the shaft's grip:\n" + string.Join("\n", badHands));
        Assert.Contains(run.Palm.Keys, k => k.StartsWith("spear_rest", StringComparison.Ordinal));
        Assert.Contains(run.ByClip.Keys, k => k.StartsWith("spear_rest", StringComparison.Ordinal));
        foreach (var clip in ClawSpearClips.All.Where(c => c.Weapon == "spear")) Assert.Contains(run.ByClip.Keys, k => k.StartsWith(clip.Name, StringComparison.Ordinal));
    }
}
