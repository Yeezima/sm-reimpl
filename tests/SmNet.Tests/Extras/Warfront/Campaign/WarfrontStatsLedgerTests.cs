using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.Campaign;

public class WarfrontStatsLedgerTests
{
    [Fact]
    public void KillsCountByClass_AndKillsIncludeSubsets()
    {
        var l = new WarfrontStatsLedger();
        l.OnKill(1, MusouEnemyType.Boy);
        l.OnKill(1, MusouEnemyType.Nob);
        l.OnKill(1, MusouEnemyType.Warboss);
        l.OnKill(1, WarfrontKillClass.Ordinary);
        var p = l.Player(1)!;
        Assert.Equal((2, 1, 1, 4), (p.OrdinaryKills, p.EliteKills, p.BossKills, p.Kills));
    }

    [Fact]
    public void EachCounter()
    {
        var l = new WarfrontStatsLedger();
        l.OnAssist(2); l.OnAssist(2);
        l.OnDeath(2);
        l.OnRevive(2); l.OnRevive(2); l.OnRevive(2);
        l.OnCapture(2); l.OnCapture(2, dBattle: true);
        l.OnBattle(2);
        var p = l.Player(2)!;
        Assert.Equal((2, 1, 3, 1, 1, 2, 1), (p.Assists, p.Deaths, p.Revives, p.CapturesAbc, p.CapturesD, p.Captures, p.Battles));
    }

    [Fact]
    public void DuplicateEventIdsCountOnce_ZeroIsUnguarded_CountersAreIndependent()
    {
        var l = new WarfrontStatsLedger();
        Assert.True(l.OnKill(1, MusouEnemyType.Nob, 77));
        Assert.False(l.OnKill(1, MusouEnemyType.Nob, 77));
        Assert.True(l.OnAssist(1, 77));
        Assert.False(l.OnAssist(1, 77));
        Assert.True(l.OnKill(2, MusouEnemyType.Nob, 77));
        Assert.True(l.OnBattle(1, 3));
        Assert.False(l.OnBattle(1, 3));
        Assert.True(l.OnDeath(1)); Assert.True(l.OnDeath(1));
        Assert.Equal(1, l.Player(1)!.EliteKills);
        Assert.Equal(2, l.Player(1)!.Deaths);
        Assert.Equal(1, l.Player(2)!.EliteKills);
    }

    [Fact]
    public void KeyZeroIsIgnored()
    {
        var l = new WarfrontStatsLedger();
        Assert.False(l.OnKill(0, MusouEnemyType.Boy));
        Assert.Empty(l.Players);
    }

    [Fact]
    public void StateRoundTrip_KeepsGuard_AndIsDeterministic()
    {
        var l = new WarfrontStatsLedger();
        l.OnKill(9, MusouEnemyType.Boy); l.OnKill(3, MusouEnemyType.Nob, 5); l.OnCapture(3, true, 8);
        var back = WarfrontStatsLedger.FromState(l.ToState());
        Assert.False(back.OnKill(3, MusouEnemyType.Nob, 5));
        Assert.False(back.OnCapture(3, true, 8));
        Assert.Equal(1, back.Player(3)!.CapturesD);
        Assert.Equal(new[] { 3UL, 9UL }, l.ToState().Players.Select(p => p.Key).ToArray());
        Assert.Empty(WarfrontStatsLedger.FromState(null).Players);
    }

    static WarfrontCampaign Run()
    {
        var c = new WarfrontCampaign(5);
        c.Join(11, "Alpha", out _); c.Join(12, "Bravo", out _);
        c.Stats.OnKill(11, MusouEnemyType.Boy); c.Stats.OnKill(11, MusouEnemyType.Nob); c.Stats.OnKill(12, MusouEnemyType.Warboss);
        c.Stats.OnRevive(12); c.Stats.OnCapture(11);
        return c;
    }

    [Fact]
    public void SaveRoundTrip_ThroughRunJson()
    {
        var c = Run();
        var back = WarfrontSave.FromJson(c.ToJson());
        Assert.Equal(c.StateHash(), back.StateHash());
        Assert.Equal(2, back.Stats.Player(11)!.Kills);
        Assert.Equal(1, back.Stats.Player(12)!.BossKills);
    }

    [Fact]
    public void OldVersionSaveLoadsWithEmptyLedger()
    {
        var json = new WarfrontCampaign(5).ToJson();
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["Version"] = 1; node.Remove("Stats");
        var old = node.ToJsonString();
        Assert.DoesNotContain("Stats", old);
        var c = WarfrontSave.FromJson(old);
        Assert.Empty(c.Stats.Players);
        Assert.Throws<InvalidDataException>(() => WarfrontSave.FromJson(json.Replace("\"Version\":2", "\"Version\":99")));
    }

    [Fact]
    public void ReturningPlayerKeepsStats()
    {
        var c = Run();
        var back = WarfrontSave.FromJson(c.ToJson());
        Assert.True(back.Join(11, "Alpha", out var kind));
        Assert.Equal(WarfrontJoinKind.Returning, kind);
        Assert.Equal(1, back.Stats.Player(11)!.CapturesAbc);
        c.Stats.OnDeath(11);
        Assert.Equal(1, c.Stats.Player(11)!.Deaths);
    }

    [Fact]
    public void MapsToLeaderboardShape()
    {
        var c = Run();
        c.Stats.OnAssist(11);
        var rows = c.Stats.ToLeaderboard(c);
        Assert.Equal(new WarfrontLedgerStats("Alpha", Connected: true, JoinIndex: 0, Kills: 2, EliteKills: 1, BossKills: 0, Assists: 1, Deaths: 0, Revives: 0, Captures: 1), rows[0]);
        Assert.Equal(new WarfrontLedgerStats("Bravo", true, 1, 1, 1, 1, 0, 0, 1, 0), rows[1]);
        Assert.Equal(0, c.Stats.ToLeaderboard(99, "x", false, 2).Kills);
    }
}
