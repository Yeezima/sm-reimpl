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
    static readonly Color Accent = Color.FromArgb(11, 92, 173);
    static readonly Color PlayGreen = Color.FromArgb(24, 120, 56);
    static readonly Color Good = Color.FromArgb(24, 120, 56);
    static readonly Color Warn = Color.FromArgb(166, 77, 0);
    static readonly Color Bad = Color.FromArgb(180, 30, 30);
    static readonly Color Muted = Color.FromArgb(96, 96, 96);

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
    readonly Panel _main = new() { Dock = DockStyle.Fill };
    readonly Panel _setup = new() { Dock = DockStyle.Fill, Visible = false };
    readonly Label _installed = new() { AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold) };
    readonly Label _installedSub = new() { AutoSize = true, ForeColor = Muted };
    readonly Label _notice = new() { AutoSize = true, ForeColor = Warn, Visible = false, MaximumSize = new Size(700, 0) };
    readonly Label _update = new() { AutoSize = true, MaximumSize = new Size(560, 0), Font = new Font("Segoe UI", 10f) };
    readonly Button _btnUpdate = new() { Text = "Update", AutoSize = true, Visible = false };
    readonly Label _notesTitle = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Text = "What's new" };
    readonly TextBox _notes = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle };
    readonly TableLayoutPanel _progressPanel = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Visible = false, Padding = new Padding(16, 4, 16, 4) };
    readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 22 };
    readonly Label _progressText = new() { AutoSize = true };
    readonly Button _btnCancel = new() { Text = "Cancel", AutoSize = true };
    readonly Button _btnPlay = new() { Text = "PLAY", AutoSize = false, Size = new Size(150, 44), Font = new Font("Segoe UI", 12f, FontStyle.Bold) };
    readonly Button _btnHost = new() { Text = "Host a match on this PC", AutoSize = true, Height = 44 };
    readonly Button _btnJoin = new() { Text = "Join a server...", AutoSize = true, Height = 44 };
    readonly Button _btnLan = new() { Text = "Host for friends (LAN)", AutoSize = true, Height = 44 };
    readonly LinkLabel _lnkCheck = Link("Check files");
    readonly LinkLabel _lnkRollback = Link("Roll back to the previous version");
    readonly LinkLabel _lnkRelease = Link("Open the release page");
    readonly LinkLabel _lnkIssues = Link("Report an issue");
    readonly LinkLabel _lnkFolder = Link("Open the install folder");
    readonly LinkLabel _lnkCaches = Link("Repair: clear caches");
    readonly LinkLabel _lnkSelfUpdate = Link("A newer launcher came with this build: update the launcher");
    readonly Label _folder = new() { AutoSize = true, ForeColor = Muted, Padding = new Padding(16, 0, 16, 8) };
    // set-up (first run)
    readonly TextBox _setupFolder = new() { Width = 600 };
    readonly Button _btnBrowse = new() { Text = "Browse...", AutoSize = true };
    readonly RadioButton _rbAdopt = new() { AutoSize = true };
    readonly RadioButton _rbDownload = new() { AutoSize = true, Text = "Download the newest build (looking it up...)" };
    readonly CheckBox _cbShortcuts = new() { AutoSize = true, Checked = true, Text = "Create shortcuts on the desktop and in the Start menu" };
    readonly Button _btnInstall = new() { Text = "Install", AutoSize = false, Size = new Size(150, 40), Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
    readonly Label _setupStatus = new() { AutoSize = true, MaximumSize = new Size(680, 0) };

    public MainForm(Args args)
    {
        _args = args;
        _source = new ReleaseSource(_http);
        _adoptCandidate = RootFinder.AdoptCandidate();
        _adoptVersion = _adoptCandidate is null ? null : PackageLayout.ReadVersion(_adoptCandidate);
        Text = "SM Reimpl Launcher";
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(780, 600);
        MinimumSize = new Size(640, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = SystemColors.Window;
        try { Icon = Icon.ExtractAssociatedIcon(LauncherInfo.ExePath ?? ""); } catch (Exception) { }
        if (_args.Has("screenshot"))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);       // off-screen, never on the owner's desktop
            ShowInTaskbar = false;
        }
        BuildLayout();
        WireEvents();
    }

    protected override bool ShowWithoutActivation => _args.Has("screenshot");

    static LinkLabel Link(string text) => new() { Text = text, AutoSize = true, LinkColor = Accent, ActiveLinkColor = Accent, Margin = new Padding(0, 2, 18, 2) };

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
        var header = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Color.FromArgb(31, 35, 40) };
        var title = new Label { Text = "SM REIMPL", ForeColor = Color.White, Font = new Font("Segoe UI", 18f, FontStyle.Bold), AutoSize = true, Location = new Point(16, 8) };
        var subtitle = new Label { Text = "Space Marine (2011) multiplayer playtest launcher", ForeColor = Color.FromArgb(200, 205, 210), AutoSize = true, Location = new Point(19, 46) };
        var version = new Label { Text = "launcher " + LauncherInfo.VersionText, ForeColor = Color.FromArgb(150, 155, 160), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        header.Controls.AddRange([title, subtitle, version]);
        header.Resize += (_, _) => version.Location = new Point(header.ClientSize.Width - version.Width - 14, 50);

        // the main view
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16, 12, 16, 4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var updateRow = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 6) };
        updateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        updateRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        updateRow.Controls.Add(_update, 0, 0);
        updateRow.Controls.Add(_btnUpdate, 1, 0);
        _update.Anchor = AnchorStyles.Left;
        Accentuate(_btnUpdate, Accent);
        _btnUpdate.Padding = new Padding(14, 4, 14, 4);
        _btnUpdate.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 10, 0, 0) };
        Accentuate(_btnPlay, PlayGreen);
        foreach (var b in new[] { _btnHost, _btnJoin, _btnLan }) { b.AutoSize = false; b.Size = new Size(b.PreferredSize.Width + 20, 44); }
        buttons.Controls.AddRange([_btnPlay, _btnHost, _btnJoin, _btnLan]);
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(_installed, 0, 0);
        grid.Controls.Add(_installedSub, 0, 1);
        grid.Controls.Add(_notice, 0, 2);
        grid.Controls.Add(updateRow, 0, 3);
        grid.Controls.Add(_notesTitle, 0, 4);
        grid.Controls.Add(_notes, 0, 5);
        grid.Controls.Add(buttons, 0, 6);
        _main.Controls.Add(grid);

        // the set-up panel (first run)
        var s = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16, 12, 16, 4) };
        var welcome = new Label { AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold), Text = "Welcome! Let's install the playtest build." };
        var explain = new Label
        {
            AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Muted,
            Text = "The launcher keeps the build up to date and starts it. It needs no administrator rights. Your profile and settings stay in " +
                   "%APPDATA%\\sm-reimpl and are never changed by an update. You need your own Space Marine (2011) installed through Steam.",
        };
        var folderTitle = new Label { AutoSize = true, Text = "Install folder:", Margin = new Padding(3, 14, 3, 2) };
        var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        folderRow.Controls.AddRange([_setupFolder, _btnBrowse]);
        var choiceTitle = new Label { AutoSize = true, Text = "What to install:", Margin = new Padding(3, 14, 3, 2) };
        Accentuate(_btnInstall, PlayGreen);
        foreach (var c in new Control[] { welcome, explain, folderTitle, folderRow, choiceTitle, _rbAdopt, _rbDownload, _cbShortcuts, _btnInstall, _setupStatus })
        {
            s.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            s.Controls.Add(c);
        }
        _cbShortcuts.Margin = new Padding(3, 12, 3, 8);
        _setup.Controls.Add(s);

        // progress, links, folder
        _progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _progressPanel.Controls.Add(_progress, 0, 0);
        _progressPanel.Controls.Add(_btnCancel, 1, 0);
        _progressPanel.Controls.Add(_progressText, 0, 1);
        _progressPanel.SetColumnSpan(_progressText, 2);
        var links = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(16, 6, 16, 4), WrapContents = true };
        links.Controls.AddRange([_lnkCheck, _lnkRollback, _lnkRelease, _lnkIssues, _lnkFolder, _lnkCaches, _lnkSelfUpdate]);
        _lnkSelfUpdate.Visible = false;
        _folder.Dock = DockStyle.Bottom;

        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(_main);
        content.Controls.Add(_setup);
        Controls.Add(content);
        Controls.Add(_progressPanel);
        Controls.Add(links);
        Controls.Add(_folder);
        Controls.Add(header);
        // docking (last added docks first): the header at the top; from the bottom edge up the folder line, the links, the progress; the content fills the rest
        _progressPanel.Dock = DockStyle.Bottom;
    }

    void WireEvents()
    {
        Shown += async (_, _) => await StartAsync();
        FormClosing += OnClosing;
        _btnUpdate.Click += async (_, _) => await UpdateAsync();
        _btnCancel.Click += (_, _) => { _cts?.Cancel(); _btnCancel.Enabled = false; };
        _btnPlay.Click += (_, _) => Start(GameStart.Play, "Starting the game...");
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
        _log = new AppLog(_layout);
        var layout = _layout;
        _lock = await Task.Run(() => RootLock.TryAcquire(layout, TimeSpan.FromSeconds(10)));
        if (_lock is null)
        {
            MessageBox.Show(this, $"Another SM Reimpl Launcher is already open for {layout.Root}.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            : $"A new build is available: {_newest.Tag}, published {_newest.PublishedAt.LocalDateTime:d MMM yyyy} ({size}).", Accent);
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
            _progressText.ForeColor = d.Note is null ? SystemColors.ControlText : Warn;
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
        _progressText.ForeColor = SystemColors.ControlText;
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
        if (ask && MessageBox.Show(this, $"This build comes with a newer launcher ({v?.ToString(3)}). Update the launcher now? It restarts by itself.", Text,
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
            GameStart.Join(dir, address);
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
        if (report.Ok) { MessageBox.Show(this, $"All {report.Checked} files of {tag} are intact.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        string problem = $"{tag} is damaged: {report.Describe()}.";
        if (_newest?.Tag == tag)
        {
            if (MessageBox.Show(this, problem + "\n\nDownload this build again? Your profile and settings are kept.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                await RepairAsync();
        }
        else if (_newest is not null)
            MessageBox.Show(this, problem + "\n\nUpdate to the newest build to replace it" + (p.Previous is not null ? ", or roll back to " + p.Previous : "") + ".", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
            MessageBox.Show(this, problem + "\n\nConnect to the internet and use Check files again to download it anew" + (p.Previous is not null ? ", or roll back to " + p.Previous : "") + ".", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        if (MessageBox.Show(this, $"Go back to {p.Previous}? {p.Current} stays installed, so you can switch back with Update.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
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
        if (target is null) { MessageBox.Show(this, refusal, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        string cache = target;
        if (!Directory.Exists(cache)) { MessageBox.Show(this, "There are no caches to clear.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (_layout is not null && GameStart.AnythingRunning(_layout.Root)) { MessageBox.Show(this, "Close the game and any server first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (MessageBox.Show(this, $"Clear the caches the game built from your Space Marine install?\n\n{cache}\n\nThe game rebuilds them on the next start (the first join is slow again). " +
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
            if (_args.Has("screenshot") || MessageBox.Show(this, "Stop the update? It continues where it stopped the next time.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
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

    void CaptureTo(string png)
    {
        try
        {
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(png))!);
            bmp.Save(png, ImageFormat.Png);
        }
        catch (Exception e) { _log?.Write("screenshot: " + e.Message); }
        Close();
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
    readonly TextBox _box = new() { Width = 300 };
    readonly Label _error = new() { AutoSize = true, ForeColor = Color.FromArgb(180, 30, 30) };
    public string? Address { get; private set; }

    public JoinDialog(string initial)
    {
        Text = "Join a server";
        Font = new Font("Segoe UI", 9.5f);
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
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.AddRange([ok, cancel]);
        layout.Controls.AddRange([new Label { AutoSize = true, Text = "Server address (host or host:port; 127.0.0.1 = this PC):" }, _box, _error, buttons]);
        Controls.Add(layout);
    }
}
