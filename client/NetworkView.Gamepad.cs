using System;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Controller in the match (owner's request 2026-10-30; spec "Controller (gamepad)", rules in <see cref="GamepadRules"/>, the device in <see cref="PadHost"/>). The original's Xbox 360 layout:
/// left stick moves (the magnitude is kept: a half-pushed stick walks), right stick looks (the data's speeds, mapping and damping of <c>camera_input_tuning</c>, per weapon and blended
/// with the aim camera), LT aim, RT fire, LB grenade, RB reload or vent, X strike, Y stun or alt strike (the stick forward at the press: the charge, the simulation's rule), B use, A evade or
/// jump pack, D-pad weapon swap, left stick click sprint (a toggle), Start the game menu, Back the scoreboard. The sampled input is the same <see cref="PlayerInput"/> the keyboard makes; the
/// server and the protocol are unchanged. The pad is the scheme while it is the last device used (<see cref="PadHost"/>); keys and the mouse take it back.
/// <c>--log-input</c> prints the scheme changes.
/// </summary>
public partial class NetworkView
{
    PadHost? _pad;
    int _padZoom;   // the scope zoom level R3 cycles to (reset when aim ends)
    bool _padInteractLatched;
    float _padHeldX, _padHeldY;   // seconds each look axis has been out of its dead zone (the data's request damping)

    /// <summary>From <c>_Ready</c>: the device node and its event hooks (a bot or a spectator window takes no pad).</summary>
    void PadSetup()
    {
        if (_bot || Observe || _demoPlayback is not null) return;
        _pad = PadHost.Ensure(GetTree().Root);
        _pad.ButtonChanged += OnPadButton;
        _pad.TriggerChanged += OnPadTrigger;
    }

    void PadTeardown()
    {
        if (_pad is null) return;
        _pad.ButtonChanged -= OnPadButton;
        _pad.TriggerChanged -= OnPadTrigger;
        _pad = null;
    }

    bool PadLive => _pad is not null && PadHost.Active && !OwnDead && !PickerBusy;

    void OnPadButton(JoyButton button, bool pressed)
    {
        if (!PadHost.Active) return;
        if (!PadLive) { if (!pressed) _latch.Release(InputButtons.Melee | InputButtons.MeleeHeavy | InputButtons.Jump | InputButtons.Grenade | InputButtons.Reload); return; }
        var held = button switch
        {
            JoyButton.X => InputButtons.Melee,
            JoyButton.Y => InputButtons.MeleeHeavy,
            JoyButton.A => InputButtons.Jump,
            JoyButton.LeftShoulder => InputButtons.Grenade,
            JoyButton.RightShoulder => InputButtons.Reload,
            _ => InputButtons.None,
        };
        if (held != InputButtons.None) { if (pressed) _latch.Press(held); else _latch.Release(held); return; }
        if (!pressed) return;
        switch (button)
        {
            case JoyButton.B: _padInteractLatched = true; break;
            case JoyButton.RightStick: if (_client?.Predicted.Aiming == true) _padZoom++; break;   // the zoom cycle (the levels of the scoped weapons)
            case JoyButton.DpadUp: PadSwap(PadControl.DpadUp); break;
            case JoyButton.DpadRight: PadSwap(PadControl.DpadRight); break;
            case JoyButton.DpadDown: PadSwap(PadControl.DpadDown); break;
        }
    }

    /// <summary>A D-pad press: one tick of the weapon's slot button (the simulation acts on the press edge).</summary>
    void PadSwap(PadControl dpad)
    {
        var button = GamepadRules.WeaponSwap(dpad);
        if (button == InputButtons.None) return;
        _latch.Press(button); _latch.Release(button);
    }

    void OnPadTrigger(int trigger, bool pressed)
    {
        if (!PadHost.Active) return;
        var button = trigger == 0 ? InputButtons.Aim : InputButtons.Fire;
        if (pressed && !PadLive) return;
        if (pressed) _latch.Press(button); else _latch.Release(button);
    }

    /// <summary>Start opens the game menu like Esc does (called from <see cref="LoadoutInput"/>'s event test).</summary>
    static bool IsMenuPress(InputEvent e) =>
        e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Escape } || e is InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.Start };

    /// <summary>Right stick turning for this frame (from <c>ProcessFrame</c>): the data's rates in degrees per second, like the mouse changes the view.</summary>
    void PadLook(double delta)
    {
        if (_client is not null && !_client.Predicted.Aiming) _padZoom = 0;
        if (_pad is null || !PadHost.Active || _client is null) { _padHeldX = _padHeldY = 0f; return; }
        var (rx, ry) = _pad.RightStick;
        float dt = (float)delta;
        _padHeldX = MathF.Abs(rx) > GamepadRules.LookDeadZone ? _padHeldX + dt : 0f;
        _padHeldY = MathF.Abs(ry) > GamepadRules.LookDeadZone ? _padHeldY + dt : 0f;
        if (_padHeldX == 0f && _padHeldY == 0f) return;
        InputTuning? ex = null, aim = null;
        if (CameraFor(_client.Predicted) is { } wc) { ex = wc.ExplorationInput; aim = wc.AimInput; }
        var t = _aimBlend >= 0.5f ? aim : ex;   // the set of the camera that is mostly in use
        float speedH = Lerp(ex?.SpeedHorizontal, aim?.SpeedHorizontal, _aimBlend, GamepadRules.FallbackHorizontal);
        float speedV = Lerp(ex?.SpeedVertical, aim?.SpeedVertical, _aimBlend, GamepadRules.FallbackVertical);
        float yawRate = GamepadRules.LookRate(rx, _padHeldX, speedH, t?.StickMapping, t?.StickDamping);
        float pitchRate = GamepadRules.LookRate(ry, _padHeldY, speedV, t?.StickMapping, t?.StickDamping);
        // Right is a turn to the right (yaw decreases); the stick's Y is down positive, like the mouse's: down lowers the pitch.
        _yaw -= Mathf.DegToRad(yawRate) * dt;
        _pitch = Math.Clamp(_pitch - Mathf.DegToRad(pitchRate) * dt, _pitchMin, _pitchMax);
    }

    static float Lerp(float? a, float? b, float w, float fallback)
    {
        float x = a is { } av && float.IsFinite(av) ? av : fallback, y = b is { } bv && float.IsFinite(bv) ? bv : x;
        return Mathf.Lerp(x, y, Math.Clamp(w, 0f, 1f));
    }

    /// <summary>This tick's input from the pad (the same <see cref="PlayerInput"/> the keyboard makes).</summary>
    PlayerInput SamplePad(uint tick)
    {
        var pad = _pad!;
        var (x, y) = GamepadRules.MoveStick(pad.LeftStick.X, pad.LeftStick.Y);
        var buttons = pad.Button(JoyButton.LeftStick) ? InputButtons.Sprint : InputButtons.None;   // a hold on the stick click (owner 2026-10-30)
        if (pad.RightTriggerDown) buttons |= InputButtons.Fire;
        if (pad.LeftTriggerDown) buttons |= InputButtons.Aim;
        if (pad.Button(JoyButton.X)) buttons |= InputButtons.Melee;
        if (pad.Button(JoyButton.Y)) buttons |= InputButtons.MeleeHeavy;
        if (pad.Button(JoyButton.RightShoulder)) buttons |= InputButtons.Reload;
        if (pad.Button(JoyButton.LeftShoulder)) buttons |= InputButtons.Grenade;
        if (pad.Button(JoyButton.A)) buttons |= InputButtons.Jump;
        bool interact = _padInteractLatched || pad.Button(JoyButton.B);
        _padInteractLatched = false;
        if (interact && !NoPickups) buttons |= InputButtons.Interact;
        buttons = LatchButtons(buttons);
        return Converged(new PlayerInput(tick, x, y, _yaw, buttons, _pitch));
    }

    /// <summary>Whether the scoreboard button (Back) is held on the pad.</summary>
    bool PadScoreboardHeld => !_bot && _pad is not null && PadHost.Active && _pad.Button(JoyButton.Back);
}
