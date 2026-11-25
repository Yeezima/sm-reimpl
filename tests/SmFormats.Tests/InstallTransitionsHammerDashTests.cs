using SmContent;

namespace SmFormats.Tests;

/// <summary>
/// Install transitions, pass 2 batch 2 (2026-11-25): the jump-pack dash's clip family (<see cref="DashClipChoice"/>). A hammer or daemon maul marine shows the hammer graph's
/// <c>SM_JMP_TH_Dash_*</c> flight and <c>SM_JMP_TH_Dash_Landing_*</c> landing for the plain clips the simulation reads; names only.
/// </summary>
public class InstallTransitionsHammerDashTests
{
    static bool All(string _) => true;

    [Theory]
    [InlineData("sm_mp_melee_2hand_thunder_hammer", DashFamily.Hammer)]
    [InlineData("csm_mp_melee_2hand_power_mace", DashFamily.Hammer)]
    [InlineData("sm_mp_melee_chainsword", DashFamily.Plain)]
    [InlineData("sm_mp_melee_power_sword", DashFamily.Plain)]
    [InlineData("sm_mp_melee_combat_knife", DashFamily.Plain)]
    public void The_wargear_picks_the_dash_family(string wargear, DashFamily expected) =>
        Assert.Equal(expected, DashClipChoice.Family(wargear, extraTransitionsOff: false));

    [Fact]
    public void The_switch_or_no_wargear_gives_the_plain_dash()
    {
        Assert.Equal(DashFamily.Plain, DashClipChoice.Family("sm_mp_melee_2hand_thunder_hammer", extraTransitionsOff: true));
        Assert.Equal(DashFamily.Plain, DashClipChoice.Family(null, extraTransitionsOff: false));
    }

    [Theory]
    [InlineData("SM_JMP_Dash_F", "SM_JMP_TH_Dash_F")]
    [InlineData("SM_JMP_Dash_B", "SM_JMP_TH_Dash_B")]
    [InlineData("SM_JMP_Dash_L", "SM_JMP_TH_Dash_L")]
    [InlineData("SM_JMP_Dash_R", "SM_JMP_TH_Dash_R")]
    [InlineData("sm_jmp_dash_r", "SM_JMP_TH_Dash_R")]
    [InlineData("SM_JMP_Dash_Landing_Shallow_01", "SM_JMP_TH_Dash_Landing_F")]
    [InlineData("SM_JMP_Dash_Landing_Shallow_F", "SM_JMP_TH_Dash_Landing_F")]
    [InlineData("SM_JMP_Dash_Landing_Shallow_B", "SM_JMP_TH_Dash_Landing_B")]
    [InlineData("SM_JMP_Dash_Landing_Shallow_L", "SM_JMP_TH_Dash_Landing_L")]
    [InlineData("SM_JMP_Dash_Landing_Shallow_R", "SM_JMP_TH_Dash_Landing_R")]
    public void Each_plain_dash_clip_maps_to_the_hammers(string plain, string hammer)
    {
        Assert.Equal(hammer, DashClipChoice.HammerClip(plain));
        Assert.Equal(hammer, DashClipChoice.Clip(plain, DashFamily.Hammer, All));
    }

    [Theory]
    [InlineData("SM_EVD_R2R_Roll_F_01")]
    [InlineData("SM_EVD_TB_Evade_L_01")]
    [InlineData("SM_JMP_DashAtk_F")]
    [InlineData("SM_JMP_Dash_Landing_Shallow_X")]
    [InlineData("SM_JMP_Dash_FF")]
    [InlineData("BOY_2HR_NAV_Evade4m_L_01")]
    public void Other_clips_are_not_dash_clips(string clip)
    {
        Assert.Null(DashClipChoice.HammerClip(clip));
        Assert.Equal(clip, DashClipChoice.Clip(clip, DashFamily.Hammer, All));
    }

    [Fact]
    public void The_plain_family_or_a_missing_hammer_clip_keeps_the_plain_clip()
    {
        Assert.Equal("SM_JMP_Dash_F", DashClipChoice.Clip("SM_JMP_Dash_F", DashFamily.Plain, All));
        Assert.Equal("SM_JMP_Dash_F", DashClipChoice.Clip("SM_JMP_Dash_F", DashFamily.Hammer, _ => false));   // the rig lacks the hammer's clip
        Assert.Equal("SM_JMP_Dash_Landing_Shallow_01", DashClipChoice.Clip("SM_JMP_Dash_Landing_Shallow_01", DashFamily.Hammer, c => c != "SM_JMP_TH_Dash_Landing_F"));
    }

    [Fact]
    public void The_mapped_names_are_cached_and_stable()
    {
        string first = DashClipChoice.HammerClip("SM_JMP_Dash_L")!;
        Assert.Same(first, DashClipChoice.HammerClip("SM_JMP_Dash_L"));
        Assert.Null(DashClipChoice.HammerClip("SM_EVD_R2R_Roll_L_01"));
        Assert.Null(DashClipChoice.HammerClip("SM_EVD_R2R_Roll_L_01"));   // a cached miss stays a miss
    }

    const string Hammer = "sm_mp_melee_2hand_thunder_hammer";

    [Fact]
    public void The_stance_is_the_one_before_the_dash_not_the_lowered_weapon_during_it()
    {
        var latch = new DashFamilyLatch();
        latch.Ground(1, Hammer);   // hammer stance on the last frame before the dash
        Assert.Equal(DashFamily.Hammer, latch.During(1, fallbackAlias: null, false, out bool now));   // during the dash the stance reads differently: ignored
        Assert.True(now);
        latch.Ground(2, null);     // pistol up before the dash
        Assert.Equal(DashFamily.Plain, latch.During(2, fallbackAlias: Hammer, false, out _));   // the lowered weapon in flight would read as the hammer stance: ignored
    }

    [Fact]
    public void The_latch_holds_through_the_flight_and_the_landing()
    {
        var latch = new DashFamilyLatch();
        latch.Ground(1, Hammer);
        Assert.Equal(DashFamily.Hammer, latch.During(1, null, false, out bool first));
        Assert.True(first);
        // The landing (the simulation restarts its tick count) and a chained dash are further During frames: no re-read, even with a different fallback.
        for (int frame = 0; frame < 5; frame++)
        {
            Assert.Equal(DashFamily.Hammer, latch.During(1, null, false, out bool again));
            Assert.False(again);
        }
        Assert.True(latch.IsLatched(1));
    }

    [Fact]
    public void The_latch_clears_when_the_evade_ends_and_when_the_player_leaves()
    {
        var latch = new DashFamilyLatch();
        latch.Ground(1, Hammer);
        latch.During(1, null, false, out _);
        latch.Ground(1, null);   // the evade ended and the pistol is up now
        Assert.False(latch.IsLatched(1));
        Assert.Equal(DashFamily.Plain, latch.During(1, Hammer, false, out bool relatched));
        Assert.True(relatched);

        latch.Forget(1);   // left: a reused id starts from nothing
        Assert.False(latch.IsLatched(1));
        Assert.Equal(DashFamily.Hammer, latch.During(1, Hammer, false, out _));   // no stance seen: the fallback (first seen mid-dash)
    }

    [Fact]
    public void The_switch_gives_the_plain_family_whatever_the_stance()
    {
        var latch = new DashFamilyLatch();
        latch.Ground(1, Hammer);
        Assert.Equal(DashFamily.Plain, latch.During(1, Hammer, extraTransitionsOff: true, out _));
    }
}
