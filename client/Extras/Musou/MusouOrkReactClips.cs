using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmFormats.Havok;
using SmNet.Extras.Musou;

namespace SmClient;

/// <summary>
/// Musou Mode, Ork hit reactions from the rig's own clips (owner 2026-11-17, <see cref="MusouOrkReactRules"/>): the Boy rig's seven additive flinches of a look's hand set, each kept as its pose
/// at its peak (the frame where the head has travelled furthest), laid over the Ork's own pose on the upper body only. The root, pelvis and legs are never touched, so the Ork keeps its facing and
/// its feet stay where its clip put them. Which clip, and how strongly, follows the flinch's direction and size (the track of <see cref="MusouOrkFlinchTrack"/>: the same timing, variety and "no growth"
/// as the procedural flinch). Built once per look from a scratch skeleton: each clip's head travel at weight 1 and the procedural flinch's head travel at size 1 (what the clips are scaled to).
/// </summary>
public sealed class MusouOrkReactClips
{
    sealed class Peak
    {
        public string Name = "";
        public float Time;
        public int[] Bone = [];
        public Vector3[] Axis = [];
        public float[] Angle = [];
        public Vector3[] Position = [];
    }

    readonly Peak[] _peaks;
    readonly System.Numerics.Vector2[] _directions;
    readonly float[] _lengths;
    readonly MusouOrkFlinchPose.Bones _bones;

    /// <summary>The procedural flinch's head travel at size 1 (skeleton units, horizontal): what a clip is scaled to.</summary>
    public float ReferenceLength { get; }
    public int Count => _peaks.Length;
    /// <summary>The clip's name, the horizontal direction its head leans (the Ork's own frame) and the head's travel at weight 1 (skeleton units), for the log.</summary>
    public (string Name, System.Numerics.Vector2 Direction, float Length) Describe(int i) => (_peaks[i].Name, _directions[i], _lengths[i]);

    MusouOrkReactClips(Peak[] peaks, System.Numerics.Vector2[] directions, float[] lengths, float reference, MusouOrkFlinchPose.Bones bones)
    { _peaks = peaks; _directions = directions; _lengths = lengths; ReferenceLength = reference; _bones = bones; }

    /// <summary>
    /// Reads the look's flinch clips and measures them. <paramref name="scratch"/> is a skeleton of the look's rig (not in the tree), <paramref name="poseIdle"/> poses it on the look's idle (clip time 0),
    /// <paramref name="scale"/> the look's scale. Null when the rig lacks the clips (fewer than three) or the bones: the procedural flinch stays.
    /// </summary>
    public static MusouOrkReactClips? Build(MusouLooks.Look look, Skeleton3D scratch, MusouOrkFlinchPose.Bones bones, Action poseIdle, float scale)
    {
        bool ranged = look.Clips[MusouLooks.Run]?.Name.Contains("2HR", StringComparison.OrdinalIgnoreCase) == true;
        var found = new List<(string Name, HavokBoundAnimation Clip)>();
        foreach (var name in MusouOrkReactRules.ClipNames(ranged))
            if (look.Assets.Animation(name) is { } clip) found.Add((name, clip));
        if (found.Count < 3) return null;

        int n = scratch.GetBoneCount();
        var upper = new bool[n];
        for (int b = 0; b < n; b++)
            for (int a = b; a >= 0; a = scratch.GetBoneParent(a)) if (a == bones.Spine1) { upper[b] = true; break; }
        poseIdle();
        var baseRot = new Quaternion[n]; var basePos = new Vector3[n];
        for (int b = 0; b < n; b++) { baseRot[b] = scratch.GetBonePoseRotation(b); basePos[b] = scratch.GetBonePosePosition(b); }
        void Reset() { for (int b = 0; b < n; b++) { scratch.SetBonePoseRotation(b, baseRot[b]); scratch.SetBonePosePosition(b, basePos[b]); } }
        Vector3 head0 = scratch.GetBoneGlobalPose(bones.Head).Origin;
        float Travel(out System.Numerics.Vector2 direction)
        {
            var d = scratch.GetBoneGlobalPose(bones.Head).Origin - head0;
            direction = new System.Numerics.Vector2(d.X, d.Y);
            return direction.Length();
        }

        var peaks = new List<Peak>(); var dirs = new List<System.Numerics.Vector2>(); var lens = new List<float>();
        var sample = new HavokTransform[found[0].Clip.Animation.TrackCount];
        foreach (var (name, clip) in found)
        {
            if (sample.Length != clip.Animation.TrackCount) sample = new HavokTransform[clip.Animation.TrackCount];
            Peak? best = null; float bestLength = 0f; var bestDir = System.Numerics.Vector2.Zero;
            const int steps = 24;
            for (int k = 0; k <= steps; k++)
            {
                float t = clip.Animation.Duration * k / steps;
                var peak = Sample(clip, sample, t, upper, name);
                Reset();
                Lay(scratch, peak, 1f);
                float len = Travel(out var dir);
                Reset();
                if (len > bestLength) { bestLength = len; best = peak; bestDir = dir / MathF.Max(len, 1e-9f); }
            }
            if (best is null || bestLength < 1e-4f) continue;
            peaks.Add(best); dirs.Add(bestDir); lens.Add(bestLength);
        }
        if (peaks.Count < 3) return null;

        // The procedural flinch at size 1 from the front (the Ork's frame: pushed back along -Y): its head's travel.
        Reset();
        MusouOrkFlinchPose.Apply(scratch, bones, new Vector3(0f, -1f, 0f), scale);
        float reference = Travel(out _);
        Reset();
        MusouOrkFlinchPose.LastFootDrift = 0f; MusouOrkFlinchPose.WorstFootDrift = 0f;
        if (!(reference > 1e-4f)) return null;
        return new MusouOrkReactClips([.. peaks], [.. dirs], [.. lens], reference, bones);
    }

    /// <summary>The clip's additive deltas at <paramref name="time"/> on the upper-body bones (rotation as axis and angle, so a weight scales it past 1).</summary>
    static Peak Sample(HavokBoundAnimation clip, HavokTransform[] buffer, float time, bool[] upper, string name)
    {
        clip.Animation.SampleInto(time, buffer);
        var bone = new List<int>(); var axis = new List<Vector3>(); var angle = new List<float>(); var pos = new List<Vector3>();
        for (int t = 0; t < buffer.Length; t++)
        {
            int b = clip.TrackToBone[t];
            if ((uint)b >= (uint)upper.Length || !upper[b]) continue;
            var q = new Quaternion(buffer[t].Rotation.X, buffer[t].Rotation.Y, buffer[t].Rotation.Z, buffer[t].Rotation.W).Normalized();
            float a = q.GetAngle();
            if (a > MathF.PI) a -= 2f * MathF.PI;   // the short way round
            var ax = q.GetAxis();
            bone.Add(b); axis.Add(a == 0f || !ax.IsFinite() || ax.LengthSquared() < 1e-8f ? Vector3.Up : ax.Normalized()); angle.Add(float.IsFinite(a) ? a : 0f);
            pos.Add(new Vector3(buffer[t].Translation.X, buffer[t].Translation.Y, buffer[t].Translation.Z));
        }
        return new Peak { Name = name, Time = time, Bone = [.. bone], Axis = [.. axis], Angle = [.. angle], Position = [.. pos] };
    }

    /// <summary>Lays a peak over the skeleton's current pose at <paramref name="weight"/> (the Havok additive convention, as <c>CharacterViewer</c>: rotation = delta^w * base, position + delta * w).</summary>
    static void Lay(Skeleton3D sk, Peak p, float weight)
    {
        for (int i = 0; i < p.Bone.Length; i++)
        {
            int b = p.Bone[i];
            sk.SetBonePoseRotation(b, (new Quaternion(p.Axis[i], p.Angle[i] * weight) * sk.GetBonePoseRotation(b)).Normalized());
            sk.SetBonePosePosition(b, sk.GetBonePosePosition(b) + p.Position[i] * weight);
        }
    }

    /// <summary>Test aid: the clip taken by the last <see cref="Apply"/> and the weight it was laid at.</summary>
    public static string LastClip = ""; public static float LastWeight;

    /// <summary>
    /// Lays the flinch of <paramref name="kick"/> (horizontal, in the Ork's own frame: its length the size in reference sizes, its direction away from the hit) over the posed skeleton:
    /// the clip that leans the head that way at the weight that makes the head travel what the procedural flinch's would. Records the worse ankle's drift in <see cref="MusouOrkFlinchPose.LastFootDrift"/> (zero by construction).
    /// </summary>
    public void Apply(Skeleton3D sk, Vector3 kick, int id, float scale)
    {
        float size = kick.Length();
        if (size < 1e-4f) return;
        size *= MusouOrkFlinchRules.TestScale;
        int i = MusouOrkReactRules.Pick(new System.Numerics.Vector2(kick.X, kick.Y), _directions, id, out float sign);
        if (i < 0) return;
        float w = MusouOrkReactRules.Weight(size, ReferenceLength, _lengths[i], sign);
        if (w == 0f) return;
        Vector3 ankleL = sk.GetBoneGlobalPose(_bones.Leg[2]).Origin, ankleR = sk.GetBoneGlobalPose(_bones.Leg[5]).Origin;
        Lay(sk, _peaks[i], w);
        LastClip = _peaks[i].Name; LastWeight = w;
        MusouOrkFlinchPose.LastFootDrift = MathF.Max(sk.GetBoneGlobalPose(_bones.Leg[2]).Origin.DistanceTo(ankleL), sk.GetBoneGlobalPose(_bones.Leg[5]).Origin.DistanceTo(ankleR)) * scale;
        MusouOrkFlinchPose.WorstFootDrift = MathF.Max(MusouOrkFlinchPose.WorstFootDrift, MusouOrkFlinchPose.LastFootDrift);
    }
}
