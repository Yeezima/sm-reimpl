using System;
using Godot;
using SmNet.Extras.Musou;

namespace SmClient;

/// <summary>Musou weight pass MW1: the bones whose ground contacts are tracked (<see cref="MusouWeightRules.Bones"/>), read after the pose (NetworkView.MusouWeight.cs).</summary>
public partial class CharacterViewer
{
    int[]? _musouContactBones;

    /// <summary>
    /// Fills <paramref name="into"/> with the contact bones in world space (Godot) and their heights above this character's feet (its position). False when the character has
    /// no skeleton, is not in the tree, or its rig lacks one of the bones (an Ork's).
    /// </summary>
    public bool MusouContactBones(Span<MusouBoneSample> into)
    {
        if (_skeleton is null || !IsInsideTree() || into.Length < MusouWeightRules.Parts) return false;
        if (_musouContactBones is null)
        {
            _musouContactBones = new int[MusouWeightRules.Parts];
            for (int i = 0; i < MusouWeightRules.Parts; i++) _musouContactBones[i] = _skeleton.FindBone(MusouWeightRules.Bones[i]);
        }
        float ground = GlobalPosition.Y;
        for (int i = 0; i < MusouWeightRules.Parts; i++)
        {
            int b = _musouContactBones[i];
            if (b < 0) return false;
            var at = _skeleton.ToGlobal(_skeleton.GetBoneGlobalPose(b).Origin);
            into[i] = new MusouBoneSample(new System.Numerics.Vector3(at.X, at.Y, at.Z), at.Y - ground);
        }
        return true;
    }

    // ------------------------------------------------------------------ MW3: the settle

    /// <summary>One settle: when it began (s, the viewer's clock), how deep the pelvis goes (m), how far the spine leans back (degrees; below 0 forward), and the times down and back (s).</summary>
    readonly record struct MusouSettleState(double Start, float Depth, float LeanDegrees, float Down, float Back);
    MusouSettleState? _musouSettle;
    int _musouSpineBone = -2;
    Vector3[]? _musouAnkles;   // lazy: only a viewer that settles carries it

    /// <summary>MW3: starts a settle (a landing, a stop's plant, a melee step's plant, a roll's end); a stronger one replaces a weaker one under way.</summary>
    public void MusouSettle(float depth, float leanDegrees, float down, float back)
    {
        double now = Time.GetTicksUsec() / 1e6;
        if (_musouSettle is { } cur)
        {
            float w = MusouWeightRules.SettleWeight((float)(now - cur.Start), cur.Down, cur.Back);
            if (w * cur.Depth > depth && w * MathF.Abs(cur.LeanDegrees) >= MathF.Abs(leanDegrees)) return;
        }
        _musouSettle = new MusouSettleState(now, depth, leanDegrees, down, back);
        _layersDirty = true;
    }

    /// <summary>
    /// MW3 (from the pose overlay, before the foot pins): the pelvis lowered in skeleton space by the settle's depth with both ankles held where the pose put them (the knees bend
    /// through <see cref="SolveLeg"/>), and the spine leant back against the stop. The torso drops with the pelvis as one piece (review, 2026-11-10: the marine rig hangs <c>RIG_SpineA</c> beside
    /// the pelvis under the root, so the drop alone lowered the hips and legs and left the torso where it was; <c>RigidWith</c> of the weapon pose carries it). Nothing without a settle under way.
    /// </summary>
    void MusouWeightPoseOverlay()
    {
        if (_musouSettle is not { } st || _skeleton is null) return;
        float age = (float)(Time.GetTicksUsec() / 1e6 - st.Start);
        float w = MusouWeightRules.SettleWeight(age, st.Down, st.Back);
        if (age >= st.Down + st.Back) { _musouSettle = null; return; }
        _layersDirty = true;   // applied again next frame, so the settle moves while a held clip shows
        if (w <= 1e-3f) return;
        if (_legs is null) ResolveLegs();
        if (_legs is not { Length: 2 } legs) return;
        var ankles = _musouAnkles ??= new Vector3[2];
        for (int i = 0; i < 2; i++) ankles[i] = _skeleton.GetBoneGlobalPose(legs[i].Foot).Origin;
        int pelvis = legs[0].Pelvis;
        // Skeleton space has Z up (the feet's plane at 0): the pelvis's own position moves by the drop turned into its parent's frame, and so does the bone carried with it (the torso).
        var drop = new Vector3(0f, 0f, -st.Depth * w);
        DropBone(pelvis, drop);
        if (RigidCarriedWith(_skeleton, pelvis) is var torso and >= 0) DropBone(torso, drop);
        if (MathF.Abs(st.LeanDegrees) > 0.01f)
        {
            if (_musouSpineBone == -2) _musouSpineBone = _skeleton.FindBone("RIG_SpineA");
            int spine = _musouSpineBone;
            if (spine >= 0 && _musouContactBones is { } cb && cb[0] >= 0 && cb[1] >= 0)
            {
                // Forward: from the ankles to the toes, flat; leaning back turns the spine's up toward the back.
                var toes = (_skeleton.GetBoneGlobalPose(cb[0]).Origin + _skeleton.GetBoneGlobalPose(cb[1]).Origin) * 0.5f;
                var forward = toes - (ankles[0] + ankles[1]) * 0.5f;
                forward.Z = 0f;
                if (forward.LengthSquared() > 1e-6f)
                {
                    forward = forward.Normalized();
                    var axis = new Vector3(0f, 0f, 1f).Cross(-forward).Normalized();   // skeleton +Z is up
                    var q = new Quaternion(axis, Mathf.DegToRad(st.LeanDegrees * w));
                    var g = _skeleton.GetBoneGlobalPose(spine).Basis.GetRotationQuaternion();
                    int sp = _skeleton.GetBoneParent(spine);
                    var pg = sp >= 0 ? _skeleton.GetBoneGlobalPose(sp).Basis.GetRotationQuaternion() : Quaternion.Identity;
                    _skeleton.SetBonePoseRotation(spine, (pg.Inverse() * (q * g)).Normalized());
                }
            }
        }
        for (int i = 0; i < 2; i++) SolveLeg(legs[i], ankles[i]);
    }

    /// <summary>Moves a bone's origin by <paramref name="drop"/> (skeleton space), turned into its parent's frame.</summary>
    void DropBone(int bone, Vector3 drop)
    {
        int parent = _skeleton!.GetBoneParent(bone);
        var local = parent >= 0 ? _skeleton.GetBoneGlobalPose(parent).Basis.Inverse() * drop : drop;
        _skeleton.SetBonePosePosition(bone, _skeleton.GetBonePosePosition(bone) + local);
    }

    // ------------------------------------------------------------------ MW5: cross-fade floors

    /// <summary>Set by the networked view for a Musou marine (MW5): this viewer's cross-fades get <see cref="MusouTransitionRules"/>' floors.</summary>
    public bool MusouBlendFloors;

    partial void ExtrasBlendSeconds(ref float seconds, bool stance)
    {
        if (MusouBlendFloors) seconds = MusouTransitionRules.Blend(seconds, stance);
    }
}
