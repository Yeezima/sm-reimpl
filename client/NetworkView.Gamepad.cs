using System;
using System.Linq;
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
    int _padInteractSamples;      // test aid (--pad-test): the samples that asked for a Use
    bool _padUseBlocked;          // the B that closed a menu (or was pressed under one) is not a "use" until it is let go
    ulong _padMenuClosedFrame = ulong.MaxValue;   // the frame the Esc menu closed (a B press in that same frame closed it)
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

    void OnPadButton(JoyButton button, bool pressed) => PadInputChanged(ControlsInput.PadCode(button), pressed);

    /// <summary>The held-type buttons a pad press feeds into the tap latch (Sprint is read from the sample only).</summary>
    const InputButtons PadHoldButtons = InputButtons.Fire | InputButtons.Aim | InputButtons.Melee | InputButtons.MeleeHeavy | InputButtons.Jump | InputButtons.Grenade | InputButtons.Reload;

    /// <summary>
    /// A pad button or trigger changed (ui/keybinds: the actions come from the player's controller bindings, the SM1 layout by default): the held-type buttons go through the tap latch,
    /// the weapon slots and the switch tap for one tick, Use latches an interact, the zoom action cycles the scope level.
    /// </summary>
    void PadInputChanged(string code, bool pressed)
    {
        if (!PadHost.Active) return;
        var actions = ControlsInput.PadActions(code);
        // B closes a menu (PadHost.IsBack): the press that did it, or that came while a menu or the death screen had the game, never acts as Use, nor does its hold after the menu is gone.
        if (actions.Contains(GameAction.Use))
        {
            if (pressed && (!PadLive || Engine.GetProcessFrames() == _padMenuClosedFrame)) { _padUseBlocked = true; _padInteractLatched = false; }
            else if (!pressed) _padUseBlocked = false;
        }
        if (!PadLive)
        {
            if (!pressed) foreach (var a in actions) if ((ControlActions.Info(a).Buttons & PadHoldButtons) != 0) _latch.Release(ControlActions.Info(a).Buttons);
            return;
        }
        foreach (var action in actions)
        {
            var button = ControlActions.Info(action).Buttons;
            if ((button & PadHoldButtons) != 0) { if (pressed) _latch.Press(button); else _latch.Release(button); continue; }
            if (!pressed) continue;
            switch (action)
            {
                case GameAction.Use: if (!_padUseBlocked) _padInteractLatched = true; break;
                case GameAction.ZoomCycle: if (_client?.Predicted.Aiming == true && !_tfwPadOrbital) _padZoom++; break;   // the zoom cycle (the levels of the scoped weapons)
                case GameAction.Weapon1 or GameAction.Weapon2 or GameAction.Weapon3 or GameAction.NextWeapon: _latch.Press(button); _latch.Release(button); break;   // one tick (the simulation acts on the press edge)
            }
        }
    }

    /// <summary>A menu (the Esc menu, or its loading request) went away this frame: a pad press of this frame was the one that closed it, so it is not a Use.</summary>
    void PadMenuClosed()
    {
        _padMenuClosedFrame = Engine.GetProcessFrames();
        if (_pad is not null && ControlsInput.PadHeld(_pad, GameAction.Use)) _padUseBlocked = true;
        _padInteractLatched = false;
    }

    void OnPadTrigger(int trigger, bool pressed) => PadInputChanged(ControlsInput.TriggerCode(trigger), pressed);

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
        float lookFactor = SmClient.Customizer.Model.LiveOptions.PadLookAim(_client.Predicted.Aiming);   // the ADS factor applies while aiming down sights
        float yawRate = GamepadRules.LookRate(rx, _padHeldX, speedH, t?.StickMapping, t?.StickDamping) * lookFactor;
        float pitchRate = GamepadRules.LookRate(ry, _padHeldY, speedV, t?.StickMapping, t?.StickDamping) * lookFactor * SmClient.Customizer.Model.LiveOptions.PadVertical;
        // Right is a turn to the right (yaw decreases); the stick's Y is down positive, like the mouse's: down lowers the pitch.
        _yaw -= Mathf.DegToRad(yawRate) * dt;
        _pitch = Math.Clamp(_pitch - Mathf.DegToRad(pitchRate) * dt, _pitchMin, _pitchMax);
    }

    static float Lerp(float? a, float? b, float w, float fallback)
    {
        float x = a is { } av && float.IsFinite(av) ? av : fallback, y = b is { } bv && float.IsFinite(bv) ? bv : x;
        return Mathf.Lerp(x, y, Math.Clamp(w, 0f, 1f));
    }

    /// <summary>This tick's input from the pad (the same <see cref="PlayerInput"/> the keyboard makes, from the controller bindings).</summary>
    PlayerInput SamplePad(uint tick)
    {
        var pad = _pad!;
        var (x, y) = GamepadRules.MoveStick(pad.LeftStick.X, pad.LeftStick.Y);
        // The bound actions as buttons (sprint is a hold on the stick click by default: owner 2026-10-30); the sprint, charge and strikes read the same bits as the keyboard's.
        var buttons = BindingSet.Sample(a => ControlsInput.PadHeld(pad, a), captured: true, includeMove: false).Buttons;
        if (_padUseBlocked && !ControlsInput.PadHeld(pad, GameAction.Use)) _padUseBlocked = false;
        bool interact = !_padUseBlocked && (_padInteractLatched || ControlsInput.PadHeld(pad, GameAction.Use));
        _padInteractLatched = false;
        if (interact) _padInteractSamples++;
        if (interact && !NoPickups) buttons |= InputButtons.Interact;
        buttons = LatchButtons(buttons);
        return Converged(new PlayerInput(tick, x, y, _yaw, buttons, _pitch));
    }

    /// <summary>Whether the scoreboard button (Back) is held on the pad.</summary>
    bool PadScoreboardHeld => !_bot && _pad is not null && PadHost.Active && ControlsInput.PadHeld(_pad, GameAction.Scoreboard);
}
