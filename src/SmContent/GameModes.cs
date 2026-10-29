using System.Text;
using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>A multiplayer team from global <c>extensions/multiplayer_settings/teams</c>: its key, player blueprint and side.</summary>
public sealed record MpTeam(string Key, string Blueprint, Team Side);

/// <summary>
/// What the install says about Annihilation (team deathmatch). The teams are attribute data; the scoring rule and the
/// spawn rule live in the compiled multiplayer scripts (<c>util.oppc</c>, Lua 5.1 bytecode), which are not executed or
/// decompiled: only the presence of the names the rules use is checked (<see cref="ScriptMarkers"/>). The score limit, time
/// limit and friendly fire are host options whose value lists the executable supplies (front end <c>HostOptions.gfx</c>:
/// <c>Native_SetTdmScoreLimitArray</c>, <c>Native_SetTimeLimitArray</c>, <c>friendly_fire_btn</c>), so the
/// <see cref="MatchRules"/> defaults stay provisional. Paths and findings: docs/discovery.md (M15).
/// </summary>
public sealed record GameModes(IReadOnlyList<MpTeam> Teams, IReadOnlyList<string> ScriptMarkersFound, IReadOnlyList<string> Notes)
{
    /// <summary>Which of <see cref="SeizeGroundMarkers"/> occur in their scripts (M16).</summary>
    public IReadOnlyList<string> SeizeGroundMarkersFound { get; init; } = [];
    /// <summary>The script of the Dominate objective type (Seize Ground's control points).</summary>
    public const string DominateScript = "util/mp/scripts/objectives/dominate.lua";

    /// <summary>
    /// Names the Seize Ground rules rest on (M16; constant pools only, values read by hand and recorded in spec/behavior.md):
    /// in <see cref="ObjectivesScript"/> the preset objectives <c>Domination</c> (type <c>Dominate</c>, game mode
    /// <c>domination</c>) and <c>DominationTimer</c> with the fields <c>captureDuration</c> (20), <c>decapDuration</c> (10) and
    /// <c>scoreSpeed</c> (2); in <see cref="DominateScript"/> the engine capture point registration, the periodic update and the
    /// host score limit. Each entry is "script|name".
    /// </summary>
    public static readonly IReadOnlyList<string> SeizeGroundMarkers =
    [
        $"{ObjectivesScript}|Domination", $"{ObjectivesScript}|DominationTimer", $"{ObjectivesScript}|Dominate", $"{ObjectivesScript}|captureDuration",
        $"{ObjectivesScript}|decapDuration", $"{ObjectivesScript}|scoreSpeed",
        $"{DominateScript}|Network_AddCapturePoint", $"{DominateScript}|DominateUpdate", $"{DominateScript}|Network_GetScoreLimit", $"{DominateScript}|owningTeam",
    ];
    public const string TeamsTable = "extensions/multiplayer_settings/teams";
    public const string UtilPackage = "data/packages/util.oppc";
    /// <summary>The script holding the multiplayer objectives (team deathmatch among them).</summary>
    public const string ObjectivesScript = "util/mp/scripts/data/objectives.lua";

    /// <summary>
    /// Names in <see cref="ObjectivesScript"/> the reimplemented rules rest on: the per-side deathmatch objectives of type
    /// <c>Deaths</c> (each side scores the other side's deaths) with <c>isDeathMatch</c>, the match timer, and the spawn
    /// randomizer over the map's <c>tdm_mkr_spawn_*</c> markers.
    /// </summary>
    public static readonly IReadOnlyList<string> ScriptMarkers =
        ["TeamDeathmatchMarine", "TeamDeathmatchChaos", "Deaths", "isDeathMatch", "TeamDeathmatchTimer", "TeamDeathmatchEnd", "RandomizeTeamSpawning", "tdm_mkr_spawn_01"];

    /// <summary>The side of a class race (<c>race</c> in the class table): "space_marine" or "chaos"; anything else none.</summary>
    public static Team SideOfRace(string race) => race.ToLowerInvariant() switch
    {
        "space_marine" => Team.SpaceMarines,
        "chaos" => Team.Chaos,
        _ => Team.None,
    };

    /// <summary>The side of a team's player blueprint path: <c>race_marine</c> or <c>race_chaos</c> in it; else none.</summary>
    public static Team SideOfBlueprint(string ebp)
    {
        var parts = ebp.ToLowerInvariant().Split('\\', '/');
        return parts.Contains("race_marine") ? Team.SpaceMarines : parts.Contains("race_chaos") ? Team.Chaos : Team.None;
    }

    /// <summary>The teams table of the global attribute file (every table entry with an <c>ebp</c>), in file order.</summary>
    public static IReadOnlyList<MpTeam> ReadTeams(BafDocument global)
    {
        var teams = global.Root.Table(TeamsTable) ?? throw new InvalidDataException($"global: no {TeamsTable}.");
        return teams.Entries.Where(e => e.Type == BafType.Table).Select(e =>
        {
            string ebp = e.AsTable().String("ebp") ?? "";
            return new MpTeam(e.Name, ebp, SideOfBlueprint(ebp));
        }).ToList();
    }

    /// <summary>Which of <see cref="ScriptMarkers"/> occur in the script's bytes (ASCII, case-sensitive).</summary>
    public static IReadOnlyList<string> FindMarkers(ReadOnlySpan<byte> script)
    {
        var found = new List<string>();
        foreach (var m in ScriptMarkers) if (script.IndexOf(Encoding.ASCII.GetBytes(m)) >= 0) found.Add(m);
        return found;
    }

    /// <summary>Which "script|name" entries of <see cref="SeizeGroundMarkers"/> occur (ASCII, case-sensitive) in the given scripts (by path, any case).</summary>
    public static IReadOnlyList<string> FindSeizeGroundMarkers(IReadOnlyDictionary<string, byte[]> scripts)
    {
        var found = new List<string>();
        foreach (var entry in SeizeGroundMarkers)
        {
            var (script, name) = (entry[..entry.IndexOf('|')], entry[(entry.IndexOf('|') + 1)..]);
            var bytes = scripts.FirstOrDefault(kv => kv.Key.Equals(script, StringComparison.OrdinalIgnoreCase)).Value;
            if (bytes is not null && bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(name)) >= 0) found.Add(entry);
        }
        return found;
    }

    /// <summary>Reads the teams from <c>global</c> and checks the objectives script (read-only).</summary>
    public static GameModes Load(string gameDir)
    {
        var notes = new List<string>();
        BafDocument? global = null;
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, MpClasses.GlobalPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == "global", (_, d) => global = BafDocument.Read(d));
        if (global is null) throw new FileNotFoundException($"global not found in {MpClasses.GlobalPackage}.");
        var teams = ReadTeams(global);
        foreach (var side in new[] { Team.SpaceMarines, Team.Chaos })
            if (teams.Count(t => t.Side == side) != 1) notes.Add($"teams: expected one {side} team, found {teams.Count(t => t.Side == side)}");

        IReadOnlyList<string> found = [];
        var seize = new List<string>();
        string util = Path.Combine(gameDir, UtilPackage);
        if (!File.Exists(util)) notes.Add($"{UtilPackage} not found; the mode scripts were not checked");
        else
        {
            bool seen = false;
            var scripts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using var archive = ObpkArchive.Open(util);
            archive.ReadEntries(e => e.Path(archive.GroupName(e)) is var p && (p.Equals(ObjectivesScript, StringComparison.OrdinalIgnoreCase) || p.Equals(DominateScript, StringComparison.OrdinalIgnoreCase)),
                (e, d) => scripts[e.Path(archive.GroupName(e))] = d.ToArray());
            if (scripts.TryGetValue(ObjectivesScript, out var objectives)) { found = FindMarkers(objectives); seen = true; }
            if (!seen) notes.Add($"{ObjectivesScript} not found in {UtilPackage}");
            else if (found.Count < ScriptMarkers.Count) notes.Add($"{ObjectivesScript}: missing {string.Join(", ", ScriptMarkers.Except(found))}");
            seize.AddRange(FindSeizeGroundMarkers(scripts));
            if (seize.Count < SeizeGroundMarkers.Count) notes.Add($"Seize Ground names missing: {string.Join(", ", SeizeGroundMarkers.Except(seize))}");
        }
        return new GameModes(teams, found, notes) { SeizeGroundMarkersFound = seize };
    }
}
