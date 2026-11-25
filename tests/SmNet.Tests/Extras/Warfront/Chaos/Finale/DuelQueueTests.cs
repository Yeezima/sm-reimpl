using SmSim.Extras.Warfront.Chaos.Finale;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>Chaos Warfront C5: the duel relay's queue (pure): one attempt each, a disconnect advances, an absent player is skipped at his turn, a returning one spectates, the exhausted queue is the loss.</summary>
public class DuelQueueTests
{
    static readonly Func<ulong, bool> All = _ => true;

    [Fact]
    public void OneAttemptEach()
    {
        var d = new WarfrontDuel();
        Assert.True(d.Snapshot([3, 1, 2], 7));
        Assert.Equal(WarfrontDuelPhase.Snapshot, d.Phase);
        Assert.Equal(7, d.LordId);
        Assert.Equal(3ul, d.Begin(All));
        Assert.Equal(2, d.Remaining);
        Assert.Equal(1ul, d.Next());
        Assert.True(d.Advance(WarfrontDuelEnd.Defeated));
        Assert.Equal(WarfrontDuelPhase.Handoff, d.Phase);
        Assert.Equal(1ul, d.Begin(All));
        Assert.True(d.Advance(WarfrontDuelEnd.Defeated));
        Assert.Equal(2ul, d.Begin(All));
        Assert.Equal(2, d.AttemptIndex);
        Assert.Equal(0, d.Remaining);
        Assert.True(d.Advance(WarfrontDuelEnd.Defeated));
        Assert.Equal(WarfrontDuelPhase.Lost, d.Phase);
        Assert.Equal(3, d.Record.Count);
        Assert.True(d.Exhausted);
        Assert.False(d.Snapshot([9], 1));   // never twice
    }

    [Fact]
    public void DisconnectAdvances()
    {
        var d = new WarfrontDuel();
        d.Snapshot([1, 2], 5);
        d.Begin(All);
        Assert.True(d.Advance(WarfrontDuelEnd.Disconnected));
        Assert.Equal(WarfrontDuelPhase.Handoff, d.Phase);
        Assert.Equal(WarfrontDuelEnd.Disconnected, d.Record[0].End);
        Assert.Equal(2ul, d.Begin(All));
    }

    [Fact]
    public void AbsentAtTurnSkipped()
    {
        var d = new WarfrontDuel();
        d.Snapshot([1, 2, 3], 5);
        d.Begin(All);
        d.Advance(WarfrontDuelEnd.Defeated);
        Assert.Equal(3ul, d.Begin(k => k != 2));   // 2 is away at his turn
        Assert.Contains(new WarfrontDuelAttempt(2, WarfrontDuelEnd.Skipped), d.Record);
        Assert.True(d.Attempted(2));
    }

    [Fact]
    public void ReturningAfterTurnSpectates()
    {
        var d = new WarfrontDuel();
        d.Snapshot([1, 2, 3], 5);
        Assert.Equal(1ul, d.Begin(k => k != 2 && k != 1 ? true : k == 1));
        d.Advance(WarfrontDuelEnd.Defeated);
        Assert.Equal(3ul, d.Begin(k => k != 2));   // 2 skipped
        d.Advance(WarfrontDuelEnd.Defeated);
        Assert.Equal(WarfrontDuelPhase.Lost, d.Phase);   // 2 is back now, but his turn is gone
        Assert.Null(d.Begin(All));
    }

    [Fact]
    public void ExhaustedIsLoss()
    {
        var d = new WarfrontDuel();
        d.Snapshot([1, 2], 5);
        d.Begin(All);
        d.Advance(WarfrontDuelEnd.Defeated);
        Assert.Null(d.Begin(_ => false));   // the last one is away
        Assert.Equal(WarfrontDuelPhase.Lost, d.Phase);
        Assert.True(d.Exhausted);
    }

    [Fact]
    public void SoloOneAttempt()
    {
        var d = new WarfrontDuel();
        d.Snapshot([4], 5);
        Assert.Equal(4ul, d.Begin(All));
        Assert.Null(d.Next());
        Assert.True(d.Advance(WarfrontDuelEnd.Defeated));
        Assert.Equal(WarfrontDuelPhase.Lost, d.Phase);   // owner: one duel death loses a solo campaign
    }

    [Fact]
    public void WinOnlyInTheDuel_and_an_empty_snapshot_is_refused()
    {
        var d = new WarfrontDuel();
        Assert.False(d.Snapshot([], 5));
        Assert.Equal(WarfrontDuelPhase.Bodyguard, d.Phase);
        Assert.False(d.Win());
        d.Snapshot([1, 1, 2], 5);
        Assert.Equal(2, d.Queue.Count);
        d.Begin(All);
        int v = d.Version;
        Assert.True(d.Win());
        Assert.Equal(WarfrontDuelPhase.Won, d.Phase);
        Assert.True(d.Version > v);
        Assert.False(d.Active);
    }
}
