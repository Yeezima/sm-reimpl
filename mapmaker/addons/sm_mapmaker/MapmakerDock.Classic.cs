using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using SmContent;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// The dock's side of "import as Classic plays it" (docs/mapmaker.md "Classic link", "Look in play", "Refresh from Classic"): the Map tab's look dropdown and Refresh from Classic
/// button with its dialog, the Object tab's wall shape, and the Scene tab's "Hidden by Classic" filter. Every edit is a core operation, so each is one undo step.
/// </summary>
public partial class MapmakerDock
{
    public const string LookName = "LookFromSm1", RefreshButtonName = "RefreshFromClassic", WallShapeName = "WallShape", HiddenFilterName = "HiddenByClassicFilter", RefreshDialogName = "RefreshDialog";

    // ---- Map tab: look ----------------------------------------------------------------------------------------------------------------------------------------

    readonly OptionButton _look = new() { Name = LookName, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(120, 0),
        TooltipText = "Draws this map in play with the lighting, sky, fog and decals of an original map. None keeps the plain daylight look." };
    IReadOnlyList<MapEntry>? _lookMaps;

    void LookSection(VBoxContainer v)
    {
        Section(v, "Look", "Draws the map in play with the lighting, sky and fog of an original map. Players need that map in their game install to join; without it the game refuses the map.");
        _look.AddItem("None (plain daylight)"); _look.SetItemMetadata(0, "");
        _look.GetPopup().AboutToPopup += FillLookList;
        _look.ItemSelected += i =>
        {
            if (_refreshing) return;
            string map = _look.GetItemMetadata((int)i).AsString();
            _plugin.SetLook(map.Length == 0 ? null : map);
        };
        v.AddChild(Row(Field("Look from SM1 map", 120), _look));
    }

    /// <summary>The competitive maps the install lists (the same roster as the SM1 library), read once, when the list is first needed.</summary>
    IReadOnlyList<MapEntry> LookMaps()
    {
        if (_lookMaps is not null) return _lookMaps;
        if (Sm1Catalog.Maps.Count > 0) return Sm1Catalog.Maps;
        try { _lookMaps = Sm1Source.GameDir() is { } dir ? Sm1Index.Maps(dir) : []; }
        catch (Exception) { _lookMaps = []; }
        return _lookMaps;
    }

    /// <summary>Test aid: gives the dropdown these maps instead of reading the install.</summary>
    public void TestSetLookMaps(IReadOnlyList<MapEntry> maps) { _lookMaps = maps; FillLookList(); }

    /// <summary>Fills the dropdown (None, every map of the install) keeping the open map's look selected, even one this install does not have.</summary>
    void FillLookList()
    {
        string current = _plugin.CurrentLook ?? "";
        bool was = _refreshing; _refreshing = true;
        try
        {
            _look.Clear();
            _look.AddItem("None (plain daylight)"); _look.SetItemMetadata(0, "");
            foreach (var m in LookMaps())
            {
                _look.AddItem($"{m.DisplayName} ({m.Name})"); _look.SetItemMetadata(_look.ItemCount - 1, m.Name);
            }
            int at = Enumerable.Range(0, _look.ItemCount).FirstOrDefault(i => _look.GetItemMetadata(i).AsString() == current, -1);
            if (at < 0 && current.Length > 0)
            {
                _look.AddItem($"{current} (not in your install)"); _look.SetItemMetadata(_look.ItemCount - 1, current);
                at = _look.ItemCount - 1;
            }
            _look.Select(Math.Max(at, 0));
        }
        finally { _refreshing = was; }
    }

    /// <summary>Called from Refresh(): the dropdown shows the open map's look and is off while no map is open.</summary>
    void RefreshLook()
    {
        string current = _plugin.CurrentLook ?? "";
        bool shown = _look.Selected >= 0 && _look.GetItemMetadata(_look.Selected).AsString() == current;
        if (!shown) FillLookList();
        _look.Disabled = _plugin.Session is null;
        RefreshClassicButton();
    }

    public string TestLookSelected() => _look.Selected >= 0 ? _look.GetItemMetadata(_look.Selected).AsString() : "";
    public int TestLookCount => _look.ItemCount;
    public OptionButton TestLookButton => _look;
    /// <summary>Test aid: picks a dropdown entry like a click does.</summary>
    public void TestPickLook(string map)
    {
        for (int i = 0; i < _look.ItemCount; i++)
            if (_look.GetItemMetadata(i).AsString() == map) { _look.Select(i); _look.EmitSignal(OptionButton.SignalName.ItemSelected, i); return; }
    }

    // ---- Map tab: Refresh from Classic --------------------------------------------------------------------------------------------------------------------------

    Button? _refreshClassic;
    bool? _installOverride;
    bool? _hasInstall;

    /// <summary>Test aid: pretends the install is there (true) or missing (false).</summary>
    public void TestSetInstall(bool? present) { _installOverride = present; RefreshClassicButton(); }
    public Button? RefreshClassicButtonControl => _refreshClassic;

    bool InstallPresent()
    {
        if (_installOverride is { } o) return o;
        if (_hasInstall is null) { try { _hasInstall = Sm1Source.GameDir() is not null; } catch (Exception) { _hasInstall = false; } }
        return _hasInstall.Value;
    }

    Button RefreshClassicButtonBuild()
    {
        _refreshClassic = Btn("Refresh from Classic", () => _ = StartRefreshFromClassic(),
            "Compares the objects imported from an SM1 map with what Classic has now, and lists what is new, gone or changed. Nothing is changed until you press Apply.");
        _refreshClassic.Name = RefreshButtonName;
        return _refreshClassic;
    }

    /// <summary>The last refresh's result line, kept under the button (the status line is overwritten by background loads).</summary>
    readonly Label _refreshResult = new() { Name = "RefreshResult", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(250, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
    public string TestRefreshResult => _refreshResult.Text;

    Label RefreshResultLabel()
    {
        _refreshResult.AddThemeColorOverride("font_color", HintColour); _refreshResult.AddThemeFontSizeOverride("font_size", 12);
        return _refreshResult;
    }

    void RefreshClassicButton()
    {
        if (_refreshClassic is null) return;
        bool install = InstallPresent();
        _refreshClassic.Disabled = _plugin.Session is null || !install;
        _refreshClassic.TooltipText = install ? "Compares the objects imported from an SM1 map with what Classic has now, and lists what is new, gone or changed. Nothing is changed until you press Apply."
            : MapmakerPlugin.NoInstallMessage;
    }

    async System.Threading.Tasks.Task StartRefreshFromClassic()
    {
        if (_plugin.Session is null) return;
        _plugin.SetStatus("Comparing with Classic (reads the original map; this can take a few seconds)...");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        ShowRefreshResult(_plugin.PreviewRefresh());
    }

    ConfirmationDialog? _refreshDialog;
    VBoxContainer? _refreshBody;
    RefreshModel _refreshModel = new();
    public RefreshModel TestRefreshModel => _refreshModel;
    public ConfirmationDialog? RefreshDialogControl => _refreshDialog;

    /// <summary>Shows what a preview found (or why it could not run). Public for the self-test.</summary>
    public void ShowRefreshResult(OpResult r)
    {
        if (!r.Ok)
        {
            _plugin.SetStatus("Refresh from Classic: " + string.Join("; ", r.Errors.Select(e => e.Code == "missing-install" ? MapmakerPlugin.NoInstallMessage : e.Message)));
            return;
        }
        var refresh = r.Refresh;
        if (refresh is null || refresh["maps"] is JsonArray { Count: 0 }) { _plugin.SetStatus("This map has no objects imported from an SM1 map, so there is nothing to refresh."); return; }
        var model = RefreshModel.From(refresh, r.Warnings);
        if (model.Empty) { _plugin.SetStatus("Already up to date: Classic has nothing new, gone or changed for this map." + (model.Warnings.Count > 0 ? " " + string.Join(" ", model.Warnings) : "")); return; }
        _refreshModel = model;
        BuildRefreshDialog();
        _refreshDialog!.PopupCentered(new Vector2I(560, 520));
    }

    void BuildRefreshDialog()
    {
        if (_refreshDialog is null)
        {
            _refreshDialog = new ConfirmationDialog { Name = RefreshDialogName, Title = "Refresh from Classic", Exclusive = true, OkButtonText = "Apply selected", CancelButtonText = "Cancel", MinSize = new Vector2I(520, 420) };
            _refreshDialog.AddButton("Apply all I haven't edited", false, "all").TooltipText = "Applies every removed or changed item you have not edited, whatever its tick; edited items are left alone. Deleted items come back only when ticked.";   // review: the ticks do not count here
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(500, 380) };
            _refreshBody = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            scroll.AddChild(_refreshBody);
            _refreshDialog.AddChild(scroll);
            _refreshDialog.Confirmed += () => RunRefresh(all: false);
            _refreshDialog.CustomAction += a => { if (a == "all") { RunRefresh(all: true); _refreshDialog.Hide(); } };
            AddChild(_refreshDialog);
        }
        foreach (var c in _refreshBody!.GetChildren()) { _refreshBody.RemoveChild(c); c.QueueFree(); }
        foreach (var w in _refreshModel.Warnings) _refreshBody.AddChild(Hint(w));
        void Block(string section, string title, string hint)
        {
            var rows = _refreshModel.In(section).ToList();
            if (rows.Count == 0) return;
            if (_refreshBody.GetChildCount() > 0) _refreshBody.AddChild(new HSeparator());
            _refreshBody.AddChild(Head($"{title} ({rows.Count})"));
            _refreshBody.AddChild(Hint(hint));
            int shown = 0;
            foreach (var row in rows)
            {
                if (++shown > 200) { _refreshBody.AddChild(Hint($"... and {rows.Count - 200} more.")); break; }
                if (row.Pickable)
                {
                    var box = new CheckBox { Text = row.Text, ButtonPressed = row.Checked, ClipText = false, TooltipText = $"{row.Map} / {row.Key}" };
                    var r = row;
                    box.Toggled += on => r.Checked = on;
                    _refreshBody.AddChild(box);
                }
                else _refreshBody.AddChild(new Label { Text = row.Text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(250, 0), TooltipText = $"{row.Map} / {row.Key}" });
            }
        }
        Block(RefreshRow.Added, "Added by Classic", "These are put into your map automatically when you press Apply.");
        Block(RefreshRow.Removed, "Removed from Classic", "Tick the ones to delete from your map. An object you edited is marked and starts unticked.");
        Block(RefreshRow.Changed, "Changed in Classic", "Tick the ones to set back to Classic's place, size and visibility. An object you edited is marked and starts unticked.");
        Block(RefreshRow.Deleted, "Deleted by you", "You deleted these. Tick one to restore it.");
        Block(RefreshRow.Skipped, "Skipped", "These did not fit in the map and are offered again next time.");
    }

    /// <summary>Apply selected (the ticked rows) or Apply all I haven't edited (every unedited change, plus the deleted ones you ticked): one undo step, then the result line.</summary>
    void RunRefresh(bool all)
    {
        var apply = _refreshModel.ApplyList(onlyDeleted: all);
        var r = _plugin.ApplyRefresh(apply, all);
        if (r.Ok) { _refreshResult.Text = RefreshModel.ResultLine(r.Refresh); _refreshResult.Visible = true; }
    }

    /// <summary>Test aid: presses Apply selected / Apply all I haven't edited.</summary>
    public void TestRefreshPress(bool all)
    {
        if (_refreshDialog is null) return;
        if (all) _refreshDialog.EmitSignal(AcceptDialog.SignalName.CustomAction, "all");
        else _refreshDialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
    }

    // ---- Object tab: wall shape ------------------------------------------------------------------------------------------------------------------------------------

    VBoxContainer? _wallShapeBox;
    readonly OptionButton _wallShape = new() { Name = WallShapeName, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "A box, or a round wall (a cylinder: scale X and Y are the diameter, Z the height)." };
    public Control? WallShapeBox => _wallShapeBox;
    public OptionButton WallShapeButton => _wallShape;

    void WallShapeSection(VBoxContainer v)
    {
        _wallShapeBox = new VBoxContainer { Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _wallShapeBox.AddChild(new HSeparator());
        _wallShapeBox.AddChild(Head("Wall shape"));
        _wallShapeBox.AddChild(Hint("An invisible wall (players only) is a box or a cylinder. Scale sets its size."));
        _wallShape.AddItem("Box"); _wallShape.AddItem("Cylinder");
        _wallShape.ItemSelected += i =>
        {
            if (_refreshing || _plugin.Session is null) return;
            var doc = _plugin.Session.Document;
            string shape = i == 1 ? PieceSpec.CylinderShape : PieceSpec.BoxShape;
            var ops = _plugin.SelectedIds().Where(id => doc.Get(id).Piece is { Kind: PieceKind.MoveWall }).Select(id => MapmakerPlugin.Op("set-piece", ("id", id), ("shape", shape))).ToArray();
            if (ops.Length > 0) _plugin.Do("Wall shape", ops);
        };
        _wallShapeBox.AddChild(Row(Field("Shape", 56), _wallShape));
        v.AddChild(_wallShapeBox);
    }

    /// <summary>Called from Refresh(): shown only while the first selected object is an invisible wall (players only).</summary>
    void RefreshWallShape(MapObject? first)
    {
        if (_wallShapeBox is null) return;
        bool wall = first?.Piece is { Kind: PieceKind.MoveWall };
        _wallShapeBox.Visible = wall;
        if (wall) _wallShape.Select(first!.Piece!.IsCylinder ? 1 : 0);
    }

    // ---- Scene tab: hidden by Classic -------------------------------------------------------------------------------------------------------------------------------

    readonly CheckBox _hiddenFilter = new() { Name = HiddenFilterName, Text = "Only objects hidden by Classic",
        TooltipText = "Lists only the imported objects Classic hides in play. They come in switched off (greyed); select one and use Toggle visibility on the Object tab to bring it back." };
    static readonly Color OffColour = new(0.5f, 0.52f, 0.56f);

    void HiddenFilterRow(VBoxContainer v)
    {
        _hiddenFilter.Toggled += _ => { _sceneVersion = -1; _sceneShape = "\0"; RefreshScene(); };
        v.AddChild(_hiddenFilter);
    }

    /// <summary>The Objects list text: the name and id, and for an object that is switched off or that Classic hides, a plain note.</summary>
    static string TreeText(MapObject o) =>
        RowText(o) + (o.Src is { ClassicHidden: true } ? (o.Visible ? "  [Classic hides it; on]" : "  [hidden by Classic]") : o.Visible ? "" : "  [off]");

    public bool HiddenFilterOn { get => _hiddenFilter.ButtonPressed; set => _hiddenFilter.ButtonPressed = value; }
}
