using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// <c>--bot-turn-sweep</c> (M40-T5 test aid, with <c>--bot --bot-gait</c>): a bot script for the turn-in-place, aim-offset idle and start / stop work, repeatable
/// on a recorded demo (<c>--record-demo</c>, then <c>--play-demo ... --headless --fixed-fps 60 --anim-probe</c>). The view is driven per rendered frame (a human's
/// mouse), the stick and buttons per input tick. One cycle (about 110 s):
/// <list type="number">
/// <item><b>pitch</b>: standing, the view pitch sweeps 0 to +60 to -60 to 0 degrees (the aim-offset idle).</item>
/// <item><b>slow pan</b>: standing, the view turns right at 45 degrees/s for 4 s and left at 45 degrees/s for 4 s (the twist builds up to the turn threshold).</item>
/// <item><b>pan 90</b>: standing, 90 degrees/s right for 4 s (a full circle), then left for 4 s.</item>
/// <item><b>steps</b>: standing, the view jumps by 45, 90, 135, 180, -90, -180 degrees in one frame, 2.2 s between (a mouse flick; the body has to catch up).</item>
/// <item><b>flick</b>: standing, 720 degrees/s for 0.5 s each way.</item>
/// <item><b>run-stop</b>: the view returns to the home heading, then a run of 1.2 s then 2.2 s standing, three times; a sprint of 1.6 s then 2.5 s standing, twice; a run with the view panning 90 degrees/s then standing; a strafe and a back-pedal then standing; each run is followed by the way back home.</item>
/// </list>
/// Sections are logged (<c>TURNSWEEP</c>). Fixed for a given build, so a before / after pair of recordings compares like with like.
/// </summary>
public partial class NetworkView
{
    /// <summary>True when <c>--bot-turn-sweep</c> was given.</summary>
    static readonly bool TurnSweepOn = OS.GetCmdlineUserArgs().Contains("--bot-turn-sweep");

    /// <summary><c>--turn-sweep-fire</c> (with <c>--bot-turn-sweep</c>): the bot taps the trigger every 8 s so the ready posture shows instead of the relaxed one.</summary>
    static readonly bool TurnSweepFire = OS.GetCmdlineUserArgs().Contains("--turn-sweep-fire");

    /// <summary>One segment: a name (logged), seconds, view yaw rate (degrees/s, + left like the simulation's yaw), an instant yaw jump at the start (degrees), the pitch target (degrees; reached at <see cref="TurnPitchRate"/>), the stick and buttons ("home" runs the way back).</summary>
    sealed record TurnSeg(string Name, float Seconds, float YawRate = 0f, float YawJump = 0f, float Pitch = 0f, float StickX = 0f, float StickY = 0f, InputButtons Buttons = InputButtons.None, bool Home = false, bool ToHome = false);

    const float TurnPitchRate = 40f;   // degrees per second

    static readonly TurnSeg[] TurnScript = BuildTurnScript();

    static TurnSeg[] BuildTurnScript()
    {
        var s = new List<TurnSeg>();
        s.Add(new("stand", 3f));
        // 1. pitch
        s.Add(new("pitch up", 1.6f, Pitch: 60f)); s.Add(new("pitch hold", 0.8f, Pitch: 60f));
        s.Add(new("pitch down", 3.2f, Pitch: -60f)); s.Add(new("pitch hold", 0.8f, Pitch: -60f));
        s.Add(new("pitch level", 1.6f, Pitch: 0f)); s.Add(new("stand", 1f));
        // 2. slow pan
        s.Add(new("slow pan right", 4f, YawRate: -45f)); s.Add(new("hold", 1.5f));
        s.Add(new("slow pan left", 4f, YawRate: 45f)); s.Add(new("hold", 1.5f));
        // 3. 90 degrees/s
        s.Add(new("pan 90 right", 4f, YawRate: -90f)); s.Add(new("hold", 1.5f));
        s.Add(new("pan 90 left", 4f, YawRate: 90f)); s.Add(new("hold", 1.5f));
        // 4. steps
        foreach (float jump in new[] { 45f, 90f, 135f, 180f, -90f, -180f }) s.Add(new($"step {jump:F0}", 2.2f, YawJump: jump));
        // 5. flick
        s.Add(new("flick right", 0.5f, YawRate: -720f)); s.Add(new("hold", 1.8f));
        s.Add(new("flick left", 0.5f, YawRate: 720f)); s.Add(new("hold", 1.8f));
        // 6. run / stop: the view comes back to the home heading first (a run starts from a body that faces the run: a human does not flick the view as the stick goes forward)
        s.Add(new("face home", 2f, ToHome: true));
        for (int i = 0; i < 3; i++) { s.Add(new("run", 1.2f, StickY: 1f)); s.Add(new("stop after run", 2.2f)); s.Add(new("home", 4f, Home: true)); s.Add(new("stop at home", 1.2f)); }
        for (int i = 0; i < 2; i++) { s.Add(new("sprint", 1.6f, StickY: 1f, Buttons: InputButtons.Sprint)); s.Add(new("stop after sprint", 2.5f)); s.Add(new("home", 5f, Home: true)); s.Add(new("stop at home", 1.2f)); }
        s.Add(new("run while panning", 2f, YawRate: 90f, StickY: 1f)); s.Add(new("stop after panning run", 2.4f)); s.Add(new("home", 5f, Home: true)); s.Add(new("stop at home", 1.2f));
        s.Add(new("strafe right", 1.2f, StickX: 1f)); s.Add(new("stop after strafe", 2.2f)); s.Add(new("home", 4f, Home: true)); s.Add(new("stop at home", 1.2f));
        s.Add(new("back-pedal", 1.0f, StickY: -1f)); s.Add(new("stop after back-pedal", 2.2f)); s.Add(new("home", 4f, Home: true)); s.Add(new("stop at home", 1.2f));
        return s.ToArray();
    }

    static readonly float TurnScriptSeconds = TurnScript.Sum(x => x.Seconds);

    float? _turnHomeYaw;
    double _turnClock;
    int _turnSeg = -1;
    float _turnSegYawBase;
    bool _turnSegStarted;
    double _turnSegAt;

    /// <summary>Which segment the script is in at <paramref name="clock"/> seconds, and the seconds into it.</summary>
    static (int Index, double In) TurnSegAt(double clock)
    {
        double t = clock % TurnScriptSeconds;
        for (int i = 0; i < TurnScript.Length; i++)
        {
            if (t < TurnScript[i].Seconds) return (i, t);
            t -= TurnScript[i].Seconds;
        }
        return (TurnScript.Length - 1, TurnScript[^1].Seconds);
    }

    /// <summary>Per rendered frame (own player): drives the bot's view through the script.</summary>
    void TurnSweepView(double delta)
    {
        if (!TurnSweepOn || !_bot || _client is null || !_client.Predicted.Alive) return;
        _turnHomeYaw ??= _yaw;
        _turnClock += delta;
        var (index, into) = TurnSegAt(_turnClock);
        var seg = TurnScript[index];
        if (index != _turnSeg)
        {
            _turnSeg = index; _turnSegAt = _turnClock;
            GD.Print($"TURNSWEEP t {_turnClock:F2} s: {seg.Name} (segment {index}, cycle {(int)(_turnClock / TurnScriptSeconds)})");
            if (seg.YawJump != 0f) _yaw += seg.YawJump * MathF.PI / 180f;
            if (seg.ToHome) _yaw = _turnHomeYaw.Value;
            // The first segment of a cycle: back to the home heading (the run segments go along the view).
            if (index == 0) _yaw = _turnHomeYaw.Value;
        }
        _yaw += seg.YawRate * MathF.PI / 180f * (float)delta;
        _yaw = MathF.IEEERemainder(_yaw, 2 * MathF.PI);
        float target = seg.Pitch * MathF.PI / 180f, step = TurnPitchRate * MathF.PI / 180f * (float)delta;
        _pitch = Math.Clamp(_pitch + Math.Clamp(target - _pitch, -step, step), _pitchMin, _pitchMax);
        // Runs go along the home heading: the view comes back to it for every run segment.
        if (seg.StickX != 0f || seg.StickY != 0f || seg.Home)
        {
            float home = _turnHomeYaw.Value;
            if (seg.YawRate == 0f) _yaw = home;
        }
    }

    PlayerInput BotTurnSweepInput(uint tick)
    {
        var me = _client!.Predicted;
        if (_gaitHome is null && me.Alive)
        {
            _gaitHome = new System.Numerics.Vector2(me.Position.X, me.Position.Y);
            GD.Print($"turn sweep home {me.Position.X:F2},{me.Position.Y:F2} facing yaw {me.Yaw:F2}");
        }
        if (_turnSeg < 0) return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch);
        var seg = TurnScript[_turnSeg];
        float x = seg.StickX, y = seg.StickY;
        if (seg.Home && _gaitHome is { } home)
        {
            var offset = home - new System.Numerics.Vector2(me.Position.X, me.Position.Y);
            if (offset.Length() > 0.35f)
            {
                // The way back in the view's frame (x right, y forward).
                float fx = -MathF.Sin(_yaw), fy = MathF.Cos(_yaw);
                var dir = offset / offset.Length();
                float stickY = dir.X * fx + dir.Y * fy, stickX = dir.X * fy - dir.Y * fx;
                float scale = Math.Min(1f, offset.Length() / 1.5f + 0.35f);   // slows into the home point so the stop is a stop
                x = stickX * scale; y = stickY * scale;
            }
        }
        var buttons = seg.Buttons;
        // --turn-sweep-fire: a 0.1 s tap of the trigger every 8 s outside the sprints keeps the weapon up (the ready posture, M27; it stays up 10 s after a shot).
        if (TurnSweepFire && (buttons & InputButtons.Sprint) == 0 && tick * (double)_config!.TickSeconds % 8.0 < 0.1) buttons |= InputButtons.Fire;
        if (BotSlot is { Length: > 0 } && _connectedFor is >= 3.0 and < 3.2)
            buttons |= BotSlot.StartsWith("pi", StringComparison.OrdinalIgnoreCase) ? InputButtons.SlotPistol : InputButtons.SlotPrimary;
        return new PlayerInput(tick, x, y, _yaw, buttons, _pitch);
    }
}
