using SmContent.Extras.Warfront;
using SmNet.Tests.Extras.Warfront.Battle;
using SmServer;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Faction;

namespace SmNet.Tests.Extras.Warfront.Chaos;

/// <summary>
/// Chaos Warfront C2: the stage's army in its config (<c>WarfrontStageArmy</c>), and the case the C2 review (R2) names for integration: a stage started on a damaged run.json records Orks, its loader
/// then builds a new run under a forced Chaos setting, and that run's army must not land on the Orks stage (<see cref="WarfrontStagePlan.CanLandHere"/> says restage; the call at the deploy,
/// GameServer.WarfrontStage.cs, is T2's file and is wired at integration). Serial with the stage tests (the stage's army and the setting are process-wide).
/// </summary>
[Collection("WarfrontStage")]
public sealed class WarfrontChaosStageArmyTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-chaosarmy-" + Guid.NewGuid().ToString("N"));

    public WarfrontChaosStageArmyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        WarfrontRunFiles.FactionSetting = WarfrontFactionSetting.Alternate;
        WarfrontRunFiles.StageFaction = WarfrontFaction.Orks;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void A_chaos_stage_gets_the_chaos_table_and_an_orks_stage_its_config_unchanged()
    {
        var orks = WarfrontBattleTests.WarfrontConfig;
        WarfrontRunFiles.StageFaction = WarfrontFaction.Orks;
        Assert.Same(orks, WarfrontStageArmy.Apply(orks, out string? none));
        Assert.Null(none);
        WarfrontRunFiles.StageFaction = WarfrontFaction.Chaos;
        var chaos = WarfrontStageArmy.Apply(orks, out string? note);
        Assert.Equal(WarfrontFaction.Chaos, chaos.WarfrontFaction);
        Assert.Equal(WarfrontChaosTuning.CultistHealth, chaos.MusouRules!.Of(SmSim.Extras.Musou.MusouEnemyType.Boy).Health);
        Assert.Contains("Warfront Chaos roster", note);
    }

    [Fact]
    public void A_damaged_save_under_a_chaos_setting_gives_a_chaos_run_that_must_not_land_on_the_orks_stage()
    {
        File.WriteAllText(Path.Combine(_dir, WarfrontSave.RunFile), "{ damaged");
        Assert.Contains("Orks", WarfrontRunFiles.RecordStageFaction(_dir));   // nothing readable: the stage records Orks
        Assert.Equal(WarfrontFaction.Orks, WarfrontRunFiles.StageFaction);
        WarfrontRunFiles.FactionSetting = WarfrontFactionSetting.Chaos;      // the stage child's --warfront-faction chaos
        var load = WarfrontRunLoader.Load(_dir, null);
        Assert.Equal(WarfrontFaction.Chaos, load.Campaign.Faction);           // the loader's new run after the damaged save
        string map = WarfrontStagePlan.MapOf(load.Campaign, WarfrontTarget.Node(0));
        Assert.False(WarfrontStagePlan.CanLandHere(map, map, load.Campaign.Faction, WarfrontRunFiles.StageFaction), "the Orks stage must restage for the Chaos run (wired at GameServer.WarfrontStage.cs at integration)");
    }
}
