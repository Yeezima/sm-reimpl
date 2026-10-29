using System;
using System.Collections.Generic;
using Godot;
using SmFormats.Havok;

namespace SmClient;

/// <summary>
/// The pose layers of a standing marine's turn in place (M40-T5; <c>NetworkView.TurnInPlace.cs</c>):
/// <list type="bullet">
/// <item>The <b>turn layer</b>: a turn clip (<c>SM_NAV_AimTurn_Left</c> / <c>_Right</c>) laid over the stance layers on the bones of its mask (the pelvis and the legs: the
/// graph's turn child) with a weight, after the leg correction and before the extra pose layer, so a recoil or reload clip still lies over it. It is separate from
/// <see cref="SetPoseLayer"/>, which a reload or an attack hand-off may be using on the same frame.</item>
/// <item>The <b>spine twist</b>: the pistol and the heavy bolter have one column of aim-offset clips and make the horizontal aim with a <c>hkbTwistModifier</c> on the
/// upper body (<c>twistAngle</c> bound to <c>FLT_Aim_Horizontal_Deg_CLAMPED</c>; its bones and distribution are not decoded): a rotation about the vertical axis, split between the
/// two spine bones (<see cref="SpineAShare"/>, ours), turning the chest, arms and weapon together.</item>
/// </list>
/// Both are set per frame by the networked view; <see cref="ClearTurnPose"/> (called first when a marine is placed) clears them.
/// </summary>
public partial class CharacterViewer
{
    HavokBoundAnimation? _turnClip;
    float _turnTime, _turnWeight;
    float[]? _turnMask;
    IReadOnlyDictionary<string, float>? _turnMaskSource;
    float _spineTwist;
    int _spineA = -1, _spineD = -1;
    bool _spineResolved;

    /// <summary>Share of the twist the lower spine bone (<c>RIG_SpineA</c>) takes; the upper one (<c>RIG_SpineD</c>) takes the rest (ours).</summary>
    public const float SpineAShare = 0.4f;

    /// <summary>
    /// Lays a turn clip over the stance layers: its pose at <paramref name="time"/> seconds (clamped to its length), blended in by <paramref name="weight"/> times each
    /// bone's mask weight (<paramref name="boneWeights"/>, bone name to 0..1; bones it does not list keep weight 1). Applies from the next pose until
    /// <see cref="ClearTurnLayer"/>. Returns false (the layer is cleared) when the clip cannot be loaded.
    /// </summary>
    public bool SetTurnLayer(string clip, float time, float weight, IReadOnlyDictionary<string, float>? boneWeights)
    {
        if (_assets is null || _skeleton is null) return false;
        if (_turnClip is null || !string.Equals(_turnClip.Name, clip, StringComparison.OrdinalIgnoreCase))
        {
            _turnClip = _assets.Animation(clip);
            if (_turnClip is null) { ClearTurnLayer(); return false; }
        }
        _turnTime = float.IsFinite(time) ? Math.Clamp(time, 0f, _turnClip.Animation.Duration) : 0f;
        _turnWeight = float.IsFinite(weight) ? Math.Clamp(weight, 0f, 1f) : 0f;
        if (!ReferenceEquals(boneWeights, _turnMaskSource))
        {
            _turnMaskSource = boneWeights;
            if (boneWeights is null) _turnMask = null;
            else
            {
                _turnMask = new float[_skeleton.GetBoneCount()];
                for (int b = 0; b < _turnMask.Length; b++)
                    _turnMask[b] = boneWeights.TryGetValue(_skeleton.GetBoneName(b), out var w) ? Math.Clamp(w, 0f, 1f) : 1f;
            }
        }
        return true;
    }

    float? _seedPhase;

    /// <summary>
    /// The shared gait phase (0 to 1, <see cref="SmContent.GaitPhase"/>) the next blend layer with a gait starts at, instead of the phase of the layer it follows (the run-start clip
    /// hands over to the run cycle at the foot it ended on). Used once, by the next new blend layer; cleared when a layer is made.
    /// </summary>
    public void SeedBlendPhase(float phase) => _seedPhase = float.IsFinite(phase) ? phase : null;

    /// <summary>Removes the turn layer.</summary>
    public void ClearTurnLayer() { _turnClip = null; _turnWeight = 0f; }

    /// <summary>Removes the turn layer and the spine twist (the networked view calls it first when it places a marine; the recoil's <see cref="ClearPoseLayer"/> leaves them).</summary>
    public void ClearTurnPose() { ClearTurnLayer(); _spineTwist = 0f; }

    /// <summary>The clip and weight of the turn layer while it is laid (the probes and the tests), else null.</summary>
    public (string Clip, float Time, float Weight)? TurnLayerState => _turnClip is { } c && _turnWeight > 1e-3f ? (c.Name, _turnTime, _turnWeight) : null;

    /// <summary>Sets the upper-body twist (radians about the vertical axis, positive counter-clockwise seen from above) laid over the pose from the next pose; 0 clears it.</summary>
    public void SetSpineTwist(float radians) => _spineTwist = float.IsFinite(radians) ? radians : 0f;

    /// <summary>The spine twist laid now (radians).</summary>
    public float SpineTwist => _spineTwist;

    void ApplyTurnLayer()
    {
        if (_turnClip is null || _turnWeight <= 1e-3f || _skeleton is null) return;
        int n = _skeleton.GetBoneCount();
        int tracks = _turnClip.Animation.TrackCount;
        if (SampleBuffer.Length < tracks) SampleBuffer = new HavokTransform[tracks];
        var local = SampleBuffer;   // the frame's scratch buffer: the stance samples are done with it
        _turnClip.Animation.SampleInto(_turnTime, local);
        for (int t = 0; t < tracks; t++)
        {
            int bone = _turnClip.TrackToBone[t];
            if ((uint)bone >= (uint)n || _accWeight.Length != n || _accWeight[bone] <= 0f) continue;   // only bones the stance layers drove this frame
            float w = _turnWeight * (_turnMask is { } m && bone < m.Length ? m[bone] : 1f);
            if (w <= 1e-3f) continue;
            var q = new Quaternion(local[t].Rotation.X, local[t].Rotation.Y, local[t].Rotation.Z, local[t].Rotation.W).Normalized();
            var pos = new Vector3(local[t].Translation.X, local[t].Translation.Y, local[t].Translation.Z);
            var baseQ = _skeleton.GetBonePoseRotation(bone);
            if (q.Dot(baseQ) < 0f) q = -q;
            _skeleton.SetBonePoseRotation(bone, baseQ.Slerp(q, w).Normalized());
            _skeleton.SetBonePosePosition(bone, _skeleton.GetBonePosePosition(bone).Lerp(pos, w));
        }
    }

    void ApplySpineTwist()
    {
        if (MathF.Abs(_spineTwist) < 1e-4f || _skeleton is null) return;
        if (!_spineResolved) { _spineResolved = true; _spineA = _skeleton.FindBone("RIG_SpineA"); _spineD = _skeleton.FindBone("RIG_SpineD"); }
        if (_spineA >= 0) TwistBone(_spineA, _spineTwist * SpineAShare);
        if (_spineD >= 0) TwistBone(_spineD, _spineTwist * (_spineA >= 0 ? 1f - SpineAShare : 1f));
    }

    /// <summary>Turns a bone about the vertical axis through its own origin (the parent's pose is already final).</summary>
    void TwistBone(int bone, float angle)
    {
        int parent = _skeleton!.GetBoneParent(bone);
        var parentRot = parent >= 0 ? _skeleton.GetBoneGlobalPose(parent).Basis.GetRotationQuaternion() : Quaternion.Identity;
        var turn = new Quaternion(Vector3.Back, angle);   // game space: +Z is up
        _skeleton.SetBonePoseRotation(bone, (parentRot.Inverse() * turn * parentRot * _skeleton.GetBonePoseRotation(bone)).Normalized());
    }
}
