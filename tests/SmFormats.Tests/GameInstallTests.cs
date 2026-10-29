using System.Text.Json;
using SmContent;
using Xunit;

namespace SmFormats.Tests;

/// <summary>Game folder detection (first run): the VDF parser, Steam libraries, the settings file and the order of the places. Synthetic folders in temp only; no original bytes.</summary>
public sealed class GameInstallTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "sm-gameinstall-" + Guid.NewGuid().ToString("N"));

    public GameInstallTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    string Dir(params string[] parts) => Directory.CreateDirectory(Path.Combine(new[] { _root }.Concat(parts).ToArray())).FullName;

    /// <summary>A fake install: data/packages/ebps.oppc (empty file: only existence is checked).</summary>
    string FakeInstall(params string[] parts)
    {
        string dir = Dir(parts);
        Directory.CreateDirectory(Path.Combine(dir, "data", "packages"));
        File.WriteAllBytes(Path.Combine(dir, "data", "packages", "ebps.oppc"), []);
        return dir;
    }

    static string Json(string path) => path.Replace("\\", "\\\\");

    static string Vdf(string path) => path.Replace("\\", "\\\\");

    static GameLocation? Locate(string data, string? explicitDir = null, string? repo = null, Dictionary<string, string>? env = null, params string[] steamRoots) =>
        GameInstall.TryLocate(explicitDir, repo, data, k => env is not null && env.TryGetValue(k, out var v) ? v : null, () => steamRoots);

    // ---- VDF ----

    [Fact]
    public void Vdf_parses_blocks_values_comments_and_escapes()
    {
        const string text = "// header\n\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"55150\"\t\t\"123\"\n\t\t}\n\t}\n\t\"1\" \"D:\\\\Lib\"\n}\n";
        Assert.True(VdfParser.TryParse(text, out var root));
        var folders = root.Get("libraryfolders");
        Assert.NotNull(folders);
        Assert.Equal(@"C:\Program Files (x86)\Steam", folders!.Get("0")!.GetString("path"));
        Assert.Equal("123", folders.Get("0")!.Get("apps")!.GetString("55150"));
        Assert.Equal(@"D:\Lib", folders.GetString("1"));
        Assert.Equal(@"D:\Lib", root.Get("LIBRARYFOLDERS")!.GetString("1"));   // keys are case-insensitive
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"a\"")]
    [InlineData("\"a\" {")]
    [InlineData("\"a\" { \"b\" }")]
    [InlineData("}")]
    [InlineData("\"a\" \"unterminated")]
    [InlineData("\"a\" { \"b\" { \"c\" ")]
    public void Vdf_rejects_malformed_text_without_throwing(string text)
    {
        bool ok = VdfParser.TryParse(text, out var root);
        if (text.Length == 0) Assert.True(ok);
        else Assert.False(ok);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void Vdf_deep_nesting_is_refused()
    {
        string text = string.Concat(Enumerable.Repeat("\"k\" {", 200)) + string.Concat(Enumerable.Repeat("}", 200));
        Assert.False(VdfParser.TryParse(text, out _));
    }

    // ---- validation ----

    [Fact]
    public void Validate_needs_data_and_the_sanity_archive()
    {
        Assert.NotNull(GameInstall.Validate(null));
        Assert.NotNull(GameInstall.Validate(Path.Combine(_root, "nope")));
        string empty = Dir("empty");
        Assert.Contains("data", GameInstall.Validate(empty));
        string dataOnly = Dir("dataonly");
        Directory.CreateDirectory(Path.Combine(dataOnly, "data"));
        Assert.Contains("ebps.oppc", GameInstall.Validate(dataOnly));
        Assert.Null(GameInstall.Validate(FakeInstall("good")));
    }

    [Fact]
    public void Normalize_turns_the_data_folder_into_the_game_folder()
    {
        string game = FakeInstall("game");
        Assert.Equal(game, GameInstall.Normalize(Path.Combine(game, "data")));
        Assert.Equal(game, GameInstall.Normalize(game));
        string other = Dir("other", "data");
        Assert.Equal(other, GameInstall.Normalize(other));
    }

    // ---- Steam ----

    [Fact]
    public void Steam_finds_the_game_in_a_second_library_via_the_manifest()
    {
        string steam = Dir("Steam");
        string lib2 = Dir("SteamLibrary");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n\"0\"\n{{\n\"path\" \"{Vdf(steam)}\"\n\"apps\" {{ \"228980\" \"1\" }}\n}}\n\"1\"\n{{\n\"path\" \"{Vdf(lib2)}\"\n\"apps\" {{ \"55150\" \"1\" }}\n}}\n}}\n");
        string game = FakeInstall("SteamLibrary", "steamapps", "common", "Space Marine Custom Name");
        File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_55150.acf"), "\"AppState\"\n{\n\"appid\" \"55150\"\n\"installdir\" \"Space Marine Custom Name\"\n}\n");

        var found = Locate(Dir("data"), steamRoots: steam);
        Assert.NotNull(found);
        Assert.Equal(game, found!.Dir);
        Assert.Equal("Steam library " + lib2, found.Source);
    }

    [Fact]
    public void Steam_old_libraryfolders_layout_and_default_folder_name_without_a_manifest()
    {
        string steam = Dir("Steam");
        string lib2 = Dir("OldLib");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $"\"LibraryFolders\"\n{{\n\"TimeNextStatsReport\" \"1700000000\"\n\"1\" \"{Vdf(lib2)}\"\n}}\n");
        string game = FakeInstall("OldLib", "steamapps", "common", GameInstall.DefaultInstallDir);
        var found = Locate(Dir("data"), steamRoots: steam);
        Assert.Equal(game, found?.Dir);
    }

    [Fact]
    public void Steam_installdir_with_a_path_is_not_trusted_and_a_bad_or_missing_install_is_skipped()
    {
        string steam = Dir("Steam");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        string outside = FakeInstall("outside");
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_55150.acf"), "\"AppState\" { \"installdir\" \"..\\\\..\\\\outside\" }");
        Assert.Null(Locate(Dir("data"), steamRoots: steam));
        // A manifest whose folder lacks the archive is not a hit either.
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_55150.acf"), "\"AppState\" { \"installdir\" \"Half\" }");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common", "Half", "data"));
        Assert.Null(Locate(Dir("data"), steamRoots: steam));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void Steam_corrupt_vdf_and_missing_roots_are_harmless()
    {
        string steam = Dir("Steam");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"0\" {");
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_55150.acf"), "garbage \"");
        Assert.Null(Locate(Dir("data"), steamRoots: [steam, Path.Combine(_root, "does-not-exist"), ""]));
    }

    [Fact]
    public void Steam_libraries_are_listed_once()
    {
        string steam = Dir("Steam");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Vdf(steam)}\" }} }}");
        Assert.Single(GameInstall.SteamLibraries([steam, steam]));
    }

    [Fact]
    public void SM_STEAM_DIR_replaces_the_machine_lookup()
    {
        Assert.Empty(GameInstall.SteamRootsFor(k => k == GameInstall.SteamDirEnvVar ? "" : null));
        var two = GameInstall.SteamRootsFor(k => k == GameInstall.SteamDirEnvVar ? "a" + Path.PathSeparator + " b " : null).ToList();
        Assert.Equal(["a", "b"], two);
    }


    // ---- settings.json and the order ----

    [Fact]
    public void Remember_writes_settings_atomically_and_Locate_reads_it()
    {
        string data = Dir("data");
        string game = FakeInstall("mygame");
        Assert.Null(GameInstall.Remember(game, data));
        Assert.False(File.Exists(Path.Combine(data, "settings.json.tmp")));
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "settings.json"))))
            Assert.Equal(game, doc.RootElement.GetProperty("gameDir").GetString());
        var found = Locate(data);
        Assert.Equal(game, found?.Dir);
        Assert.Equal("your saved choice", found!.Source);
    }

    [Fact]
    public void Remember_keeps_other_keys_replaces_corrupt_files_and_refuses_bad_folders()
    {
        string data = Dir("data");
        string game = FakeInstall("mygame");
        File.WriteAllText(Path.Combine(data, "settings.json"), "{\"other\": 5, \"gameDir\": \"old\"}");
        Assert.Null(GameInstall.Remember(game, data));
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "settings.json"))))
        {
            Assert.Equal(5, doc.RootElement.GetProperty("other").GetInt32());
            Assert.Equal(game, doc.RootElement.GetProperty("gameDir").GetString());
        }
        File.WriteAllText(Path.Combine(data, "settings.json"), "{ not json");
        Assert.Null(GameInstall.Remember(game, data));
        Assert.Equal(game, Locate(data)?.Dir);
        Assert.NotNull(GameInstall.Remember(Dir("notagame"), data));
    }

    [Fact]
    public void Remember_never_writes_inside_the_game_folder()
    {
        string game = FakeInstall("mygame");
        string insideData = Path.Combine(game, "sm-data");
        var message = GameInstall.Remember(game, insideData);
        Assert.NotNull(message);
        Assert.False(Directory.Exists(insideData));
        Assert.False(File.Exists(Path.Combine(game, "settings.json")));
    }

    [Fact]
    public void A_stale_settings_choice_falls_through_to_steam()
    {
        string data = Dir("data");
        string steam = Dir("Steam");
        string game = FakeInstall("Steam", "steamapps", "common", GameInstall.DefaultInstallDir);
        File.WriteAllText(Path.Combine(data, "settings.json"), $"{{\"gameDir\": \"{Json(Path.Combine(_root, "gone"))}\"}}");
        Assert.Equal(game, Locate(data, steamRoots: steam)?.Dir);
    }

    [Fact]
    public void Order_is_explicit_then_env_then_local_json_then_settings_then_discovery_then_steam()
    {
        string data = Dir("data");
        string explicitDir = FakeInstall("explicit"), envDir = FakeInstall("env"), localDir = FakeInstall("local"), savedDir = FakeInstall("saved"), discoveredDir = FakeInstall("discovered");
        string steam = Dir("Steam");
        FakeInstall("Steam", "steamapps", "common", GameInstall.DefaultInstallDir);
        string repo = Dir("repo");
        Directory.CreateDirectory(Path.Combine(repo, "config"));
        File.WriteAllText(Path.Combine(repo, "config", "local.json"), $"{{\"gameDir\": \"{Json(localDir)}\"}}");
        Assert.Null(GameInstall.Remember(savedDir, data));
        Directory.CreateDirectory(Path.Combine(data, "discovery", "20260101-000000"));
        File.WriteAllText(Path.Combine(data, "discovery", "20260101-000000", "game-dir.txt"), discoveredDir);
        var env = new Dictionary<string, string> { ["SM_GAME_DIR"] = envDir };

        Assert.Equal(explicitDir, Locate(data, explicitDir, repo, env, steam)?.Dir);
        Assert.Equal(envDir, Locate(data, null, repo, env, steam)?.Dir);
        Assert.Equal(localDir, Locate(data, null, repo, null, steam)?.Dir);
        Assert.Equal(savedDir, Locate(data, null, null, null, steam)?.Dir);
        File.Delete(Path.Combine(data, "settings.json"));
        Assert.Equal(discoveredDir, Locate(data, null, null, null, steam)?.Dir);
        Directory.Delete(Path.Combine(data, "discovery"), true);
        Assert.StartsWith("Steam library", Locate(data, null, null, null, steam)?.Source);
    }

    [Fact]
    public void Nothing_found_returns_null_and_Locate_throws_the_documented_message()
    {
        string data = Dir("data");
        string steam = Dir("emptysteam");
        Assert.Null(Locate(data, steamRoots: steam));
        var thrown = Assert.Throws<DirectoryNotFoundException>(() => GameInstall.Locate(null, null, data, _ => null, () => [steam]));
        Assert.Equal(GameInstall.NotFoundMessage, thrown.Message);
        Assert.Contains("Steam", thrown.Message);
        string found = FakeInstall("found");
        Assert.Equal(found, GameInstall.Locate(found, null, data, _ => null, () => [steam]));
    }

    [Fact]
    public void An_unreadable_discovery_folder_is_skipped_not_thrown()
    {
        string data = Dir("data");
        File.WriteAllText(Path.Combine(data, "discovery"), "a file where a folder should be");   // Directory.GetDirectories would throw
        string game = FakeInstall("Steam", "steamapps", "common", GameInstall.DefaultInstallDir);
        Assert.Equal(game, Locate(data, steamRoots: Path.Combine(_root, "Steam"))?.Dir);
    }

    [Fact]
    public void Oversized_text_files_are_not_read()
    {
        string data = Dir("data");
        string steam = Dir("Steam");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        string game = FakeInstall("Steam", "steamapps", "common", "Elsewhere");
        string acf = "\"AppState\" { \"installdir\" \"Elsewhere\" }";
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_55150.acf"), acf + new string(' ', (int)GameInstall.MaxTextFileBytes + 10));
        Assert.Null(Locate(data, steamRoots: steam));   // the manifest is ignored; the default folder name does not exist
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_55150.acf"), acf);
        Assert.Equal(game, Locate(data, steamRoots: steam)?.Dir);
    }

    /// <summary>A symbolic link, or on Windows (where that needs developer mode) a junction made with mklink /J; false when neither can be made.</summary>
    static bool TryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return Directory.Exists(link); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(info)!;
            p.WaitForExit(10000);
            return Directory.Exists(link);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    [Fact]
    public void Remember_resolves_a_data_folder_that_links_into_the_game_folder()
    {
        string game = FakeInstall("mygame");
        string inner = Path.Combine(game, "inner");
        Directory.CreateDirectory(inner);
        string link = Path.Combine(_root, "datalink");
        if (!TryLink(link, inner)) return;   // links not allowed here
        try
        {
            Assert.Equal(GameInstall.RealPath(inner), GameInstall.RealPath(link));
            Assert.NotNull(GameInstall.Remember(game, link));
            Assert.False(File.Exists(Path.Combine(inner, "settings.json")));
        }
        finally { Directory.Delete(link, false); }   // removes the link itself, never what it points at (the recursive clean-up in Dispose would be refused)
    }
}

