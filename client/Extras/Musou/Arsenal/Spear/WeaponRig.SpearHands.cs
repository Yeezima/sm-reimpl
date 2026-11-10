using System;
using Godot;
using SmContent.Extras.Musou;
using Role = SmContent.HandRole;

namespace SmClient;

/// <summary>
/// Musou's power spear on the hands (the approved clips in a match; docs/claw-spear-animations.md section 1): while the networked view shows a spear clip (<c>NetworkView.SpearPose.cs</c>), the
/// spear's model is put where the preview stage puts its stand-in (<see cref="SpearHold.Place"/>: the shaft from the right hand toward the left by the clip's grip track, the right hand the
/// clip's grip point in metres from the butt, the blade's edge forward), over the mount W3 gives it. Musou Mode only; nothing calls it otherwise.
/// </summary>
public sealed partial class WeaponRig
{
    string? _spearBoneName;
    int _spearBone = -1;

    /// <summary>
    /// Places the spear in hand on the hands (skeleton space: <paramref name="rightHand"/>, <paramref name="leftHand"/>, the body's <paramref name="front"/>), the left hand on the shaft by
    /// <paramref name="leftGrip"/> and the right hand <paramref name="rightFromButt"/> metres from the butt (the clip's <see cref="WeaponPoseClip.GripPointAt"/>; the model's own axis is
    /// <see cref="SpearHold.AxisAt"/> of it), blended over the mount's place by <paramref name="weight"/> (0: as <see cref="Update"/> put it, 1: on the hands). Call after <see cref="Update"/>,
    /// with this frame's pose. False when no spear is in hand.
    /// </summary>
    public bool PlaceSpearOnHands(Vector3 rightHand, Vector3 leftHand, float leftGrip, float rightFromButt, Vector3 front, float weight)
    {
        if (Faulted || weight <= 1e-3f) return false;
        foreach (var i in _items)
        {
            if (i.Role != Role.Melee || i.Mode != 1 || i.Bone is null || !IsSpear(i.Asset) || !GodotObject.IsInstanceValid(i.Holder)) continue;
            if (!string.Equals(_spearBoneName, i.Bone, StringComparison.Ordinal)) { _spearBoneName = i.Bone; _spearBone = _skeleton.FindBone(i.Bone); }
            if (_spearBone < 0) return false;
            var (butt, shaft, edge) = SpearHold.Place(SpearN(rightHand), SpearN(leftHand), leftGrip, rightFromButt, SpearN(front), new System.Numerics.Vector3(0f, 0f, 1f));
            // The model's frame (BuildSpear): the shaft along +X with its origin AxisFromButt from the butt, the blade in the XY plane with its edge on +Y.
            var x = SpearG(shaft); var y = SpearG(edge);
            var onHands = new Transform3D(new Basis(x, y, x.Cross(y).Normalized()), SpearG(butt) + x * SpearHold.AxisFromButt);
            var bone = _skeleton.GetBoneGlobalPose(_spearBone);
            var placed = onHands;
            if (weight < 0.999f)
            {
                var mounted = bone * i.Holder.Transform;
                var from = mounted.Basis.Orthonormalized().GetRotationQuaternion();
                var to = onHands.Basis.GetRotationQuaternion();
                if (from.Dot(to) < 0f) to = -to;
                placed = new Transform3D(new Basis(from.Slerp(to, weight).Normalized()), mounted.Origin.Lerp(onHands.Origin, weight));
            }
            i.Holder.Transform = bone.AffineInverse() * placed;
            return true;
        }
        return false;
    }

    /// <summary>Test aid (the spear's stills): the spear's butt and tip in world space as drawn now; false when no spear is in hand.</summary>
    public bool SpearEnds(out Vector3 butt, out Vector3 tip)
    {
        butt = tip = default;
        foreach (var i in _items)
        {
            if (i.Role != Role.Melee || i.Mode != 1 || !IsSpear(i.Asset) || !GodotObject.IsInstanceValid(i.Holder) || !i.Holder.IsInsideTree()) continue;
            var g = i.Holder.GlobalTransform;
            butt = g * new Vector3(SpearButt, 0f, 0f); tip = g * new Vector3(SpearTip, 0f, 0f);
            return true;
        }
        return false;
    }

    /// <summary>Test aid (the spear's stills): the drawn spear in skeleton space: its shaft's elevation and azimuth from the skeleton's +X and its edge's azimuth (degrees), and whether a clip holds it.</summary>
    public string SpearPoseText()
    {
        foreach (var i in _items)
        {
            if (i.Role != Role.Melee || i.Mode != 1 || !IsSpear(i.Asset) || !GodotObject.IsInstanceValid(i.Holder) || !i.Holder.IsInsideTree()) continue;
            var b = (_skeleton.GlobalTransform.AffineInverse() * i.Holder.GlobalTransform).Basis;
            var shaft = b.Column0.Normalized(); var edge = b.Column1.Normalized();
            float D(float r) => Mathf.RadToDeg(r);
            return $"shaft el {D(MathF.Asin(Mathf.Clamp(shaft.Z, -1f, 1f))):F0} az {D(MathF.Atan2(shaft.Y, shaft.X)):F0}, edge az {D(MathF.Atan2(edge.Y, edge.X)):F0} el {D(MathF.Asin(Mathf.Clamp(edge.Z, -1f, 1f))):F0}, driven {GripSlideDriven}";
        }
        return "no spear in hand";
    }

    static System.Numerics.Vector3 SpearN(Vector3 v) => new(v.X, v.Y, v.Z);
    static Vector3 SpearG(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
}
