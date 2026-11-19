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
/// Test aid (agents, never the owner's editor): <c>godot.cmd --windowed --editor --path mapmaker -- --mm-sm1lib --mm-out &lt;dir&gt;</c> (with <c>SM_MAPMAKER_CONTROL</c>
/// and <c>--mm-lib</c> pointing at scratch folders) checks the 2026-11-19 SM1 work against the owner's install: the library lists every Classic PvP map's
/// definitions grouped by map with a map filter (no map opened), a definition of a never-opened map can be placed, "Remove SM1 backdrop" is undoable and leaves
/// the objects alone, and "Import SM1 map for editing" creates the spawn areas and doors at the original's marker places. Screenshots go to <c>--mm-out</c>;
/// it prints <c>SM1LIB RESULT PASS|FAIL</c> and quits. Skipped without an install.
/// </summary>
public sealed class Sm1LibraryTest(MapmakerPlugin plugin, MapmakerDock dock)
{
    public static bool Wanted => OS.GetCmdlineUserArgs().Contains("--mm-sm1lib");
    static string Arg(string name, string fallback) { var a = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }

    SceneTree Tree => (SceneTree)Engine.GetMainLoop();
    readonly List<string> _log = new();
    bool _failed;
    string _out = "";

    async Task Frames(int n) { for (int i = 0; i < n; i++) await plugin.ToSignal(Tree, SceneTree.SignalName.ProcessFrame); }
    void Say(string m) { _log.Add(m); GD.Print("SM1LIB " + m); try { File.WriteAllLines(Path.Combine(_out, "sm1lib-result.txt"), _log); } catch (Exception) { } }
    void Check(bool ok, string what) { if (!ok) _failed = true; Say((ok ? "ok   " : "FAIL ") + what); }

    async Task<bool> Until(Func<bool> done, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done()) { if (DateTime.UtcNow > until) return false; await Frames(5); }
        return true;
    }

    async Task Shot(string name)
    {
        await Frames(15);
        if (!Thumbnails.CanRender) { Say($"shot {name}: headless, skipped"); return; }
        Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(_out, name + ".png"));
        Say($"shot {name}.png");
    }

    public async Task Run()
    {
        try { await RunCore(); }
        catch (Exception e) { _failed = true; Say("EXCEPTION " + e); }
        Say(_failed ? "RESULT FAIL" : "RESULT PASS");
        GD.Print(_failed ? "SM1LIB RESULT FAIL" : "SM1LIB RESULT PASS");
        Tree.Quit(_failed ? 1 : 0);
    }

    static bool Near(Num.Vector3 a, Num.Vector3 b, float tol = 0.06f) => (a - b).Length() <= tol;

    async Task RunCore()
    {
        _out = Arg("--mm-out", Path.Combine(Path.GetTempPath(), "mm-sm1lib-" + System.Environment.ProcessId));
        Directory.CreateDirectory(_out);
        MapHost.Library = new MapLibrary(Arg("--mm-lib", Path.Combine(Path.GetTempPath(), "mm-sm1lib-lib-" + System.Environment.ProcessId)));
        await Frames(30);
        if (!plugin.EnsureSceneRoot()) { await Frames(60); plugin.EnsureSceneRoot(); }
        if (Sm1Source.GameDir() is null) { Say("no game install: skipped"); return; }
        plugin.NewMap("sm1lib");
        await Frames(10);

        // ---- 1. the library lists every map's definitions with no map opened ----
        Check(MapHost.Sm1 is null && MapHost.LoadedMaps.Count() == 0, "no SM1 map is open");
        dock.ShowTab((int)DockTab.Library);
        dock.TestShowSource(0);
        var start = DateTime.UtcNow;
        bool indexed = await Until(() => Sm1Catalog.Progress is var p && p.Total > 0 && p.Done == p.Total, 900);
        var (done, total) = Sm1Catalog.Progress;
        Say($"index: {done} of {total} maps in {(DateTime.UtcNow - start).TotalSeconds:0} s");
        Check(indexed && total >= 12, $"every Classic PvP map is indexed ({done}/{total})");
        foreach (var m in Sm1Catalog.Maps) Say($"  {m.Name} '{m.DisplayName}': {(Sm1Catalog.Get(m.Name) is { } e ? e.Definitions.Count + " definitions, " + e.Empty + " empty" : "FAILED " + Sm1Catalog.FailureOf(m.Name))}");
        await Frames(30);
        dock.TestShowSource(0);
        var (rows, headers, entries) = dock.TestFilterStats();
        Check(rows > 1000 && headers == Sm1Catalog.Maps.Count(m => Sm1Catalog.Get(m.Name) is not null) && entries == total, $"All maps: {rows} rows under {headers} map headings, {entries} filter entries");
        Check(MapHost.Sm1 is null, "listing opened no map");
        await Shot("01-library-all-maps");

        // ---- 2. the dropdown: one map, and the popup open ----
        string map = "mp_tdm_09";
        dock.TestFilterMap(map);
        await Frames(20);
        var one = dock.TestFilterStats();
        Check(one.Headers == 0 && one.Rows == Sm1Catalog.Get(map)!.Definitions.Count, $"{map} only: {one.Rows} rows, no headings");
        bool loaded = await Until(() => MapHost.Sm1For(map) is not null, 300);
        Check(loaded && MapHost.Sm1 is null, "the chosen map loaded in the background for previews without becoming the backdrop");
        await Until(() => dock.LibraryStats() is var st && st.Item2 >= Math.Min(st.Item1, 40), 120);
        await Shot("02-library-one-map");
        dock.TestOpenFilterPopup();
        await Frames(10);
        await Shot("03-library-dropdown");
        dock.TestCloseFilterPopup();
        await Frames(5);
        dock.TestFilterMap("");

        // ---- 3. placing from a map that was never opened ----
        string other = Sm1Catalog.Maps.First(m => m.Name != map && Sm1Catalog.Get(m.Name) is { Definitions.Count: > 0 }).Name;
        string def = Sm1Catalog.Get(other)!.Definitions[0];
        Check(MapHost.Sm1For(other) is null, $"{other} is not loaded");
        var put = plugin.PlaceAt(new LibraryItem(LibraryKind.Sm1, def, def, other), new Godot.Vector3(0, 0, 0));
        Check(put.Ok && plugin.Session!.Document.Objects.Any(o => o.Asset is { IsSm1: true } a && a.Map == other && a.Definition == def), $"{def} from {other} placed without opening {other}");
        Check(await Until(() => MapHost.Sm1For(other) is not null, 300), $"{other} was loaded in the background to draw it");
        Check(MapHost.Sm1 is null, "and it did not become the backdrop");

        // ---- 4. remove the backdrop ----
        plugin.NewMap("backdrop");
        await Frames(10);
        var placed = plugin.PlaceAt(new LibraryItem(LibraryKind.Sm1, def, def, other), new Godot.Vector3(5, 0, 0));
        string before = plugin.Session!.Document.ToJson();
        dock.OpenSm1(map, true);
        Check(await Until(() => MapHost.Root?.Backdrop is not null && MapHost.Sm1 is not null, 300), $"{map} opened as the backdrop");
        await Shot("04-backdrop-open");
        plugin.RemoveBackdrop();
        await Frames(10);
        Check(MapHost.Root!.Backdrop is null && MapHost.Sm1 is null, "Remove SM1 backdrop frees the backdrop and forgets the pick map");
        Check(plugin.Session!.Document.ToJson() == before, "the map's objects are untouched");
        Check(MapHost.Sm1For(map) is not null, "the SM1 map stays in memory (placed pieces keep drawing)");
        await Shot("05-backdrop-removed");
        var history = plugin.History.GetHistoryUndoRedo(plugin.History.GetObjectHistoryId(MapHost.Root!));
        history.Undo(); await Frames(30);
        Check(MapHost.Root!.Backdrop is not null && MapHost.Sm1?.Map == map, "Undo brings the backdrop back");
        history.Redo(); await Frames(10);
        Check(MapHost.Root!.Backdrop is null && MapHost.Sm1 is null, "Redo removes it again");
        Check(plugin.Session!.Document.ToJson() == before, "the objects are still untouched");

        // ---- 5. import with the original's spawns and gates ----
        plugin.NewMap("Remix");
        await Frames(10);
        var r = await plugin.ImportSm1ForEditing(map);
        Check(r is { Ok: true }, "import ran");
        foreach (var w in r?.Warnings ?? []) Say("  warning: " + w);
        var doc = plugin.Session!.Document;
        var areas = doc.Objects.Where(o => o.Piece?.Kind == PieceKind.SpawnArea).ToList();
        var doors = doc.Objects.Where(o => o.Piece?.Kind == PieceKind.Blocker).ToList();
        foreach (var o in areas.Concat(doors)) Say($"  {o.Name}: pos {o.Transform.Position}, rot {o.Transform.Rotation}, size {o.Transform.Scale}");
        Check(areas.Count == 2 && areas.All(a => !a.Name.Contains("placeholder")), "one real spawn area per side (no placeholder)");
        Check(doors.Count == 6, $"six spawn doors (three per side): {doors.Count}");
        // The install's markers as docs/discovery.md records them (centre, metres): marines (245, -937.4, 5.5), Chaos (154.5, -664.4, 5.55).
        var marines = areas.Single(a => a.Piece!.Side == PieceSpec.Marines);
        var chaos = areas.Single(a => a.Piece!.Side == PieceSpec.Chaos);
        Check(Near(marines.Transform.Position + new Num.Vector3(0, 0, marines.Transform.Scale.Z / 2), new Num.Vector3(245f, -937.4f, 5.5f)), "marine spawn area at the recorded marker");
        Check(Near(chaos.Transform.Position + new Num.Vector3(0, 0, chaos.Transform.Scale.Z / 2), new Num.Vector3(154.5f, -664.4f, 5.55f)), "Chaos spawn area at the recorded marker");
        plugin.RunCheck(withRoute: false);
        await Frames(10);
        var report = plugin.LastCheck;
        Say("map check: " + (report?.Headline ?? "none"));
        foreach (var i in report?.Issues ?? []) Say($"  [{i.Severity}] {i.Message}");
        Check(report is { AnnihilationReady: true }, "the imported map passes the check for Annihilation");
        var pts = doc.Objects.Where(o => o.Piece?.Kind == PieceKind.CapturePoint).ToList();
        foreach (var o in pts) Say($"  {o.Name}: pos {o.Transform.Position}, size {o.Transform.Scale}");
        Check(pts.Count >= 1 && pts.All(p => !p.Name.Contains("placeholder")), $"capture points from the original ({pts.Count})");
        Check(report is { SeizeReady: true }, "the imported map passes the check for Seize Ground");

        await plugin.Frame3D(new Num.Vector3(180, -960, 0), new Num.Vector3(275, -870, 25));
        await Frames(60);
        await Shot("06-import-marine-spawn");
        await plugin.Frame3D(new Num.Vector3(125, -730, 0), new Num.Vector3(220, -640, 25));
        await Frames(60);
        await Shot("07-import-chaos-spawn");
        dock.ShowTab((int)DockTab.Test);
        await Shot("08-import-check");
    }
}
