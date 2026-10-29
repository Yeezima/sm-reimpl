using SmContent;
using Xunit;

namespace SmFormats.Tests;

/// <summary>M44: the HUD's engine-free rules (weapon pictures, clip ticks, card ammo) and the sprite table. No install bytes.</summary>
public class HudArtTests
{
    static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase)
    {
        "bolter", "bolter_chaos", "bolt_pistol", "bolt_pistol_chaos", "lascannon", "lascannon_chaos", "melta_gun", "meltagun_chaos",
        "plasma_gun", "plasma_gun_chaos", "stalker_bolter", "vengance_cannon", "frag_grenade", "krak_grenade", "jumppack", "golden_bolter",
    };

    [Theory]
    [InlineData("sm_mp_ranged_bolter", "bolter", "bolter")]
    [InlineData("csm_mp_ranged_bolter", "bolter", "bolter_chaos")]
    [InlineData("sm_mp_ranged_bolter_gold", "bolter", "golden_bolter")]
    [InlineData("sm_mp_exotic_las_cannon", "laz_cannon", "lascannon")]
    [InlineData("csm_mp_exotic_las_cannon", "laz_cannon", "lascannon_chaos")]
    [InlineData("csm_mp_ranged_melta", "melta", "meltagun_chaos")]
    [InlineData("sm_mp_ranged_grenade_launcher", "grenade_launcher", "vengance_cannon")]
    [InlineData("csm_mp_ranged_sniper_rifle", "stalker", "stalker_bolter")]
    [InlineData("sm_mp_eq_krak_grenade", "krakgrenades", "krak_grenade")]
    [InlineData("sm_mp_abil_jumppack", "jumppack", "jumppack")]
    public void Picture_follows_the_weapon_type_and_the_side(string wargear, string type, string expected) =>
        Assert.Equal(expected, HudRules.IconFor(wargear, type, Pictures.Contains));

    [Fact]
    public void Picture_without_data_falls_back_to_the_name_and_unknown_types_have_none()
    {
        Assert.Equal("bolter", HudRules.IconFor("default", null, Pictures.Contains));
        Assert.Equal("plasma_gun", HudRules.IconFor("sm_mp_ranged_plasma_gun", "", Pictures.Contains));
        Assert.Null(HudRules.IconFor("sm_mp_exotic_heavy_bolter", "heavy_bolter", Pictures.Contains));   // not in this test set
        Assert.Null(HudRules.IconFor("sm_mp_something", "bullrush", Pictures.Contains));
    }

    [Theory]
    [InlineData(6, 12, 12, 6)]
    [InlineData(0, 4, 4, 0)]
    [InlineData(30, 30, 22, 22)]
    [InlineData(1, 30, 22, 1)]
    [InlineData(15, 30, 22, 11)]
    [InlineData(5, 0, 0, 0)]
    [InlineData(40, 12, 12, 12)]
    public void Ticks_are_one_per_round_up_to_the_row_length(int clip, int clipMax, int ticks, int loaded) =>
        Assert.Equal((ticks, loaded), HudRules.Ticks(clip, clipMax));

    [Theory]
    [InlineData(6, 12, 68, "06/68")]
    [InlineData(4, 4, 23, "4/23")]
    [InlineData(0, 30, 0, "00/0")]
    [InlineData(5, 0, 10, "5/10")]
    public void Card_ammo_pads_the_clip_to_the_clip_size_digits(int clip, int clipMax, int reserve, string text) =>
        Assert.Equal(text, HudRules.CardAmmo(clip, clipMax, reserve));

    [Fact]
    public void Every_sprite_lies_inside_its_texture()
    {
        Assert.NotEmpty(HudSprites.All);
        foreach (var s in HudSprites.All) Assert.True(s.InsideTexture, $"{s.Texture} {s.X},{s.Y} {s.Width}x{s.Height}");
    }

    [Fact]
    public void Empty_art_answers_nothing()
    {
        Assert.Null(UiArt.Empty.Texture("hud_i203"));
        Assert.Null(UiArt.Empty.WeaponName("sm_mp_ranged_bolter"));
        Assert.Equal("bolter", new UiArt(new Dictionary<string, byte[]> { ["bolter"] = [1] }, new Dictionary<string, string>()).WeaponIcon("sm_mp_ranged_bolter"));
    }
}
