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

/// <summary>What a join hands over: the server, the plan for the active loadout (class, item indices, perks, looks) and the shared services.</summary>
/// <param name="Content">The content profile the match is joined with (added content E2: the lobby's choice, or <c>--content</c>); null: <c>--content</c> / the default.</param>
public sealed record JoinRequest(string Host, JoinPlan Plan, CustomizerServices Services, SmSim.ContentExtras? Content = null, bool Spectate = false);

/// <summary>
/// The front end (ui/menu-rebuild, the owner's Half-Life 2 style menu): a shell that waits for the install to be read, then shows the main menu (PLAYER VERSUS PLAYER, MUSOU MODE, OPTIONS,
/// CUSTOMIZER, QUIT) over the shared background image and its pages in turn; the customizer keeps its own dark background. It owns the 1920 x 1080 UI space of the window while it is shown
/// and gives it back when a match starts. A join goes through <see cref="JoinRequest"/>; <c>ModelViewer</c> turns it into the existing connect path.
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
    CreatorView? _creator;
    AppearanceView? _appearance;
    ControlsView? _controls;
    UiKit.MenuPage? _main, _lobby, _hostPage, _joinLocalPage, _page;
    OptionsView? _options;
    readonly Dictionary<UiKit.MenuPage, Control> _first = new();
    LineEdit? _address;
    Label? _activeLabel, _summary, _message, _contentNote;
    UiKit.MenuChoice? _sideRow, _contentRow, _spectate;
    HostPanel? _hostPanel;

    /// <summary>The menu pages (the self-test's tree and fit checks read them).</summary>
    public UiKit.MenuPage? MainPage => _main;
    public UiKit.MenuPage? LobbyPage => _lobby;
    public UiKit.MenuPage? HostPage => _hostPage;
    public UiKit.MenuPage? JoinLocalPage => _joinLocalPage;
    public OptionsView? Options => _options;
    /// <summary>The Host panel, or null when no server executable was found.</summary>
    public HostPanel? JoinHostPanel => _hostPanel;

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

    /// <summary>The player launched with <c>--content musou</c> (a test aid that keeps working and never overwrites the remembered profile).</summary>
    bool CommandLineMusou => _args.GetValueOrDefault("content") == ContentProfiles.Musou;

    public CustomizerServices Services => _services;
    public CreatorView? Creator => _creator;
    public AppearanceView? Appearance => _appearance;
    public bool ShowsMainMenu => _main?.Visible == true;

    readonly string? _startMessage;

    /// <param name="startMessage">Shown on the main menu when it opens (why the last join failed).</param>
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
        AddChild(UiKit.MenuBackground().With(n => n.SetAnchorsPreset(LayoutPreset.FullRect)));
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
        SyncMusouRow();
        if (!_warmed) _warmed = _services.Previews.WarmStep();   // the default look is built while the menu is up
        if (!_scripted && _frames > 3) { _scripted = true; _scriptFrame = _frames; RunScript(); }
        else if (_scripted && !_autoJoined && _args.ContainsKey("auto-join") && _frames > _scriptFrame + 10) { _autoJoined = true; JoinLocal(); }
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
        BuildPages();
        _creator = new CreatorView(_services, ShowMainMenu, ShowAppearance) { Visible = false };
        _appearance = new AppearanceView(_services, ShowCreatorAgain) { Visible = false };
        AddChild(_creator);
        AddChild(_appearance);
        _controls = new ControlsView(() => ShowOptions(OptionsView.Controls));   // ui/keybinds (ours)
        AddChild(_controls);
        _message = UiKit.MenuText("", null, clip: false);
        _message.Name = "MenuMessage";
        _message.ClipText = false;
        _message.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _message.VerticalAlignment = VerticalAlignment.Bottom;
        _message.Visible = false;
        _message.SetAnchorsPreset(LayoutPreset.BottomLeft);
        _message.GrowVertical = GrowDirection.Begin;
        _message.OffsetLeft = UiKit.MenuLeft + 1f; _message.OffsetRight = UiKit.MenuLeft + 1f + 1100f; _message.OffsetBottom = -24f; _message.OffsetTop = -24f - 90f;
        AddChild(_message);
        if (_args.ContainsKey("ui-selftest")) AddChild(new UiSelfTest(this, _services));
        // The profile's own notices (a repaired, corrupt or read-only file) show once.
        var notices = _services.Session!.Notices();
        if (notices.Count > 0) Say(string.Join(" ", notices), true);
        if (_startMessage is { Length: > 0 }) Say(_startMessage, true);
        switch (_start)
        {
            case StartScreen.Creator: ShowCreator(); break;
            case StartScreen.Appearance: ShowAppearance(); break;
            default: ShowMainMenu(); break;
        }
    }

    /// <summary>The tree of pages (the owner's menu, 2026-11-01): main, PLAYER VERSUS PLAYER, HOST LOCAL LOBBY, JOIN LOCAL LOBBY and OPTIONS; the customizer and the keybinds are their own screens.</summary>
    void BuildPages()
    {
        // The remembered Musou of an earlier build is not a lobby profile any more: MUSOU MODE is the way in (a --content musou launch keeps it).
        if (_services.Settings.ContentExtras.HasFlag(ContentExtras.Musou) && !CommandLineMusou) _services.SetContent(ContentExtras.All);

        var main = new UiKit.MenuPage("MainMenu");
        var joinRow = UiKit.MenuButton("Player versus Player", () => ShowPage(_lobby!));
        _first[main] = joinRow;
        main.Rows.AddChild(joinRow);
        if (ContentExtras.Compiled)   // added content: never in a faithful build
        {
            main.Rows.AddChild(UiKit.MenuButton("Musou Mode", StartMusou, "The war of the three fronts, on this PC"));
            _stopMusou = UiKit.MenuButton("Stop Musou server", StopMusou, "Stops the Musou server this game started on this PC");
            _stopMusou.Visible = false;   // only while that server runs (SyncMusouRow)
            main.Rows.AddChild(_stopMusou);
        }
        main.Rows.AddChild(UiKit.MenuButton("Options", () => ShowOptions()));
        main.Rows.AddChild(UiKit.MenuButton("Customizer", ShowCreator, "Edit your loadouts and your armour"));
        main.Rows.AddChild(UiKit.MenuButton("Quit", () => GetTree().Quit()));
        _main = main;

        var lobby = new UiKit.MenuPage("LobbyMenu", "Join or host lobby");
        // The dedicated server is bundled next to the client (beside it, in server\ below it, or in ..\server: the tester zip's game\ and server\). The UI self-test always builds the
        // panel (it never presses HOST A MATCH), with or without a server next to the client.
        string? serverExe = ServerLaunch.FindExecutable(OS.GetExecutablePath().GetBaseDir(), _args.GetValueOrDefault("server-exe"))
            ?? (_args.ContainsKey("ui-selftest") ? "SmServer (UI self-test: not started)" : null);
        var hostRow = UiKit.MenuButton("Host local lobby", () => ShowPage(_hostPage!));
        if (serverExe is null) { hostRow.Disabled = true; hostRow.TooltipText = "No server was found next to the game."; }
        _first[lobby] = hostRow;
        lobby.Rows.AddChild(hostRow);
        lobby.Rows.AddChild(UiKit.MenuButton("Join server", JoinPublicServer, "Connects to the project's server, " + JoinTargets.PublicServer));
        lobby.Rows.AddChild(UiKit.MenuButton("Join local lobby", () => ShowPage(_joinLocalPage!), "A lobby hosted on another PC: type its address"));
        lobby.Rows.AddChild(UiKit.MenuGap(0.5f));
        lobby.Rows.AddChild(_sideRow = new UiKit.MenuChoice("Side", ["AUTO", "SPACE MARINES", "CHAOS"], (int)_services.Settings.Side, i => { _services.UpdateSettings(x => x with { Side = (SideChoice)i }); RefreshSummary(); }, "Auto lets the server balance the teams."));
        lobby.Rows.AddChild(BuildContentRow());
        // Observer tooling (protocol 46; ours): join as a spectator (a free camera, no side, slot or score) instead of a player. Not remembered: every Join is a player's unless this is set again.
        lobby.Rows.AddChild(_spectate = new UiKit.MenuChoice("Spectate", ["OFF", "ON"], 0, null, "Watch with a free camera instead of playing (footage tool)"));
        lobby.Rows.AddChild(UiKit.MenuGap(0.5f));
        lobby.Rows.AddChild(_activeLabel = UiKit.MenuText("", UiKit.White));
        lobby.Rows.AddChild(_summary = UiKit.MenuText("", UiKit.Grey));
        lobby.Rows.AddChild(_contentNote = UiKit.MenuText("", UiKit.Grey));
        lobby.Rows.AddChild(UiKit.MenuGap(0.5f));
        lobby.Rows.AddChild(UiKit.MenuButton("Back", () => ShowMainMenu()));
        _lobby = lobby;

        var host = new UiKit.MenuPage("HostMenu", "Host local lobby");
        if (serverExe is not null)
        {
            _hostPanel = new HostPanel(serverExe, _services.GameDir, _args, ConnectToHost, Say, () => _services.Content, _services, () => ShowPage(_lobby!));
            _hostPanel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            host.Rows.AddChild(_hostPanel);
        }
        _hostPage = host;

        var joinLocal = new UiKit.MenuPage("JoinLocalMenu", "Join local lobby");
        _address = new LineEdit { Text = _args.GetValueOrDefault("host") ?? _services.Settings.Host, PlaceholderText = "host:port", MaxLength = 253, FocusMode = FocusModeEnum.All, SelectAllOnFocus = true, Name = "LobbyAddress" };
        _address.TextSubmitted += _ => JoinLocal();
        _address.TextChanged += _ => Say("");
        joinLocal.Rows.AddChild(UiKit.MenuField("Address", _address));
        joinLocal.Rows.AddChild(UiKit.MenuText("HOST OR HOST:PORT (DEFAULT PORT 27015)", UiKit.Grey));
        joinLocal.Rows.AddChild(UiKit.MenuGap(0.5f));
        joinLocal.Rows.AddChild(UiKit.MenuButton("Connect", JoinLocal));
        joinLocal.Rows.AddChild(UiKit.MenuButton("Back", () => ShowPage(_lobby!)));
        _joinLocalPage = joinLocal;

        var foot = UiKit.VBox(2);
        if (GameFoundNote is { Length: > 0 }) foot.AddChild(UiKit.MenuText(GameFoundNote, UiKit.Grey).With(l => l.TooltipText = GameFoundNote));
        foot.AddChild(new LinkButton { Text = "Builds and issues: " + ServerLaunch.ReleasesUrl.Replace("https://", ""), Uri = ServerLaunch.ReleasesUrl }.With(l => { l.AddThemeFontSizeOverride("font_size", UiKit.MenuTextSize); l.AddThemeColorOverride("font_color", UiKit.Grey); l.AddThemeColorOverride("font_hover_color", UiKit.White); }));
        _options = new OptionsView(_services, () => ShowMainMenu(), ShowControls, foot);

        foreach (var page in new Control[] { _main, _lobby, _hostPage, _joinLocalPage, _options }) { page.Visible = false; AddChild(page); }
        RefreshContent();
    }

    // ---- the content profile (added content E2, docs/added-content.md) ----

    /// <summary>
    /// CONTENT in the lobby menu: Faithful (SM1 multiplayer alone) or Extended (the added content this build has: the Ork Boy, later gore). The choice is remembered next to the profile, is what
    /// the joins and the host use, and must match the server's (a server with another profile refuses the client and says which it runs). A build without added content has only Faithful.
    /// Musou is not a choice here: it has its own row on the main menu (the owner, 2026-11-01).
    /// </summary>
    Control BuildContentRow()
    {
        string[] names = ContentExtras.Compiled ? [ContentProfiles.Faithful, ContentProfiles.Extended] : [ContentProfiles.Faithful];
        _contentRow = new UiKit.MenuChoice("Content", names.Select(n => n.ToUpperInvariant()).ToArray(), 0, i =>
        {
            ContentProfiles.TryParse(names[i], out var extras, out _);
            _services.SetContent(extras);
            RefreshContent();
            RefreshSummary();
        });
        return _contentRow;
    }

    /// <summary>The Host panel's server fixes the profile while it runs (a client with another one is refused).</summary>
    void SyncContentLock()
    {
        if (_contentRow is null) return;
        bool hosting = HostedServer.Running;
        if (hosting == _contentLocked) return;
        _contentLocked = hosting;
        _contentRow.SetDisabled(hosting);
        if (!hosting && _services.Content.HasFlag(ContentExtras.Musou) && !CommandLineMusou) _services.Content = _services.Settings.ContentExtras;   // a stopped Musou server ends the session's Musou
        RefreshContent();
    }

    bool _contentLocked;

    void RefreshContent()
    {
        var extras = _services.Content;
        _contentRow?.SetIndex(extras == ContentExtras.None ? 0 : 1);
        if (_contentNote is null) return;
        _contentNote.Text = (_contentLocked ? $"Content is fixed while your server runs ({ContentProfiles.Name(extras)}). Stop the server to change it."
            : !ContentExtras.Compiled ? "This build has no added content: faithful only."
            : extras == ContentExtras.None ? "SM1 multiplayer alone. The server must run --content faithful."
            : "Adds the Ork Boy (and later gore). The server must run the same profile (the default).").ToUpperInvariant();
    }

    /// <summary>Where the game folder was found (shown small under OPTIONS); set by the launcher.</summary>
    public string? GameFoundNote { get; init; }

    /// <summary>The Host panel's hand-over: join the server it started.</summary>
    void ConnectToHost(string host) => TryJoin(host, remember: false);

    void RefreshSummary()
    {
        var session = _services.Session;
        var catalog = _services.Catalog;
        if (session is null || catalog is null || _activeLabel is null) return;
        var profile = session.Working;
        var active = LoadoutEdits.Active(profile, catalog, out bool fellBack);
        var plan = _services.PlanFor(active);
        string name = active.Kind == SelectionKind.Custom && profile.ClassAt(active.Slot) is { } c ? $"CUSTOM {active.Slot}  \"{c.Name}\"" : "DEFAULTS";
        _activeLabel.Text = "ACTIVE LOADOUT: " + name;
        _activeLabel.TooltipText = _activeLabel.Text;
        _summary!.Text = (plan?.Summary ?? "").ToUpperInvariant();
        _summary.TooltipText = _summary.Text;
        if (fellBack && profile.Selected.Kind == SelectionKind.Custom)
            Say($"Slot {profile.Selected.Slot} is empty, so {(active.Kind == SelectionKind.Custom ? $"slot {active.Slot}" : "the Tactical defaults")} is used.", false);
    }

    // ---- screens ----

    void HideAll()
    {
        foreach (var page in new Control?[] { _main, _lobby, _hostPage, _joinLocalPage, _options }) if (page is not null) page.Visible = false;
        _controls?.Close();
        _creator!.Visible = false; _appearance!.Visible = false;
        _page = null;
    }

    void ShowPage(UiKit.MenuPage page)
    {
        HideAll();
        page.Visible = true;
        _page = page;
        Say("");
        RefreshSummary();
        RefreshContent();
        if (page == _hostPage) _hostPanel?.FocusFirst();
        else if (page == _joinLocalPage) { _address!.Text = _args.GetValueOrDefault("host") ?? _services.Settings.Host; _address.FocusLater(); }
        else _first.GetValueOrDefault(page)?.FocusLater();
    }

    /// <summary>The main menu. A session's Musou ends here: the lobby's remembered profile is back (unless a server fixes it, or <c>--content musou</c> asked for it).</summary>
    public void ShowMainMenu()
    {
        if (!_built) return;
        if (_services.Content.HasFlag(ContentExtras.Musou) && !CommandLineMusou && !HostedServer.Running) _services.Content = _services.Settings.ContentExtras;
        SyncMusouRow();
        string kept = _message?.Text ?? "";
        bool wasProblem = _messageProblem;
        ShowPage(_main!);
        if (_opening) { _opening = false; if (kept.Length > 0) Say(kept, wasProblem); }   // the notices of the start stay on the first page
    }

    bool _opening = true, _messageProblem;

    /// <summary>Shows a page by name (<c>--menu-screen</c>, the self-test): lobby, host, joinlocal or a tab of options.</summary>
    public void ShowMenuPage(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "lobby": ShowPage(_lobby!); break;
            case "host": ShowPage(_hostPage!); break;
            case "joinlocal": ShowPage(_joinLocalPage!); break;
            case "graphics": ShowOptions(OptionsView.Graphics); break;
            case "sound": ShowOptions(OptionsView.Sound); break;
            case "controls" or "options": ShowOptions(OptionsView.Controls); break;
            default: ShowMainMenu(); break;
        }
    }

    /// <summary>OPTIONS, on a tab (null: the last one).</summary>
    public void ShowOptions(string? tab = null)
    {
        if (!_built) return;
        HideAll();
        Say("");
        _options!.Open(tab);
        _page = null;   // it takes its own Esc and B
    }

    /// <summary>The keybind screen (key and controller rebinding; ui/keybinds), reached from OPTIONS, CONTROLS.</summary>
    public void ShowControls()
    {
        if (!_built) return;
        HideAll();
        _controls!.Open();
    }

    /// <summary>The keybind screen, or null before the screens are built (the self-test and the screenshots read it).</summary>
    public ControlsView? Controls => _controls;

    public void ShowCreator()
    {
        if (!_built) return;
        HideAll();
        _creator!.Visible = true;
        _creator.Reenter();
        _creator.FocusList();
    }

    void ShowCreatorAgain()
    {
        HideAll();
        _creator!.Visible = true;
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
            HideAll();
            _creator.Visible = true;
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
        HideAll();
        _appearance!.Visible = true;
        _appearance.Open(slot, race);
    }

    /// <summary>Esc or B steps back one page; the main menu stays.</summary>
    public override void _Input(InputEvent e)
    {
        if (_page is null || _page == _main || !_page.IsVisibleInTree() || !PadHost.IsBack(e)) return;
        GetViewport().SetInputAsHandled();
        if (_page == _lobby) ShowMainMenu(); else ShowPage(_lobby!);
    }

    // ---- joining ----

    /// <summary>JOIN SERVER: the fixed address, nothing to type (<see cref="JoinTargets.PublicServer"/>).</summary>
    void JoinPublicServer() => TryJoin(JoinTargets.PublicServer, remember: false);

    /// <summary>CONNECT in JOIN LOCAL LOBBY: the one place an address is typed; it is remembered for next time.</summary>
    void JoinLocal()
    {
        string host = _address!.Text.Trim();
        if (FrontEndSettings.ValidateHost(host) is { } problem) { Say(problem, true); _address.GrabFocus(); return; }
        TryJoin(host, remember: true);
    }

    void TryJoin(string host, bool remember)
    {
        if (_services.Session!.Dirty)
        {
            // Unsaved edits are not part of what you join with.
            _services.Session.Revert();
        }
        if (remember) _services.UpdateSettings(x => x with { Host = host });
        var plan = _services.PlanFor();
        if (plan is null) { Say("The game data is not ready yet.", true); return; }
        var request = new JoinRequest(host, plan, _services, _services.Content, _spectate?.Index == 1);
        LastJoin = request;
        if (JoinOverride is { } test) test(request); else _join(request);
    }

    /// <summary>MUSOU MODE (the owner, 2026-11-01): Musou Mode as the Host panel's Musou path starts it: this PC only, <c>--content musou</c>, joined by itself. The lobby's remembered profile is not touched.</summary>
    void StartMusou()
    {
        var (running, ready, musou) = MusouServerState();
        switch (MusouLocal.OnPress(_hostPanel is not null, running, ready, musou))
        {
            case MusouPress.NoServer: Say("No server was found next to the game, so Musou cannot start.", true); return;
            case MusouPress.OtherServer: Say("A server is running. Stop it in HOST LOCAL LOBBY first.", true); return;
            case MusouPress.Wait:   // the first press started it and the Host panel joins by itself once it listens: no second server, no early join
                Say(MusouLocal.StartingText, false);
                return;
            case MusouPress.Join:
                _services.Content = ContentExtras.Musou;
                TryJoin(ServerLaunch.LocalHost(HostedServer.Options?.Port ?? ServerLaunch.DefaultPort), remember: false);
                return;
        }
        _services.Content = ContentExtras.Musou;
        if (MusouOverride is { } test) { test(); return; }
        _hostPanel!.StartHosting();
        if (!HostedServer.Running) _services.Content = _services.Settings.ContentExtras;   // it could not start: the message says why
    }

    /// <summary>
    /// Test aid <c>--auto-musou --musou-cycle</c> (fix/musou-local-server end-to-end run): after the first press, presses MUSOU MODE again while the server starts, lets the
    /// start finish and the client join (the join is logged instead of loading a match), presses it a third time (joins the running server), presses STOP MUSOU SERVER
    /// and logs whether the very process it started is gone, then quits. Every step prints a "musou-cycle:" line.
    /// </summary>
    async void StartMusouCycle()
    {
        int joins = 0;
        JoinOverride = r => { joins++; GD.Print($"musou-cycle: join #{joins} -> {r.Host}, content {ContentProfiles.Name(r.Content ?? ContentExtras.None)}"); };
        int? pid = HostedServer.ProcessId;
        GD.Print($"musou-cycle: first press started server pid {pid}, ready {HostedServer.Ready}, bind this PC only {HostedServer.Options?.LocalOnly}, port {HostedServer.Options?.Port}, content {HostedServer.Options?.Content}");
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
        StartMusou();   // second press while it starts
        GD.Print($"musou-cycle: second press while starting: server pid {HostedServer.ProcessId} (same: {HostedServer.ProcessId == pid}), joins {joins}, stop row shown {_stopMusou?.Visible}");
        for (int i = 0; i < 600 && joins < 1 && HostedServer.Running; i++) await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"musou-cycle: start finished: ready {HostedServer.Ready}, joins {joins}");
        StartMusou();   // third press: a running Musou server is joined
        GD.Print($"musou-cycle: press on the ready server: joins {joins}, server pid {HostedServer.ProcessId} (same: {HostedServer.ProcessId == pid}), stop row shown {_stopMusou?.Visible}");
        StopMusou();
        bool alive = false;
        if (pid is { } p) { try { alive = !System.Diagnostics.Process.GetProcessById(p).WaitForExit(5000); } catch (ArgumentException) { } }
        GD.Print($"musou-cycle: STOP MUSOU SERVER: pid {pid} still alive {alive}, running {HostedServer.Running}, stop row shown {_stopMusou?.Visible}, content {ContentProfiles.Name(_services.Content)}");
        GetTree().Quit();
    }

    /// <summary>The server this client started: running, listening, and Musou. (Self-test: <see cref="MusouStateForTest"/> stands in.)</summary>
    (bool Running, bool Ready, bool Musou) MusouServerState() =>
        MusouStateForTest ?? (HostedServer.Running, HostedServer.Ready, HostedServer.Options is { Content: ContentProfiles.Musou });

    public (bool Running, bool Ready, bool Musou)? MusouStateForTest { get; set; }

    /// <summary>STOP MUSOU SERVER: stops the server this client started (only that process), puts the lobby's profile back and hides the row.</summary>
    void StopMusou()
    {
        _hostPanel?.StopHosted(MusouLocal.StoppedText);   // HostedServer.Stop: the one process started here; the Host panel's own state is reset with it
        if (!CommandLineMusou) _services.Content = _services.Settings.ContentExtras;
        SyncMusouRow();
    }

    /// <summary>The row follows the server: it is shown while a Musou server of this client runs, wherever the player came from (a match, the Host panel) and when the server dies by itself.</summary>
    void SyncMusouRow()
    {
        if (_stopMusou is null) return;
        var (running, _, musou) = MusouServerState();
        bool show = MusouLocal.ShowStopRow(running, musou);
        if (_stopMusou.Visible == show) return;
        _stopMusou.Visible = show;
        if (!show && _stopMusou.HasFocus() && _main is not null) _first.GetValueOrDefault(_main)?.FocusLater();   // the focus must not be lost with the row
    }

    Button? _stopMusou;

    /// <summary>Self-test: receives what a join would hand over, so no match starts.</summary>
    public Action<JoinRequest>? JoinOverride { get; set; }
    /// <summary>Self-test: MUSOU MODE stops before starting the server.</summary>
    public Action? MusouOverride { get; set; }
    /// <summary>What the last join asked for.</summary>
    public JoinRequest? LastJoin { get; private set; }

    /// <summary>Sets the message line (empty hides it).</summary>
    public void ShowMessage(string text, bool problem = false) => Say(text, problem);

    /// <summary>The front end has the Host panel (a server executable was found next to the client or given with <c>--server-exe</c>).</summary>
    public bool HasHostPanel => _hostPanel is not null;

    void Say(string text, bool problem = false)
    {
        if (_message is null) return;
        _message.Text = text;
        _message.Visible = text.Length > 0;
        _messageProblem = problem;
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
        // --menu-screen lobby|host|joinlocal|graphics|sound|controls: opens that page (screenshots).
        if (args.GetValueOrDefault("menu-screen") is { } menuScreen) ShowMenuPage(menuScreen);
        if (args.ContainsKey("auto-musou")) StartMusou();   // test aid: presses MUSOU MODE once
        if (args.ContainsKey("auto-musou") && args.ContainsKey("musou-cycle")) StartMusouCycle();
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
    /// a chapter, another helmet, a colour on the torso, a badge, an own emblem on the left pad and a red lens (<c>--demo-look r,g,b</c>: that lens colour instead).
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
        var lens = new LensColour(255, 40, 40);
        if (_args.GetValueOrDefault("demo-look")?.Split(',') is { Length: 3 } c && byte.TryParse(c[0], out var lr) && byte.TryParse(c[1], out var lg) && byte.TryParse(c[2], out var lb)) lens = new LensColour(lr, lg, lb);
        look = AppearanceEdits.SetLens(look, lens);
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
        RefreshSummary();
        _creator?.Reenter();
    }
}
