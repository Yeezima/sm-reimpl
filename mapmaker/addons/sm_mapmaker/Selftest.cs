using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmMapmaker;
using Num = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>
/// Test aid (agents, never the owner): <c>godot.cmd --windowed --editor --path mapmaker -- --mm-selftest [--mm-out dir]</c> drives the plugin through the same
/// calls the dock and the viewport clicks make: open Manufactorum, pick, extract to a prefab, place, edit, save; screenshots of the editor viewport; then quits.
/// </summary>
public sealed class Selftest(MapmakerPlugin plugin)
{
    public static bool Wanted => OS.GetCmdlineUserArgs().Contains("--mm-selftest");
    static string Arg(string name, string fallback) { var a = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }

    SceneTree Tree => (SceneTree)Engine.GetMainLoop();
    async Task Frames(int n) { for (int i = 0; i < n; i++) await plugin.ToSignal(Tree, SceneTree.SignalName.ProcessFrame); }

    void Shot(string name)
    {
        if (!Thumbnails.CanRender) { GD.Print($"SELFTEST shot {name}: headless, skipped"); return; }
        string dir = Arg("--mm-out", System.IO.Path.Combine(GameInstall.DataRoot, "cache", "previews", "mapmaker"));
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, name + ".png");
        plugin.Viewport3D?.GetTexture().GetImage().SavePng(path);
        Tree.Root.GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, name + "-window.png"));
        GD.Print($"SELFTEST shot {path}");
    }

    async Task UndoRedoRound(LibraryItem item)
    {
        plugin.NewMap("undo-redo");
        var root = MapHost.Root!;
        var ur = plugin.History;
        var history = ur.GetHistoryUndoRedo(ur.GetObjectHistoryId(root));
        string Doc() => plugin.Session!.Document.ToJson();
        void NodesMatch(string when)
        {
            bool all = true;
            foreach (var o in plugin.Session!.Document.Objects)
            {
                var n = plugin.NodeOf(o.Id);
                if (n is null || !n.IsInsideTree() || !n.Transform.IsEqualApprox(plugin.ExpectedTransform(o))) all = false;
            }
            int live = root.GetChildren().OfType<MapmakerObject>().Count();
            Check(all && live == plugin.Session.Document.Objects.Count, $"{when}: every node matches the document ({live} nodes, {plugin.Session.Document.Objects.Count} objects)");
        }
        string s0 = Doc();
        var placed = plugin.PlaceAt(item, new Godot.Vector3(2, 2, 0));
        string id = placed.Created[0];
        string s1 = Doc();
        var node = plugin.NodeOf(id)!;
        var from = node.Position;
        ur.CreateAction("Gizmo move", UndoRedo.MergeMode.Disable, root);
        ur.AddDoProperty(node, Node3D.PropertyName.Position, from + new Godot.Vector3(3, 0, 0));
        ur.AddUndoProperty(node, Node3D.PropertyName.Position, from);
        ur.CommitAction();
        await Frames(6);
        string s2 = Doc();
        Check(s2 != s1, "gizmo move reached the document");
        plugin.Select([id]);
        plugin.DeleteSelected();
        string s3 = Doc();
        Check(s3 != s2 && plugin.Session!.Document.Objects.Count == 0, "delete");
        NodesMatch("after delete");
        var expectUndo = new[] { s2, s1, s0 }; var expectRedo = new[] { s1, s2, s3 };
        for (int i = 0; i < 3; i++)
        {
            history.Undo(); await Frames(6);
            Check(Doc() == expectUndo[i], $"undo {i + 1}: document bytes equal the snapshot");
            NodesMatch($"undo {i + 1}");
        }
        for (int i = 0; i < 3; i++)
        {
            history.Redo(); await Frames(6);
            Check(Doc() == expectRedo[i], $"redo {i + 1}: document bytes equal the snapshot");
            NodesMatch($"redo {i + 1}");
        }
    }

    /// <summary>Owner's report (2026-11-05): the mouse wheel did not zoom the 3D view. Sends wheel events through the window like a real mouse.</summary>
    async Task WheelZoomCheck()
    {
        var vp = plugin.Viewport3D;
        if (vp?.GetParent() is not Control container || vp.GetCamera3D() is not { } cam) { Check(false, "wheel zoom: no editor viewport"); return; }
        var at = container.GetGlobalRect().GetCenter();
        Godot.Vector3 before = cam.GlobalPosition;
        for (int i = 0; i < 6; i++)
        {
            foreach (bool pressed in new[] { true, false })
                Tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = pressed, Factor = 1f, Position = at, GlobalPosition = at });
            await Frames(2);
        }
        await Frames(30);
        float moved = cam.GlobalPosition.DistanceTo(before);
        // Framed at a few hundred metres, six wheel steps must cover a good share of that, not the fraction of a metre the owner saw.
        Check(moved > 20f, $"mouse wheel zooms the 3D view in steps that suit the map (six steps moved the camera {moved:0.0} m)");
    }

    /// <summary>Owner's report (2026-11-05): clicks selected nothing. A real click (press and release through the window) with the Select tool on the
    /// middle of the view must pick the SM1 piece there and leave the scene root unselected.</summary>
    async Task SelectClickCheck()
    {
        var vp = plugin.Viewport3D;
        if (vp?.GetParent() is not Control container || MapHost.Sm1 is not { } src || src.BoundsCore() is not { } core) { Check(false, "select click: no editor viewport or map"); return; }
        plugin.SetTool(Tool.Select);
        // Down among the props, as the owner was: 20 m box at the arena centre, a few metres above the floor.
        var c = (core.Min + core.Max) / 2;
        await plugin.Frame3D(new Num.Vector3(c.X - 10, c.Y - 10, core.Min.Z), new Num.Vector3(c.X + 10, c.Y + 10, core.Min.Z + 6));
        var rect = container.GetGlobalRect();
        var picked = new System.Collections.Generic.HashSet<string>();
        bool rootSelected = false;
        int wholeComposite = 0;
        for (int gx = 1; gx <= 5; gx++)
            for (int gy = 1; gy <= 5; gy++)
            {
                var at = rect.Position + rect.Size * new Godot.Vector2(gx / 6f, gy / 6f);
                MapHost.PickedSm1.Clear();
                foreach (bool pressed in new[] { true, false })
                {
                    Tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = at, GlobalPosition = at });
                    await Frames(2);
                }
                await Frames(3);
                foreach (var p in MapHost.PickedSm1) { picked.Add(src.Label(p)); if (p.Part < 0 && src.IsComposite(p.Object)) wholeComposite++; }
                rootSelected |= EditorInterface.Singleton.GetSelection().GetSelectedNodes().Contains(MapHost.Root);
            }
        Check(picked.Count >= 4 && !rootSelected, $"clicks across the view with the Select tool pick the pieces under the mouse ({picked.Count} different: {string.Join(", ", picked.Take(8))})");
        Check(wholeComposite == 0, $"a click never picks a composite placement (tarmacgroundplane) whole ({wholeComposite} did)");
        Shot("click-picking");
        MapHost.PickedSm1.Clear(); plugin.RefreshPicked();
    }

    static void Check(bool ok, string what) { GD.Print($"SELFTEST {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) Failed = true; }
    static bool Failed;

    public async Task Run()
    {
        try
        {
            await Frames(30);
            while (MapHost.Root is null) { plugin.EnsureSceneRoot(); await Frames(20); }
            string mapsDir = Arg("--mm-lib", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mm-selftest-" + System.Environment.ProcessId + "-" + DateTime.UtcNow.Ticks));
            MapHost.Library = new MapLibrary(mapsDir);
            GD.Print($"SELFTEST library {mapsDir}");
            { var dk = plugin.Dock!; GD.Print($"SELFTEST dock parent={dk.GetParent()?.Name}/{dk.GetParent()?.GetParent()?.Name} visible={dk.IsVisibleInTree()} size={dk.Size}"); }
            plugin.NewMap("selftest");
            Check(plugin.Session is not null, "new map");

            // 1. Manufactorum from the install, in memory.
            string map = "mp_tdm_09";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var source = await Task.Run(() => Sm1Source.Load(map));
            var notes = new System.Collections.Generic.List<string>();
            MapHost.Root!.ShowBackdrop(source, notes);
            MapHost.Register(source);
            GD.Print($"SELFTEST loaded {map}: {source.Scene.Objects.Count} placements, {source.Scene.Definitions.Count} definitions, {watch.ElapsedMilliseconds} ms, {notes.Count} notes");
            Check(MapHost.Root.Backdrop is { Instances: > 0 }, $"backdrop drawn ({MapHost.Root.Backdrop?.Instances} instances)");
            if (source.BoundsCore() is { } all) await plugin.Frame3D(all.Min, all.Max);
            plugin.Dock!.ShowTab(1);
            await Frames(240);
            Shot("manufactorum-in-editor");
            await WheelZoomCheck();
            await SelectClickCheck();

            // 2. Pick by ray (the viewport click path), then extract.
            var centre = source.BoundsAll()!.Value;
            var mid = (centre.Min + centre.Max) / 2;
            int first = Enumerable.Range(0, source.Scene.Objects.Count).Where(i => !source.Hidden.Contains(i) && source.Scene.Objects[i].Name.Length > 0)
                .OrderBy(i => (source.Scene.Objects[i].Transform.Translation - mid).Length()).First();
            var wb = source.WorldBounds(first)!.Value;
            var target = (wb.Min + wb.Max) / 2;
            int hit = source.RayPick(target + new Num.Vector3(0, 0, 60), new Num.Vector3(0, 0, -1), out _);
            Check(hit >= 0, $"ray pick from above hits a placement ({(hit >= 0 ? source.Scene.Objects[hit].Name : "-")})");
            MapHost.PickedSm1.Clear();
            var near = Enumerable.Range(0, source.Scene.Objects.Count).Where(i => !source.Hidden.Contains(i) && source.Scene.Objects[i].Name.Length > 0 && source.WorldBounds(i) is not null)
                .OrderBy(i => (source.Scene.Objects[i].Transform.Translation - source.Scene.Objects[first].Transform.Translation).Length()).Take(4).ToList();
            MapHost.PickedSm1.AddRange(near.Select(i => new PickRef(i)));
            plugin.RefreshPicked();
            await Frames(10);
            Shot("manufactorum-picked");
            var dock = plugin.Dock!;
            var r = dock.Extract("Selftest piece", "selftest-piece", "bounds-bottom-center", "mesh");
            Check(r.Ok, $"extract-sm1 -> prefab ({string.Join("; ", r.Errors.Select(e => e.Message))})");
            var made = MapHost.Library.GetPrefab("selftest-piece");
            Check(made?.Parts.Count == near.Count, $"prefab has {near.Count} parts (got {made?.Parts.Count})");
            Check(made is not null && made.Provenance.Indices.OrderBy(x => x).SequenceEqual(near.OrderBy(x => x)), "provenance.indices equals the picked placement indices");

            // 2b. One part of a composite placement (owner's report 2026-11-05: the gate he clicked is part of tarmacgroundplane).
            {
                int tarmac = source.IndexOf("tarmacgroundplane");
                Check(tarmac >= 0 && source.IsComposite(tarmac), "tarmacgroundplane is picked part by part");
                int parts = tarmac >= 0 && source.Scene.Definitions.TryGetValue(source.Scene.Objects[tarmac].Definition, out var tdef) ? tdef.NearestLod().Count() : 0;
                int tall = Enumerable.Range(0, parts).FirstOrDefault(k => source.WorldBounds(new PickRef(tarmac, k)) is { } pb && pb.Max.Z - pb.Min.Z > 8f, -1);
                MapHost.PickedSm1.Clear(); MapHost.PickedSm1.Add(new PickRef(tarmac, tall));
                var gr = dock.Extract("Selftest gate", "selftest-gate", "bounds-bottom-center", "mesh");
                var gate = MapHost.Library.GetPrefab("selftest-gate");
                Check(gr.Ok && tall >= 0 && gate?.Parts.Count == 1 && gate.Parts[0].Asset?.Part == tall && !gate.Parts[0].Name.Contains("tarmac", StringComparison.OrdinalIgnoreCase),
                    $"extract one part of tarmacgroundplane (part {tall}, named {gate?.Parts[0].Name}): {string.Join("; ", gr.Errors.Select(e => e.Message))}");
                var one = gate?.Parts[0].Asset is { } ga ? ContentFactory.BuildAsset(ga) : null;
                var whole = ContentFactory.BuildAsset(SmMapmaker.AssetRef.Sm1(source.Map, source.Scene.Objects[tarmac].Definition));
                int Count(Node? n) => n is null ? 0 : n.GetChildren().OfType<MultiMeshInstance3D>().Sum(m => m.Multimesh?.InstanceCount ?? 0) + n.GetChildren().Sum(c => c is MultiMeshInstance3D ? 0 : Count(c));
                Check(one is not null && Count(one) > 0 && Count(one) < Count(whole), $"the part prefab draws only that part ({Count(one)} of {Count(whole)} instances)");
                one?.Free(); whole?.Free();
                MapHost.PickedSm1.Clear(); plugin.RefreshPicked();
            }

            // 3. A new map with prefab instances, no SM1 backdrop.
            plugin.NewMap("selftest instances");
            MapHost.Root.BackdropVisible = false;
            MapHost.PickedSm1.Clear(); plugin.RefreshPicked();
            var item = new LibraryItem(LibraryKind.Prefab, "selftest-piece", "Selftest piece");
            var a = plugin.PlaceAt(item, new Godot.Vector3(0, 0, 0));
            var b = plugin.PlaceAt(item, new Godot.Vector3(6.3f, 2.1f, 0));
            Check(a.Ok && b.Ok, "place two linked instances");
            // Owner's report (2026-11-05): prefabs showed only outline boxes when the map was open before its SM1 map was loaded.
            {
                static int Drawn(Node? n) => n is null ? 0 : n.GetChildren().Sum(c => (c is MultiMeshInstance3D m ? m.Multimesh?.InstanceCount ?? 0 : 0) + Drawn(c));
                int before = Drawn(plugin.NodeOf(a.Created[0]));
                MapHost.Forget(source.Map);
                plugin.SyncView(true);
                int gone = Drawn(plugin.NodeOf(a.Created[0]));
                for (int i = 0; i < 600 && MapHost.Sm1For(source.Map) is null; i++) await Frames(1);
                await Frames(5);
                int back = Drawn(plugin.NodeOf(a.Created[0]));
                Check(before > 0 && gone == 0 && back == before && MapHost.Sm1For(source.Map) is not null,
                    $"pieces of an SM1 map that is not loaded: it loads by itself and they are drawn ({before} -> {gone} -> {back})");
                source = MapHost.Sm1For(source.Map)!;
            }
            // A real click with the Place tool (the owner's path), not a direct call.
            {
                await plugin.Frame3D(new Num.Vector3(-8, -8, 0), new Num.Vector3(12, 8, 6));
                int count = plugin.Session!.Document.Objects.Count;
                MapHost.Armed = item; plugin.SetTool(Tool.Place); MapHost.Armed = item;
                if (plugin.Viewport3D?.GetParent() is Control vc)
                {
                    var at = vc.GetGlobalRect().GetCenter();
                    foreach (bool pressed in new[] { true, false })
                    {
                        Tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = at, GlobalPosition = at });
                        await Frames(2);
                    }
                }
                Check(plugin.Session.Document.Objects.Count == count + 1, $"a click with the Place tool places the armed prefab ({plugin.Session.Document.Objects.Count - count} placed; status: {plugin.Status})");
                plugin.SetTool(Tool.Select);
                var extra = plugin.Session.Document.Objects.Select(o => o.Id).Where(i => i != a.Created[0] && i != b.Created[0]).ToArray();
                if (extra.Length > 0) plugin.Do("Delete", MapmakerPlugin.Op("delete", ("ids", MapmakerPlugin.Strs(extra))));
            }
            var idB = b.Created[0];
            plugin.Select([idB]);
            await Frames(5);
            // The numeric panel path: rotate and scale the second instance.
            var t = plugin.Do("Set transform", MapmakerPlugin.Op("transform", ("id", idB), ("rotation", MapmakerPlugin.Vec(new Num.Vector3(0, 0, 45))), ("scale", MapmakerPlugin.Vec(new Num.Vector3(1.5f, 1.5f, 1.5f)))));
            Check(t.Ok, $"numeric transform {string.Join("; ", t.Errors.Select(e => e.Message))}");
            var d = plugin.DuplicateSelected();
            Check(d.Ok && d.Created.Count == 1, "duplicate");
            // The gizmo path: move the node itself, let the editor's version signal reconcile it.
            var node = plugin.NodeOf(idB)!;
            node.Position += new Godot.Vector3(1.01f, 0, 0);
            plugin.Reconcile();
            var moved = plugin.Session!.Document.Get(idB).Transform.Position;
            Check(Math.Abs(moved.X - (6.3f + 1.0f)) < 0.3f, $"gizmo move reconciled to the document (x = {moved.X}; grid snap on)");
            await plugin.Frame3D(new Num.Vector3(-8, -8, 0), new Num.Vector3(12, 8, 6));
            plugin.Dock!.ShowTab(2);
            await Frames(90);
            Shot("new-map-with-prefab-instances");

            // 4. Editor history: place, gizmo-style move, delete; undo x3, redo x3 through EditorUndoRedoManager's history.
            string json = plugin.Session.Document.ToJson();
            await UndoRedoRound(item);
            plugin.OpenMapFromJson(json);
            string savePath = System.IO.Path.Combine(MapHost.Library.MapsDir, "selftest.smmap");
            Check(plugin.SaveMap(savePath), "save .smmap");
            string file = System.IO.File.ReadAllText(savePath);
            Check(file.Contains("selftest-piece") && !file.Contains("ArrayMesh") && !file.Contains("vertices"), ".smmap holds references only");
            Check(file == plugin.Session.Document.ToJson(), "saved bytes equal canonical document JSON");
            var reopened = MapSession.Load(savePath, MapHost.Library);
            Check(reopened.Document.ToJson() == json, "reload round trip is byte identical");

            // 5. The hard rule: save the Godot scene and look for mesh data.
            var sceneRoot = EditorInterface.Singleton.GetEditedSceneRoot();
            string scenePath = "res://Maps/_selftest_saved.tscn";
            EditorInterface.Singleton.SaveSceneAs(scenePath);
            await Frames(30);
            string text = System.IO.File.Exists(ProjectSettings.GlobalizePath(scenePath)) ? System.IO.File.ReadAllText(ProjectSettings.GlobalizePath(scenePath)) : "";
            GD.Print($"SELFTEST scene file {text.Length} bytes");
            Check(text.Length > 0, "scene saved");
            bool clean = !text.Contains("ArrayMesh") && !text.Contains("MultiMesh") && !text.Contains("ImageTexture") && !text.Contains("PackedVector3Array") && !text.Contains("surface_");
            Check(clean, "saved .tscn holds no mesh, texture or surface data");
            try { System.IO.File.Delete(ProjectSettings.GlobalizePath(scenePath)); } catch (Exception) { }
        }
        catch (Exception e) { GD.PrintErr("SELFTEST exception: " + e); Failed = true; }
        GD.Print(Failed ? "SELFTEST RESULT FAIL" : "SELFTEST RESULT PASS");
        Tree.Quit(Failed ? 1 : 0);
    }
}
