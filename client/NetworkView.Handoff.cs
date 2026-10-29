using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Action clips that must hand the legs back to the locomotion blend (M40-T4 follow-up; the owner's playtest: after an F stun strike the character slid along the
/// ground when the player moved again as soon as allowed). The simulation lets the player move again while some actions are still playing:
/// <list type="bullet">
/// <item>a melee attack from its <c>MoveAt</c> (the graph's <c>BRANCH_Melee_Run</c> / <c>BRANCH_Melee_Draw_Run</c>, <see cref="MeleeStep.MoveAt"/>) to its end, which is the
/// tail of every combo step and heavy / stun strike; the attack clips are whole-body and stand in place;</item>
/// <item>a grenade throw, which never locks movement (the toss clip is whole-body and in place).</item>
/// </list>
/// Until now the clip kept the whole body, so a marine that ran off showed its feet planted at running speed (measured with <c>--action-sweep --anim-probe</c>: a stun
/// strike's clip showed for 0.98 s while the body moved at 6.5 m/s, the feet sliding at 6.5 m/s). Now, from the moment the body moves (<see cref="ActionHandoff"/>: above
/// 0.5 m/s on the ground, not in a jump-pack phase; for an attack: not before <c>MoveAt</c>) the legs and the rest follow the locomotion blend (cross-faded in over 0.1 s,
/// <see cref="ClipBlends.Default"/>) and the action clip stays on the upper body only (the reload's bone mask, <see cref="HipFireSet.UpperBodyMask"/>: movement on the legs,
/// the action on the rest; the prop bones and arms have weight 1, so the weapon follows) until its end, fading out over its last 0.15 s. Without that mask (no hip-fire data
/// yet, <c>--no-hip-fire</c>, a graph without the reload blend) nothing is handed over and the whole-body clip shows as before. The tag sounds of the action clip
/// keep playing from its own time (<c>SoundLayerTags</c>). The other action clips already end with the simulation's state: reactions at <c>BRANCH_Run</c> (stick), rolls
/// and hops at their <c>BRANCH_Run</c>, the charge at <c>BRANCH_Attack2Idle</c>, the landings at their move lock (<c>NetworkView.Jump.cs</c>), the reload is already a masked
/// layer, a weapon switch has no body clip. Which clips, when and with what mask: **interpretation** (the original blends these inside its graphs); <c>--no-handoff</c>
/// (test aid) restores the whole-body clips.
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-handoff</c> (test aid): action clips keep the whole body while they show, as before the follow-up.</summary>
    static readonly bool NoHandoff = OS.GetCmdlineUserArgs().Contains("--no-handoff");

    /// <summary>The locomotion's cross-fade while an action clip hands the legs over (the graphs' "Default 3 Frame Transition", 0.1 s: <see cref="ClipBlends.Default"/>), else 0 (the stance's own).</summary>
    float _fadeOverride;

    void PlaceHandedOver(CharacterViewer character, PlayerState s, bool own)
    {
        _fadeOverride = ClipBlends.Default;
        try { PlaceLocomotion(character, s, own); }
        finally { _fadeOverride = 0f; }
    }

    static bool HandoffMoving(PlayerState s) =>
        ActionHandoff.Moving(s.Alive, s.Grounded, s.Jump == JumpPhase.None, MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y));

    IReadOnlyDictionary<string, float>? _actionMask;
    bool _actionMaskResolved;

    /// <summary>The upper-body mask the action clip is laid over the locomotion with (the rifle family's reload mask, else the first family's that has one), cached once the hip-fire data is in; null before that or when there is none.</summary>
    IReadOnlyDictionary<string, float>? ActionMask()
    {
        if (_actionMaskResolved) return _actionMask;
        if (_hipAnims is null) return null;
        _actionMask = _hipAnims.For(HipFireWeaponClass.Rifle)?.UpperBodyMask;
        if (_actionMask is null)
            foreach (var set in _hipAnims.Sets)
                if (set.UpperBodyMask is { } mask) { _actionMask = mask; break; }
        _actionMaskResolved = true;
        return _actionMask;
    }

    /// <summary>
    /// A melee attack clip at <paramref name="time"/> (the time it is posed at: the remote players' smoothed time) under the simulation's attack: true when the
    /// locomotion has taken over the legs (and the clip is laid over the upper body), false when the clip should keep the whole body.
    /// </summary>
    bool HandoffAttack(CharacterViewer character, PlayerState s, bool own, string clip, float time, float? clipTime = null)
    {
        var t = LocoOf(s.PlayerId);
        if (ActionHandoff.AttackChanged(t.HandStep, t.HandHeavy, t.HandTime, s.MeleeStep, s.MeleeHeavy, s.MeleeTime)) t.AttackHand = false;   // a new step or attack: by the state's own time
        t.HandStep = s.MeleeStep; t.HandHeavy = s.MeleeHeavy; t.HandTime = s.MeleeTime;
        if (NoHandoff || _config is null || Combat.MeleeStepOf(_config, s) is not { } step || ActionMask() is not { } mask) { t.AttackHand = false; return false; }
        // feel/melee-lock: motion that is the attack's own root motion (the stick idle after MoveAt) keeps the legs in the clip.
        // feel/locks-sprint-stop: from the move gate the simulation applies (MoveAt × the action lock scale).
        if (!t.AttackHand && ActionHandoff.AttackHandsOver(time, Combat.MoveAtOf(step, _config), HandoffMoving(s) && !AttackRootDriven(s, step, time))) t.AttackHand = true;
        if (!t.AttackHand) return false;
        PlaceHandedOver(character, s, own);
        character.SetPoseLayer(clip, clipTime ?? time, ActionHandoff.TailWeight(step.Duration, time), additive: false, mask);   // feel/melee-lock: the clip at its own time (a variant of another length)
        return true;
    }

    /// <summary>The grenade toss at <paramref name="throwTime"/>: true when the locomotion has the legs and the toss is laid over the upper body.</summary>
    bool HandoffThrow(CharacterViewer character, PlayerState s, bool own, string clip, float throwTime)
    {
        var t = LocoOf(s.PlayerId);
        if (ActionHandoff.ThrowRestarted(t.HandThrowTime, throwTime)) t.ThrowHand = false;   // a new throw
        t.HandThrowTime = throwTime;
        if (NoHandoff || _config is null || ActionMask() is not { } mask) { t.ThrowHand = false; return false; }
        if (!t.ThrowHand && HandoffMoving(s)) t.ThrowHand = true;
        if (!t.ThrowHand) return false;
        PlaceHandedOver(character, s, own);
        character.SetPoseLayer(clip, throwTime, ActionHandoff.ThrowWeight(_config.ThrowDuration, throwTime), additive: false, mask);
        return true;
    }
}
