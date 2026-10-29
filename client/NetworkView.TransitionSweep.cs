using System;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// <c>--transition-sweep</c> (animation pass 3 test aid, with <c>--bot --bot-gait</c>; ours): a bot that goes through the switches of the shown state one after another, for the
/// transition probe (<c>--transition-probe</c>) on a recorded demo: sprint from standstill and let go of every key (the sprint stop), sprint then run then stop, run and stop, a roll
/// out of a run, a charge and let go, a throw on the run, a melee strike out of a run, aim mode on and off standing and walking, a weapon switch on the run. Each leg is a list of
/// segments (seconds, stick, held buttons, presses), followed by a walk back to the start point and a rest. No effect on the simulation or the protocol.
/// </summary>
public partial class NetworkView
{
    static readonly bool TransitionSweep = OS.GetCmdlineUserArgs().Contains("--transition-sweep");

    /// <summary>One segment of a leg: its length, the stick (x right, y forward), the buttons held and the buttons pressed for one tick at its start.</summary>
    sealed record SweepSegment(float Seconds, float X, float Y, InputButtons Hold, InputButtons Press = InputButtons.None);
    sealed record TransitionLeg(string Name, SweepSegment[] Segments);

    static readonly TransitionLeg[] TransitionLegs =
    [
        new("sprint start, let go", [new(2.0f, 0f, 1f, InputButtons.Sprint), new(1.8f, 0f, 0f, InputButtons.None)]),
        new("run, let go", [new(1.5f, 0f, 1f, InputButtons.None), new(1.5f, 0f, 0f, InputButtons.None)]),
        new("sprint, run on, let go", [new(1.8f, 0f, 1f, InputButtons.Sprint), new(1.2f, 0f, 1f, InputButtons.None), new(1.2f, 0f, 0f, InputButtons.None)]),
        new("run, roll, run on", [new(0.6f, 0f, 1f, InputButtons.None), new(0.1f, 0f, 1f, InputButtons.None, InputButtons.Jump), new(1.6f, 0f, 1f, InputButtons.None), new(1.2f, 0f, 0f, InputButtons.None)]),
        new("sprint, charge, let go", [new(1.2f, 0f, 1f, InputButtons.Sprint), new(0.1f, 0f, 1f, InputButtons.Sprint, InputButtons.MeleeHeavy), new(2.4f, 0f, 0f, InputButtons.None)]),
        new("sprint, charge, run on", [new(1.2f, 0f, 1f, InputButtons.Sprint), new(0.1f, 0f, 1f, InputButtons.Sprint, InputButtons.MeleeHeavy), new(2.4f, 0f, 1f, InputButtons.None), new(1.0f, 0f, 0f, InputButtons.None)]),
        new("run, throw", [new(0.6f, 0f, 1f, InputButtons.None), new(0.1f, 0f, 1f, InputButtons.None, InputButtons.Grenade), new(1.8f, 0f, 1f, InputButtons.None), new(1.2f, 0f, 0f, InputButtons.None)]),
        new("run, melee", [new(0.6f, 0f, 1f, InputButtons.None), new(0.1f, 0f, 1f, InputButtons.None, InputButtons.Melee), new(2.0f, 0f, 0f, InputButtons.None)]),
        new("aim, walk, let go", [new(1.0f, 0f, 0f, InputButtons.Aim), new(1.2f, 0f, 1f, InputButtons.Aim), new(1.0f, 0f, 0f, InputButtons.None)]),
        new("run, switch", [new(0.6f, 0f, 1f, InputButtons.None), new(0.1f, 0f, 1f, InputButtons.None, InputButtons.Switch), new(1.5f, 0f, 1f, InputButtons.None), new(1.2f, 0f, 0f, InputButtons.None)]),
        new("strafe, let go", [new(1.2f, 1f, 0f, InputButtons.None), new(1.2f, 0f, 0f, InputButtons.None)]),
        new("sprint, hop or jump", [new(1.2f, 0f, 1f, InputButtons.Sprint), new(0.1f, 0f, 1f, InputButtons.None, InputButtons.Jump), new(1.8f, 0f, 1f, InputButtons.None), new(1.5f, 0f, 0f, InputButtons.None)]),
    ];

    int _tsLeg = -1, _tsSegment, _tsPhase;   // _tsPhase: 0 the leg, 1 back to the start, 2 rest
    uint _tsTick;

    PlayerInput BotTransitionInput(uint tick)
    {
        var me = _client!.Predicted;
        float dt = _config!.TickSeconds;
        if (_gaitHome is null && me.Alive)
        {
            _gaitHome = new System.Numerics.Vector2(me.Position.X, me.Position.Y);
            GD.Print($"transition sweep home {me.Position.X:F2},{me.Position.Y:F2} facing yaw {me.Yaw:F2}");
        }
        if (_tsLeg < 0) { _tsLeg = TransitionLegs.Length - 1; _tsPhase = 2; _tsTick = tick; }   // a rest first, then the first leg (the sprint stop)
        var leg = TransitionLegs[_tsLeg % TransitionLegs.Length];
        double inSeg = (tick - _tsTick) * (double)dt;
        float x = 0f, y = 0f;
        var buttons = InputButtons.None;
        switch (_tsPhase)
        {
            case 0:
            {
                var seg = leg.Segments[_tsSegment];
                x = seg.X; y = seg.Y; buttons = seg.Hold;
                if (inSeg < dt * 1.5) buttons |= seg.Press;
                if (inSeg >= seg.Seconds)
                {
                    _tsTick = tick;
                    if (++_tsSegment >= leg.Segments.Length) { _tsSegment = 0; _tsPhase = 1; }
                }
                break;
            }
            case 1:
            {
                bool home = true;
                if (_gaitHome is { } h)
                {
                    var off = new System.Numerics.Vector2(h.X - me.Position.X, h.Y - me.Position.Y);
                    home = off.LengthSquared() < 0.6f * 0.6f;
                    if (!home)
                    {
                        // The stick toward home in the view's frame (the view does not turn).
                        var forward = new System.Numerics.Vector2(-MathF.Sin(_yaw), MathF.Cos(_yaw));
                        var right = new System.Numerics.Vector2(forward.Y, -forward.X);
                        var d = System.Numerics.Vector2.Normalize(off);
                        x = System.Numerics.Vector2.Dot(d, right) * 0.5f; y = System.Numerics.Vector2.Dot(d, forward) * 0.5f;   // a walk: the run back must not be a sprint
                    }
                }
                if ((home && inSeg > 0.3) || inSeg > 6.0) { _tsPhase = 2; _tsTick = tick; }
                break;
            }
            default:
                if (inSeg >= 1.5)
                {
                    _tsPhase = 0; _tsTick = tick; _tsSegment = 0;
                    _tsLeg++;
                    GD.Print($"TSWEEP {TransitionLegs[_tsLeg % TransitionLegs.Length].Name} at t {_connectedFor:F2}");
                }
                break;
        }
        if (!me.Alive) { x = 0f; y = 0f; buttons = InputButtons.None; }
        return new PlayerInput(tick, x, y, _yaw, buttons, 0f);
    }
}
