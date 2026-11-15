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

    // fx/bolt-smoke ("Thin the dark mist", "Flash only"): the blood mist cards and the fire puff go from a bolt or autocannon hit and kill on an Ork; the red spray, hard spikes, drops, gibs and flashes stay
    [Theory]
    [InlineData(false, "blood_breakup", true)]
    [InlineData(false, "blood_beam_breakup", true)]
    [InlineData(false, "blood_exit_beam_soft", true)]
    [InlineData(true, "muzzle_fire_anim", true)]
    [InlineData(false, "blood_exit_beam_hard", false)]
    [InlineData(false, "blood_spec_ring_tp_01", false)]
    [InlineData(false, "blood_meat", false)]
    [InlineData(false, "blood_streamer", false)]
    [InlineData(false, "blood_splat", false)]
    [InlineData(true, "add_point", false)]            // the bright flash
    [InlineData(true, "directional_flash_beam", false)]
    [InlineData(true, "cloud_beam_warm", false)]
    public void The_blood_mist_cards_and_the_fire_puff_are_the_mist_sprites(bool additive, string material, bool mist) =>
        Assert.Equal(mist, BrightFlash.IsMistSprite(additive, material));

    [Fact]
    public void An_alpha_fire_named_material_is_no_mist_and_an_additive_blood_card_is_none()
    {
        Assert.False(BrightFlash.IsMistSprite(false, "muzzle_fire_anim"));
        Assert.False(BrightFlash.IsMistSprite(true, "blood_breakup"));
    }

    [Theory]
    [InlineData(false, false, false, true)]   // a bolt blast: flash only
    [InlineData(false, false, true, false)]   // --old-bolt-smoke: the whole frag effect
    [InlineData(true, false, false, true)]    // the autocannon's blast: flash only, as before
    [InlineData(true, true, false, false)]    // --musou-old-autocannon-fx
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, true)]    // the autocannon restore does not bring the bolt blast's puff back
    public void The_bolt_blast_draws_only_its_bright_flash_unless_restored(bool autocannonBlast, bool oldAutocannon, bool oldBolt, bool flashOnly) =>
        Assert.Equal(flashOnly, BoltSmoke.BlastFlashOnly(autocannonBlast, oldAutocannon, oldBolt));

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
