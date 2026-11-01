using System;
using System.Collections.Generic;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;
using SmContent;

namespace SmClient.FrontEnd;

/// <summary>
/// HOST LOCAL LOBBY (ours; docs/changes-from-original.md): starts the bundled dedicated server next to the client, then joins it at 127.0.0.1. Mode, port, who may connect and the bots are
/// chosen in these rows. The panel is only built when a server executable was found (<see cref="ServerLaunch.FindExecutable"/>). A server started here keeps running when you leave a match and
/// is stopped when the client quits (<see cref="HostedServer"/>). "This PC only" is the default; letting other players in is an explicit choice, and hosted games refuse custom emblem images
/// unless the host allows them. fix/connect-timeout (the owner's report and requests of 2026-10-29): the start is watched (listening line or exit; a taken port says so and is never joined);
/// STOP SERVER also stops a server of this install started elsewhere (the release's host launcher). Bots (fix/hosting-bots-logs; ui/menu-rebuild, the owner's menu): ADD LOYALIST BOT and
/// ADD CHAOS BOT count up one side each (no bots is the default in every reach, never remembered; left arrow or a right click takes one off, CLEAR BOTS all), one difficulty for all of them;
/// the numbers apply when the server starts (stop the server and host again to change them); Easy scales their damage (<see cref="HostedBots"/>, rules <see cref="HostBots"/>).
/// Test aids (all optional): <c>--auto-host</c> presses the button once, with <c>--host-mode annihilation|seize</c>, <c>--host-port N</c>, <c>--host-lan</c>, <c>--host-emblems</c>,
/// <c>--host-bots 0..15|none|4v4|8v8</c> and <c>--host-difficulty easy|normal</c> (neither saved); <c>--host-stop-others</c> presses STOP SERVER once a server of this install started elsewhere is seen.
/// </summary>
public sealed partial class HostPanel : UiKit.MenuColumn
{
    readonly string _exe, _gameDir;
    readonly Action<string> _connect;
    readonly Action<string, bool> _say;
    readonly Dictionary<string, string?> _args;
    readonly Func<SmSim.ContentExtras> _content;   // the lobby's content profile (added content E2): the server is started with it
    readonly CustomizerServices? _services;        // the remembered bots' difficulty and the side asked for (fix/connect-timeout)
    readonly Action _back;

    string _mode = ServerLaunch.ModeAnnihilation;
    bool _localOnly = true, _emblems;
    readonly LineEdit _port;
    readonly Label _note, _runningText;
    readonly List<Control> _setupRows = new(), _runningRows = new();
    readonly UiKit.MenuChoice _modeRow, _reachRow, _emblemsRow, _difficultyRow;
    readonly Button _hostButton, _stopOther, _addLoyalist, _addChaos, _clearBots, _joinOwn;
    readonly Label _loyalistCount, _chaosCount;
    int _loyalists, _chaos;   // fix/hosting-bots-logs: never remembered; 0 = no bots
    bool? _musouShown;   // the Musou lock last applied to the rows (null: not yet)
    (string Mode, bool LocalOnly)? _beforeMusou;   // the mode and reach chosen before the Musou lock, restored when it lifts
    bool _pendingJoin;
    double _startedAt;
    bool _autoDone, _stopOtherDone;
    int _frames;
    double _nextInstallCheck;
    int _installServers;   // servers of this install running that this panel did not start (polled once a second)
    BotDifficulty? _difficultyOverride;

    const string BotsTooltip = "Bots are headless test players started on this PC with the server. " +
                               "Each is a game process of about 0.27 GB of memory and about 5% of one processor core: it loads no graphics, effects or sounds and runs at 30 frames a second " +
                               "(7 bots: about 1.9 GB), so on a PC with 8 GB keep to a few. The numbers apply when the server starts: to change them, stop the server and host again.";

    public HostPanel(string exe, string gameDir, Dictionary<string, string?> args, Action<string> connect, Action<string, bool> say, Func<SmSim.ContentExtras> content,
        CustomizerServices? services, Action back)
    {
        _exe = exe; _gameDir = gameDir; _args = args; _connect = connect; _say = say; _content = content; _services = services; _back = back;
        Name = "HostPanel";
        _localOnly = !args.ContainsKey("host-lan");
        _emblems = args.ContainsKey("host-emblems");
        if (args.GetValueOrDefault("host-mode") is { } m && m is ServerLaunch.ModeAnnihilation or ServerLaunch.ModeSeize) _mode = m;
        var plan = HostBots.Plan(HostBots.ParseCount(args.GetValueOrDefault("host-bots")) ?? HostBots.DefaultCount, HumanChaos);
        foreach (var slot in plan) { if (slot.Side == HostBots.Chaos) _chaos++; else _loyalists++; }
        _difficultyOverride = HostBots.ParseDifficulty(args.GetValueOrDefault("host-difficulty"));

        // ---- before: choices ----
        _modeRow = new UiKit.MenuChoice("Game mode", ["ANNIHILATION", "SEIZE GROUND"], _mode == ServerLaunch.ModeSeize ? 1 : 0, i => _mode = i == 1 ? ServerLaunch.ModeSeize : ServerLaunch.ModeAnnihilation);
        _reachRow = new UiKit.MenuChoice("Who can join", ["THIS PC ONLY", "LAN / INTERNET"], _localOnly ? 0 : 1, i => { _localOnly = i == 0; RefreshNote(); }, "This PC only: 127.0.0.1. LAN / Internet: listens on all interfaces so other players can connect.");
        _port = new LineEdit { Text = args.GetValueOrDefault("host-port") ?? ServerLaunch.DefaultPort.ToString(), MaxLength = 5, FocusMode = FocusModeEnum.All, SelectAllOnFocus = true, Name = "HostPort" };
        _port.TextChanged += _ => RefreshNote();
        _emblemsRow = new UiKit.MenuChoice("Players' emblems", ["OFF", "ON"], _emblems ? 1 : 0, i => _emblems = i == 1, "Hosted games show everyone the badge unless you allow custom emblem images");
        _difficultyRow = new UiKit.MenuChoice("Bot difficulty", ["EASY", "NORMAL"], Difficulty == BotDifficulty.Easy ? 0 : 1, i => ChooseDifficulty(i == 0 ? BotDifficulty.Easy : BotDifficulty.Normal), "Easy: bots deal a third of the damage (the server's --bot-damage-scale 0.34). Normal: full damage.");
        _addLoyalist = UiKit.MenuButton("Add Loyalist bot", () => AddBot(HostBots.Marines, 1), BotsTooltip);
        _addLoyalist.Name = "AddLoyalistBot";
        _addChaos = UiKit.MenuButton("Add Chaos bot", () => AddBot(HostBots.Chaos, 1), BotsTooltip);
        _addChaos.Name = "AddChaosBot";
        _loyalistCount = CountLabel(_addLoyalist, HostBots.Marines);
        _chaosCount = CountLabel(_addChaos, HostBots.Chaos);
        _clearBots = UiKit.MenuButton("Clear bots", () => { if (!HostedServer.Running) { _loyalists = _chaos = 0; RefreshBots(); } });
        _clearBots.Name = "ClearBots";
        _hostButton = UiKit.MenuButton("Host a match", StartHosting, "Start a server on this PC and join it");
        _hostButton.Name = "HostButton";
        _stopOther = UiKit.MenuButton("Stop server", StopOtherServers, "Stop the server of this game that is running on this PC (started by the host launcher or an earlier menu). Never stops other programs.");
        _stopOther.Disabled = true;
        foreach (var row in new Control[] { _modeRow, _reachRow, UiKit.MenuField("Port", _port), _emblemsRow, _difficultyRow, _addLoyalist, _addChaos, _clearBots, UiKit.MenuGap(0.5f), _hostButton, _stopOther })
        {
            AddChild(row);
            _setupRows.Add(row);
        }

        // ---- after: running ----
        _runningText = UiKit.MenuText("", UiKit.White);
        _runningText.MouseFilter = MouseFilterEnum.Pass;
        _joinOwn = UiKit.MenuButton("Join your server", () => { if (HostedServer.Options is { } o) _connect(ServerLaunch.LocalHost(o.Port)); });
        var stop = UiKit.MenuButton("Stop server", () => StopHosted(), "Stop the server this menu started and its bots (to change the number of bots, stop it and host again)");
        foreach (var row in new Control[] { _runningText, _joinOwn, stop })
        {
            AddChild(row);
            _runningRows.Add(row);
        }
        AddChild(RunningActions);
        _runningRows.Add(RunningActions);

        // Below both: the reach note and the way back.
        _note = UiKit.MenuText("", UiKit.Grey);
        AddChild(UiKit.MenuGap(0.5f));
        AddChild(_note);
        AddChild(UiKit.MenuGap(0.5f));
        AddChild(UiKit.MenuButton("Back", () => _back()));
        RefreshNote();
        RefreshBots();
        Refresh();
    }

    /// <summary>The number beside an add button; left, or a right click on the button, takes one off.</summary>
    Label CountLabel(Button add, string side)
    {
        var l = UiKit.MenuText("0", null, clip: false);
        l.Name = "Count";
        l.SetAnchorsPreset(LayoutPreset.LeftWide);
        l.OffsetLeft = UiKit.MenuValueColumn; l.OffsetRight = UiKit.MenuValueColumn + 200f;
        add.AddChild(l);
        add.GuiInput += e =>
        {
            if (e.IsActionPressed("ui_left") || e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) { AddBot(side, -1); add.AcceptEvent(); }
            else if (e.IsActionPressed("ui_right")) { AddBot(side, 1); add.AcceptEvent(); }
        };
        return l;
    }

    /// <summary>The number of bots the next hosted match gets, both sides (test aid <c>--host-bots</c>; default none in every reach, never remembered).</summary>
    public int BotCount => Musou ? 0 : _loyalists + _chaos;
    /// <summary>The bots asked for on each side.</summary>
    public (int Loyalists, int Chaos) Bots => (_loyalists, _chaos);
    public bool HasBotButtons => _addLoyalist is not null && _addChaos is not null;

    /// <summary>The lobby's profile is Musou (experimental added content): the hosted server is this PC only (127.0.0.1), Annihilation, without bots (the enemies are the server's own).</summary>
    public bool Musou => _content().HasFlag(SmSim.ContentExtras.Musou);

    /// <summary>The host button's text (self-test).</summary>
    public string HostButtonText => _hostButton.Text;

    /// <summary>Applies the Musou lock to the rows when the profile changes (the rows come back unlocked after).</summary>
    void ApplyMusou()
    {
        bool musou = Musou;
        if (_musouShown == musou) return;
        _musouShown = musou;
        if (musou) { _beforeMusou ??= (_mode, _localOnly); _mode = ServerLaunch.ModeAnnihilation; _localOnly = true; }
        else if (_beforeMusou is { } before) { (_mode, _localOnly) = before; _beforeMusou = null; }
        _modeRow.SetDisabled(musou); _modeRow.SetIndex(_mode == ServerLaunch.ModeSeize ? 1 : 0);
        _reachRow.SetDisabled(musou); _reachRow.SetIndex(_localOnly ? 0 : 1);
        _hostButton.Text = musou ? "START MUSOU" : "HOST A MATCH";
        _hostButton.TooltipText = musou ? "Start a Musou server on this PC only and join it" : "Start a server on this PC and join it";
        RefreshNote();
    }

    public BotDifficulty Difficulty => _difficultyOverride ?? _services?.Settings.BotDifficulty ?? BotDifficulty.Easy;
    bool HumanChaos => _services?.Settings.Side == SideChoice.Chaos;

    void Say(string text, bool problem)
    {
        GD.Print("host: " + text);
        _say(text, problem);
    }

    void RefreshNote()
    {
        int port = ServerLaunch.ValidatePort(_port.Text, out int p) is null ? p : ServerLaunch.DefaultPort;
        _note.Text = ServerLaunch.ReachNote(port, _localOnly);
        _note.TooltipText = _note.Text;
    }

    void AddBot(string side, int change)
    {
        if (HostedServer.Running) return;   // applies at the start only
        int max = HostBots.MaxForSide(side, HumanChaos);
        if (side == HostBots.Chaos) _chaos = Math.Clamp(_chaos + change, 0, max); else _loyalists = Math.Clamp(_loyalists + change, 0, max);
        RefreshBots();
    }

    void ChooseDifficulty(BotDifficulty difficulty)
    {
        _difficultyOverride = null;
        _services?.UpdateSettings(x => x with { BotDifficulty = difficulty });
        if (_services is null) _difficultyOverride = difficulty;
        RefreshBots();
    }

    /// <summary>The bot rows: the numbers (fixed while a server runs: they apply at the start) and the difficulty.</summary>
    void RefreshBots()
    {
        bool hosting = HostedServer.Running;
        bool musou = !hosting && Musou;   // Musou plans no bots (the rows show none and are locked)
        string loyalists = (musou ? 0 : _loyalists).ToString(), chaos = (musou ? 0 : _chaos).ToString();
        if (_loyalistCount.Text != loyalists) _loyalistCount.Text = loyalists;
        if (_chaosCount.Text != chaos) _chaosCount.Text = chaos;
        var difficulty = hosting && HostedServer.Options is { } d ? d.Difficulty : Difficulty;
        _difficultyRow.SetIndex(difficulty == BotDifficulty.Easy ? 0 : 1);
        _difficultyRow.SetDisabled(hosting || musou);
        _addLoyalist.Disabled = hosting || musou || _loyalists >= HostBots.MaxForSide(HostBots.Marines, HumanChaos);
        _addChaos.Disabled = hosting || musou || _chaos >= HostBots.MaxForSide(HostBots.Chaos, HumanChaos);
        _clearBots.Disabled = hosting || musou || _loyalists + _chaos == 0;
    }

    /// <summary>The bot rows shown (self-test): the two numbers and the difficulty.</summary>
    public (string Loyalists, string Chaos, string Difficulty) BotsShown => (_loyalistCount.Text, _chaosCount.Text, _difficultyRow.Value);

    /// <summary>Self-test: presses the add buttons as a click does, and chooses a difficulty (null keeps it).</summary>
    public void ChooseForTest(int loyalists, int chaos, BotDifficulty? difficulty)
    {
        for (int i = 0; i < loyalists; i++) _addLoyalist.EmitSignal(BaseButton.SignalName.Pressed);
        for (int i = 0; i < chaos; i++) _addChaos.EmitSignal(BaseButton.SignalName.Pressed);
        if (difficulty is { } d) { _difficultyRow.SetIndex(d == BotDifficulty.Easy ? 0 : 1); ChooseDifficulty(d); }
    }

    void Refresh()
    {
        bool running = HostedServer.Running || _testRunningText is not null;
        if (!running) ApplyMusou();
        foreach (var row in _setupRows) row.Visible = !running;
        foreach (var row in _runningRows) row.Visible = running;
        if (_testRunningText is not null) _runningText.Text = _testRunningText;
        else if (running && HostedServer.Options is { } o)
        {
            int count = HostedBots.Count, pending = HostedBots.Pending;
            string bots = count > 0 ? HostBots.CountText(count) + (pending > 0 ? $" ({pending} more joining)" : "") : pending > 0 ? $"{pending} bots joining" : "no bots";
            string hosted = o.Content == SmSim.ContentProfiles.Musou ? "Musou" : o.Mode == ServerLaunch.ModeSeize ? "Seize Ground" : "Annihilation";
            _runningText.Text = ($"Hosting {hosted} on port {o.Port}, {(o.LocalOnly ? "this PC only" : "all interfaces")}" + ", " + bots + (HostedServer.Ready ? "" : " (starting...)")).ToUpperInvariant();
        }
        _runningText.TooltipText = _runningText.Text;
        _stopOther.Disabled = running || _installServers == 0;
        RefreshBots();
    }

    /// <summary>The first row a player wants: the mode before hosting, JOIN YOUR SERVER while a server runs.</summary>
    public void FocusFirst() => (HostedServer.Running ? _joinOwn : _modeRow).FocusLater();

    /// <summary>Where later server controls go: rows under STOP SERVER while a server runs (fix/frontend-fit).</summary>
    public UiKit.MenuColumn RunningActions { get; } = new() { Name = "RunningActions" };

    string? _testRunningText;
    readonly List<Button> _testButtons = new();

    /// <summary>UI self-test: shows the running state with <paramref name="text"/> and <paramref name="extraButtons"/> dummy rows (no server is started); null text puts the panel back.</summary>
    public void ShowRunningForTest(string? text, int extraButtons = 0)
    {
        foreach (var b in _testButtons) { RunningActions.RemoveChild(b); b.QueueFree(); }
        _testButtons.Clear();
        _testRunningText = text;
        if (text is not null)
            for (int i = 0; i < extraButtons; i++)
            {
                var b = UiKit.MenuButton($"TEST BUTTON {i + 1}", () => { });
                _testButtons.Add(b);
                RunningActions.AddChild(b);
            }
        Refresh();
    }

    /// <summary>Who holds a port, by its owning processes (this install's server or another program).</summary>
    PortHolder HolderOf(IReadOnlyList<int> owners) => InstallServers.HolderOf(owners, _exe);

    /// <summary>Starts hosting with the rows' choices (HOST A MATCH; MUSOU MODE from the main menu); the started server is joined by itself.</summary>
    public void StartHosting()
    {
        if (ServerLaunch.ValidatePort(_port.Text, out int port) is { } portProblem) { Say(portProblem, true); if (IsVisibleInTree()) _port.GrabFocus(); return; }
        if (ServerLaunch.PortProblem(port, HolderOf) is { } taken) { Say(taken, true); if (IsVisibleInTree()) _port.GrabFocus(); CheckInstallServers(force: true); return; }
        string content = SmSim.ContentProfiles.Name(_content());   // added content E1/E2: the server gets the lobby's profile, so the client that joins it is not refused
        bool musou = Musou;
        if (musou) { _mode = ServerLaunch.ModeAnnihilation; _localOnly = true; }   // Musou: this PC only, the mode from the profile, no bots
        var plan = musou ? new List<BotSlot>() : HostBots.PlanSides(_loyalists, _chaos, HumanChaos);
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

    /// <summary>Stops the server this client started (STOP SERVER here; STOP MUSOU SERVER on the main menu): only that process, and a start still being watched is dropped.</summary>
    public void StopHosted(string message = "Server stopped.")
    {
        HostedServer.Stop();
        _pendingJoin = false;
        Say(message, false);
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
        // The start is watched on every page: MUSOU MODE starts the server from the main menu, with this page hidden.
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
        else if (IsVisibleInTree())
        {
            CheckInstallServers();
            // Test aid --host-stop-others: presses the setup row's STOP SERVER once it is enabled (a server of this install started elsewhere).
            if (!_stopOtherDone && _installServers > 0 && _args.ContainsKey("host-stop-others") && !HostedServer.Running) { _stopOtherDone = true; StopOtherServers(); }
            Refresh();   // follows the server and its bots: started elsewhere, died or stopped while the menu was open
        }
    }
}
