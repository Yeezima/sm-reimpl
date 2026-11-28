using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmMapmaker;
using Num = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>
/// Object / Edit mode (docs/mapmaker.md "Edit mode (editor)"; owner-approved brief 2026-11-25 "Blender-style controls"). Tab enters Edit mode on the pieces selected in Object mode (only
/// they are editable; any other object stays unselectable), 1 / 2 / 3 pick Vertex / Edge / Face, a click selects the component under the mouse (a face: the whole face, both triangles),
/// Shift adds or removes, A selects all, Alt+A none. G / R / S start a move / rotate / scale of the selection, X / Y / Z constrain it to the object's own axis, typed digits give the exact
/// amount (metres, degrees, factor); Enter or a left click applies, Esc or a right click cancels. The component gizmo (arrows: move along an axis, squares: scale along it, centre ring:
/// free move) does the same with the mouse. Every edit is a <see cref="MeshDrag"/> (preview from the start shapes, live dimensions) and ends in one <c>edit-mesh</c> batch, which is one
/// editor undo step (Ctrl+Z / Ctrl+Shift+Z).
/// </summary>
public partial class MapmakerPlugin
{
    public bool EditMode { get; private set; }
    public ComponentMode ComponentMode { get; private set; } = ComponentMode.Face;
    readonly List<string> _editIds = new();
    /// <summary>
    /// The pieces whose <c>_edit_lock_</c> Edit mode set (they had none), with the node it was set on: only these lose it, so a lock the maker set stays. Unlocking goes by this
    /// node, not through the edit set, so a piece that left the edit set (undo past its placement, a delete) or whose node is parked for undo is unlocked too.
    /// </summary>
    readonly Dictionary<string, ulong> _editLocked = new();
    readonly HashSet<string> _editableAtEntry = new();   // the edit set as it began: a piece an undo brings back (the same node) rejoins it
    readonly Dictionary<string, SortedSet<int>> _compSel = new();
    string? _activeEdit;
    (string? Path, string Name) _editDoc;
    readonly Dictionary<string, ulong> _editNodes = new();   // the node of each edited object when Edit mode began: opening a map makes new nodes (undo keeps them)

    /// <summary>Raised when the mode, the component selection or the live edit changes (the dock's mode bar and Dimensions row follow it).</summary>
    public event Action? EditChanged;

    /// <summary>The objects Edit mode works on (the pieces selected when it was entered), active first.</summary>
    public IReadOnlyList<string> EditIds => _activeEdit is null ? _editIds : _editIds.OrderBy(i => i == _activeEdit ? 0 : 1).ToList();
    public string? ActiveEditId => _activeEdit ?? _editIds.FirstOrDefault();
    public bool IsInEditSet(string id) => EditMode && _editIds.Contains(id);
    public IReadOnlyCollection<int> SelectedComponents(string id) => _compSel.TryGetValue(id, out var s) ? s : (IReadOnlyCollection<int>)Array.Empty<int>();

    // ---- the edit in progress ----
    MeshDrag? _drag;
    MapSession? _dragSession;
    int _dragVersion;
    MeshEditKind _dragKind;
    int? _dragAxis;
    string _typed = "";
    bool _dragGizmo;
    Vector2 _dragMouse0;
    Vector2 _dragPivot0;          // viewport pixels
    Vector3 _dragPivotGlobal;     // the active selection's centre at the start
    float _dragAngle, _dragLastAngle;
    Num.Vector3 _dragAmount;      // what the readout shows: move (m), scale factors, or (deg, 0, 0)

    public bool EditDragging => _drag is not null;
    public MeshDrag? ActiveDrag => _drag;
    /// <summary>Test aid: the hover check for keys is skipped (headless runs have no real mouse over the view).</summary>
    public bool EditKeysAnywhere { get; set; }

    /// <summary>
    /// The objects that were selected when Edit mode began (owner 2026-11-28: entering "locks the current object selection"): while Edit mode lasts, any other selection (a click on
    /// another object, a Scene dock row, a group row) is put back to exactly these, and leaving keeps them selected.
    /// </summary>
    readonly List<string> _lockedIds = new();
    /// <summary>Test aid: how often Edit mode put the locked selection back after something else changed it.</summary>
    public int SelectionRestores { get; private set; }
    /// <summary>Test aid: left presses in the 3D view that Edit mode used for a component pick or a gizmo handle (each counted once).</summary>
    public int EditClicks { get; private set; }
    /// <summary>Test aid: the objects Edit mode keeps selected.</summary>
    public IReadOnlyList<string> LockedIds => _lockedIds;

    /// <summary>
    /// Which of the selected objects become editable when Edit mode begins (the one place for that rule). Owner's decision 2026-11-28 (Blender's rule): every Build and gameplay
    /// piece of the selection; models, prefabs and SM1 pieces have no editable shape. A group is not editable by itself: only the pieces among its selected members are.
    /// </summary>
    static List<string> EditableOnEntry(MapDocument doc, IReadOnlyList<string> selected) => selected.Where(i => doc.Find(i)?.Piece is not null).ToList();

    // ---- entering and leaving ----

    /// <summary>Tab (or the mode menu): Edit mode on the selected pieces (false and a status line when none is selected), or back to Object mode with the same objects selected.</summary>
    public bool SetEditMode(bool on)
    {
        if (on == EditMode) return true;
        if (on)
        {
            if (Session is null) { SetStatus("Edit mode: open or make a map first."); return false; }
            var doc = Session.Document;
            var picked = SelectedIds().ToList();
            var pieces = EditableOnEntry(doc, picked);
            if (pieces.Count == 0)
            {
                SetStatus(picked.Count == 0 ? "Edit mode: select one or more Build or gameplay pieces first (Object mode), then press Tab."
                    : "Edit mode: only Build and gameplay pieces have an editable shape; the selection has none.");
                return false;
            }
            if (MapHost.Tool != Tool.Select) SetTool(Tool.Select);
            MapHost.Armed = null;   // nothing stays armed for placing (a drop on the view arms without the Place tool)
            CancelBox();
            _lockedIds.Clear(); _lockedIds.AddRange(picked);
            _lockedNodes.Clear();
            foreach (var id in picked) if (NodeOf(id) is { } ln) _lockedNodes[id] = ln.GetInstanceId();
            _lockedOther.Clear();   // anything else selected (the map's root, say) stays selected too
            foreach (var n in EditorInterface.Singleton.GetSelection().GetSelectedNodes()) if (n is not MapmakerObject) _lockedOther.Add(n.GetInstanceId());
            _editableAtEntry.Clear(); foreach (var id in pieces) _editableAtEntry.Add(id);
            _eatLeftRelease = _eatRightRelease = _stopLeftRelease = false;
            _editIds.Clear(); _editIds.AddRange(pieces); _compSel.Clear(); _activeEdit = pieces[0];
            _editDoc = (Session.Path, Session.Name);
            _editNodes.Clear();
            foreach (var id in pieces) if (NodeOf(id) is { } en) _editNodes[id] = en.GetInstanceId();
            EditMode = true;
            _editLocked.Clear();
            foreach (var id in _editIds) if (NodeOf(id) is { } n) { LockForEdit(id, n); n.UpdateGizmos(); }   // Godot's move gizmo leaves them alone while their shape is edited
            int skipped = picked.Count - pieces.Count;
            SetStatus($"Edit mode on {pieces.Count} piece(s){(skipped > 0 ? $" ({skipped} selected object(s) without an editable shape left out)" : "")}: 1 / 2 / 3 vertex, edge, face; click to select; G / R / S then X / Y / Z and a number; Tab back to Object mode.");
        }
        else
        {
            if (_drag is not null) CancelEdit();
            RestoreLockedSelection();   // the same objects stay selected in Object mode (owner 2026-11-28)
            EditMode = false;
            foreach (var id in _editLocked.Keys.ToList()) Unlock(id);
            foreach (var id in _editIds) if (NodeOf(id) is { } n && GodotObject.IsInstanceValid(n) && n.IsInsideTree()) n.UpdateGizmos();
            _editIds.Clear(); _compSel.Clear(); _activeEdit = null; _lockedIds.Clear(); _lockedNodes.Clear(); _lockedOther.Clear(); _editableAtEntry.Clear();
            _eatLeftRelease = _eatRightRelease = _stopLeftRelease = false;
            SetStatus("Object mode.");
        }
        UpdateEditLayer();
        RefreshEditViews();
        EditChanged?.Invoke();
        return true;
    }

    readonly Dictionary<string, ulong> _lockedNodes = new();
    readonly List<ulong> _lockedOther = new();   // other nodes selected at entry (not map objects, e.g. the map's root)   // the node of each locked object when Edit mode began (only those are put back: another map's o1 is not this one)

    /// <summary>The locked objects' nodes that still exist (the same node instances as when Edit mode began).</summary>
    List<MapmakerObject> LockedNodes()
    {
        var list = new List<MapmakerObject>();
        foreach (var id in _lockedIds)
            if (_lockedNodes.TryGetValue(id, out var iid) && GodotObject.InstanceFromId(iid) is MapmakerObject n && GodotObject.IsInstanceValid(n) && n.IsInsideTree()
                && _nodes.GetValueOrDefault(id) == n && Session?.Document.Find(id) is not null) list.Add(n);
        return list;
    }

    /// <summary>Puts the editor selection back to the locked objects when it differs (false when it already matched or none of them is left). Never loops: an equal selection is left alone.</summary>
    bool RestoreLockedSelection()
    {
        var want = LockedNodes().Cast<Node>().ToList();
        if (want.Count == 0) return false;
        foreach (var iid in _lockedOther) if (GodotObject.InstanceFromId(iid) is Node o && GodotObject.IsInstanceValid(o) && o.IsInsideTree()) want.Add(o);
        var sel = EditorInterface.Singleton.GetSelection();
        var now = sel.GetSelectedNodes();
        if (now.Count == want.Count && want.All(now.Contains)) return false;
        sel.Clear();
        foreach (var n in want) sel.AddNode(n);
        return true;
    }

    /// <summary>
    /// The editor selection changed while Edit mode is on (a click on a Scene dock row, a group row in the Mapmaker dock, anything that selects): Edit mode keeps its objects
    /// (owner 2026-11-28), so the selection goes straight back to them. The restore changes the selection once more; that second notice finds it equal and stops.
    /// </summary>
    void OnSelectionChangedForEdit()
    {
        if (!EditMode) return;
        if (!RestoreLockedSelection()) return;
        SelectionRestores++;
        SetStatus("Edit mode keeps its objects selected; press Tab (mouse over the 3D view) for Object mode to select others.");
    }

    /// <summary>
    /// Placing from the library (a click with an armed item, a drag from the list onto the view, arming a row or a Build preset, the Place tool, a check's one-click fix) is
    /// refused in Edit mode with a status line saying why (owner 2026-11-28: blocked on every route). True when refused.
    /// </summary>
    public bool RefusePlacingInEditMode()
    {
        if (!EditMode) return false;
        SetStatus("Edit mode reshapes the selected pieces; press Tab (mouse over the 3D view) for Object mode to place from the library.");
        return true;
    }

    /// <summary>Locks <paramref name="n"/> against Godot's move gizmo for the edit, remembering that Edit mode set it (a node that already had the lock keeps it afterwards).</summary>
    void LockForEdit(string id, Node n)
    {
        if (n.HasMeta("_edit_lock_")) return;
        n.SetMeta("_edit_lock_", true);
        _editLocked[id] = n.GetInstanceId();
    }

    /// <summary>Removes the lock Edit mode set on a piece's node (wherever that node is now: in the map, or parked for undo).</summary>
    void Unlock(string id)
    {
        if (!_editLocked.Remove(id, out var iid) || GodotObject.InstanceFromId(iid) is not Node3D n || !GodotObject.IsInstanceValid(n)) return;
        if (n.HasMeta("_edit_lock_")) n.RemoveMeta("_edit_lock_");
        if (n.IsInsideTree()) n.UpdateGizmos();
    }

    /// <summary>Test aid: how many pieces carry Edit mode's lock now.</summary>
    public int EditLocksHeld => _editLocked.Count;

    /// <summary>1 / 2 / 3. The selection converts the way Blender's does: what the selected vertices fully cover in the new mode.</summary>
    public void SetComponentMode(ComponentMode mode)
    {
        if (_drag is not null || mode == ComponentMode) { ComponentMode = mode; EditChanged?.Invoke(); return; }
        if (Session is not null)
            foreach (var id in _editIds.ToList())
            {
                if (!_compSel.TryGetValue(id, out var sel) || sel.Count == 0 || Session.Document.Find(id) is not { Piece: not null } o) continue;
                var mesh = EditMesh.Of(o);
                var verts = EditModeViews.VertexSet(mesh, ComponentMode, sel);
                IEnumerable<int> next = mode switch
                {
                    ComponentMode.Vertex => verts,
                    ComponentMode.Edge => Enumerable.Range(0, mesh.Edges.Count).Where(e => verts.Contains(mesh.Edges[e].A) && verts.Contains(mesh.Edges[e].B)),
                    _ => Enumerable.Range(0, mesh.Faces.Count).Where(f => mesh.Faces[f].All(verts.Contains)),
                };
                _compSel[id] = new SortedSet<int>(next);
            }
        ComponentMode = mode;
        RefreshEditViews();
        EditChanged?.Invoke();
    }

    /// <summary>Selects one component (<paramref name="add"/>: Shift, toggles it; otherwise it replaces the whole selection). Ids outside the edit set are refused.</summary>
    public bool SelectComponent(string id, int component, bool add = false)
    {
        if (!EditMode || !_editIds.Contains(id) || Session?.Document.Find(id) is not { Piece: not null } o) return false;
        var mesh = EditMesh.Of(o);
        int count = ComponentMode switch { ComponentMode.Vertex => mesh.Vertices.Count, ComponentMode.Edge => mesh.Edges.Count, _ => mesh.Faces.Count };
        if (component < 0 || component >= count) return false;
        if (!add) _compSel.Clear();
        var set = _compSel.TryGetValue(id, out var s) ? s : _compSel[id] = new SortedSet<int>();
        if (add && set.Contains(component)) set.Remove(component); else set.Add(component);
        _activeEdit = id;
        RefreshEditViews();
        EditChanged?.Invoke();
        return true;
    }

    public void ClearComponents() { _compSel.Clear(); RefreshEditViews(); EditChanged?.Invoke(); }

    /// <summary>A: every component of the current mode on every object in the edit set.</summary>
    public void SelectAllComponents()
    {
        if (Session is null) return;
        foreach (var id in _editIds)
            if (Session.Document.Find(id) is { Piece: not null } o)
            {
                var m = EditMesh.Of(o);
                _compSel[id] = new SortedSet<int>(Enumerable.Range(0, ComponentMode switch { ComponentMode.Vertex => m.Vertices.Count, ComponentMode.Edge => m.Edges.Count, _ => m.Faces.Count }));
            }
        RefreshEditViews(); EditChanged?.Invoke();
    }

    // ---- picking ----

    Num.Matrix4x4 WorldOf(string id) => Session!.Document.World(id);

    Vector3 UnitToGlobal(string id, Num.Vector3 u)
    {
        var w = Num.Vector3.Transform(u, WorldOf(id));
        var local = Frame.ToGodot(new Vector3(w.X, w.Y, w.Z));
        return MapHost.Root is { } r ? r.GlobalTransform * local : local;
    }

    /// <summary>Window coordinates to the 3D view's own pixels (what the camera projects to).</summary>
    Vector2 WindowToView(Vector2 window)
    {
        if (EditViewport is not { } vp || vp.GetParent() is not Control c) return window;
        var rect = c.GetGlobalRect();
        return (window - rect.Position) / rect.Size * new Vector2(vp.Size.X, vp.Size.Y);
    }

    bool Project(Vector3 global, out Vector2 px)
    {
        px = default;
        if (EditCamera is not { } cam || cam.IsPositionBehind(global)) return false;
        px = cam.UnprojectPosition(global);
        return true;
    }

    /// <summary>The face a ray (in the scene root's Godot frame, as <see cref="TryRay"/> gives it) hits first among the objects of the edit set, by the shape's triangles (either triangle of a quad is that face).</summary>
    public (string Id, int Face)? PickFaceByRay(Vector3 origin, Vector3 direction)
    {
        if (Session is null) return null;
        var og = Frame.ToGame(origin); var dg = Frame.ToGame(direction);
        (string, int)? best = null; float bestT = float.MaxValue;
        foreach (var id in _editIds)
        {
            if (Session.Document.Find(id) is not { Piece: not null, Visible: true } o || !Num.Matrix4x4.Invert(WorldOf(id), out var inv)) continue;
            var uo = Num.Vector3.Transform(new Num.Vector3(og.X, og.Y, og.Z), inv);
            var ud = Num.Vector3.TransformNormal(new Num.Vector3(dg.X, dg.Y, dg.Z), inv);
            var mesh = _drag?.Preview.GetValueOrDefault(id) ?? EditMesh.Of(o);
            var (v, idx, tf) = mesh.Triangulate();
            for (int t = 0; t < tf.Length; t++)
                if (RayTriangle(uo, ud, v[idx[3 * t]], v[idx[3 * t + 1]], v[idx[3 * t + 2]], out float dist) && dist < bestT) { bestT = dist; best = (id, tf[t]); }
        }
        return best;
    }

    static bool RayTriangle(Num.Vector3 o, Num.Vector3 d, Num.Vector3 a, Num.Vector3 b, Num.Vector3 c, out float t)
    {
        t = 0;
        var e1 = b - a; var e2 = c - a;
        var p = Num.Vector3.Cross(d, e2);
        float det = Num.Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1f / det;
        var s = o - a;
        float u = Num.Vector3.Dot(s, p) * inv;
        if (u < -1e-5f || u > 1 + 1e-5f) return false;
        var q = Num.Vector3.Cross(s, e1);
        float w = Num.Vector3.Dot(d, q) * inv;
        if (w < -1e-5f || u + w > 1 + 1e-5f) return false;
        t = Num.Vector3.Dot(e2, q) * inv;
        return t > 1e-6f;
    }

    /// <summary>The component under a point of the view (viewport pixels): a face by the ray, a vertex or an edge by the nearest one on screen (14 / 10 px).</summary>
    public (string Id, int Component)? PickComponentAt(Vector2 viewPx)
    {
        if (Session is null || EditCamera is not { } cam || MapHost.Root is not { } root) return null;
        if (ComponentMode == ComponentMode.Face)
        {
            var inv = root.GlobalTransform.AffineInverse();
            return PickFaceByRay(inv * cam.ProjectRayOrigin(viewPx), (inv.Basis * cam.ProjectRayNormal(viewPx)).Normalized()) is { } f ? (f.Id, f.Face) : null;
        }
        (string, int)? best = null; float bestD = ComponentMode == ComponentMode.Vertex ? 14f : 10f;
        foreach (var id in _editIds)
        {
            if (Session.Document.Find(id) is not { Piece: not null, Visible: true } o) continue;
            var mesh = EditMesh.Of(o);
            var px = new Vector2?[mesh.Vertices.Count];
            for (int i = 0; i < px.Length; i++) px[i] = Project(UnitToGlobal(id, mesh.Vertices[i]), out var p) ? p : null;
            if (ComponentMode == ComponentMode.Vertex)
            {
                for (int i = 0; i < px.Length; i++) if (px[i] is { } p && p.DistanceTo(viewPx) < bestD) { bestD = p.DistanceTo(viewPx); best = (id, i); }
            }
            else
                for (int e = 0; e < mesh.Edges.Count; e++)
                {
                    if (px[mesh.Edges[e].A] is not { } a || px[mesh.Edges[e].B] is not { } b) continue;
                    var ab = b - a; float t = ab.LengthSquared() < 1e-6f ? 0 : Mathf.Clamp((viewPx - a).Dot(ab) / ab.LengthSquared(), 0, 1);
                    float dd = (viewPx - (a + ab * t)).Length();
                    if (dd < bestD) { bestD = dd; best = (id, e); }
                }
        }
        return best;
    }

    /// <summary>A click in Edit mode: the component under the mouse, Shift toggling it; a click on nothing clears (without Shift).</summary>
    public void ClickComponent(Vector2 viewPx, bool add)
    {
        if (PickComponentAt(viewPx) is { } hit) { SelectComponent(hit.Id, hit.Component, add); SetStatus($"{Session!.Document.Get(hit.Id).Name}: {DescribeComponent(hit.Id, hit.Component)} {(add ? "toggled" : "selected")}."); }
        else if (!add) ClearComponents();
    }

    string DescribeComponent(string id, int c)
    {
        if (Session?.Document.Find(id) is not { Piece: not null } o) return "";
        var mesh = EditMesh.Of(o);
        return ComponentMode switch
        {
            ComponentMode.Vertex => $"vertex {c}",
            ComponentMode.Edge => $"edge {c} ({mesh.Edges[c].A}-{mesh.Edges[c].B})",
            _ => $"face {c}{(mesh.Topology.FaceNames[c] is { Length: > 0 } n ? " (" + n.Split('|')[0] + ")" : "")}",
        };
    }

    // ---- the edit itself ----

    List<(string Id, ComponentSelection Selection)> Targets() =>
        EditIds.Where(id => _compSel.TryGetValue(id, out var s) && s.Count > 0).Select(id => (id, new ComponentSelection(ComponentMode, _compSel[id].ToArray()))).ToList();

    static MeshEdit Identity(MeshEditKind kind, int? axis) => kind switch
    {
        MeshEditKind.Move => MeshEdit.Move(Num.Vector3.Zero, axis),
        MeshEditKind.Scale => MeshEdit.Scale(1f, axis),
        _ => MeshEdit.Rotate(axis ?? 2, 0f),
    };

    /// <summary>G / R / S (or a gizmo handle): starts an edit of the selected components. False (and a status line) when nothing is selected.</summary>
    public bool BeginEdit(MeshEditKind kind, int? axis = null, bool gizmo = false)
    {
        if (!EditMode || Session is null) return false;
        if (_drag is not null) CancelEdit();
        var targets = Targets();
        if (targets.Count == 0) { SetStatus("Edit mode: select a vertex, edge or face first."); return false; }
        try { _drag = new MeshDrag(Session, targets, Identity(kind, axis)); }
        catch (MapException e) { SetStatus($"Edit mode: {e.Code}: {e.Message}"); _drag = null; return false; }
        _dragSession = Session; _dragVersion = Session.Version;
        _dragKind = kind; _dragAxis = axis; _typed = ""; _dragGizmo = gizmo; _dragAmount = kind == MeshEditKind.Scale ? Num.Vector3.One : Num.Vector3.Zero;
        _dragMouse0 = WindowToView(_mouse);
        _dragPivotGlobal = SelectionCentreGlobal(_drag.ActiveId, _drag.Targets[0].Selection, _drag.Preview[_drag.ActiveId]);
        _dragPivot0 = Project(_dragPivotGlobal, out var pp) ? pp : _dragMouse0;
        _dragAngle = 0; _dragLastAngle = (_dragMouse0 - _dragPivot0).Angle();
        AfterEditUpdate();
        return true;
    }

    /// <summary>X / Y / Z during an edit: constrain to that axis of the object (pressing it again frees it).</summary>
    public void SetEditAxis(int axis)
    {
        if (_drag is null) return;
        _dragAxis = _dragAxis == axis && _dragKind != MeshEditKind.Rotate ? null : axis;
        Reapply();
    }

    /// <summary>Typed digits during an edit (Blender's numeric input): metres for a move, a factor for a scale, degrees for a rotation. Empty: the mouse decides again.</summary>
    public void SetTyped(string text) { if (_drag is null) return; _typed = text; Reapply(); }
    public string Typed => _typed;

    double? TypedValue => _typed.Length > 0 && _typed is not ("-" or "." or "-.") && double.TryParse(_typed, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    /// <summary>Applies an amount directly (the gizmo and tests): a move in metres along the axis (or the vector), a scale factor, a rotation in degrees.</summary>
    public bool SetEditAmount(Num.Vector3 amount)
    {
        if (_drag is null) return false;
        _dragAmount = amount;
        return Push();
    }

    void Reapply()
    {
        if (_drag is null) return;
        if (TypedValue is { } t)
        {
            float v = (float)t;
            _dragAmount = _dragKind switch
            {
                MeshEditKind.Move => AxisVector(_dragAxis ?? 0, v),
                MeshEditKind.Scale => _dragAxis is { } a ? AxisVector(a, v, 1f) : new Num.Vector3(v),
                _ => new Num.Vector3(v, 0, 0),
            };
            Push();
        }
        else UpdateEditFromMouse(WindowToView(_mouse));
    }

    static Num.Vector3 AxisVector(int a, float v, float rest = 0f) => new(a == 0 ? v : rest, a == 1 ? v : rest, a == 2 ? v : rest);

    MeshEdit CurrentEdit() => _dragKind switch
    {
        MeshEditKind.Move => MeshEdit.Move(_dragAmount, _dragAxis),
        MeshEditKind.Scale => MeshEdit.Scale(_dragAmount, _dragAxis),
        _ => MeshEdit.Rotate(_dragAxis ?? 2, _dragAmount.X),
    };

    bool Push()
    {
        if (_drag is null) return false;
        bool ok = _drag.Update(CurrentEdit());
        AfterEditUpdate();
        return ok;
    }

    /// <summary>The world (Godot, global) step of one sized metre along an object's own axis.</summary>
    Vector3 AxisStepGlobal(string id, int a)
    {
        var w = WorldOf(id);
        var o = Session!.Document.Get(id);
        float s = a == 0 ? o.Transform.Scale.X : a == 1 ? o.Transform.Scale.Y : o.Transform.Scale.Z;
        var row = a == 0 ? new Num.Vector3(w.M11, w.M12, w.M13) : a == 1 ? new Num.Vector3(w.M21, w.M22, w.M23) : new Num.Vector3(w.M31, w.M32, w.M33);
        row /= s == 0 ? 1 : s;
        var g = Frame.ToGodot(new Vector3(row.X, row.Y, row.Z));
        return MapHost.Root is { } r ? r.GlobalTransform.Basis * g : g;
    }

    /// <summary>The mouse during an edit (not while typed digits decide): the amount from how far it moved from where the edit began.</summary>
    public void UpdateEditFromMouse(Vector2 viewPx)
    {
        if (_drag is null || Session is null || TypedValue is not null) return;
        var id = _drag.ActiveId;
        var d = viewPx - _dragMouse0;
        switch (_dragKind)
        {
            case MeshEditKind.Move:
                if (_dragAxis is { } a)
                {
                    if (!Project(_dragPivotGlobal + AxisStepGlobal(id, a), out var tip)) return;
                    var s = tip - _dragPivot0;
                    float metres = s.LengthSquared() < 1e-6f ? 0 : d.Dot(s) / s.LengthSquared();
                    if (MapHost.SnapGrid) metres = Snap.Grid(metres, MapHost.GridStep);
                    _dragAmount = AxisVector(a, metres);
                }
                else if (EditCamera is { } cam && Num.Matrix4x4.Invert(WorldOf(id), out var inv))
                {
                    var n = -cam.GlobalTransform.Basis.Z;
                    var plane = new Plane(n, _dragPivotGlobal);
                    if (plane.IntersectsRay(cam.ProjectRayOrigin(_dragMouse0), cam.ProjectRayNormal(_dragMouse0)) is not { } p0
                        || plane.IntersectsRay(cam.ProjectRayOrigin(viewPx), cam.ProjectRayNormal(viewPx)) is not { } p1) return;
                    var delta = (MapHost.Root?.GlobalTransform.Basis.Inverse() ?? Basis.Identity) * (p1 - p0);
                    var dg = Frame.ToGame(delta);
                    var unit = Num.Vector3.TransformNormal(new Num.Vector3(dg.X, dg.Y, dg.Z), inv);
                    var sized = unit * Session.Document.Get(id).Transform.Scale;
                    if (MapHost.SnapGrid) sized = new Num.Vector3(Snap.Grid(sized.X, MapHost.GridStep), Snap.Grid(sized.Y, MapHost.GridStep), Snap.Grid(sized.Z, MapHost.GridStep));
                    _dragAmount = sized;
                }
                break;
            case MeshEditKind.Scale:
            {
                float f;
                if (_dragGizmo && _dragAxis is { } ga && Project(_dragPivotGlobal + AxisStepGlobal(id, ga), out var tip))
                {
                    var dir = (tip - _dragPivot0).Normalized();
                    float d0 = (_dragMouse0 - _dragPivot0).Dot(dir);
                    f = MathF.Abs(d0) < 1f ? 1f : (viewPx - _dragPivot0).Dot(dir) / d0;
                }
                else
                {
                    float d0 = (_dragMouse0 - _dragPivot0).Length();
                    f = d0 < 1f ? 1f : (viewPx - _dragPivot0).Length() / d0;
                }
                f = MathF.Round(f, 3);
                if (MathF.Abs(f) < 0.001f) f = 0.001f;
                _dragAmount = _dragAxis is { } sa ? AxisVector(sa, f, 1f) : new Num.Vector3(f);
                break;
            }
            default:
            {
                float now = (viewPx - _dragPivot0).Angle();
                float step = Mathf.Wrap(now - _dragLastAngle, -Mathf.Pi, Mathf.Pi);
                _dragLastAngle = now; _dragAngle += step;
                // counter-clockwise on screen turns counter-clockwise about an axis that points at the camera (screen y runs down)
                var axisG = AxisStepGlobal(id, _dragAxis ?? 2);
                bool toward = EditCamera is { } cam && axisG.Dot(cam.GlobalPosition - _dragPivotGlobal) > 0;
                float deg = Mathf.RadToDeg(_dragAngle) * (toward ? -1f : 1f);
                if (MapHost.SnapAngle) deg = Snap.Angle(deg, MathF.Max(MapHost.AngleStep, 1f));
                _dragAmount = new Num.Vector3(MathF.Round(deg, 3), 0, 0);
                break;
            }
        }
        Push();
    }

    /// <summary>Enter or a left click: the edit becomes one <c>edit-mesh</c> batch (one undo step). A map that changed meanwhile refuses it (<c>stale-edit</c>).</summary>
    public OpResult CommitEdit()
    {
        if (_drag is null) return OpResult.Fail("no-edit", "No edit is in progress.");
        var drag = _drag; _drag = null;
        bool stale = !ReferenceEquals(Session, _dragSession) || Session is null || Session.Version != _dragVersion;
        var error = drag.Error;
        bool nothing = IsIdentity(drag.Edit);
        var op = drag.ToOp();
        var name = Session?.Document.Find(drag.ActiveId)?.Name ?? drag.ActiveId;
        drag.Cancel();
        if (stale || error is not null || nothing)
        {
            RestoreDrawn(drag.Targets.Select(t => t.Id));
            var why = stale ? OpResult.Fail("stale-edit", "The map changed while the edit was in progress; nothing was applied.", "edit-mesh")
                : error is not null ? OpResult.Fail("bad-mesh", error, "edit-mesh") : OpResult.Fail("no-change", "The edit changed nothing.", "edit-mesh");
            SetStatus($"Edit not applied: {why.Errors[0].Message}");
            AfterEditUpdate();
            return why;
        }
        string what = drag.Edit.Kind switch { MeshEditKind.Move => "Move", MeshEditKind.Scale => "Scale", _ => "Rotate" };
        var r = Do($"Edit mesh: {what} {ComponentSelection.ModeName(ComponentMode)} of {name}{(drag.Targets.Count > 1 ? $" (+{drag.Targets.Count - 1})" : "")}", op);
        if (!r.Ok) RestoreDrawn(drag.Targets.Select(t => t.Id));
        AfterEditUpdate();
        return r;
    }

    static bool IsIdentity(MeshEdit e) => e.Kind switch
    {
        MeshEditKind.Move => (e.Axis is { } a ? ComponentEdit.Get(e.Amount, a) : e.Amount.Length()) == 0f,
        MeshEditKind.Scale => e.Axis is { } a ? ComponentEdit.Get(e.Amount, a) == 1f : e.Amount == Num.Vector3.One,
        _ => e.Degrees == 0f,
    };

    /// <summary>Esc or a right click: the edit ends with nothing changed.</summary>
    public void CancelEdit()
    {
        if (_drag is null) return;
        var drag = _drag; _drag = null;
        drag.Cancel();
        RestoreDrawn(drag.Targets.Select(t => t.Id));
        SetStatus("Edit cancelled; nothing changed.");
        AfterEditUpdate();
    }

    /// <summary>Draws the objects from the document again (a preview is dropped).</summary>
    void RestoreDrawn(IEnumerable<string> ids)
    {
        foreach (var id in ids) _contentKeys.Remove(id);
        SyncView(false);
    }

    /// <summary>Redraws the previewed pieces and the overlays, then tells the dock.</summary>
    void AfterEditUpdate()
    {
        if (_drag is not null && Session is not null)
            foreach (var (id, mesh) in _drag.Preview)
                if (NodeOf(id) is { } n && Session.Document.Find(id)?.Piece is { } piece)
                {
                    var content = PieceViews.Build(piece, mesh, out var b, Session.Document.Get(id).Transform.Scale);
                    n.SetContent(content, b);   // the content key stays the document's, so a sync meanwhile keeps the preview; cancel and commit redraw
                }
        RefreshEditViews();
        EditChanged?.Invoke();
    }

    // ---- dimensions (the brief's live readout) ----

    /// <summary>X / Y / Z in metres of a piece's resulting shape (its own axes, scale applied): the live preview during an edit, else the stored shape; null for an object without one.</summary>
    public Num.Vector3? DimsOf(string id)
    {
        if (_drag is not null && _drag.Preview.ContainsKey(id)) return _drag.Dims(id);
        return Session?.Document.Find(id) is { Piece: not null } o ? MeshDims.Of(o) : null;
    }

    /// <summary>The readout's first line: "Wall · X 5.00 m   Y 2.00 m   Z 1.00 m" for the active object.</summary>
    public string? Readout
    {
        get
        {
            var id = _drag?.ActiveId ?? ActiveEditId;
            if (id is null || Session?.Document.Find(id) is not { } o || DimsOf(id) is not { } d) return null;
            return $"{o.Name} · {MeshDims.Label(d)}";
        }
    }

    /// <summary>The readout's second line during an edit: what it does and how it ends.</summary>
    public string? ReadoutDetail
    {
        get
        {
            if (_drag is null) return EditMode ? $"Edit mode · {ComponentSelection.ModeName(ComponentMode)} · G move  R rotate  S scale  ·  Tab: Object mode" : null;
            string ax = _dragAxis is { } a ? MeshEdit.AxisName(a).ToUpperInvariant() : "";
            string amount = _dragKind switch
            {
                MeshEditKind.Move => _dragAxis is { } m ? $"Move {ax} {ComponentEdit.Get(_dragAmount, m).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)} m"
                    : string.Create(CultureInfo.InvariantCulture, $"Move {_dragAmount.X:+0.00;-0.00;0.00}, {_dragAmount.Y:+0.00;-0.00;0.00}, {_dragAmount.Z:+0.00;-0.00;0.00} m"),
                MeshEditKind.Scale => _dragAxis is { } s ? string.Create(CultureInfo.InvariantCulture, $"Scale {ax} x{ComponentEdit.Get(_dragAmount, s):0.000}") : string.Create(CultureInfo.InvariantCulture, $"Scale x{_dragAmount.X:0.000}"),
                _ => string.Create(CultureInfo.InvariantCulture, $"Rotate {(_dragAxis is { } r ? MeshEdit.AxisName(r).ToUpperInvariant() : "Z")} {_dragAmount.X:0.0} deg"),
            };
            string typed = _typed.Length > 0 ? $"  [typed {_typed}]" : "";
            string err = _drag.Error is { } e ? "  · refused: " + e : "";
            return $"{amount}{typed}  ·  Enter / click apply, Esc / right click cancel{err}";
        }
    }

    // ---- views ----

    readonly Dictionary<string, int> _fillTriangles = new();
    /// <summary>Test aid: how many triangles the face highlight of an object has (a selected wall face: 2).</summary>
    public int HighlightTriangles(string id) => _fillTriangles.GetValueOrDefault(id);

    /// <summary>Rebuilds the overlays of the edit set (and removes stale ones elsewhere).</summary>
    public void RefreshEditViews()
    {
        foreach (var (id, n) in _nodes)
            if (GodotObject.IsInstanceValid(n) && n.GetNodeOrNull(EditModeViews.NodeName) is Node old && (!EditMode || !_editIds.Contains(id))) { n.RemoveChild(old); old.QueueFree(); }
        _fillTriangles.Clear();
        if (!EditMode || Session is null) { _editLayer?.QueueRedraw(); return; }
        foreach (var id in _editIds)
        {
            if (NodeOf(id) is not { } n || !GodotObject.IsInstanceValid(n) || Session.Document.Find(id) is not { Piece: not null } o) continue;
            if (n.GetNodeOrNull(EditModeViews.NodeName) is Node old) { n.RemoveChild(old); old.QueueFree(); }
            var mesh = _drag?.Preview.GetValueOrDefault(id) ?? EditMesh.Of(o);
            var overlay = EditModeViews.Build(mesh, ComponentMode, SelectedComponents(id), id == ActiveEditId, out int tris);
            _fillTriangles[id] = tris;
            n.AddChild(overlay);   // no owner: never saved
        }
        _editLayer?.QueueRedraw();
    }

    /// <summary>After any document change (an edit, undo, redo, another tool): objects that are gone leave the edit set, ids beyond a changed shape are dropped.</summary>
    void OnEditDocChanged()
    {
        if (!EditMode) return;
        // another map was opened or started: leave Edit mode (undo keeps the path and name; a first save gives an unsaved map its path)
        if (Session is null || Session.Name != _editDoc.Name || (Session.Path is not null && _editDoc.Path is not null && !string.Equals(Session.Path, _editDoc.Path, StringComparison.OrdinalIgnoreCase)))
        { SetEditMode(false); return; }
        _editDoc = (Session.Path, Session.Name);
        if (_editIds.Any(id => _editNodes.TryGetValue(id, out var was) && NodeOf(id) is { } now && GodotObject.IsInstanceValid(now) && now.GetInstanceId() != was)) { SetEditMode(false); return; }
        if (_drag is not null && (!ReferenceEquals(Session, _dragSession) || Session.Version != _dragVersion)) CancelEdit();
        foreach (var gone in _editIds.Where(id => Session.Document.Find(id)?.Piece is null).ToList()) { _editIds.Remove(gone); Unlock(gone); }
        // a piece of the edit set that an undo or redo brings back with its own node rejoins it (and is locked again below)
        bool rejoined = false;
        foreach (var id in _lockedIds)
            if (_editableAtEntry.Contains(id) && !_editIds.Contains(id) && Session.Document.Find(id)?.Piece is not null && NodeOf(id) is { } back
                && _editNodes.TryGetValue(id, out var was0) && back.GetInstanceId() == was0 && back.IsInsideTree()) { _editIds.Add(id); rejoined = true; }
        if (_editIds.Count == 0) { SetEditMode(false); return; }
        if (rejoined) RestoreLockedSelection();   // and is selected again, as when Edit mode began
        if (_activeEdit is not null && !_editIds.Contains(_activeEdit)) _activeEdit = _editIds[0];
        foreach (var id in _compSel.Keys.ToList())
        {
            if (!_editIds.Contains(id)) { _compSel.Remove(id); continue; }
            var mesh = EditMesh.Of(Session.Document.Get(id));
            int count = ComponentMode switch { ComponentMode.Vertex => mesh.Vertices.Count, ComponentMode.Edge => mesh.Edges.Count, _ => mesh.Faces.Count };
            _compSel[id].RemoveWhere(c => c >= count);
        }
        foreach (var id in _editIds) if (NodeOf(id) is { } n) LockForEdit(id, n);
        RefreshEditViews();
        EditChanged?.Invoke();
    }

    // ---- the 2D layer (gizmo + readout), drawn inside the 3D view so its picture holds them ----

    Control? _editLayer;

    EditModeViews.GizmoLayout? Gizmo()
    {
        if (!EditMode || Session is null) return null;
        string? id = _drag?.ActiveId ?? (ActiveEditId is { } a && SelectedComponents(a).Count > 0 ? a : _editIds.FirstOrDefault(i => SelectedComponents(i).Count > 0));
        if (id is null || Session.Document.Find(id) is not { Piece: not null } o) return null;
        var mesh = _drag?.Preview.GetValueOrDefault(id) ?? EditMesh.Of(o);
        var sel = _drag?.Targets.FirstOrDefault(t => t.Id == id).Selection ?? new ComponentSelection(ComponentMode, SelectedComponents(id).ToArray());
        if (sel.Ids.Count == 0) return null;
        var c = SelectionCentreGlobal(id, sel, mesh);
        if (!Project(c, out var p)) return null;
        var axes = new Vector2[3];
        for (int k = 0; k < 3; k++)
            axes[k] = Project(c + AxisStepGlobal(id, k).Normalized() * 0.01f * MathF.Max(1f, (EditCamera!.GlobalPosition - c).Length()), out var q) && (q - p).Length() > 0.5f ? (q - p).Normalized() : Vector2.Zero;
        return new EditModeViews.GizmoLayout(p, axes, 80f * EditorInterface.Singleton.GetEditorScale());
    }

    Vector3 SelectionCentreGlobal(string id, ComponentSelection sel, EditMesh mesh)
    {
        int[] vs;
        try { vs = sel.VertexSet(mesh); } catch (MapException) { vs = Enumerable.Range(0, mesh.Vertices.Count).ToArray(); }
        var lo = new Num.Vector3(float.MaxValue); var hi = new Num.Vector3(float.MinValue);
        foreach (var i in vs) { lo = Num.Vector3.Min(lo, mesh.Vertices[i]); hi = Num.Vector3.Max(hi, mesh.Vertices[i]); }
        return UnitToGlobal(id, (lo + hi) / 2);
    }

    void UpdateEditLayer()
    {
        var vp = EditViewport;
        bool want = EditMode && vp is not null;
        if (!want)
        {
            if (_editLayer is not null && GodotObject.IsInstanceValid(_editLayer)) { _editLayer.GetParent()?.RemoveChild(_editLayer); _editLayer.QueueFree(); }
            _editLayer = null;
            return;
        }
        if (_editLayer is null || !GodotObject.IsInstanceValid(_editLayer))
        {
            _editLayer = new Control { Name = "MapmakerEditLayer", MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 50 };
            _editLayer.Draw += DrawEditLayer;
        }
        if (_editLayer.GetParent() != vp) { _editLayer.GetParent()?.RemoveChild(_editLayer); vp!.AddChild(_editLayer); }
        _editLayer.Position = Vector2.Zero; _editLayer.Size = new Vector2(vp!.Size.X, vp.Size.Y);
        _editLayer.QueueRedraw();
    }

    void DrawEditLayer()
    {
        if (_editLayer is null || !EditMode) return;
        float scale = EditorInterface.Singleton.GetEditorScale();
        if (Gizmo() is { } g) EditModeViews.DrawGizmo(_editLayer, g, _drag is null ? null : (_dragKind, _dragAxis), scale);
        if (Readout is { } title) EditModeViews.DrawReadout(_editLayer, title, ReadoutDetail, scale);
    }

    /// <summary>Test aid: the gizmo handle at a point of the view, as a click would find it.</summary>
    public (MeshEditKind Kind, int? Axis)? GizmoHandleAt(Vector2 viewPx) => Gizmo() is { } g ? EditModeViews.HitGizmo(g, viewPx, EditorInterface.Singleton.GetEditorScale()) : null;
    /// <summary>Test aid: the gizmo's layout (pivot and axis directions in view pixels).</summary>
    public EditModeViews.GizmoLayout? GizmoLayoutNow => Gizmo();

    /// <summary>Starts a gizmo drag as a press at <paramref name="viewPx"/> would (false when no handle is there).</summary>
    public bool PressGizmo(Vector2 viewPx)
    {
        if (_drag is not null || GizmoHandleAt(viewPx) is not { } h) return false;
        var keep = _mouse;
        _mouse = ViewToWindow(viewPx);
        bool ok = BeginEdit(h.Kind, h.Axis, gizmo: true);
        if (!ok) _mouse = keep;
        return ok;
    }

    Vector2 ViewToWindow(Vector2 viewPx)
    {
        if (EditViewport is not { } vp || vp.GetParent() is not Control c) return viewPx;
        var rect = c.GetGlobalRect();
        return rect.Position + viewPx / new Vector2(vp.Size.X, vp.Size.Y) * rect.Size;
    }

    /// <summary>Test aid: moves the mouse during an edit (view pixels), as a real motion event would.</summary>
    public void MoveEditMouse(Vector2 viewPx) { _mouse = ViewToWindow(viewPx); UpdateEditFromMouse(viewPx); }

    // ---- input ----
    //
    // Two routes (owner's repair brief 2026-11-28). Clicks that pick a component or a gizmo handle arrive through _Forward3DGuiInput: Godot hands it every event its 3D input surface
    // gets (forwarding is always on), with the camera of the view that got it, and a Stop there is the only consumer, so Godot neither picks an object nor starts its box behind it.
    // Keys, and the buttons and motion of an edit in progress (which may leave the view), stay in _Input with the hover check below.

    Camera3D? _editCam;          // the camera of the 3D view the mouse last worked in (split views: one of up to four)
    bool _eatLeftRelease, _eatRightRelease;   // a press _Input used (before any control): its release is used there too, so Godot never sees half a click
    bool _stopLeftRelease;                    // a press the 3D view forwarded and Edit mode used: its release is stopped there too (Godot's GUI still sees it and lets go of the mouse)

    /// <summary>The camera Edit mode picks, projects and draws with: the one of the view that got the last event (the first view's until then).</summary>
    Camera3D? EditCamera => _editCam is { } c && GodotObject.IsInstanceValid(c) && c.IsInsideTree() ? c : Viewport3D?.GetCamera3D();
    SubViewport? EditViewport => EditCamera?.GetViewport() as SubViewport ?? Viewport3D;

    /// <summary>
    /// The mouse (window coordinates) is over one of the 3D views' own input surface, not over a menu, button or popup drawn on it. Godot's view is a SubViewportContainer (the
    /// picture) with a sibling Control on top that takes the input (verified in Godot 4.7.2: the hovered control is that sibling); the shipped check only accepted the container and
    /// its children, so the real mouse was never "over the view". Remembers that view's camera (not during an edit, whose pixels belong to the view it began in).
    /// </summary>
    bool OverEditSurface(Vector2 window)
    {
        var hovered = GetViewport().GuiGetHoveredControl();
        for (int i = 0; i < 4; i++)
        {
            if (EditorInterface.Singleton.GetEditorViewport3D(i) is not { } vp || vp.GetParent() is not Control container || !container.IsVisibleInTree() || !container.GetGlobalRect().HasPoint(window)) continue;
            bool surface = hovered is null || hovered == container || container.IsAncestorOf(hovered) || hovered.GetParent() == container.GetParent();
            if (surface && _drag is null && vp.GetCamera3D() is { } cam) _editCam = cam;
            return surface;
        }
        return false;
    }

    /// <summary>Test aid: <see cref="OverEditSurface"/> for a window point (after a real mouse motion there).</summary>
    public bool TestOverEditSurface(Vector2 window) => OverEditSurface(window);

    /// <summary>An event position in the forwarding view's input surface to that view's pixels (what its camera projects to; the same unless the view renders at half resolution).</summary>
    static Vector2 SurfaceToView(Camera3D cam, Vector2 local)
    {
        if (cam.GetViewport() is not SubViewport vp || vp.GetParent() is not Control c || c.Size.X <= 0 || c.Size.Y <= 0) return local;
        return local * new Vector2(vp.Size.X, vp.Size.Y) / c.Size;
    }

    /// <summary>Alt + left drag orbits the camera in some navigation setups (3-button emulation, the Maya and Modo schemes): then it is the camera's.</summary>
    static bool AltLeftNavigates()
    {
        var s = EditorInterface.Singleton.GetEditorSettings();
        bool emulate = s.HasSetting("editors/3d/navigation/emulate_3_button_mouse") && s.GetSetting("editors/3d/navigation/emulate_3_button_mouse").AsBool();
        int scheme = s.HasSetting("editors/3d/navigation/navigation_scheme") ? s.GetSetting("editors/3d/navigation/navigation_scheme").AsInt32() : 0;
        return emulate || scheme != 0;
    }

    public override int _Forward3DGuiInput(Camera3D viewportCamera, InputEvent @event) => (int)ForwardEditInput(viewportCamera, @event);

    /// <summary>
    /// Edit mode's share of what Godot's 3D input surface receives. A left press picks the component under it (or grabs a gizmo handle) in the view and camera that got it, and is
    /// consumed (Stop) together with its release. Everything else (wheel, middle and right buttons, motion, keys) goes on to Godot, so the camera navigates as always.
    /// </summary>
    EditorPlugin.AfterGuiInput ForwardEditInput(Camera3D cam, InputEvent e)
    {
        if (!EditMode || Session is null) return EditorPlugin.AfterGuiInput.Pass;
        if (_drag is null && e is InputEventMouse) _editCam = cam;
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } b when _drag is null:
                if (b.AltPressed && AltLeftNavigates()) return EditorPlugin.AfterGuiInput.Pass;
                var px = SurfaceToView(cam, b.Position);
                EditClicks++;
                if (!PressGizmo(px)) ClickComponent(px, b.ShiftPressed);
                _stopLeftRelease = true;
                return EditorPlugin.AfterGuiInput.Stop;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _stopLeftRelease:
                // The release of that press. It comes here (not to _Input) so Godot's GUI sees it and lets go of the mouse; a gizmo drag ends with it, wherever the mouse is.
                _stopLeftRelease = false;
                if (_drag is not null && _dragGizmo) CommitEdit();
                return EditorPlugin.AfterGuiInput.Stop;
            case InputEventMouseMotion when _drag is not null && _dragGizmo:
                return EditorPlugin.AfterGuiInput.Stop;   // Godot's own gizmo hover stays out of a handle drag
        }
        return EditorPlugin.AfterGuiInput.Pass;
    }

    /// <summary>Edit mode's share of the input (called first from _Input, before any control): Tab, keys, and the buttons and motion of an edit in progress. True when it was used.</summary>
    bool EditModeInput(InputEvent e)
    {
        bool hovered = EditKeysAnywhere || OverEditSurface(_mouse);
        if (!EditMode)
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Tab, CtrlPressed: false, AltPressed: false, ShiftPressed: false } tab && hovered && EditedHasMap && !TextHasFocus() && !FreelookActive())
            {
                if (!tab.Echo) SetEditMode(true);   // a held Tab's repeats are used too: they would otherwise move the keyboard focus through the editor
                GetViewport().SetInputAsHandled();
                return true;
            }
            return false;
        }
        switch (e)
        {
            case InputEventMouseMotion m:
                if (_drag is null) return false;
                UpdateEditFromMouse(WindowToView(m.Position));
                if (_dragGizmo) GetViewport().SetInputAsHandled();
                return _dragGizmo;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } when _drag is not null:
                // during an edit a left press anywhere applies it (Blender); used here, before any control, with its release
                CommitEdit();
                _eatLeftRelease = true;
                GetViewport().SetInputAsHandled();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _drag is not null && _dragGizmo && !_stopLeftRelease:
                CommitEdit();   // a gizmo drag begun without the view's press (the test aid's PressGizmo); a real one ends in ForwardEditInput
                GetViewport().SetInputAsHandled();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _eatLeftRelease:
                _eatLeftRelease = false;   // the release of a click that applied an edit: nobody else gets half a click
                GetViewport().SetInputAsHandled();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } when _drag is not null:
                CancelEdit();
                _eatRightRelease = true;
                GetViewport().SetInputAsHandled();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false } when _eatRightRelease:
                _eatRightRelease = false;
                GetViewport().SetInputAsHandled();
                return true;
            case InputEventKey { Pressed: true } k:
                if (!hovered && _drag is null) return false;
                if (_drag is null && TextHasFocus()) return false;   // typing in a field (a name, a number, a search) is the field's, Tab included; a drag keeps its keys
                if (_drag is null && FreelookActive()) return false;   // freelook (right mouse held, or Shift+F): every key is the camera's, Shift+F again included
                if (IsFreelookToggle(k)) { if (_drag is not null) CancelEdit(); return false; }   // Shift+F starts freelook: never swallowed, an edit in progress is cancelled first
                if (HandleEditKey(k)) { GetViewport().SetInputAsHandled(); return true; }
                if (k is { Echo: true, Keycode: Key.Tab, CtrlPressed: false, AltPressed: false, ShiftPressed: false, MetaPressed: false }) { GetViewport().SetInputAsHandled(); return true; }   // a held Tab's repeats
                return false;
        }
        return false;
    }

    /// <summary>Test aid: treat the camera as in freelook (headless Godot never captures the mouse).</summary>
    public bool FreelookForTest { get; set; }

    /// <summary>
    /// Godot's 3D freelook is on: the right button held, or Shift+F's toggle (Godot captures the mouse while freelook runs, in Godot 4.7.2 as before). Then the movement keys
    /// (W A S D Q E, and Shift+F to end it) are the camera's and Edit mode takes none.
    /// </summary>
    bool FreelookActive() => FreelookForTest || Input.IsMouseButtonPressed(MouseButton.Right) || Input.MouseMode == Input.MouseModeEnum.Captured;

    /// <summary>Godot's freelook toggle at its default, Shift+F (a rebound toggle is not read: EditorSettings does not expose its shortcuts to plugins).</summary>
    static bool IsFreelookToggle(InputEventKey k) => k is { Keycode: Key.F, ShiftPressed: true, CtrlPressed: false, AltPressed: false, MetaPressed: false };

    /// <summary>True when keyboard focus is in a text field (LineEdit, including a SpinBox's, or TextEdit) of the editor or the Mapmaker dock's window.</summary>
    bool TextHasFocus()
    {
        static bool IsText(Control? c) => c is LineEdit or TextEdit;
        if (IsText(GetViewport()?.GuiGetFocusOwner())) return true;
        return _dock is { } d && GodotObject.IsInstanceValid(d) && d.GetViewport() is { } dv && dv != GetViewport() && IsText(dv.GuiGetFocusOwner());
    }

    /// <summary>Test aid: an input event through Edit mode's handler, as the 3D view would pass it.</summary>
    public bool TestInput(InputEvent e) => EditModeInput(e);

    /// <summary>One key in Edit mode (also used by the test aid). True when it was Edit mode's.</summary>
    public bool HandleEditKey(InputEventKey k)
    {
        var key = k.Keycode;
        bool plain = !k.CtrlPressed && !k.AltPressed && !k.MetaPressed;
        if (_drag is not null)
        {
            if (k.CtrlPressed && key == Key.Z) { CancelEdit(); return true; }
            switch (key)
            {
                case Key.Escape: CancelEdit(); return true;
                case Key.Enter or Key.KpEnter: CommitEdit(); return true;
                case Key.Tab: CancelEdit(); SetEditMode(false); return true;
                case Key.X or Key.Y or Key.Z when plain: SetEditAxis(key == Key.X ? 0 : key == Key.Y ? 1 : 2); return true;
                case Key.G or Key.R or Key.S when plain:
                {
                    var kind = key == Key.G ? MeshEditKind.Move : key == Key.R ? MeshEditKind.Rotate : MeshEditKind.Scale;
                    if (kind != _dragKind) { int? axis = _dragAxis; BeginEdit(kind, axis); }
                    return true;
                }
                case Key.Backspace: if (_typed.Length > 0) SetTyped(_typed[..^1]); return true;
                case Key.Minus or Key.KpSubtract: SetTyped(_typed.StartsWith('-') ? _typed[1..] : "-" + _typed); return true;
                case Key.Period or Key.KpPeriod or Key.Comma: if (!_typed.Contains('.')) SetTyped(_typed + "."); return true;
            }
            if (plain && key >= Key.Key0 && key <= Key.Key9) { SetTyped(_typed + (char)('0' + (key - Key.Key0))); return true; }
            if (plain && key >= Key.Kp0 && key <= Key.Kp9) { SetTyped(_typed + (char)('0' + (key - Key.Kp0))); return true; }
            return k.Echo || plain;   // other plain keys are swallowed during an edit (no tool shortcut fires mid-edit)
        }
        if (k.Echo) return false;
        if (key == Key.Tab && plain && !k.ShiftPressed) { SetEditMode(false); return true; }
        if (AltDeselect(k)) return true;
        if (!plain) return false;   // Ctrl+Z / Ctrl+Shift+Z / Ctrl+S stay the editor's
        switch (key)
        {
            case Key.Key1 when !k.ShiftPressed: SetComponentMode(ComponentMode.Vertex); return true;
            case Key.Key2 when !k.ShiftPressed: SetComponentMode(ComponentMode.Edge); return true;
            case Key.Key3 when !k.ShiftPressed: SetComponentMode(ComponentMode.Face); return true;
            case Key.G: BeginEdit(MeshEditKind.Move); return true;
            case Key.R: BeginEdit(MeshEditKind.Rotate); return true;
            case Key.S: BeginEdit(MeshEditKind.Scale); return true;
            case Key.A: SelectAllComponents(); return true;
            case Key.Delete: SetStatus("Edit mode changes shapes only; press Tab for Object mode to delete objects."); return true;
        }
        return false;
    }

    /// <summary>Alt+A in Blender: deselect all (Alt is otherwise left to the editor).</summary>
    bool AltDeselect(InputEventKey k) { if (EditMode && _drag is null && k.AltPressed && k.Keycode == Key.A) { ClearComponents(); return true; } return false; }

    /// <summary>The editor's redo: the open Mapmaker scene's own history (as <see cref="UndoLast"/>).</summary>
    public bool RedoLast()
    {
        var ur = GetUndoRedo();
        int id = MapHost.Root is { } root ? ur.GetObjectHistoryId(root) : (int)EditorUndoRedoManager.SpecialHistory.GlobalHistory;
        var history = ur.GetHistoryUndoRedo(id);
        if (history is null || !history.HasRedo()) { SetStatus("Nothing to redo."); return false; }
        return history.Redo();
    }

    // ---- the mode bar on the 3D view's menu, immediately left of Transform ----

    Control? _modeBar;
    bool _modeBarInContainer;

    /// <summary>Puts the dock's Object/Edit bar into the 3D editor's menu row, just before the Transform menu (or, when that menu cannot be found, into the 3D menu container).</summary>
    void InstallModeBar()
    {
        if (_modeBar is not null && GodotObject.IsInstanceValid(_modeBar) && _modeBar.GetParent() is not null) return;
        if (Dock is null) return;
        _modeBar = Dock.ModeBar;
        var main = EditorInterface.Singleton.GetEditorMainScreen();
        var transform = main.FindChildren("*", "MenuButton", true, false).OfType<MenuButton>().FirstOrDefault(m => m.Text == "Transform");
        if (transform?.GetParent() is Node row)
        {
            row.AddChild(_modeBar);
            row.MoveChild(_modeBar, transform.GetIndex());
            _modeBarInContainer = false;
        }
        else if (_slow > 300)
        {
            AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _modeBar);
            _modeBarInContainer = true;
        }
    }

    /// <summary>Test aid: the control just right of the mode bar in its row (expected: Godot's Transform menu).</summary>
    public string ModeBarNeighbour => _modeBar?.GetParent() is { } p && _modeBar.GetIndex() + 1 < p.GetChildCount() && p.GetChild(_modeBar.GetIndex() + 1) is MenuButton mb ? mb.Text : "";

    void RemoveModeBar()
    {
        if (_modeBar is null || !GodotObject.IsInstanceValid(_modeBar)) { _modeBar = null; return; }
        if (_modeBarInContainer) RemoveControlFromContainer(CustomControlContainer.SpatialEditorMenu, _modeBar);
        else _modeBar.GetParent()?.RemoveChild(_modeBar);
        _modeBar.QueueFree();
        _modeBar = null;
    }

    /// <summary>A content key part for an object's stored mesh (an edit, undo or redo redraws the piece).</summary>
    static string MeshKey(EditMesh? mesh) => mesh is null ? "" : mesh.ToNode().ToJsonString();
}
