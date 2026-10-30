using SmContent;

namespace SmFormats.Tests;

/// <summary>fx/hit-armour-sound: which layers of a weapon's hit event a player hit plays (<see cref="HitSoundRules"/>).</summary>
public class HitSoundRulesTests
{
    [Fact]
    public void Armour_up_plays_the_armour_case_only() => Assert.Equal(HitLayers.Armour, HitSoundRules.Of(true, combined: false));

    [Fact]
    public void Armour_down_plays_the_flesh_case_only() => Assert.Equal(HitLayers.Flesh, HitSoundRules.Of(false, combined: false));

    [Fact]
    public void Unknown_armour_plays_both_layers_as_before() => Assert.Equal(HitLayers.Both, HitSoundRules.Of(null, combined: false));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void The_combined_switch_restores_both_layers(bool? armoured) => Assert.Equal(HitLayers.Both, HitSoundRules.Of(armoured, combined: true));
}
