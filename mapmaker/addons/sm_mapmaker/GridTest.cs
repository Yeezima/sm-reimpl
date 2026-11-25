using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// Test aid (agents, never the owner's editor): <c>godot.cmd --editor --path mapmaker -- --mm-grid --mm-lib &lt;dir&gt;</c> checks the Map tab's Grid section: where it sits, that the drawn
/// lines follow the cell size and never the camera, height and colour, the draw-over depth test, one undo step per edit, the save / open round trip, an old map's defaults, the "from
/// selection" base height and Godot's own grid being switched off and given back. Prints <c>GRID RESULT PASS|FAIL</c>. Headless it needs no picture.
/// </summary>
public sealed class GridTest(MapmakerPlugin plugin)
{
    public static bool Wanted => OS.GetCmdlineUserArgs().Contains("--mm-grid");
    static string Arg(string name, string fallback) { var a = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }
    SceneTree Tree => (SceneTree)Engine.GetMainLoop();
    async Task Frames(int n) { for (int i = 0; i < n; i++) await plugin.ToSignal(Tree, SceneTree.SignalName.ProcessFrame); }
    bool _failed;
    void Check(bool ok, string what) { GD.Print($"GRID {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) _failed = true; }

    MeshInstance3D? Node => MapHost.Root?.GetNodeOrNull<MeshInstance3D>(EditorGrid.NodeName);
    StandardMaterial3D Mat => (StandardMaterial3D)Node!.MaterialOverride!;
    Vector3[] Verts() => ((ArrayMesh)Node!.Mesh).SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
    SubViewport? Viewport3D() => plugin.Viewport3D;
    static float[] Distinct(Vector3[] v, Func<Vector3, float> f) => v.Select(f).Select(x => MathF.Round(x, 3)).Distinct().OrderBy(x => x).ToArray();

    public async Task Run()
    {
        try
        {
            await Frames(30);
            for (int wait = 0; MapHost.Root is null; wait++) { plugin.EnsureSceneRoot(); await Frames(20); if (wait > 60) break; }
            await Frames(20);
            string lib = Arg("--mm-lib", Path.Combine(Path.GetTempPath(), "mm-grid-lib-" + System.Environment.ProcessId));
            MapHost.Library = new MapLibrary(lib);
            var dock = plugin.Dock!;

            // Godot's own grid: off while this scene is open (checked before a map exists: the scene is the trigger).
            await Frames(10);
            var states = EditorGrid.ViewGridStates();
            Check(states.Length > 0, "Godot's View Grid menu item was found");
            Check(states.Length > 0 && states.All(s => !s), $"Godot's own grid is off while a Mapmaker scene is open ({string.Join(",", EditorGrid.ViewGridStates())})");
            Check(Node is not null && Node.Visible, "our grid is drawn in an empty Mapmaker scene (defaults)");

            // Where the section is: after Snap, before SM1 reference map, on the Map tab.
            var page = dock.FindChild(MapmakerDock.GridShowName, true, false)?.GetParent();
            var heads = page is null ? [] : page.GetChildren().OfType<Label>().Where(l => l.ThemeTypeVariation == "HeaderSmall").Select(l => l.Text).ToList();
            var all = dock.FindChildren("*", "Label", true, false).OfType<Label>().Where(l => l.ThemeTypeVariation == "HeaderSmall").Select(l => l.Text).ToList();
            int snap = all.IndexOf("Snap"), grid = all.IndexOf("Grid"), sm1 = all.IndexOf("SM1 reference map");
            Check(snap >= 0 && grid == snap + 1 && sm1 == grid + 1, $"Grid is between Snap and SM1 reference map ({string.Join(" > ", all)})");
            Check(heads.Contains("Grid"), "the Grid controls share one column with their heading");
            foreach (var n in new[] { MapmakerDock.GridShowName, MapmakerDock.GridColourName, MapmakerDock.GridCellName, MapmakerDock.GridHeightName, MapmakerDock.GridFromSelectionName, MapmakerDock.GridOverName })
                Check(dock.FindChild(n, true, false) is not null, "control " + n);

            // Defaults on a new map.
            plugin.NewMap("grid-demo");
            await Frames(5);
            var g = plugin.CurrentGrid;
            Check(g == MapGrid.Default && g.Show && g.CellSize == 1f && g.Height == 0f && !g.DrawOver && g.Colour == "#80808080", "a new map has the default grid (on, grey 50%, 1 m, height 0, hidden behind objects)");
            Check(Mat.NoDepthTest == false && Node!.Position.Y == 0f, "default material keeps the depth test; plane at height 0");

            // Spacing follows the cell size; the camera changes nothing.
            var v1 = Verts();
            var xs = Distinct(v1, p => p.X);
            Check(xs.Length == 401 && Enumerable.Range(0, xs.Length - 1).All(i => MathF.Abs(xs[i + 1] - xs[i] - 1f) < 1e-3f), $"lines are 1 m apart over {xs[0]}..{xs[^1]} m ({xs.Length} lines per direction)");
            var before = Node!.Mesh;
            await plugin.Frame3D(new System.Numerics.Vector3(-5, -5, 0), new System.Numerics.Vector3(5, 5, 3));
            await Frames(10);
            var cam = plugin.Viewport3D?.GetCamera3D();
            var camBefore = cam?.GlobalPosition;
            await plugin.Frame3D(new System.Numerics.Vector3(-150, -150, 0), new System.Numerics.Vector3(150, 150, 40));
            await Frames(10);
            Check(cam is null || camBefore != cam.GlobalPosition, "the editor camera moved (zoom changed)");
            Check(ReferenceEquals(before, Node!.Mesh) && Verts().SequenceEqual(v1), "the grid mesh is the same after moving and zooming the camera");
            Check(Mat.AlbedoColor == new Color(128 / 255f, 128 / 255f, 128 / 255f, 128 / 255f) && Node.Visible, "colour and visibility unchanged by the camera");

            // Cell size.
            Check(plugin.SetGrid("Grid: cell size", ("cellSize", 2.5f)).Ok, "cell size 2.5");
            await Frames(3);
            var v2 = Verts();
            var x2 = Distinct(v2, p => p.X);
            var z2 = Distinct(v2, p => p.Z);
            Check(x2.Length == 161 && x2.Zip(x2.Skip(1)).All(p => MathF.Abs(p.Second - p.First - 2.5f) < 1e-3f) && x2[0] == -200f && z2.SequenceEqual(x2), $"2.5 m cells: {x2.Length} lines, 2.5 m apart, covering +-200 m");
            Check(!plugin.SetGrid("Grid: cell size", ("cellSize", 0f)).Ok, "a cell size of 0 is refused");
            Check(plugin.SetGrid("Grid: cell size", ("cellSize", 0.1f)).Ok, "the smallest cell size 0.1 m");
            await Frames(3);
            Check(Verts().Length == 4 * EditorGrid.MaxLinesPerAxis, $"the line count stays bounded at 0.1 m ({Verts().Length / 2} segments)");
            plugin.SetGrid("Grid: cell size", ("cellSize", 1f));

            // Height (Z up) and colour.
            Check(plugin.SetGrid("Grid: height", ("height", 3.25f)).Ok, "height 3.25");
            await Frames(3);
            Check(MathF.Abs(Node!.Position.Y - 3.25f) < 1e-5f && MathF.Abs(Frame.ToGame(Node.Position).Z - 3.25f) < 1e-5f, "the plane sits at SM1 Z 3.25 (Godot Y 3.25)");
            dock.TestSetGridColour(new Color(1f, 0f, 0f, 0.25f));
            await Frames(3);
            Check(plugin.CurrentGrid.Colour == "#ff000040" && Mat.AlbedoColor.IsEqualApprox(new Color(1f, 0f, 0f, 64 / 255f)), "colour with alpha applies (#ff000040)");

            // Draw over objects.
            Check(plugin.SetGrid("Grid: draw over objects", ("drawOver", true)).Ok, "draw over on");
            await Frames(3);
            Check(Mat.NoDepthTest, "draw over: the material has no depth test");
            plugin.SetGrid("Grid: draw over objects", ("drawOver", false));
            await Frames(3);
            Check(!Mat.NoDepthTest, "draw over off: depth test back");
            plugin.SetGrid("Grid: show", ("show", false));
            await Frames(3);
            Check(!Node!.Visible, "show off hides the grid");
            plugin.SetGrid("Grid: show", ("show", true));

            // Independent of Snap.
            float step = MapHost.GridStep;
            MapHost.GridStep = 0.75f;
            plugin.SetGrid("Grid: cell size", ("cellSize", 4f));
            Check(MapHost.GridStep == 0.75f && MathF.Abs(plugin.CurrentGrid.CellSize - 4f) < 1e-6f, "the Snap step and the grid cell size are independent");
            MapHost.GridStep = step;

            // Undo: one step per change.
            plugin.SetGrid("Grid: cell size", ("cellSize", 8f));
            Check(plugin.CurrentGrid.CellSize == 8f, "cell size 8");
            var ur = plugin.History;
            ur.GetHistoryUndoRedo(ur.GetObjectHistoryId(MapHost.Root!)).Undo();
            await Frames(8);
            Check(plugin.CurrentGrid.CellSize == 4f && x2.Length > 0 && Distinct(Verts(), p => p.X).Length == 101, "one undo reverts one grid edit (8 back to 4 m) and the drawing follows");
            Check(plugin.Dirty, "a grid edit marks the map changed");

            // Height from the selection: the lowest point of the object's box.
            Check(plugin.Do("Place", MapmakerPlugin.Op("place", ("piece", new JsonObject { ["kind"] = "box" }), ("name", "Block"),
                ("transform", new JsonObject { ["position"] = MapmakerPlugin.Vec(new System.Numerics.Vector3(3, 4, 2.5f)) }))).Ok, "placed a box");
            await Frames(5);
            plugin.Select(["o1"]);
            await Frames(3);
            float? baseZ = plugin.SelectionBaseHeight();
            Check(baseZ is { } bz && bz > 2.4f && bz < 2.6f + 1f, $"base height of the selection is its lowest point ({baseZ})");
            (dock.FindChild(MapmakerDock.GridFromSelectionName, true, false) as Button)!.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(3);
            Check(baseZ is { } bz2 && MathF.Abs(plugin.CurrentGrid.Height - bz2) < 1e-3f, "From selection sets the height");
            plugin.Select([]);
            Check(plugin.SelectionBaseHeight() is null, "no selection: no base height");

            // Save, reopen.
            string path = Path.Combine(lib, "grid-roundtrip.smmap");
            plugin.SetGrid("Grid: colour", ("colour", "#336699cc"));
            plugin.SetGrid("Grid: draw over objects", ("drawOver", true));
            plugin.SetGrid("Grid: cell size", ("cellSize", 0.5f));
            var saved = plugin.CurrentGrid;
            Check(plugin.SaveMap(path), "saved");
            string text = File.ReadAllText(path);
            Check(text.Contains("\"grid\"") && text.Contains($"\"formatVersion\": {MapDocument.BaseFormatVersion}"), "the file carries a grid object and the current formatVersion");
            plugin.NewMap("other");
            await Frames(3);
            Check(plugin.CurrentGrid == MapGrid.Default, "another new map has the defaults again");
            plugin.OpenMap(path);
            await Frames(5);
            Check(plugin.CurrentGrid == saved, $"the grid survives save and open ({plugin.CurrentGrid})");
            Check(Mat.NoDepthTest && Mathf.IsEqualApprox(Node!.Position.Y, saved.Height) && Distinct(Verts(), p => p.X).Length == 801, "and is drawn from the opened map");
            var shown = (dock.FindChild(MapmakerDock.GridOverName, true, false) as CheckBox)!;
            var cellBox = (dock.FindChild(MapmakerDock.GridCellName, true, false) as SpinBox)!;
            Check(shown.ButtonPressed && Mathf.IsEqualApprox((float)cellBox.Value, 0.5f), $"the section shows the opened map's values (draw over {shown.ButtonPressed}, cell {cellBox.Value})");

            // An old map (no grid object, older format) opens with the defaults.
            var old = JsonNode.Parse(text)!.AsObject();
            old.Remove("grid"); old["formatVersion"] = 1; old.Remove("prefabs");
            string oldPath = Path.Combine(lib, "grid-old.smmap");
            File.WriteAllText(oldPath, old.ToJsonString());
            plugin.OpenMap(oldPath);
            await Frames(5);
            Check(plugin.CurrentGrid == MapGrid.Default, "an old map without grid data opens with the defaults");
            Check(Mat.NoDepthTest == false && Node!.Visible && Mathf.IsZeroApprox(Node.Position.Y), "and draws them");

            // A picture (windowed runs only): the grid under a block, then over it.
            if (Thumbnails.CanRender && Viewport3D() is { } vp)
            {
                string outDir = Arg("--mm-out", Path.Combine(Path.GetTempPath(), "mm-grid-" + System.Environment.ProcessId));
                Directory.CreateDirectory(outDir);
                plugin.NewMap("grid-shot");
                plugin.Do("Place", MapmakerPlugin.Op("place", ("piece", new JsonObject { ["kind"] = "box" }), ("name", "Block"),
                    ("transform", new JsonObject { ["position"] = MapmakerPlugin.Vec(new System.Numerics.Vector3(0, 0, 1)), ["scale"] = MapmakerPlugin.Vec(new System.Numerics.Vector3(6, 6, 2)) })));
                plugin.SetGrid("Grid: colour", ("colour", "#ffcc00ff")); plugin.SetGrid("Grid: cell size", ("cellSize", 2f)); plugin.SetGrid("Grid: height", ("height", 1f));
                await plugin.Frame3D(new System.Numerics.Vector3(-14, -14, 0), new System.Numerics.Vector3(14, 14, 3));
                await Frames(20);
                vp.GetTexture().GetImage().SavePng(Path.Combine(outDir, "grid-behind.png"));
                plugin.SetGrid("Grid: draw over objects", ("drawOver", true));
                await Frames(20);
                vp.GetTexture().GetImage().SavePng(Path.Combine(outDir, "grid-over.png"));
                GD.Print("GRID shots in " + outDir);
            }

            // Godot's grid comes back with the user's setting.
            EditorGrid.RestoreBuiltIn();
            await Frames(3);
            Check(EditorGrid.ViewGridStates().Length > 0 && EditorGrid.ViewGridStates().All(s => s), "Godot's own grid is restored with the user's setting when the Mapmaker scene closes or the plugin unloads");
            plugin.ApplyGrid(hold: true);
            await Frames(3);
            Check(EditorGrid.ViewGridStates().All(s => !s), "and switched off again when a Mapmaker scene is in use");
            EditorGrid.RestoreBuiltIn();
        }
        catch (Exception e) { GD.Print("GRID exception " + e); _failed = true; }
        GD.Print(_failed ? "GRID RESULT FAIL" : "GRID RESULT PASS");
        Tree.Quit(_failed ? 1 : 0);
    }
}
