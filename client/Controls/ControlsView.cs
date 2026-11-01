using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer;
using SmSim;

namespace SmClient;

/// <summary>
/// The CONTROLS screen (ui/keybinds; the owner's request 2026-10-31: "add an interface in the menu for players to customize their keybinds. allow them to do it for controller as well."; ours,
/// an added option). Two tabs, Keyboard and Mouse (two bindings per action) and Controller (one). Click an entry (or press A / Enter on it) and then press the new key, mouse button or
/// controller button; Esc cancels (Start on the controller tab); Delete or a right click (X on the controller) clears the entry. An input another action uses asks Swap, Replace or Cancel.
/// A change is saved and applied at once (<see cref="ControlsInput.Use"/>). Fully drivable with the pad (menu focus; LB / RB switch the tab) and with the mouse and keyboard.
/// The bound actions drive the same input bits as before (<see cref="BindingSet.Sample"/>), so the charge and the directional strikes follow them.
/// </summary>
public sealed partial class ControlsView : Control
{
    readonly Action _onBack;
    readonly Control _content;
    readonly VBoxContainer _rows = UiKit.VBox(6);
    readonly ScrollContainer _scroll;
    readonly Label _hint, _status;
    readonly Button _tabKeyboard, _tabController, _reset, _back;
    readonly Dictionary<(BindDevice, GameAction, int), Button> _binds = new();
    BindingSet _set = BindingSet.Defaults();
    BindDevice _device = BindDevice.Keyboard;
    (BindDevice Device, GameAction Action, int Slot)? _capture;
    double _captureAt;
    bool _dialogOpen;

    /// <summary>The tab shown.</summary>
    public BindDevice Device => _device;
    /// <summary>The line under the tabs (what the last change did, or why it was refused).</summary>
    public string Status => _status.Text;
    /// <summary>Whether a new binding is being waited for.</summary>
    public bool Capturing => _capture is not null;
    /// <summary>The bindings being edited (a copy; every change is already saved and in use).</summary>
    public BindingSet Working => _set;
    public Button TabKeyboard => _tabKeyboard;
    public Button TabController => _tabController;
    public Button ResetButton => _reset;
    public Button BackButton => _back;
    /// <summary>The scrolling list of rows (the layout checks read it).</summary>
    public ScrollContainer List => _scroll;
    /// <summary>The entry button of an action's slot on the tab shown, or null.</summary>
    public Button? BindButton(GameAction action, int slot) => _binds.GetValueOrDefault((_device, action, slot));

    /// <param name="background">What the screen stands on (the in-match dim); null: nothing of its own, the screen behind shows (the front end's background).</param>
    public ControlsView(Action onBack, Control? background = null)
    {
        _onBack = onBack;
        Name = "Controls";
        SetAnchorsPreset(LayoutPreset.FullRect);
        Visible = false;
        if (background is not null) AddChild(background.With(n => n.SetAnchorsPreset(LayoutPreset.FullRect)));
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var (side, v) in new[] { ("left", 84), ("right", 84), ("top", 22), ("bottom", 20) }) margin.AddThemeConstantOverride("margin_" + side, v);
        AddChild(margin);
        _content = margin;
        var root = UiKit.VBox(10);
        margin.AddChild(root);

        var header = UiKit.HBox(16);
        header.AddChild(UiKit.Text("CONTROLS", UiKit.HeaderSize, UiKit.Gold));
        header.AddChild(UiKit.Spacer());
        _back = UiKit.Button("BACK (Esc)", () => _onBack(), 190);
        _back.Name = "ControlsBack";
        header.AddChild(_back);
        root.AddChild(header);

        var tabs = UiKit.HBox(8);
        var group = new ButtonGroup();
        _tabKeyboard = new Button { Text = "KEYBOARD & MOUSE", ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(360, UiKit.MinHit), Name = "TabKeyboard" };
        _tabController = new Button { Text = "CONTROLLER", ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(280, UiKit.MinHit), Name = "TabController" };
        _tabKeyboard.Pressed += () => SetDevice(BindDevice.Keyboard);
        _tabController.Pressed += () => SetDevice(BindDevice.Controller);
        tabs.AddChild(_tabKeyboard);
        tabs.AddChild(_tabController);
        tabs.AddChild(UiKit.Spacer());
        _reset = UiKit.Button("RESET TO DEFAULTS", AskReset, 340, "Puts every binding of this tab back to the game's own");
        _reset.Name = "ControlsReset";
        tabs.AddChild(_reset);
        UiKit.ChainHorizontal([_tabKeyboard, _tabController, _reset]);
        root.AddChild(tabs);

        _hint = UiKit.Text("", UiKit.CaptionSize, UiKit.Grey, HorizontalAlignment.Left, true);
        root.AddChild(_hint);
        _status = UiKit.Text("", UiKit.CaptionSize, UiKit.White, HorizontalAlignment.Left, true);
        _status.Name = "ControlsStatus";
        root.AddChild(_status);

        _scroll = UiKit.Scroll(_rows);
        _scroll.Name = "ControlsList";
        _scroll.CustomMinimumSize = new Vector2(0, 240);
        root.AddChild(UiKit.Panel(_scroll, 10).With(p => p.SizeFlagsVertical = SizeFlags.ExpandFill));
        _scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    // ---- showing ----

    /// <summary>Shows the screen on the tab of the device in use (the controller tab while a pad is the scheme).</summary>
    public void Open(BindDevice? device = null)
    {
        _set = ControlsInput.Bindings.Clone();
        _capture = null;
        Visible = true;
        _status.Text = "";
        if (ControlsInput.LoadNotes.Count > 0) { Say(string.Join(" ", ControlsInput.LoadNotes), true); ControlsInput.LoadNotes.Clear(); }
        SetDevice(device ?? (PadHost.Active ? BindDevice.Controller : BindDevice.Keyboard));
        (_device == BindDevice.Keyboard ? _tabKeyboard : _tabController).FocusLater();
    }

    public void Close()
    {
        _capture = null;
        Visible = false;
    }

    void SetDevice(BindDevice device)
    {
        _device = device;
        _capture = null;
        (device == BindDevice.Keyboard ? _tabKeyboard : _tabController).SetPressedNoSignal(true);
        (device == BindDevice.Keyboard ? _tabController : _tabKeyboard).SetPressedNoSignal(false);
        _hint.Text = device == BindDevice.Keyboard
            ? "Click an entry, then press the new key or mouse button. Esc cancels. Delete or a right click clears an entry. An input used twice asks what to do. Changes apply at once."
            : "Press A on an entry, then press the new button or trigger. Esc or Start cancels. X clears the entry. LB / RB switch tabs. The move and look sticks are fixed.";
        BuildRows();
    }

    // ---- rows ----

    void BuildRows()
    {
        foreach (var child in _rows.GetChildren()) { _rows.RemoveChild(child); child.QueueFree(); }
        foreach (var key in _binds.Keys.Where(k => k.Item1 == _device).ToList()) _binds.Remove(key);
        int slots = BindingSet.Slots(_device);
        var head = UiKit.HBox(10);
        head.AddChild(UiKit.Text("ACTION", UiKit.CaptionSize, UiKit.Grey).With(l => l.SizeFlagsHorizontal = SizeFlags.ExpandFill));
        for (int s = 0; s < slots; s++) head.AddChild(UiKit.Text(slots == 1 ? "BUTTON" : s == 0 ? "PRIMARY" : "SECONDARY", UiKit.CaptionSize, UiKit.Grey, HorizontalAlignment.Center).With(l => l.CustomMinimumSize = new Vector2(BindWidth, 0)));
        _rows.AddChild(head);
        string group = "";
        Button? first = null;
        foreach (var info in ControlActions.For(_device, ContentExtras.Compiled))
        {
            if (info.Group != group)
            {
                group = info.Group;
                _rows.AddChild(UiKit.Text(group.ToUpperInvariant(), UiKit.BodySize, UiKit.Gold).With(l => l.CustomMinimumSize = new Vector2(0, 36)));
            }
            var row = UiKit.HBox(10);
            row.AddChild(UiKit.Text(info.Label, UiKit.BodySize, UiKit.White).With(l => { l.SizeFlagsHorizontal = SizeFlags.ExpandFill; l.VerticalAlignment = VerticalAlignment.Center; l.ClipText = true; l.CustomMinimumSize = new Vector2(200, 0); l.TooltipText = info.Label; l.MouseFilter = MouseFilterEnum.Pass; }));
            var buttons = new List<Control>();
            for (int s = 0; s < slots; s++)
            {
                int slot = s;
                var action = info.Action;
                var b = new Button { Text = "", FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(BindWidth, UiKit.MinHit), ClipText = true, Name = $"Bind_{info.Action}_{slot}",
                    TooltipText = "Click, then press the new input. Delete or a right click clears it." };
                b.Pressed += () => BeginCapture(action, slot);
                b.GuiInput += ev => { if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } && _capture is null) { Accept(); ClearBinding(action, slot); } };
                row.AddChild(b);
                buttons.Add(b);
                _binds[(_device, action, slot)] = b;
                first ??= b;
            }
            UiKit.ChainHorizontal(buttons);
            _rows.AddChild(row);
        }
        // What cannot be rebound, so nobody looks for it.
        _rows.AddChild(UiKit.Text("FIXED", UiKit.BodySize, UiKit.Gold).With(l => l.CustomMinimumSize = new Vector2(0, 36)));
        var fixedRows = _device == BindDevice.Keyboard
            ? new[] { ("Look", "Mouse"), ("Turn (keyboard)", "Left / Right arrows"), ("Game menu", "Esc"), ("Info overlay", "F1"), ("Fullscreen", "F11") }
            : new[] { ("Move", "Left stick"), ("Look", "Right stick"), ("Game menu", "Start") };
        foreach (var (label, value) in fixedRows)
        {
            var row = UiKit.HBox(10);
            row.AddChild(UiKit.Text(label, UiKit.BodySize, UiKit.Grey).With(l => l.SizeFlagsHorizontal = SizeFlags.ExpandFill));
            row.AddChild(UiKit.Text(value, UiKit.BodySize, UiKit.Grey, HorizontalAlignment.Center).With(l => l.CustomMinimumSize = new Vector2(BindWidth, 0)));
            _rows.AddChild(row);
        }
        RefreshTexts();
    }

    const int BindWidth = 330;

    void RefreshTexts()
    {
        foreach (var ((device, action, slot), button) in _binds)
        {
            if (device != _device) continue;
            bool waiting = _capture is { } c && c.Device == device && c.Action == action && c.Slot == slot;
            button.Text = waiting ? (device == BindDevice.Keyboard ? "PRESS A KEY..." : "PRESS A BUTTON...") : BindingSet.Display(_set.Get(device, action, slot));
            button.AddThemeColorOverride("font_color", waiting ? UiKit.Gold : (_set.Get(device, action, slot) is null ? UiKit.Grey : UiKit.White));
            button.AddThemeColorOverride("font_hover_color", UiKit.Gold);
        }
    }

    void Say(string text, bool problem = false)
    {
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", problem ? UiKit.LightRed : UiKit.White);
    }

    // ---- capturing ----

    /// <summary>Waits for the new input of an entry (the entry button, or a test).</summary>
    public void BeginCapture(GameAction action, int slot)
    {
        if (_dialogOpen) return;
        _capture = (_device, action, slot);
        _captureAt = Time.GetTicksMsec() / 1000.0;
        Say($"{ControlActions.Info(action).Label}: press the new {(_device == BindDevice.Keyboard ? "key or mouse button" : "button or trigger")}. {(_device == BindDevice.Keyboard ? "Esc" : "Esc or Start")} cancels.");
        RefreshTexts();
    }

    public override void _Input(InputEvent e)
    {
        if (!IsVisibleInTree() || _dialogOpen) return;
        if (_capture is { } capture) { CaptureEvent(e, capture); return; }
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Delete or Key.Backspace } && FocusedBind() is { } at) { Accept(); ClearBinding(at.Action, at.Slot); return; }
        if (PadHost.IsBack(e)) { Accept(); _onBack(); return; }
        if (e is InputEventJoypadButton { Pressed: true } pad)
        {
            switch (pad.ButtonIndex)
            {
                case JoyButton.LeftShoulder: SetDevice(BindDevice.Keyboard); (_tabKeyboard).FocusLater(); break;
                case JoyButton.RightShoulder: SetDevice(BindDevice.Controller); (_tabController).FocusLater(); break;
                case JoyButton.X: if (FocusedBind() is { } x) ClearBinding(x.Action, x.Slot); break;
                case JoyButton.Start: break;   // the creator's Save is not for this screen
                default: return;
            }
            Accept();
        }
    }

    void Accept() => GetViewport().SetInputAsHandled();

    (GameAction Action, int Slot)? FocusedBind()
    {
        var owner = GetViewport().GuiGetFocusOwner();
        foreach (var (key, b) in _binds) if (key.Item1 == _device && b == owner) return (key.Item2, key.Item3);
        return null;
    }

    void CaptureEvent(InputEvent e, (BindDevice Device, GameAction Action, int Slot) capture)
    {
        bool bindable = e is InputEventKey or InputEventMouseButton or InputEventJoypadButton;
        if (bindable) Accept();   // nothing under the screen sees what is being bound (releases included)
        if (Time.GetTicksMsec() / 1000.0 - _captureAt < 0.15) return;   // the press that opened the entry
        if (e is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape } || e is InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.Start } && capture.Device == BindDevice.Controller
            || e is InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.B } && capture.Device == BindDevice.Keyboard)   // B is bindable on the controller tab only; on the keyboard tab it cancels like Esc
        {
            _capture = null;
            Say("Cancelled.");
            RefreshTexts();
            BindButton(capture.Action, capture.Slot)?.FocusLater();
            return;
        }
        string? input = capture.Device == BindDevice.Keyboard ? ControlsInput.KeyboardInputOf(e) : ControlsInput.PadInputOf(e);
        if (input is null) return;
        Accept();
        _capture = null;
        Resolve(capture.Device, capture.Action, capture.Slot, input);
    }

    /// <summary>Screenshot aid (<c>--controls-conflict</c>): presses the input of the grenade (the first bound one) for Reload, so the conflict prompt shows. Nothing is changed until a choice.</summary>
    public void DemoConflict()
    {
        string? used = _set.Get(_device, GameAction.Grenade, 0);
        if (used is not null) Resolve(_device, GameAction.Reload, 0, used);
    }

    void Resolve(BindDevice device, GameAction action, int slot, string input)
    {
        var info = ControlActions.Info(action);
        if (BindingSet.Reserved(device, input) is { } why) { Say(why, true); RefreshTexts(); BindButton(action, slot)?.FocusLater(); return; }
        if (_set.Get(device, action, slot) == input) { Say($"{info.Label} is already {BindingSet.Display(input)}."); RefreshTexts(); BindButton(action, slot)?.FocusLater(); return; }
        if (_set.FindConflict(device, action, slot, input) is { } other)
        {
            string otherLabel = ControlActions.Info(other.Action).Label;
            string old = BindingSet.Display(_set.Get(device, action, slot));
            string name = BindingSet.Display(input);
            Say($"{name} is already used for {otherLabel}.", true);
            RefreshTexts();
            _dialogOpen = true;
            UiKit.Dialog.Open(_content, this, device == BindDevice.Keyboard ? "Input already in use" : "Button already in use",
                $"{name} is already {otherLabel}.\n\nSwap: {otherLabel} takes {(old == "-" ? "nothing" : old)}, {info.Label} takes {name}.\nReplace: {otherLabel} is left unbound.",
                ["Swap", "Replace", "Cancel"], i =>
                {
                    _dialogOpen = false;
                    if (i == 0) Apply(device, action, slot, input, ConflictChoice.Swap);
                    else if (i == 1) Apply(device, action, slot, input, ConflictChoice.Replace);
                    else { Say("Cancelled."); BindButton(action, slot)?.FocusLater(); }
                }, 2);
            return;
        }
        Apply(device, action, slot, input, ConflictChoice.Replace);
    }

    void Apply(BindDevice device, GameAction action, int slot, string input, ConflictChoice choice)
    {
        var info = ControlActions.Info(action);
        var other = _set.FindConflict(device, action, slot, input);
        if (!_set.Assign(device, action, slot, input, choice)) { Say("That input cannot be used here.", true); return; }
        string note = $"{info.Label}: {BindingSet.Display(input)}.";
        if (other is { } o) note += choice == ConflictChoice.Swap ? $" {ControlActions.Info(o.Action).Label} now {BindingSet.Display(_set.Get(device, o.Action, o.Slot))}." : $" {ControlActions.Info(o.Action).Label} is unbound.";
        Commit(note);
        BindButton(action, slot)?.FocusLater();
    }

    void ClearBinding(GameAction action, int slot)
    {
        if (_set.Get(_device, action, slot) is null) { Say("Already empty."); return; }
        _set.Clear(_device, action, slot);
        Commit($"{ControlActions.Info(action).Label}: cleared.");
        BindButton(action, slot)?.FocusLater();
    }

    /// <summary>Applies and saves the edited set, then shows <paramref name="note"/> (or the save error).</summary>
    void Commit(string note)
    {
        string? error = ControlsInput.Use(_set.Clone());
        if (error is not null) Say(note + " " + error, true); else Say(note);
        RefreshTexts();
    }

    // ---- reset ----

    void AskReset()
    {
        if (_dialogOpen) return;
        string tab = _device == BindDevice.Keyboard ? "keyboard and mouse" : "controller";
        _dialogOpen = true;
        UiKit.Dialog.Open(_content, this, "Reset to defaults?", $"Every {tab} binding goes back to the game's own.", ["Reset", "Cancel"], i =>
        {
            _dialogOpen = false;
            if (i == 0) ResetDevice();
            else _reset.FocusLater();
        }, 1);
    }

    /// <summary>Puts the tab shown back to the defaults and saves (the RESET TO DEFAULTS button after its confirmation).</summary>
    public void ResetDevice()
    {
        _set.ResetDevice(_device);
        Commit(_device == BindDevice.Keyboard ? "Keyboard and mouse: defaults restored." : "Controller: defaults restored.");
        _reset.FocusLater();
    }
}
