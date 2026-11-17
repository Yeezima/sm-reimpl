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

    static (int Code, string Out) Run(string script, string? versionEnv, params string[] args)
    {
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(args)) psi.ArgumentList.Add(a);
        psi.Environment.Remove(MapmakerSetup.VersionDirVariable);
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
}
