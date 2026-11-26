using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Godot;
using SmClient.Customizer;
using SmContent;
using SmContent.Extras.Warfront;
using SmSim;

namespace SmClient.FrontEnd;

/// <summary>
/// The main menu's WARFRONT row and its pages (slice W16; owner's answer 3: a local campaign only he can join, the server run by his PC; since 2026-11-19 the row that replaced Musou Mode). Pages: CONTINUE CAMPAIGN (only with a run in progress, with its
/// one-line summary), NEW CAMPAIGN (asks first when a run is on disk), HOST FOR FRIENDS (the Host panel on the Warfront profile), JOIN A FRIEND (the address page), the customizer and BACK; PREPARING THE BATTLEFIELD while the server starts. The server is the Warfront supervisor, started
/// through <see cref="HostedServer"/> (<c>--bind 127.0.0.1</c>, one human, <see cref="WarfrontLocal.Options"/>); the page joins it once it listens. The rules and texts are <see cref="WarfrontLocal"/>.
/// The pages use the menu's own rows, tooltips and text sizes; no painted labels. A server this client started is stopped when the player comes back to the menu (a failed join keeps it for one retry).
/// </summary>
public sealed partial class FrontEndView
{
    UiKit.MenuPage? _wfPage, _wfPrep;
    Label? _wfStatus, _wfElapsed;
    bool _wfPending;
    double _wfStartedAt;
    Control? _wfDialog;

    /// <summary>Self-test: the run folder the page reads (null: <see cref="WarfrontLocal.RunDir"/>).</summary>
    public string? WarfrontRunDirForTest { get; set; }
    /// <summary>Self-test: stands in for the message the front end was given when it was built (null: the real one).</summary>
    internal string? WarfrontMessageForTest { get; set; }
    /// <summary>Self-test: the content profile the session is on.</summary>
    internal ContentExtras ContentForTest { get => _services.Content; set => _services.Content = value; }
    /// <summary>Self-test: what the front end does when it is built again after a match (stops this game's server, puts the profile back).</summary>
    internal ContentExtras SettingsContentForTest => _services.Settings.ContentExtras;
    internal void WarfrontAtMenuForTest() => WarfrontAtMenu();
    internal bool WarfrontJoinedForTest { get => WarfrontMenuState.Joined; set => WarfrontMenuState.Joined = value; }

    /// <summary>Self-test: stands in for the server's state (running, ready, Warfront profile).</summary>
    public (bool Running, bool Ready, bool Warfront)? WarfrontStateForTest { get; set; }
    /// <summary>Self-test: the press stops before the server starts and reports the choice (true: a new campaign).</summary>
    public Action<bool>? WarfrontOverride { get; set; }

    /// <summary>The Warfront page, or null before it was opened (self-test).</summary>
    public UiKit.MenuPage? WarfrontPage => _wfPage;
    /// <summary>The preparing screen, or null before a start (self-test).</summary>
    public UiKit.MenuPage? WarfrontPreparing => _wfPrep;
    /// <summary>The New campaign confirmation is open (self-test).</summary>
    public bool WarfrontConfirmOpen => _wfDialog is { } d && GodotObject.IsInstanceValid(d) && d.IsInsideTree() && !d.IsQueuedForDeletion();
    /// <summary>The Warfront row on the main menu, or null without the added content (self-test).</summary>
    public Button? WarfrontRow { get; private set; }
    /// <summary>Self-test: the message line's text.</summary>
    internal string MessageText => _message?.Text ?? "";

    string WarfrontDir => WarfrontRunDirForTest ?? WarfrontLocal.RunDir();

    partial void WarfrontMenuRows(UiKit.MenuPage main)
    {
        if (!ContentExtras.WarfrontCompiled) return;
        // WARFRONT replaces Musou Mode in its place (owner 2026-11-19): the first row and the default focus; STOP WARFRONT SERVER right under it while this game's server runs.
        WarfrontRow = UiKit.SteelButton(WarfrontLocal.RowText, ShowWarfrontPage, WarfrontLocal.RowTooltip, "MenuWarfront");
        main.Rows.AddChild(WarfrontRow);
        _first[main] = WarfrontRow;
        _stopMusou = UiKit.SteelButton(WarfrontLocal.StopServerRowText, StopMusou, "Stops the Warfront server this game started on this PC", "MenuStopWarfrontserver");
        _stopMusou.Visible = false;   // only while that server runs (SyncMusouRow)
        main.Rows.AddChild(_stopMusou);
        AddChild(new WarfrontMenuWatcher(WarfrontTick, WarfrontBack));
        WarfrontAtMenu();
        if (_args.ContainsKey("warfront-selftest")) AddChild(new WarfrontLocalSelfTest(this));
    }
    /// <summary>The front end is built: a server this client started is stopped when the player left a match (a failed join keeps it for one retry).</summary>
    void WarfrontAtMenu()
    {
        _startMessage = WarfrontOnline.Message(_startMessage);   // a failed online join says the campaign is not reachable
        var (running, _, warfront) = WarfrontServerState();
        bool ours = running && warfront;
        switch (WarfrontLocal.AtMenu(ours, WarfrontMenuState.Joined, WarfrontMessageForTest ?? _startMessage, WarfrontMenuState.Retries))
        {
            case WarfrontLocal.MenuReturn.Keep:
                WarfrontMenuState.Retries++;
                WarfrontMenuState.Joined = false;
                break;
            case WarfrontLocal.MenuReturn.Stop:
                GD.Print($"warfront: back at the menu, stopping the server this game started (pid {HostedServer.ProcessId})");
                HostedServer.Stop();
                WarfrontMenuState.Joined = false;
                WarfrontMenuState.Retries = 0;
                if (!CommandLineMusou) _services.Content = _services.Settings.ContentExtras;   // the rebuilt services start from the last joined profile (ModelViewer.MakeServices): the lobby's own comes back with the server's stop
                if (_args.ContainsKey("warfront-quit-at-menu")) { GD.Print($"warfront: server running after stop: {HostedServer.Running}; quitting (test aid)"); Callable.From(() => GetTree().Quit()).CallDeferred(); }
                break;
            default:
                if (!ours) { WarfrontMenuState.Joined = false; WarfrontMenuState.Retries = 0; }
                break;
        }
    }

    (bool Running, bool Ready, bool Warfront) WarfrontServerState() =>
        WarfrontStateForTest ?? (HostedServer.Running, HostedServer.Ready, HostedServer.Options is { Content: ContentProfiles.Warfront });

    /// <summary>The WARFRONT row: the page, with the session on the Warfront profile (the Host panel hosts Warfront and a join is a Warfront join from here on; the lobby's remembered profile is not touched).</summary>
    partial void WarfrontShowEntry() => ShowWarfrontPage();

    void ShowWarfrontPage()
    {
        if (!_built) return;
        var (running, _, warfront) = WarfrontServerState();
        if (!running || warfront) _services.Content = ContentProfiles.WarfrontProfile;
        _inExtras = true;
        var run = WarfrontLocal.ReadRun(WarfrontDir);
        string? honor = WarfrontLocal.HonorLine(WarfrontDir);
        if (WarfrontTakeNotice(WarfrontDir) is { Length: > 0 } taken) _wfNotice = taken;   // the loader's one line (read and deleted when the page opens)
        var progress = WarfrontOpenStore();
        _wfPage?.QueueFree();
        // The page of the concept redesign (owner, 2026-11-20): the shared frame, a status strip, one centred column of plates and the gold CONTINUE plate in the tray (WarfrontPage); no heading, no explainer text, hints on hover.
        var page = new WarfrontPage("WarfrontMenu", WarfrontLocal.PageTitle) { PrimaryTexts = [WarfrontLocal.ContinueRow.ToUpperInvariant(), WarfrontLocal.RejoinRow.ToUpperInvariant()] };
        Button? first = null, main = null;
        var column = new List<Button>();
        var choices = WarfrontLocal.Choices(run, running, warfront);
        void Add(Button b) { page.Rows.AddChild(b); }
        Button Plate(string text, Action press, string? hint) { var b = UiKit.MenuButton(text, press); if (hint is not null) UiTooltip.Attach(b, hint); return b; }
        string? note = null;
        if (running)
        {
            note = warfront ? "THE WARFRONT SERVER IS RUNNING." : WarfrontLocal.OtherServerText.ToUpperInvariant();
            if (choices.Contains(WarfrontLocal.RejoinRow)) { main = Plate(WarfrontLocal.RejoinRow, () => WarfrontStart(newRun: false), "Joins the campaign that is running"); Add(main); first = main; }
            var stop = Plate(WarfrontLocal.StopRow, WarfrontStopServer, "Stops the server this game started on this PC");
            first ??= stop;
            column.Add(stop); Add(stop);
        }
        else
        {
            if (choices.Contains(WarfrontLocal.ContinueRow)) { main = Plate(WarfrontLocal.ContinueRow, () => WarfrontStart(newRun: false), "Back to the campaign you left"); Add(main); first = main; }
            var fresh = Plate(WarfrontLocal.NewRow, () => WarfrontNew(run), "Starts a new campaign on this PC: only you can join it");
            if (run.Kind == WarfrontRunKind.Newer) { fresh.Disabled = true; UiTooltip.Attach(fresh, "The campaign file on this PC needs a newer build. Update the game."); }   // the server would refuse a new run over it
            first ??= fresh;
            column.Add(fresh); Add(fresh);
            var faction = WarfrontFactionPlate(); column.Add(faction); Add(faction);   // Chaos Warfront C3: the FACTION plate (Extras/Warfront/Chaos/FrontEndView.WarfrontFaction.cs)
        }
        // The friends' ways in, the same two as Classic PvP's lobby: the Host panel on the Warfront profile (this PC only, or LAN / Internet, up to 4 players) and the address page.
        var hostRow = Plate(WarfrontLocal.HostRow, () => ShowPage(_hostPage!), WarfrontLocal.CoopNote);
        if (!HasHostPanel) { hostRow.Disabled = true; UiTooltip.Attach(hostRow, WarfrontLocal.NoServerText); }
        else if (running) { hostRow.Disabled = true; UiTooltip.Attach(hostRow, "A server of this game is running. Stop it first."); }
        column.Add(hostRow); Add(hostRow);
        var join = Plate(WarfrontLocal.JoinRow, () => ShowPage(_joinLocalPage!), "A Warfront hosted on another PC: type its address");
        column.Add(join); Add(join);
        if (WarfrontOnline.Enabled) { var online = Plate(WarfrontOnline.Row, WarfrontJoinOnline, WarfrontOnline.RowTooltip); column.Add(online); Add(online); }
        var customizer = Plate("Customizer", WarfrontCustomizer, "Your Warfront loadouts: Standard Marines, the Librarian, the Heretic (not yet)");
        column.Add(customizer); Add(customizer);
        // RESET PROGRESSION moved to OPTIONS, INTERFACE under SCREEN FLASHES (owner 2026-11-25): the column ends with the customizer, BACK below it as before.
        Add(UiKit.MenuButton("Back", WarfrontBackToMain));
        page.SetStatus(run.CanContinue ? run.Cells : null, honor);
        if (_wfNotice.Length > 0) page.SetNote(_wfNotice, "refusal");
        else if (note is not null) page.SetNote(note, running && !warfront ? "refusal" : null);
        else if (run.Kind == WarfrontRunKind.Newer) page.SetNote(WarfrontRunInfo.NewerText.ToUpperInvariant(), "refusal");
        else if (progress is { Newer: true }) page.SetNote(WarfrontNewerNote, "refusal");
        else if (honor is not null) page.SetNote(honor, "progress");   // counts from the honor roll (RUNS 3, VICTORIES 1, FASTEST 41:20)
        page.SetPlates(column, main);
        _first[page] = first;
        page.Visible = false;
        AddChild(page);
        _wfPage = page;
        _extrasPage = page;
        WarfrontHidePages();
        ShowPage(page);
        _page = null;   // the watcher takes this page's Esc and B (the front end's own step back leads to PLAYER VERSUS PLAYER)
    }
    /// <summary>Self-test: opens the page again (it is rebuilt from the run folder each time).</summary>
    internal void ShowWarfrontPageForTest() => ShowWarfrontPage();

    void WarfrontHidePages()
    {
        if (_wfPage is { } p && GodotObject.IsInstanceValid(p)) p.Visible = false;
        if (_wfPrep is { } q && GodotObject.IsInstanceValid(q)) q.Visible = false;
    }

    void WarfrontBackToMain()
    {
        _wfNotice = "";
        WarfrontHidePages();
        ShowMainMenu();
    }

    void WarfrontCustomizer()
    {
        WarfrontHidePages();
        _services.Content = ContentProfiles.WarfrontProfile;   // the creator on the Warfront profile (Musou's loadouts: Standard Marines, the Librarian, the Heretic); its BACK (CreatorBack, _inExtras) returns to this page
        ShowCreator();
    }

    void WarfrontNew(WarfrontRunInfo run)
    {
        if (!run.NewNeedsConfirm) { WarfrontStart(newRun: true); return; }
        _wfDialog = UiKit.Dialog.Open(_wfPage!, this, WarfrontLocal.ConfirmTitle, WarfrontLocal.ConfirmBody, ["Yes", "No"], i => { if (i == 0) WarfrontStart(newRun: true); }, cancelIndex: 1);
        WarfrontFocusNo(_wfDialog);
    }

    void WarfrontStopServer()
    {
        _hostPanel?.StopHosted(WarfrontLocal.StoppedText);
        if (!CommandLineMusou) _services.Content = _services.Settings.ContentExtras;
        _wfPending = false;
        ShowWarfrontPage();
    }

    void WarfrontStart(bool newRun)
    {
        var (running, ready, warfront) = WarfrontServerState();
        switch (WarfrontLocal.OnPress(HasHostPanel, running, ready, warfront))
        {
            case WarfrontPress.NoServer: Say(WarfrontLocal.NoServerText, true); return;
            case WarfrontPress.OtherServer: Say(WarfrontLocal.OtherServerText, true); return;
            case WarfrontPress.Wait: ShowWarfrontPrep(); return;
            case WarfrontPress.Join: WarfrontJoin(); return;
        }
        if (WarfrontOverride is { } test) { _services.Content = ContentProfiles.WarfrontProfile; test(newRun); return; }
        string? exe = ServerLaunch.FindExecutable(OS.GetExecutablePath().GetBaseDir(), (_args.TryGetValue("server-exe", out var wfExe) ? wfExe : null));
        if (exe is null) { Say(WarfrontLocal.NoServerText, true); return; }
        int port = WarfrontFreePort();
        if (port == 0) { Say("No free port was found on this PC.", true); return; }
        WarfrontMenuState.Retries = 0;
        WarfrontMenuState.Joined = false;
        _wfLastStatus = "";
        _wfLogged.Clear();
        _services.Content = ContentProfiles.WarfrontProfile;
        if (HostedServer.Start(exe, WarfrontLocal.Options(port, _services.GameDir, newRun, WarfrontLocalFaction)) is { } error)
        {
            Say(error, true);
            if (!CommandLineMusou) _services.Content = _services.Settings.ContentExtras;
            return;
        }
        GD.Print($"warfront: supervisor started (pid {HostedServer.ProcessId}) on UDP {port}, this PC only, one human, new campaign {newRun}, faction {SmSim.Extras.Warfront.Faction.WarfrontFactionPolicy.Key(WarfrontLocalFaction)}; kill-on-close job: {HostedServer.Guarded}");
        _wfPending = true;
        _wfStartedAt = Time.GetTicksMsec() / 1000.0;
        ShowWarfrontPrep();
    }

    /// <summary>A free UDP port on this PC's loopback address (never every interface).</summary>
    static int WarfrontFreePort()
    {
        for (int i = 0; i < 8; i++)
        {
            try
            {
                using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
                probe.Close();
                if (port >= 1024 && ServerLaunch.PortProblem(port) is null) return port;
            }
            catch (SocketException) { }
        }
        return 0;
    }

    internal void ShowWarfrontPrep()
    {
        _wfPrep?.QueueFree();
        // The same frame as the page (owner, 2026-11-20): the title on the plate, the server's line and the clock as the frame's notes, CANCEL as a plate.
        var page = new ConceptPanelPage("WarfrontPreparing", "", WarfrontLocal.PreparingTitle) { Visible = false };
        _wfStatus = UiKit.TextKind(UiKit.MenuText(WarfrontLocal.StartingText.ToUpperInvariant(), UiKit.Grey), "progress");   // the server's own progress line
        _wfElapsed = UiKit.MenuText("0:00", UiKit.Grey);
        page.Rows.AddChild(_wfStatus);
        page.Rows.AddChild(_wfElapsed);
        page.Rows.AddChild(UiKit.MenuGap(0.5f));
        var cancel = UiKit.MenuButton("Cancel", WarfrontCancel, "Stops the server");
        page.Rows.AddChild(cancel);
        _first[page] = cancel;
        AddChild(page);
        _wfPrep = page;
        WarfrontHidePages();
        ShowPage(page);
        _page = null;
    }

    void WarfrontCancel()
    {
        _hostPanel?.StopHosted(WarfrontLocal.StoppedText);
        if (!HostedServer.Running) HostedServer.Stop();
        _wfPending = false;
        if (!CommandLineMusou) _services.Content = _services.Settings.ContentExtras;
        ShowWarfrontPage();
        Say(WarfrontLocal.StoppedText, false);
    }

    /// <summary>ONLINE WARFRONT: the shared campaign on the public host, nothing to type; the session is on the Warfront profile (as the friend join), the address is not remembered.</summary>
    void WarfrontJoinOnline()
    {
        _services.Content = ContentProfiles.WarfrontProfile;
        TryJoin(WarfrontOnline.Address, remember: false);
    }

    void WarfrontJoin()
    {
        int port = HostedServer.Options?.Port ?? ServerLaunch.DefaultPort;
        _services.Content = ContentProfiles.WarfrontProfile;
        WarfrontMenuState.Joined = true;
        GD.Print($"warfront: joining 127.0.0.1:{port}; the server listened {Time.GetTicksMsec() / 1000.0 - _wfStartedAt:0.0} s after the start");
        WarfrontLogServerLines();
        TryJoin(ServerLaunch.LocalHost(port), remember: false);
        // Test aid --warfront-leave-after <seconds>: leaves the match by itself (the Esc menu's LEAVE MATCH) that long after the join, so a proof can come back to the menu without a keyboard.
        if (_args.TryGetValue("warfront-leave-after", out var after) && double.TryParse(after, System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds > 0 && JoinOverride is null)
        {
            var tree = GetTree();
            tree.CreateTimer(seconds).Timeout += () => { GD.Print("warfront: leaving the match (test aid)"); tree.CurrentScene?.CallDeferred("LeaveMatch"); };
        }
    }

    /// <summary>Each frame: follows the server's start, fills the preparing screen, hides Warfront's pages when another page took over.</summary>
    void WarfrontTick()
    {
        if (_wfPage is WarfrontPage wfPage && GodotObject.IsInstanceValid(wfPage)) wfPage.SetPlatesShown(!WarfrontConfirmOpen);   // the plates are top-level (they draw over everything): out of the way while the question is up
        if (_built) WarfrontAids();
        if (_page is not null && _page != _wfPage) WarfrontHidePages();   // another page took over (the front end sets _page; Warfront's pages leave it null)
        if (!_wfPending) return;
        WarfrontLogServerLines();
        double elapsed = Time.GetTicksMsec() / 1000.0 - _wfStartedAt;
        WarfrontShotAid(elapsed);
        if (_wfStatus is { } status && _wfPrep is { Visible: true })
        {
            string line = _wfLastStatus;
            status.Text = (line.Length > 0 ? line : WarfrontLocal.StartingText).ToUpperInvariant();
            status.TooltipText = status.Text;
            if (_wfElapsed is { } el) el.Text = WarfrontLocal.Elapsed(elapsed);
        }
        var (running, ready, _) = WarfrontServerState();
        switch (HostStart.StateOf(running, ready, HostedServer.ExitCode, elapsed, WarfrontLocal.StartLimitSeconds))
        {
            case HostStartState.Ready:
                _wfPending = false;
                WarfrontJoin();
                break;
            case HostStartState.PortInUse:
            case HostStartState.Failed:
                _wfPending = false;
                if (!CommandLineMusou) _services.Content = _services.Settings.ContentExtras;
                ShowWarfrontPage();
                Say(HostedServer.FailureText(), true);
                break;
            case HostStartState.TooSlow:
                _wfPending = false;
                HostedServer.Stop();
                if (!CommandLineMusou) _services.Content = _services.Settings.ContentExtras;
                ShowWarfrontPage();
                Say("The server took too long to start and was stopped.", true);
                break;
        }
    }

    int _wfShotStage, _wfShotFrame;

    /// <summary>
    /// Test aid <c>--warfront-prep-shot &lt;file stem&gt; [--warfront-prep-shot-at s]</c> (windowed run; a real start): once the start has run that many seconds (default 4) saves the preparing screen as it is (with
    /// the server's own status line), then opens the main menu's HOST page while the server runs and saves that, logging the page's texts; then the preparing screen comes back. Files: <c>&lt;stem&gt;-prep-WxH.png</c>, <c>&lt;stem&gt;-host-WxH.png</c>.
    /// </summary>
    void WarfrontShotAid(double elapsed)
    {
        if (!_args.TryGetValue("warfront-prep-shot", out var stem) || stem is null || _wfShotStage >= 3) return;
        double at = _args.TryGetValue("warfront-prep-shot-at", out var atText) && double.TryParse(atText, System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : 4;
        if (elapsed < at) return;
        string Save(string what)
        {
            var image = GetViewport().GetTexture().GetImage();
            string path = $"{stem}-{what}-{image.GetWidth()}x{image.GetHeight()}.png";
            image.SavePng(path);
            return path;
        }
        switch (_wfShotStage)
        {
            case 0:
                GD.Print($"warfront: still {Save("prep")}; status row \"{_wfStatus?.Text}\", elapsed row \"{_wfElapsed?.Text}\"; server tail [{HostedServer.LastLines(12)}]");
                ShowMenuPage("host");
                _wfShotFrame = 0;
                _wfShotStage = 1;
                break;
            case 1:
                if (++_wfShotFrame < 20) return;
                var labels = new List<string>();
                if (_hostPage is not null) foreach (var n in _hostPage.FindChildren("*", "Label", true, false)) if (n is Label { Visible: true } l && l.IsVisibleInTree() && l.Text.Length > 0) labels.Add(l.Text);
                var buttons = new List<string>();
                if (_hostPage is not null) foreach (var n in _hostPage.FindChildren("*", "Button", true, false)) if (n is Button { Visible: true } b && b.IsVisibleInTree()) buttons.Add(b.Text);
                GD.Print($"warfront: still {Save("host")}; host page labels [{string.Join(" | ", labels)}] buttons [{string.Join(" | ", buttons)}]");
                ShowWarfrontPrep();
                _wfShotStage = 3;
                break;
        }
    }

    static readonly HashSet<string> _wfLogged = new();
    static string _wfLastStatus = "";

    /// <summary>Copies the server's <c>warfront:</c> lines into the client's log once each (the hosted server has no console of its own; a proof reads the run's state hash from here).</summary>
    void WarfrontLogServerLines()
    {
        if (!HostedServer.Running) return;
        foreach (var raw in HostedServer.LastLines(12).Split(" | "))
        {
            if (!raw.Contains("warfront:", StringComparison.OrdinalIgnoreCase) || !_wfLogged.Add(raw)) continue;
            GD.Print("hosted server: " + raw);
            if (WarfrontLocal.LastStatus([raw]) is { Length: > 0 } status) _wfLastStatus = status;   // the server's start-up lines scroll the 12-line tail within a second: keep the last status line here
        }
    }

    /// <summary>Esc or B on Warfront's pages: Cancel on the preparing screen, BACK on the page; not while the confirmation is open (it answers No itself).</summary>
    bool WarfrontBack(InputEvent e)
    {
        if (WarfrontConfirmOpen) return false;
        if (_wfPrep is { Visible: true }) { WarfrontCancel(); return true; }
        if (_wfPage is { Visible: true }) { WarfrontBackToMain(); return true; }
        return false;
    }

    /// <summary>
    /// Test aids, once the menus are built: <c>--auto-warfront [--auto-warfront-new]</c> presses CONTINUE (or NEW CAMPAIGN) as the player would; <c>--warfront-screen page|prep|confirm</c> opens that screen for a still
    /// (the preparing screen with sample text; nothing starts). With <c>SM_WF_DIR</c> the run folder is a synthetic one.
    /// </summary>
    void WarfrontAids()
    {
        if (_args.ContainsKey("auto-warfront")) WarfrontAuto();
        if (_args.GetValueOrDefault("warfront-screen") is { } screen && !_wfScreenDone)
        {
            _wfScreenDone = true;
            ShowWarfrontPage();
            if (screen == "prep")
            {
                ShowWarfrontPrep();
                _wfStatus!.Text = "WARFRONT: BUILDING THE WALKING GRAPH, 60 PERCENT, ".ToUpperInvariant() + "THE WHOLE LANE SLOT AND EVERY ONE OF ITS SITES ARE CHECKED ONE AFTER THE OTHER, WHICH TAKES A WHILE ON A SLOW PC";
                _wfElapsed!.Text = WarfrontLocal.Elapsed(83);
            }
            else if (screen == "confirm") WarfrontNew(WarfrontLocal.ReadRun(WarfrontDir));
        }
    }

    bool _wfScreenDone;

    /// <summary>Test aid <c>--auto-warfront [new]</c>: opens the page and presses CONTINUE (or NEW CAMPAIGN, or when there is no run, NEW CAMPAIGN) once.</summary>
    void WarfrontAuto()
    {
        if (_autoWarfrontDone) return;
        _autoWarfrontDone = true;
        ShowWarfrontPage();
        var run = WarfrontLocal.ReadRun(WarfrontDir);
        GD.Print($"warfront: auto press ({(run.CanContinue && !_args.ContainsKey("auto-warfront-new") ? "continue" : "new")}); run on disk: {run.Kind} {run.Line}");
        WarfrontStart(newRun: !(run.CanContinue && !_args.ContainsKey("auto-warfront-new")));
    }

    static bool _autoWarfrontDone;

    /// <summary>Self-test: the page behaves as while a server starts (it watches the state given with <see cref="WarfrontStateForTest"/>).</summary>
    internal void WarfrontWaitForTest(double startedSecondsAgo = 0)
    {
        _wfPending = true;
        _wfStartedAt = Time.GetTicksMsec() / 1000.0 - startedSecondsAgo;
        ShowWarfrontPrep();
    }

    /// <summary>Self-test: whether the front end is still watching a start.</summary>
    internal bool WarfrontWaiting => _wfPending;
}
