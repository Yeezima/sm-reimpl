using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmSim;

namespace SmClient;

/// <summary>Which device a binding belongs to; each has its own table and its own conflicts.</summary>
public enum BindDevice { Keyboard, Controller }

/// <summary>
/// Every gameplay action a player can rebind (ui/keybinds, the owner's request 2026-10-31: "add an interface in the menu for players to customize their keybinds. allow them to do it for
/// controller as well."). Added option, ours: the original's PC defaults are the starting bindings (see <see cref="BindingSet.Defaults"/>), the screen only lets a player change them.
/// </summary>
public enum GameAction
{
    MoveForward, MoveBack, MoveLeft, MoveRight, Sprint,
    Fire, Aim, Melee, StunStrike, Jump, Grenade, Reload, Use,
    Weapon1, Weapon2, Weapon3, NextWeapon, ZoomCycle,
    Scoreboard, Shout,
}

/// <summary>One action's row: its label, group, the sim button it presses (None for the movement actions and the client-only ones) and where it applies.</summary>
public sealed record ActionInfo(GameAction Action, string Label, string Group, InputButtons Buttons, bool Keyboard, bool Controller, bool ExtendedOnly, bool NeedsCapture)
{
    /// <summary>The InputMap action name (the Godot side registers one per action).</summary>
    public string MapName => "sm_" + Action.ToString().ToLowerInvariant();
}

/// <summary>The action table (order = the screen's order).</summary>
public static class ControlActions
{
    public static readonly IReadOnlyList<ActionInfo> All = new ActionInfo[]
    {
        new(GameAction.MoveForward, "Move forward", "Movement", InputButtons.None, true, false, false, false),
        new(GameAction.MoveBack, "Move back", "Movement", InputButtons.None, true, false, false, false),
        new(GameAction.MoveLeft, "Move left", "Movement", InputButtons.None, true, false, false, false),
        new(GameAction.MoveRight, "Move right", "Movement", InputButtons.None, true, false, false, false),
        new(GameAction.Sprint, "Sprint (hold)", "Movement", InputButtons.Sprint, true, true, false, false),
        new(GameAction.Jump, "Evade / jump pack", "Movement", InputButtons.Jump, true, true, false, false),
        new(GameAction.Fire, "Fire", "Combat", InputButtons.Fire, true, true, false, true),
        new(GameAction.Aim, "Aim", "Combat", InputButtons.Aim, true, true, false, true),
        new(GameAction.Melee, "Melee strike", "Combat", InputButtons.Melee, true, true, false, true),
        new(GameAction.StunStrike, "Stun strike (charge: Sprint + forward + this)", "Combat", InputButtons.MeleeHeavy, true, true, false, true),
        new(GameAction.Grenade, "Grenade", "Combat", InputButtons.Grenade, true, true, false, true),
        new(GameAction.Reload, "Reload / vent", "Combat", InputButtons.Reload, true, true, false, false),
        new(GameAction.Use, "Use / pick up", "Combat", InputButtons.Interact, true, true, false, true),
        new(GameAction.Weapon1, "Weapon 1 (pistol)", "Weapons", InputButtons.SlotPistol, true, true, false, false),
        new(GameAction.Weapon2, "Weapon 2 (primary)", "Weapons", InputButtons.SlotPrimary, true, true, false, false),
        new(GameAction.Weapon3, "Weapon 3 (second primary)", "Weapons", InputButtons.SlotSecond, true, true, false, false),
        new(GameAction.NextWeapon, "Next weapon", "Weapons", InputButtons.Switch, true, true, false, true),
        new(GameAction.ZoomCycle, "Scope zoom level", "Weapons", InputButtons.None, false, true, false, false),
        new(GameAction.Scoreboard, "Scoreboard (hold)", "Interface", InputButtons.None, true, true, false, false),
        new(GameAction.Shout, "Shout (extended content)", "Interface", InputButtons.None, true, true, true, false),
    };

    public static ActionInfo Info(GameAction action) => All.First(a => a.Action == action);

    public static IEnumerable<ActionInfo> For(BindDevice device, bool extended) =>
        All.Where(a => (device == BindDevice.Keyboard ? a.Keyboard : a.Controller) && (extended || !a.ExtendedOnly));
}

/// <summary>What to do with an input that another action already uses.</summary>
public enum ConflictChoice
{
    /// <summary>The other action takes the binding this slot had (cleared when it had none).</summary>
    Swap,
    /// <summary>The other action loses the input (its slot is cleared).</summary>
    Replace,
}

/// <summary>The sampled input of the bound actions (the same bits the old hard-coded keys made).</summary>
public readonly record struct SampledControls(float X, float Y, InputButtons Buttons);

/// <summary>
/// The bindings of both devices (pure, no Godot, so the tests run it directly: tests/SmNet.Tests/ControlBindingsTests.cs includes this file). Inputs are names: <c>Key:W</c> (Godot's
/// Key enum name, a physical key), <c>Mouse:Left</c> (MouseButton name, or <c>Mouse:Wheel</c> for either wheel direction) and <c>Pad:A</c> (JoyButton name, or <c>Pad:LT</c> / <c>Pad:RT</c>
/// for the triggers). The keyboard and mouse table has two slots per action, the controller table one. The move stick and the look stick are fixed (not in the table).
/// Combination rules (the charge, the directional strikes) live in the simulation and read the sampled bits, so a remapped action drives them with no code of their own:
/// the charge is Sprint + forward (the move actions) + Stun strike, whichever inputs those are bound to.
/// </summary>
public sealed class BindingSet
{
    public const int KeyboardSlots = 2, ControllerSlots = 1;
    public const string FileName = "controls.json";
    const int Version = 1;

    readonly Dictionary<GameAction, string?[]> _keyboard = new(), _controller = new();

    public static int Slots(BindDevice device) => device == BindDevice.Keyboard ? KeyboardSlots : ControllerSlots;

    Dictionary<GameAction, string?[]> Table(BindDevice device) => device == BindDevice.Keyboard ? _keyboard : _controller;

    // ---- defaults ----

    /// <summary>
    /// Today's bindings. Keyboard (the project's documented ones): WASD, Shift sprint, left mouse fire, Left Ctrl aim, right mouse or V melee, F stun strike, Space evade / jump pack, G grenade,
    /// R reload / vent, E use, 1 2 3 weapons, X or the mouse wheel next weapon, Tab scoreboard, T shout. Controller: the original's Xbox 360 layout (<see cref="GamepadRules.Bindings"/>).
    /// </summary>
    public static BindingSet Defaults()
    {
        var s = new BindingSet();
        void K(GameAction a, string? first, string? second = null) => s._keyboard[a] = [first, second];
        void P(GameAction a, string? input) => s._controller[a] = [input];
        K(GameAction.MoveForward, "Key:W"); K(GameAction.MoveBack, "Key:S"); K(GameAction.MoveLeft, "Key:A"); K(GameAction.MoveRight, "Key:D");
        K(GameAction.Sprint, "Key:Shift"); K(GameAction.Fire, "Mouse:Left"); K(GameAction.Aim, "Key:Ctrl");
        K(GameAction.Melee, "Mouse:Right", "Key:V"); K(GameAction.StunStrike, "Key:F"); K(GameAction.Jump, "Key:Space");
        K(GameAction.Grenade, "Key:G"); K(GameAction.Reload, "Key:R"); K(GameAction.Use, "Key:E");
        K(GameAction.Weapon1, "Key:Key1"); K(GameAction.Weapon2, "Key:Key2"); K(GameAction.Weapon3, "Key:Key3");
        K(GameAction.NextWeapon, "Key:X", "Mouse:Wheel"); K(GameAction.ZoomCycle, null); K(GameAction.Scoreboard, "Key:Tab"); K(GameAction.Shout, "Key:T");
        P(GameAction.MoveForward, null); P(GameAction.MoveBack, null); P(GameAction.MoveLeft, null); P(GameAction.MoveRight, null);
        P(GameAction.Sprint, "Pad:LeftStick"); P(GameAction.Fire, "Pad:RT"); P(GameAction.Aim, "Pad:LT");
        P(GameAction.Melee, "Pad:X"); P(GameAction.StunStrike, "Pad:Y"); P(GameAction.Jump, "Pad:A");
        P(GameAction.Grenade, "Pad:LeftShoulder"); P(GameAction.Reload, "Pad:RightShoulder"); P(GameAction.Use, "Pad:B");
        P(GameAction.Weapon1, "Pad:DpadUp"); P(GameAction.Weapon2, "Pad:DpadRight"); P(GameAction.Weapon3, "Pad:DpadDown");
        P(GameAction.NextWeapon, null); P(GameAction.ZoomCycle, "Pad:RightStick"); P(GameAction.Scoreboard, "Pad:Back"); P(GameAction.Shout, null);
        return s;
    }

    public BindingSet Clone()
    {
        var c = new BindingSet();
        foreach (var (k, v) in _keyboard) c._keyboard[k] = (string?[])v.Clone();
        foreach (var (k, v) in _controller) c._controller[k] = (string?[])v.Clone();
        return c;
    }

    // ---- reading ----

    public string? Get(BindDevice device, GameAction action, int slot) =>
        Table(device).TryGetValue(action, out var slots) && slot >= 0 && slot < slots.Length ? slots[slot] : null;

    /// <summary>The inputs bound to an action on a device (empty when cleared).</summary>
    public IEnumerable<string> Inputs(BindDevice device, GameAction action) =>
        Table(device).TryGetValue(action, out var slots) ? slots.Where(x => x is not null).Select(x => x!) : Enumerable.Empty<string>();

    /// <summary>Every action an input is bound to on a device (normally at most one: <see cref="Assign"/> keeps it so).</summary>
    public IReadOnlyList<GameAction> ActionsFor(BindDevice device, string input) =>
        Table(device).Where(kv => kv.Value.Contains(input)).Select(kv => kv.Key).ToList();

    public bool IsDefault(BindDevice device)
    {
        var d = Defaults().Table(device);
        var t = Table(device);
        return d.All(kv => t.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value));
    }

    // ---- editing ----

    /// <summary>Why an input cannot be bound on a device (null: it can). Esc cancels the capture and opens the menu; F1 is the info overlay; F11 is fullscreen; Start is the controller's menu button.</summary>
    public static string? Reserved(BindDevice device, string input)
    {
        if (!ValidFor(device, input)) return "That input is not available on this device.";
        return input switch
        {
            "Key:Escape" => "Esc is the menu key and cancels a rebinding.",
            "Key:F1" => "F1 is the info overlay.",
            "Key:F11" => "F11 switches between fullscreen and the window.",
            "Pad:Start" => "Start is the game menu.",
            _ => null,
        };
    }

    public static bool ValidFor(BindDevice device, string? input)
    {
        if (string.IsNullOrEmpty(input) || input.Length > 40) return false;
        int colon = input.IndexOf(':');
        if (colon <= 0 || colon == input.Length - 1) return false;
        string kind = input[..colon];
        if (!input[(colon + 1)..].All(char.IsLetterOrDigit)) return false;
        return device == BindDevice.Keyboard ? kind is "Key" or "Mouse" : kind == "Pad";
    }

    /// <summary>The action (and slot) that already holds <paramref name="input"/>, other than this very slot; null when the input is free.</summary>
    public (GameAction Action, int Slot)? FindConflict(BindDevice device, GameAction action, int slot, string input)
    {
        foreach (var (a, slots) in Table(device))
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] == input && !(a == action && i == slot)) return (a, i);
        return null;
    }

    /// <summary>
    /// Binds <paramref name="input"/> to a slot. An input another slot holds is resolved by <paramref name="choice"/>: <see cref="ConflictChoice.Swap"/> gives that other slot this slot's old input,
    /// <see cref="ConflictChoice.Replace"/> clears it. The two slots of one action never hold the same input. Returns false when the input is reserved or not for the device.
    /// </summary>
    public bool Assign(BindDevice device, GameAction action, int slot, string input, ConflictChoice choice = ConflictChoice.Replace)
    {
        if (Reserved(device, input) is not null) return false;
        var table = Table(device);
        if (!table.TryGetValue(action, out var mine) || slot < 0 || slot >= mine.Length) return false;
        var old = mine[slot];
        if (FindConflict(device, action, slot, input) is { } other)
            table[other.Action][other.Slot] = choice == ConflictChoice.Swap ? old : null;
        mine[slot] = input;
        return true;
    }

    public void Clear(BindDevice device, GameAction action, int slot)
    {
        if (Table(device).TryGetValue(action, out var slots) && slot >= 0 && slot < slots.Length) slots[slot] = null;
    }

    public void ResetDevice(BindDevice device)
    {
        var d = Defaults().Table(device);
        var t = Table(device);
        t.Clear();
        foreach (var (k, v) in d) t[k] = (string?[])v.Clone();
    }

    // ---- sampling (the sim's input from the bindings) ----

    /// <summary>
    /// The sampled input of the bound actions: <paramref name="held"/> says whether any input of an action is down. The move axes come from the four move actions (X right, Y forward).
    /// Fire, Aim, Melee, Stun strike, Grenade and Next weapon act only while the mouse is captured (<paramref name="captured"/>), like before; the rest always. Use (Interact) has its own latch
    /// and is not set here. The sim's charge and directional strikes read these bits, so they follow the bound actions with no key of their own.
    /// </summary>
    public static SampledControls Sample(Func<GameAction, bool> held, bool captured, bool includeMove = true)
    {
        float x = 0f, y = 0f;
        if (includeMove)
        {
            x = (held(GameAction.MoveRight) ? 1f : 0f) - (held(GameAction.MoveLeft) ? 1f : 0f);
            y = (held(GameAction.MoveForward) ? 1f : 0f) - (held(GameAction.MoveBack) ? 1f : 0f);
        }
        var buttons = InputButtons.None;
        foreach (var info in ControlActions.All)
        {
            if (info.Buttons == InputButtons.None || info.Buttons == InputButtons.Interact) continue;
            if (info.NeedsCapture && !captured) continue;
            if (held(info.Action)) buttons |= info.Buttons;
        }
        return new SampledControls(x, y, buttons);
    }

    // ---- display ----

    /// <summary>A short readable name of an input for the screen.</summary>
    public static string Display(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "-";
        int colon = input.IndexOf(':');
        if (colon < 0) return input;
        string kind = input[..colon], name = input[(colon + 1)..];
        if (kind == "Mouse")
            return name switch { "Left" => "Left mouse", "Right" => "Right mouse", "Middle" => "Middle mouse", "Wheel" => "Mouse wheel", "WheelUp" => "Wheel up", "WheelDown" => "Wheel down", "Xbutton1" => "Mouse 4", "Xbutton2" => "Mouse 5", _ => "Mouse " + name };
        if (kind == "Pad")
            return name switch
            {
                "A" or "B" or "X" or "Y" => name, "LT" => "LT", "RT" => "RT", "LeftShoulder" => "LB", "RightShoulder" => "RB", "LeftStick" => "L3 (stick click)", "RightStick" => "R3 (stick click)",
                "DpadUp" => "D-pad up", "DpadDown" => "D-pad down", "DpadLeft" => "D-pad left", "DpadRight" => "D-pad right", "Back" => "Back", "Start" => "Start", "Guide" => "Guide",
                _ => name,
            };
        if (name.Length == 4 && name.StartsWith("Key", StringComparison.Ordinal) && char.IsDigit(name[3])) return name[3].ToString();
        if (name.StartsWith("Kp", StringComparison.Ordinal) && name.Length > 2) return "Num " + name[2..];
        return name switch
        {
            "Capslock" => "Caps Lock", "Bracketleft" => "[", "Bracketright" => "]", "Semicolon" => ";", "Apostrophe" => "'", "Comma" => ",", "Period" => ".", "Slash" => "/",
            "Backslash" => "\\", "Minus" => "-", "Equal" => "=", "Quoteleft" => "`", "Pageup" => "Page Up", "Pagedown" => "Page Down",
            _ => name,
        };
    }

    // ---- saving ----

    public string ToJson()
    {
        JsonObject Device(Dictionary<GameAction, string?[]> t)
        {
            var o = new JsonObject();
            foreach (var info in ControlActions.All)
                if (t.TryGetValue(info.Action, out var slots)) o[info.Action.ToString()] = new JsonArray(slots.Select(s => s is null ? null : JsonValue.Create(s)).ToArray<JsonNode?>());
            return o;
        }
        var root = new JsonObject { ["version"] = Version, ["keyboard"] = Device(_keyboard), ["controller"] = Device(_controller) };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Reads a saved set over the defaults. Anything unreadable falls back to the default for that action (<paramref name="notes"/> says what); an input saved twice in one device
    /// is kept for the first action only. Never throws.
    /// </summary>
    public static BindingSet FromJson(string? json, out List<string> notes)
    {
        notes = new List<string>();
        var set = Defaults();
        if (string.IsNullOrWhiteSpace(json)) return set;
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (Exception) { notes.Add("The controls file could not be read; the default controls are used."); return set; }
        if (root is not JsonObject obj) { notes.Add("The controls file could not be read; the default controls are used."); return set; }
        foreach (var device in new[] { BindDevice.Keyboard, BindDevice.Controller })
        {
            string key = device == BindDevice.Keyboard ? "keyboard" : "controller";
            if (obj[key] is not JsonObject saved) continue;
            var table = set.Table(device);
            foreach (var info in ControlActions.All)
            {
                if (!table.TryGetValue(info.Action, out var slots) || saved[info.Action.ToString()] is not JsonArray arr) continue;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (i >= arr.Count) { slots[i] = null; continue; }
                    string? value = arr[i] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                    if (value is not null && Reserved(device, value) is not null) { notes.Add($"{info.Label}: \"{value}\" is not a usable {key} input; cleared."); value = null; }
                    slots[i] = value;
                }
            }
            // An input twice in one device: the first (in the screen's order) keeps it.
            var seen = new HashSet<string>();
            foreach (var info in ControlActions.All)
                if (table.TryGetValue(info.Action, out var slots))
                    for (int i = 0; i < slots.Length; i++)
                        if (slots[i] is { } input && !seen.Add(input)) { notes.Add($"{Display(input)} was bound twice; kept for the first action only."); slots[i] = null; }
        }
        return set;
    }

    public static BindingSet Load(string directory, out List<string> notes)
    {
        notes = new List<string>();
        try
        {
            string path = Path.Combine(directory, FileName);
            return File.Exists(path) ? FromJson(File.ReadAllText(path), out notes) : Defaults();
        }
        catch (Exception)
        {
            notes.Add("The controls file could not be read; the default controls are used.");
            return Defaults();
        }
    }

    /// <summary>Writes the file (temp file, then rename). Returns an error text or null. A set equal to the defaults removes the file.</summary>
    public string? Save(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileName);
            if (IsDefault(BindDevice.Keyboard) && IsDefault(BindDevice.Controller)) { if (File.Exists(path)) File.Delete(path); return null; }
            string temp = path + ".tmp";
            File.WriteAllText(temp, ToJson());
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "Could not save the controls: " + e.Message; }
    }
}
