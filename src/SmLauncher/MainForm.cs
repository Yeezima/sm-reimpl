using System.Drawing.Imaging;
using SmLauncher.Core;

namespace SmLauncher;

/// <summary>
/// The launcher window. Top: the installed build and the update line (Update / Resume update). Middle: "What's new" of the newest build, as plain
/// text. Then the progress (percent, MB, speed, time left, Cancel) while an update runs, the Play / Host / Join buttons, and the links: Check files,
/// Roll back, the release page, Report an issue, the install folder, Clear caches. The first run shows a set-up panel instead (install folder,
/// use the unpacked build the launcher came in, or download the newest).
/// </summary>
sealed class MainForm : Form
{
    static readonly Color Accent = Color.FromArgb(0x7A, 0x5A, 0x1E);        // the Update button (dark amber; the links use Theme.Accent)
    static readonly Color PlayGreen = Theme.Green;
    static readonly Color Good = Theme.GoodText;
    static readonly Color Warn = Theme.WarnText;
    static readonly Color Bad = Theme.BadText;
    static readonly Color Muted = Theme.Muted;

    readonly Args _args;
    readonly SafeHttp _http = new(NetPolicy.GitHub);
    readonly ReleaseSource _source;
    readonly string? _adoptCandidate;
    readonly VersionFile? _adoptVersion;
    InstallLayout? _layout;
    RootLock? _lock;
    Updater? _updater;
    AppLog? _log;
    ReleaseInfo? _newest;
    string? _checkProblem;

    CancellationTokenSource? _cts;
    bool _busy;
    UpdatePhase? _busyPhase;
    bool _firstRun;
    bool _closingAfterCancel;
    bool _playable;
    string? _newerLauncher;
    bool _handedOver;

    // controls
    readonly TilePanel _main = new() { Dock = DockStyle.Fill };
    readonly TilePanel _setup = new() { Dock = DockStyle.Fill, Visible = false };
    readonly DarkLabel _installed = new() { AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold) };
    readonly DarkLabel _installedSub = new() { AutoSize = true, ForeColor = Muted };
    readonly DarkLabel _notice = new() { AutoSize = true, ForeColor = Warn, Visible = false };
    readonly DarkLabel _update = new() { AutoSize = true, Font = new Font("Segoe UI", 10f) };
    readonly Button _btnUpdate = new() { Text = "Update", AutoSize = true, Visible = false };
    readonly DarkLabel _notesTitle = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Text = "What's new" };
    readonly TextBox _notes = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Theme.Input, BorderStyle = BorderStyle.FixedSingle };
    readonly TileTable _progressPanel = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Visible = false, Padding = new Padding(16, 4, 16, 4) };
    readonly DarkProgress _progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 22, MinimumSize = new Size(40, 22), Margin = new Padding(0, 3, 8, 3) };
    readonly DarkLabel _progressText = new() { AutoSize = true };
    readonly Button _btnCancel = new() { Text = "Cancel", AutoSize = true };
    readonly Button _btnPlay = new() { Text = "PLAY", AutoSize = false, Size = new Size(150, 44), Font = new Font("Segoe UI", 12f, FontStyle.Bold) };
    readonly Button _btnHost = new() { Text = "Host a match on this PC", AutoSize = true, Height = 44 };
    readonly Button _btnJoin = new() { Text = "Join a server...", AutoSize = true, Height = 44 };
    readonly Button _btnLan = new() { Text = "Host for friends (LAN)", AutoSize = true, Height = 44 };
    readonly DarkCheckBox _cbSafeMode = new() { AutoSize = true, Text = "Old GPU / safe mode (simpler graphics for old graphics cards)", Margin = new Padding(0, 10, 3, 3) };
    readonly LinkLabel _lnkCheck = Link("Check files");
    readonly LinkLabel _lnkRollback = Link("Roll back to the previous version");
    readonly LinkLabel _lnkRelease = Link("Open the release page");
    readonly LinkLabel _lnkIssues = Link("Report an issue");
    readonly LinkLabel _lnkFolder = Link("Open the install folder");
    readonly LinkLabel _lnkCaches = Link("Repair: clear caches");
    readonly LinkLabel _lnkSelfUpdate = Link("A newer launcher came with this build: update the launcher");
    readonly DarkLabel _folder = new() { AutoSize = false, Ellipsize = true, ForeColor = Muted, Padding = new Padding(16, 2, 16, 8) };
    // set-up (first run)
    readonly TextBox _setupFolder = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 8, 3) };
    readonly Button _btnBrowse = new() { Text = "Browse...", AutoSize = true };
    readonly DarkRadioButton _rbAdopt = new() { AutoSize = true };
    readonly DarkRadioButton _rbDownload = new() { AutoSize = true, Text = "Download the newest build (looking it up...)" };
    readonly DarkCheckBox _cbShortcuts = new() { AutoSize = true, Checked = true, Text = "Create shortcuts on the desktop and in the Start menu" };
    readonly Button _btnInstall = new() { Text = "Install", AutoSize = false, Size = new Size(150, 40), Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
    readonly DarkLabel _setupStatus = new() { AutoSize = true, Margin = new Padding(3, 10, 3, 3) };

    public MainForm(Args args)
    {
        _args = args;
        _source = new ReleaseSource(_http);
        _adoptCandidate = RootFinder.AdoptCandidate();
        _adoptVersion = _adoptCandidate is null ? null : PackageLayout.ReadVersion(_adoptCandidate);
        Text = "SM Reimpl Launcher";
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleDimensions = new SizeF(96f, 96f);       // sizes are in pixels at 96 dpi and scale with the display (fonts are in points)
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(780, 680);
        MinimumSize = new Size(660, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Base;
        try { Icon = Icon.ExtractAssociatedIcon(LauncherInfo.ExePath ?? ""); } catch (Exception) { }
        if (_args.Has("screenshot"))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);       // off-screen, never on the owner's desktop
            ShowInTaskbar = false;
        }
        BuildLayout();
        Theme.Apply(this);
        WireEvents();
        if (_args["ui-workarea"] is { } wk && wk.Split('x') is [var ww, var wh] && int.TryParse(ww, out int wwi) && int.TryParse(wh, out int whi)) _simWork = new Rectangle(0, 0, wwi, whi);
        if (_args["ui-scale"] is { } us && double.TryParse(us, System.Globalization.CultureInfo.InvariantCulture, out double sim) && sim is > 0.5 and < 4) SimulateScale(sim);
    }

    Rectangle? _simWork;

    protected override void OnLoad(EventArgs e) { base.OnLoad(e); FitToWorkArea(); }

    /// <summary>Keeps the window inside the screen's work area (above the taskbar) after DPI scaling and centres it there; the notes box takes the lost height, and a
    /// work area too small for the content makes the panels scroll instead of cutting the controls off.</summary>
    internal void FitToWorkArea()
    {
        var wa = _simWork ?? Screen.FromControl(this).WorkingArea;
        int w = Math.Min(Width, wa.Width), h = Math.Min(Height, wa.Height);
        MinimumSize = new Size(Math.Min(MinimumSize.Width, w), Math.Min(MinimumSize.Height, h));
        Size = new Size(w, h);
        if (!_args.Has("screenshot"))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2);
        }
        _main.AutoScroll = true;
        _main.AutoScrollMinSize = new Size(0, (int)(330 * Theme.Scale(this)));
        _setup.AutoScroll = true;
        _setup.AutoScrollMinSize = new Size(0, (int)(480 * Theme.Scale(this)));
    }

    protected override bool ShowWithoutActivation => _args.Has("screenshot");

    /// <summary>Lets a wrapping label use the width of its host (minus the label's margin and the host's padding) instead of a fixed pixel width.</summary>
    static void WrapTo(Label l, Control host, int reserve = 0)
    {
        void Fit() { int w = host.ClientSize.Width - host.Padding.Horizontal - l.Margin.Horizontal - reserve; if (w > 40) l.MaximumSize = new Size(w, 0); }
        host.Resize += (_, _) => Fit();
        host.HandleCreated += (_, _) => Fit();
        Fit();
    }

    static LinkLabel Link(string text) => new DarkLink { Text = text, AutoSize = true, LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentHover, Margin = new Padding(0, 2, 18, 2) };

    static void Accentuate(Button b, Color c)
    {
        b.BackColor = c;
        b.ForeColor = Color.White;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.UseVisualStyleBackColor = false;
    }

    void BuildLayout()
    {
        // Nothing here uses transparent controls, fixed pixel positions or fixed wrap widths: every label paints its own background (DarkLabel), every
        // container is a table or flow that sizes to its content, and wrapping labels take the width of the cell they are in.
        var header = new TileTable { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2, Solid = Theme.Header, Padding = new Padding(16, 8, 16, 10) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new DarkLabel { Text = "SM REIMPL", ForeColor = Theme.Text, Font = new Font("Segoe UI", 18f, FontStyle.Bold), AutoSize = true, Margin = new Padding(0) };
        var subtitle = new DarkLabel { Text = "Space Marine (2011) multiplayer playtest launcher", ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(3, 0, 0, 0) };
        var version = new DarkLabel { Text = "launcher " + LauncherInfo.VersionText, ForeColor = Theme.Muted, AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Margin = new Padding(12, 0, 0, 0) };
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(subtitle, 0, 1);
        header.Controls.Add(version, 1, 1);

        // the main view
        var grid = new TileTable { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16, 12, 16, 4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var updateRow = new TileTable { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 6) };
        updateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        updateRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        updateRow.Controls.Add(_update, 0, 0);
        updateRow.Controls.Add(_btnUpdate, 1, 0);
        _update.Anchor = AnchorStyles.Left;
        _btnUpdate.Anchor = AnchorStyles.Right;
        _btnUpdate.Margin = new Padding(12, 3, 0, 3);
        Accentuate(_btnUpdate, Accent);
        _btnUpdate.Padding = new Padding(14, 4, 14, 4);
        _btnUpdate.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        var buttons = new TileFlow { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 10, 0, 0) };
        Accentuate(_btnPlay, PlayGreen);
        foreach (var b in new[] { _btnHost, _btnJoin, _btnLan }) { b.AutoSize = false; b.Size = new Size(b.PreferredSize.Width + 20, 44); }
        buttons.Controls.AddRange([_btnPlay, _btnHost, _btnJoin, _btnLan]);
        for (int i = 0; i < 5; i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _notes.MinimumSize = new Size(100, 90);
        _notes.Margin = new Padding(3, 3, 3, 6);
        grid.Controls.Add(_installed, 0, 0);
        grid.Controls.Add(_installedSub, 0, 1);
        grid.Controls.Add(_notice, 0, 2);
        grid.Controls.Add(updateRow, 0, 3);
        grid.Controls.Add(_notesTitle, 0, 4);
        grid.Controls.Add(_notes, 0, 5);
        grid.Controls.Add(buttons, 0, 6);
        grid.Controls.Add(_cbSafeMode, 0, 7);
        WrapTo(_installedSub, grid);
        WrapTo(_notice, grid);
        updateRow.Resize += (_, _) => WrapUpdate(updateRow);
        _btnUpdate.VisibleChanged += (_, _) => WrapUpdate(updateRow);
        _btnUpdate.SizeChanged += (_, _) => WrapUpdate(updateRow);
        buttons.TabIndex = 0; _cbSafeMode.TabIndex = 1; updateRow.TabIndex = 2; _notes.TabIndex = 3;      // tab order: PLAY first, then Host, Join, LAN, safe mode, Update, the notes
        _btnPlay.TabIndex = 0; _btnHost.TabIndex = 1; _btnJoin.TabIndex = 2; _btnLan.TabIndex = 3;
        _main.Controls.Add(grid);

        // the set-up panel (first run)
        var s = new TileTable { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16, 12, 16, 4) };
        s.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var welcome = new DarkLabel { AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold), Text = "Welcome! Let's install the playtest build." };
        var explain = new DarkLabel
        {
            AutoSize = true, ForeColor = Muted,
            Text = "The launcher keeps the build up to date and starts it. It needs no administrator rights. Your profile and settings stay in " +
                   "%APPDATA%\\sm-reimpl and are never changed by an update. You need your own Space Marine (2011) installed through Steam.",
        };
        var folderTitle = new DarkLabel { AutoSize = true, Text = "Install folder:", Margin = new Padding(3, 14, 3, 2) };
        var folderRow = new TileTable { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _setupFolder.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        folderRow.Controls.Add(_setupFolder, 0, 0);
        folderRow.Controls.Add(_btnBrowse, 1, 0);
        var choiceTitle = new DarkLabel { AutoSize = true, Text = "What to install:", Margin = new Padding(3, 14, 3, 2) };
        Accentuate(_btnInstall, PlayGreen);
        foreach (var c in new Control[] { welcome, explain, folderTitle, folderRow, choiceTitle, _rbAdopt, _rbDownload, _cbShortcuts, _btnInstall, _setupStatus })
        {
            s.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            s.Controls.Add(c);
        }
        _cbShortcuts.Margin = new Padding(3, 12, 3, 8);
        WrapTo(explain, s);
        WrapTo(_setupStatus, s);
        _setup.Controls.Add(s);

        // progress, links, folder
        _progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _progressPanel.Controls.Add(_progress, 0, 0);
        _progressPanel.Controls.Add(_btnCancel, 1, 0);
        _progressPanel.Controls.Add(_progressText, 0, 1);
        _progressPanel.SetColumnSpan(_progressText, 2);
        _progress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _btnCancel.Anchor = AnchorStyles.Right;
        WrapTo(_progressText, _progressPanel);
        var links = new TileFlow { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(16, 6, 16, 4), WrapContents = true };
        links.Controls.AddRange([_lnkCheck, _lnkRollback, _lnkRelease, _lnkIssues, _lnkFolder, _lnkCaches, _lnkSelfUpdate]);
        _lnkSelfUpdate.Visible = false;
        _folder.Dock = DockStyle.Bottom;

        var content = new TilePanel { Dock = DockStyle.Fill };
        content.Controls.Add(_main);
        content.Controls.Add(_setup);
        Controls.Add(content);
        Controls.Add(_progressPanel);
        Controls.Add(links);
        Controls.Add(_folder);
        Controls.Add(header);
        // docking (last added docks first): the header at the top; from the bottom edge up the folder line, the links, the progress; the content fills the rest
        _progressPanel.Dock = DockStyle.Bottom;
        content.TabIndex = 0; _progressPanel.TabIndex = 1; links.TabIndex = 2; _folder.TabStop = false; header.TabStop = false;
    }

    /// <summary>The update line may use the row's width minus the Update button's, whether that button is showing or not.</summary>
    void WrapUpdate(Control row)
    {
        int reserve = _btnUpdate.Visible ? _btnUpdate.Width + _btnUpdate.Margin.Horizontal : 0;
        int w = row.ClientSize.Width - _update.Margin.Horizontal - reserve;
        if (w > 40) _update.MaximumSize = new Size(w, 0);
    }

    void WireEvents()
    {
        Shown += async (_, _) =>
        {
            if (_args["ui-state"] is { } state)         // test aid: a fixed state of the window without install folder or network (layout check)
            {
                ApplyDemoState(state);
                if (_args["screenshot"] is { } shot) { await Task.Delay(300); CaptureTo(shot); }
            }
            else await StartAsync();
        };
        FormClosing += OnClosing;
        _btnUpdate.Click += async (_, _) => await UpdateAsync();
        _btnCancel.Click += (_, _) => { _cts?.Cancel(); _btnCancel.Enabled = false; };
        _btnPlay.Click += (_, _) => PlayGame();
        _cbSafeMode.CheckedChanged += (_, _) => SaveSafeMode();
        _btnHost.Click += (_, _) => Start(GameStart.Host, "Starting a server only this PC can join (its window lists what to do next)...");
        _btnLan.Click += (_, _) => Start(GameStart.HostLan, "Starting a server other PCs can join (Windows Firewall may ask once)...");
        _btnJoin.Click += (_, _) => Join();
        _lnkCheck.LinkClicked += async (_, _) => await CheckFilesAsync();
        _lnkRollback.LinkClicked += (_, _) => Rollback();
        _lnkRelease.LinkClicked += (_, _) => GameStart.OpenUrl(ReleasePage());
        _lnkIssues.LinkClicked += (_, _) => GameStart.OpenUrl(ReleaseSource.IssuesUrl);
        _lnkFolder.LinkClicked += (_, _) => { if (_layout is not null) GameStart.OpenFolder(_updater?.CurrentDir() ?? _layout.Root); };
        _lnkCaches.LinkClicked += async (_, _) => await ClearCachesAsync();
        _lnkSelfUpdate.LinkClicked += (_, _) => OfferLauncherUpdate(ask: false);
        _btnBrowse.Click += (_, _) => Browse();
        _btnInstall.Click += async (_, _) => await FirstInstallAsync();
    }

    // ---- start-up ------------------------------------------------------------------------------------------------------------

    async Task StartAsync()
    {
        if (_args["screenshot"] is { } shot && double.TryParse(_args["screenshot-after"], System.Globalization.CultureInfo.InvariantCulture, out double after))
        {
            var timer = new System.Windows.Forms.Timer { Interval = 100 };
            var due = DateTime.UtcNow.AddSeconds(Math.Max(0, after));
            timer.Tick += (_, _) => { if (DateTime.UtcNow >= due) { timer.Stop(); CaptureTo(shot); } };
            timer.Start();
        }
        string? root = RootFinder.Find(_args);
        if (root is not null && await OpenRootAsync(root) && _updater!.CurrentDir() is not null)
        {
            _firstRun = false;
            ShowMain();
        }
        else
        {
            if (_lock is null && root is not null && _layout is not null) { Close(); return; }      // another launcher holds it
            _firstRun = true;
            ShowSetup(root ?? InstallLayout.DefaultRoot);
        }
        await CheckForUpdatesAsync();
        // A launcher that replaced itself keeps its old copy until the new one got this far (its first update check, done or failed).
        if (_args.Has("self-updated")) SelfUpdate.ConfirmStarted(_args["self-updated"]);
        else SelfUpdate.CleanOld();
        var pending = _updater?.ReadState();
        if (!_firstRun && pending is { Source: "download", Phase: >= UpdatePhase.Verifying } && (_newest is null || _newest.Tag == pending.Tag))
            await UpdateAsync();                                       // an interrupted update with nothing left to download: finish it now
        else if (_args.Has("auto-update") && !_firstRun && _btnUpdate.Visible) await UpdateAsync();
        if (_args.Has("auto-update-launcher") && !_firstRun) { OfferLauncherUpdate(ask: false); if (IsDisposed || _handedOver) return; }
        if (_args.Has("launcher-update-failed"))
        {
            _notice.Text = (string.IsNullOrEmpty(_notice.Text) ? "" : _notice.Text + Environment.NewLine) + "The new launcher did not start, so the previous one was kept. Please report it (Report an issue).";
            _notice.Visible = true;
        }
        if (_args["screenshot"] is { } png && !_args.Has("screenshot-after")) CaptureTo(png);
    }

    /// <summary>Takes the install folder: its lock (one launcher per folder), the recovery after an interrupted update, the installed state.</summary>
    async Task<bool> OpenRootAsync(string root)
    {
        if (_layout is not null && string.Equals(_layout.Root, Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && _lock is not null) return true;
        _lock?.Dispose();
        _lock = null;
        _layout = new InstallLayout(root);
        LoadSafeMode();
        _log = new AppLog(_layout);
        var layout = _layout;
        _lock = await Task.Run(() => RootLock.TryAcquire(layout, TimeSpan.FromSeconds(10)));
        if (_lock is null)
        {
            DarkBox.Show(this, $"Another SM Reimpl Launcher is already open for {layout.Root}.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        _log.Write($"launcher {LauncherInfo.VersionText} started from {LauncherInfo.ExePath}");
        _updater = new Updater(layout, new Downloader(_http), _log.Write);
        var updater = _updater;
        var report = await Task.Run(() => updater.Recover());
        _notice.Text = string.Join(Environment.NewLine, report.Notes);
        _notice.Visible = report.Notes.Count > 0;
        _folder.Text = "Install folder: " + layout.Root;
        return true;
    }

    void ShowMain()
    {
        _setup.Visible = false;
        _main.Visible = true;
        RefreshInstalled();
    }

    void ShowSetup(string folder)
    {
        _main.Visible = false;
        _setup.Visible = true;
        _setupFolder.Text = folder;
        _folder.Text = "";
        if (_adoptCandidate is not null && _adoptVersion?.Tag is { } tag)
        {
            _rbAdopt.Text = $"Use the build in this folder: {tag} (copied, no download)";
            _rbAdopt.Visible = true;
            _rbAdopt.Checked = true;
        }
        else { _rbAdopt.Visible = false; _rbDownload.Checked = true; }
        if (_args.Has("no-shortcuts")) { _cbShortcuts.Checked = false; _cbShortcuts.Enabled = false; }
        UpdateLinks();
    }

    // ---- the installed build, the update line ----------------------------------------------------------------------------------

    void RefreshInstalled()
    {
        if (IsDisposed) return;
        if (_updater is null) return;
        var p = _updater.ReadPointer();
        _playable = p.Current is { } t0 && _updater.IsUsable(t0);
        _newerLauncher = _layout is null ? null : SelfUpdate.NewerBundled(_updater.CurrentDir(), _layout.LauncherExe);
        if (p.Current is { } tag && _updater.IsUsable(tag))
        {
            var meta = p.Versions.GetValueOrDefault(tag);
            _installed.Text = $"Installed: {tag}";
            var bits = new List<string>();
            if (meta?.PublishedAt is { } pub) bits.Add($"published {pub.LocalDateTime:d MMM yyyy}");
            else if (meta?.Built is { } built) bits.Add($"built {built}");
            if (meta is not null) bits.Add($"installed {meta.InstalledAt.LocalDateTime:d MMM yyyy HH:mm}");
            if (p.Previous is not null) bits.Add($"previous: {p.Previous}");
            _installedSub.Text = string.Join(" · ", bits);
        }
        else
        {
            _installed.Text = "Nothing installed yet";
            _installedSub.Text = "";
        }
        _lnkRollback.Text = p.Previous is not null ? $"Roll back to {p.Previous}" : "Roll back to the previous version";
        ShowNotes();
        UpdateLinks();
        UpdateButtons();
    }

    void ShowNotes()
    {
        if (IsDisposed) return;
        if (_updater is null) return;
        var p = _updater.ReadPointer();
        if (_newest is not null && _newest.Tag != p.Current)
        {
            _notesTitle.Text = $"What's new in {_newest.Tag}";
            _notes.Text = Lines(_newest.WhatsNew is { Length: > 0 } w ? w : "(The release page lists no changes for this build.)");
        }
        else
        {
            var meta = p.Current is null ? null : p.Versions.GetValueOrDefault(p.Current);
            _notesTitle.Text = p.Current is null ? "What's new" : $"What's new in {p.Current}";
            _notes.Text = Lines(meta?.WhatsNew is { Length: > 0 } w ? w : (_newest?.Tag == p.Current && _newest?.WhatsNew is { Length: > 0 } n ? n : "(No notes for this build.)"));
        }
    }

    static string Lines(string s) => s.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

    async Task CheckForUpdatesAsync()
    {
        SetLine("Looking for updates...", Muted);
        _btnUpdate.Visible = false;
        if (_args.Has("offline")) { _newest = null; _checkProblem = "The update check was skipped (--offline)."; }
        else
        {
            try { _newest = await _source.GetNewestAsync(CancellationToken.None); _checkProblem = null; }
            catch (ReleaseCheckException e) { _newest = null; _checkProblem = e.Message; }
            catch (NetPolicyException e) { _newest = null; _checkProblem = e.Message; }
            catch (Exception e) { _newest = null; _checkProblem = "The update check failed: " + e.Message; }
        }
        _log?.Write(_newest is not null ? $"check: newest {_newest.Tag}" : $"check: {_checkProblem ?? "nothing published"}");
        if (_source.LastSkipped.Count > 0 && !_args.Has("offline"))
        {
            foreach (var s in _source.LastSkipped) _log?.Write("check: not offered: " + s);
            string note = "A newer release could not be offered: " + _source.LastSkipped[0] + ".";
            _notice.Text = string.IsNullOrEmpty(_notice.Text) ? note : _notice.Text + Environment.NewLine + note;
            _notice.Visible = true;
        }
        ShowUpdateLine();
        if (_firstRun) UpdateSetupChoices();
        else ShowNotes();
    }

    void ShowUpdateLine()
    {
        if (IsDisposed) return;
        if (_firstRun || _updater is null) return;
        var p = _updater.ReadPointer();
        var pending = _updater.ReadState();
        _btnUpdate.Visible = false;
        if (_checkProblem is not null)
        {
            string text = (p.Current is not null ? "Offline: the installed build can be played. " : "") + _checkProblem;
            if (pending is { Source: "download" }) { text += " An unfinished update is kept and continues when GitHub is reachable."; }
            SetLine(text, Warn);
            return;
        }
        if (_newest is null) { SetLine("No playtest build is published right now. The installed build can be played.", Muted); return; }
        if (p.Current == _newest.Tag) { SetLine($"You have the newest build (published {_newest.PublishedAt.LocalDateTime:d MMM yyyy}).", Good); return; }
        bool resume = pending is { Source: "download" } && pending.Tag == _newest.Tag;
        string size = _updater.IsUsable(_newest.Tag) ? "installed already, no download"
            : resume && pending!.AssetName is { } a && File.Exists(_updater.Layout.PartFile(a))
            ? $"{Format.Mb(new FileInfo(_updater.Layout.PartFile(a)).Length)} of {Format.Mb(_newest.Asset.Size)} downloaded"
            : $"{Format.Mb(_newest.Asset.Size)} download";
        bool older = UpdateOffer.Classify(p.Current, p.Current is null ? null : p.Versions.GetValueOrDefault(p.Current), _newest) == OfferKind.Older;
        SetLine(older
            ? $"The current playtest build is {_newest.Tag}, published {_newest.PublishedAt.LocalDateTime:d MMM yyyy} ({size}); yours is newer. Everyone in a match needs the same build."
            : $"A new build is available: {_newest.Tag}, published {_newest.PublishedAt.LocalDateTime:d MMM yyyy} ({size}).", Theme.Accent);
        _btnUpdate.Text = resume ? "Resume update" : older ? "Switch to it" : "Update";
        _btnUpdate.Visible = !_busy;
    }

    void SetLine(string text, Color color)
    {
        if (IsDisposed) return;
        _update.Text = text;
        _update.ForeColor = color;
        _setupStatus.Text = _firstRun ? text : _setupStatus.Text;
        _setupStatus.ForeColor = color;
    }

    void UpdateSetupChoices()
    {
        if (_newest is not null) _rbDownload.Text = $"Download the newest build: {_newest.Tag} ({Format.Mb(_newest.Asset.Size)})";
        else _rbDownload.Text = "Download the newest build (GitHub cannot be reached right now)";
        _rbDownload.Enabled = _newest is not null;
        if (_newest is null && !_rbAdopt.Visible) _rbDownload.Checked = true;
        if (_checkProblem is not null) SetLine(_checkProblem, Warn);
        else SetLine("", Muted);
    }

    void UpdateButtons()
    {
        bool playable = _playable;                     // cached by RefreshInstalled: this runs on every progress report
        bool locked = _busy && _busyPhase is not null and not UpdatePhase.Downloading;
        foreach (var b in new[] { _btnPlay, _btnHost, _btnJoin, _btnLan }) b.Enabled = playable && !locked;
        _btnInstall.Enabled = !_busy;
        _btnBrowse.Enabled = !_busy;
    }

    void UpdateLinks()
    {
        bool have = _updater?.CurrentDir() is not null;
        var p = _updater?.ReadPointer();
        _lnkCheck.Enabled = have && !_busy;
        _lnkRollback.Enabled = !_busy && p?.Previous is not null && _updater!.IsUsable(p.Previous);
        _lnkCaches.Enabled = !_busy;
        _lnkFolder.Enabled = _layout is not null && Directory.Exists(_layout.Root);
        _lnkSelfUpdate.Visible = !_busy && _layout is not null && _newerLauncher is not null;       // found by RefreshInstalled (it hashes the file)
    }

    string ReleasePage()
    {
        var p = _updater?.ReadPointer();
        if (_newest is not null && (p?.Current is null || _newest.Tag != p.Current)) return _newest.HtmlUrl;
        if (p?.Current is { } t && p.Versions.GetValueOrDefault(t)?.HtmlUrl is { Length: > 0 } url) return url;
        return _newest?.HtmlUrl ?? ReleaseSource.ReleasesUrl;
    }

    // ---- update ----------------------------------------------------------------------------------------------------------------

    async Task UpdateAsync()
    {
        if (_updater is null || _busy) return;
        var pending = _updater.ReadState();
        if (_newest is null && pending is null) return;
        BeginBusy(UpdatePhase.Downloading);
        _cts = new CancellationTokenSource();
        var progress = new Progress<UpdateStatus>(OnProgress);
        string? done = null;
        try
        {
            done = _newest is not null                                  // the newest release (a different pending update is dropped)
                ? await _updater.InstallAsync(_newest, progress, _cts.Token)
                : await _updater.ResumeAsync(progress, _cts.Token);     // offline: finish what update.json records
            EndBusy();
            if (done is not null)
            {
                RefreshInstalled();
                SetLine($"Updated to {done}. Press PLAY.", Good);
                OfferLauncherUpdate(ask: true);
            }
        }
        catch (OperationCanceledException)
        {
            EndBusy();
            SetLine(_closingAfterCancel ? "" : "The update is paused. Press Resume update to continue; what was downloaded is kept.", Warn);
            _btnUpdate.Text = "Resume update";
            _btnUpdate.Visible = _newest is not null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)       // whatever it was, the window must leave the busy state
        {
            EndBusy();
            _log?.Write($"update failed: {e.GetType().Name}: {e.Message}");
            SetLine(e.Message, e is VerificationException ? Bad : Warn);
            _btnUpdate.Text = "Try again";
            _btnUpdate.Visible = _newest is not null;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            if (_closingAfterCancel) BeginInvoke(Close);
        }
    }

    void OnProgress(UpdateStatus s)
    {
        if (IsDisposed) return;
        _busyPhase = s.Phase;
        UpdateButtons();
        string step = s.Phase switch
        {
            UpdatePhase.Downloading => "Downloading",
            UpdatePhase.Verifying => "Checking",
            UpdatePhase.Extracting => "Unpacking",
            UpdatePhase.Switching => "Switching",
            _ => "",
        };
        _btnCancel.Visible = s.Phase is UpdatePhase.Downloading or UpdatePhase.Verifying or UpdatePhase.Extracting;
        if (s.Download is { } d)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = (int)(d.Fraction * 1000);
            _progressText.Text = $"{step}: {Format.Download(d)}";
            _progressText.ForeColor = d.Note is null ? Theme.Text : Warn;
            return;
        }
        if (s.Fraction is { } f)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = (int)(Math.Clamp(f, 0, 1) * 1000);
            _progressText.Text = $"{(s.Message.StartsWith(step, StringComparison.Ordinal) ? s.Message : step + ": " + s.Message)} · {f:P0}";
        }
        else
        {
            _progress.Style = ProgressBarStyle.Marquee;
            _progressText.Text = s.Message;
        }
        _progressText.ForeColor = Theme.Text;
    }

    void BeginBusy(UpdatePhase phase)
    {
        _busy = true;
        _busyPhase = phase;
        _progressPanel.Visible = true;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = 0;
        _progressText.Text = "Starting...";
        _btnCancel.Enabled = true;
        _btnCancel.Visible = true;
        _btnUpdate.Visible = false;
        UpdateButtons();
        UpdateLinks();
    }

    void EndBusy()
    {
        if (IsDisposed) return;
        _busy = false;
        _busyPhase = null;
        _progressPanel.Visible = false;
        UpdateButtons();
        UpdateLinks();
    }

    // ---- first run ---------------------------------------------------------------------------------------------------------

    void Browse()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose the folder for the playtest build (a new, empty folder is best)", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(_setupFolder.Text)) dialog.InitialDirectory = _setupFolder.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _setupFolder.Text = dialog.SelectedPath;
    }

    async Task FirstInstallAsync()
    {
        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_setupFolder.Text.Trim())); }
        catch (Exception) { SetLine("That is not a folder path.", Bad); return; }
        if (_adoptCandidate is not null && (Updater.IsInsideFolder(root, _adoptCandidate) || Updater.IsInsideFolder(_adoptCandidate, root)))
        { SetLine("Choose an install folder outside the unpacked zip folder (the default is fine).", Bad); return; }
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !InstallLayout.LooksLikeRoot(root))
        { SetLine("That folder is not empty. Choose an empty or new folder (the default is fine).", Bad); return; }
        try { Directory.CreateDirectory(root); }
        catch (Exception e) { SetLine($"The folder cannot be created: {e.Message}", Bad); return; }
        if (!await OpenRootAsync(root)) return;
        bool adopt = _rbAdopt.Visible && _rbAdopt.Checked && _adoptCandidate is not null;
        if (!adopt && _newest is null) { SetLine("GitHub cannot be reached, so nothing can be downloaded right now. Try again later.", Warn); return; }
        BeginBusy(adopt ? UpdatePhase.Extracting : UpdatePhase.Downloading);
        _cts = new CancellationTokenSource();
        var progress = new Progress<UpdateStatus>(OnProgress);
        try
        {
            string tag = adopt ? await _updater!.AdoptAsync(_adoptCandidate!, progress, _cts.Token) : await _updater!.InstallAsync(_newest!, progress, _cts.Token);
            EndBusy();
            FinishFirstRun(tag);
        }
        catch (OperationCanceledException) { EndBusy(); SetLine("Cancelled. Press Install to continue; what was downloaded is kept.", Warn); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            EndBusy();
            _log?.Write("first install failed: " + e.Message);
            SetLine(e.Message, e is VerificationException ? Bad : Warn);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            if (_closingAfterCancel) BeginInvoke(Close);
        }
    }

    /// <summary>The launcher moves in: a copy in the install folder (the build's own when it is newer), the remembered folder, shortcuts if wanted.</summary>
    void FinishFirstRun(string tag)
    {
        var layout = _layout!;
        string home = layout.LauncherExe;
        string message = $"Installed {tag}. Press PLAY.";
        try
        {
            if (!string.Equals(Path.GetFullPath(LauncherInfo.ExePath ?? ""), home, StringComparison.OrdinalIgnoreCase))
            {
                // The build's own launcher when it is verified and newer than this one, else this one; never over a newer launcher already there.
                string source = LauncherVersions.NewerBundled(_updater!.CurrentDir(), LauncherInfo.Version) ?? LauncherInfo.ExePath!;
                if (SelfUpdate.Install(source, home) == SelfUpdate.Outcome.NotNewer) _log?.Write($"first run: kept the launcher in {home} (as new or newer)");
                if (!_args.Has("root")) RootFinder.Remember(layout.Root);
                if (_cbShortcuts.Checked && _cbShortcuts.Enabled) { Shortcuts.Create(home); message += " From now on start \"SM Reimpl Launcher\" from the desktop or the Start menu."; }
                else message += $" From now on start the launcher in {layout.Root}.";
            }
        }
        catch (Exception e) { _log?.Write("first run: " + e.Message); message += $" (The launcher could not be copied to {home}: {e.Message})"; }
        _firstRun = false;
        ShowMain();
        _folder.Text = "Install folder: " + layout.Root;
        ShowUpdateLine();
        SetLine(message, Good);
    }

    // ---- launcher self-update ------------------------------------------------------------------------------------------------

    void OfferLauncherUpdate(bool ask)
    {
        if (_layout is null || _updater is null) return;
        // Compared with the launcher in the install folder (not only this one), and only a copy that matches the build's file list.
        string? newer = SelfUpdate.NewerBundled(_updater.CurrentDir(), _layout.LauncherExe);
        _newerLauncher = newer;
        UpdateLinks();
        if (newer is null || (ask && _args.Has("screenshot"))) return;     // no modal question in a screenshot run
        var v = SelfUpdate.FileVersionOf(newer);
        if (ask && DarkBox.Show(this, $"This build comes with a newer launcher ({v?.ToString(3)}). Update the launcher now? It restarts by itself.", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            var outcome = SelfUpdate.Install(newer, _layout.LauncherExe);
            _log?.Write($"launcher: {outcome} {v} for {_layout.LauncherExe}");
            if (outcome == SelfUpdate.Outcome.NeedsHelper)
            {
                // This launcher is the one to replace. The verified new one does it as a helper once this process has exited: it keeps this one as
                // .old until the new window has finished its first update check, and puts it back (and starts it) if the new one fails.
                var forward = new List<string>();                          // test aids reach the new launcher too (off-screen, no network)
                if (_args.Has("offline")) forward.Add("--offline");
                if (_args.Has("no-shortcuts")) forward.Add("--no-shortcuts");
                if (_args["screenshot"] is { } png) { forward.Add("--screenshot"); forward.Add(Path.ChangeExtension(png, ".after-update.png")); }
                SelfUpdate.StartHelper(newer, _layout.LauncherExe, _layout.Root, window: true, forwardRoot: _args.Has("root"), forward);
                _log?.Write("launcher: handed over to the new launcher; closing");
                _lock?.Dispose();
                _lock = null;
                _handedOver = true;
                Close();
                return;
            }
            if (outcome == SelfUpdate.Outcome.Installed) SetLine($"The launcher in {_layout.Root} was updated to {v?.ToString(3)}.", Good);
        }
        catch (Exception e)
        {
            _log?.Write($"launcher: the update failed: {e.GetType().Name}: {e.Message}");
            SetLine("The launcher could not be updated: " + e.Message, Warn);
        }
        _newerLauncher = SelfUpdate.NewerBundled(_updater.CurrentDir(), _layout.LauncherExe);
        UpdateLinks();
    }

    // ---- play ----------------------------------------------------------------------------------------------------------------

    void Start(Action<string> how, string message)
    {
        string? dir = _updater?.CurrentDir();
        if (dir is null) return;
        try { how(dir); SetLine(message, Muted); }
        catch (Exception e) { SetLine(e.Message, Bad); }
    }

    bool _loadingSafeMode;

    /// <summary>Shows the saved choice of the "Old GPU / safe mode" box (without saving it again).</summary>
    void LoadSafeMode()
    {
        if (_layout is null) return;
        _loadingSafeMode = true;
        try { _cbSafeMode.Checked = Store.ReadSettings(_layout).SafeMode; } finally { _loadingSafeMode = false; }
    }

    void SaveSafeMode()
    {
        if (_loadingSafeMode || _layout is null) return;
        try { var s = Store.ReadSettings(_layout); s.SafeMode = _cbSafeMode.Checked; Store.WriteSettings(_layout, s); }
        catch (Exception e) { _log?.Write("safe mode: could not save the setting: " + e.Message); }
    }

    /// <summary>PLAY: starts the game (in safe mode when the box is ticked) and, for a game that fails at once, turns safe mode on and tries once more (SafeModePolicy).</summary>
    void PlayGame()
    {
        string? dir = _updater?.CurrentDir();
        if (dir is null) return;
        bool safe = _cbSafeMode.Checked;
        try
        {
            var launched = DateTime.UtcNow;
            GameStart.Play(dir, safe);
            SetLine(safe ? "Starting the game in Old GPU / safe mode..." : "Starting the game...", Muted);
            _ = WatchPlayAsync(dir, safe, retried: false, launched);
        }
        catch (Exception e) { SetLine(e.Message, Bad); }
    }

    async Task WatchPlayAsync(string dir, bool safe, bool retried, DateTime launched)
    {
        try
        {
            var result = await GameStart.WatchEarlyExitAsync(dir, launched, CancellationToken.None);
            if (result is not { } r || IsDisposed) return;
            _log?.Write($"game exited with code {r.ExitCode} after {r.RanFor.TotalSeconds:0.0} s (safe mode {(safe ? "on" : "off")})");
            if (r.ExitCode == 0) return;
            if (SafeModePolicy.ShouldRetryInSafeMode(safe, retried, r.ExitCode, r.RanFor))
            {
                _cbSafeMode.Checked = true;      // saved by CheckedChanged
                SetLine(SafeModePolicy.AutoOnMessage, Warn);
                var again = DateTime.UtcNow;
                GameStart.Play(dir, safeMode: true);
                _ = WatchPlayAsync(dir, true, retried: true, again);
            }
            else if (safe || retried) SetLine(SafeModePolicy.StillFailsMessage, Bad);
        }
        catch (Exception e) { _log?.Write("watching the game: " + e.Message); }
    }

    void Join()
    {
        string? dir = _updater?.CurrentDir();
        if (dir is null || _layout is null) return;
        var settings = Store.ReadSettings(_layout);
        using var dialog = new JoinDialog(settings.LastServer ?? "127.0.0.1:27015");
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Address is not { } address) return;
        try
        {
            settings.LastServer = address;
            Store.WriteSettings(_layout, settings);
            GameStart.Join(dir, address, _cbSafeMode.Checked);
            SetLine($"Joining {address}...", Muted);
        }
        catch (Exception e) { SetLine(e.Message, Bad); }
    }

    // ---- check files, rollback, caches ----------------------------------------------------------------------------------------

    async Task CheckFilesAsync()
    {
        if (_updater is null || _busy) return;
        var p = _updater.ReadPointer();
        if (p.Current is not { } tag) return;
        BeginBusy(UpdatePhase.Verifying);
        _btnCancel.Visible = false;
        var progress = new Progress<(int Done, int Total)>(x => OnProgress(new UpdateStatus(UpdatePhase.Verifying, $"Checking the files of {tag} ({x.Done} of {x.Total})", (double)x.Done / Math.Max(1, x.Total))));
        var updater = _updater;
        ManifestReport report;
        try { report = await Task.Run(() => updater.CheckFiles(tag, progress)); }
        finally { EndBusy(); }
        _log?.Write($"check files {tag}: {report.Describe()}");
        if (report.Ok) { DarkBox.Show(this, $"All {report.Checked} files of {tag} are intact.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        string problem = $"{tag} is damaged: {report.Describe()}.";
        if (_newest?.Tag == tag)
        {
            if (DarkBox.Show(this, problem + "\n\nDownload this build again? Your profile and settings are kept.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                await RepairAsync();
        }
        else if (_newest is not null)
            DarkBox.Show(this, problem + "\n\nUpdate to the newest build to replace it" + (p.Previous is not null ? ", or roll back to " + p.Previous : "") + ".", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
            DarkBox.Show(this, problem + "\n\nConnect to the internet and use Check files again to download it anew" + (p.Previous is not null ? ", or roll back to " + p.Previous : "") + ".", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    async Task RepairAsync()
    {
        if (_updater is null || _newest is null) return;
        BeginBusy(UpdatePhase.Downloading);
        _cts = new CancellationTokenSource();
        try
        {
            string tag = await _updater.InstallAsync(_newest, new Progress<UpdateStatus>(OnProgress), _cts.Token, repair: true);
            EndBusy();
            RefreshInstalled();
            SetLine($"{tag} was installed again.", Good);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            EndBusy();
            SetLine(e is OperationCanceledException ? "The repair is paused." : e.Message, Warn);
        }
        finally { _cts?.Dispose(); _cts = null; }
    }

    void Rollback()
    {
        if (_updater is null || _busy) return;
        var p = _updater.ReadPointer();
        if (p.Previous is null) return;
        if (DarkBox.Show(this, $"Go back to {p.Previous}? {p.Current} stays installed, so you can switch back with Update.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            string tag = _updater.Rollback();
            RefreshInstalled();
            ShowUpdateLine();
            SetLine($"Rolled back to {tag}. Everyone in a match must use the same build.", Good);
        }
        catch (Exception e) { SetLine(e.Message, Bad); }
    }

    async Task ClearCachesAsync()
    {
        var (target, refusal) = CacheRepair.Target(Environment.GetEnvironmentVariable(CacheRepair.DataDirEnvVar),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (target is null) { DarkBox.Show(this, refusal, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        string cache = target;
        if (!Directory.Exists(cache)) { DarkBox.Show(this, "There are no caches to clear.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (_layout is not null && GameStart.AnythingRunning(_layout.Root)) { DarkBox.Show(this, "Close the game and any server first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (DarkBox.Show(this, $"Clear the caches the game built from your Space Marine install?\n\n{cache}\n\nThe game rebuilds them on the next start (the first join is slow again). " +
                "Your profile, custom classes and settings (%APPDATA%\\sm-reimpl) are not touched, nor is the Space Marine install.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            await Task.Run(() => Updater.DeleteDir(cache));
            _log?.Write("repair: cleared " + cache);
            SetLine("The caches were cleared.", Good);
        }
        catch (Exception e) { SetLine("Some cache files could not be deleted (in use?): " + e.Message, Warn); }
    }

    // ---- closing, screenshots ------------------------------------------------------------------------------------------------

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy && _cts is not null && !_closingAfterCancel)
        {
            if (_args.Has("screenshot") || DarkBox.Show(this, "Stop the update? It continues where it stopped the next time.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _closingAfterCancel = true;
                _cts.Cancel();
            }
            e.Cancel = true;        // closes once the update has stopped
            return;
        }
        if (_busy && !_closingAfterCancel && _busyPhase is UpdatePhase.Switching) { e.Cancel = true; return; }
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>The window as it is drawn now (the layout check and --screenshot use it).</summary>
    internal Bitmap Snapshot()
    {
        var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
        return bmp;
    }

    void CaptureTo(string png)
    {
        try
        {
            using var bmp = Snapshot();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(png))!);
            bmp.Save(png, ImageFormat.Png);
            if (_args.Has("ui-check") && LayoutCheck.Problems(this) is { Count: > 0 } bad) { foreach (var line in bad) _log?.Write("layout: " + line); File.WriteAllLines(Path.ChangeExtension(png, ".layout.txt"), bad); Environment.ExitCode = 2; }
        }
        catch (Exception e) { _log?.Write("screenshot: " + e.Message); }
        Close();
    }

    // ---- test aids: simulated display scale and fixed states (--ui-scale, --ui-state; used by --layout-check) ------------------------

    /// <summary>Simulates a display at <paramref name="f"/> times 96 dpi: every font and every pixel size is scaled, as Windows does at 125 / 150 / 200 %.</summary>
    void SimulateScale(double f)
    {
        Theme.SimScale = f;
        var fonts = new List<(Control, Font)>();
        void Collect(Control c) { fonts.Add((c, c.Font)); foreach (Control k in c.Controls) Collect(k); }
        Collect(this);
        foreach (var (c, font) in fonts) c.Font = new Font(font.FontFamily, font.Size * (float)f, font.Style);
        Scale(new SizeF((float)f, (float)f));
    }

    internal static readonly string[] DemoStates = ["idle", "checking", "offered", "downloading", "error", "setup"];

    /// <summary>Puts the window into one of its states with realistic (long) texts and no install folder behind it: idle, checking, offered (an update line
    /// as long as it gets), downloading, error (with a notice and the launcher-update link) and setup.</summary>
    internal void ApplyDemoState(string state)
    {
        const string tag = "tester-2026-10-30-3abb29b";
        string notes = string.Join(Environment.NewLine, Enumerable.Range(1, 24).Select(i => $"- Playtest change {i}: the sound hunt, a very long line about melee interactions and the charge that has to wrap inside the box instead of running past its edge ({i})."));
        _firstRun = state == "setup";
        _main.Visible = !_firstRun;
        _setup.Visible = _firstRun;
        _folder.Text = _firstRun ? "" : @"Install folder: D:\Games\A Rather Long Library Folder Name\SM Reimpl\a\very\deep\folder\structure\for\the\playtest";
        _installed.Text = $"Installed: {tag}";
        _installedSub.Text = "published 30 Oct 2026 · installed 31 Oct 2026 09:41 · previous: tester-2026-10-30-7cd91e2";
        _notesTitle.Text = $"What's new in {tag}";
        _notes.Text = Lines(notes);
        _notice.Visible = false;
        _btnUpdate.Visible = false;
        _progressPanel.Visible = false;
        _lnkSelfUpdate.Visible = false;
        foreach (var b in new[] { _btnPlay, _btnHost, _btnJoin, _btnLan }) b.Enabled = true;
        foreach (var l in new[] { _lnkCheck, _lnkRollback, _lnkRelease, _lnkIssues, _lnkFolder, _lnkCaches }) l.Enabled = true;
        _cbSafeMode.Checked = true;
        switch (state)
        {
            case "idle": SetLine("You have the newest build (published 30 Oct 2026).", Good); break;
            case "checking": SetLine("Looking for updates...", Muted); break;
            case "offered":
                SetLine("The current playtest build is tester-2026-10-29-aaaaaaa, published 29 Oct 2026 (1,020.4 MB download); yours is newer. Everyone in a match needs the same build.", Theme.Accent);
                _btnUpdate.Text = "Switch to it";
                _btnUpdate.Visible = true;
                break;
            case "downloading":
                SetLine("A new build is available: tester-2026-10-31-bbbbbbb, published 31 Oct 2026 (1,020.4 MB download).", Theme.Accent);
                _progressPanel.Visible = true;
                _progress.Value = 430;
                _progressText.Text = "Downloading: 438.8 MB of 1,020.4 MB (43 %) · 18.4 MB/s · about 32 s left · retrying after a short pause (the connection was reset)";
                foreach (var b in new[] { _btnPlay, _btnHost, _btnJoin, _btnLan }) b.Enabled = false;
                break;
            case "error":
                SetLine("Offline: the installed build can be played. The update check failed: The remote name could not be resolved: 'api.github.com'. An unfinished update is kept and continues when GitHub is reachable.", Warn);
                _notice.Text = "A newer release could not be offered: it has two playtest zips." + Environment.NewLine + "The new launcher did not start, so the previous one was kept. Please report it (Report an issue).";
                _notice.Visible = true;
                _btnUpdate.Text = "Try again";
                _btnUpdate.Visible = true;
                _lnkSelfUpdate.Visible = true;
                break;
            case "setup":
                _setupFolder.Text = @"D:\Games\A Rather Long Library Folder Name\SM Reimpl";
                _rbAdopt.Text = $"Use the build in this folder: {tag} (copied, no download)";
                _rbAdopt.Visible = true;
                _rbAdopt.Checked = true;
                _rbDownload.Text = "Download the newest build: tester-2026-10-31-bbbbbbb (1,020.4 MB)";
                SetLine("The update check failed: The remote name could not be resolved: 'api.github.com'. Choose the build in this folder or try again later.", Warn);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _lock?.Dispose(); _http.Dispose(); _cts?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Asks for a server address (host or host:port; the default port is 27015).</summary>
sealed class JoinDialog : Form
{
    readonly TextBox _box = new() { Width = 380 };
    readonly DarkLabel _error = new() { AutoSize = true, ForeColor = Theme.BadText, MaximumSize = new Size(420, 0) };
    public string? Address { get; private set; }

    public JoinDialog(string initial)
    {
        Text = "Join a server";
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        _box.Text = initial;
        var ok = new Button { Text = "Join", DialogResult = DialogResult.None, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) =>
        {
            Address = ServerAddress.Normalise(_box.Text);
            if (Address is null) { _error.Text = "Type an address such as 192.168.1.20 or 203.0.113.5:27015."; return; }
            DialogResult = DialogResult.OK;
        };
        AcceptButton = ok;
        CancelButton = cancel;
        var layout = new TileFlow { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        var buttons = new TileFlow { AutoSize = true };
        buttons.Controls.AddRange([ok, cancel]);
        layout.Controls.AddRange([new DarkLabel { AutoSize = true, MaximumSize = new Size(420, 0), Text = "Server address (host or host:port; 127.0.0.1 = this PC):" }, _box, _error, buttons]);
        Controls.Add(layout);
        Theme.Apply(this);
    }
}
