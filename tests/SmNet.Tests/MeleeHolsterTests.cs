using SmContent;
using SmSim;
using Xunit;

namespace SmNet.Tests;

/// <summary>fix/versatility-knife-stow: which primary goes to the left thigh during a melee strike (owner, 2026-11-07).</summary>
public class MeleeHolsterTests
{
    [Theory]
    [InlineData(HandRole.Primary, WeaponSlot.Ranged, true, true, false, true)]    // the primary selected: thigh
    [InlineData(HandRole.Second, WeaponSlot.Ranged, true, true, false, false)]    // the other one hangs on the back
    [InlineData(HandRole.Second, WeaponSlot.Second, true, true, false, true)]     // the second selected: thigh
    [InlineData(HandRole.Primary, WeaponSlot.Second, true, true, false, false)]
    [InlineData(HandRole.Primary, WeaponSlot.Ranged, false, true, false, false)]  // no Weapon Versatility: unchanged
    [InlineData(HandRole.Primary, WeaponSlot.Ranged, true, false, false, false)]  // no strike shown
    [InlineData(HandRole.Primary, WeaponSlot.Ranged, true, true, true, false)]    // both guns already away (switch gap, stim)
    [InlineData(HandRole.Primary, WeaponSlot.Pistol, true, true, false, false)]   // pistol selected: both primaries are away anyway
    [InlineData(HandRole.Pistol, WeaponSlot.Pistol, true, true, false, false)]
    [InlineData(HandRole.Melee, WeaponSlot.Ranged, true, true, false, false)]
    public void Thigh_only_for_the_selected_primary_of_a_versatility_tactical_during_a_strike(HandRole role, WeaponSlot shown, bool hasSecond, bool meleeOut, bool away, bool expected) =>
        Assert.Equal(expected, MeleeHolster.OnThigh(role, shown, hasSecond, meleeOut, away));
}
