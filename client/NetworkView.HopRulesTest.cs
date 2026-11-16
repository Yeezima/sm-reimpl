using System;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// sim/hop-rules capture aid <c>--hop-rules-test swing-hop|tackle-hop</c> (with <c>--bot</c>, an Assault or Raptor class; not original behaviour; no effect on the simulation or the protocol):
/// <c>swing-hop</c> presses melee, taps Space in the swing (the hop) and presses melee every third tick through the hop until the next swing starts (swing, hop, swing, hop ...), logging the
/// seconds from each hop's start to the swing that follows (<c>HOPRULES swing</c>); <c>tackle-hop</c> dashes forward (a Space tap), presses melee in the dash's window (the aerial tackle) and, a few ticks
/// into the tackle, taps Space (<c>--hop-rules-at</c> ticks, default 4), logging whether the hop started on the press tick (<c>HOPRULES tackle</c>). The charge off a ledge is <c>--bot --bot-gait --bot-ledge
/// --ledge-charge --ledge-jump</c> (<c>NetworkView.Ledge.cs</c>). Every cycle (<c>--hop-rules-cycle</c> seconds, default 9) the view turns round.
/// </summary>
public partial class NetworkView
{
    static readonly string? HopRulesTestMode = CmdArg("hop-rules-test");
    static readonly float HopRulesCycle = Math.Max(4f, CmdFloat("--hop-rules-cycle", 9f));
    static readonly int HopRulesAt = (int)CmdFloat("--hop-rules-at", 4f);
    double _hrStart = -1;
    float _hrYaw;
    int _hrCycle = -1, _hrTick;
    bool _hrJumpSent, _hrPressed, _hrHopSent;
    double _hrHopAt = double.NaN;
    bool _hrTapReleased;

    PlayerInput? HopRulesTestInput(PlayerState me, uint tick)
    {
        if (HopRulesTestMode is not { Length: > 0 } mode || !me.Alive || _config is null) { _hrStart = -1; return null; }
        if (_hrStart < 0) { _hrStart = _connectedFor + 1.5; _hrYaw = _yaw; }
        double t = _connectedFor - _hrStart;
        PlayerInput Make(float mx, float my, InputButtons b) => new(tick, mx, my, _hrYaw, b, -0.2f);
        if (t < 0) return Make(0f, 0f, InputButtons.None);
        int cycle = (int)(t / HopRulesCycle);
        double phase = t - cycle * HopRulesCycle;
        if (cycle != _hrCycle)
        {
            if (_hrCycle >= 0) _hrYaw = ViewSmoothing.WrapPi(_hrYaw + MathF.PI);
            _hrCycle = cycle; _hrTick = 0; _hrJumpSent = false; _hrPressed = false; _hrHopSent = false; _hrTapReleased = false; _hrHopAt = double.NaN;
        }
        _yaw = _hrYaw; _pitch = -0.2f;
        _hrTick++;
        if (mode.StartsWith("tackle", StringComparison.OrdinalIgnoreCase))
        {
            if (phase < 0.5 || phase > HopRulesCycle - 1.0) return Make(0f, 0f, InputButtons.None);
            if (_hrTick % 15 == 0) GD.Print($"HOPRULES state t {_connectedFor:F2}: grounded {me.Grounded} jump {me.Jump} evade {me.EvadeTicks}/{me.EvadeLock} rush {me.Rush}/{me.RushLock} melee {me.MeleeStep} held {me.JumpHeld}");
            if (Rush.IsDash(me.Rush))
            {
                if (!_hrHopSent && me.RushTicks >= HopRulesAt)
                {
                    _hrHopSent = true;
                    GD.Print($"HOPRULES tackle t {_connectedFor:F2}: Space {me.RushTicks} ticks into the {me.Rush} (evade spacing {me.EvadeLock} ticks)");
                    return Make(0f, 1f, InputButtons.Jump);
                }
                return Make(0f, 0f, InputButtons.None);
            }
            if (_hrHopSent) { if (Evade.Evading(me) && me.EvadeTicks == 1) GD.Print($"HOPRULES tackle t {_connectedFor:F2}: the hop started in the press tick"); return Make(0f, 1f, InputButtons.None); }
            if (Evade.Evading(me) && !me.EvadeLanded)
            {
                bool press = Rush.CanDashAttack(me, _config, _client!.Collision) && me.RushLock == 0 && !me.MeleeHeld;
                return Make(0f, 0f, press ? InputButtons.Melee : InputButtons.None);
            }
            if (me.Jump == JumpPhase.TakeOff) return Make(0f, 1f, InputButtons.None);   // released: the tap is the dash
            if (!_hrJumpSent && me.Grounded && me.Jump == JumpPhase.None && !me.JumpHeld && me.EvadeLock == 0 && me.RushLock == 0 && me.MeleeStep == 0)
            {
                _hrJumpSent = true;
                return Make(0f, 1f, InputButtons.Jump);
            }
            return Make(0f, 0f, InputButtons.None);
        }
        // swing-hop: swing, hop, swing, hop ... with the melee press repeated through each hop.
        if (phase < 0.5 || phase > HopRulesCycle - 1.5) return Make(0f, 0f, InputButtons.None);
        if (Evade.Evading(me))
        {
            if (double.IsNaN(_hrHopAt)) _hrHopAt = _connectedFor;
            bool press = _hrTick % 3 == 0 && !me.MeleeHeld;
            return Make(0f, 0f, press ? InputButtons.Melee : InputButtons.None);
        }
        if (me.MeleeStep > 0)
        {
            if (!double.IsNaN(_hrHopAt)) { GD.Print($"HOPRULES swing t {_connectedFor:F2}: the swing came {_connectedFor - _hrHopAt:F2} s after the hop started"); _hrHopAt = double.NaN; }
            if (me.Jump == JumpPhase.TakeOff) return Make(0f, 0f, InputButtons.None);
            if (me.MeleeTime >= 0.07f && me.Jump == JumpPhase.None && !me.JumpHeld && me.EvadeLock == 0 && me.Grounded) return Make(0f, 0f, InputButtons.Jump);
            return Make(0f, 0f, InputButtons.None);
        }
        if (me.Jump == JumpPhase.TakeOff) return Make(0f, 0f, InputButtons.None);   // released: the tap is the hop
        if (me.Grounded && me.MeleeStep == 0 && me.Jump == JumpPhase.None && !me.MeleeHeld && _hrTick % 3 == 0) return Make(0f, 0f, InputButtons.Melee);
        return Make(0f, 0f, InputButtons.None);
    }
}
