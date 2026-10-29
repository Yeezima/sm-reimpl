using System;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// <c>--bot-ledge</c> (animation pass 1 test aid, with <c>--bot --bot-gait</c>): a bot that walks off ledges. From where it stands it simulates a straight run along each of 16 headings with the real
/// movement function (<see cref="Movement.Step"/> against the map's collision, the same the server and the prediction run) for up to 8 s and picks the earliest heading on which it leaves the
/// ground, lands without a kill region and falls at least <c>--ledge-min-drop</c> metres (default 3); it then runs that heading, logs the fall and landing events the fall pose sees
/// (<c>NetworkView.Fall.cs</c>) and, a second after it has landed, plans again from the lower floor. Without such a heading it runs a random one for 2 s. No effect on the simulation or the protocol.
/// </summary>
public partial class NetworkView
{
    static readonly bool LedgeWalk = OS.GetCmdlineUserArgs().Contains("--bot-ledge");
    static readonly float LedgeMinDrop = FloatArg("ledge-min-drop") ?? 3f;
    /// <summary><c>--ledge-charge</c> (animation pass 3 test aid): the run off the ledge is a sprint with the charge pressed 0.5 s in, so the charge goes off the ledge (the airborne charge's fall pose).</summary>
    static readonly bool LedgeCharge = OS.GetCmdlineUserArgs().Contains("--ledge-charge");
    double _ledgeStart;

    bool _ledgeRunning;
    float _ledgeYaw;
    double _ledgeUntil, _ledgeSettleUntil;
    bool _ledgeLeft;
    int _ledgePlans;
    readonly System.Random _ledgeRandom = new(7);

    PlayerInput BotLedgeInput(uint tick)
    {
        var me = _client!.Predicted;
        var idle = new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch);
        if (!me.Alive || _client.Collision is null || _config is null) { _ledgeRunning = false; return idle; }
        if (_ledgeRunning)
        {
            if (!me.Grounded) _ledgeLeft = true;
            bool landed = _ledgeLeft && me.Grounded;
            if (landed) { _ledgeRunning = false; _ledgeSettleUntil = _connectedFor + 1.2; GD.Print($"t {_connectedFor:F2} s: LEDGE walked off and landed at {me.Position.X:F1},{me.Position.Y:F1},{me.Position.Z:F1}"); return idle; }
            if (_connectedFor < _ledgeUntil)
            {
                var b = InputButtons.None;
                if (LedgeCharge) { b = InputButtons.Sprint; double since = _connectedFor - _ledgeStart; if (since is >= 0.5 and < 0.56) b |= InputButtons.MeleeHeavy; }
                return new PlayerInput(tick, 0f, 1f, _ledgeYaw, b, _pitch);
            }
            _ledgeRunning = false;
            _ledgeSettleUntil = _connectedFor + 0.5;
        }
        if (!me.Grounded || _connectedFor < _ledgeSettleUntil || _connectedFor < 3.0) return idle;
        if (me.Velocity.X * me.Velocity.X + me.Velocity.Y * me.Velocity.Y > 0.25f) return idle;   // plan from rest: the plan is exact only then
        _ledgePlans++;
        var plan = PlanLedge(me);
        if (plan is { } p)
        {
            _ledgeYaw = p.Yaw; _yaw = p.Yaw;
            _ledgeUntil = _connectedFor + p.Seconds + 3.0; _ledgeLeft = false; _ledgeRunning = true; _ledgeStart = _connectedFor;
            GD.Print($"t {_connectedFor:F2} s: LEDGE plan {_ledgePlans}: heading {p.Yaw * 180f / MathF.PI:F0} deg, leaves the ground after {p.Seconds:F1} s, lands {p.Drop:F1} m lower after {p.AirSeconds:F2} s, peak {p.Peak:F1} m/s down");
        }
        else
        {
            _ledgeYaw = (float)(_ledgeRandom.NextDouble() * 2 * Math.PI); _yaw = _ledgeYaw;
            _ledgeUntil = _connectedFor + 2.0; _ledgeLeft = false; _ledgeRunning = true; _ledgeStart = _connectedFor;
            GD.Print($"t {_connectedFor:F2} s: LEDGE plan {_ledgePlans}: no ledge of {LedgeMinDrop:0.#} m within 8 s; a random heading for 2 s");
        }
        return idle;
    }

    /// <summary>The earliest straight run (of 16 headings, 8 s) that walks off a ledge and lands, or null.</summary>
    (float Yaw, float Seconds, float Drop, float AirSeconds, float Peak)? PlanLedge(PlayerState me)
    {
        var world = _client!.Collision!;
        var best = ((float Yaw, float Seconds, float Drop, float AirSeconds, float Peak)?)null;
        for (int k = 0; k < 16; k++)
        {
            float yaw = k * MathF.PI / 8f;
            var s = me with { Velocity = System.Numerics.Vector3.Zero, Yaw = yaw };
            var input = new PlayerInput(0, 0f, 1f, yaw, InputButtons.None, 0f);
            bool left = false; float leftZ = 0f, peak = 0f; int leftTick = 0;
            for (int t = 0; t < 30 * 8; t++)
            {
                var before = s.Position;
                s = Movement.Step(s, input, _config!, world);
                if (world.KillsPlayer(before, s.Position, _config!.CapsuleHeight)) break;
                if (!s.Grounded) { if (!left) { left = true; leftZ = s.Position.Z; leftTick = t; } peak = MathF.Max(peak, -s.Velocity.Z); }
                else if (left)
                {
                    float drop = leftZ - s.Position.Z;
                    if (drop >= LedgeMinDrop && (best is null || leftTick / 30f < best.Value.Seconds)) best = (yaw, leftTick / 30f, drop, (t - leftTick) / 30f, peak);
                    break;
                }
            }
        }
        return best;
    }

    /// <summary>The bot log's ledge counter.</summary>
    string LedgeStats() => LedgeWalk ? $"ledge plans {_ledgePlans}" : "";
}
