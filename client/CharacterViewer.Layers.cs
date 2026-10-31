using System;
using System.Collections.Generic;
using Godot;
using SmFormats.Havok;

namespace SmClient;

/// <summary>
/// The pose layer slots of a <see cref="CharacterViewer"/> (animation pass 1). Each slot holds at most one clip laid over whatever drives the pose; the slots are applied in
/// ascending order, so a higher slot is drawn over a lower one (the priority is the number). A slot is owned by one system, which sets it every frame it wants it and clears it
/// otherwise; the networked view clears all slots at the start of a character's placement (<see cref="CharacterViewer.ClearPoseLayers"/>) and each system lays its own again.
/// </summary>
public static class PoseSlots
{
    /// <summary>
    /// feel/melee-lock: the upper distance variant of a melee attack, laid over the lower one (the posed clip) at the blend weight (NetworkView.MeleeLock.cs). The lowest
    /// slot, so it is part of the base pose and the other layers (the flinch above all) go over the blend.
    /// </summary>
    public const int MeleeVariant = 0;
    /// <summary>The legs of a marine that is falling (<c>SM_ACT_B_button_drop_fall_01</c> on the 14-bone leg mask; the fall system, <c>NetworkView.Fall.cs</c>).</summary>
    public const int Fall = 1;
    /// <summary>The landing after a hard fall, or the heavy bolter's drop and landing: a whole-body clip with a weight envelope (the fall system).</summary>
    public const int Land = 2;
    /// <summary>
    /// The single layer the stack replaced (M27): the recoil of a shot, the melee / throw hand-off clip over the upper body (M40-T4) and the reload (M35). They exclude each other as
    /// before (the reload is laid only when the slot is empty; <see cref="CharacterViewer.PoseLayerClip"/> and <see cref="CharacterViewer.PoseLayerState"/> read this slot).
    /// </summary>
    public const int Action = 3;
    /// <summary>Animation pass 2: the weapon switch (<c>SM_Swap_*</c> over the upper body, the legs keep the movement; <c>NetworkView.UpperBody.cs</c>).</summary>
    public const int Switch = 4;
    /// <summary>fix/stow-right: the next piece of a weapon switch fading in over the one in <see cref="Switch"/> at the joint between the old weapon's stow and the new one's draw.</summary>
    public const int SwitchNext = 5;
    /// <summary>The hit flinch (an additive <c>SM_REC_Flinch_*</c> clip, pass 2).</summary>
    public const int Flinch = 6;
    /// <summary>The flight wobble of the jump pack (<c>SM_JMP_Blend_90_Pos_Fast</c> as an offset from its first pose, pass 2).</summary>
    public const int Wobble = 7;
    /// <summary>Slot 8 is free; it sits above the others.</summary>
    public const int FirstFree = 8;
    /// <summary>The number of slots.</summary>
    public const int Count = 9;

    /// <summary>The slot's name, for logs.</summary>
    public static string Name(int slot) => slot switch { Fall => "fall", Land => "land", Action => "action", Switch => "switch", SwitchNext => "switch next", Flinch => "flinch", Wobble => "wobble", MeleeVariant => "melee variant", _ => $"slot{slot}" };
}

public partial class CharacterViewer
{
    /// <summary><c>--no-layer-stack</c> (test aid): every layer goes into the one action slot again (the last one set wins), as before the stack.</summary>
    public static readonly bool NoLayerStack = OS.GetCmdlineUserArgs().Contains("--no-layer-stack");

    /// <summary>One slot's state. The mask array is rebuilt only for another source dictionary; nothing here allocates per frame.</summary>
    sealed class LayerSlot
    {
        public HavokBoundAnimation? Clip;
        public float Time, Weight;
        public bool Additive;
        /// <summary>Pass 2: when 0 or more, the clip is a whole pose laid as an offset: its pose at this time is taken away (the flight wobble); implies <see cref="Additive"/>.</summary>
        public float RefTime = -1f;
        public float[]? Mask;
        public IReadOnlyDictionary<string, float>? MaskSource;
    }

    readonly LayerSlot[] _slots = CreateSlots();

    static LayerSlot[] CreateSlots()
    {
        var slots = new LayerSlot[PoseSlots.Count];
        for (int i = 0; i < slots.Length; i++) slots[i] = new LayerSlot();
        return slots;
    }

    static int Slot(int slot) => NoLayerStack ? PoseSlots.Action : (uint)slot < PoseSlots.Count ? slot : PoseSlots.Action;

    /// <summary>The clip of the action slot, or null when none is set.</summary>
    public string? PoseLayerClip => _slots[PoseSlots.Action].Clip?.Name;

    /// <summary>The action slot's clip, time and length (s) while it is laid with weight (the action clip over the upper body, M40-T4 follow-up), else null.</summary>
    public (string Clip, float Time, float Duration)? PoseLayerState => _slots[PoseSlots.Action] is { Clip: { } c } s && s.Weight > 1e-3f ? (c.Name, s.Time, c.Animation.Duration) : null;

    /// <summary>The clip of a slot, or null when none is set.</summary>
    public string? LayerClip(int slot) => _slots[Slot(slot)].Clip?.Name;

    /// <summary>A slot's clip, time, length (s) and weight while it is laid with weight, else null.</summary>
    public (string Clip, float Time, float Duration, float Weight)? LayerState(int slot) =>
        _slots[Slot(slot)] is { Clip: { } c } s && s.Weight > 1e-3f ? (c.Name, s.Time, c.Animation.Duration, s.Weight) : null;

    /// <summary>The number of slots holding a clip with weight now (test aids).</summary>
    public int ActiveLayers
    {
        get { int n = 0; foreach (var s in _slots) if (s.Clip is not null && s.Weight > 1e-3f) n++; return n; }
    }

    /// <summary>
    /// Lays a clip in a slot over whatever drives the pose (the stance blend, or a clip shown by <see cref="Play"/> / <see cref="Pose"/>), sampled at <paramref name="time"/> seconds
    /// (clamped to its length) and applied after the stances every frame until <see cref="ClearLayer"/> / <see cref="ClearPoseLayers"/> or the next call for the slot. Slots are applied
    /// in ascending order (<see cref="PoseSlots"/>). <paramref name="weight"/> is 0..1. <paramref name="additive"/> clips (Havok additive animations, binding blend hint 1: the tracks store
    /// the difference from the pose they are added to, identity when idle) are added: rotation = delta (scaled by the weight toward identity) times the base rotation, translation plus
    /// the weighted delta, scale times the delta; other clips are blended toward (rotation slerp, translation and scale lerp). <paramref name="boneWeights"/> (bone name to 0..1; null: 1
    /// everywhere; a bone it does not name keeps 1) scales the weight per bone. Only bones the base pose drives are touched, so an additive layer never accumulates from frame to frame.
    /// Returns false (the slot is cleared) when the clip cannot be loaded. The order of an additive rotation (delta * base, in the parent bone's frame) is the usual Havok convention:
    /// <b>interpretation</b>.
    /// </summary>
    public bool SetLayer(int slot, string clip, float time, float weight, bool additive, IReadOnlyDictionary<string, float>? boneWeights = null, float referenceTime = -1f)
    {
        if (_assets is null || _skeleton is null) return false;
        slot = Slot(slot);
        var s = _slots[slot];
        if (s.Clip is null || !string.Equals(s.Clip.Name, clip, StringComparison.OrdinalIgnoreCase))
        {
            s.Clip = _assets.Animation(clip);
            if (s.Clip is null) { ClearLayer(slot); return false; }
        }
        s.Time = float.IsFinite(time) ? Math.Clamp(time, 0f, s.Clip.Animation.Duration) : 0f;
        s.Weight = float.IsFinite(weight) ? Math.Clamp(weight, 0f, additive ? 2f : 1f) : 0f;   // feel/weight-impact: an additive clip may be laid stronger than authored
        s.Additive = additive || referenceTime >= 0f;
        s.RefTime = float.IsFinite(referenceTime) && referenceTime >= 0f ? Math.Min(referenceTime, s.Clip.Animation.Duration) : -1f;
        _layersDirty = true;   // a clip shown by Pose() is applied again for it (pass 2)
        if (!ReferenceEquals(boneWeights, s.MaskSource))
        {
            s.MaskSource = boneWeights;
            if (boneWeights is null) s.Mask = null;
            else
            {
                s.Mask = new float[_skeleton.GetBoneCount()];
                for (int b = 0; b < s.Mask.Length; b++)
                    s.Mask[b] = boneWeights.TryGetValue(_skeleton.GetBoneName(b), out var w) ? Math.Clamp(w, 0f, 1f) : 1f;
            }
        }
        return true;
    }

    /// <summary>Empties a slot (its bone mask is kept: it is rebuilt only for another weight source).</summary>
    public void ClearLayer(int slot)
    {
        var s = _slots[Slot(slot)];
        if (s.Clip is not null && s.Weight > 1e-3f) _layersDirty = true;
        s.Clip = null; s.Weight = 0f;
    }

    /// <summary>Empties every slot (the networked view does it before it places a character; each system lays its layer again when it applies).</summary>
    public void ClearPoseLayers()
    {
        foreach (var s in _slots) { if (s.Clip is not null && s.Weight > 1e-3f) _layersDirty = true; s.Clip = null; s.Weight = 0f; }
    }

    /// <summary>Set when a layer was laid or removed since the pose was applied: a clip held by <see cref="Pose"/> at the same time is then applied again (the layers are written onto the bones).</summary>
    bool _layersDirty;

    /// <summary>The M27 single layer: <see cref="SetLayer"/> in the action slot.</summary>
    public bool SetPoseLayer(string clip, float time, float weight, bool additive, IReadOnlyDictionary<string, float>? boneWeights = null) =>
        SetLayer(PoseSlots.Action, clip, time, weight, additive, boneWeights);

    /// <summary>Empties the action slot only (the recoil, hand-off and reload layer; the fall slots are not touched).</summary>
    public void ClearPoseLayer() => ClearLayer(PoseSlots.Action);

    /// <summary>Applies the filled slots in ascending order over the pose the stances or the clip gave.</summary>
    void ApplyPoseLayer(bool stanceMode)
    {
        _layersDirty = false;
        if (_skeleton is null) return;
        foreach (var s in _slots)
            if (s.Clip is not null && s.Weight > 1e-3f)
            {
                // anim/melee-flow: the melee variant is part of the clip's pose, so it fades in with the clip's entry (the share the snapshot keeps is not laid over): before, the
                // upper variant came in whole on the first frame of an attack's cross-fade (the transition probe: up to 234 cm per frame per frame at a combo step).
                if (!stanceMode && !NoMeleeFlow && ReferenceEquals(s, _slots[PoseSlots.MeleeVariant]) && _clipEntryKeep > 0f)
                {
                    float full = s.Weight;
                    s.Weight = full * (1f - _clipEntryKeep);
                    if (s.Weight > 1e-3f) ApplySlot(s, stanceMode);
                    s.Weight = full;
                    continue;
                }
                ApplySlot(s, stanceMode);
            }
    }

    /// <summary>anim/melee-flow: the share of the entry snapshot in the clip pose being applied (0 once the entry is over).</summary>
    float _clipEntryKeep;

    HavokTransform[] RefBuffer = Array.Empty<HavokTransform>();

    void ApplySlot(LayerSlot s, bool stanceMode)
    {
        var clip = s.Clip!;
        int n = _skeleton!.GetBoneCount();
        int tracks = clip.Animation.TrackCount;
        if (SampleBuffer.Length < tracks) SampleBuffer = new HavokTransform[tracks];
        var local = SampleBuffer;   // the frame's scratch buffer: the stance samples are done with it
        clip.Animation.SampleInto(s.Time, local);
        HavokTransform[]? reference = null;
        if (s.RefTime >= 0f)
        {
            if (RefBuffer.Length < tracks) RefBuffer = new HavokTransform[tracks];
            clip.Animation.SampleInto(s.RefTime, RefBuffer);
            reference = RefBuffer;
        }
        for (int t = 0; t < tracks; t++)
        {
            int bone = clip.TrackToBone[t];
            if ((uint)bone >= (uint)n) continue;
            // A bone the base pose did not drive this frame keeps its pose: a delta laid on it would add up from frame to frame.
            if (stanceMode ? _accWeight.Length != n || _accWeight[bone] <= 0f : _driven.Length != n || !_driven[bone]) continue;
            float w = s.Weight * (s.Mask is { } m && bone < m.Length ? m[bone] : 1f);
            if (w <= 1e-3f) continue;
            var q = new Quaternion(local[t].Rotation.X, local[t].Rotation.Y, local[t].Rotation.Z, local[t].Rotation.W).Normalized();
            var pos = new Vector3(local[t].Translation.X, local[t].Translation.Y, local[t].Translation.Z);
            var scale = new Vector3(local[t].Scale.X, local[t].Scale.Y, local[t].Scale.Z);
            var baseQ = _skeleton.GetBonePoseRotation(bone);
            if (reference is not null)
            {
                // A whole-pose clip as an offset: the rotation and translation it adds to its own reference pose.
                var rq = new Quaternion(reference[t].Rotation.X, reference[t].Rotation.Y, reference[t].Rotation.Z, reference[t].Rotation.W).Normalized();
                q = (q * rq.Inverse()).Normalized();
                pos -= new Vector3(reference[t].Translation.X, reference[t].Translation.Y, reference[t].Translation.Z);
                scale = Vector3.One;
            }
            if (s.Additive)
            {
                _skeleton.SetBonePoseRotation(bone, (Quaternion.Identity.Slerp(q, w) * baseQ).Normalized());
                _skeleton.SetBonePosePosition(bone, _skeleton.GetBonePosePosition(bone) + pos * w);
                _skeleton.SetBonePoseScale(bone, _skeleton.GetBonePoseScale(bone) * Vector3.One.Lerp(scale, w));
            }
            else
            {
                _skeleton.SetBonePoseRotation(bone, baseQ.Slerp(q, w).Normalized());
                _skeleton.SetBonePosePosition(bone, _skeleton.GetBonePosePosition(bone).Lerp(pos, w));
                _skeleton.SetBonePoseScale(bone, _skeleton.GetBonePoseScale(bone).Lerp(scale, w));
            }
        }
    }
}
