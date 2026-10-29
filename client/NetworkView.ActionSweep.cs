using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// <c>--action-sweep</c> (M40-T4 follow-up test aid, with <c>--bot --bot-gait</c>): a bot that does one action after another while holding the stick forward, so that
/// the moment the simulation lets it move again (melee <c>MoveAt</c>, the throw, a roll's branch, a landing) can be seen in the pose: <c>--anim-probe</c> reports,
/// per action clip, how long the clip kept showing while the body moved and how far the planted feet slid meanwhile. Each action is followed by a back-run to the
/// start point and a short rest. <c>--action-class jump</c> swaps the legs for the jump-pack ones (jump, ground pound, landing; run it with <c>--class assault</c>).
/// </summary>
public partial class NetworkView
{
    static readonly bool ActionSweep = OS.GetCmdlineUserArgs().Contains("--action-sweep");
    static readonly bool ActionJump = OS.GetCmdlineUserArgs().Contains("--action-jump");

    /// <summary>One leg: a name (logged), presses (seconds into the leg, buttons for one tick), buttons held through the leg, the forward stick, seconds.</summary>
    sealed record ActionLeg(string Name, (float At, InputButtons Buttons)[] Presses, InputButtons Hold, float Stick, float Seconds);

    static readonly ActionLeg[] ActionLegs = BuildActionLegs();

    static ActionLeg[] BuildActionLegs()
    {
        if (ActionJump)
            return
            [
                new("jump", [(0f, InputButtons.Jump)], InputButtons.None, 1f, 3.0f),
                new("jump and ground pound", [(0f, InputButtons.Jump), (0.45f, InputButtons.Melee)], InputButtons.None, 1f, 4.0f),
                new("jump, forward run on landing", [(0f, InputButtons.Jump)], InputButtons.Sprint, 1f, 3.0f),
            ];
        return
        [
            new("stun strike", [(0f, InputButtons.MeleeHeavy)], InputButtons.None, 1f, 2.6f),
            new("light combo x3", [(0f, InputButtons.Melee), (0.5f, InputButtons.Melee), (1.0f, InputButtons.Melee)], InputButtons.None, 1f, 3.6f),
            new("light attack", [(0f, InputButtons.Melee)], InputButtons.None, 1f, 2.6f),
            new("heavy strikes x2", [(0f, InputButtons.MeleeHeavy), (0.7f, InputButtons.MeleeHeavy)], InputButtons.None, 1f, 3.2f),
            new("grenade throw", [(0f, InputButtons.Grenade)], InputButtons.None, 1f, 2.6f),
            new("reload", [(0f, InputButtons.Fire), (0.15f, InputButtons.Fire), (0.3f, InputButtons.Fire), (0.6f, InputButtons.Reload)], InputButtons.None, 1f, 3.2f),
            new("weapon switch", [(0f, InputButtons.Switch)], InputButtons.None, 1f, 2.6f),
            new("roll", [(0f, InputButtons.Jump)], InputButtons.None, 1f, 2.6f),
            new("charge", [(1.2f, InputButtons.MeleeHeavy)], InputButtons.Sprint, 1f, 3.4f),
        ];
    }

    int _actionLeg = -1, _actionPhase;   // _actionPhase: 0 the action, 1 back to the start, 2 rest
    uint _actionTick;

    PlayerInput BotActionInput(uint tick)
    {
        var me = _client!.Predicted;
        float dt = _config!.TickSeconds;
        if (_gaitHome is null && me.Alive)
        {
            _gaitHome = new System.Numerics.Vector2(me.Position.X, me.Position.Y);
            GD.Print($"action sweep home {me.Position.X:F2},{me.Position.Y:F2} facing yaw {me.Yaw:F2}");
        }
        if (_actionLeg < 0) { _actionLeg = 0; _actionPhase = 2; _actionTick = tick; }   // a rest first
        var leg = ActionLegs[_actionLeg % ActionLegs.Length];
        double inLeg = (tick - _actionTick) * (double)dt;
        float x = 0f, y = 0f;
        var buttons = InputButtons.None;
        switch (_actionPhase)
        {
            case 0:
                y = leg.Stick; buttons = leg.Hold;
                foreach (var (at, b) in leg.Presses) if (inLeg >= at && inLeg < at + dt * 1.5) buttons |= b;
                if (inLeg >= leg.Seconds) { _actionPhase = 1; _actionTick = tick; }
                break;
            case 1:
            {
                y = -1f;
                bool home = false;
                if (_gaitHome is { } h)
                {
                    var forward = new System.Numerics.Vector2(-MathF.Sin(_yaw), MathF.Cos(_yaw));
                    home = System.Numerics.Vector2.Dot(new System.Numerics.Vector2(me.Position.X, me.Position.Y) - h, forward) <= 0.5f;
                }
                if ((home && inLeg > 0.3) || inLeg > 4.0) { _actionPhase = 2; _actionTick = tick; }
                break;
            }
            default:
                if (inLeg >= 1.2)
                {
                    _actionPhase = 0; _actionTick = tick;
                    _actionLeg++;
                    GD.Print($"ACTION {ActionLegs[_actionLeg % ActionLegs.Length].Name} at {DateTime.Now:HH:mm:ss.fff}");
                }
                break;
        }
        if (!me.Alive) { x = 0f; y = 0f; buttons = InputButtons.None; }
        return new PlayerInput(tick, x, y, _yaw, buttons, 0f);
    }
}
