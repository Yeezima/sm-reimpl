using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using SmLauncher.Core;

namespace SmLauncher.Tests;

public class RangeResumeTests
{
    [Fact]
    public void Range_header_asks_from_the_bytes_already_there()
    {
        Assert.Null(RangeResume.RequestRange(0));
        var r = RangeResume.RequestRange(1234)!;
        Assert.Equal(1234, r.Ranges.Single().From);
        Assert.Null(r.Ranges.Single().To);
    }

    [Theory]
    [InlineData(206, 100, 1000, 100L, 1000L, RangeAction.Append)]
    [InlineData(206, 100, 1000, 50L, 1000L, RangeAction.DiscardAndRetry)]     // resumed at the wrong place
    [InlineData(206, 100, 1000, 100L, 999L, RangeAction.Fail)]               // the file changed size
    [InlineData(200, 100, 1000, null, null, RangeAction.StartOver)]          // no resume support: start again
    [InlineData(200, 0, 1000, null, null, RangeAction.StartOver)]
    [InlineData(416, 1000, 1000, null, null, RangeAction.Complete)]
    [InlineData(416, 999, 1000, null, null, RangeAction.DiscardAndRetry)]
    [InlineData(404, 0, 1000, null, null, RangeAction.Fail)]
    [InlineData(410, 0, 1000, null, null, RangeAction.Fail)]
    [InlineData(503, 10, 1000, null, null, RangeAction.Retry)]
    [InlineData(429, 10, 1000, null, null, RangeAction.Retry)]
    [InlineData(403, 10, 1000, null, null, RangeAction.Retry)]               // an expired signed link
    [InlineData(400, 10, 1000, null, null, RangeAction.Fail)]
    public void Answers_are_mapped_to_actions(int status, long have, long size, long? from, long? total, RangeAction expected)
    {
        ContentRangeHeaderValue? cr = from is long f ? new ContentRangeHeaderValue(f, (total ?? size) - 1, total ?? size) : null;
        Assert.Equal(expected, RangeResume.Decide(status, have, size, cr, null).Action);
    }

    [Fact]
    public void A_whole_file_answer_of_the_wrong_size_is_refused() =>
        Assert.Equal(RangeAction.Fail, RangeResume.Decide(200, 0, 1000, null, 999).Action);
}

public class DownloaderTests
{
    const string Url = "https://github.com/Yeezima/sm-reimpl-releases/releases/download/t/SM-Reimpl-Playtest-x-Windows.zip";
    static readonly byte[] Data = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31 + 7)).ToArray();

    static (Downloader, FakeGitHub) Make(DownloadOptions? o = null)
    {
        var gh = new FakeGitHub();
        gh.Serve(Url, Data);
        return (new Downloader(new SafeHttp(NetPolicy.GitHub, gh), o ?? Builds.Fast), gh);
    }

    [Fact]
    public async Task Downloads_the_whole_file_and_reports_progress()
    {
        using var t = new TempDir();
        var (dl, gh) = Make();
        var seen = new List<DownloadProgress>();
        await dl.DownloadAsync(new Uri(Url), t["x.part"], Data.Length, new ActionProgress<DownloadProgress>(seen.Add), default);
        Assert.Equal(Data, File.ReadAllBytes(t["x.part"]));
        Assert.Null(gh.Requests.Single().RangeFrom);
        Assert.Equal(Data.Length, seen[^1].Done);
        Assert.Equal(1.0, seen[^1].Fraction);
    }

    [Fact]
    public async Task Resumes_a_part_file_with_a_range_request()
    {
        using var t = new TempDir();
        File.WriteAllBytes(t["x.part"], Data[..123_456]);
        var (dl, gh) = Make();
        await dl.DownloadAsync(new Uri(Url), t["x.part"], Data.Length, null, default);
        Assert.Equal(Data, File.ReadAllBytes(t["x.part"]));
        Assert.Equal(123_456, gh.Requests.Single().RangeFrom);
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_restarts_the_file()
    {
        using var t = new TempDir();
        File.WriteAllBytes(t["x.part"], Data[..1000]);
        var (dl, gh) = Make();
        gh.IgnoreRange = true;
        await dl.DownloadAsync(new Uri(Url), t["x.part"], Data.Length, null, default);
        Assert.Equal(Data, File.ReadAllBytes(t["x.part"]));
    }

    [Fact]
    public async Task A_complete_part_file_needs_no_request()
    {
        using var t = new TempDir();
        File.WriteAllBytes(t["x.part"], Data);
        var (dl, gh) = Make();
        await dl.DownloadAsync(new Uri(Url), t["x.part"], Data.Length, null, default);
        Assert.Empty(gh.Requests);
    }

    [Fact]
    public async Task A_part_file_longer_than_the_file_is_discarded()
    {
        using var t = new TempDir();
        File.WriteAllBytes(t["x.part"], Data.Concat(new byte[10]).ToArray());
        var (dl, _) = Make();
        await dl.DownloadAsync(new Uri(Url), t["x.part"], Data.Length, null, default);
        Assert.Equal(Data, File.ReadAllBytes(t["x.part"]));
    }

    [Fact]
    public async Task Broken_connections_are_retried_and_each_retry_resumes()
    {
        using var t = new TempDir();
        var waits = new List<TimeSpan>();
        var o = new DownloadOptions { Delay = (d, _) => { waits.Add(d); return Task.CompletedTask; } };
        var (dl, gh) = Make(o);
        gh.Then(Url, -50_000, -50_000, 503, -50_000);            // drop, drop, a server error, drop, then the rest
        var notes = new List<string>();
        await dl.DownloadAsync(new Uri(Url), t["x.part"], Data.Length, new ActionProgress<DownloadProgress>(p => { if (p.Note is not null) notes.Add(p.Note); }), default);
        Assert.Equal(Data, File.ReadAllBytes(t["x.part"]));
        Assert.Equal(new long?[] { null, 50_000, 100_000, 100_000, 150_000 }, gh.Requests.Select(r => r.RangeFrom).ToArray());
        Assert.Equal(4, notes.Count);
        Assert.All(notes, n => Assert.Contains("Trying again", n));
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2)], waits);   // progress resets the count
    }

    [Fact]
    public async Task Gives_up_after_five_failures_in_a_row_with_a_clear_message_and_keeps_the_part()
    {
        using var t = new TempDir();
        var waits = new List<TimeSpan>();
        var (dl, gh) = Make(new DownloadOptions { Delay = (d, _) => { waits.Add(d); return Task.CompletedTask; } });
        gh.Then(Url, -10_000, 503, 503, 503, 503, 503);
        var e = await Assert.ThrowsAsync<DownloadFailedException>(() => dl.DownloadAsync(new Uri(Url), t["x.part"], Data.Length, null, default));
        Assert.Contains("failed 5 times in a row", e.Message);
        Assert.Contains("press Update to try again", e.Message);
        Assert.Equal(10_000, new FileInfo(t["x.part"]).Length);
        Assert.Equal([2, 4, 8, 16], waits.Select(w => (int)w.TotalSeconds));
    }

    [Fact]
    public async Task A_missing_file_fails_at_once()
    {
        using var t = new TempDir();
        var (dl, gh) = Make();
        gh.Then(Url, 404);
        var e = await Assert.ThrowsAsync<DownloadFailedException>(() => dl.DownloadAsync(new Uri(Url), t["x.part"], Data.Length, null, default));
        Assert.Contains("no longer on GitHub", e.Message);
        Assert.Single(gh.Requests);
    }

    [Fact]
    public async Task Redirects_to_githubs_download_host_are_followed_with_the_range()
    {
        using var t = new TempDir();
        File.WriteAllBytes(t["x.part"], Data[..777]);
        var (dl, gh) = Make();
        const string cdn = "https://release-assets.githubusercontent.com/github-production-release-asset/1/abc?sig=x";
        gh.Redirect(Url, cdn);
        gh.Serve(cdn, Data);
        await dl.DownloadAsync(new Uri(Url), t["x.part"], Data.Length, null, default);
        Assert.Equal(Data, File.ReadAllBytes(t["x.part"]));
        Assert.Equal([777L, 777L], gh.Requests.Select(r => r.RangeFrom!.Value));
        Assert.Equal("release-assets.githubusercontent.com", gh.Requests[1].Url.Host);
    }

    [Theory]
    [InlineData("https://evil.example/x.zip")]
    [InlineData("http://objects.githubusercontent.com/x.zip")]
    [InlineData("file:///C:/x.zip")]
    public async Task Redirects_away_from_github_or_https_are_refused(string target)
    {
        using var t = new TempDir();
        var (dl, gh) = Make();
        gh.Redirect(Url, target);
        await Assert.ThrowsAsync<NetPolicyException>(() => dl.DownloadAsync(new Uri(Url), t["x.part"], Data.Length, null, default));
        Assert.Single(gh.Requests);
    }

    [Fact]
    public async Task Cancel_keeps_what_was_downloaded()
    {
        using var t = new TempDir();
        var (dl, gh) = Make();
        using var cts = new CancellationTokenSource();
        gh.OnServed = sent => { if (sent >= 100_000) cts.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dl.DownloadAsync(new Uri(Url), t["x.part"], Data.Length, null, cts.Token));
        gh.OnServed = null;
        long kept = new FileInfo(t["x.part"]).Length;
        Assert.InRange(kept, 1, Data.Length - 1);
        await dl.DownloadAsync(new Uri(Url), t["x.part"], Data.Length, null, default);
        Assert.Equal(Data, File.ReadAllBytes(t["x.part"]));
        Assert.Equal(kept, gh.Requests[^1].RangeFrom);
    }

    [Fact]
    public void Speed_and_time_left_come_from_the_last_seconds()
    {
        var time = new ManualTime();
        var m = new SpeedMeter(time);
        m.Reset(0);
        time.Advance(TimeSpan.FromSeconds(1));
        var (speed, left) = m.Add(1_000_000, 10_000_000);
        Assert.Equal(1_000_000, speed, 1);
        Assert.Equal(9, left!.Value.TotalSeconds, 1);
    }

    sealed class ManualTime : TimeProvider
    {
        long _ticks = 1;
        public void Advance(TimeSpan t) => _ticks += (long)(t.TotalSeconds * TimestampFrequency);
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => 10_000_000;
    }
}

/// <summary>The same resume and retry over a real socket: a tiny HTTP/1.1 server on 127.0.0.1 (a random port; never every interface) that breaks the first connection.</summary>
public class LoopbackDownloadTests
{
    [Fact]
    public async Task Resumes_over_a_real_connection_that_breaks_mid_way()
    {
        var data = Enumerable.Range(0, 2_000_000).Select(i => (byte)(i % 251)).ToArray();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ranges = new List<long>();
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            int connection = 0;
            while (!stop.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(stop.Token); } catch (OperationCanceledException) { break; }
                connection++;
                using (c)
                {
                    var s = c.GetStream();
                    var reader = new StreamReader(s, Encoding.ASCII, false, 1024, leaveOpen: true);
                    long from = 0;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                        if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) from = long.Parse(line["Range: bytes=".Length..].TrimEnd('-'));
                    lock (ranges) ranges.Add(from);
                    string head = from > 0
                        ? $"HTTP/1.1 206 Partial Content\r\nContent-Length: {data.Length - from}\r\nContent-Range: bytes {from}-{data.Length - 1}/{data.Length}\r\nConnection: close\r\n\r\n"
                        : $"HTTP/1.1 200 OK\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n";
                    await s.WriteAsync(Encoding.ASCII.GetBytes(head));
                    int count = connection == 1 ? 700_000 : data.Length - (int)from;      // the first connection ends after 700 KB of the promised 2 MB
                    await s.WriteAsync(data.AsMemory((int)from, count));
                    await s.FlushAsync();
                }
            }
        });
        try
        {
            using var t = new TempDir();
            var policy = new NetPolicy([], [], allowHttpLoopback: true);
            var dl = new Downloader(new SafeHttp(policy), Builds.Fast);
            await dl.DownloadAsync(new Uri($"http://127.0.0.1:{port}/x.zip"), t["x.part"], data.Length, null, default);
            Assert.Equal(data, File.ReadAllBytes(t["x.part"]));
            Assert.Equal(2, ranges.Count);
            Assert.Equal(0, ranges[0]);
            Assert.Equal(700_000, ranges[1]);                                         // resumed exactly where the broken connection stopped
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await server; } catch (Exception) { }
        }
    }

    [Fact]
    public void The_github_policy_allows_only_github_over_https()
    {
        var p = NetPolicy.GitHub;
        Assert.True(p.IsAllowed(new Uri("https://api.github.com/repos/Yeezima/sm-reimpl-releases/releases")));
        Assert.True(p.IsAllowed(new Uri("https://github.com/Yeezima/sm-reimpl-releases/releases/download/t/x.zip")));
        Assert.True(p.IsAllowed(new Uri("https://objects.githubusercontent.com/x")));
        Assert.True(p.IsAllowed(new Uri("https://release-assets.githubusercontent.com/x")));
        Assert.False(p.IsAllowed(new Uri("http://github.com/x")));
        Assert.False(p.IsAllowed(new Uri("https://githubusercontent.com.evil.example/x")));
        Assert.False(p.IsAllowed(new Uri("https://evilgithub.com/x")));
        Assert.False(p.IsAllowed(new Uri("https://user:pw" + "@" + "github.com/x")));
        Assert.False(p.IsAllowed(new Uri("http://127.0.0.1:1234/x")));
        Assert.True(new NetPolicy([], [], true).IsAllowed(new Uri("http://127.0.0.1:1234/x")));
        Assert.False(new NetPolicy([], [], true).IsAllowed(new Uri("http://10.0.0.1:1234/x")));
    }
}
