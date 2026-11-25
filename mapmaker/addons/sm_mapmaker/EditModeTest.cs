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
            else Check(false, "the gizmo has a layout (camera present)");

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

    static void Save(SubViewport vp, string dir, string name) => vp.GetTexture().GetImage().SavePng(Path.Combine(dir, name));

    void SaveWindow(string dir, string name) => plugin.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(dir, name));
}
