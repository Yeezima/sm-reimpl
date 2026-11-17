using System;
using System.Linq;
using Godot;
using SmFormats.Havok;

namespace SmClient;

/// <summary>
/// Test aid <c>--musou-crowd-test --kinds Boy --react-probe [--lock-yaw]</c> (Ork reaction clips, owner 2026-11-17): prints, for every <c>_REC_</c> clip of the Ork Boy rig, what it does to the body's
/// facing and footing: the turn of the root, pelvis and chest bones from the rest pose (degrees about the vertical) at the start, middle and end, and how far the ankles travel from where they stood
/// at frame 0 (the root's flat motion locked, as the crowd draws it; <c>--lock-yaw</c> also locks the root's turn). One REACTPROBE line per clip. Reads the install at run time; nothing is written.
/// </summary>
public partial class MusouCrowdTestView
{
    void ReactProbe(MusouLooks.Look look)
    {
        var rig = look.Assets.Model.Rig;
        var skeleton = rig.Skeleton;
        int n = skeleton.BoneNames.Count;
        var rest = new Transform3D[n];
        var parents = new int[n];
        for (int i = 0; i < n; i++) { rest[i] = MarineAssets.ToGodot(skeleton.ReferencePose[i]); parents[i] = skeleton.Parents[i]; }
        int Bone(string name) => skeleton.BoneNames.ToList().IndexOf(name);
        int pelvis = Bone("RIG_Pelvis"), chest = Bone("RIG_Spine3"), footL = Bone("RIG_LLeg_Foot"), footR = Bone("RIG_RLeg_Foot");
        bool lockYaw = _args.ContainsKey("lock-yaw");
        var restG = Globals(rest, parents);
        foreach (var name in rig.AnimationNames.Where(a => a.Contains("_REC_", StringComparison.OrdinalIgnoreCase) && a.StartsWith("BOY_", StringComparison.OrdinalIgnoreCase)).OrderBy(a => a, StringComparer.Ordinal))
        {
            var clip = look.Assets.Animation(name);
            if (clip is null) { GD.Print($"REACTPROBE {name}: not bound"); continue; }
            float d = clip.Animation.Duration;
            string line = $"REACTPROBE {name,-38} {d:F2} s tracks {clip.Animation.TrackCount}";
            Vector3 foot0L = default, foot0R = default; float worst = 0f;
            foreach (float f in new[] { 0f, 0.5f, 1f })
            {
                var sample = new HavokTransform[clip.Animation.TrackCount];
                clip.Animation.SampleInto(f * d, sample);
                var local = (Transform3D[])rest.Clone();
                for (int k = 0; k < sample.Length; k++) { int b = clip.TrackToBone[k]; if ((uint)b < (uint)local.Length) local[b] = MarineAssets.ToGodot(sample[k]); }
                MusouVat.LockRoot(local, rest);
                if (lockYaw) local[0] = new Transform3D(rest[0].Basis, local[0].Origin);
                var g = Globals(local, parents);
                if (f == 0f) { foot0L = g[footL].Origin; foot0R = g[footR].Origin; }
                worst = MathF.Max(worst, MathF.Max(g[footL].Origin.DistanceTo(foot0L), g[footR].Origin.DistanceTo(foot0R)));
                line += $" | t{f:0.0}: root {Turn(g[0], restG[0]):F0} pelvis {Turn(g[pelvis], restG[pelvis]):F0} chest {Turn(g[chest], restG[chest]):F0}";
            }
            GD.Print(line + $" | ankle travel {worst * look.Scale:F2} m");
        }
        GetTree().Quit(0);
    }

    static Transform3D[] Globals(Transform3D[] local, int[] parents)
    {
        var g = new Transform3D[local.Length];
        for (int b = 0; b < local.Length; b++) g[b] = parents[b] >= 0 && parents[b] < b ? g[parents[b]] * local[b] : local[b];
        return g;
    }

    /// <summary>How far a bone's own forward has turned from its rest (degrees about the vertical, signed).</summary>
    static float Turn(Transform3D pose, Transform3D rest)
    {
        var f = (pose.Basis * rest.Basis.Inverse()) * new Vector3(0f, 1f, 0f);
        return Mathf.RadToDeg(MathF.Atan2(-f.X, f.Y));
    }
}
