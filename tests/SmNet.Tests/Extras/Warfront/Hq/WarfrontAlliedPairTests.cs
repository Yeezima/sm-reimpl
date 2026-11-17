using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Warfront.Hq;

/// <summary>W13d part 3 (owner 2026-11-17: "Two extra machines only for local singleplayer"; an interpretation): the allied pair comes only to a solo campaign, whose server's human cap is one.</summary>
public class WarfrontAlliedPairTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void The_pair_is_for_a_human_cap_of_one(int cap, bool pair) => Assert.Equal(pair, World.WarfrontAlliedPair(cap));

    [Fact]
    public void A_solo_hq_landing_spawns_the_two_marines_and_a_co_op_one_none()
    {
        var solo = WarfrontHqBattleTests.Hq(out _, pair: true);
        Assert.Equal(2, solo.Players.Keys.Count(TfwContracts.IsPuppet));
        var coop = WarfrontHqBattleTests.Hq(out _);
        Assert.Equal(0, coop.Players.Keys.Count(TfwContracts.IsPuppet));
    }
}
