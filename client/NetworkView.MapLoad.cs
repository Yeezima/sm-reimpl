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
        public long ContentMs, WorldMs, SceneMs, WorkerMs;
        public required System.Diagnostics.Stopwatch Watch;
    }

    /// <summary>The job <see cref="GameClient"/> polls: the worker's task, then one frame for the caption, then the main thread's stage.</summary>
    sealed class MapLoadJob(NetworkView view, Task<MapStage1> task) : IMapLoad
    {
        bool _armed;

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
            if (!_armed)
            {
                // One frame with the caption drawn before the main thread's stage holds the frame.
                _armed = true;
                view.SetLoadingCaption("DRAWING THE MAP");
                return false;
            }
            loaded = view.Stage2(task.Result);
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

    /// <summary>The whole load on the calling thread (bots, replays, <c>--sync-map-load</c>): the two stages one after the other.</summary>
    LoadedMap? LoadMap(string name) => Stage2(Stage1(name, onMainThread: true));

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
        var stage = new MapStage1 { Name = name, File = file, Map = map, World = world, Hash = hash, Generated = generated is not null, ContentMs = contentMs, WorldMs = worldMs, Watch = watch };
        if (generated is null && !Lean)
        {
            // The map's look: the original meshes and textures read here (MapScene.Load is engine-free); MapView.Build draws them on the main thread in Stage2.
            SetLoadingCaption("READING THE MAP'S MESHES AND TEXTURES");
            try { stage.Scene = MapScene.Load(_gameDir, file); }
            catch (Exception e) when (e is not (StackOverflowException or AccessViolationException)) { stage.SceneError = $"{e.GetType().Name}: {e.Message}"; }
        }
        stage.SceneMs = watch.ElapsedMilliseconds;
        stage.WorkerMs = stage.SceneMs;
        return stage;
    }

    /// <summary>
    /// Builds what needs Godot (main thread): the ragdoll world, the navigation's start, the map view with its lights (or added content's drawn map), the collision debug mesh, the
    /// blockers and props, the looks and the warm-ups; prints the map note and the LOADTIME line; returns the loaded map (collision and hash) for the handshake.
    /// </summary>
    LoadedMap Stage2(MapStage1 s)
    {
        var watch = s.Watch;
        string name = s.Name;
        var map = s.Map; var world = s.World; ulong hash = s.Hash;
        Ph("map load: collision and content");
        long stage2Start = watch.ElapsedMilliseconds;
        if (!Lean) BuildRagdollWorld(map);
        long ragdollMs = watch.ElapsedMilliseconds;
        PrepareNavigation(map, world); MemStep("navigation");
        _mapCentre = map.Spawns.Count > 0 ? new Vector2(map.Spawns.Average(sp => sp.Position.X), map.Spawns.Average(sp => sp.Position.Y)) : null;
        long collisionMs = watch.ElapsedMilliseconds;
        if (Lean) return LeanMapLoaded(name, hash, world, collisionMs, watch);   // perf/lean-bots: collision, navigation and the jump ground only (NetworkView.Lean.cs)
        _gameSpace?.GetNodeOrNull("MapCollision")?.QueueFree();
        _gameSpace?.GetNodeOrNull("MapVisuals")?.QueueFree();

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
        long visualsEndMs = watch.ElapsedMilliseconds;
        // The default look of every class and the own look, built now so no player's first look stalls a frame (customizer T7).
        Ph("map load: looks");
        _warmClock = System.Diagnostics.Stopwatch.StartNew();   // Ork O7a: one deadline for the marines' and the Ork's warm-up
        WarmLooks();
        WarmOrk();   // Ork O7a: an Ork player's own look and weapons (NetworkView.Ork.cs)
        PrewarmWeaponDefaults();   // M40-T9: the weapon models of every class's default loadout
        PrewarmGrenades();   // feel/grenades-quickdraw: the grenade models, so the first throw does not stall a frame
        PrewarmPickups();   // perf/pass-1: the drops' models (the first drop cost a 100 ms frame); a lean bot returned above (LeanMapLoaded) and warms none
        PrewarmCollision(world);   // M40-T9: the crosshair's first ray
        _jumpGround = world;   // M45: the flight pose's height above the ground
        // polish pass P1 (2026-11-10): one line with the load's stages, for testers' logs and the baseline harness (every number is wall time, ms; P2: the worker's stages apart from the main thread's).
        long totalMs = watch.ElapsedMilliseconds;
        GD.Print($"LOADTIME map {name}: worker {s.WorkerMs} ms (content {s.ContentMs}, collision world {s.WorldMs - s.ContentMs}, scene {s.SceneMs - s.WorldMs}), main thread {totalMs - stage2Start} ms (ragdoll world {ragdollMs - stage2Start}, navigation {collisionMs - ragdollMs}, visuals {visualsEndMs - collisionMs}, looks and warm-ups {totalMs - visualsEndMs}), total {totalMs} ms{(SyncMapLoad || _bot ? " (synchronous)" : "")}");
        return new LoadedMap(world, hash);
    }
}
