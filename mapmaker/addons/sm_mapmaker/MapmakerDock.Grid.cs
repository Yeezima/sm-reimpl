using System;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>The Map tab's Grid section (docs/mapmaker.md "Grid"): the settings of the 3D view's fixed reference grid, saved with the map, independent of the Snap step.</summary>
public partial class MapmakerDock
{
    // Names let the self tests find the controls.
    public const string GridShowName = "GridShow", GridColourName = "GridColour", GridCellName = "GridCell", GridHeightName = "GridHeight", GridFromSelectionName = "GridFromSelection", GridOverName = "GridOver";

    readonly CheckBox _gridShow = new() { Name = GridShowName, Text = "Show grid", ButtonPressed = true };
    readonly CheckBox _gridOver = new() { Name = GridOverName, Text = "Draw over objects" };
    readonly ColorPickerButton _gridColour = new() { Name = GridColourName, EditAlpha = true, CustomMinimumSize = new Vector2(60, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill, Color = new Color(0.5f, 0.5f, 0.5f, 0.5f) };
    readonly SpinBox _gridCell = new() { Name = GridCellName, MinValue = MapGrid.MinCell, MaxValue = MapGrid.MaxCell, Step = 0.01, CustomArrowStep = 0.5, Value = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill };
    readonly SpinBox _gridHeight = new() { Name = GridHeightName, MinValue = -1000, MaxValue = 1000, Step = 0.001, CustomArrowStep = 0.5, AllowGreater = true, AllowLesser = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
    readonly Button _gridFromSelection = new() { Name = GridFromSelectionName, Text = "From selection", TooltipText = "Sets the height to the lowest point of the selected objects." };

    void GridSection(VBoxContainer v)
    {
        Section(v, "Grid", "A fixed reference grid in the 3D view, saved with the map. It does not change with zoom or with the Snap step.");
        _gridShow.Toggled += on => { if (!_refreshing) _plugin.SetGrid("Grid: show", ("show", on)); };
        _gridOver.Toggled += on => { if (!_refreshing) _plugin.SetGrid("Grid: draw over objects", ("drawOver", on)); };
        // The colour picker reports every step of a drag: the view follows live, and one undo step is made when the picker closes.
        _gridColour.ColorChanged += c => { if (!_refreshing) _plugin.ApplyGrid(_plugin.CurrentGrid with { Colour = GridColourText(c) }); };
        _gridColour.PopupClosed += CommitGridColour;
        _gridCell.ValueChanged += x => { if (!_refreshing) _plugin.SetGrid("Grid: cell size", ("cellSize", (float)x)); };
        _gridHeight.ValueChanged += x => { if (!_refreshing) _plugin.SetGrid("Grid: height", ("height", (float)x)); };
        _gridFromSelection.Pressed += GridHeightFromSelection;
        v.AddChild(_gridShow);
        v.AddChild(Row(Field("Colour", 90), _gridColour));
        v.AddChild(Row(Field("Cell size (m)", 90), _gridCell));
        v.AddChild(Row(Field("Height (m)", 90), _gridHeight));
        v.AddChild(Row(Field("", 90), _gridFromSelection));
        v.AddChild(_gridOver);
    }

    static string GridColourText(Color c) => MapGrid.ColourText((byte)Math.Round(c.R * 255), (byte)Math.Round(c.G * 255), (byte)Math.Round(c.B * 255), (byte)Math.Round(c.A * 255));

    void CommitGridColour()
    {
        var text = GridColourText(_gridColour.Color);
        if (_plugin.Session is null) return;
        if (text == _plugin.CurrentGrid.Colour) { _plugin.ApplyGrid(); return; }
        _plugin.SetGrid("Grid: colour", ("colour", text));
    }

    /// <summary>Test aid: the colour picker's change and close, as the user's drag and click-away would make them.</summary>
    public void TestSetGridColour(Color c) { _gridColour.Color = c; CommitGridColour(); }

    void GridHeightFromSelection()
    {
        if (_plugin.SelectionBaseHeight() is not { } z) { _plugin.SetStatus("Grid height: select an object first."); return; }
        _plugin.SetGrid("Grid: height from selection", ("height", z));
    }

    /// <summary>Shows the open map's grid settings (the defaults while no map is open, when the controls are off).</summary>
    void RefreshGrid()
    {
        var g = _plugin.CurrentGrid;
        var (r, gr, b, a) = g.Bytes();
        bool open = _plugin.Session is not null;
        _gridShow.SetPressedNoSignal(g.Show); _gridOver.SetPressedNoSignal(g.DrawOver);
        _gridColour.Color = new Color(r / 255f, gr / 255f, b / 255f, a / 255f);
        _gridCell.SetValueNoSignal(g.CellSize); _gridHeight.SetValueNoSignal(g.Height);
        _gridShow.Disabled = _gridOver.Disabled = _gridFromSelection.Disabled = !open;
        _gridCell.Editable = _gridHeight.Editable = open;
        _gridColour.Disabled = !open;
    }
}
