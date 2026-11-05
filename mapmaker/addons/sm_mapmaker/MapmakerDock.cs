using System;
using System.Collections.Generic;
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

/// <summary>The Mapmaker dock: Map (files, SM1 view, tools, snap), Library, Object (numeric transform), Prefab.</summary>
[Tool]
public partial class MapmakerDock : VBoxContainer
{
    readonly MapmakerPlugin _plugin;
    bool _refreshing;
    TabContainer? _tabs;

    /// <summary>Brings the dock to the front of its slot and shows one of its tabs (0 Map, 1 Library, 2 Object, 3 Prefab).</summary>
    public void ShowTab(int index)
    {
        if (_tabs is not null) _tabs.CurrentTab = index;
        if (GetParent() is Control wrapper && wrapper.GetParent() is TabContainer slot) slot.CurrentTab = wrapper.GetIndex();
    }

    Label _title = new(), _status = new();
    OptionButton _recent = new();
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
        AddChild(tabs);
        tabs.AddChild(MapTab()); tabs.SetTabTitle(0, "Map");
        tabs.AddChild(LibraryTab()); tabs.SetTabTitle(1, "Library");
        tabs.AddChild(ObjectTab()); tabs.SetTabTitle(2, "Object");
        tabs.AddChild(PrefabTab()); tabs.SetTabTitle(3, "Prefab");
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart; _status.CustomMinimumSize = new Vector2(280, 0);
        AddChild(_status);
        _plugin.DocChanged += Refresh;
        _plugin.StatusChanged += s => _status.Text = s;
        MapHost.Sm1Changed += () => { RebuildLibrary(); Refresh(); };
        MapHost.PickedChanged += Refresh;
        MapHost.LibraryChanged += () => { RebuildLibrary(); RefreshPrefabs(); };
        Refresh();
    }

    // ---- layout helpers ----

    static Button Btn(string text, Action on, string? tip = null) { var b = new Button { Text = text, TooltipText = tip ?? "" }; b.Pressed += on; return b; }
    static HBoxContainer Row(params Control[] c) { var h = new HBoxContainer(); foreach (var x in c) h.AddChild(x); return h; }
    static HBoxContainer VecRow(string label, SpinBox[] s) => Row(new Label { Text = label, CustomMinimumSize = new Vector2(48, 0) }, s[0], s[1], s[2]);
    static Label Head(string t) => new() { Text = t, ThemeTypeVariation = "HeaderSmall" };

    VBoxContainer Page()
    {
        var v = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        return v;
    }

    // ---- Map tab ----

    Control MapTab()
    {
        var v = Page();
        v.AddChild(_title);
        v.AddChild(Row(Btn("New", NewMap), Btn("Open...", OpenMap), Btn("Save", () => SaveMap(false)), Btn("Save As...", () => SaveMap(true))));
        v.AddChild(_recent);
        _recent.ItemSelected += i => { if (_refreshing) return; var path = _recent.GetItemMetadata((int)i).AsString(); if (path.Length > 0) _plugin.OpenMap(path); };
        v.AddChild(Head("SM1 map"));
        v.AddChild(Row(_sm1Map, Btn("Open SM1 map", OpenSm1)));
        _sm1Map.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(_backdrop);
        _backdrop.Toggled += on => { if (MapHost.Root is { } r) r.BackdropVisible = on; };
        v.AddChild(_sm1Info);
        v.AddChild(Head("Tool (3D view left click)"));
        _toolSelect.Pressed += () => _plugin.SetTool(Tool.Select);
        _toolPick.Pressed += () => _plugin.SetTool(Tool.PickSm1);
        _toolPlace.Pressed += () => _plugin.SetTool(Tool.Place);
        v.AddChild(Row(_toolSelect, _toolPick, _toolPlace));
        v.AddChild(Head("Snap"));
        var gridOn = new CheckBox { Text = "Grid (m)", ButtonPressed = true }; gridOn.Toggled += on => MapHost.SnapGrid = on;
        var angOn = new CheckBox { Text = "Angle (deg)", ButtonPressed = true }; angOn.Toggled += on => MapHost.SnapAngle = on;
        _grid.ValueChanged += x => MapHost.GridStep = (float)x; _angle.ValueChanged += x => MapHost.AngleStep = (float)x;
        v.AddChild(Row(gridOn, _grid)); v.AddChild(Row(angOn, _angle));
        v.AddChild(new Label { Text = "Snap applies to placement and to gizmo moves on release; typed values are never snapped.", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(280, 0) });
        v.AddChild(Row(Btn("Undo", () => EditorInterface.Singleton.GetEditorUndoRedo()?.Call("undo"), "Editor undo (Ctrl+Z)"), Btn("Frame SM1 map", () => { if (MapHost.Sm1 is { } s && s.BoundsCore() is { } b) _ = _plugin.Frame3D(b.Min, b.Max); })));
        return v;
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
        if (_files is null) { _files = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, Exclusive = true, InitialPosition = Window.WindowInitialPosition.CenterMainWindowScreen, Size = new Vector2I(700, 460) }; _files.FileSelected += p => _filePicked?.Invoke(p); AddChild(_files); }
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
        _source.AddItem("SM1 definitions"); _source.AddItem("Custom assets"); _source.AddItem("Prefabs");
        _source.ItemSelected += _ => RebuildLibrary();
        _search.TextChanged += _ => RebuildLibrary(); _search.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(Row(_source, _search));
        _list.ItemAt = i => i >= 0 && i < _items.Count ? _items[i] : null;
        _list.DroppedOutside = (item, pos) => { if (_plugin.OverViewport(pos) && _plugin.ReadyToPlace() && _plugin.TryRay(pos, out var o, out var d)) _plugin.PlaceAt(item, _plugin.GamePointOnRay(o, d)); };
        _list.ItemSelected += i => Arm(_items[(int)i]);
        _list.ItemActivated += i => Arm(_items[(int)i]);
        v.AddChild(_list);
        v.AddChild(_armed);
        v.AddChild(Row(Btn("Import asset (.glb/.gltf)...", () => Pick("Import glTF", FileDialog.FileModeEnum.OpenFile, "", ["*.glb,*.gltf ; glTF 2.0"], p => ImportOp("import-asset", p))),
                       Btn("Import prefab...", () => Pick("Import .smprefab", FileDialog.FileModeEnum.OpenFile, "", ["*.smprefab ; Mapmaker prefab"], p => ImportOp("import-prefab", p)))));
        v.AddChild(Row(Btn("Extract picked SM1 objects to prefab...", ShowExtract)));
        v.AddChild(new Label { Text = "Click a row, then click in the 3D view to place it (Esc or right click to stop). Rows can also be dragged into the view.", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(280, 0) });
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

    void RebuildLibrary()
    {
        _list.Clear(); _items = new();
        string f = _search.Text.Trim();
        bool Match(string s) => f.Length == 0 || s.Contains(f, StringComparison.OrdinalIgnoreCase);
        switch (_source.Selected)
        {
            case 0:
                if (MapHost.Sm1 is { } src)
                    foreach (var name in src.DefinitionNames.Where(n => Match(n) && src.HasDefinition(n)).Take(400))
                        _items.Add(new LibraryItem(LibraryKind.Sm1, name, name, src.Map));
                break;
            case 1:
                try
                {
                    var skipped = new List<string>();
                    foreach (var a in MapHost.Library.ListAssets(skipped).Where(a => Match(a.Name) || Match(a.Id))) _items.Add(new LibraryItem(LibraryKind.Asset, a.Id, a.Name));
                    if (skipped.Count > 0) _status.Text = $"{skipped.Count} asset(s) skipped: {string.Join("; ", skipped.Take(2))}";
                }
                catch (MapException) { }
                break;
            default:
                try
                {
                    var skipped = new List<string>();
                    foreach (var p in MapHost.Library.ListPrefabs(skipped).Where(p => Match(p.Name) || Match(p.Id))) _items.Add(new LibraryItem(LibraryKind.Prefab, p.Id, p.Name));
                    if (skipped.Count > 0) _status.Text = $"{skipped.Count} prefab file(s) skipped: {string.Join("; ", skipped.Take(2))}";
                }
                catch (MapException) { }
                break;
        }
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            int row = _list.AddItem(item.Label, MapmakerPlugin.Thumbs?.Cached(item.Key));
            if (MapmakerPlugin.Thumbs?.Cached(item.Key) is null && i < 60) RequestThumbnail(item);
        }
    }

    void RequestThumbnail(LibraryItem item)
    {
        MapmakerPlugin.Thumbs?.Request(item.Key, () =>
        {
            var parts = new List<ResolvedPart>();
            AssetRef? asset = item.Kind switch { LibraryKind.Sm1 => AssetRef.Sm1(item.Map!, item.Id), LibraryKind.Asset => AssetRef.Custom(item.Id), _ => null };
            if (asset is not null) parts.Add(new ResolvedPart("t", null, item.Label, asset, Num.Matrix4x4.Identity, true, null));
            else
            {
                var ctx = new MapContext(MapHost.Library, null);
                var doc = MapDocument.Create("t");
                doc.Objects.Add(new MapObject("o1", item.Label, null, "default", true, MapTransform.Identity, null, item.Id, []));
                parts.AddRange(MapResolver.Flatten(doc, ctx));
            }
            var node = ContentFactory.BuildObject(parts, Num.Matrix4x4.Identity, out var bounds);
            if (bounds.Size == Vector3.Zero && node is not null && asset is { IsSm1: true } && MapHost.Sm1For(asset.Map!)?.BoundsOf(asset.Definition!) is { } b)
                bounds = ContentFactory.ToGodot(new Bounds(b.Min, b.Max));
            if (bounds.Size == Vector3.Zero) bounds = new Aabb(new Vector3(-1, 0, -1), new Vector3(2, 2, 2));
            return (node, bounds);
        }, tex =>
        {
            int idx = _items.FindIndex(x => x.Key == item.Key);
            if (idx >= 0 && idx < _list.ItemCount && _list.GetItemText(idx) == item.Label) _list.SetItemIcon(idx, tex);
        });
    }

    void ShowExtract()
    {
        if (MapHost.PickedSm1.Count == 0 || MapHost.Sm1 is null) { _status.Text = "Pick SM1 objects first (Map tab: Open SM1 map, tool Pick SM1; shift/ctrl click adds)."; return; }
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
        v.AddChild(_objInfo);
        v.AddChild(Row(new Label { Text = "Name" }, _name));
        _name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _name.TextSubmitted += t => { foreach (var id in _plugin.SelectedIds().Take(1)) _plugin.Do("Rename", MapmakerPlugin.Op("rename", ("id", id), ("name", t))); };
        _space.AddItem("Local (document values)"); _space.AddItem("World");
        _space.ItemSelected += _ => Refresh();
        v.AddChild(_space);
        v.AddChild(VecRow("Position", _pos)); v.AddChild(VecRow("Rotation", _rot)); v.AddChild(VecRow("Scale", _scl));
        foreach (var s in _pos.Concat(_rot).Concat(_scl)) s.ValueChanged += _ => ApplyTransform();
        v.AddChild(Row(Btn("Duplicate", () => _plugin.DuplicateSelected(), "Ctrl+D over the 3D view"), Btn("Delete", () => _plugin.DeleteSelected(), "Del over the 3D view")));
        v.AddChild(Row(Btn("Group to prefab...", GroupToPrefab), Btn("Toggle visible", () => ToggleVisible())));
        v.AddChild(new Label { Text = "Rotation is Euler degrees about X, then Y, then Z of the SM1 frame (Z up). Gizmo moves in the 3D view are stored on release.", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(280, 0) });
        return v;
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
        foreach (var id in ids)
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
        v.AddChild(Row(new Label { Text = "Prefab" }, _prefab));
        _prefab.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _prefab.ItemSelected += _ => LoadPrefab();
        v.AddChild(Row(new Label { Text = "Name" }, _prefabName));
        _prefabName.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _prefabName.TextSubmitted += t => EditPrefab(("name", t));
        _collision.AddItem("mesh"); _collision.AddItem("original-cut");
        _collision.ItemSelected += i => { if (!_refreshing) EditPrefab(("collision", _collision.GetItemText((int)i))); };
        v.AddChild(Row(new Label { Text = "Collision" }, _collision));
        v.AddChild(Head("Meshes (parts)"));
        v.AddChild(_parts);
        _parts.ItemSelected += i => ShowPart((int)i);
        v.AddChild(VecRow("Position", _ppos)); v.AddChild(VecRow("Rotation", _prot)); v.AddChild(VecRow("Scale", _pscl));
        foreach (var s in _ppos.Concat(_prot).Concat(_pscl)) s.ValueChanged += _ => ApplyPart();
        v.AddChild(Row(Btn("Remove part", RemovePart), Btn("Hide part on selected instance", () => OverridePart(false)), Btn("Show", () => OverridePart(true))));
        v.AddChild(Head("Pivot / origin (point in prefab coordinates that becomes the new origin)"));
        v.AddChild(VecRow("Shift", _porg));
        v.AddChild(Row(Btn("Apply pivot", () => EditPrefab(("origin", MapmakerPlugin.Vec(V(_porg))))), Btn("Bottom centre", () => EditPrefab(("origin", "bounds-bottom-center"))), Btn("First part", () => EditPrefab(("origin", "first")))));
        v.AddChild(_prefabInfo);
        RefreshPrefabs();
        return v;
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
        _title.Text = s is null ? "No map open (New or Open)" : $"{s.Name}{(_plugin.Dirty ? " *" : "")}  -  {(s.Path ?? "unsaved")}";
        _toolSelect.SetPressedNoSignal(MapHost.Tool == Tool.Select); _toolPick.SetPressedNoSignal(MapHost.Tool == Tool.PickSm1); _toolPlace.SetPressedNoSignal(MapHost.Tool == Tool.Place);
        _armed.Text = MapHost.Armed is { } a ? $"Placing: {a.Label}" : "";
        _sm1Info.Text = MapHost.Sm1 is { } src ? $"{src.Map} loaded. Picked: {MapHost.PickedSm1.Count} ({string.Join(", ", MapHost.PickedSm1.Take(4).Select(src.Label))}{(MapHost.PickedSm1.Count > 4 ? ", ..." : "")})" : "No SM1 map loaded.";
        _recent.Clear();
        _recent.AddItem("Recent maps..."); _recent.SetItemMetadata(0, "");
        try
        {
            if (System.IO.Directory.Exists(MapHost.Library.MapsDir))
                foreach (var f in System.IO.Directory.EnumerateFiles(MapHost.Library.MapsDir, "*.smmap").Order().Take(30)) { _recent.AddItem(System.IO.Path.GetFileName(f)); _recent.SetItemMetadata(_recent.ItemCount - 1, f); }
        }
        catch (Exception) { }
        var sel = s is null ? new List<string>() : _plugin.SelectedIds().ToList();
        if (s is not null && sel.Count > 0)
        {
            var o = s.Document.Get(sel[0]);
            _objInfo.Text = sel.Count > 1 ? $"{sel.Count} objects selected (edits move all by the delta of the first)" : $"{o.Id}: {(o.Asset is { } a2 ? a2.Label : o.Prefab is not null ? "prefab " + o.Prefab : "group")}";
            _name.Text = o.Name;
            var t = _space.Selected == 1 ? Safe(() => MapTransform.FromMatrix(s.Document.World(o.Id))) : o.Transform;
            if (t.Scale == Num.Vector3.Zero) t = o.Transform;
            Set(_pos, t.Position); Set(_rot, t.Rotation); Set(_scl, t.Scale);
            if (o.Prefab is { } pid) SelectPrefab(pid);
        }
        else _objInfo.Text = "Nothing selected (click an object in the 3D view).";
        _refreshing = false;
    }

    public void TestSetExtractFields(string name, string id) { _exName.Text = name; _exId.Text = id; }
}
