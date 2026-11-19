using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Warfront.Hq;

/// <summary>W13d part 3 (owner 2026-11-17: "Two extra machines only for local singleplayer"; an interpretation): the allied pair comes only to a solo campaign, whose server's human cap is one.</summary>
public class WarfrontAlliedPairTests
{
    [Theory]
    [InlineData(1, true, true)]
    [InlineData(1, false, false)]   // the HQ defence and the fortress: none even solo (owner 2026-11-17)
    [InlineData(2, true, false)]
    [InlineData(3, true, false)]
    [InlineData(4, true, false)]
    [InlineData(2, false, false)]
    public void The_pair_is_for_a_solo_lane_battle_only(int cap, bool lane, bool pair) => Assert.Equal(pair, World.WarfrontAlliedPair(cap, lane));

    [Fact]
    public void A_pair_run_spawns_the_two_marines_and_a_run_without_it_none()
    {
        var solo = WarfrontHqBattleTests.Hq(out _, pair: true);
        Assert.Equal(2, solo.Players.Keys.Count(TfwContracts.IsPuppet));
        var coop = WarfrontHqBattleTests.Hq(out _);
        Assert.Equal(0, coop.Players.Keys.Count(TfwContracts.IsPuppet));
    }
}
