using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using SmLauncher.Core;

namespace SmLauncher.Tests;

/// <summary>A build in parts (tools/release-parts): the manifest, the split, the upload plan and the launcher's part selection and checks. Synthetic data only.</summary>
public class PartsTests
{
    const string PartsBase = Builds.Repo + "parts/";

    static string Tag(string sha) => $"tester-2026-10-29-{sha}";
    static string ManifestName(string sha) => $"SM-Reimpl-Playtest-2026-10-29-{sha}-Windows.parts.json";

    /// <summary>Builds the parts of a folder holding <paramref name="files"/> (and their CONTENTS list).</summary>
    static PartsBuilder.Result Build(string dir, string sha, Dictionary<string, byte[]> files)
    {
        string folder = Path.Combine(dir, "folder-" + sha);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Builds.WriteFolder(folder, files);
        return PartsBuilder.Build(folder, Path.Combine(dir, "parts-" + sha), Path.Combine(dir, "out-" + sha, ManifestName(sha)), Tag(sha), sha, sha + "000", "2026-10-29");
    }

    /// <summary>A release whose only download is the parts manifest; the parts are served from the parts release.</summary>
    static ReleaseInfo Release(FakeGitHub gh, PartsBuilder.Result r, string sha, int minute = 0, byte[]? zip = null, bool wrongBodyHash = false)
    {
        foreach (var p in r.Manifest.Parts) gh.Serve(PartsBase + p.Asset, File.ReadAllBytes(r.PartZips[p.Name]));
        byte[] manifest = File.ReadAllBytes(r.ManifestPath);
        string name = ManifestName(sha);
        string url = Builds.Repo + Tag(sha) + "/" + name;
        gh.Serve(url, manifest);
        string hash = Builds.Sha(manifest);
        string body = $"## What's new in this build\n\n- change {sha}\n\n{ReleaseNotes.WhatsNewEndMarker}\n\n## Download check\n\nSHA-256 of `{name}`:\n```\n{(wrongBodyHash ? new string('0', 64) : hash)}\n```\n";
        ReleaseAsset? zipAsset = null;
        if (zip is not null)
        {
            string zipName = $"SM-Reimpl-Playtest-2026-10-29-{sha}-Windows.zip";
            string zipUrl = Builds.Repo + Tag(sha) + "/" + zipName;
            gh.Serve(zipUrl, zip);
            body += $"\nSHA-256 of `{zipName}`:\n```\n{Builds.Sha(zip)}\n```\n";
            zipAsset = new ReleaseAsset(zipName, zip.Length, zipUrl, Builds.Sha(zip));
        }
        return new ReleaseInfo(Tag(sha), "sm-reimpl tester build " + sha, new DateTimeOffset(2026, 10, 29, 12, minute, 0, TimeSpan.Zero),
            $"https://github.com/Yeezima/sm-reimpl-releases/releases/tag/{Tag(sha)}", body, true, zipAsset, new ReleaseAsset(name, manifest.Length, url, hash));
    }

    /// <summary>The next build: a new VERSION.txt and, as asked, a changed server (everything else byte for byte the same).</summary>
    static Dictionary<string, byte[]> Next(Dictionary<string, byte[]> from, string sha, bool server = true, bool engine = false)
    {
        var f = new Dictionary<string, byte[]>(from, StringComparer.Ordinal)
        {
            [@"notes\VERSION.txt"] = Encoding.ASCII.GetBytes($"sm-reimpl {sha}\r\ncommit: {sha}aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\nprotocol: 40\r\nbuilt: 2026-10-29 (UTC)\r\n"),
        };
        if (server) f[@"server\SmServer.exe"] = Encoding.ASCII.GetBytes("MZ server " + sha + new string('s', 5000));
        if (engine) f[@"game\sm-reimpl.exe"] = Encoding.ASCII.GetBytes("MZ engine " + sha + new string('e', 5000));
        return f;
    }

    // ---- the split and the manifest ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0 - SM Reimpl Launcher.exe", "launcher")]
    [InlineData("2 - Play.cmd", "meta")]
    [InlineData("notes/CONTENTS-SHA256.txt", "meta")]
    [InlineData("game/Launcher.ps1", "meta")]
    [InlineData("game/sm-reimpl.exe", "engine")]
    [InlineData("game/sm-reimpl.console.exe", "engine")]
    [InlineData("game/sm-reimpl.pck", "client")]
    [InlineData("game/data_SmClient_windows_x86_64/GodotSharp.dll", "engine")]
    [InlineData("game/data_SmClient_windows_x86_64/System.Private.CoreLib.dll", "dotnet")]
    [InlineData("game/data_SmClient_windows_x86_64/coreclr.dll", "dotnet")]
    [InlineData("game/data_SmClient_windows_x86_64/SmClient.dll", "client")]
    [InlineData("game/data_SmClient_windows_x86_64/SmClient.deps.json", "client")]
    [InlineData("game/assets/menu/background.jpg", "media")]
    [InlineData("game/ashfall-kit/models/wall.glb", "ashfall")]
    [InlineData("server/SmServer.exe", "server")]
    [InlineData("server/SmSim.dll", "server")]
    [InlineData("server/System.Text.Json.dll", "dotnet")]
    [InlineData("server/hostfxr.dll", "dotnet")]
    [InlineData("source/sm-reimpl-source-abc.zip", "source")]
    public void Every_path_has_its_part(string path, string part) => Assert.Equal(part, PartRules.PartOf(path));

    [Fact]
    public void The_release_no_longer_ships_the_menu_video_loop()
    {
        // Owner 2026-11-14: menu-loop.ogv (20 MB, a dev aid behind --menu-video) is out of the release: not copied beside the exe, left out of the source zip
        // (the content guard also refuses an .ogv in a binary package: PackageScannerTests).
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "SmReimpl.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var script = File.ReadAllLines(Path.Combine(root!, "tools", "release", "release.ps1"));
        var copyList = script.Single(l => l.Contains("assets/menu/background.jpg") && l.Contains("foreach"));
        Assert.DoesNotContain(".ogv", copyList);
        Assert.Contains(script, l => l.Contains("'client/assets/menu/menu-loop.ogv'"));
    }

    [Fact]
    public void The_manifest_lists_every_file_and_parses_back()
    {
        using var t = new TempDir();
        var files = Builds.Files("aaaaaa1");
        var r = Build(t.Path, "aaaaaa1", files);
        var m = PartsManifest.Parse(File.ReadAllText(r.ManifestPath));
        Assert.Equal(files.Count + 1, m.Files.Count);                                    // + notes\CONTENTS-SHA256.txt
        Assert.Equal(Tag("aaaaaa1"), m.Tag);
        Assert.Equal(PartsManifest.DefaultPartsTag, m.PartsTag);
        Assert.Equal(["meta", "launcher", "engine", "client", "server"], m.Parts.Select(p => p.Name));
        foreach (var f in m.Files)
        {
            Assert.Equal(PartRules.PartOf(f.Path), f.Part);
            Assert.Equal(Builds.Sha(File.ReadAllBytes(Path.Combine(t.Path, "folder-aaaaaa1", f.Path))), f.Sha256);
        }
        foreach (var p in m.Parts)
        {
            Assert.True(Names.IsPartAsset(p.Asset));
            Assert.Equal(Builds.Sha(File.ReadAllBytes(r.PartZips[p.Name])), p.ZipSha256);
            Assert.Equal(new FileInfo(r.PartZips[p.Name]).Length, p.ZipSize);
            using var zip = ZipFile.OpenRead(r.PartZips[p.Name]);
            Assert.Equal(m.Files.Where(f => f.Part == p.Name).Select(f => f.Path).Order(StringComparer.Ordinal), zip.Entries.Select(e => e.FullName));
        }
    }

    [Fact]
    public void Same_files_give_the_same_part_names_and_bytes_and_one_changed_file_changes_one_part()
    {
        using var t = new TempDir();
        var files = Builds.Files("aaaaaa1");
        var a = Build(t.Path, "aaaaaa1", files);
        Thread.Sleep(20);
        var b = Build(t.Path, "bbbbbb2", Next(files, "bbbbbb2", server: true));
        var pa = a.Manifest.Parts.ToDictionary(p => p.Name);
        var pb = b.Manifest.Parts.ToDictionary(p => p.Name);
        foreach (var same in new[] { "launcher", "engine", "client" })
        {
            Assert.Equal(pa[same].Asset, pb[same].Asset);
            Assert.Equal(pa[same].ZipSha256, pb[same].ZipSha256);                       // fixed entry times: the zip is the same file
        }
        Assert.NotEqual(pa["server"].Asset, pb["server"].Asset);
        Assert.NotEqual(pa["meta"].Asset, pb["meta"].Asset);
    }

    [Fact]
    public void Identical_bytes_are_carried_once()
    {
        using var t = new TempDir();
        var files = Builds.Files("aaaaaa1");
        byte[] runtime = Encoding.ASCII.GetBytes("MZ runtime" + new string('r', 3000));
        files[@"game\data_SmClient_windows_x86_64\System.Runtime.dll"] = runtime;
        files[@"server\System.Runtime.dll"] = runtime;
        var r = Build(t.Path, "aaaaaa1", files);
        Assert.All(r.Manifest.Files.Where(f => f.Path.EndsWith("System.Runtime.dll", StringComparison.Ordinal)), f => Assert.Equal("dotnet", f.Part));
        using var zip = ZipFile.OpenRead(r.PartZips["dotnet"]);
        Assert.Equal(["game/data_SmClient_windows_x86_64/System.Runtime.dll"], zip.Entries.Select(e => e.FullName));
    }

    public static TheoryData<string, Action<JsonObject>> BadManifests => new()
    {
        { "format", m => m["format"] = "sm-reimpl-parts/9" },
        { "tag", m => m["tag"] = "../evil" },
        { "parts tag", m => m["partsTag"] = "CON" },
        { "unsafe path", m => m["files"]![0]!["path"] = "../../evil.exe" },
        { "drive path", m => m["files"]![0]!["path"] = "C:/evil.exe" },
        { "stream path", m => m["files"]![0]!["path"] = "game/x.exe:hidden" },
        { "duplicate path", m => m["files"]![1]!["path"] = m["files"]![0]!["path"]!.GetValue<string>().ToUpperInvariant() },
        { "unknown part", m => m["files"]![0]!["part"] = "nope" },
        { "bad file hash", m => m["files"]![0]!["sha256"] = "xyz" },
        { "negative size", m => m["files"]![0]!["size"] = -1 },
        { "bad asset name", m => m["parts"]![0]!["asset"] = "../evil.zip" },
        { "bad zip hash", m => m["parts"]![0]!["zipSha256"] = "00" },
        { "no zip size", m => m["parts"]![0]!["zipSize"] = 0 },
        { "bad part name", m => m["parts"]![0]!["name"] = "Bad Name" },
        { "no game", m => { var f = (JsonArray)m["files"]!; f.Remove(f.First(x => x!["path"]!.GetValue<string>() == "game/sm-reimpl.exe")); } },
        { "no file list", m => { var f = (JsonArray)m["files"]!; f.Remove(f.First(x => x!["path"]!.GetValue<string>() == "notes/CONTENTS-SHA256.txt")); } },
    };

    [Theory]
    [MemberData(nameof(BadManifests))]
    public void A_manifest_that_could_misdirect_the_launcher_is_refused(string why, Action<JsonObject> tamper)
    {
        using var t = new TempDir();
        var r = Build(t.Path, "aaaaaa1", Builds.Files("aaaaaa1"));
        var json = JsonNode.Parse(File.ReadAllText(r.ManifestPath))!.AsObject();
        tamper(json);
        var e = Assert.Throws<InvalidDataException>(() => PartsManifest.Parse(json.ToJsonString()));
        Assert.False(string.IsNullOrEmpty(e.Message), why);
        Assert.Throws<InvalidDataException>(() => PartsManifest.Parse("{ not json"));
    }

    // ---- the selection (launcher) and the upload plan (ship) --------------------------------------------------------------

    [Fact]
    public void Only_parts_carrying_a_file_the_install_lacks_are_downloaded()
    {
        using var t = new TempDir();
        var files = Builds.Files("aaaaaa1");
        var a = Build(t.Path, "aaaaaa1", files);
        var b = Build(t.Path, "bbbbbb2", Next(files, "bbbbbb2"));
        var have = a.Manifest.Files.Select(f => f.Sha256).ToHashSet();
        var plan = PartsPlan.Select(b.Manifest, have);
        Assert.Equal(["meta", "server"], plan.Download.Select(p => p.Name));
        Assert.Equal(["launcher", "engine", "client"], plan.Reuse.Select(p => p.Name));
        Assert.Equal(plan.Download.Sum(p => p.ZipSize), plan.DownloadBytes);
        // nothing installed: everything
        Assert.Equal(b.Manifest.Parts.Count, PartsPlan.Select(b.Manifest, new HashSet<string>()).Download.Count);
        // the installed hashes come from an installed version's own file list
        string dir = Path.Combine(t.Path, "folder-aaaaaa1");
        var installed = PartsPlan.InstalledHashes(dir, null);
        Assert.Equal(Builds.Files("aaaaaa1").Count, installed.Count);
        Assert.All(installed, kv => Assert.StartsWith(dir, kv.Value, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_ship_uploads_only_the_parts_the_parts_release_lacks_and_reuses_the_others_by_their_hash()
    {
        using var t = new TempDir();
        var files = Builds.Files("aaaaaa1");
        var a = Build(t.Path, "aaaaaa1", files);
        var b = Build(t.Path, "bbbbbb2", Next(files, "bbbbbb2"));
        // the parts release as GitHub lists it, after build a was shipped (one asset still uploading, one foreign file)
        var assets = new JsonArray();
        foreach (var p in a.Manifest.Parts)
            assets.Add(new JsonObject { ["name"] = p.Asset, ["size"] = p.ZipSize + 7, ["state"] = "uploaded", ["digest"] = "sha256:" + new string('a', 63) + p.Name.Length % 10 });
        assets.Add(new JsonObject { ["name"] = "sm-reimpl-part-server-0123456789abcdef.zip", ["size"] = 5, ["state"] = "new" });
        assets.Add(new JsonObject { ["name"] = "notes.txt", ["size"] = 5, ["state"] = "uploaded" });
        var remote = PartsUpload.ParseAssets(new JsonObject { ["tag_name"] = "parts", ["assets"] = assets }.ToJsonString());
        Assert.Equal(a.Manifest.Parts.Count + 1, remote.Count);                         // the asset still uploading is not there yet
        var plan = PartsUpload.Plan(b.Manifest, remote);
        Assert.Equal(["meta", "server"], plan.Upload.Select(p => p.Name).Order());
        Assert.Equal(["client", "engine", "launcher"], plan.Reuse.Select(p => p.Name).Order());
        Assert.Empty(plan.MissingDigest);
        // a reused part takes the zip that is on GitHub: its hash and size
        var engine = b.Manifest.Part("engine")!;
        Assert.Equal(new string('a', 63) + "6", engine.ZipSha256);
        Assert.Equal(a.Manifest.Part("engine")!.ZipSize + 7, engine.ZipSize);
        Assert.Equal(plan.Upload.Sum(p => p.ZipSize), plan.UploadBytes);
        // the plan's manifest still parses
        PartsManifest.Parse(b.Manifest.ToJson());
        // nothing on GitHub: everything goes up
        Assert.Equal(b.Manifest.Parts.Count, PartsUpload.Plan(Build(t.Path, "ccccdd3", files).Manifest, []).Upload.Count);
    }

    [Fact]
    public void An_existing_part_without_a_digest_must_be_hashed_before_it_is_reused()
    {
        using var t = new TempDir();
        var a = Build(t.Path, "aaaaaa1", Builds.Files("aaaaaa1"));
        var engine = a.Manifest.Part("engine")!;
        var remote = new List<RemotePart> { new(engine.Asset, engine.ZipSize, null) };
        var plan = PartsUpload.Plan(a.Manifest, remote);
        Assert.Equal([engine.Asset], plan.MissingDigest);
        Assert.DoesNotContain(plan.Reuse, p => p.Name == "engine");
        Assert.DoesNotContain(plan.Upload, p => p.Name == "engine");                  // never uploaded over the existing asset
        var known = new Dictionary<string, string> { [engine.Asset] = new string('b', 64) };
        var again = PartsUpload.Plan(Build(t.Path, "aaaaaa1", Builds.Files("aaaaaa1")).Manifest, remote, known);
        Assert.Empty(again.MissingDigest);
        Assert.Contains(again.Reuse, p => p.Name == "engine" && p.ZipSha256 == new string('b', 64));
    }

    // ---- the release feed ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_release_with_a_parts_manifest_is_offered_with_or_without_a_zip()
    {
        string Asset(string name, int size = 100) => $$"""{"name":"{{name}}","size":{{size}},"browser_download_url":"https://github.com/x/{{name}}","state":"uploaded","digest":"sha256:{{new string('1', 64)}}"}""";
        string Rel(string tag, string at, params string[] assets) => $$"""{"draft":false,"prerelease":true,"tag_name":"{{tag}}","published_at":"{{at}}","assets":[{{string.Join(",", assets)}}]}""";
        var parts = ReleaseFeed.SelectNewest($"[{Rel("tester-2026-10-30-bbbbbbb", "2026-10-30T00:00:00Z", Asset(ManifestName("bbbbbbb")), Asset("sm-reimpl-part-meta-0123456789abcdef.zip"))}," +
                                             $"{Rel("tester-2026-10-29-aaaaaaa", "2026-10-29T00:00:00Z", Asset("SM-Reimpl-Playtest-2026-10-29-aaaaaaa-Windows.zip"))}]")!;
        Assert.Equal("tester-2026-10-30-bbbbbbb", parts.Tag);
        Assert.Null(parts.Asset);
        Assert.Equal(ManifestName("bbbbbbb"), parts.Parts!.Name);
        Assert.Same(parts.Parts, parts.Primary);
        var both = ReleaseFeed.SelectNewest($"[{Rel("tester-2026-10-30-bbbbbbb", "2026-10-30T00:00:00Z", Asset(ManifestName("bbbbbbb")), Asset("SM-Reimpl-Playtest-2026-10-30-bbbbbbb-Windows.zip"))}]")!;
        Assert.NotNull(both.Asset);
        Assert.NotNull(both.Parts);
        // two manifests: not offered, and said why; an oversized manifest is ignored
        ReleaseFeed.SelectNewest($"[{Rel("tester-2026-10-30-bbbbbbb", "2026-10-30T00:00:00Z", Asset(ManifestName("bbbbbbb")), Asset(ManifestName("bbbbbb2")))}]", out var skipped);
        Assert.Contains(skipped, s => s.Contains("parts lists", StringComparison.Ordinal));
        Assert.Null(ReleaseFeed.SelectNewest($"[{Rel("tester-2026-10-30-bbbbbbb", "2026-10-30T00:00:00Z", Asset(ManifestName("bbbbbbb"), PartsManifest.MaxManifestBytes + 1))}]"));
        // the parts release itself (part zips only) is never offered
        Assert.Null(ReleaseFeed.SelectNewest($"[{Rel("parts", "2026-10-31T00:00:00Z", Asset("sm-reimpl-part-meta-0123456789abcdef.zip"))}]"));
    }

    [Fact]
    public void A_part_link_is_the_manifest_link_on_the_parts_release()
    {
        var u = Updater.PartUrl("https://github.com/Yeezima/sm-reimpl-releases/releases/download/tester-2026-10-29-aaaaaa1/" + ManifestName("aaaaaa1"), "parts", "sm-reimpl-part-meta-0123456789abcdef.zip");
        Assert.Equal("https://github.com/Yeezima/sm-reimpl-releases/releases/download/parts/sm-reimpl-part-meta-0123456789abcdef.zip", u.AbsoluteUri);
        Assert.Throws<InvalidDataException>(() => Updater.PartUrl("https://github.com/Yeezima/x/archive/a.zip", "parts", "x"));
    }

    // ---- the launcher's install from parts ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_fresh_install_downloads_every_part_checks_every_file_and_cleans_up()
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var files = Builds.Files("aaaaaa1");
        var r = Release(gh, Build(t["b"], "aaaaaa1", files), "aaaaaa1");
        Assert.Equal(r.Tag, await u.InstallAsync(r, null, default));
        Assert.Equal(5, u.LastPartsPlan!.Download.Count);
        var dir = l.VersionDir(r.Tag);
        Assert.True(Manifest.Verify(dir).Ok);
        foreach (var (rel, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(dir, rel)));
        Assert.Equal(r.Tag, u.ReadPointer().Current);
        Assert.Equal(r.Parts!.ApiSha256, u.ReadPointer().Versions[r.Tag].ZipSha256);
        Assert.False(File.Exists(l.StateFile));
        Assert.False(Directory.Exists(l.PartsDir));
        Assert.False(File.Exists(l.PartFile(r.Parts.Name)));
        Assert.False(Directory.Exists(l.StagingDir(r.Tag)));
    }

    [Fact]
    public async Task An_update_downloads_only_the_changed_parts_and_takes_the_rest_from_the_installed_build()
    {
        using var t = new TempDir();
        var (u, l, gh, log) = Builds.NewInstall(t["root"]);
        var files1 = Builds.Files("aaaaaa1");
        var b1 = Build(t["b"], "aaaaaa1", files1);
        await u.InstallAsync(Release(gh, b1, "aaaaaa1", minute: 1), null, default);
        var files2 = Next(files1, "bbbbbb2");
        var b2 = Build(t["b"], "bbbbbb2", files2);
        var r2 = Release(gh, b2, "bbbbbb2", minute: 2);
        Assert.Equal(r2.Tag, await u.InstallAsync(r2, null, default));
        Assert.Equal(["meta", "server"], u.LastPartsPlan!.Download.Select(p => p.Name));
        foreach (var same in new[] { "launcher", "engine", "client" })
            Assert.Equal(1, gh.CountFor(PartsBase + b2.Manifest.Part(same)!.Asset));    // only the first install fetched it
        var dir = l.VersionDir(r2.Tag);
        Assert.True(Manifest.Verify(dir).Ok);
        foreach (var (rel, bytes) in files2) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(dir, rel)));
        Assert.Equal((r2.Tag, Tag("aaaaaa1")), (u.ReadPointer().Current, u.ReadPointer().Previous));
        Assert.Contains(log, s => s.Contains("2 of 5 to download", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_damaged_installed_file_makes_its_part_download_after_all()
    {
        using var t = new TempDir();
        var (u, l, gh, log) = Builds.NewInstall(t["root"]);
        var files1 = Builds.Files("aaaaaa1");
        var r1 = Release(gh, Build(t["b"], "aaaaaa1", files1), "aaaaaa1", minute: 1);
        await u.InstallAsync(r1, null, default);
        File.WriteAllBytes(Path.Combine(l.VersionDir(r1.Tag), @"game\sm-reimpl.exe"), new byte[40_000]);      // same size, other bytes
        var files2 = Next(files1, "bbbbbb2");
        var r2 = Release(gh, Build(t["b"], "bbbbbb2", files2), "bbbbbb2", minute: 2);
        await u.InstallAsync(r2, null, default);
        Assert.Contains("engine", u.LastPartsPlan!.Download.Select(p => p.Name));
        Assert.Equal(files2[@"game\sm-reimpl.exe"], File.ReadAllBytes(Path.Combine(l.VersionDir(r2.Tag), @"game\sm-reimpl.exe")));
        Assert.Contains(log, s => s.Contains("did not match their list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_damaged_part_is_refused_and_the_installed_build_stays()
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var files1 = Builds.Files("aaaaaa1");
        var r1 = Release(gh, Build(t["b"], "aaaaaa1", files1), "aaaaaa1", minute: 1);
        await u.InstallAsync(r1, null, default);
        var b2 = Build(t["b"], "bbbbbb2", Next(files1, "bbbbbb2"));
        var r2 = Release(gh, b2, "bbbbbb2", minute: 2);
        var server = b2.Manifest.Part("server")!;
        var bad = File.ReadAllBytes(b2.PartZips["server"]);
        bad[^30] ^= 0xFF;
        gh.Serve(PartsBase + server.Asset, bad);                                          // same size, other bytes
        await Assert.ThrowsAsync<VerificationException>(() => u.InstallAsync(r2, null, default));
        Assert.Equal(r1.Tag, u.ReadPointer().Current);
        Assert.False(Directory.Exists(l.StagingDir(r2.Tag)));
        Assert.False(File.Exists(l.PartZip(server.Asset)));
    }

    [Fact]
    public async Task A_part_holding_a_file_the_manifest_gives_another_part_is_refused()
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var b = Build(t["b"], "aaaaaa1", Builds.Files("aaaaaa1"));
        // the meta part's zip also holds a program the manifest does not give it (the zip's hash in the manifest is updated, so only the content check can catch it)
        string metaZip = b.PartZips["meta"];
        using (var zip = ZipFile.Open(metaZip, ZipArchiveMode.Update))
        {
            using var s = zip.CreateEntry("game/data/evil.dll").Open();
            s.Write("MZ evil"u8);
        }
        var meta = b.Manifest.Part("meta")!;
        meta.ZipSha256 = Hashes.Sha256File(metaZip);
        meta.ZipSize = new FileInfo(metaZip).Length;
        File.WriteAllText(b.ManifestPath, b.Manifest.ToJson());
        var r = Release(gh, b, "aaaaaa1");
        var e = await Assert.ThrowsAsync<VerificationException>(() => u.InstallAsync(r, null, default));
        Assert.Contains("does not give it", e.Message);
        Assert.Null(u.ReadPointer().Current);
        Assert.False(Directory.Exists(l.StagingDir(r.Tag)));
    }

    [Fact]
    public async Task A_manifest_the_release_does_not_vouch_for_is_not_used_and_the_zip_is_the_fallback()
    {
        using var t = new TempDir();
        var (u, l, gh, log) = Builds.NewInstall(t["root"]);
        var files = Builds.Files("aaaaaa1");
        var zip = Builds.Zip(files);
        var r = Release(gh, Build(t["b"], "aaaaaa1", files), "aaaaaa1", zip: zip, wrongBodyHash: true);
        Assert.Equal(r.Tag, await u.InstallAsync(r, null, default));
        Assert.Contains(log, s => s.Contains("falling back to the full zip", StringComparison.Ordinal));
        Assert.Equal(1, gh.CountFor(r.Asset!.DownloadUrl));
        Assert.True(Manifest.Verify(l.VersionDir(r.Tag)).Ok);
        // without a zip the refusal reaches the caller and nothing is installed
        using var t2 = new TempDir();
        var (u2, _, gh2, _) = Builds.NewInstall(t2["root"]);
        var r2 = Release(gh2, Build(t2["b"], "aaaaaa1", files), "aaaaaa1", wrongBodyHash: true);
        await Assert.ThrowsAsync<VerificationException>(() => u2.InstallAsync(r2, null, default));
        Assert.Null(u2.ReadPointer().Current);
    }

    [Fact]
    public async Task An_interrupted_parts_update_resumes_and_keeps_the_parts_it_already_checked()
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var b = Build(t["b"], "aaaaaa1", Builds.Files("aaaaaa1"));
        var r = Release(gh, b, "aaaaaa1");
        var server = b.Manifest.Part("server")!;
        gh.Serve(PartsBase + server.Asset, Array.Empty<byte>());
        gh.Then(PartsBase + server.Asset, 404);                                             // the last part is not there (yet)
        await Assert.ThrowsAsync<DownloadFailedException>(() => u.InstallAsync(r, null, default));
        Assert.Equal("parts", u.ReadState()!.Source);
        var meta = b.Manifest.Part("meta")!;
        Assert.True(File.Exists(l.PartZip(meta.Asset)));
        // a restart: the recovery keeps the update and its parts; resuming fetches only what is missing
        Assert.NotNull(u.Recover().Pending);
        Assert.True(File.Exists(l.PartZip(meta.Asset)));
        gh.Serve(PartsBase + server.Asset, File.ReadAllBytes(b.PartZips["server"]));
        Assert.Equal(r.Tag, await u.ResumeAsync(null, default));
        Assert.Equal(1, gh.CountFor(PartsBase + meta.Asset));
        Assert.True(Manifest.Verify(l.VersionDir(r.Tag)).Ok);
        Assert.False(Directory.Exists(l.PartsDir));
    }

    [Fact]
    public async Task A_local_parts_folder_installs_with_the_same_checks()
    {
        using var t = new TempDir();
        var b = Build(t["b"], "aaaaaa1", Builds.Files("aaaaaa1"));
        // the smoke test's layout: the manifest in a folder, the zips in its parts\ subfolder
        string folder = Path.GetDirectoryName(b.ManifestPath)!;
        Directory.CreateDirectory(Path.Combine(folder, "parts"));
        foreach (var z in b.PartZips.Values) File.Copy(z, Path.Combine(folder, "parts", Path.GetFileName(z)));
        var layout = new InstallLayout(t["root"]);
        var u = new Updater(layout, null) { LocalPartsDir = folder };
        string name = Path.GetFileName(b.ManifestPath);
        var r = new ReleaseInfo(b.Manifest.Tag, "local", DateTimeOffset.UtcNow, "", "", true, null,
            new ReleaseAsset(name, new FileInfo(b.ManifestPath).Length, Builds.Repo + b.Manifest.Tag + "/" + name, Hashes.Sha256File(b.ManifestPath)));
        Assert.Equal(b.Manifest.Tag, await u.InstallAsync(r, null, default));
        Assert.True(Manifest.Verify(layout.VersionDir(b.Manifest.Tag)).Ok);
        // a part that does not match the manifest is refused there too
        using var t2 = new TempDir();
        var bytes = File.ReadAllBytes(Path.Combine(folder, "parts", b.Manifest.Part("engine")!.Asset));
        bytes[^30] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(folder, "parts", b.Manifest.Part("engine")!.Asset), bytes);
        var u2 = new Updater(new InstallLayout(t2["root"]), null) { LocalPartsDir = folder };
        await Assert.ThrowsAsync<VerificationException>(() => u2.InstallAsync(r, null, default));
    }
}
