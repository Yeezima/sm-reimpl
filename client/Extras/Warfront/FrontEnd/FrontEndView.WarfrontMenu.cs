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
/// The main menu's WARFRONT row and its pages (slice W16; owner's answer 3: a local campaign only he can join, the server run by his PC). Pages: CONTINUE CAMPAIGN (only with a run in progress, with its
/// one-line summary), NEW CAMPAIGN (asks first when a run is on disk), the Musou customizer and BACK; PREPARING THE BATTLEFIELD while the server starts. The server is the Warfront supervisor, started
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

    /// <summary>Self-test: the services have a configuration for the content profile in effect (it is read again after a change of profile).</summary>
    internal bool ServicesReadyForTest => _services.Ready;

    /// <summary>Self-test: the message line's text.</summary>
    internal string MessageText => _message?.Text ?? "";

    string WarfrontDir => WarfrontRunDirForTest ?? WarfrontLocal.RunDir();

    partial void WarfrontMenuRows(UiKit.MenuPage main)
    {
        if (!ContentExtras.WarfrontCompiled) return;
        WarfrontRow = UiKit.MenuButton(WarfrontLocal.RowText, ShowWarfrontPage, WarfrontLocal.RowTooltip);
        main.Rows.AddChild(WarfrontRow);
        AddChild(new WarfrontMenuWatcher(WarfrontTick, WarfrontBack));
        WarfrontAtMenu();
        if (_args.ContainsKey("warfront-selftest")) AddChild(new WarfrontLocalSelfTest(this));
    }

    /// <summary>The front end is built: a server this client started is stopped when the player left a match (a failed join keeps it for one retry).</summary>
    void WarfrontAtMenu()
    {
        bool ours = HostedServer.Running && HostedServer.Options is { Content: ContentProfiles.Warfront };
        switch (WarfrontLocal.AtMenu(ours, WarfrontMenuState.Joined, _startMessage, WarfrontMenuState.Retries))
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
                if (_args.ContainsKey("warfront-quit-at-menu")) { GD.Print($"warfront: server running after stop: {HostedServer.Running}; quitting (test aid)"); Callable.From(() => GetTree().Quit()).CallDeferred(); }
                break;
            default:
                if (!ours) { WarfrontMenuState.Joined = false; WarfrontMenuState.Retries = 0; }
                break;
        }
    }

    (bool Running, bool Ready, bool Warfront) WarfrontServerState() =>
        WarfrontStateForTest ?? (HostedServer.Running, HostedServer.Ready, HostedServer.Options is { Content: ContentProfiles.Warfront });

    void ShowWarfrontPage()
    {
        if (!_built) return;
        var (running, _, warfront) = WarfrontServerState();
        var run = WarfrontLocal.ReadRun(WarfrontDir);
        string? honor = WarfrontLocal.HonorLine(WarfrontDir);
        _wfPage?.QueueFree();
        var page = new UiKit.MenuPage("WarfrontMenu", WarfrontLocal.PageTitle) { Visible = false };
        Control? first = null;
        if (running)
        {
            page.Rows.AddChild(UiKit.MenuText(warfront ? "THE WARFRONT SERVER IS RUNNING." : WarfrontLocal.OtherServerText.ToUpperInvariant(), UiKit.Grey));
            first = UiKit.MenuButton("Stop server", WarfrontStopServer, "Stops the server this game started on this PC");
            page.Rows.AddChild(first);
        }
        if (run.CanContinue)
        {
            var cont = UiKit.MenuButton("Continue campaign", () => WarfrontStart(newRun: false), "Back to the campaign you left");
            first ??= cont;
            page.Rows.AddChild(cont);
            page.Rows.AddChild(UiKit.MenuText(run.Line, UiKit.Grey).With(l => l.TooltipText = run.Line));
        }
        var fresh = UiKit.MenuButton("New campaign", () => WarfrontNew(run), "Starts a new campaign");
        first ??= fresh;
        page.Rows.AddChild(fresh);
        if (honor is not null) page.Rows.AddChild(UiKit.MenuText(honor, UiKit.Grey).With(l => l.TooltipText = honor));
        page.Rows.AddChild(UiKit.MenuGap(0.5f));
        page.Rows.AddChild(UiKit.MenuButton("Customizer", WarfrontCustomizer, "Your Musou loadouts: Standard Marines, the Librarian, the Heretic (not yet)"));
        page.Rows.AddChild(UiKit.MenuButton("Back", WarfrontBackToMain));
        _first[page] = first;
        AddChild(page);
        _wfPage = page;
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
        WarfrontHidePages();
        ShowMainMenu();
    }

    void WarfrontCustomizer()
    {
        WarfrontHidePages();
        _services.Content = ContentExtras.Musou;   // the Musou profile's creator, as Musou Mode's page opens it; its BACK returns to the main menu, which puts the lobby's profile back
        ShowCreator();
    }

    void WarfrontNew(WarfrontRunInfo run)
    {
        if (!run.NewNeedsConfirm) { WarfrontStart(newRun: true); return; }
        _wfDialog = UiKit.Dialog.Open(_wfPage!, this, WarfrontLocal.ConfirmTitle, WarfrontLocal.ConfirmBody, ["Yes", "No"], i => { if (i == 0) WarfrontStart(newRun: true); }, cancelIndex: 1);
        var dialog = _wfDialog;
        Callable.From(() =>   // after the dialog's own first focus: No is the default answer
        {
            if (!GodotObject.IsInstanceValid(dialog) || !dialog.IsInsideTree()) return;
            foreach (var n in dialog.FindChildren("*", "Button", true, false))
                if (n is Button b && b.Text.Equals("No", StringComparison.OrdinalIgnoreCase)) { b.GrabFocus(); return; }
        }).CallDeferred();
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
        _services.Content = ContentProfiles.WarfrontProfile;
        if (HostedServer.Start(exe, WarfrontLocal.Options(port, _services.GameDir, newRun)) is { } error)
        {
            Say(error, true);
            if (!CommandLineMusou) _services.Content = _services.Settings.ContentExtras;
            return;
        }
        GD.Print($"warfront: supervisor started (pid {HostedServer.ProcessId}) on UDP {port}, this PC only, one human, new campaign {newRun}; kill-on-close job: {HostedServer.Guarded}");
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
        var page = new UiKit.MenuPage("WarfrontPreparing", WarfrontLocal.PageTitle) { Visible = false };
        page.Rows.AddChild(UiKit.MenuText(WarfrontLocal.PreparingTitle, UiKit.White));
        _wfStatus = UiKit.MenuText(WarfrontLocal.StartingText.ToUpperInvariant(), UiKit.Grey);
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
        if (_built) WarfrontAids();
        if (_page is not null) WarfrontHidePages();   // another page took over (the front end sets _page; Warfront's pages leave it null)
        if (!_wfPending) return;
        WarfrontLogServerLines();
        double elapsed = Time.GetTicksMsec() / 1000.0 - _wfStartedAt;
        if (_wfStatus is { } status && _wfPrep is { Visible: true })
        {
            string line = WarfrontLocal.LastStatus(HostedServer.LastLines(12).Split(" | "));
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

    static readonly HashSet<string> _wfLogged = new();

    /// <summary>Copies the server's <c>warfront:</c> lines into the client's log once each (the hosted server has no console of its own; a proof reads the run's state hash from here).</summary>
    void WarfrontLogServerLines()
    {
        if (!HostedServer.Running) return;
        foreach (var raw in HostedServer.LastLines(12).Split(" | "))
            if (raw.Contains("warfront:", StringComparison.OrdinalIgnoreCase) && _wfLogged.Add(raw)) GD.Print("hosted server: " + raw);
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
                _wfStatus!.Text = "WARFRONT: BUILDING THE WALKING GRAPH, 60 PERCENT";
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
