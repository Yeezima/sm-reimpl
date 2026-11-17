using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmMapmaker;
using Num = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>
/// Test aid (agents, never the owner's editor): <c>godot.cmd --windowed --editor --path mapmaker -- --mm-flow --mm-out &lt;dir&gt; --mm-lib &lt;dir&gt; [--mm-play]</c> walks the whole path a new
/// user takes (docs/mapmaker.md "How to make a map in 2 minutes") through the same calls and clicks the dock makes: a new map from a layout, the check, a broken map and its Fix, placing a
/// piece with a real click, undo and redo, Seize Ground needing a point, save and reload, the Play test in both modes (with <c>--mm-play</c>), sharing. Screenshots of every step go to
/// <c>--mm-out</c>, the steps and their results to <c>flow-result.txt</c>; it prints <c>FLOW RESULT PASS|FAIL</c> and quits. Run it with <c>SM_MAPMAKER_CONTROL=&lt;scratch dir&gt;</c> so no editor file of the owner is touched.
/// </summary>
public sealed class FlowTest(MapmakerPlugin plugin)
{
    public static bool Wanted => OS.GetCmdlineUserArgs().Contains("--mm-flow");
    static string Arg(string name, string fallback) { var a = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }
    static bool Flag(string name) => OS.GetCmdlineUserArgs().Contains(name);

    SceneTree Tree => (SceneTree)Engine.GetMainLoop();
    async Task Frames(int n) { for (int i = 0; i < n; i++) await plugin.ToSignal(Tree, SceneTree.SignalName.ProcessFrame); }
    string _out = "";
    readonly List<string> _log = [];
    bool _failed;
    int _step;

    void Say(string line) { GD.Print("FLOW " + line); _log.Add(line); try { File.WriteAllLines(Path.Combine(_out, "flow-result.txt"), _log); } catch (IOException) { } }
    void Check(bool ok, string what) { Say($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) _failed = true; }

    async Task Shot(string name)
    {
        await Frames(12);
        if (!Thumbnails.CanRender) { Say($"shot {name}: headless, skipped"); return; }
        string file = $"{++_step:00}-{name}";
        Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(_out, file + ".png"));
        Say($"shot {file}.png");
    }

    /// <summary>A real left click (press and release through the window) on a point of the map, as the user's mouse would.</summary>
    async Task ClickAtWorld(Num.Vector3 game)
    {
        var vp = plugin.Viewport3D;
        if (vp?.GetParent() is not Control container || vp.GetCamera3D() is not { } cam) { Check(false, "click: no editor viewport"); return; }
        var local = cam.UnprojectPosition(Frame.ToGodot(new Godot.Vector3(game.X, game.Y, game.Z)));
        var at = container.GetGlobalRect().Position + local / new Godot.Vector2(vp.Size.X, vp.Size.Y) * container.GetGlobalRect().Size;
        foreach (bool pressed in new[] { true, false })
        {
            Tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = at, GlobalPosition = at });
            await Frames(3);
        }
        await Frames(6);
    }

    string Doc() => plugin.Session!.Document.ToJson();

    // ---- viewport mouse helpers (window coordinates, pushed through the window as the user's mouse would) -------------------------------------------------

    Control View => (Control)plugin.Viewport3D!.GetParent();
    Godot.Vector2 Win(Godot.Vector3 godot)
    {
        var vp = plugin.Viewport3D!; var rect = View.GetGlobalRect();
        return rect.Position + vp.GetCamera3D()!.UnprojectPosition(godot) / new Godot.Vector2(vp.Size.X, vp.Size.Y) * rect.Size;
    }
    Godot.Vector2 WinGame(float x, float y, float z) => Win(Frame.ToGodot(new Godot.Vector3(x, y, z)));
    void Move(Godot.Vector2 at, bool left = false) =>
        Tree.Root.PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at, ButtonMask = left ? MouseButtonMask.Left : 0 });
    void Button(Godot.Vector2 at, MouseButton b, bool pressed, bool shift = false) =>
        Tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = b, Pressed = pressed, Position = at, GlobalPosition = at, ShiftPressed = shift, ButtonMask = pressed && b == MouseButton.Left ? MouseButtonMask.Left : 0 });
    async Task Click(Godot.Vector2 at) { Move(at); await Frames(3); Button(at, MouseButton.Left, true); await Frames(3); Button(at, MouseButton.Left, false); await Frames(8); }
    void Wheel(Godot.Vector2 at, bool up, bool shift = false) { foreach (bool pressed in new[] { true, false }) Button(at, up ? MouseButton.WheelUp : MouseButton.WheelDown, pressed, shift); }
    Godot.Vector2 CentreOf(MapmakerObject n) => Win(n.GlobalTransform * n.LocalBounds.GetCenter());

    /// <summary>A screenshot cropped to the 3D view.</summary>
    async Task ShotView(string name)
    {
        await Frames(12);
        if (!Thumbnails.CanRender) { Say($"shot {name}: headless, skipped"); return; }
        var r = View.GetGlobalRect();
        var img = Tree.Root.GetTexture().GetImage().GetRegion(new Rect2I((Vector2I)r.Position, (Vector2I)r.Size));
        string file = $"{++_step:00}-{name}.png";
        img.SavePng(Path.Combine(_out, file));
        Say($"shot {file}");
    }

    /// <summary>Owner's decisions 2026-11-18: a left drag from anywhere box-selects (also from over an object); an armed item shows a ghost that wheel turns and scales, LMB stamps, Esc ends.</summary>
    async Task BoxAndGhost(MapmakerDock dock)
    {
        // 5b. Box select starting over an object.
        MapHost.Tool = Tool.Select; MapHost.Armed = null;
        var sel = EditorInterface.Singleton.GetSelection(); sel.Clear();
        await Frames(10);
        var rect = View.GetGlobalRect(); var cam = plugin.Viewport3D!.GetCamera3D()!;
        var inner = rect.GrowIndividual(-60, -60, -60, -60);
        var boxes = plugin.Session!.Document.Objects.Where(o => o.Piece is { Kind: PieceKind.Box }).Select(o => plugin.NodeOf(o.Id)).OfType<MapmakerObject>()
            .Where(n => !cam.IsPositionBehind(n.GlobalTransform * n.LocalBounds.GetCenter()) && inner.HasPoint(CentreOf(n))).ToList();
        var a = boxes.OrderBy(n => CentreOf(n).DistanceTo(rect.GetCenter())).FirstOrDefault();
        var b = a is null ? null : boxes.Where(n => n != a && CentreOf(n).DistanceTo(CentreOf(a)) > 12f).OrderBy(n => CentreOf(n).DistanceTo(CentreOf(a))).FirstOrDefault();
        Check(a is not null && b is not null, "two solid blocks are in view for the box select");
        if (a is null || b is null) return;
        var from = CentreOf(a); var to = CentreOf(b) + new Godot.Vector2(MathF.Sign(CentreOf(b).X - from.X) * 14f, 80f);   // a visible rectangle, from over block a to past block b
        Move(from); await Frames(4);
        Button(from, MouseButton.Left, true); await Frames(3);
        for (int i = 1; i <= 6; i++) { Move(from.Lerp(to, i / 6f), true); await Frames(2); }
        Check(plugin.BoxDrawing, "a left drag that starts over a placed block draws the selection rectangle (the body is not dragged)");
        await ShotView("box-select");
        Button(to, MouseButton.Left, false); await Frames(10);
        var picked = plugin.SelectedIds().ToList();
        Check(picked.Count >= 2 && picked.Contains(a.ObjectId) && picked.Contains(b.ObjectId), $"the box selected {picked.Count} objects, both blocks among them");
        Check(!plugin.BoxDrawing, "the rectangle is gone after the release");
        // Ctrl removes: a drag over b only, from outside it.
        var one = CentreOf(b); var p0 = one + (one - from).Normalized() * 70f; var p1 = one;   // away from a, and clear of the gizmo disc about the selection's centre
        Move(p0); await Frames(3);
        Tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = p0, CtrlPressed = true, ButtonMask = MouseButtonMask.Left }); await Frames(3);
        for (int i = 1; i <= 4; i++) { Move(p0.Lerp(p1, i / 4f), true); await Frames(2); }
        Tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = p1, CtrlPressed = true }); await Frames(10);
        Check(!plugin.SelectedIds().Contains(b.ObjectId), "Ctrl + box removes from the selection");
        // A plain click still selects one object.
        sel.Clear(); await Frames(5);
        await Click(CentreOf(a)); await Frames(10);
        Check(plugin.SelectedIds().Count() == 1, $"a plain click still selects one object ({plugin.SelectedIds().Count()})");
        sel.Clear(); await Frames(5);

        // 5c. The placement ghost.
        int before = plugin.Session!.Document.Objects.Count;
        dock.ShowTab(DockTab.Build);
        dock.TestArmPreset("cover");
        var at = WinGame(8, 8, 0);
        Move(at); await Frames(10);
        var ghost = plugin.GhostNode;
        Check(ghost is { Visible: true } && ghost.Owner is null && MapHost.Root!.GetChildren().Contains(ghost), "an armed block shows a ghost under the cursor, with no owner (never saved)");
        var camAt = cam.GlobalPosition;
        Wheel(at, true); Wheel(at, true); await Frames(8);
        Check(plugin.GhostYaw == 30f, $"two wheel steps turn the ghost 30 degrees (snap 15): {plugin.GhostYaw}");
        Wheel(at, true, shift: true); await Frames(8);
        Check(Mathf.IsEqualApprox(plugin.GhostScale, 1.1f), $"Shift + wheel scales it by 10 %: {plugin.GhostScale}");
        Check(cam.GlobalPosition.DistanceTo(camAt) < 1e-3f, "the wheel did not zoom the camera while armed");
        await ShotView("ghost-before-placement");
        var ghostPos = Frame.Components(plugin.GhostNode!.Transform).Position;
        await Click(at);
        Check(plugin.Session!.Document.Objects.Count == before + 1, "LMB placed a copy at the ghost");
        var first = plugin.Session.Document.Objects.Last();
        Check(MathF.Abs(first.Transform.Rotation.Z - 30f) < 0.01f && (first.Transform.Scale - new Num.Vector3(3.3f, 3.3f, 1.65f)).Length() < 0.01f && (first.Transform.Position - new Num.Vector3(ghostPos.X, ghostPos.Y, ghostPos.Z)).Length() < 0.01f,
            $"the copy has the ghost's exact transform (rotation {first.Transform.Rotation}, scale {first.Transform.Scale}, position {first.Transform.Position} vs ghost {ghostPos})");
        Check(MapHost.Armed is not null && plugin.GhostNode is not null && plugin.GhostYaw == 30f, "the item stays armed and the ghost keeps its turn");
        await Click(WinGame(-8, 8, 0));
        Check(plugin.Session.Document.Objects.Count == before + 2 && MathF.Abs(plugin.Session.Document.Objects.Last().Transform.Rotation.Z - 30f) < 0.01f, "a second click placed a second copy with the same turn");
        Move(new Godot.Vector2(2, 2)); await Frames(8);
        Check(plugin.GhostNode is { Visible: false }, "the ghost hides when the mouse leaves the 3D view");
        Move(at); await Frames(10);
        Check(plugin.GhostNode is { Visible: true }, "and comes back when the mouse returns");
        Tree.Root.PushInput(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true }); await Frames(6);
        Check(MapHost.Armed is null && MapHost.Tool == Tool.Select && plugin.GhostNode is null, "Esc removes the ghost and disarms");
        await Frames(4);
    }

    /// <summary>Owner, 2026-11-18: the map editor works in any scene tab. Prefab extracted from an SM1 map in tab 1, placed in tab 2.</summary>
    async Task TwoTabs(MapmakerDock dock)
    {
        string tab2 = "res://Maps/FlowTab2.tscn";
        Say("two tabs: start");
        Sm1Source? source = null;
        try { source = await Task.Run(() => Sm1Source.Load("mp_tdm_09")); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { Say("two tabs: SM1 map not available here (" + e.Message + "), skipped"); return; }
        var root1 = MapHost.Root!;
        MapHost.Root!.ShowBackdrop(source, []);
        MapHost.Register(source);
        var core = source.BoundsAll()!.Value; var mid = (core.Min + core.Max) / 2;
        var near = Enumerable.Range(0, source.Scene.Objects.Count).Where(i => !source.Hidden.Contains(i) && source.Scene.Objects[i].Name.Length > 0 && source.WorldBounds(i) is not null)
            .OrderBy(i => (source.Scene.Objects[i].Transform.Translation - mid).Length()).Take(3).ToList();
        MapHost.PickedSm1.Clear(); MapHost.PickedSm1.AddRange(near.Select(i => new PickRef(i))); plugin.RefreshPicked();
        var r = dock.Extract("Flow piece", "flow-piece", "bounds-bottom-center", "mesh");
        Check(r.Ok && MapHost.Library.GetPrefab("flow-piece") is not null, "extracted a prefab from the SM1 map in tab 1");
        MapHost.PickedSm1.Clear(); plugin.RefreshPicked();
        string doc1 = Doc(); int count1 = plugin.Session!.Document.Objects.Count;
        // Tab 2: a plain scene.
        var packed = new PackedScene(); var plain = new Node3D { Name = "Plain" };
        packed.Pack(plain); plain.Free();
        Check(ResourceSaver.Save(packed, tab2) == Error.Ok, "made a plain second scene");
        EditorInterface.Singleton.OpenSceneFromPath(tab2);
        for (int i = 0; i < 120 && EditorInterface.Singleton.GetEditedSceneRoot()?.SceneFilePath != tab2; i++) await Frames(3);
        await Frames(20);
        Check(EditorInterface.Singleton.GetEditedSceneRoot()?.SceneFilePath == tab2 && !plugin.EditedHasMap, "tab 2 is a plain scene without a Mapmaker map");
        MapHost.Armed = new LibraryItem(LibraryKind.Prefab, "flow-piece", "Flow piece"); MapHost.Tool = Tool.Place;
        Check(!plugin.ReadyToPlace() && EditorInterface.Singleton.GetEditedSceneRoot()?.SceneFilePath == tab2, "placing there asks to add a map and does not jump back to tab 1");
        await Frames(10);
        foreach (var d in dock.GetChildren().OfType<ConfirmationDialog>().Where(d => d.Title == "Not a Mapmaker map" && d.Visible)) { Check(d.DialogText.Contains("Add one here"), "the dialog offers to add a Mapmaker map to this tab"); d.Hide(); }
        Check(await plugin.AddMapToCurrentScene() && plugin.EditedHasMap && plugin.Session is { } s2 && s2.Document.Objects.Count == 0 && Doc() != doc1, "Add a Mapmaker map here: tab 2 has its own empty map");
        MapHost.Armed = new LibraryItem(LibraryKind.Prefab, "flow-piece", "Flow piece"); MapHost.Tool = Tool.Place;
        await Frames(20);
        Move(WinGame(0, 0, 0)); await Frames(10);
        Check(plugin.GhostNode is not null && plugin.GhostNode.GetParent() == MapHost.Root, "the armed ghost was rebuilt in tab 2's scene");
        await Click(WinGame(0, 0, 0));
        Check(plugin.Session!.Document.Objects.Count == 1 && plugin.Session.Document.Objects[0].Prefab == "flow-piece", "the prefab was placed in tab 2");
        string doc2 = Doc();
        await ShotView("placed-in-tab-2");
        MapHost.Armed = null; MapHost.Tool = Tool.Select; await Frames(5);
        // Back to tab 1: untouched, still holding the SM1 map.
        EditorInterface.Singleton.OpenSceneFromPath("res://Maps/Empty.tscn");
        for (int i = 0; i < 120 && MapHost.Root != root1; i++) await Frames(3);
        await Frames(20);
        Check(MapHost.Root == root1 && Doc() == doc1 && plugin.Session!.Document.Objects.Count == count1 && MapHost.Sm1For("mp_tdm_09") is not null, "tab 1 is untouched and still has its map and the SM1 map");
        EditorInterface.Singleton.OpenSceneFromPath(tab2);
        for (int i = 0; i < 120 && EditorInterface.Singleton.GetEditedSceneRoot()?.SceneFilePath != tab2; i++) await Frames(3);
        await Frames(20);
        Check(plugin.Session is not null && Doc() == doc2, "tab 2 kept its own map when switching back");
        EditorInterface.Singleton.OpenSceneFromPath("res://Maps/Empty.tscn");
        await Frames(30);
        try { DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(tab2)); } catch (Exception) { }
    }

    public async Task Run()
    {
        try
        {
            _out = Arg("--mm-out", Path.Combine(Path.GetTempPath(), "mm-flow-" + System.Environment.ProcessId));
            Directory.CreateDirectory(_out);
            Say("flow test started");
            await Frames(30);
            for (int wait = 0; MapHost.Root is null; wait++) { if (wait % 5 == 0) Say($"waiting for the Mapmaker scene ({wait})"); plugin.EnsureSceneRoot(); await Frames(20); }
            string lib = Arg("--mm-lib", Path.Combine(Path.GetTempPath(), "mm-flow-lib-" + System.Environment.ProcessId));
            MapHost.Library = new MapLibrary(lib);
            Say($"library {lib}");
            var dock = plugin.Dock!;
            dock.ShowTab(DockTab.Start);
            await Frames(30);
            await Shot("start-no-map");

            // 1. A new map from a layout: one press.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await dock.TestNewFromLayout("Flow Arena", "arena");
            await Frames(60);
            Check(plugin.Session is { } s && s.Document.Objects.Count > 20, $"New map from the arena layout made {plugin.Session?.Document.Objects.Count} pieces in {watch.ElapsedMilliseconds} ms");
            Check(plugin.Session?.Path is { } p && File.Exists(p), $"the new map was saved at once ({plugin.Session?.Path})");
            await Frames(240);
            await Shot("new-from-layout");

            // 2. The check: ready for both modes.
            plugin.RunCheck(true);
            for (int i = 0; i < 400 && plugin.RouteChecking; i++) await Frames(3);
            Check(plugin.LastCheck is { AnnihilationReady: true, SeizeReady: true }, $"the check says: {plugin.LastCheck?.Headline}");
            Check(plugin.LastCheck!.Facts.Any(f => f.Contains("Bot route: both sides")), "the bots' route on the real graph: both sides reach each other and every point");
            await Shot("check-ready");

            // 3. Break it (delete the Chaos spawn area), read the plain words, press Fix.
            var chaos = plugin.Session!.Document.Objects.First(o => o.Piece is { Kind: PieceKind.SpawnArea, Side: PieceSpec.Chaos });
            plugin.Do("Delete Chaos spawn", MapmakerPlugin.Op("delete", ("ids", MapmakerPlugin.Strs([chaos.Id]))));
            plugin.RunCheck(false);
            await Frames(20);
            var broken = plugin.LastCheck!;
            Check(!broken.AnnihilationReady && broken.Issues.Any(i => i.Code == "no-spawn-chaos" && i.Action == "add-spawn-chaos"), $"deleting a spawn area is caught in plain words: \"{broken.Issues.FirstOrDefault(i => i.Code == "no-spawn-chaos")?.Message}\"");
            await Shot("check-broken");
            plugin.Fix(broken.Issues.First(i => i.Code == "no-spawn-chaos"));
            plugin.RunCheck(false);
            await Frames(20);
            Check(plugin.LastCheck!.AnnihilationReady, "the Fix button put a Chaos spawn area back and the map is ready again");
            await Shot("check-fixed");

            // 4. A piece placed with a real click on the Build tab's button and the 3D view.
            int before = plugin.Session!.Document.Objects.Count;
            dock.ShowTab(DockTab.Build);
            dock.TestArmPreset("cover");
            Check(MapHost.Tool == Tool.Place && MapHost.Armed is { Kind: LibraryKind.Piece }, "the Build tab's Cover block button arms the place tool");
            await Frames(10);
            await ClickAtWorld(new Num.Vector3(-8, -8, 0));
            Check(plugin.Session!.Document.Objects.Count == before + 1, $"a real click on the 3D view placed a cover block ({before} -> {plugin.Session.Document.Objects.Count})");
            var placed = plugin.Session.Document.Objects.Last();
            Check(placed.Piece is { Kind: PieceKind.Box } && placed.Transform.Scale == new Num.Vector3(3, 3, 1.5f), $"it has the default size {placed.Transform.Scale} at {placed.Transform.Position}");
            await Shot("placed-cover");

            // 5. Undo and redo.
            var ur = plugin.History;
            var history = ur.GetHistoryUndoRedo(ur.GetObjectHistoryId(MapHost.Root!));
            string afterPlace = Doc();
            history.Undo(); await Frames(8);
            Check(plugin.Session!.Document.Objects.Count == before, "Ctrl+Z took the block away again");
            history.Redo(); await Frames(8);
            Check(Doc() == afterPlace, "redo brought it back, byte for byte");
            MapHost.Tool = Tool.Select; MapHost.Armed = null;

            await BoxAndGhost(dock);

            // 6. Seize Ground needs a capture point; Annihilation does not.
            var points = plugin.Session!.Document.Objects.Where(o => o.Piece is { Kind: PieceKind.CapturePoint }).Select(o => o.Id).ToList();
            plugin.Do("Delete the points", MapmakerPlugin.Op("delete", ("ids", MapmakerPlugin.Strs(points))));
            plugin.RunCheck(false);
            await Frames(20);
            Check(plugin.LastCheck is { AnnihilationReady: true, SeizeReady: false }, $"with no capture points: Annihilation is ready, Seize Ground is not (\"{plugin.LastCheck?.Headline}\")");
            await Shot("seize-needs-a-point");
            plugin.Fix(plugin.LastCheck!.Issues.First(i => i.Code == "no-points"));
            plugin.RunCheck(false);
            Check(plugin.LastCheck!.SeizeReady, "the Fix button added a capture point and Seize Ground is ready");

            // 7. Save and reload: the same bytes.
            string file = plugin.Session.Path!;
            Check(plugin.SaveMap(), "save");
            string saved = File.ReadAllText(file);
            plugin.OpenMap(file);
            await Frames(30);
            Check(Doc() == saved, "reopening the file gives the same document");
            Check(plugin.Compiled()!.Hash == CustomMap.HashOf(MapDocument.Parse(saved)), $"the compiled map's hash is the file's hash ({plugin.Compiled()!.Hash:X16})");

            // 8. Play test, both modes (the real thing: server on 127.0.0.1, bots, an observer on the test desktop).
            if (Flag("--mm-play"))
            {
                foreach (var mode in new[] { "annihilation", "seize" })
                {
                    string shots = Path.Combine(_out, "ingame-" + mode);
                    var w = System.Diagnostics.Stopwatch.StartNew();
                    string? err = dock.TestPlay(mode, 2, "marines", ["-Test", "-OutDir", shots, "-Seconds", "50"]);
                    Check(err is null, $"Play test ({mode}) started" + (err is null ? "" : ": " + err));
                    if (err is not null) continue;
                    await Shot("play-test-started-" + mode);
                    for (int i = 0; i < 6000 && plugin.PlayRunning; i++) await Frames(10);
                    int stills = Directory.Exists(shots) ? Directory.GetFiles(shots, "*.png").Length : 0;
                    Check(!plugin.PlayRunning && stills > 0, $"Play test ({mode}) finished after {w.Elapsed.TotalSeconds:0} s with {stills} in-game stills; status: {plugin.PlayStatus}");
                }
            }
            else Say("play tests skipped (pass --mm-play)");

            // 10. Any scene tab (owner, 2026-11-18): extract a prefab from an SM1 map in tab 1, place it in a scene of tab 2; tab 1 is untouched.
            await TwoTabs(dock);

            // 9. Share: the file is the whole map.
            string shared = plugin.ShareFile()!;
            string other = Path.Combine(_out, "shared");
            Directory.CreateDirectory(other);
            File.Copy(shared, Path.Combine(other, Path.GetFileName(shared)), true);
            Check(CustomMaps.Load(Path.Combine(other, Path.GetFileName(shared))).Hash == plugin.Compiled()!.Hash, "a copy of the file elsewhere compiles to the same hash (shareable as one file)");
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            Check(false, "exception: " + e);
        }
        Say(_failed ? "FLOW RESULT FAIL" : "FLOW RESULT PASS");
        Tree.Quit(_failed ? 1 : 0);
    }
}


