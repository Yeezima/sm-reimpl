using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>Musou Mode's score (pass 2's M6b; the owner's item 17): the chain's rule, what a map banks, who scores, the local best.</summary>
public class TfwScoreTests
{
    const int Human = TfwRig.Human;
    static readonly MusouRules Rules = MusouRules.Default;

    static MusouKill Ko(MusouEnemyType type = MusouEnemyType.Boy, int player = Human) => new(1, type, player, default);

    [Theory]
    [InlineData(MusouEnemyType.Boy, 0, 10)]
    [InlineData(MusouEnemyType.Boy, 1, 12)]
    [InlineData(MusouEnemyType.Boy, 5, 20)]
    [InlineData(MusouEnemyType.Boy, 10, 30)]
    [InlineData(MusouEnemyType.Boy, 40, 30)]        // the multiplier stops at 3
    [InlineData(MusouEnemyType.Gretchin, 1, 6)]
    [InlineData(MusouEnemyType.Nob, 3, 160)]
    [InlineData(MusouEnemyType.Warboss, 10, 6000)]
    public void A_ko_earns_the_types_points_times_the_chain_rounded(MusouEnemyType type, int chain, int points) =>
        Assert.Equal(points, TfwScore.Points(Rules, type, chain));

    [Fact]
    public void The_chain_is_the_directors_two_second_window()
    {
        var score = new TfwScore();
        score.BeginMap(TfwLane.A, 1, [Human]);
        score.Note(Ko(), Rules, 30, 100);
        score.Note(Ko(), Rules, 30, 160);    // exactly 2 s: chained
        score.Note(Ko(), Rules, 30, 221);    // 2.03 s: a new chain
        var own = score.OwnOf(Human);
        Assert.Equal((10u + 12u + 10u, (ushort)3, (ushort)2), (own.MapPoints, own.Kos, own.BestChain));
    }

    [Fact]
    public void A_map_banks_on_any_result_and_the_run_adds_them_up()
    {
        var score = new TfwScore();
        score.BeginMap(TfwLane.B, 1, [Human]);
        score.Note(Ko(), Rules, 30, 10);
        score.Bank(TfwMapResult.Left);
        score.Bank(TfwMapResult.Won);        // nothing open
        score.BeginMap(TfwLane.B, 2, [Human]);
        Assert.Equal(0u, score.OwnOf(Human).MapPoints);
        score.Note(Ko(MusouEnemyType.Nob), Rules, 30, 20);   // the chain starts over at a landing
        score.Bank(TfwMapResult.Lost);
        Assert.Equal(110u, score.TotalOf(Human));
        Assert.Equal(new[] { new TfwMapScore(TfwLane.B, 1, TfwMapResult.Left, 1, 1, 10), new TfwMapScore(TfwLane.B, 2, TfwMapResult.Lost, 1, 1, 100) }, score.HistoryOf(Human));
        score.NewRun();
        Assert.Equal(0u, score.TotalOf(Human));
        Assert.Empty(score.HistoryOf(Human));
    }

    [Fact]
    public void Nothing_scores_outside_a_map()
    {
        var score = new TfwScore();
        score.Note(Ko(), Rules, 30, 10);
        Assert.Equal(0u, score.TotalOf(Human));
    }

    [Fact]
    public void In_a_war_a_humans_ko_scores_while_the_directors_score_stays_dead()
    {
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        var at = w.Players[Human].Position + new Vector3(3f, 0f, 0f);
        Assert.NotEqual((ushort)0, w.Musou!.Spawn(MusouEnemyType.Boy, at, 0f, 85f));
        int alive = w.Musou.Alive;
        TfwRig.KillAll(w);
        TfwRig.Steps(w, 2);
        var score = w.Tfw!.Score.OwnOf(Human);
        Assert.True(score.Kos >= alive, $"{score.Kos} of {alive}");
        Assert.True(score.MapPoints >= 10u * (uint)alive / 2u, score.MapPoints.ToString());
        Assert.Equal(0, w.MusouDirector!.Kos);                       // the addendum's reading: OnKill returns while its phase is Waiting
        Assert.Equal(MusouPhase.Waiting, w.MusouDirector.Phase);
    }

    [Fact]
    public void The_ai_marines_the_guardsmen_and_fire_support_score_nothing()
    {
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        var enemies = w.Musou!;
        var near = new ushort[MusouSim.Capacity];
        int n = enemies.Query(w.Players[Human].Position, 500f, near);
        Assert.True(n >= 3);
        enemies.Damage(near[0], TfwContracts.PuppetIdFirst, 1e6f, Vector3.Zero, out _);   // a retainer's KO
        enemies.Damage(near[1], 0, 1e6f, Vector3.Zero, out _);                             // a Guardsman's or a barrage's: no player
        TfwRig.Steps(w, 2);
        Assert.Equal((0u, (ushort)0), (w.Tfw!.Score.OwnOf(Human).MapPoints, w.Tfw.Score.OwnOf(Human).Kos));
        Assert.Equal(0u, w.Tfw.Score.TotalOf(TfwContracts.PuppetIdFirst));
    }

    [Fact]
    public void A_whole_run_banks_every_map_won_and_the_end_the_total_stays()
    {
        var w = TfwRig.War();
        TfwRig.WinBattle(w, TfwLane.A);
        var run = w.Tfw!;
        var first = Assert.Single(run.Score.HistoryOf(Human));
        Assert.Equal((TfwLane.A, TfwMapResult.Won), (first.Lane, first.Result));
        uint total = run.Score.TotalOf(Human);
        Assert.Equal(first.Points, total);
        TfwRig.WinBattle(w, TfwLane.A);
        Assert.Equal(2, run.Score.HistoryOf(Human).Count);
        Assert.Equal(run.Score.HistoryOf(Human).Sum(m => (long)m.Points), run.Score.TotalOf(Human));
    }

    [Fact]
    public void An_extraction_banks_as_extracted_and_a_lost_run_as_lost()
    {
        var w = TfwRig.War();
        TfwRig.Deploy(w);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 1f);
        var run = w.Tfw!;
        Assert.Equal(TfwMapResult.Extracted, Assert.Single(run.Score.HistoryOf(Human)).Result);
        TfwRig.Seconds(w, 1f);
        TfwRig.Deploy(w);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 1f);
        Assert.Equal(TfwRunPhase.Hub, run.Phase);
        TfwRig.Seconds(w, 1f);
        TfwRig.Deploy(w);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 1f);
        Assert.Equal(TfwRunPhase.Lost, run.Phase);
        Assert.Equal(TfwMapResult.Lost, run.Score.HistoryOf(Human)[^1].Result);
    }

    // ---------------------------------------------------------------- the local best

    sealed class Folder : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sm-tfw-best-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    static readonly DateTime Day = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_best_is_kept_until_beaten()
    {
        using var f = new Folder();
        Assert.Null(TfwBest.Load(f.Path));
        Assert.True(TfwBest.Note(f.Path, 500, "DEFEAT", Day));
        Assert.False(TfwBest.Note(f.Path, 400, "VICTORY", Day));
        Assert.False(TfwBest.Note(f.Path, 500, "VICTORY", Day));
        Assert.True(TfwBest.Note(f.Path, 501, "VICTORY", Day));
        Assert.Equal(new TfwBestRecord(501, "2026-11-01", "VICTORY", TfwRules.Version), TfwBest.Load(f.Path));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{\"Total\":-4,\"Date\":\"x\",\"Outcome\":\"y\",\"Version\":0}")]
    public void A_damaged_file_reads_as_no_best(string text)
    {
        using var f = new Folder();
        Directory.CreateDirectory(f.Path);
        File.WriteAllText(Path.Combine(f.Path, TfwBest.FileName), text);
        Assert.Null(TfwBest.Load(f.Path));
        Assert.True(TfwBest.Note(f.Path, 1, "DEFEAT", Day));
    }

    [Fact]
    public void A_best_from_another_rules_version_does_not_count()
    {
        using var f = new Folder();
        Directory.CreateDirectory(f.Path);
        File.WriteAllText(Path.Combine(f.Path, TfwBest.FileName), $"{{\"Total\":99999,\"Date\":\"2026-10-01\",\"Outcome\":\"VICTORY\",\"Version\":{TfwRules.Version - 1}}}");
        Assert.Null(TfwBest.Load(f.Path));
        Assert.True(TfwBest.Note(f.Path, 10, "DEFEAT", Day));
    }

    [Fact]
    public void The_result_line_has_what_players_compare()
    {
        string line = TfwBest.ResultLine(12345, "VICTORY", 3, Day);
        Assert.Contains("12,345", line);
        Assert.Contains("VICTORY", line);
        Assert.Contains("3 maps", line);
        Assert.Contains("2026-11-01", line);
        Assert.Contains($"v{TfwRules.Version}", line);
    }

    [Fact]
    public void The_table_ranks_by_total_then_id()
    {
        var ranked = TfwBest.Ranked(3, 50, [new TfwScorePlayer(2, 90), new TfwScorePlayer(1, 50)]);
        Assert.Equal(new ushort[] { 2, 1, 3 }, ranked.Select(r => r.Player));
    }
}
