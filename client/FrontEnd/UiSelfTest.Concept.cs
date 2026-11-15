using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;
using SmSim;

namespace SmClient.FrontEnd;

// ui/concept-redesign (the owner's four-panel concept, 2026-11-15), phase 1: the steel kit, the main menu in the concept's layout and OPTIONS in the concept's frame. The kit's styles and types,
// the menu's order, focus and navigation by key and by pad, OPTIONS' tabs, columns, APPLY / REVERT / BACK, the settings' persistence through them, and Back by Esc and by B.
public sealed partial class UiSelfTest
{
    async Task ConceptChecks()
    {
        var kept = _services.Settings;
        try
        {
            // ---- the kit ----
            var theme = UiKit.Theme;
            Check("concept: the theme has the steel types", new[] { Steel.ButtonType, Steel.TabType, Steel.RowType, Steel.SliderType, Steel.FrameType, Steel.AmberFrameType, Steel.PrimaryType }.All(t => theme.HasStylebox("normal", t) || theme.HasStylebox("panel", t) || theme.HasStylebox("slider", t)));
            bool Painted(string state, string type) => theme.GetStylebox(state, type) is StyleBoxTexture || theme.GetStylebox(state, type) is Steel.SteelStyle;
            Check("concept: a button is a painted (or drawn) plate in every state, its focus and pressed plates gold, a tab's focus a ring", new[] { "normal", "hover", "pressed", "focus", "disabled" }.All(st => Painted(st, Steel.ButtonType) && Painted(st, Steel.TabType))
                && (theme.GetStylebox("focus", Steel.ButtonType) is StyleBoxTexture { Texture: not null } || theme.GetStylebox("focus", Steel.ButtonType) is Steel.SteelStyle { Kind: Steel.Kind.Gold }) && theme.GetStylebox("focus", Steel.TabType) is Steel.SteelStyle { Kind: Steel.Kind.Rim });

            // ---- the main menu: keys and the pad ----
            _front.ShowMainMenu();
            await Frames(4);
            var main = _front.MainPage!;
            var rows = RowTexts(main);
            var firstText = rows[0];
            Check("concept: the main menu's first row has the focus (gold)", GetViewport().GuiGetFocusOwner() is Button { Text: var focused } focusButton && focused == firstText && focusButton.HasFocus(), GetViewport().GuiGetFocusOwner()?.Name ?? "none");
            await SendKey(Key.Down);
            Check("concept: Down moves the focus to the next row", GetViewport().GuiGetFocusOwner() is Button { Text: var second } && second == rows[1], (GetViewport().GuiGetFocusOwner() as Button)?.Text ?? "none");
            await SendPad(JoyButton.DpadDown);
            Check("concept: the pad's Down moves it on", GetViewport().GuiGetFocusOwner() is Button { Text: var third } && third == rows[2], (GetViewport().GuiGetFocusOwner() as Button)?.Text ?? "none");
            await SendKey(Key.Up);
            await SendPad(JoyButton.DpadUp);
            Check("concept: Up and the pad's Up move it back", GetViewport().GuiGetFocusOwner() is Button { Text: var back } && back == firstText, (GetViewport().GuiGetFocusOwner() as Button)?.Text ?? "none");

            // ---- OPTIONS: from the menu by its row (Enter), the frame, the tabs ----
            await PressRow("OPTIONS", main);
            var options = _front.Options!;
            await Frames(4);
            Check("concept: OPTIONS opens with the focus on its tab", options.Visible && GetViewport().GuiGetFocusOwner() is Button { Text: "GRAPHICS" } && options.Tab == OptionsView.Graphics, GetViewport().GuiGetFocusOwner()?.Name ?? "none");
            Check("concept: OPTIONS has four category tabs, the open one gold (pressed)", new[] { "GRAPHICS", "SOUND", "CONTROLS", "INTERFACE" }.All(t => FindButton(options, b => b.Text == t && b.ThemeTypeVariation == Steel.TabType) is not null)
                && FindButton(options, b => b.Text == "GRAPHICS")!.ButtonPressed && !FindButton(options, b => b.Text == "SOUND")!.ButtonPressed);
            Check("concept: the rows are in two columns on a wide window and every choice and slider wears the steel skin", options.Grid is GridContainer { Columns: 2 }
                && AllButtons(options.Grid).OfType<UiKit.MenuChoice>().All(c => c.IsSteel) && options.Grid.GetChildren().OfType<UiKit.MenuSlider>().Any());
            int graphicsRows = options.Grid.GetChildCount() + options.FindChild("OptionsWide", true, false)!.GetChildCount();
            Check("concept: GRAPHICS is the densest tab (12 rows and the renderer line, since the four interface rows moved to INTERFACE)", graphicsRows == 13, graphicsRows.ToString());
            await SendKey(Key.Right);
            Check("concept: Right on a tab moves to the next tab", GetViewport().GuiGetFocusOwner() is Button { Text: "SOUND" });
            await SendKey(Key.Left);
            Check("concept: Left comes back", GetViewport().GuiGetFocusOwner() is Button { Text: "GRAPHICS" });
            await SendKey(Key.Down);
            Check("concept: Down from a tab enters the rows", GetViewport().GuiGetFocusOwner() is { } inRow && options.Grid.IsAncestorOf(inRow), GetViewport().GuiGetFocusOwner()?.Name ?? "none");

            // ---- APPLY, REVERT: the options are live and saved at once; REVERT goes back to how the page was opened ----
            await PressRow("GRAPHICS", options);   // back to the page's first tab, as the player would
            var vsync = FindButton(options, b => b.Text == "VSYNC") as UiKit.MenuChoice;
            Check("concept: VSYNC is an OFF / ON switch", vsync is { IsSteel: true } && vsync.Values.SequenceEqual(["OFF", "ON"]));
            if (vsync is not null)
            {
                int was = vsync.Index;
                vsync.GrabFocus();
                await SendKey(Key.Enter);
                Check("concept: Enter on a row steps it, live and saved", vsync.Index != was && _services.Settings.Options.Vsync == (vsync.Index == 1) && FrontEndSettings.Load(_services.SettingsDirectory).Options.Vsync == (vsync.Index == 1));
                options.ApplyButton.EmitSignal(BaseButton.SignalName.Pressed);
                await Frames(2);
                Check("concept: APPLY says the settings are applied", options.Toast.Visible && options.Toast.Text.Contains("SETTINGS APPLIED", StringComparison.Ordinal), options.Toast.Text);
                Check("concept: ... and leaves them as they are, saved", _services.Settings.Options.Vsync == (vsync.Index == 1) && FrontEndSettings.Load(_services.SettingsDirectory).Options.Vsync == (vsync.Index == 1));
                int applied = vsync.Index;
                var again = FindButton(options, b => b.Text == "VSYNC") as UiKit.MenuChoice;
                again!.Step(1);
                Check("concept: a further change is saved at once, before any APPLY", _services.Settings.Options.Vsync == (again.Index == 1) && again.Index != applied);
                options.RevertButton.EmitSignal(BaseButton.SignalName.Pressed);
                await Frames(3);
                var after = FindButton(options, b => b.Text == "VSYNC") as UiKit.MenuChoice;
                Check("concept: REVERT puts it back to the last APPLY, shown and saved", after is not null && after.Index == applied && _services.Settings.Options.Vsync == (applied == 1)
                    && FrontEndSettings.Load(_services.SettingsDirectory).Options.Vsync == (applied == 1), $"row {after?.Index} applied {applied}");
                Check("concept: REVERT says so and gives the focus to APPLY", options.Toast.Text.Contains("REVERTED", StringComparison.Ordinal) && options.ApplyButton.HasFocus(), options.Toast.Text);
            }
            // A slider and a Settings-level option (not in GameOptions) revert too (on INTERFACE, where they live since 2026-11-15).
            options.ShowTab(OptionsView.Interface);
            await Frames(3);
            Check("concept: INTERFACE has the four moved rows and GRAPHICS no longer has them", new[] { "REDUCE MOTION", "SHOW OTHERS' EMBLEMS" }.All(t => FindButton(options, b => b.Text == t) is UiKit.MenuChoice)
                && options.FindChild("CameraShakeSlider", true, false) is HSlider && options.FindChild("ScreenFlashesSlider", true, false) is HSlider);
            var emblems = FindButton(options, b => b.Text == "SHOW OTHERS' EMBLEMS") as UiKit.MenuChoice;
            bool emblemsWere = _services.Settings.ShowOthersEmblems;
            emblems?.Step(1);
            var shake = options.FindChild("CameraShakeSlider", true, false) as HSlider;
            double shakeWas = shake?.Value ?? 1;
            if (shake is not null) shake.Value = 0.4;
            Check("concept: the Settings-level and slider changes land", _services.Settings.ShowOthersEmblems != emblemsWere && Mathf.IsEqualApprox(_services.Settings.Options.CameraShake, 0.4f));
            options.RevertButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(3);
            Check("concept: REVERT keeps the tab it was on", options.Tab == OptionsView.Interface);
            Check("concept: REVERT undoes both", _services.Settings.ShowOthersEmblems == emblemsWere && Mathf.IsEqualApprox(_services.Settings.Options.CameraShake, (float)shakeWas)
                && FrontEndSettings.Load(_services.SettingsDirectory).ShowOthersEmblems == emblemsWere, $"emblems {_services.Settings.ShowOthersEmblems} shake {_services.Settings.Options.CameraShake}");

            // Every label is whole in its column, on every tab (a label under the slider's track was cut, 2026-11-15).
            foreach (var tab in new[] { OptionsView.Graphics, OptionsView.Sound, OptionsView.Controls, OptionsView.Interface })
            {
                options.ShowTab(tab);
                await Frames(3);
                // Measured at the kit's base sizes (a headless window is tiny, so the theme's size, raised to stay readable there, is not the one at 1080p).
                var cut = new System.Collections.Generic.List<string>();
                float Need(string text) => UiKit.Font.GetStringSize(text, HorizontalAlignment.Left, -1, Steel.RowSize).X;
                foreach (var l in AllLabels(options.Grid).Where(x => x.IsVisibleInTree() && x.ThemeTypeVariation == Steel.LabelType && x.GetParent() is UiKit.MenuSlider && !x.Name.ToString().EndsWith("Value", StringComparison.Ordinal)))
                    if (Need(l.Text) > OptionsView.LabelWidth - 8f) cut.Add($"{l.Text} {Need(l.Text):F0}");
                foreach (var b in AllButtons(options.Grid).OfType<UiKit.MenuChoice>().Where(x => x.IsVisibleInTree()))
                    if (Need(b.Text) > OptionsView.LabelWidth - 8f) cut.Add($"{b.Text} {Need(b.Text):F0}");
                Check($"concept: every label on {tab} is whole in its column", cut.Count == 0, string.Join(", ", cut));
            }
            options.ShowTab(OptionsView.Graphics);
            await Frames(2);

            // ---- the other tabs ----
            options.ShowTab(OptionsView.Sound);
            await Frames(3);
            Check("concept: SOUND shows its four sliders and the mute switch in the steel skin", new[] { "MasterVolumeSlider", "MusicVolumeSlider", "EffectsVolumeSlider", "VoiceVolumeSlider" }.All(n => options.FindChild(n, true, false) is HSlider { ThemeTypeVariation: var t } && t == Steel.SliderType)
                && FindButton(options, b => b.Text == "MUTE WHEN UNFOCUSED") is UiKit.MenuChoice { IsSteel: true });
            options.ShowTab(OptionsView.Controls);
            await Frames(3);
            Check("concept: CONTROLS keeps KEYBINDS as a steel button under the rows", FindButton(options, b => b.Name == "ControlsButton") is { } keybinds && keybinds.ThemeTypeVariation == Steel.ButtonType && !options.Grid.IsAncestorOf(keybinds));

            // ---- Back: BACK, Esc and B all leave for the main menu ----
            options.ApplyButton.GrabFocus();
            options.BackButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(3);
            Check("concept: BACK leaves OPTIONS for the main menu", !options.Visible && _front.ShowsMainMenu);
            _front.ShowOptions(OptionsView.Graphics);
            await Frames(3);
            await SendKey(Key.Escape);
            Check("concept: Esc leaves OPTIONS for the main menu", !options.Visible && _front.ShowsMainMenu);
            _front.ShowOptions(OptionsView.Graphics);
            await Frames(3);
            await SendPad(JoyButton.B);
            Check("concept: B on the pad leaves OPTIONS for the main menu", !options.Visible && _front.ShowsMainMenu);
        }
        finally
        {
            _services.UpdateSettings(_ => kept);
            _front.ShowMainMenu();
            await Frames(2);
        }
    }
}
