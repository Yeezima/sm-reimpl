using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;

namespace SmClient.FrontEnd;

/// <summary>
/// OPTIONS (ui/menu-rebuild, the owner's menu; ours): GRAPHICS, SOUND and CONTROLS, one page of rows each, in the main menu and in the in-match menu. Every row changes the live game at once
/// and is saved in <c>frontend.json</c> (<see cref="GameOptions"/>; applied by <see cref="OptionsHost"/>). <see cref="ShowControls"/> opens the keybind screen.
/// </summary>
public sealed partial class OptionsView : UiKit.MenuPage
{
    public const string Graphics = "GRAPHICS", Sound = "SOUND", Controls = "CONTROLS";
    static readonly string[] Tabs = [Graphics, Sound, Controls];
    static readonly string[] OffOn = ["OFF", "ON"];

    readonly CustomizerServices _services;
    readonly Action _onBack, _onKeybinds;
    readonly Dictionary<string, Button> _tabs = new();
    readonly UiKit.MenuColumn _page = new() { Name = "OptionsRows" };
    public string Tab { get; private set; } = Graphics;
    UiKit.MenuChoice? _windowRow, _resolutionRow;   // the Graphics rows F11 changes behind the player's back

    GameOptions O => _services.Settings.Options;
    void Set(Func<GameOptions, GameOptions> change) => _services.UpdateSettings(x => x with { Options = change(x.Options) });

    /// <param name="foot">A small note under the rows, in the main menu (where the game was found, the builds' address).</param>
    public OptionsView(CustomizerServices services, Action onBack, Action onKeybinds, Control? foot = null) : base("Options", "Options")
    {
        _services = services; _onBack = onBack; _onKeybinds = onKeybinds;
        Visible = false;
        var tabRow = new HBoxContainer { Name = "OptionsTabs", MouseFilter = MouseFilterEnum.Ignore };
        tabRow.AddThemeConstantOverride("separation", 28);
        var group = new ButtonGroup { AllowUnpress = false };
        var buttons = new List<Control>();
        foreach (var tab in Tabs)
        {
            string t = tab;
            var b = UiKit.MenuButton(tab, () => ShowTab(t));
            b.ToggleMode = true; b.ButtonGroup = group;
            tabRow.AddChild(b);
            _tabs[tab] = b;
            buttons.Add(b);
        }
        UiKit.ChainHorizontal(buttons);
        Rows.AddChild(tabRow);
        Rows.AddChild(UiKit.MenuGap(0.5f));
        Rows.AddChild(_page);
        Rows.AddChild(UiKit.MenuGap(0.5f));
        Rows.AddChild(UiKit.MenuButton("Back", () => _onBack()));
        if (foot is not null) { Rows.AddChild(UiKit.MenuGap()); Rows.AddChild(foot); }
    }

    /// <summary>Opens the page of a tab (null keeps the last one) and puts the focus on its tab.</summary>
    public void Open(string? tab = null)
    {
        Visible = true;
        ShowTab(tab ?? Tab);
        _tabs[Tab].FocusLater();
    }

    /// <summary>Shows a tab's rows.</summary>
    public void ShowTab(string tab)
    {
        Tab = tab;
        foreach (var (name, b) in _tabs) b.SetPressedNoSignal(name == tab);
        foreach (var child in _page.GetChildren()) { _page.RemoveChild(child); child.QueueFree(); }
        switch (tab)
        {
            case Sound: BuildSound(); break;
            case Controls: BuildControls(); break;
            default: BuildGraphics(); break;
        }
        _page.UpdateMinimumSize();
        Rows.UpdateMinimumSize();
    }

    void Add(Control row) => _page.AddChild(row);

    void Choice(string label, string[] values, int index, Action<int> changed, string? tip = null) => Add(new UiKit.MenuChoice(label, values, index, changed, tip));

    static string Percent(double v) => $"{v * 100.0:F0}%";

    void BuildGraphics()
    {
        var size = GetWindow()?.Size ?? DisplayServer.WindowGetSize();
        var screen = DisplayServer.ScreenGetSize();
        var sizes = GameOptions.Resolutions.Where(r => r.Width <= Math.Max(screen.X, 1280) && r.Height <= Math.Max(screen.Y, 720)).ToList();
        (int, int) now = O.WindowWidth > 0 ? (O.WindowWidth, O.WindowHeight) : (size.X, size.Y);
        if (!sizes.Contains(now)) sizes.Add(now);
        sizes = sizes.OrderBy(r => r.Item1).ThenBy(r => r.Item2).ToList();
        var resolution = new UiKit.MenuChoice("Resolution", sizes.Select(r => $"{r.Item1} X {r.Item2}").ToArray(), sizes.IndexOf(now),
            i => Set(x => x with { WindowWidth = sizes[i].Item1, WindowHeight = sizes[i].Item2 }), "The window's size. Fullscreen and borderless use the desktop's resolution.");
        resolution.SetDisabled(O.Window != WindowChoice.Windowed);
        Add(resolution);
        _resolutionRow = resolution;
        _windowRow = new UiKit.MenuChoice("Window mode", ["WINDOWED", "BORDERLESS", "FULLSCREEN"], (int)O.Window, i =>
        {
            Set(x => x with { Window = (WindowChoice)i });
            resolution.SetDisabled(O.Window != WindowChoice.Windowed);
        }, "F11 switches between fullscreen and the window, anywhere.");
        Add(_windowRow);
        Choice("Vsync", OffOn, O.Vsync ? 1 : 0, i => Set(x => x with { Vsync = i == 1 }));
        var caps = GameOptions.FpsCaps;
        Choice("FPS cap", caps.Select(c => c == 0 ? "UNLIMITED" : c.ToString()).ToArray(), Array.IndexOf(caps, O.FpsCap), i => Set(x => x with { FpsCap = caps[i] }));
        Add(new UiKit.MenuSlider("Render scale", GameOptions.MinRenderScale, GameOptions.MaxRenderScale, GameOptions.RenderScaleStep, O.RenderScale, Percent, v => Set(x => x with { RenderScale = (float)v }), "RenderScaleSlider",
            "The 3D picture's resolution as a part of the window's; lower runs faster, the menus and the HUD stay sharp."));
        Choice("Shadow quality", ["DEFAULT", "LOW", "MEDIUM", "HIGH"], (int)O.Shadows, i => Set(x => x with { Shadows = (ShadowChoice)i }), "DEFAULT follows the lighting preset. Takes effect on the sun's shadows at once.");
        var aa = RenderQuality.Choices;
        var aaRow = new UiKit.MenuChoice("Anti-aliasing", aa.Select(RenderQuality.ChoiceName).ToArray(), Math.Max(0, Array.IndexOf(aa, O.Aa)), i => Set(x => x with { Aa = aa[i] }),
            "Cut-out edges (grates, fences) follow the new choice when the next map loads.");
        aaRow.SetDisabled(RenderQuality.AaFromCommandLine);
        Add(aaRow);
        Add(UiKit.FovRow(_services.Settings.FovDegrees, f => _services.UpdateSettings(x => x with { FovDegrees = f })));   // ui/fov-slider (ours)
        Add(new UiKit.MenuSlider("Brightness", GameOptions.MinBrightness, GameOptions.MaxBrightness, GameOptions.BrightnessStep, O.Brightness, Percent, v => Set(x => x with { Brightness = (float)v }), "BrightnessSlider", "A gamma curve over the whole picture. 100% leaves it untouched."));
        Choice("Reduce motion", OffOn, _services.Settings.ReduceMotion ? 1 : 0, i => _services.UpdateSettings(x => x with { ReduceMotion = i == 1 }), "No camera moves, turntable or fades in the customizer; no artillery shake in Musou Mode.");
        Choice("Show others' emblems", OffOn, _services.Settings.ShowOthersEmblems ? 1 : 0, i => _services.UpdateSettings(x => x with { ShowOthersEmblems = i == 1 }));
        var renderer = new Control { Name = "RendererRow", MouseFilter = MouseFilterEnum.Ignore };
        var caption = UiKit.MenuText("RENDERER", UiKit.Grey);
        caption.SetAnchorsPreset(LayoutPreset.LeftWide); caption.OffsetRight = UiKit.MenuValueColumn;
        var value = UiKit.MenuText((SafeMode.Compatibility ? "COMPATIBILITY" : "FORWARD+") + " (THE LAUNCHER'S OLD GPU / SAFE MODE CHOOSES)", UiKit.Grey, clip: false);
        value.SetAnchorsPreset(LayoutPreset.LeftWide); value.OffsetLeft = UiKit.MenuValueColumn; value.OffsetRight = UiKit.MenuValueColumn + 530f;
        renderer.AddChild(caption); renderer.AddChild(value);
        Add(renderer);
    }

    void BuildSound()
    {
        Add(new UiKit.MenuSlider("Master volume", 0, 1, GameOptions.VolumeStep, O.MasterVolume, Percent, v => Set(x => x with { MasterVolume = (float)v }), "MasterVolumeSlider"));
        Add(new UiKit.MenuSlider("Music volume", 0, 1, GameOptions.VolumeStep, O.MusicVolume, Percent, v => Set(x => x with { MusicVolume = (float)v }), "MusicVolumeSlider", "The match's countdown and victory or defeat music."));
        Add(new UiKit.MenuSlider("Effects volume", 0, 1, GameOptions.VolumeStep, O.EffectsVolume, Percent, v => Set(x => x with { EffectsVolume = (float)v }), "EffectsVolumeSlider"));
        Add(new UiKit.MenuSlider("Voice volume", 0, 1, GameOptions.VolumeStep, O.VoiceVolume, Percent, v => Set(x => x with { VoiceVolume = (float)v }), "VoiceVolumeSlider", "The characters' spoken lines."));
        Choice("Mute when unfocused", OffOn, O.MuteUnfocused ? 1 : 0, i => Set(x => x with { MuteUnfocused = i == 1 }), "All sound stops while another window has the focus.");
    }

    void BuildControls()
    {
        Add(UiKit.MouseSensitivityRow(_services.Settings.MouseSensitivity, m => _services.UpdateSettings(x => x with { MouseSensitivity = m })));   // ui/mouse-sensitivity (ours)
        Add(new UiKit.MenuSlider("Aim sensitivity", GameOptions.MinLook, GameOptions.MaxLook, GameOptions.LookStep, O.AimSensitivity, v => $"{v:F2}x", v => Set(x => x with { AimSensitivity = (float)v }), "AimSensitivitySlider",
            "A factor on the mouse look while aiming, on top of the mouse sensitivity."));
        Choice("Invert Y", OffOn, O.InvertY ? 1 : 0, i => Set(x => x with { InvertY = i == 1 }));
        Add(new UiKit.MenuSlider("Gamepad look sensitivity", GameOptions.MinLook, GameOptions.MaxLook, GameOptions.LookStep, O.PadLookSensitivity, v => $"{v:F2}x", v => Set(x => x with { PadLookSensitivity = (float)v }), "PadLookSlider",
            "A factor on the right stick's turn rates. 1.00x is the original game's data."));
        Choice("Invert gamepad Y", OffOn, O.InvertPadY ? 1 : 0, i => Set(x => x with { InvertPadY = i == 1 }));
        var keybinds = UiKit.MenuButton("Keybinds", () => _onKeybinds(), "Rebind the keyboard, mouse and controller");
        keybinds.Name = "ControlsButton";
        Add(keybinds);
    }

    public override void _EnterTree() => CustomizerServices.WindowModeToggled += OnWindowModeToggled;

    public override void _ExitTree() => CustomizerServices.WindowModeToggled -= OnWindowModeToggled;

    /// <summary>F11 changed the window mode: the open page shows it at once (the rows keep their focus).</summary>
    void OnWindowModeToggled(GameOptions options)
    {
        if (_windowRow is null || !GodotObject.IsInstanceValid(_windowRow)) return;
        _windowRow.SetIndex((int)options.Window);
        if (_resolutionRow is not null && GodotObject.IsInstanceValid(_resolutionRow)) _resolutionRow.SetDisabled(options.Window != WindowChoice.Windowed);
    }

    public override void _Input(InputEvent e)
    {
        if (!IsVisibleInTree() || !PadHost.IsBack(e)) return;
        GetViewport().SetInputAsHandled();
        _onBack();
    }
}
