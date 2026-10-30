using SmClient.ImpactRules;
using SmContent;
using SmSim;

namespace SmNet.Tests;

/// <summary>fx/character-visuals: the armour flash rules, the sword swing flare, and which primary hangs on the back with Weapon Versatility.</summary>
public class CharacterVisualsTests
{
    [Fact]
    public void An_armoured_hit_flashes_yellow_and_the_first_unarmoured_hit_after_it_flashes_red()
    {
        var t = new ArmourFlashTracker();
        t.Hit(7, 10.0, armourUp: true, regenDelay: 5f);
        var a = t.At(7, 10.05);
        Assert.True(a.Hit > 0.5f); Assert.Equal(0f, a.Break); Assert.Equal(0f, a.Glow);
        Assert.Equal(0f, t.At(7, 10.0 + ArmourFlashTracker.HitSeconds + 0.01).Hit);
        t.Hit(7, 11.0, armourUp: false, regenDelay: 5f);
        Assert.True(t.At(7, 11.05).Break > 0.5f);
        t.Hit(7, 11.2, armourUp: false, regenDelay: 5f);   // already down: no second break flash
        Assert.Equal(0f, t.At(7, 11.45).Break);
    }

    [Fact]
    public void The_recharge_glow_starts_one_regeneration_delay_after_the_last_damage_seen_and_only_once()
    {
        var t = new ArmourFlashTracker();
        t.Hit(3, 20.0, true, 4f);
        Assert.Equal(0f, t.At(3, 23.9).Glow);
        Assert.True(t.At(3, 24.2).Glow > 0.3f);
        Assert.Equal(0f, t.At(3, 24.0 + ArmourFlashTracker.GlowSeconds + 0.1).Glow);
        Assert.Equal(0f, t.At(3, 40.0).Glow);   // not again without new damage
        t.Hit(3, 30.0, true, 4f); t.Reset(3);
        Assert.Equal((0f, 0f, 0f), t.At(3, 34.2));
    }

    [Fact]
    public void The_own_shield_gives_exact_flashes()
    {
        var t = new ArmourFlashTracker();
        t.OwnShield(1, 0.0, 100f, 100f, true);
        t.OwnShield(1, 1.0, 80f, 100f, true);
        Assert.True(t.At(1, 1.05).Hit > 0.5f);
        t.OwnShield(1, 2.0, 0f, 100f, true);
        Assert.True(t.At(1, 2.05).Break > 0.5f);
        t.OwnShield(1, 7.0, 2f, 100f, true);   // rising: the glow starts
        Assert.True(t.At(1, 7.1).Glow > 0.3f);
        double glowAt = 7.0;
        for (double now = 7.03; now < 7.6; now += 0.03) t.OwnShield(1, now, 2f + (float)(now - 7.0) * 20f, 100f, true);
        Assert.Equal(0f, t.At(1, glowAt + ArmourFlashTracker.GlowSeconds + 0.05).Glow);   // a continuing rise did not start it again
    }

    [Fact]
    public void The_sword_flare_rises_while_swinging_and_falls_after_and_stays_below_a_hits_full_flare()
    {
        float f = 0f;
        for (int i = 0; i < 30; i++) f = SwordSwing.Next(f, true, 1f / 60f);
        Assert.Equal(SwordSwing.Level, f, 3);
        Assert.True(SwordSwing.Level < 1f);
        for (int i = 0; i < 60; i++) f = SwordSwing.Next(f, false, 1f / 60f);
        Assert.Equal(0f, f);
    }

    [Theory]
    [InlineData(WeaponSlot.Ranged, WeaponSlot.Ranged, false, HandRole.Second)]    // primary in hand: the second is on the back
    [InlineData(WeaponSlot.Second, WeaponSlot.Second, false, HandRole.Primary)]   // second in hand: the primary
    [InlineData(WeaponSlot.Pistol, WeaponSlot.Second, false, HandRole.Second)]    // pistol out, the second was swapped out last
    [InlineData(WeaponSlot.Pistol, WeaponSlot.Ranged, false, HandRole.Primary)]
    [InlineData(WeaponSlot.Ranged, WeaponSlot.Ranged, true, HandRole.Primary)]    // between release and take the old weapon goes to the back
    public void The_primary_swapped_out_last_hangs_on_the_back(WeaponSlot shown, WeaponSlot last, bool between, HandRole expected) =>
        Assert.Equal(expected, BackWeapon.Owner(shown, hasSecond: true, last, between));

    [Fact]
    public void Without_a_second_primary_the_primary_is_the_one_on_the_back_as_before() =>
        Assert.Equal(HandRole.Primary, BackWeapon.Owner(WeaponSlot.Pistol, false, WeaponSlot.Ranged, false));

    [Fact]
    public void The_second_primary_is_in_hand_only_in_its_slot() =>
        Assert.True(WeaponHands.InHand(HandRole.Second, WeaponSlot.Second, false, false, true, null, null)
                    && !WeaponHands.InHand(HandRole.Second, WeaponSlot.Ranged, false, false, true, null, null)
                    && !WeaponHands.InHand(HandRole.Primary, WeaponSlot.Second, false, false, true, null, null));
}
