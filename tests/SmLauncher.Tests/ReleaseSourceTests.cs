using SmLauncher.Core;

namespace SmLauncher.Tests;

public class ReleaseSourceTests
{
    const string ListUrl = "https://api.github.com/repos/Yeezima/sm-reimpl-releases/releases?per_page=30";

    [Fact]
    public async Task Reads_the_release_list_not_releases_latest()
    {
        var gh = new FakeGitHub();
        gh.Serve(ListUrl, """[{"tag_name":"tester-2026-10-29-d211613","draft":false,"prerelease":true,"published_at":"2026-10-29T07:04:39Z","body":"","assets":[{"name":"SM-Reimpl-Playtest-2026-10-29-d211613-Windows.zip","size":10,"browser_download_url":"https://github.com/x"}]}]""");
        var r = await new ReleaseSource(new SafeHttp(NetPolicy.GitHub, gh)).GetNewestAsync(default);
        Assert.Equal("tester-2026-10-29-d211613", r!.Tag);
        Assert.Equal(ListUrl, gh.Requests.Single().Url.AbsoluteUri);
    }

    [Fact]
    public async Task Offline_is_a_clear_check_problem_after_a_few_tries()
    {
        var gh = new FakeGitHub { Offline = true };
        var e = await Assert.ThrowsAsync<ReleaseCheckException>(() => new ReleaseSource(new SafeHttp(NetPolicy.GitHub, gh)).GetNewestAsync(default));
        Assert.Contains("offline", e.Message);
        Assert.Equal(3, gh.Requests.Count);
    }

    [Fact]
    public async Task Githubs_rate_limit_is_named()
    {
        var gh = new FakeGitHub();
        gh.Serve(ListUrl, "[]");
        gh.Then(ListUrl, 403);
        var e = await Assert.ThrowsAsync<ReleaseCheckException>(() => new ReleaseSource(new SafeHttp(NetPolicy.GitHub, gh)).GetNewestAsync(default));
        Assert.Contains("limiting", e.Message);
    }

    [Fact]
    public async Task A_server_error_is_retried()
    {
        var gh = new FakeGitHub();
        gh.Serve(ListUrl, "[]");
        gh.Then(ListUrl, 502);
        Assert.Null(await new ReleaseSource(new SafeHttp(NetPolicy.GitHub, gh)).GetNewestAsync(default));
        Assert.Equal(2, gh.Requests.Count);
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1:27015")]
    [InlineData(" 203.0.113.5:27016 ", "203.0.113.5:27016")]
    [InlineData("my-host.example", "my-host.example:27015")]
    [InlineData("host:0", null)]
    [InlineData("host:70000", null)]
    [InlineData("host name", null)]
    [InlineData("", null)]
    [InlineData("a;calc", null)]
    public void Join_addresses_follow_the_join_scripts_rule(string input, string? expected) => Assert.Equal(expected, ServerAddress.Normalise(input));
}
