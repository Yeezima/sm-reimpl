using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Warfront.Pins;

/// <summary>
/// W3 isolation pins: under Musou without the Warfront flag the Warfront object is null, no hook implementation changes state (the Musou costs stay, the war clock runs, the retainer lands) and a Warboss still comes
/// after three positions; under Warfront none of those holds. The numeric Musou pins are <c>MusouRunPin</c> and the others in this folder.
/// </summary>
public class IsolationPins
{
    [Fact]
    public void Under_Musou_without_the_Warfront_flag_nothing_of_Warfront_is_present_and_a_Warboss_comes_after_three_positions()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        Assert.Null(run.Warfront);
        double clock = run.War.Clock;
        var battle = TfwRig.Deploy(w);
        Assert.False(battle.WarfrontNode);
        Assert.Equal(TfwBattleRules.StartTransport - TfwBattleRules.DeployCost, run.Transport);   // the deploy cost stands
        TfwRig.Seconds(w, 2f);
        Assert.True(run.War.Clock > clock);   // the war clock runs
        Assert.NotNull(run.RetainerOf(TfwRig.Human));   // the retainer lands
        for (int k = 0; k < 3; k++) TfwRig.Secure(w, battle, k);
        Assert.NotEqual(0, battle.BossId);
        Assert.True(w.Musou!.TryGet(battle.BossId, out var boss) && boss.Type == MusouEnemyType.Warboss);
    }

    [Fact]
    public void Under_Warfront_a_battle_never_has_a_Warboss_after_three_positions()
    {
        var w = WarfrontBattleTests.War();
        var battle = TfwRig.Deploy(w);
        for (int k = 0; k < 3; k++) TfwRig.Secure(w, battle, k);
        Assert.Equal(0, battle.BossId);
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        Assert.DoesNotContain(all.Take(n), e => e.Alive && e.Type == MusouEnemyType.Warboss);
    }
}
