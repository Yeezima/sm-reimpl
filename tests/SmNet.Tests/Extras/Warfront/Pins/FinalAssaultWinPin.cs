using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Warfront.Pins;

/// <summary>
/// W13b pin: <c>WarfrontBattleWon</c> is now called before the final assault's own end in <c>TfwRun.Fight</c> (moved up for the fortress). Under Musou (no Warfront flag) the hook is empty, so a won final assault must still end
/// the run on that very tick: <c>Phase == Won</c>, no pickup countdown, the match ended. Captured on the code before the move (claude/warfront at 13c0fcb4) with the same assertions.
/// </summary>
public class FinalAssaultWinPin
{
    [Fact]
    public void A_musou_final_assault_won_ends_the_run_on_that_tick_with_no_pickup_countdown()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        Assert.Null(run.Warfront);
        TfwRig.WinBattle(w, TfwLane.A);
        TfwRig.WinBattle(w, TfwLane.A);
        var battle = TfwRig.Deploy(w);
        Assert.Equal(TfwRunPhase.FinalAssault, run.Phase);
        TfwRig.KillAll(w);
        w.Step(_ => default);
        w.Step(_ => default);
        Assert.Equal(TfwRunPhase.Won, run.Phase);
        Assert.Equal(0, run.PickupTicks);
        Assert.True(battle.Won);
        Assert.Equal(MatchState.Ended, w.State);
    }
}
