using SmContent;
using SmFormats.Fx;

namespace SmFormats.Tests;

/// <summary>
/// fx/no-smoke: the engine-free rule that sorts the effects' sprites into smoke (removed unless <c>--smoke on</c>) and kept (flashes, fire, sparks, debris, god rays), the "skip the
/// whole node" rule and the switch. Material and element names are invented, shaped like the install's; nothing is read from the install.
/// </summary>
public class SmokeRulesTests
{
    static SmokeTraits Alpha(string material, string name = "", float s0 = 1f, float s1 = 1f, float a0 = 1f, float a1 = 1f, float duration = 1f) =>
        new(material, 0, name, false, s0, s1, a0, a1, duration);

    [Theory]
    [InlineData("test_smoke_cloud")]
    [InlineData("lingering_test_smoke")]
    [InlineData("alpha_volume_test")]
    [InlineData("alpha_trail_test")]
    [InlineData("alpha_spike_test")]
    [InlineData("test_shockwave")]
    [InlineData("ground_dust_test")]
    [InlineData("test_puff")]
    [InlineData("test_steam")]
    public void Alpha_sprites_with_a_smoke_or_dust_texture_are_smoke(string material) =>
        Assert.Equal(SmokeClass.Smoke, SmokeRules.Classify(Alpha(material)));

    [Theory]
    [InlineData("test_stone_chunks")]
    [InlineData("alpha_fragments_test")]
    [InlineData("test_debris")]
    [InlineData("test_soil_specs")]
    [InlineData("blood_test_spray")]
    [InlineData("test_sparks")]
    [InlineData("test_metal_bolts")]
    public void Alpha_sprites_with_a_debris_blood_or_spark_texture_are_kept(string material) =>
        Assert.Equal(SmokeClass.Keep, SmokeRules.Classify(Alpha(material, name: "smoke")));   // the texture wins over a smoky name

    [Fact]
    public void Explain_names_the_rule_that_decided()
    {
        Assert.Equal((SmokeClass.Keep, 1), SmokeRules.Explain(Alpha("test_beam_haze") with { GodRay = true }));
        Assert.Equal((SmokeClass.Keep, 2), SmokeRules.Explain(Alpha("test_smoke") with { Blend = 1 }));
        Assert.Equal((SmokeClass.Keep, 3), SmokeRules.Explain(Alpha("test_stone_chunks")));
        Assert.Equal((SmokeClass.Smoke, 4), SmokeRules.Explain(Alpha("test_smoke")));
        Assert.Equal((SmokeClass.Smoke, 5), SmokeRules.Explain(Alpha("plain_card", "dust")));
        Assert.Equal((SmokeClass.Smoke, 6), SmokeRules.Explain(Alpha("plain_trail", "trail", 0.14f, 0.26f, 0.54f, 0f, 0.4f)));
        Assert.Equal((SmokeClass.Keep, 7), SmokeRules.Explain(Alpha("plain_card", "flake")));
        // An alpha fireball or flash that grows but holds its alpha is not a puff.
        Assert.Equal((SmokeClass.Keep, 7), SmokeRules.Explain(Alpha("plain_fireball", "fireball", 1f, 3f, 1f, 0.9f, 0.3f)));
    }

    [Fact]
    public void A_debris_word_wins_over_a_smoke_word_in_the_same_texture_name() =>
        Assert.Equal(SmokeClass.Keep, SmokeRules.Classify(Alpha("smoke_debris_test")));

    [Theory]
    [InlineData("test_smoke_add")]
    [InlineData("test_volume_soft")]
    [InlineData("test_flash")]
    public void Additive_and_distortion_sprites_are_kept_whatever_their_texture_says(string material)
    {
        Assert.Equal(SmokeClass.Keep, SmokeRules.Classify(Alpha(material, name: "smoke") with { Blend = 1 }));
        Assert.Equal(SmokeClass.Keep, SmokeRules.Classify(Alpha(material, name: "smoke") with { Blend = 2 }));
    }

    [Fact]
    public void God_rays_are_light_and_kept_even_when_alpha_blended() =>
        Assert.Equal(SmokeClass.Keep, SmokeRules.Classify(Alpha("test_beam_haze") with { GodRay = true }));

    [Theory]
    [InlineData("thick smoke")]
    [InlineData("ground dust")]
    [InlineData("thin and linger")]
    [InlineData("wispy")]
    [InlineData("Mist")]
    public void A_plain_alpha_sprite_named_as_smoke_is_smoke(string name) =>
        Assert.Equal(SmokeClass.Smoke, SmokeRules.Classify(Alpha("plain_card", name)));

    [Fact]
    public void A_plain_alpha_sprite_with_another_name_and_no_puff_shape_is_kept() =>
        Assert.Equal(SmokeClass.Keep, SmokeRules.Classify(Alpha("plain_card", "flake")));

    [Fact]
    public void A_plain_alpha_sprite_that_grows_and_fades_out_is_a_puff()
    {
        // Shaped like a bolter ignition trail: 0.14 -> 0.26 m wide, alpha 0.54 -> 0, 0.4 s.
        var puff = Alpha("plain_trail", "trail", 0.14f, 0.26f, 0.54f, 0f, 0.4f);
        Assert.Equal(SmokeClass.Smoke, SmokeRules.Classify(puff));
        Assert.True(SmokeRules.IsPuffShaped(puff));
        Assert.Equal(SmokeClass.Smoke, SmokeRules.Classify(puff with { SizeStart = 0f }));                   // from nothing to a puff
        Assert.Equal(SmokeClass.Keep, SmokeRules.Classify(puff with { AlphaEnd = 0.5f }));                   // does not fade out
        Assert.Equal(SmokeClass.Keep, SmokeRules.Classify(puff with { SizeStart = 0.3f, SizeEnd = 0.32f }));  // does not grow enough
        Assert.Equal(SmokeClass.Keep, SmokeRules.Classify(puff with { SizeStart = 0.05f, SizeEnd = 0.2f })); // too small at the end
        Assert.Equal(SmokeClass.Keep, SmokeRules.Classify(puff with { Duration = 0.1f }));                   // a flash, not a puff
        Assert.Equal(SmokeClass.Keep, SmokeRules.Classify(puff with { Blend = 1 }));                         // additive: light
    }

    [Fact]
    public void Traits_and_classes_are_read_from_the_effect_data()
    {
        var fx = FxSystem.Read("test_tracer", SyntheticFxWriter.TracerSystem(streak: "test_streak_00", smoke: "test_smoke"));
        var all = fx.ElementsWithCollisions.ToList();
        var streak = all.Single(e => e.Name == "streak");
        var smokeBeam = all.Single(e => e.Name == "smoke");
        var puff = all.Single(e => e.Name == "puff");
        var trail = all.Single(e => e.Name == "trail");
        Assert.Equal(SmokeClass.Keep, SmokeRules.Classify(streak));           // additive tracer streak
        Assert.Equal(SmokeClass.Smoke, SmokeRules.Classify(smokeBeam));       // alpha beam with a smoke texture
        Assert.Equal(SmokeClass.Smoke, SmokeRules.Classify(puff));            // alpha ring with a smoke texture
        Assert.Equal(SmokeClass.Smoke, SmokeRules.Classify(trail));           // alpha streamer with a smoke texture
        Assert.Equal(SmokeClass.NotSprite, SmokeRules.Classify(all.Single(e => e is FxLight)));
        Assert.Equal(SmokeClass.NotSprite, SmokeRules.Classify(all.Single(e => e is FxDecal)));
        Assert.Equal(SmokeClass.NotSprite, SmokeRules.Classify(all.Single(e => e is FxMesh)));

        var t = SmokeRules.Traits(smokeBeam)!.Value;
        Assert.Equal(("test_smoke", 0, 0.2f, 0.9f, 1f), (t.Material, t.Blend, t.SizeStart, t.SizeEnd, t.AlphaEnd));
        var s = SmokeRules.Traits(streak)!.Value;
        Assert.Equal((1, 1f, 0f), (s.Blend, s.AlphaStart, s.AlphaEnd));       // the colour curve's alpha 255 -> 0
        Assert.Null(SmokeRules.Traits(all.Single(e => e is FxLight)));
    }

    [Fact]
    public void A_node_is_skipped_whole_only_when_everything_it_draws_is_smoke()
    {
        Assert.True(SmokeRules.OnlySmoke(true, false, []));                     // a smoke sprite
        Assert.False(SmokeRules.OnlySmoke(false, false, []));                   // a kept sprite, a light, a decal
        Assert.True(SmokeRules.OnlySmoke(false, true, [true, true]));           // a combo of smoke
        Assert.False(SmokeRules.OnlySmoke(false, true, [true, false]));         // a combo with a flash or a decal in it
        Assert.False(SmokeRules.OnlySmoke(false, true, []));                    // an empty combo is left alone
        Assert.True(SmokeRules.OnlySmoke(true, false, [true]));                 // a smoke sprite whose collision content is smoke too
        Assert.False(SmokeRules.OnlySmoke(true, false, [false]));               // review Q1: a smoke collider that starts a decal or debris keeps its parents drawn

        var fx = FxSystem.Read("test_tracer", SyntheticFxWriter.TracerSystem(streak: "test_streak_00", smoke: "test_smoke"));
        var roots = fx.Emitters.Single().Elements;
        Assert.False(SmokeRules.OnlySmoke(roots[0]));   // the spray holds the additive streak beside the smoke beam
        Assert.False(SmokeRules.OnlySmoke(roots[1]));   // the one-at-random combo holds a light beside the smoke puff
        Assert.True(SmokeRules.OnlySmoke(roots.Single(e => e.Name == "trail")));

        // The dropper's collision descriptions count: its ring starts a decal (kept) and a dust ring (smoke).
        var dropper = FxSystem.Read("test_dropper", SyntheticFxWriter.DropperSystem(dust: "test_dust"));
        Assert.False(SmokeRules.OnlySmoke(dropper.Emitters.Single().Elements.Single()));
        Assert.Equal(SmokeClass.Smoke, SmokeRules.Classify(dropper.ElementsWithCollisions.Single(e => e.Name == "dust")));
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--smoke", "on" }, true)]
    [InlineData(new[] { "--connect", "127.0.0.1:27590", "--smoke", "ON" }, true)]
    [InlineData(new[] { "--smoke=on" }, true)]
    [InlineData(new[] { "--smoke", "1" }, true)]
    [InlineData(new[] { "--smoke", "off" }, false)]
    [InlineData(new[] { "--smoke" }, false)]
    [InlineData(new[] { "--old-smoke" }, false)]
    public void The_switch_is_off_unless_smoke_on_is_given(string[] args, bool on) =>
        Assert.Equal(on, SmokeRules.SwitchOn(args));
}
