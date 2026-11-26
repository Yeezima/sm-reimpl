using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using SmMapmaker;
using Num = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>
/// Test aid (agents, never the owner's editor): <c>godot.cmd --editor --path mapmaker -- --mm-perf --mm-lib &lt;dir&gt; --mm-out &lt;dir&gt; [--mm-perf-n 100,500,2000] [--mm-perf-sm1 mp_tdm_09]</c>
/// (set SM_MAPMAKER_CONTROL to a scratch folder). The edit-hang investigation and its regression check (2026-11-27): for maps of N synthetic blocks, and for an SM1 map imported for
/// editing, it times one edit by each trigger (a typed value, a gizmo release on two objects, undo, redo, an Edit mode move), including the frames after it (the deferred reconcile, the
/// check), prints the slowest phases and the view's work counters, and checks that the work per edit follows the edited objects, not the map: a one-object edit visits and writes at most a
/// few nodes and rebuilds no content, and every selected object stays selected after each transform, undo and redo. Prints <c>PERF RESULT PASS|FAIL</c>; perf-result.txt in --mm-out.
/// </summary>
public sealed class PerfTest(MapmakerPlugin plugin)
{
    public static bool Wanted => OS.GetCmdlineUserArgs().Contains("--mm-perf");
    /// <summary>--mm-perf-old: placing aims and the Check route button take their pre-2026-11-27 paths (a fresh collision world per aim, the check at once), for the before numbers.</summary>
    public static bool OldPaths => _oldPaths ??= Wanted && OS.GetCmdlineUserArgs().Contains("--mm-perf-old");   // read once (it is asked on every aim)
    static bool? _oldPaths;
    static string Arg(string name, string fallback) { var a = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }
    SceneTree Tree => (SceneTree)Engine.GetMainLoop();
    async Task Frames(int n) { for (int i = 0; i < n; i++) await plugin.ToSignal(Tree, SceneTree.SignalName.ProcessFrame); }
    bool _failed;
    int _passed;
    readonly List<string> _lines = new();
    void Log(string s) { GD.Print("PERF " + s); _lines.Add(s); }
    void Check(bool ok, string what) { Log($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) _failed = true; else _passed++; }

    /// <summary>Runs <paramref name="edit"/> and follows the editor for <paramref name="seconds"/>: the call's own time, the longest frame after it and the time spent in frames over 150 ms.</summary>
    async Task<(double Call, double MaxFrame, double Stall)> Time(string label, int n, Action edit, double seconds = 1.5)
    {
        await Frames(3);
        EditCost.Reset();
        ulong t0 = Time();
        edit();
        double call = (Time() - t0) / 1000.0;
        double max = 0, stall = 0;
        ulong last = Time(), end = last + (ulong)(seconds * 1e6);
        while (Time() < end)
        {
            await Frames(1);
            ulong now = Time();
            double gap = (now - last) / 1000.0;
            last = now;
            max = Math.Max(max, gap);
            if (gap > 150) stall += gap;   // an unfocused (headless) editor ticks about every 100 ms when idle
        }
        Log($"{label,-18} N={n,5}: call {call,8:0.0} ms, longest frame after {max,8:0.0} ms, stalled frames {stall,8:0.0} ms | {EditCost.Counters()} | {EditCost.Top(6)}");
        return (call, max, stall);
        static ulong Time() => Godot.Time.GetTicksUsec();
    }

    /// <summary>The Edit mode selection: every object in the edit set with its selected components.</summary>
    string CompSel() => string.Join(" ", plugin.EditIds.OrderBy(x => x, StringComparer.Ordinal).Select(id => $"{id}:[{string.Join(",", plugin.SelectedComponents(id))}]"));

    bool SameSelection(IEnumerable<string> want) => plugin.SelectedIds().OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(want.OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>What the 3D gizmo does on release: the nodes were dragged live, then one editor history action sets their global transforms (Godot's commit_transform).</summary>
    void GizmoRelease(IReadOnlyList<string> ids, Func<Transform3D, Transform3D> change, string name = "Translate")
    {
        var nodes = ids.Select(plugin.NodeOf).OfType<MapmakerObject>().ToList();
        var before = nodes.Select(n => n.GlobalTransform).ToList();
        foreach (var n in nodes) n.GlobalTransform = change(n.GlobalTransform);
        var ur = plugin.History;
        ur.CreateAction(name, UndoRedo.MergeMode.Disable, MapHost.Root);
        for (int i = 0; i < nodes.Count; i++)
        {
            ur.AddDoMethod(nodes[i], Node3D.MethodName.SetGlobalTransform, nodes[i].GlobalTransform);
            ur.AddUndoMethod(nodes[i], Node3D.MethodName.SetGlobalTransform, before[i]);
        }
        ur.CommitAction();
    }

    JsonObject Box(int i, int side)
    {
        float x = (i % side) * 3f, y = (i / side) * 3f;
        return MapmakerPlugin.Op("place", ("piece", new JsonObject { ["kind"] = PieceKind.Box }), ("name", "Block"),
            ("transform", new JsonObject { ["position"] = MapmakerPlugin.Vec(new Num.Vector3(x, y, 0)), ["scale"] = MapmakerPlugin.Vec(new Num.Vector3(2, 2, 2)) }));
    }

    /// <summary>Every trigger on the open map, with <paramref name="a"/> and <paramref name="b"/> the two objects edited.</summary>
    async Task Triggers(string label, int n, string a, string b, bool budget)
    {
        var doc = () => plugin.Session!.Document;
        var pair = new[] { a, b };
        plugin.Select(pair);
        await Frames(3);
        Check(SameSelection(pair), $"{label}: two objects selected before the edits");

        // A typed value (the Object tab's spin box; with two selected it moves both by the change to the first).
        float x0 = doc().Get(a).Transform.Position.X;
        var typed = await Time($"{label} typed", n, () => plugin.Dock!.TestTypeTransform(TransformProp.Position, 0, x0 + 1.5f));
        Check(MathF.Abs(doc().Get(a).Transform.Position.X - (x0 + 1.5f)) < 1e-3f, $"{label}: the typed value moved the first object");
        Check(SameSelection(pair), $"{label}: both objects stay selected after a typed move ({string.Join(",", plugin.SelectedIds())})");
        if (budget) Bounded($"{label} typed", 2);

        var gizmo = await Time($"{label} gizmo move", n, () => GizmoRelease(pair, t => t.Translated(new Vector3(2, 0, 0))));
        Check(SameSelection(pair), $"{label}: both objects stay selected after a gizmo move ({string.Join(",", plugin.SelectedIds())})");
        if (budget) Bounded($"{label} gizmo move", 2);

        await Time($"{label} gizmo rotate", n, () => GizmoRelease(pair, t => new Transform3D(t.Basis.Rotated(Vector3.Up, 0.5f), t.Origin), "Rotate"));
        Check(SameSelection(pair), $"{label}: both objects stay selected after a gizmo rotate");
        await Time($"{label} gizmo scale", n, () => GizmoRelease(pair, t => new Transform3D(t.Basis.Scaled(new Vector3(1.25f, 1.25f, 1.25f)), t.Origin), "Scale"));
        Check(SameSelection(pair), $"{label}: both objects stay selected after a gizmo scale");
        await Time($"{label} typed rotate", n, () => plugin.Dock!.TestTypeTransform(TransformProp.Rotation, 2, 30f));
        Check(SameSelection(pair), $"{label}: both objects stay selected after a typed rotation");
        await Time($"{label} typed scale", n, () => plugin.Dock!.TestTypeTransform(TransformProp.Scale, 0, 3f));
        Check(SameSelection(pair), $"{label}: both objects stay selected after a typed scale");

        var undo = await Time($"{label} undo", n, () => plugin.UndoLast());
        Check(SameSelection(pair), $"{label}: both objects stay selected after undo ({string.Join(",", plugin.SelectedIds())})");
        if (budget) Bounded($"{label} undo", 2, restore: true);
        var redo = await Time($"{label} redo", n, () => plugin.RedoLast());
        Check(SameSelection(pair), $"{label}: both objects stay selected after redo ({string.Join(",", plugin.SelectedIds())})");
        if (budget) Bounded($"{label} redo", 2, restore: true);
        _rows.Add((label, n, typed, gizmo, undo));
    }

    /// <summary>
    /// Placing on the map: the aim point under the mouse (what the placement preview asks every mouse move, and a click before it places) and a click that places a cover block.
    /// A ray straight down onto the middle of the SM1 map, so it meets the imported decoration.
    /// </summary>
    async Task Placing(string label, int n, string sm1)
    {
        if (MapHost.Sm1For(sm1)?.BoundsCore() is not { } b) { Log($"note: {label}: no SM1 bounds, placing not measured"); return; }
        var mid = new Num.Vector3((b.Min.X + b.Max.X) / 2, (b.Min.Y + b.Max.Y) / 2, b.Max.Z + 50f);
        var origin = Frame.ToGodot(new Vector3(mid.X, mid.Y, mid.Z));
        var down = Frame.ToGodot(new Vector3(0, 0, -1));
        Vector3 aim = default;
        var first = await Time($"{label} aim (first)", n, () => aim = plugin.GamePointOnRay(origin, down), 0.5);
        Log($"{label}: first aim point {aim} (the ground plane while the map's collision is still being built)");
        var built = System.Diagnostics.Stopwatch.StartNew();
        while (!OldPaths && !plugin.SolidWorldCurrent && built.Elapsed.TotalSeconds < 180) { plugin.GamePointOnRay(origin, down); await Frames(5); }
        Log($"{label}: the aim's collision world was ready after {built.Elapsed.TotalSeconds:0.0} s more (in the background)");
        var again = await Time($"{label} aim (again)", n, () => aim = plugin.GamePointOnRay(origin, down), 0.5);
        bool againBudget = EditCost.MainWorldBuilds == 0 && EditCost.MainCompiles == 0;
        Log($"{label}: the aim point {aim} (ray from z {mid.Z:0})");
        var item = new LibraryItem(LibraryKind.Piece, "cover", "Cover block");
        var click = await Time($"{label} click place", n, () => plugin.PlaceAt(item, plugin.GamePointOnRay(origin, down)), 1.0);
        bool clickBudget = EditCost.MainWorldBuilds == 0 && EditCost.FullSyncs == 0 && EditCost.NodesWritten <= 3 && EditCost.ContentRebuilt <= 1;
        string clickWork = EditCost.Counters();
        var after = await Time($"{label} aim after edit", n, () => aim = plugin.GamePointOnRay(origin, down), 0.5);
        bool afterBudget = EditCost.MainWorldBuilds == 0;
        _place.Add((label, n, first.Call, again.Call, click.Call, after.Call));
        // budgeted on work, not wall time (review of 919e4b4b: the click's own batch varies with the machine); the times are printed above
        if (budget)
        {
            Check(againBudget && afterBudget, $"{label}: aiming builds no collision world and compiles nothing on the main thread (world builds {EditCost.MainWorldBuilds})");
            Check(clickBudget, $"{label}: a placement click builds no collision world on the main thread and touches only the placed node ({clickWork})");
        }
        plugin.UndoLast();
        await Frames(3);

        // the world built in the background is the one the aim hits: find a spot where it stands above the ground, forget the world, aim (ground), let it be built, aim again
        if (!OldPaths)
        {
            while (!plugin.SolidWorldCurrent && built.Elapsed.TotalSeconds < 400) { plugin.GamePointOnRay(origin, down); await Frames(5); }
            Vector3? spotFrom = null; float high = 0;
            foreach (var o in plugin.Session!.Document.Objects.Where(o => o.Piece is not null && o.Visible).Take(400))
            {
                var p = o.Transform.Position;
                var from = Frame.ToGodot(new Vector3(p.X, p.Y, b.Max.Z + 50f));
                var hit = plugin.GamePointOnRay(from, down);
                if (hit.Z > 0.5f) { spotFrom = from; high = hit.Z; break; }
            }
            if (spotFrom is { } at)
            {
                plugin.TestForgetAimWorld();
                var ground = plugin.GamePointOnRay(at, down);
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (!plugin.SolidWorldCurrent && wait.Elapsed.TotalSeconds < 300) { plugin.GamePointOnRay(at, down); await Frames(5); }
                var landed = plugin.GamePointOnRay(at, down);
                Check(MathF.Abs(landed.Z - high) < 0.01f && MathF.Abs(ground.Z - landed.Z) > 0.4f,
                    $"{label}: the aim hits the background-built world once it lands (z {ground.Z:0.00} before, {landed.Z:0.00} after {wait.Elapsed.TotalSeconds:0.0} s; expected {high:0.00})");
            }
            else Check(false, $"{label}: found no spot where the map's collision stands above the ground");
        }
    }

    /// <summary>The Check route button: the quick check and the route run off the main thread (the button's call returns at once and the result lands later).</summary>
    async Task CheckButton(string label, int n)
    {
        var press = await Time($"{label} Check button", n, () => plugin.PressCheckRoute(), 0.5);
        Check(plugin.Checking, $"{label}: the Check button shows checking while the check runs (headline: {plugin.Dock!.TestCheckHeadline})");
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while ((plugin.Checking || plugin.RouteChecking) && waited.Elapsed.TotalSeconds < 300) await Frames(5);
        Log($"{label}: the check (and route) landed after {waited.Elapsed.TotalSeconds:0.0} s; headline: {plugin.LastCheck?.Headline}");
        Check(plugin.LastCheck is not null && !plugin.Checking, $"{label}: the Check button's result landed");
        _press = (label, n, press.Call, press.MaxFrame, waited.Elapsed.TotalSeconds);
        if (budget) Check(press.Call < 100 && press.MaxFrame < 500, $"{label}: the Check button returns at once ({press.Call:0} ms, longest frame {press.MaxFrame:0} ms)");
    }

    /// <summary>
    /// Gizmo releases whose record does not change (review of 919e4b4b): a move that snaps back to the stored value and a move the core refuses must still put the nodes back on
    /// their stored transform (the view skips unchanged records).
    /// </summary>
    async Task Reconcile()
    {
        plugin.NewMap("perf-reconcile");
        JsonObject At(float x) => MapmakerPlugin.Op("place", ("piece", new JsonObject { ["kind"] = PieceKind.Box }), ("name", "Box"),
            ("transform", new JsonObject { ["position"] = MapmakerPlugin.Vec(new Num.Vector3(x, 0, 0)), ["scale"] = MapmakerPlugin.Vec(new Num.Vector3(1, 1, 1)) }));
        var r = plugin.Do("Two boxes", At(0f), At(0.1f));
        string a = r.Created[0], b = r.Created[1];
        bool OnStored(string id) => plugin.NodeOf(id)!.Transform.IsEqualApprox(plugin.ExpectedTransform(plugin.Session!.Document.Get(id)));
        MapHost.SnapGrid = true; MapHost.GridStep = 0.25f;
        plugin.Select([a, b]);
        await Frames(2);
        GizmoRelease([a, b], t => t.Translated(Frame.ToGodot(new Vector3(0.1f, 0, 0))));
        await Frames(4);
        var doc = plugin.Session!.Document;
        Check(MathF.Abs(doc.Get(a).Transform.Position.X) < 1e-4f && MathF.Abs(doc.Get(b).Transform.Position.X - 0.25f) < 1e-4f, $"snapped gizmo move: {a} snaps back to 0, {b} to 0.25 ({doc.Get(a).Transform.Position.X}, {doc.Get(b).Transform.Position.X})");
        Check(OnStored(a) && OnStored(b), $"snapped gizmo move: both nodes stand on their stored transforms ({plugin.NodeOf(a)!.Position}, {plugin.NodeOf(b)!.Position})");
        MapHost.SnapGrid = false;
        GizmoRelease([a], t => new Transform3D(t.Basis.Scaled(new Vector3(0f, 1f, 1f)), t.Origin), "Scale");   // a scale with a zero component: the core refuses it (bad-transform)
        await Frames(4);
        Check(plugin.Status.StartsWith("Move rejected", StringComparison.Ordinal), $"a scale to zero is refused (status: {plugin.Status})");
        Check(OnStored(a), $"after a refused move the node is back on its stored transform ({plugin.NodeOf(a)!.Position})");
    }

    bool budget = true;
    readonly List<(string Label, int N, double First, double Again, double Click, double After)> _place = new();
    (string Label, int N, double Call, double Frame, double Landed)? _press;

    readonly List<(string Label, int N, (double, double, double) Typed, (double, double, double) Gizmo, (double, double, double) Undo)> _rows = new();

    /// <summary>
    /// The bounded-work rule: an edit of <paramref name="edited"/> objects visits and writes only those (with a small slack), rebuilds no drawing, makes no forced sync and does not
    /// rebuild the Scene lists. Undo and redo (<paramref name="restore"/>) read the whole document back from its text, so every object is compared (no Godot call for an unchanged one),
    /// but they too write only the changed nodes.
    /// </summary>
    void Bounded(string what, int edited, bool restore = false)
    {
        int slack = edited + 2;
        Check(EditCost.FullSyncs == 0 && (restore || EditCost.NodesVisited <= slack) && EditCost.NodesWritten <= slack && EditCost.ContentRebuilt == 0 && EditCost.SceneRows == 0,
            $"{what}: bounded view work ({EditCost.Counters()}; at most {slack} nodes written{(restore ? "" : " and visited")}, no rebuild, no forced sync, no Scene list rebuild)");
    }

    public async Task Run()
    {
        string outDir = Arg("--mm-out", Path.Combine(Path.GetTempPath(), "mm-perf-" + System.Environment.ProcessId));
        Directory.CreateDirectory(outDir);
        try
        {
            EditCost.Enabled = true;
            await Frames(30);
            for (int wait = 0; MapHost.Root is null; wait++) { plugin.EnsureSceneRoot(); await Frames(20); if (wait > 60) break; }
            await Frames(20);
            MapHost.Library = new MapLibrary(Arg("--mm-lib", Path.Combine(outDir, "lib")));
            budget = !OS.GetCmdlineUserArgs().Contains("--mm-perf-nobudget");

            // Snap starts off (owner 2026-11-27): every snap kind, every session.
            Check(!MapHost.SnapGrid && !MapHost.SnapAngle, $"snap starts off (grid {MapHost.SnapGrid}, angle {MapHost.SnapAngle})");
            Check(plugin.Dock!.TestSnapBoxes() is (false, false), "the Snap check boxes start unticked");
            // Godot's own Use Snap (review of 919e4b4b: the button was not found, so a pressed Use Snap stayed pressed): found, switched on, reset by opening a map, off
            Check(EditorSnap.BuiltInOn is not null, $"Godot's Use Snap button is found (state: {EditorSnap.BuiltInOn?.ToString() ?? "not found"})");
            EditorSnap.TestSwitchOnBuiltIn();
            Check(EditorSnap.BuiltInOn == true, "Godot's Use Snap can be switched on");
            plugin.NewMap("perf-snap");
            Check(EditorSnap.BuiltInOn == false, $"opening a map switches Godot's Use Snap off ({EditorSnap.BuiltInOn?.ToString() ?? "not found"})");
            plugin.Dock.TestSetSnap(true, false);
            await Frames(2);
            Check(MapHost.SnapGrid, "ticking Grid turns grid snap on");
            await plugin.TestPlaceInEmptyTabKeepsSnap();
            Check(MapHost.SnapGrid && plugin.Dock.TestSnapBoxes().Grid, "the untitled map a first placement starts keeps a Grid box the user just ticked");
            plugin.Dock.TestSetSnap(false, false);
            await Reconcile();

            foreach (var n in Arg("--mm-perf-n", "100,500,2000").Split(',').Select(int.Parse))
            {
                plugin.NewMap($"perf-{n}");
                Check(!MapHost.SnapGrid && !MapHost.SnapAngle, "a new map starts with snap off");
                int side = (int)Math.Ceiling(Math.Sqrt(n));
                var placed = plugin.Do($"Place {n}", Enumerable.Range(0, n).Select(i => Box(i, side)).ToArray());
                Check(placed.Ok && plugin.Session!.Document.Objects.Count == n, $"{n} blocks placed");
                await Frames(10);
                await Time("settle", n, () => { }, 2.0);
                await Triggers("blocks", n, placed.Created[0], placed.Created[1], budget);
            }

            // Edit mode: two blocks' faces moved by a typed G X 1 Enter (one undo step), then undo; the component selection is kept.
            {
                int n = 500;
                plugin.NewMap("perf-edit");
                int side = (int)Math.Ceiling(Math.Sqrt(n));
                var placed = plugin.Do($"Place {n}", Enumerable.Range(0, n).Select(i => Box(i, side)).ToArray());
                await Time("settle", n, () => { }, 2.0);
                string a = placed.Created[0], b = placed.Created[1];
                plugin.EditKeysAnywhere = true;
                plugin.Select([a, b]);
                await Frames(3);
                plugin.SetEditMode(true);
                plugin.SetComponentMode(ComponentMode.Face);
                await Frames(2);
                plugin.SelectComponent(a, 0); plugin.SelectComponent(b, 0, add: true);
                var selBefore = CompSel();
                await Time("edit-mode move", n, () =>
                {
                    plugin.TestInput(new InputEventKey { Keycode = Key.G, Pressed = true });
                    plugin.TestInput(new InputEventKey { Keycode = Key.X, Pressed = true });
                    plugin.TestInput(new InputEventKey { Keycode = Key.Key1, Pressed = true });
                    plugin.TestInput(new InputEventKey { Keycode = Key.Enter, Pressed = true });
                });
                Check(plugin.EditMode && CompSel() == selBefore, $"Edit mode keeps both objects and their faces selected after a move ({CompSel()})");
                if (budget) Check(EditCost.FullSyncs == 0 && EditCost.NodesVisited <= 4, $"edit-mode move: bounded view work ({EditCost.Counters()})");
                await Time("edit-mode undo", n, () => plugin.UndoLast());
                Check(plugin.EditMode && CompSel() == selBefore, $"Edit mode keeps the selection after undo ({CompSel()})");
                await Time("edit-mode redo", n, () => plugin.RedoLast());
                Check(plugin.EditMode && CompSel() == selBefore, $"Edit mode keeps the selection after redo ({CompSel()})");
                plugin.SetEditMode(false);
            }

            // An original map imported for editing (needs the install; skipped without it).
            string sm1 = Arg("--mm-perf-sm1", "");
            if (sm1.Length > 0)
            {
                plugin.NewMap("perf-sm1");
                var r = await plugin.ImportSm1ForEditing(sm1);
                if (r is { Ok: true })
                {
                    int n = plugin.Session!.Document.Objects.Count;
                    await Time("settle", n, () => { }, 4.0);
                    await Time("settle", n, () => { }, 2.0);
                    var drawn = plugin.Session.Document.Objects.Where(o => o.Asset is not null).Select(o => o.Id).Take(2).ToArray();
                    if (drawn.Length == 2) await Triggers("sm1 " + sm1, n, drawn[0], drawn[1], budget);
                    await Placing("sm1 " + sm1, n, sm1);
                    await CheckButton("sm1 " + sm1, n);
                }
                else Log($"note: SM1 import of {sm1} not run ({plugin.Status})");
            }

            Log("summary (call / longest frame after / stalled ms):");
            foreach (var (label, n, t, g, u) in _rows) Log($"  {label,-16} N={n,5}  typed {t.Item1:0}/{t.Item2:0}/{t.Item3:0}  gizmo {g.Item1:0}/{g.Item2:0}/{g.Item3:0}  undo {u.Item1:0}/{u.Item2:0}/{u.Item3:0}");
            foreach (var p in _place) Log($"  {p.Label,-16} N={p.N,5}  aim first {p.First:0} ms, aim again {p.Again:0} ms, click place {p.Click:0} ms, aim after the edit {p.After:0} ms");
            if (_press is { } c) Log($"  {c.Label,-16} N={c.N,5}  Check button call {c.Call:0} ms, longest frame {c.Frame:0} ms, result after {c.Landed:0.0} s");
        }
        catch (Exception e) { Log("exception " + e); _failed = true; }
        EditCost.Enabled = false;
        string result = _failed ? "PERF RESULT FAIL" : "PERF RESULT PASS";
        _lines.Add($"{_passed} checks passed");
        _lines.Add(result);
        try { File.WriteAllLines(Path.Combine(outDir, "perf-result.txt"), _lines); } catch (IOException) { }
        GD.Print(result);
        Tree.Quit(_failed ? 1 : 0);
    }
}
