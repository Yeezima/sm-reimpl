using SmContent;

namespace SmFormats.Tests;

/// <summary>musou/autocannon-flash (owner 2026-11-15): which sprites of the autocannon's hit and blast effects the flash-only draw keeps (names as in the data; no original bytes).</summary>
public class BrightFlashTests
{
    [Theory]
    [InlineData(true, false, "1 frame flash", true)]
    [InlineData(true, false, "ground flash", true)]
    [InlineData(true, false, "flash", true)]
    [InlineData(true, false, "streaks", true)]
    [InlineData(true, false, "fire", false)]
    [InlineData(true, false, "flare", false)]
    [InlineData(true, false, "hot cloud", false)]
    [InlineData(true, false, "inner fire", false)]
    [InlineData(false, false, "flash", false)]        // an alpha sprite named flash is not a flash
    [InlineData(true, true, "flash", false)]          // a distortion sprite never
    [InlineData(false, false, "smoke ball", false)]
    [InlineData(false, false, "mid debris", false)]
    public void Only_additive_flash_and_streak_sprites_stay(bool additive, bool distortion, string name, bool kept) =>
        Assert.Equal(kept, BrightFlash.Keeps(additive, distortion, name));

    [Theory]
    [InlineData("stone_chunks_small_00", true)]
    [InlineData("stone_chunks_00", true)]
    [InlineData("debris_animated", true)]
    [InlineData("ground_dust", true)]
    [InlineData("dirt_puff", true)]
    [InlineData("blood_splat", false)]
    [InlineData("blood_breakup", false)]
    [InlineData("blood_meat", false)]
    [InlineData("blood_spec_ring_tp_01", false)]
    [InlineData("add_point", false)]
    [InlineData("", false)]
    public void Grit_is_dust_and_stone_sprites_never_blood(string material, bool grit) => Assert.Equal(grit, BrightFlash.IsGrit(material));

    [Fact]
    public void The_autocannon_and_frag_effects_keep_at_least_one_flash_sprite()
    {
        // The sprite names and blends as read from the install with smtool fx-dump on 2026-11-15 (autocannon_dmg, autocannon_dmg_body, frag_grenade_explosion); no original bytes.
        (bool Additive, string Name)[] autocannonDmg = [(true, "1 frame flash"), (true, "1 frame flash"), (false, "new"), (false, "debris column"), (true, "hot cloud"), (true, "inner fire"), (true, "ground flash"), (true, "ground flash")];
        (bool Additive, string Name)[] autocannonBody = [(true, " 1 frame flash"), (true, "flash core"), (true, "explosion"), (true, "flash"), (false, "blood splat"), (false, "smoke dissipating")];
        (bool Additive, string Name)[] frag = [(true, "flash"), (true, "streaks"), (true, "flare"), (true, "fire"), (false, "dirt"), (false, "smoke"), (true, "flash")];
        foreach (var effect in new[] { autocannonDmg, autocannonBody, frag })
            Assert.Contains(effect, s => BrightFlash.Keeps(s.Additive, false, s.Name));
        // and the fire, the flare and the clouds are not kept
        Assert.DoesNotContain(frag, s => s.Name is "fire" or "flare" or "smoke" or "dirt" && BrightFlash.Keeps(s.Additive, false, s.Name));
    }
}
