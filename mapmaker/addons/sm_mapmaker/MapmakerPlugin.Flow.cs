using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmMapmaker;
using SmSim;
using Num = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>One thing the Build tab places: a piece kind with a side, a size and a plain label (the same kind can have several presets: floor, wall, cover).</summary>
public sealed record PiecePreset(string Id, string Label, string Kind, string? Side, Num.Vector3 Size, bool TopAtClick, string Name, string Tip, string? Shape = null)
{
    public static readonly IReadOnlyList<PiecePreset> All =
    [
        new("floor", "Floor", PieceKind.Box, null, new(40, 40, 1), true, "Floor", "A flat slab to stand on, 40 x 40 m. Its top surface lands where you click. Resize it with the Object tab's Size."),
        new("wall", "Wall", PieceKind.Box, null, new(8, 1, 4), false, "Wall", "A solid wall, 8 m long and 4 m tall. Turn it with the Object tab's rotation, or stretch it with Size."),
        new("cover", "Cover block", PieceKind.Box, null, new(3, 3, 1.5f), false, "Cover", "Waist-high cover, 3 x 3 x 1.5 m. Click on a floor to stand it there; click on a block to stack."),
        new("ramp", "Ramp", PieceKind.Ramp, null, new(4, 8, 2), false, "Ramp", PieceKind.All.First(k => k.Id == PieceKind.Ramp).Tip),
        new("column", "Column", PieceKind.Column, null, new(1.5f, 1.5f, 4), false, "Pillar", PieceKind.All.First(k => k.Id == PieceKind.Column).Tip),
        new("invisible", "Invisible wall", PieceKind.CollisionBox, null, new(4, 4, 3), false, "Invisible wall", PieceKind.All.First(k => k.Id == PieceKind.CollisionBox).Tip),
        new("move-wall", "Invisible wall (players only)", PieceKind.MoveWall, null, new(4, 1, 3), false, "Invisible wall (players only)", PieceKind.All.First(k => k.Id == PieceKind.MoveWall).Tip),
        new("classic-cover", "Cover (shoot-through)", PieceKind.Cover, null, new(1.2f, 3f, 1.5f), false, "Cover (shoot-through)", PieceKind.All.First(k => k.Id == PieceKind.Cover).Tip),
        new("spawn-marines", "Marine spawn", PieceKind.SpawnArea, PieceSpec.Marines, new(12, 16, 3), false, "Marine spawn area", "Where the Space Marines appear: a 12 x 16 m box on the floor, facing its green arrow. Every map needs one for each side."),
        new("spawn-chaos", "Chaos spawn", PieceKind.SpawnArea, PieceSpec.Chaos, new(12, 16, 3), false, "Chaos spawn area", "Where Chaos appears: a 12 x 16 m box on the floor, facing its green arrow. Every map needs one for each side."),
        new("door-marines", "Marine door", PieceKind.Blocker, PieceSpec.Marines, new(12, 1, 6), false, "Marine spawn door", "A shield across the exit of the Marine spawn room. Chaos cannot cross it; Marines can."),
        new("door-chaos", "Chaos door", PieceKind.Blocker, PieceSpec.Chaos, new(12, 1, 6), false, "Chaos spawn door", "A shield across the exit of the Chaos spawn room. Marines cannot cross it; Chaos can."),
        new("point", "Capture point", PieceKind.CapturePoint, null, new(8, 8, 4), false, "Capture point", PieceKind.All.First(k => k.Id == PieceKind.CapturePoint).Tip),
        new("kill", "Kill zone", PieceKind.KillZone, null, new(20, 20, 4), false, "Kill zone", PieceKind.All.First(k => k.Id == PieceKind.KillZone).Tip),
    ];

    public static PiecePreset? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}

/// <summary>
/// The end-to-end flow (docs/mapmaker.md "How to make a map in 2 minutes"): a new map from a layout, placing pieces, the live check, the one-button play test and sharing.
/// Everything here goes through the core (<see cref="MapTemplate"/>, <see cref="CustomMap"/>, <see cref="MapCheck"/>) and the same operations as every other edit.
/// </summary>
public partial class MapmakerPlugin
{
    // ---- compiled view of the open map -------------------------------------------------------------------------------------------------------------------

    CustomMap? _compiled;
    string _compiledJson = "";

    bool _compiledPiecesOnly;
    DecorWorker? _decorWorker;

    /// <summary>The decoration of the open map is being compiled in the background: <see cref="Compiled"/> holds the pieces only until it lands.</summary>
    public bool DecorPending => _compiledPiecesOnly || _decorWorker?.Pending == true;

    /// <summary>
    /// The open map compiled for play (cached until the document changes); null when no map is open. The SM1 pieces, prefabs and models are never compiled on the main thread unless
    /// <paramref name="wait"/>: a miss compiles the pieces at once and asks the <see cref="DecorWorker"/> for the decoration (debounced, latest edit wins), and the check runs when it lands.
    /// Pass <paramref name="wait"/> where the full result is needed now (tests, saving a hash).
    /// </summary>
    public CustomMap? Compiled(bool wait = false)
    {
        if (Session is null) return null;
        string json = Session.Document.ToJson();
        string key = json + "\n" + EditorScenes.StateKey();   // a map loaded later brings its SM1 pieces into the collision
        if (_compiled is not null && key == _compiledJson && !(wait && _compiledPiecesOnly)) return _compiled;
        var doc = Session.Document.Clone();
        var options = EditorScenes.Options();
        if (wait || !CustomMaps.NeedsContent(doc) || EditorScenes.Cache.TryGet(DecorCache.KeyOf(doc, options.CacheTag), out _))
        {
            _compiled = CustomMap.Compile(doc, options);
            _compiledPiecesOnly = false;
        }
        else
        {
            _compiled = CustomMap.Compile(doc);   // pieces only: fast
            _compiledPiecesOnly = true;
            _decorWorker ??= new DecorWorker(EditorScenes.Cache, TimeSpan.FromMilliseconds(400), a => Callable.From(a).CallDeferred(), OnDecorReady);
            _decorWorker.Request(doc.Clone(), options, DecorCache.KeyOf(doc, options.CacheTag));
        }
        _compiledJson = key;
        return _compiled;
    }

    void OnDecorReady()
    {
        if (!GodotObject.IsInstanceValid(this) || Session is null) return;   // the plugin was closed or the map was closed while the build ran
        _compiledJson = "";   // the next Compiled() finds the decoration in the cache
        bool route = _routeWanted; _routeWanted = false;
        RunCheck(route);
    }

    /// <summary>Where a ray first meets a solid piece (document frame), or null.</summary>
    Vector3? SolidHit(Vector3 origin, Vector3 direction)
    {
        if (Compiled() is not { SolidCount: > 0 } map) return null;
        var hit = map.ToCollisionWorld().Character.Raycast(ToNum(origin), ToNum(direction), 2000f);
        return hit is { } h ? new Vector3(h.Point.X, h.Point.Y, h.Point.Z) : null;
    }

    // ---- placing pieces ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Which way a new spawn area or door should face: toward the middle of the ground (snapped to the angle step), so it usually points into the map.</summary>
    float DefaultYaw(Vector3 at)
    {
        if (Compiled()?.SolidBounds is not { } b) return 0f;
        var mid = (b.Min + b.Max) / 2f;
        float yaw = MathF.Atan2(-(mid.X - at.X), mid.Y - at.Y) * 180f / MathF.PI;
        if (Num.Vector2.Distance(new Num.Vector2(mid.X, mid.Y), new Num.Vector2(at.X, at.Y)) < 1f) return 0f;
        return MapHost.SnapAngle ? Snap.Angle(yaw, MathF.Max(MapHost.AngleStep, 15f)) : yaw;
    }

    OpResult PlacePiece(LibraryItem item, Vector3 game)
    {
        var preset = PiecePreset.Find(item.Id);
        if (preset is null) return OpResult.Fail("unknown-piece", $"No piece '{item.Id}'.");
        var pos = ToNum(Frame.Round(game));
        if (preset.TopAtClick) pos.Z -= preset.Size.Z;
        float yaw = preset.Kind is PieceKind.SpawnArea or PieceKind.Blocker ? DefaultYaw(game) : 0f;
        var piece = new JsonObject { ["kind"] = preset.Kind };
        if (preset.Side is not null) piece["side"] = preset.Side;
        if (preset.Shape is not null) piece["shape"] = preset.Shape;
        var op = Op("place", ("name", preset.Name), ("piece", piece), ("transform", new JsonObject { ["position"] = Vec(pos), ["rotation"] = Vec(new Num.Vector3(0, 0, yaw)), ["scale"] = Vec(preset.Size) }));
        var r = Do($"Place {preset.Label}", op);
        if (r.Ok && r.Created.Count > 0) Select(r.Created);
        return r;
    }

    // ---- the live check -----------------------------------------------------------------------------------------------------------------------------------

    public CheckReport? LastCheck { get; private set; }
    /// <summary>The bots' route is being checked in the background.</summary>
    public bool RouteChecking { get; private set; }
    public event Action? CheckChanged;
    string _checkedJson = "";
    bool _checkQueued;
    bool _routeInFlight;   // a Check route run is building the walking graph in the background
    bool _routeWanted;     // Check route was pressed while the decoration was still compiling: it runs when the decoration lands
    string _routeJson = "";   // the document the last finished route check verified
    static SmSim.SimConfig? _config;
    static bool _configTried;

    /// <summary>Re-runs the check shortly after the last edit (it is skipped while the document is unchanged).</summary>
    public void RecheckSoon()
    {
        if (_checkQueued || Session is null) return;
        if (Session.Document.ToJson() == _checkedJson && LastCheck is not null) return;
        _checkQueued = true;
        GetTree().CreateTimer(0.5).Timeout += () => { _checkQueued = false; RunCheck(); };
    }

    /// <summary>
    /// The check now: the quick part (spawns, floor, doors, points) at once. The bots' route on the real walking graph runs only when asked
    /// (<paramref name="withRoute"/>: the Check route button, owner 2026-11-21): building the graph takes every core for seconds, and running it after every
    /// edit pinned testers' CPUs at 99 % as soon as both spawn areas stood on floor.
    /// </summary>
    public void RunCheck(bool withRoute = false)
    {
        if (Session is null) { LastCheck = null; CheckChanged?.Invoke(); return; }
        if (withRoute && _routeInFlight) { SetStatus("The bots' route is already being checked."); return; }   // one route check at a time
        string json = Session.Document.ToJson();
        // a route verified for exactly this document stays shown (a quick re-check of an unchanged map does not throw it away)
        if (!withRoute && json == _routeJson && LastCheck is not null && !_routeInFlight) { _checkedJson = json; CheckChanged?.Invoke(); return; }
        _checkedJson = json;
        var map = Compiled()!;
        if (_compiledPiecesOnly)
        {
            // the decoration is still compiling: the check would judge a map without its SM1 pieces; it runs when they land (with the route, if that was asked)
            _routeWanted |= withRoute;
            RouteChecking = true;
            CheckChanged?.Invoke();
            return;
        }
        RouteChecking = _routeInFlight;
        LastCheck = MapCheck.Run(map);
        bool ready = LastCheck.Ready(CheckMode.Annihilation) || LastCheck.Ready(CheckMode.Seize);
        bool startRoute = withRoute && ready;
        if (!startRoute)
        {
            LastCheck.Facts.RemoveAll(f => f.StartsWith("Bot route:", StringComparison.Ordinal));
            LastCheck.Facts.Add(ready ? "Bot route: not checked since the last edit. Press Check route to see whether bots can walk between the spawns and to every point."
                                      : "Bot route: can be checked once the map has a spawn area with floor for each side.");
        }
        CheckChanged?.Invoke();
        if (!startRoute) return;
        RouteChecking = true;
        _routeInFlight = true;
        CheckChanged?.Invoke();
        var routeOptions = EditorScenes.Options();   // taken on the main thread: a snapshot of the loaded maps
        _ = Task.Run(() =>
        {
            CheckReport? route = null;
            try
            {
                if (!_configTried) { _configTried = true; try { _config = GameplayTuning.Load(GameInstall.Locate()).Config; } catch (Exception e) when (e is IOException or DirectoryNotFoundException) { } }
                var copy = CustomMap.Compile(MapDocument.Parse(json), routeOptions);
                route = MapCheck.Run(copy, _config);
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { GD.PushWarning("Mapmaker: route check: " + e.Message); }
            finally
            {
                // always hand the result back, so a failed run never leaves Check route stuck
                Callable.From(() =>
                {
                    _routeInFlight = false;
                    RouteChecking = false;
                    if (route is not null && Session is not null && Session.Document.ToJson() == json) { LastCheck = route; _routeJson = json; }
                    CheckChanged?.Invoke();
                }).CallDeferred();
            }
        });
    }

    /// <summary>One-click repair for a check issue's <see cref="Issue.Action"/>: places what is missing, where it makes sense.</summary>
    public OpResult? Fix(Issue issue)
    {
        if (Session is null || issue.Action is null) return null;
        var map = Compiled()!;
        var b = map.SolidBounds ?? new Bounds(new Num.Vector3(-30, -20, -1), new Num.Vector3(30, 20, 0));
        var size = b.Max - b.Min;
        var mid = (b.Min + b.Max) / 2f;
        float top = map.SolidBounds is null ? 0f : b.Max.Z;
        // The floor under a spot: the first walkable surface found by a ray from above, else 0.
        float GroundAt(float x, float y)
        {
            if (map.SolidCount == 0) return 0f;
            return map.ToCollisionWorld().Character.Raycast(new Num.Vector3(x, y, top + 50f), -Num.Vector3.UnitZ, 400f) is { } hit ? hit.Point.Z : 0f;
        }
        switch (issue.Action)
        {            case "add-floor":
                return PlacePieceAt("floor", new Num.Vector3(0, 0, 0));
            case "add-spawn-marines": return PlacePieceAt("spawn-marines", new Num.Vector3(b.Min.X + 8f, mid.Y, GroundAt(b.Min.X + 8f, mid.Y)));
            case "add-spawn-chaos": return PlacePieceAt("spawn-chaos", new Num.Vector3(b.Max.X - 8f, mid.Y, GroundAt(b.Max.X - 8f, mid.Y)));
            case "add-door-marines": return PlaceDoor(map, Team.SpaceMarines, "door-marines");
            case "add-door-chaos": return PlaceDoor(map, Team.Chaos, "door-chaos");
            case "add-capture-point": return PlacePieceAt("point", new Num.Vector3(mid.X, mid.Y, GroundAt(mid.X, mid.Y)));
        }
        return null;
    }

    OpResult PlacePieceAt(string preset, Num.Vector3 at) => PlacePiece(new LibraryItem(LibraryKind.Piece, preset, preset), new Vector3(at.X, at.Y, at.Z));

    OpResult PlaceDoor(CustomMap map, Team side, string preset)
    {
        // In front of the spawn area, at its far edge in the direction it faces.
        var area = map.Content.StartAreas.FirstOrDefault(a => a.Side == side);
        if (area is null) return OpResult.Fail("no-spawn", "Add the spawn area first.");
        var forward = new Num.Vector2(-MathF.Sin(area.Yaw), MathF.Cos(area.Yaw));
        var at = new Num.Vector3(area.Position.X + forward.X * (area.Scale.Y / 2f), area.Position.Y + forward.Y * (area.Scale.Y / 2f), area.Position.Z - area.Scale.Z / 2f);
        var p = PiecePreset.Find(preset)!;
        float yawDeg = area.Yaw * 180f / MathF.PI;
        var piece = new JsonObject { ["kind"] = p.Kind, ["side"] = p.Side };
        var r = Do($"Add {p.Label}", Op("place", ("name", p.Name), ("piece", piece), ("transform", new JsonObject { ["position"] = Vec(at), ["rotation"] = Vec(new Num.Vector3(0, 0, yawDeg)), ["scale"] = Vec(new Num.Vector3(area.Scale.X, 1, 6)) })));
        if (r.Ok && r.Created.Count > 0) Select(r.Created);
        return r;
    }

    // ---- a new map from a layout --------------------------------------------------------------------------------------------------------------------------

    /// <summary>A file name in the library's maps folder for a map called <paramref name="name"/>, never one that exists.</summary>
    public string NewMapPath(string name)
    {
        string slug = string.Concat(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')).Trim('-');
        if (slug.Length == 0) slug = "map";
        Directory.CreateDirectory(MapHost.Library.MapsDir);
        string path = Path.Combine(MapHost.Library.MapsDir, slug + ".smmap");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(MapHost.Library.MapsDir, $"{slug}-{i}.smmap");
        return path;
    }

    /// <summary>New map named <paramref name="name"/> from a layout (<see cref="MapTemplate"/>), saved at once, the camera on it. One undo step removes the layout.</summary>
    public OpResult NewFromTemplate(string name, string templateId)
    {
        var template = MapTemplate.Find(templateId);
        if (template is null) return OpResult.Fail("unknown-template", $"No layout '{templateId}'.");
        name = name.Trim().Length == 0 ? "My Map" : name.Trim();
        NewMap(name);
        if (MapHost.Root is { } root) root.BackdropVisible = false;
        var r = Do($"New map from \"{template.Label}\"", template.Ops().ToArray());
        if (!r.Ok) return r;
        SaveMap(NewMapPath(name));
        if (Compiled()?.SolidBounds is { } b) _ = Frame3D(b.Min, b.Max);
        SetStatus($"New map \"{name}\" from the {template.Label} layout, saved as {Path.GetFileName(Session!.Path!)}. Check it, then press Play test.");
        RunCheck();
        return r;
    }

    // ---- play test ----------------------------------------------------------------------------------------------------------------------------------------

    int _playPid;
    bool _playFromInstall;
    public string PlayStatus { get; private set; } = "";
    public event Action? PlayStatusChanged;
    public bool PlayRunning => _playPid != 0 && OS.IsProcessRunning(_playPid);

    static string PlayDir => Path.Combine(GameInstall.DataRoot, "tmp-build", "mapmaker-playtest");
    static string PlayStatusFile => Path.Combine(PlayDir, "status.txt");

    /// <summary>The checkout the plugin runs from (the folder that has <c>tools/mapmaker-playtest.ps1</c>), or null.</summary>
    public static string? RepoRoot()
    {
        string dir = ProjectSettings.GlobalizePath("res://").TrimEnd('/', '\\');
        for (int i = 0; i < 4 && dir.Length > 0; i++)
        {
            if (File.Exists(Path.Combine(dir, "tools", "mapmaker-playtest.ps1"))) return dir;
            dir = Path.GetDirectoryName(dir) ?? "";
        }
        return null;
    }

    /// <summary>
    /// The tester install's play test script (<c>playtest.ps1</c>, shipped in <c>mapmaker\</c> and copied by the launcher into the project the editor runs from, <c>&lt;install&gt;\mapmaker-project\</c>),
    /// or null when this project has none. It runs the prebuilt server and game of the active version (found from SM_MAPMAKER_VERSION_DIR, which the launcher sets, or the install's current.json).
    /// </summary>
    public static string? InstallScript()
    {
        string dir = ProjectSettings.GlobalizePath("res://").TrimEnd('/', '\\');
        string script = Path.Combine(dir, "playtest.ps1");
        return File.Exists(script) ? script : null;
    }

    /// <summary>The script a play test runs and the arguments before its own: the checkout's tools script, else the install's. Null script when neither exists.</summary>
    static (string? Script, string[] Lead) PlayScript()
    {
        if (RepoRoot() is { } root) return (Path.Combine(root, "tools", "mapmaker-playtest.ps1"), ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"]);
        // the tester's play test is a hidden PowerShell: the only window is the game's
        return (InstallScript(), ["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File"]);
    }

    /// <summary>
    /// The Play test button (after its file dialog): checks the saved map file <paramref name="mapFile"/> and starts <c>tools/mapmaker-playtest.ps1</c> (server on 127.0.0.1, bots, the game) with exactly
    /// that file: the server, the bots and the game all load it by path. Unsaved edits in the editor are never played (owner, 2026-11-18): save with Save map first. Returns null when it started, else the reason.
    /// <paramref name="extra"/> adds script arguments (the self test's <c>-Test -OutDir ...</c>).
    /// </summary>
    public string? PlayTest(string mapFile, string mode, int bots, string side, string[]? extra = null)
    {
        if (PlayRunning) return Fail("A play test is already running. Close the game window (or press Stop) first.");
        if (!File.Exists(mapFile)) return Fail($"The map file '{mapFile}' does not exist.");
        var (script, lead) = PlayScript();
        if (script is null) return Fail("Play test is not available here: the Mapmaker project has no playtest.ps1. Open the Mapmaker from the launcher (the MAPMAKER button) of the newest build.");
        bool fromInstall = RepoRoot() is null;
        _playFromInstall = fromInstall;
        string path = Path.GetFullPath(mapFile);
        CustomMap played;
        try { played = CustomMaps.Load(path); }
        catch (MapException e) { return Fail($"The file {Path.GetFileName(path)} cannot be played: {e.Message}"); }
        var report = MapCheck.Run(played);
        var need = mode == "seize" ? CheckMode.Seize : CheckMode.Annihilation;
        if (!report.Ready(need))
        {
            var first = report.Errors.Where(e => e.Mode is CheckMode.Both || e.Mode == need).Select(e => e.Message).FirstOrDefault();
            return Fail($"{Path.GetFileName(path)} is not playable in {(mode == "seize" ? "Seize Ground" : "Annihilation")} yet: {first} (fix it in the editor, save with Save map, and pick the file again).");
        }
        string unsaved = Session is not null && Dirty && Session.Path is { } open && string.Equals(Path.GetFullPath(open), path, StringComparison.OrdinalIgnoreCase)
            ? " (the editor has unsaved changes; they are not in this file)" : "";
        try { Directory.CreateDirectory(PlayDir); File.WriteAllText(PlayStatusFile, "starting|Starting the play test..."); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        // The script checks the server's log against this hash: a server that loaded another map (or the default arena) stops the play test with a plain reason.
        var args = new List<string>(lead) { script, "-Map", path, "-Mode", mode, "-Bots", bots.ToString(), "-Side", side, "-ExpectHash", played.Hash.ToString("X16") };
        if (extra is not null) args.AddRange(extra);
        var sameName = CustomMaps.SameNameAs(path);
        if (sameName.Count > 0) unsaved += $" ({sameName.Count} other saved file{(sameName.Count == 1 ? " has" : "s have")} the same map name \"{played.Document.Name}\"; this exact file is played, but rename the map to tell them apart)";
        _playPid = OS.CreateProcess("powershell.exe", args.ToArray(), false);
        if (_playPid <= 0) { _playPid = 0; return Fail("Could not start PowerShell for the play test."); }
        string head = $"Playing the saved file {Path.GetFileName(path)}{unsaved}. ";
        SetPlayStatus(head + (fromInstall ? "Starting the play test: the server and the bots, then the game..." : "Starting the play test: building, then the server and the bots (the first time takes about half a minute)..."));
        PollPlayStatus();
        return null;

        string Fail(string why) { SetStatus(why); SetPlayStatus(why); return why; }
    }

    void SetPlayStatus(string text) { PlayStatus = text; PlayStatusChanged?.Invoke(); }

    async void PollPlayStatus()
    {
        int pid = _playPid;
        while (pid != 0 && pid == _playPid && OS.IsProcessRunning(pid))
        {
            await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
            try
            {
                if (File.Exists(PlayStatusFile))
                {
                    var line = File.ReadAllText(PlayStatusFile).Trim();
                    int bar = line.IndexOf('|');
                    SetPlayStatus(bar >= 0 ? line[(bar + 1)..] : line);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (pid == _playPid)
        {
            _playPid = 0;
            string last = "";
            try { if (File.Exists(PlayStatusFile)) last = File.ReadAllText(PlayStatusFile).Trim(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            int bar = last.IndexOf('|');
            if (last.StartsWith("failed")) SetPlayStatus("Play test failed: " + (bar >= 0 ? last[(bar + 1)..] : last));
            else if (_playFromInstall && !last.StartsWith("done"))
            {
                // the installed script writes "done|..." as its last act: anything else means PowerShell never ran it (execution policy, a parse error) or it died early
                string why = "Play test failed: the play test script did not run to its end (PowerShell was blocked or stopped; last state: " + (last.Length == 0 ? "none" : last) + "). Check the execution policy or run playtest.ps1 from the mapmaker-project folder in the install.";
                GD.PushWarning("Mapmaker: " + why);
                SetPlayStatus(why);
            }
            else SetPlayStatus("Play test over (the server and the bots are stopped).");
        }
    }

    /// <summary>Stop: stops only the processes the last play test recorded.</summary>
    public void StopPlayTest()
    {
        var (script, lead) = PlayScript();
        if (script is null) return;
        OS.CreateProcess("powershell.exe", [.. lead, script, "-Stop"], false);
        SetPlayStatus("Stopping the play test...");
    }

    // ---- sharing ------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Saves the map if it has no file yet and returns the file (the whole map: send it to someone, who puts it in their maps folder).</summary>
    public string? ShareFile()
    {
        if (Session is null) { SetStatus("There is no map to share yet."); return null; }
        if (Session.Path is null || Dirty) { if (!SaveMap(Session.Path ?? NewMapPath(Session.Name))) return null; }
        return Session.Path;
    }
}

