using System.Diagnostics;
using SmContent;
using SmSim;

namespace SmServer;

/// <summary>
/// The map rotation (classic-maps-all, 2026-11-15; <c>--maps all|a,b,c</c>): one server process per match. This supervisor starts the server itself as a child on the first map
/// (<c>--map m --rotation-child</c>, the other arguments as given), waits for it, and starts the next map of the list when the child exits with
/// <see cref="ServerCommandLine.RotationExit"/> (its match ended and it said so in its snapshots; protocol 79); any other exit code ends the supervisor with that code. The list repeats.
/// Each map's mode support is checked against the install's front-end data (<see cref="MapCatalog"/>) before anything starts; a map that is not playable here or does not offer the
/// chosen mode is an error, not skipped silently (except in <c>all</c>, which takes the maps that are).
/// </summary>
static class RotationSupervisor
{
    /// <summary>The ordered map list the arguments ask for, or an error line (exit code 1).</summary>
    public static bool TryPlan(string install, ServerArguments cli, out List<string> maps, out string error) =>
        TryPlan(MapCatalog.Read(install), cli, out maps, out error);

    /// <summary><see cref="TryPlan(string, ServerArguments, out List{string}, out string)"/> against a given roster (tests).</summary>
    public static bool TryPlan(IReadOnlyList<MapEntry> catalog, ServerArguments cli, out List<string> maps, out string error)
    {
        maps = []; error = "";
        bool Offers(MapEntry m) => cli.Mode == GameMode.SeizeGround ? m.SupportsSeize : m.SupportsAnnihilation;
        if (cli.Maps!.Count == 1 && cli.Maps[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            maps = catalog.Where(m => m.Playable && Offers(m)).Select(m => m.Name).ToList();
            if (maps.Count == 0) error = "--maps all: the install lists no playable competitive map for this mode";
            return error.Length == 0;
        }
        foreach (var name in cli.Maps)
        {
            var entry = catalog.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) { error = $"--maps: '{name}' is not a competitive map of this install (smtool maps-audit lists them)"; return false; }
            if (!entry.Playable) { error = $"--maps: '{entry.Name}' ({entry.DisplayName}) has no collision, spawn or point data in this install; it cannot be played"; return false; }
            if (!Offers(entry)) { error = $"--maps: '{entry.Name}' ({entry.DisplayName}) does not offer {(cli.Mode == GameMode.SeizeGround ? "Seize Ground" : "Annihilation")} in its data"; return false; }
            if (!maps.Contains(entry.Name)) maps.Add(entry.Name);
        }
        return maps.Count > 0;
    }

    /// <summary>
    /// The child's arguments: the supervisor's own with <c>--maps</c> replaced by <c>--map m</c>, plus <c>--rotation-child</c>. devtools-god-custommap: a map file the developer tools picked
    /// (<see cref="IsMapFilePick"/>) is played with <c>--custom-map</c> instead, and <paramref name="modeOverride"/> (the pick's fallback, <c>annihilation</c>) replaces <c>--mode</c> for that match only.
    /// </summary>
    public static List<string> ChildArguments(IReadOnlyList<string> args, string map, int weatherDraw = 0, string? modeOverride = null)
    {
        var child = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--maps" || args[i] == "--custom-map" || (modeOverride is not null && args[i] == "--mode")) { i++; continue; }
            child.Add(args[i]);
        }
        child.Add(IsMapFilePick(map) ? "--custom-map" : "--map"); child.Add(map); child.Add("--rotation-child");
        if (modeOverride is not null) { child.Add("--mode"); child.Add(modeOverride); }
        return WithWeatherLoad(child, weatherDraw);
    }

    /// <summary>devtools-god-custommap: a pick is a saved Mapmaker map file (a full path to an existing <c>.smmap</c>), not a Classic map's archive name.</summary>
    public static bool IsMapFilePick(string? pick) => pick is not null && Path.IsPathRooted(pick) && pick.EndsWith(".smmap", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Weather W3: a server given <c>--weather</c> tells each child which map load it is (<c>--weather-draw n</c>, replacing any), so a Random choice differs from map to map and is reproducible from the seed (<see cref="WithWeatherSeed"/>);
    /// without <c>--weather</c> the arguments are returned unchanged. Text only: the supervisor never names the catalog.
    /// </summary>
    public static List<string> WithWeatherLoad(List<string> args, int draw)
    {
        if (!args.Contains("--weather")) return args;
        for (int i = args.Count - 2; i >= 0; i--)
            if (args[i] == "--weather-draw") args.RemoveRange(i, 2);
        args.Add("--weather-draw"); args.Add(draw.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return args;
    }

    /// <summary>Weather W3: with <c>--weather</c> and no <c>--weather-seed</c>, the arguments plus one seed chosen now, so every child of this supervisor draws from the same one.</summary>
    public static IReadOnlyList<string> WithWeatherSeed(IReadOnlyList<string> args, uint seed)
    {
        if (!args.Contains("--weather") || args.Contains("--weather-seed")) return args;
        return [.. args, "--weather-seed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture)];
    }

    /// <summary>
    /// The environment variables a supervisor gives its child: the file the child writes a picked map to (devtools-map-pick), the supervisor's own process id (the child leaves when it is gone) and
    /// (devtools-god-custommap) the mode the supervisor was started in, so a child that plays a fallen-back match knows which mode a pick returns to.
    /// </summary>
    public const string PickFileVariable = "SM_ROTATION_PICK_FILE", SupervisorPidVariable = "SM_SUPERVISOR_PID", HomeModeVariable = "SM_DEV_HOME_MODE";

    /// <summary>
    /// The child tells its supervisor which map the developer tools picked by writing it to the file the supervisor named; nothing without one. devtools-god-custommap: the map is a Classic archive
    /// name or a map file's full path, and a second line <c>annihilation</c> says the next match plays in Annihilation (the map cannot run the server's mode).
    /// </summary>
    public static void WritePick(string map, string? modeOverride = null)
    {
        if (Environment.GetEnvironmentVariable(PickFileVariable) is { Length: > 0 } path)
            try { File.WriteAllText(path, modeOverride is null ? map : map + "\n" + modeOverride); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"rotation: the picked map could not be handed to the supervisor ({e.Message})"); }
    }

    /// <summary>The map a child left in its pick file (null: none, or not a map name); the file is removed.</summary>
    public static string? TakePick(string path) => TakePick(path, out _);

    /// <summary>
    /// The map a child left in its pick file and the mode it plays in (<paramref name="modeOverride"/>: <c>annihilation</c>, or null for the server's own); null: none, or neither a Classic map name
    /// nor an existing <c>.smmap</c> file's full path. The file is removed.
    /// </summary>
    public static string? TakePick(string path, out string? modeOverride)
    {
        modeOverride = null;
        try
        {
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllText(path).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            File.Delete(path);
            if (lines.Length == 0) return null;
            string text = lines[0];
            if (lines.Length > 1 && lines[1] == "annihilation") modeOverride = "annihilation";
            if (System.Text.RegularExpressions.Regex.IsMatch(text, "^mp_[A-Za-z0-9_]{1,40}$")) return text;
            return IsMapFilePick(text) && File.Exists(text) ? Path.GetFullPath(text) : null;   // devtools-god-custommap: a saved Mapmaker map the tools picked
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { modeOverride = null; return null; }
    }

    /// <summary>The value of <c>--mode</c> in <paramref name="args"/> (the last one), else null.</summary>
    static string? ModeOf(IReadOnlyList<string> args)
    {
        string? mode = null;
        for (int i = 0; i + 1 < args.Count; i++) if (args[i] == "--mode") mode = args[i + 1];
        return mode;
    }

    /// <summary>
    /// Whether this process's supervisor is gone (the child was left behind by a kill of the supervisor alone): then the child exits too. The supervisor passes its own id in
    /// <see cref="SupervisorPidVariable"/>; with none there is no supervisor to lose.
    /// </summary>
    public static bool SupervisorGone()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(SupervisorPidVariable), out int pid)) return false;
        try { using var parent = Process.GetProcessById(pid); return parent.HasExited; }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    static (string Exe, string[] Host) Self()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        return (exe, Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? [Environment.GetCommandLineArgs()[0]] : []);
    }

    /// <summary>Runs the server itself as a child with these arguments; its exit code, the map it asked for (null: none) and the mode that map plays in (null: the server's own).</summary>
    static (int Code, string? Pick, string? Mode) RunChild(string exe, string[] host, IEnumerable<string> childArgs, Action<Process?> track, string? homeMode)
    {
        string pickFile = Path.Combine(Path.GetTempPath(), $"sm-pick-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var h in host) info.ArgumentList.Add(h);
        foreach (var a in childArgs) info.ArgumentList.Add(a);
        info.Environment[PickFileVariable] = pickFile;
        info.Environment[SupervisorPidVariable] = Environment.ProcessId.ToString();
        if (homeMode is not null) info.Environment[HomeModeVariable] = homeMode;
        var running = Process.Start(info) ?? throw new InvalidOperationException("the rotation child did not start");
        track(running);
        running.WaitForExit();
        int code = running.ExitCode;
        running.Dispose(); track(null);
        string? pick = TakePick(pickFile, out string? mode);
        return (code, pick, pick is null ? null : mode);
    }

    /// <summary>The home mode a child reads (<see cref="HomeModeVariable"/>), when its supervisor gave one and it is a mode the command line takes.</summary>
    public static SmSim.GameMode? HomeModeFromEnvironment() => Environment.GetEnvironmentVariable(HomeModeVariable) switch
    {
        "seize" => SmSim.GameMode.SeizeGround,
        "annihilation" or "ffa" => SmSim.GameMode.Annihilation,
        _ => null,
    };

    /// <summary>
    /// devtools-map-pick: the supervisor of a <c>--dev-tools</c> server that plays one map. It runs the server as a child (<c>--dev-supervised</c>, the arguments as given) and, when the tools pick another map,
    /// starts the child again on it (the picked map replaces any <c>--map</c>); the picked map stays until the next pick. Any other exit ends the supervisor with that code. devtools-god-custommap: the
    /// server may play a map file (<c>--custom-map</c>, added content) and the tools may pick one; a pick the server's mode cannot run plays in Annihilation (its <c>--mode</c> replaced for that match).
    /// </summary>
    public static int RunDev(IReadOnlyList<string> args)
    {
        var (exe, host) = Self();
        Process? running = null;
        Console.CancelKeyPress += (_, e) => { try { if (running is { HasExited: false }) running.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } };
        string? map = null, mode = null;
        string? homeMode = ModeOf(args);
        args = WithWeatherSeed(args, (uint)Random.Shared.NextInt64(0, uint.MaxValue));   // weather W3: one Random seed for every child of this supervisor
        for (int turn = 0; ; turn++)
        {
            if (turn > 0) Console.WriteLine($"dev tools: starting the match on {map}{(mode is not null ? $" in {mode} (the map cannot run the server's mode)" : "")}");
            var (code, pick, pickMode) = RunChild(exe, host, DevChildArguments(args, map, turn, mode), p => running = p, homeMode);
            if (code != ServerCommandLine.RotationExit) return code;
            if (pick is not null) { map = pick; mode = pickMode; }
        }
    }

    /// <summary>
    /// The dev child's arguments: the supervisor's own plus <c>--dev-supervised</c>, with <c>--map</c> (and <c>--custom-map</c>) replaced by <paramref name="map"/> when one was picked: a Classic map with
    /// <c>--map</c>, a map file (devtools-god-custommap) with <c>--custom-map</c>; <paramref name="modeOverride"/> replaces <c>--mode</c>.
    /// </summary>
    public static List<string> DevChildArguments(IReadOnlyList<string> args, string? map, int weatherDraw = 0, string? modeOverride = null)
    {
        var child = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (map is not null && (args[i] == "--map" || args[i] == "--custom-map")) { i++; continue; }
            if (modeOverride is not null && args[i] == "--mode") { i++; continue; }
            child.Add(args[i]);
        }
        if (map is not null) { child.Add(IsMapFilePick(map) ? "--custom-map" : "--map"); child.Add(map); }
        if (modeOverride is not null) { child.Add("--mode"); child.Add(modeOverride); }
        child.Add("--dev-supervised");
        return WithWeatherLoad(child, weatherDraw);
    }

    public static int Run(IReadOnlyList<string> args, List<string> maps)
    {
        var (exe, host) = Self();
        Console.WriteLine($"rotation: {maps.Count} map{(maps.Count == 1 ? "" : "s")}, one match each, then round again: {string.Join(", ", maps)}");
        Process? running = null;
        Console.CancelKeyPress += (_, e) => { try { if (running is { HasExited: false }) running.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } };
        string? forced = null, forcedMode = null;   // devtools-map-pick: a map the tools picked plays next; the list's own turn is not used up by it
        string? homeMode = ModeOf(args);
        args = WithWeatherSeed(args, (uint)Random.Shared.NextInt64(0, uint.MaxValue));   // weather W3: one Random seed for the whole rotation; each map load draws by its count
        int loads = 0;
        for (int turn = 0; ; )
        {
            string map = forced ?? maps[turn % maps.Count];
            Console.WriteLine($"rotation: match {turn + 1} on {map}{(forced is not null ? " (picked with the developer tools)" : "")}{(forcedMode is not null ? $" in {forcedMode}" : "")}");
            var (code, pick, pickMode) = RunChild(exe, host, ChildArguments(args, map, loads++, forced is null ? null : forcedMode), p => running = p, homeMode);
            if (code != ServerCommandLine.RotationExit) return code;
            forced = pick; forcedMode = pickMode;
            if (forced is null) turn++;
        }
    }
}
