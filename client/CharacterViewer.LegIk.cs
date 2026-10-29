using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using SmFormats.Havok;

namespace SmClient;

/// <summary>
/// Leg correction for blended locomotion (M40-T4; ours). Blending the local rotations of two walk or run clips that differ (a back diagonal and the back
/// run, a strafe and a diagonal) bends the legs to a pose neither clip has: measured on the install the blended toe sinks up to 7 cm below the ground
/// and the planted foot drifts. Positions, unlike rotations, blend linearly: the ankle of the blended pose should be at the weighted mean of the clips'
/// ankles (model space), whose planted ankle then moves back at exactly the weighted mean of the clips' planted speeds (the gait maths in
/// <c>SmContent.GaitSpace</c> relies on it). So after a blend layer's pose is built, each leg is solved by two-bone IK (thigh, calf; the foot keeps its
/// blended orientation) to reach that mean ankle position, bending the knee the way the blended pose already does. The amount is the blend layers'
/// share of the pose, so cross-fades from other stances and clips ease in; a pose without blend layers is untouched. Not the ground-adaptive foot IK of the
/// audit's T11 (no ground probing): only the blend's own leg geometry.
/// </summary>
public partial class CharacterViewer
{
    sealed class Leg
    {
        public int Pelvis, Thigh, Calf, Foot;
        public int[] Chain = Array.Empty<int>();   // root first, down to the foot
    }

    Leg[]? _legs;
    bool _legsResolved;
    readonly Vector3[] _ikTarget = new Vector3[2];
    float _ikWeight;
    int _ikClips;
    static readonly ConditionalWeakTable<HavokBoundAnimation, int[]> TrackOfBone = new();
    /// <summary>Test aid counters (<c>--anim-probe</c>): legs corrected, those whose wanted ankle was out of reach (the leg is then straight and the ankle short of it), the largest shortfall and the largest correction (m).</summary>
    public static int IkLegs, IkUnreachable, IkSingleLegs;
    public static float IkSingleMoveSum, IkSingleMoveMax;
    public static float IkWorstShortfall, IkWorstMove;
    public static string IkWorstWhere = "";
    /// <summary><c>--no-leg-ik</c> (test aid): the blended poses as the plain nlerp.</summary>
    public static readonly bool NoLegIk = OS.GetCmdlineUserArgs().Contains("--no-leg-ik");

    void ResolveLegs()
    {
        if (_legsResolved || _assets is null) return;
        _legsResolved = true;
        var sk = _assets.Model.Rig.Skeleton;
        int Find(string n) { for (int i = 0; i < sk.BoneNames.Count; i++) if (string.Equals(sk.BoneNames[i], n, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
        var legs = new List<Leg>();
        foreach (var side in new[] { "L", "R" })
        {
            // The marine's bones are RIG_L_Thigh ...; the Ork boy's RIG_LLeg_Thigh ... (Ork O7a).
            int thigh = Find($"RIG_{side}_Thigh"), calf = Find($"RIG_{side}_Calf"), foot = Find($"RIG_{side}_Foot");
            if (thigh < 0) { thigh = Find($"RIG_{side}Leg_Thigh"); calf = Find($"RIG_{side}Leg_Calf"); foot = Find($"RIG_{side}Leg_Foot"); }
            if (thigh < 0 || calf < 0 || foot < 0 || sk.Parents[thigh] < 0) return;
            var chain = new List<int>();
            for (int b = foot; b >= 0; b = sk.Parents[b]) chain.Add(b);
            chain.Reverse();
            legs.Add(new Leg { Pelvis = sk.Parents[thigh], Thigh = thigh, Calf = calf, Foot = foot, Chain = chain.ToArray() });
        }
        _legs = legs.ToArray();
    }

    void IkBegin() { _ikClips = 0; _ikWeight = 0f; _ikTarget[0] = _ikTarget[1] = Vector3.Zero; }

    /// <summary>Adds a blended clip's ankles (model space, from forward kinematics of the sampled pose) to the target, with its weight.</summary>
    void IkAdd(HavokBoundAnimation clip, HavokTransform[] sampled, float weight)
    {
        ResolveLegs();
        if (_legs is null || weight <= 0f || _assets is null) return;
        var sk = _assets.Model.Rig.Skeleton;
        if (!TrackOfBone.TryGetValue(clip, out var trackOf))
        {
            trackOf = new int[sk.BoneNames.Count];
            Array.Fill(trackOf, -1);
            for (int t = 0; t < clip.TrackToBone.Length; t++) if ((uint)clip.TrackToBone[t] < (uint)trackOf.Length) trackOf[clip.TrackToBone[t]] = t;
            TrackOfBone.Add(clip, trackOf);
        }
        for (int l = 0; l < _legs.Length; l++)
        {
            System.Numerics.Matrix4x4 model = System.Numerics.Matrix4x4.Identity;
            foreach (int b in _legs[l].Chain)
            {
                var local = trackOf[b] >= 0 ? sampled[trackOf[b]] : sk.ReferencePose[b];
                model = local.ToMatrix() * model;   // row-vector convention: child * parent
            }
            _ikTarget[l] += new Vector3(model.M41, model.M42, model.M43) * weight;
        }
        _ikWeight += weight;
        _ikClips++;
    }

    /// <summary>Moves each ankle toward the weighted mean ankle of the blended clips by <paramref name="amount"/> (0 to 1) and bends the knee to reach it.</summary>
    void IkApply(float amount)
    {
        if (_legs is null || _skeleton is null || _ikWeight <= 1e-4f || amount <= 1e-3f || NoLegIk) return;
        for (int i = 0; i < _legs.Length; i++)
        {
            var leg = _legs[i];
            var target = _ikTarget[i] / _ikWeight;
            var pelvis = _skeleton.GetBoneGlobalPose(leg.Pelvis);
            var thigh = _skeleton.GetBoneGlobalPose(leg.Thigh);
            var calf = _skeleton.GetBoneGlobalPose(leg.Calf);
            var foot = _skeleton.GetBoneGlobalPose(leg.Foot);
            Vector3 hip = thigh.Origin, knee = calf.Origin, ankle = foot.Origin;
            var wanted = ankle.Lerp(target, Math.Clamp(amount, 0f, 1f));
            float l1 = hip.DistanceTo(knee), l2 = knee.DistanceTo(ankle);
            var toWanted = wanted - hip;
            float distance = toWanted.Length();
            if (l1 < 1e-4f || l2 < 1e-4f || distance < 1e-4f) continue;
            float d = Math.Clamp(distance, MathF.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f);
            IkLegs++;
            if (_ikClips == 1) { IkSingleLegs++; IkSingleMoveSum += ankle.DistanceTo(wanted); IkSingleMoveMax = MathF.Max(IkSingleMoveMax, ankle.DistanceTo(wanted)); }
            if (MathF.Abs(d - distance) > 1e-3f) { IkUnreachable++; IkWorstShortfall = MathF.Max(IkWorstShortfall, MathF.Abs(d - distance)); }
            if (ankle.DistanceTo(wanted) > IkWorstMove) { IkWorstMove = ankle.DistanceTo(wanted); if (AnimProbe.Enabled) IkWorstWhere = LayerDebug() + $" amount {amount:F2}"; }
            SolveLeg(leg, wanted);
        }
    }

    /// <summary>
    /// Two-bone IK of one leg (thigh, calf; the foot keeps its orientation relative to the calf): the ankle goes to <paramref name="wanted"/> (skeleton space), as far as the leg
    /// reaches, the knee bending the way the pose already bends it. Used by the blend's leg correction and by the foot pins (<c>CharacterViewer.FootPin.cs</c>).
    /// </summary>
    void SolveLeg(Leg leg, Vector3 wanted)
    {
        var pelvis = _skeleton!.GetBoneGlobalPose(leg.Pelvis);
        var thigh = _skeleton.GetBoneGlobalPose(leg.Thigh);
        var calf = _skeleton.GetBoneGlobalPose(leg.Calf);
        var foot = _skeleton.GetBoneGlobalPose(leg.Foot);
        Vector3 hip = thigh.Origin, knee = calf.Origin, ankle = foot.Origin;
        float l1 = hip.DistanceTo(knee), l2 = knee.DistanceTo(ankle);
        var toWanted = wanted - hip;
        float distance = toWanted.Length();
        if (l1 < 1e-4f || l2 < 1e-4f || distance < 1e-4f) return;
        float d = Math.Clamp(distance, MathF.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f);
        var u = toWanted / distance;
        // Where the knee goes: on the circle of points l1 from the hip and l2 from the wanted ankle, on the side of the knee's current bend.
        float a = (l1 * l1 - l2 * l2 + d * d) / (2f * d);
        float h = MathF.Sqrt(MathF.Max(l1 * l1 - a * a, 0f));
        var bend = (knee - hip) - u * (knee - hip).Dot(u);
        if (bend.LengthSquared() < 1e-8f) bend = u.Cross(Vector3.Right).LengthSquared() > 1e-6f ? u.Cross(Vector3.Right) : u.Cross(Vector3.Up);
        bend = bend.Normalized();
        var newKnee = hip + u * a + bend * h;
        var newAnkle = hip + u * d;
        // Rotate the thigh so the knee goes there (the calf and foot follow), then the calf so the ankle does, keeping the foot's blended orientation.
        var q1 = Swing(knee - hip, newKnee - hip);
        var thighRot = q1 * thigh.Basis.GetRotationQuaternion();
        var calfRot = q1 * calf.Basis.GetRotationQuaternion();
        var ankleAfter = newKnee + q1 * (ankle - knee);
        var q2 = Swing(ankleAfter - newKnee, newAnkle - newKnee);
        calfRot = q2 * calfRot;
        var footRot = foot.Basis.GetRotationQuaternion();
        var pelvisRot = pelvis.Basis.GetRotationQuaternion();
        _skeleton.SetBonePoseRotation(leg.Thigh, (pelvisRot.Inverse() * thighRot).Normalized());
        _skeleton.SetBonePoseRotation(leg.Calf, (thighRot.Inverse() * calfRot).Normalized());
        _skeleton.SetBonePoseRotation(leg.Foot, (calfRot.Inverse() * footRot).Normalized());
    }

    /// <summary>The shortest rotation taking direction <paramref name="from"/> to <paramref name="to"/>.</summary>
    static Quaternion Swing(Vector3 from, Vector3 to)
    {
        from = from.Normalized(); to = to.Normalized();
        float dot = Math.Clamp(from.Dot(to), -1f, 1f);
        if (dot > 0.99999f) return Quaternion.Identity;
        var axis = from.Cross(to);
        if (axis.LengthSquared() < 1e-10f) axis = from.Cross(Vector3.Right).LengthSquared() > 1e-6f ? from.Cross(Vector3.Right) : from.Cross(Vector3.Up);
        return new Quaternion(axis.Normalized(), MathF.Acos(dot));
    }
}
