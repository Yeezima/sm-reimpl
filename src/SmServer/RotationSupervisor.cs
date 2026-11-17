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

    /// <summary>The child's arguments: the supervisor's own with <c>--maps</c> replaced by <c>--map m</c>, plus <c>--rotation-child</c>.</summary>
    public static List<string> ChildArguments(IReadOnlyList<string> args, string map)
    {
        var child = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--maps") { i++; continue; }
            child.Add(args[i]);
        }
        child.Add("--map"); child.Add(map); child.Add("--rotation-child");
        return child;
    }

    /// <summary>The environment variables a supervisor gives its child: the file the child writes a picked map to (devtools-map-pick) and the supervisor's own process id (the child leaves when it is gone).</summary>
    public const string PickFileVariable = "SM_ROTATION_PICK_FILE", SupervisorPidVariable = "SM_SUPERVISOR_PID";

    /// <summary>The child tells its supervisor which map the developer tools picked by writing it to the file the supervisor named; nothing without one.</summary>
    public static void WritePick(string map)
    {
        if (Environment.GetEnvironmentVariable(PickFileVariable) is { Length: > 0 } path)
            try { File.WriteAllText(path, map); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"rotation: the picked map could not be handed to the supervisor ({e.Message})"); }
    }

    /// <summary>The map a child left in its pick file (null: none, or not a map name); the file is removed.</summary>
    public static string? TakePick(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path).Trim();
            File.Delete(path);
            return System.Text.RegularExpressions.Regex.IsMatch(text, "^mp_[A-Za-z0-9_]{1,40}$") ? text : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
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

    /// <summary>Runs the server itself as a child with these arguments; its exit code, and the map it asked for (null: none).</summary>
    static (int Code, string? Pick) RunChild(string exe, string[] host, IEnumerable<string> childArgs, Action<Process?> track)
    {
        string pickFile = Path.Combine(Path.GetTempPath(), $"sm-pick-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var h in host) info.ArgumentList.Add(h);
        foreach (var a in childArgs) info.ArgumentList.Add(a);
        info.Environment[PickFileVariable] = pickFile;
        info.Environment[SupervisorPidVariable] = Environment.ProcessId.ToString();
        var running = Process.Start(info) ?? throw new InvalidOperationException("the rotation child did not start");
        track(running);
        running.WaitForExit();
        int code = running.ExitCode;
        running.Dispose(); track(null);
        return (code, TakePick(pickFile));
    }

    /// <summary>
    /// devtools-map-pick: the supervisor of a <c>--dev-tools</c> server that plays one map. It runs the server as a child (<c>--dev-supervised</c>, the arguments as given) and, when the tools pick another map,
    /// starts the child again on it (the picked map replaces any <c>--map</c>); the picked map stays until the next pick. Any other exit ends the supervisor with that code.
    /// </summary>
    public static int RunDev(IReadOnlyList<string> args)
    {
        var (exe, host) = Self();
        Process? running = null;
        Console.CancelKeyPress += (_, e) => { try { if (running is { HasExited: false }) running.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } };
        string? map = null;
        for (int turn = 0; ; turn++)
        {
            if (turn > 0) Console.WriteLine($"dev tools: starting the match on {map}");
            var (code, pick) = RunChild(exe, host, DevChildArguments(args, map), p => running = p);
            if (code != ServerCommandLine.RotationExit) return code;
            map = pick ?? map;
        }
    }

    /// <summary>The dev child's arguments: the supervisor's own plus <c>--dev-supervised</c>, with <c>--map</c> replaced by <paramref name="map"/> when one was picked.</summary>
    public static List<string> DevChildArguments(IReadOnlyList<string> args, string? map)
    {
        var child = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (map is not null && args[i] == "--map") { i++; continue; }
            child.Add(args[i]);
        }
        if (map is not null) { child.Add("--map"); child.Add(map); }
        child.Add("--dev-supervised");
        return child;
    }

    public static int Run(IReadOnlyList<string> args, List<string> maps)
    {
        var (exe, host) = Self();
        Console.WriteLine($"rotation: {maps.Count} map{(maps.Count == 1 ? "" : "s")}, one match each, then round again: {string.Join(", ", maps)}");
        Process? running = null;
        Console.CancelKeyPress += (_, e) => { try { if (running is { HasExited: false }) running.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } };
        string? forced = null;   // devtools-map-pick: a map the tools picked plays next; the list's own turn is not used up by it
        for (int turn = 0; ; )
        {
            string map = forced ?? maps[turn % maps.Count];
            Console.WriteLine($"rotation: match {turn + 1} on {map}{(forced is not null ? " (picked with the developer tools)" : "")}");
            var (code, pick) = RunChild(exe, host, ChildArguments(args, map), p => running = p);
            if (code != ServerCommandLine.RotationExit) return code;
            forced = pick;
            if (forced is null) turn++;
        }
    }
}
