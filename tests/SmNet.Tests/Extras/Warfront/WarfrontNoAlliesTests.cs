using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmNet.Tests.Extras.Warfront.Fortress;
using SmNet.Tests.Extras.Warfront.Hq;

namespace SmNet.Tests.Extras.Warfront;

/// <summary>W13c pass 2 (designer review of W13c, ruling b; ours until the owner confirms): Musou's allied AI Marine pair is off on every Warfront map (a lane, the HQ, the fortress); Musou's own default stays on (its pins are unchanged).</summary>
public class WarfrontNoAlliesTests
{
    static int Puppets(World w) => w.Players.Keys.Count(TfwContracts.IsPuppet);

    [Fact]
    public void A_lane_battle_lands_with_no_allied_pair()
    {
        var w = WarfrontBattleTests.War();
        Assert.False(w.Tfw!.Allies);
        TfwRig.Deploy(w);
        Assert.Equal(0, Puppets(w));
    }

    [Fact]
    public void The_hq_defence_lands_with_no_allied_pair()
    {
        var w = WarfrontHqBattleTests.Hq(out _);
        Assert.False(w.Tfw!.Allies);
        Assert.Equal(0, Puppets(w));
    }

    [Fact]
    public void The_fortress_lands_with_no_allied_pair()
    {
        var (w, _, _) = WarfrontFortressBattleTests.Fortress();
        Assert.False(w.Tfw!.Allies);
        Assert.Equal(0, Puppets(w));
    }

    [Fact]
    public void Musous_own_run_keeps_the_pair_on()
    {
        var w = TfwRig.War();
        Assert.True(w.Tfw!.Allies);
    }
}
