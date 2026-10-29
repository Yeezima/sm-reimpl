using SmContent;
using SmFormats.Baf;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// M37 reader: the item the files call the krak grenade is the blind grenade (owner's knowledge of the original; ammunition
/// type <c>blind_grenade</c>). Its blinding blasts are kept as blind-only splashes (no damage, no reaction), the rest is
/// dropped. Synthetic files shaped like the install's (three blasts: r 8 stun + blindness, r 30 damage + blindness, r 8 with
/// an unmapped reaction); no original bytes.
/// </summary>
public class BlindGrenadeTuningTests
{
    static (string, object)[] T(params (string, object)[] items) => items;
    static (string, object) Default(string name, params (string, object)[] values) =>
        (name, values.Prepend(("required_upgrade_name", T(("required_list", T())))).ToArray());
    static (string, object) FallOff(float distance, float intensity) => ("fall_off_entry", T(("distance", distance), ("intensity_value", intensity)));

    /// <summary>A thrown item whose ammunition entry has <paramref name="ammunitionType"/>.</summary>
    static BafDocument Item(string blueprint, string ammunitionType)
    {
        var type = T(("$ref", "_base_templates\\ranged_delivery_types\\projectile"), ("ebp", blueprint));
        var delivery = T(
            ("max_range", 10f),
            ("basic_weapon_tuning", T(Default("entry", ("shot_cooldown", 1f)))),
            ("ranged_delivery_type", T(Default("entry", ("type", type)))));
        var utility = T(("$ref", "wargear\\ability_types\\standard_ranged"), ("delivery", delivery));
        var ammunition = T(
            ("ammunition_type", ammunitionType),
            ("by_upgrade", T(Default("wargear\\ammunition_info", ("capacity_clip", 2), ("initial_clip_amount", 1)))),
            ("name", "krak_grenade"));
        return BafDocument.Read(SyntheticBafWriter.Build(
            ("ability_data", T(("ability_definitions", T(("ability_utility", utility))))),
            ("common_data", T(("ammunition", T(("entry", ammunition)))))));
    }

    static (string, object) Damage(float general) => ("do_damage", T(
        ("damage_general", general), ("damage_armourshield", 0f),
        ("damage_material_multipliers", T(("default_body", 1f), ("default_head", 1f))),
        ("armourshield_damage_material_multipliers", T(("default_body", 1f), ("default_head", 1f)))));

    static (string, object) Reaction(string type) => ("trigger_reaction", T(("reaction_by_intensity", T(("entry", T(("intensity", 0f), ("reaction_type", type)))))));

    /// <summary>A blind-grenade-shaped blueprint (the install's layout, synthetic values).</summary>
    static BafDocument Blueprint(float innerBlind = 3.5f, float outerBlind = 2f) => BafDocument.Read(SyntheticBafWriter.Build(
        ("extensions", T(("projectile_ext", T(
            ("lifetime", 7f), ("speed", 47.5f),
            ("detonate_action", T(("commands", T(
                ("area_of_effect", T(
                    ("hit_actions", T(("commands", T(Reaction("reaction_mp_stun"), ("do_blindness", T(("blindness_duration", innerBlind))), ("apply_stun_damage", T(("stun_damage", 70f))))))),
                    ("target_filter", "target_all"), ("shape", T(("$ref", "_base_templates\\shape\\sphere"), ("radius", 8f))),
                    ("line_of_sight", true), ("ignore_self", false),
                    ("fall_off", T(FallOff(0f, 1f), FallOff(4f, 1f), FallOff(8f, 0.05f))))),
                ("area_of_effect", T(
                    ("hit_actions", T(("commands", T(Damage(10f), ("do_blindness", T(("blindness_duration", outerBlind))), ("apply_stun_damage", T(("stun_damage", 700f))))))),
                    ("target_filter", "target_all"), ("shape", T(("$ref", "_base_templates\\shape\\sphere"), ("radius", 30f))),
                    ("line_of_sight", true), ("ignore_self", false),
                    ("fall_off", T(FallOff(8f, 1f), FallOff(8.05f, 0.5f), FallOff(30f, 0.05f))))),
                ("area_of_effect", T(
                    ("hit_actions", T(("commands", T(Reaction("cs_2xy"))))),
                    ("target_filter", "target_all"), ("shape", T(("$ref", "_base_templates\\shape\\sphere"), ("radius", 8f))),
                    ("line_of_sight", false), ("ignore_self", false))))))),
            ("collision_type", "rigidbody"), ("detonate_on_collision", false), ("max_bounce_count", 1), ("stop_at_max_bounce", true),
            ("ground_lifetime", 1f), ("detonate_when_lifetime_over", true),
            ("rigid_info", T(("ground_angle", 54f)))))))));

    static Func<string, BafDocument?> Blueprints(BafDocument doc) => path => path == "ebps\\p\\krak" ? doc : null;

    [Fact]
    public void The_blind_grenade_keeps_only_its_blinding_blasts_with_no_damage_and_no_reaction()
    {
        var notes = new List<string>();
        var k = WeaponTuning.ReadThrown("sm_mp_eq_krak_grenade", Item("ebps\\p\\krak", WeaponTuning.BlindAmmunition), notes, Blueprints(Blueprint()))!;
        Assert.True(k.IsBlindGrenade);
        Assert.Equal("blind grenade", k.PlayerName);
        Assert.Null(k.Projectile.DetonateDamage);
        var blasts = k.Projectile.AllSplashes.ToList();
        // r 8: 3.5 s, r 30: 2 s (radius order); the unmapped r 8 reaction blast is gone.
        Assert.Equal([(8f, 3.5f), (30f, 2f)], blasts.Select(s => (s.Radius, s.Blindness)));
        foreach (var s in blasts)
        {
            Assert.True(s.BlindOnly);
            Assert.Equal((0f, 0f), (s.Damage.Damage, s.Damage.ShieldDamage));
            Assert.Empty(s.Reactions);
            Assert.Empty(s.FallOff);
            Assert.False(s.IgnoreSelf);   // the thrower can blind themselves (ignore_self false in the data)
            Assert.False(s.Lingers);
        }
        Assert.Contains(notes, n => n.Contains("blind grenade") && n.Contains("r 8 m: 3.5 s") && n.Contains("r 30 m: 2 s") && n.Contains("not applied"));
        // Throw values are kept.
        Assert.Equal((2, 1, 1f), (k.Count, k.SpawnCount, k.Cooldown));
        Assert.Equal((47.5f, 7f, "rigidbody"), (k.Projectile.Speed, k.Projectile.Lifetime, k.Projectile.CollisionType));
    }

    [Fact]
    public void Negative_control_another_ammunition_type_keeps_the_M28_reading_with_damage_and_reactions()
    {
        var notes = new List<string>();
        var k = WeaponTuning.ReadThrown("x", Item("ebps\\p\\krak", "projectile"), notes, Blueprints(Blueprint()))!;
        Assert.False(k.IsBlindGrenade);
        Assert.Null(k.PlayerName);
        Assert.Equal((30f, 10f, 2f), (k.Projectile.Splash!.Radius, k.Projectile.Splash.Damage.Damage, k.Projectile.Splash.Blindness));
        var inner = Assert.Single(k.Projectile.ExtraSplashes);
        Assert.Equal((8f, 3.5f, ReactionKind.Stun), (inner.Radius, inner.Blindness, Assert.Single(inner.Reactions).Kind));
        Assert.DoesNotContain(k.Projectile.AllSplashes, s => s.BlindOnly);
    }

    [Fact]
    public void A_blind_grenade_without_blindness_does_nothing_and_says_so()
    {
        var notes = new List<string>();
        var k = WeaponTuning.ReadThrown("y", Item("ebps\\p\\krak", WeaponTuning.BlindAmmunition), notes, Blueprints(Blueprint(0f, 0f)))!;
        Assert.Empty(k.Projectile.AllSplashes);
        Assert.Null(k.Projectile.DetonateDamage);
        Assert.Contains(notes, n => n.StartsWith("y:") && n.Contains("does nothing"));
    }
}
