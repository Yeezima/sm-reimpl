using SmContent.Extras.Warfront;
using SmNet.Tests.Extras.Warfront.Battle;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>Chaos Warfront C5: the Chosen and the Chaos Lord have marine-sized bodies in the Chaos table (C3 draws them x1.2 and x1.35 of a marine), for the walk, the solids and the shots; the Orks' table keeps its Boss Orks x3 and Warboss x4.5.</summary>
public class ChaosBodyTests
{
    static MusouRules Chaos => WarfrontChaosRosterTests.ChaosConfig.MusouRules!;
    static MusouRules Orks => WarfrontBattleTests.WarfrontConfig.MusouRules ?? MusouRules.Default;

    [Fact]
    public void The_lord_and_the_chosen_are_marine_sized_in_the_chaos_table()
    {
        var (r, h) = MusouSolids.Body(Chaos, MusouEnemyType.Warboss);   // the movement solids' body (as MusouSim.Body)
        Assert.Equal(0.5f * 1.35f, r, 3);
        Assert.Equal(2.3f * 1.35f, h, 3);
        var (sr, sh) = MusouSim.ShotBodyOf(Chaos, MusouEnemyType.Warboss);   // the shots'
        Assert.Equal(0.5f * 1.35f * MusouSim.ShotRadiusScale, sr, 3);
        Assert.Equal(2.3f * 1.35f, sh, 3);
        Assert.Equal(WarfrontChaosTuning.LordReach, Chaos.Of(MusouEnemyType.Warboss).MeleeRange);
        foreach (var kind in MusouBossOrks.Kinds)
        {
            var (cr, ch) = MusouSolids.Body(Chaos, kind);
            Assert.Equal(0.6f, cr, 3);
            Assert.Equal(2.76f, ch, 3);
            Assert.Equal(0.6f * MusouSim.ShotRadiusScale, MusouSim.ShotBodyOf(Chaos, kind).Radius, 3);   // the shot height is the Boss Orks' rule's until C3's ShotHeightsFromTable lands (integration note)
            Assert.Equal(WarfrontChaosTuning.ChosenReach, Chaos.Of(kind).MeleeRange);
            Assert.Equal(Orks.Of(kind).Health, Chaos.Of(kind).Health);   // only the body changed
        }
        Assert.Equal(Orks.Of(MusouEnemyType.Warboss).Health, Chaos.Of(MusouEnemyType.Warboss).Health);
    }

    [Fact]
    public void The_orks_table_keeps_its_boss_bodies()
    {
        Assert.Equal(4.5f, Orks.BossScale);
        var (r, h) = MusouSolids.Body(Orks, MusouEnemyType.Warboss);
        Assert.Equal(0.75f * 4.5f, r, 3);
        Assert.Equal(2.9f * 4.5f, h, 3);
        Assert.Equal(5.4f, Orks.Of(MusouEnemyType.Warboss).MeleeRange);
        foreach (var kind in MusouBossOrks.Kinds) Assert.Equal(MusouRules.Default.Of(kind).Radius, Orks.Of(kind).Radius);
    }
}
