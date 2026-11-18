using System.Diagnostics;
using SmLauncher.Core;

namespace SmLauncher.Tests;

/// <summary>
/// The Mapmaker's Play test in the tester install (docs/releasing.md "The Mapmaker"): the launcher hands the editor the build's folder, and the shipped
/// <c>mapmaker\playtest.ps1</c> finds the installed server and game from it (or from the install's current.json). Synthetic files; the script is run only with -Locate
/// (it starts nothing).
/// </summary>
public class MapmakerPlaytestTests
{
    static string? ScriptSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, "mapmaker", "playtest.ps1");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>An install: root\current.json, root\versions\tag\{server,game} with placeholder executables, and the script in root\mapmaker-project.</summary>
    static (string Script, string Version) MakeInstall(TempDir t, bool withConsole = true)
    {
        string source = ScriptSource() ?? throw new FileNotFoundException("mapmaker/playtest.ps1 was not found above the test folder");
        string v = t[@"root\versions\tester-2026-11-18-abc1234"];
        Directory.CreateDirectory(Path.Combine(v, "server")); Directory.CreateDirectory(Path.Combine(v, "game"));
        File.WriteAllText(Path.Combine(v, "server", "SmServer.exe"), "x");
        File.WriteAllText(Path.Combine(v, "game", "sm-reimpl.exe"), "x");
        if (withConsole) File.WriteAllText(Path.Combine(v, "game", "sm-reimpl.console.exe"), "x");
        File.WriteAllText(t[@"root\current.json"], "{ \"schema\": 1, \"current\": \"tester-2026-11-18-abc1234\", \"versions\": {} }");
        string script = t[@"root\mapmaker-project\playtest.ps1"];
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.Copy(source, script);
        return (script, v);
    }

    static (int Code, string Out) Run(string script, string? versionEnv, params string[] args) => RunWith(script, versionEnv, null, args);

    static (int Code, string Out) RunWith(string script, string? versionEnv, string? dataDir, params string[] args)
    {
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(args)) psi.ArgumentList.Add(a);
        psi.Environment.Remove(MapmakerSetup.VersionDirVariable);
        if (dataDir is not null) psi.Environment["SM_DATA_DIR"] = dataDir;
        if (versionEnv is not null) psi.Environment[MapmakerSetup.VersionDirVariable] = versionEnv;
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60_000)) { p.Kill(); throw new TimeoutException("playtest.ps1 -Locate did not finish"); }
        return (p.ExitCode, o);
    }

    [Fact]
    public void The_editor_gets_the_build_folder_so_the_play_test_finds_the_installed_game()
    {
        using var t = new TempDir();
        var setup = new MapmakerSetup(new InstallLayout(t[@"root"])) { RuntimeRoots = () => [] };
        var cmd = setup.EditorCommand(versionDir: t[@"root\versions\tag"]);
        Assert.Equal(Path.GetFullPath(t[@"root\versions\tag"]), cmd.Environment[MapmakerSetup.VersionDirVariable]);
        Assert.False(setup.EditorCommand().Environment.ContainsKey(MapmakerSetup.VersionDirVariable));
    }

    [Fact]
    public void The_build_must_ship_the_play_test_script()
    {
        Assert.Contains(MapmakerSetup.PlaytestScript, MapmakerSetup.Needed);
        Assert.NotNull(ScriptSource());
    }

    [Fact]
    public void Locate_finds_the_server_and_game_from_the_variable_the_launcher_sets()
    {
        using var t = new TempDir();
        var (script, v) = MakeInstall(t);
        File.Delete(t[@"root\current.json"]);                      // the variable alone is enough
        var (code, o) = Run(script, v, "-Locate");
        Assert.Equal(0, code);
        Assert.Contains("LOCATE ok|" + v, o);
        Assert.Contains(@"server\SmServer.exe", o);
        Assert.Contains("sm-reimpl.console.exe", o);
    }

    [Fact]
    public void Locate_falls_back_to_the_installs_current_json_beside_mapmaker_project()
    {
        using var t = new TempDir();
        var (script, v) = MakeInstall(t);
        var (code, o) = Run(script, null, "-Locate");
        Assert.Equal(0, code);
        Assert.Contains("LOCATE ok|" + v, o);
    }

    [Fact]
    public void Locate_names_the_missing_file_and_says_to_open_the_mapmaker_from_the_launcher()
    {
        using var t = new TempDir();
        var (script, _) = MakeInstall(t, withConsole: false);
        var (code, o) = Run(script, null, "-Locate");
        Assert.Equal(1, code);
        Assert.Contains("game files not found", o);
        Assert.Contains("sm-reimpl.console.exe", o);
        Assert.Contains("MAPMAKER button", o);
    }

    [Fact]
    public void Locate_without_any_install_information_fails_in_plain_words()
    {
        using var t = new TempDir();
        var (script, _) = MakeInstall(t);
        File.Delete(t[@"root\current.json"]);
        var (code, o) = Run(script, null, "-Locate");
        Assert.Equal(1, code);
        Assert.Contains("game files not found", o);
        Assert.Contains("install folder is not known", o);
    }

    /// <summary>An install and a map whose folders hold a space, an apostrophe and non-ASCII letters (a Windows user name, "SM Reimpl").</summary>
    static (string Script, string Version, string Map) MakeAwkwardInstall(TempDir t)
    {
        string source = ScriptSource()!;
        string v = t["Mein Ordnär O'Neil\\versions\\tester-2026-11-18-abc1234"];
        Directory.CreateDirectory(Path.Combine(v, "server")); Directory.CreateDirectory(Path.Combine(v, "game"));
        foreach (var f in new[] { @"server\SmServer.exe", @"game\sm-reimpl.exe", @"game\sm-reimpl.console.exe" }) File.WriteAllText(Path.Combine(v, f), "x");
        string script = t["Mein Ordnär O'Neil\\mapmaker-project\\playtest.ps1"];
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.Copy(source, script);
        string map = t["Meine Karten über\\my map's.smmap"];
        Directory.CreateDirectory(Path.GetDirectoryName(map)!);
        File.WriteAllText(map, "{}");
        return (script, v, map);
    }

    [Fact]
    public void The_visible_client_gets_its_arguments_after_the_double_dash_like_Play_cmd_does()
    {
        using var t = new TempDir();
        var (script, v) = MakeInstall(t);
        string map = t[@"maps\a.smmap"]; Directory.CreateDirectory(Path.GetDirectoryName(map)!); File.WriteAllText(map, "{}");
        var (code, o) = Run(script, v, "-Plan", "-Map", map, "-Side", "chaos", "-Class", "raptor", "-Port", "27705");
        Assert.Equal(0, code);
        var lines = o.Split('\n');
        var client = lines.Single(l => l.StartsWith("PLAN client:"));
        // the exact chosen file goes to the client and the bots too (a lookup by name could find another file of the same map name: hash mismatch)
        Assert.Contains(@"sm-reimpl.exe -- --connect 127.0.0.1:27705 --custom-map ", client);
        Assert.EndsWith(" --side chaos --class raptor", client.TrimEnd());
        Assert.Contains(Path.GetFullPath(map), client);
        var bot = lines.Single(l => l.StartsWith("PLAN bot:"));
        Assert.Contains(" -- --connect 127.0.0.1:27705 --custom-map ", bot);
        Assert.Contains(Path.GetFullPath(map), bot);
        Assert.Contains("--bind 127.0.0.1 --port 27705 --mode annihilation --custom-map", lines.Single(l => l.StartsWith("PLAN server:")));
        Assert.DoesNotContain("0.0.0.0", o);
    }

    [Fact]
    public void Paths_with_spaces_apostrophes_and_non_ASCII_letters_are_quoted_whole()
    {
        using var t = new TempDir();
        var (script, v, map) = MakeAwkwardInstall(t);
        var (code, o) = Run(script, v, "-Plan", "-Map", map);
        Assert.Equal(0, code);
        Assert.DoesNotContain("failed", o);
        Assert.Matches("--custom-map \".*my map's\\.smmap\"", o);       // one quoted token
        Assert.Matches("PLAN server: .*SmServer.exe --bind", o);
        var (lc, lo) = Run(script, v, "-Locate");
        Assert.Equal(0, lc);
        Assert.StartsWith("LOCATE ok|", lo);
    }

    [Fact]
    public void An_unknown_side_or_a_port_out_of_range_is_refused_before_anything_starts()
    {
        using var t = new TempDir();
        var (script, v) = MakeInstall(t);
        Assert.NotEqual(0, Run(script, v, "-Plan", "-Map", script, "-Side", "orks").Code);
        Assert.NotEqual(0, Run(script, v, "-Plan", "-Map", script, "-Port", "80").Code);
    }

    [Fact]
    public void Stop_ends_a_recorded_process_only_when_its_start_time_matches()
    {
        using var t = new TempDir();
        var (script, _) = MakeInstall(t);
        string data = t["data"];
        string state = Path.Combine(data, "tmp-build", "mapmaker-playtest");
        Directory.CreateDirectory(state);
        using var victim = Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "-NoProfile", "-Command", "Start-Sleep -Seconds 60" } })!;
        try
        {
            long start = victim.StartTime.ToFileTimeUtc();
            // a recycled id: the same pid with another start time is left alone
            File.WriteAllText(Path.Combine(state, "run.json"), "{\"pids\":[{\"pid\":" + victim.Id + ",\"start\":" + (start + 12345) + "}]}");
            var (c1, o1) = RunWith(script, null, data, "-Stop");
            Assert.Equal(0, c1);
            Assert.Contains("Stopped 0", o1);
            Assert.False(victim.HasExited);
            File.WriteAllText(Path.Combine(state, "run.json"), "{\"pids\":[{\"pid\":" + victim.Id + ",\"start\":" + start + "}]}");
            var (c2, o2) = RunWith(script, null, data, "-Stop");
            Assert.Equal(0, c2);
            Assert.Contains("Stopped 1", o2);
            Assert.True(victim.WaitForExit(10_000));
        }
        finally { if (!victim.HasExited) victim.Kill(); }     // only the process this test started
    }
}
