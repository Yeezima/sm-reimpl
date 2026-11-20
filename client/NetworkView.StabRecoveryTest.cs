using System;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// sim/stab-release capture aid <c>--stab-recovery-test move|fire|melee|evade|cycle</c> (with <c>--bot --class assault --melee "power sword"</c>; not original behaviour; no effect on the simulation
/// or the protocol): the bot stabs (the Stab bit for four ticks) and, from the tick the stab's hit lands, mashes the follow-up (<c>move</c>: W held; <c>fire</c>: the trigger tapped every third tick;
/// <c>melee</c>: a light press every third tick; <c>evade</c>: Space tapped every third tick, W held; <c>cycle</c>: one of them per stab in turn), logging <c>STABREC</c> lines with the seconds
/// from the stab's start to its hit, to the end of its attack state and to the follow-up's first effect, and burning a timer into the picture (seconds since the stab's press, the stab's state, the
/// action under way) for recordings. Every cycle (<c>--stab-recovery-cycle</c> seconds, default 5) the view turns round so the bot stays in the open.
/// </summary>
public partial class NetworkView
{
    static readonly string? StabRecMode = CmdArg("stab-recovery-test");
    static readonly float StabRecCycle = Math.Max(3.5f, CmdFloat("--stab-recovery-cycle", 5f));
    static readonly string[] StabRecActions = ["move", "fire", "melee", "evade"];
    static readonly double StabRecLead = Math.Max(2f, CmdFloat("--stab-recovery-lead", 2f));
    double _srStart = -1;
    float _srYaw;
    int _srCycle = -1, _srTick, _srStabTick = -1;
    string _srAction = "move";
    bool _srHit, _srEnded, _srEffect;
    int _srHitTick, _srEndTick;
    int _srPrevStep, _srPrevClip;
    Label? _srLabel;

    PlayerInput? StabRecoveryTestInput(PlayerState me, uint tick)
    {
        if (StabRecMode is not { Length: > 0 } mode || !me.Alive || _config is null) { _srStart = -1; return null; }
        if (_srStart < 0) { _srStart = _connectedFor + StabRecLead; _srYaw = _yaw; }
        if (_srLabel is null)
        {
            var layer = new CanvasLayer { Layer = 100 };
            _srLabel = new Label { Position = new Vector2(24f, 24f), Text = "" };
            _srLabel.AddThemeFontSizeOverride("font_size", 34);
            _srLabel.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.2f));
            _srLabel.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
            _srLabel.AddThemeConstantOverride("outline_size", 8);
            layer.AddChild(_srLabel);
            AddChild(layer);
        }
        float dt = _config.TickSeconds;
        double t = _connectedFor - _srStart;
        PlayerInput Make(float mx, float my, InputButtons b) => new(tick, mx, my, _srYaw, b, -0.1f);
        if (t < 0) return Make(0f, StabRecLead > 3f && t > -StabRecLead + 2.0 ? 1f : 0f, InputButtons.None);   // a lead-in walk (--stab-recovery-lead s) to reach open floor
        int cycle = (int)(t / StabRecCycle);
        double phase = t - cycle * StabRecCycle;
        if (cycle != _srCycle)
        {
            if (_srCycle >= 0) _srYaw = ViewSmoothing.WrapPi(_srYaw + MathF.PI);
            _srCycle = cycle; _srTick = 0; _srStabTick = -1; _srHit = _srEnded = _srEffect = false;
            _srAction = mode == "cycle" ? StabRecActions[cycle % StabRecActions.Length] : mode;
        }
        _yaw = _srYaw; _pitch = -0.1f;
        _srTick++;
        bool stabbing = me.MeleeStep > 0 && me.MeleeHeavy;
        if (_srStabTick >= 0)
        {
            int since = _srTick - _srStabTick;
            double secs = since * dt;
            if (!_srHit && me.MeleeTime >= 0.636f - 1e-4f) { _srHit = true; _srHitTick = since; }
            if (_srHit && !_srEnded && !stabbing) { _srEnded = true; _srEndTick = since; }
            bool effect = _srAction switch
            {
                "move" => me.Velocity.Length() > 1f && (!stabbing || me.MeleeTime >= 0.9f),
                "fire" => me.Clip < _srPrevClip,
                "melee" => me.MeleeStep > 0 && !me.MeleeHeavy && _srHit,
                "evade" => me.EvadeTicks > 0,
                _ => false,
            };
            if (!_srEffect && _srHit && effect)
            {
                _srEffect = true;
                GD.Print($"STABREC {_srAction} t {_connectedFor:F2}: stab press at +0 s, hit +{_srHitTick * dt:F3} s (tick {_srHitTick}), attack state over +{(_srEnded ? _srEndTick * dt : double.NaN):F3} s, first {_srAction} +{secs:F3} s (tick {since}), {(since - _srHitTick) * dt:F3} s after the hit");
            }
            _srLabel.Text = $"STAB +{secs:0.000} s   {(!_srHit ? "swinging" : stabbing ? "HIT landed, still locked" : "FREE")}   [{_srAction}]{(_srEffect ? $"  {_srAction.ToUpperInvariant()} at +{secs:0.000}" : "")}";
        }
        else _srLabel.Text = $"[{_srAction}]";
        _srPrevStep = me.MeleeStep; _srPrevClip = me.Clip;
        // Stab at 0.5 s into the cycle from rest; then, from the hit on, mash the follow-up.
        if (_srStabTick < 0)
        {
            if (phase < 0.5 || me.MeleeStep != 0 || !me.Grounded) return Make(0f, 0f, InputButtons.None);
            _srStabTick = _srTick;
            return Make(0f, 0f, InputButtons.Stab);
        }
        if (_srTick - _srStabTick < 4) return Make(0f, 0f, InputButtons.Stab);
        if (!_srHit && stabbing) return Make(0f, 0f, InputButtons.None);
        if (_srTick - _srStabTick > 5.0f / dt) return Make(0f, 0f, InputButtons.None);
        bool tap = _srTick % 3 == 0;
        return _srAction switch
        {
            "move" => Make(0f, 1f, InputButtons.None),
            "fire" => Make(0f, 0f, tap ? InputButtons.Fire : InputButtons.None),
            "melee" => Make(0f, 0f, tap ? InputButtons.Melee : InputButtons.None),
            "evade" => Make(0f, 1f, tap ? InputButtons.Jump : InputButtons.None),
            _ => Make(0f, 0f, InputButtons.None),
        };
    }
}
