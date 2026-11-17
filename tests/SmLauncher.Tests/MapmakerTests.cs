using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SmLauncher.Core;

namespace SmLauncher.Tests;

/// <summary>The launcher's MAPMAKER button (docs/releasing.md "The Mapmaker"): the shipped project, the command lines, the project copy and the pinned, checked downloads. Synthetic files only.</summary>
public class MapmakerTests
{
    /// <summary>A version folder shaped like a build's: mapmaker\ with the files the editor needs (placeholder text, no game data).</summary>
    static string MakeVersion(TempDir t, string tag = "tester-2026-11-17-abcdef1", string dllText = "dll v1", params string[] skip)
    {
        string v = t[@"root\versions\" + tag];
        foreach (var rel in MapmakerSetup.Needed.Concat([@"addons\sm_mapmaker\Other.cs", @"bin\SmSim.dll"]).Where(r => !skip.Contains(r)))
        {
            string p = Path.Combine(v, MapmakerSetup.ShippedDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, rel.StartsWith("bin", StringComparison.Ordinal) ? dllText : "text " + rel);
        }
        return v;
    }

    static MapmakerSetup Setup(TempDir t, params string[] runtimeRoots) => new(new InstallLayout(t[@"root"])) { RuntimeRoots = () => runtimeRoots };

    static string FakeRuntime(TempDir t, string version = "10.0.5")
    {
        string root = t[@"dotnet-" + version];
        Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", version));
        Directory.CreateDirectory(Path.Combine(root, "host", "fxr", version));
        return root;
    }

    [Fact]
    public void A_build_without_the_project_or_with_a_file_missing_is_refused_in_plain_words()
    {
        using var t = new TempDir();
        string none = t[@"root\versions\a"];
        Directory.CreateDirectory(none);
        Assert.Contains("no Mapmaker folder", MapmakerSetup.CheckShipped(none));
        string partial = MakeVersion(t, "tester-2026-11-17-0000001", skip: @"bin\Mapmaker.dll");
        Assert.Contains(@"bin\Mapmaker.dll", MapmakerSetup.CheckShipped(partial));
        Assert.Null(MapmakerSetup.CheckShipped(MakeVersion(t)));
    }

    [Fact]
    public void The_plan_says_what_is_missing()
    {
        using var t = new TempDir();
        string v = MakeVersion(t);
        var plan = Setup(t).Plan(v);
        Assert.True(plan.ShippedOk);
        Assert.True(plan.NeedGodot);
        Assert.True(plan.NeedDotNet);
        Assert.True(plan.NeedSync);
        Assert.True(plan.NeedImport);
        Assert.Equal(MapmakerTools.Godot.Size + MapmakerTools.DotNet.Size, plan.DownloadBytes);
        // a .NET 10 runtime on the PC: no second download
        var withRuntime = Setup(t, FakeRuntime(t)).Plan(v);
        Assert.False(withRuntime.NeedDotNet);
        // only a .NET 8 runtime: not enough for Godot 4.7
        Assert.True(Setup(t, FakeRuntime(t, "8.0.30")).Plan(v).NeedDotNet);
    }

    [Fact]
    public void The_editor_command_opens_the_work_project_visibly_and_the_import_is_hidden()
    {
        using var t = new TempDir();
        var setup = Setup(t, FakeRuntime(t));
        var editor = setup.EditorCommand();
        Assert.Equal(["--editor", "--path", setup.WorkProject], editor.Arguments);
        Assert.EndsWith("Godot_v4.7.2-stable_mono_win64.exe", editor.FileName);
        Assert.StartsWith(setup.GodotDir, editor.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(editor.Environment);                                // the PC's own runtime is used
        Assert.Equal(setup.WorkProject, editor.WorkingDirectory);
        Assert.StartsWith(t.Path, setup.WorkProject, StringComparison.OrdinalIgnoreCase);   // under the install folder, never inside the build's folder
        var import = setup.EditorCommand(headlessImport: true);
        Assert.Contains("--headless", import.Arguments);
        Assert.Contains("--quit-after", import.Arguments);
        Assert.EndsWith("_console.exe", import.FileName);
        var safe = setup.EditorCommand(safeMode: true);
        Assert.Contains("gl_compatibility", safe.Arguments);
        Assert.DoesNotContain("--headless", safe.Arguments);
        Assert.DoesNotContain("--headless", editor.Arguments);
    }

    [Fact]
    public void Without_a_runtime_on_the_pc_the_private_one_is_pointed_at()
    {
        using var t = new TempDir();
        var setup = Setup(t);
        var cmd = setup.EditorCommand();
        Assert.Equal(setup.DotNetDir, cmd.Environment["DOTNET_ROOT"]);
        Assert.Contains("\"" + setup.WorkProject + "\"", cmd.Describe() + "\"" + setup.WorkProject + "\"");
    }

    [Fact]
    public void Syncing_copies_the_project_puts_assemblies_where_the_editor_loads_them_and_keeps_the_import_cache()
    {
        using var t = new TempDir();
        string v1 = MakeVersion(t, "tester-2026-11-17-0000001", "dll v1");
        var setup = Setup(t);
        Assert.Equal(MapmakerSetup.ShippedFiles(MapmakerSetup.Shipped(v1)).Count, setup.SyncProject(v1));
        Assert.True(File.Exists(Path.Combine(setup.WorkProject, MapmakerSetup.EditorAssemblyDir, "Mapmaker.dll")));
        Assert.False(Directory.Exists(Path.Combine(setup.WorkProject, "bin")));
        Assert.True(File.Exists(Path.Combine(setup.WorkProject, "project.godot")));
        Assert.False(setup.Plan(v1).NeedSync);
        Assert.Equal(0, setup.SyncProject(v1));                          // nothing differs: nothing copied

        // the editor's own cache and an old addon file that a newer build no longer ships
        Directory.CreateDirectory(Path.Combine(setup.WorkProject, ".godot", "imported"));
        File.WriteAllText(Path.Combine(setup.WorkProject, ".godot", "imported", "x.import"), "cache");
        File.WriteAllText(Path.Combine(setup.WorkProject, "addons", "sm_mapmaker", "Gone.cs"), "old");
        Assert.False(setup.Plan(v1).NeedImport);
        string v2 = MakeVersion(t, "tester-2026-11-18-0000002", "dll v2");
        Assert.True(setup.Plan(v2).NeedSync);                            // another build
        int copied = setup.SyncProject(v2);
        Assert.Equal(MapmakerSetup.ShippedFiles(MapmakerSetup.Shipped(v2)).Count(f => f.StartsWith("bin", StringComparison.Ordinal)), copied);
        Assert.Equal("dll v2", File.ReadAllText(Path.Combine(setup.WorkProject, MapmakerSetup.EditorAssemblyDir, "Mapmaker.dll")));
        Assert.False(File.Exists(Path.Combine(setup.WorkProject, "addons", "sm_mapmaker", "Gone.cs")));
        Assert.True(File.Exists(Path.Combine(setup.WorkProject, ".godot", "imported", "x.import")));
    }

    [Fact]
    public void Syncing_a_build_without_the_project_throws_and_writes_nothing()
    {
        using var t = new TempDir();
        string v = t[@"root\versions\a"];
        Directory.CreateDirectory(v);
        var setup = Setup(t);
        Assert.Throws<FileNotFoundException>(() => setup.SyncProject(v));
        Assert.False(Directory.Exists(setup.WorkProject));
    }

    [Fact]
    public void The_pinned_tools_name_official_hosts_and_full_length_hashes()
    {
        foreach (var p in new[] { MapmakerTools.Godot, MapmakerTools.DotNet })
        {
            Assert.Equal(128, p.Sha512.Length);
            Assert.All(p.Sha512, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
            Assert.True(NetPolicy.Tools.IsAllowed(new Uri(p.Url)), p.Url);
            Assert.True(p.Size > 1_000_000);
        }
        Assert.StartsWith("https://github.com/godotengine/godot/releases/download/4.7.2-stable/", MapmakerTools.Godot.Url);
        Assert.StartsWith("https://builds.dotnet.microsoft.com/dotnet/Runtime/", MapmakerTools.DotNet.Url);
        Assert.False(NetPolicy.Tools.IsAllowed(new Uri("https://example.com/x.zip")));
        Assert.False(NetPolicy.GitHub.IsAllowed(new Uri(MapmakerTools.DotNet.Url)));     // the update policy is not widened
    }

    [Fact]
    public void The_checksum_list_is_read_by_file_name()
    {
        string h1 = new('a', 128), h2 = new('B', 128);
        string text = $"{h1}  Godot_v4.7.2-stable_mono_win64.zip\n{h2} *other.zip\r\nnot a line\n";
        Assert.Equal(h1, MapmakerSetup.ParseSums(text, "Godot_v4.7.2-stable_mono_win64.zip"));
        Assert.Equal(h2.ToLowerInvariant(), MapmakerSetup.ParseSums(text, "other.zip"));
        Assert.Null(MapmakerSetup.ParseSums(text, "missing.zip"));
        Assert.Null(MapmakerSetup.ParseSums("abc  x.zip", "x.zip"));      // a hash that is not 128 hex digits
    }

    // ---- the download path, against an in-process fake host ------------------------------------------------------------------

    const string ZipUrl = "https://github.com/godotengine/godot/releases/download/test/Tool_win64.zip";
    const string SumsUrl = "https://github.com/godotengine/godot/releases/download/test/SHA512-SUMS.txt";

    static byte[] MakeZip(params (string Name, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, text) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        return ms.ToArray();
    }

    static string Sha512(byte[] bytes) => Convert.ToHexStringLower(SHA512.HashData(bytes));

    static (ToolPackage Pkg, FakeGitHub Host, SafeHttp Http) Fixture(byte[] zip, string? pin = null, string? published = null)
    {
        var pkg = new ToolPackage("tool", "the test tool", ZipUrl, zip.Length, pin ?? Sha512(zip), SumsUrl, "Tool_win64.zip", [@"Tool\tool.exe"]) { DirName = "tool-1" };
        var gh = new FakeGitHub();
        gh.Serve(ZipUrl, zip);
        gh.Serve(SumsUrl, $"{published ?? pkg.Sha512}  Tool_win64.zip\n");
        return (pkg, gh, new SafeHttp(NetPolicy.Tools, gh));
    }

    [Fact]
    public async Task A_download_that_matches_its_pin_and_its_published_checksum_is_unpacked_with_progress()
    {
        using var t = new TempDir();
        var (pkg, gh, http) = Fixture(MakeZip((@"Tool/tool.exe", "exe")));
        var setup = Setup(t);
        var seen = new List<UpdateStatus>();
        string dest = Path.Combine(setup.ToolsDir, pkg.DirName);
        await setup.InstallToolAsync(pkg, dest, http, new ActionProgress<UpdateStatus>(seen.Add), default, Builds.Fast);
        Assert.Equal("exe", File.ReadAllText(Path.Combine(dest, "Tool", "tool.exe")));
        Assert.False(Directory.Exists(dest + ".staging"));
        Assert.Contains(seen, s => s.Phase == UpdatePhase.Downloading);
        Assert.Contains(seen, s => s.Phase == UpdatePhase.Verifying);
        Assert.Contains(seen, s => s.Phase == UpdatePhase.Extracting);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(setup.ToolsDir, "downloads")));      // the zip is removed
        int requests = gh.Requests.Count;
        await setup.InstallToolAsync(pkg, dest, http, null, default, Builds.Fast);              // installed already: no request
        Assert.Equal(requests, gh.Requests.Count);
    }

    [Fact]
    public async Task A_file_that_does_not_match_the_pin_is_deleted_and_never_unpacked()
    {
        using var t = new TempDir();
        byte[] zip = MakeZip((@"Tool/tool.exe", "exe"));
        var (pkg, _, http) = Fixture(zip, pin: new string('0', 128), published: new string('0', 128));
        var setup = Setup(t);
        string dest = Path.Combine(setup.ToolsDir, pkg.DirName);
        var e = await Assert.ThrowsAsync<VerificationException>(() => setup.InstallToolAsync(pkg, dest, http, null, default, Builds.Fast));
        Assert.Contains("damaged", e.Message);
        Assert.False(Directory.Exists(dest));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(setup.ToolsDir, "downloads")));
    }

    [Fact]
    public async Task A_published_checksum_that_differs_from_the_pin_stops_before_any_download()
    {
        using var t = new TempDir();
        var (pkg, gh, http) = Fixture(MakeZip((@"Tool/tool.exe", "exe")), published: new string('f', 128));
        var setup = Setup(t);
        await Assert.ThrowsAsync<VerificationException>(() => setup.InstallToolAsync(pkg, Path.Combine(setup.ToolsDir, pkg.DirName), http, null, default, Builds.Fast));
        Assert.Equal(0, gh.CountFor(ZipUrl));
    }

    [Fact]
    public async Task A_zip_without_the_expected_files_is_not_installed()
    {
        using var t = new TempDir();
        var (pkg, _, http) = Fixture(MakeZip((@"Other/readme.txt", "hi")));
        var setup = Setup(t);
        string dest = Path.Combine(setup.ToolsDir, pkg.DirName);
        await Assert.ThrowsAsync<VerificationException>(() => setup.InstallToolAsync(pkg, dest, http, null, default, Builds.Fast));
        Assert.False(Directory.Exists(dest));
        Assert.False(Directory.Exists(dest + ".staging"));
    }

    [Fact]
    public async Task A_zip_with_an_unsafe_path_is_refused()
    {
        using var t = new TempDir();
        var (pkg, _, http) = Fixture(MakeZip((@"../evil.txt", "x"), (@"Tool/tool.exe", "exe")));
        var setup = Setup(t);
        await Assert.ThrowsAsync<VerificationException>(() => setup.InstallToolAsync(pkg, Path.Combine(setup.ToolsDir, pkg.DirName), http, null, default, Builds.Fast));
        Assert.False(File.Exists(t[@"root\tools\evil.txt"]));
    }
}
