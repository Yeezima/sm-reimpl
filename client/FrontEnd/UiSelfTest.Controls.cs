using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmSim;

namespace SmClient.FrontEnd;

// ui/keybinds (the owner's request 2026-10-31): the CONTROLS screen, driven with synthetic key, mouse and pad events through the same input path a player's devices use.
public sealed partial class UiSelfTest
{
    async Task Seconds(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    async Task SendKey(Key key)
    {
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = true });
        await Frames(2);
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = false });
        await Frames(2);
    }

    async Task SendPad(JoyButton button)
    {
        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = button, Pressed = true });
        await Frames(2);
        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = button, Pressed = false });
        await Frames(2);
    }

    /// <summary>The prompt over the CONTROLS screen (a child of it), or null.</summary>
    UiKit.Dialog? ControlsDialog(ControlsView view) => view.GetChildren().OfType<UiKit.Dialog>().FirstOrDefault(d => !d.IsQueuedForDeletion());

    async Task<bool> AnswerControlsDialog(ControlsView view, string text)
    {
        var d = ControlsDialog(view);
        var b = d is null ? null : FindButton(d, x => x.Text == text);
        if (b is null) return false;
        b.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        return true;
    }

    /// <summary>Starts a capture and waits out the guard against the press that opened the entry.</summary>
    async Task Arm(ControlsView view, GameAction action, int slot)
    {
        view.BeginCapture(action, slot);
        await Seconds(0.25);
    }

    async Task ControlsChecks()
    {
        var controls = _front.Controls;
        Check("controls: the screen exists", controls is not null);
        if (controls is null) return;
        string dir = _services.SettingsDirectory;
        string file = Path.Combine(dir, BindingSet.FileName);
        var keptScreen = ControlsInput.Bindings.Clone();
        ControlsInput.Use(BindingSet.Defaults());
        const BindDevice KB = BindDevice.Keyboard, Pad = BindDevice.Controller;
        try
        {
            _front.ShowOptions(OptionsView.Controls);
            await Frames(4);
            var open = FindButton(_front.Options!, b => b.Name == "ControlsButton");
            Check("controls: OPTIONS, CONTROLS has a KEYBINDS row", open is not null);
            open?.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(4);
            Check("controls: it opens on the keyboard tab", controls.Visible && !_front.Options!.Visible && controls.Device == KB);
            bool listed = true;
            string missing = "";
            foreach (var info in ControlActions.For(KB, ContentExtras.Compiled))
                for (int slot = 0; slot < BindingSet.KeyboardSlots; slot++)
                {
                    var b = controls.BindButton(info.Action, slot);
                    string want = BindingSet.Display(BindingSet.Defaults().Get(KB, info.Action, slot));
                    if (b is null || b.Text != want) { listed = false; missing += $" {info.Action}/{slot}={b?.Text ?? "none"}(want {want})"; }
                }
            Check("controls: every keyboard action is listed with its default binding", listed, missing);

            // A free key.
            await Arm(controls, GameAction.Reload, 0);
            Check("controls: a click on an entry waits for the new input", controls.Capturing && controls.BindButton(GameAction.Reload, 0)?.Text.StartsWith("PRESS", StringComparison.Ordinal) == true);
            await SendKey(Key.Q);
            Check("controls: a key rebinds the entry", !controls.Capturing && controls.Working.Get(KB, GameAction.Reload, 0) == "Key:Q" && controls.BindButton(GameAction.Reload, 0)?.Text == "Q", controls.Status);
            Check("controls: the new binding is in use at once (the Godot InputMap)", ControlsInput.Bindings.Get(KB, GameAction.Reload, 0) == "Key:Q"
                && InputMap.ActionGetEvents("sm_reload").OfType<InputEventKey>().Any(k => k.PhysicalKeycode == Key.Q) && !InputMap.ActionGetEvents("sm_reload").OfType<InputEventKey>().Any(k => k.PhysicalKeycode == Key.R));
            Check("controls: the change is saved beside the profile", File.Exists(file) && BindingSet.Load(dir, out _).Get(KB, GameAction.Reload, 0) == "Key:Q");
            Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Q, Keycode = Key.Q, Pressed = true });
            await Frames(2);
            bool answers = ControlsInput.Held(GameAction.Reload);
            Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Q, Keycode = Key.Q, Pressed = false });
            await Frames(2);
            Check("controls: the engine action follows the new key", answers && !ControlsInput.Held(GameAction.Reload));

            // A conflict: Q is Reload's now. Cancel keeps both, Swap trades, Replace unbinds the other.
            await Arm(controls, GameAction.Grenade, 0);
            await SendKey(Key.Q);
            Check("controls: a used key asks Swap, Replace or Cancel", ControlsDialog(controls) is { } d && FindButton(d, b => b.Text == "Swap") is not null && FindButton(d, b => b.Text == "Replace") is not null
                && FindButton(d, b => b.Text == "Cancel") is not null && controls.Status.Contains("already used", StringComparison.Ordinal), controls.Status);
            await AnswerControlsDialog(controls, "Cancel");
            Check("controls: Cancel changes nothing", controls.Working.Get(KB, GameAction.Grenade, 0) == "Key:G" && controls.Working.Get(KB, GameAction.Reload, 0) == "Key:Q");
            await Arm(controls, GameAction.Grenade, 0);
            await SendKey(Key.Q);
            await AnswerControlsDialog(controls, "Swap");
            Check("controls: Swap trades the two inputs", controls.Working.Get(KB, GameAction.Grenade, 0) == "Key:Q" && controls.Working.Get(KB, GameAction.Reload, 0) == "Key:G", $"{controls.Working.Get(KB, GameAction.Grenade, 0)} / {controls.Working.Get(KB, GameAction.Reload, 0)}");
            await Arm(controls, GameAction.Aim, 0);
            await SendKey(Key.Q);
            await AnswerControlsDialog(controls, "Replace");
            Check("controls: Replace takes the key and leaves the other action unbound", controls.Working.Get(KB, GameAction.Aim, 0) == "Key:Q" && controls.Working.Get(KB, GameAction.Grenade, 0) is null
                && controls.BindButton(GameAction.Grenade, 0)?.Text == "-");

            // Esc cancels; F1 and Esc cannot be bound; a mouse button and the wheel can.
            await Arm(controls, GameAction.Jump, 0);
            await SendKey(Key.Escape);
            Check("controls: Esc cancels the capture and the screen stays", !controls.Capturing && controls.Visible && controls.Working.Get(KB, GameAction.Jump, 0) == "Key:Space");
            await Arm(controls, GameAction.Jump, 0);
            await SendKey(Key.F1);
            Check("controls: a reserved key is refused with a reason", !controls.Capturing && controls.Working.Get(KB, GameAction.Jump, 0) == "Key:Space" && controls.Status.Contains("F1", StringComparison.Ordinal), controls.Status);
            await Arm(controls, GameAction.Jump, 1);
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = true });
            await Frames(2);
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = false });
            await Frames(2);
            Check("controls: a mouse button binds", controls.Working.Get(KB, GameAction.Jump, 1) == "Mouse:Middle" && controls.BindButton(GameAction.Jump, 1)?.Text == "Middle mouse");
            await Arm(controls, GameAction.Weapon1, 1);
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Factor = 1f });
            await Frames(2);
            Check("controls: the wheel binds (either direction) and asks about Next weapon, which had it", ControlsDialog(controls) is not null);
            await AnswerControlsDialog(controls, "Replace");
            Check("controls: after Replace the wheel is Weapon 1's", controls.Working.Get(KB, GameAction.Weapon1, 1) == "Mouse:Wheel" && controls.Working.Get(KB, GameAction.NextWeapon, 1) is null);

            // Clearing: Delete on the focused entry.
            controls.BindButton(GameAction.Jump, 1)!.GrabFocus();
            await Frames(2);
            await SendKey(Key.Delete);
            Check("controls: Delete clears the focused entry", controls.Working.Get(KB, GameAction.Jump, 1) is null && controls.BindButton(GameAction.Jump, 1)?.Text == "-");

            // Reset to defaults asks first and puts the tab back.
            controls.ResetButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(3);
            Check("controls: RESET TO DEFAULTS asks first", ControlsDialog(controls) is not null && !controls.Working.IsDefault(KB));
            await AnswerControlsDialog(controls, "Cancel");
            Check("controls: Cancel keeps the bindings", !controls.Working.IsDefault(KB));
            controls.ResetButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(3);
            await AnswerControlsDialog(controls, "Reset");
            Check("controls: Reset puts the keyboard tab back and removes the file", controls.Working.IsDefault(KB) && ControlsInput.Bindings.IsDefault(KB) && !File.Exists(file), File.Exists(file) ? "file left" : "");

            // The controller tab.
            controls.TabController.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(4);
            Check("controls: the CONTROLLER tab lists the SM1 layout, one entry per action", controls.Device == Pad && controls.BindButton(GameAction.Fire, 0)?.Text == "RT" && controls.BindButton(GameAction.Aim, 0)?.Text == "LT"
                && controls.BindButton(GameAction.Melee, 0)?.Text == "X" && controls.BindButton(GameAction.StunStrike, 0)?.Text == "Y" && controls.BindButton(GameAction.Jump, 0)?.Text == "A"
                && controls.BindButton(GameAction.Fire, 1) is null && controls.BindButton(GameAction.MoveForward, 0) is null);
            await Arm(controls, GameAction.Jump, 0);
            await SendPad(JoyButton.Y);
            Check("controls: a pad button already used asks Swap, Replace or Cancel", ControlsDialog(controls) is not null);
            await AnswerControlsDialog(controls, "Swap");
            Check("controls: Swap trades the pad buttons", controls.Working.Get(Pad, GameAction.Jump, 0) == "Pad:Y" && controls.Working.Get(Pad, GameAction.StunStrike, 0) == "Pad:A");
            Check("controls: the pad layer reads the remapped buttons", ControlsInput.PadActions("Pad:Y").Contains(GameAction.Jump) && ControlsInput.PadActions("Pad:A").Contains(GameAction.StunStrike) && !ControlsInput.PadActions("Pad:Y").Contains(GameAction.StunStrike));
            await Arm(controls, GameAction.Reload, 0);
            Input.ParseInputEvent(new InputEventJoypadMotion { Axis = JoyAxis.TriggerLeft, AxisValue = 1f });
            await Frames(2);
            Input.ParseInputEvent(new InputEventJoypadMotion { Axis = JoyAxis.TriggerLeft, AxisValue = 0f });
            await Frames(2);
            await AnswerControlsDialog(controls, "Replace");
            Check("controls: a trigger binds like a button", controls.Working.Get(Pad, GameAction.Reload, 0) == "Pad:LT" && controls.Working.Get(Pad, GameAction.Aim, 0) is null);
            await Arm(controls, GameAction.Grenade, 0);
            await SendPad(JoyButton.Start);
            Check("controls: Start cancels a pad capture", !controls.Capturing && controls.Working.Get(Pad, GameAction.Grenade, 0) == "Pad:LeftShoulder");
            controls.BindButton(GameAction.Grenade, 0)!.GrabFocus();
            await Frames(2);
            await SendPad(JoyButton.X);
            Check("controls: X clears the focused pad entry", controls.Working.Get(Pad, GameAction.Grenade, 0) is null);
            await SendPad(JoyButton.LeftShoulder);
            Check("controls: LB switches back to the keyboard tab", controls.Device == KB);
            await SendPad(JoyButton.RightShoulder);
            Check("controls: RB switches to the controller tab", controls.Device == Pad);
            controls.ResetButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(3);
            await AnswerControlsDialog(controls, "Reset");
            Check("controls: Reset puts the controller tab back", controls.Working.IsDefault(Pad) && ControlsInput.Bindings.IsDefault(Pad) && !File.Exists(file));

            // Back (Esc) returns to OPTIONS.
            await SendKey(Key.Escape);
            Check("controls: Esc goes back to OPTIONS", !controls.Visible && _front.Options!.Visible);
        }
        finally
        {
            ControlsInput.Use(BindingSet.Defaults());
            controls.Close();
            _front.ShowMainMenu();
            await Frames(2);
        }
    }

    /// <summary>The CONTROLS screen at the window size now: both tabs lay out inside the window.</summary>
    async Task ControlsFitChecks(Rect2 view, string at)
    {
        var controls = _front.Controls!;
        _front.ShowControls();
        await Frames(4);
        CheckFits($"fit {at}: controls, keyboard and mouse tab", controls, view);
        Check($"fit {at}: controls list keeps a usable height", controls.List.Size.Y >= 200f, $"{controls.List.Size.Y:F0}");
        controls.Open(BindDevice.Controller);
        await Frames(4);
        CheckFits($"fit {at}: controls, controller tab", controls, view);
        controls.Open(BindDevice.Keyboard);
        await Frames(2);
        _front.ShowMainMenu();
    }
}
