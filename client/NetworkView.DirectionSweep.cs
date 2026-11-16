using System;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// <c>--direction-sweep</c> (anim/strafe-blend test aid, with <c>--bot --bot-gait</c>; ours): a bot that changes its movement direction again and again at a fixed view, for the
/// direction events of the transition probe (<c>--transition-probe</c>, best on a recorded demo replayed at 60 fps): strafe and forward / back reversals at a run and at a walk, run to
/// strafe (90 degrees), diagonal changes and reversals, 45 degree steps, a sprint left for a strafe or a backpedal and a strafe into a sprint, the same in aim mode and in the ready run
/// (a trigger tap at the start of each segment keeps the weapon up). Each leg is a list of segments (as <c>--transition-sweep</c>), then a walk back to the start point and a rest.
/// No effect on the simulation or the protocol.
/// </summary>
public partial class NetworkView
{
    static readonly bool DirectionSweepOn = OS.GetCmdlineUserArgs().Contains("--direction-sweep");

    const float D = 0.7071f;
    static SweepSegment Seg(float seconds, float x, float y, InputButtons hold = InputButtons.None, InputButtons press = InputButtons.None) => new(seconds, x, y, hold, press);

    static TransitionLeg[] Reversals(string name, float stick, InputButtons hold, InputButtons press, params (float X, float Y)[] dirs) =>
        [new(name, Array.ConvertAll(dirs, d => Seg(0.7f, d.X * stick, d.Y * stick, hold, press)))];

    static readonly TransitionLeg[] DirectionLegs =
    [
        .. Reversals("run strafe reversals", 1f, InputButtons.None, InputButtons.None, (1, 0), (-1, 0), (1, 0), (-1, 0), (1, 0), (-1, 0)),
        .. Reversals("run forward-back reversals", 1f, InputButtons.None, InputButtons.None, (0, 1), (0, -1), (0, 1), (0, -1), (0, 1), (0, -1)),
        .. Reversals("run 90 degree turns", 1f, InputButtons.None, InputButtons.None, (0, 1), (1, 0), (0, -1), (-1, 0), (0, 1), (-1, 0), (0, -1), (1, 0)),
        .. Reversals("run diagonals", 1f, InputButtons.None, InputButtons.None, (-D, D), (D, D), (D, -D), (-D, -D), (-D, D), (D, -D), (-D, D), (D, -D)),
        .. Reversals("run 45 degree steps", 1f, InputButtons.None, InputButtons.None, (0, 1), (D, D), (1, 0), (D, -D), (0, -1), (-D, -D), (-1, 0), (-D, D)),
        .. Reversals("walk strafe and forward-back reversals", 0.4f, InputButtons.None, InputButtons.None, (1, 0), (-1, 0), (1, 0), (-1, 0), (0, 1), (0, -1), (0, 1), (0, -1)),
        new("sprint, strafe off, reverse", [Seg(1.3f, 0, 1, InputButtons.Sprint), Seg(0.8f, 1, 0), Seg(0.7f, -1, 0)]),
        new("strafe, sprint, backpedal", [Seg(0.8f, -1, 0), Seg(1.3f, 0, 1, InputButtons.Sprint), Seg(0.9f, 0, -1)]),
        new("sprint, diagonal back", [Seg(1.3f, 0, 1, InputButtons.Sprint), Seg(0.9f, D, -D), Seg(0.7f, 1, 0, InputButtons.None)]),
        .. Reversals("aim strafe reversals", 1f, InputButtons.Aim, InputButtons.None, (1, 0), (-1, 0), (1, 0), (-1, 0), (1, 0), (-1, 0)),
        .. Reversals("aim turns and forward-back", 1f, InputButtons.Aim, InputButtons.None, (0, 1), (1, 0), (0, -1), (-1, 0), (0, 1), (0, -1), (D, D), (-D, -D)),
        .. Reversals("ready strafe reversals", 1f, InputButtons.None, InputButtons.Fire, (1, 0), (-1, 0), (1, 0), (-1, 0), (1, 0), (-1, 0)),
        .. Reversals("ready turns and forward-back", 1f, InputButtons.None, InputButtons.Fire, (0, 1), (1, 0), (0, -1), (-1, 0), (0, 1), (0, -1), (D, D), (-D, -D)),
    ];

    int _dsLeg = -1, _dsSegment, _dsPhase;   // _dsPhase: 0 the leg, 1 back to the start, 2 rest
    uint _dsTick;

    PlayerInput BotDirectionInput(uint tick)
    {
        var me = _client!.Predicted;
        float dt = _config!.TickSeconds;
        if (_gaitHome is null && me.Alive)
        {
            _gaitHome = new System.Numerics.Vector2(me.Position.X, me.Position.Y);
            GD.Print($"direction sweep home {me.Position.X:F2},{me.Position.Y:F2} facing yaw {me.Yaw:F2}");
        }
        if (_dsLeg < 0) { _dsLeg = DirectionLegs.Length - 1; _dsPhase = 2; _dsTick = tick; }   // a rest first, then the first leg
        var leg = DirectionLegs[_dsLeg % DirectionLegs.Length];
        double inSeg = (tick - _dsTick) * (double)dt;
        float x = 0f, y = 0f;
        var buttons = InputButtons.None;
        switch (_dsPhase)
        {
            case 0:
            {
                var seg = leg.Segments[_dsSegment];
                x = seg.X; y = seg.Y; buttons = seg.Hold;
                if (inSeg < dt * 1.5) buttons |= seg.Press;
                if (inSeg >= seg.Seconds)
                {
                    _dsTick = tick;
                    if (++_dsSegment >= leg.Segments.Length) { _dsSegment = 0; _dsPhase = 1; }
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
                        var forward = new System.Numerics.Vector2(-MathF.Sin(_yaw), MathF.Cos(_yaw));
                        var right = new System.Numerics.Vector2(forward.Y, -forward.X);
                        var d = System.Numerics.Vector2.Normalize(off);
                        x = System.Numerics.Vector2.Dot(d, right) * 0.5f; y = System.Numerics.Vector2.Dot(d, forward) * 0.5f;
                    }
                }
                if ((home && inSeg > 0.3) || inSeg > 6.0) { _dsPhase = 2; _dsTick = tick; }
                break;
            }
            default:
                if (inSeg >= 1.2)
                {
                    _dsPhase = 0; _dsTick = tick; _dsSegment = 0;
                    _dsLeg++;
                    GD.Print($"DSWEEP {DirectionLegs[_dsLeg % DirectionLegs.Length].Name} at t {_connectedFor:F2}");
                }
                break;
        }
        if (!me.Alive) { x = 0f; y = 0f; buttons = InputButtons.None; }
        return new PlayerInput(tick, x, y, _yaw, buttons, 0f);
    }
}
