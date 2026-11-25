using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Chaos.Finale;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>Chaos Warfront C5: the handoff between two challengers: the Lord's health bit for bit (never written, never healed), his effects cleared, back on his spot, a 3 s pause, the next challenger.</summary>
public class DuelHandoffTests
{
    /// <summary>A two-human duel with the Lord hurt by the first challenger, then the first challenger dead and one tick for the handoff.</summary>
    static (SmSim.World W, SmSim.Extras.Musou.Campaign.TfwBattle B, WarfrontDuel D, int Bits) Handoff()
    {
        var (w, b, d) = FinaleRig.Duel(humans: 2);
        Assert.Equal(1, w.WarfrontDuelist);
        Assert.True(w.Musou!.Damage(b.BossId, 1, FinaleRig.Lord(w, b).Health * 0.6f + 0.123f, Vector3.Zero, out _));
        FinaleRig.Steps(w, 3);
        int bits = FinaleRig.HealthBits(w, b);
        Assert.True(w.TfwKillForTests(1));
        FinaleRig.Steps(w, 1);
        Assert.Equal(WarfrontDuelPhase.Handoff, d.Phase);
        return (w, b, d, bits);
    }

    [Fact]
    public void LordHealthBitForBitAcrossHandoff_and_NoHeal()
    {
        var (w, b, d, bits) = Handoff();
        Assert.Equal(bits, FinaleRig.HealthBits(w, b));
        FinaleRig.Steps(w, 30 * 3 + 2);
        Assert.Equal(WarfrontDuelPhase.Duel, d.Phase);
        Assert.Equal(2, w.WarfrontDuelist);
        Assert.Equal(bits, FinaleRig.HealthBits(w, b));   // the second challenger meets the Lord exactly as the first left him
        Assert.True(FinaleRig.Roster(w).Admits(b.BossId, 2));
        Assert.False(FinaleRig.Roster(w).Admits(b.BossId, 1));
    }

    [Fact]
    public void ThreeSecondPause()
    {
        var (w, b, d, _) = Handoff();
        uint ends = w.WarfrontDuelHandoffEnds;
        Assert.Equal(90u, ends - w.Tick);   // set on this tick (the World's tick counts from the step's start)
        while (w.Tick < ends) { Assert.Equal(WarfrontDuelPhase.Handoff, d.Phase); Assert.False(FinaleRig.Roster(w).Admits(b.BossId, 2)); FinaleRig.Steps(w, 1); }
        FinaleRig.Steps(w, 1);
        Assert.Equal(WarfrontDuelPhase.Duel, d.Phase);
    }

    [Fact]
    public void LordReturnsToSpot_and_TemporaryEffectsCleared()
    {
        var (w, b, d) = FinaleRig.Duel(humans: 2);
        var spot = b.FortressPlan!.BossSpot;
        FinaleRig.Steps(w, 30 * 6);   // he goes for the duelist
        Assert.True(w.TfwKillForTests(1));
        FinaleRig.Steps(w, 1);
        Assert.Equal(WarfrontDuelPhase.Handoff, d.Phase);
        var l = FinaleRig.Lord(w, b);
        Assert.True(FinaleRig.Flat(l.Position, spot) < 2f, $"{l.Position} vs {spot}");
        Assert.False(w.Musou!.BossStunned(b.BossId));
        Assert.Equal(0, w.TfwBurning);
        var boss = w.MusouBoss!;
        Assert.Equal(MusouBossAction.None, boss.Action);
        Assert.False(boss.Leaping);
        FinaleRig.Steps(w, 60);
        Assert.True(FinaleRig.Flat(FinaleRig.Lord(w, b).Position, spot) < 3f);   // held there through the pause
    }
}
