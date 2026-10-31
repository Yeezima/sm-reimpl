using SmContent;

namespace SmFormats.Tests;

/// <summary>
/// anim/relic-bolter-idle: the client's animation families (<see cref="HipFireAnimations.AnimationFamilyOf"/>, <see cref="ReloadAnimations.AnimationFamilyOf"/>, behind
/// <c>NetworkView.AnimationFamilyOf</c> and <c>ReloadAnimationFamilyOf</c>): the True Grit pistol-slot bolters play the bolt pistol's set; every other weapon keeps its name rule.
/// </summary>
public class AnimationFamilyTests
{
    [Theory]
    [InlineData("sm_mp_pistol_bolter")]
    [InlineData("sm_mp_pistol_bolter_gold")]
    [InlineData("csm_mp_pistol_bolter")]
    [InlineData("csm_mp_pistol_bolter_gold")]
    public void The_true_grit_bolters_play_the_bolt_pistols_set(string name)
    {
        Assert.Equal(HipFireWeaponClass.Rifle, HipFireAnimations.FamilyOf(name));   // the name rule alone (the before)
        Assert.Equal(HipFireWeaponClass.Pistol, HipFireAnimations.AnimationFamilyOf(name));
        Assert.Equal(ReloadFamily.Bolter, ReloadAnimations.FamilyOf(name));
        Assert.Equal(ReloadFamily.BoltPistol, ReloadAnimations.AnimationFamilyOf(name));
    }

    [Theory]
    [InlineData("sm_mp_ranged_bolter", HipFireWeaponClass.Rifle, ReloadFamily.Bolter)]
    [InlineData("csm_mp_ranged_bolter_gold", HipFireWeaponClass.Rifle, ReloadFamily.Bolter)]
    [InlineData("sm_mp_pistol_bolt_pistol", HipFireWeaponClass.Pistol, ReloadFamily.BoltPistol)]
    [InlineData("csm_mp_pistol_bolt_pistol_devastator", HipFireWeaponClass.Pistol, ReloadFamily.BoltPistol)]
    [InlineData("sm_mp_pistol_plasma_pistol", HipFireWeaponClass.Pistol, ReloadFamily.PlasmaPistol)]
    [InlineData("sm_mp_ranged_storm_bolter", HipFireWeaponClass.Rifle, ReloadFamily.StormBolter)]
    [InlineData("sm_mp_exotic_heavy_bolter", HipFireWeaponClass.Heavy, null)]
    public void Every_other_weapon_keeps_its_name_rule(string name, HipFireWeaponClass family, ReloadFamily? reload)
    {
        Assert.Equal(family, HipFireAnimations.AnimationFamilyOf(name));
        Assert.Equal(HipFireAnimations.FamilyOf(name), HipFireAnimations.AnimationFamilyOf(name));
        Assert.Equal(reload, ReloadAnimations.AnimationFamilyOf(name));
        Assert.Equal(ReloadAnimations.FamilyOf(name), ReloadAnimations.AnimationFamilyOf(name));
    }
}
