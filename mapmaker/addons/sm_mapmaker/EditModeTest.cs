using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using SmMapmaker;
using Num = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>
/// Test aid (agents, never the owner's editor): <c>godot.cmd --editor --path mapmaker -- --mm-editmode --mm-lib &lt;dir&gt; --mm-out &lt;dir&gt;</c> (set SM_MAPMAKER_CONTROL to a scratch folder)
/// checks Edit mode (docs/mapmaker.md "Edit mode (editor)"): the mode bar left of Transform, Tab and 1 / 2 / 3, a face picked by a ray (both triangles highlighted, a block in front not
/// selectable), the brief's one-sided 4 x 2 x 1 -> 5 x 2 x 1 wall edit by G X 1 Enter with the live readout during and after, cancel by Esc and by right click, the gizmo, undo, redo,
/// save and reopen; two objects in Edit mode with a third outside, shared vertices moving once, a column's top taper, a ramp vertex, an edge scaled numerically, and every Build preset.
/// Windowed (<c>--windowed</c>) it also writes stills and the frames of the one-sided edit. Prints <c>EDITMODE RESULT PASS|FAIL</c>.
/// </summary>
public sealed class EditModeTest(MapmakerPlugin plugin)
{
    public static bool Wanted => OS.GetCmdlineUserArgs().Contains("--mm-editmode");
    static string Arg(string name, string fallback) { var a = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }
    SceneTree Tree => (SceneTree)Engine.GetMainLoop();
    async Task Frames(int n) { for (int i = 0; i < n; i++) await plugin.ToSignal(Tree, SceneTree.SignalName.ProcessFrame); }
    bool _failed;
    int _passed;
    void Check(bool ok, string what) { GD.Print($"EDITMODE {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) _failed = true; else _passed++; }

    static Num.Vector3 V(float x, float y, float z) => new(x, y, z);
    static bool Near(Num.Vector3 a, Num.Vector3 b, float eps = 1e-4f) => (a - b).Length() < eps;

    string Place(string kind, string name, Num.Vector3 pos, Num.Vector3 scale, string? side = null, string? shape = null)
    {
        var piece = new JsonObject { ["kind"] = kind };
        if (side is not null) piece["side"] = side;
        if (shape is not null) piece["shape"] = shape;
        var r = plugin.Do("Place " + name, MapmakerPlugin.Op("place", ("piece", piece), ("name", name),
            ("transform", new JsonObject { ["position"] = MapmakerPlugin.Vec(pos), ["scale"] = MapmakerPlugin.Vec(scale) })));
        if (!r.Ok) throw new InvalidOperationException("place failed: " + string.Join("; ", r.Errors.Select(e => e.Message)));
        return r.Created[0];
    }

    MapObject Obj(string id) => plugin.Session!.Document.Get(id);
    Num.Vector3 Dims(string id) => MeshDims.Of(Obj(id));

    bool Key(Key k, bool shift = false, bool ctrl = false) => plugin.TestInput(new InputEventKey { Keycode = k, Pressed = true, ShiftPressed = shift, CtrlPressed = ctrl });
    void Keys(params Key[] ks) { foreach (var k in ks) Key(k); }

    async Task Enter(params string[] ids)
    {
        if (plugin.EditMode) plugin.SetEditMode(false);
        await Frames(3);   // a selection made right after leaving Edit mode or rebuilding the map was observed to revert a frame later (the previous rows re-selected); let it settle first
        plugin.Select(ids);
        GD.Print($"EDITMODE note: selected now [{string.Join(",", plugin.SelectedIds())}] (wanted [{string.Join(",", ids)}])");
        await Frames(2);
        GD.Print($"EDITMODE note: two frames later [{string.Join(",", plugin.SelectedIds())}] nodes [{string.Join(",", EditorInterface.Singleton.GetSelection().GetSelectedNodes().Select(n => n.Name))}]");
        Key(Godot.Key.Tab);
        await Frames(2);
    }

    public async Task Run()
    {
        string outDir = Arg("--mm-out", Path.Combine(Path.GetTempPath(), "mm-editmode-" + System.Environment.ProcessId));
        Directory.CreateDirectory(outDir);
        try
        {
            await Frames(30);
            for (int wait = 0; MapHost.Root is null; wait++) { plugin.EnsureSceneRoot(); await Frames(20); if (wait > 60) break; }
            await Frames(20);
            MapHost.Library = new MapLibrary(Arg("--mm-lib", Path.Combine(outDir, "lib")));
            plugin.EditKeysAnywhere = true;
            var dock = plugin.Dock!;
            _ = dock.ModeBar;
            await Frames(130);   // the mode bar goes onto the 3D menu row on a slow tick
            Check(plugin.ModeBarNeighbour == "Transform", $"the Object/Edit bar sits immediately left of Transform (right of it: '{plugin.ModeBarNeighbour}')");
            await RealInput(dock);

            // ---- the brief's one-sided wall edit ----
            plugin.NewMap("editmode-wall");
            var wall = Place(PieceKind.Box, "Wall", V(0, 0, 0), V(4, 2, 1));
            var block = Place(PieceKind.Box, "Block", V(5, 0, 0), V(2, 2, 2));
            var blockBefore = Obj(block);
            await Frames(3);
            Check(!Key(Godot.Key.Tab) || !plugin.EditMode, "Tab with nothing selected stays in Object mode");
            await Enter(wall);
            Check(plugin.EditMode && plugin.EditIds.SequenceEqual([wall]), "Tab enters Edit mode on the selected wall only");
            Check(plugin.NodeOf(wall)!.HasMeta("_edit_lock_"), "Godot's move gizmo is locked off the edited object");
            Key(Godot.Key.Key1); Check(plugin.ComponentMode == ComponentMode.Vertex, "1: Vertex");
            Key(Godot.Key.Key2); Check(plugin.ComponentMode == ComponentMode.Edge, "2: Edge");
            Key(Godot.Key.Key3); Check(plugin.ComponentMode == ComponentMode.Face, "3: Face");
            dock.RefreshEditBar();
            // a ray from +X: the block (not in Edit mode) is in front, yet the wall's +X face is what it picks
            var hit = plugin.PickFaceByRay(Frame.ToGodot(new Vector3(10, 0.3f, 0.5f)), Frame.ToGodot(new Vector3(-1, 0, 0)));
            int plusX = MeshTopology.Box.FaceNamed("+x");
            Check(hit is { } h0 && h0.Id == wall && h0.Face == plusX, $"a ray picks the wall's +X face through the block outside the edit set ({hit})");
            // either triangle of the quad selects the whole face (a ray near each corner of the face)
            var hitLow = plugin.PickFaceByRay(Frame.ToGodot(new Vector3(10, -0.9f, 0.1f)), Frame.ToGodot(new Vector3(-1, 0, 0)));
            var hitHigh = plugin.PickFaceByRay(Frame.ToGodot(new Vector3(10, 0.9f, 0.9f)), Frame.ToGodot(new Vector3(-1, 0, 0)));
            Check(hitLow?.Face == plusX && hitHigh?.Face == plusX, "both triangles of the face pick the same face");
            plugin.SelectComponent(wall, plusX);
            Check(plugin.HighlightTriangles(wall) == 2, $"the highlight fills the whole face: both triangles ({plugin.HighlightTriangles(wall)})");
            Check(!plugin.SelectComponent(block, 1), "a component of the object outside the edit set cannot be selected");

            // G X 1 Enter
            Keys(Godot.Key.G, Godot.Key.X, Godot.Key.Key1);
            Check(plugin.EditDragging && plugin.Typed == "1", "G X 1: an edit in progress with the typed value");
            Check(plugin.Readout == "Wall · X 5.00 m   Y 2.00 m   Z 1.00 m", $"live readout during the edit: '{plugin.Readout}'");
            dock.RefreshEditBar();
            Check(dock.DimensionsText.Contains("X 5.00 m   Y 2.00 m   Z 1.00 m"), $"the Object tab's Dimensions follow the live edit: '{dock.DimensionsText}'");
            Check(Near(Dims(wall), V(4, 2, 1)) && Obj(wall).Mesh is null, "the document is untouched during the edit");
            Check(MathF.Abs(plugin.NodeOf(wall)!.LocalBounds.Size.X - 1.25f) < 1e-4f, $"the piece is drawn from the preview (unit width 1.25: {plugin.NodeOf(wall)!.LocalBounds.Size.X})");
            Key(Godot.Key.Enter);
            await Frames(2);
            Check(!plugin.EditDragging && Near(Dims(wall), V(5, 2, 1)), $"Enter applies: the wall measures {MeshDims.Label(Dims(wall))}");
            var mesh = Obj(wall).Mesh!;
            Check(new[] { 0, 2, 4, 6 }.All(i => mesh.Vertices[i].X == -0.5f) && new[] { 1, 3, 5, 7 }.All(i => mesh.Vertices[i].X == 0.75f), "only the +X face moved; the -X face kept its bits");
            Check(Obj(block) == blockBefore && Obj(block).Mesh is null, "the neighbouring block is unchanged");
            Check(plugin.Readout == "Wall · X 5.00 m   Y 2.00 m   Z 1.00 m", $"readout after the edit: '{plugin.Readout}'");
            dock.Refresh();
            Check(dock.DimensionsText == "X 5.00 m   Y 2.00 m   Z 1.00 m", $"Object tab Dimensions after the edit: '{dock.DimensionsText}'");

            // cancel by Esc and by right click
            Keys(Godot.Key.G, Godot.Key.X, Godot.Key.Key2);
            Check(plugin.Readout!.Contains("X 7.00 m"), "a second edit previews 7 m");
            Key(Godot.Key.Escape);
            await Frames(2);
            Check(!plugin.EditDragging && Near(Dims(wall), V(5, 2, 1)) && MathF.Abs(plugin.NodeOf(wall)!.LocalBounds.Size.X - 1.25f) < 1e-4f, "Esc cancels: shape, drawing and readout back at 5 m");
            Keys(Godot.Key.S, Godot.Key.X, Godot.Key.Key3);
            Check(plugin.EditDragging && plugin.ActiveDrag!.Edit.Kind == MeshEditKind.Scale, "S starts a scale");
            plugin.TestInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
            await Frames(2);
            Check(!plugin.EditDragging && Near(Dims(wall), V(5, 2, 1)), "a right click cancels too");

            // undo, redo
            plugin.UndoLast(); await Frames(4);
            Check(Near(Dims(wall), V(4, 2, 1)) && Obj(wall).Mesh is null, "Ctrl+Z undoes the edit as one step (stock shape again)");
            Check(plugin.EditMode, "undo keeps Edit mode");
            plugin.RedoLast(); await Frames(4);
            Check(Near(Dims(wall), V(5, 2, 1)) && Obj(wall).Mesh!.SameAs(mesh), "redo brings back the same bits");

            // the gizmo (needs the editor camera)
            await plugin.Frame3D(V(-3, -2, 0), V(7, 2, 2));
            await Frames(10);
            plugin.SelectComponent(wall, plusX);
            if (plugin.GizmoLayoutNow is { } g && g.Axes[0].LengthSquared() > 0.5f)
            {
                var tip = g.MoveTip(0) - g.Axes[0] * 10f;
                Check(plugin.GizmoHandleAt(tip) is { Kind: MeshEditKind.Move, Axis: 0 }, "the gizmo's X arrow is under its tip");
                Check(plugin.PressGizmo(tip), "a press on the X arrow starts a move along X");
                plugin.MoveEditMouse(tip + g.Axes[0] * 60f);
                float during = plugin.DimsOf(wall)!.Value.X;
                Check(during > 5.01f, $"dragging the arrow lengthens the wall live ({during:0.00} m)");
                plugin.TestInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = plugin.Viewport3D!.GetParent<Control>().GetGlobalRect().GetCenter() });
                await Frames(2);
                Check(!plugin.EditDragging && MathF.Abs(Dims(wall).X - during) < 1e-4f, $"release applies exactly the previewed length ({Dims(wall).X:0.000} m)");
                plugin.UndoLast(); await Frames(4);
                Check(Near(Dims(wall), V(5, 2, 1)), "and it undoes as one step");
                // a mouse-driven G with the X constraint, then cancel
                plugin.MoveEditMouse(g.Pivot);   // the edit starts where the mouse is
                Keys(Godot.Key.G, Godot.Key.X);
                plugin.MoveEditMouse(g.Pivot + g.Axes[0] * 50f);
                Check(plugin.DimsOf(wall)!.Value.X > 5.01f, "G X follows the mouse along X");
                Key(Godot.Key.Escape);
                Check(Near(Dims(wall), V(5, 2, 1)), "and Esc cancels it");
            }
            else Check(false, $"the gizmo has a layout (camera present; edit mode {plugin.EditMode}, set [{string.Join(",", plugin.EditIds)}], comps [{string.Join(",", plugin.SelectedComponents(wall))}], layout {plugin.GizmoLayoutNow?.Pivot} axes {(plugin.GizmoLayoutNow is { } gl ? string.Join(" ", gl.Axes) : "-")}, cam {plugin.Viewport3D?.GetCamera3D()?.GlobalPosition})");

            // save and reopen
            string mapFile = Path.Combine(outDir, "editmode-wall.smmap");
            Check(plugin.SaveMap(mapFile), "save");
            Check(plugin.EditMode, "the first save keeps Edit mode");
            plugin.OpenMap(mapFile); await Frames(4);
            Check(!plugin.EditMode, "opening a map leaves Edit mode");
            var reopened = plugin.Session!.Document.Objects.First(o => o.Name == "Wall");
            Check(Near(MeshDims.Of(reopened), V(5, 2, 1)) && reopened.Mesh!.SameAs(mesh), "the reopened map has the same shape and dimensions");
            await Frames(2);

            // windowed: stills and the clip's frames of the one-sided edit
            if (Thumbnails.CanRender && plugin.Viewport3D is { } vp)
            {
                plugin.NewMap("editmode-shot");
                var w2 = Place(PieceKind.Box, "Wall", V(0, 0, 0), V(4, 2, 1));
                Place(PieceKind.Box, "Block", V(5, 0, 0), V(2, 2, 2));
                Place(PieceKind.Box, "Floor", V(1, 0, -0.5f), V(14, 8, 0.5f));
                await plugin.Frame3D(V(-3, -3, 0), V(7, 3, 2));
                await Frames(20);
                await Enter(w2);
                Key(Godot.Key.Key3);
                plugin.SelectComponent(w2, plusX);
                await Frames(10);
                Save(vp, outDir, "01-edit-face.png");
                SaveWindow(outDir, "01-edit-face-window.png");
                string frames = Path.Combine(outDir, "frames");
                Directory.CreateDirectory(frames);
                int f = 0;
                for (int i = 0; i < 10; i++) { await Frames(2); Save(vp, frames, $"f{f++:000}.png"); }
                Keys(Godot.Key.G, Godot.Key.X);
                for (int i = 0; i <= 40; i++)
                {
                    plugin.SetEditAmount(V(i / 40f, 0, 0));
                    await Frames(2);
                    Save(vp, frames, $"f{f++:000}.png");
                    if (i == 20) { Save(vp, outDir, "02-live-readout.png"); SaveWindow(outDir, "02-live-readout-window.png"); }
                }
                Keys(Godot.Key.Key1);   // the typed value: exactly 1 m
                await Frames(4);
                Key(Godot.Key.Enter);
                for (int i = 0; i < 15; i++) { await Frames(2); Save(vp, frames, $"f{f++:000}.png"); }
                Check(Near(Dims(w2), V(5, 2, 1)), "the filmed edit ends at 5 x 2 x 1 m");
                dock.TestShowTabIndex((int)DockTab.Object);
                dock.Refresh();
                await Frames(10);
                SaveWindow(outDir, "03-object-tab-dimensions-window.png");
                plugin.SetEditMode(false);
                GD.Print("EDITMODE shots in " + outDir);
            }

            // ---- two objects in Edit mode, a third outside ----
            plugin.NewMap("editmode-multi");
            var a = Place(PieceKind.Box, "A", V(0, 0, 0), V(4, 2, 1));
            var b = Place(PieceKind.Box, "B", V(0, 6, 0), V(4, 2, 1));
            var c = Place(PieceKind.Box, "C", V(0, 12, 0), V(4, 2, 1));
            await Enter(a, b);
            Check(plugin.EditIds.Count == 2 && !plugin.EditIds.Contains(c), "Edit mode on two objects, the third outside");
            var rayC = plugin.PickFaceByRay(Frame.ToGodot(new Vector3(0, 12, 10)), Frame.ToGodot(new Vector3(0, 0, -1)));
            Check(rayC is null, "a ray at the third object picks nothing");
            Key(Godot.Key.Key1);
            plugin.SelectComponent(a, 7);
            plugin.SelectComponent(b, 7, add: true);
            Check(!plugin.SelectComponent(c, 7, add: true), "the third object's vertex is not selectable");
            Keys(Godot.Key.G, Godot.Key.Z, Godot.Key.Period, Godot.Key.Key5);
            Check(plugin.Readout?.StartsWith("B · ") == true, $"the readout is labelled with the active object's name: '{plugin.Readout}'");
            dock.RefreshEditBar();
            Check(dock.DimensionsText.StartsWith("B · "), $"the Object tab labels a multi-object readout with the active object: '{dock.DimensionsText}'");
            Key(Godot.Key.Enter); await Frames(2);
            Check(Obj(a).Mesh!.Vertices[7] == V(0.5f, 0.5f, 1.5f) && Obj(b).Mesh!.Vertices[7] == V(0.5f, 0.5f, 1.5f), "the selected vertex of both objects moved 0.5 m up");
            Check(Enumerable.Range(0, 7).All(i => Obj(a).Mesh!.Vertices[i] == MeshTopology.Box.StockVertices[i]), "every other vertex kept its place");
            Check(Obj(c).Mesh is null, "the third object is unchanged");
            plugin.UndoLast(); await Frames(4);
            Check(Obj(a).Mesh is null && Obj(b).Mesh is null, "one undo takes back both objects' edit");
            // two faces that share vertices: each shared vertex moves once
            Key(Godot.Key.Key3);
            plugin.SelectComponent(a, MeshTopology.Box.FaceNamed("+x"));
            plugin.SelectComponent(a, MeshTopology.Box.FaceNamed("+z"), add: true);
            Keys(Godot.Key.G, Godot.Key.Z, Godot.Key.Key1, Godot.Key.Enter); await Frames(2);
            Check(Obj(a).Mesh!.Vertices[5] == V(0.5f, -0.5f, 2f) && Obj(a).Mesh!.Vertices[1] == V(0.5f, -0.5f, 1f), "a vertex two selected faces share moves once");

            // ---- column top taper ----
            plugin.NewMap("editmode-column");
            var col = Place(PieceKind.Column, "Pillar", V(0, 0, 0), V(1.5f, 1.5f, 4));
            await Enter(col);
            Key(Godot.Key.Key3);
            plugin.SelectComponent(col, MeshTopology.Column12.FaceNamed("top"));
            Keys(Godot.Key.S, Godot.Key.Period, Godot.Key.Key5, Godot.Key.Enter); await Frames(2);
            var cm = Obj(col).Mesh!;
            Check(Enumerable.Range(0, 12).All(i => cm.Vertices[i] == MeshTopology.Column12.StockVertices[i]), "the column's base stays planted (bits)");
            Check(Enumerable.Range(12, 12).All(i => MathF.Abs(new Num.Vector2(cm.Vertices[i].X, cm.Vertices[i].Y).Length() - 0.25f) < 1e-5f && cm.Vertices[i].Z == 1f), "the top ring is half as wide at the same height");
            Check(Near(Dims(col), V(1.5f, 1.5f, 4)), $"its dimensions are the base's ({MeshDims.Label(Dims(col))})");

            // ---- ramp vertex ----
            var ramp = Place(PieceKind.Ramp, "Ramp", V(6, 0, 0), V(4, 8, 2));
            await Enter(ramp);
            Key(Godot.Key.Key1);
            bool rampSel = plugin.SelectComponent(ramp, 4);
            Check(rampSel && plugin.EditIds.SequenceEqual([ramp]) && plugin.ComponentMode == ComponentMode.Vertex, $"Edit mode on the ramp, vertex 4 selected (edit set {string.Join(",", plugin.EditIds)}, ramp {ramp}, mode {plugin.ComponentMode}, selected {rampSel})");
            Keys(Godot.Key.G, Godot.Key.Z, Godot.Key.Key1, Godot.Key.Enter); await Frames(2);
            Check(Obj(ramp).Mesh!.Vertices[4] == V(0.5f, 0.5f, 1.5f) && Obj(ramp).Mesh!.Vertices[5] == V(-0.5f, 0.5f, 1f), "one ramp vertex moved 1 m up, its neighbour did not");
            Check(Near(Dims(ramp), V(4, 8, 3)), $"the ramp now measures {MeshDims.Label(Dims(ramp))}");

            // ---- an edge scaled numerically ----
            var bx = Place(PieceKind.Box, "Beam", V(0, 8, 0), V(4, 2, 1));
            await Enter(bx);
            Key(Godot.Key.Key2);
            int edge = MeshTopology.Box.EdgeId(6, 7);
            plugin.SelectComponent(bx, edge);
            Keys(Godot.Key.S, Godot.Key.X, Godot.Key.Key2, Godot.Key.Enter); await Frames(2);
            var em = Obj(bx).Mesh!;
            Check(em.Vertices[6] == V(-1f, 0.5f, 1f) && em.Vertices[7] == V(1f, 0.5f, 1f) && Enumerable.Range(0, 6).All(i => em.Vertices[i] == MeshTopology.Box.StockVertices[i]), "S X 2 doubles the edge's length about its midpoint; nothing else moves");
            Check(Near(Dims(bx), V(8, 2, 1)), $"the beam now measures {MeshDims.Label(Dims(bx))}");
            // rotate with a typed angle
            Key(Godot.Key.Key3);
            plugin.SelectComponent(bx, MeshTopology.Box.FaceNamed("top"));
            Keys(Godot.Key.R, Godot.Key.Z, Godot.Key.Key4, Godot.Key.Key5);
            Check(plugin.ReadoutDetail?.Contains("Rotate Z 45.0 deg") == true, $"R Z 45: '{plugin.ReadoutDetail}'");
            Check(plugin.CommitEdit().Ok, "the rotation applies");

            // ---- an unedited capture point reads and draws as a circle of its X size (contract v2) ----
            var pt = Place(PieceKind.CapturePoint, "Point", V(0, 30, 0), V(8, 6, 4));
            await Frames(2);
            Check(Near(Dims(pt), V(8, 8, 4)) && plugin.DimsOf(pt) is { } pd && Near(pd, V(8, 8, 4)), $"an unedited capture point reads X, X, Z ({MeshDims.Label(Dims(pt))})");
            Check(MathF.Abs(plugin.NodeOf(pt)!.LocalBounds.Size.Z - 8f / 6f) < 1e-4f, $"and is drawn as a circle of its X size (unit depth {plugin.NodeOf(pt)!.LocalBounds.Size.Z:0.000})");

            // ---- every Build preset is editable ----
            plugin.NewMap("editmode-presets");
            int n = 0;
            foreach (var preset in PiecePreset.All)
            {
                var id = Place(preset.Kind, preset.Name, V(n * 30f, 0, 0), preset.Size, preset.Side, preset.Shape);
                n++;
                await Enter(id);
                Key(Godot.Key.Key3);
                bool sel = plugin.SelectComponent(id, 1);
                Keys(Godot.Key.G, Godot.Key.Z, Godot.Key.Period, Godot.Key.Key5);
                var r = plugin.CommitEdit();
                await Frames(1);
                Check(sel && r.Ok && Obj(id).Mesh is not null && Dims(id).Z > preset.Size.Z - 1e-3f, $"preset {preset.Id} ({preset.Kind}): top face moved up ({(r.Ok ? MeshDims.Label(Dims(id)) : string.Join("; ", r.Errors.Select(e => e.Message)))})");
            }
            var round = Place(PieceKind.MoveWall, "Round wall", V(n * 30f, 0, 0), V(4, 4, 3), shape: PieceSpec.CylinderShape);
            await Enter(round);
            plugin.SelectComponent(round, 1);
            Keys(Godot.Key.G, Godot.Key.Z, Godot.Key.Key1);
            Check(plugin.CommitEdit().Ok && Obj(round).Mesh?.Topology == MeshTopology.Cylinder16, "the round players-only wall (cylinder) is editable too");
            plugin.SetEditMode(false);
            Check(!plugin.NodeOf(round)!.HasMeta("_edit_lock_"), "Object mode gives Godot's gizmo back");
        }
        catch (Exception e) { GD.Print("EDITMODE exception " + e); _failed = true; }
        try { plugin.SetEditMode(false); } catch (Exception) { }
        await Frames(3);
        GD.Print($"EDITMODE {_passed} checks passed");
        GD.Print(_failed ? "EDITMODE RESULT FAIL" : "EDITMODE RESULT PASS");
        try { File.WriteAllText(Path.Combine(outDir, "editmode-result.txt"), _failed ? "FAIL" : "PASS"); } catch (Exception) { }
        Tree.Quit(_failed ? 1 : 0);
    }

    // ---- real input (owner's repair brief 2026-11-28): events pushed through the editor window, so they take Godot's own route to the 3D view's input surface ----

    SubViewport Vp => plugin.Viewport3D!;
    Control View => (Control)Vp.GetParent();
    Vector2 WinOfGlobal(Vector3 global) => View.GetGlobalRect().Position + Vp.GetCamera3D()!.UnprojectPosition(global) / new Vector2(Vp.Size.X, Vp.Size.Y) * View.GetGlobalRect().Size;
    Vector2 WinOfGame(Num.Vector3 g) => WinOfGlobal(MapHost.Root!.GlobalTransform * Frame.ToGodot(new Vector3(g.X, g.Y, g.Z)));
    /// <summary>A point of a piece's unit shape (stock vertices) in window pixels.</summary>
    Vector2 WinOfUnit(string id, Num.Vector3 unit) => WinOfGame(Num.Vector3.Transform(unit, plugin.Session!.Document.World(id)));
    Vector2 WinOfView(Vector2 viewPx) => View.GetGlobalRect().Position + viewPx / new Vector2(Vp.Size.X, Vp.Size.Y) * View.GetGlobalRect().Size;

    static MouseButtonMask MaskOf(MouseButton b) => b switch { MouseButton.Left => MouseButtonMask.Left, MouseButton.Right => MouseButtonMask.Right, MouseButton.Middle => MouseButtonMask.Middle, _ => 0 };
    void PushMove(Vector2 at, MouseButtonMask mask = 0, Vector2 relative = default, bool shift = false) =>
        Tree.Root.PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at, ButtonMask = mask, Relative = relative, ShiftPressed = shift });
    void PushButton(Vector2 at, MouseButton b, bool pressed, bool shift = false, bool doubleClick = false, MouseButtonMask held = 0) =>
        Tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = b, Pressed = pressed, Position = at, GlobalPosition = at, ShiftPressed = shift, DoubleClick = doubleClick, Factor = 1f, ButtonMask = held | (pressed ? MaskOf(b) : 0) });
    async Task RealClick(Vector2 at, bool shift = false, bool doubleClick = false)
    {
        PushMove(at); await Frames(2);
        PushButton(at, MouseButton.Left, true, shift, doubleClick); await Frames(2);
        PushButton(at, MouseButton.Left, false, shift); await Frames(3);
    }
    async Task RealKey(Key k, bool shift = false, bool ctrl = false, char unicode = '\0')
    {
        foreach (bool pressed in new[] { true, false })
        {
            Tree.Root.PushInput(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = pressed, ShiftPressed = shift, CtrlPressed = ctrl, Unicode = unicode });
            await Frames(1);
        }
        await Frames(1);
    }
    async Task RealKeys(params Key[] ks) { foreach (var k in ks) await RealKey(k); }
    /// <summary>The editor's own Ctrl+Z, as a key through the window (no text field focused, the mouse over the 3D view).</summary>
    async Task RealUndo(bool redo = false)
    {
        if (plugin.GetViewport().GuiGetFocusOwner() is { } fo && fo is LineEdit or TextEdit) fo.ReleaseFocus();
        PushMove(View.GetGlobalRect().GetCenter()); await Frames(2);
        await RealKey(Godot.Key.Z, shift: redo, ctrl: true);
        await Frames(4);
    }
    void PushTab(bool pressed, bool echo = false) => Tree.Root.PushInput(new InputEventKey { Keycode = Godot.Key.Tab, PhysicalKeycode = Godot.Key.Tab, Pressed = pressed, Echo = echo });

    string[] GodotSelected() => EditorInterface.Singleton.GetSelection().GetSelectedNodes().OfType<MapmakerObject>().Select(n => n.ObjectId).OrderBy(i => i).ToArray();
    static string Ids(params string[] ids) => string.Join(",", ids.OrderBy(i => i));
    bool SelectedExactly(params string[] ids) => Ids(GodotSelected()) == Ids(ids) && EditorInterface.Singleton.GetSelection().GetSelectedNodes().Count == ids.Length;
    string Comp(string id) => string.Join(",", plugin.SelectedComponents(id));

    /// <summary>
    /// The owner's acceptance (2026-11-28) driven by window input: select a wall, Tab (and the mode menu), the first click on a vertex, edge or face picks it and a move edits it
    /// (each undone); two walls stay locked through clicks on empty space, the crate, the crate's Scene dock row and its group; Tab out keeps them; camera navigation and text
    /// fields keep their input; one component click is used once and Godot neither picks nor box-selects behind it.
    /// </summary>
    async Task RealInput(MapmakerDock dock)
    {
        plugin.EditKeysAnywhere = false;
        plugin.NewMap("editmode-real");
        var wall = Place(PieceKind.Box, "Wall", V(0, 0, 0), V(4, 2, 1));
        var crate = Place(PieceKind.Box, "Crate", V(6, 0, 0), V(2, 2, 2));
        var crateBefore = Obj(crate);
        bool CrateSame() => Obj(crate).Mesh is null && Obj(crate).Transform.Equals(crateBefore.Transform);
        await plugin.Frame3D(V(-3, -3, 0), V(8, 3, 3));
        await Frames(10);
        var centre = View.GetGlobalRect().GetCenter();
        PushMove(centre); await Frames(3);
        var hov = plugin.GetViewport().GuiGetHoveredControl();
        GD.Print($"EDITMODE note: over the 3D view the hovered control is {hov?.GetClass()} '{hov?.Name}', a child of {hov?.GetParent()?.GetClass()} (the picture's container is {View.GetClass()})");
        Check(hov is not null && hov != View && !View.IsAncestorOf(hov), "Godot's 3D input surface (not the picture's container) is what the mouse is over: the case the shipped hover check rejected");
        Check(plugin.TestOverEditSurface(centre), "the hover check now accepts Godot's 3D input surface");

        // 1. select the wall, Tab (real key, mouse over the view)
        plugin.Select([wall]); await Frames(3);
        PushMove(centre); await Frames(2);
        await RealKey(Godot.Key.Tab);
        Check(plugin.EditMode && plugin.EditIds.SequenceEqual([wall]) && SelectedExactly(wall), $"Tab over the view enters Edit mode on the wall; the wall stays selected (selected [{Ids(GodotSelected())}])");
        int top = MeshTopology.Box.FaceNamed("top");
        int restores0 = plugin.SelectionRestores, clicks0 = plugin.EditClicks;
        // first click: the top face (Face mode is the default)
        await RealKey(Godot.Key.Key3);
        var topAt = WinOfUnit(wall, V(0, 0, 1));
        await RealClick(topAt);
        Check(Comp(wall) == top.ToString(), $"the first click after Tab selects the face under the mouse (selected [{Comp(wall)}], wanted {top})");
        Check(plugin.EditClicks == clicks0 + 1, $"the click was used once by Edit mode ({plugin.EditClicks - clicks0})");
        Check(SelectedExactly(wall) && plugin.SelectionRestores == restores0 && !plugin.BoxDrawing, "Godot neither picked nor box-selected behind it: the wall is still the only selection, nothing had to be put back");
        // a Shift click toggles exactly once (a doubly handled click would toggle it back); off the face's centre, where the component gizmo now sits
        var topOff = WinOfUnit(wall, V(-0.4f, -0.3f, 1));
        await RealClick(topOff, shift: true);
        Check(Comp(wall) == "", $"Shift + click toggles the face off once ([{Comp(wall)}])");
        await RealClick(topOff, shift: true);
        Check(Comp(wall) == top.ToString(), $"and on again ([{Comp(wall)}])");
        PushMove(topAt); await Frames(2);
        await RealKeys(Godot.Key.G, Godot.Key.X, Godot.Key.Key1, Godot.Key.Enter); await Frames(3);
        var fm = Obj(wall).Mesh;
        Check(fm is not null && new[] { 4, 5, 6, 7 }.All(i => MathF.Abs(fm.Vertices[i].X - (MeshTopology.Box.StockVertices[i].X + 0.25f)) < 1e-5f) && new[] { 0, 1, 2, 3 }.All(i => fm.Vertices[i] == MeshTopology.Box.StockVertices[i]),
            "G X 1 Enter moves only the top face 1 m along X");
        Check(SelectedExactly(wall) && plugin.EditMode, "the wall stays selected after the edit");
        await RealUndo();
        Check(Obj(wall).Mesh is null && plugin.EditMode && SelectedExactly(wall), "undo: the stock shape again, Edit mode and the selection kept");
        // first click on a vertex
        await RealKey(Godot.Key.Key1);
        var v7 = WinOfUnit(wall, MeshTopology.Box.StockVertices[7]);
        await RealClick(v7 + new Vector2(3, 2));
        Check(Comp(wall) == "7", $"Vertex mode: a click next to a corner selects that vertex ([{Comp(wall)}])");
        PushMove(v7); await Frames(2);
        await RealKeys(Godot.Key.G, Godot.Key.Z, Godot.Key.Key1, Godot.Key.Enter); await Frames(3);
        var vm = Obj(wall).Mesh;
        Check(vm is not null && vm.Vertices[7] == V(0.5f, 0.5f, 2f) && Enumerable.Range(0, 7).All(i => vm.Vertices[i] == MeshTopology.Box.StockVertices[i]), "G Z 1 Enter lifts that vertex only");
        await RealUndo();
        Check(Obj(wall).Mesh is null, "undo");
        // first click on an edge
        await RealKey(Godot.Key.Key2);
        int e67 = MeshTopology.Box.EdgeId(6, 7);
        var mid = WinOfUnit(wall, (MeshTopology.Box.StockVertices[6] + MeshTopology.Box.StockVertices[7]) / 2);
        await RealClick(mid);
        Check(Comp(wall) == e67.ToString(), $"Edge mode: a click on an edge selects it ([{Comp(wall)}], wanted {e67})");
        PushMove(mid); await Frames(2);
        await RealKeys(Godot.Key.G, Godot.Key.Z, Godot.Key.Key1, Godot.Key.Enter); await Frames(3);
        var em = Obj(wall).Mesh;
        Check(em is not null && em.Vertices[6].Z == 2f && em.Vertices[7].Z == 2f && Enumerable.Range(0, 6).All(i => em.Vertices[i] == MeshTopology.Box.StockVertices[i]), "G Z 1 Enter lifts that edge only");
        await RealUndo();
        Check(Obj(wall).Mesh is null, "undo");
        // the component gizmo by real press, motion and release
        await RealKey(Godot.Key.Key3);
        await RealClick(topAt);
        await Frames(2);
        if (plugin.GizmoLayoutNow is { } g && g.Axes[0].LengthSquared() > 0.5f)
        {
            var tip = WinOfView(g.MoveTip(0) - g.Axes[0] * 10f);
            int c1 = plugin.EditClicks;
            PushMove(tip); await Frames(2);
            PushButton(tip, MouseButton.Left, true); await Frames(2);
            Check(plugin.EditDragging && plugin.EditClicks == c1 + 1, "a real press on the gizmo's X arrow starts a move (used once)");
            for (int i = 1; i <= 6; i++) { PushMove(tip + g.Axes[0] * (10f * i), MouseButtonMask.Left); await Frames(1); }
            float during = plugin.DimsOf(wall)!.Value.X;
            PushButton(tip + g.Axes[0] * 60f, MouseButton.Left, false); await Frames(3);
            Check(!plugin.EditDragging && during > 4.01f && MathF.Abs(Dims(wall).X - during) < 1e-4f, $"the real release applies the drag ({Dims(wall).X:0.00} m, previewed {during:0.00} m)");
            Check(SelectedExactly(wall) && plugin.SelectionRestores == restores0, "the wall stayed selected through the drag, with nothing put back");
            await RealUndo();
            Check(Obj(wall).Mesh is null, "undo");
        }
        else Check(false, "the gizmo has a layout (camera present)");
        // a left drag across the crate: no box select of Godot's or ours, no component box either
        var crateTop = WinOfUnit(crate, V(0, 0, 1));
        var emptyAt = View.GetGlobalRect().Position + new Vector2(30, View.GetGlobalRect().Size.Y - 30);
        PushMove(emptyAt); await Frames(2);
        PushButton(emptyAt, MouseButton.Left, true); await Frames(2);
        for (int i = 1; i <= 6; i++) { PushMove(emptyAt.Lerp(crateTop + new Vector2(40, -40), i / 6f), MouseButtonMask.Left); await Frames(1); }
        PushButton(crateTop + new Vector2(40, -40), MouseButton.Left, false); await Frames(4);
        Check(SelectedExactly(wall) && plugin.SelectionRestores == restores0 && !plugin.BoxDrawing && CrateSame(), "a left drag over the crate selects nothing behind Edit mode (no box, nothing put back)");
        // Tab out: the same object stays selected
        PushMove(centre); await Frames(2);
        await RealKey(Godot.Key.Tab);
        Check(!plugin.EditMode && SelectedExactly(wall) && !plugin.NodeOf(wall)!.HasMeta("_edit_lock_"), $"Tab back: Object mode with the wall still selected ([{Ids(GodotSelected())}])");

        // 1b. the same through the mode menu
        string how = dock.TestPickMode(1); await Frames(3);
        Check(plugin.EditMode && plugin.EditIds.SequenceEqual([wall]), $"the mode menu's Edit enters Edit mode on the wall (via {how})");
        await RealClick(topAt);
        Check(Comp(wall) == top.ToString() && SelectedExactly(wall), $"the first click after the menu selects the face, the wall stays selected ([{Comp(wall)}])");
        PushMove(topAt); await Frames(2);
        await RealKeys(Godot.Key.G, Godot.Key.X, Godot.Key.Key1, Godot.Key.Enter); await Frames(3);
        Check(Obj(wall).Mesh is { } mm && mm.Vertices[4].X > -0.5f + 0.2f, "and a move edits it");
        await RealUndo();
        how = dock.TestPickMode(0); await Frames(3);
        Check(!plugin.EditMode && SelectedExactly(wall), $"the menu's Object leaves like Tab, the wall still selected (via {how})");

        // 2. two walls locked against clicks elsewhere
        var wall2 = Place(PieceKind.Box, "Wall 2", V(0, -5, 0), V(4, 2, 1));
        plugin.Select([crate]); await Frames(2);
        var grp = plugin.GroupSelected("Props");
        string? groupId = plugin.Session!.Document.Groups.FirstOrDefault(gr => gr.Name == "Props")?.Id;
        Check(grp.Ok && groupId is not null, "a group holding the crate");
        // the Scene dock's row of the crate, clicked in Object mode first (it selects the crate there).
        // Note: with ONE object selected the inspector edits that MapmakerObject, which this plugin handles, so Godot forwards the 3D view's input to it even without
        // "forwarding always on"; the two-wall section below (the inspector edits several nodes, which the plugin does not handle) is the part that needs it.
        var sceneDock = EditorInterface.Singleton.GetBaseControl().FindChildren("*", "SceneTreeDock", true, false).OfType<Control>().FirstOrDefault();
        var sceneTree = sceneDock?.FindChildren("*", "Tree", true, false).OfType<Tree>().FirstOrDefault(t => t.IsVisibleInTree());
        Vector2? RowAt(string nodeName)
        {
            if (sceneTree is null) return null;
            TreeItem? Find(TreeItem? it) { for (; it is not null; it = it.GetNext()) { if (it.GetText(0) == nodeName) return it; if (Find(it.GetFirstChild()) is { } f) return f; } return null; }
            if (Find(sceneTree.GetRoot()) is not { } item) return null;
            sceneTree.ScrollToItem(item);
            for (float y = 2; y < sceneTree.Size.Y; y += 2)
                for (float x = 40; x < sceneTree.Size.X && x < 120; x += 20)
                    if (sceneTree.GetItemAtPosition(new Vector2(x, y)) == item) return sceneTree.GetGlobalRect().Position + new Vector2(x, y);
            return null;
        }
        var crateName = plugin.NodeOf(crate)!.Name.ToString();
        plugin.Select([wall]); await Frames(3);
        var row = RowAt(crateName);
        bool rowWorks = false;
        if (row is { } r0) { await RealClick(r0); await Frames(4); rowWorks = SelectedExactly(crate); }
        GD.Print($"EDITMODE note: Scene dock tree {(sceneTree is null ? "not found" : "found")}, crate row {(row is null ? "not found" : "at " + row)}, a click on it in Object mode selects [{Ids(GodotSelected())}]");
        Check(rowWorks, "in Object mode a real click on the crate's Scene dock row selects the crate (the row click reaches Godot)");

        plugin.Select([wall, wall2]); await Frames(3);
        PushMove(centre); await Frames(2);
        await RealKey(Godot.Key.Tab);
        Check(plugin.EditMode && Ids(plugin.EditIds.ToArray()) == Ids(wall, wall2) && SelectedExactly(wall, wall2), "two walls selected, Tab: both editable and selected");
        await RealClick(topAt);
        Check(Comp(wall) == top.ToString(), "a face of the first wall picked");
        int restores = plugin.SelectionRestores;
        await RealClick(emptyAt);
        Check(SelectedExactly(wall, wall2) && Comp(wall) == "" && Comp(wall2) == "" && plugin.SelectionRestores == restores, "a click on empty space clears the component highlight; both walls stay selected (Godot never saw it)");
        await RealClick(crateTop);
        Check(SelectedExactly(wall, wall2) && plugin.SelectionRestores == restores && CrateSame() && !plugin.IsInEditSet(crate), "a click on the crate: both walls stay selected, the crate is untouched and not editable");
        await RealClick(crateTop, doubleClick: true);
        Check(SelectedExactly(wall, wall2) && plugin.SelectionRestores == restores, "a double click on the grouped crate does not select its group");
        if (row is { } r1)
        {
            await RealClick(r1); await Frames(4);
            Check(SelectedExactly(wall, wall2) && plugin.SelectionRestores > restores && Ids(plugin.EditIds.ToArray()) == Ids(wall, wall2) && plugin.EditMode,
                $"a real click on the crate's Scene dock row: Edit mode puts both walls back ([{Ids(GodotSelected())}], put back {plugin.SelectionRestores - restores} time(s))");
        }
        restores = plugin.SelectionRestores;
        Check(dock.TestSelectSceneRow(groupId ?? "", groupList: true), "the Mapmaker dock lists the group");
        await Frames(4);
        Check(SelectedExactly(wall, wall2) && plugin.SelectionRestores > restores && plugin.EditMode, "selecting the group row in the Mapmaker dock: both walls put back, Edit mode stays");
        restores = plugin.SelectionRestores;
        Check(dock.TestSelectSceneRow(crate, groupList: false), "the Mapmaker dock lists the crate");
        await Frames(4);
        Check(SelectedExactly(wall, wall2) && plugin.SelectionRestores > restores, "selecting the crate's row in the Mapmaker dock's Objects list: both walls put back");
        int settled = plugin.SelectionRestores;
        await Frames(10);
        Check(plugin.SelectionRestores == settled, "putting the selection back does not loop");
        Check(CrateSame(), "the crate is unchanged");

        // 3. camera navigation and text fields keep their input in Edit mode
        var cam = Vp.GetCamera3D()!;
        var t0 = cam.GlobalTransform;
        PushMove(centre); await Frames(2);
        for (int i = 0; i < 3; i++) { PushButton(centre, MouseButton.WheelUp, true); PushButton(centre, MouseButton.WheelUp, false); await Frames(2); }
        await Frames(20);
        Check(cam.GlobalPosition.DistanceTo(t0.Origin) > 1e-3f && plugin.EditMode && SelectedExactly(wall, wall2), $"the wheel still zooms in Edit mode (camera moved {cam.GlobalPosition.DistanceTo(t0.Origin):0.000} m)");
        var t1 = cam.GlobalTransform;
        PushButton(centre, MouseButton.Middle, true); await Frames(1);
        for (int i = 1; i <= 8; i++) { PushMove(centre + new Vector2(8 * i, 0), MouseButtonMask.Middle, new Vector2(8, 0)); await Frames(1); }
        PushButton(centre + new Vector2(64, 0), MouseButton.Middle, false); await Frames(20);
        Check(!cam.GlobalTransform.Basis.IsEqualApprox(t1.Basis), "a middle drag still orbits");
        var t2 = cam.GlobalTransform;
        PushButton(centre, MouseButton.Middle, true, shift: true); await Frames(1);
        for (int i = 1; i <= 8; i++) { PushMove(centre + new Vector2(0, 6 * i), MouseButtonMask.Middle, new Vector2(0, 6), shift: true); await Frames(1); }
        PushButton(centre + new Vector2(0, 48), MouseButton.Middle, false, shift: true); await Frames(20);
        Check(cam.GlobalPosition.DistanceTo(t2.Origin) > 1e-3f && Comp(wall) == "" && SelectedExactly(wall, wall2), "Shift + middle drag still pans; no component or object selection changed");
        // freelook: right mouse held, the movement keys are the camera's (Input.ParseInputEvent: Godot polls the held keys from Input)
        await RealClick(topAt);
        Check(Comp(wall) == top.ToString(), "a face selected again");
        var t3 = cam.GlobalPosition;
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = centre, GlobalPosition = centre, ButtonMask = MouseButtonMask.Right });
        await Frames(2);
        Tree.Root.PushInput(new InputEventKey { Keycode = Godot.Key.G, PhysicalKeycode = Godot.Key.G, Pressed = true });
        await Frames(1);
        Tree.Root.PushInput(new InputEventKey { Keycode = Godot.Key.G, PhysicalKeycode = Godot.Key.G, Pressed = false });
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.W, PhysicalKeycode = Godot.Key.W, Pressed = true });
        await Frames(20);
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.W, PhysicalKeycode = Godot.Key.W, Pressed = false });
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = centre, GlobalPosition = centre });
        await Frames(4);
        Check(!plugin.EditDragging && plugin.EditMode, "with the right button held, G is left to the camera (no edit starts)");
        GD.Print($"EDITMODE note: freelook W with the right button held moved the camera {cam.GlobalPosition.DistanceTo(t3):0.000} m (0 when the headless editor does not run its freelook from Input)");
        // text fields: a dock field, a number field, and the Scene dock's filter
        var fields = new System.Collections.Generic.List<(string What, LineEdit Edit)>();
        for (Node? up = dock; up is not null; up = up.GetParent())   // bring the Mapmaker dock to the front of its dock slot, on its Object tab
            if (up.GetParent() is TabContainer tc) { tc.CurrentTab = up.GetIndex(); break; }
        dock.ShowTab(DockTab.Object); dock.Refresh(); await Frames(6);
        if (dock.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().FirstOrDefault(l => l.IsVisibleInTree() && l.Editable && l.GetParent() is not SpinBox) is { } dl) fields.Add(("a Mapmaker dock text field", dl));
        if (dock.FindChildren("*", "SpinBox", true, false).Concat(EditorInterface.Singleton.GetBaseControl().FindChildren("*", "SpinBox", true, false)).OfType<SpinBox>().FirstOrDefault(s => s.IsVisibleInTree() && s.Editable) is { } sb) fields.Add(("a number field", sb.GetLineEdit()));
        if (sceneDock?.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().FirstOrDefault(l => l.IsVisibleInTree()) is { } fl) fields.Add(("the Scene dock's filter", fl));
        GD.Print($"EDITMODE note: text fields found: {string.Join(", ", fields.Select(f => f.What))}");
        Check(fields.Count >= 2, "text fields to type into are on screen");
        string docBefore = plugin.Session!.Document.ToJson();
        foreach (var (what, edit) in fields)
        {
            string before = edit.Text;
            edit.GrabFocus(); await Frames(2);
            PushMove(centre); await Frames(2);   // the mouse rests over the 3D view while typing
            bool number = edit.GetParent() is SpinBox;
            string typed = number ? "12" : "g1";   // G would start a move and 1 / 2 switch to Vertex / Edge if Edit mode took them
            edit.SelectAll();
            foreach (char ch in typed) await RealKey(ch == 'g' ? Godot.Key.G : ch == '1' ? Godot.Key.Key1 : Godot.Key.Key2, unicode: ch);
            Check(plugin.EditMode && !plugin.EditDragging && plugin.ComponentMode == ComponentMode.Face && edit.Text.Contains(typed), $"typing in {what} with the mouse over the view: the field gets '{typed}' ('{edit.Text}'), Edit mode takes none of the keys");
            await RealKey(Godot.Key.Tab);
            Check(plugin.EditMode, $"Tab in {what} is the field's: Edit mode stays on");
            edit.ReleaseFocus();
            if (plugin.GetViewport().GuiGetFocusOwner() is { } fo) fo.ReleaseFocus();
            await Frames(3);
            if (plugin.Session!.Document.ToJson() != docBefore)
            {
                // the field applied what was typed to the locked walls (a rename, a set-transform): one undo step, taken back by the editor's real Ctrl+Z
                await RealUndo();
                Check(plugin.Session!.Document.ToJson() == docBefore && Obj(wall).Mesh is null && Obj(wall2).Mesh is null, $"{what}: its commit is one undo step and a real Ctrl+Z takes it back (walls as before)");
            }
            if (edit.Text != before && plugin.Session!.Document.ToJson() == docBefore) edit.Text = before;   // the Scene dock filter: show every row again
            await Frames(2);
        }
        // the Scene dock's rename (its row editor)
        if (sceneTree is not null && sceneTree.GetSelected() is not null)
        {
            sceneTree.EditSelected(true); await Frames(3);
            var renameEdit = sceneTree.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().FirstOrDefault(l => l.IsVisibleInTree());
            if (renameEdit is not null)
            {
                string before = renameEdit.Text;
                renameEdit.GrabFocus(); await Frames(1);
                PushMove(centre); await Frames(2);
                await RealKey(Godot.Key.G, unicode: 'g');
                Check(plugin.EditMode && !plugin.EditDragging && renameEdit.Text != before, $"renaming in the Scene dock: the row editor gets the key ('{renameEdit.Text}'), no edit starts");
                renameEdit.Text = before;
                await RealKey(Godot.Key.Escape); await Frames(3);
            }
            else GD.Print("EDITMODE note: the Scene dock's row editor did not open here (not checked)");
        }
        else GD.Print("EDITMODE note: no selected Scene dock row to rename (not checked)");
        Check(SelectedExactly(wall, wall2) && plugin.EditMode, "after the navigation and typing both walls are still selected in Edit mode");

        // Tab out keeps both walls; then the crate alone
        PushMove(centre); await Frames(2);
        await RealKey(Godot.Key.Tab);
        Check(!plugin.EditMode && SelectedExactly(wall, wall2), $"Tab back: both walls remain selected ([{Ids(GodotSelected())}])");
        plugin.Select([crate]); await Frames(3);
        PushMove(centre); await Frames(2);
        await RealKey(Godot.Key.Tab);
        Check(plugin.EditMode && plugin.EditIds.SequenceEqual([crate]) && SelectedExactly(crate), "select the crate, Tab: only the crate is editable");
        PushMove(centre); await Frames(2);
        await RealKey(Godot.Key.Tab);
        Check(!plugin.EditMode && SelectedExactly(crate), "Tab back with the crate selected");
        EditorInterface.Singleton.GetSelection().Clear(); await Frames(2);
        await RealKey(Godot.Key.Tab);
        Check(!plugin.EditMode, "with nothing editable selected, Tab stays in Object mode");

        await HeldTab(wall);
        await Freelook(wall, topOff, topAt);
        await PlacingBlocked(dock, wall);
        await LockLeaks(wall, wall2);
        plugin.EditKeysAnywhere = true;
    }

    /// <summary>A held Tab: its repeats are used (no focus traversal through the editor), in Object mode and in Edit mode.</summary>
    async Task HeldTab(string wall)
    {
        var centre = View.GetGlobalRect().GetCenter();
        plugin.Select([wall]); await Frames(3);
        PushMove(centre); await Frames(2);
        var surface = plugin.GetViewport().GuiGetHoveredControl();
        surface?.GrabFocus(); await Frames(1);
        var focus0 = plugin.GetViewport().GuiGetFocusOwner();
        PushTab(true); await Frames(1);
        for (int i = 0; i < 4; i++) { PushTab(true, echo: true); await Frames(1); }
        PushTab(false); await Frames(2);
        Check(plugin.EditMode && plugin.GetViewport().GuiGetFocusOwner() == focus0, $"a held Tab enters Edit mode once; its repeats move no focus (focus {plugin.GetViewport().GuiGetFocusOwner()?.GetClass()}, was {focus0?.GetClass()})");
        PushTab(true); await Frames(1);
        for (int i = 0; i < 4; i++) { PushTab(true, echo: true); await Frames(1); }
        PushTab(false); await Frames(2);
        Check(!plugin.EditMode && plugin.GetViewport().GuiGetFocusOwner() == focus0 && SelectedExactly(wall), "held again it leaves once; the repeats neither re-enter nor move the focus");
        focus0?.ReleaseFocus();
    }

    /// <summary>Freelook (right button held, or Shift+F): every key is the camera's; Shift+F during an edit cancels the edit and still reaches Godot.</summary>
    async Task Freelook(string wall, Vector2 faceAt, Vector2 centreOfFace)
    {
        var centre = View.GetGlobalRect().GetCenter();
        plugin.Select([wall]); await Frames(3);
        PushMove(centre); await Frames(2);
        await RealKey(Godot.Key.Tab);
        await RealKey(Godot.Key.Key3);
        await RealClick(faceAt);
        Check(plugin.EditMode && plugin.SelectedComponents(wall).Count == 1, "Edit mode on the wall with a face selected");
        PushMove(centre); await Frames(2);
        var surface = plugin.GetViewport().GuiGetHoveredControl();   // the 3D view's input surface has the keyboard focus after a click in it, as for the user
        Check(await WheelMovesCamera(), "before: Godot's freelook is off (the wheel zooms)");
        plugin.FreelookForTest = true;   // Godot captures the mouse in freelook; a headless editor cannot, so the test says so
        // (no digits here: passed on to Godot, 1 / 3 / 7 are its emulated-numpad views, which turn the camera orthogonal)
        await RealKeys(Godot.Key.W, Godot.Key.A, Godot.Key.S, Godot.Key.D, Godot.Key.Q, Godot.Key.E, Godot.Key.G, Godot.Key.R);
        bool shiftF = plugin.TestInput(new InputEventKey { Keycode = Godot.Key.F, PhysicalKeycode = Godot.Key.F, ShiftPressed = true, Pressed = true });   // not pushed here: Godot's freelook is not really on
        await RealKey(Godot.Key.Tab);
        Check(!shiftF && plugin.EditMode && !plugin.EditDragging && plugin.SelectedComponents(wall).Count == 1 && Obj(wall).Mesh is null,
            "in freelook W A S D Q E, G, R, Shift+F and Tab are all left to Godot: no edit starts, still Edit mode");
        plugin.FreelookForTest = false;
        surface?.GrabFocus(); await Frames(1);   // Tab moved the focus on (it was Godot's); back to the view
        await RealKey(Godot.Key.Q);   // Godot's Select tool again (W, E and R chose its Move, Rotate and Scale tools)
        await RealKey(Godot.Key.G);
        Check(plugin.EditDragging, "out of freelook, G starts a move again");
        var before = plugin.Session!.Document.ToJson();
        await RealKey(Godot.Key.F, shift: true);
        Check(!plugin.EditDragging && plugin.Session!.Document.ToJson() == before, "Shift+F during an edit cancels it");
        Check(!await WheelMovesCamera(), "and reaches Godot: its freelook is on (the wheel sets the fly speed, it no longer zooms)");
        plugin.FreelookForTest = true;   // what the captured mouse says in a real window while freelook runs
        await RealKey(Godot.Key.G);
        await RealKey(Godot.Key.F, shift: true);
        plugin.FreelookForTest = false;
        Check(!plugin.EditDragging && await WheelMovesCamera(), "in freelook G is left alone and a second Shift+F reaches Godot: freelook off, the wheel zooms again");
        GD.Print($"EDITMODE note: mouse mode during freelook stays {Input.MouseMode} headless (Godot captures it in a real window: FreelookActive's capture test and Godot's own Shift+F toggle need a windowed check)");
        await RealKey(Godot.Key.Tab);
        Check(!plugin.EditMode && SelectedExactly(wall), "Tab back to Object mode");
    }

    /// <summary>Godot's freelook is off when a wheel step zooms the camera (in freelook the wheel changes the fly speed). The step is taken back.</summary>
    async Task<bool> WheelMovesCamera()
    {
        var centre = View.GetGlobalRect().GetCenter();
        var cam = Vp.GetCamera3D()!;
        var p0 = cam.GlobalPosition;
        PushMove(centre); await Frames(1);
        PushButton(centre, MouseButton.WheelUp, true); PushButton(centre, MouseButton.WheelUp, false);
        await Frames(15);
        bool moved = cam.GlobalPosition.DistanceTo(p0) > 1e-3f;
        PushButton(centre, MouseButton.WheelDown, true); PushButton(centre, MouseButton.WheelDown, false);   // back (the zoom, or in freelook the fly speed)
        await Frames(15);
        return moved;
    }

    /// <summary>Library placing is refused in Edit mode on every route; something armed before is disarmed on entry.</summary>
    async Task PlacingBlocked(MapmakerDock dock, string wall)
    {
        var centre = View.GetGlobalRect().GetCenter();
        var preset = PiecePreset.All.First();
        var item = new LibraryItem(LibraryKind.Piece, preset.Id, preset.Label);
        int count0 = plugin.Session!.Document.Objects.Count;
        plugin.Select([wall]); await Frames(2);
        dock.TestArm(item); await Frames(2);
        Check(MapHost.Tool == Tool.Place && MapHost.Armed == item, "Object mode: a Library row arms its item (Place tool)");
        plugin.Select([wall]); await Frames(2);
        PushMove(centre); await Frames(2);
        await RealKey(Godot.Key.Tab);
        Check(plugin.EditMode && MapHost.Armed is null && MapHost.Tool == Tool.Select, "entering Edit mode disarms it");
        await RealClick(View.GetGlobalRect().Position + new Vector2(30, View.GetGlobalRect().Size.Y - 30));
        dock.TestArm(item); await Frames(2);
        Check(MapHost.Armed is null && MapHost.Tool == Tool.Select && plugin.Status.Contains("Edit mode"), $"a Library row (or Build preset) does not arm in Edit mode, and says why ('{plugin.Status}')");
        plugin.SetTool(Tool.Place); await Frames(1);
        Check(MapHost.Tool == Tool.Select, "the Place tool stays off in Edit mode");
        dock.TestDropOutside(item, centre); await Frames(3);
        var placed = plugin.PlaceAt(item, new Vector3(20, 20, 0));
        Check(plugin.Session!.Document.Objects.Count == count0 && MapHost.Armed is null && !placed.Ok, "a drag from the list onto the view and a direct placement place nothing in Edit mode");
        Check(SelectedExactly(wall) && plugin.EditMode, "the wall stays selected in Edit mode");
        PushMove(centre); await Frames(2);
        await RealKey(Godot.Key.Tab);
        dock.TestArm(item); await Frames(2);
        Check(!plugin.EditMode && MapHost.Tool == Tool.Place && MapHost.Armed == item, "back in Object mode the row arms again");
        plugin.SetTool(Tool.Select); await Frames(1);
    }

    /// <summary>Godot's move lock that Edit mode sets never outlives Edit mode: undo past a placement, a delete in Edit mode and its undo.</summary>
    async Task LockLeaks(string wall, string wall2)
    {
        var centre = View.GetGlobalRect().GetCenter();
        // place, Tab, undo past the placement (Edit mode ends by itself), redo
        var wall3 = Place(PieceKind.Box, "Wall 3", V(0, 8, 0), V(4, 2, 1));
        plugin.Select([wall3]); await Frames(3);
        PushMove(centre); await Frames(2);
        await RealKey(Godot.Key.Tab);
        Check(plugin.EditMode && plugin.NodeOf(wall3)!.HasMeta("_edit_lock_"), "a new wall in Edit mode carries the move lock");
        await RealUndo();
        Check(plugin.Session!.Document.Find(wall3) is null && !plugin.EditMode && plugin.EditLocksHeld == 0, "a real Ctrl+Z past its placement: the wall goes, Edit mode ends by itself, no lock is left");
        await RealUndo(redo: true);
        if (plugin.Session!.Document.Find(wall3) is null) { plugin.RedoLast(); await Frames(4); GD.Print("EDITMODE note: Ctrl+Shift+Z did not redo here; redone through the editor's history"); }
        Check(plugin.NodeOf(wall3) is { } n3 && !n3.HasMeta("_edit_lock_"), "redo brings the wall back without the lock");
        // two walls, one deleted in Edit mode, then Tab out and undo
        plugin.Select([wall, wall2]); await Frames(3);
        PushMove(centre); await Frames(2);
        await RealKey(Godot.Key.Tab);
        var del = plugin.Do("Delete", MapmakerPlugin.Op("delete", ("ids", MapmakerPlugin.Strs([wall2]))));
        await Frames(3);
        Check(del.Ok && plugin.EditMode && plugin.EditIds.SequenceEqual([wall]) && plugin.EditLocksHeld == 1, "deleting one of two walls in Edit mode (as the Scene dock or the dock would): the other stays in Edit mode");
        await RealKey(Godot.Key.Tab);
        await RealUndo();
        Check(plugin.NodeOf(wall2) is { } n2 && !n2.HasMeta("_edit_lock_") && !plugin.NodeOf(wall)!.HasMeta("_edit_lock_") && plugin.EditLocksHeld == 0, "Tab out, then undo the delete: both walls without the lock");
        // the same, undone while still in Edit mode: the wall rejoins
        plugin.Select([wall, wall2]); await Frames(3);
        PushMove(centre); await Frames(2);
        await RealKey(Godot.Key.Tab);
        plugin.Do("Delete", MapmakerPlugin.Op("delete", ("ids", MapmakerPlugin.Strs([wall2])))); await Frames(3);
        await RealUndo();
        Check(plugin.EditMode && Ids(plugin.EditIds.ToArray()) == Ids(wall, wall2) && plugin.NodeOf(wall2)!.HasMeta("_edit_lock_") && SelectedExactly(wall, wall2),
            $"undoing the delete in Edit mode: the wall rejoins the edit set, locked and selected (edit set [{Ids(plugin.EditIds.ToArray())}], selected [{Ids(GodotSelected())}])");
        PushMove(centre); await Frames(2);
        await RealKey(Godot.Key.Tab);
        Check(!plugin.EditMode && !plugin.NodeOf(wall)!.HasMeta("_edit_lock_") && !plugin.NodeOf(wall2)!.HasMeta("_edit_lock_") && plugin.EditLocksHeld == 0 && SelectedExactly(wall, wall2), "Tab out: both unlocked and still selected");
    }

    static void Save(SubViewport vp, string dir, string name) => vp.GetTexture().GetImage().SavePng(Path.Combine(dir, name));

    void SaveWindow(string dir, string name) => plugin.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name));
}
