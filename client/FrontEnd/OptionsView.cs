using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;

namespace SmClient.FrontEnd;

/// <summary>
/// OPTIONS (ui/menu-rebuild, the owner's menu; ours; in the concept's steel layout since ui/concept-redesign, panel 04): GRAPHICS, SOUND and CONTROLS, one page of rows each, in the main menu and in the in-match
/// menu. Every row changes the live game at once and is saved in <c>frontend.json</c> (<see cref="GameOptions"/>; applied by <see cref="OptionsHost"/>), exactly as before. The page is a centred heavy
/// steel frame with the winged crest and an OPTIONS plate over it, category tabs that wrap, the rows in two columns (one when the window is narrow) in a scrolling recess, and a row of actions that
/// stays in view: APPLY (keeps what is set as the point REVERT goes back to, and says so), REVERT (puts every option on this page back to how it was when the page was opened or last applied) and
/// BACK. <see cref="ShowControls"/> opens the keybind screen.
/// </summary>
public sealed partial class OptionsView : UiKit.MenuPage
{
    public const string Graphics = "GRAPHICS", Sound = "SOUND", Controls = "CONTROLS", Interface = "INTERFACE";
    static readonly string[] Tabs = [Graphics, Sound, Controls, Interface];
    static readonly string[] OffOn = ["OFF", "ON"];

    /// <summary>The label column of a row, the frame's greatest width and the room above it for the crest and the plate (UI pixels).</summary>
    public const float LabelWidth = 300f, FrameMaxWidth = 1340f, TopRoom = 126f, FrameSide = 50f;

    readonly CustomizerServices _services;
    readonly Action _onBack, _onKeybinds;
    readonly Dictionary<string, Button> _tabs = new();
    readonly HFlowContainer _tabRow = new() { Name = "OptionsTabs", MouseFilter = MouseFilterEnum.Ignore, Alignment = FlowContainer.AlignmentMode.Center };
    readonly GridContainer _grid = new() { Name = "OptionsRows", Columns = 2, MouseFilter = MouseFilterEnum.Ignore };
    readonly VBoxContainer _wide = new() { Name = "OptionsWide", MouseFilter = MouseFilterEnum.Ignore };
    readonly ScrollContainer _list = new() { Name = "OptionsList", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true, MouseFilter = MouseFilterEnum.Pass };
    readonly PanelContainer _frame = new() { Name = "OptionsFrame", MouseFilter = MouseFilterEnum.Stop };
    readonly VBoxContainer _inner = new() { Name = "OptionsInner", MouseFilter = MouseFilterEnum.Ignore };
    readonly Label _plate = new() { Name = "OptionsPlate", Text = "OPTIONS", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
    readonly UiKit.WingedCrest _crest = new(460f, 90f);
    readonly UiKit.WingedCrest _skull = new(70f, 54f, wings: false);
    readonly UiKit.StonePillar _pillarLeft = new(73f, 3, "stone-options", flip: true), _pillarRight = new(73f, 11, "stone-options");
    readonly ColorRect _dim = new() { Name = "OptionsDim", Color = new Color(0f, 0f, 0f, 0.42f), MouseFilter = MouseFilterEnum.Ignore };
    readonly Button _apply, _revert, _back;
    readonly Label _toast = new() { Name = "OptionsToast", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
    FrontEndSettings _snapshot;
    public string Tab { get; private set; } = Graphics;
    UiKit.MenuChoice? _windowRow, _resolutionRow;   // the Graphics rows F11 changes behind the player's back

    public override Control List => _list;
    /// <summary>The APPLY, REVERT and BACK buttons and the confirmation line (the self-test reads them).</summary>
    public Button ApplyButton => _apply;
    public Button RevertButton => _revert;
    public Button BackButton => _back;
    public Label Toast => _toast;
    public Control Frame => _frame;
    public Control TabRow => _tabRow;
    public Control Grid => _grid;

    GameOptions O => _services.Settings.Options;
    void Set(Func<GameOptions, GameOptions> change) => _services.UpdateSettings(x => x with { Options = change(x.Options) });

    /// <param name="foot">A small note under the frame, in the main menu (where the game was found, the builds' address).</param>
    public OptionsView(CustomizerServices services, Action onBack, Action onKeybinds, Control? foot = null) : base("Options", "Options")
    {
        _services = services; _onBack = onBack; _onKeybinds = onKeybinds;
        _snapshot = services.Settings;
        Visible = false;
        _title.Visible = false; _heading.Visible = false; _scroll.Visible = false;   // the base page's title and rows give way to the steel layout
        var style = new Steel.SteelStyle(Steel.Kind.Frame, 16f, FrameSide, 28f);
        style.SetContentMargin(Side.Top, 70f); style.SetContentMargin(Side.Bottom, 46f);
        _frame.AddThemeStyleboxOverride("panel", style);
        _frame.AddChild(_inner);
        // Category tabs: they wrap onto a second line when the frame is narrower than they are.
        _tabRow.AddThemeConstantOverride("h_separation", 12); _tabRow.AddThemeConstantOverride("v_separation", 8);
        var group = new ButtonGroup { AllowUnpress = false };
        var buttons = new List<Control>();
        foreach (var tab in Tabs)
        {
            string t = tab;
            var b = UiKit.SteelButton(tab, () => ShowTab(t), null, "Menu" + tab[0] + tab[1..].ToLowerInvariant(), tab: true, minHeight: 46);
            b.CustomMinimumSize = new Vector2(230f, 46f);
            b.ToggleMode = true; b.ButtonGroup = group;
            _tabRow.AddChild(b);
            _tabs[tab] = b;
            buttons.Add(b);
        }
        UiKit.ChainHorizontal(buttons);
        _inner.AddChild(_tabRow);
        _grid.AddThemeConstantOverride("h_separation", 44); _grid.AddThemeConstantOverride("v_separation", 4);
        _grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _wide.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _wide.AddThemeConstantOverride("separation", 6);
        var page = new VBoxContainer { Name = "OptionsPage", MouseFilter = MouseFilterEnum.Ignore };
        page.AddThemeConstantOverride("separation", 10);
        page.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        page.AddChild(_grid); page.AddChild(_wide);
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", 6); margin.AddThemeConstantOverride("margin_right", 14); margin.AddThemeConstantOverride("margin_top", 8);
        margin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        margin.AddChild(page);
        _list.AddChild(margin);
        _list.SizeFlagsVertical = SizeFlags.ExpandFill;
        _inner.AddChild(_list);
        _grid.MinimumSizeChanged += Place; _wide.MinimumSizeChanged += Place;
        // The actions stay in view under the scrolling rows: APPLY (gold, the primary), REVERT, then BACK apart on the right.
        var actions = new HBoxContainer { Name = "OptionsActions", Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        actions.AddThemeConstantOverride("separation", 18);
        _apply = UiKit.SteelButton("Apply", Apply, "Keeps the options as they are now (they already work and are saved): REVERT goes back to this point", "OptionsApply", minHeight: 52);
        _apply.ThemeTypeVariation = Steel.PrimaryType;
        _revert = UiKit.SteelButton("Revert", Revert, "Puts the options on this page back to how they were when you opened it or last pressed APPLY", "OptionsRevert", minHeight: 52);
        _back = UiKit.SteelButton("Back", () => _onBack(), null, "OptionsBack", minHeight: 52);
        foreach (var b in new[] { _apply, _revert, _back }) b.CustomMinimumSize = new Vector2(b == _back ? 170f : 230f, 52f);
        var space = new Control { CustomMinimumSize = new Vector2(80f, 0f), MouseFilter = MouseFilterEnum.Ignore };
        actions.AddChild(_apply); actions.AddChild(_revert); actions.AddChild(space); actions.AddChild(_back);
        var toastBox = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(0f, 38f) };
        _toast.AddThemeFontSizeOverride("font_size", Steel.RowSize);
        _toast.AddThemeColorOverride("font_color", Steel.GoldHi);
        _toast.AddThemeStyleboxOverride("normal", new Steel.SteelStyle(Steel.Kind.Recess, 6f, 22f, 5f));
        _toast.CustomMinimumSize = new Vector2(280f, 34f);
        toastBox.AddChild(_toast);
        _inner.AddChild(actions);
        _inner.AddChild(toastBox);
        _inner.AddThemeConstantOverride("separation", 12);
        _plate.AddThemeFontOverride("font", new FontVariation { BaseFont = UiKit.Font.BaseFont, VariationEmbolden = 0.35f, SpacingGlyph = 5 });
        _plate.AddThemeFontSizeOverride("font_size", 42);
        _plate.AddThemeColorOverride("font_color", new Color(0.86f, 0.84f, 0.78f));
        _plate.AddThemeColorOverride("font_outline_color", new Color(0.02f, 0.02f, 0.02f));
        _plate.AddThemeConstantOverride("outline_size", 5);
        if (ConceptArt.Patch("title-plate", new Vector2(60f, 20f), new Vector2(30f, 6f)) is { } plateArt)
        {
            _plate.AddThemeStyleboxOverride("normal", plateArt);
            _plate.AddThemeColorOverride("font_color", Colors.Transparent);   // OPTIONS is in the painted plate (fixed text)
            _plate.AddThemeColorOverride("font_outline_color", Colors.Transparent);
        }
        else _plate.AddThemeStyleboxOverride("normal", new Steel.SteelStyle(Steel.Kind.Plate, 10f, 30f, 6f));
        AddChild(_dim); MoveChild(_dim, 0);
        AddChild(_pillarLeft); AddChild(_pillarRight);
        AddChild(_frame); AddChild(_crest); AddChild(_plate); AddChild(_skull);
        Foot = foot;   // under the frame (Place), outside its scroll list
    }

    /// <summary>Opens the page of a tab (null keeps the last one), takes the point REVERT goes back to, and puts the focus on its tab.</summary>
    public void Open(string? tab = null)
    {
        Visible = true;
        _snapshot = _services.Settings;
        _toast.Visible = false;
        ShowTab(tab ?? Tab);
        _tabs[Tab].FocusLater();
    }

    /// <summary>Shows a tab's rows.</summary>
    public void ShowTab(string tab)
    {
        Tab = tab;
        foreach (var (name, b) in _tabs) b.SetPressedNoSignal(name == tab);
        foreach (var box in new Container[] { _grid, _wide })
            foreach (var child in box.GetChildren()) { box.RemoveChild(child); child.QueueFree(); }
        switch (tab)
        {
            case Sound: BuildSound(); break;
            case Controls: BuildControls(); break;
            case Interface: BuildInterface(); break;
            default: BuildGraphics(); break;
        }
        _grid.UpdateMinimumSize(); _wide.UpdateMinimumSize();
        _list.ScrollVertical = 0;
        Place();
    }

    /// <summary>APPLY: the options are live and saved already; this keeps them as the point REVERT goes back to and says so.</summary>
    public void Apply()
    {
        _snapshot = _services.Settings;
        ShowToast("\u2713  SETTINGS APPLIED");
    }

    /// <summary>REVERT: every option this page edits goes back to the last point (the page opened, or APPLY), applied and saved like any change.</summary>
    public void Revert()
    {
        var back = _snapshot;
        _services.UpdateSettings(x => x with { SavedOptions = back.SavedOptions, FovDegrees = back.FovDegrees, MouseSensitivity = back.MouseSensitivity, ReduceMotion = back.ReduceMotion, ShowOthersEmblems = back.ShowOthersEmblems });
        ShowTab(Tab);
        _apply.FocusLater();
        ShowToast("SETTINGS REVERTED");
    }

    void ShowToast(string text)
    {
        _toast.Text = text; _toast.Visible = true;
        if (GetTree() is not { } tree) return;
        var timer = tree.CreateTimer(3.0);
        timer.Timeout += () => { if (IsInstanceValid(_toast) && _toast.Text == text) _toast.Visible = false; };
    }

    /// <summary>Adds an option's row to the page: a choice or a slider gets the steel skin (the label's column, then the control), in the grid's next cell.</summary>
    void Add(Control row)
    {
        switch (row)
        {
            case UiKit.MenuChoice choice: choice.UseSteel(LabelWidth); break;
            case UiKit.MenuSlider slider: slider.UseSteel(LabelWidth, slider.Slider.Name == "FovSlider" ? 176f : 96f); break;
        }
        row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _grid.AddChild(row);
    }

    /// <summary>Adds a row that takes the page's whole width, under the two columns (the renderer note, KEYBINDS).</summary>
    void AddWide(Control row) { row.SizeFlagsHorizontal = SizeFlags.ExpandFill; _wide.AddChild(row); }

    void Choice(string label, string[] values, int index, Action<int> changed, string? tip = null) => Add(new UiKit.MenuChoice(label, values, index, changed, tip));

    static string Percent(double v) => $"{v * 100.0:F0}%";

    /// <summary>The frame at the window's size: centred, as wide as the window allows, as tall as its rows need and no taller than the window; only the rows scroll.</summary>
    protected override void Place()
    {
        if (_apply is null) return;   // the base class's constructor asks before the buttons exist
        float w = Size.X, h = Size.Y;
        float footH = _foot is { Visible: true } f ? f.GetCombinedMinimumSize().Y + 14f : 0f;
        float frameW = Mathf.Clamp(w - 2f * 150f, 700f, FrameMaxWidth);
        _grid.Columns = frameW < 1120f ? 1 : 2;
        var style = (Steel.SteelStyle)_frame.GetThemeStylebox("panel");
        float chrome = _inner.GetCombinedMinimumSize().Y - _list.CustomMinimumSize.Y + style.GetMargin(Side.Top) + style.GetMargin(Side.Bottom);
        float rowsH = _grid.GetCombinedMinimumSize().Y + _wide.GetCombinedMinimumSize().Y + 10f + 8f;
        float maxFrame = h - TopRoom - 28f - footH;
        float listH = Mathf.Clamp(rowsH, 140f, Mathf.Max(140f, maxFrame - chrome));
        _list.CustomMinimumSize = new Vector2(0f, listH);
        float frameH = chrome + listH;
        float top = Mathf.Max(TopRoom, (h - frameH - footH) / 2f + 40f);
        float left = (w - frameW) / 2f;
        _dim.Position = Vector2.Zero; _dim.Size = Size;
        _frame.Position = new Vector2(left, top); _frame.Size = new Vector2(frameW, frameH);
        float plateW = ConceptArt.Get("title-plate") is { } pt ? pt.GetWidth() : 380f, plateH = ConceptArt.Get("title-plate") is { } ph ? ph.GetHeight() : 74f;
        _pillarLeft.Position = new Vector2(left - 56f, top + 40f); _pillarLeft.Size = new Vector2(73f, Mathf.Max(60f, frameH - 80f));
        _pillarRight.Position = new Vector2(left + frameW - 17f, top + 40f); _pillarRight.Size = new Vector2(73f, Mathf.Max(60f, frameH - 80f));
        var crestSize = _crest.CustomMinimumSize;
        _plate.Size = new Vector2(plateW, plateH); _plate.Position = new Vector2((w - plateW) / 2f, top - plateH * 0.55f);
        _crest.Size = crestSize; _crest.Position = new Vector2((w - crestSize.X) / 2f, _plate.Position.Y - crestSize.Y + 18f);
        _skull.Visible = false;
        if (_foot is { } foot) { foot.Position = new Vector2(left, top + frameH + 14f); foot.Size = new Vector2(frameW, foot.GetCombinedMinimumSize().Y); }
        // The draw order: the pillars behind the frame, the crest and the plate over its edge.
    }

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
        // musou/perf-pass (the owner's brief, ours): FSR 2 upscaling, default off. On, its preset sets the 3D scale and it is the anti-aliasing, so the render scale and anti-aliasing rows rest.
        var upscales = Enum.GetValues<UpscaleChoice>();
        bool fsrOk = RenderQuality.Fsr2Supported;
        UiKit.MenuSlider? scaleRow = null;
        UiKit.MenuChoice? aaRow = null;
        void Rest()
        {
            bool fsr = fsrOk && O.Upscale != UpscaleChoice.Off;
            scaleRow?.SetDisabled(fsr);
            aaRow?.SetDisabled(fsr || RenderQuality.AaFromCommandLine);
        }
        var upscaleRow = new UiKit.MenuChoice("Upscaling", upscales.Select(Upscaling.Name).ToArray(), Math.Max(0, Array.IndexOf(upscales, O.Upscale)), i => { Set(x => x with { Upscale = upscales[i] }); Rest(); },
            fsrOk ? "AMD FSR 2: renders the 3D picture smaller and rebuilds it at full size (QUALITY, BALANCED, PERFORMANCE), or at full size as its own anti-aliasing (NATIVE AA). The menus and the HUD stay sharp. OFF uses the render scale and the anti-aliasing below. Cut-out edges (grates, fences) follow the change when the next map loads."
                  : "FSR 2 needs the Forward+ renderer; the Compatibility renderer (old GPU / safe mode) has none.");
        upscaleRow.Name = "UpscalingRow";
        upscaleRow.SetDisabled(!fsrOk || RenderQuality.UpscaleFromCommandLine);
        Add(upscaleRow);
        scaleRow = new UiKit.MenuSlider("Render scale", GameOptions.MinRenderScale, GameOptions.MaxRenderScale, GameOptions.RenderScaleStep, O.RenderScale, Percent, v => Set(x => x with { RenderScale = (float)v }), "RenderScaleSlider",
            "The 3D picture's resolution as a part of the window's; lower runs faster, the menus and the HUD stay sharp. With FSR 2 on, its preset sets the scale.");
        Add(scaleRow);
        Choice("Shadow quality", ["DEFAULT", "LOW", "MEDIUM", "HIGH"], (int)O.Shadows, i => Set(x => x with { Shadows = (ShadowChoice)i }), "DEFAULT follows the lighting preset. Takes effect on the sun's shadows at once.");
        var aa = RenderQuality.Choices;
        aaRow = new UiKit.MenuChoice("Anti-aliasing", aa.Select(RenderQuality.ChoiceName).ToArray(), Math.Max(0, Array.IndexOf(aa, O.Aa)), i => Set(x => x with { Aa = aa[i] }),
            "Cut-out edges (grates, fences) follow the new choice when the next map loads. With FSR 2 on, FSR 2 is the anti-aliasing.");
        Add(aaRow);
        Rest();
        // musou/perf-pass, the owner's "option in Graphics" (2026-11-08): Ashfall's grime as an overlay pass (exact, the default) or folded into the kit's materials (faster on weak GPUs, close but not exact).
        // polish pass P1 (2026-11-10): in safe mode (the Compatibility renderer) the grime is always FAST (SafeMode.GrimeFoldedInSafeMode, a core flag: nothing from Extras/ here); the row shows it and rests.
        bool grimeForced = SafeMode.GrimeFoldedInSafeMode;
        var grimeRow = new UiKit.MenuChoice("Ashfall grime", ["EXACT (OVERLAY)", "FAST (FOLDED, WEAK GPUS)"], grimeForced || O.GrimeFast ? 1 : 0, i => Set(x => x with { GrimeFast = i == 1 }),
            grimeForced ? "Musou Mode's Ashfall only. In old GPU / safe mode the grime is always FAST (worked into the ruins' own materials): the extra overlay pass is what the Compatibility renderer pays most for."
                        : "Musou Mode's Ashfall only. EXACT draws the dirt and soot as an extra pass over the ruins. FAST works it into the ruins' own materials: the ruins are drawn once instead of three times, the look is close but not exact. Takes effect at once.");
        grimeRow.Name = "AshfallGrimeRow";
        grimeRow.SetDisabled(grimeForced);
        Add(grimeRow);
        Add(UiKit.FovRow(_services.Settings.FovDegrees, f => _services.UpdateSettings(x => x with { FovDegrees = f })));   // ui/fov-slider (ours)
        Add(new UiKit.MenuSlider("Brightness", GameOptions.MinBrightness, GameOptions.MaxBrightness, GameOptions.BrightnessStep, O.Brightness, Percent, v => Set(x => x with { Brightness = (float)v }), "BrightnessSlider", "A gamma curve over the whole picture. 100% leaves it untouched."));
        // ui/lighting-presets (the owner's request of 2026-11-06; ours, cosmetic): ten looks over the map's lighting, applied live (LightingGradeView); Faithful Remaster is today's look.
        Choice("Lighting preset", LightingPresets.Labels.Select(l => l.ToUpperInvariant()).ToArray(), (int)O.Lighting, i => Set(x => x with { Lighting = (LightingPresetChoice)i }),
            "The look of the light, haze and colour on every map. FAITHFUL REMASTER is the game's own look; the others only change the mood, never what you can see.");
        var renderer = new Control { Name = "RendererRow", MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(0f, 34f) };
        var caption = UiKit.MenuText("RENDERER", Steel.TextDim);
        caption.ThemeTypeVariation = Steel.LabelType;
        caption.SetAnchorsPreset(LayoutPreset.LeftWide); caption.OffsetRight = LabelWidth;
        var value = UiKit.MenuText((SafeMode.Compatibility ? "COMPATIBILITY" : "FORWARD+") + " (THE LAUNCHER'S OLD GPU / SAFE MODE CHOOSES)", Steel.TextDim, clip: false);
        value.ThemeTypeVariation = Steel.LabelType;
        value.SetAnchorsPreset(LayoutPreset.FullRect); value.OffsetLeft = LabelWidth; value.OffsetRight = 0f;
        renderer.AddChild(caption); renderer.AddChild(value);
        AddWide(renderer);
    }

    /// <summary>
    /// INTERFACE (owner 2026-11-15: "create a new interface tab"): the options that are about how the screen behaves rather than how it renders, moved here from GRAPHICS unchanged (same rows, defaults,
    /// handlers and saves): REDUCE MOTION, CAMERA SHAKE, SCREEN FLASHES, SHOW OTHERS' EMBLEMS. No setting was added.
    /// </summary>
    void BuildInterface()
    {
        Choice("Reduce motion", OffOn, _services.Settings.ReduceMotion ? 1 : 0, i => _services.UpdateSettings(x => x with { ReduceMotion = i == 1 }), "No camera moves, turntable or fades in the customizer; no artillery shake in Musou Mode.");
        // combat feel (owner's brief, 2026-11-09): accessibility controls for the shake and the flashes; 100% is the game as before. The game has no controller vibration, so there is none to control.
        Add(new UiKit.MenuSlider("Camera shake", 0, 1, GameOptions.AccessibilityStep, O.CameraShake, Percent, v => Set(x => x with { CameraShake = (float)v }), "CameraShakeSlider",
            "How much the camera shakes and kicks at blasts, hits, landings and heavy shots. 100% is the game's own; 0% turns it off."));
        Add(new UiKit.MenuSlider("Screen flashes", 0, 1, GameOptions.AccessibilityStep, O.ScreenFlashes, Percent, v => Set(x => x with { ScreenFlashes = (float)v }), "ScreenFlashesSlider",
            "How strong the flashes over the whole screen are (a krak grenade close by, Musou Mode's call-outs) and the bursts of power weapon hits. 100% is the game's own; 0% turns them off."));
        Choice("Show others' emblems", OffOn, _services.Settings.ShowOthersEmblems ? 1 : 0, i => _services.UpdateSettings(x => x with { ShowOthersEmblems = i == 1 }));
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
        Add(new UiKit.MenuSlider("Gamepad aim sensitivity", GameOptions.MinLook, GameOptions.MaxLook, GameOptions.LookStep, O.PadAimSensitivity, v => $"{v:F2}x", v => Set(x => x with { PadAimSensitivity = (float)v }), "PadAimSlider",
            "A factor on the right stick's turn rates while aiming down sights, on top of the gamepad look sensitivity. 1.00x turns as fast as without aiming."));
        Choice("Invert gamepad Y", OffOn, O.InvertPadY ? 1 : 0, i => Set(x => x with { InvertPadY = i == 1 }));
        var keybinds = UiKit.SteelButton("Keybinds", () => _onKeybinds(), "Rebind the keyboard, mouse and controller", "ControlsButton", minHeight: 52);
        keybinds.CustomMinimumSize = new Vector2(320f, 52f);
        keybinds.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        AddWide(keybinds);
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
