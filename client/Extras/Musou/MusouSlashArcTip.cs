using System;
using System.Collections.Generic;
using Godot;
using SmFormats.Havok;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Musou Mode's swing arc follows the path of the weapon's tip (owner, 2026-11-14; <see cref="SmNet.Extras.Musou.MusouSlashArcPath"/>). The melee weapon's tip in the weapon's own frame: the far end of its
/// drawn mesh along its longest axis, measured from the grip (the model's origin is where the hand holds it). Ours: an estimate (the models have no tip locator). Client only, Musou only.
/// </summary>
public sealed partial class WeaponRig
{
    /// <summary>
    /// The melee weapon in hand: the bone it rides on and its tip in that bone's frame (skeleton space, Z up), the far end of the weapon's longest axis. False when no melee weapon is in hand
    /// (the caller asks again at the next strike) or its mesh is empty.
    /// </summary>
    public bool TryMeleeTip(out string bone, out Vector3 tipInBone, out float length)
    {
        bone = ""; tipInBone = default; length = 0f;
        if (!_alive || Faulted || MeleeInHand() is not { } i || i.Bone is null || i.Holder.GetNodeOrNull<MeshInstance3D>("Mesh") is not { Mesh: { } mesh }) return false;
        var box = mesh.GetAabb();
        var size = box.Size;
        if (!(size.X > 0f || size.Y > 0f || size.Z > 0f)) return false;
        int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        var centre = box.GetCenter();
        var low = centre; low[axis] = box.Position[axis];
        var high = centre; high[axis] = box.Position[axis] + size[axis];
        // The two ends in the bone's frame: the grip is the bone's origin, the tip the end farther from it.
        var a = i.Holder.Transform * low;
        var b = i.Holder.Transform * high;
        tipInBone = a.LengthSquared() >= b.LengthSquared() ? a : b;
        bone = i.Bone;
        length = tipInBone.Length();
        return float.IsFinite(length) && length > 0.1f;
    }
}

public partial class CharacterViewer
{
    /// <summary>
    /// The melee weapon's tip over one clip (the swing's animation): <paramref name="count"/> samples from <paramref name="from"/> to <paramref name="to"/> seconds, in this character's own frame
    /// (X forward, Y left, Z up, metres; the clip alone, before the stance, layers and the simulation's own movement), and the chest (the mean of the spine bone over the samples) in the same frame.
    /// Null when the clip cannot be loaded, no melee weapon is in hand or its bones are missing. Does not touch the pose shown.
    /// </summary>
    public (Num.Vector3[] Tip, Num.Vector3 Chest, float WeaponLength)? SampleMeleeTip(string clipName, float from, float to, int count)
    {
        if (_assets is null || _skeleton is null || Weapons is not { } rig || count < 2 || !(to > from)) return null;
        if (!rig.TryMeleeTip(out string handName, out var tipInBone, out float weaponLength)) return null;
        if (_assets.Animation(clipName) is not { } clip) return null;
        int hand = _skeleton.FindBone(handName), spine = _skeleton.FindBone("RIG_SpineD");
        if (spine < 0) spine = _skeleton.FindBone("RIG_SpineA");
        if (hand < 0 || spine < 0) return null;
        int n = _skeleton.GetBoneCount();
        var trackOf = new int[n];
        Array.Fill(trackOf, -1);
        for (int t = 0; t < clip.Animation.TrackCount; t++) if ((uint)clip.TrackToBone[t] < (uint)n) trackOf[clip.TrackToBone[t]] = t;
        var sample = new HavokTransform[clip.Animation.TrackCount];
        var tips = new Num.Vector3[count];
        var chest = Vector3.Zero;
        for (int k = 0; k < count; k++)
        {
            float time = Math.Clamp(from + (to - from) * k / (count - 1), 0f, clip.Animation.Duration);
            clip.Animation.SampleInto(time, sample);
            var tip = GlobalOf(hand, sample, trackOf) * tipInBone;
            chest += GlobalOf(spine, sample, trackOf).Origin;
            tips[k] = Local(tip);
        }
        return (tips, Local(chest / count), weaponLength);
    }

    /// <summary>Skeleton space (the game's Z up, +Y the heading at yaw 0, +X the swinger's right) to the swinger's frame: X forward, Y left, Z up.</summary>
    static Num.Vector3 Local(Vector3 p) => new(p.Y, -p.X, p.Z);

    /// <summary>The bone's transform in skeleton space from one sampled clip pose: the clip's local transform for a bone it animates, the rest pose for any other.</summary>
    Transform3D GlobalOf(int bone, HavokTransform[] sample, int[] trackOf)
    {
        var chain = new List<int>(24);
        for (int b = bone; b >= 0; b = _skeleton!.GetBoneParent(b)) chain.Add(b);
        var global = Transform3D.Identity;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            int b = chain[i];
            Transform3D local;
            if (trackOf[b] is var t && t >= 0)
            {
                var s = sample[t];
                var q = new Quaternion(s.Rotation.X, s.Rotation.Y, s.Rotation.Z, s.Rotation.W).Normalized();
                local = new Transform3D(new Basis(q).Scaled(new Vector3(s.Scale.X, s.Scale.Y, s.Scale.Z)), new Vector3(s.Translation.X, s.Translation.Y, s.Translation.Z));
            }
            else local = _skeleton!.GetBoneRest(b);
            global *= local;
        }
        return global;
    }
}
