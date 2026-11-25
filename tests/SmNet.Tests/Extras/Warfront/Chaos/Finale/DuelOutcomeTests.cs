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
    public void DisconnectAdvances()
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
    public void The_last_human_leaving_during_the_relay_is_FinaleLost()
    {
        // solo: the duelist leaves: the relay runs out, the battle is left (Left), the server maps it to FinaleLost (lead 40: quitting never dodges the one attempt)
        var (w, b, d) = FinaleRig.Duel();
        var ends = Ends(w);
        w.Remove(1);
        FinaleRig.Steps(w, 2);
        Assert.Equal([TfwMapResult.Left], ends);
        Assert.True(w.WarfrontFinaleLost);
        // two humans leaving together mid-duel: the relay is still on when the battle is left
        var (w2, b2, d2) = FinaleRig.Duel(humans: 2);
        var ends2 = Ends(w2);
        w2.Remove(1);
        w2.Remove(2);
        FinaleRig.Steps(w2, 2);
        Assert.Equal([TfwMapResult.Left], ends2);
        Assert.True(w2.WarfrontFinaleLost || w2.WarfrontDuelActive);
    }

    [Fact]
    public void A_burn_kill_after_the_duel_step_still_wins()
    {
        var (w, b, d) = FinaleRig.Duel();
        var ends = Ends(w);
        float h = FinaleRig.Lord(w, b).Health;
        Assert.True(w.Musou!.Damage(b.BossId, 1, h - 1f, Vector3.Zero, out _));
        FinaleRig.Steps(w, 1);
        Assert.Equal(WarfrontDuelPhase.Duel, d.Phase);
        w.WarfrontDuelBurnForTests(b.BossId, 1, 1e6f);   // lands in the next tick's burn step, after its Warfront step: the battle wins and is left that tick
        FinaleRig.Steps(w, 1);
        Assert.Equal([TfwMapResult.Won], ends);
        Assert.Null(w.Tfw!.Battle);
        Assert.Equal(WarfrontDuelPhase.Won, w.WarfrontChaosDuel!.Phase);   // settled from the battle's own win
        Assert.False(w.WarfrontDuelActive);
        Assert.False(w.WarfrontFinaleLost);
    }

    [Fact]
    public void Downed_humans_are_in_the_queue()
    {
        var (w, b) = FinaleRig.Fortress();
        Assert.True(w.TfwKillForTests(1));
        FinaleRig.Steps(w, 2);
        FinaleRig.KillChosen(w, b, ticks: 1);   // the last Chosen falls with the only human downed (lead 40: the downed are in the queue)
        var d = w.WarfrontChaosDuel!;
        Assert.Equal(WarfrontDuelPhase.Duel, d.Phase);
        Assert.Equal(1, w.WarfrontDuelist);
        Assert.True(w.Players[1].Alive);   // his fresh life at his turn
    }

    [Fact]
    public void BodyguardWipeIsNormalFortressWipe()
    {
        var (w, b) = FinaleRig.Fortress();
        var ends = Ends(w);
        Assert.True(w.TfwKillForTests(1));
        for (int t = 0; t < 30 * 60 && w.Tfw!.Battle is not null; t++) w.Step(_ => default);   // the Chosen stand; the revive window runs out
        Assert.Equal([TfwMapResult.Lost], ends);   // the fortress wipe
        Assert.Equal(WarfrontDuelPhase.Bodyguard, w.WarfrontChaosDuel!.Phase);
        Assert.False(w.WarfrontFinaleLost);
        Assert.False(w.WarfrontDuelActive);
    }
}
