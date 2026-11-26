using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmNet.Tests.Extras.Warfront.Fortress;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Fortress;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>
/// Chaos Warfront C5: nothing of the finale reaches an Orks fortress (its Warboss is freed by the plaza as before, no duel, no shield) or a Musou Mode fortress. The seeded pins of the Orks lane, HQ and fortress
/// battles (<see cref="WarfrontChaosOrkPins"/>) and the Musou and Classic pins stay as they were: they run in the same suites.
/// </summary>
public class FinaleIsolationTests
{
    [Fact]
    public void NoDuelWithoutChaosRoster_the_orks_fortress_frees_its_warboss_at_the_plaza()
    {
        var w = TfwRig.War(config: WarfrontBattleTests.WarfrontConfig);
        TfwRig.Steps(w, 5);
        var setup = WarfrontFortressSetup.For(new WarfrontFortressBattleTests.StubArena());
        w.Tfw!.Warfront!.Fortress = setup;
        w.Tfw.War.SetFrontsForTests(WarRules.FrontStronghold);
        var b = TfwRig.Deploy(w);
        Assert.False(b.ChaosFortress);
        Assert.False(b.ChaosBodyguardDown);
        foreach (ushort id in b.BossOrks.ToList()) w.Musou!.Damage(id, 1, 1e7f, Vector3.Zero, out _);
        TfwRig.Teleport(w, setup.Plan.BossSpot + new Vector3(0f, -10f, 0f));
        TfwRig.Steps(w, 5);
        Assert.Null(w.WarfrontChaosDuel);
        Assert.False(w.Musou!.Leashed(b.BossId));   // the plaza freed him, as before C5
        Assert.True(w.Musou.Damage(b.BossId, 1, 10f, Vector3.Zero, out _));   // no shield
        Assert.False(b.ChaosDuelHold);
    }

    [Fact]
    public void MusouUnchanged_a_musou_run_has_no_warfront_rules_and_no_duel()
    {
        var w = TfwRig.War();
        Assert.Null(w.Tfw!.Warfront);
        TfwRig.Deploy(w);
        TfwRig.Steps(w, 30);
        Assert.Null(w.WarfrontChaosDuel);
        Assert.Equal(0, w.Musou!.LordTelegraphCount);
    }
}
