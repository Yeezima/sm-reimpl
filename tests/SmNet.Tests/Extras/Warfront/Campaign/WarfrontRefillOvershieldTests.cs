using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Warfront.Campaign;

/// <summary>
/// Executions X4 phase 3 (series-37 integration): a level-up refill (<c>World.WarfrontRefill</c>) fills health and keeps an overshield: max(shield, the bar).
/// Moved here from <c>ExecutionRewardTests</c> at the series-38 integration: it reads the Warfront progression, so it lives in the Warfront folders that
/// <c>-p:SmWarfront=false</c> drops, and it builds its battle with <c>TfwRig</c> under the Warfront profile instead of the executions test kit (the executions
/// namespace stays in its own folders, <c>ExecutionsBoundaryTests</c>; <c>SM_WARFRONT</c> stays in its pinned files, <c>WarfrontBoundaryTests</c>).
/// The overshield is set directly (the reward of a completed execution is <c>ExecutionRewardTests</c>' own check).
/// </summary>
public class WarfrontRefillOvershieldTests
{
    static World Battle()
    {
        var w = TfwRig.War(config: TfwRig.Config with { Extras = ContentProfiles.WarfrontProfile });
        TfwRig.Deploy(w, TfwLane.B);
        return w;
    }

    static void Idle(World w) => w.Step(id => new PlayerInput(0, 0f, 0f, w.Players.TryGetValue(id, out var p) ? p.Yaw : 0f, InputButtons.None, 0f, w.Tick));

    [Fact]
    public void LevelUpRefillKeepsOvershield()
    {
        var w = Battle();
        Assert.True(w.Config.KeepsOvershield);   // Warfront with executions: armour above the bar is kept
        var body = Loadouts.Body(w.Config, w.Players[1]);
        w.ExecutionsSetVitalsForTests(1, 30f, body.ShieldMax * 1.5f);
        var rt = w.Tfw!.Warfront!.Progression;
        Assert.NotEmpty(rt.Xp.Capture(100000, eventKey: 0x7E57));   // a level-up for every connected player
        Idle(w);
        Assert.Equal(body.HealthMax, w.Players[1].Health);
        Assert.Equal(body.ShieldMax * 1.5f, w.Players[1].Shield, 3);
    }

    [Fact]   // the X8 acceptance name for the same check (was WarfrontExecutionAcceptanceTests.LevelUpRefill_KeepsOvershield)
    public void LevelUpRefill_KeepsOvershield() => LevelUpRefillKeepsOvershield();
}
