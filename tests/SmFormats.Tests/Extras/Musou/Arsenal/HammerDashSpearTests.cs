using SmContent;
using SmSim.Extras.Musou.Arsenal;

namespace SmFormats.Tests;

/// <summary>Install transitions, pass 2 batch 2: Musou's power spear dashes as the hammer through its animation alias (added content; as its falls and landings, batch 1).</summary>
public class HammerDashSpearTests
{
    [Fact]
    public void The_spear_takes_the_hammers_dash_through_its_alias()
    {
        string alias = AnimationAlias.Of(ArsenalRules.PowerSpear);
        Assert.Equal(DashFamily.Hammer, DashClipChoice.Family(alias, extraTransitionsOff: false));
        var latch = new DashFamilyLatch();
        latch.Ground(7, alias);
        Assert.Equal(DashFamily.Hammer, latch.During(7, null, false, out _));
    }
}
