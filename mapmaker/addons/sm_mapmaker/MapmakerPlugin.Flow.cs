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
public sealed record PiecePreset(string Id, string Label, string Kind, string? Side, Num.Vector3 Size, bool TopAtClick, string Name, string Tip)
{
    public static readonly IReadOnlyList<PiecePreset> All =
    [
        new("floor", "Floor", PieceKind.Box, null, new(40, 40, 1), true, "Floor", "A flat slab to stand on, 40 x 40 m. Its top surface lands where you click. Resize it with the Object tab's Size."),
        new("wall", "Wall", PieceKind.Box, null, new(8, 1, 4), false, "Wall", "A solid wall, 8 m long and 4 m tall. Turn it with the Object tab's rotation, or stretch it with Size."),
        new("cover", "Cover block", PieceKind.Box, null, new(3, 3, 1.5f), false, "Cover", "Waist-high cover, 3 x 3 x 1.5 m. Click on a floor to stand it there; click on a block to stack."),
        new("ramp", "Ramp", PieceKind.Ramp, null, new(4, 8, 2), false, "Ramp", PieceKind.All.First(k => k.Id == PieceKind.Ramp).Tip),
        new("column", "Column", PieceKind.Column, null, new(1.5f, 1.5f, 4), false, "Pillar", PieceKind.All.First(k => k.Id == PieceKind.Column).Tip),
        new("invisible", "Invisible wall", PieceKind.CollisionBox, null, new(4, 4, 3), false, "Invisible wall", PieceKind.All.First(k => k.Id == PieceKind.CollisionBox).Tip),
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

    /// <summary>The open map compiled for play (cached until the document changes); null when no map is open.</summary>
    public CustomMap? Compiled()
    {
        if (Session is null) return null;
        string json = Session.Document.ToJson();
        if (_compiled is null || json != _compiledJson) { _compiled = CustomMap.Compile(Session.Document.Clone()); _compiledJson = json; }
        return _compiled;
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

    /// <summary>The check now: the quick part at once, then (in the background) the bots' route on the real walking graph.</summary>
    public void RunCheck(bool withRoute = true)
    {
        if (Session is null) { LastCheck = null; CheckChanged?.Invoke(); return; }
        string json = Session.Document.ToJson();
        _checkedJson = json;
        var map = Compiled()!;
        LastCheck = MapCheck.Run(map);
        CheckChanged?.Invoke();
        if (!withRoute || !LastCheck.Ready(CheckMode.Annihilation) && !LastCheck.Ready(CheckMode.Seize)) return;
        RouteChecking = true;
        CheckChanged?.Invoke();
        _ = Task.Run(() =>
        {
            CheckReport? route = null;
            try
            {
                if (!_configTried) { _configTried = true; try { _config = GameplayTuning.Load(GameInstall.Locate()).Config; } catch (Exception e) when (e is IOException or DirectoryNotFoundException) { } }
                var copy = CustomMap.Compile(MapDocument.Parse(json));
                route = MapCheck.Run(copy, _config);
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { GD.PushWarning("Mapmaker: route check: " + e.Message); }
            Callable.From(() =>
            {
                RouteChecking = false;
                if (route is not null && Session is not null && Session.Document.ToJson() == json) LastCheck = route;
                CheckChanged?.Invoke();
            }).CallDeferred();
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
        // Ground level: the floor under the middle, else 0.
        float ground = 0f;
        if (map.SolidCount > 0 && map.ToCollisionWorld().Character.Raycast(new Num.Vector3(mid.X, mid.Y, top + 50f), -Num.Vector3.UnitZ, 400f) is { } hit) ground = hit.Point.Z;
        switch (issue.Action)
        {
            case "add-floor":
                return PlacePieceAt("floor", new Num.Vector3(0, 0, 0));
            case "add-spawn-marines": return PlacePieceAt("spawn-marines", new Num.Vector3(b.Min.X + 8f, mid.Y, ground));
            case "add-spawn-chaos": return PlacePieceAt("spawn-chaos", new Num.Vector3(b.Max.X - 8f, mid.Y, ground));
            case "add-door-marines": return PlaceDoor(map, Team.SpaceMarines, "door-marines");
            case "add-door-chaos": return PlaceDoor(map, Team.Chaos, "door-chaos");
            case "add-capture-point": return PlacePieceAt("point", new Num.Vector3(mid.X, mid.Y, ground));
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
    /// The Play test button: saves the map, checks it, and starts <c>tools/mapmaker-playtest.ps1</c> (server on 127.0.0.1, bots, the game). Returns null when it started, else the reason.
    /// <paramref name="extra"/> adds script arguments (the self test's <c>-Test -OutDir ...</c>).
    /// </summary>
    public string? PlayTest(string mode, int bots, string side, string[]? extra = null)
    {
        if (Session is null) return Fail("There is no map to play yet. Press New map first.");
        if (PlayRunning) return Fail("A play test is already running. Close the game window (or press Stop) first.");
        string? root = RepoRoot();
        if (root is null) return Fail("Play test needs the sm-reimpl checkout (tools/mapmaker-playtest.ps1 was not found next to the mapmaker folder).");
        var report = MapCheck.Run(Compiled()!);
        var need = mode == "seize" ? CheckMode.Seize : CheckMode.Annihilation;
        if (!report.Ready(need))
        {
            RunCheck(false);
            Dock?.ShowTab(DockTab.Start);
            var first = report.Errors.Where(e => e.Mode is CheckMode.Both || e.Mode == need).Select(e => e.Message).FirstOrDefault();
            return Fail($"Not playable in {(mode == "seize" ? "Seize Ground" : "Annihilation")} yet: {first} (the Check list shows every item, with a Fix button).");
        }
        string path = Session.Path ?? NewMapPath(Session.Name);
        if (Session.Path is null || Dirty) { if (!SaveMap(path)) return Fail("The map could not be saved (see the message below)."); }
        path = Session.Path!;
        try { Directory.CreateDirectory(PlayDir); File.WriteAllText(PlayStatusFile, "starting|Starting the play test..."); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "tools", "mapmaker-playtest.ps1"), "-Map", path, "-Mode", mode, "-Bots", bots.ToString(), "-Side", side };
        if (extra is not null) args.AddRange(extra);
        _playPid = OS.CreateProcess("powershell.exe", args.ToArray(), false);
        if (_playPid <= 0) { _playPid = 0; return Fail("Could not start PowerShell for the play test."); }
        SetPlayStatus("Starting the play test: building, then the server and the bots (the first time takes about half a minute)...");
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
            SetPlayStatus(last.StartsWith("failed") ? "Play test failed: " + (bar >= 0 ? last[(bar + 1)..] : last) : "Play test over (the server and the bots are stopped).");
        }
    }

    /// <summary>Stop: stops only the processes the last play test recorded.</summary>
    public void StopPlayTest()
    {
        string? root = RepoRoot();
        if (root is null) return;
        OS.CreateProcess("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "tools", "mapmaker-playtest.ps1"), "-Stop"], false);
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
