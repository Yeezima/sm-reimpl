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
/// The 3D view's mouse tools (docs/mapmaker.md, "Godot plugin"): the always-on box select of the Select tool and the placement ghost of the Place tool.
/// Owner's decisions (2026-11-18): "Always box-select" (a left drag from anywhere draws a rectangle; objects move only by the gizmo handles) and
/// "Keys + stamp" (an armed item follows the mouse as a see-through preview; wheel turns it, Shift+wheel scales it, a click stamps a copy and the item stays armed).
/// </summary>
public partial class MapmakerPlugin
{
    // ---- shared helpers ----------------------------------------------------------------------------------------------------------------------------------

    Vector2 _mouse;
    bool _pushingWheel;

    /// <summary>The mouse is over the 3D view itself (not over a toolbar or popup drawn on top of it).</summary>
    bool ViewportHovered(Vector2 window)
    {
        if (!OverViewport(window)) return false;
        if (Viewport3D?.GetParent() is not Control container) return false;
        var hovered = GetViewport().GuiGetHoveredControl();
        return hovered is null || hovered == container || container.IsAncestorOf(hovered);
    }

    Vector2 ToWindow(Vector2 viewportLocal)
    {
        var vp = Viewport3D!; var rect = ((Control)vp.GetParent()).GetGlobalRect();
        return rect.Position + viewportLocal / new Vector2(vp.Size.X, vp.Size.Y) * rect.Size;
    }

    /// <summary>The window-coordinate rectangle that a box (in the frame of <paramref name="toGlobal"/>) covers on screen; false when all of it is behind the camera.</summary>
    bool ScreenBounds(Transform3D toGlobal, Aabb box, out Rect2 rect)
    {
        rect = default;
        var cam = Viewport3D?.GetCamera3D();
        if (cam is null) return false;
        bool any = false; Vector2 lo = default, hi = default;
        for (int i = 0; i < 8; i++)
        {
            var world = toGlobal * box.GetEndpoint(i);
            if (cam.IsPositionBehind(world)) continue;
            var p = ToWindow(cam.UnprojectPosition(world));
            if (!any) { lo = hi = p; any = true; } else { lo = lo.Min(p); hi = hi.Max(p); }
        }
        if (!any) return false;
        rect = new Rect2(lo, hi - lo);
        return true;
    }

    void InitViewportTools()
    {
        MapHost.LibraryChanged += FreeGhost;
        MapHost.Sm1Changed += FreeGhost;
    }

    /// <summary>Drops everything the viewport tools hold (called when the plugin leaves).</summary>
    void FreeViewportTools()
    {
        FreeGhost();
        if (_overlay is not null && GodotObject.IsInstanceValid(_overlay)) _overlay.QueueFree();
        _overlay = null; _boxPending = false; _boxActive = false;
        MapHost.LibraryChanged -= FreeGhost;
        MapHost.Sm1Changed -= FreeGhost;
    }

    int _slow;

    public override void _Process(double delta)
    {
        UpdateGhost(delta);
        if (++_slow % 60 == 0) { SyncRootMeta(); PruneStash(); }
    }

    // ---- box select --------------------------------------------------------------------------------------------------------------------------------------

    Control? _overlay;
    Rect2? _boxRect;
    Vector2 _boxFrom;
    bool _boxPending, _boxActive, _replaying;
    InputEventMouseButton? _boxPress;
    bool _boxAlt;   // the press began with Alt held: a left drag is a box and a release without a drag selects nothing

    /// <summary>
    /// The Select tool's left button. A press over the view is held back (Godot never sees it, so it cannot start moving the body under the cursor); a drag
    /// past 6 px draws the rectangle and selects what it covers on release; a release without a drag hands Godot the click again, so a plain click
    /// behaves as it always did. A press near the gizmo of the current selection goes to Godot untouched (the handles stay usable).
    /// </summary>
    bool BoxSelectInput(InputEvent e)
    {
        if (_replaying) return false;
        if (MapHost.Tool != Tool.Select) { if (_boxPending) CancelBox(); return false; }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
        {
            if (b.Pressed)
            {
                // Alt held: any left press in the view starts a box, over objects and near the gizmo too (owner 2026-11-19); Shift and Ctrl still add and remove.
                if (_boxPending || !ViewportHovered(b.Position) || !TryRay(b.Position, out _, out _) || (!b.AltPressed && NearSelectionGizmo(b.Position))) return false;
                _boxAlt = b.AltPressed;
                _boxFrom = b.Position; _boxPending = true; _boxActive = false; _boxPress = (InputEventMouseButton)b.Duplicate();
                GetViewport().SetInputAsHandled();
                return true;
            }
            if (!_boxPending) return false;
            bool drew = _boxActive; var press = _boxPress; bool alt = _boxAlt;
            CancelBox();
            GetViewport().SetInputAsHandled();
            if (drew || alt) _dblPendingAt = 0;
            if (drew) FinishBox(new Rect2(_boxFrom, Vector2.Zero).Expand(b.Position), b.ShiftPressed, b.CtrlPressed);
            else if (press is not null && !alt) _ = ReplayClick(press, (InputEventMouseButton)b.Duplicate());
            return true;
        }
        if (e is InputEventMouseMotion m && _boxPending)
        {
            if (!m.ButtonMask.HasFlag(MouseButtonMask.Left)) { CancelBox(); return false; }
            if (!_boxActive && m.Position.DistanceTo(_boxFrom) > 6f) _boxActive = true;
            if (_boxActive) ShowBox(new Rect2(_boxFrom, Vector2.Zero).Expand(m.Position));
            GetViewport().SetInputAsHandled();
            return true;
        }
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape } && _boxPending) { CancelBox(); GetViewport().SetInputAsHandled(); return true; }
        return false;
    }

    async Task ReplayClick(InputEventMouseButton press, InputEventMouseButton release)
    {
        _replayInFlight = true;
        try
        {
            _replaying = true; try { GetTree().Root.PushInput(press); } finally { _replaying = false; }
            for (int i = 0; i < 2; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            _replaying = true; try { GetTree().Root.PushInput(release); } finally { _replaying = false; }
        }
        finally { _replayInFlight = false; }
        await ExpandPending();   // a double-click's group expansion goes after the replayed release, which would otherwise select the single object again
    }

    void CancelBox()
    {
        _boxPending = false; _boxActive = false; _boxPress = null; _boxAlt = false;
        _boxRect = null;
        if (_overlay is not null && GodotObject.IsInstanceValid(_overlay)) _overlay.QueueRedraw();
    }

    void ShowBox(Rect2 window)
    {
        var host = EditorInterface.Singleton.GetBaseControl();
        if (_overlay is null || !GodotObject.IsInstanceValid(_overlay))
        {
            _overlay = new Control { Name = "MapmakerBoxSelect", MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 100 };
            _overlay.Draw += DrawBox;
        }
        // The editor's base control, on top of everything, in window coordinates (a child of the view's own container is not drawn over the 3D picture).
        if (_overlay.GetParent() != host) { _overlay.GetParent()?.RemoveChild(_overlay); host.AddChild(_overlay); }
        _overlay.TopLevel = true;
        _overlay.GlobalPosition = Vector2.Zero; _overlay.Size = host.Size;
        _boxRect = window;
        _overlay.QueueRedraw();
    }

    void DrawBox()
    {
        if (_boxRect is not { } b || _overlay is null) return;
        _overlay.DrawRect(b, new Color(0.35f, 0.75f, 1f, 0.18f), true);
        _overlay.DrawRect(b, new Color(0.45f, 0.82f, 1f, 0.95f), false, 1.5f);
    }

    /// <summary>Test aid: a box is being drawn right now.</summary>
    public bool BoxDrawing => _boxActive;

    public int StashCount => _stash.Count;

    /// <summary>Test aid: the press at this window position would go to Godot's gizmo (a handle of the selection) rather than start a box.</summary>
    public bool IsOnGizmo(Vector2 window) => NearSelectionGizmo(window);

    /// <summary>
    /// True when the press lies on a handle of the gizmo of the selected objects: the three arrows (move and scale handles sit on them), the plane squares, the rotate
    /// rings (and the outer view ring), or the small centre disc. The gizmo is drawn at a constant screen size, so its handles are projected from the selection's
    /// centre: an axis is its world direction shortened by how much it points at the camera; lengths come from the editor's manipulator size and scale (measured by
    /// the flow test against the real gizmo: arrow tips 2.0 x (farthest pixel 1.9 x), rotate rings 1.26 x size x scale). Global and local axes are both accepted, since the editor's
    /// space toggle cannot be read. Everything else starts a box.
    /// </summary>
    bool NearSelectionGizmo(Vector2 window)
    {
        var picked = EditorInterface.Singleton.GetSelection().GetSelectedNodes().OfType<MapmakerObject>().ToList();
        if (picked.Count == 0 || Viewport3D?.GetCamera3D() is not { } cam) return false;
        var centre = picked.Aggregate(Vector3.Zero, (a, n) => a + n.GlobalPosition) / picked.Count;
        if (cam.IsPositionBehind(centre)) return false;
        float scale = EditorInterface.Singleton.GetEditorScale();
        float size = 80f;
        try { size = EditorInterface.Singleton.GetEditorSettings().Get("editors/3d/manipulator_gizmo_size").AsSingle(); } catch (Exception) { }
        float tip = 2.0f * size * scale, ring = 1.26f * size * scale, reach = 10f * scale;
        var o = ToWindow(cam.UnprojectPosition(centre));
        var p = window - o;
        if (p.Length() < 16f * scale) return true;
        if (MathF.Abs(p.Length() - ring) < reach) return true;   // the outer view ring
        var look = cam.Projection == Camera3D.ProjectionType.Perspective ? (centre - cam.GlobalPosition).Normalized() : -cam.GlobalTransform.Basis.Z;   // orthographic views look along the camera's forward axis
        var spaces = new List<Basis> { Basis.Identity };
        var local = picked[0].GlobalTransform.Basis.Orthonormalized();
        if (!local.IsEqualApprox(Basis.Identity)) spaces.Add(local);
        foreach (var basis in spaces)
        {
            var axes = new Vector2[3];   // screen vectors of the three axes, in px, at ring length 1
            for (int i = 0; i < 3; i++)
            {
                var a = (i == 0 ? basis.Column0 : i == 1 ? basis.Column1 : basis.Column2).Normalized();
                var ahead = centre + a * 0.5f;
                Vector2 d;
                if (!cam.IsPositionBehind(ahead)) d = ToWindow(cam.UnprojectPosition(ahead)) - o;
                else if (!cam.IsPositionBehind(centre - a * 0.5f)) d = -(ToWindow(cam.UnprojectPosition(centre - a * 0.5f)) - o);   // the tip would be behind the camera: use the other side
                else d = Vector2.Zero;
                float frac = MathF.Sqrt(Math.Max(0f, 1f - a.Dot(look) * a.Dot(look)));
                axes[i] = d.Length() < 1e-3f ? Vector2.Zero : d.Normalized() * frac;
            }
            for (int i = 0; i < 3; i++)
            {
                var end = axes[i] * tip;
                float len2 = end.LengthSquared();
                float t = len2 < 1e-3f ? 0f : Mathf.Clamp(p.Dot(end) / len2, 0f, 1f);
                if ((p - end * t).Length() < reach) return true;   // an arrow (move) or its scale handle
                int j = (i + 1) % 3;
                // the plane square between axes i and j: p = s * tip * axes[i] + t * tip * axes[j], s and t in 0.12 .. 0.38
                float det = axes[i].X * axes[j].Y - axes[i].Y * axes[j].X;
                if (MathF.Abs(det) > 0.05f)
                {
                    float s0 = (p.X * axes[j].Y - p.Y * axes[j].X) / det / tip, t0 = (axes[i].X * p.Y - axes[i].Y * p.X) / det / tip;
                    float m = reach / tip;
                    if (s0 > 0.12f - m && s0 < 0.38f + m && t0 > 0.12f - m && t0 < 0.38f + m) return true;
                }
                // the rotate ring in the plane of axes i and j
                var prev = axes[i] * ring;
                for (int k = 1; k <= 48; k++)
                {
                    float ang = k * Mathf.Tau / 48f;
                    var pt = (axes[i] * MathF.Cos(ang) + axes[j] * MathF.Sin(ang)) * ring;
                    var seg = pt - prev;
                    float u = seg.LengthSquared() < 1e-6f ? 0f : Mathf.Clamp((p - prev).Dot(seg) / seg.LengthSquared(), 0f, 1f);
                    if ((p - (prev + seg * u)).Length() < reach) return true;
                    prev = pt;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Selects what a finished rectangle covers (window coordinates). Placed objects count when their drawn box overlaps the rectangle (Godot's own rule);
    /// SM1 map pieces when the centre of their box is inside it, so a floor slab that merely lies under the rectangle is left alone. Shift adds, Ctrl removes.
    /// </summary>
    void FinishBox(Rect2 rect, bool shift, bool ctrl)
    {
        var sel = EditorInterface.Singleton.GetSelection();
        if (!shift && !ctrl) { sel.Clear(); MapHost.PickedSm1.Clear(); }
        var hits = _nodes.Values.Where(n => GodotObject.IsInstanceValid(n) && n.IsInsideTree() && n.IsVisibleInTree()
            && ScreenBounds(n.GlobalTransform, n.LocalBounds, out var r) && rect.Intersects(r, true)).ToList();
        foreach (var n in hits) { if (ctrl) sel.RemoveNode(n); else sel.AddNode(n); }
        int pieces = 0;
        if (MapHost.Sm1 is { } src && MapHost.Root is { } root && root.BackdropVisible && Viewport3D?.GetCamera3D() is { } cam)
        {
            var camGame = ToNum(Frame.ToGame(root.GlobalTransform.AffineInverse() * cam.GlobalPosition));
            for (int i = 0; i < src.Scene.Objects.Count; i++)
            {
                if (src.Hidden.Contains(i) || src.WorldBounds(i) is not { } b) continue;
                if (camGame.X >= b.Min.X && camGame.X <= b.Max.X && camGame.Y >= b.Min.Y && camGame.Y <= b.Max.Y && camGame.Z >= b.Min.Z && camGame.Z <= b.Max.Z) continue; // the camera is inside it
                var centre = root.GlobalTransform * Frame.ToGodot(new Vector3((b.Min.X + b.Max.X) / 2, (b.Min.Y + b.Max.Y) / 2, (b.Min.Z + b.Max.Z) / 2));
                if (cam.IsPositionBehind(centre) || !rect.HasPoint(ToWindow(cam.UnprojectPosition(centre)))) continue;
                var pick = new PickRef(i);
                if (ctrl) { if (MapHost.PickedSm1.Remove(pick)) pieces++; }
                else if (!MapHost.PickedSm1.Contains(pick)) { MapHost.PickedSm1.Add(pick); pieces++; }
            }
        }
        if (MapHost.Root is { } r0 && sel.GetSelectedNodes().Contains(r0)) sel.RemoveNode(r0);
        RefreshPicked();
        DocChanged?.Invoke();
        SetStatus($"Box select: {hits.Count} object(s) and {pieces} SM1 piece(s) {(ctrl ? "removed from" : "added to")} the selection.");
    }

    // ---- placement ghost ---------------------------------------------------------------------------------------------------------------------------------

    Node3D? _ghost;
    string _ghostKey = "", _ghostFailed = "";
    float? _ghostYaw;
    float _ghostScale = 1f;
    Vector2 _ghostMouse = new(-1, -1);
    bool _ghostDirty;
    double _ghostClock;

    /// <summary>Test aids: the preview node, its turn about the up axis (degrees) and its scale factor.</summary>
    public Node3D? GhostNode => _ghost is not null && GodotObject.IsInstanceValid(_ghost) ? _ghost : null;
    public float GhostYaw => _ghostYaw ?? 0f;
    public float GhostScale => _ghostScale;

    void FreeGhost()
    {
        if (_ghost is not null && GodotObject.IsInstanceValid(_ghost)) { _ghost.GetParent()?.RemoveChild(_ghost); _ghost.QueueFree(); }
        _ghost = null; _ghostKey = ""; _ghostFailed = "";
    }

    static void MakeSeeThrough(Node n)
    {
        switch (n)
        {
            case GeometryInstance3D g: g.Transparency = 0.55f; g.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off; break;
            case CollisionObject3D c: c.CollisionLayer = 0; c.CollisionMask = 0; break;
        }
        foreach (var child in n.GetChildren()) MakeSeeThrough(child);
    }

    void BuildGhost(LibraryItem item, MapmakerRoot root)
    {
        Node3D? content = null;
        if (item.Kind == LibraryKind.Piece)
        {
            if (PiecePreset.Find(item.Id) is { } preset) content = PieceViews.Build(new PieceSpec(preset.Kind, preset.Side), out _);
        }
        else
        {
            try
            {
                var tmp = MapSession.New("ghost", MapHost.Library, _sm1Adapter);
                var r = tmp.Apply(PlaceOp(item, Num.Vector3.Zero, null, 1f));
                if (r.Ok && r.Created.Count > 0)
                    content = ContentFactory.BuildObject(tmp.Resolve().Where(p => p.ObjectId == r.Created[0]), tmp.Document.World(r.Created[0]), out _);
                if (ContentFactory.MissingMaps.Count > 0) LoadMissingSm1();
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { GD.PushWarning("Mapmaker: placement preview: " + e.Message); }
        }
        if (content is null) { _ghostFailed = item.Key; return; }
        MakeSeeThrough(content);
        _ghost = new Node3D { Name = "MapmakerGhost", Visible = false };   // no owner: never saved, never in undo history
        _ghost.AddChild(content);
        root.AddChild(_ghost);
        _ghostKey = item.Key; _ghostDirty = true;
    }

    /// <summary>The wanted position, turn and size of an item put at <paramref name="snapped"/>: the one rule for the preview and for the placed object.</summary>
    (Num.Vector3 Pos, float Yaw, Num.Vector3 Size) Placement(LibraryItem item, Vector3 snapped, float? yaw, float scale)
    {
        if (item.Kind == LibraryKind.Piece && PiecePreset.Find(item.Id) is { } preset)
        {
            var size = preset.Size * scale;
            var pos = ToNum(Frame.Round(snapped));
            if (preset.TopAtClick) pos.Z -= size.Z;
            float y = yaw ?? (preset.Kind is PieceKind.SpawnArea or PieceKind.Blocker ? DefaultYaw(snapped) : 0f);
            return (pos, y, size);
        }
        return (ToNum(Frame.Round(snapped)), yaw ?? 0f, new Num.Vector3(scale, scale, scale));
    }

    Vector3 SnapPoint(Vector3 game) =>
        MapHost.SnapGrid ? new Vector3(Snap.Grid(game.X, MapHost.GridStep), Snap.Grid(game.Y, MapHost.GridStep), Snap.Grid(game.Z, MapHost.GridStep)) : game;

    JsonObject PlaceOp(LibraryItem item, Num.Vector3 pos, float? yaw, float scale)
    {
        var transform = new JsonObject { ["position"] = Vec(pos) };
        if (yaw is { } y && Math.Abs(y) > 1e-4f) transform["rotation"] = Vec(new Num.Vector3(0, 0, y));
        if (Math.Abs(scale - 1f) > 1e-4f) transform["scale"] = Vec(new Num.Vector3(scale, scale, scale));
        var placed = Op("place", ("name", item.Label), ("transform", transform));
        switch (item.Kind)
        {
            case LibraryKind.Sm1: placed["asset"] = new JsonObject { ["source"] = "sm1", ["map"] = item.Map, ["definition"] = item.Id }; break;
            case LibraryKind.Asset: placed["asset"] = new JsonObject { ["source"] = "custom", ["id"] = item.Id }; break;
            default: placed["prefab"] = item.Id; break;
        }
        return placed;
    }

    /// <summary>A piece put down with a turn and a size factor (the ghost's); the plain click path keeps <see cref="PlacePiece"/>.</summary>
    OpResult PlacePieceAt(LibraryItem item, Vector3 game, float? yaw, float scale)
    {
        var preset = PiecePreset.Find(item.Id);
        if (preset is null) return OpResult.Fail("unknown-piece", $"No piece '{item.Id}'.");
        var (pos, y, size) = Placement(item, game, yaw, scale);
        var piece = new JsonObject { ["kind"] = preset.Kind };
        if (preset.Side is not null) piece["side"] = preset.Side;
        var op = Op("place", ("name", preset.Name), ("piece", piece), ("transform", new JsonObject { ["position"] = Vec(pos), ["rotation"] = Vec(new Num.Vector3(0, 0, y)), ["scale"] = Vec(size) }));
        var r = Do($"Place {preset.Label}", op);
        if (r.Ok && r.Created.Count > 0) Select(r.Created);
        return r;
    }

    void UpdateGhost(double delta)
    {
        bool want = MapHost.Tool == Tool.Place && MapHost.Armed is not null && MapHost.Root is not null && EditedHasMap;
        if (!want) { if (_ghost is not null || _ghostKey != "" || _ghostFailed != "") FreeGhost(); _ghostYaw = null; _ghostScale = 1f; return; }
        var item = MapHost.Armed!; var root = MapHost.Root!;
        if (_ghost is not null && (!GodotObject.IsInstanceValid(_ghost) || _ghost.GetParent() != root)) FreeGhost();
        if ((_ghostKey != "" && _ghostKey != item.Key) || (_ghostFailed != "" && _ghostFailed != item.Key)) { FreeGhost(); _ghostYaw = null; _ghostScale = 1f; }
        if (_ghost is null)
        {
            if (_ghostFailed == item.Key) return;
            BuildGhost(item, root);
            if (_ghost is null) return;
        }
        var mouse = _mouse;
        bool over = ViewportHovered(mouse) && TryRay(mouse, out _, out _);
        if (_ghost.Visible != over) _ghost.Visible = over;
        if (!over) { _ghostMouse = new Vector2(-1, -1); return; }
        _ghostClock += delta;
        if (!_ghostDirty && (mouse == _ghostMouse || _ghostClock < 0.03)) return;
        _ghostClock = 0; _ghostMouse = mouse; _ghostDirty = false;
        TryRay(mouse, out var o, out var d);
        var (pos, yaw, size) = Placement(item, SnapPoint(GamePointOnRay(o, d)), _ghostYaw, _ghostScale);
        _ghost.Transform = Frame.NodeTransform(ToGd(pos), new Vector3(0, 0, yaw), ToGd(size));
    }

    /// <summary>The mouse wheel while an item is armed: turns the preview (15 degrees, or the angle snap), with Shift scales it (10 %). Swallowed, so the camera never zooms.</summary>
    bool GhostWheelInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } w || _pushingWheel) return false;
        if (MapHost.Tool != Tool.Place || MapHost.Armed is not { } item || _ghost is null || !ViewportHovered(w.Position)) return false;   // no ghost (nothing to draw): the wheel is the camera's
        GetViewport().SetInputAsHandled();
        if (!w.Pressed) return true;
        int dir = w.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
        if (w.ShiftPressed) _ghostScale = Mathf.Clamp(MathF.Round(_ghostScale * MathF.Pow(1.1f, dir), 4), 0.05f, 20f);
        else
        {
            float step = MapHost.SnapAngle ? MathF.Max(MapHost.AngleStep, 1f) : 15f;
            float now = _ghostYaw ?? (TryRay(w.Position, out var o, out var d) ? Placement(item, SnapPoint(GamePointOnRay(o, d)), null, _ghostScale).Yaw : 0f);
            float next = MathF.Round((now + dir * step) / step) * step;
            next = ((next + 180f) % 360f + 360f) % 360f - 180f;
            _ghostYaw = next == -180f ? 180f : next;
        }
        _ghostDirty = true;
        return true;
    }
}
