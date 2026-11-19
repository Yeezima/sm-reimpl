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
        var one = CentreOf(b); var dirAway = (one - from).Normalized(); var p0 = one + dirAway * 70f; var p1 = one;
        for (int k = 0; k < 20 && plugin.IsOnGizmo(p0); k++) p0 += dirAway * 20f;   // clear of the selection's gizmo handles
        Move(p0); await Frames(3);
        Tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = p0, CtrlPressed = true, ButtonMask = MouseButtonMask.Left }); await Frames(3);
        for (int i = 1; i <= 4; i++) { Move(p0.Lerp(p1, i / 4f), true); await Frames(2); }
        Tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = p1, CtrlPressed = true }); await Frames(10);
        Check(!plugin.SelectedIds().Contains(b.ObjectId), "Ctrl + box removes from the selection");
        // A plain click still selects one object.
        sel.Clear(); await Frames(5);
        await Click(CentreOf(a)); await Frames(10);
        Check(plugin.SelectedIds().Count() == 1, $"a plain click still selects one object ({plugin.SelectedIds().Count()})");
        // Alt held (owner 2026-11-19): a press over an object, even on the selection's gizmo, never single-selects; a drag boxes.
        var ca = CentreOf(a); var cb = CentreOf(b);
        Move(cb); await Frames(3);
        Tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = cb, AltPressed = true, ButtonMask = MouseButtonMask.Left }); await Frames(3);
        Tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = cb, AltPressed = true }); await Frames(10);
        Check(plugin.SelectedIds().Count() == 1 && !plugin.SelectedIds().Contains(b.ObjectId), "Alt + click over another object does not select it");
        var altTo = ca + new Godot.Vector2(MathF.Sign(cb.X - ca.X) * 20f, 40f);
        Move(ca); await Frames(3);
        Tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = ca, AltPressed = true, ButtonMask = MouseButtonMask.Left }); await Frames(3);
        for (int i = 1; i <= 4; i++) { Move(ca.Lerp(altTo, i / 4f), true); await Frames(2); }
        Check(plugin.BoxDrawing, "Alt + drag from over the selected object (on its gizmo) draws a box");
        await ShotView("alt-box-select");
        Tree.Root.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = altTo, AltPressed = true }); await Frames(10);
        Check(!plugin.BoxDrawing && plugin.SelectedIds().Contains(a.ObjectId), "the Alt box ends on release and selects what it covers");
        // Spin arrows (owner 2026-11-19): a right click steps by 10, never jumps to the limit; a held arrow starts slowly.
        dock.ShowTab(DockTab.Object); await Frames(10);
        var spin = dock.FindChildren("*", "SpinBox", true, false).OfType<SpinBox>().FirstOrDefault(s => s.IsVisibleInTree() && s.GetNodeOrNull("SpinArrows") is not null && s.MinValue == -100000);
        Check(spin is not null, "the Object tab's position spin boxes are in view");
        if (spin is not null)
        {
            var r = spin.GetGlobalRect(); var up = new Godot.Vector2(r.End.X - 5, r.Position.Y + 3); var down = new Godot.Vector2(r.End.X - 5, r.End.Y - 3);
            double v0 = spin.Value;
            r = spin.GetGlobalRect(); up = new Godot.Vector2(r.End.X - 5, r.Position.Y + 3); Move(up); await Frames(3);
            Button(up, MouseButton.Right, true); await Frames(2); Button(up, MouseButton.Right, false); await Frames(4);
            Check(Math.Abs(spin.Value - (v0 + 10)) < 1e-6, $"a right click on the up arrow adds 10 ({v0} -> {spin.Value})");
            r = spin.GetGlobalRect(); down = new Godot.Vector2(r.End.X - 5, r.End.Y - 3); Move(down); await Frames(3);
            Button(down, MouseButton.Right, true); await Frames(2); Button(down, MouseButton.Right, false); await Frames(4);
            Check(Math.Abs(spin.Value - v0) < 1e-6, $"a right click on the down arrow subtracts 10 ({spin.Value})");
            r = spin.GetGlobalRect(); up = new Godot.Vector2(r.End.X - 5, r.Position.Y + 3); Move(up); await Frames(3);
            Button(up, MouseButton.Left, true); await Frames(3); Button(up, MouseButton.Left, false); await Frames(4);
            Check(Math.Abs(spin.Value - (v0 + spin.Step)) < 1e-6, $"a left click on the up arrow adds one step ({spin.Value})");
            spin.Value = v0;
        }
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
    /// <summary>Scene maps stay apart: two scenes with the same file name in different folders, and Save As, never share or overwrite a map; object nodes saved in a scene are reported when a map replaces them.</summary>
    async Task SceneIsolation(MapmakerDock dock)
    {
        string dirA = "res://Maps/FlowDirA", dirB = "res://Maps/FlowDirB";
        foreach (var d in new[] { dirA, dirB }) DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(d));
        string sa = dirA + "/Same.tscn", sb = dirB + "/Same.tscn", copy = dirB + "/Copy.tscn";
        foreach (var path in new[] { sa, sb })
        {
            var packed = new PackedScene(); var plain = new Node3D { Name = "Same" }; packed.Pack(plain); plain.Free();
            ResourceSaver.Save(packed, path);
        }
        var cover = new LibraryItem(LibraryKind.Piece, "cover", "Cover block");
        async Task Fill(string path, int copies)
        {
            EditorInterface.Singleton.OpenSceneFromPath(path);
            for (int i = 0; i < 120 && EditorInterface.Singleton.GetEditedSceneRoot()?.SceneFilePath != path; i++) await Frames(3);
            await Frames(15);
            await plugin.AddMapToCurrentScene();
            for (int i = 0; i < copies; i++) plugin.PlaceAt(cover, new Godot.Vector3(6 * i, 0, 0));
            EditorInterface.Singleton.SaveScene(); await Frames(20);
        }
        await Fill(sa, 1);
        await Fill(sb, 2);
        string? ma = plugin.SceneMapOfFile(sa), mb = plugin.SceneMapOfFile(sb);
        Check(ma is not null && mb is not null && ma != mb, $"two scenes called Same.tscn in different folders have different map files ({(ma is null ? "-" : Path.GetFileName(ma))}, {(mb is null ? "-" : Path.GetFileName(mb))})");
        Check(ma is not null && mb is not null && MapDocument.Parse(File.ReadAllText(ma)).Objects.Count == 1 && MapDocument.Parse(File.ReadAllText(mb)).Objects.Count == 2, "each map file holds its own scene's objects (1 and 2)");
        // Save As: the new scene gets its own copy.
        EditorInterface.Singleton.SaveSceneAs(copy, false);
        for (int i = 0; i < 150 && plugin.SceneMapOfFile(copy) is null; i++) await Frames(2);
        string? mc = plugin.SceneMapOfFile(copy);
        Check(mc is not null && mc != mb && File.Exists(mb!) && MapDocument.Parse(File.ReadAllText(mc!)).Objects.Count == 2 && plugin.SceneMapOfFile(sb) == mb,
            $"Save As gives the new scene its own copy of the map and leaves the original's alone ({(mc is null ? "-" : Path.GetFileName(mc))} vs {(mb is null ? "-" : Path.GetFileName(mb))})");
        // An explicit open of a map another scene uses keeps the chosen file (no silent copy), and says so.
        int filesBefore = Directory.GetFiles(MapHost.Library.MapsDir, "*.smmap").Length;
        plugin.OpenMap(ma!);
        bool saidShared = plugin.Status.Contains("also used by");
        await Frames(140);
        Check(saidShared && plugin.Session?.Path is { } opened && Path.GetFullPath(opened) == Path.GetFullPath(ma!) && Directory.GetFiles(MapHost.Library.MapsDir, "*.smmap").Length == filesBefore,
            $"opening a map another scene uses keeps that file, says it is shared, and makes no copy (status: \"{plugin.Status}\")");
        // An entry written for another scene (uid mismatch, as at a reused path) is not used; entries of scene files that are gone are dropped on the next write.
        string sidecar = Path.Combine(MapHost.Library.MapsDir, "scene-maps.json");
        string original = File.ReadAllText(sidecar);
        var tampered = System.Text.Json.Nodes.JsonNode.Parse(original)!.AsObject();
        tampered[sa]!["uid"] = "uid://notthisscene";
        tampered["res://Maps/FlowDirA/Gone.tscn"] = new System.Text.Json.Nodes.JsonObject { ["map"] = ma, ["uid"] = "" };
        File.WriteAllText(sidecar, tampered.ToJsonString());
        Check(plugin.SceneMapOfFile(sa) is null, "an entry whose scene uid differs (a new scene at a reused path) is not used");
        File.WriteAllText(sidecar, tampered.ToJsonString().Replace("uid://notthisscene", System.Text.Json.Nodes.JsonNode.Parse(original)![sa]!["uid"]!.GetValue<string>()));
        plugin.PlaceAt(new LibraryItem(LibraryKind.Piece, "cover", "Cover block"), new Godot.Vector3(40, 0, 0));
        EditorInterface.Singleton.SaveScene(); await Frames(20);
        Check(!File.ReadAllText(sidecar).Contains("Gone.tscn") && File.ReadAllText(sidecar).Contains("Same.tscn"), "the next write drops entries of scene files that are gone and keeps the others (the file is re-read, not overwritten from memory)");
        // Object nodes saved in a scene are reported, not silently deleted.
        if (MapHost.Root is { } root)
        {
            root.AddChild(new MapmakerObject { Name = "Stray", ObjectId = "stray" });
            plugin.NewMap("Replaced"); await Frames(5);
            Check(plugin.Status.Contains("replaced"), $"saved object nodes replaced by a map are reported: \"{plugin.Status}\"");
        }
        EditorInterface.Singleton.OpenSceneFromPath("res://Maps/Empty.tscn");
        await Frames(140);
        Check(plugin.StashCount == EditorInterface.Singleton.GetOpenScenes().Length - 1, $"stashed maps are exactly those of the other open tabs ({plugin.StashCount} held, {EditorInterface.Singleton.GetOpenScenes().Length} tabs open)");
        foreach (var f in new[] { sa, sb, copy }) { try { DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(f)); } catch (Exception) { } }
        foreach (var d in new[] { dirA, dirB }) { try { DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(d)); } catch (Exception) { } }
    }

    ulong _tab2Root;

    /// <summary>Measures how far the real move gizmo reaches from the selected object's origin (pixels that appear when W is pressed), against the plugin's bypass radius.</summary>
    async Task MeasureGizmo()
    {
        var ids = plugin.SelectedIds().ToList();
        if (ids.Count != 1 || plugin.NodeOf(ids[0]) is not { } node) { Say("gizmo: nothing selected, skipped"); return; }
        if (!Thumbnails.CanRender) { Say("gizmo: headless, skipped"); return; }
        var rect = View.GetGlobalRect(); var centre = Win(node.GlobalPosition) - rect.Position;
        Move(Win(node.GlobalPosition) + new Godot.Vector2(300, 300)); await Frames(4);
        Image Grab() { var r = View.GetGlobalRect(); return Tree.Root.GetTexture().GetImage().GetRegion(new Rect2I((Vector2I)r.Position, (Vector2I)r.Size)); }
        foreach (bool pressed in new[] { true, false }) Tree.Root.PushInput(new InputEventKey { Keycode = Key.W, PhysicalKeycode = Key.W, Pressed = pressed });
        await Frames(15);
        var with = Grab();
        EditorInterface.Singleton.GetSelection().Clear(); await Frames(15);
        var without = Grab();
        plugin.Select(ids); await Frames(5);
        with.SavePng(System.IO.Path.Combine(_out, "gizmo-with.png")); without.SavePng(System.IO.Path.Combine(_out, "gizmo-without.png"));
        float far = 0; int n = 0; var radii = new List<float>(); var gizmoPx = new List<Godot.Vector2>();
        for (int y = 0; y < with.GetHeight(); y++)
            for (int x = 0; x < with.GetWidth(); x++)
            {
                var a = with.GetPixel(x, y); var b = without.GetPixel(x, y);
                if (MathF.Abs(a.R - b.R) + MathF.Abs(a.G - b.G) + MathF.Abs(a.B - b.B) < 0.25f) continue;
                static float Near(Color c, float r, float g, float bl) => MathF.Abs(c.R - r) + MathF.Abs(c.G - g) + MathF.Abs(c.B - bl);
                if (Near(a, 0.96f, 0.21f, 0.32f) > 0.3f && Near(a, 0.53f, 0.84f, 0.01f) > 0.3f && Near(a, 0.16f, 0.55f, 0.96f) > 0.3f) continue;   // only the gizmo's red, green and blue (not the selection box's orange outline)
                if (new Godot.Vector2(x, y).DistanceTo(centre) > 320f) continue;   // the selection box of a large object runs far; the gizmo itself stays near
                gizmoPx.Add(new Godot.Vector2(x, y) + rect.Position); n++; float dd = new Godot.Vector2(x, y).DistanceTo(centre); radii.Add(dd); far = MathF.Max(far, dd);
            }
        radii.Sort(); Say($"gizmo radii: p50 {radii[radii.Count / 2]:0} p90 {radii[radii.Count * 9 / 10]:0} p99 {radii[radii.Count * 99 / 100]:0} centre {centre}");
        Say($"gizmo: {n} changed pixels, farthest {far:0} px from the object's origin; editor scale {EditorInterface.Singleton.GetEditorScale()}, manipulator size setting {EditorInterface.Singleton.GetEditorSettings().Get("editors/3d/manipulator_gizmo_size")}");
        Check(n > 0, "the move gizmo appeared with W (measurement valid)");
        int onHandle = gizmoPx.Count(g => plugin.IsOnGizmo(g));
        Check(n > 0 && onHandle >= n * 0.95f, $"the hit test covers the real gizmo: {onHandle} of {n} gizmo pixels go to Godot");
        var tipPx = gizmoPx.OrderByDescending(g => g.DistanceTo(centre + rect.Position)).First();
        Check(plugin.IsOnGizmo(tipPx), $"the farthest arrow tip pixel ({tipPx.DistanceTo(centre + rect.Position):0} px out) goes to Godot");
        // A press 60 px from the centre between the handles starts a box instead.
        var origin = centre + rect.Position; Godot.Vector2? free = null;
        float freeAt = 0;
        for (float rad = 50f; rad <= 110f && free is null; rad += 5f)
            for (int deg = 0; deg < 360 && free is null; deg += 5)
            {
                var q = origin + new Godot.Vector2(MathF.Cos(deg * Mathf.Pi / 180f), MathF.Sin(deg * Mathf.Pi / 180f)) * rad;
                if (!plugin.IsOnGizmo(q) && !gizmoPx.Any(g => g.DistanceTo(q) < 10f)) { free = q; freeAt = rad; }
            }
        Check(free is not null, $"there is a spot between the handles, {freeAt:0} px from the centre (the real gizmo's rings and squares leave little room closer in)");
        if (free is { } f0)
        {
            Move(f0); await Frames(3);
            Button(f0, MouseButton.Left, true); await Frames(3);
            for (int i = 1; i <= 4; i++) { Move(f0 + new Godot.Vector2(14, 14) * i, true); await Frames(2); }
            Check(plugin.BoxDrawing, "a press between the handles starts a box");
            Button(f0 + new Godot.Vector2(56, 56), MouseButton.Left, false); await Frames(8);
            plugin.Select(ids); await Frames(5);
        }
        foreach (var k in new[] { Key.Q, Key.E, Key.R }) await QModeProbe(ids, centre, k);
    }

    /// <summary>Does Godot draw gizmo handles in Select mode (Q)? Counts gizmo-coloured pixels that appear with the selection.</summary>
    async Task QModeProbe(List<string> ids, Godot.Vector2 centre, Key key)
    {
        foreach (bool pressed in new[] { true, false }) Tree.Root.PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = pressed });
        await Frames(15);
        Image Grab() { var r = View.GetGlobalRect(); return Tree.Root.GetTexture().GetImage().GetRegion(new Rect2I((Vector2I)r.Position, (Vector2I)r.Size)); }
        plugin.Select(ids); await Frames(10);
        var with = Grab();
        EditorInterface.Singleton.GetSelection().Clear(); await Frames(15);
        var without = Grab();
        plugin.Select(ids); await Frames(5);
        static float Near(Color c, float r, float g, float bl) => MathF.Abs(c.R - r) + MathF.Abs(c.G - g) + MathF.Abs(c.B - bl);
        int n = 0;
        for (int y = 0; y < with.GetHeight(); y++)
            for (int x = 0; x < with.GetWidth(); x++)
            {
                var a = with.GetPixel(x, y); var b = without.GetPixel(x, y);
                if (MathF.Abs(a.R - b.R) + MathF.Abs(a.G - b.G) + MathF.Abs(a.B - b.B) < 0.25f) continue;
                if (Near(a, 0.96f, 0.21f, 0.32f) > 0.3f && Near(a, 0.53f, 0.84f, 0.01f) > 0.3f && Near(a, 0.16f, 0.55f, 0.96f) > 0.3f) continue;
                if (new Godot.Vector2(x, y).DistanceTo(centre) <= 320f) n++;
            }
        Say($"gizmo with key {key}: {n} gizmo-coloured changed pixels near the origin (with W it was thousands)");
        foreach (bool pressed in new[] { true, false }) Tree.Root.PushInput(new InputEventKey { Keycode = Key.W, PhysicalKeycode = Key.W, Pressed = pressed });
        await Frames(10);
    }

    async Task TwoTabs(MapmakerDock dock)
    {
        string tab2 = "res://Maps/FlowTab2.tscn";
        Say("two tabs: start");
        Sm1Source? source = null;
        try { if (!Flag("--mm-no-sm1")) source = await Task.Run(() => Sm1Source.Load("mp_tdm_09")); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { Say("two tabs: SM1 map not available here (" + e.Message + "); using a prefab made from two blocks instead"); }
        var root1 = MapHost.Root!;
        if (source is not null)
        {
            MapHost.Root!.ShowBackdrop(source, []);
            MapHost.Register(source);
            var core = source.BoundsAll()!.Value; var mid = (core.Min + core.Max) / 2;
            var near = Enumerable.Range(0, source.Scene.Objects.Count).Where(i => !source.Hidden.Contains(i) && source.Scene.Objects[i].Name.Length > 0 && source.WorldBounds(i) is not null)
                .OrderBy(i => (source.Scene.Objects[i].Transform.Translation - mid).Length()).Take(3).ToList();
            MapHost.PickedSm1.Clear(); MapHost.PickedSm1.AddRange(near.Select(i => new PickRef(i))); plugin.RefreshPicked();
            var r = dock.Extract("Flow piece", "flow-piece", "bounds-bottom-center", "mesh");
            Check(r.Ok && MapHost.Library.GetPrefab("flow-piece") is not null, "extracted a prefab from the SM1 map in tab 1");
            MapHost.PickedSm1.Clear(); plugin.RefreshPicked();
        }
        else
        {
            // A one-triangle glTF written here, imported as an asset, placed, and grouped into the prefab.
            var bytes = new byte[36]; Buffer.BlockCopy(new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, 0, bytes, 0, 36);
            string gltf = Path.Combine(_out, "flow-tri.gltf");
            File.WriteAllText(gltf, "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}],\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}],"
                + "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]}],\"bufferViews\":[{\"buffer\":0,\"byteLength\":36}],"
                + "\"buffers\":[{\"byteLength\":36,\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(bytes) + "\"}]}");
            var imp = plugin.Do("Import asset", MapmakerPlugin.Op("import-asset", ("path", gltf), ("id", "flow-tri"), ("name", "Flow triangle")));
            var put = plugin.PlaceAt(new LibraryItem(LibraryKind.Asset, "flow-tri", "Flow triangle"), new Godot.Vector3(30, 30, 0));
            var grp = put.Ok && put.Created.Count > 0 ? plugin.Do("Group to prefab", MapmakerPlugin.Op("group-to-prefab", ("ids", MapmakerPlugin.Strs(put.Created)), ("prefab", "flow-piece"), ("name", "Flow piece"), ("origin", "bounds-bottom-center"))) : put;
            Check(imp.Ok && grp.Ok && MapHost.Library.GetPrefab("flow-piece") is not null, "made a synthetic prefab from an imported one-triangle glTF in tab 1 (" + string.Join("; ", imp.Errors.Concat(grp.Errors).Select(e => e.Message)) + ")");
        }
        // Two placements in tab 1 for the undo check after the tab switches.
        var cover = new LibraryItem(LibraryKind.Piece, "cover", "Cover block");
        plugin.PlaceAt(cover, new Godot.Vector3(20, 20, 0)); plugin.PlaceAt(cover, new Godot.Vector3(24, 20, 0));
        string doc1 = Doc(); int count1 = plugin.Session!.Document.Objects.Count; string map1 = plugin.Session.Path!;
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
        string doc2 = Doc(); _tab2Root = MapHost.Root!.GetInstanceId();
        await ShotView("placed-in-tab-2");
        MapHost.Armed = null; MapHost.Tool = Tool.Select; await Frames(5);   // the box select belongs to the Select tool
        await MeasureGizmo();
        int onDisk = MapDocument.Parse(File.ReadAllText(map1)).Objects.Count;
        Check(EditorInterface.Singleton.SaveScene() == Error.Ok, "saved scene 2 (which also saves its map)");
        doc2 = Doc();   // a save embeds the prefabs the map uses (pass 3), so tab 2's map is compared as saved
        Check(onDisk < count1 && MapDocument.Parse(File.ReadAllText(map1)).Objects.Count == onDisk, $"saving scene 2 leaves the changed map of background tab 1 alone ({onDisk} objects on disk, {count1} in memory)");
        await Frames(80);   // the plugin notes which map belongs to the scene file
        await Frames(10);
        MapHost.Armed = null; MapHost.Tool = Tool.Select; await Frames(5);
        // Back to tab 1: untouched, still holding the SM1 map.
        EditorInterface.Singleton.OpenSceneFromPath("res://Maps/Empty.tscn");
        for (int i = 0; i < 120 && MapHost.Root != root1; i++) await Frames(3);
        await Frames(20);
        Check(MapHost.Root == root1 && Doc() == doc1 && plugin.Session!.Document.Objects.Count == count1 && (source is null || MapHost.Sm1For("mp_tdm_09") is not null), "tab 1 is untouched and still has its map and the SM1 map");
        var hist = plugin.History.GetHistoryUndoRedo(plugin.History.GetObjectHistoryId(root1));
        hist.Undo(); await Frames(8);
        Check(plugin.Session!.Document.Objects.Count == count1 - 1, $"Ctrl+Z still works in tab 1 after the tab switches ({count1} -> {plugin.Session.Document.Objects.Count})");
        hist.Redo(); await Frames(8);
        Check(Doc() == doc1, "Ctrl+Y brings the placement back, byte for byte");
        EditorInterface.Singleton.OpenSceneFromPath(tab2);
        for (int i = 0; i < 120 && EditorInterface.Singleton.GetEditedSceneRoot()?.SceneFilePath != tab2; i++) await Frames(3);
        await Frames(20);
        Check(plugin.Session is not null && Doc() == doc2, "tab 2 kept its own map when switching back");
        // Close and reopen scene 2 (a reload makes a new root): its map is loaded again from the scene's saved map path.
        EditorInterface.Singleton.ReloadSceneFromPath(tab2);
        for (int i = 0; i < 120 && !(MapHost.Root is { } rr && rr.GetInstanceId() != _tab2Root); i++) await Frames(3);
        await Frames(30);
        Check(plugin.Session is { } reopened && reopened.Document.Objects.Count == 1 && reopened.Document.Objects[0].Prefab == "flow-piece", "after closing and reopening scene 2 its map is back (loaded from the scene's map path)");
        EditorInterface.Singleton.OpenSceneFromPath("res://Maps/Empty.tscn");
        await Frames(30);
        await Frames(130);   // the slow poll prunes
        Check(plugin.StashCount == EditorInterface.Singleton.GetOpenScenes().Length - 1, $"stashed maps are exactly those of the other open tabs ({plugin.StashCount} held, {EditorInterface.Singleton.GetOpenScenes().Length} tabs open)");
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
            dock.ShowTab(DockTab.Map);
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

            // 1b. Dock layout documentation (agents only): one cropped picture of every tab, with an object selected so the Object tab is filled.
            if (Flag("--mm-docktabs"))
            {
                plugin.Select([plugin.Session!.Document.Objects.First(o => o.Piece is { Kind: PieceKind.Box }).Id]);
                await Frames(20);
                for (int i = 0; i < dock.TestTabCount; i++)
                {
                    dock.TestShowTabIndex(i);
                    await Frames(20);
                    if (!Thumbnails.CanRender) { Say($"dock tab {i}: headless, skipped"); continue; }
                    var rect = dock.GetGlobalRect();
                    var img = Tree.Root.GetTexture().GetImage();
                    var region = new Rect2I((int)rect.Position.X, (int)rect.Position.Y, (int)rect.Size.X, (int)rect.Size.Y).Intersection(new Rect2I(Vector2I.Zero, img.GetSize()));
                    string tabFile = $"{i + 1:00}-{dock.TestTabTitle(i).ToLowerInvariant()}-{Arg("--mm-tabsuffix", "shot")}.png";
                    img.GetRegion(region).SavePng(Path.Combine(_out, tabFile));
                    Say($"dock tab shot {tabFile} ({region.Size.X}x{region.Size.Y})");
                }
                dock.TestShowTabIndex(0);
            }

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

            // 7b. The server loads the chosen file by path and a client is given the same file (--custom-map): the same hash, whatever else is in the maps folder.
            Check(CustomMaps.LoadForPlay(plugin.Compiled()!.Name, file).Hash == CustomMaps.Load(file).Hash, "the server's and the client's load of the chosen file have the same hash");

            // 8. Play test, both modes (the real thing: server on 127.0.0.1, bots, an observer on the test desktop).
            if (Flag("--mm-play"))
            {
                foreach (var mode in new[] { "annihilation", "seize" })
                {
                    string shots = Path.Combine(_out, "ingame-" + mode);
                    var w = System.Diagnostics.Stopwatch.StartNew();
                    string? err = dock.TestPlay(plugin.Session!.Path!, mode, 2, "marines", ["-Test", "-OutDir", shots, "-Seconds", "50"]);
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
            await SceneIsolation(dock);

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


