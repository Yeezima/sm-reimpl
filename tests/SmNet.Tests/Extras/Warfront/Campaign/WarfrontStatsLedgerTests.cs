using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;
using SmSim.Extras.Warfront.Results;
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
        Assert.Throws<InvalidDataException>(() => WarfrontSave.FromJson(json.Replace("\"Version\":4", "\"Version\":99")));
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
    public void MapsToPlayerStats_ElitesAndBossesAreDisjointSubsets_AndScoreMatches()
    {
        var c = Run();
        c.Stats.OnAssist(11);
        Assert.Equal(new WarfrontPlayerStats("", Kills: 2, EliteKills: 1, BossKills: 0, Assists: 1, Captures: 1), c.Stats.ToPlayerStats(11));
        Assert.Equal(new WarfrontPlayerStats("", Kills: 1, EliteKills: 0, BossKills: 1, Revives: 1), c.Stats.ToPlayerStats(12));
        Assert.Equal(0, c.Stats.ToPlayerStats(99).Kills);

        var l = new WarfrontStatsLedger();
        l.OnKill(1, MusouEnemyType.Boy); l.OnKill(1, MusouEnemyType.Boy); l.OnKill(1, MusouEnemyType.Nob); l.OnKill(1, MusouEnemyType.Warboss);
        Assert.Equal(2 + 5 + 25, WarfrontLeaderboard.PlayerScore(l.ToPlayerStats(1), WarfrontScoreWeights.Default));
    }

    [Fact]
    public void EventIds_AreUniqueAcrossDeployments_AndTheSameTripleCountsOnce()
    {
        var l = new WarfrontStatsLedger();
        Assert.True(l.OnKill(1, MusouEnemyType.Nob, WarfrontStatsLedger.EventId(1, 40, 900)));
        Assert.True(l.OnKill(1, MusouEnemyType.Nob, WarfrontStatsLedger.EventId(2, 40, 900)));
        Assert.False(l.OnKill(1, MusouEnemyType.Nob, WarfrontStatsLedger.EventId(2, 40, 900)));
        Assert.Equal(2, l.Player(1)!.EliteKills);
        Assert.NotEqual(WarfrontStatsLedger.EventId(1, 2, 3), WarfrontStatsLedger.EventId(1, 3, 2));
    }

    [Fact]
    public void FromState_ToleratesNullListsAndKeyZero()
    {
        var l = WarfrontStatsLedger.FromState(new WarfrontStatsLedger.State { Players = null, Seen = null });
        Assert.Empty(l.Players);
        var s = new WarfrontStatsLedger.State();
        s.Players!.Add(new WarfrontPlayerLedger());
        Assert.Empty(WarfrontStatsLedger.FromState(s).Players);
    }

    [Fact]
    public void ArchiveHoldsNoKeys_AndFiguresMatchPerJoinIndex()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wfstats-" + Guid.NewGuid().ToString("N"));
        try
        {
            var c = new WarfrontCampaign(5);
            c.Join(987654321, "A", out _); c.Join(555123456, "B", out _);
            c.Stats.OnKill(987654321, MusouEnemyType.Nob, WarfrontStatsLedger.EventId(1, 7, 8));
            c.Stats.OnRevive(555123456, WarfrontStatsLedger.EventId(1, 1, 2));
            c.Stats.OnDeath(777000111);
            var rec = WarfrontSave.Archive(dir, c, new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc));
            var text = File.ReadAllText(rec);
            foreach (var k in new[] { "987654321", "555123456", "777000111" }) Assert.DoesNotContain(k, text);
            var st = System.Text.Json.Nodes.JsonNode.Parse(text)!["Stats"]!;
            int Field(int idx, string name) => st["Players"]!.AsArray().Single(p => (int)p!["Key"]! == idx)![name]!.GetValue<int>();
            Assert.Equal(1, Field(0, "EliteKills"));
            Assert.Equal(1, Field(1, "Revives"));
            Assert.Equal(1, Field(2, "Deaths"));
            Assert.Empty(st["Seen"]!.AsArray());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
