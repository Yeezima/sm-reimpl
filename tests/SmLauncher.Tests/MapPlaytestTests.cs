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
        string map = t[@"Meine Karten über\" + name];
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
        Assert.Equal(v, cmd.WorkingDirectory);
        // the launcher's Old GPU / safe mode box and its run tag
        var safe = MapPlaytest.StartCommand(v, new MapPlaytestChoice(map, "seize", 2, "chaos"), safeMode: true, runTag: "launcher-1-2");
        Assert.Equal(["-SafeMode", "-RunTag", "launcher-1-2"], safe.Arguments.Skip(cmd.Arguments.Count));
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
        Assert.Contains("does not exist", MapPlaytest.Problem(new MapPlaytestChoice(t[@"Meine Karten über\gone.smmap"], "annihilation", 3, "marines")));
        string scene = t[@"Meine Karten über\scene.tscn"]; File.WriteAllText(scene, "");
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
        // a deliberate Stop ends as stopped, never as the step it cut short
        Assert.Equal((MapPlaytest.StoppedText, true), MapPlaytest.Outcome(new MapPlaytestStatus("stopped", "Play test stopped", DateTime.UtcNow), 0));
        Assert.Equal((MapPlaytest.StoppedText, true), MapPlaytest.Outcome(failed, 1, stopAsked: true));
    }

    static MapPlaytestStatus At(string stage, string text, DateTime t) => new(stage, text, t);
    static readonly (string? Line, bool Bad) None = (null, false);

    [Fact]
    public void The_status_line_is_written_only_when_the_scripts_status_changes_and_an_earlier_runs_line_is_ignored()
    {
        var t0 = new DateTime(2026, 11, 28, 10, 0, 0, DateTimeKind.Utc);
        var w = new MapPlaytestWatch(t0);
        Assert.Equal(None, w.Tick(At("done", "Play test over (old run)", t0.AddMinutes(-5)), false, 0));      // an earlier run's line
        Assert.Equal(((string?)"Starting the server on 127.0.0.1:27701...", false), w.Tick(At("server", "Starting the server on 127.0.0.1:27701...", t0.AddSeconds(1)), false, 0));
        // the same status again (every second while the server starts): the launcher's other lines are not overwritten
        Assert.Equal(None, w.Tick(At("server", "Starting the server on 127.0.0.1:27701...", t0.AddSeconds(1)), false, 0));
        Assert.Equal(None, w.Tick(At("server", "Starting the server on 127.0.0.1:27701...", t0.AddSeconds(1)), false, 0));
        Assert.Equal(((string?)"Starting 6 bots...", false), w.Tick(At("bots", "Starting 6 bots...", t0.AddSeconds(4)), false, 0));
        Assert.Equal(((string?)"the server did not start", true), w.Tick(At("failed", "the server did not start", t0.AddSeconds(5)), false, 0));
        var end = w.Tick(At("failed", "the server did not start", t0.AddSeconds(5)), true, 1);
        Assert.Equal(("Play test failed: the server did not start", true), (end.Line!, end.Bad));
        Assert.True(w.Finished);
        Assert.Equal(None, w.Tick(null, true, 1));        // shown once

        // Stop pressed during start-up: no more progress lines, and the end is "stopped" even if the script last said its server failed
        var s = new MapPlaytestWatch(t0) { StopAsked = false };
        s.Tick(At("server", "Starting the server...", t0.AddSeconds(1)), false, 0);
        s.StopAsked = true;
        Assert.Equal(None, s.Tick(At("failed", "the server did not start", t0.AddSeconds(2)), false, 0));
        Assert.Equal(((string?)MapPlaytest.StoppedText, false), s.Tick(At("failed", "the server did not start", t0.AddSeconds(2)), true, 1));
    }

    [Fact]
    public void The_state_folder_and_the_maps_folder_follow_the_data_root()
    {
        string? Env(string k) => k switch { "SM_DATA_DIR" => @"D:\sm data", _ => null };
        Assert.Equal(@"D:\sm data\tmp-build\mapmaker-playtest", MapPlaytest.StateDir(Env));
        Assert.Equal(@"D:\sm data\mapmaker\maps", MapPlaytest.MapsDir(Env));
        string? Env2(string k) => k switch { "SM_DATA_DIR" => @"D:\sm data", MapPlaytest.StateDirVariable => @"D:\elsewhere", _ => null };
        Assert.Equal(@"D:\elsewhere", MapPlaytest.StateDir(Env2));
        // only a full path counts, as in the script: a relative or blank one is ignored
        string? Env3(string k) => k switch { "SM_DATA_DIR" => @"D:\sm data", MapPlaytest.StateDirVariable => @"relative\dir", _ => null };
        Assert.Equal(@"D:\sm data\tmp-build\mapmaker-playtest", MapPlaytest.StateDir(Env3));
        string? Env4(string k) => k switch { "SM_DATA_DIR" => @"D:\sm data", MapPlaytest.StateDirVariable => "   ", _ => null };
        Assert.Equal(@"D:\sm data\tmp-build\mapmaker-playtest", MapPlaytest.StateDir(Env4));
    }

    [Fact]
    public void The_run_tag_is_read_from_run_json()
    {
        using var t = new TempDir();
        string state = t["state"];
        Directory.CreateDirectory(state);
        Assert.Null(MapPlaytest.RecordedTag(state));
        File.WriteAllText(MapPlaytest.RunFile(state), "{ \"pids\": [ { \"pid\": 1, \"start\": 2 } ], \"tag\": \"launcher-9-99\" }");
        Assert.Equal("launcher-9-99", MapPlaytest.RecordedTag(state));
        File.WriteAllText(MapPlaytest.RunFile(state), "{ \"pids\": [ { \"pid\": 1, \"start\": 2 } ], \"tag\": \"\" }");      // the Mapmaker's run
        Assert.Null(MapPlaytest.RecordedTag(state));
    }

    [Fact]
    public void The_scripts_early_exit_window_and_safe_mode_options_match_the_launchers()
    {
        string text = File.ReadAllText(ScriptSource());
        Assert.Contains($"$EarlyExitSeconds = {SafeModePolicy.EarlyExitWindow.TotalSeconds:0}", text);
        Assert.Contains("$SafeEngineArgs = @(" + string.Join(", ", SafeModePolicy.EngineArgs.Select(a => $"'{a}'")) + ")", text);
        Assert.Contains($"$SafeGameArg = '{SafeModePolicy.GameArg}'", text);
    }

    static (int Code, string Out) Run(MapmakerLaunch cmd, string stateDir, params string[] more) => RunEnv(cmd, stateDir, null, more);

    static (int Code, string Out) RunEnv(MapmakerLaunch cmd, string stateDir, Dictionary<string, string>? env, params string[] more)
    {
        var psi = new ProcessStartInfo(cmd.FileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cmd.WorkingDirectory };
        foreach (var a in cmd.Arguments.Concat(more)) psi.ArgumentList.Add(a);
        psi.Environment.Remove(MapmakerSetup.VersionDirVariable);
        psi.Environment[MapPlaytest.StateDirVariable] = stateDir;
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
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
        Assert.True(Directory.Exists(t["state"]));                               // SM_PLAYTEST_STATE_DIR was taken
        Assert.False(File.Exists(MapPlaytest.RunFile(t["state"])));             // -Plan starts and records nothing
    }

    [Fact]
    public void With_the_safe_mode_box_only_the_game_window_gets_the_compatibility_renderer_and_safe_mode()
    {
        using var t = new TempDir();
        string v = MakeVersion(t);
        string map = MakeMap(t);
        string Line(string[] lines, string kind) => lines.Single(l => l.StartsWith(kind, StringComparison.Ordinal));
        foreach (bool safe in new[] { false, true })
        {
            var cmd = MapPlaytest.StartCommand(v, new MapPlaytestChoice(map, "annihilation", 1, "marines"), safeMode: safe);
            var (code, o) = Run(cmd, t["state"], "-Plan", "-Port", "27706");
            Assert.True(code == 0, o);
            var lines = o.Split('\n').Select(l => l.TrimEnd()).ToArray();
            string client = Line(lines, "PLAN client:");
            string game = "PLAN client: " + Path.Combine(v, "game", "sm-reimpl.exe") + " ";
            if (safe)
            {
                Assert.StartsWith(game + "--rendering-method gl_compatibility --rendering-driver opengl3 -- --connect 127.0.0.1:27706 ", client);
                Assert.EndsWith(" --class tactical --safe-mode", client);
            }
            else
            {
                Assert.StartsWith(game + "-- --connect 127.0.0.1:27706 ", client);
                Assert.DoesNotContain("gl_compatibility", client);
                Assert.DoesNotContain("--safe-mode", client);
            }
            string bot = Line(lines, "PLAN bot:");
            Assert.Contains(" --headless ", bot);
            Assert.DoesNotContain("gl_compatibility", bot);      // the bots stay headless either way
            Assert.DoesNotContain("--safe-mode", bot);
        }
    }

    [Fact]
    public void Stop_leaves_the_marker_and_clears_a_record_whose_process_is_gone()
    {
        using var t = new TempDir();
        string v = MakeVersion(t);
        string state = t["state"];
        Directory.CreateDirectory(state);
        using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        gone.WaitForExit();
        long start = gone.StartTime.ToFileTimeUtc();
        File.WriteAllText(MapPlaytest.RunFile(state), "{\"pids\":[{\"pid\":" + gone.Id + ",\"start\":" + start + "}],\"tag\":\"launcher-1-2\"}");
        var (code, o) = Run(MapPlaytest.StopCommand(v), state);
        Assert.True(code == 0, o);
        Assert.Contains("Stopped 0 process(es)", o);
        Assert.False(File.Exists(MapPlaytest.RunFile(state)));
        Assert.True(File.Exists(MapPlaytest.StopFile(state)));                  // a run still starting sees it between its steps
        Assert.False(MapPlaytest.RecordedRunAlive(state));
    }

    [Fact]
    public void Stop_with_a_run_tag_stops_only_the_run_recorded_under_that_tag()
    {
        using var t = new TempDir();
        string v = MakeVersion(t);
        string state = t["state"];
        Directory.CreateDirectory(state);
        using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        gone.WaitForExit();
        string record = "{\"pids\":[{\"pid\":" + gone.Id + ",\"start\":" + gone.StartTime.ToFileTimeUtc() + "}],\"tag\":\"\"}";      // a Mapmaker run: no tag
        File.WriteAllText(MapPlaytest.RunFile(state), record);
        var (c1, o1) = Run(MapPlaytest.StopCommand(v, "launcher-1-2"), state);
        Assert.True(c1 == 0, o1);
        Assert.Contains("Not stopped", o1);
        Assert.True(File.Exists(MapPlaytest.RunFile(state)));                   // the other run's record is left alone
        Assert.False(File.Exists(MapPlaytest.StopFile(state)));                 // and no marker that would stop it
        File.WriteAllText(MapPlaytest.RunFile(state), record.Replace("\"tag\":\"\"", "\"tag\":\"launcher-1-2\""));
        var (c2, o2) = Run(MapPlaytest.StopCommand(v, "launcher-1-2"), state);
        Assert.True(c2 == 0, o2);
        Assert.Contains("Stopped 0 process(es)", o2);
        Assert.False(File.Exists(MapPlaytest.RunFile(state)));
        // a plain -Stop (no tag) keeps today's behaviour: whatever run is recorded
        File.WriteAllText(MapPlaytest.RunFile(state), record);
        var (c3, o3) = Run(MapPlaytest.StopCommand(v), state);
        Assert.True(c3 == 0, o3);
        Assert.False(File.Exists(MapPlaytest.RunFile(state)));
        // a tag that is not letters, digits, '.', '_' or '-' is refused before anything happens
        Assert.NotEqual(0, Run(MapPlaytest.StopCommand(v), state, "-RunTag", "bad tag;rm").Code);
    }

    [Fact]
    public void A_stop_marker_left_by_an_earlier_stop_does_not_stop_the_next_run()
    {
        using var t = new TempDir();
        string v = MakeVersion(t);                 // placeholder executables: the run fails when it starts the server, after its start-up clean-up
        string state = t["state"];
        Directory.CreateDirectory(state);
        File.WriteAllText(MapPlaytest.StopFile(state), "earlier");
        var cmd = MapPlaytest.StartCommand(v, new MapPlaytestChoice(MakeMap(t), "annihilation", 0, "marines"), runTag: "launcher-3-4");
        var (code, o) = Run(cmd, state, "-Headless", "-Seconds", "1");
        Assert.Equal(1, code);
        Assert.Contains("PLAYTEST failed", o);                                  // not "stopped": the stale marker was cleared at the start
        Assert.DoesNotContain("PLAYTEST stopped", o);
        Assert.Equal("failed", MapPlaytest.ReadStatus(state)!.Stage);
        Assert.False(File.Exists(MapPlaytest.StopFile(state)));
        Assert.False(File.Exists(MapPlaytest.RunFile(state)));                  // nothing was recorded
    }

    [Theory]
    [InlineData(0, 3.0, false, "")]                 // closed normally
    [InlineData(1, 30.0, false, "")]                // an error after playing a while: not "right away"
    [InlineData(1, 8.0, false, "Tick \"Old GPU / safe mode\"")]
    [InlineData(-1073741819, 2.5, false, "Tick \"Old GPU / safe mode\"")]
    [InlineData(1, 2.0, true, "even in Old GPU / safe mode")]
    public void A_game_that_closes_with_an_error_right_away_gets_the_safe_mode_hint(int exitCode, double seconds, bool safe, string expected)
    {
        using var t = new TempDir();
        string v = MakeVersion(t);
        var cmd = new MapmakerLaunch("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", MapPlaytest.Script(v), .. (safe ? new[] { "-SafeMode" } : [])], v, new Dictionary<string, string>());
        var (code, o) = RunEnv(cmd, t["state"], new() { ["SM_PLAYTEST_EARLY_EXIT_CHECK"] = $"{exitCode},{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}" });
        Assert.True(code == 0, o);
        string line = o.Split('\n').Single(l => l.StartsWith("EARLY", StringComparison.Ordinal)).TrimEnd();
        if (expected.Length == 0) Assert.Equal("EARLY", line);
        else
        {
            Assert.Contains("the game closed right after it started (exit code " + exitCode, line);
            Assert.Contains("). ", line);                                       // a full stop before the hint
            Assert.Contains(expected, line);
        }
        Assert.False(Directory.Exists(t["state"]));                              // the check touches nothing
    }

    [Fact]
    public void A_replaced_run_ends_with_its_own_exit_code_and_the_launcher_says_so()
    {
        Assert.Equal((MapPlaytest.ReplacedText, true), MapPlaytest.Outcome(new MapPlaytestStatus("server", "the new run starts", DateTime.UtcNow), MapPlaytest.ReplacedExitCode));
        string text = File.ReadAllText(ScriptSource());
        Assert.Contains("elseif ($Replaced) { " + MapPlaytest.ReplacedExitCode + " }", text);
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
