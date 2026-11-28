using SmLauncher.Core;

namespace SmLauncher;

/// <summary>
/// PLAY TEST MAP's dialog (<see cref="MapPlaytest"/>): the saved map file (Browse... opens the Mapmaker's maps folder), the mode, the bots a side (0 to 6) and the
/// player's side, then Start or Cancel. A missing file is refused with a plain line under the choices. Laid out like the main window: tables that size to their
/// content, pixel sizes at 96 dpi scaled once by Windows Forms (a simulated scale for the layout check, as MainForm's --ui-scale).
/// </summary>
sealed class PlaytestDialog : Form
{
    readonly TextBox _map = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 3, 8, 3) };
    const int ChoiceWidth = 470, TextWidth = 600;      // px at 96 dpi: the choices' column, the wrap width of the explanation and the refusal line
    readonly Button _browse = new() { Text = "Browse...", AutoSize = false, Size = new Size(96, 28), Anchor = AnchorStyles.Right };
    readonly DarkRadioButton _annihilation = new() { AutoSize = true, Text = "Annihilation" };
    readonly DarkRadioButton _seize = new() { AutoSize = true, Text = "Seize Ground" };
    readonly NumericUpDown _bots = new() { Minimum = 0, Maximum = MapPlaytest.MaxBots, Width = 56, TextAlign = HorizontalAlignment.Center, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(3, 3, 10, 3) };
    readonly DarkLabel _botsNote = new() { AutoSize = true, ForeColor = Theme.Muted, Text = "each bot is a background copy of the game: fewer bots use less memory" };
    readonly DarkRadioButton _marines = new() { AutoSize = true, Text = "Space Marines" };
    readonly DarkRadioButton _chaos = new() { AutoSize = true, Text = "Chaos" };
    readonly DarkLabel _problem = new() { AutoSize = true, ForeColor = Theme.BadText, Margin = new Padding(3, 10, 3, 0) };
    readonly Button _start = new() { Text = "Start", AutoSize = false, Size = new Size(96, 32), Margin = new Padding(8, 0, 0, 0) };
    readonly Button _cancel = new() { Text = "Cancel", AutoSize = false, Size = new Size(96, 32), DialogResult = DialogResult.Cancel, Margin = new Padding(8, 0, 0, 0) };
    readonly ToolTip _tips = new() { InitialDelay = 300, AutoPopDelay = 15000 };
    readonly string _mapsDir;

    /// <summary>What Start accepted (null after Cancel).</summary>
    public MapPlaytestChoice? Choice { get; private set; }

    /// <summary>Test aid: shown off-screen without taking the foreground (the layout check).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Quiet { get; init; }
    protected override bool ShowWithoutActivation => Quiet;

    /// <param name="uiScale">Test aid (the layout check): the display scale to show (1.25 = 125 %) whatever this PC's own is, simulated as MainForm's --ui-scale does. Null: this display's own.</param>
    public PlaytestDialog(MapPlaytestChoice initial, string mapsDir, double? uiScale = null)
    {
        _mapsDir = mapsDir;
        double simScale = 1.0;
        if (uiScale is { } us)
        {
            simScale = us * 96.0 / DeviceDpi;
            if (Math.Abs(simScale - 1.0) < 0.001) simScale = 1.0;
            Theme.SimScale = simScale;
        }
        SuspendLayout();
        Text = "Play test a map";
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleDimensions = new SizeF((float)(96 / simScale), (float)(96 / simScale));
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16, 12, 16, 14);

        // one column: the explanation, the choices (labels left, choices right), the refusal line, Start / Cancel. The explanation and the refusal wrap at a
        // fixed width (scaled with the display) instead of spanning the choices' columns, which would widen the labels' column.
        var outer = new TileTable { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        var intro = new DarkLabel
        {
            AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(3, 0, 3, 10), MaximumSize = new Size(TextWidth, 0),
            Text = "Plays a saved map with bots on this PC only, without the Mapmaker open. Close the game window to stop the server and the bots.",
        };
        var grid = new TileTable { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ChoiceWidth));

        var mapRow = new TileTable { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0) };
        mapRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mapRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mapRow.Controls.Add(_map, 0, 0);
        mapRow.Controls.Add(_browse, 1, 0);
        AddRow(grid, 1, "Map file:", mapRow);

        var modeRow = Flow(_annihilation, _seize);
        AddRow(grid, 2, "Mode:", modeRow);

        var botsRow = new TileTable { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0) };
        botsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        botsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        botsRow.Controls.Add(_bots, 0, 0);
        botsRow.Controls.Add(_botsNote, 1, 0);
        _botsNote.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        AddRow(grid, 3, "Bots per side:", botsRow);

        var sideRow = Flow(_marines, _chaos);
        AddRow(grid, 4, "Your side:", sideRow);

        _problem.MaximumSize = new Size(TextWidth, 0);
        _problem.Visible = false;

        var buttons = new TileFlow { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, WrapContents = false, Margin = new Padding(0, 14, 0, 0) };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_start);
        _start.TabIndex = 0; _cancel.TabIndex = 1;        // drawn right to left (Cancel on the edge), tabbed Start first
        foreach (var c in new Control[] { intro, grid, _problem, buttons })
        {
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.Controls.Add(c);
        }
        Controls.Add(outer);

        _map.BackColor = Theme.Input;
        _bots.BackColor = Theme.Input;
        _bots.ForeColor = Theme.Text;
        Accentuate(_start);
        AcceptButton = _start;
        CancelButton = _cancel;

        string tipBots = "Each bot is a background copy of the game (no window). Fewer bots use less memory; 0 plays the map alone.";
        _tips.SetToolTip(_bots, tipBots);
        _tips.SetToolTip(_botsNote, tipBots);
        _tips.SetToolTip(_browse, "Opens your Mapmaker maps folder. Only a saved .smmap file is played: save the map in the Mapmaker first.");
        _tips.SetToolTip(_map, "The saved map file (.smmap) to play.");

        _map.Text = initial.MapFile;
        (initial.Mode == "seize" ? _seize : _annihilation).Checked = true;
        _bots.Value = Math.Clamp(initial.Bots, 0, MapPlaytest.MaxBots);
        (initial.Side == "chaos" ? _chaos : _marines).Checked = true;

        Theme.Apply(this);
        _browse.Click += (_, _) => Browse();
        _start.Click += (_, _) => TryStart();
        _map.TextChanged += (_, _) => _problem.Visible = false;
        if (simScale != 1.0)
        {
            var fonts = new List<(Control, Font)>();
            void Collect(Control c) { fonts.Add((c, c.Font)); foreach (Control k in c.Controls) Collect(k); }
            Collect(this);
            foreach (var (c, font) in fonts) c.Font = new Font(font.FontFamily, font.Size * (float)simScale, font.Style);
        }
        ResumeLayout(false);
        PerformLayout();
    }

    static TileFlow Flow(params Control[] items)
    {
        var f = new TileFlow { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
        foreach (var c in items) { c.Margin = new Padding(3, 5, 18, 3); f.Controls.Add(c); }
        return f;
    }

    static void AddRow(TileTable grid, int row, string label, Control content)
    {
        var l = new DarkLabel { AutoSize = true, Text = label, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 6) };
        grid.Controls.Add(l, 0, row);
        grid.Controls.Add(content, 1, row);
    }

    static void Accentuate(Button b)
    {
        b.BackColor = Theme.Green;
        b.ForeColor = Color.White;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.UseVisualStyleBackColor = false;
    }

    MapPlaytestChoice Current() => new(_map.Text.Trim().Trim('"'), _seize.Checked ? "seize" : "annihilation", (int)_bots.Value, _chaos.Checked ? "chaos" : "marines");

    void Browse()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a saved map", Filter = "Saved maps (*.smmap)|*.smmap", CheckFileExists = true, Multiselect = false, RestoreDirectory = true,
        };
        string current = _map.Text.Trim().Trim('"');
        string? folder = null;
        try { if (current.Length > 0 && Path.GetDirectoryName(Path.GetFullPath(current)) is { } d && Directory.Exists(d)) folder = d; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        folder ??= Directory.Exists(_mapsDir) ? _mapsDir : null;
        if (folder is not null) dialog.InitialDirectory = folder;
        if (current.Length > 0 && File.Exists(current)) dialog.FileName = Path.GetFileName(current);
        if (dialog.ShowDialog(this) == DialogResult.OK) _map.Text = dialog.FileName;
    }

    void TryStart()
    {
        var c = Current();
        if (MapPlaytest.Problem(c) is { } why) { ShowProblem(why); return; }
        Choice = c with { MapFile = Path.GetFullPath(c.MapFile) };
        DialogResult = DialogResult.OK;
        Close();
    }

    void ShowProblem(string why)
    {
        _problem.Text = why;
        _problem.Visible = true;
        _map.Focus();
    }

    // ---- test aids (the layout check, --screenshot) -------------------------------------------------------------------------------
    internal void ShowProblemForTest(string why) => ShowProblem(why);
    internal Size StartButtonSize() => _start.Size;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
