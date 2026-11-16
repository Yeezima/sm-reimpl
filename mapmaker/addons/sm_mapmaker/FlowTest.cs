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


