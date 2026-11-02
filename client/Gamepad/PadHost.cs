using System;
using System.Collections.Generic;
using Godot;

namespace SmClient;

/// <summary>
/// The controller's device node (owner's request 2026-10-30): one node for the app, added next to the menu sounds. It keeps the pad's state from the input events (so synthetic events of a
/// test, <c>Input.ParseInputEvent</c>, act like a real device), decides which scheme is in use (<see cref="SchemeTracker"/>: the last device used wins, a connection or a disconnect swaps at
/// once), hides the mouse cursor in the menus while the pad is in use and gives the first control the focus when a pad press finds none, so the menus can be driven with the pad (Godot's
/// built-in <c>ui_*</c> actions: D-pad and left stick move the focus, A accepts, B is <c>ui_cancel</c>). The match reads it through <c>NetworkView.Gamepad.cs</c>.
/// </summary>
public partial class PadHost : Node
{
    public static PadHost? Current { get; private set; }

    /// <summary>Whether the controller is the scheme in use.</summary>
    public static bool Active => Current?.Tracker.Scheme == InputScheme.Gamepad;

    public readonly SchemeTracker Tracker = new();

    /// <summary>Pad state from the events: the six axes (left X/Y, right X/Y, left and right trigger) and the pressed buttons.</summary>
    readonly float[] _axes = new float[6];
    readonly bool[] _buttons = new bool[16];
    readonly bool[] _triggerDown = new bool[2];
    bool _hidMouse;
    double _nextScan;

    /// <summary>Raised on every pad button press and release (button, pressed) after the state is updated.</summary>
    public event Action<JoyButton, bool>? ButtonChanged;
    /// <summary>Raised when a trigger crosses its press level (0 left, 1 right; pressed).</summary>
    public event Action<int, bool>? TriggerChanged;
    /// <summary>Raised when the scheme changes (test log and the HUD's prompts).</summary>
    public event Action<InputScheme>? SchemeChanged;

    /// <summary>Test aid (<c>--log-input</c>): print the scheme changes.</summary>
    public static bool LogChanges;

    static PadHost? _pending;

    /// <summary>The app's one node; added on the next idle moment (the parent may be setting up its children when this is called).</summary>
    public static PadHost Ensure(Node parent)
    {
        if (Current is not null && IsInstanceValid(Current)) return Current;
        if (_pending is not null && IsInstanceValid(_pending)) return _pending;
        var node = new PadHost { Name = "PadHost" };
        _pending = node;
        parent.CallDeferred(Node.MethodName.AddChild, node);
        return node;
    }

    public override void _EnterTree()
    {
        Current = this;
        BindMenuAccept();
        Tracker.Changed += s =>
        {
            ApplyCursor();
            SchemeChanged?.Invoke(s);
            if (LogChanges) GD.Print($"input scheme: {s} (controllers connected {Tracker.Connected})");
        };
        Input.JoyConnectionChanged += OnJoyConnection;
        var pads = Input.GetConnectedJoypads();
        // A pad already plugged in at start is the scheme (the owner: "autodetect when a controller is connected").
        if (pads.Count > 0 && !DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase)) Tracker.Connect((int)pads[0], true, pads.Count);
        else Tracker.Connect(-1, false, pads.Count);
    }

    /// <summary>A on a controller presses what has the focus, as a left click does (owner 2026-11-02). Godot 4's built-in <c>ui_accept</c> holds only Enter, Keypad Enter and Space, so the pad's A is added to it here.</summary>
    static void BindMenuAccept()
    {
        var a = new InputEventJoypadButton { ButtonIndex = JoyButton.A, Device = -1 };
        if (!InputMap.ActionHasEvent("ui_accept", a)) InputMap.ActionAddEvent("ui_accept", a);
    }

    public override void _ExitTree()
    {
        Input.JoyConnectionChanged -= OnJoyConnection;
        if (Current == this) Current = null;
        if (_hidMouse) Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    void OnJoyConnection(long device, bool connected)
    {
        if (!connected) { Array.Clear(_axes); Array.Clear(_buttons); _triggerDown[0] = _triggerDown[1] = false; }
        Tracker.Connect((int)device, connected, Input.GetConnectedJoypads().Count);
    }

    // ---- state ----

    public (float X, float Y) LeftStick => (_axes[0], _axes[1]);
    public (float X, float Y) RightStick => (_axes[2], _axes[3]);
    public float LeftTrigger => _axes[4];
    public float RightTrigger => _axes[5];
    public bool LeftTriggerDown => _triggerDown[0];
    public bool RightTriggerDown => _triggerDown[1];
    public bool Button(JoyButton b) => (int)b is >= 0 and < 16 && _buttons[(int)b];

    /// <summary>Test aid: the state as a one-line description.</summary>
    public string Describe() => $"scheme {Tracker.Scheme}; LS {_axes[0]:F2},{_axes[1]:F2} RS {_axes[2]:F2},{_axes[3]:F2} LT {_axes[4]:F2} RT {_axes[5]:F2}; buttons {string.Join(",", Pressed())}";

    IEnumerable<string> Pressed()
    {
        for (int i = 0; i < _buttons.Length; i++) if (_buttons[i]) yield return ((JoyButton)i).ToString();
    }

    public override void _Input(InputEvent e)
    {
        switch (e)
        {
            case InputEventJoypadMotion m:
            {
                int axis = (int)m.Axis;
                if (axis is >= 0 and < 6) _axes[axis] = m.AxisValue;
                if (axis is 4 or 5)
                {
                    int i = axis - 4;
                    bool now = GamepadRules.Trigger(_triggerDown[i], m.AxisValue);
                    if (now != _triggerDown[i]) { _triggerDown[i] = now; TriggerChanged?.Invoke(i, now); }
                }
                Tracker.PadAxis(m.Device, m.AxisValue);
                if (Tracker.Scheme == InputScheme.Gamepad) MenuFocus(e);
                break;
            }
            case InputEventJoypadButton b:
            {
                int i = (int)b.ButtonIndex;
                if (i is >= 0 and < 16) _buttons[i] = b.Pressed;
                if (b.Pressed) Tracker.PadButton(b.Device);
                ButtonChanged?.Invoke(b.ButtonIndex, b.Pressed);
                if (b.Pressed && Tracker.Scheme == InputScheme.Gamepad) MenuFocus(e);
                break;
            }
            case InputEventKey { Pressed: true }:
            case InputEventMouseButton { Pressed: true }:
                Tracker.KeyOrClick();
                break;
            case InputEventMouseMotion mm:
                Tracker.MouseMoved(mm.Relative.Length());
                break;
        }
    }

    // ---- cursor and menu focus ----

    /// <summary>The cursor is hidden in the menus while the pad is in use and shown again with the keyboard and mouse (a match's captured mouse is left alone).</summary>
    void ApplyCursor()
    {
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase)) return;
        if (Tracker.Scheme == InputScheme.Gamepad && Input.MouseMode == Input.MouseModeEnum.Visible) { Input.MouseMode = Input.MouseModeEnum.Hidden; _hidMouse = true; }
        else if (Tracker.Scheme == InputScheme.KeyboardMouse && _hidMouse && Input.MouseMode == Input.MouseModeEnum.Hidden) { Input.MouseMode = Input.MouseModeEnum.Visible; _hidMouse = false; }
        else if (Tracker.Scheme == InputScheme.KeyboardMouse) _hidMouse = false;
    }

    public override void _Process(double delta)
    {
        // A screen that sets the mouse visible while the pad is in use (the loadout menu opening): hide it again.
        if (Tracker.Scheme == InputScheme.Gamepad && Input.MouseMode == Input.MouseModeEnum.Visible) ApplyCursor();
        AutoFocus();
    }

    /// <summary>
    /// A pad press or push with nothing focused in a menu (the mouse is not captured by a match): the first visible control that takes focus gets it, preferring the topmost layer; the press
    /// that did it is not passed on. Godot's <c>ui_*</c> actions move the focus from there.
    /// </summary>
    void MenuFocus(InputEvent e)
    {
        if (Input.MouseMode == Input.MouseModeEnum.Captured) return;
        var viewport = GetViewport();
        if (viewport is null || viewport.GuiGetFocusOwner() is not null) return;
        bool navigation = e is InputEventJoypadButton { ButtonIndex: JoyButton.DpadUp or JoyButton.DpadDown or JoyButton.DpadLeft or JoyButton.DpadRight or JoyButton.A }
            || e is InputEventJoypadMotion { Axis: JoyAxis.LeftX or JoyAxis.LeftY } mo && MathF.Abs(mo.AxisValue) >= 0.6f;
        if (!navigation) return;
        // Nothing focusable: do not walk the tree on every stick event; ask again after a quarter second.
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _nextScan) return;
        var target = FirstFocusable(GetTree().Root, out _);
        if (target is null) { _nextScan = now + 0.25; return; }
        target.GrabFocus();
        viewport.SetInputAsHandled();
    }

    /// <summary>
    /// A menu opened while the pad is in use gets a focused control at once (owner 2026-11-02: A must press like a left click, and a press needs a focus): when the pad is the scheme, the mouse is not
    /// captured by a match and nothing has the focus, the first focusable control of the topmost layer takes it (scanned at most four times a second, and only while something is visible to take it).
    /// </summary>
    void AutoFocus()
    {
        if (Tracker.Scheme != InputScheme.Gamepad || Input.MouseMode == Input.MouseModeEnum.Captured) return;
        var viewport = GetViewport();
        if (viewport is null || viewport.GuiGetFocusOwner() is not null) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _nextScan) return;
        _nextScan = now + 0.25;
        FirstFocusable(GetTree().Root, out _)?.GrabFocus();
    }

    /// <summary>The first control, in the topmost canvas layer, that is visible and takes focus.</summary>
    public static Control? FirstFocusable(Node root, out int layer)
    {
        Control? best = null; int bestLayer = int.MinValue;
        void Walk(Node n, int currentLayer)
        {
            if (n is CanvasLayer cl) currentLayer = cl.Layer;
            if (n is CanvasItem { Visible: false }) return;
            if (n is Control c && c.FocusMode == Control.FocusModeEnum.All && c.IsVisibleInTree() && !(c is BaseButton { Disabled: true }) && currentLayer > bestLayer)
            { best = c; bestLayer = currentLayer; }
            foreach (var child in n.GetChildren()) Walk(child, currentLayer);
        }
        Walk(root, 0);
        layer = bestLayer;
        return best;
    }

    // ---- back ----

    /// <summary>
    /// The menu "back" press (owner's request 2026-11-01): Esc, or B on a controller (the east face button, the console convention), through Godot's <c>ui_cancel</c> and, so the rule does not
    /// depend on the input map, B itself. Every screen that Esc closes or steps back from asks this, so B does the same there. While a rebinding waits for an input B is not "back" (the
    /// controller tab binds it; the keyboard tab treats it like Esc: cancel).
    /// </summary>
    public static bool IsBack(InputEvent e) =>
        e.IsActionPressed("ui_cancel") || e is InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.B };

    // ---- prompts ----

    /// <summary>The key hint for the scheme in use: <paramref name="keyboard"/> (e.g. "Esc") or the pad's name (e.g. "START"), in the original's naming.</summary>
    public static string Hint(string keyboard, string pad) => Active ? pad : keyboard;
}
