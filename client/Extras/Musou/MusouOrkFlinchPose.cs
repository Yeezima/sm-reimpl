using System;
using Godot;
using SmNet.Extras.Musou;

namespace SmClient;

/// <summary>
/// Musou Mode, Ork hit reactions (owner 2026-11-13, reference v3): the procedural additive flinch laid over a skeletal Ork's pose after its clip is sampled (<see cref="CrowdView"/>).
/// Torso recoil (the spine, neck and head tip back), the pelvis shifts away from the hit and drops, and each leg is solved again by two-bone IK so the feet stay where the clip put them
/// (the knees bend). Facing, path and attack phase are the clip's and never touched. The Boy rig's bones (<c>RIG_Pelvis</c>, <c>RIG_Spine1..3</c>, <c>RIG_LLeg_Thigh</c> ...); a rig without
/// them gets the whole-body lean the baked tier uses. Presentation only; every number is in <see cref="MusouOrkFlinchRules"/> (ours).
/// </summary>
public static class MusouOrkFlinchPose
{
    /// <summary>The bone indices the pose needs (-1: the rig has none).</summary>
    public sealed class Bones
    {
        public int Pelvis, Spine1, Spine2, Spine3, Neck, Head;
        public int[] Leg = new int[6];   // left thigh, calf, foot, right thigh, calf, foot
    }

    /// <summary>The rig's bones, or null when it is not the Boy rig.</summary>
    public static Bones? Resolve(Skeleton3D sk)
    {
        var b = new Bones
        {
            Pelvis = sk.FindBone("RIG_Pelvis"), Spine1 = sk.FindBone("RIG_Spine1"), Spine2 = sk.FindBone("RIG_Spine2"), Spine3 = sk.FindBone("RIG_Spine3"),
            Neck = sk.FindBone("RIG_Neck"), Head = sk.FindBone("RIG_Head"),
        };
        string[] legs = ["RIG_LLeg_Thigh", "RIG_LLeg_Calf", "RIG_LLeg_Foot", "RIG_RLeg_Thigh", "RIG_RLeg_Calf", "RIG_RLeg_Foot"];
        for (int i = 0; i < 6; i++) b.Leg[i] = sk.FindBone(legs[i]);
        if (b.Pelvis < 0 || b.Spine1 < 0 || b.Spine2 < 0 || b.Spine3 < 0 || b.Neck < 0 || b.Head < 0) return null;
        foreach (int i in b.Leg) if (i < 0) return null;
        return b;
    }

    /// <summary>Lays the flinch of <paramref name="kick"/> (horizontal, in the Ork's own frame: its length the size in reference sizes, its direction away from the hit) over the pose; <paramref name="scale"/> is the model's scale (bone positions are in model units).</summary>
    public static void Apply(Skeleton3D sk, Bones b, Vector3 kick, float scale)
    {
        float size = kick.Length();
        if (size < 1e-4f) return;
        var away = kick / size;
        size *= MusouOrkFlinchRules.TestScale;
        var up = new Vector3(0f, 0f, 1f);   // skeleton space is the game's: Z up, the Ork faces +Y
        // The feet to keep where the clip put them (skeleton space), before anything moves.
        Vector3 ankleL = sk.GetBoneGlobalPose(b.Leg[2]).Origin, ankleR = sk.GetBoneGlobalPose(b.Leg[5]).Origin;

        var shift = (away * MusouOrkFlinchRules.PelvisBack - up * MusouOrkFlinchRules.PelvisDrop) * size / scale;
        float share = Reachable(sk, b, shift, ankleL, ankleR);
        if (DebugPlant) GD.Print($"PLANT size {size:F2}: the legs take {share * 100f:F0} % of the pelvis shift");
        shift *= share;   // the largest share of the shift the legs can take with the feet kept (a leg the clip left nearly straight cannot follow the hips)
        Move(sk, b.Pelvis, shift);
        Move(sk, b.Spine1, new Vector3(shift.X, shift.Y, 0f));   // the torso follows the hips' shift (not their drop: the spine bends instead)
        // The torso tips back: about the axis up x away, so the top moves along the hit's direction.
        var axis = up.Cross(away).Normalized();
        float d = Mathf.DegToRad(1f) * size;
        Tip(sk, b.Spine1, axis, MusouOrkFlinchRules.Spine1 * d);
        Tip(sk, b.Spine2, axis, MusouOrkFlinchRules.Spine2 * d);
        Tip(sk, b.Spine3, axis, MusouOrkFlinchRules.Spine3 * d);
        float dHead = Mathf.DegToRad(1f) * MathF.Min(size, MusouOrkFlinchRules.HeadMaxSize);   // the neck and head stop at this size: past it the face points at the sky and the head sinks into the shoulders (seen at 2.5)
        Tip(sk, b.Neck, axis, MusouOrkFlinchRules.Neck * dHead);
        Tip(sk, b.Head, axis, MusouOrkFlinchRules.Head * dHead);
        Plant(sk, b.Leg[0], b.Leg[1], b.Leg[2], ankleL);
        Plant(sk, b.Leg[3], b.Leg[4], b.Leg[5], ankleR);
        if (DebugPlant && MathF.Max(sk.GetBoneGlobalPose(b.Leg[2]).Origin.DistanceTo(ankleL), sk.GetBoneGlobalPose(b.Leg[5]).Origin.DistanceTo(ankleR)) > 0.02f) GD.Print($"PLANT drift L {sk.GetBoneGlobalPose(b.Leg[2]).Origin.DistanceTo(ankleL):F3} R {sk.GetBoneGlobalPose(b.Leg[5]).Origin.DistanceTo(ankleR):F3} shift {shift.Length():F3} size {size:F2}");
        LastFootDrift = MathF.Max(sk.GetBoneGlobalPose(b.Leg[2]).Origin.DistanceTo(ankleL), sk.GetBoneGlobalPose(b.Leg[5]).Origin.DistanceTo(ankleR)) * scale;
        WorstFootDrift = MathF.Max(WorstFootDrift, LastFootDrift);
    }

    /// <summary>Test aid: how far (m, world) the worse ankle ended from where the clip put it on the last pose, and the worst so far (the feet-planted check).</summary>
    public static float LastFootDrift, WorstFootDrift;
    public static bool DebugPlant;

    /// <summary>The largest k in [0, 1] such that moving the hips by k x <paramref name="worldShiftScaled"/> (skeleton units) keeps both ankles within their leg's reach (about 2 % short of straight).</summary>
    static float Reachable(Skeleton3D sk, Bones b, Vector3 worldShiftScaled, Vector3 ankleL, Vector3 ankleR)
    {
        float k = 1f;
        for (int leg = 0; leg < 2; leg++)
        {
            int o = leg * 3;
            var hip = sk.GetBoneGlobalPose(b.Leg[o]).Origin;
            float reach = (hip.DistanceTo(sk.GetBoneGlobalPose(b.Leg[o + 1]).Origin) + sk.GetBoneGlobalPose(b.Leg[o + 1]).Origin.DistanceTo(sk.GetBoneGlobalPose(b.Leg[o + 2]).Origin)) * 0.98f;
            var ankle = leg == 0 ? ankleL : ankleR;
            var shift = worldShiftScaled;   // skeleton units: unchanged
            if ((ankle - (hip + shift * k)).Length() <= reach) continue;
            float lo = 0f, hi = k;
            for (int i = 0; i < 8; i++) { float mid = (lo + hi) * 0.5f; if ((ankle - (hip + shift * mid)).Length() <= reach) lo = mid; else hi = mid; }
            k = lo;
        }
        return k;
    }

    /// <summary>Moves a bone by a skeleton-space offset (its position pose is in its parent's frame, which may be turned).</summary>
    static void Move(Skeleton3D sk, int bone, Vector3 offset)
    {
        int parent = sk.GetBoneParent(bone);
        var inParent = parent >= 0 ? sk.GetBoneGlobalPose(parent).Basis.Orthonormalized().Inverse() * offset : offset;
        sk.SetBonePosePosition(bone, sk.GetBonePosePosition(bone) + inParent);
    }

    /// <summary>Tips a bone about a skeleton-space axis by <paramref name="angle"/> (radians), keeping its children's own motion.</summary>
    static void Tip(Skeleton3D sk, int bone, Vector3 axis, float angle)
    {
        int parent = sk.GetBoneParent(bone);
        var parentRot = parent >= 0 ? sk.GetBoneGlobalPose(parent).Basis.GetRotationQuaternion() : Quaternion.Identity;
        var world = new Quaternion(axis, angle);
        var local = sk.GetBonePoseRotation(bone);
        sk.SetBonePoseRotation(bone, parentRot.Inverse() * world * parentRot * local);
    }

    /// <summary>Two-bone IK: the leg's ankle goes back to <paramref name="target"/> (skeleton space), the knee bending the way it bent.</summary>
    static void Plant(Skeleton3D sk, int thigh, int calf, int foot, Vector3 target)
    {
        var hip = sk.GetBoneGlobalPose(thigh).Origin;
        var knee = sk.GetBoneGlobalPose(calf).Origin;
        var ankle = sk.GetBoneGlobalPose(foot).Origin;
        float l1 = hip.DistanceTo(knee), l2 = knee.DistanceTo(ankle);
        if (l1 < 1e-4f || l2 < 1e-4f) return;
        var toTarget = target - hip;
        float dist = toTarget.Length();
        if (dist < 1e-4f) return;
        var dir = toTarget / dist;
        dist = Math.Clamp(dist, MathF.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f);
        // The knee's plane: the bend it has now, perpendicular to the hip-to-target line.
        var bend = knee - hip;
        bend -= dir * bend.Dot(dir);
        if (bend.LengthSquared() < 1e-8f) bend = Vector3.Right;
        bend = bend.Normalized();
        float a = (l1 * l1 - l2 * l2 + dist * dist) / (2f * dist);
        float h = MathF.Sqrt(MathF.Max(0f, l1 * l1 - a * a));
        var newKnee = hip + dir * a + bend * h;
        var newAnkle = hip + dir * dist;
        if (DebugPlant && (target - newAnkle).Length() > 0.02f) GD.Print($"PLANT clamp: want {toTarget.Length():F3} have l1 {l1:F3} l2 {l2:F3} dist {dist:F3}");

        // The foot keeps the orientation the clip gave it.
        var footWorld = sk.GetBoneGlobalPose(foot).Basis.GetRotationQuaternion();
        Aim(sk, thigh, knee - hip, newKnee - hip);
        // The calf's origin has moved with the thigh: aim it from where it is now.
        var calfAt = sk.GetBoneGlobalPose(calf).Origin;
        var ankleNow = sk.GetBoneGlobalPose(foot).Origin;
        if ((ankleNow - calfAt).LengthSquared() > 1e-8f && (newAnkle - calfAt).LengthSquared() > 1e-8f) Aim(sk, calf, ankleNow - calfAt, newAnkle - calfAt);
        var calfRot = sk.GetBoneGlobalPose(calf).Basis.GetRotationQuaternion();
        sk.SetBonePoseRotation(foot, calfRot.Inverse() * footWorld);
    }

    /// <summary>Turns a bone (and so its children) by the shortest rotation taking skeleton-space direction <paramref name="from"/> to <paramref name="to"/>.</summary>
    static void Aim(Skeleton3D sk, int bone, Vector3 from, Vector3 to)
    {
        if (from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f) return;
        var world = new Quaternion(from.Normalized(), to.Normalized());
        int parent = sk.GetBoneParent(bone);
        var parentRot = parent >= 0 ? sk.GetBoneGlobalPose(parent).Basis.GetRotationQuaternion() : Quaternion.Identity;
        sk.SetBonePoseRotation(bone, parentRot.Inverse() * world * parentRot * sk.GetBonePoseRotation(bone));
    }

    /// <summary>The whole-body lean about the feet for the baked tier and any rig without the bones: top toward the hit's direction, plus a small shift (<see cref="MusouOrkFlinchRules.FarLeanRadians"/>).</summary>
    public static (Basis Lean, Vector3 Shift) WholeBody(Vector3 kick, float strengthScale = 1f)
    {
        float size = kick.Length();
        if (size < 1e-4f) return (Basis.Identity, Vector3.Zero);
        var away = kick / size;
        size *= MusouOrkFlinchRules.TestScale;
        var axis = new Vector3(0f, 0f, 1f).Cross(away).Normalized();
        return (new Basis(axis, MusouOrkFlinchRules.FarLeanRadians(size) * strengthScale), away * (MusouOrkFlinchRules.FarShiftMetres(size) * strengthScale));
    }
}
