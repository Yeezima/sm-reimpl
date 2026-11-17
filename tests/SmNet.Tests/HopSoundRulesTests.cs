using SmContent;

namespace SmNet.Tests;

/// <summary>A Space tap of an Assault Marine or a Raptor plays one jump-pack hop sound (<see cref="HopSoundRules"/>). Engine-free.</summary>
public class HopSoundRulesTests
{
    [Theory]
    [InlineData("fx_evade_start", true)]
    [InlineData("FX_Evade_Start", true)]
    [InlineData("actn_dash_takeoff", true)]
    [InlineData("fx_jump_evade", false)]
    [InlineData("evt_left_foot", false)]
    public void The_dash_extras_are_the_evade_start_and_the_take_off(string tag, bool extra) =>
        Assert.Equal(extra, HopSoundRules.IsDashExtraTag(tag));

    [Fact]
    public void A_dash_right_after_a_launch_sound_is_the_same_hop_and_posts_nothing()
    {
        Assert.False(HopSoundRules.DashPostsLaunch(4.610, 4.509));   // the tap's launch, 0.1 s before the dash
        Assert.False(HopSoundRules.DashPostsLaunch(10.0, 10.0 - HopSoundRules.SameHopSeconds + 0.01));
    }

    [Fact]
    public void A_dash_seen_with_no_launch_sound_posts_its_own_once()
    {
        Assert.True(HopSoundRules.DashPostsLaunch(4.610, double.NegativeInfinity));
        Assert.True(HopSoundRules.DashPostsLaunch(10.0, 3.0));
    }

    [Fact]
    public void The_boundary_is_exactly_the_window()
    {
        Assert.False(HopSoundRules.DashPostsLaunch(HopSoundRules.SameHopSeconds, 0.0));   // exactly 0.6 s: still the same hop
        Assert.True(HopSoundRules.DashPostsLaunch(HopSoundRules.SameHopSeconds + 0.001, 0.0));
    }

    [Fact]
    public void Only_jump_pack_classes_have_a_dash_evade()
    {
        var c = MeleeCancelTests.Pack;
        for (int k = 0; k < c.Classes.Count; k++)
            if (SmSim.Loadouts.Evade(c, k) is { Dash: true })
                Assert.NotNull(SmSim.Loadouts.JumpPack(c, k));   // the sound rule treats a Dash evade as a jump-pack hop
    }
}
