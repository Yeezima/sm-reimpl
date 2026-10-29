using System.IO.Compression;
using SmLauncher.Core;

namespace SmLauncher.Tests;

public class HashCheckTests
{
    const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void A_matching_digest_or_description_hash_passes()
    {
        Hashes.CheckZip(A, A, A, "z.zip");
        Hashes.CheckZip(A, A, null, "z.zip");
        Hashes.CheckZip(A, null, A, "z.zip");
        Hashes.CheckZip(A, A.ToUpperInvariant(), null, "z.zip");
    }

    [Fact]
    public void Mismatch_missing_or_disagreeing_hashes_are_refused()
    {
        Assert.Contains("damaged", Assert.Throws<VerificationException>(() => Hashes.CheckZip(B, A, null, "z.zip")).Message);
        Assert.Contains("damaged", Assert.Throws<VerificationException>(() => Hashes.CheckZip(B, null, A, "z.zip")).Message);
        Assert.Contains("disagree", Assert.Throws<VerificationException>(() => Hashes.CheckZip(A, A, B, "z.zip")).Message);
        Assert.Contains("no SHA-256", Assert.Throws<VerificationException>(() => Hashes.CheckZip(A, null, null, "z.zip")).Message);
    }

    [Fact]
    public void Github_digests_are_read_only_for_sha256()
    {
        Assert.Equal(A, Hashes.FromDigest("sha256:" + A));
        Assert.Null(Hashes.FromDigest("sha512:" + A));
        Assert.Null(Hashes.FromDigest("sha256:xyz"));
        Assert.Null(Hashes.FromDigest(null));
    }

    [Fact]
    public async Task File_hashes_are_lowercase_hex()
    {
        using var t = new TempDir();
        File.WriteAllText(t["f"], "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", await Hashes.Sha256FileAsync(t["f"], null, default));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Hashes.Sha256File(t["f"]));
    }
}

public class SafeZipTests
{
    [Fact]
    public void Extracts_a_build_and_its_manifest_verifies()
    {
        using var t = new TempDir();
        var files = Builds.Files("abc1234");
        File.WriteAllBytes(t["b.zip"], Builds.Zip(files));
        int n = SafeZip.Extract(t["b.zip"], t["out"]);
        Assert.Equal(files.Count + 1, n);
        var r = Manifest.Verify(t["out"]);
        Assert.True(r.Ok, r.Describe());
        Assert.Equal(files.Count, r.Checked);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("game/../../evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("game/file.txt:stream")]
    [InlineData("\\\\server\\share\\evil.txt")]
    [InlineData("game/NUL.txt")]
    [InlineData("game/evil. ")]
    public void Zip_slip_and_unsafe_names_refuse_the_whole_zip_before_writing(string entry)
    {
        using var t = new TempDir();
        File.WriteAllBytes(t["b.zip"], Builds.Zip(Builds.Files("abc1234"), extraEntries: [entry]));
        Assert.Throws<VerificationException>(() => SafeZip.Extract(t["b.zip"], t["out"]));
        Assert.False(Directory.Exists(t["out"]));
        Assert.False(File.Exists(Path.Combine(t.Path, "evil.txt")));
    }

    [Fact]
    public void Two_entries_for_one_file_are_refused()
    {
        using var t = new TempDir();
        using (var zip = ZipFile.Open(t["b.zip"], ZipArchiveMode.Create))
        {
            zip.CreateEntry("game/a.txt");
            zip.CreateEntry("GAME/A.TXT");
        }
        Assert.Throws<VerificationException>(() => SafeZip.Extract(t["b.zip"], t["out"]));
    }

    [Theory]
    [InlineData("game/ok.txt", @"game\ok.txt")]
    [InlineData(@"notes\CONTENTS-SHA256.txt", @"notes\CONTENTS-SHA256.txt")]
    [InlineData("2 - Play.cmd", "2 - Play.cmd")]
    [InlineData("a//b", null)]
    [InlineData("./a", null)]
    [InlineData("a/con", null)]
    [InlineData("a/COM1.log", null)]
    [InlineData("a/b*", null)]
    public void Relative_paths_are_normalised_or_refused(string input, string? expected) => Assert.Equal(expected, PackageLayout.SafeRelative(input));
}

public class ManifestTests
{
    [Fact]
    public void Missing_changed_and_unsafe_entries_are_reported()
    {
        using var t = new TempDir();
        var files = Builds.Files("abc1234");
        Builds.WriteFolder(t.Path, files);
        File.Delete(Path.Combine(t.Path, @"server\SmServer.exe"));
        File.WriteAllText(Path.Combine(t.Path, @"game\data\lib.dll"), "changed");
        File.AppendAllText(Path.Combine(t.Path, PackageLayout.ManifestPath), new string('0', 64) + "  ..\\outside.txt\r\n");
        var r = Manifest.Verify(t.Path);
        Assert.False(r.Ok);
        Assert.Equal([@"server\SmServer.exe"], r.Missing);
        Assert.Equal([@"game\data\lib.dll"], r.Changed);
        Assert.Single(r.Unsafe);
        Assert.Contains("missing", r.Describe());
    }

    [Fact]
    public void A_folder_without_a_manifest_is_not_ok()
    {
        using var t = new TempDir();
        Builds.WriteFolder(t.Path, Builds.Files("abc1234"), manifest: false);
        var r = Manifest.Verify(t.Path);
        Assert.False(r.HasManifest);
        Assert.False(r.Ok);
    }

    [Theory]
    [InlineData("sm-reimpl d211613\ncommit: d2116131234\nbuilt: 2026-10-29 (UTC)\n", "tester-2026-10-29-d211613")]
    [InlineData("sm-reimpl v0.3.0\nbuilt: 2026-11-02 (UTC)\n", "v0.3.0")]
    [InlineData("sm-reimpl 0.3.0-5-gabc1234\n", "v0.3.0-5-gabc1234")]
    [InlineData("sm-reimpl d211613\n", null)]                       // a bare hash needs the build date
    public void The_tag_of_an_unpacked_folder_follows_the_release_rule(string text, string? tag) => Assert.Equal(tag, VersionFile.Parse(text)?.Tag);
}
