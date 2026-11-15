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
    public static bool TryPlan(string install, ServerArguments cli, out List<string> maps, out string error)
    {
        maps = []; error = "";
        var catalog = MapCatalog.Read(install);
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

    public static int Run(IReadOnlyList<string> args, List<string> maps)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        string[] host = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? [Environment.GetCommandLineArgs()[0]] : [];
        Console.WriteLine($"rotation: {maps.Count} map{(maps.Count == 1 ? "" : "s")}, one match each, then round again: {string.Join(", ", maps)}");
        Process? running = null;
        Console.CancelKeyPress += (_, e) => { try { if (running is { HasExited: false }) running.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } };
        for (int turn = 0; ; turn++)
        {
            string map = maps[turn % maps.Count];
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var h in host) info.ArgumentList.Add(h);
            foreach (var a in ChildArguments(args, map)) info.ArgumentList.Add(a);
            Console.WriteLine($"rotation: match {turn + 1} on {map}");
            running = Process.Start(info) ?? throw new InvalidOperationException("the rotation child did not start");
            running.WaitForExit();
            int code = running.ExitCode;
            running.Dispose(); running = null;
            if (code != ServerCommandLine.RotationExit) return code;
        }
    }
}
