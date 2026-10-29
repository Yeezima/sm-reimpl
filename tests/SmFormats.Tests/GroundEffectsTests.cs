using SmContent;
using SmFormats.Baf;
using SmFormats.Fx;

namespace SmFormats.Tests;

/// <summary>
/// M41: the effects a ground pound and the landings play. Collision descriptions of a "dropper" particle (what the install's <c>jumppack_land_*</c> effects hold: decals, dust,
/// debris) are typed as <see cref="FxElement.OnCollision"/>, and the jump pack's wargear gives the pound's and the landings' effect names. Synthetic files only.
/// </summary>
public class GroundEffectsTests
{
    [Fact]
    public void Collision_descriptions_of_a_dropper_are_typed_and_kept_out_of_the_plain_descendants()
    {
        var fx = FxSystem.Read("test_dropper", SyntheticFxWriter.DropperSystem());
        Assert.Empty(fx.Notes);
        var spray = Assert.IsType<FxSpray>(Assert.Single(fx.Emitters).Elements[0]);
        Assert.Equal((100f, 6), (spray.Environment[0].Gravity, spray.Environment[0].CollisionResponse));
        var collider = Assert.IsType<FxRing>(Assert.Single(spray.Children));
        Assert.Empty(spray.OnCollision);
        var combo = Assert.IsType<FxCombo>(Assert.Single(collider.OnCollision));
        Assert.Equal([FxElementKind.Decal, FxElementKind.Ring], combo.Children.Select(c => c.Kind));
        var crater = Assert.IsType<FxDecal>(combo.Children[0]);
        Assert.Equal(("test_crater_dif", 4f, 4f), (crater.DiffuseMap, crater.Width, crater.Height));

        // A player that does not run collisions sees the dropper and its collider only; one that does follows them too.
        Assert.DoesNotContain(fx.Elements, e => e is FxDecal);
        Assert.Contains(fx.ElementsWithCollisions, e => e is FxDecal);
        Assert.Equal(2, fx.ElementsWithCollisions.OfType<FxRing>().Count());   // the collider and the dust
    }

    [Fact]
    public void Elements_without_collision_descriptions_have_none()
    {
        var fx = FxSystem.Read("test_tracer", SyntheticFxWriter.TracerSystem());
        Assert.All(fx.ElementsWithCollisions, e => Assert.Empty(e.OnCollision));
        Assert.Equal(fx.Elements.Count(), fx.ElementsWithCollisions.Count());
    }

    static (string, object)[] PlayFx(string name, float scale) =>
    [
        ("$ref", "action_commands\\play_fx"), ("fx_package", "test_fx"), ("fx_name", name), ("scale", scale), ("length", scale), ("attach_to_marker", false),
        ("trigger_point", "self"), ("marker", Array.Empty<(string, object)>()), ("trigger_point_target", ""), ("marker_target", Array.Empty<(string, object)>()),
        ("rotation_source", "world"), ("trigger_direction", "up"), ("scale_if_from_player", false), ("on_stop_policy", "stop_emitting"), ("lifetime", 0f), ("apply_only_if_on_player", false),
    ];

    static (string, object)[] Landing(float drop, string effect) =>
    [
        ("distance_from_apex_to_land", drop),
        ("action", new (string, object)[] { ("commands", new (string, object)[] { ("play_fx", PlayFx(effect, 1f)) }) }),
    ];

    static (string, object)[] Upgrade(string name) =>
        [("$ref", "_base_templates\\required_upgrade_name"), ("required_list", new (string, object)[] { ("upgrade_name", name) })];

    static BafDocument Pack() => BafDocument.Read(SyntheticBafWriter.Build(
        ("ability_data", new (string, object)[] { ("ability_definitions", new (string, object)[] { ("ability_jumppack", new (string, object)[]
        {
            ("jump_info", new (string, object)[] { ("entry", new (string, object)[]
            {
                ("action_land_regular_by_height", new (string, object)[]
                {
                    ("landing_action_entry", Landing(15f, "test_land_normal")),
                    ("landing_action_entry", Landing(0f, "test_land_light")),
                }),
            }) }),
        }) }) }),
        ("action_data", new (string, object)[] { ("triggered_actions", new (string, object)[] { ("actn_gp_hit", new (string, object)[] { ("commands", new (string, object)[]
        {
            ("actions_by_upgrade", new (string, object)[]
            {
                // The perk variant comes first in the file: it is not the default.
                ("entry", new (string, object)[] { ("required_upgrade_name", Upgrade("test_perk")), ("action", new (string, object)[] { ("commands", new (string, object)[] { ("play_fx", PlayFx("test_pound_perk", 1.4f)) }) }) }),
                ("entry", new (string, object)[] { ("action", new (string, object)[] { ("commands", new (string, object)[] { ("play_fx", PlayFx("test_pound", 1f)) }) }) }),
            }),
        }) }) }) })));

    [Fact]
    public void A_jump_pack_gives_the_effects_of_its_pound_and_its_landings_by_drop()
    {
        var effects = JumpPackEffects.Read("test_pack", Pack());
        Assert.NotNull(effects);
        Assert.Equal(["test_pound"], effects!.Pound.Select(p => p.Name));   // the default entry, not the perk's
        Assert.Equal([0f, 15f], effects.Landings.Select(l => l.MinDrop));   // sorted by drop
        Assert.Equal(["test_land_light"], effects.LandingFor(3f).Select(p => p.Name));
        Assert.Equal(["test_land_light"], effects.LandingFor(14.99f).Select(p => p.Name));
        Assert.Equal(["test_land_normal"], effects.LandingFor(15f).Select(p => p.Name));   // exactly the entry's drop takes it
        Assert.Equal(["test_land_normal"], effects.LandingFor(40f).Select(p => p.Name));
        Assert.Empty(effects.LandingFor(-1f));
        Assert.Equal(["test_land_light"], effects.LightLanding.Select(p => p.Name));
        Assert.Equal(["test_pound", "test_land_light", "test_land_normal"], effects.Names);
    }

    [Fact]
    public void A_file_without_a_jump_pack_gives_no_effects()
    {
        var doc = BafDocument.Read(SyntheticBafWriter.Build(("common_data", new (string, object)[] { ("slot", "ranged") })));
        Assert.Null(JumpPackEffects.Read("rifle", doc));
    }
}
