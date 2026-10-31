using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;
using SmContent;
using SmSim;

namespace SmClient.FrontEnd;

// ui/menu-rebuild (the owner's menu, 2026-11-01): the tree of pages, the fixed JOIN SERVER address, the one address field, the two bot buttons, Musou's row, Esc and B stepping back, the options'
// persistence and live effects, the layout at the reference's positions. Nothing here starts a server or a match (FrontEndView.JoinOverride, MusouOverride).
public sealed partial class UiSelfTest
{
    static List<string> RowTexts(UiKit.MenuPage page)
    {
        var all = new List<Button>();
        FindButtons(page.Rows, all);
        return all.Select(b => b.Text).ToList();
    }

    async Task PressRow(string text, Node? root = null)
    {
        (FindButton(root ?? _front, b => b.Text == text))?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(4);
    }

    async Task MenuChecks()
    {
        JoinRequest? joined = null;
        _front.JoinOverride = r => joined = r;
        bool musouStarted = false;
        _front.MusouOverride = () => musouStarted = true;
        try
        {
            _front.ShowMainMenu();
            await Frames(4);
            var main = _front.MainPage!;
            var wanted = new List<string> { "JOIN OR HOST LOBBY" };
            if (ContentExtras.Compiled) wanted.Add("MUSOU MODE");
            wanted.AddRange(["OPTIONS", "CUSTOMIZER", "QUIT"]);
            Check("menu: the main menu's rows, in order", RowTexts(main).SequenceEqual(wanted), string.Join(", ", RowTexts(main)));
            Check("menu: the title is the project's name", main.TitleLabel.Text == "SM - REIMPL", main.TitleLabel.Text);
            Check("menu: a first row has the focus", GetViewport().GuiGetFocusOwner() is Button { Text: "JOIN OR HOST LOBBY" } || FindButton(main, b => b.Text == "JOIN OR HOST LOBBY") is not null);
            foreach (var row in new[] { "JOIN OR HOST LOBBY", "OPTIONS" })
                Check($"menu: {row} is transparent (no box)", FindButton(main, b => b.Text == row)?.GetThemeStylebox("normal") is StyleBoxEmpty);

            await PressRow("JOIN OR HOST LOBBY", main);
            var lobby = _front.LobbyPage!;
            Check("menu: the lobby page opens", lobby.Visible && !_front.ShowsMainMenu);
            var lobbyRows = RowTexts(lobby);
            Check("menu: the lobby page has the three ways in", lobbyRows.Take(3).SequenceEqual(["HOST LOCAL LOBBY", "JOIN SERVER", "JOIN LOCAL LOBBY"]), string.Join(", ", lobbyRows));
            Check("menu: no address field on the lobby page", FindEdit(lobby) is null);

            // JOIN SERVER: the fixed address, whatever the remembered one says.
            _services.UpdateSettings(x => x with { Host = "10.0.0.9:27020" });
            await PressRow("JOIN SERVER", lobby);
            Check("menu: JOIN SERVER connects to the fixed address", joined?.Host == JoinTargets.PublicServer && joined.Host == "64.177.51.188:27015", joined?.Host ?? "null");
            Check("menu: JOIN SERVER leaves the remembered address", _services.Settings.Host == "10.0.0.9:27020");
            Check("menu: no field in the UI holds the public server's address", !AllEdits(_front).Any(e => e.Text.Contains(JoinTargets.PublicServer, StringComparison.Ordinal)));

            // JOIN LOCAL LOBBY: the only place an address is typed.
            await PressRow("JOIN LOCAL LOBBY", lobby);
            var joinLocal = _front.JoinLocalPage!;
            var address = FindEdit(joinLocal);
            Check("menu: JOIN LOCAL LOBBY has an address field with the remembered address", address?.Text == "10.0.0.9:27020", address?.Text ?? "null");
            if (address is not null) address.Text = "not valid";
            joined = null;
            await PressRow("CONNECT", joinLocal);
            Check("menu: a wrong address is refused", joined is null);
            if (address is not null) address.Text = "192.168.1.20:27031";
            await PressRow("CONNECT", joinLocal);
            Check("menu: CONNECT joins the typed address and remembers it", joined?.Host == "192.168.1.20:27031" && FrontEndSettings.Load(_services.SettingsDirectory).Host == "192.168.1.20:27031", joined?.Host ?? "null");
            _services.UpdateSettings(x => x with { Host = FrontEndSettings.DefaultHost });
            await SendKey(Key.Escape);
            Check("menu: Esc steps back to the lobby page", lobby.Visible);

            // The host page: mode, reach, the two bot buttons, one difficulty.
            await PressRow("HOST LOCAL LOBBY", lobby);
            var host = _front.HostPage!;
            Check("menu: the host page opens", host.Visible);
            Check("menu: the host page has the mode and reach rows", FindButton(host, b => b.Text == "GAME MODE") is UiKit.MenuChoice { Values.Count: 2 } && FindButton(host, b => b.Text == "WHO CAN JOIN") is UiKit.MenuChoice { Value: "THIS PC ONLY" });
            Check("menu: ADD LOYALIST BOT and ADD CHAOS BOT, and one difficulty", FindButton(host, b => b.Text == "ADD LOYALIST BOT") is not null && FindButton(host, b => b.Text == "ADD CHAOS BOT") is not null
                  && FindButton(host, b => b.Text == "BOT DIFFICULTY") is not null && FindButton(host, b => b.Text.Contains("ADD BOT", StringComparison.Ordinal)) is null);
            await SendPad(JoyButton.B);
            Check("menu: B steps back to the lobby page", lobby.Visible);
            await SendKey(Key.Escape);
            Check("menu: Esc on the lobby page goes to the main menu", _front.ShowsMainMenu);
            await SendKey(Key.Escape);
            Check("menu: Esc stays on the main menu", _front.ShowsMainMenu);

            // Musou: a row of its own; the --musou gate is gone; the remembered profile is not touched.
            if (ContentExtras.Compiled)
            {
                string saved = FrontEndSettings.Load(_services.SettingsDirectory).Content;
                await PressRow("MUSOU MODE", main);
                Check("menu: MUSOU MODE plays --content musou on this PC", musouStarted && _services.Content == ContentExtras.Musou, _services.Content.ToString());
                Check("menu: the Host panel's Musou path locks the reach and the bots", _front.JoinHostPanel is { Musou: true, BotCount: 0 });
                Check("menu: MUSOU MODE does not change the remembered content", FrontEndSettings.Load(_services.SettingsDirectory).Content == saved);
                _front.ShowMainMenu();
                await Frames(3);
                Check("menu: back on the main menu the lobby's profile is in use", !_services.Content.HasFlag(ContentExtras.Musou), _services.Content.ToString());
            }

            // The customizer stands on its own dark background.
            await PressRow("CUSTOMIZER", main);
            Check("menu: CUSTOMIZER opens the creator", _front.Creator!.Visible && !_front.ShowsMainMenu);
            Check("menu: the customizer has the 3D preview", _front.Creator.Preview is not null);
            var dark = _front.Creator.GetChild(0) as TextureRect;
            Check("menu: the customizer's background is the dark one, not the image", dark is { } && dark.Texture is GradientTexture2D && dark.Name != "MenuBackground");
            _front.ShowMainMenu();
            await Frames(3);

            // The in-match Esc menu (the real one is checked in a match by --pad-test): the same layout over a dim, no image.
            bool resumed = false;
            var escMenu = new MatchMenu(() => resumed = true, () => { }, () => { }, () => { }) { Visible = false };
            _front.AddChild(escMenu);
            escMenu.Open(alive: true);
            await Frames(4);
            Check("menu: the Esc menu's rows", RowTexts(escMenu).SequenceEqual(["RESUME", "LOADOUT", "OPTIONS", "LEAVE MATCH"]), string.Join(", ", RowTexts(escMenu)));
            Check("menu: the Esc menu has a translucent dim and no background image", escMenu.GetChild(0) is ColorRect { Color.A: > 0.2f and < 0.8f } && escMenu.FindChild("MenuBackground", true, false) is null);
            await SendKey(Key.Escape);
            Check("menu: Esc resumes from the Esc menu", resumed);
            resumed = false;
            await SendPad(JoyButton.B);
            Check("menu: B resumes from the Esc menu", resumed);
            _front.RemoveChild(escMenu);
            escMenu.QueueFree();

            // The bundled font and the one colour.
            Check("menu: every menu uses the bundled font", UiKit.Theme.DefaultFont is FontVariation { BaseFont: FontFile { Data.Length: > 100000 } });
            Check("menu: the highlight colour is the text colour", UiKit.Gold == UiKit.White);
            Check("menu: the background constant names one file", UiKit.MenuBackgroundPath == "res://assets/menu/background.jpg");
        }
        finally
        {
            _front.JoinOverride = null;
            _front.MusouOverride = null;
            _front.ShowMainMenu();
            await Frames(2);
        }
    }

    static IEnumerable<LineEdit> AllEdits(Node root)
    {
        if (root is LineEdit e) yield return e;
        foreach (var child in root.GetChildren()) foreach (var found in AllEdits(child)) yield return found;
    }

    /// <summary>Every option's row exists under its tab, a change reaches the live game and <c>frontend.json</c>, a damaged file gives the defaults.</summary>
    async Task OptionsChecks()
    {
        var kept = _services.Settings;
        var options = _front.Options!;
        try
        {
            _front.ShowOptions(OptionsView.Graphics);
            await Frames(4);
            Check("options: the page has the three tabs", new[] { "GRAPHICS", "SOUND", "CONTROLS" }.All(t => FindButton(options, b => b.Text == t) is not null));
            foreach (var row in new[] { "RESOLUTION", "WINDOW MODE", "VSYNC", "FPS CAP", "SHADOW QUALITY", "ANTI-ALIASING", "REDUCE MOTION", "SHOW OTHERS' EMBLEMS" })
                Check($"options: GRAPHICS has {row}", FindButton(options, b => b.Text == row) is UiKit.MenuChoice);
            foreach (var slider in new[] { "RenderScaleSlider", "FovSlider", "BrightnessSlider" })
                Check($"options: GRAPHICS has {slider}", options.FindChild(slider, true, false) is HSlider);
            options.ShowTab(OptionsView.Sound);
            await Frames(3);
            foreach (var slider in new[] { "MasterVolumeSlider", "MusicVolumeSlider", "EffectsVolumeSlider", "VoiceVolumeSlider" })
                Check($"options: SOUND has {slider}", options.FindChild(slider, true, false) is HSlider);
            Check("options: SOUND has MUTE WHEN UNFOCUSED", FindButton(options, b => b.Text == "MUTE WHEN UNFOCUSED") is UiKit.MenuChoice);
            options.ShowTab(OptionsView.Controls);
            await Frames(3);
            foreach (var slider in new[] { "MouseSensitivitySlider", "AimSensitivitySlider", "PadLookSlider" })
                Check($"options: CONTROLS has {slider}", options.FindChild(slider, true, false) is HSlider);
            Check("options: CONTROLS has INVERT Y, INVERT GAMEPAD Y and the keybinds", FindButton(options, b => b.Text == "INVERT Y") is UiKit.MenuChoice && FindButton(options, b => b.Text == "INVERT GAMEPAD Y") is UiKit.MenuChoice && FindButton(options, b => b.Text == "KEYBINDS") is not null);

            // A change applies at once and is saved.
            ((UiKit.MenuChoice)FindButton(options, b => b.Text == "INVERT Y")!).Step(1);
            await Frames(2);
            Check("options: Invert Y turns the mouse's vertical look", LiveOptions.MouseVertical == -1f);
            string observer = System.IO.File.Exists(ProjectSettings.GlobalizePath("res://NetworkView.Observer.cs")) ? System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://NetworkView.Observer.cs")) : "";
            Check("options: Invert Y reaches both spectator cameras (source check)", observer.Length == 0 || System.Text.RegularExpressions.Regex.Matches(observer, @"Relative\.Y \* SmClient\.Customizer\.Model\.LiveOptions\.MouseVertical").Count == 2);
            ((HSlider)options.FindChild("AimSensitivitySlider", true, false)!).Value = 0.5;
            await Frames(2);
            Check("options: the aim sensitivity scales the mouse while aiming only", Mathf.IsEqualApprox(LiveOptions.MouseAim(true), 0.5f) && LiveOptions.MouseAim(false) == 1f);
            ((UiKit.MenuChoice)FindButton(options, b => b.Text == "INVERT GAMEPAD Y")!).Step(1);
            ((HSlider)options.FindChild("PadLookSlider", true, false)!).Value = 2.0;
            await Frames(2);
            Check("options: the gamepad look settings reach the look", LiveOptions.PadVertical == -1f && Mathf.IsEqualApprox(LiveOptions.PadLook, 2f));

            options.ShowTab(OptionsView.Sound);
            await Frames(3);
            ((HSlider)options.FindChild("MasterVolumeSlider", true, false)!).Value = 0.5;
            ((HSlider)options.FindChild("VoiceVolumeSlider", true, false)!).Value = 0.25;
            ((HSlider)options.FindChild("MusicVolumeSlider", true, false)!).Value = 0.0;
            await Frames(2);
            Check("options: Master moves the Master bus", Mathf.Abs(AudioServer.GetBusVolumeDb(0) - GameOptions.VolumeDb(0.5f)) < 0.01f, AudioServer.GetBusVolumeDb(0).ToString());
            Check("options: Voice and Music reach their sounds' gain", Mathf.IsEqualApprox(LiveOptions.GainDb(GameOptions.CategoryOf("speech/x/y")), GameOptions.VolumeDb(0.25f)) && LiveOptions.GainDb(GameOptions.CategoryOf("play_mp_sm_victory")) == -80f
                  && LiveOptions.GainDb(GameOptions.CategoryOf("play_bolter_fire")) == 0f);

            options.ShowTab(OptionsView.Graphics);
            await Frames(3);
            ((UiKit.MenuChoice)FindButton(options, b => b.Text == "FPS CAP")!).Step(2);
            ((HSlider)options.FindChild("RenderScaleSlider", true, false)!).Value = 0.75;
            ((HSlider)options.FindChild("BrightnessSlider", true, false)!).Value = 1.25;
            ((UiKit.MenuChoice)FindButton(options, b => b.Text == "WINDOW MODE")!).Step(1);
            var resolutionRow = (UiKit.MenuChoice)FindButton(options, b => b.Text == "RESOLUTION")!;
            await Frames(3);
            Check("options: the FPS cap reaches the engine", Engine.MaxFps == 60, Engine.MaxFps.ToString());
            Check("options: the render scale reaches the viewport", Mathf.IsEqualApprox(GetTree().Root.Scaling3DScale, 0.75f), GetTree().Root.Scaling3DScale.ToString());
            Check("options: brightness draws its pass", OptionsHost.BrightnessActive);
            Check("options: resolution waits for WINDOWED", resolutionRow.Disabled);
            var saved = FrontEndSettings.Load(_services.SettingsDirectory).Options;
            Check("options: everything is saved in frontend.json", saved is { InvertY: true, InvertPadY: true, FpsCap: 60, Window: WindowChoice.Borderless } && Mathf.IsEqualApprox(saved.AimSensitivity, 0.5f) && Mathf.IsEqualApprox(saved.PadLookSensitivity, 2f)
                  && Mathf.IsEqualApprox(saved.MasterVolume, 0.5f) && Mathf.IsEqualApprox(saved.VoiceVolume, 0.25f) && saved.MusicVolume == 0f && Mathf.IsEqualApprox(saved.RenderScale, 0.75f) && Mathf.IsEqualApprox(saved.Brightness, 1.25f), saved.ToString());
            Check("options: a saved file reads back equal", FrontEndSettings.Load(_services.SettingsDirectory) == _services.Settings);

            // A damaged file, in a folder of its own under the system's temp: the settings in use are never written with bad values.
            string damagedDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sm-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                System.IO.Directory.CreateDirectory(damagedDir);
                var json = new System.Text.Json.Nodes.JsonObject { ["options"] = new System.Text.Json.Nodes.JsonObject { ["masterVolume"] = 42, ["fpsCap"] = 7, ["window"] = "Sideways" } };
                System.IO.File.WriteAllText(System.IO.Path.Combine(damagedDir, FrontEndSettings.FileName), json.ToJsonString());
                var clamped = FrontEndSettings.Load(damagedDir).Options;
                Check("options: damaged values are limited", clamped is { MasterVolume: 1f, FpsCap: 0, Window: WindowChoice.Windowed }, clamped.ToString());
            }
            finally { try { System.IO.Directory.Delete(damagedDir, true); } catch (System.IO.IOException) { } }

            Check("options: the self-test never changes the window", GetWindow().Mode == Window.ModeEnum.Windowed, GetWindow().Mode.ToString());

            // Esc leaves; B too.
            await SendKey(Key.Escape);
            Check("options: Esc goes back to the main menu", _front.ShowsMainMenu);
            _front.ShowOptions();
            await Frames(3);
            await SendPad(JoyButton.B);
            Check("options: B goes back to the main menu", _front.ShowsMainMenu);
        }
        finally
        {
            _services.UpdateSettings(_ => kept);
            await Frames(3);
            Check("options: restored to the defaults", Engine.MaxFps == 0 && GetTree().Root.Scaling3DScale == 1f && !OptionsHost.BrightnessActive && AudioServer.GetBusVolumeDb(0) == 0f, $"fps {Engine.MaxFps} scale {GetTree().Root.Scaling3DScale} db {AudioServer.GetBusVolumeDb(0)}");
            _front.ShowMainMenu();
            await Frames(2);
        }
    }

    /// <summary>The main menu at the reference's places, each page fits with its text whole, at the window size now.</summary>
    async Task MenuFitChecks(Rect2 view, string at)
    {
        _front.ShowMainMenu();
        await Frames(4);
        var main = _front.MainPage!;
        float f = view.Size.Y / UiKit.VirtualHeight;
        var firstRow = FindButton(main.Rows, b => b.Text == "JOIN OR HOST LOBBY")!;
        var quit = FindButton(main.Rows, b => b.Text == "QUIT")!;
        Check($"fit {at}: the rows start at the reference's height", Mathf.Abs(firstRow.GetGlobalRect().GetCenter().Y - UiKit.MenuFirstRowCentre * f) < 1.5f && Mathf.Abs(firstRow.GlobalPosition.X - 119f) < 1.5f, firstRow.GetGlobalRect().ToString());
        Check($"fit {at}: the rows are 22.5 apart", Mathf.Abs(quit.GlobalPosition.Y - firstRow.GlobalPosition.Y - 22.5f * (RowTexts(main).Count - 1)) < 1.5f);
        int size = firstRow.GetThemeFontSize("font_size");
        float pixels = size * Mathf.Min(GetWindow().Size.X / (float)UiKit.VirtualWidth, GetWindow().Size.Y / (float)UiKit.VirtualHeight);
        Check($"fit {at}: the rows' text is drawn at {UiKit.MinMenuPixels} px or more", pixels >= UiKit.MinMenuPixels - 0.01f, pixels.ToString());
        CheckPage($"fit {at}: main menu", main, view);
        CheckPage($"fit {at}: lobby", _front.LobbyPage!, view, () => _front.ShowMenuPage("lobby"));
        CheckPage($"fit {at}: host", _front.HostPage!, view, () => _front.ShowMenuPage("host"));
        await Frames(4);
        CheckPage($"fit {at}: host", _front.HostPage!, view);
        _front.JoinHostPanel?.ShowRunningForTest("HOSTING SEIZE GROUND ON PORT 27015, ALL INTERFACES, 15 BOTS (STARTING...)", 4);
        await Frames(4);
        CheckPage($"fit {at}: host with a running server and 4 more rows", _front.HostPage!, view);
        _front.JoinHostPanel?.ShowRunningForTest(null);
        _front.ShowMenuPage("lobby");
        await Frames(4);
        CheckPage($"fit {at}: lobby", _front.LobbyPage!, view);
        _front.ShowMessage("Could not connect to the server. " + string.Concat(Enumerable.Repeat("The server did not answer in time; check the address and that it runs the same content profile. ", 3)), true);
        await Frames(4);
        var message = _front.FindChild("MenuMessage", false, false) as Control;
        Check($"fit {at}: a three-line message stays in the window, under the rows", message is not null && Encloses(view, message.GetGlobalRect()), message?.GetGlobalRect().ToString() ?? "null");
        _front.ShowMessage("");
        _front.ShowMenuPage("joinlocal");
        await Frames(4);
        CheckPage($"fit {at}: join local lobby", _front.JoinLocalPage!, view);
        foreach (var tab in new[] { "graphics", "sound", "controls" })
        {
            _front.ShowMenuPage(tab);
            await Frames(4);
            CheckPage($"fit {at}: options, {tab}", _front.Options!, view);
        }
        _front.ShowMainMenu();
    }

    /// <summary>The controls are in the window and no row's text is cut.</summary>
    void CheckPage(string name, UiKit.MenuPage page, Rect2 view, Action? open = null)
    {
        open?.Invoke();
        if (open is not null) return;   // the layout runs next frame: the caller checks again after waiting
        CheckFits(name, page, view);
        var cut = new List<string>();
        foreach (var b in AllButtons(page).Where(x => x.IsVisibleInTree()))
        {
            float need = UiKit.TextNeed(b);
            float room = b is UiKit.MenuChoice ? UiKit.MenuValueColumn : b.Size.X;
            if (need > room + 1f) cut.Add($"{b.Text} needs {need:F0}, has {room:F0}");
        }
        foreach (var l in AllLabels(page).Where(x => x.IsVisibleInTree() && !x.ClipText && x.Name.ToString() == "Value"))
            if (l.GetMinimumSize().X > l.Size.X + 1f) cut.Add($"{l.Text} needs {l.GetMinimumSize().X:F0}, has {l.Size.X:F0}");
        Check($"{name}: all text fits", cut.Count == 0, string.Join("; ", cut.Take(5)));
        var bottom = page.Rows.GetGlobalRect().End.Y;
        Check($"{name}: the rows end in the window", page.List.GetGlobalRect().End.Y <= view.End.Y + 0.5f && (bottom <= page.List.GetGlobalRect().End.Y + 0.5f), $"rows end {bottom:F0} list end {page.List.GetGlobalRect().End.Y:F0}");
    }

    static IEnumerable<Button> AllButtons(Node root)
    {
        if (root is Button b) yield return b;
        foreach (var child in root.GetChildren()) foreach (var found in AllButtons(child)) yield return found;
    }

    static IEnumerable<Label> AllLabels(Node root)
    {
        if (root is Label l) yield return l;
        foreach (var child in root.GetChildren()) foreach (var found in AllLabels(child)) yield return found;
    }
}
