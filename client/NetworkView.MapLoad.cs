using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmNet;

namespace SmClient;

/// <summary>
/// Polish pass P2 (2026-11-10, ours; both modes, presentation only): the map load off the main thread. Before, <see cref="LoadMap"/> ran whole inside the handshake's frame
/// (3.5 s on Manufactorum, 28 s on Ashfall, measured), the loading screen a still picture and the server's timeout only bridged by the keep-alive. Now the part that
/// reads and computes (the install's map files, the collision world, added content's generated level, the map scene's meshes and textures: <see cref="Stage1"/>, nothing of
/// Godot's scene in it) runs on a worker while the loading screen draws its caption (<see cref="SetLoadingCaption"/>), and the part that builds Godot's nodes (collision bodies,
/// the map view, the lights, the looks and warm-ups: <see cref="Stage2"/>) runs on the main thread in one frame once the worker is done, after a frame that shows "DRAWING THE
/// MAP". <see cref="GameClient"/> polls the job each update (<see cref="IMapLoad"/>) and sends its Hello with the map hash once it is complete; its connect timeout waits meanwhile,
/// as it did for the synchronous load. Bots, demo replays and <c>--sync-map-load</c> keep the synchronous load (the same two stages, one after the other, on the main thread).
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--sync-map-load</c>: the map load whole on the main thread, as before P2 (the before / after switch).</summary>
    static readonly bool SyncMapLoad = CmdFlag("sync-map-load");

    volatile string _loadingCaption = "";

    /// <summary>The loading screen's caption (any thread; the loading screen reads it each frame).</summary>
    public void SetLoadingCaption(string text) => _loadingCaption = text;

    /// <summary>What the worker produces: everything the main thread's stage needs, and the stage's timings.</summary>
    sealed class MapStage1
    {
        public required string Name, File;
        public required MapContent Map;
        public required SmSim.CollisionWorld World;
        public required ulong Hash;
        public required bool Generated;
        public MapScene? Scene;
        public string? SceneError;
        public List<Vector3>? RagdollFaces;
        public long ContentMs, WorldMs, SceneMs, WorkerMs, VisualsDoneMs;
        public int WarmupStep;
        public bool Async;
        public MapAudio? Audio;
        public List<string>? AudioNotes;
        public required System.Diagnostics.Stopwatch Watch;
    }

    /// <summary>
    /// The job <see cref="GameClient"/> polls: the worker's task, then one frame for the caption, then the main thread's build of the map (one frame), then the looks and
    /// weapon warm-ups one a frame (each a few tens of ms), then the loaded map.
    /// </summary>
    sealed class MapLoadJob(NetworkView view, Task<MapStage1> task) : IMapLoad
    {
        bool _armed, _built;

        public bool Poll(out LoadedMap? loaded, out string? error)
        {
            loaded = null; error = null;
            if (!task.IsCompleted) return false;
            if (task.IsFaulted)
            {
                var e = task.Exception?.GetBaseException() ?? new Exception("the map load failed");
                error = $"{e.GetType().Name}: {e.Message}";
                return true;
            }
            var s = task.Result;
            if (!_armed)
            {
                // One frame with the caption drawn before the main thread's stage holds the frame.
                _armed = true;
                view.SetLoadingCaption("DRAWING THE MAP");
                return false;
            }
            if (!_built)
            {
                _built = true;
                if (view.Lean) { loaded = view.Stage2Visuals(s); return true; }
                view.LoadDraws3D(false);   // P3: no 3D drawn between the build and the last warm-up (see LoadDraws3D)
                try { view.Stage2Visuals(s); } catch { view.LoadDraws3D(true); throw; }
                view.SetLoadingCaption("PREPARING LOOKS AND WEAPONS");
                return false;
            }
            try { if (view.WarmupStep(s)) return false; }   // one warm-up a frame, then the shader sweep's frames
            catch { view.LoadDraws3D(true); throw; }
            view.LoadDraws3D(true);
            loaded = view.Finish(s);
            view.SetLoadingCaption("JOINING");
            return true;
        }
    }

    /// <summary>Starts the map load off the main thread; null for a bot, a replay or <c>--sync-map-load</c> (the client then calls <see cref="LoadMap"/>).</summary>
    IMapLoad? BeginLoadMap(string name)
    {
        if (_bot || SyncMapLoad || DemoPlayPath is not null) return null;
        SetLoadingCaption("READING THE MAP");
        return new MapLoadJob(this, Task.Run(() => Stage1(name, onMainThread: false)));
    }

    /// <summary>The whole load on the calling thread (bots, replays, <c>--sync-map-load</c>): the stages one after the other.</summary>
    LoadedMap? LoadMap(string name)
    {
        var s = Stage1(name, onMainThread: true);
        if (Stage2Visuals(s) is { } lean) return lean;
        while (WarmupStep(s)) { }
        return Finish(s);
    }

    /// <summary>
    /// Reads and computes (any thread; nothing of Godot's scene tree): the install's map content (or added content's generated level), the collision world (with added content's
    /// solids and hash), and for a drawn map the scene's meshes and textures. The extras hooks it calls write only fields the main thread reads after the task completes.
    /// </summary>
    MapStage1 Stage1(string name, bool onMainThread)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string file = MapFileOf(name);
        if (onMainThread) Ph("map load: collision and content");
        MemStep("before map"); MapContent? generated = null;
        SetLoadingCaption("READING THE MAP");
        ExtrasMapContent(name, ref generated);   // EXTRAS-HOOK: a map added content generates in code (Musou Mode's Ashfall Citadel): no install file is read
        var map = generated ?? MapContent.Load(_gameDir, file); MemStep("map content");
        long contentMs = watch.ElapsedMilliseconds;   // polish pass P1: the LOADTIME line's stages
        SetLoadingCaption("BUILDING COLLISION");
        var world = map.ToCollisionWorld(); MemStep("collision world");
        ulong hash = map.Hash;
        ExtrasMapCollision(name, ref world, ref hash);   // EXTRAS-HOOK: added content's solids beside the level, and the hash of the whole (Musou Mode's Stormbird)
        long worldMs = watch.ElapsedMilliseconds;
        var stage = new MapStage1 { Name = name, File = file, Map = map, World = world, Hash = hash, Generated = generated is not null, ContentMs = contentMs, WorldMs = worldMs, Watch = watch, Async = !onMainThread };
        if (!Lean) stage.RagdollFaces = RagdollWorldFaces(map);   // P2: the ragdoll world's triangles gathered here, its body and shape made on the main thread (P3: a shape made on this worker crashed the Compatibility renderer's load)
        if (!Lean && !NoSound && !MapAudioInPlay && file != "none")
        {
            // P3: the map's sound emitters and regions read here (engine-free), not in the first frame of play (measured: 145 ms of that frame, in every join with sound).
            // A failure is left to SetUpMapSound, which reads again and reports it as before.
            var notes = new List<string>();
            try { stage.Audio = MapAudio.Load(_gameDir, file, notes); stage.AudioNotes = notes; }
            catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or ArgumentException or NotSupportedException) { }
        }
        if (generated is null && !Lean && (onMainThread || !SceneOnMain))
        {
            // The map's look: the original meshes and textures read here (MapScene.Load is engine-free); MapView.Build draws them on the main thread in Stage2.
            SetLoadingCaption("READING THE MAP'S MESHES AND TEXTURES");
            LoadScene(stage, file);
        }
        stage.SceneMs = watch.ElapsedMilliseconds;
        stage.WorkerMs = stage.SceneMs;
        return stage;
    }

    long _stage2StartMs, _ragdollMs, _collisionMs;

    /// <summary>polish pass P3: <c>--map-audio-in-play</c> reads the map's sound emitters and regions in the first frame of play, as before P3.</summary>
    static readonly bool MapAudioInPlay = CmdFlag("map-audio-in-play");

    /// <summary>polish pass P3 (measurement aid): <c>--scene-on-main</c> reads the map scene in the main thread's stage instead of on the worker (to tell whether the worker's read is what holds a frame).</summary>
    static readonly bool SceneOnMain = CmdFlag("scene-on-main");

    void LoadScene(MapStage1 stage, string file)
    {
        try { stage.Scene = MapScene.Load(_gameDir, file); }
        catch (Exception e) when (e is not (StackOverflowException or AccessViolationException)) { stage.SceneError = $"{e.GetType().Name}: {e.Message}"; }
    }

    /// <summary>
    /// Builds what needs Godot (main thread, one frame): the ragdoll world's body, the navigation's start, added content's hub nodes, the map view with its lights (or added content's drawn
    /// map), the collision debug mesh, the blockers and props; prints the map note. Returns the loaded map at once for a lean bot (nothing more to build), else null: the warm-ups
    /// (<see cref="WarmupStep"/>) and <see cref="Finish"/> follow.
    /// </summary>
    LoadedMap? Stage2Visuals(MapStage1 s)
    {
        var watch = s.Watch;
        string name = s.Name;
        var map = s.Map; var world = s.World; ulong hash = s.Hash;
        Ph("map load: collision and content");
        _stage2StartMs = watch.ElapsedMilliseconds;
        _mapAudioAhead = s.Audio is null ? null : (s.File, s.Audio, s.AudioNotes ?? []);   // P3: for SetUpMapSound (NetworkView.Sound.cs)
        if (!Lean) BuildRagdollWorld(s.RagdollFaces ?? RagdollWorldFaces(map));
        _ragdollMs = watch.ElapsedMilliseconds;
        PrepareNavigation(map, world); MemStep("navigation");
        _mapCentre = map.Spawns.Count > 0 ? new Vector2(map.Spawns.Average(sp => sp.Position.X), map.Spawns.Average(sp => sp.Position.Y)) : null;
        long collisionMs = _collisionMs = watch.ElapsedMilliseconds;
        if (Lean) return LeanMapLoaded(name, hash, world, collisionMs, watch);   // perf/lean-bots: collision, navigation and the jump ground only (NetworkView.Lean.cs)
        _gameSpace?.GetNodeOrNull("MapCollision")?.QueueFree();
        _gameSpace?.GetNodeOrNull("MapVisuals")?.QueueFree();
        if (!s.Generated && s.Scene is null && s.SceneError is null && SceneOnMain) LoadScene(s, s.File);   // --scene-on-main (P3 measurement aid)
        ExtrasMapCollisionVisuals();   // EXTRAS-HOOK (P2): the nodes of what the collision hook composed on the worker (Musou Mode's Stormbird and Firing Range)

        // The map's look: original meshes and diffuse textures (MapView). Failure falls back to the grey collision.
        string? visualsError = s.SceneError;
        MapView? view = null;
        bool extrasDrawn = false;
        if (s.Generated) ExtrasMapVisuals(name, map, ref extrasDrawn);   // EXTRAS-HOOK: draws the generated map (and its look) instead of MapScene.Load
        string lightingNote = "lighting: provisional approximation (warm sun, fog, SSAO)";
        var visualNotes = new List<string>();
        if (!extrasDrawn && s.Scene is { } scene) try
        {
            Ph("map load: visuals");
            visualNotes.AddRange(scene.Notes);
            view = MapView.Build(scene, visualNotes);
            if (view.Instances == 0)
            {
                visualsError = "no part of the map could be drawn";
                view.Free();
                view = null;
            }
            else
            {
                _gameSpace?.AddChild(view);
                try
                {
                    if (_sun is not null && _environment is not null
                        && MapLightingView.Apply(scene.Lighting, _sun, _environment, visualNotes, out lightingNote, out _blender) is { } placed)
                        _gameSpace?.AddChild(placed);
                }
                catch (Exception e) when (e is not (StackOverflowException or AccessViolationException))
                {
                    // The map stays drawn with the provisional sun and fog; the half-built lights were freed by Apply.
                    _blender = null;
                    visualNotes.Add($"the map's lighting failed ({e.GetType().Name}: {e.Message}); the provisional look stays");
                }
            }
        }
        catch (Exception e) when (e is not (StackOverflowException or AccessViolationException))
        {
            visualsError = $"{e.GetType().Name}: {e.Message}";
            view = null;   // MapView.Build frees a half-built view itself
        }
        foreach (var note in visualNotes) GD.Print("  map note: " + note);

        if (((view is null && !extrasDrawn) || _showCollision) && map.Layer(MapContent.CharacterLayer) is { } layer && _gameSpace is not null)
        {
            var vertices = new Vector3[layer.Indices.Length];
            var normals = new Vector3[layer.Indices.Length];
            for (int t = 0; t < layer.Indices.Length; t += 3)
            {
                var a = layer.Vertices[layer.Indices[t]]; var b = layer.Vertices[layer.Indices[t + 1]]; var c = layer.Vertices[layer.Indices[t + 2]];
                var n = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(b - a, c - a));
                if (!float.IsFinite(n.X)) n = System.Numerics.Vector3.UnitZ;
                // Godot's front faces wind clockwise; the game's winding is not known, so both sides are drawn.
                vertices[t] = new(a.X, a.Y, a.Z); vertices[t + 1] = new(c.X, c.Y, c.Z); vertices[t + 2] = new(b.X, b.Y, b.Z);
                normals[t] = normals[t + 1] = normals[t + 2] = new(n.X, n.Y, n.Z);
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            var material = (StandardMaterial3D)_groundMaterial!.Duplicate();
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            _gameSpace.AddChild(new MeshInstance3D { Name = "MapCollision", Mesh = mesh, MaterialOverride = material });
        }
        if (GetNodeOrNull<Node3D>("Ground") is { } ground) ground.Visible = false;
        DrawBlockers(world);
        DrawProps(world);
        _mapNote = extrasDrawn ? $"map {name}: generated in code, drawn by added content (placeholder demonstration)"
            : view is not null
            ? $"map {name}: original meshes and textures from the install ({view.Objects:N0} objects, {view.Meshes} meshes, {view.TexturedSurfaces}/{view.Surfaces} surfaces textured, {view.Materials.NormalMapped} with normal maps, {view.Materials.Specular} with specular maps, {view.Materials.Emissive} emissive, {view.Materials.Layered} two-layer, {view.Materials.CutOut} cut-out, {view.Materials.Tinted} vertex-tinted); {lightingNote}, shaders: placeholder"
              + (_showCollision ? "; collision shown in grey" : "")
            : $"map {name}: collision geometry only, placeholder grey (visuals failed: {visualsError})";
        GD.Print($"{_mapNote}; collision {collisionMs} ms, total {watch.ElapsedMilliseconds} ms");
        if (VisualOverhaul.ApplyWorld(_camera, _environment) is { } overhaulNote) GD.Print(overhaulNote);   // visual-overhaul proposal (--visual-overhaul on; default off)
        s.VisualsDoneMs = watch.ElapsedMilliseconds;
        return null;
    }

    /// <summary>polish pass P3: <c>--no-shader-sweep</c> skips the sweep's frames (before P3 the camera looked at nothing during the load, so the map's shaders compiled at the first frames of play).</summary>
    static readonly bool NoShaderSweep = CmdFlag("no-shader-sweep");

    /// <summary>polish pass P3: <c>--load-draws-3d</c> lets the renderer draw the 3D world during the asynchronous load's build and warm-up frames, as P2 did (see <see cref="LoadDraws3D"/>).</summary>
    static readonly bool LoadDraws3DAlways = CmdFlag("load-draws-3d");

    /// <summary>
    /// polish pass P3 (a crash fix for P2's asynchronous load): while the map's nodes are built and the warm-ups make and free their models, one a frame, the viewport draws no 3D
    /// (the loading screen covers it anyway). Measured on the Compatibility renderer, Musou joins, the development PC: with 3D drawn in those frames the client crashed during the
    /// warm-up frames (signal 11 between frames) in 28 of 55 joins and with none drawn in 1 of 57 (still one: the cause inside the engine is not known, and running the warm-ups
    /// in the build's frame there did no better, 1 of 40); the synchronous load, which finishes no frame until the join, in 0 of 15; Forward+ in 0 of 12.
    /// <c>--load-draws-3d</c> restores P2's behaviour.
    /// </summary>
    void LoadDraws3D(bool on)
    {
        if (LoadDraws3DAlways) return;
        if (GetViewport() is { } viewport && viewport.Disable3D == on) viewport.Disable3D = !on;
    }

    const int WarmupSteps = 6, SweepSteps = 6;


    /// <summary>
    /// polish pass P3: after the warm-ups, still behind the loading screen, the camera stands over the map's centre and looks round, a sixth of a turn a frame for six frames, so the
    /// renderer compiles the map's materials' shaders and pipelines now (measured before: the Compatibility renderer held two frames of 350 ms at the first sight of each material
    /// after the join; Forward+ one of 58 ms). The join then places the camera as before.
    /// </summary>
    void ShaderSweepFrame(int step)
    {
        if (_camera is null || _gameSpace is null || _mapCentre3 is not { } centre) return;
        var eye = _gameSpace.Transform * new Vector3(centre.X, centre.Y, centre.Z + 14f);
        float yaw = step * Mathf.Tau / SweepSteps;
        var target = _gameSpace.Transform * new Vector3(centre.X + 40f * Mathf.Cos(yaw), centre.Y + 40f * Mathf.Sin(yaw), centre.Z + 2f);
        _camera.GlobalTransform = new Transform3D(Basis.LookingAt(target - eye, Vector3.Up), eye);
    }

    /// <summary>
    /// One warm-up a call (the default look of every class and the own look, the Ork's, the default loadouts' weapon models, the grenades, the pickups, the crosshair's first ray),
    /// so no player's first look or shot stalls a frame (customizer T7, M40-T9); the asynchronous load runs one a frame while the loading screen is up, then the shader sweep's
    /// frames with the 3D world drawn again; the synchronous load runs the warm-ups all at once and no sweep. True while steps remain.
    /// </summary>
    bool WarmupStep(MapStage1 s)
    {
        Ph("map load: looks");
        int step = s.WarmupStep++;
        switch (step)
        {
            case 0: _warmClock = System.Diagnostics.Stopwatch.StartNew(); WarmLooks(); return true;   // Ork O7a: one deadline for the marines' and the Ork's warm-up
            case 1: WarmOrk(); return true;   // Ork O7a: an Ork player's own look and weapons (NetworkView.Ork.cs)
            case 2: PrewarmWeaponDefaults(); return true;   // M40-T9: the weapon models of every class's default loadout
            case 3: PrewarmGrenades(); return true;   // feel/grenades-quickdraw: the grenade models, so the first throw does not stall a frame
            case 4: PrewarmPickups(); return true;   // perf/pass-1: the drops' models (the first drop cost a 100 ms frame); a lean bot never gets here (LeanMapLoaded) and warms none
            case 5: PrewarmCollision(s.World); return true;   // M40-T9: the crosshair's first ray
        }
        if (!s.Async || NoShaderSweep || step >= WarmupSteps + SweepSteps) return false;
        LoadDraws3D(true);   // the warm-ups are done: the sweep's frames draw the map
        ShaderSweepFrame(step - WarmupSteps);
        return true;
    }

    /// <summary>The LOADTIME line and the loaded map (collision and hash) for the handshake.</summary>
    LoadedMap Finish(MapStage1 s)
    {
        var watch = s.Watch;
        _jumpGround = s.World;   // M45: the flight pose's height above the ground
        // polish pass P1 (2026-11-10): one line with the load's stages, for testers' logs and the baseline harness (every number is wall time, ms; P2: the worker's stages apart from the main
        // thread's; asynchronous, the warm-ups span frames and their wall time includes the frames between).
        long totalMs = watch.ElapsedMilliseconds;
        GD.Print($"LOADTIME map {s.Name}: worker {s.WorkerMs} ms (content {s.ContentMs}, collision world {s.WorldMs - s.ContentMs}, scene {s.SceneMs - s.WorldMs}), main thread {s.VisualsDoneMs - _stage2StartMs} ms in one frame (ragdoll world {_ragdollMs - _stage2StartMs}, navigation {_collisionMs - _ragdollMs}, visuals {s.VisualsDoneMs - _collisionMs}), looks and warm-ups {totalMs - s.VisualsDoneMs} ms{(SyncMapLoad || _bot ? " in the same frame" : " over frames")}, total {totalMs} ms{(SyncMapLoad || _bot ? " (synchronous)" : "")}");
        return new LoadedMap(s.World, s.Hash);
    }
}
