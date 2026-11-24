using System;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// <c>--brace-turn-test</c> (capture aid, install transitions batch 3; with <c>--bot</c>, a heavy bolter class and <c>--bot-human-camera</c>): the bot runs forward 1.5 s, braces with Reload as it stops, then holds
/// the aim 100 degrees left of where it braced for 4 s (past the brace's left threshold, 60), back ahead for 3 s, 100 degrees right for 4 s (past the right one, 77), ahead for 3 s, and
/// repeats; a <c>BRACE-TURN</c> line each 0.25 s gives the phase, the brace, the aim against the drawn body, the turn side and the turn layer. Ours.
/// </summary>
public partial class NetworkView
{
    static readonly bool BraceTurnTest = OS.GetCmdlineUserArgs().Contains("--brace-turn-test");
    double _braceTestStart = -1, _braceTestLogAt;
    float _braceTestYaw;
    bool _braceTestPressed;

    PlayerInput? BraceTurnTestInput(PlayerState me, uint tick)
    {
        if (!BraceTurnTest || !me.Alive) { _braceTestStart = -1; return null; }
        if (_braceTestStart < 0) { _braceTestStart = _connectedFor + 1.0; _braceTestYaw = _yaw; _braceTestPressed = false; }
        double t = _connectedFor - _braceTestStart;
        var buttons = InputButtons.None;
        // Review: it runs forward for 1.5 s first and presses Reload as it lets go of the stick, so the brace starts out of a run (the gait flags still set).
        float forward = t is >= 0.0 and < 1.5 ? 1f : 0f;
        // One Reload press (released the tick after) while still running (the stance stops the body; the gait flags are still set), again whenever the stance has been left.
        if (t >= 1.2 && me.Brace == BracePhase.None && !me.ReloadHeld && !_braceTestPressed) { buttons |= InputButtons.Reload; _braceTestPressed = true; }
        else if (me.Brace != BracePhase.None) _braceTestPressed = false;
        double phase = t < 4.5 ? -1.0 : (t - 4.5) % 14.0;
        float offset = phase switch { < 0.0 => 0f, < 4.0 => 100f, < 7.0 => 0f, < 11.0 => -100f, _ => 0f };
        _yaw = ViewSmoothing.WrapPi(_braceTestYaw + offset * MathF.PI / 180f); _pitch = 0f;
        if (_connectedFor - _braceTestLogAt >= 0.25)
        {
            _braceTestLogAt = _connectedFor;
            var loco = LocoOf(me.PlayerId);
            float twist = loco.Body.Valid ? loco.Body.Twist(DisplayYaw(me)) * 180f / MathF.PI : float.NaN;
            GD.Print($"t {_connectedFor:F2} s: BRACE-TURN offset {offset:F0} brace {me.Brace} aim-vs-body {twist:F1} deg side {loco.Body.Side} layer {loco.Body.LayerSide} {loco.Body.LayerWeight:F2} turns {loco.Body.TurnsStarted} stand {loco.Stand?.Name ?? "none"} gait {(loco.GaitMoving || loco.HipGaitMoving ? "set" : "clear")}");
        }
        return new PlayerInput(tick, forward, 0f, _yaw, buttons, _pitch);
    }
}
