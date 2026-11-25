using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Chaos.Finale;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>Chaos Warfront C5: how the finale ends: the duelist's kill wins (the fortress's win), a second challenger can win, the queue run out loses (FinaleLost), a wipe in the bodyguard fight is the normal fortress wipe, the win tripwire stays silent.</summary>
public class DuelOutcomeTests
{
    static List<TfwMapResult> Ends(SmSim.World w)
    {
        var ends = new List<TfwMapResult>();
        w.Tfw!.Warfront!.NodeEnded += (_, _, r) => ends.Add(r);
        return ends;
    }

    [Fact]
    public void DuelistKillWinsCampaign_and_TripwireSilentOnDuelWin()
    {
        var (w, b, d) = FinaleRig.Duel();
        var ends = Ends(w);
        string? report = null;
        w.Tfw!.Warfront!.FortressLog = s => report = s;
        Assert.True(w.Musou!.Damage(b.BossId, 1, 1e9f, Vector3.Zero, out _));
        FinaleRig.Steps(w, 3);
        Assert.Equal(WarfrontDuelPhase.Won, d.Phase);
        Assert.Equal([TfwMapResult.Won], ends);
        Assert.Null(w.Tfw.Battle);
        Assert.False(w.WarfrontFinaleLost);
        Assert.NotNull(report);
        Assert.DoesNotContain("FAULT", report);
    }

    [Fact]
    public void SecondChallengerWins()
    {
        var (w, b, d) = FinaleRig.Duel(humans: 2);
        var ends = Ends(w);
        Assert.True(w.TfwKillForTests(1));
        FinaleRig.Steps(w, 30 * 3 + 3);
        Assert.Equal(2, w.WarfrontDuelist);
        Assert.True(w.Musou!.Damage(b.BossId, 2, 1e9f, Vector3.Zero, out _));
        FinaleRig.Steps(w, 3);
        Assert.Equal(WarfrontDuelPhase.Won, d.Phase);
        Assert.Equal([TfwMapResult.Won], ends);
    }

    [Fact]
    public void ExhaustedFinaleLostCampaignLost()
    {
        var (w, b, d) = FinaleRig.Duel();
        var ends = Ends(w);
        Assert.True(w.TfwKillForTests(1));
        FinaleRig.Steps(w, 2);
        Assert.Equal(WarfrontDuelPhase.Lost, d.Phase);
        Assert.True(w.WarfrontFinaleLost);   // the server maps the loss to WarfrontOutcome.FinaleLost (the campaign's phase Lost: C1's test)
        Assert.Equal([TfwMapResult.Lost], ends);
        Assert.Null(w.Tfw!.Battle);
    }

    [Fact]
    public void DisconnectAdvances_and_the_last_one_leaving_loses()
    {
        var (w, b, d) = FinaleRig.Duel(humans: 2);
        w.Remove(1);
        FinaleRig.Steps(w, 1);
        Assert.Equal(WarfrontDuelPhase.Handoff, d.Phase);
        Assert.Equal(WarfrontDuelEnd.Disconnected, d.Record[^1].End);
        FinaleRig.Steps(w, 30 * 3 + 2);
        Assert.Equal(2, w.WarfrontDuelist);
    }

    [Fact]
    public void BodyguardWipeIsNormalFortressWipe()
    {
        var (w, b) = FinaleRig.Fortress();
        var ends = Ends(w);
        Assert.True(w.TfwKillForTests(1));
        FinaleRig.Steps(w, 2);
        FinaleRig.KillChosen(w, b, ticks: 1);   // the last Chosen falls with nobody living: no snapshot
        Assert.Equal(WarfrontDuelPhase.Bodyguard, w.WarfrontChaosDuel!.Phase);
        for (int t = 0; t < 30 * 60 && w.Tfw!.Battle is not null; t++) w.Step(_ => default);
        Assert.Equal([TfwMapResult.Lost], ends);   // the revive window ran out: the fortress wipe
        Assert.False(w.WarfrontFinaleLost);
    }
}
