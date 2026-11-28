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
        public ulong RootId;   // the scene whose history holds this step; Godot keeps one history per scene, so steps outlive tab switches
        public JsonObject[] Ops = [];
        public bool Executed;
        public OpResult? Result;
        public string BeforeDoc = "", AfterDoc = "";
        public Dictionary<string, string?> BeforePrefabs = new(), AfterPrefabs = new();
        public (string Id, string Slot)[] DeletedAssets = [];   // custom assets this step moved to the library trash (undo moves them back)
        public (string Id, string Slot)[] ReplacedAssets = [];  // per id deleted and re-imported in the same batch: the slot that parks the new copy while the step is undone
    }

    MapmakerDock? _dock;
    ObjectGizmoPlugin? _gizmo;
    readonly Dictionary<int, Step> _steps = new();
    int _stepId;
    (Step Step, string Label, int Hold)? _holdStep;   // the undo step a held spin arrow keeps folding its repeats into
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
        GD.Print("Mapmaker: plugin entering the tree, args: " + string.Join(" ", OS.GetCmdlineUserArgs()));
        MapHost.Plugin = this;
        _sm1Adapter = new EditorSm1Source(MapHost.Sm1For, SafeInstalled());
        Thumbs = new Thumbnails { Name = "MapmakerThumbs" };
        AddChild(Thumbs);
        _control = new EditorControl(this);
        AddChild(_control);
        _gizmo = new ObjectGizmoPlugin();
        AddNode3DGizmoPlugin(_gizmo);
        _dock = new MapmakerDock(this) { Name = "Mapmaker" };
        AddControlToDock(DockSlot.RightUl, _dock);
        GetUndoRedo().VersionChanged += OnUndoVersionChanged;
        GetUndoRedo().HistoryChanged += OnUndoVersionChanged;
        SetInputEventForwardingAlwaysEnabled();   // Edit mode picks components from what Godot's 3D input surface gets, whatever node is being inspected (MapmakerPlugin.EditMode.cs)
        EditorInterface.Singleton.GetSelection().SelectionChanged += OnSelectionChangedForEdit;   // first: Edit mode puts its locked selection back before the panels refresh
        EditorInterface.Singleton.GetSelection().SelectionChanged += RaiseDocChanged;
        MapHost.RootChanged += OnRootChanged;
        SceneSaved += OnSceneSaved;
        MapHost.Sm1Changed += OnSm1Changed;
        DocChanged += RecheckSoon;
        InitViewportTools();
        CallDeferred(MethodName.Startup);
    }

    public override void _ExitTree()
    {
        _decorWorker?.Dispose(); _decorWorker = null;   // no background build is applied to a plugin that is gone
        _closing = true;
        // The background check and the placement world (2026-11-27) allocate heavily on worker threads; a collection they cause while the plugin tears down was seen to crash the
        // gizmo plugin's release below (0xC0000005 at exit in some runs, base 40d64bfa 0 of 2). Give a running one a short, bounded chance to finish first.
        WaitForBackground(TimeSpan.FromSeconds(5));
        if (PlayRunning) StopPlayTest();   // closing the editor mid-test stops that test's server and bots (only the processes it recorded)
        MapHost.RootChanged -= OnRootChanged;
        MapHost.Sm1Changed -= OnSm1Changed;
        try { GetUndoRedo().VersionChanged -= OnUndoVersionChanged; GetUndoRedo().HistoryChanged -= OnUndoVersionChanged; } catch (Exception) { }
        if (_hooked is not null && GodotObject.IsInstanceValid(_hooked)) _hooked.VersionChanged -= OnUndoVersionChanged;
        _hooked = null;
        try { var es = EditorInterface.Singleton.GetSelection(); es.SelectionChanged -= OnSelectionChangedForEdit; es.SelectionChanged -= RaiseDocChanged; } catch (Exception) { }
        FreeViewportTools();
        EditorGrid.RestoreBuiltIn();   // Godot's own grid comes back with the user's setting
        SceneSaved -= OnSceneSaved;
        ClearObjectGizmos();   // before any node is freed and before the gizmo plugin is released (see ClearObjectGizmos)
        FreeDetachedNodes(); _nodes.Clear(); _stash.Clear();
        if (_dock is not null) { RemoveControlFromDocks(_dock); _dock.QueueFree(); _dock = null; }
        // the gizmo plugin's handle is released here, on the main thread: left to the finalizer thread (any later GC, e.g. the material test's) its release removes the
        // gizmos from the scene's nodes off the main thread (Godot's thread guard: "can't call remove_gizmo()", M5 review)
        if (_gizmo is not null)
        {
            RemoveNode3DGizmoPlugin(_gizmo);
            // The engine object is never destroyed while the editor closes (2026-11-27): its destructor detaches the gizmos it still knows and refreshes every gizmo of the edited scene, and
            // at exit that scene is being torn down (0xC0000005 inside the release, 7 of 10 headless self-test exits already on base 40d64bfa). One extra engine reference keeps the small
            // object alive until the process ends; the managed handle is still released here, on the main thread (M5 review), and nothing is left to Godot's shutdown tracker (which crashed
            // every exit with a big imported map open, review of 919e4b4b).
            _gizmo.Reference();
            _gizmo.Dispose();
            _gizmo = null;
        }
        Thumbs?.QueueFree(); Thumbs = null;
        MapHost.Shutdown();
        if (MapHost.Plugin == this) MapHost.Plugin = null;
        // No forced GC here (M5 review): it handed the gizmo plugin's teardown to the finalizer thread (16 thread-guard errors at exit). The material handles are released
        // explicitly instead (MapmakerDock._ExitTree -> ReleaseMaterialViews); MaterialTest keeps its own collection.
    }

    /// <summary>
    /// Removes every map object's gizmo while the nodes still exist. Releasing the gizmo plugin makes the engine walk the gizmos it still has registered and detach each from its node; a
    /// gizmo whose node was freed before that walk is a dangling pointer (an intermittent 0xC0000005 inside the plugin's release at exit). Clearing them first leaves it nothing to walk.
    /// </summary>
    void ClearObjectGizmos()
    {
        var seen = new HashSet<ulong>();
        void Clear(Node3D n) { if (GodotObject.IsInstanceValid(n) && seen.Add(n.GetInstanceId())) n.ClearGizmos(); }
        foreach (var n in _nodes.Values) Clear(n);
        if (MapHost.Root is { } root && GodotObject.IsInstanceValid(root))
            foreach (var n in root.FindChildren("*", "Node3D", true, false)) if (n is MapmakerObject o) Clear(o);
    }

    static ISm1Source? SafeInstalled()
    {
        try { return InstalledSm1Source.TryCreate(); } catch (Exception) { return null; }
    }

    public override bool _Handles(GodotObject @object) => @object is MapmakerRoot or MapmakerObject;

    EditorControl? _control;
    int _resumeTries;

    void Startup()
    {
        if (OS.GetCmdlineUserArgs().Length > 0) GD.Print("Mapmaker: started with " + string.Join(" ", OS.GetCmdlineUserArgs()));
        EnsureSceneRoot();
        MapHost.ResetSnap();   // snap starts off in every session; Godot re-applies a scene's saved Use Snap when it opens, so once more a little later
        GetTree().CreateTimer(2.0).Timeout += EditorSnap.SwitchOffBuiltIn;
        if (Selftest.Wanted) _ = new Selftest(this).Run();
        else if (FlowTest.Wanted) _ = new FlowTest(this).Run();
        else if (GroupsTest.Wanted) _ = new GroupsTest(this).Run();
        else if (GridTest.Wanted) _ = new GridTest(this).Run();
        else if (ClassicTest.Wanted) _ = new ClassicTest(this).Run();
        else if (EditModeTest.Wanted) _ = new EditModeTest(this).Run();
        else if (PerfTest.Wanted) _ = new PerfTest(this).Run();
        else if (ExitTest.Wanted) _ = new ExitTest(this).Run();
        else GetTree().CreateTimer(1.0).Timeout += ResumeWhenReady;
    }

    /// <summary>After a restart on request (EditorControl): reopens what was open, once the Mapmaker scene is the edited one.</summary>
    void ResumeWhenReady()
    {
        if (EditorInterface.Singleton.GetEditedSceneRoot() is not MapmakerRoot && ++_resumeTries < 20) { EnsureSceneRoot(); GetTree().CreateTimer(0.5).Timeout += ResumeWhenReady; return; }
        _control?.Resume();
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

    // Each scene tab holds its own map (owner, 2026-11-18: place an extracted prefab in a scene of another tab). Godot keeps only the edited scene in the
    // tree, so a root leaving the tree stashes its session here, and a root entering gets its own back (or none: its first placement starts an untitled map).
    readonly Dictionary<ulong, (MapSession? Session, bool Dirty)> _stash = new();
    ulong _activeRoot;

    void OnRootChanged()
    {
        FreeGhost();
        var now = MapHost.Root;
        ulong id = now is not null && GodotObject.IsInstanceValid(now) ? now.GetInstanceId() : 0;
        if (id != _activeRoot)
        {
            if (_activeRoot != 0 && Session is not null) _stash[_activeRoot] = (Session, Dirty);
            if (id == 0) { if (_activeRoot != 0) { Session = null; Dirty = false; } }
            else if (_stash.Remove(id, out var back)) { Session = back.Session; Dirty = back.Dirty; }
            else if (_activeRoot != 0 || Session is null) { Session = null; Dirty = false; }   // a scene of its own: no map yet
            _activeRoot = id;
            ForgetSolidWorld();   // another tab aims at its own pieces, never at the previous tab's world (review of 919e4b4b)
        }
        if (now is not null && GodotObject.IsInstanceValid(now) && SceneFileOf(now).Length > 0) _rootScene.TryAdd(id, SceneFileOf(now));
        HookHistory();
        FreeDetachedNodes();
        _nodes.Clear(); _contentKeys.Clear(); _viewed.Clear();
        PruneStash();
        HoldEditorGrid();
        if (MapHost.Root is not null && Session is not null) SyncView(true);
        else if (MapHost.Root is { } fresh && SceneMapOf(fresh) is { } saved && System.IO.File.Exists(saved)) OpenMap(saved);   // a reopened scene loads its map again
        RaiseDocChanged();
    }

    /// <summary>
    /// A scene saved while it is a background tab (Save All, or the close prompt's Save) writes its own map too: _SaveExternalData only sees the edited
    /// scene, so the stashed session whose root holds that scene file is saved here and marked clean. A tab the user chose not to save is never written
    /// (review of the exit flush, 2026-11-18).
    /// </summary>
    void OnSceneSaved(string filepath)
    {
        foreach (var (k, v) in _stash.ToList())
        {
            if (!v.Dirty || v.Session is null || GodotObject.InstanceFromId(k) is not Node root || !GodotObject.IsInstanceValid(root) || SceneFileOf(root) != filepath) continue;
            try { if (SaveSceneMap(root, v.Session, true)) _stash[k] = (v.Session, false); }
            catch (Exception e) { GD.PushWarning($"Mapmaker: the map of {filepath} was not saved: {e.Message}"); }
        }
    }

    /// <summary>Forgets the maps of scene tabs that were closed (their root is gone).</summary>
    void PruneStash()
    {
        foreach (var k in _stash.Keys.Where(k => GodotObject.InstanceFromId(k) is null).ToList()) _stash.Remove(k);
        foreach (var k in _rootScene.Keys.Where(k => GodotObject.InstanceFromId(k) is null).ToList()) _rootScene.Remove(k);
        foreach (var k in _steps.Where(x => x.Value.RootId != 0 && GodotObject.InstanceFromId(x.Value.RootId) is null).Select(x => x.Key).ToList()) _steps.Remove(k);
    }

    // Which map belongs to which scene file: scene-maps.json next to the maps, { "res://a.tscn": { "map": "<file>", "uid": "uid://..." } }. The uid is the scene's own
    // resource uid, so a new scene at a reused path never picks up an old scene's map. The file is re-read before every write (another editor may have changed it) and
    // entries of scene files that no longer exist are dropped.
    string SceneMapsFile => System.IO.Path.Combine(MapHost.Library.MapsDir, "scene-maps.json");

    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var t) ? t : "";

    /// <summary>The recorded scene-to-map entries. A file that cannot be parsed is set aside as scene-maps.json.bad (with a warning) so it is never overwritten blindly.</summary>
    JsonObject ReadSceneMaps()
    {
        string file = SceneMapsFile;
        try
        {
            if (!System.IO.File.Exists(file)) return new JsonObject();
            if (JsonNode.Parse(System.IO.File.ReadAllText(file)) is JsonObject o) return o;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or ArgumentException) { }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { return new JsonObject(); }
        try { System.IO.File.Move(file, file + ".bad", true); GD.PushWarning($"Mapmaker: {file} could not be read; kept as {file}.bad and started a new one."); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { }
        return new JsonObject();
    }

    static string SceneUid(string scene)
    {
        try { long id = ResourceLoader.GetResourceUid(scene); return id == -1 ? "" : ResourceUid.IdToText(id); } catch (Exception) { return ""; }
    }

    static string SceneFileOf(Node root) => (root.Owner ?? root).SceneFilePath;

    static bool SameFile(string a, string b)
    {
        try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return false; }
    }

    /// <summary>The map recorded for a scene file, when the entry was written for this very scene (same uid) and the map file is there.</summary>
    public string? SceneMapOfFile(string scene)
    {
        if (scene.Length == 0 || ReadSceneMaps()[scene] is not JsonObject e || Str(e["map"]) is not { Length: > 0 } map) return null;
        string uid = Str(e["uid"]);
        if (uid.Length > 0 && SceneUid(scene) != uid) return null;
        return System.IO.File.Exists(map) ? map : null;
    }

    string? SceneMapOf(Node root) => SceneMapOfFile(SceneFileOf(root));

    /// <summary>Another existing scene (with its own uid) that already uses this map file, or null.</summary>
    string? OtherOwnerOfMap(string map, string scene)
    {
        foreach (var (k, v) in ReadSceneMaps())
            if (k != scene && v is JsonObject e && Str(e["map"]) is { Length: > 0 } m && SameFile(m, map) && FileAccess.FileExists(k) && (Str(e["uid"]) is var u && (u.Length == 0 || SceneUid(k) == u))) return k;
        return null;
    }

    /// <summary>A map file name that no file and no scene entry uses: <paramref name="prefix"/><paramref name="stem"/>.smmap, then -2, -3 and so on, in the library's maps folder.</summary>
    public string UniqueMapPath(string stem, string prefix = "scene-")
    {
        stem = Sanitize(stem.Length > 0 ? stem : "untitled");
        var used = ReadSceneMaps();
        for (int i = 1; ; i++)
        {
            string path = System.IO.Path.Combine(MapHost.Library.MapsDir, i == 1 ? $"{prefix}{stem}.smmap" : $"{prefix}{stem}-{i}.smmap");
            if (!System.IO.File.Exists(path) && used.All(kv => kv.Value is not JsonObject e || Str(e["map"]) is not { Length: > 0 } m || !SameFile(m, path))) return path;
        }
    }

    string _recorded = "";
    readonly Dictionary<ulong, string> _rootScene = new();   // the scene file each root had when last looked at, to tell a Save As (the file changed) from an explicit open

    /// <summary>Adds or replaces a scene's entry (re-reading the file first, dropping entries of vanished scene files) and writes it through a temp file. False when it could not be written.</summary>
    bool RecordSceneMap(string scene, string map)
    {
        try
        {
            var all = ReadSceneMaps();
            foreach (var k in all.Select(x => x.Key).ToList()) if (k != scene && !FileAccess.FileExists(k)) all.Remove(k);
            all[scene] = new JsonObject { ["map"] = map, ["uid"] = SceneUid(scene) };
            System.IO.Directory.CreateDirectory(MapHost.Library.MapsDir);
            string tmp = SceneMapsFile + ".tmp";
            System.IO.File.WriteAllText(tmp, all.ToJsonString());
            System.IO.File.Move(tmp, SceneMapsFile, true);
            _recorded = scene + "|" + map;
            return true;
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { return false; }
    }

    /// <summary>
    /// Keeps one scene's map apart from every other scene's. A scene saved under a new name (Save As) while holding another scene's map gets its own copy; an
    /// explicit open of a map another scene uses is left alone (the user chose that file). A dirty map is written only when it is asked for
    /// (<paramref name="save"/>), and never for a scene that has no file yet (it stays in memory until the scene gets a path). Returns true when the map file was written.
    /// </summary>
    bool SaveSceneMap(Node root, MapSession session, bool save)
    {
        string scene = SceneFileOf(root);
        ulong id = root.GetInstanceId();
        bool renamed = _rootScene.TryGetValue(id, out var before) && before.Length > 0 && scene.Length > 0 && before != scene;
        if (scene.Length > 0) _rootScene[id] = scene;
        try
        {
            string? path = session.Path;
            if (renamed && path is not null && SceneMapOfFile(scene) is null && OtherOwnerOfMap(path, scene) is { } owner)
            {
                string copy = UniqueMapPath(System.IO.Path.GetFileNameWithoutExtension(scene));
                session.Save(copy);
                SetStatus($"{scene} now has its own copy of the map ({copy}); {owner} keeps the original.");
                RecordSceneMap(scene, session.Path!);
                return true;
            }
            if (!save)
            {
                if (path is not null && scene.Length > 0 && _recorded != scene + "|" + path) RecordSceneMap(scene, path);
                return false;
            }
            if (path is null)
            {
                if (scene.Length == 0) { SetStatus("This scene has no file yet, so its map was not saved; save the scene again once it has a name."); try { EditorInterface.Singleton.MarkSceneAsUnsaved(); } catch (Exception) { } return false; }
                path = UniqueMapPath(System.IO.Path.GetFileNameWithoutExtension(scene));
            }
            session.Save(path);
            if (scene.Length > 0) RecordSceneMap(scene, session.Path!);
            return true;
        }
        catch (Exception e) when (e is MapException or System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { SetStatus("Saving the scene's map failed: " + e.Message); return false; }
    }

    /// <summary>Notes the edited scene's file against its map's file (called after saves and from a slow poll); a Save As forks the map here.</summary>
    void SyncRootMeta()
    {
        if (MapHost.Root is not { } r || !GodotObject.IsInstanceValid(r) || Session is null || Session.Path is not { } p || SceneFileOf(r) is not { Length: > 0 } f) return;
        ulong id = r.GetInstanceId();
        if (_recorded == f + "|" + p && (!_rootScene.TryGetValue(id, out var seen) || seen == f)) return;
        if (SaveSceneMap(r, Session, false)) { Dirty = false; RaiseDocChanged(); }
    }

    /// <summary>Saving the scene also saves its map. Only the edited scene's map is written: Godot does not say which scenes a save covers, so changed maps of other open tabs stay in memory until their scene is saved.</summary>
    public override void _SaveExternalData()
    {
        if (Session is not null && MapHost.Root is { } r && SaveSceneMap(r, Session, Dirty)) { Dirty = false; RaiseDocChanged(); }
    }

    /// <summary>Nodes of deleted objects are kept detached so undo can reattach them; free them when the map they belong to goes away.</summary>
    void FreeDetachedNodes()
    {
        foreach (var n in _nodes.Values)
            if (GodotObject.IsInstanceValid(n) && n.GetParent() is null) n.QueueFree();
    }

    public void NotifyDoc() => RaiseDocChanged();

    /// <summary>Test aid: a placement click in a Mapmaker tab that has no map yet (it starts an untitled one there).</summary>
    public async Task TestPlaceInEmptyTabKeepsSnap()
    {
        Session = null;
        ReadyToPlace();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>Tells the panels the document (or the selection) changed; with <see cref="EditCost.Enabled"/> each listener's time is recorded.</summary>
    void RaiseDocChanged()
    {
        if (!EditCost.Enabled) { DocChanged?.Invoke(); return; }
        foreach (var d in DocChanged?.GetInvocationList() ?? [])
        {
            long t = EditCost.Start();
            ((Action)d).Invoke();
            EditCost.End("DocChanged:" + (d.Method.DeclaringType?.Name ?? "") + "." + d.Method.Name, t);
        }
    }
    public void SetStatus(string text) { Status = text; GD.Print("Mapmaker: " + text); StatusChanged?.Invoke(text); }

    // ---- documents ------------------------------------------------------------------------------------------------------------------------------------

    /// <param name="resetSnap">false for the untitled map a first placement starts in an empty tab: a snap box the user just ticked for that placement stays ticked (review of 2026-11-27).</param>
    public MapSession NewMap(string name, bool resetSnap = true)
    {
        var s = MapSession.New(name, MapHost.Library, _sm1Adapter);
        Adopt(s, resetSnap);
        return s;
    }

    public OpResult? OpenMap(string path)
    {
        try
        {
            Adopt(MapSession.Load(path, MapHost.Library, _sm1Adapter));
            string? shared = MapHost.Root is { } r && SceneFileOf(r) is { Length: > 0 } f ? OtherOwnerOfMap(Session!.Path!, f) : null;
            SetStatus($"Opened {path}" + (shared is null ? "" : $". This map is also used by {shared}; edits affect both.") + string.Concat(Session!.OpenNotes.Select(n => " " + n)));
            return null;
        }
        catch (MapException e) { SetStatus($"Open failed: {e.Code}: {e.Message}"); return OpResult.Fail(e.Code, e.Message, "open"); }
    }

    public bool SaveMap(string? path = null)
    {
        if (Session is null) return false;
        try { Session.Save(path); Dirty = false; SyncRootMeta(); SetStatus($"Saved {Session.Path}"); RaiseDocChanged(); return true; }
        catch (Exception e) when (e is MapException or System.IO.IOException or UnauthorizedAccessException) { SetStatus("Save failed: " + e.Message); return false; }
    }

    void Adopt(MapSession s, bool resetSnap = true)
    {
        HookHistory();
        Session = s; Dirty = false;
        if (resetSnap)
        {
            MapHost.ResetSnap();   // every map opened or started begins with snap off (owner 2026-11-27)
            // A scene opened from its file has Godot re-apply its saved Use Snap a moment later (the scene's editor state): switch it off once more then.
            if (IsInsideTree()) GetTree().CreateTimer(0.5).Timeout += EditorSnap.SwitchOffBuiltIn;
        }
        ForgetSolidWorld();
        foreach (var k in _steps.Where(x => x.Value.RootId == _activeRoot).Select(x => x.Key).ToList()) _steps.Remove(k);   // this scene's history is cleared below; other tabs keep theirs
        foreach (var n in _nodes.Values) if (GodotObject.IsInstanceValid(n)) { n.GetParent()?.RemoveChild(n); n.QueueFree(); }
        _nodes.Clear(); _contentKeys.Clear(); _viewed.Clear();
        if (MapHost.Root is { } root)
        {
            var strays = root.GetChildren().OfType<MapmakerObject>().ToList();
            if (strays.Count > 0)
            {
                // Object nodes saved in the scene file are not the map (the document is); they are replaced by the opened map's objects.
                GD.PushWarning($"Mapmaker: {strays.Count} object node(s) saved in this scene are replaced by the objects of the opened map.");
                SetStatus($"Opened a map: {strays.Count} object node(s) saved in this scene were replaced by the map's own objects.");
            }
            foreach (var c in strays) { root.RemoveChild(c); c.QueueFree(); }
            try { GetUndoRedo().ClearHistory(GetUndoRedo().GetObjectHistoryId(root), true); } catch (Exception) { }
        }
        SyncView(true); SyncRootMeta();
        RaiseDocChanged();
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
        long t0 = EditCost.Start();
        var step = new Step { RootId = _activeRoot, Ops = ops.Select(o => (JsonObject)o.DeepClone()).ToArray() };
        // the snapshots come from the session, which serialises the map once before and once after the batch anyway (the edit-hang fix: no second copy of each)
        step.BeforePrefabs = PrefabSnapshot(PrefabIds(step.Ops));
        EditCost.End("Do:before-snapshot", t0); t0 = EditCost.Start();
        var applied = Session.Apply(step.Ops);   // a batch is one transaction: on failure nothing changed, and no history entry is made
        EditCost.End("Do:core-apply", t0);
        if (!applied.Ok)
        {
            SetStatus($"{label}: {string.Join("; ", applied.Errors.Select(e => $"{e.Code}: {e.Message}"))}");
            return applied;
        }
        step.Result = applied;
        step.BeforeDoc = Session.LastBeforeJson ?? Session.Document.ToJson();
        bool deletes = step.Ops.Any(o => o["op"]?.ToString() == "delete-asset");
        step.DeletedAssets = deletes ? Session.LastTrashed.ToArray() : [];
        step.ReplacedAssets = deletes ? Session.LastReplaced.ToArray() : [];
        step.AfterDoc = Session.LastAfterJson ?? Session.Document.ToJson();
        step.AfterPrefabs = PrefabSnapshot(PrefabIds(step.Ops).Concat(applied.CreatedPrefabs));
        foreach (var created in applied.CreatedPrefabs) step.BeforePrefabs.TryAdd(created, null);
        // A held spin-box arrow repeats many edits: the first makes the undo step, the rest fold into it (one hold = one undo step).
        if (SpinArrows.Holding && _holdStep is { } held && held.Step.RootId == step.RootId && held.Label == label && held.Hold == SpinArrows.HoldId && _steps.ContainsValue(held.Step))
        {
            held.Step.AfterDoc = step.AfterDoc; held.Step.AfterPrefabs = step.AfterPrefabs; held.Step.Result = applied;
            AfterChange(prefabsChanged: StepTouchesLibrary(step));
            SetStatus(label);
            return applied;
        }
        int id = ++_stepId;
        _steps[id] = step;
        _holdStep = SpinArrows.Holding ? (step, label, SpinArrows.HoldId) : null;
        t0 = EditCost.Start();
        AfterChange(prefabsChanged: StepTouchesLibrary(step));
        EditCost.End("Do:after-change", t0); t0 = EditCost.Start();
        var ur = GetUndoRedo();
        ur.CreateAction(label, UndoRedo.MergeMode.Disable, MapHost.Root);
        ur.AddDoMethod(this, MethodName.StepDo, id);      // runs on redo only: the batch is already applied
        ur.AddUndoMethod(this, MethodName.StepUndo, id);
        ur.CommitAction(false);
        EditCost.End("Do:history-commit", t0);
        var result = applied;
        if (result.Warnings.Count > 0) SetStatus($"{label}: " + string.Join("; ", result.Warnings));
        else SetStatus(label);
        return result;
    }

    static IEnumerable<string> PrefabIds(JsonObject[] ops) =>
        ops.Where(o => o["op"]?.ToString() is "prefab-edit" or "delete-prefab").Select(o => o["prefab"]?.ToString()).OfType<string>();

    Dictionary<string, string?> PrefabSnapshot(IEnumerable<string> ids)
    {
        var d = new Dictionary<string, string?>();
        foreach (var id in ids.Distinct()) { try { d[id] = MapHost.Library.GetPrefab(id)?.ToJson(); } catch (MapException) { d[id] = null; } }
        return d;
    }

    static bool StepTouchesLibrary(Step step) =>
        step.Result is { CreatedPrefabs.Count: > 0 } || step.BeforePrefabs.Count > 0 || step.Ops.Any(o => o["op"]?.ToString() is "import-asset" or "import-prefab" or "delete-asset");

    /// <summary>Redo: the editor re-runs the step, which restores the document as it was after the batch.</summary>
    public void StepDo(int id)
    {
        if (!_steps.TryGetValue(id, out var step) || step.RootId != _activeRoot) return;   // a history entry whose step was dropped (its map was replaced), or another scene's
        if (!SwapAssets(step, undo: false)) return;   // a failed storage step is reported and leaves the document as it was, so files and map stay in step
        Restore(step.AfterDoc, step.AfterPrefabs);
        AfterChange(prefabsChanged: StepTouchesLibrary(step));
    }

    public void StepUndo(int id)
    {
        if (!_steps.TryGetValue(id, out var step) || step.RootId != _activeRoot) return;
        if (!SwapAssets(step, undo: true)) return;
        Restore(step.BeforeDoc, step.BeforePrefabs);
        AfterChange(prefabsChanged: StepTouchesLibrary(step));
    }

    /// <summary>Undoes or redoes the step's asset moves with the library's swap helpers (the same ones <c>MapSession.Undo/Redo</c> use, so a delete + import of one id swaps old and new). On a failure the moves already made are reversed (best effort), the error is reported and false is returned.</summary>
    bool SwapAssets(Step step, bool undo)
    {
        int done = 0;
        string? ParkedOf(string id) => step.ReplacedAssets.FirstOrDefault(r => r.Id == id).Slot;
        try
        {
            foreach (var (a, slot) in step.DeletedAssets)
            {
                if (undo) MapHost.Library.UndoAssetSwap(a, slot, ParkedOf(a));
                else MapHost.Library.RedoAssetSwap(a, slot, ParkedOf(a));
                done++;
            }
            return true;
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            foreach (var (a, slot) in step.DeletedAssets.Take(done).Reverse())
            {
                try { if (undo) MapHost.Library.RedoAssetSwap(a, slot, ParkedOf(a)); else MapHost.Library.UndoAssetSwap(a, slot, ParkedOf(a)); }
                catch (Exception) { }
            }
            SetStatus($"{(undo ? "Undo" : "Redo")} failed, the map was left as it was: {e.Message}");
            GD.PushError($"Mapmaker: {(undo ? "undo" : "redo")} of an asset move failed: {e.Message}");
            return false;
        }
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
        SyncRootMeta();
        try { EditorInterface.Singleton.MarkSceneAsUnsaved(); } catch (Exception) { }   // so Godot's own close prompt fires for a scene whose map changed
        if (prefabsChanged)
        {
            _lastPrefabStamp++;
            // an armed prefab that is gone (deleted, or a redo of its delete) would only fail at the next click
            if (MapHost.Armed is { Kind: LibraryKind.Prefab } armed && MapHost.Library.GetPrefab(armed.Id) is null) MapHost.Armed = null;
            MapHost.NotifyLibraryChanged();
        }
        var keep = SelectedNodes();
        SyncView(false);
        KeepSelected(keep);
        RaiseDocChanged();
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
        ApplyGrid();
        if (root is null || Session is null) return;
        _busy = true;
        long t0 = EditCost.Start();
        try
        {
            var doc = Session.Document;
            // Every object's resolved parts (the whole map) are needed only when some object's drawing is rebuilt, so they are made on first use.
            Dictionary<string, List<ResolvedPart>>? parts = null;
            Dictionary<string, List<ResolvedPart>> Parts()
            {
                if (parts is not null) return parts;
                long tr = EditCost.Start();
                parts = Session.Resolve().GroupBy(p => p.ObjectId).ToDictionary(g => g.Key, g => g.ToList());
                EditCost.End("SyncView:resolve", tr);
                return parts;
            }
            var byId = new Dictionary<string, MapObject>(doc.Objects.Count);
            foreach (var o in doc.Objects) byId.TryAdd(o.Id, o);
            // A node is kept for its id for good (detached while the object is gone): editor history entries made by the gizmo refer to the node, so
            // undoing a delete must hand back the same instance.
            foreach (var gone in _nodes.Keys.Where(k => !byId.ContainsKey(k)).ToList())
            {
                _viewed.Remove(gone);
                if (GodotObject.IsInstanceValid(_nodes[gone]) && _nodes[gone].GetParent() is { } held) held.RemoveChild(_nodes[gone]);
            }
            // Strangers (nodes the editor added: a pasted or duplicated node) are not in the document; the document wins.
            var ours = new HashSet<MapmakerObject>(_nodes.Values);
            foreach (var stray in root.GetChildren().OfType<MapmakerObject>().Where(n => !ours.Contains(n)).ToList()) { root.RemoveChild(stray); stray.QueueFree(); }
            bool stampChanged = _viewedStamp != _lastPrefabStamp;
            _viewedStamp = _lastPrefabStamp;
            var guard = new HashSet<string>();
            foreach (var o in doc.Objects) Ensure(o, byId, root, Parts, force, stampChanged, guard);
            if (force) EditCost.FullSyncs++;
        }
        finally { _busy = false; EditCost.End("SyncView:nodes", t0); }
        LoadMissingSm1();
    }

    // The edit-hang fix (owner's report 2026-11-27): the view used to write every node's name, transform and visibility on every edit, and to sync a parent once per child. Renaming a
    // node (even to the same name) makes Godot notify its whole subtree and the editor's Scene dock, so an SM1 map imported for editing (thousands of objects under one group node) spent
    // tens of seconds per edit. Now an object whose document record is the one the node already shows is skipped, and a visited node is written only where it differs.
    readonly Dictionary<string, MapObject> _viewed = new();   // the document record each node shows now
    int _viewedStamp;

    /// <summary>Test aid: object nodes the view holds (attached or detached).</summary>
    public int NodeCount => _nodes.Count;

    MapmakerObject Ensure(MapObject o, Dictionary<string, MapObject> byId, MapmakerRoot root, Func<Dictionary<string, List<ResolvedPart>>> parts, bool force, bool stampChanged, HashSet<string> guard)
    {
        if (!guard.Add(o.Id)) return _nodes[o.Id];
        Node parentNode = o.Parent is { } p && byId.TryGetValue(p, out var po) ? Ensure(po, byId, root, parts, force, stampChanged, guard) : root;
        if (!force && _viewed.TryGetValue(o.Id, out var seen) && ReferenceEquals(seen, o) && !(stampChanged && o.Prefab is not null) && _contentKeys.ContainsKey(o.Id)
            && _nodes.TryGetValue(o.Id, out var same) && GodotObject.IsInstanceValid(same) && same.GetParent() == parentNode)
            return same;   // unchanged since the node last showed it
        EditCost.NodesVisited++;
        if (!_nodes.TryGetValue(o.Id, out var node) || !GodotObject.IsInstanceValid(node))
        {
            node = new MapmakerObject { ObjectId = o.Id };
            _nodes[o.Id] = node;
            _contentKeys.Remove(o.Id);
        }
        bool wrote = false;
        string key = $"{o.Asset}|{o.Prefab}|{o.Piece}|{string.Join(",", o.Overrides.Select(x => $"{x.Part}:{x.Visible}:{x.Transform}"))}|{(o.Prefab is null ? 0 : _lastPrefabStamp)}|{MeshKey(o.Mesh)}";
        string name = Sanitize($"{o.Name}_{o.Id}");
        if (node.Name.ToString() != name) { node.Name = name; wrote = true; }
        if (node.GetParent() != parentNode)
        {
            node.GetParent()?.RemoveChild(node);
            parentNode.AddChild(node);
            node.Owner = root;
            wrote = true;
        }
        else if (node.Owner != root) node.Owner = root;
        var expected = ExpectedTransform(o);
        if (node.Transform != expected) { node.Transform = expected; wrote = true; }
        if (node.Visible != o.Visible) { node.Visible = o.Visible; wrote = true; }
        if (wrote) EditCost.NodesWritten++;
        if (force || !_contentKeys.TryGetValue(o.Id, out var old) || old != key)
        {
            var notes = new List<string>();
            Node3D? content; Aabb bounds;
            if (o.Piece is { } piece) content = PieceViews.Build(piece, o.Mesh, out bounds, o.Transform.Scale);   // the edited shape, or the stock one (Edit mode, docs/mapmaker.md)
            else
            {
                var objParts = parts().GetValueOrDefault(o.Id) ?? new List<ResolvedPart>();
                var world = Session!.Document.World(o.Id);
                content = ContentFactory.BuildObject(objParts, world, out bounds, notes);
            }
            node.SetContent(content, bounds);
            EditCost.ContentRebuilt++;
            _contentKeys[o.Id] = key;
            foreach (var n in notes.Distinct().Take(3)) GD.PushWarning("Mapmaker: " + n);
        }
        _viewed[o.Id] = o;
        return node;
    }

    /// <summary>
    /// An SM1 map finished loading: redraw every piece. Owner's report (2026-11-05): placed prefabs showed only their outline boxes, because a map
    /// opened before its SM1 map was loaded (after a restart, or with no SM1 map opened) kept pieces that were built with nothing to draw.
    /// </summary>
    void OnSm1Changed()
    {
        if (Session is not null && MapHost.Root is not null) SyncView(true);
    }

    readonly HashSet<string> _loadingSm1 = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads, from the install, SM1 maps that placed pieces need (in the background, without showing them as the backdrop), then redraws.</summary>
    async void LoadMissingSm1()
    {
        foreach (var map in ContentFactory.MissingMaps.ToList())
        {
            ContentFactory.MissingMaps.Remove(map);
            if (MapHost.Sm1For(map) is not null || !_loadingSm1.Add(map)) continue;
            SetStatus($"Loading {map} from the install to draw its pieces...");
            try
            {
                var source = await System.Threading.Tasks.Task.Run(() => Sm1Source.Load(map));
                if (MapHost.Sm1For(map) is null) MapHost.Register(source, makeCurrent: false);
                SetStatus($"Loaded {map} from the install; its pieces are drawn.");
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { SetStatus($"Could not load {map} to draw its pieces: {e.Message}"); }
            finally { _loadingSm1.Remove(map); }
        }
    }

    public MapmakerObject? NodeOf(string id) => _nodes.GetValueOrDefault(id);

    public IEnumerable<string> SelectedIds() =>
        EditorInterface.Singleton.GetSelection().GetSelectedNodes().OfType<MapmakerObject>().Select(n => n.ObjectId).Where(i => Session?.Document.Find(i) is not null);

    static List<MapmakerObject> SelectedNodes() => EditorInterface.Singleton.GetSelection().GetSelectedNodes().OfType<MapmakerObject>().ToList();

    /// <summary>
    /// After an edit, undo or redo: every object that was selected before and is still in the map stays selected (owner 2026-11-27: a group moved, turned or scaled keeps
    /// all of its members selected, in Object mode by the gizmo or by typed values). Objects the step removed are not brought back; nothing is added that was not selected.
    /// </summary>
    void KeepSelected(List<MapmakerObject> before)
    {
        if (before.Count == 0 || Session is null) return;
        var sel = EditorInterface.Singleton.GetSelection();
        var now = sel.GetSelectedNodes().OfType<MapmakerObject>().ToHashSet();
        foreach (var n in before)
            if (!now.Contains(n) && GodotObject.IsInstanceValid(n) && n.IsInsideTree() && _nodes.GetValueOrDefault(n.ObjectId) == n && Session.Document.Find(n.ObjectId) is not null) sel.AddNode(n);
    }

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
    public void ReconcileDeferred() { _reconcilePending = false; long t = EditCost.Start(); Reconcile(); EditCost.End("Reconcile", t); }
    bool _reconcilePending;

    public void Reconcile()
    {
        if (_busy || Session is null) return;
        if (ReconcileParents()) return;   // a drag in the Scene dock: becomes set-parent (then the view is synced to the document)
        var doc = Session.Document;
        var ops = new List<JsonObject>();
        int displaced = 0;
        foreach (var o in doc.Objects)
        {
            if (!_nodes.TryGetValue(o.Id, out var node) || !GodotObject.IsInstanceValid(node)) continue;
            var expected = ExpectedTransform(o);
            if (node.Transform == expected) continue;
            // A node off its stored transform must be written back by the sync below even when its record does not change: a gizmo move that snaps back to the stored value, or a move
            // the core refuses, leaves the record as it was, and the view skips unchanged records (review of 919e4b4b).
            _viewed.Remove(o.Id);
            displaced++;
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
        if (ops.Count == 0)
        {
            if (displaced > 0) { var held = SelectedNodes(); SyncView(false); KeepSelected(held); }   // snapped back to the stored values: the nodes follow
            return;
        }
        var r = Session.Apply(ops);
        if (!r.Ok) SetStatus("Move rejected: " + string.Join("; ", r.Errors.Select(e => e.Message)));
        else Dirty = true;
        var keep = SelectedNodes();
        SyncView(false);   // snaps the nodes to the stored values
        KeepSelected(keep);
        RaiseDocChanged();
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

    /// <summary>The edited scene tab holds a Mapmaker root (the scene's own root, or one added under it).</summary>
    public bool EditedHasMap
    {
        get
        {
            var edited = EditorInterface.Singleton.GetEditedSceneRoot(); var r = MapHost.Root;
            return edited is not null && r is not null && GodotObject.IsInstanceValid(r) && r.IsInsideTree() && (edited == r || edited.IsAncestorOf(r));
        }
    }

    /// <summary>
    /// Before a placement: the scene tab being edited must hold a Mapmaker map (owner, 2026-11-18: any tab, never a forced jump back to the first). A tab
    /// without a Mapmaker root is offered one by the dock; a Mapmaker tab with no map yet starts an untitled one.
    /// </summary>
    public bool ReadyToPlace()
    {
        if (MapHost.StartingEmptyMap) return false;
        if (RefusePlacingInEditMode()) return false;
        if (!EditedHasMap)
        {
            // Placement targets the scene tab being edited; it never switches tabs (owner, 2026-11-18). A tab without a Mapmaker root is offered one.
            if (EditorInterface.Singleton.GetEditedSceneRoot() is null) { EnsureSceneRoot(); SetStatus("No scene is open. Opened the Mapmaker scene: click (or drop) again to place."); }
            else if (Dock is { } dock) dock.AskWrongTab(MapHost.Armed);
            else SetStatus("This scene tab has no Mapmaker map. Add one from the Mapmaker dock first.");
            return false;
        }
        if (Session is null) { NewMap("Untitled", resetSnap: false); SetStatus("This scene had no map, so a new untitled map was started here (Save As to keep it)."); }
        return true;
    }

    /// <summary>Adds a Mapmaker map to the edited scene tab (a child node under its root, saved with the scene as a plain node) and starts an untitled map in it.</summary>
    public async Task<bool> AddMapToCurrentScene()
    {
        var edited = EditorInterface.Singleton.GetEditedSceneRoot();
        if (edited is null) return false;
        if (!EditedHasMap)
        {
            var root = new MapmakerRoot { Name = "Map" };
            edited.AddChild(root, true);
            root.Owner = edited;
            for (int i = 0; i < 30 && MapHost.Root != root; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (MapHost.Root != root) return false;
        }
        if (Session is null) NewMap("Untitled", resetSnap: false);
        SetStatus("Added a Mapmaker map to this scene tab (untitled; Save As to keep it). Click again to place.");
        return true;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouse mouse && !_pushingWheel) _mouse = mouse.Position;
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } place && MapHost.Tool == Tool.Place && MapHost.Armed is not null
            && OverViewport(place.Position) && !ReadyToPlace()) { GetViewport().SetInputAsHandled(); return; }
        if (MapHost.Root is null) return;
        if (EditModeInput(@event)) return;   // Edit mode (Tab) takes its keys and an edit's buttons first: G / R / S, 1 / 2 / 3, Tab; its clicks come through _Forward3DGuiInput
        if (EditMode) return;   // none of Object mode's tools (box select, click select, group double-click, placing, Delete, Ctrl+D) work behind Edit mode
        GroupDoubleClick(@event);
        if (GhostWheelInput(@event) || BoxSelectInput(@event)) return;
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
            ApplyPick(src.ClickPick(ToNum(Frame.ToGame(o)), ToNum(Frame.ToGame(d))), button.ShiftPressed || button.CtrlPressed);
            if (MapHost.Root is { } r) EditorInterface.Singleton.GetSelection().RemoveNode(r); // the root's yellow box hid what was picked
            RefreshPicked();
            GetViewport().SetInputAsHandled();
        }
        else if (MapHost.Tool == Tool.Place && MapHost.Armed is { } item)
        {
            PlaceAt(item, GamePointOnRay(o, d), _ghostYaw, _ghostScale);   // the preview's own turn and size: the stamp is what the ghost shows
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
        var hit = src.ClickPick(ToNum(Frame.ToGame(o)), ToNum(Frame.ToGame(d)));
        if (hit is null && additive) return;
        if (nodes.Contains(root)) sel.RemoveNode(root);
        ApplyPick(hit, additive);
        RefreshPicked();
    }

    /// <summary>A click's pick: replaces the picks, or with Shift/Ctrl adds it (or removes it when already picked); nothing hit clears unless additive.</summary>
    static void ApplyPick(PickRef? hit, bool additive)
    {
        if (hit is not { } h) { if (!additive) MapHost.PickedSm1.Clear(); return; }
        if (additive && MapHost.PickedSm1.Remove(h)) return;
        if (!additive) MapHost.PickedSm1.Clear();
        MapHost.PickedSm1.Add(h);
    }

    public void SetTool(Tool tool)
    {
        if (tool == Tool.Place && RefusePlacingInEditMode()) { RaiseDocChanged(); return; }   // the dock's tool buttons show Select again
        MapHost.Tool = tool;
        if (tool != Tool.Place) MapHost.Armed = null;
        RaiseDocChanged();
    }

    public void RefreshPicked()
    {
        if (MapHost.Sm1 is { } src && MapHost.Root is { } root)
            root.ShowHighlights(MapHost.PickedSm1.Select(p => src.WorldBounds(p)).OfType<(Num.Vector3 Min, Num.Vector3 Max)>().Select(b => (new Vector3(b.Min.X, b.Min.Y, b.Min.Z), new Vector3(b.Max.X, b.Max.Y, b.Max.Z))));
        MapHost.NotifyPickedChanged();
    }

    /// <summary>Where a ray meets the SM1 backdrop's part boxes, else the ground (document z = 0), as a document-frame point.</summary>
    public Vector3 GamePointOnRay(Vector3 origin, Vector3 direction)
    {
        var og = Frame.ToGame(origin); var dg = Frame.ToGame(direction);
        if (MapHost.Root?.BackdropVisible == true && MapHost.Sm1 is { } src && src.RayPick(ToNum(og), ToNum(dg), out float t) >= 0) return og + dg * t;
        if (SolidHit(og, dg) is { } onPiece) return onPiece;   // a click on a block lands on it, so cover and platforms stack
        if (MathF.Abs(dg.Z) > 1e-4f && -og.Z / dg.Z is var tg && tg > 0) return og + dg * tg;
        return og + dg * 10f;
    }

    /// <summary>Puts an item down at a point (snapped like the preview), optionally turned about the up axis (degrees) and scaled; one undo step.</summary>
    public OpResult PlaceAt(LibraryItem item, Vector3 game, float? yawDegrees = null, float scale = 1f)
    {
        if (RefusePlacingInEditMode()) return OpResult.Fail("edit-mode", "Placing is off in Edit mode; press Tab for Object mode.");
        game = SnapPoint(game);
        if (item.Kind == LibraryKind.Piece) return yawDegrees is null && scale == 1f ? PlacePiece(item, game) : PlacePieceAt(item, game, yawDegrees, scale);
        var r = Do($"Place {item.Label}", PlaceOp(item, ToNum(Frame.Round(game)), yawDegrees, scale));
        if (r.Ok && r.Created.Count > 0) Select(r.Created);
        return r;
    }

    /// <summary>The objects of the open map that are placed copies of <paramref name="prefabId"/>.</summary>
    public List<string> PrefabUsers(string prefabId) => Session?.Document.Objects.Where(o => o.Prefab == prefabId).Select(o => o.Id).ToList() ?? new List<string>();

    /// <summary>How many objects deleting the placed copies of <paramref name="prefabId"/> removes (the copies and anything parented under them).</summary>
    public int PrefabUsersWithChildren(string prefabId) =>
        Session is null ? 0 : PrefabUsers(prefabId).SelectMany(id => Session.Document.Subtree(id)).Select(o => o.Id).Distinct().Count();

    /// <summary>Deletes a prefab from the library together with its placed copies in the open map, as one undo step (Ctrl+Z brings both back).</summary>
    public OpResult DeletePrefab(string prefabId)
    {
        var users = PrefabUsers(prefabId);
        var ops = new List<JsonObject>();
        if (users.Count > 0) ops.Add(Op("delete", ("ids", Strs(users))));
        ops.Add(Op("delete-prefab", ("prefab", prefabId)));
        return Do($"Delete prefab {prefabId}", ops.ToArray());
    }

    /// <summary>Objects of the open map that place the custom asset <paramref name="assetId"/> directly.</summary>
    public List<string> AssetUsers(string assetId) => Session?.Document.Objects.Where(o => o.Asset is { IsSm1: false } a && a.Id == assetId).Select(o => o.Id).ToList() ?? new List<string>();

    /// <summary>Deletes a custom asset with its placed copies in the open map, as one undo step (Ctrl+Z brings both back). Refused (nothing changes) while a prefab uses it.</summary>
    public OpResult DeleteAsset(string assetId)
    {
        var users = AssetUsers(assetId);
        var ops = new List<JsonObject>();
        if (users.Count > 0) ops.Add(Op("delete", ("ids", Strs(users))));
        ops.Add(Op("delete-asset", ("asset", assetId)));
        return Do($"Delete asset {assetId}", ops.ToArray());
    }

    public OpResult DeleteSelected() => Do("Delete", Op("delete", ("ids", Strs(SelectedIds()))));

    public OpResult DuplicateSelected()
    {
        var r = Do("Duplicate", Op("duplicate", ("ids", Strs(SelectedIds())), ("offset", Vec(Num.Vector3.Zero))));   // the copy stays exactly on the original (owner 2026-11-19)
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
            {
                _pushingWheel = true;   // the camera framing's own wheel input: the placement preview must not take it
                try
                {
                    GetTree().Root.PushInput(new InputEventMouseButton
                    { ButtonIndex = factor > 0 ? MouseButton.WheelDown : MouseButton.WheelUp, Pressed = pressed, Factor = Mathf.Abs(factor), Position = at, GlobalPosition = at });
                }
                finally { _pushingWheel = false; }
            }
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

