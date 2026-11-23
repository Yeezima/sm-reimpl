using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// cancel-editor (owner, 2026-11-17): the live animation cancel matrix, F8 (Classic PvP; ours, a test aid). A grid of numbered "row.column" cells (rows: the action in progress; columns: the input) coloured by
/// state (green yes, red no, amber window, blue queued, grey not applicable); a click opens the inspector (state, from and until seconds); the class and weapon selectors show and edit the per-class and per-weapon
/// overrides ("inherits" when the scope has none). Every edit is a <see cref="DevCancelEdit"/> request: the server (started with <c>--dev-tools</c> on loopback, the same gate as F6) applies it to the next tick
/// and sends the table back, so what the grid shows is what the server plays by (the "in sync" mark compares the hashes). Export writes a timestamped file to the exports folder
/// (<see cref="CancelConfigStore.Folder"/>), import loads one. Test aids: <c>--cancel-panel</c> opens it, <c>--cancel-script "ID:STATE[:FROM:UNTIL],..."</c> edits cells in turn (the ids are
/// <c>row.col</c> numbers; <c>export</c>, <c>reset</c> and <c>import</c> are words too), logging <c>cancel: ...</c>. Client only.
/// </summary>
public partial class CancelEditorPanel : CanvasLayer
{
    public const string PanelArg = "--cancel-panel", ScriptArg = "--cancel-script";

    readonly Func<GameClient?> _client;
    PanelContainer? _panel;
    Label? _status, _info, _note, _inherit, _title;
    OptionButton? _classBox, _weaponBox, _stateBox, _importBox;
    SpinBox? _from, _until;
    CheckBox? _fromNatural, _untilNatural;
    Godot.Button[,]? _cells;
    int _selRow = -1, _selCol = -1;
    CancelScope _scope = CancelScope.Any;
    uint _drawnHash = uint.MaxValue; CancelScope _drawnScope; int _drawnSyncs = -1;
    bool _syncing;
    Input.MouseModeEnum _previousMouse = Input.MouseModeEnum.Captured;
    string _message = "";
    static readonly Color Blue = new(0.55f, 0.8f, 1f);

    public CancelEditorPanel(Func<GameClient?> client)
    {
        _client = client;
        Layer = 96;
        Name = "CancelEditor";
    }

    public bool IsOpen => _panel?.Visible == true;

    static Color StateColor(CancelState s) => s switch
    {
        CancelState.Yes => new Color(0.25f, 0.62f, 0.3f), CancelState.No => new Color(0.72f, 0.25f, 0.25f), CancelState.Window => new Color(0.85f, 0.65f, 0.2f),
        CancelState.Queued => new Color(0.3f, 0.5f, 0.85f), _ => new Color(0.25f, 0.25f, 0.28f),
    };

    static string StateLetter(CancelCell c) => c.State switch { CancelState.Yes => "Y", CancelState.No => "N", CancelState.Window => c.HasTimes ? "w*" : "W", CancelState.Queued => "Q", _ => "-" };

    static string Short(string label) { int i = label.IndexOf(" (", StringComparison.Ordinal); return i > 0 ? label[..i] : label; }

    public override void _Ready()
    {
        var panel = new PanelContainer { Name = "Panel", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.04f, 0.045f, 0.06f, 0.95f), BorderColor = new Color(0.85f, 0.7f, 0.25f), BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2, ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 8, ContentMarginBottom = 8 });
        panel.Position = new Vector2(8, 8);
        panel.CustomMinimumSize = new Vector2(1500, 0);
        AddChild(panel);
        _panel = panel;
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 4);
        panel.AddChild(outer);

        outer.AddChild(new Label { Text = "CANCEL MATRIX   (F8 or Esc closes)  -  Classic PvP, local test server only, edits apply to the next tick", Modulate = new Color(0.95f, 0.8f, 0.3f) });
        var bar = new HBoxContainer(); bar.AddThemeConstantOverride("separation", 8); outer.AddChild(bar);
        bar.AddChild(new Label { Text = "Class" });
        _classBox = new OptionButton { CustomMinimumSize = new Vector2(150, 0) }; bar.AddChild(_classBox);
        bar.AddChild(new Label { Text = "Weapon" });
        _weaponBox = new OptionButton { CustomMinimumSize = new Vector2(190, 0) }; bar.AddChild(_weaponBox);
        _classBox.ItemSelected += _ => ScopeChanged(); _weaponBox.ItemSelected += _ => ScopeChanged();
        _status = new Label { Modulate = Blue, CustomMinimumSize = new Vector2(300, 0) }; bar.AddChild(_status);
        Btn(bar, "RESET ALL", () => { _client()?.SendCancelEdit(DevCancelOp.ResetAll, []); Say("reset every cell to the shipped default"); }, "Puts every cell back to the release default.");
        Btn(bar, "EXPORT", Export, "Writes the table to a timestamped file in the exports folder.");
        _importBox = new OptionButton { CustomMinimumSize = new Vector2(240, 0) }; bar.AddChild(_importBox);
        Btn(bar, "IMPORT", Import, "Loads the chosen export and applies it live.");
        Btn(bar, "REFRESH", RefreshImports, "Re-reads the exports folder.");
        _info = new Label { Modulate = new Color(0.8f, 0.8f, 0.8f) }; outer.AddChild(_info);

        var body = new HBoxContainer(); body.AddThemeConstantOverride("separation", 14); outer.AddChild(body);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(1010, 700), HorizontalScrollMode = ScrollContainer.ScrollMode.Auto }; body.AddChild(scroll);
        var grid = new GridContainer { Columns = CancelLayout.ColCount + 1 };
        grid.AddThemeConstantOverride("h_separation", 2); grid.AddThemeConstantOverride("v_separation", 2);
        scroll.AddChild(grid);
        var doc = CancelData.Doc;
        grid.AddChild(new Label { Text = "" });
        for (int c = 0; c < CancelLayout.ColCount; c++)
            grid.AddChild(new Label { Text = $"{c + 1} {ColName(c)}", CustomMinimumSize = new Vector2(44, 0), ClipText = true, TooltipText = doc.ColLabel(c), Modulate = IsStabCol(c) ? StabTint : Blue, MouseFilter = Control.MouseFilterEnum.Pass });
        _cells = new Godot.Button[CancelLayout.RowCount, CancelLayout.ColCount];
        for (int r = 0; r < CancelLayout.RowCount; r++)
        {
            // sim/stab-key-only (owner 2026-11-23): the Power Sword Stab section, its own heading above its row; its column (the Sword Stab key) is tinted the same way.
            if (IsStabRow(r))
            {
                grid.AddChild(new Label { Text = "POWER SWORD STAB (Sword Stab key only; column Stab = the key pressed during any action)", Modulate = StabTint, MouseFilter = Control.MouseFilterEnum.Pass });
                for (int c = 0; c < CancelLayout.ColCount; c++) grid.AddChild(new Control { MouseFilter = Control.MouseFilterEnum.Ignore });
            }
            grid.AddChild(new Label { Text = $"{r + 1} {Short(doc.RowLabel(r))}", CustomMinimumSize = new Vector2(250, 0), ClipText = true, TooltipText = doc.RowLabel(r), Modulate = IsStabRow(r) ? StabTint : Colors.White, MouseFilter = Control.MouseFilterEnum.Pass });
            for (int c = 0; c < CancelLayout.ColCount; c++)
            {
                int rr = r, cc = c;
                var b = new Godot.Button { CustomMinimumSize = new Vector2(44, 18), ToggleMode = false, FocusMode = Control.FocusModeEnum.None, TooltipText = $"{CancelLayout.Number(r, c)}  {Short(doc.RowLabel(r))}  x  {Short(doc.ColLabel(c))}" };
                b.AddThemeFontSizeOverride("font_size", 10);
                b.Pressed += () => Select(rr, cc);
                grid.AddChild(b);
                _cells[r, c] = b;
            }
        }

        var side = new VBoxContainer { CustomMinimumSize = new Vector2(430, 0) }; side.AddThemeConstantOverride("separation", 6); body.AddChild(side);
        side.AddChild(new Label { Text = "CELL", Modulate = Blue });
        _title = new Label { Text = "click a cell", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0) }; side.AddChild(_title);
        _inherit = new Label { Modulate = new Color(0.9f, 0.8f, 0.4f), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0) }; side.AddChild(_inherit);
        var stateRow = new HBoxContainer(); side.AddChild(stateRow);
        stateRow.AddChild(new Label { Text = "State", CustomMinimumSize = new Vector2(70, 0) });
        _stateBox = new OptionButton { CustomMinimumSize = new Vector2(200, 0) };
        foreach (var n in new[] { "yes (ends the action, input starts)", "no (input ignored)", "window (yes from-until, else no)", "queued (starts when the action ends)" }) _stateBox.AddItem(n);
        _stateBox.ItemSelected += _ => Commit();
        stateRow.AddChild(_stateBox);
        (_from, _fromNatural) = TimeRow(side, "From (s)");
        (_until, _untilNatural) = TimeRow(side, "Until (s)");
        side.AddChild(new Label { Text = "Times count seconds into the action; \"data timing\" leaves that end open (from the start, to the end). A window edited here replaces the data's window.", Modulate = new Color(0.65f, 0.65f, 0.65f), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0) });
        var cellBtns = new HBoxContainer(); side.AddChild(cellBtns);
        Btn(cellBtns, "RESET CELL", () => { if (_selRow >= 0) { _client()?.SendCancelEdit(DevCancelOp.ResetCell, [new CancelChange(_scope, _selRow, _selCol, new CancelCell(CancelState.Yes))]); Say($"reset {CancelLayout.Number(_selRow, _selCol)}"); } }, "Puts this cell back to the release default (at this scope).");
        _note = new Label { Modulate = new Color(0.78f, 0.78f, 0.78f), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0) }; side.AddChild(_note);
        side.AddChild(new Label { Text = "Legend: Y yes  N no  W window  Q queued  - not applicable  (* = edited times). A bright border: differs from the release default at this scope.", Modulate = new Color(0.65f, 0.65f, 0.65f), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0) });
        RefreshImports();
    }

    static readonly Color StabTint = new(1f, 0.55f, 0.85f);
    static bool IsStabRow(int r) => CancelLayout.Rows[r] == CancelData.StabRowId;
    static bool IsStabCol(int c) => CancelLayout.Cols[c] == CancelData.StabColId;

    string ColName(int c) => CancelLayout.Cols[c] switch { "stab" => "Stab","light" => "Lt", "heavy" => "Hv", "charge" => "Chg", "roll" => "Roll", "pack" => "Pack", "fire" => "Fire", "aim" => "Aim", "reload" => "Rld", "switch" => "Swt", "grenade" => "Gren", "sprint" => "Spr", "stick" => "WAD", "back" => "S", "exec" => "Exe", "hit_react" => "Stun", "hit_plain" => "Hit", var o => o };

    (SpinBox, CheckBox) TimeRow(VBoxContainer into, string label)
    {
        var row = new HBoxContainer(); into.AddChild(row);
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(70, 0) });
        var spin = new SpinBox { MinValue = 0, MaxValue = 60, Step = 0.01, CustomMinimumSize = new Vector2(120, 0) };
        var natural = new CheckBox { Text = "data timing", ButtonPressed = true };
        spin.ValueChanged += _ => { if (!_syncing) { natural.ButtonPressed = false; Commit(); } };
        natural.Toggled += _ => { if (!_syncing) Commit(); };
        row.AddChild(spin); row.AddChild(natural);
        return (spin, natural);
    }

    static void Btn(Control into, string text, Action onPress, string tip)
    {
        var b = new Godot.Button { Text = text, TooltipText = tip, FocusMode = Control.FocusModeEnum.None };
        b.Pressed += onPress;
        into.AddChild(b);
    }

    void Say(string text) { _message = text; GD.Print("cancel: " + text); }

    // ---- scope and selection ----
    void FillScopes(SimConfig c)
    {
        if (_classBox is null || _weaponBox is null || _classBox.ItemCount > 0) return;
        _classBox.AddItem("All classes");
        foreach (var k in c.Classes) _classBox.AddItem(k.Name);
        _weaponBox.AddItem("All weapons");
        foreach (var w in c.Weapons.Select(w => w.Name).Distinct()) _weaponBox.AddItem(w);
    }

    void ScopeChanged()
    {
        string cls = _classBox!.Selected > 0 ? _classBox.GetItemText(_classBox.Selected) : "";
        string wpn = _weaponBox!.Selected > 0 ? _weaponBox.GetItemText(_weaponBox.Selected) : "";
        _scope = new CancelScope(cls, wpn);
        Redraw(true);
    }

    void Select(int row, int col) { _selRow = row; _selCol = col; Redraw(true); }

    /// <summary>The cell the grid shows at the current scope: the scope's own override, else what a player of that scope gets.</summary>
    CancelCell Shown(CancelData t, int r, int c) => t.At(_scope, r, c);

    void Commit()
    {
        if (_syncing || _selRow < 0 || _client() is not { } client) return;
        var state = _stateBox!.Selected switch { 0 => CancelState.Yes, 1 => CancelState.No, 2 => CancelState.Window, _ => CancelState.Queued };
        var cell = state == CancelState.Window
            ? new CancelCell(state, _fromNatural!.ButtonPressed ? CancelCell.Natural : (int)Math.Round(_from!.Value * 1000), _untilNatural!.ButtonPressed ? CancelCell.Natural : (int)Math.Round(_until!.Value * 1000))
            : new CancelCell(state);
        client.SendCancelEdit(DevCancelOp.Apply, [new CancelChange(_scope, _selRow, _selCol, cell)]);
        Say($"{CancelLayout.Number(_selRow, _selCol)} [{_scope}] -> {cell}");
    }

    // ---- export / import ----
    void Export()
    {
        if (_client() is not { } client) return;
        try { string path = CancelConfigStore.Export(client.CancelTable); Say("exported " + path); RefreshImports(); }
        catch (Exception e) { Say("export failed: " + e.Message); }
    }

    void RefreshImports()
    {
        if (_importBox is null) return;
        _importBox.Clear();
        foreach (var f in CancelConfigStore.List()) _importBox.AddItem(System.IO.Path.GetFileName(f));
        if (_importBox.ItemCount == 0) _importBox.AddItem("(no exports yet)");
    }

    void Import()
    {
        if (_client() is not { } client || _importBox is null || _importBox.ItemCount == 0) return;
        string name = _importBox.GetItemText(_importBox.Selected);
        string path = System.IO.Path.Combine(CancelConfigStore.Folder(), name);
        ImportFile(client, path);
    }

    void ImportFile(GameClient client, string path)
    {
        var table = CancelConfigStore.Import(path, out string error);
        if (table is null) { Say($"import refused: {error}"); return; }
        table = table.WithoutUnknownScopes(client.Config, out var dropped);
        client.SendCancelEdit(DevCancelOp.ResetAll, []);
        client.SendCancelEdit(DevCancelOp.Apply, table.DiffFromShipped());
        Say($"imported {System.IO.Path.GetFileName(path)}: {table.DiffFromShipped().Count} change(s)" + (dropped.Count > 0 ? $"; dropped {dropped.Count} override(s) naming a class or weapon this build lacks ({string.Join(", ", dropped)})" : ""));
    }

    // ---- drawing ----
    public void Sync() => Redraw(false);

    void Redraw(bool force)
    {
        if (_client() is not { } client || _cells is null) return;
        FillScopes(client.Config);
        var t = client.CancelTable;
        if (!force && t.Hash == _drawnHash && _scope == _drawnScope && client.CancelSyncs == _drawnSyncs && _status!.Text.Length > 0) { UpdateStatus(client, t); return; }
        _drawnHash = t.Hash; _drawnScope = _scope; _drawnSyncs = client.CancelSyncs;
        var shipped = CancelData.Shipped;
        for (int r = 0; r < CancelLayout.RowCount; r++)
            for (int c = 0; c < CancelLayout.ColCount; c++)
            {
                var cell = Shown(t, r, c);
                var b = _cells[r, c];
                b.Text = StateLetter(cell);
                bool edited = cell != shipped.At(_scope, r, c) || t.Overrides(_scope, r, c) != shipped.Overrides(_scope, r, c);
                var color = StateColor(cell.State);
                var sb = new StyleBoxFlat { BgColor = color, BorderColor = edited ? Colors.White : color.Darkened(0.4f), BorderWidthLeft = edited ? 3 : 1, BorderWidthRight = edited ? 3 : 1, BorderWidthTop = edited ? 3 : 1, BorderWidthBottom = edited ? 3 : 1 };
                b.AddThemeStyleboxOverride("normal", sb); b.AddThemeStyleboxOverride("hover", sb); b.AddThemeStyleboxOverride("pressed", sb);
                b.Disabled = cell.State == CancelState.NotApplicable && false;
            }
        UpdateStatus(client, t);
        DrawInspector(t);
    }

    void UpdateStatus(GameClient client, CancelData t)
    {
        int diff = t.DiffFromShipped().Count;
        _status!.Text = client.CancelInSync ? $"in sync with the server (hash {t.Hash:x8})" : "NOT in sync with the server (waiting)";
        _status.Modulate = client.CancelInSync ? new Color(0.5f, 0.9f, 0.5f) : new Color(1f, 0.6f, 0.3f);
        _info!.Text = $"{diff} cell(s) differ from the release default.  Scope: {_scope}.  Exports: {CancelConfigStore.Folder()}.  {_message}";
    }

    void DrawInspector(CancelData t)
    {
        if (_selRow < 0) return;
        _syncing = true;
        var doc = CancelData.Doc;
        var cell = Shown(t, _selRow, _selCol);
        _title!.Text = $"{CancelLayout.Number(_selRow, _selCol)}  {doc.RowLabel(_selRow)}\nwhen the input is: {doc.ColLabel(_selCol)}";
        bool own = t.Overrides(_scope, _selRow, _selCol);
        _inherit!.Text = _scope.IsBase ? "Scope: the table itself (every class and weapon without an override)." : own ? $"Override for {_scope}." : $"Inherits the default for {_scope}: {cell}. Editing makes an override.";
        _stateBox!.Selected = cell.State switch { CancelState.Yes => 0, CancelState.No => 1, CancelState.Window => 2, CancelState.Queued => 3, _ => 1 };
        _fromNatural!.ButtonPressed = cell.FromMs == CancelCell.Natural; _from!.Value = cell.FromMs == CancelCell.Natural ? 0 : cell.FromMs / 1000.0;
        _untilNatural!.ButtonPressed = cell.UntilMs == CancelCell.Natural; _until!.Value = cell.UntilMs == CancelCell.Natural ? 0 : cell.UntilMs / 1000.0;
        var baseline = CancelData.Baseline.Base(_selRow, _selCol);
        _note!.Text = $"The code does: {baseline}.\n{doc.Note(_selRow, _selCol)}\nSource: {doc.Source(_selRow, _selCol)}"
            + (cell.State == CancelState.NotApplicable ? "\nNot applicable: this action cannot meet this input; an edit here does nothing." : "");
        _syncing = false;
    }

    // ---- test aids ----
    /// <summary>Presses one script word: <c>R.C:state[:from:until]</c> (state yes|no|window|queued|reset; seconds), <c>export</c>, <c>resetall</c>, <c>import:FILE</c>, or <c>scope:CLASS/WEAPON</c> ("-" for any).</summary>
    public bool Press(string word)
    {
        if (_client() is not { } client) return false;
        var parts = word.Split(':');
        switch (parts[0])
        {
            case "export": Export(); return true;
            case "resetall": client.SendCancelEdit(DevCancelOp.ResetAll, []); Say("reset all"); return true;
            case "import" when parts.Length == 2:
                string file = parts[1] == "latest" ? CancelConfigStore.List().FirstOrDefault() ?? "" : parts[1];
                if (file.Length == 0) { Say("import: no exports yet"); return false; }
                ImportFile(client, System.IO.Path.IsPathRooted(file) ? file : System.IO.Path.Combine(CancelConfigStore.Folder(), file)); return true;
            case "scope" when parts.Length == 2:
                var cw = parts[1].Split('/');
                _scope = new CancelScope(cw[0] == "-" ? "" : cw[0], cw.Length > 1 && cw[1] != "-" ? cw[1] : "");
                Say($"scope {_scope}"); return true;
        }
        var rc = parts[0].Split('.');
        if (rc.Length != 2 || !int.TryParse(rc[0], out int row) || !int.TryParse(rc[1], out int col) || row < 1 || col < 1 || row > CancelLayout.RowCount || col > CancelLayout.ColCount || parts.Length < 2) return false;
        row--; col--;
        _selRow = row; _selCol = col;   // the inspector shows the cell the script touched
        if (parts[1] == "reset") { client.SendCancelEdit(DevCancelOp.ResetCell, [new CancelChange(_scope, row, col, new CancelCell(CancelState.Yes))]); Say($"reset {CancelLayout.Number(row, col)}"); return true; }
        var state = CancelData.ParseState(parts[1]);
        if (state is null || state == CancelState.NotApplicable) return false;
        int Ms(int i) => parts.Length > i && double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? (int)Math.Round(s * 1000) : CancelCell.Natural;
        var cell = new CancelCell(state.Value, Ms(2), Ms(3));
        client.SendCancelEdit(DevCancelOp.Apply, [new CancelChange(_scope, row, col, cell)]);
        Say($"{CancelLayout.Number(row, col)} [{_scope}] -> {cell.Normalized()}");
        return true;
    }

    // ---- opening ----
    public void SetOpen(bool open)
    {
        if (_panel is null || open == _panel.Visible) return;
        _panel.Visible = open;
        if (open) { RefreshImports(); Redraw(true); _previousMouse = Input.MouseMode; Input.MouseMode = Input.MouseModeEnum.Visible; }
        else Input.MouseMode = _previousMouse;
    }

    public void Toggle() => SetOpen(!IsOpen);
}
