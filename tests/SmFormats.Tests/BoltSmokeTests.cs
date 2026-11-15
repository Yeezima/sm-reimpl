using SmContent;

namespace SmFormats.Tests;

/// <summary>fx/bolt-smoke (owner 2026-11-15): which wargear files the no-smoke rule covers, and that the autocannon is not one of them (it has its own rule).</summary>
public class BoltSmokeTests
{
    [Theory]
    [InlineData("sm_mp_ranged_bolter", true)]
    [InlineData("csm_mp_ranged_bolter_gold", true)]
    [InlineData("sm_mp_pistol_bolt_pistol", true)]
    [InlineData("sm_mp_ranged_storm_bolter", true)]
    [InlineData("csm_mp_exotic_heavy_bolter", true)]
    [InlineData("sm_mp_ranged_stalker_bolt_rifle", true)]
    [InlineData("sm_mp_exotic_sniper_rifle", true)]
    [InlineData("sm_mp_exotic_auto_cannon", false)]
    [InlineData("csm_mp_exotic_auto_cannon", false)]
    [InlineData("sm_mp_ranged_plasma_gun", false)]
    [InlineData("sm_mp_ranged_melta", false)]
    [InlineData("sm_mp_eq_frag_grenade", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_rule_covers_the_bolt_weapons_only(string? wargear, bool covered) => Assert.Equal(covered, BoltSmoke.IsBoltWeapon(wargear));

    [Fact]
    public void A_covered_weapon_draws_no_alpha_sprites_unless_restored()
    {
        Assert.Equal(0f, BoltSmoke.ShareFor("sm_mp_ranged_bolter", old: false));
        Assert.Equal(1f, BoltSmoke.ShareFor("sm_mp_ranged_bolter", old: true));
        Assert.Equal(1f, BoltSmoke.ShareFor("sm_mp_ranged_plasma_gun", old: false));
    }
}
