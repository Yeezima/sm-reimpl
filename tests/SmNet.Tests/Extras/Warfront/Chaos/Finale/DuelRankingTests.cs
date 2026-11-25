using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Chaos.Finale;
using SmSim.Extras.Warfront.Results;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>Chaos Warfront C5: the duel queue's order (pure): the leaderboard's total, its tie-breaks, then the join order; only the living; the same order every time.</summary>
public class DuelRankingTests
{
    static (ulong, WarfrontContributor) Row(ulong key, int join, string name, int kills = 0, int revives = 0, int captures = 0, int deaths = 0) =>
        (key, WarfrontContributor.Solo(new WarfrontPlayerStats(name, true, join, Kills: kills, Revives: revives, Captures: captures, Deaths: deaths)));

    [Fact]
    public void RankByTotalThenTieBreaksThenJoinIndex()
    {
        var rows = new[]
        {
            Row(10, 0, "a", kills: 5),
            Row(11, 1, "b", kills: 9),
            Row(12, 2, "c", kills: 5),
            Row(13, 3, "d", kills: 5, captures: 0, revives: 0, deaths: 3),
        };
        var q = WarfrontDuelRanking.Queue(rows, _ => true);
        Assert.Equal(11ul, q[0]);   // the most points
        // a, c and d tie on points; the weights' tie-breaks (captures, revives, fewer deaths) put d last, then the join order
        Assert.Equal(new ulong[] { 11, 10, 12, 13 }, q);
    }

    [Fact]
    public void DownedNotLiving()
    {
        var rows = new[] { Row(1, 0, "a", kills: 50), Row(2, 1, "b", kills: 1) };
        Assert.Equal(new ulong[] { 2 }, WarfrontDuelRanking.Queue(rows, k => k != 1));   // the caller's test: 1 is downed
    }

    [Fact]
    public void DisconnectedNotLiving()
    {
        var c = new WarfrontCampaign(7);
        c.Join(1, "a", out _);
        c.Join(2, "b", out _);
        c.Leave(1);
        var q = WarfrontDuelRanking.Queue(c, k => c.IsConnected(k));
        Assert.Equal(new ulong[] { 2 }, q);
    }

    [Fact]
    public void PuppetsNever()
    {
        // the ranking only knows the campaign's keys: a puppet is never one (the World's living test also refuses its ids)
        var c = new WarfrontCampaign(7);
        c.Join(1, "a", out _);
        var q = WarfrontDuelRanking.Queue(c, _ => true);
        Assert.Equal(new ulong[] { 1 }, q);
    }

    [Fact]
    public void StableAcrossRepeats()
    {
        var rows = Enumerable.Range(0, 8).Select(i => Row((ulong)(100 + i), i, "p" + i, kills: i % 3)).ToArray();
        var first = WarfrontDuelRanking.Queue(rows, _ => true);
        for (int k = 0; k < 20; k++) Assert.Equal(first, WarfrontDuelRanking.Queue(rows.Reverse().ToArray(), _ => true));
    }

    [Fact]
    public void LiveLedgerCountsThisBattle()
    {
        var c = new WarfrontCampaign(7);
        c.Join(1, "a", out _);
        c.Join(2, "b", out _);
        Assert.Equal(new ulong[] { 1, 2 }, WarfrontDuelRanking.Queue(c, _ => true));   // the join order on a tie
        c.Stats.OnKill(2, SmSim.Extras.Musou.MusouEnemyType.Boy);   // the battle's feed writes the ledger live
        Assert.Equal(new ulong[] { 2, 1 }, WarfrontDuelRanking.Queue(c, _ => true));
    }
}
