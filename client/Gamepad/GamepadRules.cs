using System;
using System.Collections.Generic;
using SmSim;

namespace SmClient;

/// <summary>Where a controller binding comes from (spec "Controller (gamepad)": kind column).</summary>
public enum PadSource
{
    /// <summary>The original's own data: the install's locale strings and tuning files.</summary>
    Data,
    /// <summary>The owner's screenshot of the original's Xbox 360 binding screen (2026-10-30); the data holds the same labels in the locale but not which button.</summary>
    OwnerScreenshot,
    /// <summary>The owner's recollection of the original (2026-10-30).</summary>
    OwnerRecollection,
    /// <summary>Ours: nothing in the data or the owner's words; a provisional default to check in a playtest.</summary>
    Estimate,
}

/// <summary>The Xbox 360 pad's controls, in the original's naming (the binding screen's).</summary>
public enum PadControl { LeftStick, RightStick, LeftTrigger, RightTrigger, LeftBumper, RightBumper, A, B, X, Y, DpadUp, DpadRight, DpadDown, DpadLeft, LeftStickClick, RightStickClick, Start, Back }

/// <summary>One row of the controller binding table.</summary>
public sealed record PadBinding(string Action, PadControl Control, PadSource Source, string Note);

/// <summary>
/// Controller feature (owner's request 2026-10-30): the original's Xbox 360 layout, the stick curves, and the device-switch rule. Pure code (no Godot), so the unit tests
/// run it directly (tests/SmNet.Tests/GamepadTests.cs includes this file). The Godot side is <c>PadHost</c> (the device, menus) and <c>NetworkView.Gamepad.cs</c> (the match).
/// </summary>
public static class GamepadRules
{
    /// <summary>The binding table: the action, the control, the source of the row and where it comes from.</summary>
    public static readonly IReadOnlyList<PadBinding> Bindings = new PadBinding[]
    {
        new("Move", PadControl.LeftStick, PadSource.OwnerScreenshot, "binding screen: left stick; locale 110651 'MOVEMENT'; locale 122127 '%THUMBLY_UP% to Navigate'"),
        new("Look / aim camera", PadControl.RightStick, PadSource.OwnerScreenshot, "binding screen: right stick; locale 110665 'Look / Turn'; speeds and curves from camera_input_tuning (data)"),
        new("Aim", PadControl.LeftTrigger, PadSource.OwnerScreenshot, "binding screen 'Aim'; locale 110660; tutorial strings use %AIM_MODE%"),
        new("Fire", PadControl.RightTrigger, PadSource.OwnerScreenshot, "binding screen 'Fire'; locale 110657; %ABILITY_RANGE%"),
        new("Grenade (equipment)", PadControl.LeftBumper, PadSource.OwnerScreenshot, "binding screen 'Grenade'; locale 110661; %ABILITY_USEEQUIPMENT%"),
        new("Reload / Vent", PadControl.RightBumper, PadSource.OwnerScreenshot, "binding screen 'Reload / Vent'; locale 110659; %ABILITY_RELOAD%"),
        new("Strike (light melee)", PadControl.X, PadSource.OwnerScreenshot, "binding screen 'Strike' (blue); locale 110662; %ABILITY_MELEE%"),
        new("Stun / Alt Strike (heavy melee; with the stick forward: the charge)", PadControl.Y, PadSource.OwnerScreenshot, "binding screen (yellow); locale 110692; %ABILITY_MELEE_HEAVY%; locale 110691 'Charge Attack (while Sprinting)'; the charge rule is the simulation's (stick forward at the press)"),
        new("Use / Execute (pickups)", PadControl.B, PadSource.OwnerScreenshot, "binding screen (red); locale 110664"),
        new("Evade / Jump Pack", PadControl.A, PadSource.OwnerScreenshot, "binding screen (green); locale 110693; %ABILITY_CONTEXTMOTION% 'tap to perform a combat roll'"),
        new("Weapon swap: primary", PadControl.DpadUp, PadSource.Estimate, "the original has four weapon slots on the D-pad (locale 120990: %ABILITY_SET_RANGED_FIRST%..%ABILITY_SET_RANGED_FOURTH%) and 'Weapon Swap' on the binding screen; which direction holds which weapon is not in the data read"),
        new("Weapon swap: next weapon (toggle / cycle)", PadControl.DpadRight, PadSource.Estimate, "as above"),
        new("Weapon swap: second primary (Weapon Versatility)", PadControl.DpadDown, PadSource.Estimate, "the south slot of the weapon menu (lead's brief, 2026-10-30)"),
        new("Weapon swap: pistol", PadControl.DpadLeft, PadSource.Estimate, "as above"),
        new("Sprint (press to toggle; ends when the stick is released)", PadControl.LeftStickClick, PadSource.OwnerRecollection, "locale 122101 'Movement (Press to Sprint)'; owner 2026-10-30; the toggle is ours"),
        new("Scope zoom cycle (not in the simulation yet)", PadControl.RightStickClick, PadSource.Data, "locale 122097 'Camera (Press - Lascannon Zoom)'; locale 105867 and 111535 say [L3] (a conflict in the data: the tutorial strings read like the older layout); the level cycle is not implemented (spec 'Zoom levels')"),
        new("Game menu (the Esc menu: loadout, resume, leave)", PadControl.Start, PadSource.OwnerRecollection, "owner 2026-10-30"),
        new("Scoreboard (held)", PadControl.Back, PadSource.OwnerRecollection, "owner 2026-10-30"),
    };

    // ---- sticks ----

    /// <summary>The movement stick's inner and outer zones (ours; the simulation's own dead zone is 0.1) and the look stick's dead zone (the data: the mapping is 0 up to 0.2).</summary>
    public const float MoveDeadZone = 0.2f, MoveOuterZone = 0.95f, LookDeadZone = 0.2f;

    /// <summary>The trigger levels: a press at 0.5, a release below 0.35 (ours).</summary>
    public const float TriggerPress = 0.5f, TriggerRelease = 0.35f;

    /// <summary>
    /// The movement stick as sim input (X right, Y forward): the dead zone removed and the rest stretched over 0..1 (radial, so the direction is kept), clamped to the unit circle.
    /// The simulation takes any length up to 1 (a half-pushed stick walks slower), so the magnitude is kept. <paramref name="stickDown"/> is Godot's Y (down positive).
    /// </summary>
    public static (float X, float Y) MoveStick(float stickX, float stickDown)
    {
        float x = Finite(stickX), y = -Finite(stickDown);
        float len = MathF.Sqrt(x * x + y * y);
        if (len <= MoveDeadZone) return (0f, 0f);
        float scaled = Math.Clamp((len - MoveDeadZone) / (MoveOuterZone - MoveDeadZone), 0f, 1f);
        return (x / len * scaled, y / len * scaled);
    }

    /// <summary>A piecewise-linear curve (points in file order, x ascending); the end values hold outside. Null or empty: the identity.</summary>
    public static float Curve(IReadOnlyList<(float From, float To)>? points, float x)
    {
        if (points is null || points.Count == 0) return x;
        if (x <= points[0].From) return points[0].To;
        for (int i = 1; i < points.Count; i++)
        {
            var (x0, y0) = points[i - 1]; var (x1, y1) = points[i];
            if (x <= x1) return x1 - x0 <= 1e-6f ? y1 : y0 + (y1 - y0) * (x - x0) / (x1 - x0);
        }
        return points[^1].To;
    }

    /// <summary>
    /// The look stick's turning rate in degrees per second for one axis (signed; the data's mapping and speed). The request is the stick's mapped value (<c>input_mapping</c>: raw stick to request, 0 below 0.2)
    /// held back by the damping cap while the stick has been out for <paramref name="heldSeconds"/> (<c>request_damping</c>: time to cap). Interpretation: the file does not say the time's unit or the cap's
    /// meaning; seconds and "the request may not exceed the cap" fit its shape (0.12 at once rising to 1 within a second; the aim set's to 1 within 0.15 s).
    /// </summary>
    public static float LookRate(float raw, float heldSeconds, float degreesPerSecond, IReadOnlyList<(float From, float To)>? mapping, IReadOnlyList<(float From, float To)>? damping)
    {
        float r = Finite(raw);
        float request = Curve(mapping, MathF.Min(1f, MathF.Abs(r)));
        if (damping is { Count: > 0 }) request = MathF.Min(request, Curve(damping, MathF.Max(0f, heldSeconds)));
        return MathF.Sign(r) * request * (float.IsFinite(degreesPerSecond) ? degreesPerSecond : 0f);
    }

    /// <summary>Our fallback speeds when a weapon's tuning has none (degrees per second: the data's default set, 300 and 200).</summary>
    public const float FallbackHorizontal = 300f, FallbackVertical = 200f;

    // ---- triggers ----

    /// <summary>A trigger's pressed state with hysteresis (press at <see cref="TriggerPress"/>, release below <see cref="TriggerRelease"/>).</summary>
    public static bool Trigger(bool wasPressed, float value) => wasPressed ? value >= TriggerRelease : value >= TriggerPress;

    // ---- the D-pad ----

    /// <summary>The weapon-swap button of a D-pad direction (Up, Right, Down, Left); None for another control.</summary>
    public static InputButtons WeaponSwap(PadControl dpad) => dpad switch
    {
        PadControl.DpadUp => InputButtons.SlotPrimary,
        PadControl.DpadRight => InputButtons.Switch,
        PadControl.DpadDown => InputButtons.SlotSecond,
        PadControl.DpadLeft => InputButtons.SlotPistol,
        _ => InputButtons.None,
    };

    /// <summary>The weapon menu's slot a held stick points at (stick selection of the radial menu): the D-pad control of the quadrant, or null inside the dead zone (0.5).</summary>
    public static PadControl? RadialSlot(float stickX, float stickDown, float deadZone = 0.5f)
    {
        float x = Finite(stickX), y = Finite(stickDown);
        if (x * x + y * y < deadZone * deadZone) return null;
        return MathF.Abs(x) > MathF.Abs(y) ? (x > 0 ? PadControl.DpadRight : PadControl.DpadLeft) : (y > 0 ? PadControl.DpadDown : PadControl.DpadUp);
    }

    // ---- the charge ----

    /// <summary>Whether the stick counts as forward for the charge (the simulation's rule: forward at least <c>RushSpec.MinForwardStick</c>, 0.7, at the heavy press; here for the tests).</summary>
    public static bool ChargeForward(float stickX, float stickDown, float minForward = 0.7f) => MoveStick(stickX, stickDown).Y >= minForward;

    static float Finite(float v) => float.IsFinite(v) ? v : 0f;
}

/// <summary>The control scheme in use.</summary>
public enum InputScheme { KeyboardMouse, Gamepad }

/// <summary>
/// Which device the player is using; the last one used wins (owner's request 2026-10-30: "autodetect when a controller is connected and switch to that as the primary way to control the game.
/// when the controller is disconnected swap back to keyboard/mouse controls"). A controller that connects becomes the scheme at once; a disconnect, or a key, click or mouse movement,
/// swaps back; a button press or a stick pushed past 0.5 swaps to the pad again (drift below that does nothing). Pure state, driven by <c>PadHost</c>.
/// </summary>
public sealed class SchemeTracker
{
    /// <summary>The mouse movement that counts as the player's hand: this many pixels in one event, or twice that since the last pad input (a bumped desk is not).</summary>
    public const float MouseDeadPixels = 3f;
    /// <summary>A stick or trigger value that counts as use (below it: drift).</summary>
    public const float ActivityLevel = 0.5f;

    public InputScheme Scheme { get; private set; } = InputScheme.KeyboardMouse;
    /// <summary>How many controllers are connected.</summary>
    public int Connected { get; private set; }
    /// <summary>The device the scheme follows (-1: none).</summary>
    public int Device { get; private set; } = -1;
    float _mousePixels;

    /// <summary>Raised when the scheme changes (the new scheme).</summary>
    public event Action<InputScheme>? Changed;

    /// <summary>A controller connected or disconnected; <paramref name="connectedCount"/> is how many remain.</summary>
    public void Connect(int device, bool connected, int connectedCount)
    {
        Connected = Math.Max(0, connectedCount);
        if (connected) { Device = device; _mousePixels = 0; Set(InputScheme.Gamepad); return; }
        if (device == Device) Device = -1;
        if (Connected == 0 || Device < 0) { if (Connected == 0) Device = -1; Set(InputScheme.KeyboardMouse); }
    }

    /// <summary>A pad button was pressed (any device the system reports).</summary>
    public void PadButton(int device)
    {
        Device = device; _mousePixels = 0;
        Set(InputScheme.Gamepad);
    }

    /// <summary>A stick or trigger moved to <paramref name="value"/>.</summary>
    public void PadAxis(int device, float value)
    {
        if (MathF.Abs(value) >= ActivityLevel) PadButton(device);
    }

    /// <summary>A key or a mouse button was pressed.</summary>
    public void KeyOrClick() { _mousePixels = 0; Set(InputScheme.KeyboardMouse); }

    /// <summary>The mouse moved by <paramref name="pixels"/>.</summary>
    public void MouseMoved(float pixels)
    {
        _mousePixels += MathF.Abs(pixels);
        if (MathF.Abs(pixels) >= MouseDeadPixels || _mousePixels >= MouseDeadPixels * 2f) { _mousePixels = 0; Set(InputScheme.KeyboardMouse); }
    }

    void Set(InputScheme scheme)
    {
        if (Scheme == scheme) return;
        Scheme = scheme;
        Changed?.Invoke(scheme);
    }
}
