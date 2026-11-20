using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using SmClient.Customizer;
using SmClient.Extras.Warfront;
using SmSim.Extras.Warfront.Results;

namespace SmClient;

/// <summary>
/// The end-of-run panel of Warfront (plan section 7, slice W15): the same ranked leaderboard on victory and on defeat, one row per player with the squad's figures beside the player's own,
/// each squad's top retainer and why, the score formula, and the buttons COPY FOR DISCORD (plain text on the clipboard; nothing is posted), EXPORT (a .txt and a .md under the warfront
/// folder) and HONOR ROLL (every archived run). Full screen in the menus' style (<see cref="UiKit"/>), in the 1920 by 1080 space, one text style per level (title; cells; captions;
/// rows of buttons), live labels only. Layout and look ours.
/// </summary>
public sealed partial class WarfrontResultsPanel : Control
{
    const float Left = UiKit.MenuLeft;
    // Column left edges and widths of the table (numbers are right-aligned in them).
    const float RankX = 118f, RankW = 50f, NameX = 190f, NameW = 380f, ScoreX = 570f, ScoreW = 140f;
    static readonly (string Head, float X, float W)[] Cols =
    {
        ("K", 720f, 90f), ("A", 810f, 80f), ("D", 890f, 70f), ("REV", 960f, 80f), ("CAP", 1040f, 80f),
        ("K", 1160f, 100f), ("KO", 1260f, 80f), ("REV", 1340f, 80f), ("MEN", 1420f, 80f), ("TOTAL K", 1500f, 130f),
    };
    const int BodySize = WarfrontStyle.Body, CaptionSize = WarfrontStyle.Small, ButtonSize = WarfrontStyle.Body;
    const float ButtonWidth = 330f, ButtonPitch = 345f;
    static readonly Color Dim = WarfrontStyle.Dim, Head = WarfrontStyle.Gold;

    readonly WarfrontRunRecord _record;
    readonly string _dir;
    readonly Action? _close;
    readonly Control _page;
    readonly Label _status;
    readonly Backing _backing;

    /// <summary>The cell texts of the table, row by row (the self test holds them against the record).</summary>
    public List<List<string>> TableText { get; } = new();
    public Button CopyButton { get; }
    public Button ExportButton { get; }
    public Button HonorButton { get; }
    public Button CloseButton { get; }
    /// <summary>The fifth action, NEW RUN (the leader's; null when the panel was built without it).</summary>
    public Button? NewRunButton { get; }
    /// <summary>What the last copy put on the clipboard (null before the first).</summary>
    public string? LastCopied { get; private set; }
    public string StatusText => _status.Text;
    public bool ShowingHonorRoll { get; private set; }

    public WarfrontResultsPanel(WarfrontRunRecord record, string warfrontDir, Action? close = null, Action? newRun = null)
    {
        _record = record; _dir = warfrontDir; _close = close;
        Name = "WarfrontResultsPanel";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Name = "ResultsDim", Color = new Color(0f, 0f, 0f, 0.88f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        var head = new Label { Name = "ResultsTitle", Text = record.Result switch { "won" => "THE WARFRONT IS WON", "lost" => "THE WARFRONT IS LOST", _ => "WARFRONT RUN ENDED" },
            MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(Left - 3.5f, 52f) };
        head.AddThemeFontOverride("font", UiKit.TitleFont);
        head.AddThemeFontSizeOverride("font_size", UiKit.MenuTitleSize);
        head.AddThemeColorOverride("font_color", WarfrontStyle.Ink);
        AddChild(head);
        var t = TimeSpan.FromSeconds(record.DurationSeconds);
        Line("ResultsSummary", (record.EndedUtc.Length >= 16 ? record.EndedUtc[..16].Replace('T', ' ') : record.EndedUtc) + " UTC    RUN TIME " + ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture)
            + t.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture) + "    " + record.Battles.Count + " BATTLES    ROLLS LOST " + record.RollsLost + " OF " + record.RollsTotal, Left, 146f, CaptionSize, 1500f, Dim);

        _backing = new Backing();
        _backing.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_backing);
        _page = new Control { Name = "ResultsPage", MouseFilter = MouseFilterEnum.Ignore };
        _page.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_page);
        BuildTable();

        _status = Line("ResultsStatus", "", Left, 896f, CaptionSize, 1500f, Dim);
        UiKit.TextKind(_status, "progress");   // the result of COPY and EXPORT (or why it failed)
        // five actions of one size in one row: COPY, EXPORT, HONOR ROLL, NEW RUN (when given), CLOSE
        CopyButton = Row("Copy for Discord", Copy, 940f, Left + 0 * ButtonPitch, ButtonWidth);
        UiTooltip.Attach(CopyButton, "Copies the results as plain text. Nothing is posted: paste it into Discord yourself.");
        ExportButton = Row("Export", Export, 940f, Left + 1 * ButtonPitch, ButtonWidth);
        HonorButton = Row("Honor roll", ToggleHonorRoll, 940f, Left + 2 * ButtonPitch, ButtonWidth);
        if (newRun is not null) { NewRunButton = Row("New run", newRun, 940f, Left + 3 * ButtonPitch, ButtonWidth); NewRunButton.Name = "WarfrontNewRun"; }
        CloseButton = Row("Close", () => _close?.Invoke(), 940f, Left + 4 * ButtonPitch, ButtonWidth);
        LayoutButtons();
    }

    /// <summary>The kit's recessed table behind the leaderboard and the dark strip behind its column heads.</summary>
    sealed partial class Backing : Control
    {
        public Backing() { Name = "ResultsBacking"; MouseFilter = MouseFilterEnum.Pass; }
        public override void _Draw()
        {
            WarfrontStyle.Table(this, new Rect2(96f, 180f, 1568f, 676f));
            WarfrontStyle.Strip(this, new Rect2(96f, 184f, 1568f, 66f));
        }
    }

    /// <summary>NEW RUN is the leader's: shown or hidden as the viewer leads or not (the page follows the leader), the buttons that stand spaced evenly with no hole.</summary>
    public void ShowNewRun(bool visible)
    {
        if (NewRunButton is null || NewRunButton.Visible == visible) return;
        NewRunButton.Visible = visible;
        LayoutButtons();
    }

    void LayoutButtons()
    {
        int i = 0;
        foreach (var b in new[] { CopyButton, ExportButton, HonorButton, NewRunButton, CloseButton })
            if (b is not null && (b != NewRunButton || b.Visible)) b.Position = new Vector2(Left + i++ * ButtonPitch, b.Position.Y);
    }

    static void Place(Control c, float x, float y, float w, float h)
    {
        c.SetAnchorsPreset(LayoutPreset.TopLeft);
        c.Position = new Vector2(x, y);
        c.Size = new Vector2(w, h);
    }

    Label Line(string name, string text, float x, float y, int size, float width, Color? colour = null, HorizontalAlignment align = HorizontalAlignment.Left, Control? parent = null)
    {
        var l = UiKit.MenuText(text, colour, clip: false);
        l.Name = name;
        l.HorizontalAlignment = align;
        WarfrontStyle.Skin(l, size, colour);
        Place(l, x, y, width, size + 8f);
        (parent ?? this).AddChild(l);
        return l;
    }

    Button Row(string text, Action act, float y, float x = Left, float width = 360f)
    {
        var b = UiKit.MenuButton(text, act);
        Place(b, x, y, width, 52f);
        WarfrontStyle.Plate(b, ButtonSize, width);
        AddChild(b);
        return b;
    }

    static string N(decimal v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    void Clear()
    {
        foreach (var c in _page.GetChildren()) c.QueueFree();
        TableText.Clear();
    }

    void BuildTable()
    {
        Clear();
        ShowingHonorRoll = false;
        var rows = _record.Rows;
        const float top = 190f;
        Line("GroupPlayer", "PLAYER", 720f, top, CaptionSize, 400f, Head, parent: _page);
        Line("GroupSquad", "SQUAD", 1160f, top, CaptionSize, 340f, Head, parent: _page);
        float hy = top + 24f;
        Line("HeadRank", "#", RankX, hy, CaptionSize, RankW, Head, HorizontalAlignment.Right, _page);
        Line("HeadName", "NAME", NameX, hy, CaptionSize, NameW, Head, parent: _page);
        Line("HeadScore", "SCORE", ScoreX, hy, CaptionSize, ScoreW, Head, HorizontalAlignment.Right, _page);
        foreach (var (head, x, w) in Cols) Line("Head" + head + x, head, x, hy, CaptionSize, w, Head, HorizontalAlignment.Right, _page);
        bool retainers = rows.Count <= 8;
        float pitch = retainers ? Math.Min(66f, 560f / Math.Max(1, rows.Count)) : Math.Min(38f, 560f / Math.Max(1, rows.Count));
        float y = hy + 44f;
        for (int i = 0; i < rows.Count; i++, y += pitch)
        {
            var r = rows[i];
            var cells = new List<string>
            {
                r.Rank.ToString(CultureInfo.InvariantCulture), r.Connected ? r.Name : r.Name + " (LEFT)", N(r.Total),
                r.Player.Kills.ToString(CultureInfo.InvariantCulture), r.Player.Assists.ToString(CultureInfo.InvariantCulture), r.Player.Deaths.ToString(CultureInfo.InvariantCulture),
                r.Player.Revives.ToString(CultureInfo.InvariantCulture), r.Player.Captures.ToString(CultureInfo.InvariantCulture),
                r.Squad.Kills.ToString(CultureInfo.InvariantCulture), r.Squad.Knockouts.ToString(CultureInfo.InvariantCulture), r.Squad.Revives.ToString(CultureInfo.InvariantCulture),
                r.Squad.Members.ToString(CultureInfo.InvariantCulture), r.TotalKills.ToString(CultureInfo.InvariantCulture),
            };
            TableText.Add(cells);
            Line("Rank" + i, cells[0], RankX, y, BodySize, RankW, null, HorizontalAlignment.Right, _page);
            Line("Name" + i, cells[1], NameX, y, BodySize, NameW, null, parent: _page);
            Line("Score" + i, cells[2], ScoreX, y, BodySize, ScoreW, null, HorizontalAlignment.Right, _page);
            for (int c = 0; c < Cols.Length; c++) Line($"Cell{i}_{c}", cells[3 + c], Cols[c].X, y, BodySize, Cols[c].W, null, HorizontalAlignment.Right, _page);
            if (retainers && r.TopRetainer is { } top1)
                Line("Retainer" + i, "Top retainer  " + top1.Name + (top1.ClassName.Length > 0 ? " (" + top1.ClassName + ")" : "") + "  -  " + top1.Reason, NameX, y + 34f, CaptionSize, 1300f, Dim, parent: _page);
        }
        UiTooltip.Attach(_backing, _record.Weights.FormulaText());   // how the score is made: a hover hint on the table, not a line under it
    }

    /// <summary>COPY FOR DISCORD: the fixed-width block in a code fence on the clipboard. Nothing leaves the PC.</summary>
    public void Copy()
    {
        LastCopied = WarfrontExport.Discord(_record);
        DisplayServer.ClipboardSet(LastCopied);
        _status.Text = "COPIED TO THE CLIPBOARD";
    }

    /// <summary>EXPORT: a .txt and a .md under the warfront folder's exports; the status names the files, never a full path.</summary>
    public void Export()
    {
        try
        {
            var now = DateTime.UtcNow;
            var txt = WarfrontResultsArchive.Export(_dir, _record, now, markdown: false);
            var md = WarfrontResultsArchive.Export(_dir, _record, now, markdown: true);
            _status.Text = "SAVED EXPORTS/" + System.IO.Path.GetFileName(txt) + " AND EXPORTS/" + System.IO.Path.GetFileName(md);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _status.Text = "COULD NOT SAVE THE EXPORT: " + ex.Message;
        }
    }

    /// <summary>HONOR ROLL: every archived run's records and the lifetime table in place of the leaderboard; pressed again, back to the leaderboard.</summary>
    public void ToggleHonorRoll()
    {
        if (ShowingHonorRoll) { BuildTable(); HonorButton.Text = "HONOR ROLL"; return; }
        Clear();
        UiTooltip.Attach(_backing, "");
        ShowingHonorRoll = true;
        HonorButton.Text = "LEADERBOARD";
        var roll = WarfrontResultsArchive.HonorRoll(_dir);
        float y = 190f;
        Line("HonorHead", $"{roll.Runs} RUNS    {roll.Won} WON    {roll.Lost} LOST    {roll.Abandoned} ABANDONED", Left, y, CaptionSize, 1500f, Head, parent: _page);
        y += 40f;
        foreach (var e in roll.Entries.Count > 0 ? roll.Entries : Array.Empty<WarfrontHonorEntry>())
        {
            Line("HonorTitle" + y, e.Title.ToUpperInvariant(), Left, y, BodySize, 460f, null, parent: _page);
            Line("HonorHolder" + y, e.Holder, 600f, y, BodySize, 640f, null, parent: _page);
            Line("HonorValue" + y, e.Value, 1260f, y, BodySize, 240f, null, HorizontalAlignment.Right, _page);
            y += 32f;
        }
        if (roll.Entries.Count == 0) Line("HonorEmpty", "NO FINISHED RUNS ARCHIVED YET.", Left, y, BodySize, 900f, null, parent: _page);
        y += 24f;
        if (roll.Players.Count > 0) Line("HonorLifeHead", "TOTAL SCORE", Left, y, CaptionSize, 460f, Head, parent: _page);
        y += 28f;
        foreach (var p in roll.Players)
        {
            if (y > 860f) break;
            Line("LifeName" + y, p.Name, Left, y, BodySize, 460f, null, parent: _page);
            Line("LifeRuns" + y, p.Runs + (p.Runs == 1 ? " RUN" : " RUNS"), 600f, y, BodySize, 300f, null, parent: _page);
            Line("LifeTotal" + y, N(p.TotalScore), 1260f, y, BodySize, 240f, null, HorizontalAlignment.Right, _page);
            y += 32f;
        }
    }

    public void Focus() => CopyButton.FocusLater();

    public override void _Input(InputEvent e)
    {
        if (!IsVisibleInTree() || !PadHost.IsBack(e)) return;
        GetViewport().SetInputAsHandled();
        if (ShowingHonorRoll) { ToggleHonorRoll(); return; }
        _close?.Invoke();
    }

    /// <summary>Puts the panel on its own layer over <paramref name="host"/> in the 1920 by 1080 menu space and returns it; closing it frees the layer and gives the window's scaling back.</summary>
    public static WarfrontResultsPanel Open(Node host, Window window, WarfrontRunRecord record, string warfrontDir, Action? closed = null)
    {
        UiKit.EnterVirtualSpace(window);
        var layer = new CanvasLayer { Name = "WarfrontResults", Layer = 60 };
        var root = new Control { Name = "WarfrontResultsRoot", Theme = UiKit.Theme };
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        WarfrontResultsPanel? panel = null;
        panel = new WarfrontResultsPanel(record, warfrontDir, () => { layer.QueueFree(); UiKit.LeaveVirtualSpace(window); closed?.Invoke(); });
        root.AddChild(panel);
        layer.AddChild(root);
        host.AddChild(layer);
        panel.Focus();
        return panel;
    }
}
