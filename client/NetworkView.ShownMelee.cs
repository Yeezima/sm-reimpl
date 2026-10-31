using System.Collections.Generic;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// sim/stomp-in-place follow-up (owner, 2026-10-31: the stomp's "effects and damage/stun is going out early ... when the foot hits the ground ... should trigger"; client
/// presentation only, ours). A melee strike's effects (the stomp's rings, cracks, light, the sword's flare), its camera shake and the inferred hit sound used to fire when the
/// SIMULATION's attack time crossed the hit time: the own predicted state is up to a tick (33 ms) ahead of the pose drawn from it (<see cref="OwnClipClock"/>), and an other
/// player's snapshot value steps while the drawn clip clock runs on ahead of it. They now fire when the time of the clip actually DRAWN crosses the hit time
/// (the data's DISPLACE_FootStomp tag, 0.767 s, where the drawn foot lands: measured at 0.735 s).
/// </summary>
public partial class NetworkView
{
    readonly Dictionary<int, float> _shownAttack = new();
    readonly List<(bool Blast, float HitTime, float FxTime)> _pendingShakes = new();

    /// <summary>Records the attack clip time drawn for a player this frame (called by the clip clocks); other clips clear it.</summary>
    void NoteShownAttack(int id, ClipKind kind, float shown)
    {
        if (kind == ClipKind.Attack) _shownAttack[id] = shown; else _shownAttack.Remove(id);
    }

    /// <summary>The attack time of the pose drawn for a player (seconds into the strike); the state's own time when no clip clock covers it (smoothing off, bots).</summary>
    float ShownMeleeTime(PlayerState s) => !SimMeleeGate && _shownAttack.TryGetValue(s.PlayerId, out float t) ? t : s.MeleeTime;

    /// <summary>Test aid <c>--stomp-sim-gate</c>: the old gate (the state's own attack time) for before / after captures.</summary>
    bool SimMeleeGate => _simMeleeGate ??= CmdFlag("stomp-sim-gate");
    bool? _simMeleeGate;

    /// <summary>The foot's landing in the stomp clip (0.735 s of 1.667 s, measured from the left toe bone's height; ours, for the capture log only).</summary>
    public const float FootContactFraction = 0.441f;
    readonly Dictionary<int, long> _footFrame = new();

    /// <summary>Under <c>--log-melee-fx</c>: notes the frame the drawn stomp pose first reaches the foot's landing (call every frame a stomp strike is shown).</summary>
    void NoteFootContact(PlayerState s, MeleeStep step)
    {
        if (!CmdFlag("log-melee-fx") || step.Stomp is null) return;
        if (!_shownAttack.TryGetValue(s.PlayerId, out float shown)) shown = s.MeleeTime;
        if (shown < FootContactFraction * step.Duration || s.MeleeTime < FootContactFraction * step.Duration) {   // the drawn time never leads the state's: a stale drawn time from the strike before is ignored
            _footFrame.Remove(s.PlayerId); return; }
        if (!_footFrame.ContainsKey(s.PlayerId)) _footFrame[s.PlayerId] = (long)Godot.Engine.GetFramesDrawn();
    }

    /// <summary>The time into the strike its effects, shake and hit sound fire at: the hit time, except the heavy classes' in-place stomp, which fires where the drawn foot lands (<see cref="FootContactFraction"/>, never later than the hit).</summary>
    /// <summary>The time the effect gate is given for a player's strike (<see cref="MeleeHitGate.EffectTime"/>).</summary>
    float GateTime(PlayerState s, MeleeStep step) => ImpactRules.MeleeHitGate.EffectTime(ShownMeleeTime(s), s.MeleeTime, FxTime(step), step.HitTime);

    float FxTime(MeleeStep step) => step.InPlace && !SimMeleeGate ? System.MathF.Min(step.HitTime, FootContactFraction * step.Duration) : step.HitTime;

    string FootContactText(int id) => _footFrame.TryGetValue(id, out long f) ? $"drawn foot contact at frame {f}, effects at frame {Godot.Engine.GetFramesDrawn()} (+{Godot.Engine.GetFramesDrawn() - f})" : "foot contact not seen";
}
