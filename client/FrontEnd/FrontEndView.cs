using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;
using SmSim;

namespace SmClient.FrontEnd;

/// <summary>Which screen the front end opens on (<c>--menu</c>, <c>--customizer</c>, <c>--appearance</c>).</summary>
public enum StartScreen { Join, Creator, Appearance }

/// <summary>What the Join button hands over: the server, the plan for the active loadout (class, item indices, perks, looks) and the shared services.</summary>
/// <param name="Content">The content profile the match is joined with (added content E2: the Join screen's picker, or <c>--content</c>); null: <c>--content</c> / the default.</param>
public sealed record JoinRequest(string Host, JoinPlan Plan, CustomizerServices Services, SmSim.ContentExtras? Content = null, bool Spectate = false);

/// <summary>
/// The front end (docs/customizer-design.md F1, F3): a shell that waits for the install to be read, then shows the Join screen (server, side, the loadout strip with
/// the ACTIVE one, CUSTOMIZE, JOIN), the creator and the appearance editor in turn. It owns the 1920 x 1080 UI space of the window while it is shown and gives it
/// back when a match starts. The client's Join goes through <see cref="JoinRequest"/>; <c>ModelViewer</c> turns it into the existing connect path.
/// </summary>
public sealed partial class FrontEndView : Control
{
    readonly Func<CustomizerServices> _makeServices;
    readonly Action<JoinRequest> _join;
    readonly StartScreen _start;
    readonly Dictionary<string, string?> _args;
    CustomizerServices _services;

    Control? _loading;
    Label? _loadingText;
    Control? _joinScreen;
    CreatorView? _creator;
    AppearanceView? _appearance;
    ControlsView? _controls;
    SlotStrip? _strip;
    LineEdit? _host;
    Label? _activeLabel, _summary, _message;
    ScrollContainer? _middle;
    VBoxContainer? _middleContent;
    HostPanel? _hostPanel;

    /// <summary>What <see cref="JoinMiddle"/> scrolls (its height is what the middle needs).</summary>
    public VBoxContainer? JoinMiddleContent => _middleContent;
    /// <summary>The Join screen's Host panel, or null when no server executable was found.</summary>
    public HostPanel? JoinHostPanel => _hostPanel;

    /// <summary>The Join screen's scrolling middle (server, host, loadouts) between the title and the action bar; the UI self-test's fit check reads it.</summary>
    public ScrollContainer? JoinMiddle => _middle;
    /// <summary>The Join screen's root (the fit check lays it out at several window sizes).</summary>
    public Control? JoinScreen => _joinScreen;
    readonly List<Button> _sideButtons = new();
    Button? _spectate;   // observer tooling (protocol 46): the Join screen's SPECTATE toggle
    Button? _joinButton;
    bool _built;
    int _frames;
    double _builtAt;
    bool _scripted, _warmed;
    static bool _autoJoined;   // --auto-join (test aid) joins once per run: a join that fails returns to this screen and must not retry
    int _scriptFrame;

    /// <summary>A screenshot may be taken: the install is read, the screen is built and its preview (if any) has drawn.</summary>
    public bool ReadyForScreenshot => _services.Failed ? _frames > 30 : _built && _scripted && _frames > _scriptFrame + 25 && (CurrentPreview?.Loaded ?? true)
        && (!_args.ContainsKey("preview-switch-to") || _switchChecked);
    int _switchedAt, _switchPhase;
    bool _switchChecked;
    PreviewStage? CurrentPreview => _creator?.Visible == true ? _creator.Preview : _appearance?.Visible == true ? _appearance.Preview : null;

    public CustomizerServices Services => _services;
    public CreatorView? Creator => _creator;
    public AppearanceView? Appearance => _appearance;
    public bool ShowsJoin => _joinScreen?.Visible == true;

    readonly string? _startMessage;

    /// <param name="startMessage">Shown on the Join screen when it opens (why the last join failed).</param>
    public FrontEndView(Func<CustomizerServices> makeServices, Action<JoinRequest> join, StartScreen start, Dictionary<string, string?> args, string? startMessage = null)
    {
        _makeServices = makeServices; _join = join; _start = start; _args = args; _startMessage = startMessage; _services = makeServices();
        Name = "FrontEnd";
        SetAnchorsPreset(LayoutPreset.FullRect);
        Theme = UiKit.Theme;
    }

    public override void _Ready()
    {
        UiKit.EnterVirtualSpace(GetWindow());
        AddChild(UiKit.Background().With(n => n.SetAnchorsPreset(LayoutPreset.FullRect)));
        ShowLoading("Reading your Space Marine install...");
    }

    /// <summary>Gives the window back to the game (the match draws in real pixels).</summary>
    public void Release() => UiKit.LeaveVirtualSpace(GetWindow());

    // ---- loading ----

    void ShowLoading(string text)
    {
        _loading?.QueueFree();
        var box = UiKit.VBox(12);
        _loadingText = UiKit.Text(text, UiKit.HeaderSize, UiKit.White, HorizontalAlignment.Center);
        box.AddChild(_loadingText);
        _loading = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        _loading.SetAnchorsPreset(LayoutPreset.FullRect);
        _loading.AddChild(box);
        AddChild(_loading);
    }

    void ShowFailure(string error)
    {
        _loading?.QueueFree();
        var box = UiKit.VBox(16);
        box.CustomMinimumSize = new Vector2(900, 0);
        box.AddChild(UiKit.Text("ERROR", UiKit.HeaderSize, UiKit.LightRed));
        box.AddChild(UiKit.Text("The game install could not be read:", UiKit.BodySize, UiKit.White));
        box.AddChild(UiKit.Text(error, UiKit.BodySize, UiKit.LightRed, HorizontalAlignment.Left, true).With(l => l.CustomMinimumSize = new Vector2(860, 0)));
        box.AddChild(UiKit.Text("Check that Warhammer 40,000: Space Marine is installed (or pass --game-dir), then try again.", UiKit.CaptionSize, UiKit.Grey, HorizontalAlignment.Left, true).With(l => l.CustomMinimumSize = new Vector2(860, 0)));
        var buttons = UiKit.HBox(12);
        var retry = UiKit.Button("RETRY", () => { _services = _makeServices(); _loading?.QueueFree(); ShowLoading("Reading your Space Marine install..."); }, 200);
        buttons.AddChild(retry);
        buttons.AddChild(UiKit.Button("QUIT", () => GetTree().Quit(), 200));
        box.AddChild(buttons);
        var panel = UiKit.Panel(box, 28);
        panel.AddThemeStyleboxOverride("panel", UiKit.Box(UiKit.PanelSolid, UiKit.LightRed, 3, 28));
        _loading = new CenterContainer();
        _loading.SetAnchorsPreset(LayoutPreset.FullRect);
        _loading.AddChild(panel);
        AddChild(_loading);
        retry.FocusLater();
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (!_built)
        {
            if (_services.Failed && _loading is not null && _loadingText is not null) { ShowFailure(_services.Error ?? "unknown error"); _loadingText = null; }
            else if (_services.Ready) Build();
            return;
        }
        SyncContentLock();
        if (!_warmed) _warmed = _services.Previews.WarmStep();   // the default look is built while the menu is up
        if (!_scripted && _frames > 3) { _scripted = true; _scriptFrame = _frames; RunScript(); }
        else if (_scripted && !_autoJoined && _args.ContainsKey("auto-join") && _frames > _scriptFrame + 10) { _autoJoined = true; TryJoin(); }
        if (_scripted && _args.GetValueOrDefault("preview-switch-to") is { } to) SwitchCheck(to);
    }

    /// <summary>
    /// <c>--preview-switch-to chaos|marines</c> (test aid; started from the other side, e.g. <c>--preview-side chaos --preview-switch-to marines</c>): once the preview has drawn,
    /// (1) switches to the named side and back two frames later (the same character stays on stage) and, 40 frames after, reports the glow quads: a Chaos preview's eye glow
    /// must restart (more than 0); (2) switches to the named side and, 40 frames after, reports the quads still on the stage (0 for Space Marines). The screenshot waits.
    /// </summary>
    void SwitchCheck(string to)
    {
        var preview = CurrentPreview;
        if (preview is null || _creator is null || _switchChecked) return;
        bool toChaos = to.StartsWith("c", StringComparison.OrdinalIgnoreCase);
        string there = toChaos ? CustomizationCatalog.Chaos : CustomizationCatalog.SpaceMarine, home = toChaos ? CustomizationCatalog.SpaceMarine : CustomizationCatalog.Chaos;
        switch (_switchPhase)
        {
            case 0:
                if (!preview.Loaded || _frames <= _scriptFrame + 25) return;
                GD.Print($"preview eye glow before the switch: {preview.EyeGlowQuads} quads ({preview.EyeGlowState})");
                _creator.SetPreviewRace(there); _switchedAt = _frames; _switchPhase = 1;
                break;
            case 1:
                if (_frames < _switchedAt + 2) return;
                _creator.SetPreviewRace(home); _switchedAt = _frames; _switchPhase = 2;
                break;
            case 2:
                if (_frames <= _switchedAt + 40 || !preview.Loaded) return;
                GD.Print($"preview eye glow after the quick switch to {to} and back: {preview.EyeGlowQuads} quads ({preview.EyeGlowState}); {(toChaos || preview.EyeGlowQuads > 0 ? "OK" : "FAIL: the glow did not restart")}");
                _creator.SetPreviewRace(there); _switchedAt = _frames; _switchPhase = 3;
                break;
            default:
                if (_frames <= _switchedAt + 40 || !preview.Loaded) return;
                _switchChecked = true;
                GD.Print($"preview eye glow after the switch to {to}: {preview.EyeGlowQuads} quads ({preview.EyeGlowState}); {(toChaos || preview.EyeGlowQuads == 0 ? "OK" : "FAIL: glow quads left on the stage")}");
                break;
        }
    }

    // ---- building ----

    void Build()
    {
        _built = true;
        _loading?.QueueFree();
        _loading = null;
        _joinScreen = BuildJoin();
        AddChild(_joinScreen);
        _creator = new CreatorView(_services, ShowJoin, ShowAppearance) { Visible = false };
        _appearance = new AppearanceView(_services, ShowCreatorAgain) { Visible = false };
        AddChild(_creator);
        AddChild(_appearance);
        _controls = new ControlsView(ShowJoin);   // ui/keybinds (ours)
        AddChild(_controls);
        if (_args.ContainsKey("ui-selftest")) AddChild(new UiSelfTest(this, _services));
        // The profile's own notices (a repaired, corrupt or read-only file) show once.
        var notices = _services.Session!.Notices();
        if (notices.Count > 0) Say(string.Join(" ", notices), true);
        if (_startMessage is { Length: > 0 }) Say(_startMessage, true);
        switch (_start)
        {
            case StartScreen.Creator: ShowCreator(); break;
            case StartScreen.Appearance: ShowAppearance(); break;
            default: ShowJoin(); break;
        }
    }

    Control BuildJoin()
    {
        var root = new MarginContainer { Name = "Join" };
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var (side, v) in new[] { ("left", 84), ("right", 84), ("top", 28), ("bottom", 22) }) root.AddThemeConstantOverride("margin_" + side, v);
        // The title on top and the action bar at the bottom always stay in the window; the part between them (server, host, loadouts) scrolls when it does not fit
        // (fix/frontend-fit, release issue 1: with the Host panel the column was taller than the 1080-high UI space, so the bar fell off the window's bottom edge).
        var column = UiKit.VBox(14);
        root.AddChild(column);
        var middle = UiKit.VBox(12);

        var title = UiKit.HBox(16);
        title.AddChild(UiKit.Text("SPACE MARINE", UiKit.TitleSize, UiKit.Gold));
        title.AddChild(UiKit.Text("MULTIPLAYER", UiKit.TitleSize, UiKit.White));
        title.AddChild(UiKit.Spacer());
        title.AddChild(UiKit.Text("Reimplementation - alpha", UiKit.CaptionSize, UiKit.Grey).With(l => l.VerticalAlignment = VerticalAlignment.Center));
        column.AddChild(title);

        // Server and side.
        var connect = UiKit.VBox(10);
        var serverRow = UiKit.HBox(12);
        serverRow.AddChild(UiKit.Text("SERVER", UiKit.CaptionSize, UiKit.Grey).With(l => { l.CustomMinimumSize = new Vector2(120, 0); l.VerticalAlignment = VerticalAlignment.Center; }));
        _host = new LineEdit { Text = _args.GetValueOrDefault("host") ?? _services.Settings.Host, CustomMinimumSize = new Vector2(560, UiKit.MinHit), PlaceholderText = "host:port", MaxLength = 253, FocusMode = FocusModeEnum.All, SelectAllOnFocus = true };
        _host.TextSubmitted += _ => TryJoin();
        _host.TextChanged += _ => Say("");
        serverRow.AddChild(_host);
        serverRow.AddChild(UiKit.Text("host or host:port (default port 27015)", UiKit.CaptionSize, UiKit.Grey).With(l => l.VerticalAlignment = VerticalAlignment.Center));
        connect.AddChild(serverRow);
        var sideRow = UiKit.HBox(12);
        sideRow.AddChild(UiKit.Text("SIDE", UiKit.CaptionSize, UiKit.Grey).With(l => { l.CustomMinimumSize = new Vector2(120, 0); l.VerticalAlignment = VerticalAlignment.Center; }));
        foreach (var (side, text) in new[] { (SideChoice.Auto, "Auto"), (SideChoice.SpaceMarines, "Space Marines"), (SideChoice.Chaos, "Chaos") })
        {
            var s = side;
            var b = new Button { Text = text, ToggleMode = true, ButtonPressed = _services.Settings.Side == side, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(210, UiKit.MinHit) };
            b.Pressed += () => { _services.UpdateSettings(x => x with { Side = s }); RefreshSide(); RefreshSummary(); };
            sideRow.AddChild(b);
            _sideButtons.Add(b);
        }
        UiKit.ChainHorizontal(_sideButtons.Cast<Control>().ToList());
        // Observer tooling (protocol 46; ours): join as a spectator (a free camera, no side, slot or score) instead of a player. Not remembered: every Join is a player's unless this is pressed again.
        _spectate = new Button { Text = "Spectate", ToggleMode = true, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(150, UiKit.MinHit), TooltipText = "Watch with a free camera instead of playing (footage tool)" };
        sideRow.AddChild(_spectate);
        sideRow.AddChild(UiKit.Text("Auto lets the server balance the teams.", UiKit.CaptionSize, UiKit.Grey).With(l => l.VerticalAlignment = VerticalAlignment.Center));
        connect.AddChild(sideRow);
        connect.AddChild(BuildContentRow());
        middle.AddChild(UiKit.Panel(connect, 16));

        // Host a match (ours): only when the dedicated server is bundled next to the client (beside it, in server\ below it, or in ..\server: the tester zip's game\ and server\).
        // The UI self-test always lays the panel out (its fit checks need it; it never presses HOST A MATCH), with or without a server next to the client.
        string? serverExe = ServerLaunch.FindExecutable(OS.GetExecutablePath().GetBaseDir(), _args.GetValueOrDefault("server-exe"))
            ?? (_args.ContainsKey("ui-selftest") ? "SmServer (UI self-test: not started)" : null);
        if (serverExe is not null)
        {
            _hostPanel = new HostPanel(serverExe, _services.GameDir, _args, ConnectToHost, Say, () => _services.Content, _services);
            middle.AddChild(_hostPanel);
        }

        // The loadout strip (chips 150 wide: the CUSTOM row leaves room for the scroll bar when the middle scrolls).
        middle.AddChild(UiKit.Text("YOUR LOADOUT", UiKit.CaptionSize, UiKit.Gold));
        _strip = new SlotStrip(_services, showDefaults: true, chipWidth: 150) { Picked = OnPick };
        middle.AddChild(_strip);
        _activeLabel = UiKit.Text("", UiKit.HeaderSize, UiKit.Gold);
        middle.AddChild(_activeLabel);
        _summary = UiKit.Text("", UiKit.BodySize, UiKit.White, HorizontalAlignment.Left, true);
        middle.AddChild(_summary);
        _middleContent = middle;
        _middle = UiKit.Scroll(middle);
        _middle.Name = "JoinMiddle";
        _middle.CustomMinimumSize = new Vector2(0, 240);   // never squeezed to a sliver by a long message
        column.AddChild(_middle);

        // The message line (why a join failed, a profile notice) sits just above the bar, so it is always on screen; it takes room only while it says something.
        _message = UiKit.Text("", UiKit.BodySize, UiKit.White, HorizontalAlignment.Left, true);
        _message.Visible = false;
        column.AddChild(_message);

        var bar = UiKit.HBox(16);
        bar.AddChild(UiKit.Button("CUSTOMIZE", ShowCreator, 280, "Edit your loadouts and your armour"));
        bar.AddChild(StateToggle("Reduce motion", _services.Settings.ReduceMotion, on => _services.UpdateSettings(x => x with { ReduceMotion = on }), 280));
        bar.AddChild(StateToggle("Show others' emblems", _services.Settings.ShowOthersEmblems, on => _services.UpdateSettings(x => x with { ShowOthersEmblems = on }), 380));
        bar.AddChild(UiKit.Spacer());
        bar.AddChild(UiKit.Button("QUIT", () => GetTree().Quit(), 160));
        _joinButton = UiKit.Button("JOIN", TryJoin, 320);
        _joinButton.AddThemeFontSizeOverride("font_size", UiKit.HeaderSize);
        _joinButton.CustomMinimumSize = new Vector2(320, 64);
        _joinButton.AddThemeStyleboxOverride("normal", UiKit.Box(new Color(0.2f, 0.16f, 0.02f, 0.97f), UiKit.Gold, 4, 12));
        bar.AddChild(_joinButton);
        column.AddChild(bar);
        var footer = UiKit.HBox(24);
        footer.AddChild(UiKit.FovRow(_services.Settings.FovDegrees, f => _services.UpdateSettings(x => x with { FovDegrees = f })));   // ui/fov-slider (ours)
        footer.AddChild(UiKit.Button("CONTROLS", ShowControls, 200, "Rebind the keyboard, mouse and controller").With(b => { b.Name = "ControlsButton"; b.CustomMinimumSize = new Vector2(200, 28); b.AddThemeFontSizeOverride("font_size", UiKit.CaptionSize);
            // The footer is one slider high: slim styles (the theme's boxes carry 12 px of margin all round), so the Join screen keeps its room.
            foreach (var (state, box) in new[] { ("normal", UiKit.Box(new Color(0.06f, 0.06f, 0.06f, 0.92f), new Color(0.55f, 0.45f, 0.1f), 2, 12)), ("hover", UiKit.Box(UiKit.Hover, UiKit.Gold, 2, 12)), ("pressed", UiKit.Box(new Color(0.25f, 0.2f, 0.03f, 0.98f), UiKit.Gold, 2, 12)) })
            { box.ContentMarginTop = 1; box.ContentMarginBottom = 1; b.AddThemeStyleboxOverride(state, box); } }));   // ui/keybinds (ours)
        if (GameFoundNote is { Length: > 0 }) footer.AddChild(UiKit.Text(GameFoundNote, UiKit.CaptionSize, UiKit.Grey).With(l => { l.ClipText = true; l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; l.TooltipText = GameFoundNote; l.MouseFilter = Control.MouseFilterEnum.Pass; }));
        else footer.AddChild(UiKit.Spacer());
        footer.AddChild(new LinkButton { Text = "Builds and issues: " + ServerLaunch.ReleasesUrl.Replace("https://", ""), Uri = ServerLaunch.ReleasesUrl }.With(l => l.AddThemeFontSizeOverride("font_size", UiKit.CaptionSize)));
        column.AddChild(footer);
        return root;
    }

    // ---- the content profile (added content E2, docs/added-content.md) ----

    readonly List<Button> _contentButtons = new();
    Label? _contentNote;

    /// <summary>
    /// CONTENT on the Join screen: Faithful (SM1 multiplayer alone) or Extended (the added content this build has: the Ork Boy, later gore). The choice is remembered
    /// next to the profile, is what Join and Host a match use, and must match the server's (a server with another profile refuses the client and says which it runs).
    /// A build without added content shows only Faithful. Musou (experimental added content): the third button, a mode of its own (<c>--content musou</c>: waves of
    /// Orks on Manufactorum, on this PC); disabled without added content, and the Host panel then hosts it on this PC only. Hidden until Musou is ready (the owner,
    /// 2026-10-31): shown only with <c>--musou</c> or <c>SM_MUSOU=1</c>; a remembered Musou choice falls back to Extended while it is hidden.
    /// </summary>
    Control BuildContentRow()
    {
        var row = UiKit.HBox(12);
        row.AddChild(UiKit.Text("CONTENT", UiKit.CaptionSize, UiKit.Grey).With(l => { l.CustomMinimumSize = new Vector2(120, 0); l.VerticalAlignment = VerticalAlignment.Center; }));
        bool showMusou = MusouShown;
        if (!showMusou && _services.Content.HasFlag(SmSim.ContentExtras.Musou)) _services.SetContent(SmSim.ContentExtras.All);
        foreach (var (name, text) in new[] { (SmSim.ContentProfiles.Faithful, "Faithful"), (SmSim.ContentProfiles.Extended, "Extended"), (SmSim.ContentProfiles.Musou, "Musou") })
        {
            string profile = name;
            bool available = name == SmSim.ContentProfiles.Faithful || SmSim.ContentExtras.Compiled;
            var b = new Button { Text = text, ToggleMode = true, ButtonPressed = false, Disabled = !available, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(showMusou ? 170 : 210, UiKit.MinHit), Name = "Content" + text };
            if (name == SmSim.ContentProfiles.Musou) b.Visible = showMusou;
            b.Pressed += () =>
            {
                SmSim.ContentProfiles.TryParse(profile, out var extras, out _);
                _services.SetContent(extras);
                RefreshContent();
                RefreshSummary();
            };
            row.AddChild(b);
            _contentButtons.Add(b);
        }
        UiKit.ChainHorizontal(_contentButtons.Where(b => b.Visible).Cast<Control>().ToList());
        _contentNote = UiKit.Text("", UiKit.CaptionSize, UiKit.Grey).With(l => l.VerticalAlignment = VerticalAlignment.Center);
        row.AddChild(_contentNote);
        RefreshContent();
        return row;
    }

    /// <summary>The Musou button is shown (<c>--musou</c> or <c>SM_MUSOU=1</c>): hidden until the mode is ready.</summary>
    public bool MusouShown => _args.ContainsKey("musou") || System.Environment.GetEnvironmentVariable("SM_MUSOU") == "1";

    bool _contentLocked;

    /// <summary>While the Host panel's server runs the profile cannot change (the server was started with it, and a client with another one is refused).</summary>
    void SyncContentLock()
    {
        if (_contentButtons.Count < 3) return;
        bool hosting = HostedServer.Running;
        if (hosting == _contentLocked) return;
        _contentLocked = hosting;
        _contentButtons[0].Disabled = hosting;
        _contentButtons[1].Disabled = hosting || !ContentExtras.Compiled;
        _contentButtons[2].Disabled = hosting || !ContentExtras.Compiled;
        RefreshContent();
    }

    void RefreshContent()
    {
        var extras = _services.Content;
        bool musou = extras.HasFlag(SmSim.ContentExtras.Musou);
        _contentButtons[0].SetPressedNoSignal(extras == SmSim.ContentExtras.None);
        _contentButtons[1].SetPressedNoSignal(extras != SmSim.ContentExtras.None && !musou);
        _contentButtons[2].SetPressedNoSignal(musou);
        if (_contentNote is not null)
            _contentNote.Text = _contentLocked ? $"Fixed while your server runs ({ContentProfiles.Name(extras)}). Stop the server to change it."
                : !SmSim.ContentExtras.Compiled ? "This build has no added content: faithful only."
                : extras == SmSim.ContentExtras.None ? "SM1 multiplayer alone. The server must run --content faithful."
                : musou ? "Musou (experimental): waves of Orks on Manufactorum, on your PC. The server runs --content musou."
                : "Adds the Ork Boy (and later gore). The server must run the same profile (the default).";
    }

    /// <summary>Where the game folder was found (shown small on the Join screen); set by the launcher.</summary>
    public string? GameFoundNote { get; init; }

    /// <summary>The Host panel's hand-over: join the server it started.</summary>
    void ConnectToHost(string host)
    {
        _host!.Text = host;
        TryJoin();
    }

    /// <summary>A toggle whose state is in its words (ON / OFF), not only in its colour.</summary>
    static Button StateToggle(string label, bool on, Action<bool> changed, int width)
    {
        var b = UiKit.Toggle($"{label}: {(on ? "ON" : "OFF")}", on, _ => { }, width);
        b.Toggled += v => { b.Text = $"{label}: {(v ? "ON" : "OFF")}"; changed(v); };
        return b;
    }

    void RefreshSide()
    {
        var order = new[] { SideChoice.Auto, SideChoice.SpaceMarines, SideChoice.Chaos };
        for (int i = 0; i < _sideButtons.Count; i++) _sideButtons[i].SetPressedNoSignal(_services.Settings.Side == order[i]);
    }

    void RefreshSummary()
    {
        var session = _services.Session;
        var catalog = _services.Catalog;
        if (session is null || catalog is null) return;
        var profile = session.Working;
        var active = LoadoutEdits.Active(profile, catalog, out bool fellBack);
        _strip!.SetOpen(null);
        _strip.Refresh();
        var plan = _services.PlanFor(active);
        string name = active.Kind == SelectionKind.Custom && profile.ClassAt(active.Slot) is { } c ? $"CUSTOM {active.Slot}  \"{c.Name}\"" : "DEFAULTS";
        _activeLabel!.Text = "ACTIVE: " + name;
        _summary!.Text = plan?.Summary ?? "";
        if (fellBack && profile.Selected.Kind == SelectionKind.Custom)
            Say($"Slot {profile.Selected.Slot} is empty, so {(active.Kind == SelectionKind.Custom ? $"slot {active.Slot}" : "the Tactical defaults")} is used.", false);
    }

    // ---- screens ----

    public void ShowJoin()
    {
        if (!_built) return;
        _creator!.Visible = false; _appearance!.Visible = false; _joinScreen!.Visible = true;
        bool fromControls = _controls?.Visible == true;
        _controls?.Close();
        RefreshSummary();
        if (fromControls && FindChild("ControlsButton", true, false) is Button controlsButton) controlsButton.FocusLater(); else _joinButton!.FocusLater();
    }

    /// <summary>The CONTROLS screen (key and controller rebinding; ui/keybinds), reached from the Join screen's footer.</summary>
    public void ShowControls()
    {
        if (!_built) return;
        _creator!.Visible = false; _appearance!.Visible = false; _joinScreen!.Visible = false;
        _controls!.Open();
    }

    /// <summary>The CONTROLS screen, or null before the screens are built (the self-test and the screenshots read it).</summary>
    public ControlsView? Controls => _controls;

    public void ShowCreator()
    {
        if (!_built) return;
        _joinScreen!.Visible = false; _appearance!.Visible = false; _creator!.Visible = true;
        _creator.Reenter();
        _creator.FocusList();
    }

    void ShowCreatorAgain()
    {
        _appearance!.Visible = false; _creator!.Visible = true;
        _creator.Reenter();
        _creator.FocusList();
    }

    public void ShowAppearance()
    {
        if (!_built) return;
        var open = _creator!.Open;
        if (_creator.IsOrkOpen)
        {
            // The Ork Boy has no armour or colours (ui/customizer-ork): his look choice, the creator's LOOK step, instead of the appearance editor.
            _joinScreen!.Visible = false; _appearance!.Visible = false; _creator.Visible = true;
            _creator.OpenStep("LOOK");
            _creator.FocusList();
            return;
        }
        int slot = open.Kind == SelectionKind.Custom ? open.Slot : 0;
        if (_services.Session!.Working.ClassAt(slot) is null)
        {
            // Nothing custom open (--appearance, or a DEFAULTS loadout): use the active slot, else slot 1.
            var active = LoadoutEdits.Active(_services.Session.Working, _services.Catalog!, out _);
            slot = active.Kind == SelectionKind.Custom && _services.Session.Working.ClassAt(active.Slot) is not null ? active.Slot
                : _services.Session.Working.Classes.FirstOrDefault()?.Slot ?? 0;
            if (slot == 0)
            {
                // Make slot 1 so there is something to dress.
                _services.Session.Edit(p => LoadoutEdits.Create(p, 1, ProfileStore.FirstRole(_services.Catalog!), _services.Catalog!).Profile);
                slot = 1;
            }
            _creator.OpenSelection(Selection.CustomSlot(slot));
        }
        string race = _services.Settings.Side == SideChoice.Chaos ? CustomizationCatalog.Chaos : CustomizationCatalog.SpaceMarine;
        _creator.Visible = false; _joinScreen!.Visible = false; _appearance!.Visible = true;
        _appearance.Open(slot, race);
    }

    // ---- joining ----

    void OnPick(Selection selection)
    {
        var session = _services.Session!;
        if (!LoadoutEdits.IsValid(session.Working, selection)) { Say($"Slot {selection.Slot} is empty. Choose CUSTOMIZE to fill it.", false); return; }
        session.Edit(p => LoadoutEdits.Select(p, selection));
        string? error = session.Save();
        if (error is not null) Say("Could not remember your choice: " + error, true);
        RefreshSummary();
    }

    void TryJoin()
    {
        string host = _host!.Text.Trim();
        if (FrontEndSettings.ValidateHost(host) is { } problem) { Say(problem, true); _host.GrabFocus(); return; }
        if (_services.Session!.Dirty)
        {
            // Unsaved edits are not part of what you join with.
            _services.Session.Revert();
        }
        _services.UpdateSettings(x => x with { Host = host });
        var plan = _services.PlanFor();
        if (plan is null) { Say("The game data is not ready yet.", true); return; }
        _join(new JoinRequest(host, plan, _services, _services.Content, _spectate?.ButtonPressed ?? false));
    }

    /// <summary>Sets the Join screen's message line; empty hides it (the UI self-test's fit check measures the screen with and without one).</summary>
    public void ShowMessage(string text, bool problem = false) => Say(text, problem);

    /// <summary>The Join screen has the Host panel (a server executable was found next to the client or given with <c>--server-exe</c>).</summary>
    public bool HasHostPanel => _hostPanel is not null;

    void Say(string text, bool problem = false)
    {
        if (_message is null) return;
        _message.Text = text;
        _message.Visible = text.Length > 0;
        _message.AddThemeColorOverride("font_color", problem ? UiKit.LightRed : UiKit.White);
    }

    // ---- scripted views (screenshots) ----

    void RunScript()
    {
        var args = _args;
        if (args.ContainsKey("demo-profile")) DemoProfile();
        // --controls-screen [keyboard|controller] [--controls-conflict]: the CONTROLS screen (ui/keybinds screenshots); with --controls-conflict an input in use is pressed, so the prompt shows.
        if (args.ContainsKey("controls-screen"))
        {
            ShowControls();
            _controls!.Open(args["controls-screen"] is { } which && which.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? BindDevice.Controller : BindDevice.Keyboard);
            if (args.ContainsKey("controls-conflict")) _controls.DemoConflict();
        }
        if (args.ContainsKey("demo-look")) DemoLook();
        // --loadout <default:role|1..10>: the loadout to open with (held in memory, not saved), as at a --connect launch.
        if (args.GetValueOrDefault("loadout") is { } wanted && LaunchOptions.ParseLoadout(wanted, _services.Catalog!) is { } chosen && LoadoutEdits.IsValid(_services.Session!.Working, chosen))
        {
            _services.Session.Seed(LoadoutEdits.Select(_services.Session.Working, chosen));
            _creator?.OpenSelection(chosen);
            RefreshSummary();
        }
        if (args.GetValueOrDefault("slot") is { } slotText && int.TryParse(slotText, out int slot) && slot is >= 1 and <= 10 && _creator is not null)
            _creator.OpenSelection(Selection.CustomSlot(slot));
        if (args.GetValueOrDefault("role") is { } role && LaunchOptions.ParseLoadout("default:" + role, _services.Catalog!) is { } selection) _creator?.OpenSelection(selection);
        if (args.GetValueOrDefault("step") is { } step) _creator?.OpenStep(step);
        if (args.GetValueOrDefault("preview-side") is { } side) _creator?.SetPreviewRace(side.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? CustomizationCatalog.Chaos : CustomizationCatalog.SpaceMarine);
        if (_appearance?.Visible == true)
        {
            string race = args.GetValueOrDefault("race") is { } r && r.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? CustomizationCatalog.Chaos : _services.Settings.Side == SideChoice.Chaos ? CustomizationCatalog.Chaos : CustomizationCatalog.SpaceMarine;
            int appearanceSlot = _creator is { Open.Kind: SelectionKind.Custom } ? _creator.Open.Slot : 1;
            _appearance.Open(appearanceSlot, race, args.GetValueOrDefault("section"));
            if (ModelViewer.EmblemFile is not null && args.ContainsKey("import")) _appearance.OpenImporter();
            if (args.GetValueOrDefault("part") is { } part) _appearance.OpenSection(args.GetValueOrDefault("section") ?? "armour", part);
        }
    }

    /// <summary>
    /// <c>--demo-look</c> (with <c>--demo-profile</c>; held in memory): a Space Marine look that uses every kind of edit, for screenshots of the appearance screens:
    /// a chapter, another helmet, a colour on the torso, a badge, an own emblem on the left pad and a red lens.
    /// </summary>
    void DemoLook()
    {
        var catalog = _services.Catalog!;
        var session = _services.Session!;
        var race = catalog.SpaceMarines;
        var p = session.Working;
        if (p.ClassAt(1) is null) return;
        var (withLook, look) = AppearanceEdits.EnsureLook(p, 1, CustomizationCatalog.SpaceMarine);
        if (look is null) return;
        var preset = race.Presets.FirstOrDefault(x => x.Name.Contains("blood_angels", StringComparison.OrdinalIgnoreCase)) ?? race.Presets[Math.Min(3, race.Presets.Count - 1)];
        look = AppearanceEdits.SetPreset(look, preset, catalog);
        if (race.PiecesFor("head").Count > 2) look = AppearanceEdits.SetPiece(look, "head", race.PiecesFor("head")[2].Name);
        if (catalog.Palette.Count > 12) look = AppearanceEdits.SetColour(look, ["torso"], PaintChannel.Primary, catalog.Palette[12].Name, null);
        if (race.Decals.FirstOrDefault(d => !d.IsBlank) is { } badge) look = AppearanceEdits.SetDecal(look, "rightshoulder", DecalChoice.ForBadge(badge.Name));
        // An own emblem drawn here (a ring and a cross), cropped with the default margin, stored like an imported one.
        var rgba = new byte[256 * 256 * 4];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                double d = Math.Sqrt((x - 128.0) * (x - 128.0) + (y - 128.0) * (y - 128.0));
                bool ring = d is > 70 and < 105, cross = Math.Abs(x - 128) < 12 && d < 60 || Math.Abs(y - 128) < 12 && d < 60;
                int i = (y * 256 + x) * 4;
                rgba[i] = (byte)(ring || cross ? 245 : 120); rgba[i + 1] = (byte)(ring ? 190 : cross ? 245 : 20); rgba[i + 2] = (byte)(ring ? 20 : cross ? 245 : 30); rgba[i + 3] = 255;
            }
        var emblem = EmblemPipeline.Add(_services.Store, EmblemPipeline.Build(rgba, 256, 256, EmblemPipeline.CropFor(256, 256, EmblemPipeline.InsetZoom(256, 256), 128f, 128f))).Hash;
        if (!emblem.IsNone) look = AppearanceEdits.ApplyEmblem(look, ["leftshoulder"], emblem.ToHex()).Look;
        look = AppearanceEdits.SetLens(look, new LensColour(255, 40, 40));
        session.Seed(AppearanceEdits.ReplaceAppearance(withLook, look));
    }

    /// <summary>A few loadouts to look at (<c>--demo-profile</c>; held in memory, never written unless you save).</summary>
    void DemoProfile()
    {
        var catalog = _services.Catalog!;
        var session = _services.Session!;
        var p = CustomizationProfile.Default(catalog);
        var roles = ProfileStore.Roles(catalog);
        p = LoadoutEdits.Create(p, 1, "tactical", catalog).Profile;
        p = LoadoutEdits.Rename(p, 1, "Bolter line").Profile;
        p = LoadoutEdits.Create(p, 2, roles.Contains("heavy") ? "heavy" : roles[0], catalog).Profile;
        p = LoadoutEdits.Rename(p, 2, "Heavy support").Profile;
        p = LoadoutEdits.Create(p, 3, roles.Contains("assault") ? "assault" : roles[0], catalog).Profile;
        p = LoadoutEdits.Rename(p, 3, "Jump trooper").Profile;
        p = LoadoutEdits.Select(p, Selection.CustomSlot(1));
        session.Seed(p);
        _strip?.Refresh();
        RefreshSummary();
        _creator?.Reenter();
    }
}
