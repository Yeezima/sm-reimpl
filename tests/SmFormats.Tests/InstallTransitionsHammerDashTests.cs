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
    public void The_names_are_the_hammer_graphs()
    {
        Assert.Equal("MP_SM_MEL_ThunderHammer", EvadeTuning.HammerGraph);
        Assert.StartsWith(EvadeTuning.HammerDashClipPrefix, EvadeTuning.HammerDashLandingPrefix);
    }
}
