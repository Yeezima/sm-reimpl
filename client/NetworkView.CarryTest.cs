using System;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// anim/relic-bolter-idle capture aid <c>--carry-test</c> (with <c>--bot</c>; not original behaviour): the own marine runs a fixed 24 s cycle with the
/// weapon it spawned with, so screenshots show each carry pose of that weapon: stands (0 to 3 s: the relaxed idle), walks forward (3 to 5.5 s), stands,
/// holds aim mode (6.5 to 8 s), stands, sprints (9 to 10 s), stands, taps the trigger once (11 s: the raise and the shot, then the ready hold), taps melee
/// once (12.5 s: the strike and its hand-over back to the carry) and stands to the end, long enough for the 10 s ready hold to end before the next cycle's
/// relaxed idle. Each cycle turns round. It has the human follow camera (pitch <c>--carry-pitch</c>, default
/// -0.2 rad) and prints a line at each phase with the hip-fire family, the idle clip, the ready flag and its place and yaw (for a spectator's <c>--observe-pose</c>).
/// </summary>
public partial class NetworkView
{
    static readonly bool CarryTest = OS.GetCmdlineUserArgs().Contains("--carry-test");
    static readonly float CarryPitch = float.TryParse(CmdArg("carry-pitch"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) && float.IsFinite(p) ? p : -0.2f;
    double _carryStart = -1;
    float _carryYaw;
    int _carryCycle = -1;
    string _carryPhase = "";

    PlayerInput? CarryTestInput(PlayerState me, uint tick)
    {
        if (!CarryTest || !me.Alive) { _carryStart = -1; return null; }
        if (_carryStart < 0) { _carryStart = _connectedFor + 1.0; _carryYaw = _yaw; }
        double t = _connectedFor - _carryStart;
        if (t < 0) return new PlayerInput(tick, 0f, 0f, _carryYaw, InputButtons.None, CarryPitch);
        int cycle = (int)(t / 24.0);
        double phase = t - cycle * 24.0;
        if (cycle != _carryCycle)
        {
            if (_carryCycle >= 0) _carryYaw = ViewSmoothing.WrapPi(_carryYaw + MathF.PI);   // back the way it came
            _carryCycle = cycle;
        }
        _yaw = _carryYaw; _pitch = CarryPitch;
        var (name, forward, buttons) = phase switch
        {
            < 3.0 => ("idle", 0f, InputButtons.None),
            < 5.5 => ("walk", 0.5f, InputButtons.None),
            < 6.5 => ("idle", 0f, InputButtons.None),
            < 8.0 => ("aim", 0f, InputButtons.Aim),
            < 9.0 => ("idle", 0f, InputButtons.None),
            < 10.0 => ("sprint", 1f, InputButtons.Sprint),
            < 11.0 => ("idle", 0f, InputButtons.None),
            < 11.2 => ("fire", 0f, InputButtons.Fire),   // 0.2 s, so a slow frame does not skip the press
            < 12.5 => ("ready", 0f, InputButtons.None),
            < 12.65 => ("melee", 0f, InputButtons.Melee),
            < 13.5 => ("handover", 0f, InputButtons.None),
            _ => ("idle", 0f, InputButtons.None),
        };
        if (name != _carryPhase)
        {
            _carryPhase = name;
            GD.Print($"t {_connectedFor:F2} s: carry-test #{cycle} {name}: weapon {(_config is not null ? Loadouts.InHand(_config, me)?.Name : null) ?? "none"}, " +
                     $"hip family {HipFamilyOf(me)?.ToString() ?? "none"}, idle clip {IdleClipOf(me) ?? "none"}, ready {HipReady(me)}; " +
                     $"at {me.Position.X:F2},{me.Position.Y:F2},{me.Position.Z:F2} yaw {me.Yaw * 180f / MathF.PI:F1} deg");
        }
        return new PlayerInput(tick, 0f, forward, _carryYaw, buttons, CarryPitch);
    }
}
