using SmClient.ImpactRules;

namespace SmNet.Tests;

/// <summary>
/// fx/weapon-visuals (owner, 2026-11-14; client presentation, engine-free rules in client/ImpactRules/PowerWeaponGlow.cs): which melee weapon gets which light and field, and which bolter shot draws the
/// Kraken tracer. Weapon names only; no original data.
/// </summary>
public class WeaponVisualsTests
{
    [Theory]
    [InlineData("sm_mp_melee_2hand_thunder_hammer")]
    [InlineData("sm_mp_melee_powersword")]
    [InlineData("csm_mp_melee_powersword")]
    [InlineData("sm_mp_melee_power_axe")]
    [InlineData("csm_mp_melee_2hand_power_mace")]
    public void Every_power_weapon_has_the_blue_light_with_or_without_the_perk(string wargear)
    {
        Assert.Equal(PowerGlowColour.Blue, PowerWeaponGlow.ColourFor(wargear, serrated: false));
        Assert.Equal(PowerGlowColour.Blue, PowerWeaponGlow.ColourFor(wargear, serrated: true));
    }

    [Fact]
    public void Only_the_hammer_has_its_light_from_the_data()
    {
        Assert.True(PowerWeaponGlow.DataBindsLight("sm_mp_melee_2hand_thunder_hammer"));
        foreach (var w in new[] { "sm_mp_melee_powersword", "csm_mp_melee_powersword", "sm_mp_melee_power_axe", "csm_mp_melee_2hand_power_mace", "sm_mp_melee_combat_knife" })
            Assert.False(PowerWeaponGlow.DataBindsLight(w));
    }

    [Theory]
    [InlineData("sm_mp_melee_combat_knife")]
    [InlineData("csm_mp_melee_combat_knife")]
    public void The_combat_knife_glows_green_only_with_the_serrated_perk(string wargear)
    {
        Assert.Equal(PowerGlowColour.Green, PowerWeaponGlow.ColourFor(wargear, serrated: true));
        Assert.Equal(PowerGlowColour.None, PowerWeaponGlow.ColourFor(wargear, serrated: false));
        Assert.True(PowerWeaponGlow.KnifeField(wargear, true));
        Assert.False(PowerWeaponGlow.KnifeField(wargear, false));
    }

    [Theory]
    [InlineData("sm_mp_melee_chainsword")]
    [InlineData("csm_mp_melee_chainsword")]
    [InlineData("sm_mp_melee_chainaxe")]
    [InlineData("ork_choppa")]
    [InlineData(null)]
    public void Other_melee_weapons_get_nothing(string? wargear)
    {
        Assert.Equal(PowerGlowColour.None, PowerWeaponGlow.ColourFor(wargear, serrated: true));
        Assert.False(PowerWeaponGlow.KnifeField(wargear, true));
    }

    [Fact]
    public void The_light_copies_the_hammers_data_and_the_knifes_is_green()
    {
        Assert.Equal((3f / 255f, 113f / 255f, 221f / 255f), PowerWeaponGlow.Rgb(PowerGlowColour.Blue));
        Assert.Equal(1f, PowerWeaponGlow.LightRange);
        Assert.Equal(1f, PowerWeaponGlow.LightIntensity);
        var (r, g, b) = PowerWeaponGlow.Rgb(PowerGlowColour.Green);
        Assert.True(g > r * 3f && g > b * 3f);
        Assert.Equal("RIG_Prop_RHand", PowerWeaponGlow.LightMarkers[^1]);   // the grip locator every model has is the last resort
    }

    [Theory]
    [InlineData("sm_mp_ranged_bolter", true)]
    [InlineData("sm_mp_ranged_bolter_gold", true)]
    [InlineData("sm_mp_pistol_bolter", true)]
    [InlineData("csm_mp_ranged_bolter", true)]
    [InlineData("csm_mp_ranged_bolter_gold", true)]
    [InlineData("csm_mp_pistol_bolter", true)]
    [InlineData("csm_mp_pistol_bolter_gold", true)]
    [InlineData("sm_mp_ranged_heavy_bolter", false)]
    [InlineData("sm_mp_ranged_storm_bolter", false)]
    [InlineData("sm_mp_ranged_stalker_bolter", false)]
    [InlineData("sm_mp_ranged_plasma_gun", false)]
    [InlineData(null, false)]
    public void Only_the_bolters_that_take_kraken_can_draw_its_tracer(string? wargear, bool expected)
    {
        Assert.Equal(expected, KrakenTracer.CanTake(wargear));
        Assert.Equal(expected, KrakenTracer.Applies(wargear, shooterHasPerk: true));
        Assert.False(KrakenTracer.Applies(wargear, shooterHasPerk: false));
    }

    [Fact]
    public void The_kraken_tracer_is_red_and_larger_than_the_plain_bolt_tracer()
    {
        var (r, g, b) = KrakenTracer.Red;
        Assert.True(r > 0.9f && g < 0.2f && b < 0.2f);
        Assert.True(KrakenTracer.WidthScale > 2f * 0.28f);   // the plain bolt tracer keeps 0.28 of the data's width and 0.22 of its trail
        Assert.True(KrakenTracer.TrailScale > 1.5f * 0.22f);
    }
}
