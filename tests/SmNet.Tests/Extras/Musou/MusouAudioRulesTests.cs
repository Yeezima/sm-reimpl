using SmContent;
using SmSim;
using Xunit;

namespace SmNet.Tests.Extras.Musou;

/// <summary>musou/audio-pass (owner, 2026-11-09): the gate that keeps Annihilation and Seize Ground's sound as it was, the bolter family, the far Orks.</summary>
public class MusouAudioRulesTests
{
    [Theory]
    [InlineData(ContentExtras.None)]
    [InlineData(ContentExtras.Orks)]
    [InlineData(ContentExtras.Gore)]
    [InlineData(ContentExtras.Orks | ContentExtras.Gore)]
    public void Classic_PvP_runs_none_of_the_pass(ContentExtras extras) => Assert.False(MusouAudioRules.Applies(extras));

    [Fact]
    public void Musou_runs_the_pass() => Assert.True(MusouAudioRules.Applies(ContentExtras.Musou));

    [Theory]
    [InlineData("sm_mp_ranged_bolter")]
    [InlineData("csm_mp_ranged_bolter")]
    [InlineData("sm_mp_ranged_storm_bolter")]
    [InlineData("csm_mp_ranged_storm_bolter")]
    [InlineData("sm_mp_ranged_sniper_rifle")]
    [InlineData("csm_mp_ranged_sniper_rifle")]
    [InlineData("sm_mp_exotic_heavy_bolter")]
    [InlineData("csm_mp_exotic_heavy_bolter")]
    public void The_bolter_rifles_are_found(string wargear) => Assert.True(MusouAudioRules.IsBolterRifle(wargear));

    [Theory]
    [InlineData("sm_mp_ranged_bolt_pistol")]
    [InlineData("sm_mp_ranged_plasma_gun")]
    [InlineData("sm_mp_ranged_melta")]
    [InlineData("sm_mp_ranged_lascannon")]
    [InlineData("sm_mp_mel_chainsword")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_weapons_and_pistols_are_not(string? wargear) => Assert.False(MusouAudioRules.IsBolterRifle(wargear));

    [Fact]
    public void The_far_Orks_are_far_quiet_and_dull_and_the_rest_of_the_battle_is_as_before()
    {
        var ork = MusouAmbienceRules.SpecOf(MusouAmbienceKind.OrkMob);
        Assert.True(MusouAudioRules.IsFarOrk(ork.Kind));
        Assert.True(ork.Near >= 55f && ork.Far >= 120f);
        Assert.True(ork.GainDb <= -6f);
        Assert.True(ork.Lpf >= 35f);
        var bolter = MusouAmbienceRules.SpecOf(MusouAmbienceKind.Bolter);
        Assert.Equal((40f, 100f, -4f, 10f), (bolter.Near, bolter.Far, bolter.GainDb, bolter.Lpf));
    }

    [Fact]
    public void The_bolter_hit_is_a_smack_then_an_explosion()
    {
        Assert.Equal("play_bolter_smack", MusouAudioRules.SmackEvent);
        Assert.True(MusouAudioRules.BlastDelaySeconds > 0f);
        Assert.True(MusouAudioRules.DeepPitchCents < 0f);
    }
    [Fact]
    public void The_power_sword_is_found_by_wargear_and_louder()
    {
        Assert.True(MusouAudioRules.IsPowerSword("sm_mp_melee_powersword"));
        Assert.True(MusouAudioRules.IsPowerSword("csm_mp_melee_powersword"));
        Assert.False(MusouAudioRules.IsPowerSword("sm_mp_melee_chainsword"));
        Assert.False(MusouAudioRules.IsPowerSword(null));
        Assert.Equal(6f, MusouAudioRules.PowerSwordHitGainDb);
    }

    [Fact]
    public void The_hit_pair_is_half_the_first_passes_level_and_the_own_gun_is_heavier_than_others()
    {
        Assert.Equal(2f, MusouAudioRules.SmackGainDb);     // was +8: 6 dB = half amplitude (owner follow-up 2026-11-09)
        Assert.Equal(-15f, MusouAudioRules.BlastGainDb);   // was -9
        Assert.True(MusouAudioRules.OwnDeepGainDb > MusouAudioRules.DeepGainDb);
        Assert.True(MusouAudioRules.OwnThumpGainDb > MusouAudioRules.ThumpGainDb);
        Assert.True(MusouAudioRules.OwnSubPitchCents < MusouAudioRules.DeepPitchCents);
    }
}
