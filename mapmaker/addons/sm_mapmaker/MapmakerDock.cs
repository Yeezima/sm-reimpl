using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using SmMapmaker;
using Num = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>An item list whose rows can be dragged; a drop that no control accepts over the 3D viewport places the item there.</summary>
public partial class LibraryList : ItemList
{
    public Func<int, LibraryItem?>? ItemAt;
    public Action<LibraryItem, Vector2>? DroppedOutside;
    LibraryItem? _dragged;

    public override Variant _GetDragData(Vector2 atPosition)
    {
        int i = GetItemAtPosition(atPosition, true);
        if (i < 0 || ItemAt?.Invoke(i) is not { } item) return default;
        _dragged = item;
        SetDragPreview(new Label { Text = item.Label });
        return Variant.From(item.Key);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationDragEnd && _dragged is { } item)
        {
            _dragged = null;
            if (!GetViewport().GuiIsDragSuccessful()) DroppedOutside?.Invoke(item, GetViewport().GetMousePosition());
        }
    }
}

/// <summary>The Mapmaker dock, in the order of the work: Map (files, modes, snap, SM1 reference), Library (place and import), Build (gameplay pieces), Object (the selection), Prefab, Test (check, play settings, share), with the mouse tool and Undo in a bar above and Play test always in the footer.</summary>
[Tool]
public partial class MapmakerDock : VBoxContainer
{
    readonly MapmakerPlugin _plugin;
    bool _refreshing;
    TabContainer? _tabs;

    /// <summary>Brings the dock to the front of its slot and shows one of its tabs (see <see cref="DockTab"/>).</summary>
    public void ShowTab(int index)
    {
        if (_tabs is not null) _tabs.CurrentTab = index;
        if (GetParent() is Control wrapper && wrapper.GetParent() is TabContainer slot) slot.CurrentTab = wrapper.GetIndex();
    }

    Label _title = new(), _status = new();
    LineEdit _sm1Map = new() { Text = "mp_tdm_09" };
    CheckBox _backdrop = new() { Text = "Show SM1 map", ButtonPressed = true };
    Label _sm1Info = new();
    Button _toolSelect = new() { Text = "Select", ToggleMode = true }, _toolPick = new() { Text = "Pick SM1", ToggleMode = true }, _toolPlace = new() { Text = "Place", ToggleMode = true };
    SpinBox _grid = new() { MinValue = 0, MaxValue = 100, Step = 0.05, Value = 0.25 }, _angle = new() { MinValue = 0, MaxValue = 180, Step = 1, Value = 15 };

    // library
    LineEdit _search = new() { PlaceholderText = "Filter" };
    OptionButton _source = new();
    LibraryList _list = new() { IconMode = ItemList.IconModeEnum.Top, MaxTextLines = 2, FixedIconSize = new Vector2I(Thumbnails.Size / 2, Thumbnails.Size / 2), SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 220) };
    List<LibraryItem> _items = new();
    Label _armed = new();
    Label _libInfo = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(280, 0) };
    readonly Dictionary<string, string> _sigs = new();          // thumbnail content signature per item key (asset hash, prefab text hash)
    readonly Dictionary<string, string> _reasons = new();       // why a row shows a placeholder
    readonly HashSet<string> _requested = new();
    int _background;                                            // next row the background pass looks at
    double _thumbClock;
    int _hiddenEmpty;
    LineEdit _renameEdit = new() { Visible = false, SelectAllOnFocus = true };
    PopupMenu _rowMenu = new();
    int _renameIndex = -1;
    bool _importing;

    // object
    LineEdit _name = new();
    readonly SpinBox[] _pos = Spins(-100000, 100000, 0.01), _rot = Spins(-360, 360, 0.1), _scl = Spins(0.001, 1000, 0.01);
    OptionButton _space = new();
    Label _objInfo = new();

    // prefab
    OptionButton _prefab = new();
    ItemList _parts = new() { CustomMinimumSize = new Vector2(0, 110) };
    readonly SpinBox[] _ppos = Spins(-100000, 100000, 0.01), _prot = Spins(-360, 360, 0.1), _pscl = Spins(0.001, 1000, 0.01), _porg = Spins(-100000, 100000, 0.01);
    OptionButton _collision = new();
    LineEdit _prefabName = new();
    Label _prefabInfo = new();
    List<PrefabPart> _partList = new();

    ConfirmationDialog? _extract;
    ConfirmationDialog? _wrongTab;
    LibraryItem? _wrongTabItem;
    ConfirmationDialog? _deletePrefab;
    string? _deletePrefabId;
    LibraryItem? _lastRow;
    LineEdit _exName = new(), _exId = new();
    OptionButton _exOrigin = new(), _exCollision = new();
    FileDialog? _files;
    Action<string>? _filePicked;

    static SpinBox[] Spins(double min, double max, double step) =>
        Enumerable.Range(0, 3).Select(_ => new SpinBox { MinValue = min, MaxValue = max, Step = step, AllowGreater = true, AllowLesser = true, CustomMinimumSize = new Vector2(62, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill }).ToArray();

    public MapmakerDock(MapmakerPlugin plugin)
    {
        _plugin = plugin;
        CustomMinimumSize = new Vector2(300, 0);
        var tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _tabs = tabs;
        AddChild(ToolBar());
        AddChild(tabs);
        tabs.AddChild(MapTab()); tabs.SetTabTitle((int)DockTab.Map, "Map");
        tabs.AddChild(LibraryTab()); tabs.SetTabTitle((int)DockTab.Library, "Library");
        tabs.AddChild(BuildTab()); tabs.SetTabTitle((int)DockTab.Build, "Build");
        tabs.AddChild(ObjectTab()); tabs.SetTabTitle((int)DockTab.Object, "Object");
        tabs.AddChild(PrefabTab()); tabs.SetTabTitle((int)DockTab.Prefab, "Prefab");
        tabs.AddChild(TestTab()); tabs.SetTabTitle((int)DockTab.Test, "Test");
        AddChild(Footer());
        // Compact tab titles so all six fit a narrow dock, and drop-downs that clip a long entry instead of widening the dock.
        foreach (var o in FindChildren("*", "OptionButton", true, false)) if (o is OptionButton ob) ob.ClipText = true;
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart; _status.CustomMinimumSize = new Vector2(280, 0);
        AddChild(_status);
        _plugin.DocChanged += Refresh;
        SetProcess(true);
        _plugin.StatusChanged += s => _status.Text = s;
        MapHost.Sm1Changed += () => { RebuildLibrary(); Refresh(); };
        MapHost.PickedChanged += Refresh;
        MapHost.LibraryChanged += () => { RebuildLibrary(); RefreshPrefabs(); };
        Refresh();
    }

    // ---- layout helpers ----

    static Button Btn(string text, Action on, string? tip = null) { var b = new Button { Text = text, TooltipText = tip ?? "" }; b.Pressed += on; return b; }
    static HBoxContainer Row(params Control[] c) { var h = new HBoxContainer(); foreach (var x in c) h.AddChild(x); return h; }
    static HBoxContainer VecRow(string label, SpinBox[] s) => Row(Field(label, 56), s[0], s[1], s[2]);
    static Label Head(string t) => new() { Text = t, ThemeTypeVariation = "HeaderSmall" };

    // One text style per level (owner's rule): tab titles are Godot's, a section is a separator and a Head, a field has a plain Label, a hint is a muted small Label.
    static readonly Color HintColour = new(0.66f, 0.7f, 0.76f);
    static Label Hint(string t)
    {
        var l = new Label { Text = t, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(250, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        l.AddThemeColorOverride("font_color", HintColour); l.AddThemeFontSizeOverride("font_size", 12);
        return l;
    }
    static Label Field(string t, int width = 56) => new() { Text = t, CustomMinimumSize = new Vector2(width, 0) };

    /// <summary>Starts a titled section on a page: a rule above it (not the first) and the heading, then an optional one-line hint.</summary>
    static void Section(VBoxContainer v, string title, string? hint = null)
    {
        if (v.GetChildCount() > 0) v.AddChild(new HSeparator());
        v.AddChild(Head(title));
        if (hint is not null) v.AddChild(Hint(hint));
    }

    /// <summary>A tab's page: the sections stack in a column (the tab wraps it in a scroll when it is taller than the dock).</summary>
    VBoxContainer Page() => new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };

    // ---- Mouse tool bar (above the tabs) and the Map tab's Snap and SM1 sections ----

    /// <summary>The mouse tool and Undo, always in reach: what a left click does in the 3D view.</summary>
    Control ToolBar()
    {
        _toolSelect.Pressed += () => _plugin.SetTool(Tool.Select);
        _toolPick.Pressed += () => _plugin.SetTool(Tool.PickSm1);
        _toolPlace.Pressed += () => _plugin.SetTool(Tool.Place);
        _toolSelect.TooltipText = "Left click selects objects, and the gizmo moves them.";
        _toolPick.TooltipText = "Left click picks parts of the SM1 map (Shift or Ctrl adds). Then extract them to a prefab on the Prefab tab.";
        _toolPlace.TooltipText = "Left click places the armed Library row or Build piece.";
        foreach (var b in new[] { _toolSelect, _toolPick, _toolPlace }) b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var undo = Btn("Undo", () => EditorInterface.Singleton.GetEditorUndoRedo()?.Call("undo"), "Editor undo (Ctrl+Z)");
        return Row(_toolSelect, _toolPick, _toolPlace, undo);
    }

    void SnapSection(VBoxContainer v)
    {
        Section(v, "Snap", "Applies to placing and gizmo moves; typed values are never snapped.");
        var gridOn = new CheckBox { Text = "Grid (m)", ButtonPressed = true, CustomMinimumSize = new Vector2(110, 0) }; gridOn.Toggled += on => MapHost.SnapGrid = on;
        var angOn = new CheckBox { Text = "Angle (deg)", ButtonPressed = true, CustomMinimumSize = new Vector2(110, 0) }; angOn.Toggled += on => MapHost.SnapAngle = on;
        _grid.ValueChanged += x => MapHost.GridStep = (float)x; _angle.ValueChanged += x => MapHost.AngleStep = (float)x;
        _grid.SizeFlagsHorizontal = SizeFlags.ExpandFill; _angle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(Row(gridOn, _grid)); v.AddChild(Row(angOn, _angle));
    }

    void Sm1Section(VBoxContainer v)
    {
        Section(v, "SM1 reference map", "Shows an original map as a backdrop to place against and pick from.");
        _sm1Map.SizeFlagsHorizontal = SizeFlags.ExpandFill; _sm1Map.CustomMinimumSize = new Vector2(90, 0);
        v.AddChild(Row(_sm1Map, Btn("Open SM1 map", OpenSm1, "Reads the map from your game install.")));
        v.AddChild(_backdrop);
        _backdrop.Toggled += on => { if (MapHost.Root is { } r) r.BackdropVisible = on; };
        _sm1Info.AutowrapMode = TextServer.AutowrapMode.WordSmart; _sm1Info.CustomMinimumSize = new Vector2(250, 0);
        _sm1Info.AddThemeColorOverride("font_color", HintColour); _sm1Info.AddThemeFontSizeOverride("font_size", 12);
        v.AddChild(_sm1Info);
        v.AddChild(Btn("Import SM1 map for editing", () => _ = _plugin.ImportSm1ForEditing(_sm1Map.Text), "Copies every piece of this original map into your map as editable objects, grouped under a node named after it (Scene tree). Save as... keeps it as a new map; the file holds references only."));
        v.AddChild(Btn("Frame SM1 map",() => { if (MapHost.Sm1 is { } s && s.BoundsCore() is { } b) _ = _plugin.Frame3D(b.Min, b.Max); }, "Moves the 3D view to show the whole SM1 map."));
    }

    void NewMap()
    {
        if (!_plugin.EnsureSceneRoot()) { _status.Text = "Opening the Mapmaker scene; press New again."; return; }
        _plugin.NewMap("Untitled");
    }

    void OpenMap()
    {
        Pick("Open .smmap", FileDialog.FileModeEnum.OpenFile, MapHost.Library.MapsDir, ["*.smmap ; Mapmaker map"], p => { if (_plugin.EnsureSceneRoot()) _plugin.OpenMap(p); });
    }

    void SaveMap(bool asNew)
    {
        if (_plugin.Session is null) return;
        if (asNew || _plugin.Session.Path is null)
            Pick("Save .smmap", FileDialog.FileModeEnum.SaveFile, MapHost.Library.MapsDir, ["*.smmap ; Mapmaker map"], p => _plugin.SaveMap(p.EndsWith(".smmap") ? p : p + ".smmap"));
        else _plugin.SaveMap();
    }

    void Pick(string title, FileDialog.FileModeEnum mode, string dir, string[] filters, Action<string> done)
    {
        if (_files is null) { _files = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, Exclusive = true, InitialPosition = Window.WindowInitialPosition.CenterMainWindowScreen, Size = new Vector2I(700, 460) }; _files.FileSelected += p => _filePicked?.Invoke(p); _files.DirSelected += p => _filePicked?.Invoke(p); AddChild(_files); }
        _files.Title = title; _files.FileMode = mode; _files.Filters = filters;
        try { System.IO.Directory.CreateDirectory(dir); _files.CurrentDir = dir; } catch (Exception) { }
        _filePicked = done;
        _files.Popup();
    }

    /// <summary>Opens an SM1 map by name (EditorControl's resume after a restart).</summary>
    public void OpenSm1(string map, bool shown)
    {
        _sm1Map.Text = map;
        _backdrop.SetPressedNoSignal(shown);
        OpenSm1(shown);
    }

    public void OpenSm1() => OpenSm1(true);

    async void OpenSm1(bool shown)
    {
        string map = _sm1Map.Text.Trim();
        if (!_plugin.EnsureSceneRoot()) { _status.Text = "Opening the Mapmaker scene; press Open SM1 map again."; return; }
        _status.Text = $"Reading {map} from the install...";
        try
        {
            var source = await Task.Run(() => Sm1Source.Load(map));
            var notes = new List<string>();
            MapHost.Root!.ShowBackdrop(source, notes);
            MapHost.Root.BackdropVisible = shown;
            MapHost.Register(source);
            if (source.BoundsCore() is { } b) _ = _plugin.Frame3D(b.Min, b.Max);
            _plugin.SetTool(Tool.PickSm1);
            _status.Text = $"{map}: {source.Scene.Objects.Count} placements, {source.Scene.Definitions.Count} definitions. {notes.Count} view notes.";
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { _status.Text = $"Could not open {map}: {e.Message}"; }
    }

    // ---- Library tab ----

    Control LibraryTab()
    {
        var v = Page();
        v.SizeFlagsVertical = SizeFlags.ExpandFill;
        Section(v, "Library", "Click a row, then click in the 3D view (Esc or right click stops). Rows can also be dragged into the view.");
        _libInfo.AutowrapMode = TextServer.AutowrapMode.WordSmart; _libInfo.CustomMinimumSize = new Vector2(250, 0);
        _libInfo.AddThemeColorOverride("font_color", HintColour); _libInfo.AddThemeFontSizeOverride("font_size", 12);
        _source.AddItem("SM1 definitions"); _source.AddItem("Custom assets"); _source.AddItem("Prefabs");
        _source.ItemSelected += _ => RebuildLibrary();
        _search.TextChanged += _ => RebuildLibrary(); _search.SizeFlagsHorizontal = SizeFlags.ExpandFill; _search.CustomMinimumSize = new Vector2(70, 0);
        v.AddChild(Row(_source, _search));
        _list.ItemAt = i => i >= 0 && i < _items.Count ? _items[i] : null;
        _list.DroppedOutside = (item, pos) => { if (_plugin.OverViewport(pos)) MapHost.Armed = item; if (_plugin.OverViewport(pos) && _plugin.ReadyToPlace() && _plugin.TryRay(pos, out var o, out var d)) _plugin.PlaceAt(item, _plugin.GamePointOnRay(o, d)); };
        _list.ItemSelected += i => { _lastRow = _items[(int)i]; Arm(_items[(int)i]); };
        _list.ItemActivated += i => { if (_items[(int)i].Kind is LibraryKind.Asset or LibraryKind.Prefab) BeginRename((int)i); else Arm(_items[(int)i]); };   // double click edits the name; SM1 rows are read-only
        _list.ItemClicked += (i, pos, button) =>
        {
            if ((MouseButton)button != MouseButton.Right || i < 0 || i >= _items.Count) return;
            _list.Select((int)i); _lastRow = _items[(int)i];
            _rowMenu.Clear();
            bool editable = _items[(int)i].Kind is LibraryKind.Asset or LibraryKind.Prefab;
            _rowMenu.AddItem(editable ? "Rename...   (F2)" : "Rename (SM1 definitions are read-only)", 1);
            _rowMenu.SetItemDisabled(0, !editable);
            _rowMenu.Position = (Vector2I)DisplayServer.MouseGetPosition(); _rowMenu.Popup();
        };
        _list.GuiInput += e => { if (e is InputEventKey { Pressed: true, Keycode: Key.F2 } && _list.GetSelectedItems() is { Length: > 0 } sel) { BeginRename(sel[0]); _list.AcceptEvent(); } };
        _rowMenu.IdPressed += id => { if (id == 1 && _list.GetSelectedItems() is { Length: > 0 } sel) BeginRename(sel[0]); };
        AddChild(_rowMenu);
        _renameEdit.TextSubmitted += CommitRename;
        _renameEdit.FocusExited += () => { if (_renameIndex >= 0 && _renameEdit.Visible) EndRename(); };
        _renameEdit.GuiInput += e => { if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { EndRename(); _list.GrabFocus(); _renameEdit.AcceptEvent(); } };
        _list.AddChild(_renameEdit);
        v.AddChild(_list);
        v.AddChild(_armed);
        v.AddChild(_libInfo);
        Section(v, "Import", "Quixel / Fab zip with FBX and textures, FBX, GLB or glTF. You can also drag a file onto the list or the 3D view.");
        var importAsset = Btn("Import asset...", () => Pick("Import asset", FileDialog.FileModeEnum.OpenFile, "", ["*.zip,*.fbx,*.glb,*.gltf ; Quixel / Fab download or model"], p => ImportPaths([p])), "A .zip, .fbx, .glb or .gltf file.");
        var importFolder = Btn("Import folder...", () => Pick("Import asset folder", FileDialog.FileModeEnum.OpenDir, "", [], p => ImportPaths([p])), "An unzipped Quixel / Fab download.");
        importAsset.SizeFlagsHorizontal = SizeFlags.ExpandFill; importFolder.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(Row(importAsset, importFolder));
        v.AddChild(Btn("Import prefab...", () => Pick("Import .smprefab", FileDialog.FileModeEnum.OpenFile, "", ["*.smprefab ; Mapmaker prefab"], p => ImportOp("import-prefab", p)), "A .smprefab file from another map maker."));
        Section(v, "Remove");
        v.AddChild(Btn("Delete selected prefab...", () =>
        {
            if (_source.Selected != 2 || _lastRow is not { Kind: LibraryKind.Prefab } row || !_items.Contains(row)) { _status.Text = "Choose Prefabs in the list above, click the prefab to delete, then press Delete selected prefab."; return; }
            AskDeletePrefab(row.Id);
        }, "Deletes the prefab chosen in the list above (it asks first)."));
        return v;
    }

    void Arm(LibraryItem item)
    {
        MapHost.Armed = item; MapHost.Tool = Tool.Place;
        _plugin.NotifyDoc();
    }

    void ImportOp(string op, string path)
    {
        if (_plugin.Session is null) { _status.Text = "Open or create a map first."; return; }
        var r = _plugin.Do(op == "import-asset" ? "Import asset" : "Import prefab", MapmakerPlugin.Op(op, ("path", path)));
        if (r.Ok) MapHost.NotifyLibraryChanged();
    }

    readonly Dictionary<string, int> _rowOf = new();

    static string ShortHash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Everything that changes how a prefab looks: its file text and the content hash of every custom asset it draws (so a changed asset renews the picture).</summary>
    static string PrefabSignature(string id)
    {
        try
        {
            if (MapHost.Library.GetPrefab(id) is not { } p) return "";
            var text = new System.Text.StringBuilder(p.ToJson());
            foreach (var part in p.Parts.Where(x => x.Asset is { IsSm1: false }).Select(x => x.Asset!.Id!).Distinct().Order(StringComparer.Ordinal))
                text.Append('|').Append(part).Append(':').Append(MapHost.Library.GetAsset(part)?.Sha256 ?? "");
            return ShortHash(text.ToString());
        }
        catch (MapException) { return ""; }
    }

    void RebuildLibrary()
    {
        EndRename();
        _list.Clear(); _items = new(); _sigs.Clear(); _reasons.Clear(); _requested.Clear(); _rowOf.Clear(); _background = 0; _hiddenEmpty = 0;
        MapmakerPlugin.Thumbs?.ClearPending();
        string f = _search.Text.Trim();
        bool Match(string s) => f.Length == 0 || s.Contains(f, StringComparison.OrdinalIgnoreCase);
        switch (_source.Selected)
        {
            case 0:
                if (MapHost.Sm1 is { } src)
                    foreach (var name in src.DefinitionNames.Where(Match))
                    {
                        // Definitions with no visual part (markers, lights, groups of child references) cannot be placed as something visible: counted, not listed.
                        if (!src.HasDefinition(name)) { _hiddenEmpty++; continue; }
                        _items.Add(new LibraryItem(LibraryKind.Sm1, name, name, src.Map));
                    }
                break;
            case 1:
                try
                {
                    var skipped = new List<string>();
                    foreach (var a in MapHost.Library.ListAssets(skipped).Where(a => Match(a.Name) || Match(a.Id)).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Id, StringComparer.Ordinal))
                    {
                        var item = new LibraryItem(LibraryKind.Asset, a.Id, a.Name);
                        _items.Add(item); _sigs[item.Key] = a.Sha256;
                    }
                    if (skipped.Count > 0) _status.Text = $"{skipped.Count} asset(s) skipped: {string.Join("; ", skipped.Take(2))}";
                }
                catch (MapException) { }
                break;
            default:
                try
                {
                    var skipped = new List<string>();
                    foreach (var p in MapHost.Library.ListPrefabs(skipped).Where(p => Match(p.Name) || Match(p.Id)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal))
                    {
                        var item = new LibraryItem(LibraryKind.Prefab, p.Id, p.Name);
                        _items.Add(item); _sigs[item.Key] = PrefabSignature(p.Id);
                    }
                    if (skipped.Count > 0) _status.Text = $"{skipped.Count} prefab file(s) skipped: {string.Join("; ", skipped.Take(2))}";
                }
                catch (MapException) { }
                break;
        }
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            int row = _list.AddItem(item.Label, MapmakerPlugin.Thumbs?.Cached(item.Key, SigOf(item)));
            _list.SetItemTooltip(row, item.Kind is LibraryKind.Sm1 ? item.Id : $"{item.Label}  ({item.Id})");
            _rowOf[item.Key] = row;
        }
        _libInfo.Text = $"{_items.Count} item(s)" + (_hiddenEmpty > 0 ? $"; {_hiddenEmpty} definition(s) without a visual part (markers, lights, groups) are not listed" : "")
            + (_source.Selected is 1 or 2 ? ". Double-click a name or press F2 to rename." : "") + (Thumbnails.CanRender ? "" : " (No previews in headless runs.)");
    }

    string SigOf(LibraryItem item) => _sigs.TryGetValue(item.Key, out var s) ? s : "";

    /// <summary>Called a few times a second: the rows in view get their pictures first, then the rest are filled in the background (a few at a time, so the editor stays responsive).</summary>
    public override void _Process(double delta)
    {
        if ((_thumbClock += delta) < 0.15) return;
        _thumbClock = 0;
        if (MapmakerPlugin.Thumbs is not { } th || !Thumbnails.CanRender || _items.Count == 0 || _tabs is null || _tabs.CurrentTab != (int)DockTab.Library || !IsVisibleInTree()) return;
        var visible = new SortedSet<int>();
        var size = _list.Size;
        for (float y = 4; y < size.Y; y += 20)
            for (float x = 4; x < size.X; x += 24)
                if (_list.GetItemAtPosition(new Vector2(x, y), false) is var i and >= 0) visible.Add(i);
        foreach (var i in visible) RequestRow(i, true);
        if (th.Pending < 4)
            for (int n = 0; n < 6 && _background < _items.Count;)
                if (RequestRow(_background++, false)) n++;
    }

    bool RequestRow(int row, bool urgent)
    {
        if (row < 0 || row >= _items.Count || _list.GetItemIcon(row) is not null) return false;
        var item = _items[row];
        bool first = _requested.Add(item.Key);
        if (!first && !urgent) return false;
        RequestThumbnail(item, urgent);
        return first;
    }

    void RequestThumbnail(LibraryItem item, bool urgent = true)
    {
        MapmakerPlugin.Thumbs?.Request(item.Key, SigOf(item), () => BuildThumb(item), (tex, reason) =>
        {
            if (!_rowOf.TryGetValue(item.Key, out int idx) || idx >= _list.ItemCount || _list.GetItemText(idx) != item.Label) return;
            _list.SetItemIcon(idx, tex);
            if (reason is not null)
            {
                _reasons[item.Key] = reason;
                _list.SetItemTooltip(idx, $"{(item.Kind is LibraryKind.Sm1 ? item.Id : $"{item.Label}  ({item.Id})")}\nNo preview: {reason}");
            }
        }, urgent);
    }

    /// <summary>A reason for the placeholder icon from what the factory noted while building.</summary>
    static string ReasonFromNotes(IReadOnlyList<string> notes)
    {
        string n = notes.FirstOrDefault() ?? "";
        if (n.Contains("not loaded yet", StringComparison.Ordinal)) return "map not loaded";
        if (n.Contains("has no mesh", StringComparison.Ordinal) || n.Contains("nothing to draw", StringComparison.Ordinal)) return "no mesh";
        if (n.Contains("not in the library", StringComparison.Ordinal)) return "missing asset";
        return "load failed";
    }

    Thumbnails.ThumbBuild BuildThumb(LibraryItem item)
    {
        var notes = new List<string>();
        try
        {
            if (item.Kind == LibraryKind.Sm1)
            {
                if (MapHost.Sm1For(item.Map!) is not { } src) return new(null, default, "map not loaded");
                var sm1 = src.Instantiate(item.Id);
                if (sm1 is null)
                {
                    string why = ContentFactory.WhySm1Empty(src, item.Id);
                    // Left out of the map on purpose, but it is a real model: draw it for the library anyway.
                    if (why == "hidden decor" && ContentFactory.BuildHiddenSm1Preview(src, item.Id) is { } hidden) return new(hidden, ContentFactory.MeasureBounds(hidden));
                    return new(null, default, why);
                }
                return new(sm1, ContentFactory.MeasureBounds(sm1));   // measured from what is drawn: the definition's stored box follows another LOD rule
            }
            var parts = new List<ResolvedPart>();
            if (item.Kind == LibraryKind.Asset) parts.Add(new ResolvedPart("t", null, item.Label, AssetRef.Custom(item.Id), Num.Matrix4x4.Identity, true, null));
            else
            {
                if (MapHost.Library.GetPrefab(item.Id) is null) return new(null, default, "unreadable");
                var ctx = new MapContext(MapHost.Library, null);
                var doc = MapDocument.Create("t");
                doc.Objects.Add(new MapObject("o1", item.Label, null, "default", true, MapTransform.Identity, null, item.Id, []));
                parts.AddRange(MapResolver.Flatten(doc, ctx));
                if (parts.Count == 0) return new(null, default, "empty prefab");
            }
            var node = ContentFactory.BuildObject(parts, Num.Matrix4x4.Identity, out var bounds, notes);
            // A part whose SM1 map is not loaded yet leaves a hole: do not cache that picture (the placeholder is shown and renewed when the map loads).
            if (notes.Any(n => n.Contains("not loaded yet", StringComparison.Ordinal))) return new(node, bounds, "map not loaded");
            if (node is null) return new(null, default, notes.Count > 0 ? ReasonFromNotes(notes) : "no mesh");
            if (bounds.Size.LengthSquared() < 1e-10f && notes.Count > 0) return new(node, bounds, ReasonFromNotes(notes));
            return new(node, bounds);
        }
        catch (MapException e) { return new(null, default, e.Code is "unknown-asset" ? "missing asset" : e.Code is "unknown-prefab" ? "missing prefab" : "unreadable"); }
    }

    // ---- rename (custom assets and prefabs; SM1 definitions are read-only) ----

    void BeginRename(int row)
    {
        if (row < 0 || row >= _items.Count) return;
        var item = _items[row];
        if (item.Kind is not (LibraryKind.Asset or LibraryKind.Prefab)) { _status.Text = "SM1 definitions are read-only and cannot be renamed. Extract them to a prefab to get an item you can name."; return; }
        _renameIndex = row;
        var rect = _list.GetItemRect(row);
        _renameEdit.Text = item.Label;
        _renameEdit.Position = new Vector2(Mathf.Max(0, rect.Position.X - 2), rect.End.Y - 30);
        _renameEdit.Size = new Vector2(Mathf.Max(rect.Size.X + 4, 150), 28);
        _renameEdit.Visible = true;
        _renameEdit.GrabFocus();
        _renameEdit.SelectAll();
        _status.Text = $"Renaming '{item.Label}': Enter confirms, Esc cancels.";
    }

    void EndRename()
    {
        if (_renameIndex < 0) return;
        _renameIndex = -1;
        _renameEdit.Visible = false;
    }

    void CommitRename(string text)
    {
        if (_renameIndex < 0 || _renameIndex >= _items.Count) { EndRename(); return; }
        var item = _items[_renameIndex];
        try
        {
            string name = item.Kind == LibraryKind.Asset ? MapHost.Library.RenameAsset(item.Id, text) : MapHost.Library.RenamePrefab(item.Id, text);
            EndRename();
            _status.Text = name == item.Label ? $"'{name}' is unchanged." : $"Renamed '{item.Label}' to '{name}'. Maps keep using it (they refer to its id, {item.Id}).";
            MapHost.NotifyLibraryChanged();
            if (_items.FindIndex(x => x.Key == item.Key) is var at and >= 0) { _list.Select(at); _list.EnsureCurrentIsVisible(); _lastRow = _items[at]; }
        }
        catch (MapException e) { _status.Text = e.Message; _renameEdit.GrabFocus(); }   // stays open so the name can be fixed
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _status.Text = $"Could not save the new name: {e.Message}"; }
    }

    // ---- test aids (LibraryTest) ----

    public override void _Ready()
    {
        // Compact tab titles so all six fit a narrow dock; taken here, in the editor tree, so the boxes come from the editor theme.
        if (_tabs is { } tabs)
        {
            foreach (var name in new[] { "tab_selected", "tab_unselected", "tab_hovered", "tab_disabled", "tab_focus" })
                if (tabs.GetThemeStylebox(name) is { } box) { var narrow = (StyleBox)box.Duplicate(); narrow.ContentMarginLeft = 5; narrow.ContentMarginRight = 5; tabs.AddThemeStyleboxOverride(name, narrow); }
            tabs.AddThemeFontSizeOverride("font_size", 14);
        }
                if (LibraryTest.Wanted) GetTree().CreateTimer(3.0).Timeout += () => _ = new LibraryTest(_plugin, this).Run();
    }

    public void TestShowSource(int source) { _source.Select(source); _search.Text = ""; RebuildLibrary(); }

    /// <summary>Rows, rows that show a picture (real or placeholder) and rows that show a placeholder.</summary>
    public (int Rows, int WithPicture, int Placeholders) LibraryStats() =>
        (_items.Count, Enumerable.Range(0, _items.Count).Count(i => _list.GetItemIcon(i) is not null), _reasons.Count);

    public int TestRowOf(string id) => _items.FindIndex(x => x.Id == id);

    public string TestRename(int row, string text) { BeginRename(row); CommitRename(text); var m = _status.Text; EndRename(); return m; }

    // ---- import (drag and drop, buttons) ----

    Window.FilesDroppedEventHandler? _dockDrop, _mainDrop;
    Window? _dockWindow, _mainWindow;

    public override void _EnterTree()
    {
        base._EnterTree();
        // The dock's own window, and the editor's main window too when the dock floats; each event is judged in its own window.
        _dockWindow = GetWindow();
        _dockWindow.FilesDropped += _dockDrop = f => OnFilesDropped(_dockWindow, f);
        var main = EditorInterface.Singleton.GetBaseControl()?.GetWindow();
        if (main is not null && main != _dockWindow) { _mainWindow = main; main.FilesDropped += _mainDrop = f => OnFilesDropped(main, f); }
    }

    public override void _ExitTree()
    {
        if (_dockDrop is not null && _dockWindow is not null && GodotObject.IsInstanceValid(_dockWindow)) _dockWindow.FilesDropped -= _dockDrop;
        if (_mainDrop is not null && _mainWindow is not null && GodotObject.IsInstanceValid(_mainWindow)) _mainWindow.FilesDropped -= _mainDrop;
        _dockDrop = _mainDrop = null;
        base._ExitTree();
    }

    /// <summary>The mouse in a window's own (content-scaled) coordinates, the frame the controls' global rects use. The window's own mouse position can be stale while
    /// the OS drags a file over it (no motion events), so this reads the screen position.</summary>
    static Vector2 LocalMouse(Window w)
    {
        var screen = DisplayServer.MouseGetPosition();
        return w.GetFinalTransform().AffineInverse() * new Vector2(screen.X - w.Position.X, screen.Y - w.Position.Y);
    }

    void OnFilesDropped(Window? window, string[] files)
    {
        if (window is null) return;
        var mine = files.Where(f => AssetImporter.IsImportable(f) || f.EndsWith(".smprefab", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (mine.Length == 0) return;
        // Only a drop onto the Mapmaker dock or the 3D view is ours; one onto Godot's FileSystem dock or any other panel is Godot's.
        var at = LocalMouse(window);
        bool overDock = window == GetWindow() && IsVisibleInTree() && GetGlobalRect().HasPoint(at);
        bool overView = window == EditorInterface.Singleton.GetBaseControl()?.GetWindow() && _plugin.OverViewport(at);
        if (!overDock && !overView) { Report("Drop ignored: drop files on the Mapmaker panel or the 3D view to import them."); return; }
        HandleDrop(mine);
    }

    /// <summary>What a drop does (also the test aid's entry): every .zip, .fbx, .glb, .gltf, folder or .smprefab is imported with no further prompt.</summary>
    public void HandleDrop(string[] files) { ShowTab((int)DockTab.Library); ImportPaths(files); }

    /// <summary>The asset imported last: its id and the message shown (test aids read these).</summary>
    public string? LastImportId { get; private set; }
    public string LastImportMessage { get; private set; } = "";
    public bool Importing => _importing;

    async void ImportPaths(string[] paths)
    {
        if (_plugin.Session is null) { Report("Open or create a map first (Map tab: New map), then import again."); return; }
        if (_importing) { Report("An import is still running; drop the file again when it is done."); return; }
        _importing = true;
        var messages = new List<string>();
        try
        {
            foreach (var path in paths)
            {
                string label = System.IO.Path.GetFileName(path.TrimEnd('/', '\\'));
                if (path.EndsWith(".smprefab", StringComparison.OrdinalIgnoreCase))
                {
                    try { ImportOp("import-prefab", path); messages.Add($"Imported prefab {label}"); }
                    catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { messages.Add($"Could not import {label}: {e.Message}"); }
                    continue;
                }
                _status.Text = $"Importing {label}...";
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);   // let the status paint before the load blocks
                if (!IsInstanceValid(this) || !IsInsideTree()) return;
                string staging = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sm-mapmaker-import-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var r = await AssetImporter.Prepare(path, staging);
                    if (!IsInstanceValid(this) || !IsInsideTree()) return;
                    string sha = await Task.Run(() => ModelImport.ContentSha(r.GlbPath));
                    if (!IsInstanceValid(this) || !IsInsideTree()) return;
                    // The same download again: found by what it held, then by the converted content (the conversion is not guaranteed byte-for-byte repeatable).
                    var same = MapHost.Library.ListAssets().FirstOrDefault(x => x.Import?["sourceSha256"]?.ToString() == r.SourceSha || x.Sha256 == sha);
                    string id = same?.Id ?? r.Id;
                    for (int n = 2; same is null && MapHost.Library.GetAsset(id) is not null; n++) id = $"{r.Id}-{n}";
                    bool already = same is not null;
                    if (already) { LastImportId = id; messages.Add($"{r.Name} is already in the library ({id})"); ShowImported(id); continue; }
                    var box = new SmMapmaker.Bounds(new Num.Vector3(r.Bounds.Position.X, r.Bounds.Position.Y, r.Bounds.Position.Z), new Num.Vector3(r.Bounds.End.X, r.Bounds.End.Y, r.Bounds.End.Z));
                    var res = _plugin.Do("Import asset", MapmakerPlugin.Op("import-asset", ("path", r.GlbPath), ("id", id), ("name", r.Name), ("from", r.From), ("bounds", DoubleBox(box)), ("import", r.Meta)));
                    if (!res.Ok) { messages.Add($"Could not import {label}: {res.Errors.FirstOrDefault()?.Message ?? "unknown error"}"); continue; }
                    LastImportId = id;
                    messages.Add(already ? $"{r.Name} is already in the library ({id})" : $"Imported {r.Name} ({r.Summary})");
                    MapHost.NotifyLibraryChanged();
                    ShowImported(id);
                }
                catch (MapException e) { messages.Add($"Could not import {label}: {e.Message}"); }
                catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { GD.PushWarning(e.ToString()); messages.Add($"Could not import {label}: {e.GetType().Name}: {e.Message}"); }
                finally { try { if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true); } catch (Exception) { } }
            }
        }
        finally { if (IsInstanceValid(this)) _importing = false; }
        if (IsInstanceValid(this)) Report(string.Join("; ", messages));
    }

    /// <summary>The box as JSON numbers the operation reader accepts (it reads doubles; a float-valued node is refused).</summary>
    static System.Text.Json.Nodes.JsonObject DoubleBox(SmMapmaker.Bounds b)
    {
        static System.Text.Json.Nodes.JsonArray V(Num.Vector3 v) => new(System.Text.Json.Nodes.JsonValue.Create((double)v.X), System.Text.Json.Nodes.JsonValue.Create((double)v.Y), System.Text.Json.Nodes.JsonValue.Create((double)v.Z));
        return new System.Text.Json.Nodes.JsonObject { ["min"] = V(b.Min), ["max"] = V(b.Max) };
    }

    void Report(string message)
    {
        LastImportMessage = message;
        _status.Text = message;
        GD.Print("Mapmaker import: " + message);
        try { EditorInterface.Singleton.GetEditorToaster()?.PushToast(message, message.Contains("Could not") ? EditorToaster.Severity.Warning : EditorToaster.Severity.Info); } catch (Exception) { }
    }

    /// <summary>Shows Custom assets with the new asset selected (without arming it).</summary>
    void ShowImported(string id)
    {
        _search.Text = "";
        _source.Select(1);
        RebuildLibrary();
        if (_items.FindIndex(x => x.Kind == LibraryKind.Asset && x.Id == id) is var at and >= 0) { _list.Select(at); _list.EnsureCurrentIsVisible(); _lastRow = _items[at]; }
    }

    void ShowExtract()
    {
        if (MapHost.PickedSm1.Count == 0 || MapHost.Sm1 is null) { _status.Text = "Pick SM1 objects first (Map tab: Open SM1 map, then the Pick SM1 tool; Shift or Ctrl click adds)."; return; }
        if (_extract is null)
        {
            _extract = new ConfirmationDialog { Title = "Extract to prefab", Exclusive = true };
            var v = new VBoxContainer();
            v.AddChild(new Label { Text = "Prefab name" }); v.AddChild(_exName);
            v.AddChild(new Label { Text = "Prefab id (lowercase, digits, - _)" }); v.AddChild(_exId);
            _exName.TextChanged += t => _exId.Text = Slug(t);
            _exOrigin.AddItem("Origin: bounds bottom centre"); _exOrigin.AddItem("Origin: first object"); v.AddChild(_exOrigin);
            _exCollision.AddItem("Collision: mesh"); _exCollision.AddItem("Collision: original-cut"); v.AddChild(_exCollision);
            _extract.AddChild(v);
            _extract.Confirmed += DoExtract;
            AddChild(_extract);
        }
        _exName.Text = MapHost.Sm1.ShortName(MapHost.PickedSm1[0]); _exId.Text = Slug(_exName.Text);
        _extract.PopupCentered(new Vector2I(380, 220));
    }

    /// <summary>
    /// A placement click on a plain Godot scene tab (Godot's "+" makes an ordinary scene; Mapmaker maps are edited in the Mapmaker scene).
    /// OK: a new empty map (SM1 backdrop hidden) with the piece placed at the centre. Cancel: switch to the open map; nothing is placed.
    /// </summary>
    public void AskWrongTab(LibraryItem? item)
    {
        if (_wrongTab is { Visible: true }) return;
        if (_wrongTab is null)
        {
            _wrongTab = new ConfirmationDialog { Title = "Not a Mapmaker map", Exclusive = true, OkButtonText = "Add a Mapmaker map here", CancelButtonText = "Cancel" };
            _wrongTab.Confirmed += () => _ = _plugin.AddMapToCurrentScene();   // placement follows the tab being edited; it never switches tabs
            _wrongTab.Canceled += () => _status.Text = "Nothing placed.";
            AddChild(_wrongTab);
        }
        _wrongTabItem = item;
        _wrongTab.DialogText = "This scene tab has no Mapmaker map. Add one here (a Mapmaker node under the scene's root, with its own untitled map), " +
            "then click or drop " + (item is null ? "the item" : item.Label) + " again to place it. The other tabs and their maps are left alone; the SM1 map and the prefab library stay available.";
        _wrongTab.PopupCentered(new Vector2I(460, 200));
    }

    public async Task StartEmptyMap(LibraryItem? item)
    {
        if (MapHost.StartingEmptyMap) return;   // no second new map while the first waits for the scene switch
        MapHost.StartingEmptyMap = true;
        try { await StartEmptyMapCore(item); }
        finally { MapHost.StartingEmptyMap = false; }
    }

    async Task StartEmptyMapCore(LibraryItem? item)
    {
        var s = _plugin.Session;
        if (s is not null && _plugin.Dirty)
        {
            if (s.Path is null) { _plugin.EnsureSceneRoot(); _status.Text = $"The open map \"{s.Name}\" has unsaved changes and no file yet: Save As first, then press New."; return; }
            if (!_plugin.SaveMap()) return;
        }
        _plugin.EnsureSceneRoot();
        for (int i = 0; i < 60 && !(EditorInterface.Singleton.GetEditedSceneRoot() is MapmakerRoot && MapHost.Root is not null); i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (MapHost.Root is null) { _status.Text = "Could not open the Mapmaker scene."; return; }
        _plugin.NewMap("Untitled");
        MapHost.Root.BackdropVisible = false;
        _backdrop.SetPressedNoSignal(false);
        if (item is null) { _status.Text = "New empty map (SM1 backdrop hidden; tick Show SM1 map to see it)."; return; }
        var r = _plugin.PlaceAt(item, Vector3.Zero);
        if (!r.Ok) return;
        Bounds? b = item.Kind == LibraryKind.Prefab ? MapHost.Library.ListPrefabs().FirstOrDefault(p => p.Id == item.Id)?.Bounds : null;
        await _plugin.Frame3D(b?.Min ?? new Num.Vector3(-10, -10, 0), b?.Max ?? new Num.Vector3(10, 10, 4));
        _status.Text = $"New empty map with {item.Label} at the centre (SM1 backdrop hidden; tick Show SM1 map to see it). Save As to keep it.";
    }

    /// <summary>Asks before deleting a prefab from the library; its placed copies in the open map go with it, and Ctrl+Z undoes both.</summary>
    void AskDeletePrefab(string id)
    {
        if (_plugin.Session is null) { _status.Text = "Open or create a map first."; return; }
        if (_deletePrefab is null)
        {
            _deletePrefab = new ConfirmationDialog { Title = "Delete prefab", Exclusive = true, OkButtonText = "Delete" };
            _deletePrefab.Confirmed += () => { if (_deletePrefabId is { } d) { if (_plugin.DeletePrefab(d).Ok) _lastRow = null; } };
            AddChild(_deletePrefab);
        }
        _deletePrefabId = id;
        int n = _plugin.PrefabUsers(id).Count, all = _plugin.PrefabUsersWithChildren(id);
        string name = MapHost.Library.ListPrefabs().FirstOrDefault(p => p.Id == id)?.Name ?? id;
        _deletePrefab.DialogText = $"Delete the prefab \"{name}\" ({id}) from the library?" +
            (n > 0 ? $"\n\nIt is placed {n} time(s) in this map; those copies are deleted too{(all > n ? $", with {all - n} object(s) parented under them" : "")}." : "") +
            "\n\nEdit > Undo (Ctrl+Z) brings it back.";
        _deletePrefab.PopupCentered(new Vector2I(420, 160));
    }

    static string Slug(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s.ToLowerInvariant()) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        return sb.ToString().Trim('-');
    }

    public void DoExtract() => Extract(_exName.Text, _exId.Text, _exOrigin.Selected == 1 ? "first" : "bounds-bottom-center", _exCollision.Selected == 1 ? "original-cut" : "mesh");

    public OpResult Extract(string name, string id, string origin, string collision)
    {
        var src = MapHost.Sm1!;
        // Exact picks: the placement's index in the map's object list (names are not unique).
        var picks = new JsonArray(MapHost.PickedSm1.Distinct().Select(p => (JsonNode)(p.Part >= 0 ? new JsonObject { ["index"] = p.Object, ["part"] = p.Part } : new JsonObject { ["index"] = p.Object })).ToArray());
        var r = _plugin.Do($"Extract {id}", MapmakerPlugin.Op("extract-sm1", ("map", src.Map), ("objects", picks), ("prefab", id), ("name", name), ("origin", origin), ("collision", collision)));
        if (r.Ok) { MapHost.PickedSm1.Clear(); _plugin.RefreshPicked(); MapHost.NotifyLibraryChanged(); _source.Select(2); RebuildLibrary(); }
        return r;
    }

    // ---- Object tab ----

    Control ObjectTab()
    {
        var v = Page();
        Section(v, "Selection", "Click an object in the 3D view. With several selected, edits move all by the change to the first.");
        _objInfo.AutowrapMode = TextServer.AutowrapMode.WordSmart; _objInfo.CustomMinimumSize = new Vector2(250, 0);
        v.AddChild(_objInfo);
        v.AddChild(Row(Field("Name"), _name));
        _name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _name.TextSubmitted += t => { foreach (var id in _plugin.SelectedIds().Take(1)) _plugin.Do("Rename", MapmakerPlugin.Op("rename", ("id", id), ("name", t))); };
        Section(v, "Transform", "Rotation is Euler degrees about X, Y, Z of the SM1 frame (Z up). Gizmo moves are stored on release.");
        _space.AddItem("Local (document values)"); _space.AddItem("World");
        _space.ItemSelected += _ => Refresh();
        v.AddChild(Row(Field("Space"), _space));
        _space.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(VecRow("Position", _pos)); v.AddChild(VecRow("Rotation", _rot)); v.AddChild(VecRow("Scale", _scl));
        foreach (var s in _pos.Concat(_rot).Concat(_scl)) s.ValueChanged += _ => ApplyTransform();
        Section(v, "Actions");
        var dup = Btn("Duplicate", () => _plugin.DuplicateSelected(), "Ctrl+D over the 3D view");
        var vis = Btn("Toggle visibility", () => ToggleVisible(), "Shows or hides the selected object.");
        dup.SizeFlagsHorizontal = SizeFlags.ExpandFill; vis.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(Row(dup, vis));
        v.AddChild(Btn("Group to prefab...", GroupToPrefab, "Turns the selected objects into one reusable prefab."));
        v.AddChild(new HSeparator());
        v.AddChild(Btn("Delete", () => _plugin.DeleteSelected(), "Del over the 3D view"));
        return Scroll(v);
    }

    void ToggleVisible()
    {
        foreach (var id in _plugin.SelectedIds().Take(1))
            _plugin.Do("Visibility", MapmakerPlugin.Op("set-visible", ("id", id), ("visible", !_plugin.Session!.Document.Get(id).Visible)));
    }

    void GroupToPrefab()
    {
        var ids = _plugin.SelectedIds().ToList();
        if (ids.Count == 0) return;
        string name = _plugin.Session!.Document.Get(ids[0]).Name;
        var r = _plugin.Do("Group to prefab", MapmakerPlugin.Op("group-to-prefab", ("ids", MapmakerPlugin.Strs(ids)), ("prefab", Slug(name) + "-group"), ("name", name + " group"), ("replace", true)));
        if (r.Ok) _plugin.Select(r.Created);
    }

    void ApplyTransform()
    {
        if (_refreshing || _plugin.Session is null) return;
        var ids = _plugin.SelectedIds().ToList();
        if (ids.Count == 0) return;
        var doc = _plugin.Session.Document;
        var target = new MapTransform(V(_pos), V(_rot), V(_scl));
        var ops = new List<JsonObject>();
        var first = doc.Get(ids[0]);
        MapTransform firstNow = _space.Selected == 1 ? Safe(() => MapTransform.FromMatrix(doc.World(first.Id))) : first.Transform;
        // Only the selected objects are edited; one whose ancestor is also selected rides on that ancestor's entry (no double turn), a lone selected child turns alone.
        foreach (var id in TransformTargets.Roots(doc, ids))
        {
            var o = doc.Get(id);
            // A multi-selection moves by the delta of the first object's edit; one object takes the typed values.
            MapTransform local;
            if (ids.Count == 1)
            {
                local = target;
                if (_space.Selected == 1 && o.Parent is not null && Num.Matrix4x4.Invert(doc.World(o.Parent), out var inv)) local = Safe(() => MapTransform.FromMatrix(target.ToMatrix() * inv));
            }
            else local = new MapTransform(o.Transform.Position + (target.Position - firstNow.Position), o.Transform.Rotation + (target.Rotation - firstNow.Rotation), o.Transform.Scale * (target.Scale / firstNow.Scale));
            ops.Add(MapmakerPlugin.Op("transform", ("id", id), ("position", MapmakerPlugin.Vec(local.Position)), ("rotation", MapmakerPlugin.Vec(local.Rotation)), ("scale", MapmakerPlugin.Vec(local.Scale))));
        }
        _plugin.Do("Set transform", ops.ToArray());
    }

    static T Safe<T>(Func<T> f) { try { return f(); } catch (MapException) { return default!; } }
    static Num.Vector3 V(SpinBox[] s) => new((float)s[0].Value, (float)s[1].Value, (float)s[2].Value);
    static void Set(SpinBox[] s, Num.Vector3 v) { s[0].SetValueNoSignal(v.X); s[1].SetValueNoSignal(v.Y); s[2].SetValueNoSignal(v.Z); }

    // ---- Prefab tab ----

    Control PrefabTab()
    {
        var v = Page();
        Section(v, "Create", "Pick SM1 objects (Map tab, SM1 reference map), or select objects and use Object > Group to prefab.");
        v.AddChild(Btn("Extract picked SM1 objects...", ShowExtract, "Makes a prefab from the SM1 objects picked in the 3D view."));
        Section(v, "Prefab");
        _prefab.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _prefab.ItemSelected += _ => LoadPrefab();
        v.AddChild(Row(Field("Prefab", 68), _prefab));
        _prefabName.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _prefabName.TextSubmitted += t => EditPrefab(("name", t));
        v.AddChild(Row(Field("Name", 68), _prefabName));
        _collision.AddItem("mesh"); _collision.AddItem("original-cut");
        _collision.ItemSelected += i => { if (!_refreshing) EditPrefab(("collision", _collision.GetItemText((int)i))); };
        _collision.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(Row(Field("Collision", 68), _collision));
        _prefabInfo.AutowrapMode = TextServer.AutowrapMode.WordSmart; _prefabInfo.CustomMinimumSize = new Vector2(250, 0);
        _prefabInfo.AddThemeColorOverride("font_color", HintColour); _prefabInfo.AddThemeFontSizeOverride("font_size", 12);
        v.AddChild(_prefabInfo);
        Section(v, "Parts", "Part values are in prefab coordinates.");
        v.AddChild(_parts);
        _parts.ItemSelected += i => ShowPart((int)i);
        v.AddChild(VecRow("Position", _ppos)); v.AddChild(VecRow("Rotation", _prot)); v.AddChild(VecRow("Scale", _pscl));
        foreach (var s in _ppos.Concat(_prot).Concat(_pscl)) s.ValueChanged += _ => ApplyPart();
        var hide = Btn("Hide part", () => OverridePart(false), "Hides this part on the selected instance of the prefab only.");
        var show = Btn("Show part", () => OverridePart(true), "Shows this part again on the selected instance.");
        hide.SizeFlagsHorizontal = SizeFlags.ExpandFill; show.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(Row(hide, show));
        v.AddChild(Btn("Remove part", RemovePart, "Removes the part from the prefab itself (every map that uses it)."));
        Section(v, "Pivot", "The point, in prefab coordinates, that becomes the new origin.");
        v.AddChild(VecRow("Shift", _porg));
        var apply = Btn("Apply pivot", () => EditPrefab(("origin", MapmakerPlugin.Vec(V(_porg)))));
        var bottom = Btn("Use bottom centre", () => EditPrefab(("origin", "bounds-bottom-center")));
        var first = Btn("Use first part", () => EditPrefab(("origin", "first")));
        foreach (var b in new[] { apply, bottom, first }) b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(apply); v.AddChild(Row(bottom, first));
        Section(v, "Remove");
        v.AddChild(Btn("Delete this prefab...", () => { if (CurrentPrefabId is { } id) AskDeletePrefab(id); else _status.Text = "No prefab chosen."; }, "Deletes the prefab chosen above (it asks first)."));
        RefreshPrefabs();
        return Scroll(v);
    }

    string? CurrentPrefabId => _prefab.Selected >= 0 && _prefab.ItemCount > 0 ? _prefab.GetItemMetadata(_prefab.Selected).AsString() : null;

    void RefreshPrefabs()
    {
        string? keep = CurrentPrefabId;
        _prefab.Clear();
        try
        {
            foreach (var p in MapHost.Library.ListPrefabs(new List<string>())) { _prefab.AddItem($"{p.Name} ({p.Id})"); _prefab.SetItemMetadata(_prefab.ItemCount - 1, p.Id); }
        }
        catch (MapException) { }
        for (int i = 0; i < _prefab.ItemCount; i++) if (_prefab.GetItemMetadata(i).AsString() == keep) _prefab.Select(i);
        if (_prefab.ItemCount > 0 && _prefab.Selected < 0) _prefab.Select(0);
        LoadPrefab();
    }

    public void SelectPrefab(string id)
    {
        for (int i = 0; i < _prefab.ItemCount; i++) if (_prefab.GetItemMetadata(i).AsString() == id && _prefab.Selected != i) { _prefab.Select(i); LoadPrefab(); }
    }

    void LoadPrefab()
    {
        _refreshing = true;
        _parts.Clear(); _partList = new();
        var id = CurrentPrefabId;
        Prefab? p = null;
        try { p = id is null ? null : MapHost.Library.GetPrefab(id); } catch (MapException) { }
        if (p is not null)
        {
            _prefabName.Text = p.Name; _collision.Select(Array.IndexOf(Prefab.Collisions, p.Collision));
            foreach (var part in p.Parts) { _partList.Add(part); _parts.AddItem($"{part.Id}  {part.Name}  [{(part.Asset?.Label ?? part.Prefab)}]"); }
            _prefabInfo.Text = $"{p.Parts.Count} parts. Source: {p.Provenance.Source}{(p.Provenance.Map is null ? "" : " " + p.Provenance.Map)}. Origin in source frame: {p.Origin.X:0.###}, {p.Origin.Y:0.###}, {p.Origin.Z:0.###}.";
        }
        else _prefabInfo.Text = "No prefab selected. Extract SM1 objects or import one.";
        _refreshing = false;
        if (_partList.Count > 0) { _parts.Select(0); ShowPart(0); }
    }

    void ShowPart(int i)
    {
        if (i < 0 || i >= _partList.Count) return;
        _refreshing = true;
        var t = _partList[i].Transform;
        Set(_ppos, t.Position); Set(_prot, t.Rotation); Set(_pscl, t.Scale);
        _refreshing = false;
    }

    void ApplyPart()
    {
        if (_refreshing || CurrentPrefabId is not { } id || _parts.GetSelectedItems().Length == 0) return;
        var part = _partList[_parts.GetSelectedItems()[0]];
        var tr = new JsonObject { ["position"] = MapmakerPlugin.Vec(V(_ppos)), ["rotation"] = MapmakerPlugin.Vec(V(_prot)), ["scale"] = MapmakerPlugin.Vec(V(_pscl)) };
        EditPrefab(("setPart", new JsonArray(new JsonObject { ["id"] = part.Id, ["transform"] = tr })));
    }

    void RemovePart()
    {
        if (_parts.GetSelectedItems().Length == 0) return;
        EditPrefab(("removeParts", MapmakerPlugin.Strs([_partList[_parts.GetSelectedItems()[0]].Id])));
    }

    void OverridePart(bool visible)
    {
        if (_parts.GetSelectedItems().Length == 0) return;
        string part = _partList[_parts.GetSelectedItems()[0]].Id;
        foreach (var id in _plugin.SelectedIds().Where(i => _plugin.Session!.Document.Get(i).Prefab is not null).Take(1))
            _plugin.Do("Part visibility", MapmakerPlugin.Op("instance-override", ("id", id), ("part", part), ("visible", visible)));
    }

    OpResult? EditPrefab(params (string Key, JsonNode? Value)[] fields)
    {
        if (CurrentPrefabId is not { } id) return null;
        var op = MapmakerPlugin.Op("prefab-edit", [("prefab", id), .. fields]);
        var r = _plugin.Do("Edit prefab", op);
        LoadPrefab();
        return r;
    }

    // ---- refresh ----

    public void Refresh()
    {
        _refreshing = true;
        var s = _plugin.Session;
        _title.Text = s is null ? "No map open (New map or Open map)" : $"{s.Name}{(_plugin.Dirty ? " *" : "")}  -  {(s.Path ?? "unsaved")}";
        _title.TooltipText = _title.Text;
        _toolSelect.SetPressedNoSignal(MapHost.Tool == Tool.Select); _toolPick.SetPressedNoSignal(MapHost.Tool == Tool.PickSm1); _toolPlace.SetPressedNoSignal(MapHost.Tool == Tool.Place);
        _armed.Text = MapHost.Armed is { } a ? $"Placing: {a.Label}" : "";
        _sm1Info.Text = MapHost.Sm1 is { } src ? $"{src.Map} loaded. Picked: {MapHost.PickedSm1.Count} ({string.Join(", ", MapHost.PickedSm1.Take(4).Select(src.Label))}{(MapHost.PickedSm1.Count > 4 ? ", ..." : "")})" : "No SM1 map loaded.";
        _recentStart.Clear();
        _recentStart.AddItem("Your maps..."); _recentStart.SetItemMetadata(0, "");
        try
        {
            if (System.IO.Directory.Exists(MapHost.Library.MapsDir))
                foreach (var f in System.IO.Directory.EnumerateFiles(MapHost.Library.MapsDir, "*.smmap").OrderByDescending(System.IO.File.GetLastWriteTimeUtc).Take(30))
                {
                    _recentStart.AddItem(System.IO.Path.GetFileName(f)); _recentStart.SetItemMetadata(_recentStart.ItemCount - 1, f);
                }
        }
        catch (Exception) { }
        RefreshBuild();
        var sel = s is null ? new List<string>() : _plugin.SelectedIds().ToList();
        if (s is not null && sel.Count > 0)
        {
            var o = s.Document.Get(sel[0]);
            _objInfo.Text = sel.Count > 1 ? $"{sel.Count} objects selected (edits move all by the delta of the first)" : $"{o.Id}: {(o.Asset is { } a2 ? a2.Label : o.Prefab is not null ? "prefab " + o.Prefab : o.Piece is { } pc ? o.Name + (o.Piece.Info.Solid ? " (solid)" : "") + ". Scale is the size in metres; position is the middle of its footprint on the ground; Rotation Z turns it" : "group")}";
            _name.Text = o.Name;
            var t = _space.Selected == 1 ? Safe(() => MapTransform.FromMatrix(s.Document.World(o.Id))) : o.Transform;
            if (t.Scale == Num.Vector3.Zero) t = o.Transform;
            Set(_pos, t.Position); Set(_rot, t.Rotation); Set(_scl, t.Scale);
            if (o.Prefab is { } pid) SelectPrefab(pid);
        }
        else _objInfo.Text = "Nothing selected (click an object in the 3D view).";
        _refreshing = false;
    }

    public int TestTabCount => _tabs?.GetTabCount() ?? 0;
    public void TestShowTabIndex(int i) => ShowTab(i);
    public string TestTabTitle(int i) => _tabs?.GetTabTitle(i) ?? "";

    public void TestSetExtractFields(string name, string id) { _exName.Text = name; _exId.Text = id; }
}
