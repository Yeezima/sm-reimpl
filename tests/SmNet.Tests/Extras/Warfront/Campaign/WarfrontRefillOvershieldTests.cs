using SmSim;
using SmNet.Tests.Extras.Executions.Orks;

namespace SmNet.Tests.Extras.Warfront.Campaign;

/// <summary>
/// Executions X4 phase 3 (series-37 integration): a level-up refill (<c>World.WarfrontRefill</c>) fills health and keeps an overshield: max(shield, the bar).
/// Moved here from <c>ExecutionRewardTests</c> at the series-38 integration: it reads the Warfront progression, so it lives in the Warfront folders that
/// <c>-p:SmWarfront=false</c> drops (the Warfront namespace stays in its own folders).
/// </summary>
public class WarfrontRefillOvershieldTests
{
    [Fact]
    public void LevelUpRefillKeepsOvershield()
    {
        var (w, _) = ExecutionRewardTests.Executed();
        var body = Loadouts.Body(w.Config, w.Players[1]);
        w.ExecutionsSetVitalsForTests(1, 30f, body.ShieldMax * 1.5f);
        var rt = w.Tfw!.Warfront!.Progression;
        Assert.NotEmpty(rt.Xp.Capture(100000, eventKey: 0x7E57));   // a level-up for every connected player
        ExecutionOrkKit.Idle(w);
        Assert.Equal(body.HealthMax, w.Players[1].Health);
        Assert.Equal(body.ShieldMax * 1.5f, w.Players[1].Shield, 3);
    }

    [Fact]   // the X8 acceptance name for the same check (was WarfrontExecutionAcceptanceTests.LevelUpRefill_KeepsOvershield)
    public void LevelUpRefill_KeepsOvershield() => LevelUpRefillKeepsOvershield();
}
