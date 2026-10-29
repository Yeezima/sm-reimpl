using SmLauncher.Core;

namespace SmLauncher.Tests;

/// <summary>Disk space, hashes before the download, the header timeout, the root marker, cleanup limits, adopt and the launcher's own update.</summary>
public class DiskFullTests
{
    const string Url = "https://github.com/Yeezima/sm-reimpl-releases/releases/download/t/SM-Reimpl-Playtest-x-Windows.zip";
    static readonly byte[] Data = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();

    [Theory]
    [InlineData(unchecked((int)0x80070070), true)]     // ERROR_DISK_FULL
    [InlineData(unchecked((int)0x80070027), true)]     // ERROR_HANDLE_DISK_FULL
    [InlineData(unchecked((int)0x80070020), false)]    // a sharing violation
    [InlineData(0, false)]
    public void A_full_disk_is_recognised_in_an_io_error(int hresult, bool full) =>
        Assert.Equal(full, DiskSpace.IsDiskFull(new IOException("x", hresult)));

    [Fact]
    public async Task Too_little_free_space_stops_the_download_before_a_request()
    {
        using var t = new TempDir();
        var gh = new FakeGitHub();
        gh.Serve(Url, Data);
        var o = new DownloadOptions { Delay = (_, _) => Task.CompletedTask, FreeBytes = _ => 10_000 };
        var e = await Assert.ThrowsAsync<DiskFullException>(() => new Downloader(new SafeHttp(NetPolicy.GitHub, gh), o).DownloadAsync(new Uri(Url), t["x.part"], Data.Length, null, default));
        Assert.Contains("Not enough disk space", e.Message);
        Assert.Empty(gh.Requests);
    }

    [Fact]
    public async Task A_disk_that_fills_during_the_download_fails_at_once_without_retries()
    {
        using var t = new TempDir();
        var gh = new FakeGitHub { DropWith = () => new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)) };
        gh.Serve(Url, Data);
        gh.Then(Url, -50_000);
        var e = await Assert.ThrowsAsync<DiskFullException>(() => new Downloader(new SafeHttp(NetPolicy.GitHub, gh), Builds.Fast).DownloadAsync(new Uri(Url), t["x.part"], Data.Length, null, default));
        Assert.Contains("disk is full", e.Message);
        Assert.DoesNotContain("internet", e.Message);
        Assert.Single(gh.Requests);
    }

    [Fact]
    public async Task Too_little_space_to_unpack_keeps_the_previous_version_and_the_verified_download()
    {
        using var t = new TempDir();
        long free = long.MaxValue;
        var (u, l, gh, _) = Builds.NewInstall(t["root"], freeBytes: _ => free);
        var r1 = Builds.Release(gh, "aaaaaa1", Builds.Zip(Builds.Files("aaaaaa1")));
        await u.InstallAsync(r1, null, default);
        var r2 = Builds.Release(gh, "bbbbbb2", Builds.Zip(Builds.Files("bbbbbb2")));
        free = 1000;
        var e = await Assert.ThrowsAsync<DiskFullException>(() => u.InstallAsync(r2, null, default));
        Assert.Contains("unpack", e.Message);
        Assert.Equal(r1.Tag, u.ReadPointer().Current);
        Assert.False(Directory.Exists(l.StagingDir(r2.Tag)));
        Assert.True(File.Exists(l.PartFile(r2.Asset.Name)));
        free = long.MaxValue;
        int requests = gh.Requests.Count;
        Assert.Equal(r2.Tag, await u.ResumeAsync(null, default));                                // space freed: no new download
        Assert.Equal(requests, gh.Requests.Count);
    }
}

public class BeforeDownloadTests
{
    [Fact]
    public async Task Hashes_that_disagree_are_refused_before_any_byte_is_fetched()
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var good = Builds.Release(gh, "aaaaaa1", Builds.Zip(Builds.Files("aaaaaa1")));
        var r = good with { Asset = good.Asset with { ApiSha256 = new string('a', 64) } };     // the description still states the real hash
        var e = await Assert.ThrowsAsync<VerificationException>(() => u.InstallAsync(r, null, default));
        Assert.Contains("disagree", e.Message);
        Assert.Empty(gh.Requests);
        Assert.False(File.Exists(l.StateFile));
    }

    [Fact]
    public async Task No_answer_within_the_header_timeout_is_retried_like_a_broken_connection()
    {
        using var t = new TempDir();
        const string url = "https://github.com/Yeezima/sm-reimpl-releases/releases/download/t/SM-Reimpl-Playtest-x-Windows.zip";
        var data = new byte[50_000];
        var gh = new FakeGitHub();
        gh.Serve(url, data);
        gh.Then(url, FakeGitHub.Hang);
        var notes = new List<string>();
        var o = new DownloadOptions { Delay = (_, _) => Task.CompletedTask, HeaderTimeout = TimeSpan.FromMilliseconds(200) };
        await new Downloader(new SafeHttp(NetPolicy.GitHub, gh), o).DownloadAsync(new Uri(url), t["x.part"], data.Length,
            new ActionProgress<DownloadProgress>(p => { if (p.Note is not null) notes.Add(p.Note); }), default);
        Assert.Equal(2, gh.Requests.Count);
        Assert.Contains(notes, n => n.Contains("no answer from GitHub"));
    }
}

public class OwnFilesOnlyTests
{
    [Fact]
    public void Only_the_launchers_own_files_make_a_folder_an_install()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t["versions"]);
        Assert.False(InstallLayout.LooksLikeRoot(t.Path));
        File.WriteAllText(t["current.json"], "{}");
        Assert.True(InstallLayout.LooksLikeRoot(t.Path));
    }

    [Fact]
    public async Task Cleanup_leaves_files_in_downloads_that_are_not_the_launchers()
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        await u.InstallAsync(Builds.Release(gh, "aaaaaa1", Builds.Zip(Builds.Files("aaaaaa1"))), null, default);
        Directory.CreateDirectory(l.DownloadsDir);
        File.WriteAllText(Path.Combine(l.DownloadsDir, "my notes.txt"), "mine");
        File.WriteAllText(Path.Combine(l.DownloadsDir, "something-else.zip.part"), "mine");
        File.WriteAllText(Path.Combine(l.DownloadsDir, "SM-Reimpl-Playtest-2026-10-30-ccccccc-Windows.zip.part"), "a stray one of ours");
        Directory.CreateDirectory(Path.Combine(l.VersionsDir, "not a tag.staging"));
        u.Recover();
        Assert.True(File.Exists(Path.Combine(l.DownloadsDir, "my notes.txt")));
        Assert.True(File.Exists(Path.Combine(l.DownloadsDir, "something-else.zip.part")));
        Assert.False(File.Exists(Path.Combine(l.DownloadsDir, "SM-Reimpl-Playtest-2026-10-30-ccccccc-Windows.zip.part")));
        Assert.True(Directory.Exists(Path.Combine(l.VersionsDir, "not a tag.staging")));
    }

    [Fact]
    public async Task Adopt_copies_only_the_listed_files_and_check_files_flags_unlisted_programs()
    {
        using var t = new TempDir();
        var (u, l, _, _) = Builds.NewInstall(t["root"]);
        Builds.WriteFolder(t["unpacked"], Builds.Files("abc1234"));
        File.WriteAllText(Path.Combine(t["unpacked"], "6 - Extra.cmd"), "@echo off");
        File.WriteAllText(Path.Combine(t["unpacked"], @"game\extra.dll"), "MZ");
        File.WriteAllText(Path.Combine(t["unpacked"], @"game\notes-of-mine.txt"), "x");
        string tag = await u.AdoptAsync(t["unpacked"], null, default);
        string dir = l.VersionDir(tag);
        Assert.False(File.Exists(Path.Combine(dir, "6 - Extra.cmd")));
        Assert.False(File.Exists(Path.Combine(dir, @"game\extra.dll")));
        Assert.False(File.Exists(Path.Combine(dir, @"game\notes-of-mine.txt")));
        Assert.True(u.CheckFiles(tag).Ok);
        File.WriteAllText(Path.Combine(dir, "6 - Extra.cmd"), "@echo off");                        // something put a program in later
        var r = u.CheckFiles(tag);
        Assert.False(r.Ok);
        Assert.Equal(["6 - Extra.cmd"], r.Unlisted);
        Assert.Contains("do not belong", r.Describe());
    }
}

public class LauncherVersionTests
{
    [Theory]
    [InlineData("2026.1029.499", "2026.1029.498", true, true)]
    [InlineData("2026.1029.499", "2026.1029.499", true, false)]       // the same: no replace
    [InlineData("2026.1029.499", "2026.1030.1", true, false)]         // never a downgrade
    [InlineData("1.0.0", "2026.1029.499", true, false)]               // a local build never replaces a released one
    [InlineData("2026.1029.499", null, true, true)]                   // the installed file has no readable version: broken
    [InlineData("2026.1029.499", null, false, true)]                  // nothing installed
    [InlineData(null, "1.0.0", true, false)]                          // the candidate's version is unknown
    public void A_launcher_is_only_replaced_by_a_newer_one(string? candidate, string? installed, bool exists, bool replace) =>
        Assert.Equal(replace, LauncherVersions.ShouldReplace(candidate is null ? null : Version.Parse(candidate), installed is null ? null : Version.Parse(installed), exists));

    [Fact]
    public void The_bundled_launcher_counts_only_when_it_matches_the_builds_file_list_and_is_newer()
    {
        using var t = new TempDir();
        // A real PE file with a file version stands in for the launcher: this library (version 1.0.0.0).
        byte[] pe = File.ReadAllBytes(typeof(LauncherVersions).Assembly.Location);
        var files = Builds.Files("abc1234");
        files[PackageLayout.BundledLauncher] = pe;
        Builds.WriteFolder(t.Path, files);
        Assert.Equal(Path.Combine(t.Path, PackageLayout.BundledLauncher), LauncherVersions.NewerBundled(t.Path, new Version(0, 9)));
        Assert.Null(LauncherVersions.NewerBundled(t.Path, new Version(1, 0, 0, 0)));
        Assert.Null(LauncherVersions.NewerBundled(t.Path, new Version(2026, 1029, 499)));
        File.WriteAllBytes(Path.Combine(t.Path, PackageLayout.BundledLauncher), pe.Concat(new byte[] { 0 }).ToArray());     // altered after the list was made
        Assert.Null(LauncherVersions.NewerBundled(t.Path, new Version(0, 9)));
    }

    [Fact]
    public void Placing_a_launcher_keeps_the_old_one_as_a_backup_and_removes_a_stale_new_file()
    {
        using var t = new TempDir();
        string target = t["SM Reimpl Launcher.exe"];
        File.WriteAllText(t["source.exe"], "new launcher");
        Assert.Null(LauncherVersions.Place(t["source.exe"], target));                               // nothing there yet: no backup
        Assert.Equal("new launcher", File.ReadAllText(target));
        File.WriteAllText(target, "old launcher");
        File.WriteAllText(target + ".new", "stale half copy");
        string? backup = LauncherVersions.Place(t["source.exe"], target);
        Assert.Equal("new launcher", File.ReadAllText(target));
        Assert.Equal("old launcher", File.ReadAllText(backup!));
        Assert.False(File.Exists(target + ".new"));
        LauncherVersions.Restore(backup!, target);                                                  // the new one did not start
        Assert.Equal("old launcher", File.ReadAllText(target));
        Assert.False(File.Exists(backup));
    }
}

public class CacheRepairTests
{
    const string Local = @"C:\Users\test\AppData\Local";
    const string Profile = @"C:\Users\test";

    [Theory]
    [InlineData(null, @"C:\Users\test\AppData\Local\sm-reimpl\cache")]
    [InlineData("", @"C:\Users\test\AppData\Local\sm-reimpl\cache")]
    [InlineData("   ", @"C:\Users\test\AppData\Local\sm-reimpl\cache")]
    [InlineData("  D:\\smdata  ", @"D:\smdata\cache")]                     // trimmed, as the game does
    [InlineData(@"D:\smdata\", @"D:\smdata\cache")]
    public void The_cache_is_found_as_the_game_finds_its_data_root(string? smDataDir, string expected)
    {
        var (cache, refusal) = CacheRepair.Target(smDataDir, Local, Profile);
        Assert.Null(refusal);
        Assert.Equal(expected, cache);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Users\test")]
    [InlineData(@"C:\Users\test\")]
    [InlineData(@"C:\Users")]
    public void A_drive_root_or_the_user_folder_is_never_cleared(string smDataDir)
    {
        var (cache, refusal) = CacheRepair.Target(smDataDir, Local, Profile);
        Assert.Null(cache);
        Assert.NotNull(refusal);
    }
}

public class OfferTests
{
    static ReleaseInfo Rel(string tag, DateTimeOffset at) =>
        new(tag, tag, at, "", "", true, new ReleaseAsset("SM-Reimpl-Playtest-x-Windows.zip", 1, "https://github.com/x", null));

    [Fact]
    public void An_older_published_build_is_not_called_new()
    {
        var at = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var installed = new InstalledVersion { Tag = "b", PublishedAt = at };
        Assert.Equal(OfferKind.UpToDate, UpdateOffer.Classify("b", installed, Rel("b", at)));
        Assert.Equal(OfferKind.Newer, UpdateOffer.Classify("b", installed, Rel("c", at.AddHours(1))));
        Assert.Equal(OfferKind.Older, UpdateOffer.Classify("b", installed, Rel("a", at.AddHours(-1))));
        var adopted = new InstalledVersion { Tag = "b", Built = "2026-10-30", Source = "adopted" };
        Assert.Equal(OfferKind.Older, UpdateOffer.Classify("b", adopted, Rel("a", at)));
        Assert.Equal(OfferKind.Newer, UpdateOffer.Classify("b", adopted, Rel("c", at.AddDays(1))));
        Assert.Equal(OfferKind.Newer, UpdateOffer.Classify(null, null, Rel("c", at)));
    }

    [Fact]
    public void A_newer_release_with_two_playtest_zips_is_reported_not_offered()
    {
        const string zip = """{"name":"SM-Reimpl-Playtest-NAME-Windows.zip","size":5,"browser_download_url":"https://github.com/x"}""";
        string json = "[" +
            """{"tag_name":"tester-2026-10-30-bbbbbbb","draft":false,"published_at":"2026-10-30T00:00:00Z","assets":[""" + zip.Replace("NAME", "a") + "," + zip.Replace("NAME", "b") + "]}," +
            """{"tag_name":"tester-2026-10-29-aaaaaaa","draft":false,"published_at":"2026-10-29T00:00:00Z","assets":[""" + zip.Replace("NAME", "c") + "]}]";
        var r = ReleaseFeed.SelectNewest(json, out var skipped);
        Assert.Equal("tester-2026-10-29-aaaaaaa", r!.Tag);
        Assert.Single(skipped);
        Assert.Contains("2 playtest zips", skipped[0]);
    }
}
