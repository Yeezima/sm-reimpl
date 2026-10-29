using SmLauncher.Core;

namespace SmLauncher.Tests;

public class ReleaseFeedTests
{
    static string Rel(string tag, string published, bool draft = false, bool pre = true, string assets = "", string body = "") =>
        $$"""{"tag_name":"{{tag}}","name":"build {{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(pre ? "true" : "false")}},"published_at":{{(published.Length > 0 ? "\"" + published + "\"" : "null")}},"html_url":"https://github.com/Yeezima/sm-reimpl-releases/releases/tag/{{tag}}","body":"{{body}}","assets":[{{assets}}]}""";

    static string Zip(string tag, long size = 118063419, string? digest = "sha256:dc409b3ff6b2f9a52cdc4f97cd840487de8c3f214ee25e4730eddc90fd1bb9b7", string? name = null) =>
        $$"""{"name":"{{name ?? $"SM-Reimpl-Playtest-{tag["tester-".Length..]}-Windows.zip"}}","size":{{size}},"state":"uploaded","browser_download_url":"https://github.com/Yeezima/sm-reimpl-releases/releases/download/{{tag}}/x.zip"{{(digest is null ? "" : $",\"digest\":\"{digest}\"")}}}""";

    const string Other = """{"name":"sm-reimpl-server-linux-x64-abc.tar.gz","size":5,"browser_download_url":"https://github.com/x/y"}""";

    [Fact]
    public void Picks_the_newest_published_release_including_prereleases_and_skips_drafts()
    {
        string json = "[" + string.Join(",",
            Rel("tester-2026-10-30-aaaaaaa", "", draft: true, assets: Zip("tester-2026-10-30-aaaaaaa")),        // a hidden (draft) newer build
            Rel("tester-2026-10-28-bbbbbbb", "2026-10-28T10:00:00Z", assets: Zip("tester-2026-10-28-bbbbbbb")),
            Rel("tester-2026-10-29-ccccccc", "2026-10-29T07:04:39Z", pre: true, assets: Other + "," + Zip("tester-2026-10-29-ccccccc"))) + "]";
        var r = ReleaseFeed.SelectNewest(json);
        Assert.NotNull(r);
        Assert.Equal("tester-2026-10-29-ccccccc", r!.Tag);
        Assert.True(r.Prerelease);
        Assert.Equal("SM-Reimpl-Playtest-2026-10-29-ccccccc-Windows.zip", r.Asset.Name);
        Assert.Equal(118063419, r.Asset.Size);
        Assert.Equal("dc409b3ff6b2f9a52cdc4f97cd840487de8c3f214ee25e4730eddc90fd1bb9b7", r.Asset.ApiSha256);
    }

    [Fact]
    public void Newest_is_by_publish_time_not_list_order()
    {
        string json = "[" + Rel("tester-2026-10-28-bbbbbbb", "2026-10-28T10:00:00Z", assets: Zip("tester-2026-10-28-bbbbbbb")) + "," +
                      Rel("tester-2026-10-29-ccccccc", "2026-10-29T10:00:00Z", assets: Zip("tester-2026-10-29-ccccccc")) + "]";
        Assert.Equal("tester-2026-10-29-ccccccc", ReleaseFeed.SelectNewest(json)!.Tag);
    }

    [Fact]
    public void Releases_without_exactly_one_playtest_zip_or_with_unsafe_tags_are_skipped()
    {
        string json = "[" + string.Join(",",
            Rel("tester-2026-10-31-ddddddd", "2026-10-31T00:00:00Z", assets: Other),                                                  // no zip
            Rel("tester-2026-10-30-eeeeeee", "2026-10-30T00:00:00Z", assets: Zip("tester-2026-10-30-eeeeeee") + "," + Zip("tester-2026-10-30-eeeeeee", name: "SM-Reimpl-Playtest-2-Windows.zip")),
            Rel("../../evil", "2026-11-01T00:00:00Z", assets: Zip("tester-2026-10-30-fffffff")),
            Rel("tester-2026-10-29-ccccccc", "2026-10-29T00:00:00Z", assets: Zip("tester-2026-10-29-ccccccc", digest: null))) + "]";
        var r = ReleaseFeed.SelectNewest(json);
        Assert.Equal("tester-2026-10-29-ccccccc", r!.Tag);
        Assert.Null(r.Asset.ApiSha256);
    }

    [Fact]
    public void Empty_or_all_draft_lists_give_nothing()
    {
        Assert.Null(ReleaseFeed.SelectNewest("[]"));
        Assert.Null(ReleaseFeed.SelectNewest("[" + Rel("tester-2026-10-30-aaaaaaa", "", draft: true, assets: Zip("tester-2026-10-30-aaaaaaa")) + "]"));
        Assert.Throws<InvalidDataException>(() => ReleaseFeed.SelectNewest("{}"));
    }

    [Theory]
    [InlineData("tester-2026-10-29-d211613", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData(@"a\b", false)]
    [InlineData("x.staging", false)]
    [InlineData("x.replaced", false)]
    [InlineData("x.trash-1234", false)]
    [InlineData("", false)]
    [InlineData("-x", false)]
    [InlineData("v1.", false)]                 // Windows drops the dot: the folder of "v1"
    [InlineData("v1. ", false)]
    [InlineData("tester-1\n", false)]          // "$" would have matched before the newline
    [InlineData("CON", false)]
    [InlineData("nul.1", false)]
    [InlineData("COM1", false)]
    [InlineData("lpt9.x", false)]
    [InlineData("CONSOLE-1", true)]
    public void Tags_that_become_folder_names_are_checked(string tag, bool ok) => Assert.Equal(ok, Names.IsTag(tag));

    [Fact]
    public void Asset_names_are_only_the_playtest_zip()
    {
        Assert.True(Names.IsPlaytestAsset("SM-Reimpl-Playtest-2026-10-29-d211613-Windows.zip"));
        Assert.False(Names.IsPlaytestAsset("sm-reimpl-source-d211613.zip"));
        Assert.False(Names.IsPlaytestAsset("SM-Reimpl-Playtest-../x-Windows.zip"));
    }
}

public class ReleaseNotesTests
{
    const string Hash = "dc409b3ff6b2f9a52cdc4f97cd840487de8c3f214ee25e4730eddc90fd1bb9b7";
    const string ServerHash = "1111111111111111111111111111111111111111111111111111111111111111";
    const string Zip = "SM-Reimpl-Playtest-2026-10-29-d211613-Windows.zip";

    static string Body(bool marker) =>
        "## What's new in this build\n\n- Custom shoulder images: **PNG**, `JPG` and [more](https://example.com).\n\n### Hosting and bots\n- Hosting starts no bots.\n  Continued line.\n\n" +
        (marker ? ReleaseNotes.WhatsNewEndMarker + "\n" : "") +
        "An unofficial fan project that recreates the multiplayer.\n\n## What you need\n\n- Windows\n\n## Install and start\n\n1. Download **`" + Zip + "`** (113 MB).\n\n" +
        "## Dedicated servers (for server hosts)\n\n| File | SHA-256 |\n|---|---|\n| `sm-reimpl-server-win64-d211613.zip` | `" + ServerHash + "` |\n\n" +
        "## Download check\n\nSHA-256 of `" + Zip + "`:\n```\n" + Hash + "\n```\nIn PowerShell: `Get-FileHash .\\" + Zip + "`\n";

    [Fact]
    public void Finds_the_zip_hash_of_the_download_check_not_another_files()
    {
        Assert.Equal(Hash, ReleaseNotes.FindZipHash(Body(true), Zip));
        Assert.Null(ReleaseNotes.FindZipHash("no hash here", Zip));
        Assert.Null(ReleaseNotes.FindZipHash("| `other.zip` | `" + ServerHash + "` |", Zip));
        Assert.Equal(ServerHash, ReleaseNotes.FindZipHash("| `" + Zip + "` | `" + ServerHash + "` |", Zip));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Whats_new_is_the_top_section_as_plain_text(bool marker)
    {
        string w = ReleaseNotes.WhatsNew(Body(marker));
        Assert.StartsWith("• Custom shoulder images: PNG, JPG and more.", w);
        Assert.Contains("Hosting and bots:", w);
        Assert.Contains("• Hosting starts no bots.", w);
        Assert.Contains("Continued line.", w);
        Assert.DoesNotContain("unofficial", w);
        Assert.DoesNotContain("**", w);
        Assert.DoesNotContain("`", w);
        Assert.DoesNotContain("What you need", w);
    }

    [Fact]
    public void No_whats_new_section_gives_empty_text() => Assert.Equal("", ReleaseNotes.WhatsNew("An unofficial fan project.\n\n## What you need\n"));
}
