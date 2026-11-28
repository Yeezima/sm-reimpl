using System.Diagnostics;
using System.Runtime.InteropServices;
using SmLauncher.Core;

namespace SmLauncher.Tests;

/// <summary>
/// The launcher's PLAY TEST MAP button (docs/releasing.md "The Mapmaker"): when it is available, the hidden PowerShell's command line (quoted whole for an install
/// folder with spaces), the remembered choices, the refusal of a missing file, the "already running" check against the script's run.json, and the shipped
/// <c>mapmaker\playtest.ps1</c> run with <c>-Plan</c> from a synthetic version folder (it starts nothing). No original bytes: placeholder executables.
/// </summary>
public class MapPlaytestTests
{
    const string Tag = "tester-2026-11-28-abc1234";

    static string ScriptSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "mapmaker", "playtest.ps1");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("mapmaker/playtest.ps1 was not found above the test folder");
    }

    /// <summary>A version folder as the launcher installs it, in a folder with a space ("SM Reimpl"): server, game, the console wrapper and mapmaker\playtest.ps1.</summary>
    static string MakeVersion(TempDir t, params string[] leaveOut)
    {
        string v = t[$@"SM Reimpl\versions\{Tag}"];
        foreach (var rel in MapPlaytest.Required)
        {
            if (leaveOut.Contains(rel)) continue;
            string f = Path.Combine(v, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            if (rel.EndsWith(".ps1", StringComparison.Ordinal)) File.Copy(ScriptSource(), f);
            else File.WriteAllText(f, "x");
        }
        return v;
    }

    static string MakeMap(TempDir t, string name = "my map's arena.smmap")
    {
        string map = t[@"Meine Karten\" + name];
        Directory.CreateDirectory(Path.GetDirectoryName(map)!);
        File.WriteAllText(map, "{}");
        return map;
    }

    [Fact]
    public void The_button_needs_the_script_the_server_the_game_and_its_console_wrapper()
    {
        using var t = new TempDir();
        Assert.Equal("No build is installed yet.", MapPlaytest.Unavailable(null));
        Assert.Null(MapPlaytest.Unavailable(MakeVersion(t)));
        foreach (var rel in MapPlaytest.Required)
        {
            using var t2 = new TempDir();
            string why = MapPlaytest.Unavailable(MakeVersion(t2, rel))!;
            Assert.Contains(rel, why);
            Assert.Contains("Check files", why);
        }
        Assert.Contains(@"mapmaker\playtest.ps1", MapPlaytest.Required);
        Assert.Contains(@"game\sm-reimpl.console.exe", MapPlaytest.Required);
    }

    [Fact]
    public void The_command_runs_the_versions_own_script_hidden_with_the_choices_and_the_version_folder()
    {
        using var t = new TempDir();
        string v = MakeVersion(t);
        string map = MakeMap(t);
        var cmd = MapPlaytest.StartCommand(v + "\\", new MapPlaytestChoice(map, "seize", 2, "chaos"));
        Assert.EndsWith("powershell.exe", cmd.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", Path.Combine(v, "mapmaker", "playtest.ps1"),
            "-Map", map, "-Mode", "seize", "-Bots", "2", "-Side", "chaos", "-VersionDir", v], cmd.Arguments);
        Assert.Equal(v, cmd.WorkingDirectory);           // no trailing backslash: a quoted "...\" would swallow its closing quote
        var stop = MapPlaytest.StopCommand(v);
        Assert.Equal("-Stop", stop.Arguments[^1]);
        Assert.Contains("Hidden", stop.Arguments);
        Assert.DoesNotContain(stop.Arguments, a => a == "-Map");
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string cmdLine, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);

    static string[] Split(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW(commandLine, out int n);
        try { return Enumerable.Range(0, n).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(argv); }
    }

    [Theory]
    [InlineData(@"D:\Games\SM Reimpl\versions\tester-2026-11-28-abc1234")]
    [InlineData(@"D:\Karten über\my map's arena.smmap")]
    [InlineData(@"C:\trailing space\")]
    [InlineData("he said \"go\"")]
    [InlineData(@"a\\""b")]
    [InlineData("")]
    [InlineData("plain")]
    public void Every_argument_is_quoted_so_Windows_reads_it_back_whole(string arg)
    {
        var parts = Split("x.exe " + MapPlaytest.QuoteArg(arg) + " next");
        Assert.Equal(["x.exe", arg, "next"], parts);
    }

    [Fact]
    public void The_logged_command_line_splits_back_into_the_same_arguments()
    {
        using var t = new TempDir();
        string v = MakeVersion(t);
        var cmd = MapPlaytest.StartCommand(v, new MapPlaytestChoice(MakeMap(t), "annihilation", 3, "marines"));
        Assert.Equal(new[] { cmd.FileName }.Concat(cmd.Arguments), Split(MapPlaytest.CommandLine(cmd)));
        Assert.Contains("\"" + v + "\"", MapPlaytest.CommandLine(cmd));      // the install folder has a space: one quoted token
    }

    [Fact]
    public void The_choices_are_remembered_in_the_launchers_settings_beside_safe_mode()
    {
        using var t = new TempDir();
        var layout = new InstallLayout(t["root"]);
        var s = Store.ReadSettings(layout);
        Assert.Equal(new MapPlaytestChoice("", "annihilation", 3, "marines"), MapPlaytest.FromSettings(s));      // the defaults
        s.SafeMode = true;
        MapPlaytest.Remember(s, new MapPlaytestChoice(@"D:\maps\a.smmap", "seize", 0, "chaos"));
        Store.WriteSettings(layout, s);
        var back = Store.ReadSettings(layout);
        Assert.True(back.SafeMode);
        Assert.Equal(new MapPlaytestChoice(@"D:\maps\a.smmap", "seize", 0, "chaos"), MapPlaytest.FromSettings(back));
        // a hand-edited or older file: values out of range fall back to the defaults
        back.PlaytestMode = "musou"; back.PlaytestBots = 40; back.PlaytestSide = "orks";
        Assert.Equal(new MapPlaytestChoice(@"D:\maps\a.smmap", "annihilation", 3, "marines"), MapPlaytest.FromSettings(back));
        // settings written by an older launcher (no play test fields) still read
        File.WriteAllText(layout.SettingsFile, "{ \"lastServer\": \"1.2.3.4:27015\", \"safeMode\": false }");
        Assert.Equal(new MapPlaytestChoice("", "annihilation", 3, "marines"), MapPlaytest.FromSettings(Store.ReadSettings(layout)));
    }

    [Fact]
    public void A_missing_or_unsaved_map_file_is_refused_in_one_plain_line()
    {
        using var t = new TempDir();
        string map = MakeMap(t);
        Assert.Null(MapPlaytest.Problem(new MapPlaytestChoice(map, "annihilation", 3, "marines")));
        Assert.Null(MapPlaytest.Problem(new MapPlaytestChoice("\"" + map + "\"", "seize", 0, "chaos")));        // pasted with quotes
        Assert.Contains("Choose a saved map", MapPlaytest.Problem(new MapPlaytestChoice("  ", "annihilation", 3, "marines")));
        Assert.Contains("does not exist", MapPlaytest.Problem(new MapPlaytestChoice(t[@"Meine Karten\gone.smmap"], "annihilation", 3, "marines")));
        string scene = t[@"Meine Karten\scene.tscn"]; File.WriteAllText(scene, "");
        Assert.Contains(".smmap", MapPlaytest.Problem(new MapPlaytestChoice(scene, "annihilation", 3, "marines")));
        Assert.NotNull(MapPlaytest.Problem(new MapPlaytestChoice(map, "annihilation", 7, "marines")));
        Assert.NotNull(MapPlaytest.Problem(new MapPlaytestChoice(map, "annihilation", -1, "marines")));
        Assert.NotNull(MapPlaytest.Problem(new MapPlaytestChoice(map, "musou", 3, "marines")));
    }

    [Fact]
    public void A_recorded_run_counts_as_running_only_while_one_of_its_processes_lives_with_its_recorded_start()
    {
        using var t = new TempDir();
        string state = t["state"];
        Directory.CreateDirectory(state);
        Assert.False(MapPlaytest.RecordedRunAlive(state));                       // no run.json
        File.WriteAllText(MapPlaytest.RunFile(state), "\uFEFF{ \"pids\": [ { \"pid\": 11, \"start\": 100 }, { \"pid\": 22, \"start\": 200 } ] }");
        Assert.Equal([(11, 100L), (22, 200L)], MapPlaytest.RecordedProcesses(state));
        Assert.True(MapPlaytest.RecordedRunAlive(state, (pid, start) => pid == 22 && start == 200));
        Assert.False(MapPlaytest.RecordedRunAlive(state, (pid, start) => pid == 22 && start == 201));     // a recycled id
        // PowerShell 5.1 writes a single record as an object, not a list
        File.WriteAllText(MapPlaytest.RunFile(state), "{ \"pids\": { \"pid\": 33, \"start\": 300 } }");
        Assert.Equal([(33, 300L)], MapPlaytest.RecordedProcesses(state));
        File.WriteAllText(MapPlaytest.RunFile(state), "not json");
        Assert.False(MapPlaytest.RecordedRunAlive(state, (_, _) => true));
        // with the real process check: this test's own process, at its recorded start time, is alive; at another time it is not
        using var me = Process.GetCurrentProcess();
        long start0 = me.StartTime.ToFileTimeUtc();
        File.WriteAllText(MapPlaytest.RunFile(state), "{\"pids\":[{\"pid\":" + me.Id + ",\"start\":" + start0 + "}]}");
        Assert.True(MapPlaytest.RecordedRunAlive(state));
        File.WriteAllText(MapPlaytest.RunFile(state), "{\"pids\":[{\"pid\":" + me.Id + ",\"start\":" + (start0 + 1) + "}]}");
        Assert.False(MapPlaytest.RecordedRunAlive(state));
    }

    [Fact]
    public void The_status_line_and_the_outcome_follow_the_script()
    {
        using var t = new TempDir();
        string state = t["state"];
        Assert.Null(MapPlaytest.ReadStatus(state));
        MapPlaytest.WriteStarting(state);
        Assert.Equal("starting", MapPlaytest.ReadStatus(state)!.Stage);
        File.WriteAllText(MapPlaytest.StatusFile(state), "\uFEFFfailed|the server did not start: port in use\r\n");
        var failed = MapPlaytest.ReadStatus(state)!;
        Assert.True(failed.Failed);
        Assert.Equal(("Play test failed: the server did not start: port in use", false), MapPlaytest.Outcome(failed, 1));
        Assert.True(MapPlaytest.Outcome(new MapPlaytestStatus("done", "Play test over", DateTime.UtcNow), 0).Ok);
        var (text, ok) = MapPlaytest.Outcome(new MapPlaytestStatus("bots", "Starting 6 bots...", DateTime.UtcNow), 1);
        Assert.False(ok);
        Assert.Contains("did not run to its end", text);
        Assert.Contains("did not run to its end", MapPlaytest.Outcome(null, 0).Text);
    }

    [Fact]
    public void The_state_folder_and_the_maps_folder_follow_the_data_root()
    {
        string? Env(string k) => k switch { "SM_DATA_DIR" => @"D:\sm data", _ => null };
        Assert.Equal(@"D:\sm data\tmp-build\mapmaker-playtest", MapPlaytest.StateDir(Env));
        Assert.Equal(@"D:\sm data\mapmaker\maps", MapPlaytest.MapsDir(Env));
        string? Env2(string k) => k switch { "SM_DATA_DIR" => @"D:\sm data", MapPlaytest.StateDirVariable => @"D:\elsewhere", _ => null };
        Assert.Equal(@"D:\elsewhere", MapPlaytest.StateDir(Env2));
    }

    static (int Code, string Out) Run(MapmakerLaunch cmd, string stateDir, params string[] more)
    {
        var psi = new ProcessStartInfo(cmd.FileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cmd.WorkingDirectory };
        foreach (var a in cmd.Arguments.Concat(more)) psi.ArgumentList.Add(a);
        psi.Environment.Remove(MapmakerSetup.VersionDirVariable);
        psi.Environment[MapPlaytest.StateDirVariable] = stateDir;
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd() + stderr.Result;
        if (!p.WaitForExit(60_000)) { p.Kill(); throw new TimeoutException("playtest.ps1 did not finish"); }
        return (p.ExitCode, o);
    }

    [Fact]
    public void The_launchers_command_with_Plan_lands_every_argument_in_the_script()
    {
        using var t = new TempDir();
        string v = MakeVersion(t);
        string map = MakeMap(t);
        var cmd = MapPlaytest.StartCommand(v, new MapPlaytestChoice(map, "seize", 2, "chaos"));
        var (code, o) = Run(cmd, t["state"], "-Plan", "-Port", "27705");
        Assert.True(code == 0, o);
        var lines = o.Split('\n').Select(l => l.TrimEnd()).ToArray();
        string server = lines.Single(l => l.StartsWith("PLAN server:", StringComparison.Ordinal));
        Assert.StartsWith("PLAN server: " + Path.Combine(v, "server", "SmServer.exe") + " --bind 127.0.0.1 --port 27705 --mode seize --custom-map \"" + map + "\"", server);
        string bot = lines.Single(l => l.StartsWith("PLAN bot:", StringComparison.Ordinal));
        Assert.Contains(Path.Combine(v, "game", "sm-reimpl.console.exe") + " --headless", bot);
        Assert.Contains("--custom-map \"" + map + "\"", bot);
        string client = lines.Single(l => l.StartsWith("PLAN client:", StringComparison.Ordinal));
        Assert.StartsWith("PLAN client: " + Path.Combine(v, "game", "sm-reimpl.exe") + " -- --connect 127.0.0.1:27705 --custom-map \"" + map + "\"", client);
        Assert.EndsWith("--side chaos --class tactical", client);
        Assert.DoesNotContain("0.0.0.0", o);
        Assert.False(File.Exists(MapPlaytest.RunFile(t["state"])));             // -Plan starts and records nothing
    }

    [Fact]
    public void Run_from_a_versions_own_mapmaker_folder_the_script_finds_that_build_without_being_told()
    {
        using var t = new TempDir();
        string v = MakeVersion(t);
        var cmd = new MapmakerLaunch("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", MapPlaytest.Script(v), "-Locate"], v, new Dictionary<string, string>());
        var (code, o) = Run(cmd, t["state"]);
        Assert.True(code == 0, o);
        Assert.Contains("LOCATE ok|" + v + "|", o);
    }
}
