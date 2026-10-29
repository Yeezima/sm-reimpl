using SmContent;
using SmFormats.Baf;

namespace SmFormats.Tests;

/// <summary>
/// F-D1: the death effects the MP data binds to player deaths (<see cref="DeathEffects"/>) and the rules that pick them (<see cref="DeathEffectRules"/>). Synthetic files only: the shapes
/// follow the install's player blueprint (<c>entity_actions_ext/triggered_actions/actn_plasma_death</c>, <c>health_ext/death_action/actions_by_upgrade</c>) and jump pack
/// (<c>action_data/triggered_actions/actn_jumpdeath_start</c>) as read with <c>smtool baf-dump</c>.
/// </summary>
public class DeathEffectsTests
{
    static (string, object)[] Play(string effect, string marker, float scale = 1f, bool attached = false, string direction = "negative_hit_direction", string rotation = "trigger_point") =>
    [
        ("$ref", "action_commands\\play_fx"), ("fx_package", "master_fx"), ("fx_name", effect), ("scale", scale), ("length", scale), ("attach_to_marker", attached),
        ("trigger_point", "other"), ("marker", new (string, object)[] { ("marker", marker) }), ("trigger_point_target", ""), ("marker_target", Array.Empty<(string, object)>()),
        ("rotation_source", rotation), ("trigger_direction", direction), ("scale_if_from_player", true), ("on_stop_policy", "play_to_end"), ("lifetime", 0f), ("apply_only_if_on_player", false),
    ];

    static (string, object)[] Sound(string eventName, int delayMs = 0) =>
        [("$ref", "action_commands\\post_sound_event"), ("start_event_name", eventName), ("stop_event_name", ""), ("trigger_point", "self"), ("time_delay", delayMs)];

    static (string, object)[] Delayed(float seconds, params (string, object)[] commands) =>
        [("$ref", "action_commands\\delayed_action"), ("action", new (string, object)[] { ("$ref", "action"), ("commands", commands) }), ("delay", seconds)];

    static (string, object) Commands(params (string, object)[] commands) => ("commands", commands);

    static BafDocument Player(bool withMelta = true)
    {
        var plasma = new (string, object)[]
        {
            Commands(
                ("delayed_action", Delayed(1.7f, ("play_fx", Play("test_vanish", "rig_head")))),
                ("play_fx", Play("test_vanish", "rig_l_hand")),
                ("play_fx", Play("test_burn", "rig_spinea", attached: true)),
                ("delayed_action", Delayed(1.2f, ("remove_wargear", new (string, object)[] { ("$ref", "action_commands\\remove_wargear"), ("wargear_slot", "ranged") })))),
        };
        var triggered = new List<(string, object)>
        {
            ("muzzleflash", new (string, object)[] { Commands(("play_fx", Play("test_muzzle", "fx_muzzle", attached: true))) }),
            ("actn_plasma_death", plasma),
        };
        if (withMelta) triggered.Add(("actn_melta_death", new (string, object)[] { Commands(("play_fx", Play("test_vaporize", "rig_lhand"))) }));
        var vengeance = new (string, object)[]
        {
            ("required_upgrade_name", new (string, object)[] { ("required_list", new (string, object)[] { ("upgrade_name", DeathEffectRules.FinalVengeanceUpgrade) }) }),
            ("action", new (string, object)[]
            {
                Commands(
                    ("delayed_action", Delayed(2.5f, ("play_fx", Play("test_blast", "rig_pelvis", attached: true)), ("post_sound_event", Sound("test_boom")))),
                    ("post_sound_event", Sound("test_timer", 250)),
                    ("play_fx", Play("test_predeath", "rig_spinea", attached: true))),
            }),
        };
        var deathAction = new (string, object)[]
        {
            Commands(
                ("post_sound_event", Sound("play_kill_shot_mp")),
                ("actions_by_upgrade", new (string, object)[] { ("actions", new (string, object)[] { ("entry", vengeance) }) })),
        };
        return BafDocument.Read(SyntheticBafWriter.Build(("extensions", new (string, object)[]
        {
            ("entity_actions_ext", new (string, object)[] { ("triggered_actions", triggered.ToArray()) }),
            ("health_ext", new (string, object)[] { ("death_action", deathAction) }),
        })));
    }

    static BafDocument Pack() => BafDocument.Read(SyntheticBafWriter.Build(
        ("action_data", new (string, object)[] { ("triggered_actions", new (string, object)[]
        {
            ("actn_jumpdeath_start", new (string, object)[] { Commands(("play_fx", Play("test_jump_death", "rig_spinea", 0.6f)), ("play_fx", Play("test_burn_limb", "rig_l_upperarm", attached: true, direction: "up")), ("post_sound_event", Sound("test_explode"))) }),
            ("actn_jumpdeath_impact", new (string, object)[] { Commands(("play_fx", Play("test_land", "rig_root", 0.9f)), ("post_sound_event", Sound("test_thud"))) }),
        }) })));

    static BafDocument Weapon(params string[] reactions) => BafDocument.Read(SyntheticBafWriter.Build(
        ("common_data", new (string, object)[] { ("slot", "ranged") }),
        ("ability_data", new (string, object)[] { ("hit", new (string, object)[] { ("trigger_reaction", new (string, object)[]
        {
            ("reaction_by_intensity", reactions.Select(r => ("entry", (object)new (string, object)[] { ("intensity", 0f), ("reaction_type", r) })).ToArray()),
        }) }) })));

    static DeathEffects Effects() => DeathEffects.Build(Player(), Player(withMelta: false), Pack(), null,
        new Dictionary<string, BafDocument> { ["test_plasma"] = Weapon("rng_plasma_gun_weak"), ["test_melta"] = Weapon("rng_melta_short"), ["test_bolter"] = Weapon("rng_bolter") });

    [Fact]
    public void The_plasma_death_is_read_with_its_delayed_steps_and_its_weapon_removal()
    {
        var plasma = Effects().Marines.Plasma;
        Assert.NotNull(plasma);
        Assert.Equal(DeathEffects.PlasmaAction, plasma!.Name);
        // In file order: the delayed head effect at 1.7 s, the immediate hand effect and burning body, then the weapon removal at 1.2 s.
        Assert.Equal([(DeathStepKind.Effect, 1.7f), (DeathStepKind.Effect, 0f), (DeathStepKind.Effect, 0f), (DeathStepKind.RemoveWargear, 1.2f)], plasma.Steps.Select(s => (s.Kind, s.Delay)));
        Assert.Equal(["test_vanish", "test_vanish", "test_burn"], plasma.Effects.Select(s => s.Fx!.Name));
        var burn = plasma.Effects.Last().Fx!;
        Assert.Equal(("rig_spinea", true, "other"), (burn.Marker, burn.Attached, burn.TriggerPoint));
        Assert.Equal("ranged", plasma.Steps.Last().Slot);
        Assert.Equal(["test_vanish", "test_burn"], plasma.EffectNames);
    }

    [Fact]
    public void A_side_without_a_melta_death_has_none_and_says_so()
    {
        var notes = new List<string>();
        var effects = DeathEffects.Build(Player(), Player(withMelta: false), null, null, new Dictionary<string, BafDocument>(), notes);
        Assert.NotNull(effects.Marines.Melta);
        Assert.Null(effects.Chaos.Melta);
        Assert.Contains(notes, n => n.StartsWith("csm: no actn_melta_death", StringComparison.Ordinal));
    }

    [Fact]
    public void The_jump_deaths_come_from_the_jump_pack_and_the_perk_death_from_the_blueprints_upgrade_entry()
    {
        var marines = Effects().Marines;
        Assert.Equal(["test_jump_death", "test_burn_limb"], marines.JumpStart!.EffectNames);
        Assert.Equal(["test_explode"], marines.JumpStart.Steps.Where(s => s.Kind == DeathStepKind.Sound).Select(s => s.Sound));
        Assert.Equal(["test_land"], marines.JumpImpact!.EffectNames);
        var vengeance = Assert.Single(marines.Perks);
        Assert.Equal(DeathEffectRules.FinalVengeanceUpgrade, vengeance.Upgrade);
        // The delayed explosion at 2.5 s (effect and sound), the timer sound 250 ms in, the pre-death effect at once.
        Assert.Equal([(DeathStepKind.Effect, 2.5f, "test_blast"), (DeathStepKind.Sound, 2.5f, "test_boom"), (DeathStepKind.Sound, 0.25f, "test_timer"), (DeathStepKind.Effect, 0f, "test_predeath")],
            vengeance.Action.Steps.Select(s => (s.Kind, s.Delay, s.Fx?.Name ?? s.Sound)));
        // The Chaos side without a pack file has no jump deaths.
        Assert.Null(Effects().Chaos.JumpStart);
    }

    [Fact]
    public void Weapons_are_classified_by_the_reactions_in_their_file_then_by_name()
    {
        var effects = Effects();
        Assert.Equal(DeathFamily.Plasma, effects.FamilyOf("test_plasma"));
        Assert.Equal(DeathFamily.Melta, effects.FamilyOf("test_melta"));
        Assert.Equal(DeathFamily.None, effects.FamilyOf("test_bolter"));
        Assert.Equal(DeathFamily.Plasma, effects.FamilyOf("sm_mp_exotic_plasma_cannon"));   // its reaction types are not in its file; the name says it
        Assert.Equal(DeathFamily.Melta, effects.FamilyOf("csm_mp_ranged_melta"));
        Assert.Equal(DeathFamily.None, effects.FamilyOf("sm_mp_ranged_bolter"));
        Assert.Equal(DeathFamily.Plasma, DeathEffects.FamilyOfFile(Weapon("rng_leg_hit", "rng_plasma_pistol_strong")));
        Assert.Equal(DeathFamily.None, DeathEffects.FamilyOfFile(Weapon()));
    }

    [Fact]
    public void Every_effect_the_actions_play_is_listed_once_for_preloading()
    {
        var names = Effects().EffectNames.ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("test_vaporize", names);
        Assert.Contains("test_land", names);
        Assert.Contains("test_predeath", names);
        Assert.DoesNotContain("test_muzzle", names);   // other triggered actions are not death effects
    }

    [Theory]
    [InlineData(false, false, DeathFamily.Plasma, DeathKind.Plasma)]
    [InlineData(false, true, DeathFamily.Melta, DeathKind.Melta)]
    [InlineData(false, true, DeathFamily.None, DeathKind.None)]
    [InlineData(true, true, DeathFamily.Plasma, DeathKind.Jump)]   // the jump death outranks the plasma and melta deaths
    [InlineData(true, true, DeathFamily.None, DeathKind.Jump)]
    [InlineData(true, false, DeathFamily.Plasma, DeathKind.Plasma)]   // no pack, no jump death
    public void A_death_in_a_jump_plays_the_jump_death_else_the_weapons_family_decides(bool jumping, bool hasPack, DeathFamily family, DeathKind expected) =>
        Assert.Equal(expected, DeathEffectRules.Choose(jumping, hasPack, family));

    [Fact]
    public void The_actions_start_a_frame_after_the_death_and_the_impact_waits_for_the_fall()
    {
        Assert.Equal(1f / 30f, DeathEffectRules.StartOf(DeathKind.Plasma), 5);
        Assert.Equal(DeathEffectRules.StartActionDelay, DeathEffectRules.StartOf(DeathKind.Melta));
        Assert.Equal(DeathEffectRules.JumpStartDelay, DeathEffectRules.StartOf(DeathKind.Jump));
        // Near the ground at once is still not before the start clip hands over to the fall (0.9412 of 0.6 s).
        Assert.False(DeathEffectRules.ImpactDue(0.3f, 0.2f));
        Assert.True(DeathEffectRules.ImpactDue(0.6f, 0.2f));
        // High up, it waits; with no level below it (a pit), it waits for the limit.
        Assert.False(DeathEffectRules.ImpactDue(1.5f, 6f));
        Assert.False(DeathEffectRules.ImpactDue(1.5f, null));
        Assert.True(DeathEffectRules.ImpactDue(DeathEffectRules.JumpImpactLimit, null));
    }

    [Fact]
    public void The_actions_pick_by_kind_and_a_missing_one_gives_nothing()
    {
        var marines = Effects().Marines;
        Assert.Equal(DeathEffects.PlasmaAction, DeathEffectRules.ActionOf(marines, DeathKind.Plasma)!.Name);
        Assert.Equal(DeathEffects.MeltaAction, DeathEffectRules.ActionOf(marines, DeathKind.Melta)!.Name);
        Assert.Equal(DeathEffects.JumpStartAction, DeathEffectRules.ActionOf(marines, DeathKind.Jump)!.Name);
        Assert.Null(DeathEffectRules.ActionOf(marines, DeathKind.None));
        Assert.Null(DeathEffectRules.ActionOf(DeathSide.Empty, DeathKind.Plasma));
    }

    [Theory]
    [InlineData("RIG_L_Hand", "rig_l_hand", true)]
    [InlineData("RIG_SpineA", "rig_spinea", true)]
    [InlineData("RIG_L_Hand", "rig_lhand", true)]      // the melta death's spelling
    [InlineData("RIG_R_Thigh", "rig_rthigh", true)]
    [InlineData("RIG_L_Calf", "rig_lcalf", true)]
    [InlineData("RIG_Root", "rig_root", true)]
    [InlineData("RIG_L_Hand", "rig_r_hand", false)]
    [InlineData("RIG_Prop_LHand", "rig_lhand", false)]
    [InlineData("RIG_SpineD", "rig_spine1", false)]
    [InlineData("RIG_Pelvis", "rig_chest", false)]
    [InlineData("RIG_Head", "rig_head_", true)]        // trailing underscores are ignored too
    [InlineData("", "rig_head", false)]
    public void Markers_match_bones_ignoring_case_and_underscores(string bone, string marker, bool expected) =>
        Assert.Equal(expected, DeathEffectRules.SameBone(bone, marker));
}
