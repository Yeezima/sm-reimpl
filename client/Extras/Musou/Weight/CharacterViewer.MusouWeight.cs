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
}
