using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmMapmaker;
using Num = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>
/// Test aid (agents, never the owner's editor): <c>godot.cmd --editor --path mapmaker -- --mm-classic --mm-lib &lt;dir&gt; [--mm-out &lt;dir&gt;]</c> checks the editor side of "import as Classic plays it"
/// (slice S6): the invisible-wall (players only) and cover pieces and their drawing, the wall shape toggle, the look dropdown, the Hidden by Classic filter and greying, the Refresh from Classic
/// dialog (preview, apply selected, apply all unedited, cancel, one undo step) on a synthetic Classic source, the import's counts line and the dock's Undo button. Prints
/// <c>CLASSIC RESULT PASS|FAIL</c>. Headless it needs no picture; windowed it saves one.
/// </summary>
public sealed class ClassicTest(MapmakerPlugin plugin)
{
    public static bool Wanted => OS.GetCmdlineUserArgs().Contains("--mm-classic");
    static string Arg(string name, string fallback) { var a = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }
    SceneTree Tree => (SceneTree)Engine.GetMainLoop();
    async Task Frames(int n) { for (int i = 0; i < n; i++) await plugin.ToSignal(Tree, SceneTree.SignalName.ProcessFrame); }
    bool _failed;
    void Check(bool ok, string what) { GD.Print($"CLASSIC {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) _failed = true; }

    /// <summary>A fake Classic map whose data the test edits between refreshes.</summary>
    sealed class Source : ISm1Source
    {
        public List<(string Name, Num.Vector3 At)> Shown = [("s0", new(0, 0, 1)), ("s1", new(3, 0, 1))];
        public List<(string Name, Num.Vector3 At)> Hidden = [("h0", new(0, 3, 1))];
        public List<MapWall> Walls = [Wall("w1", 10), Wall("w2", 20)];
        public List<MapKillRegion> Kills = [new("kill_pit", new Num.Vector3(0, 0, -10), 0f, new Num.Vector3(20, 10, 4), 0)];
        public List<MapProp> Props = [new("crate", "ammo_crate", "e_cover", 4, [new Num.Vector3(5, 5, 0), new Num.Vector3(6, 6, 1)], [0, 1, 1])];

        public static MapWall Wall(string name, float x) => new(name, new Num.Vector3(x, 0, 5), Num.Vector3.Zero, new Num.Vector3(10, 2, 10), 1, 2, 1);

        public Sm1Map LoadMap(string map)
        {
            if (map != "mp_fake") throw new MapException("unknown-map", $"No map '{map}'.");
            var objects = new List<Sm1Object>();
            var hide = new HashSet<int>();
            foreach (var (n, at) in Shown) objects.Add(new(n, "def_wall", "l", MapScene.Transform(at, Num.Vector3.Zero, Num.Vector3.One), 0));
            foreach (var (n, at) in Hidden) { hide.Add(objects.Count); objects.Add(new(n, "def_wall", "l", MapScene.Transform(at, Num.Vector3.Zero, Num.Vector3.One), 0)); }
            var defs = new Dictionary<string, Sm1Definition>(StringComparer.OrdinalIgnoreCase) { ["def_wall"] = new("def_wall", 1, new Bounds(new Num.Vector3(-1, -1, 0), new Num.Vector3(1, 1, 2))) };
            return new Sm1Map(map, objects, defs, []) { Hidden = hide };
        }

        public Sm1Gameplay? LoadGameplay(string map) => new Sm1Gameplay(Walls, Kills, Props);
    }

    MapDocument Doc => plugin.Session!.Document;
    MapObject ByKey(string key) => Doc.Objects.Single(o => o.Src?.Key == key);
    MapObject? ByKeyOrNull(string key) => Doc.Objects.FirstOrDefault(o => o.Src?.Key == key);
    string DocText() => Doc.ToJson();

    static Color ShapeColour(MapmakerObject n) => n.FindChild("Shape", true, false) is MeshInstance3D { MaterialOverride: StandardMaterial3D m } ? m.AlbedoColor : new Color(0, 0, 0, 0);
    static int ShapeVertices(MapmakerObject n) => n.FindChild("Shape", true, false) is MeshInstance3D { Mesh: ArrayMesh a } ? a.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length : -1;

    public async Task Run()
    {
        try
        {
            await Frames(30);
            for (int wait = 0; MapHost.Root is null; wait++) { plugin.EnsureSceneRoot(); await Frames(20); if (wait > 60) break; }
            await Frames(20);
            string lib = Arg("--mm-lib", Path.Combine(Path.GetTempPath(), "mm-classic-lib-" + System.Environment.ProcessId));
            MapHost.Library = new MapLibrary(lib);
            var dock = plugin.Dock!;
            plugin.NewMap("classic-demo");
            await Frames(5);

            // ---- the two new pieces: Build tab buttons, placing, drawing --------------------------------------------------------------------------------------
            var presetButtons = dock.FindChildren("*", "Button", true, false).OfType<Button>().Select(b => b.Text).ToList();
            Check(presetButtons.Contains("Invisible wall (players only)") && presetButtons.Contains("Cover (shoot-through)"), "the Build tab has the two new piece buttons");
            Check(PiecePreset.Find("move-wall") is { Kind: PieceKind.MoveWall } && PiecePreset.Find("classic-cover") is { Kind: PieceKind.Cover } && PiecePreset.Find("cover") is { Kind: PieceKind.Box }, "presets: move-wall, classic-cover (the old Cover block is unchanged)");
            var wallId = plugin.PlaceAt(new LibraryItem(LibraryKind.Piece, "move-wall", "Invisible wall (players only)"), new Godot.Vector3(0, 0, 0)).Created.Single();
            var coverId = plugin.PlaceAt(new LibraryItem(LibraryKind.Piece, "classic-cover", "Cover (shoot-through)"), new Godot.Vector3(8, 0, 0)).Created.Single();
            var oldId = plugin.PlaceAt(new LibraryItem(LibraryKind.Piece, "invisible", "Invisible wall"), new Godot.Vector3(16, 0, 0)).Created.Single();
            await Frames(8);
            Check(Doc.Get(wallId).Piece is { Kind: PieceKind.MoveWall, Shape: null } && Doc.Get(coverId).Piece is { Kind: PieceKind.Cover }, "placed pieces have the new kinds");
            Check(Doc.Get(wallId).Transform.Scale == new Num.Vector3(4, 1, 3) && Doc.Get(coverId).Transform.Scale == new Num.Vector3(1.2f, 3f, 1.5f), "default sizes 4 x 1 x 3 and 1.2 x 3 x 1.5");
            var wallNode = plugin.NodeOf(wallId)!; var coverNode = plugin.NodeOf(coverId)!; var oldNode = plugin.NodeOf(oldId)!;
            var cw = ShapeColour(wallNode); var cc = ShapeColour(coverNode); var co = ShapeColour(oldNode);
            Check(cw.A > 0f && cw.A < 0.5f && cc.A > 0f && cc.A < 0.5f, $"both are drawn translucent (alpha {cw.A:0.00} and {cc.A:0.00})");
            Check(!cw.IsEqualApprox(cc) && !cw.IsEqualApprox(co) && !cc.IsEqualApprox(co), "each kind has its own tint (wall, cover, old Invisible wall)");
            Check(ShapeVertices(wallNode) > 0 && ShapeVertices(coverNode) > 0, "both draw a shape");

            // Shape toggle on the Object tab.
            plugin.Select([wallId]); dock.Refresh();
            Check(dock.WallShapeBox is { Visible: true } && dock.WallShapeButton.Selected == 0, "the Object tab shows the wall shape (Box) for an invisible wall (players only)");
            int boxVerts = ShapeVertices(wallNode);
            dock.WallShapeButton.Select(1); dock.WallShapeButton.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
            await Frames(8);
            Check(Doc.Get(wallId).Piece is { Shape: PieceSpec.CylinderShape }, "choosing Cylinder sets the piece shape");
            wallNode = plugin.NodeOf(wallId)!;
            Check(ShapeVertices(wallNode) != boxVerts, $"and the editor draws a cylinder ({boxVerts} box vertices, {ShapeVertices(wallNode)} now)");
            plugin.UndoLast(); await Frames(8); plugin.Select([wallId]); dock.Refresh();
            Check(Doc.Get(wallId).Piece is { Shape: null } && dock.WallShapeButton.Selected == 0, "undo puts the box back and the toggle follows");
            plugin.Select([coverId]); dock.Refresh();
            Check(dock.WallShapeBox is { Visible: false }, "the shape toggle is hidden for other pieces");

            // ---- Undo button -------------------------------------------------------------------------------------------------------------------------------
            var undo = dock.FindChildren("*", "Button", true, false).OfType<Button>().First(b => b.Text == "Undo");
            string before = DocText();
            plugin.PlaceAt(new LibraryItem(LibraryKind.Piece, "move-wall", "Invisible wall (players only)"), new Godot.Vector3(30, 0, 0));
            Check(DocText() != before, "a placement changed the document");
            undo.EmitSignal(BaseButton.SignalName.Pressed); await Frames(8);
            Check(DocText() == before, "the dock's Undo button undoes the last edit");
            undo.EmitSignal(BaseButton.SignalName.Pressed); await Frames(8);   // more steps back (placing the last pieces): must not throw either
            Check(true, "pressing Undo again is harmless");

            // A picture of the three kinds (windowed runs only).
            if (Thumbnails.CanRender && plugin.Viewport3D is { } vp)
            {
                string outDir = Arg("--mm-out", Path.Combine(Path.GetTempPath(), "mm-classic-" + System.Environment.ProcessId));
                Directory.CreateDirectory(outDir);
                plugin.NewMap("classic-shot");
                foreach (var (preset, x) in new[] { ("move-wall", -6f), ("classic-cover", 0f), ("invisible", 6f) })
                    plugin.PlaceAt(new LibraryItem(LibraryKind.Piece, preset, preset), new Godot.Vector3(x, 0, 0));
                var round = plugin.PlaceAt(new LibraryItem(LibraryKind.Piece, "move-wall", "round"), new Godot.Vector3(12, 0, 0)).Created.Single();
                plugin.Do("Wall shape", MapmakerPlugin.Op("set-piece", ("id", round), ("shape", "cylinder")));
                plugin.Select([]);
                await plugin.Frame3D(new Num.Vector3(-10, -6, 0), new Num.Vector3(16, 6, 4));
                await Frames(20);
                vp.GetTexture().GetImage().SavePng(Path.Combine(outDir, "classic-pieces.png"));
                GD.Print("CLASSIC shots in " + outDir);
                plugin.NewMap("classic-demo2");
                await Frames(5);
            }

            // ---- Look dropdown -------------------------------------------------------------------------------------------------------------------------------
            plugin.NewMap("look-demo"); await Frames(5);
            dock.TestSetLookMaps([new MapEntry("mp_tdm_09", "Manufactorum", ["teamdeathmatch"], true), new MapEntry("mp_tdm_17", "Shattered Bridge", ["teamdeathmatch"], true)]);
            Check(dock.TestLookCount == 3 && dock.TestLookSelected() == "", "the look dropdown lists None and the install's maps; a new map has no look");
            dock.TestPickLook("mp_tdm_17"); await Frames(5);
            Check(Doc.Look?.Sm1Map == "mp_tdm_17", "choosing a map calls set-look");
            plugin.UndoLast(); await Frames(5); dock.Refresh();
            Check(Doc.Look is null && dock.TestLookSelected() == "", "undo clears it and the dropdown follows");
            dock.TestPickLook("mp_tdm_09"); await Frames(5);
            dock.TestPickLook(""); await Frames(5);
            Check(Doc.Look is null, "choosing None clears the look");
            plugin.Do("Look", MapmakerPlugin.Op("set-look", ("sm1Map", "mp_xyz_01"))); await Frames(5); dock.Refresh();
            Check(dock.TestLookSelected() == "mp_xyz_01", "a look the install does not have still shows (marked as not in your install)");
            plugin.UndoLast(); await Frames(5);

            // ---- Import counts, Hidden by Classic -----------------------------------------------------------------------------------------------------------------
            var fake = new Source();
            plugin.NewMap("classic-import"); await Frames(5);
            plugin.Session!.Sm1 = fake;
            var imp = plugin.Do("Import SM1 map mp_fake", MapmakerPlugin.Op("import-sm1-map", ("map", "mp_fake")));
            await Frames(10);
            Check(imp.Ok, "the synthetic import ran: " + string.Join(" | ", imp.Errors.Select(e => e.Message)));
            string summary = MapmakerPlugin.ImportSummary(Doc, "mp_fake");
            GD.Print("CLASSIC summary: " + summary);
            Check(summary.Contains("2 object(s), 2 invisible wall(s), 1 kill box(es), 1 cover and 1 hidden by Classic"), "the import result text carries the counts of walls, kill boxes, cover and hidden");
            Check(imp.Warnings.Any(w => w.Contains("2 invisible wall(s)")) , "and the core's own warning line names them too");

            var hiddenObj = ByKey("h0");
            Check(!hiddenObj.Visible && hiddenObj.Src!.ClassicHidden, "the hidden placement was imported switched off");
            var tree = dock.ObjectTree;
            TreeItem? Row(string id) { for (var it = tree.GetRoot()?.GetFirstChild(); it is not null; it = Next(it)) if (it.GetMetadata(0).AsString() == id) return it; return null; }
            var hrow = Row(hiddenObj.Id);
            Check(hrow is not null && hrow.GetText(0).Contains("hidden by Classic") && hrow.GetCustomColor(0).IsEqualApprox(new Color(0.5f, 0.52f, 0.56f)), "its Scene row is greyed and says so");
            var shownRow = Row(ByKey("s0").Id);
            Check(shownRow is not null && !shownRow.GetText(0).Contains("hidden") && !shownRow.GetCustomColor(0).IsEqualApprox(new Color(0.5f, 0.52f, 0.56f)), "a visible object's row is not greyed");
            dock.HiddenFilterOn = true; await Frames(3);
            int rows = CountRows(tree);
            Check(rows == 1 && Row(hiddenObj.Id) is not null, $"the Hidden by Classic filter lists exactly the hidden objects ({rows} row)");
            dock.HiddenFilterOn = false; await Frames(3);
            Check(CountRows(tree) > 1, "switching the filter off lists everything again");
            plugin.Select([hiddenObj.Id]); dock.Refresh();
            var toggled = plugin.Do("Visibility", MapmakerPlugin.Op("set-visible", ("id", hiddenObj.Id), ("visible", true)));
            await Frames(3);
            Check(toggled.Ok && Doc.Get(hiddenObj.Id).Visible && Row(hiddenObj.Id)!.GetText(0).Contains("Classic hides it; on"), "the existing visibility toggle brings it back (the row says Classic hides it, now on)");
            plugin.UndoLast(); await Frames(5);

            // ---- Refresh from Classic --------------------------------------------------------------------------------------------------------------------------
            var button = dock.RefreshClassicButtonControl;
            dock.TestSetInstall(false);
            Check(button is { Disabled: true } && button.TooltipText == MapmakerPlugin.NoInstallMessage, "without an install the Refresh button is disabled and shows the missing-install message");
            dock.TestSetInstall(true);
            Check(button is { Disabled: false }, "with an install it is enabled");
            string ok0 = DocText();
            var empty = plugin.PreviewRefresh();
            Check(empty.Ok && empty.Refresh is { } e0 && new[] { "added", "removed", "changed", "deleted", "skipped" }.All(k => ((JsonArray)e0[k]!).Count == 0), "right after the import there is nothing to refresh");

            // Classic moves on: w1 moves, w2 and the kill pit are gone, w3 and a hidden h1 are new. The owner edits w2 and deletes s1.
            fake.Walls[0] = Source.Wall("w1", 11); fake.Walls.RemoveAt(1); fake.Walls.Add(Source.Wall("w3", 30));
            fake.Kills.Clear(); fake.Hidden.Add(("h1", new Num.Vector3(0, 6, 1)));
            plugin.Do("Edit w2", MapmakerPlugin.Op("transform", ("id", ByKey("w2").Id), ("move", MapmakerPlugin.Vec(new Num.Vector3(0, 0, 2)))));
            string s1Id = ByKey("s1").Id;
            plugin.Do("Delete s1", MapmakerPlugin.Op("delete", ("ids", MapmakerPlugin.Strs([s1Id]))));
            await Frames(8);
            string beforeRefresh = DocText(); int versionBefore = plugin.Session!.Version;

            var preview = plugin.PreviewRefresh();
            Check(preview.Ok && DocText() == beforeRefresh && plugin.Session.Version == versionBefore, "the preview changes nothing (no undo step, same document)");
            var model = RefreshModel.From(preview.Refresh!, preview.Warnings);
            string Keys(string section) => string.Join(",", model.In(section).Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal));
            Check(Keys("added") == "h1,w3" && Keys("removed") == "kill_pit,w2" && Keys("changed") == "w1" && Keys("deleted") == "s1", $"the model lists added {Keys("added")}, removed {Keys("removed")}, changed {Keys("changed")}, deleted {Keys("deleted")}");
            Check(model.In("removed").Single(r => r.Key == "w2").Edited && !model.In("removed").Single(r => r.Key == "kill_pit").Edited, "w2 is marked edited by you; the kill box is not");
            Check(model.In("removed").Single(r => r.Key == "kill_pit").Checked && !model.In("removed").Single(r => r.Key == "w2").Checked && !model.In("deleted").Single().Checked, "untouched changes start ticked; edited ones and restores start unticked");

            dock.ShowRefreshResult(preview); await Frames(5);
            var dialog = dock.RefreshDialogControl;
            Check(dialog is { Visible: true }, "the dialog opens");
            var boxes = dialog!.FindChildren("*", "CheckBox", true, false).OfType<CheckBox>().ToList();
            var labels = dialog.FindChildren("*", "Label", true, false).OfType<Label>().Select(l => l.Text).ToList();
            var heads = labels.Where(t => t.Contains('(')).ToList();
            Check(boxes.Count == 4, $"four rows have a checkbox (removed 2, changed 1, deleted 1): {boxes.Count}");
            Check(boxes.Any(b => b.Text.Contains("edited by you")), "the edited row says 'edited by you'");
            Check(new[] { "Added by Classic (2)", "Removed from Classic (2)", "Changed in Classic (1)", "Deleted by you (1)" }.All(h => labels.Contains(h)), $"the four section headings are there ({string.Join(" | ", heads)})");
            Check(labels.Any(t => t.Contains("w3")) && labels.Any(t => t.Contains("h1")), "added items are listed as plain lines");
            var ok = dialog.GetOkButton(); var cancel = dialog.GetCancelButton();
            Check(ok.Text == "Apply selected" && cancel.Text == "Cancel" && dialog.FindChildren("*", "Button", true, false).OfType<Button>().Any(b => b.Text == "Apply all I haven't edited"), "the three buttons: Apply selected, Apply all I haven't edited, Cancel");

            // Cancel: nothing changes.
            cancel.EmitSignal(BaseButton.SignalName.Pressed); await Frames(5);
            Check(DocText() == beforeRefresh, "Cancel changes nothing");

            // Apply selected: tick the deleted s1 too.
            dock.ShowRefreshResult(plugin.PreviewRefresh()); await Frames(5);
            var restoreBox = dock.RefreshDialogControl!.FindChildren("*", "CheckBox", true, false).OfType<CheckBox>().First(b => b.Text.Contains("s1"));
            restoreBox.ButtonPressed = true; await Frames(2);
            dock.TestRefreshPress(false); await Frames(10);
            Check(ByKeyOrNull("w3") is not null && ByKeyOrNull("h1") is { Visible: false }, "Apply inserted the added items (the hidden one switched off)");
            Check(ByKeyOrNull("kill_pit") is null && ByKeyOrNull("w2") is not null, "the ticked removal was applied; the edited, unticked one was kept");
            Check(ByKey("w1").Transform.Position.X == 11f, "the ticked change moved w1 to Classic's place");
            Check(ByKeyOrNull("s1") is not null, "the ticked deleted item was restored");
            Check(dock.TestRefreshResult.StartsWith("Refreshed from Classic: 2 added, 1 changed, 1 removed, 1 restored."), "the result line: " + dock.TestRefreshResult);
            plugin.UndoLast(); await Frames(10);
            Check(DocText() == beforeRefresh, "one undo takes the whole refresh back");

            // Apply all I haven't edited: skips the edited w2 and the unticked restore.
            dock.ShowRefreshResult(plugin.PreviewRefresh()); await Frames(5);
            dock.TestRefreshPress(true); await Frames(10);
            Check(ByKeyOrNull("w3") is not null && ByKeyOrNull("kill_pit") is null && ByKey("w1").Transform.Position.X == 11f, "Apply all I haven't edited: added, removal and change applied");
            Check(ByKeyOrNull("w2") is not null && ByKeyOrNull("s1") is null, "the edited w2 and the deleted s1 were left alone");
            plugin.UndoLast(); await Frames(10);
            Check(DocText() == beforeRefresh, "and that is one undo too");

            // No install source at all: the message, not an exception.
            plugin.Session.Sm1 = null;
            dock.ShowRefreshResult(plugin.PreviewRefresh()); await Frames(3);
            Check(plugin.Status.Contains("install was not found"), "a refresh with no install says so: " + plugin.Status);
            dock.TestSetInstall(null);
        }
        catch (Exception e) { GD.Print("CLASSIC exception " + e); _failed = true; }
        GD.Print(_failed ? "CLASSIC RESULT FAIL" : "CLASSIC RESULT PASS");
        Tree.Quit(_failed ? 1 : 0);
    }

    static TreeItem? Next(TreeItem it)
    {
        if (it.GetFirstChild() is { } c) return c;
        for (var cur = it; cur is not null; cur = cur.GetParent())
            if (cur.GetNext() is { } n) return n;
        return null;
    }

    static int CountRows(Tree tree)
    {
        int n = 0;
        for (var it = tree.GetRoot()?.GetFirstChild(); it is not null; it = Next(it)) n++;
        return n;
    }
}
