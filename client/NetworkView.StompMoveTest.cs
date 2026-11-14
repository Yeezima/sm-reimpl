using System;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// anim/stomp-to-move capture aid <c>--stomp-move-test walk|sprint</c> (with <c>--bot</c>; not original behaviour): the own heavy-class marine stands, presses the stun strike
/// (F: the Devastator's and the Havoc's stomp; any class's first heavy strike), holds W (and Shift for <c>sprint</c>) from 0.3 s after the press, so the simulation lets it move at the
/// strike's move gate, walks or sprints until 2.6 s, stands, turns round and does it again every 5 s. It has the human follow camera (pitch <c>--stomp-move-pitch</c>, default -0.25 rad)
/// and prints one line per frame of the strike: the strike time, the ground speed and the upper-body weight of the strike after the hand-over (1 before it).
/// </summary>
public partial class NetworkView
{
    static readonly string? StompMoveTestMode = CmdArg("stomp-move-test");
    static bool StompMoveTest => StompMoveTestMode is { Length: > 0 };
    static readonly float StompMovePitch = float.TryParse(CmdArg("stomp-move-pitch"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) && float.IsFinite(p) ? p : -0.25f;
    double _stompMoveStart = -1;
    float _stompMoveYaw;
    int _stompMoveCycle = -1;
    /// <summary>The own strike's upper-body weight after the hand-over this frame (1: not handed over), for the test log.</summary>
    float _ownHandWeight = 1f;

    PlayerInput? StompMoveTestInput(PlayerState me, uint tick)
    {
        if (!StompMoveTest || !me.Alive) { _stompMoveStart = -1; return null; }
        if (_stompMoveStart < 0) { _stompMoveStart = _connectedFor + 1.0; _stompMoveYaw = _yaw; }
        // sim/stomp-release capture mode (--stomp-move-test release; the owner 2026-11-14): the bolt pistol in hand, F every 4 s, then the fire button tapped every 0.2 s from 0.6 s on: nothing leaves until the
        // stomp frees the player 1.2 s into its clip, then the pistol fires and a walk starts at 2.2 s.
        if (StompMoveTestMode == "release")
        {
            if (me.Slot != WeaponSlot.Pistol) { _stompMoveStart = _connectedFor + 3.0; return new PlayerInput(tick, 0f, 0f, _stompMoveYaw, InputButtons.SlotPistol, StompMovePitch); }
            double rt = _connectedFor - _stompMoveStart;
            if (rt < 0) return new PlayerInput(tick, 0f, 0f, _stompMoveYaw, InputButtons.None, StompMovePitch);
            double ph = rt - Math.Floor(rt / 4.0) * 4.0;
            _yaw = _stompMoveYaw; _pitch = StompMovePitch;
            var rb = ph < 0.12 && me.MeleeStep == 0 ? InputButtons.MeleeHeavy : InputButtons.None;
            if (ph is >= 0.6 and < 2.2 && ((int)(ph * 5.0) % 2 == 0)) rb |= InputButtons.Fire;
            return new PlayerInput(tick, 0f, ph is >= 2.2 and < 3.2 ? 1f : 0f, _stompMoveYaw, rb, StompMovePitch);
        }
        double t = _connectedFor - _stompMoveStart;
        if (t < 0) return new PlayerInput(tick, 0f, 0f, _stompMoveYaw, InputButtons.None, StompMovePitch);
        int cycle = (int)(t / 5.0);
        double phase = t - cycle * 5.0;
        if (cycle != _stompMoveCycle)
        {
            if (_stompMoveCycle >= 0) _stompMoveYaw = ViewSmoothing.WrapPi(_stompMoveYaw + MathF.PI);   // back the way it came
            _stompMoveCycle = cycle;
            GD.Print($"t {_connectedFor:F2} s: stomp-move-test #{cycle}: {StompMoveTestMode}");
        }
        _yaw = _stompMoveYaw; _pitch = StompMovePitch;
        bool sprint = StompMoveTestMode!.StartsWith("s", StringComparison.OrdinalIgnoreCase);
        var buttons = phase < 0.12 && me.MeleeStep == 0 ? InputButtons.MeleeHeavy : InputButtons.None;
        bool moving = phase is >= 0.3 and < 2.6;
        if (moving && sprint) buttons |= InputButtons.Sprint;
        return new PlayerInput(tick, 0f, moving ? 1f : 0f, _stompMoveYaw, buttons, StompMovePitch);
    }

    /// <summary>Each frame of the test: one line while the own strike shows.</summary>
    void StompMoveTestFrame(PlayerState me)
    {
        if (!StompMoveTest || _config is null) return;
        if (me.MeleeStep > 0)
            GD.Print($"t {_connectedFor:F3} s (frame {Engine.GetFramesDrawn()}): stomp-move strike {(me.MeleeHeavy ? "heavy" : "light")} {me.MeleeStep} time {me.MeleeTime:F3} s, speed {MathF.Sqrt(me.Velocity.X * me.Velocity.X + me.Velocity.Y * me.Velocity.Y):F2} m/s, upper weight {_ownHandWeight:F2}");
        _ownHandWeight = 1f;
    }
}
