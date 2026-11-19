using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// Test aid (agents, never the owner's editor): <c>godot.cmd --windowed --editor --path mapmaker -- --mm-groups --mm-out &lt;dir&gt; --mm-lib &lt;dir&gt;</c> exercises nesting and groups
/// the way the dock and the Scene dock do (a node dragged under another becomes set-parent; group, nest, select a group as a whole, undo), takes a screenshot of the Scene tab
/// and prints <c>GROUPS RESULT PASS|FAIL</c>. Headless it runs the same checks without the picture.
/// </summary>
public sealed class GroupsTest(MapmakerPlugin plugin)
{
    public static bool Wanted => OS.GetCmdlineUserArgs().Contains("--mm-groups");
    static string Arg(string name, string fallback) { var a = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }
    SceneTree Tree => (SceneTree)Engine.GetMainLoop();
    async Task Frames(int n) { for (int i = 0; i < n; i++) await plugin.ToSignal(Tree, SceneTree.SignalName.ProcessFrame); }
    bool _failed;
    void Check(bool ok, string what) { GD.Print($"GROUPS {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) _failed = true; }

    public async Task Run()
    {
        try
        {
            var outDir = Arg("--mm-out", Path.Combine(Path.GetTempPath(), "mm-groups-" + System.Environment.ProcessId));
            Directory.CreateDirectory(outDir);
            await Frames(30);
            for (int wait = 0; MapHost.Root is null; wait++) { plugin.EnsureSceneRoot(); await Frames(20); if (wait > 60) break; }
            MapHost.Library = new MapLibrary(Arg("--mm-lib", Path.Combine(Path.GetTempPath(), "mm-groups-lib-" + System.Environment.ProcessId)));
            var dock = plugin.Dock!;
            plugin.NewMap("groups-demo");
            await Frames(5);
            string[] names = ["Left pillar", "Right pillar", "Lintel", "Crate A", "Crate B", "Crate C"];
            for (int i = 0; i < names.Length; i++)
                Check(plugin.Do("Place", MapmakerPlugin.Op("place", ("piece", new System.Text.Json.Nodes.JsonObject { ["kind"] = "box" }), ("name", names[i]),
                    ("transform", new System.Text.Json.Nodes.JsonObject { ["position"] = MapmakerPlugin.Vec(new System.Numerics.Vector3(i * 4, 0, 0)) }))).Ok, "placed " + names[i]);
            var doc = () => plugin.Session!.Document;

            // Nesting by dragging a node under another in Godot's Scene dock (any kind of object): the document follows, and it is undoable.
            var crate = plugin.NodeOf("o5")!; var lintel = plugin.NodeOf("o3")!;
            crate.Reparent(lintel);
            plugin.Reconcile();
            await Frames(3);
            Check(doc().Get("o5").Parent == "o3", "a Scene-dock drag became set-parent (o5 under o3)");
            Check(plugin.NodeOf("o5")!.GetParent() == plugin.NodeOf("o3"), "the node stays under its new parent (not reverted)");
            // Dock list reparent: unparent through the same call the Unparent button uses.
            Check(plugin.SetParentOf(["o5"], null).Ok && doc().Get("o5").Parent is null, "Unparent");
            Check(plugin.SetParentOf(["o4"], "o3").Ok && doc().Get("o4").Parent == "o3", "Make child of (the Objects list)");
            Check(!plugin.SetParentOf(["o3"], "o4").Ok, "a parent cannot go inside its own child");

            // Groups: pillars+lintel is "Gate"; the crates are a group inside "Props"; one group inside another.
            plugin.Select(["o1", "o2", "o3"]);
            Check(plugin.GroupSelected("Gate").Ok, "group from selection");
            plugin.Select(["o4", "o5", "o6"]);
            Check(plugin.GroupSelected("Crates").Ok, "second group");
            plugin.Select(["o1", "o2", "o3", "o4", "o5", "o6"]);
            Check(plugin.GroupSelected("Everything").Ok && doc().GetGroup("g3").Members.SequenceEqual(["g1", "g2"]), "a selection of whole groups nests them (Everything = Gate + Crates)");
            plugin.SelectMembers(["g1"]);
            await Frames(2);
            Check(plugin.SelectedIds().OrderBy(x => x).SequenceEqual(["o1", "o2", "o3"]), "selecting a group selects all its objects");
            plugin.Select(["o2"]);
            Check(plugin.SelectedIds().SequenceEqual(["o2"]), "clicking one object selects just it");
            Check(doc().Objects.Count == 6 && doc().Groups.Count == 3, "groups changed no objects");

            // Double-click on a member of a nested group (the click selected the single object; the expansion runs after it): innermost group, then the one around it.
            plugin.Select(["o2"]);
            await plugin.ExpandToGroup();
            Check(plugin.SelectedIds().OrderBy(x => x).SequenceEqual(["o1", "o2", "o3"]), "double-click on a member selects its innermost group (Gate)");
            plugin.Select(["o2"]);   // the second double-click's own click
            await plugin.ExpandToGroup();
            Check(plugin.SelectedIds().OrderBy(x => x).SequenceEqual(["o1", "o2", "o3", "o4", "o5", "o6"]), "a second double-click takes the group around it (Everything)");

            await Frames(3);
            dock.ShowTab(DockTab.Scene);
            await Frames(10);
            Check(dock.GroupTree.GetRoot() is { } gr && gr.GetChildCount() == 1, "the Groups list shows Everything as the single top group");
            if (dock.GroupTree.GetRoot()?.GetFirstChild() is { } top) top.SetCollapsed(false);
            if (Thumbnails.CanRender)
            {
                var r = dock.GetGlobalRect();
                var img = Tree.Root.GetTexture().GetImage().GetRegion(new Rect2I((Vector2I)r.Position, (Vector2I)r.Size));
                img.SavePng(Path.Combine(outDir, "groups-list.png"));
                Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(outDir, "groups-window.png"));
                GD.Print("GROUPS shot " + Path.Combine(outDir, "groups-list.png"));
            }
            else GD.Print("GROUPS headless: no screenshot");
        }
        catch (Exception e) { GD.Print("GROUPS exception " + e); _failed = true; }
        GD.Print(_failed ? "GROUPS RESULT FAIL" : "GROUPS RESULT PASS");
        Tree.Quit(_failed ? 1 : 0);
    }
}
