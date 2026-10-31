using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmSim;

namespace SmClient.FrontEnd;

// ui/pad-b-back (the owner's request 2026-11-01): B on the controller closes the open menu or goes back one level, like Esc does. Synthetic joypad events (the same path a pad takes);
// the match side (no Use from the B that closes the Esc menu) is checked by the --pad-test run (NetworkView.GamepadTest.cs).
public sealed partial class UiSelfTest
{
    async Task PadBackChecks()
    {
        var controls = _front.Controls;
        Check("pad back: the controls screen exists", controls is not null);
        if (controls is null) return;
        var keptScreen = ControlsInput.Bindings.Clone();
        ControlsInput.Use(BindingSet.Defaults());
        try
        {
            // The keybind screen: B goes back to OPTIONS.
            _front.ShowControls();
            await Frames(4);
            Check("pad back: the controls screen is open", controls.Visible && !_front.ShowsMainMenu);
            await SendPad(JoyButton.B);
            Check("pad back: B leaves the controls screen for OPTIONS", !controls.Visible && _front.Options!.Visible);

            // While waiting for a rebind input: B is bindable on the controller tab (it asks about its current Use), and cancels like Esc on the keyboard tab.
            _front.ShowControls();
            await Frames(4);
            controls.Open(BindDevice.Controller);
            await Frames(3);
            await Arm(controls, GameAction.Jump, 0);
            await SendPad(JoyButton.B);
            Check("pad back: B is bound, not 'back', while a controller entry waits for an input", controls.Visible && !controls.Capturing && ControlsDialog(controls) is not null, controls.Status);
            await AnswerControlsDialog(controls, "Cancel");
            Check("pad back: Cancel on that prompt leaves the bindings", controls.Visible && controls.Working.IsDefault(BindDevice.Controller));
            controls.Open(BindDevice.Keyboard);
            await Frames(3);
            await Arm(controls, GameAction.Jump, 0);
            await SendPad(JoyButton.B);
            Check("pad back: B cancels a keyboard capture like Esc and the screen stays", !controls.Capturing && controls.Visible && controls.Working.IsDefault(BindDevice.Keyboard));
            await SendPad(JoyButton.B);
            Check("pad back: B again (nothing waiting) leaves the screen", !controls.Visible && _front.Options!.Visible);

            // The loadout screen (the in-match menu is this screen): B goes back like its BACK button (a plain leave or the unsaved-changes question).
            var creator = _front.Creator!;
            _services.Session!.Save();
            _front.ShowCreator();
            await Frames(4);
            Check("pad back: the loadout screen is open", creator.Visible);
            await SendPad(JoyButton.B);
            Check("pad back: B backs out of the loadout screen to the main menu", !creator.Visible && _front.ShowsMainMenu);
            _front.ShowCreator();
            await Frames(4);

            // Confirm dialogs, and the prompt with a text field: B picks the cancel button (the last) and nothing else closes.
            int chosen = -1;
            UiKit.Dialog.Open(creator, _front, "Pad back", "Cancel with B.", ["Yes", "No", "Cancel"], i => chosen = i);
            await Frames(3);
            await SendPad(JoyButton.B);
            Check("pad back: B answers a confirm dialog with its cancel button", chosen == 2 && creator.Visible, $"chosen {chosen}");
            int promptChoice = -1;
            UiKit.Dialog.Prompt(creator, _front, "Pad back", "Cancel with B.", "x", ["OK", "Cancel"], (i, _) => promptChoice = i);
            await Frames(3);
            await SendPad(JoyButton.B);
            Check("pad back: B cancels a text prompt", promptChoice == 1 && creator.Visible, $"chosen {promptChoice}");

            // The appearance editor: B returns to the loadouts.
            _front.ShowAppearance();
            await Frames(4);
            var appearance = _front.Appearance!;
            if (appearance.Visible)
            {
                await SendPad(JoyButton.B);
                Check("pad back: B leaves the appearance editor for the loadouts", !appearance.Visible && creator.Visible);
            }
            else Check("pad back: the appearance editor opened", false);
        }
        finally
        {
            ControlsInput.Use(keptScreen);
            controls.Close();
            _front.ShowMainMenu();
            await Frames(2);
        }
    }
}
