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
    /// <summary>
    /// <c>--ledge-charge</c> (animation pass 3 test aid): the run off the ledge is a sprint with the charge pressed 0.5 s in, so the charge goes off the ledge (the airborne charge's fall pose).
    /// feel/melee-charge-2: pressed later on a longer run (the sprint covers the plan's run in about 0.65 of its time; pressed 0.5 s before that), so the charge is still
    /// under way at the edge; each landing logs whether the charge ended there (<c>--log-falls</c>).
    /// </summary>
    static readonly bool LedgeCharge = OS.GetCmdlineUserArgs().Contains("--ledge-charge");
    /// <summary>
    /// <c>--ledge-hop</c> (sim/movement-fixes capture aid): Space is pressed for one tick when the run would leave the ground within 8 ticks, so a Devastator / Havoc hops off the ledge
    /// (W held: the hop goes down while it keeps going forward). <c>--ledge-tap</c>: a one-tick Space tap 4 ticks after the body left the ground, so an Assault / Raptor dashes
    /// in the fall. Both log the press (<c>LEDGE hop</c> / <c>LEDGE tap</c>).
    /// </summary>
    static readonly bool LedgeHop = OS.GetCmdlineUserArgs().Contains("--ledge-hop"), LedgeTap = OS.GetCmdlineUserArgs().Contains("--ledge-tap");
    double _ledgeStart, _ledgeChargeAt = 0.5, _ledgeLeftAt;
    bool _ledgePressed;
    int _ledgeAirTicks;

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
            if (!me.Grounded && !_ledgeLeft) { _ledgeLeft = true; _ledgeLeftAt = _connectedFor; }
            bool landed = _ledgeLeft && me.Grounded && !(LedgeHop && Evade.Evading(me));
            if (landed) { _ledgeRunning = false; _ledgeSettleUntil = _connectedFor + 1.2; GD.Print($"t {_connectedFor:F2} s: LEDGE walked off and landed at {me.Position.X:F1},{me.Position.Y:F1},{me.Position.Z:F1}"); return idle; }
            if (_connectedFor < _ledgeUntil)
            {
                var b = InputButtons.None;
                if (LedgeCharge) { b = InputButtons.Sprint; double since = _connectedFor - _ledgeStart; if (since >= _ledgeChargeAt && since < _ledgeChargeAt + 0.06) b |= InputButtons.MeleeHeavy; }
                // sim/movement-fixes capture aids: one tick of Space before the edge (the hop) or in the fall (the dash).
                _ledgeAirTicks = me.Grounded ? 0 : _ledgeAirTicks + 1;
                bool pressNow = !_ledgePressed && (LedgeHop ? me.Grounded && LeavesWithin(me, _ledgeYaw, 8) : LedgeTap && _ledgeAirTicks >= 4);
                if (pressNow)
                {
                    _ledgePressed = true; b |= InputButtons.Jump;
                    GD.Print($"t {_connectedFor:F2} s: LEDGE {(LedgeHop ? "hop" : "tap")} at {me.Position.X:F1},{me.Position.Y:F1},{me.Position.Z:F1} ({(me.Grounded ? "on the ground" : $"{_connectedFor - _ledgeLeftAt:F2} s into the fall")})");
                }
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
            _ledgeChargeAt = Math.Max(0.5, 0.65 * p.Seconds - 0.5);
            _ledgePressed = false;
            GD.Print($"t {_connectedFor:F2} s: LEDGE plan {_ledgePlans}: heading {p.Yaw * 180f / MathF.PI:F0} deg, leaves the ground after {p.Seconds:F1} s, lands {p.Drop:F1} m lower after {p.AirSeconds:F2} s, peak {p.Peak:F1} m/s down");
        }
        else
        {
            _ledgeYaw = (float)(_ledgeRandom.NextDouble() * 2 * Math.PI); _yaw = _ledgeYaw;
            _ledgeUntil = _connectedFor + 2.0; _ledgeLeft = false; _ledgeRunning = true; _ledgeStart = _connectedFor; _ledgeChargeAt = 0.5; _ledgePressed = true;   // no hop or tap on a random run
            GD.Print($"t {_connectedFor:F2} s: LEDGE plan {_ledgePlans}: no ledge of {LedgeMinDrop:0.#} m within 8 s; a random heading for 2 s");
        }
        return idle;
    }

    /// <summary><c>--ledge-hop</c>: whether running on along <paramref name="yaw"/> leaves the ground within <paramref name="ticks"/> ticks (the real movement step on the map).</summary>
    bool LeavesWithin(PlayerState me, float yaw, int ticks)
    {
        var s = me;
        var input = new PlayerInput(0, 0f, 1f, yaw, InputButtons.None, 0f);
        for (int t = 0; t < ticks; t++) { s = Movement.Step(s, input, _config!, _client!.Collision); if (!s.Grounded) return true; }
        return false;
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
