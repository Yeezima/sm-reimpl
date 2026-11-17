using System.Linq;
using Godot;
using SmContent;
using SmSim;
using SmContent.Extras.Musou;
using SmContent.Extras.Warfront;
using SmContent.Extras.Warfront.Maps;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Maps;

namespace SmClient;

/// <summary>
/// A Warfront lane slot on the client (slice W2; plan section 2): one band, hub plus ONE battlefield. The client builds the slot's level from (profile, seed) through <see cref="WarfrontLayoutCache"/> (the host's generator, the same hash),
/// predicts on its collision as the server does, and draws it the way the front pilot draws a band: the avenue's openings in the boundary wall, the kit look, the storm look and the pilot's outside scenery
/// (<see cref="AshfallFrontScenery"/>: the 10-degree outside ground, the avenue running on, manufactorum stacks, the far cathedral spire). The neighbouring bands' massing copies are the same lane's adjacent slots, built or read through the layout cache. Client only: nothing
/// here changes the collision, the hash or the wire. The map-name hooks (<see cref="WarfrontMapContent"/>, <see cref="WarfrontMapVisuals"/>, <see cref="WarfrontBandFrame"/>) are called from the stage flow (W5); no Musou file is changed here.
/// </summary>
public partial class NetworkView
{
    AshfallLevel? _warfrontLevel;
    AshfallDressing? _warfrontDressing;
    Node3D? _warfrontBand;
    bool _warfrontShown = true;

    /// <summary>Test aid (ours): <c>SM_WF_SYNC_BUILD=1</c> runs the W5b path (every layout built or read on the main thread) to measure the stall before W5d; never set in play.</summary>
    const double NeighbourWaitSeconds = 30.0;
    static readonly bool OldSyncBuild = System.Environment.GetEnvironmentVariable("SM_WF_SYNC_BUILD") == "1";

    // The hooks W5 calls from the dispatchers (client/Extras/NetworkView.Extras.cs, Musou/Tfw/NetworkView.Tfw.cs): declared here as partial methods of this class, implemented below, so the
    // file compiles without callers and an unused hook costs nothing; a build without Warfront (-p:SmWarfront=false) leaves this folder out and nothing refers to it.
    // (declared in client/Extras/NetworkView.Extras.cs and client/Extras/Musou/Tfw/NetworkView.Tfw.cs with their call sites, W5b: a build without Warfront still compiles the calls)

    /// <summary>The map content of a lane slot's name (null for any other map): the generated band as collision, from the layout cache.</summary>
    partial void WarfrontMapContent(string name, ref MapContent? map)
    {
        if (name == WarfrontMaps.HqName) { WarfrontHqContent(ref map); return; }   // W13b (WarfrontHqClient.cs)
        if (WarfrontSlots.ByName(name) is not { } slot) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (NoAshfallCache) AshfallLayoutCache.Disabled = true;
        SetLoadingCaption("READING THE WARFRONT BATTLEFIELD");
        // the layout comes from the background warm-up (W5d: started when the Stage section named this map, shared with the server's and the prebuild's cache files); this thread only takes it
        var got = OldSyncBuild ? null : WarfrontLayoutWarm.TryGet(slot);
        string note = "taken from the background warm-up";
        if (OldSyncBuild) note = "SM_WF_SYNC_BUILD=1: the W5b path, built on this thread";
        else if (got is null)
        {
            got = WarfrontLayoutWarm.WaitFor(slot, NoAshfallCache, out long waited);
            note = $"the warm-up had not finished: waited {waited} ms for it (a cold cache, or a join that skipped the Preparing section; the map load runs on a worker, the loading screen keeps drawing)";
            GD.Print($"warfront: {note}");
        }
        if (got is null) { got = WarfrontLayoutCache.Build(slot, out string fallback); note = "warm-up failed; built here: " + fallback; }
        (_warfrontLevel, _warfrontDressing) = got.Value;
        if (!OldSyncBuild) WarfrontLayoutWarm.Warm(slot, NoAshfallCache);   // the neighbours, if the join skipped the Preparing section
        // this runs on the map load's worker (the loading screen keeps drawing; bots and --sync-map-load run it on the main thread and draw nothing): give the neighbours' copies up to 30 s, so the stage-2 drawing finds them
        if (!OldSyncBuild && OS.GetThreadCallerId() != OS.GetMainThreadId())
        {
            SetLoadingCaption("READING THE NEIGHBOURING BATTLEFIELDS");
            int ready = WarfrontLayoutWarm.WaitForNeighbours(slot, NeighbourWaitSeconds, out long neighbourWait);
            GD.Print($"warfront: {slot.Name} neighbours ready {ready} of {WarfrontLayoutWarm.Needed(slot).Count - 1} after {neighbourWait} ms on the load worker");
            SetLoadingCaption("READING THE WARFRONT BATTLEFIELD");
        }
        map = AshfallContent.ToMapContent(_warfrontLevel);
        GD.Print($"warfront: {slot.Name} {note}; content in {watch.ElapsedMilliseconds} ms, hash {_warfrontLevel.Hash:X16}; nothing read from the install");
    }

    /// <summary>The rest of the band's drawing, one step per frame (W5d): the band is hidden behind the travel screen and the ship while they run, and no frame carries more than the largest step.</summary>
    readonly System.Collections.Generic.Queue<(string Name, System.Action Run)> _warfrontSteps = new();

    /// <summary>One queued step, then the next after a frame (a tree timer: no dependence on the dispatcher's hooks, which an observer's view does not run).</summary>
    void WarfrontScheduleStep()
    {
        if (_warfrontSteps.Count == 0) return;
        if (!IsInsideTree()) { while (_warfrontSteps.Count > 0) _warfrontSteps.Dequeue().Run(); return; }   // no tree to time against: all now
        GetTree().CreateTimer(0.05).Timeout += () =>
        {
            if (_warfrontSteps.Count == 0) return;
            _warfrontSteps.Dequeue().Run();
            WarfrontScheduleStep();
        };
    }

    /// <summary>
    /// Draws a lane slot's band (graybox, kit look, outside scenery, storm) under one node at the origin; any failure leaves the grey collision. W5d: the pure work is already done (the layouts come from <see cref="WarfrontLayoutWarm"/>); the drawing itself
    /// is cut into four steps, the first now (the node and the graybox view, which hide the grey collision), the kit, the scenery and the storm one per frame after it (<see cref="WarfrontScheduleStep"/>), so no single frame carries all of it.
    /// <c>SM_WF_SYNC_BUILD=1</c> runs them all here, as before.
    /// </summary>
    partial void WarfrontMapVisuals(string name, ref bool drawn)
    {
        if (name == WarfrontMaps.HqName) { WarfrontHqVisuals(ref drawn); return; }   // W13b (WarfrontHqClient.cs)
        if (WarfrontSlots.ByName(name) is not { } slot || _warfrontLevel is null || _warfrontDressing is null || _gameSpace is null) return;
        try
        {
            var level = _warfrontLevel; var dressing = _warfrontDressing;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var split = new System.Text.StringBuilder();
            long mark = 0, longest = 0;
            void Lap(string what) { long now = watch.ElapsedMilliseconds; split.Append($"{(split.Length > 0 ? ", " : "")}{what} {now - mark} ms"); mark = now; }
            var node = new Node3D { Name = $"WarfrontBand{slot.Band}" };
            _gameSpace.AddChild(node);
            _warfrontBand = node;
            _warfrontSteps.Clear();
            var drawn1 = AshfallMap.OpenFrontEdges(level);   // drawing only: the avenue's openings in the boundary wall (the collision stays closed)
            AshfallView? view = null;
            var drawnNeighbours = new System.Collections.Generic.List<int>();
            string goal = "";
            void Step(string what, System.Action work)
            {
                long t0 = watch.ElapsedMilliseconds;
                try { if (IsInstanceValid(node)) work(); }
                catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException or System.OutOfMemoryException)) { GD.Print($"warfront: band step \"{what}\" failed ({e.GetType().Name}: {e.Message}); the band is drawn without it"); }
                longest = System.Math.Max(longest, watch.ElapsedMilliseconds - t0);
                Lap(what);
            }
            _warfrontSteps.Enqueue(("kit", () => Step("TfwFrontKit", () => TfwFrontKit(drawn1, dressing, view!, node))));
            _warfrontSteps.Enqueue(("scenery", () => Step("neighbours and AshfallFrontScenery.Build", () =>
            {
                // the scenery draws the neighbouring bands as massing copies: the same lane's adjacent slots (node - 1 south, node + 1 north); node 3 has none to the north (the spire and the stacks stand there)
                var empty = new AshfallDressing([], [], dressing.Version);
                var bands = new (AshfallLevel Level, AshfallDressing Dressing)[AshfallFront.BandCount];
                for (int k = 0; k < bands.Length; k++) bands[k] = (level, empty);
                bands[slot.Band] = (level, dressing);
                foreach (int k in new[] { slot.Band - 1, slot.Band + 1 })
                {
                    if (k < 0 || k >= AshfallFront.BandCount) continue;
                    // never generated here (W5d): a neighbour the warm-up has not finished is skipped, its copy missing for this landing
                    if (OldSyncBuild) { bands[k] = WarfrontLayoutCache.Build(WarfrontSlots.Of(slot.Lane, k + 1), out string neighbourNote); GD.Print($"warfront: SM_WF_SYNC_BUILD neighbour band {k} layout: {neighbourNote.Split(';')[0]}"); drawnNeighbours.Add(k); }
                    else if (WarfrontLayoutWarm.TryGet(WarfrontSlots.Of(slot.Lane, k + 1)) is { } neighbour) { bands[k] = neighbour; drawnNeighbours.Add(k); }
                    else GD.Print($"warfront: the neighbouring band {k} is not ready (the background warm-up is still on it); its copy is skipped, not waited for");
                }
                var scenery = AshfallFrontScenery.Build(slot.Band, bands);
                goal = WarfrontFarGoal.Apply(scenery, slot.Band);
                node.AddChild(scenery);
            })));
            _warfrontSteps.Enqueue(("storm", () =>
            {
                Step("look and storm", () =>
                {
                    if (_sun is null || _environment is null) return;
                    AshfallView.ApplyLook(_sun, _environment);
                    if (AshfallStorm.Off) return;
                    var host = new Node3D { Name = "WarfrontStorm" };
                    node.AddChild(host);
                    try { AshfallStorm.Attach(host, level, dressing, _sun, _environment); }
                    catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException or System.OutOfMemoryException)) { GD.Print($"warfront: the storm look failed ({e.GetType().Name}: {e.Message}); the plain look stays"); }
                });
                GD.Print($"warfront: stall split for {slot.Name}: {split}; largest step {longest} ms");
                float spireY = WarfrontFarGoal.SpireDistance(slot.Band);
                var points = level.Points.Select(c => $"{c.Name} {c.Center.X:F0},{c.Center.Y:F0} ({spireY - c.Center.Y:F0} m to the spire)");
                var landing = level.Spawns.Count > 0 ? $"{level.Spawns[0].X:F0},{level.Spawns[0].Y:F0} ({spireY - level.Spawns[0].Y:F0} m to the spire)" : "none";
                GD.Print($"warfront: {slot.Name} ({slot.Profile}): the {goal}; spire distances by node 1/2/3: {string.Join("/", Enumerable.Range(0, AshfallFront.BandCount).Select(b => WarfrontFarGoal.SpireDistance(b).ToString("F0")))} m; first spawn {landing}; control points {string.Join("; ", points)}");
                GD.Print($"warfront: {slot.Name} neighbour copies drawn: {(drawnNeighbours.Count == 0 ? "none" : string.Join(", ", drawnNeighbours.Select(k => "band " + k)))}");
                GD.Print($"warfront: {slot.Name} ({slot.Profile}, seed {slot.Seed}) drawn in {watch.ElapsedMilliseconds} ms of build time (outside scenery {AshfallFrontScenery.LastTriangles:N0} triangles, client only)");
            }));
            Step("open edges and AshfallView.Build", () => { view = AshfallView.Build(drawn1); node.AddChild(view); });
            drawn = true;
            if (OldSyncBuild) while (_warfrontSteps.Count > 0) _warfrontSteps.Dequeue().Run();
            else WarfrontScheduleStep();
        }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException))
        {
            GD.Print($"warfront: the band view failed ({e.GetType().Name}: {e.Message}); the grey collision is drawn instead");
        }
    }

    /// <summary>
    /// Once a frame on a lane slot: the band is drawn only while the player (and the camera) stand in its square, as the front pilot does (<see cref="FrontBandShown"/>): its outside ground reaches 1.2 km and would bury the hub (beyond the
    /// level's south edge) and the range.
    /// </summary>
    partial void WarfrontBandFrame(PlayerState me)
    {
        if (_warfrontBand is null || !IsInstanceValid(_warfrontBand)) return;
        int shown = FrontBandShown(_client?.Predicted.Position ?? me.Position, _camera is null ? null : new System.Numerics.Vector3(_camera.GlobalPosition.X, -_camera.GlobalPosition.Z, _camera.GlobalPosition.Y));
        bool show = shown >= 0;
        if (show == _warfrontShown) return;
        _warfrontBand.Visible = show;
        _warfrontShown = show;
        GD.Print(show ? "warfront: band shown" : "warfront: band hidden (the Stormbird, the Firing Range or the flight)");
    }
}
