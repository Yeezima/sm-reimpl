using System;
using System.Collections.Generic;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;
using SmContent;

namespace SmClient.FrontEnd;

/// <summary>
/// "Host a match" on the Join screen (ours; docs/changes-from-original.md): starts the bundled dedicated server next to the client, then joins it at 127.0.0.1.
/// Mode, port and who may connect are chosen here. The panel is only built when a server executable was found (<see cref="ServerLaunch.FindExecutable"/>).
/// A server started here keeps running when you leave a match and is stopped when the client quits (<see cref="HostedServer"/>).
/// "This PC only" is the default; letting other players in is an explicit choice, and hosted games refuse custom emblem images unless the host allows them.
/// fix/connect-timeout (the owner's report and requests of 2026-10-29): the start is watched (listening line or exit; a taken port says so and is never joined);
/// STOP SERVER also stops a server of this install started elsewhere (the release's host launcher). fix/hosting-bots-logs (the owner's decisions of 2026-10-29, after a
/// tester's 8 GB laptop ran out of memory with the old default of 7 bots): the BOTS stepper sets how many test bots the hosted match gets, 0 ("No bots", the default in
/// every reach, never remembered) to 15, split over the sides with the side with fewer players first; the number applies when the server starts (stop the server and host
/// again to change it); Easy scales their damage (<see cref="HostedBots"/>, rules <see cref="HostBots"/>).
/// Test aids (all optional): <c>--auto-host</c> presses the button once, with <c>--host-mode annihilation|seize</c>, <c>--host-port N</c>, <c>--host-lan</c>, <c>--host-emblems</c>,
/// <c>--host-bots 0..15|none|4v4|8v8</c> and <c>--host-difficulty easy|normal</c> (neither saved); <c>--host-stop-others</c> presses STOP SERVER once a server of this install
/// started elsewhere is seen.
/// </summary>
public sealed partial class HostPanel : PanelContainer
{
    readonly string _exe, _gameDir;
    readonly Action<string> _connect;
    readonly Action<string, bool> _say;
    readonly Dictionary<string, string?> _args;
    readonly Func<SmSim.ContentExtras> _content;   // the Join screen's content profile (added content E2): the server is started with it
    readonly CustomizerServices? _services;        // the remembered bots' difficulty and the side asked for (fix/connect-timeout)

    string _mode = ServerLaunch.ModeAnnihilation;
    bool _localOnly = true, _emblems;
    LineEdit? _port;
    Label? _note, _runningText;
    Control? _setup, _running;
    Button? _hostButton, _stopOther, _fewerBots, _moreBots;
    Label? _botsLabel;
    OptionButton? _difficultyOption;
    int _botCount = HostBots.DefaultCount;   // fix/hosting-bots-logs: never remembered; 0 = no bots
    readonly List<(string Mode, Button Button)> _modeButtons = new();
    readonly List<(bool Local, Button Button)> _reachButtons = new();
    bool? _musouShown;   // the Musou lock last applied to the mode, reach and bots rows (null: not yet)
    (string Mode, bool LocalOnly)? _beforeMusou;   // the mode and reach chosen before the Musou lock, restored when it lifts
    bool _pendingJoin;
    double _startedAt;
    bool _autoDone, _stopOtherDone;
    int _frames;
    double _nextInstallCheck;
    int _installServers;   // servers of this install running that this panel did not start (polled once a second)
    BotDifficulty? _difficultyOverride;

    public HostPanel(string exe, string gameDir, Dictionary<string, string?> args, Action<string> connect, Action<string, bool> say, Func<SmSim.ContentExtras> content,
        CustomizerServices? services = null)
    {
        _exe = exe; _gameDir = gameDir; _args = args; _connect = connect; _say = say; _content = content; _services = services;
        Name = "HostPanel";
        _localOnly = !args.ContainsKey("host-lan");
        _emblems = args.ContainsKey("host-emblems");
        if (args.GetValueOrDefault("host-mode") is { } m && m is ServerLaunch.ModeAnnihilation or ServerLaunch.ModeSeize) _mode = m;
        _botCount = HostBots.ParseCount(args.GetValueOrDefault("host-bots")) ?? HostBots.DefaultCount;
        _difficultyOverride = HostBots.ParseDifficulty(args.GetValueOrDefault("host-difficulty"));
    }

    /// <summary>The number of bots the next hosted match gets (the BOTS stepper; the test aid <c>--host-bots</c>; default none in every reach, never remembered).</summary>
    public int BotCount => Musou ? 0 : _botCount;

    /// <summary>
    /// The Join screen's profile is Musou (experimental added content): the hosted server is this PC only (127.0.0.1), Annihilation (the mode row is locked: the
    /// mode comes from the profile), without bots (the enemies are the server's own), and the button reads START MUSOU; the usual hand-over joins it.
    /// </summary>
    public bool Musou => _content().HasFlag(SmSim.ContentExtras.Musou);

    /// <summary>The host button's text (self-test).</summary>
    public string HostButtonText => _hostButton?.Text ?? "";

    /// <summary>Applies the Musou lock to the rows when the profile changes (pressed states follow the forced choice; the rows come back unlocked after).</summary>
    void ApplyMusou()
    {
        bool musou = Musou;
        if (_musouShown == musou) return;
        _musouShown = musou;
        if (musou) { _beforeMusou ??= (_mode, _localOnly); _mode = ServerLaunch.ModeAnnihilation; _localOnly = true; }
        else if (_beforeMusou is { } before) { (_mode, _localOnly) = before; _beforeMusou = null; }
        foreach (var (mode, b) in _modeButtons) { b.Disabled = musou; b.SetPressedNoSignal(_mode == mode); }
        foreach (var (local, b) in _reachButtons) { b.Disabled = musou; b.SetPressedNoSignal(_localOnly == local); }
        if (_hostButton is not null)
        {
            _hostButton.Text = musou ? "START MUSOU" : "HOST A MATCH";
            _hostButton.TooltipText = musou ? "Start a Musou server on this PC only and join it" : "Start a server on this PC and join it";
        }
        RefreshNote();
    }
    public BotDifficulty Difficulty => _difficultyOverride ?? _services?.Settings.BotDifficulty ?? BotDifficulty.Easy;
    bool HumanChaos => _services?.Settings.Side == SideChoice.Chaos;

    const string BotsTooltip = "Bots are headless test players started on this PC with the server, split over the two sides (the side with fewer players first). " +
                               "Each is a game process of about 0.27 GB of memory and about 5% of one processor core: it loads no graphics, effects or sounds and runs at 30 frames a second " +
                               "(7 bots: about 1.9 GB), so on a PC with 8 GB keep to a few. The number applies when the server starts: " +
                               "to change it, stop the server and host again.";

    public override void _Ready()
    {
        var content = UiKit.VBox(8);

        // ---- before: choices ----
        var setup = UiKit.VBox(8);
        var row = UiKit.HBox(12);
        row.AddChild(UiKit.Text("HOST", UiKit.CaptionSize, UiKit.Grey).With(l => { l.CustomMinimumSize = new Vector2(120, 0); l.VerticalAlignment = VerticalAlignment.Center; }));
        var modeGroup = new ButtonGroup();
        var modeButtons = new List<Control>();
        foreach (var (mode, text) in new[] { (ServerLaunch.ModeAnnihilation, "Annihilation"), (ServerLaunch.ModeSeize, "Seize Ground") })
        {
            var chosen = mode;
            var b = new Button { Text = text, ToggleMode = true, ButtonGroup = modeGroup, ButtonPressed = _mode == mode, FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(190, UiKit.MinHit) };
            b.Pressed += () => _mode = chosen;
            row.AddChild(b);
            modeButtons.Add(b);
            _modeButtons.Add((mode, b));
        }
        row.AddChild(UiKit.Text("PORT", UiKit.CaptionSize, UiKit.Grey).With(l => l.VerticalAlignment = VerticalAlignment.Center));
        _port = new LineEdit { Text = (_args.GetValueOrDefault("host-port") ?? ServerLaunch.DefaultPort.ToString()), CustomMinimumSize = new Vector2(120, UiKit.MinHit), MaxLength = 5, FocusMode = Control.FocusModeEnum.All, SelectAllOnFocus = true };
        _port.TextChanged += _ => RefreshNote();
        row.AddChild(_port);
        var reachGroup = new ButtonGroup();
        foreach (var (local, text) in new[] { (true, "This PC only"), (false, "LAN / Internet") })
        {
            var chosen = local;
            var b = new Button { Text = text, ToggleMode = true, ButtonGroup = reachGroup, ButtonPressed = _localOnly == local, FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(local ? 220 : 230, UiKit.MinHit), TooltipText = local ? "Only this PC can connect (127.0.0.1)" : "Listen on all interfaces so other players can connect" };
            b.Pressed += () => { _localOnly = chosen; RefreshNote(); RefreshBots(); };
            row.AddChild(b);
            modeButtons.Add(b);
            _reachButtons.Add((local, b));
        }
        UiKit.ChainHorizontal(modeButtons);
        setup.AddChild(row);
        var second = UiKit.HBox(12);
        second.AddChild(UiKit.Text("", UiKit.CaptionSize).With(l => l.CustomMinimumSize = new Vector2(120, 0)));
        var emblems = UiKit.Toggle("Players' emblems: OFF", _emblems, _ => { }, 330);
        emblems.TooltipText = "Hosted games show everyone the badge unless you allow custom emblem images";
        emblems.Toggled += on => { _emblems = on; emblems.Text = "Players' emblems: " + (on ? "ON" : "OFF"); };
        if (_emblems) emblems.Text = "Players' emblems: ON";
        second.AddChild(emblems);
        _note = UiKit.Text("", UiKit.CaptionSize, UiKit.Grey, HorizontalAlignment.Left, true);
        _note.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _note.CustomMinimumSize = new Vector2(300, 0);
        _note.VerticalAlignment = VerticalAlignment.Center;
        second.AddChild(_note);
        _hostButton = UiKit.Button("HOST A MATCH", StartHosting, 280, "Start a server on this PC and join it");
        second.AddChild(_hostButton);
        setup.AddChild(second);
        _setup = setup;

        // ---- the BOTS stepper (fix/hosting-bots-logs, the owner's decision: 0 = no bots, the default, to 15) at the end of the first row, and STOP SERVER for a server
        // of this install started elsewhere after HOST A MATCH: no new row, so the Join screen keeps its height (fix/frontend-fit) ----
        _fewerBots = UiKit.Button("-", () => ChooseBotCount(_botCount - 1), 50, "One bot fewer");
        _fewerBots.Name = "BotsFewer";
        _botsLabel = UiKit.Text(HostBots.Label(_botCount), UiKit.BodySize, UiKit.White, HorizontalAlignment.Center).With(l => { l.Name = "BotsCount"; l.CustomMinimumSize = new Vector2(110, 0); l.VerticalAlignment = VerticalAlignment.Center; l.TooltipText = BotsTooltip; l.MouseFilter = MouseFilterEnum.Pass; });
        _moreBots = UiKit.Button("+", () => ChooseBotCount(_botCount + 1), 50, "One bot more");
        _moreBots.Name = "BotsMore";
        row.AddChild(_fewerBots);
        row.AddChild(_botsLabel);
        row.AddChild(_moreBots);
        _difficultyOption = new OptionButton { Name = "BotsDifficulty", FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(150, UiKit.MinHit),
            TooltipText = "Easy: bots deal a third of the damage (the server's --bot-damage-scale 0.34). Normal: full damage." };
        _difficultyOption.AddItem("Easy");
        _difficultyOption.AddItem("Normal");
        _difficultyOption.ItemSelected += index => ChooseDifficulty(index == 0 ? BotDifficulty.Easy : BotDifficulty.Normal);
        row.AddChild(_difficultyOption);
        // fix/connect-timeout (the owner's request): stops a server of this install that this menu did not start (the host launcher's); never another program.
        _stopOther = UiKit.Button("STOP SERVER", StopOtherServers, 240, "Stop the server of this game that is running on this PC (started by the host launcher or an earlier menu). Never stops other programs.");
        _stopOther.Disabled = true;
        second.AddChild(_stopOther);

        // ---- after: running ----
        // The status line fills its row and is cut (full text in its tooltip); the buttons sit in a flow row under it that wraps instead of widening the screen
        // (fix/frontend-fit: a wide row here would widen the whole Join screen and push JOIN off the right edge).
        var running = UiKit.VBox(8);
        var status = UiKit.HBox(12);
        status.AddChild(UiKit.Text("HOST", UiKit.CaptionSize, UiKit.Grey).With(l => { l.CustomMinimumSize = new Vector2(120, 0); l.VerticalAlignment = VerticalAlignment.Center; }));
        _runningText = UiKit.Text("", UiKit.BodySize, UiKit.White).With(l => { l.VerticalAlignment = VerticalAlignment.Center; l.SizeFlagsHorizontal = SizeFlags.ExpandFill; l.ClipText = true; l.MouseFilter = MouseFilterEnum.Pass; });
        status.AddChild(_runningText);
        running.AddChild(status);
        var actions = UiKit.HBox(12);
        actions.AddChild(new Control { CustomMinimumSize = new Vector2(120, 0), MouseFilter = MouseFilterEnum.Ignore });
        RunningActions.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        RunningActions.AddThemeConstantOverride("h_separation", 12);
        RunningActions.AddThemeConstantOverride("v_separation", 8);
        RunningActions.AddChild(UiKit.Button("JOIN YOUR SERVER", () => { if (HostedServer.Options is { } o) _connect(ServerLaunch.LocalHost(o.Port)); }, 300));
        RunningActions.AddChild(UiKit.Button("STOP SERVER", StopHosted, 240, "Stop the server this menu started and its bots (to change the number of bots, stop it and host again)"));
        actions.AddChild(RunningActions);
        running.AddChild(actions);
        _running = running;

        content.AddChild(setup);
        content.AddChild(running);
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 16);
        margin.AddChild(content);
        AddChild(margin);
        RefreshNote();
        RefreshBots();
        Refresh();
    }

    void Say(string text, bool problem)
    {
        GD.Print("host: " + text);
        _say(text, problem);
    }

    void RefreshNote()
    {
        if (_note is null || _port is null) return;
        int port = ServerLaunch.ValidatePort(_port.Text, out int p) is null ? p : ServerLaunch.DefaultPort;
        _note.Text = ServerLaunch.ReachNote(port, _localOnly);
    }

    void ChooseBotCount(int count)
    {
        if (HostedServer.Running) return;   // applies at the start only
        _botCount = Math.Clamp(count, 0, HostBots.MaxBots);
        RefreshBots();
    }

    void ChooseDifficulty(BotDifficulty difficulty)
    {
        _difficultyOverride = null;
        _services?.UpdateSettings(x => x with { BotDifficulty = difficulty });
        if (_services is null) _difficultyOverride = difficulty;
        RefreshBots();
    }

    /// <summary>The BOTS row: the number (fixed while a server runs: it applies at the start) with its - / + and the difficulty.</summary>
    void RefreshBots()
    {
        if (_botsLabel is null || _difficultyOption is null || _fewerBots is null || _moreBots is null) return;
        bool hosting = HostedServer.Running;
        bool musou = !hosting && Musou;   // Musou plans no bots (the stepper shows none and is locked)
        int count = hosting && HostedServer.Options is { } o ? o.Bots : musou ? 0 : _botCount;
        var difficulty = hosting && HostedServer.Options is { } d ? d.Difficulty : Difficulty;
        int difficultyIndex = difficulty == BotDifficulty.Easy ? 0 : 1;
        string label = HostBots.Label(count);
        if (_botsLabel.Text != label) _botsLabel.Text = label;
        if (_difficultyOption.Selected != difficultyIndex) _difficultyOption.Select(difficultyIndex);
        _difficultyOption.Disabled = hosting || musou;
        _fewerBots.Disabled = hosting || musou || count <= 0;
        _moreBots.Disabled = hosting || musou || count >= HostBots.MaxBots;
    }

    /// <summary>The BOTS row shown (test aid for the self-test): the number's text and the difficulty's.</summary>
    public (string Count, string Difficulty) BotsShown => (_botsLabel?.Text ?? "", _difficultyOption?.GetItemText(_difficultyOption.Selected) ?? "");

    /// <summary>Self-test: presses the BOTS stepper's - (negative) or + (positive) <paramref name="steps"/> times as a click does, and chooses a difficulty (null keeps it).</summary>
    public void ChooseForTest(int steps, BotDifficulty? difficulty)
    {
        for (int i = 0; i < Math.Abs(steps); i++) (steps < 0 ? _fewerBots : _moreBots)?.EmitSignal(BaseButton.SignalName.Pressed);
        if (difficulty is { } d && _difficultyOption is not null) { int i = d == BotDifficulty.Easy ? 0 : 1; _difficultyOption.Select(i); _difficultyOption.EmitSignal(OptionButton.SignalName.ItemSelected, (long)i); }
    }

    void Refresh()
    {
        bool running = HostedServer.Running || _testRunningText is not null;
        if (!running) ApplyMusou();
        _setup!.Visible = !running;
        _running!.Visible = running;
        if (_testRunningText is not null) _runningText!.Text = _testRunningText;
        else if (running && HostedServer.Options is { } o)
        {
            int count = HostedBots.Count, pending = HostedBots.Pending;
            string bots = count > 0 ? HostBots.CountText(count) + (pending > 0 ? $" ({pending} more joining)" : "") : pending > 0 ? $"{pending} bots joining" : "no bots";
            string hosted = o.Content == SmSim.ContentProfiles.Musou ? "Musou" : o.Mode == ServerLaunch.ModeSeize ? "Seize Ground" : "Annihilation";
            _runningText!.Text = $"Hosting {hosted} on port {o.Port}, {(o.LocalOnly ? "this PC only" : "all interfaces")}"
                                 + " · " + bots + (HostedServer.Ready ? "" : " (starting...)");
        }
        _runningText!.TooltipText = _runningText.Text;
        _stopOther!.Disabled = running || _installServers == 0;
        RefreshBots();
    }

    /// <summary>
    /// The running server's buttons (JOIN YOUR SERVER, STOP SERVER): a flow row that wraps onto a second line when full, so more server controls can be
    /// appended here without changing the layout (fix/frontend-fit).
    /// </summary>
    public HFlowContainer RunningActions { get; } = new() { Name = "RunningActions" };

    string? _testRunningText;
    readonly List<Button> _testButtons = new();

    /// <summary>
    /// UI self-test: shows the running state with <paramref name="text"/> and <paramref name="extraButtons"/> dummy buttons in <see cref="RunningActions"/> (no server
    /// is started); null text puts the panel back as it was.
    /// </summary>
    public void ShowRunningForTest(string? text, int extraButtons = 0)
    {
        foreach (var b in _testButtons) { RunningActions.RemoveChild(b); b.QueueFree(); }
        _testButtons.Clear();
        _testRunningText = text;
        if (text is not null)
            for (int i = 0; i < extraButtons; i++)
            {
                var b = UiKit.Button($"TEST BUTTON {i + 1}", () => { }, 200);
                _testButtons.Add(b);
                RunningActions.AddChild(b);
            }
        Refresh();
    }

    /// <summary>Who holds a port, by its owning processes (this install's server or another program).</summary>
    PortHolder HolderOf(IReadOnlyList<int> owners) => InstallServers.HolderOf(owners, _exe);

    void StartHosting()
    {
        if (ServerLaunch.ValidatePort(_port!.Text, out int port) is { } portProblem) { Say(portProblem, true); _port.GrabFocus(); return; }
        if (ServerLaunch.PortProblem(port, HolderOf) is { } taken) { Say(taken, true); _port.GrabFocus(); CheckInstallServers(force: true); return; }
        string content = SmSim.ContentProfiles.Name(_content());   // added content E1/E2: the server gets the Join screen's profile, so the client that joins it is not refused
        bool musou = Musou;
        if (musou) { _mode = ServerLaunch.ModeAnnihilation; _localOnly = true; }   // Musou: this PC only, the mode from the profile, no bots
        var plan = HostBots.Plan(musou ? 0 : _botCount, HumanChaos);
        BotLaunch? bots = null;
        if (plan.Count > 0 && HostBots.BotExecutable(OS.GetExecutablePath()) is { } botExe)
        {
            // From the editor binary (client.cmd), the bots need the project folder; an exported game carries its own.
            string? project = OS.HasFeature("template") ? null : ProjectSettings.GlobalizePath("res://").TrimEnd('/', '\\');
            bots = new BotLaunch(botExe, project, port, content, HumanChaos, plan, System.IO.Path.GetFullPath(BotLogs.Folder(ProjectSettings.GlobalizePath("user://"))));
        }
        var options = new HostOptions(_mode, port, _localOnly, _gameDir, _emblems, content, bots is null ? 0 : plan.Count, Difficulty);
        if (HostedServer.Start(_exe, options, bots) is { } error) { Say(error, true); return; }
        GD.Print($"host: server started on UDP {port} ({(_localOnly ? "this PC only" : "all interfaces")}); kill-on-close job: {HostedServer.Guarded}; bots planned: {plan.Count} ({options.Difficulty})");
        _pendingJoin = true;
        _startedAt = Time.GetTicksMsec() / 1000.0;
        Say("Starting the server (it reads the game files first, this can take a minute)...", false);
        Refresh();
    }

    void StopHosted()
    {
        HostedServer.Stop();
        _pendingJoin = false;
        Say("Server stopped.", false);
        Refresh();
    }

    void StopOtherServers()
    {
        int stopped = InstallServers.StopAll(_exe);
        CheckInstallServers(force: true);
        Say(stopped > 0 ? "Server stopped." : "No server of this game was running.", false);
        Refresh();
    }

    void CheckInstallServers(bool force = false)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (!force && now < _nextInstallCheck) return;
        _nextInstallCheck = now + 1.0;
        int count = InstallServers.Running(_exe).Count;
        if (HostedServer.Running) count = 0;   // ours: the running row's STOP SERVER handles it
        _installServers = count;
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (!_autoDone && _args.ContainsKey("auto-host") && _frames > 20 && !HostedServer.Running) { _autoDone = true; StartHosting(); return; }
        if (!Visible || !IsVisibleInTree()) return;
        if (_pendingJoin)
        {
            int port = HostedServer.Options?.Port ?? ServerLaunch.DefaultPort;
            switch (HostStart.StateOf(HostedServer.Running, HostedServer.Ready, HostedServer.ExitCode, Time.GetTicksMsec() / 1000.0 - _startedAt))
            {
                case HostStartState.Ready:
                    _pendingJoin = false;
                    Refresh();
                    _connect(ServerLaunch.LocalHost(port));
                    break;
                case HostStartState.PortInUse:
                case HostStartState.Failed:
                    // Never joined: a port in use means another server answers there (the owner's case: an old one, which never answered this version).
                    _pendingJoin = false;
                    Say(HostedServer.FailureText(HolderOf), true);
                    CheckInstallServers(force: true);
                    Refresh();
                    break;
                case HostStartState.TooSlow:
                    _pendingJoin = false;
                    HostedServer.Stop();
                    Say("The server took too long to start and was stopped.", true);
                    Refresh();
                    break;
            }
        }
        else
        {
            CheckInstallServers();
            // Test aid --host-stop-others: presses the setup row's STOP SERVER once it is enabled (a server of this install started elsewhere).
            if (!_stopOtherDone && _installServers > 0 && _args.ContainsKey("host-stop-others") && !HostedServer.Running) { _stopOtherDone = true; StopOtherServers(); }
            Refresh();   // follows the server and its bots: started elsewhere, died or stopped while the menu was open
        }
    }
}
