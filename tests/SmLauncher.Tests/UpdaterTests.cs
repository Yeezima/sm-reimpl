using System.Text;
using SmLauncher.Core;

namespace SmLauncher.Tests;

public class UpdaterInstallTests
{
    [Fact]
    public async Task A_fresh_install_unpacks_verifies_and_points_current_at_it()
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var r = Builds.Release(gh, "aaaaaa1", Builds.Zip(Builds.Files("aaaaaa1")));
        var phases = new List<UpdatePhase?>();
        string tag = await u.InstallAsync(r, new ActionProgress<UpdateStatus>(s => { if (phases.Count == 0 || phases[^1] != s.Phase) phases.Add(s.Phase); }), default);
        Assert.Equal(r.Tag, tag);
        Assert.Equal([UpdatePhase.Downloading, UpdatePhase.Verifying, UpdatePhase.Extracting, UpdatePhase.Switching], phases.Distinct());
        var p = u.ReadPointer();
        Assert.Equal(r.Tag, p.Current);
        Assert.Null(p.Previous);
        Assert.True(Manifest.Verify(l.VersionDir(tag)).Ok);
        Assert.False(File.Exists(l.StateFile));
        Assert.False(File.Exists(l.PartFile(r.Asset.Name)));
        Assert.False(Directory.Exists(l.StagingDir(tag)));
        Assert.False(File.Exists(l.CurrentFile + ".tmp"));
        Assert.Equal("• change aaaaaa1", p.Versions[tag].WhatsNew);
        Assert.Equal(r.Asset.ApiSha256, p.Versions[tag].ZipSha256);
    }

    [Fact]
    public async Task Updates_keep_the_last_two_versions_for_rollback()
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var r1 = Builds.Release(gh, "aaaaaa1", Builds.Zip(Builds.Files("aaaaaa1")), minute: 1);
        var r2 = Builds.Release(gh, "bbbbbb2", Builds.Zip(Builds.Files("bbbbbb2")), minute: 2);
        var r3 = Builds.Release(gh, "ccccdd3", Builds.Zip(Builds.Files("ccccdd3")), minute: 3);
        await u.InstallAsync(r1, null, default);
        await u.InstallAsync(r2, null, default);
        Assert.Equal((r2.Tag, r1.Tag), (u.ReadPointer().Current, u.ReadPointer().Previous));
        await u.InstallAsync(r3, null, default);
        var p = u.ReadPointer();
        Assert.Equal((r3.Tag, r2.Tag), (p.Current, p.Previous));
        Assert.False(Directory.Exists(l.VersionDir(r1.Tag)));
        Assert.Equal(new[] { r2.Tag, r3.Tag }.Order(), u.InstalledTags().Order());
        Assert.DoesNotContain(r1.Tag, p.Versions.Keys);
    }

    [Fact]
    public async Task Rollback_swaps_current_and_previous_and_can_be_undone()
    {
        using var t = new TempDir();
        var (u, _, gh, _) = Builds.NewInstall(t["root"]);
        var r1 = Builds.Release(gh, "aaaaaa1", Builds.Zip(Builds.Files("aaaaaa1")));
        var r2 = Builds.Release(gh, "bbbbbb2", Builds.Zip(Builds.Files("bbbbbb2")));
        await u.InstallAsync(r1, null, default);
        Assert.Throws<InvalidOperationException>(() => u.Rollback());
        await u.InstallAsync(r2, null, default);
        Assert.Equal(r1.Tag, u.Rollback());
        Assert.Equal((r1.Tag, r2.Tag), (u.ReadPointer().Current, u.ReadPointer().Previous));
        int before = gh.Requests.Count;
        Assert.Equal(r2.Tag, await u.InstallAsync(r2, null, default));       // Update after a rollback: switches back, no download
        Assert.Equal(before, gh.Requests.Count);
        Assert.Equal((r2.Tag, r1.Tag), (u.ReadPointer().Current, u.ReadPointer().Previous));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_download_that_does_not_match_its_hash_is_deleted_and_nothing_changes(bool api, bool body)
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var r1 = Builds.Release(gh, "aaaaaa1", Builds.Zip(Builds.Files("aaaaaa1")));
        await u.InstallAsync(r1, null, default);
        var r2 = Builds.Release(gh, "bbbbbb2", Builds.Zip(Builds.Files("bbbbbb2")), apiHash: api, bodyHash: body, wrongHash: new string('e', 64));
        var e = await Assert.ThrowsAsync<VerificationException>(() => u.InstallAsync(r2, null, default));
        Assert.Contains("damaged", e.Message);
        Assert.Equal(r1.Tag, u.ReadPointer().Current);
        Assert.False(File.Exists(l.PartFile(r2.Asset.Name)));
        Assert.False(File.Exists(l.StateFile));
        Assert.False(Directory.Exists(l.VersionDir(r2.Tag)));
        Assert.False(Directory.Exists(l.StagingDir(r2.Tag)));
    }

    [Fact]
    public async Task A_release_with_no_hash_at_all_is_not_installed()
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var r = Builds.Release(gh, "aaaaaa1", Builds.Zip(Builds.Files("aaaaaa1")), apiHash: false, bodyHash: false);
        await Assert.ThrowsAsync<VerificationException>(() => u.InstallAsync(r, null, default));
        Assert.Empty(gh.Requests);                                                                   // refused before a byte was fetched
        Assert.False(File.Exists(l.StateFile));
        Assert.Null(u.ReadPointer().Current);
        Assert.False(Directory.Exists(l.VersionDir(r.Tag)));
    }

    [Fact]
    public async Task Unpacked_files_that_differ_from_the_builds_manifest_are_refused_and_the_zip_is_kept_for_a_retry()
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var r1 = Builds.Release(gh, "aaaaaa1", Builds.Zip(Builds.Files("aaaaaa1")));
        await u.InstallAsync(r1, null, default);
        var r2 = Builds.Release(gh, "bbbbbb2", Builds.Zip(Builds.Files("bbbbbb2"), tamper: f => f[@"game\sm-reimpl.exe"] = [1, 2, 3]));
        var e = await Assert.ThrowsAsync<VerificationException>(() => u.InstallAsync(r2, null, default));
        Assert.Contains("file list", e.Message);
        Assert.Equal(r1.Tag, u.ReadPointer().Current);
        Assert.False(Directory.Exists(l.StagingDir(r2.Tag)));
        Assert.False(Directory.Exists(l.VersionDir(r2.Tag)));
        Assert.Equal(UpdatePhase.Verifying, u.ReadState()!.Phase);
        Assert.True(File.Exists(l.PartFile(r2.Asset.Name)));
        int requests = gh.Requests.Count;
        await Assert.ThrowsAsync<VerificationException>(() => u.InstallAsync(r2, null, default));   // the retry unpacks the kept zip: no new download
        Assert.Equal(requests, gh.Requests.Count);
    }

    [Fact]
    public async Task A_zip_slip_entry_refuses_the_update()
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var r = Builds.Release(gh, "aaaaaa1", Builds.Zip(Builds.Files("aaaaaa1"), extraEntries: ["../../escaped.txt"]));
        await Assert.ThrowsAsync<VerificationException>(() => u.InstallAsync(r, null, default));
        Assert.False(File.Exists(Path.Combine(l.VersionsDir, "..", "escaped.txt")));
        Assert.False(File.Exists(t["escaped.txt"]));
        Assert.Null(u.ReadPointer().Current);
    }

    [Fact]
    public async Task A_newer_release_drops_an_unfinished_download_of_an_older_one()
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var r1 = Builds.Release(gh, "aaaaaa1", Builds.Zip(Builds.Files("aaaaaa1")));
        gh.Then(r1.Asset.DownloadUrl, -5000, 503, 503, 503, 503, 503);
        await Assert.ThrowsAsync<DownloadFailedException>(() => u.InstallAsync(r1, null, default));
        Assert.True(File.Exists(l.PartFile(r1.Asset.Name)));
        var r2 = Builds.Release(gh, "bbbbbb2", Builds.Zip(Builds.Files("bbbbbb2")));
        await u.InstallAsync(r2, null, default);
        Assert.False(File.Exists(l.PartFile(r1.Asset.Name)));
        Assert.Equal(r2.Tag, u.ReadPointer().Current);
    }

    [Fact]
    public async Task Repair_installs_a_damaged_current_version_again()
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var r = Builds.Release(gh, "aaaaaa1", Builds.Zip(Builds.Files("aaaaaa1")));
        await u.InstallAsync(r, null, default);
        File.WriteAllText(Path.Combine(l.VersionDir(r.Tag), @"game\data\lib.dll"), "damaged");
        Assert.False(u.CheckFiles(r.Tag).Ok);
        Assert.Equal(r.Tag, await u.InstallAsync(r, null, default));                  // without repair: already current, nothing done
        Assert.False(u.CheckFiles(r.Tag).Ok);
        await u.InstallAsync(r, null, default, repair: true);
        Assert.True(u.CheckFiles(r.Tag).Ok);
        Assert.False(Directory.Exists(l.ReplacedDir(r.Tag)));
        Assert.Equal(r.Tag, u.ReadPointer().Current);
    }
}

public class CarryOverTests
{
    [Fact]
    public async Task Settings_a_tester_added_inside_the_old_version_come_along_and_shipped_files_do_not()
    {
        using var t = new TempDir();
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var r1 = Builds.Release(gh, "aaaaaa1", Builds.Zip(Builds.Files("aaaaaa1")));
        await u.InstallAsync(r1, null, default);
        string old = l.VersionDir(r1.Tag);
        File.WriteAllText(Path.Combine(old, @"game\override.cfg"), "[display]\nwindow/size/mode=3\n");
        File.WriteAllText(Path.Combine(old, @"server\bans.json"), "[]");
        File.WriteAllText(Path.Combine(old, @"game\crash.txt"), "a log");                       // not a settings file
        File.WriteAllBytes(Path.Combine(old, @"game\big.json"), new byte[(int)CarryOver.MaxBytes + 1]);
        File.WriteAllText(Path.Combine(old, @"server\SmServer.exe"), "edited shipped file");     // listed in the old manifest: never carried
        var files2 = Builds.Files("bbbbbb2");
        files2[@"server\bans.json"] = Encoding.ASCII.GetBytes("[\"new build's own\"]");
        var r2 = Builds.Release(gh, "bbbbbb2", Builds.Zip(files2));
        await u.InstallAsync(r2, null, default);
        string neu = l.VersionDir(r2.Tag);
        Assert.Equal("[display]\nwindow/size/mode=3\n", File.ReadAllText(Path.Combine(neu, @"game\override.cfg")));
        Assert.Equal("[\"new build's own\"]", File.ReadAllText(Path.Combine(neu, @"server\bans.json")));   // the new build wins
        Assert.False(File.Exists(Path.Combine(neu, @"game\crash.txt")));
        Assert.False(File.Exists(Path.Combine(neu, @"game\big.json")));
        Assert.Equal(files2[@"server\SmServer.exe"], File.ReadAllBytes(Path.Combine(neu, @"server\SmServer.exe")));
        Assert.True(u.CheckFiles(r2.Tag).Ok);                                                      // extra files do not fail the check
    }
}

public class AdoptTests
{
    [Fact]
    public async Task An_unpacked_zip_folder_is_copied_in_checked_and_made_current()
    {
        using var t = new TempDir();
        var (u, l, _, _) = Builds.NewInstall(t["root"]);
        Builds.WriteFolder(t["unpacked"], Builds.Files("abc1234"));
        string tag = await u.AdoptAsync(t["unpacked"], null, default);
        Assert.Equal("tester-2026-10-29-abc1234", tag);
        Assert.Equal(tag, u.ReadPointer().Current);
        Assert.Equal("adopted", u.ReadPointer().Versions[tag].Source);
        Assert.True(u.CheckFiles(tag).Ok);
        Assert.True(File.Exists(Path.Combine(t["unpacked"], PackageLayout.GameExe)));             // the source folder is left as it was
    }

    [Fact]
    public async Task A_folder_without_its_file_list_or_with_damaged_files_is_refused()
    {
        using var t = new TempDir();
        var (u, l, _, _) = Builds.NewInstall(t["root"]);
        Builds.WriteFolder(t["nolist"], Builds.Files("abc1234"), manifest: false);
        await Assert.ThrowsAsync<VerificationException>(() => u.AdoptAsync(t["nolist"], null, default));
        Builds.WriteFolder(t["bad"], Builds.Files("abc1234"));
        File.WriteAllText(Path.Combine(t["bad"], @"game\data\lib.dll"), "x");
        await Assert.ThrowsAsync<VerificationException>(() => u.AdoptAsync(t["bad"], null, default));
        Assert.Null(u.ReadPointer().Current);
        Assert.Empty(Directory.EnumerateDirectories(l.VersionsDir));
        Assert.False(File.Exists(l.StateFile));
    }

    [Fact]
    public async Task A_folder_inside_the_install_is_refused()
    {
        using var t = new TempDir();
        var (u, l, _, _) = Builds.NewInstall(t["root"]);
        Builds.WriteFolder(Path.Combine(l.Root, "inside"), Builds.Files("abc1234"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => u.AdoptAsync(Path.Combine(l.Root, "inside"), null, default));
    }
}

/// <summary>A crash or power cut in every phase of an update: the next start resumes or cleans up, and current never points at a broken version.</summary>
public class RecoveryTests
{
    static async Task<(Updater U, InstallLayout L, FakeGitHub Gh, ReleaseInfo R1, ReleaseInfo R2, byte[] Zip2)> TwoReleases(TempDir t)
    {
        var (u, l, gh, _) = Builds.NewInstall(t["root"]);
        var r1 = Builds.Release(gh, "aaaaaa1", Builds.Zip(Builds.Files("aaaaaa1")));
        await u.InstallAsync(r1, null, default);
        var zip2 = Builds.Zip(Builds.Files("bbbbbb2"));
        var r2 = Builds.Release(gh, "bbbbbb2", zip2);
        return (u, l, gh, r1, r2, zip2);
    }

    /// <summary>A new Updater on the same folder: what the next start of the launcher sees.</summary>
    static Updater Restart(InstallLayout l, FakeGitHub gh) => new(l, new Downloader(new SafeHttp(NetPolicy.GitHub, gh), Builds.Fast));

    [Fact]
    public async Task Killed_while_downloading_resumes_from_the_part_file()
    {
        using var t = new TempDir();
        var (u, l, gh, r1, r2, zip2) = await TwoReleases(t);
        Store.WriteState(l, UpdateState.For(r2));
        Directory.CreateDirectory(l.DownloadsDir);
        File.WriteAllBytes(l.PartFile(r2.Asset.Name), zip2[..(zip2.Length / 3)]);
        var u2 = Restart(l, gh);
        var rep = u2.Recover();
        Assert.Equal(UpdatePhase.Downloading, rep.Pending!.Phase);
        Assert.Equal(r1.Tag, u2.ReadPointer().Current);                                           // still playable meanwhile
        Assert.True(File.Exists(l.PartFile(r2.Asset.Name)));
        Assert.Equal(r2.Tag, await u2.ResumeAsync(null, default));
        Assert.Equal(zip2.Length / 3, gh.Requests.Last(r => r.Url.AbsoluteUri == r2.Asset.DownloadUrl).RangeFrom);
        Assert.Equal((r2.Tag, r1.Tag), (u2.ReadPointer().Current, u2.ReadPointer().Previous));
    }

    [Fact]
    public async Task Killed_while_verifying_checks_the_complete_file_again_without_the_network()
    {
        using var t = new TempDir();
        var (_, l, gh, r1, r2, zip2) = await TwoReleases(t);
        var s = UpdateState.For(r2);
        s.Phase = UpdatePhase.Verifying;
        Store.WriteState(l, s);
        Directory.CreateDirectory(l.DownloadsDir);
        File.WriteAllBytes(l.PartFile(r2.Asset.Name), zip2);
        var u2 = Restart(l, gh);
        gh.Offline = true;
        Assert.Equal(UpdatePhase.Verifying, u2.Recover().Pending!.Phase);
        Assert.Equal(r2.Tag, await u2.ResumeAsync(null, default));
        Assert.Equal(r2.Tag, u2.ReadPointer().Current);
    }

    [Fact]
    public async Task Killed_while_extracting_removes_the_half_done_staging_and_unpacks_again()
    {
        using var t = new TempDir();
        var (_, l, gh, r1, r2, zip2) = await TwoReleases(t);
        var s = UpdateState.For(r2);
        s.Phase = UpdatePhase.Extracting;
        Store.WriteState(l, s);
        Directory.CreateDirectory(l.DownloadsDir);
        File.WriteAllBytes(l.PartFile(r2.Asset.Name), zip2);
        string staging = l.StagingDir(r2.Tag);
        Directory.CreateDirectory(Path.Combine(staging, "game"));
        File.WriteAllText(Path.Combine(staging, @"game\sm-reimpl.exe"), "half");
        var u2 = Restart(l, gh);
        gh.Offline = true;
        var rep = u2.Recover();
        Assert.False(Directory.Exists(staging));
        Assert.Equal(r1.Tag, u2.ReadPointer().Current);
        Assert.Equal(UpdatePhase.Extracting, rep.Pending!.Phase);
        Assert.Equal(r2.Tag, await u2.ResumeAsync(null, default));
        Assert.True(u2.CheckFiles(r2.Tag).Ok);
    }

    [Fact]
    public async Task Killed_while_switching_before_the_rename_finishes_the_switch_on_start()
    {
        using var t = new TempDir();
        var (_, l, gh, r1, r2, zip2) = await TwoReleases(t);
        SafeZip.Extract(WriteZip(t, zip2), l.StagingDir(r2.Tag));
        var s = UpdateState.For(r2);
        s.Phase = UpdatePhase.Switching;
        Store.WriteState(l, s);
        var u2 = Restart(l, gh);
        var rep = u2.Recover();
        Assert.Null(rep.Pending);
        Assert.Contains(rep.Notes, n => n.Contains("completed"));
        Assert.Equal((r2.Tag, r1.Tag), (u2.ReadPointer().Current, u2.ReadPointer().Previous));
        Assert.False(File.Exists(l.StateFile));
    }

    [Fact]
    public async Task Killed_after_the_rename_but_before_current_json_finishes_the_switch_on_start()
    {
        using var t = new TempDir();
        var (_, l, gh, r1, r2, zip2) = await TwoReleases(t);
        SafeZip.Extract(WriteZip(t, zip2), l.VersionDir(r2.Tag));
        var s = UpdateState.For(r2);
        s.Phase = UpdatePhase.Switching;
        Store.WriteState(l, s);
        Assert.Equal(r1.Tag, Restart(l, gh).ReadPointer().Current);
        var u2 = Restart(l, gh);
        u2.Recover();
        Assert.Equal((r2.Tag, r1.Tag), (u2.ReadPointer().Current, u2.ReadPointer().Previous));
    }

    [Fact]
    public async Task A_damaged_staging_found_while_switching_is_not_switched_to()
    {
        using var t = new TempDir();
        var (_, l, gh, r1, r2, zip2) = await TwoReleases(t);
        SafeZip.Extract(WriteZip(t, zip2), l.StagingDir(r2.Tag));
        File.WriteAllText(Path.Combine(l.StagingDir(r2.Tag), @"server\SmServer.exe"), "zeroed by a power cut");
        var s = UpdateState.For(r2);
        s.Phase = UpdatePhase.Switching;
        Store.WriteState(l, s);
        Directory.CreateDirectory(l.DownloadsDir);
        File.WriteAllBytes(l.PartFile(r2.Asset.Name), zip2);
        var u2 = Restart(l, gh);
        var rep = u2.Recover();
        Assert.Equal(r1.Tag, u2.ReadPointer().Current);
        Assert.False(Directory.Exists(l.StagingDir(r2.Tag)));
        Assert.False(Directory.Exists(l.VersionDir(r2.Tag)));
        Assert.Equal(UpdatePhase.Verifying, rep.Pending!.Phase);
        Assert.Equal(r2.Tag, await u2.ResumeAsync(null, default));                                  // the kept zip is checked and unpacked again
        Assert.True(u2.CheckFiles(r2.Tag).Ok);
    }

    [Fact]
    public async Task Killed_in_the_middle_of_a_reinstall_swap_puts_the_new_copy_in_place()
    {
        using var t = new TempDir();
        var (u, l, gh, r1, r2, zip2) = await TwoReleases(t);
        await u.InstallAsync(r2, null, default);
        // the reinstall of r2: the old copy was set aside, the new staging was not moved yet
        Directory.Move(l.VersionDir(r2.Tag), l.ReplacedDir(r2.Tag));
        SafeZip.Extract(WriteZip(t, zip2), l.StagingDir(r2.Tag));
        var s = UpdateState.For(r2);
        s.Phase = UpdatePhase.Switching;
        Store.WriteState(l, s);
        var u2 = Restart(l, gh);
        u2.Recover();
        Assert.Equal(r2.Tag, u2.ReadPointer().Current);
        Assert.True(u2.CheckFiles(r2.Tag).Ok);
        Assert.False(Directory.Exists(l.ReplacedDir(r2.Tag)));
        Assert.False(Directory.Exists(l.StagingDir(r2.Tag)));
    }

    [Fact]
    public async Task An_interrupted_reinstall_with_no_record_puts_the_old_copy_back()
    {
        using var t = new TempDir();
        var (u, l, gh, r1, _, _) = await TwoReleases(t);
        Directory.Move(l.VersionDir(r1.Tag), l.ReplacedDir(r1.Tag));
        var u2 = Restart(l, gh);
        u2.Recover();
        Assert.Equal(r1.Tag, u2.ReadPointer().Current);
        Assert.True(u2.IsUsable(r1.Tag));
    }

    [Fact]
    public async Task Current_pointing_at_a_broken_version_falls_back_to_the_previous_one()
    {
        using var t = new TempDir();
        var (u, l, gh, r1, r2, _) = await TwoReleases(t);
        await u.InstallAsync(r2, null, default);
        File.Delete(Path.Combine(l.VersionDir(r2.Tag), PackageLayout.GameExe));
        var u2 = Restart(l, gh);
        var rep = u2.Recover();
        Assert.Equal(r1.Tag, u2.ReadPointer().Current);
        Assert.Null(u2.ReadPointer().Previous);
        Assert.Contains(rep.Notes, n => n.Contains("went back"));
    }

    [Fact]
    public async Task An_unreadable_current_json_or_update_json_is_rebuilt_from_what_is_installed()
    {
        using var t = new TempDir();
        var (u, l, gh, r1, r2, _) = await TwoReleases(t);
        await u.InstallAsync(r2, null, default);
        File.WriteAllText(l.CurrentFile, "{ not json");
        File.WriteAllText(l.StateFile, "garbage");
        var u2 = Restart(l, gh);
        var rep = u2.Recover();
        Assert.Null(rep.Pending);
        Assert.False(File.Exists(l.StateFile));
        Assert.Equal(r2.Tag, u2.ReadPointer().Current);                                              // the newest installed
        Assert.Equal(r1.Tag, u2.ReadPointer().Previous);
    }

    [Fact]
    public async Task Leftovers_no_update_refers_to_are_removed_on_start()
    {
        using var t = new TempDir();
        var (u, l, gh, r1, r2, _) = await TwoReleases(t);
        Directory.CreateDirectory(l.DownloadsDir);
        File.WriteAllText(Path.Combine(l.DownloadsDir, r2.Asset.Name + ".part"), "stray");
        Directory.CreateDirectory(l.StagingDir(r2.Tag));
        Directory.CreateDirectory(l.VersionDir(r1.Tag) + ".trash-12345678");
        File.WriteAllText(l.CurrentFile + ".tmp", "half-written");
        var u2 = Restart(l, gh);
        u2.Recover();
        Assert.Empty(Directory.EnumerateFiles(l.DownloadsDir));
        Assert.False(Directory.Exists(l.StagingDir(r2.Tag)));
        Assert.False(Directory.Exists(l.VersionDir(r1.Tag) + ".trash-12345678"));
        Assert.False(File.Exists(l.CurrentFile + ".tmp"));
        Assert.Equal(r1.Tag, u2.ReadPointer().Current);
    }

    [Fact]
    public async Task An_interrupted_adopt_is_cleaned_up_and_the_previous_version_stays()
    {
        using var t = new TempDir();
        var (_, l, gh, r1, _, _) = await TwoReleases(t);
        var s = new UpdateState { Tag = "tester-2026-10-29-abc1234", Phase = UpdatePhase.Extracting, Source = "adopt", AdoptFrom = t["x"] };
        Store.WriteState(l, s);
        Directory.CreateDirectory(l.StagingDir(s.Tag));
        var u2 = Restart(l, gh);
        var rep = u2.Recover();
        Assert.Null(rep.Pending);
        Assert.False(Directory.Exists(l.StagingDir(s.Tag)));
        Assert.Equal(r1.Tag, u2.ReadPointer().Current);
    }

    static string WriteZip(TempDir t, byte[] zip)
    {
        string p = t["copy-" + Guid.NewGuid().ToString("N")[..6] + ".zip"];
        File.WriteAllBytes(p, zip);
        return p;
    }
}

public class AtomicSwitchTests
{
    [Fact]
    public void The_pointer_file_is_replaced_whole_and_no_temp_file_stays()
    {
        using var t = new TempDir();
        var l = new InstallLayout(t.Path);
        var p = new CurrentPointer { Current = "tester-2026-10-29-aaaaaa1" };
        Store.WritePointer(l, p);
        p.Current = "tester-2026-10-29-bbbbbb2";
        p.Previous = "tester-2026-10-29-aaaaaa1";
        Store.WritePointer(l, p);
        Assert.False(File.Exists(l.CurrentFile + ".tmp"));
        var back = Store.ReadPointer(l)!;
        Assert.Equal(("tester-2026-10-29-bbbbbb2", "tester-2026-10-29-aaaaaa1"), (back.Current, back.Previous));
    }

    [Fact]
    public void A_half_written_temp_file_never_replaces_the_pointer()
    {
        using var t = new TempDir();
        var l = new InstallLayout(t.Path);
        Store.WritePointer(l, new CurrentPointer { Current = "tester-2026-10-29-aaaaaa1" });
        File.WriteAllText(l.CurrentFile + ".tmp", "{\"current\":\"tester-2026-1");          // a crash while writing the next one
        Assert.Equal("tester-2026-10-29-aaaaaa1", Store.ReadPointer(l)!.Current);
        AtomicFile.WriteAllText(l.CurrentFile, "{\"current\":\"x\"}");                     // the next write starts over
        Assert.False(File.Exists(l.CurrentFile + ".tmp"));
    }

    [Fact]
    public void Only_one_launcher_holds_an_install_folder()
    {
        using var t = new TempDir();
        var l = new InstallLayout(t.Path);
        using (var first = RootLock.TryAcquire(l, TimeSpan.Zero))
        {
            Assert.NotNull(first);
            Assert.Null(RootLock.TryAcquire(l, TimeSpan.FromMilliseconds(300)));
        }
        using var again = RootLock.TryAcquire(l, TimeSpan.Zero);
        Assert.NotNull(again);
    }
}
