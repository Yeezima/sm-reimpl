using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
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
    public static Thumbnails? Thumbs { get; private set; }
    public MapSession? Session { get; private set; }
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
        EditorInterface.Singleton.GetSelection().SelectionChanged += () => DocChanged?.Invoke();
        MapHost.RootChanged += OnRootChanged;
        CallDeferred(MethodName.Startup);
    }

    public override void _ExitTree()
    {
        MapHost.RootChanged -= OnRootChanged;
        try { GetUndoRedo().VersionChanged -= OnUndoVersionChanged; } catch (Exception) { }
        if (_dock is not null) { RemoveControlFromDocks(_dock); _dock.QueueFree(); _dock = null; }
        if (_gizmo is not null) { RemoveNode3DGizmoPlugin(_gizmo); _gizmo = null; }
        Thumbs?.QueueFree();
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

    void OnRootChanged()
    {
        _nodes.Clear(); _contentKeys.Clear();
        if (MapHost.Root is not null && Session is not null) SyncView(true);
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
        Session = s; Dirty = false; _steps.Clear(); _nodes.Clear(); _contentKeys.Clear();
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
        if (Session is null) return OpResult.Fail("no-document", "No map is open: use New or Open first.");
        var dry = Session.Apply(ops.Select(o => (JsonObject)o.DeepClone()), true);
        if (!dry.Ok)
        {
            SetStatus($"{label}: {string.Join("; ", dry.Errors.Select(e => $"{e.Code}: {e.Message}"))}");
            return dry;
        }
        var step = new Step { Ops = ops.Select(o => (JsonObject)o.DeepClone()).ToArray() };
        int id = ++_stepId;
        _steps[id] = step;
        var ur = GetUndoRedo();
        ur.CreateAction(label, UndoRedo.MergeMode.Disable, MapHost.Root);
        ur.AddDoMethod(this, MethodName.StepDo, id);
        ur.AddUndoMethod(this, MethodName.StepUndo, id);
        ur.CommitAction();
        var result = step.Result ?? dry;
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

    public void StepDo(int id)
    {
        var step = _steps[id];
        if (!step.Executed)
        {
            step.BeforeDoc = Session!.Document.ToJson();
            step.BeforePrefabs = PrefabSnapshot(PrefabIds(step.Ops));
            step.Result = Session.Apply(step.Ops);
            step.Executed = true;
            step.AfterDoc = Session.Document.ToJson();
            step.AfterPrefabs = PrefabSnapshot(PrefabIds(step.Ops).Concat(step.Result.CreatedPrefabs));
            foreach (var created in step.Result.CreatedPrefabs) step.BeforePrefabs.TryAdd(created, null);
        }
        else Restore(step.AfterDoc, step.AfterPrefabs);
        AfterChange(prefabsChanged: step.Result is { CreatedPrefabs.Count: > 0 } || step.BeforePrefabs.Count > 0 || step.Ops.Any(o => o["op"]?.ToString() is "import-asset" or "import-prefab"));
    }

    public void StepUndo(int id)
    {
        var step = _steps[id];
        Restore(step.BeforeDoc, step.BeforePrefabs);
        AfterChange(prefabsChanged: step.BeforePrefabs.Count > 0);
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

    Transform3D ExpectedTransform(MapObject o) => Frame.NodeTransform(ToGd(o.Transform.Position), ToGd(o.Transform.Rotation), ToGd(o.Transform.Scale));
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
            foreach (var gone in _nodes.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                var n = _nodes[gone];
                if (GodotObject.IsInstanceValid(n)) { n.GetParent()?.RemoveChild(n); n.QueueFree(); }
                _nodes.Remove(gone); _contentKeys.Remove(gone);
            }
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
        if (_busy || Session is null || MapHost.Root is null) return;
        CallDeferred(MethodName.Reconcile);
    }

    /// <summary>Node transforms that no longer match the document (the move/rotate/scale gizmo, or the editor undoing it) become core transform operations.</summary>
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

    public override void _Input(InputEvent @event)
    {
        if (MapHost.Root is null) return;
        if (@event is InputEventKey { Pressed: true } key)
        {
            if (key.Keycode == Key.Escape && MapHost.Tool != Tool.Select) { SetTool(Tool.Select); GetViewport().SetInputAsHandled(); return; }
            bool over = TryRay(GetViewport().GetMousePosition(), out _, out _);
            if (over && key.Keycode == Key.Delete && SelectedIds().Any()) { DeleteSelected(); GetViewport().SetInputAsHandled(); }
            else if (over && key.Keycode == Key.D && key.CtrlPressed && SelectedIds().Any()) { DuplicateSelected(); GetViewport().SetInputAsHandled(); }
            return;
        }
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
            RefreshPicked();
            GetViewport().SetInputAsHandled();
        }
        else if (MapHost.Tool == Tool.Place && MapHost.Armed is { } item)
        {
            PlaceAt(item, GamePointOnRay(o, d));
            GetViewport().SetInputAsHandled();
        }
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

    /// <summary>Points the editor camera at a box given in the document frame (the editor re-reads its own orbit on the next navigation).</summary>
    public void Frame3D(Num.Vector3 min, Num.Vector3 max)
    {
        var cam = Viewport3D?.GetCamera3D();
        if (cam is null) return;
        var centre = Frame.ToGodot(new Vector3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2));
        float radius = Mathf.Max(new Vector3(max.X - min.X, max.Y - min.Y, max.Z - min.Z).Length() * 0.5f, 1f);
        var dir = new Vector3(0.6f, 0.75f, 0.9f).Normalized();
        cam.GlobalPosition = centre + dir * radius * 2.2f;
        cam.LookAt(centre, Vector3.Up);
    }
}
