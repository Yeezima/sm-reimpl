using System;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// <c>--brace-turn-test</c> (capture aid, install transitions batch 3; with <c>--bot</c>, a heavy bolter class and <c>--bot-human-camera</c>): the bot runs forward 1.5 s, braces with Reload as it stops, then holds
/// the aim 100 degrees left of where it braced for 4 s (past the brace's left threshold, 60), back ahead for 3 s, 100 degrees right for 4 s (past the right one, 77), ahead for 3 s, and
/// repeats; a <c>BRACE-TURN</c> line each 0.25 s gives the phase, the brace, the aim against the drawn body, the turn side and the turn layer. Ours.
/// <para>Follow-up (tester): the Reload press is retried every <see cref="BracePressRetrySeconds"/> while the stance has not started, at most <see cref="BracePressMaxTries"/> times
/// per attempt (a dropped or refused press left the bot unbraced for the whole run); a <c>BRACE-TURN brace not started</c> line names why when it gives up.</para>
/// <para><c>--brace-turn-extra</c>: a 24 s cycle with the mid-turn cases: the left turn, ahead, the right turn with the trigger held from 0.5 s to 2 s into it (fire mid-turn), ahead,
/// a 0.1 s flick to 100 degrees left and back (the damped aim: no turn expected), ahead, a left turn left by a Reload press 0.4 s into it (unbrace mid-turn; the exit's first frames),
/// unbraced until 21.4 s into the cycle and the brace again (the retried press). The log line's <c>phase</c> names each.</para>
/// <para><c>--brace-turn-cam [deg[,m[,h]]]</c>: a fixed world camera placed once, when the stance first starts, at <c>m</c> metres (default 5) from the bot and <c>h</c> (default 1.8) up, <c>deg</c>
/// (default 35) round from straight ahead of the brace's heading toward its left (90: the left side, -90: the right, 180: behind); it never moves after (the <c>--spectate cam:</c> exact pose), so
/// a <c>--write-movie</c> capture shows the body turning against the world. The usual camera until then.</para>
/// <para><c>--bot-hold</c> (any bot): the bot stands where it spawned, keeps its view and presses nothing (the other side's bots in a capture: <c>bots.cmd ... --extra "--bot-hold"</c>).</para>
/// </summary>
public partial class NetworkView
{
    static readonly bool BraceTurnTest = OS.GetCmdlineUserArgs().Contains("--brace-turn-test");
    static readonly bool BraceTurnExtra = OS.GetCmdlineUserArgs().Contains("--brace-turn-extra");
    static readonly bool BotStay = OS.GetCmdlineUserArgs().Contains("--bot-hold");
    /// <summary>Seconds between the aid's Reload presses while the stance has not started (ours).</summary>
    const double BracePressRetrySeconds = 0.4;
    /// <summary>Presses per attempt before the aid gives up and logs why (ours: 12 x 0.4 s, about 5 s).</summary>
    const int BracePressMaxTries = 12;
    double _braceTestStart = -1, _braceTestLogAt, _braceTestPressAt = double.NegativeInfinity;
    float _braceTestYaw;
    int _braceTestTries;
    bool _braceTestGaveUp, _braceTestCamPlaced;
    float _botStayYaw = float.NaN;

    /// <summary><c>--bot-hold</c>: no movement, no buttons, the view it spawned with (null when the flag is off).</summary>
    PlayerInput? BotStayInput(PlayerState me, uint tick)
    {
        if (!BotStay) return null;
        if (!me.Alive) { _botStayYaw = float.NaN; return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f); }
        if (float.IsNaN(_botStayYaw)) _botStayYaw = me.Yaw;
        return new PlayerInput(tick, 0f, 0f, _botStayYaw, InputButtons.None, 0f);
    }

    /// <summary>The aid's aim offset (degrees, positive left), trigger and unbrace press for <paramref name="phase"/> seconds into the cycle, and the phase's name.</summary>
    static (float Offset, bool Fire, bool Unbrace, bool AllowBrace, string Name) BraceTestPhase(double phase)
    {
        if (phase < 0.0) return (0f, false, false, true, "settle");
        if (!BraceTurnExtra)
            return phase switch { < 4.0 => (100f, false, false, true, "left"), < 7.0 => (0f, false, false, true, "ahead"), < 11.0 => (-100f, false, false, true, "right"), _ => (0f, false, false, true, "ahead") };
        return phase switch
        {
            < 4.0 => (100f, false, false, true, "left"),
            < 7.0 => (0f, false, false, true, "ahead"),
            < 11.0 => (-100f, phase is >= 7.5 and < 9.0, false, true, phase is >= 7.5 and < 9.0 ? "right+fire" : "right"),
            < 13.0 => (0f, false, false, true, "ahead"),
            < 13.1 => (100f, false, false, true, "flick"),
            < 16.0 => (0f, false, false, true, "after-flick"),
            < 16.4 => (100f, false, false, true, "left"),
            < 18.0 => (100f, false, true, false, "unbrace-mid-turn"),
            < 21.4 => (0f, false, false, false, "unbraced"),
            _ => (0f, false, false, true, "rebrace"),
        };
    }

    PlayerInput? BraceTurnTestInput(PlayerState me, uint tick)
    {
        if (!BraceTurnTest || !me.Alive) { _braceTestStart = -1; return null; }
        if (_braceTestStart < 0) { _braceTestStart = _connectedFor + 1.0; _braceTestYaw = _yaw; _braceTestTries = 0; _braceTestGaveUp = false; _braceTestPressAt = double.NegativeInfinity; }
        double t = _connectedFor - _braceTestStart;
        var buttons = InputButtons.None;
        // Review: it runs forward for 1.5 s first and presses Reload as it lets go of the stick, so the brace starts out of a run (the gait flags still set).
        float forward = t is >= 0.0 and < 1.5 ? 1f : 0f;
        double cycle = BraceTurnExtra ? 24.0 : 14.0;
        double phase = t < 4.5 ? -1.0 : (t - 4.5) % cycle;
        var (offset, fire, unbrace, allowBrace, name) = BraceTestPhase(phase);
        // The brace press from 1.2 s (still running: the stance stops the body), retried while the stance has not started (a press dropped or refused), and again whenever it was left.
        bool pressedThisTick = false;
        if (me.Brace != BracePhase.None) { _braceTestTries = 0; _braceTestGaveUp = false; }
        else if (t >= 1.2 && allowBrace && !me.ReloadHeld && !_braceTestGaveUp && _connectedFor - _braceTestPressAt >= BracePressRetrySeconds)
        {
            if (_braceTestTries >= BracePressMaxTries)
            {
                _braceTestGaveUp = true;
                var held = _config is null ? null : Loadouts.InHand(_config, me);
                GD.Print($"t {_connectedFor:F2} s: BRACE-TURN brace not started after {_braceTestTries} presses: in hand {held?.Name ?? "nothing"}, brace {(held?.Brace is null ? "none (not a bracing weapon)" : "yes")}, " +
                         $"can start {Brace.CanStart(me)}, grounded {me.Grounded}, jump {me.Jump}, reaction {me.Reaction}, switching {me.SwitchLeft > 0f}");
            }
            else { buttons |= InputButtons.Reload; pressedThisTick = true; _braceTestTries++; _braceTestPressAt = _connectedFor; }
        }
        // --brace-turn-extra: the unbrace press (from the phase's start while still braced, retried like the brace press) and the trigger.
        if (unbrace && me.Brace == BracePhase.Braced && !me.ReloadHeld && _connectedFor - _braceTestPressAt >= BracePressRetrySeconds) { buttons |= InputButtons.Reload; pressedThisTick = true; _braceTestPressAt = _connectedFor; }
        if (fire) buttons |= InputButtons.Fire;
        _yaw = ViewSmoothing.WrapPi(_braceTestYaw + offset * MathF.PI / 180f); _pitch = 0f;
        if (me.Brace != BracePhase.None) BraceTestPlaceCamera(me);
        if (_connectedFor - _braceTestLogAt >= 0.25 || pressedThisTick)
        {
            _braceTestLogAt = _connectedFor;
            var loco = LocoOf(me.PlayerId);
            float twist = loco.Body.Valid ? loco.Body.Twist(DisplayYaw(me)) * 180f / MathF.PI : float.NaN;
            GD.Print($"t {_connectedFor:F2} s: BRACE-TURN phase {name} offset {offset:F0} brace {me.Brace} presses {_braceTestTries}{(pressedThisTick ? " (Reload now)" : "")} fire {(fire ? "held" : "off")} aim-vs-body {twist:F1} deg side {loco.Body.Side} layer {loco.Body.LayerSide} {loco.Body.LayerWeight:F2} turns {loco.Body.TurnsStarted} stand {loco.Stand?.Name ?? "none"} gait {(loco.GaitMoving || loco.HipGaitMoving ? "set" : "clear")}");
        }
        return new PlayerInput(tick, forward, 0f, _yaw, buttons, _pitch);
    }

    /// <summary><c>--brace-turn-cam</c>: places the fixed world camera once, the first frame the stance has started (see the class summary).</summary>
    void BraceTestPlaceCamera(PlayerState me)
    {
        if (_braceTestCamPlaced || !CmdFlag("brace-turn-cam") || _gameSpace is null) return;
        _braceTestCamPlaced = true;
        var f = (CmdArg("brace-turn-cam") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        float Num(int i, float fallback) => i < f.Length && float.TryParse(f[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : fallback;
        float deg = Num(0, 35f), dist = Num(1, 5f), height = Num(2, 1.8f);
        float a = _braceTestYaw + deg * MathF.PI / 180f;   // the game's yaw grows counter-clockwise (toward the left)
        var p = me.Position;
        var eye = new System.Numerics.Vector3(p.X - MathF.Sin(a) * dist, p.Y + MathF.Cos(a) * dist, p.Z + height);
        var target = new System.Numerics.Vector3(p.X, p.Y, p.Z + 1.2f);
        // The --spectate "cam:" exact pose (NetworkView.Spectate.cs): no springs, cast or push-out.
        _spectateMode = "cam:brace-turn"; _spectateFixedParsed = true; _spectateFixed = (SpectateG(eye), SpectateG(target));
        GD.Print($"BRACE-TURN camera fixed at {eye.X:F2},{eye.Y:F2},{eye.Z:F2} looking at {target.X:F2},{target.Y:F2},{target.Z:F2} ({deg:F0} deg from ahead, {dist:F1} m)");
    }

    /// <summary>The <c>--brace-turn-cam</c> view once placed (from <see cref="SpectateCamera"/>, which otherwise needs <c>--spectate</c>).</summary>
    (Vector3 Eye, Vector3 Target)? BraceTestCameraView() => BraceTurnTest && _braceTestCamPlaced ? _spectateFixed : null;
}
