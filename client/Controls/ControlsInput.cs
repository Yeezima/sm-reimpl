using System;
using System.Collections.Generic;
using Godot;

namespace SmClient;

/// <summary>
/// The player's bindings in the engine (ui/keybinds; ours, an added option): the keyboard and mouse table is registered in Godot's InputMap as one <c>sm_*</c> action per
/// <see cref="GameAction"/>, rebuilt whenever the bindings change (so a rebinding applies live, also in a running match), and the match reads those actions
/// (<c>Input.IsActionPressed</c>, <c>InputEvent.IsAction</c>). The controller table is read by <see cref="PadHost"/>'s events through <see cref="PadActions"/> (the pad keeps its own
/// device state, the trigger levels and the scheme switch). The bindings live in <c>controls.json</c> beside the front end's settings (the profile folder, else the user data folder).
/// </summary>
public static class ControlsInput
{
    static BindingSet _bindings = BindingSet.Defaults();
    static string? _directory;
    static readonly Dictionary<string, GameAction[]> _padIndex = new();

    /// <summary>The bindings in use.</summary>
    public static BindingSet Bindings => _bindings;

    /// <summary>Notes from reading the file (shown once on the controls screen).</summary>
    public static List<string> LoadNotes { get; private set; } = new();

    /// <summary>Where the bindings are saved (null before <see cref="UseDirectory"/>).</summary>
    public static string? Directory => _directory;

    /// <summary>Loads <c>controls.json</c> from <paramref name="directory"/> (defaults when absent) and registers it.</summary>
    public static void UseDirectory(string directory)
    {
        if (_directory == directory) return;
        _directory = directory;
        _bindings = BindingSet.Load(directory, out var notes);
        LoadNotes = notes;
        Apply();
    }

    /// <summary>Makes sure the actions exist (the user data folder's file when no front end has chosen a folder yet).</summary>
    public static void Ensure()
    {
        if (_directory is null) UseDirectory(ProjectSettings.GlobalizePath("user://"));
    }

    /// <summary>Takes a changed set into use: registers it with the InputMap and saves it. Returns the save error or null.</summary>
    public static string? Use(BindingSet set)
    {
        _bindings = set;
        Apply();
        return _directory is null ? null : set.Save(_directory);
    }

    // ---- the InputMap ----

    public static string Name(GameAction action) => ControlActions.Info(action).MapName;

    /// <summary>Rebuilds the InputMap actions and the pad index from the current bindings.</summary>
    public static void Apply()
    {
        foreach (var info in ControlActions.All)
        {
            string name = info.MapName;
            if (!InputMap.HasAction(name)) InputMap.AddAction(name);
            else InputMap.ActionEraseEvents(name);
            foreach (var input in _bindings.Inputs(BindDevice.Keyboard, info.Action))
                foreach (var ev in EventsOf(input)) InputMap.ActionAddEvent(name, ev);
        }
        _padIndex.Clear();
        foreach (var info in ControlActions.All)
            foreach (var input in _bindings.Inputs(BindDevice.Controller, info.Action))
                _padIndex[input] = _padIndex.TryGetValue(input, out var list) ? [.. list, info.Action] : [info.Action];
    }

    /// <summary>The InputMap events of a keyboard / mouse input (the wheel is two events); an unknown name gives none.</summary>
    static IEnumerable<InputEvent> EventsOf(string input)
    {
        int colon = input.IndexOf(':');
        if (colon < 0) yield break;
        string kind = input[..colon], name = input[(colon + 1)..];
        if (kind == "Key" && Enum.TryParse<Key>(name, out var key) && key != Key.None)
            yield return new InputEventKey { PhysicalKeycode = key };
        else if (kind == "Mouse" && name == "Wheel")
        {
            yield return new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp };
            yield return new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown };
        }
        else if (kind == "Mouse" && Enum.TryParse<MouseButton>(name, out var button) && button != MouseButton.None)
            yield return new InputEventMouseButton { ButtonIndex = button };
    }

    // ---- reading ----

    /// <summary>Whether any keyboard / mouse input of the action is down now (the wheel never is).</summary>
    public static bool Held(GameAction action) => InputMap.HasAction(Name(action)) && Input.IsActionPressed(Name(action));

    /// <summary>Whether the event is a press or release of an input bound to the action (keyboard and mouse table).</summary>
    public static bool Is(InputEvent e, GameAction action) => InputMap.HasAction(Name(action)) && e.IsAction(Name(action));

    /// <summary>Whether the event is a mouse wheel notch.</summary>
    public static bool IsWheel(InputEvent e) => e is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown };

    /// <summary>The actions a controller input is bound to (see <see cref="PadCode"/>).</summary>
    public static IReadOnlyList<GameAction> PadActions(string code) => _padIndex.TryGetValue(code, out var list) ? list : Array.Empty<GameAction>();

    /// <summary>The binding name of a pad button.</summary>
    public static string PadCode(JoyButton button) => "Pad:" + button;

    /// <summary>The binding name of a trigger (0 left, 1 right).</summary>
    public static string TriggerCode(int trigger) => trigger == 0 ? "Pad:LT" : "Pad:RT";

    /// <summary>Whether any controller input of the action is down (<see cref="PadHost"/>'s state).</summary>
    public static bool PadHeld(PadHost pad, GameAction action)
    {
        foreach (var input in _bindings.Inputs(BindDevice.Controller, action))
        {
            if (input == "Pad:LT") { if (pad.LeftTriggerDown) return true; }
            else if (input == "Pad:RT") { if (pad.RightTriggerDown) return true; }
            else if (Enum.TryParse<JoyButton>(input.AsSpan(4), out var b) && pad.Button(b)) return true;
        }
        return false;
    }

    /// <summary>The first input bound to the action on the device in use, as a prompt ("E", "B"); "?" when nothing is bound.</summary>
    public static string Prompt(GameAction action)
    {
        foreach (var input in _bindings.Inputs(PadHost.Active ? BindDevice.Controller : BindDevice.Keyboard, action)) return BindingSet.Display(input);
        return "?";
    }

    // ---- capturing a new binding (the controls screen) ----

    /// <summary>The binding name of a key or mouse event worth binding, else null (releases, echoes, motion, and the other device's events give null).</summary>
    public static string? KeyboardInputOf(InputEvent e) => e switch
    {
        InputEventKey { Pressed: true, Echo: false } k => NameOf(k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode),
        InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } => "Mouse:Wheel",
        InputEventMouseButton { Pressed: true } m when m.ButtonIndex != MouseButton.None => "Mouse:" + m.ButtonIndex,
        _ => null,
    };

    static string? NameOf(Key key) => key == Key.None ? null : "Key:" + key;

    /// <summary>The binding name of a controller event worth binding (a button press, or a trigger pushed past its press level), else null.</summary>
    public static string? PadInputOf(InputEvent e) => e switch
    {
        InputEventJoypadButton { Pressed: true } b => PadCode(b.ButtonIndex),
        InputEventJoypadMotion { Axis: JoyAxis.TriggerLeft } m when GamepadRules.Trigger(false, m.AxisValue) => "Pad:LT",
        InputEventJoypadMotion { Axis: JoyAxis.TriggerRight } m when GamepadRules.Trigger(false, m.AxisValue) => "Pad:RT",
        _ => null,
    };
}
