using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using SmMapmaker;
using Num = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>
/// Mapmaker Phase 1 (docs/mapmaker.md): the dock, the document session and its undo steps, the click tools of the 3D viewport.
/// The open document lives in the core's <see cref="MapSession"/>; Godot nodes only view it. Every edit is a batch of core operations;
/// editor undo/redo steps restore document snapshots taken around those batches (the editor's history is the one undo authority, because the
/// gizmo adds its own entries to it).
/// </summary>
[Tool]
public partial class MapmakerPlugin : EditorPlugin
{
    sealed class Step
    {
        public JsonObject[] Ops = [];
        public bool Executed;
        public OpResult? Result;
        public string BeforeDoc = "", AfterDoc = "";
        public Dictionary<string, string?> BeforePrefabs = new(), AfterPrefabs = new();
    }

    MapmakerDock? _dock;
    ObjectGizmoPlugin? _gizmo;
    readonly Dictionary<int, Step> _steps = new();
    int _stepId;
    bool _busy;
    int _lastPrefabStamp;
    readonly Dictionary<string, MapmakerObject> _nodes = new();
    readonly Dictionary<string, string> _contentKeys = new();
    EditorSm1Source? _sm1Adapter;

    public MapmakerDock? Dock => _dock;
    public EditorUndoRedoManager History => GetUndoRedo();
    public static Thumbnails? Thumbs { get; private set; }
    public MapSession? Session { get; private set; }

    /// <summary>Test aid: makes a session of this document text (no history).</summary>
    public void OpenMapFromJson(string json) => Adopt(new MapSession(MapDocument.Parse(json), MapHost.Library, _sm1Adapter));
    public bool Dirty { get; private set; }
    public string Status { get; private set; } = "";

    public event Action? DocChanged;
    public event Action<string>? StatusChanged;

    public override void _EnterTree()
    {
        MapHost.Plugin = this;
        _sm1Adapter = new EditorSm1Source(MapHost.Sm1For, SafeInstalled());
        Thumbs = new Thumbnails { Name = "MapmakerThumbs" };
        AddChild(Thumbs);
        _gizmo = new ObjectGizmoPlugin();
        AddNode3DGizmoPlugin(_gizmo);
        _dock = new MapmakerDock(this) { Name = "Mapmaker" };
        AddControlToDock(DockSlot.RightUl, _dock);
        GetUndoRedo().VersionChanged += OnUndoVersionChanged;
        GetUndoRedo().HistoryChanged += OnUndoVersionChanged;
        EditorInterface.Singleton.GetSelection().SelectionChanged += () => DocChanged?.Invoke();
        MapHost.RootChanged += OnRootChanged;
        CallDeferred(MethodName.Startup);
    }

    public override void _ExitTree()
    {
        MapHost.RootChanged -= OnRootChanged;
        try { GetUndoRedo().VersionChanged -= OnUndoVersionChanged; GetUndoRedo().HistoryChanged -= OnUndoVersionChanged; } catch (Exception) { }
        if (_hooked is not null && GodotObject.IsInstanceValid(_hooked)) _hooked.VersionChanged -= OnUndoVersionChanged;
        _hooked = null;
        FreeDetachedNodes(); _nodes.Clear();
        if (_dock is not null) { RemoveControlFromDocks(_dock); _dock.QueueFree(); _dock = null; }
        if (_gizmo is not null) { RemoveNode3DGizmoPlugin(_gizmo); _gizmo = null; }
        Thumbs?.QueueFree(); Thumbs = null;
        MapHost.Shutdown();
        if (MapHost.Plugin == this) MapHost.Plugin = null;
    }

    static ISm1Source? SafeInstalled()
    {
        try { return InstalledSm1Source.TryCreate(); } catch (Exception) { return null; }
    }

    public override bool _Handles(GodotObject @object) => @object is MapmakerRoot or MapmakerObject;

    void Startup()
    {
        EnsureSceneRoot();
        if (Selftest.Wanted) _ = new Selftest(this).Run();
    }

    /// <summary>Makes the edited scene a Mapmaker scene (opens the empty one when something else, or nothing, is open).</summary>
    public bool EnsureSceneRoot()
    {
        if (EditorInterface.Singleton.GetEditedSceneRoot() is MapmakerRoot) return true;
        EditorInterface.Singleton.OpenSceneFromPath("res://Maps/Empty.tscn");
        return false;
    }

    UndoRedo? _hooked;

    /// <summary>The scene's own history signals every commit, undo and redo, including ones made through the history object directly.</summary>
    void HookHistory()
    {
        if (MapHost.Root is not { } root) return;
        var h = GetUndoRedo().GetHistoryUndoRedo(GetUndoRedo().GetObjectHistoryId(root));
        if (h == _hooked) return;
        if (_hooked is not null && GodotObject.IsInstanceValid(_hooked)) _hooked.VersionChanged -= OnUndoVersionChanged;
        _hooked = h;
        h.VersionChanged += OnUndoVersionChanged;
    }

    void OnRootChanged()
    {
        HookHistory();
        FreeDetachedNodes();
        _nodes.Clear(); _contentKeys.Clear();
        if (MapHost.Root is not null && Session is not null) SyncView(true);
    }

    /// <summary>Nodes of deleted objects are kept detached so undo can reattach them; free them when the map they belong to goes away.</summary>
    void FreeDetachedNodes()
    {
        foreach (var n in _nodes.Values)
            if (GodotObject.IsInstanceValid(n) && n.GetParent() is null) n.QueueFree();
    }

    public void NotifyDoc() => DocChanged?.Invoke();
    public void SetStatus(string text) { Status = text; GD.Print("Mapmaker: " + text); StatusChanged?.Invoke(text); }

    // ---- documents ------------------------------------------------------------------------------------------------------------------------------------

    public MapSession NewMap(string name)
    {
        var s = MapSession.New(name, MapHost.Library, _sm1Adapter);
        Adopt(s);
        return s;
    }

    public OpResult? OpenMap(string path)
    {
        try { Adopt(MapSession.Load(path, MapHost.Library, _sm1Adapter)); SetStatus($"Opened {path}"); return null; }
        catch (MapException e) { SetStatus($"Open failed: {e.Code}: {e.Message}"); return OpResult.Fail(e.Code, e.Message, "open"); }
    }

    public bool SaveMap(string? path = null)
    {
        if (Session is null) return false;
        try { Session.Save(path); Dirty = false; SetStatus($"Saved {Session.Path}"); DocChanged?.Invoke(); return true; }
        catch (Exception e) when (e is MapException or System.IO.IOException or UnauthorizedAccessException) { SetStatus("Save failed: " + e.Message); return false; }
    }

    void Adopt(MapSession s)
    {
        HookHistory();
        Session = s; Dirty = false; _steps.Clear();
        foreach (var n in _nodes.Values) if (GodotObject.IsInstanceValid(n)) { n.GetParent()?.RemoveChild(n); n.QueueFree(); }
        _nodes.Clear(); _contentKeys.Clear();
        if (MapHost.Root is { } root)
        {
            foreach (var c in root.GetChildren().OfType<MapmakerObject>()) { root.RemoveChild(c); c.QueueFree(); }
            try { GetUndoRedo().ClearHistory(GetUndoRedo().GetObjectHistoryId(root), true); } catch (Exception) { }
        }
        SyncView(true);
        DocChanged?.Invoke();
    }

    // ---- applying operations (one editor undo step each) -----------------------------------------------------------------------------------------------

    public static JsonObject Op(string op, params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject { ["op"] = op };
        foreach (var (k, v) in fields) o[k] = v;
        return o;
    }

    public static JsonArray Strs(IEnumerable<string> s) => new(s.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
    public static JsonArray Vec(Num.Vector3 v) => new(JsonValue.Create((double)v.X), JsonValue.Create((double)v.Y), JsonValue.Create((double)v.Z));

    /// <summary>Validates with a dry run, then applies the batch as one editor undo step. Errors are reported and nothing changes.</summary>
    public OpResult Do(string label, params JsonObject[] ops)
    {
        if (Session is null) { SetStatus("No map is open: use New or Open first."); return OpResult.Fail("no-document", "No map is open: use New or Open first."); }
        var step = new Step { Ops = ops.Select(o => (JsonObject)o.DeepClone()).ToArray() };
        step.BeforeDoc = Session.Document.ToJson();
        step.BeforePrefabs = PrefabSnapshot(PrefabIds(step.Ops));
        var applied = Session.Apply(step.Ops);   // a batch is one transaction: on failure nothing changed, and no history entry is made
        if (!applied.Ok)
        {
            SetStatus($"{label}: {string.Join("; ", applied.Errors.Select(e => $"{e.Code}: {e.Message}"))}");
            return applied;
        }
        step.Result = applied;
        step.AfterDoc = Session.Document.ToJson();
        step.AfterPrefabs = PrefabSnapshot(PrefabIds(step.Ops).Concat(applied.CreatedPrefabs));
        foreach (var created in applied.CreatedPrefabs) step.BeforePrefabs.TryAdd(created, null);
        int id = ++_stepId;
        _steps[id] = step;
        AfterChange(prefabsChanged: StepTouchesLibrary(step));
        var ur = GetUndoRedo();
        ur.CreateAction(label, UndoRedo.MergeMode.Disable, MapHost.Root);
        ur.AddDoMethod(this, MethodName.StepDo, id);      // runs on redo only: the batch is already applied
        ur.AddUndoMethod(this, MethodName.StepUndo, id);
        ur.CommitAction(false);
        var result = applied;
        if (result.Warnings.Count > 0) SetStatus($"{label}: " + string.Join("; ", result.Warnings));
        else SetStatus(label);
        return result;
    }

    static IEnumerable<string> PrefabIds(JsonObject[] ops) =>
        ops.Where(o => o["op"]?.ToString() is "prefab-edit").Select(o => o["prefab"]?.ToString()).OfType<string>();

    Dictionary<string, string?> PrefabSnapshot(IEnumerable<string> ids)
    {
        var d = new Dictionary<string, string?>();
        foreach (var id in ids.Distinct()) { try { d[id] = MapHost.Library.GetPrefab(id)?.ToJson(); } catch (MapException) { d[id] = null; } }
        return d;
    }

    static bool StepTouchesLibrary(Step step) =>
        step.Result is { CreatedPrefabs.Count: > 0 } || step.BeforePrefabs.Count > 0 || step.Ops.Any(o => o["op"]?.ToString() is "import-asset" or "import-prefab");

    /// <summary>Redo: the editor re-runs the step, which restores the document as it was after the batch.</summary>
    public void StepDo(int id)
    {
        var step = _steps[id];
        Restore(step.AfterDoc, step.AfterPrefabs);
        AfterChange(prefabsChanged: StepTouchesLibrary(step));
    }

    public void StepUndo(int id)
    {
        var step = _steps[id];
        Restore(step.BeforeDoc, step.BeforePrefabs);
        AfterChange(prefabsChanged: StepTouchesLibrary(step));
    }

    void Restore(string docJson, Dictionary<string, string?> prefabs)
    {
        foreach (var (id, json) in prefabs)
        {
            try
            {
                if (json is null) MapHost.Library.DeletePrefab(id);
                else MapHost.Library.SavePrefab(Prefab.Parse(json));
            }
            catch (Exception e) when (e is MapException or System.IO.IOException) { SetStatus("Restoring prefab " + id + ": " + e.Message); }
        }
        var old = Session!;
        Session = new MapSession(MapDocument.Parse(docJson), old.Library, old.Sm1, old.Path);
    }

    void AfterChange(bool prefabsChanged)
    {
        Dirty = true;
        if (prefabsChanged) { _lastPrefabStamp++; MapHost.NotifyLibraryChanged(); }
        SyncView(false);
        DocChanged?.Invoke();
    }

    // ---- the view ---------------------------------------------------------------------------------------------------------------------------------------

    public Transform3D ExpectedTransform(MapObject o) => Frame.NodeTransform(ToGd(o.Transform.Position), ToGd(o.Transform.Rotation), ToGd(o.Transform.Scale));
    static Vector3 ToGd(Num.Vector3 v) => new(v.X, v.Y, v.Z);
    static Num.Vector3 ToNum(Vector3 v) => new(v.X, v.Y, v.Z);

    static string Sanitize(string name)
    {
        var chars = name.Select(c => c is '.' or ':' or '@' or '/' or '"' or '%' ? '_' : c).ToArray();
        return new string(chars);
    }

    /// <summary>Makes the scene tree match the document: nodes added, removed, moved; content rebuilt only for objects whose drawing changed.</summary>
    public void SyncView(bool force)
    {
        var root = MapHost.Root;
        if (root is null || Session is null) return;
        _busy = true;
        try
        {
            var doc = Session.Document;
            var parts = Session.Resolve().GroupBy(p => p.ObjectId).ToDictionary(g => g.Key, g => g.ToList());
            var ids = doc.Objects.Select(o => o.Id).ToHashSet();
            // A node is kept for its id for good (detached while the object is gone): editor history entries made by the gizmo refer to the node, so
            // undoing a delete must hand back the same instance.
            foreach (var gone in _nodes.Keys.Where(k => !ids.Contains(k)).ToList())
                if (GodotObject.IsInstanceValid(_nodes[gone]) && _nodes[gone].GetParent() is { } held) held.RemoveChild(_nodes[gone]);
            // Strangers (nodes the editor added: a pasted or duplicated node) are not in the document; the document wins.
            foreach (var stray in root.GetChildren().OfType<MapmakerObject>().Where(n => !_nodes.ContainsValue(n)).ToList()) { root.RemoveChild(stray); stray.QueueFree(); }
            foreach (var o in doc.Objects) Ensure(o, doc, root, parts, force, new HashSet<string>());
        }
        finally { _busy = false; }
    }

    MapmakerObject Ensure(MapObject o, MapDocument doc, MapmakerRoot root, Dictionary<string, List<ResolvedPart>> parts, bool force, HashSet<string> guard)
    {
        if (!guard.Add(o.Id)) return _nodes[o.Id];
        Node parentNode = o.Parent is { } p && doc.Find(p) is { } po ? Ensure(po, doc, root, parts, force, guard) : root;
        if (!_nodes.TryGetValue(o.Id, out var node) || !GodotObject.IsInstanceValid(node))
        {
            node = new MapmakerObject { ObjectId = o.Id };
            _nodes[o.Id] = node;
            _contentKeys.Remove(o.Id);
        }
        string key = $"{o.Asset}|{o.Prefab}|{string.Join(",", o.Overrides.Select(x => $"{x.Part}:{x.Visible}:{x.Transform}"))}|{(o.Prefab is null ? 0 : _lastPrefabStamp)}";
        node.Name = Sanitize($"{o.Name}_{o.Id}");
        if (node.GetParent() != parentNode)
        {
            node.GetParent()?.RemoveChild(node);
            parentNode.AddChild(node);
            node.Owner = root;
        }
        else if (node.Owner != root) node.Owner = root;
        node.Transform = ExpectedTransform(o);
        node.Visible = o.Visible;
        if (force || !_contentKeys.TryGetValue(o.Id, out var old) || old != key)
        {
            var notes = new List<string>();
            var objParts = parts.GetValueOrDefault(o.Id) ?? new List<ResolvedPart>();
            var world = doc.World(o.Id);
            var content = ContentFactory.BuildObject(objParts, world, out var bounds, notes);
            node.SetContent(content, bounds);
            _contentKeys[o.Id] = key;
            foreach (var n in notes.Distinct().Take(3)) GD.PushWarning("Mapmaker: " + n);
        }
        return node;
    }

    public MapmakerObject? NodeOf(string id) => _nodes.GetValueOrDefault(id);

    public IEnumerable<string> SelectedIds() =>
        EditorInterface.Singleton.GetSelection().GetSelectedNodes().OfType<MapmakerObject>().Select(n => n.ObjectId).Where(i => Session?.Document.Find(i) is not null);

    public void Select(IEnumerable<string> ids)
    {
        var sel = EditorInterface.Singleton.GetSelection();
        sel.Clear();
        foreach (var id in ids) if (_nodes.TryGetValue(id, out var n)) sel.AddNode(n);
    }

    // ---- gizmo edits -> core operations -------------------------------------------------------------------------------------------------------------------

    void OnUndoVersionChanged()
    {
        if (_busy || _reconcilePending || Session is null || MapHost.Root is null) return;
        _reconcilePending = true;
        CallDeferred(MethodName.ReconcileDeferred);
    }

    /// <summary>Node transforms that no longer match the document (the move/rotate/scale gizmo, or the editor undoing it) become core transform operations.</summary>
    public void ReconcileDeferred() { _reconcilePending = false; Reconcile(); }
    bool _reconcilePending;

    public void Reconcile()
    {
        if (_busy || Session is null) return;
        var doc = Session.Document;
        var ops = new List<JsonObject>();
        foreach (var o in doc.Objects)
        {
            if (!_nodes.TryGetValue(o.Id, out var node) || !GodotObject.IsInstanceValid(node)) continue;
            var expected = ExpectedTransform(o);
            if (node.Transform.IsEqualApprox(expected)) continue;
            var (pos, rot, scale) = Frame.Components(node.Transform);
            var op = Op("transform", ("id", o.Id));
            var cur = o.Transform;
            if (MapHost.SnapGrid) pos = new Vector3(Snap.Grid(pos.X, MapHost.GridStep), Snap.Grid(pos.Y, MapHost.GridStep), Snap.Grid(pos.Z, MapHost.GridStep));
            if (MapHost.SnapAngle && !(node.Transform.Basis.IsEqualApprox(expected.Basis))) rot = new Vector3(Snap.Angle(rot.X, MapHost.AngleStep), Snap.Angle(rot.Y, MapHost.AngleStep), Snap.Angle(rot.Z, MapHost.AngleStep));
            pos = Frame.Round(pos, 4); rot = Frame.Round(rot, 3); scale = Frame.Round(scale, 4);
            if ((ToNum(pos) - cur.Position).Length() > 1e-4f) op["position"] = Vec(ToNum(pos));
            if (!node.Transform.Basis.IsEqualApprox(expected.Basis))
            {
                if ((ToNum(rot) - cur.Rotation).Length() > 1e-3f) op["rotation"] = Vec(ToNum(rot));
                if ((ToNum(scale) - cur.Scale).Length() > 1e-4f) op["scale"] = Vec(ToNum(scale));
            }
            if (op.Count > 2) ops.Add(op);
        }
        if (ops.Count == 0) return;
        var r = Session.Apply(ops);
        if (!r.Ok) SetStatus("Move rejected: " + string.Join("; ", r.Errors.Select(e => e.Message)));
        Dirty = true;
        SyncView(false);   // snaps the nodes to the stored values
        DocChanged?.Invoke();
    }

    // ---- the 3D viewport ----------------------------------------------------------------------------------------------------------------------------------

    public SubViewport? Viewport3D => EditorInterface.Singleton.GetEditorViewport3D(0);

    /// <summary>A screen position (window coordinates) as a ray in the scene root's Godot frame, if it lies over the 3D editor viewport.</summary>
    public bool TryRay(Vector2 window, out Vector3 origin, out Vector3 direction)
    {
        origin = default; direction = default;
        var vp = Viewport3D;
        if (vp?.GetParent() is not Control container || !container.IsVisibleInTree() || MapHost.Root is not { } root) return false;
        var rect = container.GetGlobalRect();
        if (!rect.HasPoint(window)) return false;
        var local = (window - rect.Position) / rect.Size * new Vector2(vp.Size.X, vp.Size.Y);
        var cam = vp.GetCamera3D();
        if (cam is null) return false;
        var inverse = root.GlobalTransform.AffineInverse();
        origin = inverse * cam.ProjectRayOrigin(local);
        direction = (inverse.Basis * cam.ProjectRayNormal(local)).Normalized();
        return true;
    }

    /// <summary>The mouse is over the 3D editor view (window coordinates), whatever scene is open.</summary>
    public bool OverViewport(Vector2 window) => Viewport3D?.GetParent() is Control c && c.IsVisibleInTree() && c.GetGlobalRect().HasPoint(window);

    /// <summary>
    /// Before a placement: the open scene tab must be the Mapmaker scene, and a map must be open. Owner's report (2026-11-05): placing on a new
    /// Godot scene tab, or before pressing New, did nothing and said nothing. Switches to the Mapmaker scene (and asks for the click again), and
    /// starts an untitled map when none is open.
    /// </summary>
    public bool ReadyToPlace()
    {
        if (EditorInterface.Singleton.GetEditedSceneRoot() is not MapmakerRoot || MapHost.Root is null)
        {
            EnsureSceneRoot();
            SetStatus("That scene tab is not a Mapmaker map. Switched to the Mapmaker scene: click (or drop) again to place.");
            return false;
        }
        if (Session is null) { NewMap("Untitled"); SetStatus("No map was open, so a new untitled map was started (Save As to keep it)."); }
        return true;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } place && MapHost.Tool == Tool.Place && MapHost.Armed is not null
            && OverViewport(place.Position) && !ReadyToPlace()) { GetViewport().SetInputAsHandled(); return; }
        if (MapHost.Root is null) return;
        if (@event is InputEventKey { Pressed: true } key)
        {
            if (key.Keycode == Key.Escape && MapHost.Tool != Tool.Select) { SetTool(Tool.Select); GetViewport().SetInputAsHandled(); return; }
            bool over = TryRay(GetViewport().GetMousePosition(), out _, out _);
            if (over && key.Keycode == Key.Delete && SelectedIds().Any()) { DeleteSelected(); GetViewport().SetInputAsHandled(); }
            else if (over && key.Keycode == Key.D && key.CtrlPressed && SelectedIds().Any()) { DuplicateSelected(); GetViewport().SetInputAsHandled(); }
            return;
        }
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } lmb && MapHost.Tool == Tool.Select) SelectClick(lmb);
        if (@event is not InputEventMouseButton { Pressed: true } button) return;
        if (!TryRay(button.Position, out var o, out var d)) return;
        if (button.ButtonIndex == MouseButton.Right && MapHost.Tool == Tool.Place) { SetTool(Tool.Select); GetViewport().SetInputAsHandled(); return; }
        if (button.ButtonIndex != MouseButton.Left) return;
        if (MapHost.Tool == Tool.PickSm1 && MapHost.Sm1 is { } src)
        {
            int hit = src.RayPick(ToNum(Frame.ToGame(o)), ToNum(Frame.ToGame(d)), out _);
            bool additive = button.ShiftPressed || button.CtrlPressed;
            if (hit < 0) { if (!additive) MapHost.PickedSm1.Clear(); }
            else if (additive && MapHost.PickedSm1.Remove(hit)) { }
            else { if (!additive) MapHost.PickedSm1.Clear(); MapHost.PickedSm1.Add(hit); }
            if (MapHost.Root is { } r) EditorInterface.Singleton.GetSelection().RemoveNode(r); // the root's yellow box hid what was picked
            RefreshPicked();
            GetViewport().SetInputAsHandled();
        }
        else if (MapHost.Tool == Tool.Place && MapHost.Armed is { } item)
        {
            PlaceAt(item, GamePointOnRay(o, d));
            GetViewport().SetInputAsHandled();
        }
    }

    Vector2? _selectPress;

    /// <summary>
    /// Select tool, a plain click (owner's report 2026-11-05: "i can't select anything"). SM1 pieces are view nodes outside the scene, so Godot's own
    /// click cannot select them (it selects nothing, or keeps the scene root "Map", drawn as a yellow box). After Godot has handled the click: when it
    /// chose a map object, keep that; otherwise the click picks the SM1 piece under the mouse (Shift/Ctrl adds or removes) and the root is deselected.
    /// </summary>
    async void SelectClick(InputEventMouseButton button)
    {
        if (button.Pressed) { _selectPress = TryRay(button.Position, out _, out _) ? button.Position : null; return; }
        if (_selectPress is not { } from || from.DistanceTo(button.Position) > 6f) { _selectPress = null; return; } // a drag: gizmo or box select
        _selectPress = null;
        if (!TryRay(button.Position, out var o, out var d) || MapHost.Sm1 is not { } src || MapHost.Root is not { } root || !root.BackdropVisible) return;
        bool additive = button.ShiftPressed || button.CtrlPressed;
        for (int i = 0; i < 2; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var sel = EditorInterface.Singleton.GetSelection();
        var nodes = sel.GetSelectedNodes();
        if (nodes.Any(n => n is MapmakerObject)) return;
        int hit = src.RayPick(ToNum(Frame.ToGame(o)), ToNum(Frame.ToGame(d)), out _);
        if (hit < 0 && additive) return;
        if (nodes.Contains(root)) sel.RemoveNode(root);
        if (hit < 0) MapHost.PickedSm1.Clear();
        else if (additive && MapHost.PickedSm1.Remove(hit)) { }
        else { if (!additive) MapHost.PickedSm1.Clear(); MapHost.PickedSm1.Add(hit); }
        RefreshPicked();
    }

    public void SetTool(Tool tool)
    {
        MapHost.Tool = tool;
        if (tool != Tool.Place) MapHost.Armed = null;
        DocChanged?.Invoke();
    }

    public void RefreshPicked()
    {
        if (MapHost.Sm1 is { } src && MapHost.Root is { } root)
            root.ShowHighlights(MapHost.PickedSm1.Select(i => src.WorldBounds(i)).OfType<(Num.Vector3 Min, Num.Vector3 Max)>().Select(b => (new Vector3(b.Min.X, b.Min.Y, b.Min.Z), new Vector3(b.Max.X, b.Max.Y, b.Max.Z))));
        MapHost.NotifyPickedChanged();
    }

    /// <summary>Where a ray meets the SM1 backdrop's part boxes, else the ground (document z = 0), as a document-frame point.</summary>
    public Vector3 GamePointOnRay(Vector3 origin, Vector3 direction)
    {
        var og = Frame.ToGame(origin); var dg = Frame.ToGame(direction);
        if (MapHost.Root?.BackdropVisible == true && MapHost.Sm1 is { } src && src.RayPick(ToNum(og), ToNum(dg), out float t) >= 0) return og + dg * t;
        if (MathF.Abs(dg.Z) > 1e-4f && -og.Z / dg.Z is var tg && tg > 0) return og + dg * tg;
        return og + dg * 10f;
    }

    public OpResult PlaceAt(LibraryItem item, Vector3 game)
    {
        if (MapHost.SnapGrid) game = new Vector3(Snap.Grid(game.X, MapHost.GridStep), Snap.Grid(game.Y, MapHost.GridStep), Snap.Grid(game.Z, MapHost.GridStep));
        var placed = Op("place", ("name", item.Label), ("transform", new JsonObject { ["position"] = Vec(ToNum(Frame.Round(game))) }));
        switch (item.Kind)
        {
            case LibraryKind.Sm1: placed["asset"] = new JsonObject { ["source"] = "sm1", ["map"] = item.Map, ["definition"] = item.Id }; break;
            case LibraryKind.Asset: placed["asset"] = new JsonObject { ["source"] = "custom", ["id"] = item.Id }; break;
            default: placed["prefab"] = item.Id; break;
        }
        var r = Do($"Place {item.Label}", placed);
        if (r.Ok && r.Created.Count > 0) Select(r.Created);
        return r;
    }

    public OpResult DeleteSelected() => Do("Delete", Op("delete", ("ids", Strs(SelectedIds()))));

    public OpResult DuplicateSelected()
    {
        var r = Do("Duplicate", Op("duplicate", ("ids", Strs(SelectedIds())), ("offset", Vec(new Num.Vector3(MapHost.GridStep, MapHost.GridStep, 0)))));
        if (r.Ok) Select(r.Created);
        return r;
    }

    // ---- camera -----------------------------------------------------------------------------------------------------------------------------------------

    int _frameRequest;

    /// <summary>
    /// Points the editor camera at a box given in the document frame. The editor orbits a pivot a set distance in front of its camera and scales that
    /// distance on each wheel step; moving the camera alone left the distance at a few metres, so the wheel barely moved on a map hundreds of metres
    /// wide (owner's report 2026-11-05). So the camera is put at the box centre and then zoomed out through the editor's own wheel input until it
    /// stands at the framing distance: the pivot ends at the centre and the wheel scales with the map.
    /// </summary>
    public async Task Frame3D(Num.Vector3 min, Num.Vector3 max)
    {
        int request = ++_frameRequest;
        var vp = Viewport3D;
        if (vp?.GetParent() is not Control container || vp.GetCamera3D() is not { } cam) return;
        var centre = Frame.ToGodot(new Vector3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2));
        float radius = Mathf.Max(new Vector3(max.X - min.X, max.Y - min.Y, max.Z - min.Z).Length() * 0.5f, 1f);
        float target = Mathf.Clamp(radius * 2.2f, 3f, 600f); // the editor clamps its zoom distance to far/4 (1 km by default)
        var dir = new Vector3(0.6f, 0.75f, 0.9f).Normalized();
        var tree = (SceneTree)Engine.GetMainLoop();
        async Task Settle()
        {
            // The editor applies wheel input a frame or more later and then eases the camera, so wait for several still frames in a row.
            var last = cam.GlobalPosition;
            int still = 0;
            for (int i = 0; i < 180 && still < 6; i++)
            {
                await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                still = cam.GlobalPosition.DistanceTo(last) < 1e-3f * Mathf.Max(1f, target) ? still + 1 : 0;
                last = cam.GlobalPosition;
            }
        }
        void Wheel(float factor)
        {
            var at = container.GetGlobalRect().GetCenter();
            foreach (bool pressed in new[] { true, false })
                GetTree().Root.PushInput(new InputEventMouseButton
                { ButtonIndex = factor > 0 ? MouseButton.WheelDown : MouseButton.WheelUp, Pressed = pressed, Factor = Mathf.Abs(factor), Position = at, GlobalPosition = at });
        }
        // 1. Collapse the editor's orbit distance to its minimum, so the pivot sits on the camera.
        Wheel(-1000f); await Settle();
        if (request != _frameRequest) return;
        // 2. Eye at the centre, looking along -dir: the pivot is (nearly) the centre, and zooming out walks the eye back along +dir.
        cam.GlobalPosition = centre;
        cam.LookAt(centre - dir, Vector3.Up);
        await Settle();
        // 3. Zoom out through the editor until the eye stands at the framing distance. The editor's step size per wheel factor is measured as it goes
        //    (distance scales by about 1 + k * factor per event; k is re-estimated from each step).
        float k = 0.08f;
        float d = Mathf.Max(cam.GlobalPosition.DistanceTo(centre), 0.05f);
        for (int i = 0; i < 10 && Mathf.Abs(d / target - 1f) > 0.04f && request == _frameRequest; i++)
        {
            bool outward = d < target;
            float wanted = outward ? target / d : d / target;
            float factor = Mathf.Clamp((wanted - 1f) / k, 0.05f, 5000f);
            Wheel(outward ? factor : -factor); await Settle();
            float now = Mathf.Max(cam.GlobalPosition.DistanceTo(centre), 0.05f);
            float got = outward ? now / d : d / now;
            if (got > 1.0001f) k = (got - 1f) / factor;
            d = now;
        }
        if (OS.IsStdOutVerbose() || Selftest.Wanted) GD.Print($"Mapmaker frame: target {target:0.0} m, camera {d:0.0} m from the centre");
    }
}
