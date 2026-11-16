using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;
using SmSim;

namespace SmClient;

/// <summary>
/// Musou weight pass MW4, attack punch (owner 2026-11-10: "Yes, retime"; "Pose hold + camera push"). Every marine's strike is drawn with a slower wind-up and a faster strike
/// that still hits at the strike's hit time and ends at its end (<see cref="MusouPunchRules.ClipTime"/>; the simulation is untouched). The own marine's heavy strikes and combo
/// finishers that meet Orks hold his pose 50 ms at the hit while the Orks move on (no freeze, no shake) and push the camera a few centimetres along the swing
/// (<see cref="ExtrasCameraOffset"/>, scaled by the Camera shake option). Kills are not known at the strike (the server reports them about 0.19 s later), so the hold
/// marks heavy strikes and finishers only. Hit Orks of the rank and file are pushed back by the weapon (<see cref="MusouPunchRules.OrkPush"/>).
/// Switch <c>--no-musou-punch</c>.
/// </summary>
public partial class NetworkView
{
    static readonly bool NoMusouPunch = CmdFlag("no-musou-punch");

    sealed class MusouStrikeShow
    {
        public int Step;
        public bool Heavy;
        public float Last = -1f;
        public float Hold;
    }

    readonly Dictionary<int, MusouStrikeShow> _musouStrikes = new();
    double _musouPushAt = double.NegativeInfinity;
    float _musouPushForward, _musouPushSide;
    int _musouHolds;

    bool MusouPunchOn => MusouWeightOn && !NoMusouPunch;

    /// <summary>The clip time drawn for a marine's strike (from the attack pose, through <see cref="ExtrasAttackTime"/>): the remap, and for the own marine the hold decided at the hit.</summary>
    void MusouPunchTime(PlayerState s, bool own, ref float attackTime)
    {
        if (!MusouPunchOn || _config is null || s.MeleeStep <= 0 || IsOrkVoice(s) || Combat.MeleeStepOf(_config, s) is not { } step) return;
        if (!_musouStrikes.TryGetValue(s.PlayerId, out var show)) _musouStrikes[s.PlayerId] = show = new MusouStrikeShow();
        if (show.Step != s.MeleeStep || show.Heavy != s.MeleeHeavy || attackTime + 1e-4f < show.Last)
        {
            show.Step = s.MeleeStep; show.Heavy = s.MeleeHeavy; show.Hold = 0f; show.Last = -1f;
            if (own && LogMusouWeight) GD.Print($"MUSOU WEIGHT strike begins t {_connectedFor:F3} step {s.MeleeStep}{(s.MeleeHeavy ? " heavy" : "")} at {attackTime:F3} s (hit {step.HitTime:F2} s)");
        }
        if (own && show.Last < step.HitTime && attackTime >= step.HitTime && show.Last >= 0f)
        {
            int steps = (s.MeleeHeavy ? Loadouts.MeleeHeavy(_config, s) : Loadouts.Melee(_config, s)).Count;
            MusouStrikeTargets(s, step, MeleeWargear(s));
            if (LogMusouWeight) GD.Print($"MUSOU WEIGHT strike t {_connectedFor:F3} step {s.MeleeStep}/{steps}{(s.MeleeHeavy ? " heavy" : "")} hit {step.HitTime:F2} of {step.Duration:F2} s: {_musouStrikeTargets.Count} Orks in reach");
            if (MusouPunchRules.Accented(s.MeleeHeavy, s.MeleeStep, steps, _musouStrikeTargets.Count))
            {
                show.Hold = MusouPunchRules.Hold;
                _musouHolds++;
                _musouPushAt = _connectedFor;
                _musouPushForward = s.MeleeHeavy ? MusouPunchRules.PushForwardHeavy : MusouPunchRules.PushForward;
                _musouPushSide = (s.MeleeStep % 2 == 0 ? -1f : 1f) * MusouPunchRules.PushSide;   // the combo's swings alternate sides
                if (LogMusouWeight) GD.Print($"MUSOU WEIGHT punch t {_connectedFor:F3} step {s.MeleeStep}/{steps}{(s.MeleeHeavy ? " heavy" : "")}: {_musouStrikeTargets.Count} Orks met, hold {show.Hold * 1000f:F0} ms, push {_musouPushForward * 100f:F1} cm");
            }
        }
        show.Last = attackTime;
        attackTime = MusouPunchRules.ClipTime(attackTime, step.HitTime, step.Duration, s.MeleeHeavy, show.Hold);
    }

    /// <summary>The camera's push along the own swing (camera space: X right, Y up, Z back), before the Camera shake option's factor.</summary>
    partial void ExtrasCameraOffset(double delta, ref Vector3 offset)
    {
        if (!MusouPunchOn) return;
        float w = MusouPunchRules.PushWeight((float)(_connectedFor - _musouPushAt));
        if (w <= 0f) return;
        offset += new Vector3(_musouPushSide * w, 0f, -_musouPushForward * w);
    }

    /// <summary>The push back of an Ork of the rank and file hit by a marine's blow: by the weapon (MW4), the pilot's 0.2 m without the pass.</summary>
    float MusouMeleePush(PlayerState attacker) => MusouPunchOn ? MusouPunchRules.OrkPush(MeleeWargear(attacker)) : MusouContactRules.RecoilMelee;
}
