using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer;

namespace SmClient.Extras.Warfront.Squad;

/// <summary>
/// The squad panel aboard the ship (slice W10): one row per Marine of the viewer's roster (name, class, the eight service figures, state), the name editable in place (click, type, Enter sends the rename), and a
/// second tab with the other players' squads, read only. No gear screens. One text size for the cells (<see cref="WarfrontSquadHud.TextSize"/>, the caption level), the concept's painted steel ring round a dark floor (<see cref="UiKit.Panel"/>), the concept's plates and fields, every label live; the rename hint is a hover hint on the names and the other tab says READ ONLY.
/// </summary>
public sealed partial class WarfrontSquadPanel : Control
{
    readonly WarfrontSquadModel _model;
    readonly Func<byte, string> _className;
    readonly VBoxContainer _rows = UiKit.VBox(6);
    readonly Label _title;
    readonly Button _ownTab, _otherTab;
    readonly Label _hint;
    bool _own = true;
    string _signature = "";
    readonly List<LineEdit> _edits = new();

    /// <summary>The player pressed Enter on a name: roster slot and the new name.</summary>
    public event Action<int, string>? RenameRequested;
    public event Action? CloseRequested;

    public bool OwnTab => _own;
    public IReadOnlyList<LineEdit> NameEdits => _edits;
    public int RowCount => _edits.Count;

    public WarfrontSquadPanel(WarfrontSquadModel model, Func<byte, string> className)
    {
        _model = model; _className = className;
        Name = "WarfrontSquadPanel";
        Theme = UiKit.Theme;
        Visible = false;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(centre);

        var content = UiKit.VBox(12);
        _title = WarfrontStyle.Skin(new Label { Text = "SQUAD", MouseFilter = MouseFilterEnum.Ignore }, WarfrontStyle.Heading, WarfrontStyle.Gold);
        var tabs = UiKit.HBox(8);
        _ownTab = Plate("YOUR SQUAD", () => Select(true));
        _otherTab = Plate("OTHER SQUADS", () => Select(false));
        tabs.AddChild(_title); tabs.AddChild(UiKit.Spacer()); tabs.AddChild(_ownTab); tabs.AddChild(_otherTab);
        content.AddChild(tabs);
        content.AddChild(_rows);
        _hint = Cell("", WarfrontStyle.Dim);
        var footer = UiKit.HBox(8);
        footer.AddChild(_hint); footer.AddChild(UiKit.Spacer()); footer.AddChild(Plate("CLOSE", () => CloseRequested?.Invoke(), 160f));
        content.AddChild(footer);
        var panel = UiKit.Panel(content, 18);
        panel.MouseFilter = MouseFilterEnum.Stop;
        centre.AddChild(panel);
        Select(true);
    }

    /// <summary>Shows the panel on the own squad's tab.</summary>
    public void Open() { Visible = true; Select(true); }
    public void Close() { Visible = false; ReleaseFocus(); }

    void Select(bool own)
    {
        _own = own;
        _ownTab.Disabled = own; _otherTab.Disabled = !own;
        _hint.Text = own ? "" : "READ ONLY";   // a lock's few words; the rename hint is on the names
        _signature = "";
        Refresh();
    }

    /// <summary>Rebuilds the rows when the model's changed (and no name is being typed: a keystroke is never thrown away).</summary>
    public void Refresh()
    {
        if (!Visible) return;
        var rows = _model.PanelRows(_own);
        string signature = string.Join("|", rows.Select(r => $"{r.Owner}:{r.Slot}:{r.Name}:{r.Class}:{r.State}:{string.Join(",", r.Record)}"));
        if (signature == _signature) return;
        if (_edits.Any(e => e.HasFocus())) return;
        _signature = signature;
        Rebuild(rows);
    }

    void Rebuild(List<WarfrontSquadRow> rows)
    {
        foreach (var child in _rows.GetChildren()) { _rows.RemoveChild(child); child.QueueFree(); }
        _edits.Clear();
        var grid = new GridContainer { Columns = 3 + WarfrontSquadModel.RecordHeadings.Length };
        grid.AddThemeConstantOverride("h_separation", 22);
        grid.AddThemeConstantOverride("v_separation", 6);
        foreach (var h in new[] { _own ? "NAME" : "SQUAD / NAME", "CLASS" }.Concat(WarfrontSquadModel.RecordHeadings).Append("STATE"))
            grid.AddChild(Cell(h, WarfrontStyle.Gold, h is "NAME" or "SQUAD / NAME" or "CLASS" or "STATE" ? HorizontalAlignment.Left : HorizontalAlignment.Right));
        if (rows.Count == 0) { _rows.AddChild(grid); _rows.AddChild(Cell(_own ? "NO MARINES YET" : "NO OTHER SQUADS", WarfrontStyle.Dim)); return; }
        foreach (var r in rows)
        {
            if (_own)
            {
                var edit = new LineEdit { Text = r.Name, MaxLength = 24, CustomMinimumSize = new Vector2(330, 36), SelectAllOnFocus = true, Name = $"Name{r.Slot}" };
                edit.AddThemeFontOverride("font", UiKit.ConceptFont());
                edit.AddThemeFontSizeOverride("font_size", WarfrontSquadHud.TextSize);
                edit.AddThemeColorOverride("font_color", WarfrontStyle.Ink);
                edit.AddThemeStyleboxOverride("normal", new Steel.SteelStyle(Steel.Kind.Recess, 5f, 10f, 2f));   // the concept's field: steel rim at rest, gold under the focus
                edit.AddThemeStyleboxOverride("focus", new Steel.SteelStyle(Steel.Kind.CardGold, 5f, 10f, 2f));
                UiTooltip.Attach(edit, "Click a name to change it, Enter to keep it");
                int slot = r.Slot;
                edit.TextSubmitted += text => { RenameRequested?.Invoke(slot, text); edit.ReleaseFocus(); };
                edit.FocusExited += () => edit.Text = _model.PanelRows(true).FirstOrDefault(x => x.Slot == slot)?.Name ?? edit.Text;   // an edit that was not sent goes back
                _edits.Add(edit);
                grid.AddChild(edit);
            }
            else grid.AddChild(Cell($"{OwnerLabel(r)}  {r.Name}", WarfrontStyle.Ink));
            grid.AddChild(Cell(_className(r.Class), WarfrontStyle.Ink));
            foreach (var figure in r.Record) grid.AddChild(Cell(figure.ToString(), WarfrontStyle.Ink, HorizontalAlignment.Right));
            grid.AddChild(Cell(r.State, r.State is "DOWN" ? WarfrontStyle.Alert : WarfrontStyle.Ink));
        }
        _rows.AddChild(grid);
    }

    /// <summary>One cell of the table: the concept lettering at the table's one size.</summary>
    static Label Cell(string text, Color ink, HorizontalAlignment align = HorizontalAlignment.Left) =>
        WarfrontStyle.Skin(new Label { Text = text, HorizontalAlignment = align, MouseFilter = MouseFilterEnum.Ignore }, WarfrontSquadHud.TextSize, ink);

    static Button Plate(string text, Action press, float width = 200f)
    {
        var b = new Button { Text = text, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(width, 48f) };
        b.Pressed += press;
        return WarfrontStyle.Plate(b, WarfrontStyle.Body, width);
    }

    static string OwnerLabel(WarfrontSquadRow r) => r.OwnerPlayer >= 0 ? $"P{r.OwnerPlayer}" : "--";

    /// <summary>Test aid: starts editing the name in <paramref name="slot"/>'s row with <paramref name="text"/> typed (not sent).</summary>
    public void BeginEdit(int slot, string text)
    {
        var edit = _edits.FirstOrDefault(e => e.Name == $"Name{slot}");
        if (edit is null) return;
        edit.GrabFocus();
        edit.Text = text;
        edit.CaretColumn = text.Length;
    }

    public override void _Process(double delta) { if (Visible && Engine.GetProcessFrames() % 15 == 0) Refresh(); }
}
