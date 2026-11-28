using System.Text.Json;

namespace SmLauncher.Core;

/// <summary>What the PLAY TEST MAP dialog asks for: the saved map file, the mode, the bots a side and the player's side (the script's own words).</summary>
public sealed record MapPlaytestChoice(string MapFile, string Mode, int Bots, string Side);

/// <summary>The stage and text of the script's <c>status.txt</c> ("stage|text"), and when it was written.</summary>
public sealed record MapPlaytestStatus(string Stage, string Text, DateTime WrittenUtc)
{
    public bool Failed => Stage == "failed";
    public bool Done => Stage == "done";
}

/// <summary>
/// The launcher's PLAY TEST MAP button (docs/releasing.md "The Mapmaker", docs/mapmaker.md "Play test"): plays a saved <c>.smmap</c> file with bots WITHOUT the Mapmaker
/// open (owner, 2026-11-28: the Godot editor beside the game, its server and its bots ran a PC out of memory). It runs the active build's own
/// <c>mapmaker\playtest.ps1</c> in a hidden PowerShell, the same script the Mapmaker's Play test runs: a server on 127.0.0.1 only with <c>--custom-map</c>, the bots
/// as hidden headless clients and the game in a window; the script stops the server and the bots when the game closes. Added content (the Mapmaker): the
/// button is there whenever the build ships the Mapmaker, like MAPMAKER (docs/added-content.md 5b). Only a saved file is ever played (owner rule).
/// </summary>
public static class MapPlaytest
{
    public static readonly string[] Modes = ["annihilation", "seize"];
    public static readonly string[] Sides = ["marines", "chaos"];
    public const int MaxBots = 6;
    public const int DefaultBots = 3;
    /// <summary>Test aid: the folder the script keeps its <c>status.txt</c> and <c>run.json</c> in, instead of <c>&lt;data&gt;\tmp-build\mapmaker-playtest</c> (a test run never meets a real play test's record).</summary>
    public const string StateDirVariable = "SM_PLAYTEST_STATE_DIR";

    /// <summary>The files of the active build the button needs, relative to its folder.</summary>
    public static readonly string[] Required = [Path.Combine(MapmakerSetup.ShippedDir, MapmakerSetup.PlaytestScript), @"server\SmServer.exe", @"game\sm-reimpl.exe", @"game\sm-reimpl.console.exe"];

    public static string Script(string versionDir) => Path.Combine(versionDir, MapmakerSetup.ShippedDir, MapmakerSetup.PlaytestScript);

    /// <summary>Null when the active build can play a map, else a plain sentence for the button's tooltip.</summary>
    public static string? Unavailable(string? versionDir)
    {
        if (versionDir is null) return "No build is installed yet.";
        foreach (var rel in Required)
            if (!File.Exists(Path.Combine(versionDir, rel)))
                return $"This build cannot play test a map: {rel} is missing. Update to the newest build, or use Check files.";
        return null;
    }

    /// <summary>The data root as the game and the script find it (SM_DATA_DIR, else %LOCALAPPDATA%\sm-reimpl).</summary>
    public static string DataRoot(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        return CacheRepair.DataRoot(env(CacheRepair.DataDirEnvVar), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    /// <summary>Where the script writes <c>status.txt</c> and <c>run.json</c> (the Mapmaker's Play test uses the same folder, so a run of either is seen).</summary>
    public static string StateDir(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        return env(StateDirVariable) is { Length: > 0 } s && !string.IsNullOrWhiteSpace(s) ? Path.GetFullPath(s.Trim()) : Path.Combine(DataRoot(env), "tmp-build", "mapmaker-playtest");
    }

    public static string StatusFile(string stateDir) => Path.Combine(stateDir, "status.txt");
    public static string RunFile(string stateDir) => Path.Combine(stateDir, "run.json");

    /// <summary>The Mapmaker library's maps folder (<c>&lt;data&gt;\mapmaker\maps</c>, SmMapmaker's <c>MapLibrary.MapsDir</c>), where Browse... opens.</summary>
    public static string MapsDir(Func<string, string?>? env = null) => Path.Combine(DataRoot(env), "mapmaker", "maps");

    /// <summary>The last choices from the launcher's settings, or the defaults (Annihilation, 3 bots a side, Space Marines, no file).</summary>
    public static MapPlaytestChoice FromSettings(LauncherSettings s) => new(
        s.PlaytestMap ?? "",
        s.PlaytestMode is { } m && Modes.Contains(m) ? m : Modes[0],
        s.PlaytestBots is { } b && b is >= 0 and <= MaxBots ? b : DefaultBots,
        s.PlaytestSide is { } side && Sides.Contains(side) ? side : Sides[0]);

    public static void Remember(LauncherSettings s, MapPlaytestChoice c)
    {
        s.PlaytestMap = c.MapFile;
        s.PlaytestMode = c.Mode;
        s.PlaytestBots = c.Bots;
        s.PlaytestSide = c.Side;
    }

    /// <summary>Null when the choice can be played, else one plain line for the dialog (a missing file, a file that is not a saved map, out-of-range values).</summary>
    public static string? Problem(MapPlaytestChoice c)
    {
        string f = c.MapFile.Trim().Trim('"');
        if (f.Length == 0) return "Choose a saved map file (.smmap) first.";
        string full;
        try { full = Path.GetFullPath(f); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return "That is not a file path."; }
        if (!string.Equals(Path.GetExtension(full), ".smmap", StringComparison.OrdinalIgnoreCase)) return "That is not a saved map: choose a .smmap file.";
        if (!File.Exists(full)) return $"The map file {Path.GetFileName(full)} does not exist (moved or deleted?). Choose it again.";
        if (!Modes.Contains(c.Mode)) return "Choose Annihilation or Seize Ground.";
        if (!Sides.Contains(c.Side)) return "Choose Space Marines or Chaos.";
        if (c.Bots is < 0 or > MaxBots) return $"Bots per side: 0 to {MaxBots}.";
        return null;
    }

    static string PowerShellExe()
    {
        string p = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(p) ? p : "powershell.exe";
    }

    static List<string> Lead(string versionDir) => ["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", Script(versionDir)];

    /// <summary>No trailing separator: a quoted argument that ends in a backslash ("C:\x\") would swallow its closing quote.</summary>
    static string Folder(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    /// <summary>
    /// The hidden PowerShell that plays <paramref name="c"/>: <c>powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File &lt;version&gt;\mapmaker\playtest.ps1
    /// -Map &lt;file&gt; -Mode m -Bots n -Side s -VersionDir &lt;version&gt;</c>, one argument per item (the caller passes them as an argument list; no shell, so no quoting by hand).
    /// <paramref name="extra"/> adds script arguments (the test run's <c>-Headless -Seconds 20</c>).
    /// </summary>
    public static MapmakerLaunch StartCommand(string versionDir, MapPlaytestChoice c, IEnumerable<string>? extra = null)
    {
        string dir = Folder(versionDir);
        var args = Lead(dir);
        args.AddRange(["-Map", Path.GetFullPath(c.MapFile.Trim().Trim('"')), "-Mode", c.Mode, "-Bots", c.Bots.ToString(System.Globalization.CultureInfo.InvariantCulture), "-Side", c.Side, "-VersionDir", dir]);
        if (extra is not null) args.AddRange(extra);
        return new MapmakerLaunch(PowerShellExe(), args, dir, new Dictionary<string, string>());
    }

    /// <summary>The script's <c>-Stop</c>: ends only the processes the last play test recorded (by id and start time).</summary>
    public static MapmakerLaunch StopCommand(string versionDir)
    {
        string dir = Folder(versionDir);
        var args = Lead(dir);
        args.Add("-Stop");
        return new MapmakerLaunch(PowerShellExe(), args, dir, new Dictionary<string, string>());
    }

    /// <summary>
    /// One Windows command line from separate arguments, quoted the way CommandLineToArgvW (and so PowerShell's -File) reads them back: an argument with a space, a tab
    /// or a quote, or an empty one, goes in quotes; backslashes before a quote are doubled. For logs and the tests; the launcher itself passes an argument list.
    /// </summary>
    public static string CommandLine(MapmakerLaunch l) => string.Join(' ', new[] { l.FileName }.Concat(l.Arguments).Select(QuoteArg));

    public static string QuoteArg(string a)
    {
        if (a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0) return a;
        var sb = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (char ch in a)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes).Append(ch);
            slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>The script's status line, or null when there is none (or it cannot be read).</summary>
    public static MapPlaytestStatus? ReadStatus(string stateDir)
    {
        string f = StatusFile(stateDir);
        try
        {
            if (!File.Exists(f)) return null;
            string line = AtomicFile.ReadAllText(f).Trim().TrimStart('\uFEFF');
            int bar = line.IndexOf('|');
            return new MapPlaytestStatus(bar >= 0 ? line[..bar] : "", bar >= 0 ? line[(bar + 1)..] : line, File.GetLastWriteTimeUtc(f));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Written before the script starts, so a status line left by an earlier run is never shown as this one's.</summary>
    public static void WriteStarting(string stateDir)
    {
        try { Directory.CreateDirectory(stateDir); File.WriteAllText(StatusFile(stateDir), "starting|Starting the play test..."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A play test is running: the script's <c>run.json</c> names a process (server, bot or game) that is still alive with the start time it was recorded with (a
    /// recycled process id is not the same process). Covers the Mapmaker's own Play test too (same folder). <paramref name="alive"/> is replaceable in tests.
    /// </summary>
    public static bool RecordedRunAlive(string stateDir, Func<int, long, bool>? alive = null)
    {
        alive ??= ProcessAlive;
        foreach (var (pid, start) in RecordedProcesses(stateDir))
            if (alive(pid, start)) return true;
        return false;
    }

    /// <summary>The (pid, start as FILETIME UTC) pairs of <c>run.json</c> (<c>{"pids":[{"pid":n,"start":t}, ...]}</c>, or one object instead of the list); none when it is missing or unreadable.</summary>
    public static List<(int Pid, long Start)> RecordedProcesses(string stateDir)
    {
        var list = new List<(int, long)>();
        string f = RunFile(stateDir);
        try
        {
            if (!File.Exists(f)) return list;
            using var doc = JsonDocument.Parse(AtomicFile.ReadAllText(f).TrimStart('\uFEFF'));
            if (!doc.RootElement.TryGetProperty("pids", out var pids)) return list;
            IEnumerable<JsonElement> items = pids.ValueKind == JsonValueKind.Array ? pids.EnumerateArray() : pids.ValueKind == JsonValueKind.Object ? [pids] : [];
            foreach (var e in items)
                if (e.TryGetProperty("pid", out var p) && p.TryGetInt32(out int pid) && e.TryGetProperty("start", out var s) && s.TryGetInt64(out long start))
                    list.Add((pid, start));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return list;
    }

    static bool ProcessAlive(int pid, long start)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited && p.StartTime.ToFileTimeUtc() == start;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
    }

    /// <summary>
    /// The line for the launcher once the script's PowerShell ended: the script's failure reason, "over" when it finished, or that it never ran to its end (PowerShell
    /// blocked, a parse error: the script writes "done|..." or "failed|..." as its last act). <paramref name="status"/> is the status line written after the start.
    /// </summary>
    public static (string Text, bool Ok) Outcome(MapPlaytestStatus? status, int exitCode)
    {
        if (status is { Failed: true }) return ("Play test failed: " + status.Text, false);
        if (status is { Done: true } && exitCode == 0) return ("Play test over (the server and the bots are stopped).", true);
        string last = status is null ? "none" : $"{status.Stage}: {status.Text}";
        return ($"Play test failed: the play test script did not run to its end (exit code {exitCode}; last state {last}).", false);
    }
}
