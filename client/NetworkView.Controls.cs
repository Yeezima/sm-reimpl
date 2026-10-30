using System;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Responsive controls (feel/controls, the owner's request of 2026-10-29: "responsiveness of controls, smoothness of visuals"). Client
/// presentation and input handling only, all ours; the simulation, the data-driven timings (raise delay, melee clips, reload times) and the
/// protocol are unchanged. Measured with <c>--latency-probe</c> (NetworkView.LatencyProbe.cs; spec "Responsive controls (feel/controls)").
/// <list type="bullet">
/// <item><b>Taps are never lost</b> (<see cref="InputLatch"/>): the game buttons were read held once per 30 Hz tick, so a click or key tap that
/// went down and up between two samples (shorter than up to 33 ms) did nothing. Every press and release event is noted and folded into the next
/// sample. <c>--no-tap-latch</c> restores the held-only reading.</item>
/// <item><b>Early tick</b> (<see cref="EarlyTick"/>): a press or release of a game button or a movement key runs the next client tick in the
/// frame the event arrives instead of at its scheduled time (up to 33 ms later): the shot, its muzzle flash and sound, the melee, roll, jump,
/// throw, reload, switch, aim and the start or stop of a run begin in that frame. The tick keeps its number and the schedule is kept, so the server
/// and the prediction agree as before. The drawn own position and the camera stay on the schedule's timeline (GameClient.DrawPair: no step,
/// no stall); the run's legs follow the new velocity at once.
/// <c>--no-early-tick</c> turns it off.</item>
/// <item><b>Fresh aim</b> (<see cref="FreshAim"/>): the converged shot's ray was read from the camera as the previous frame placed it, while the
/// input's yaw and pitch already carried this frame's mouse movement: shots left one frame of mouse movement behind the crosshair. The ray is
/// turned by the view's change since. <c>--no-fresh-aim</c> turns it off.</item>
/// <item><b>Own clips at the frame rate</b> (<see cref="OwnClipClock"/>): the own melee attack, stun strike, throw, reaction and charge
/// clips were posed at the predicted timers, which change once per tick (each pose shown for two frames at 60 fps, four or five at 144 fps).
/// They are drawn a tick back plus the fraction elapsed, like the own position (M36). <c>--no-own-clip-smoothing</c> turns it off.</item>
/// </list>
/// <item><b>Camera wall casts from the drawn head</b>: the camera is placed from the drawn position, which after an early tick can be two ticks
/// behind the simulation's; its wall casts start there too (the simulation's head when the way between the two is blocked).
/// <c>--sim-camera-cast</c> restores the M36 cast from the simulation's head.</item>
/// <c>--old-controls</c> turns all five off (before / after comparisons).
/// </summary>
public partial class NetworkView
{
    static readonly string[] ControlArgs = OS.GetCmdlineUserArgs();
    static readonly bool OldControls = ControlArgs.Contains("--old-controls");
    /// <summary><c>--no-tap-latch</c> (test aid): buttons are read held at each tick's sample, as before feel/controls.</summary>
    static readonly bool NoTapLatch = OldControls || ControlArgs.Contains("--no-tap-latch");
    /// <summary><c>--no-early-tick</c> (test aid): input edges wait for the next scheduled tick, as before feel/controls.</summary>
    static readonly bool NoEarlyTick = OldControls || ControlArgs.Contains("--no-early-tick");
    /// <summary><c>--no-fresh-aim</c> (test aid): the converged shot uses the camera as the previous frame placed it, as before feel/controls.</summary>
    static readonly bool NoFreshAim = OldControls || ControlArgs.Contains("--no-fresh-aim");
    /// <summary><c>--no-own-clip-smoothing</c> (test aid): the own clips are posed at the predicted (tick-stepped) timers, as before feel/controls.</summary>
    static readonly bool NoOwnClipSmoothing = OldControls || ControlArgs.Contains("--no-own-clip-smoothing");
    /// <summary><c>--sim-camera-cast</c> (test aid): the follow camera's wall casts start from the simulation's head (M36) instead of the drawn one.</summary>
    static readonly bool NoDrawnCameraCast = OldControls || ControlArgs.Contains("--sim-camera-cast");

    /// <summary>The buttons the tap latch handles (Interact and the wheel switch have latches of their own).</summary>
    const InputButtons LatchedButtons = InputButtons.Fire | InputButtons.Melee | InputButtons.MeleeHeavy | InputButtons.Reload | InputButtons.Grenade
        | InputButtons.Switch | InputButtons.Jump | InputButtons.SlotPistol | InputButtons.SlotPrimary | InputButtons.SlotSecond | InputButtons.Aim | InputButtons.Sprint;

    readonly InputLatch _latch = new();

    /// <summary>The game buttons that need the mouse captured act only then (a click on a released mouse recaptures it); the latency probe counts as captured (headless).</summary>
    bool MouseCaptured => Input.MouseMode == Input.MouseModeEnum.Captured || LatencyProbeOn;

    /// <summary>From <c>_UnhandledInput</c> (human players): notes presses and releases of the game buttons and movement keys for the latch and the early tick.</summary>
    void ControlEvent(InputEvent e)
    {
        if (NoTapLatch && NoEarlyTick) return;
        if (OwnDead || PickerBusy) { _latch.Reset(); return; }
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } left:
                if (left.Pressed) { if (MouseCaptured) _latch.Press(InputButtons.Fire); }
                else _latch.Release(InputButtons.Fire);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } right:
                if (right.Pressed) { if (MouseCaptured) _latch.Press(InputButtons.Melee); }
                else if (!Input.IsPhysicalKeyPressed(Key.V)) _latch.Release(InputButtons.Melee);   // V is the same button
                break;
            case InputEventKey { Echo: false } k:
                var key = k.PhysicalKeycode;
                if (key is Key.W or Key.A or Key.S or Key.D) { _latch.MoveChanged(); break; }
                var (button, needsCapture) = key switch
                {
                    Key.V => (InputButtons.Melee, true),
                    Key.F => (InputButtons.MeleeHeavy, true),
                    Key.R => (InputButtons.Reload, false),
                    Key.G => (InputButtons.Grenade, true),
                    Key.X => (InputButtons.Switch, true),
                    Key.Space => (InputButtons.Jump, false),
                    Key.Key1 => (InputButtons.SlotPistol, false),
                    Key.Key2 => (InputButtons.SlotPrimary, false),
                    Key.Key3 => (InputButtons.SlotSecond, false),
                    Key.Ctrl => (InputButtons.Aim, true),
                    Key.Shift => (InputButtons.Sprint, false),
                    _ => (InputButtons.None, false),
                };
                if (button == InputButtons.None) break;
                if (k.Pressed) { if (!needsCapture || MouseCaptured) _latch.Press(button); }
                else if (button != InputButtons.Melee || !Input.IsMouseButtonPressed(MouseButton.Right)) _latch.Release(button);
                break;
        }
    }

    /// <summary>The sampled buttons with the latch's taps (the latch is always sampled, so its events never pile up).</summary>
    InputButtons LatchButtons(InputButtons held)
    {
        var latched = _latch.Sample(held & LatchedButtons);
        return NoTapLatch ? held : (held & ~LatchedButtons) | latched;
    }

    /// <summary>Whether this frame's client update should run the next tick now (an input edge since the last sample; human players only).</summary>
    bool EarlyTickWanted => !NoEarlyTick && !_bot && _demoPlayback is null && !OwnDead && !PickerBusy && _latch.Pending;

    // ---- fresh aim ----
    float _cameraPlacedYaw, _cameraPlacedPitch;
    System.Numerics.Vector3 _cameraPlacedFeet;
    bool _cameraPlacedValid;

    /// <summary>From <see cref="RigCamera"/>: the view the camera was placed with this frame.</summary>
    void NoteCameraPlaced(float yaw, float pitch, System.Numerics.Vector3 feet)
    {
        _cameraPlacedYaw = yaw; _cameraPlacedPitch = pitch; _cameraPlacedFeet = feet; _cameraPlacedValid = true;
    }

    /// <summary>The screen-centre ray (game space) as it will be when the camera is placed at the view of this moment; null: the camera's own (no rig camera, or <c>--no-fresh-aim</c>).</summary>
    (System.Numerics.Vector3 Origin, System.Numerics.Vector3 Direction)? FreshCenterRay()
    {
        if (NoFreshAim || !_cameraPlacedValid || _camera is null || _gameSpace is null || !RigCameraWanted) return null;
        var inverse = _gameSpace.Transform.AffineInverse();
        var o3 = inverse * _camera.GlobalPosition;
        var d3 = (inverse.Basis * (-_camera.GlobalBasis.Z)).Normalized();
        float dyaw = ViewSmoothing.WrapPi(_yaw - _cameraPlacedYaw);
        float dpitch = Math.Clamp(_pitch, _pitchMin, _pitchMax) - _cameraPlacedPitch;
        var (o, d) = FreshAim.Turn(new System.Numerics.Vector3(o3.X, o3.Y, o3.Z), new System.Numerics.Vector3(d3.X, d3.Y, d3.Z), _cameraPlacedFeet, dyaw, dpitch, MaxPitch);
        return (o, d);
    }

    // ---- own clips at the frame rate ----
    readonly OwnClipClock _ownClip = new(), _ownThrow = new();   // the reload clip already runs on its own real-time clock (M35, ReloadPlayer)

    /// <summary>The own clip (reaction, charge, attack) to pose this frame at the frame rate, or null (none, dead, or <c>--no-own-clip-smoothing</c>).</summary>
    SmoothClip? SmoothOwnClip(PlayerState me)
    {
        if (_client is null || _config is null || !me.Alive || OwnDead) { _ownClip.Stop(); return null; }
        var (kind, key, target, length) = ClipTargetOf(me);
        if (kind == ClipKind.None) { _ownClip.Stop(); return null; }
        if (NoOwnClipSmoothing) { if (kind == ClipKind.Attack) _ownClipShown = target; _ownClip.Stop(); return null; }
        float shown = _ownClip.Step(key, target, _client.ClipFraction, _config.TickSeconds, length);
        if (kind == ClipKind.Attack) _ownClipShown = shown;   // --latency-probe: the drawn attack time
        return new SmoothClip(kind, shown);
    }

    /// <summary>The own throw's clip time this frame (null: not throwing).</summary>
    float? OwnThrowTime(PlayerState me)
    {
        if (!me.Alive || !me.Throwing) { _ownThrow.Stop(); return null; }
        if (NoOwnClipSmoothing || _client is null || _config is null) return me.ThrowTime;
        return _ownThrow.Step(1, me.ThrowTime, _client.ClipFraction, _config.TickSeconds);
    }
}
