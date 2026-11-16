using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SmContent;
using SmSim;

namespace SmMapmaker;

public enum Severity { Error, Warning, Info }

/// <summary>Which game mode an issue stops: both, or only one (a map with no capture points still plays Annihilation).</summary>
public enum CheckMode { Both, Annihilation, Seize }

/// <summary>
/// One finding of <see cref="MapCheck"/>, in plain words: <paramref name="Message"/> says what is wrong, <paramref name="Fix"/> what to do about it, and
/// <paramref name="Ids"/> the objects it is about (the editor selects them). <paramref name="Action"/> names a one-click repair the editor offers
/// (<c>add-spawn-marines</c>, <c>add-spawn-chaos</c>, <c>add-capture-point</c>, <c>add-door-marines</c>, <c>add-door-chaos</c>, <c>add-floor</c>), or is null.
/// </summary>
public sealed record Issue(Severity Severity, string Code, string Message, string? Fix = null, IReadOnlyList<string>? Ids = null, CheckMode Mode = CheckMode.Both, string? Action = null)
{
    public JsonObject ToNode()
    {
        var o = new JsonObject { ["severity"] = Severity.ToString().ToLowerInvariant(), ["code"] = Code, ["message"] = Message };
        if (Fix is not null) o["fix"] = Fix;
        if (Ids is { Count: > 0 }) o["ids"] = new JsonArray(Ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
        if (Mode != CheckMode.Both) o["mode"] = Mode.ToString().ToLowerInvariant();
        if (Action is not null) o["action"] = Action;
        return o;
    }
}

/// <summary>What <see cref="MapCheck.Run"/> found.</summary>
public sealed class CheckReport
{
    public List<Issue> Issues { get; } = [];
    /// <summary>Plain facts about the map: sizes and counts.</summary>
    public List<string> Facts { get; } = [];
    /// <summary>The game modes the map declares (any combination; names the checker does not know are kept for future modes).</summary>
    public List<string> Declared { get; init; } = [.. MapDocument.DefaultModes];
    public IEnumerable<Issue> Errors => Issues.Where(i => i.Severity == Severity.Error);
    /// <summary>The map is declared for <paramref name="mode"/> and nothing blocks it.</summary>
    public bool Ready(CheckMode mode) => Declared.Contains(mode == CheckMode.Seize ? "seize" : "annihilation") && !Issues.Any(i => i.Severity == Severity.Error && (i.Mode == CheckMode.Both || i.Mode == mode));
    public bool AnnihilationReady => Ready(CheckMode.Annihilation);
    public bool SeizeReady => Ready(CheckMode.Seize);

    /// <summary>The one line the editor shows above the list: what the map can be played as.</summary>
    public string Headline
    {
        get
        {
            var ready = new List<string>();
            if (AnnihilationReady) ready.Add("Annihilation");
            if (SeizeReady) ready.Add("Seize Ground");
            if (ready.Count == 0)
                return Errors.Any() ? $"Not playable yet: {Errors.Count()} thing{(Errors.Count() == 1 ? "" : "s")} to fix." : "No game mode is switched on for this map.";
            string text = $"Ready to play: {string.Join(" and ", ready)}.";
            if (Declared.Contains("annihilation") && !AnnihilationReady) text += " Annihilation is blocked (see below).";
            if (Declared.Contains("seize") && !SeizeReady) text += " Seize Ground needs a capture point.";
            return text;
        }
    }
    public JsonObject ToNode() => new()
    {
        ["annihilation"] = AnnihilationReady, ["seize"] = SeizeReady, ["headline"] = Headline,
        ["facts"] = new JsonArray(Facts.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
        ["issues"] = new JsonArray(Issues.Select(i => (JsonNode)i.ToNode()).ToArray()),
        ["modes"] = new JsonArray(Declared.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
    };

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Headline);
        foreach (var f in Facts) sb.AppendLine("  " + f);
        foreach (var i in Issues.OrderBy(i => i.Severity))
        {
            sb.AppendLine($"  [{i.Severity.ToString().ToUpperInvariant()}] {i.Message}");
            if (i.Fix is not null) sb.AppendLine($"      -> {i.Fix}");
        }
        return sb.ToString();
    }
}

/// <summary>
/// The instant check of a custom map, in plain words (docs/mapmaker.md "The check"): spawns for each side, capture points for Seize Ground, floor under
/// every spawn and point, doors, size, and (with a <see cref="SimConfig"/>) whether bots can walk from each spawn to the other side and to every point on
/// the graph they will really use. Needs no install unless a config is passed.
/// </summary>
public static class MapCheck
{
    const float CapsuleHeight = 1.9f;
    /// <summary>The spawn spots of an area (the simulation's own grid: 2 m apart, 1 m in from the edge; <c>World.StartAreaSpots</c>).</summary>
    public static List<Vector3> SpotsIn(MapStartArea area, StaticMesh character)
    {
        const float step = 2f, margin = 1f;
        var half = area.Scale / 2f;
        float halfHeight = MathF.Max(half.Z, 1f);
        int nu = (int)MathF.Floor(MathF.Max(0f, half.X - margin) / step), nv = (int)MathF.Floor(MathF.Max(0f, half.Y - margin) / step);
        float c = MathF.Cos(area.Yaw), s = MathF.Sin(area.Yaw);
        var spots = new List<Vector3>();
        for (int u = -nu; u <= nu; u++)
            for (int v = -nv; v <= nv; v++)
            {
                var local = new Vector2(u * step, v * step);
                float x = area.Position.X + local.X * c - local.Y * s, y = area.Position.Y + local.X * s + local.Y * c;
                if (Standable(character, x, y, area.Position.Z + halfHeight, area.Position.Z - halfHeight - 1f) is { } ground) spots.Add(ground);
            }
        return spots;
    }

    static Vector3? Standable(StaticMesh mesh, float x, float y, float top, float bottom)
    {
        Vector3? best = null;
        float z = top;
        for (int guard = 0; guard < 64 && z > bottom; guard++)
        {
            if (mesh.Raycast(new Vector3(x, y, z), -Vector3.UnitZ, z - bottom) is not { } hit) break;
            if (hit.Normal.Z >= 0.7f && mesh.Raycast(hit.Point + new Vector3(0, 0, 0.05f), Vector3.UnitZ, CapsuleHeight) is null) best = hit.Point;
            z = hit.Point.Z - 0.01f;
        }
        return best;
    }

    static string SideName(Team t) => t == Team.SpaceMarines ? "Space Marines" : "Chaos";
    static string Letter(int i) => ((char)('A' + i)).ToString();

    /// <summary>Checks <paramref name="map"/>. With <paramref name="config"/> the bots' route is checked too (builds, or reads from the cache, the walking graph).</summary>
    public static CheckReport Run(CustomMap map, SimConfig? config = null, string? navCache = null)
    {
        var r = new CheckReport { Declared = [.. map.Document.Modes] };
        var content = map.Content;
        var world = map.ToCollisionWorld();
        var mesh = world.Character;
        static IReadOnlyList<string> ids(IEnumerable<DrawPiece> ps) => ps.Select(p => p.Id).ToList();

        if (map.SolidCount == 0)
            r.Issues.Add(new Issue(Severity.Error, "no-ground", "The map has no solid ground. Players would fall straight through.", "Add a Block for the floor (Pieces > Block) or start from a template.", null, CheckMode.Both, "add-floor"));

        var marinesAreas = content.StartAreas.Where(a => a.Side == Team.SpaceMarines).ToList();
        var chaosAreas = content.StartAreas.Where(a => a.Side == Team.Chaos).ToList();
        var areaPieces = map.Pieces.Where(p => p.Piece.Kind == PieceKind.SpawnArea).ToList();
        if (marinesAreas.Count == 0)
            r.Issues.Add(new Issue(Severity.Error, "no-spawn-marines", "Space Marines have nowhere to spawn.", "Add a Marine spawn area on the floor.", null, CheckMode.Both, "add-spawn-marines"));
        if (chaosAreas.Count == 0)
            r.Issues.Add(new Issue(Severity.Error, "no-spawn-chaos", "Chaos has nowhere to spawn.", "Add a Chaos spawn area on the floor.", null, CheckMode.Both, "add-spawn-chaos"));

        var spotsBySide = new Dictionary<Team, List<Vector3>>();
        foreach (var a in content.StartAreas)
        {
            var piece = map.Pieces.FirstOrDefault(p => p.Piece.Kind == PieceKind.SpawnArea && (p.Piece.Side == PieceSpec.Marines) == (a.Side == Team.SpaceMarines)
                && MathF.Abs(p.World.M43 - (a.Position.Z - a.Scale.Z / 2f)) < 1e-2f && MathF.Abs(p.World.M41 - a.Position.X) < 1e-2f && MathF.Abs(p.World.M42 - a.Position.Y) < 1e-2f);
            var pid = piece is null ? null : new[] { piece.Id };
            var spots = SpotsIn(a, mesh);
            if (!spotsBySide.ContainsKey(a.Side)) spotsBySide[a.Side] = spots;
            if (spots.Count == 0)
                r.Issues.Add(new Issue(Severity.Error, "spawn-no-floor", $"The {SideName(a.Side)} spawn area has no floor inside it: nobody could stand there.",
                    "Move the spawn area onto the floor (its bottom should touch the floor), or add floor under it.", pid));
            else if (spots.Count < 6)
                r.Issues.Add(new Issue(Severity.Warning, "spawn-small", $"The {SideName(a.Side)} spawn area fits only {spots.Count} player{(spots.Count == 1 ? "" : "s")} at a time (spots are 2 m apart).",
                    "Make the spawn area bigger (10 x 10 m fits about 16).", pid));
        }

        // Spawn area overlap between the sides.
        foreach (var m in marinesAreas)
            foreach (var c in chaosAreas)
                if (BoxesOverlap(m.Position, m.Scale, m.Yaw, c.Position, c.Scale, c.Yaw))
                    r.Issues.Add(new Issue(Severity.Error, "spawn-overlap", "The Marine and Chaos spawn areas overlap: the sides would spawn on each other.", "Move one spawn area away from the other.", ids(areaPieces)));

        // Doors.
        foreach (var side in new[] { Team.SpaceMarines, Team.Chaos })
            if (content.StartAreas.Any(a => a.Side == side) && !content.Blockers.Any(b => b.Side == side))
                r.Issues.Add(new Issue(Severity.Warning, side == Team.SpaceMarines ? "no-door-marines" : "no-door-chaos",
                    $"The {SideName(side)} spawn room has no spawn door, so the other side can walk in (and spawning players face the way their area points, not toward an exit).",
                    "Add a spawn door across the room's exit.", null, CheckMode.Both, side == Team.SpaceMarines ? "add-door-marines" : "add-door-chaos"));

        // Capture points.
        if (content.ControlPoints.Count == 0 && map.Document.Modes.Contains("seize"))
            r.Issues.Add(new Issue(Severity.Error, "no-points", "There are no capture points, so Seize Ground cannot be played on this map (Annihilation can).", "Add a Capture point (three is the classic A, B, C).", null, CheckMode.Seize, "add-capture-point"));
        else if (content.ControlPoints.Count is > 0 and < 3)
            r.Issues.Add(new Issue(Severity.Info, "few-points", $"Only {content.ControlPoints.Count} capture point{(content.ControlPoints.Count == 1 ? "" : "s")}: Seize Ground plays best with 3.", null, null, CheckMode.Seize));
        if (content.ControlPoints.Count > World.MaxControlPoints)
            r.Issues.Add(new Issue(Severity.Error, "too-many-points", $"{content.ControlPoints.Count} capture points: a match takes at most {World.MaxControlPoints}.", "Delete the extra capture points.", null, CheckMode.Seize));
        for (int i = 0; i < content.ControlPoints.Count; i++)
        {
            var p = content.ControlPoints[i];
            var piece = map.Pieces.Where(x => x.Piece.Kind == PieceKind.CapturePoint).ElementAtOrDefault(i);
            var floor = Standable(mesh, p.Position.X, p.Position.Y, p.Position.Z + p.Scale.Z / 2f, p.Position.Z - p.Scale.Z / 2f - 1f);
            if (floor is null)
                r.Issues.Add(new Issue(Severity.Warning, "point-no-floor", $"Capture point {Letter(i)} has no floor in its middle, so nobody can stand on it.", "Move it onto the floor, or lower it.", piece is null ? null : [piece.Id], CheckMode.Seize));
        }

        // Anything inside a kill zone (a spawn in a pit would die at once).
        var killVolumes = world.KillVolumes;
        foreach (var kz in content.KillRegions.Where(k => k.Name != "cm_kill_floor"))
        {
            var kv = kz.ToSim();
            foreach (var (what, at) in content.StartAreas.Select(a => ($"The {SideName(a.Side)} spawn area", a.Position))
                .Concat(content.ControlPoints.Select((p, i) => ($"Capture point {Letter(i)}", p.Position))))
                if (kv.Contains(at))
                    r.Issues.Add(new Issue(Severity.Error, "in-kill-zone", $"{what} is inside a kill zone: players there would die at once.", "Move the kill zone or the piece.", null));
        }

        // Size and sanity.
        if (map.SolidBounds is { } b)
        {
            var size = b.Max - b.Min;
            r.Facts.Add($"Ground and cover: {map.SolidCount} solid piece{(map.SolidCount == 1 ? "" : "s")}, {size.X:0} x {size.Y:0} m, {size.Z:0} m tall.");
            if (size.X > 600f || size.Y > 600f)
                r.Issues.Add(new Issue(Severity.Warning, "huge", $"The map is {size.X:0} x {size.Y:0} m: that is bigger than every Space Marine map and slow to load.", "Pull far-away pieces in, or delete strays."));
            if (size.X < 20f && size.Y < 20f)
                r.Issues.Add(new Issue(Severity.Warning, "tiny", "The map is smaller than 20 x 20 m: there is hardly room for two sides.", null));
            if (spotsBySide.Values.SelectMany(x => x).Any() && b.Max.Z - b.Min.Z > 0)
            {
                // players who walk off the edge die once they fall; say so, since it surprises people.
            }
        }
        r.Facts.Add($"Spawn spots: Marines {spotsBySide.GetValueOrDefault(Team.SpaceMarines)?.Count ?? 0}, Chaos {spotsBySide.GetValueOrDefault(Team.Chaos)?.Count ?? 0}. Capture points: {content.ControlPoints.Count}. Spawn doors: {content.Blockers.Count}. Kill zones: {content.KillRegions.Count(k => k.Name != "cm_kill_floor")}.");
        r.Facts.Add($"Anyone who falls {CustomMap.KillFloorDepth:0} m below the lowest floor dies, so open edges are safe to leave.");
        foreach (var n in map.Notes) r.Issues.Add(new Issue(Severity.Info, "note", n));
        foreach (var m in map.Document.Modes.Where(m => !MapDocument.KnownModes.Contains(m)))
            r.Issues.Add(new Issue(Severity.Info, "unknown-mode", $"Game mode \"{m}\" is not known to this version, so nothing is checked for it and it cannot be play-tested here."));

        // Document-level problems (parent cycles and such) are the core's own; surface the first few.
        if (config is not null && spotsBySide.TryGetValue(Team.SpaceMarines, out var ms) && spotsBySide.TryGetValue(Team.Chaos, out var cs) && ms.Count > 0 && cs.Count > 0)
            Route(r, map, world, config, navCache, ms[0], cs[0]);
        else if (config is null)
            r.Facts.Add("Bot route: not checked here (needs the game install).");
        return r;
    }

    static void Route(CheckReport r, CustomMap map, CollisionWorld world, SimConfig config, string? navCache, Vector3 marine, Vector3 chaos)
    {
        try
        {
            string cache = navCache ?? BotNavigation.CacheDirectory;
            foreach (var side in new[] { Team.SpaceMarines, Team.Chaos })
            {
                var graph = BotNavigation.LoadOrBuildWalk(map.Content, world, config, side, cache, out _);
                var from = side == Team.SpaceMarines ? marine : chaos;
                var enemy = side == Team.SpaceMarines ? chaos : marine;
                if (graph.FindPath(from, enemy) is null)
                    r.Issues.Add(new Issue(Severity.Error, "no-route", $"Bots of the {SideName(side)} cannot walk from their spawn to the other side's spawn. The two halves of the map are cut apart (a wall with no gap, a drop too high, or a door that never opens).",
                        "Add a ramp or open a gap between the sides."));
                for (int i = 0; i < map.Content.ControlPoints.Count; i++)
                {
                    var p = map.Content.ControlPoints[i].Position - new Vector3(0, 0, map.Content.ControlPoints[i].Scale.Z / 2f);
                    if (graph.FindPath(from, p) is null)
                        r.Issues.Add(new Issue(Severity.Warning, "point-unreachable", $"Bots of the {SideName(side)} cannot walk to capture point {Letter(i)}.", "Add a ramp or step up to it, or move it onto reachable floor.", null, CheckMode.Seize));
                }
            }
            if (!r.Issues.Any(i => i.Code is "no-route" or "point-unreachable")) r.Facts.Add("Bot route: both sides can walk to each other and to every capture point.");
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException)
        {
            r.Facts.Add($"Bot route: could not be checked ({e.Message}).");
        }
    }

    static bool BoxesOverlap(Vector3 ca, Vector3 sa, float ya, Vector3 cb, Vector3 sb, float yb)
    {
        // Separating-axis test in plan (the two boxes' four edge directions), and heights.
        if (MathF.Abs(ca.Z - cb.Z) > (sa.Z + sb.Z) / 2f) return false;
        Vector2[] Axes(float y) => [new(MathF.Cos(y), MathF.Sin(y)), new(-MathF.Sin(y), MathF.Cos(y))];
        float Radius(Vector2 axis, Vector3 size, float yaw)
        {
            var ax = Axes(yaw);
            return MathF.Abs(Vector2.Dot(axis, ax[0])) * size.X / 2f + MathF.Abs(Vector2.Dot(axis, ax[1])) * size.Y / 2f;
        }
        var d = new Vector2(cb.X - ca.X, cb.Y - ca.Y);
        foreach (var axis in Axes(ya).Concat(Axes(yb)))
            if (MathF.Abs(Vector2.Dot(d, axis)) > Radius(axis, sa, ya) + Radius(axis, sb, yb)) return false;
        return true;
    }
}

