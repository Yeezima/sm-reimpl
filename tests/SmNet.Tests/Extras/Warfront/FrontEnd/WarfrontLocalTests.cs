using SmContent;
using SmContent.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Results;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.FrontEnd;

/// <summary>Slice W16: the press logic, the server arguments and the page's reading of run.json and records (pure; synthetic temp folders, never the owner's save).</summary>
public sealed class WarfrontLocalTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf16-" + Guid.NewGuid().ToString("N"));

    public WarfrontLocalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(false, false, false, false, WarfrontPress.NoServer)]
    [InlineData(true, false, false, false, WarfrontPress.Start)]
    [InlineData(true, true, false, true, WarfrontPress.Wait)]      // starting
    [InlineData(true, true, true, true, WarfrontPress.Join)]       // ready
    [InlineData(true, true, true, false, WarfrontPress.OtherServer)]   // a Musou server runs
    [InlineData(true, true, false, false, WarfrontPress.OtherServer)]
    public void Press_table(bool exe, bool running, bool ready, bool warfront, WarfrontPress expected) =>
        Assert.Equal(expected, WarfrontLocal.OnPress(exe, running, ready, warfront));

    [Fact]
    public void Arguments_are_loopback_one_human_and_new_only_when_asked()
    {
        var cont = ServerLaunch.Arguments(WarfrontLocal.Options(27123, "game", newCampaign: false));
        var fresh = ServerLaunch.Arguments(WarfrontLocal.Options(27123, "game", newCampaign: true));
        foreach (var a in new[] { cont, fresh })
        {
            Assert.Equal("127.0.0.1", a[a.IndexOf("--bind") + 1]);
            Assert.Equal("warfront", a[a.IndexOf("--content") + 1]);
            Assert.Equal("27123", a[a.IndexOf("--port") + 1]);
            Assert.Equal("1", a[a.IndexOf("--warfront-max-humans") + 1]);
            Assert.DoesNotContain("0.0.0.0", a);
        }
        Assert.DoesNotContain("--warfront-new", cont);
        Assert.Contains("--warfront-new", fresh);
        Assert.True(WarfrontLocal.Options(1, "g", false).LocalOnly);
    }

    static WarfrontCampaign Run(int level = 1)
    {
        var c = new WarfrontCampaign(7);
        c.Embark();
        c.Join(11, "Owner", out _);
        return c;
    }

    [Fact]
    public void No_run_file_gives_no_continue_and_no_confirmation()
    {
        var run = WarfrontLocal.ReadRun(_dir);
        Assert.Equal(WarfrontRunKind.None, run.Kind);
        Assert.False(run.CanContinue);
        Assert.False(run.NewNeedsConfirm);
        Assert.Equal(["New campaign"], WarfrontLocal.Choices(run));
    }

    [Fact]
    public void A_run_gives_continue_with_a_summary_line_and_asks_before_new()
    {
        WarfrontSave.Write(_dir, Run());
        var run = WarfrontLocal.ReadRun(_dir);
        Assert.Equal(WarfrontRunKind.Continuable, run.Kind);
        Assert.True(run.CanContinue);
        Assert.True(run.NewNeedsConfirm);
        Assert.Equal("DEPLOYMENTS 0, FRONTS 1 / 1 / 1, LEVEL 1", run.Line);
        Assert.Equal(["Continue campaign", "New campaign"], WarfrontLocal.Choices(run));
    }

    [Fact]
    public void Reading_never_writes()
    {
        WarfrontSave.Write(_dir, Run());
        string path = Path.Combine(_dir, WarfrontSave.RunFile);
        var before = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path), Directory.GetFiles(_dir).Length);
        WarfrontLocal.ReadRun(_dir);
        WarfrontLocal.HonorLine(_dir);
        Assert.Equal(before.Item1, File.ReadAllBytes(path));
        Assert.Equal(before.Item2, File.GetLastWriteTimeUtc(path));
        Assert.Equal(before.Item3, Directory.GetFiles(_dir).Length);
        Assert.False(Directory.Exists(Path.Combine(_dir, "records")));
    }

    [Theory]
    [InlineData("Won")]
    [InlineData("Lost")]
    public void A_finished_run_gives_no_continue_and_no_confirmation(string phase)
    {
        string json = Run().ToJson();
        Assert.Contains("\"Phase\":\"Ship\"", json);
        File.WriteAllText(Path.Combine(_dir, WarfrontSave.RunFile), json.Replace("\"Phase\":\"Ship\"", "\"Phase\":\"" + phase + "\""));
        var run = WarfrontLocal.ReadRun(_dir);
        Assert.Equal(WarfrontRunKind.Finished, run.Kind);
        Assert.False(run.CanContinue);
        Assert.False(run.NewNeedsConfirm);
        Assert.Equal(["New campaign"], WarfrontLocal.Choices(run));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Version\":99}")]
    [InlineData("{}")]
    public void An_unreadable_run_gives_no_continue_but_asks_before_new(string text)
    {
        File.WriteAllText(Path.Combine(_dir, WarfrontSave.RunFile), text);
        var run = WarfrontLocal.ReadRun(_dir);
        Assert.Equal(WarfrontRunKind.Unreadable, run.Kind);
        Assert.False(run.CanContinue);
        Assert.True(run.NewNeedsConfirm);
    }

    static void Record(string dir, string stem, string result, int seconds)
    {
        Directory.CreateDirectory(Path.Combine(dir, "records"));
        var rec = new WarfrontRunRecord(result, "2026-11-17T00:00:00Z", seconds, WarfrontScoreWeights.Default, Array.Empty<string>(), 0, 0, 0, Array.Empty<WarfrontLeaderRow>());
        File.WriteAllText(Path.Combine(dir, "records", stem + WarfrontResultsArchive.RecordSuffix), rec.ToJson());
    }

    [Fact]
    public void Honor_line_counts_runs_and_victories_and_shows_the_fastest()
    {
        Assert.Null(WarfrontLocal.HonorLine(_dir));
        Record(_dir, "20261101T000000Z-won", "won", 2480);
        Record(_dir, "20261102T000000Z-lost", "lost", 900);
        Record(_dir, "20261103T000000Z-won", "won", 3600);
        Assert.Equal("RUNS 3, VICTORIES 2, FASTEST 41:20", WarfrontLocal.HonorLine(_dir));
    }

    [Fact]
    public void Honor_line_without_a_victory_has_no_fastest()
    {
        Record(_dir, "20261102T000000Z-lost", "lost", 900);
        Assert.Equal("RUNS 1, VICTORIES 0", WarfrontLocal.HonorLine(_dir));
    }

    [Fact]
    public void Status_line_is_the_last_warfront_line_and_elapsed_reads_as_minutes()
    {
        Assert.Equal("", WarfrontLocal.LastStatus(["smserver on UDP 1", "reading"]));
        Assert.Equal("warfront: walking graph 60 percent", WarfrontLocal.LastStatus(["12:00:01 warfront: layout built", "x", "12:00:50 warfront: walking graph 60 percent", "noise"]));
        Assert.Equal("0:00", WarfrontLocal.Elapsed(-3));
        Assert.Equal("2:05", WarfrontLocal.Elapsed(125.9));
        Assert.Equal("1:01:01", WarfrontLocal.Elapsed(3661));
    }

    [Fact]
    public void Local_campaign_is_loopback_only_in_every_argument_list()
    {
        // the page has no LAN or internet choice: Options always sets LocalOnly, so --bind 127.0.0.1 is always present
        foreach (bool n in new[] { false, true })
            Assert.Contains("--bind", ServerLaunch.Arguments(WarfrontLocal.Options(30000, "", n)));
    }

    [Theory]
    [InlineData(false, true, "Could not connect.", 0, WarfrontLocal.MenuReturn.Nothing)]
    [InlineData(true, false, "", 0, WarfrontLocal.MenuReturn.Nothing)]                  // still starting
    [InlineData(true, true, WarfrontLocal.LeftMatchText, 0, WarfrontLocal.MenuReturn.Stop)]
    [InlineData(true, true, "", 0, WarfrontLocal.MenuReturn.Stop)]
    [InlineData(true, true, "Rejected: the squad is full (1 of 1).", 0, WarfrontLocal.MenuReturn.Keep)]   // one retry
    [InlineData(true, true, "Rejected: the squad is full (1 of 1).", 1, WarfrontLocal.MenuReturn.Stop)]   // then it stops
    public void A_failed_join_keeps_the_server_once_and_leaving_stops_it(bool ours, bool joined, string message, int retries, WarfrontLocal.MenuReturn expected) =>
        Assert.Equal(expected, WarfrontLocal.AtMenu(ours, joined, message, retries));
}
