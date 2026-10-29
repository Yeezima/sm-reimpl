using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// M28: reactions from data (synthetic files only): the multiplayer reaction mapping, <c>trigger_reaction</c> entries, a melee
/// hit's reaction (direct or the default upgrade entry), blast reactions and blindness, the reaction graph's clip timing, the
/// attack windows without <c>BRANCH_HitReact</c>, and the heavy strikes with the stomp read by <see cref="MeleeTuning"/>.
/// </summary>
public class ReactionTuningTests
{
    static (string, object) Mapping(string source, string zone, string destination) => ("character_reaction_mapping", new (string, object)[]
    {
        ("$ref", "character_reaction_mapping"), ("source_reaction", source), ("source_hit_zone", zone), ("destination_reaction", destination),
    });

    static readonly BafDocument Mapper = BafDocument.Read(SyntheticBafWriter.Build(("reaction_mapping", new (string, object)[]
    {
        Mapping("", "left_leg", "rng_leg_hit"),
        Mapping("cs_default_1", "", "reaction_mp_melee"),
        Mapping("cs_default_1", "", "reaction_mp_stun"),     // a second entry for the same source: the first wins
        Mapping("ax_default_2", "", "reaction_mp_melee"),
    })));

    static (string, object) Entry(float intensity, string type, float back = 0f, float duration = 0f) => ("entry", new (string, object)[]
    {
        ("intensity", intensity), ("reaction_type", type),
        ("reaction_distance", new (string, object)[] { ("distance_right", 0f), ("distance_backward", back), ("distance_up", 0f), ("duration", duration) }),
    });

    static (string, object) Trigger(params (string, object)[] entries) => ("trigger_reaction", new (string, object)[]
    {
        ("$ref", "action_commands\\trigger_reaction"), ("reaction_by_intensity", entries),
    });

    [Fact]
    public void The_mapping_keeps_entries_without_a_hit_zone_and_renames_melee_reactions()
    {
        var map = ReactionTuning.ReadMapping(Mapper);
        Assert.Equal(2, map.Count);
        Assert.Equal("reaction_mp_melee", ReactionTuning.Resolve(map, "cs_default_1"));
        Assert.Equal("reaction_mp_melee", ReactionTuning.Resolve(map, "AX_DEFAULT_2"));   // case-insensitive, as names in the data vary
        Assert.Equal("ck_x", ReactionTuning.Resolve(map, "ck_x"));
        Assert.Equal(ReactionKind.Interrupt, Reaction.KindOf(ReactionTuning.Resolve(map, "cs_default_1")));
        Assert.Equal(ReactionKind.None, Reaction.KindOf("reaction_flinch"));
    }

    [Fact]
    public void Entries_keep_multiplayer_reactions_with_their_knockback_and_a_hit_takes_the_direct_or_default_entry()
    {
        var map = ReactionTuning.ReadMapping(Mapper);
        var doc = BafDocument.Read(SyntheticBafWriter.Build(
            ("blast", new (string, object)[]
            {
                ("hit_actions", new (string, object)[] { ("commands", new (string, object)[]
                {
                    Trigger(Entry(0.8f, "reaction_mp_stagger", 2f, 0.1f), Entry(0.61f, "reaction_mp_stun", 1f, 0.5f), Entry(0f, "reaction_mp_melee"), Entry(0f, "pa_3xy")),
                    ("do_blindness", new (string, object)[] { ("blindness_duration", 3.5f) }),
                }) }),
            }),
            ("direct", new (string, object)[] { Trigger(Entry(0f, "cs_default_1")), ("delayed_action", new (string, object)[] { ("action", new (string, object)[] { ("commands", new (string, object)[] { Trigger(Entry(0f, "reaction_mp_stun")) }) }) }) }),
            ("by_upgrade", new (string, object)[] { ("actions_by_upgrade", new (string, object)[] { ("actions", new (string, object)[]
            {
                ("entry", new (string, object)[] { ("required_upgrade_name", new (string, object)[] { ("required_list", new (string, object)[] { ("upgrade_name", "perk") }) }),
                    ("action", new (string, object)[] { ("commands", new (string, object)[] { Trigger(Entry(0f, "reaction_mp_stagger", 1.5f, 0.1f)) }) }) }),
                ("entry", new (string, object)[] { ("required_upgrade_name", new (string, object)[] { ("required_list", Array.Empty<(string, object)>()) }),
                    ("action", new (string, object)[] { ("commands", new (string, object)[] { Trigger(Entry(0f, "reaction_mp_stun")) }) }) }),
            }) }) }),
            ("flinch_only", new (string, object)[] { Trigger(Entry(0f, "reaction_ranged_light")) })));
        var (reactions, blind) = ReactionTuning.BlastEffects(doc.Root.Table("blast")!.Value);
        Assert.Equal([new ReactionEntry(0.8f, ReactionKind.Stagger, 2f, 0f, 0f, 0.1f), new ReactionEntry(0.61f, ReactionKind.Stun, 1f, 0f, 0f, 0.5f), new ReactionEntry(0f, ReactionKind.Interrupt)], reactions);
        Assert.Equal(3.5f, blind);
        Assert.Equal(new ReactionEntry(0f, ReactionKind.Interrupt), ReactionTuning.HitReaction(doc.Root.Table("direct"), map));   // mapped; the delayed one is not read
        Assert.Equal(ReactionKind.None, ReactionTuning.HitReaction(doc.Root.Table("direct"), null).Kind);                      // unmapped: a campaign name
        Assert.Equal(new ReactionEntry(0f, ReactionKind.Stun), ReactionTuning.HitReaction(doc.Root.Table("by_upgrade"), map));  // the default entry, not the perk's
        Assert.Equal(ReactionKind.None, ReactionTuning.HitReaction(doc.Root.Table("flinch_only"), map).Kind);
    }

    static BehaviorTag Branch(string name, float start, float end = 1f) => new("Branch", start, end, name, null);

    [Fact]
    public void The_window_without_HitReact_is_the_first_gap_in_its_branches()
    {
        BehaviorSequencer Seq(params BehaviorTag[] tags) => new("s", ["a"], tags, [1f]);
        var (from, to) = ReactionTuning.NoReactWindow(Seq(Branch("BRANCH_HitReact", 0f, 0.1778f), Branch("BRANCH_HitReact", 0.2444f, 1f)), 1.4333f);
        Assert.Equal((0.2548f, 0.3503f), (MathF.Round(from, 4), MathF.Round(to, 4)));
        Assert.Equal((0.5f, 2f), ReactionTuning.NoReactWindow(Seq(Branch("BRANCH_HitReact", 0f, 0.25f)), 2f));
        Assert.Equal((0f, 0.4f), ReactionTuning.NoReactWindow(Seq(Branch("BRANCH_HitReact", 0.2f, 1f)), 2f));
        Assert.Equal((0f, 0f), ReactionTuning.NoReactWindow(Seq(Branch("BRANCH_Run", 0.2f)), 2f));                                  // no HitReact tag
        Assert.Equal((0f, 0f), ReactionTuning.NoReactWindow(Seq(Branch("BRANCH_HitReact", 0f, 0.6f), Branch("BRANCH_HitReact", 0.5f, 1f)), 2f));   // covered
    }

    static BehaviorSequencer Clip(string name, string animation, params BehaviorTag[] tags) => new(name, [animation], tags, [1f]);

    static BehaviorGraph Graph(params BehaviorSequencer[] extra) => new(ReactionTuning.Graph, [],
    [
        Clip("SequencerGenerator04", "SM_REC_Light_Itrpt_FHL_01", Branch("BRANCH_Reaction_End", 0.9853f), Branch("BRANCH_HitReact", 0f)),   // not the CSM one
        Clip("SequencerGenerator CSM MA1 Light FHL", "SM_REC_Light_Itrpt_FHL_01", Branch("BRANCH_Evade", 0.4f), Branch("BRANCH_HitReact", 0.32f),
            Branch("BRANCH_Reaction_End", 0.84f), Branch("BRANCH_Melee_Combo", 0.68f), Branch("BRANCH_Run", 0.84f)),
        Clip("SequencerGenerator CSM MA1 Light BHR", "SM_REC_Light_Itrpt_BHR_01", Branch("BRANCH_Evade", 0.2f), Branch("BRANCH_HitReact", 0.4f),
            Branch("BRANCH_Reaction_End", 0.92f), Branch("BRANCH_Melee_Combo", 0.72f), Branch("BRANCH_Run", 0.92f)),
        Clip("SequencerGenerator06", "SM_MP_REC_Grenade_F_01", Branch("BRANCH_Reaction_End", 0.5333f), Branch("BRANCH_Evade", 0.3333f)),
        Clip("SequencerGenerator07", "SM_MP_REC_Grenade_R_01", Branch("BRANCH_Reaction_End", 0.8f), Branch("BRANCH_Evade", 0.3333f)),
        Clip("SequencerGenerator08", "SM_MP_REC_Grenade_B_01", Branch("BRANCH_Reaction_End", 0.6667f), Branch("BRANCH_Evade", 0.3333f)),
        Clip("REC Grenade L", "SM_MP_REC_Grenade_L_01", Branch("BRANCH_Reaction_End", 0.8f), Branch("BRANCH_Evade", 0.3333f)),
        Clip("SequencerGenerator09", "SM_REC_Stagger_F_01", Branch("BRANCH_Reaction_End", 0.3953f)),
        .. extra,
    ]);

    static float Length(string animation) => animation.Contains("Stagger") ? 1.4333f : animation.Contains("FHL") ? 1.1667f : 1f;

    [Fact]
    public void Reaction_clips_come_from_the_reaction_graph_with_their_branch_times_and_a_missing_clip_keeps_the_placeholders()
    {
        var notes = new List<string>();
        Assert.Null(ReactionTuning.Specs([Graph()], Length, notes));   // no back stagger clip
        Assert.Contains(notes, n => n.Contains("SM_REC_Stagger_B_01"));
        var specs = ReactionTuning.Specs([Graph(Clip("SequencerGenerator11", "SM_REC_Stagger_B_01", Branch("BRANCH_Reaction_End", 0.7442f)))], Length, notes)!;
        Assert.Equal([ReactionKind.Interrupt, ReactionKind.Stun, ReactionKind.Stagger], specs.Select(s => s.Kind));
        var front = specs[0].Front;
        Assert.Equal(("SM_REC_Light_Itrpt_FHL_01", 0.98f, 0.98f, 0.4667f, 0.7934f, 0.3733f),
            (front.Animation, MathF.Round(front.EndAt, 4), MathF.Round(front.RunAt, 4), MathF.Round(front.EvadeAt, 4), MathF.Round(front.MeleeAt, 4), MathF.Round(front.ReactAt, 4)));
        Assert.Equal(front, specs[0].Right);   // two-way: the side clips are the front's
        Assert.False(specs[0].FourWay);
        Assert.True(specs[1].FourWay);
        var stun = specs[1].Back;
        Assert.Equal((0.6667f, 0.6667f, 0.3333f, 0.6667f), (stun.EndAt, stun.RunAt, stun.EvadeAt, stun.ReactAt));   // missing branches: the end
        Assert.Equal(0.5666f, MathF.Round(specs[2].Front.EndAt, 4));
    }

    // ---------------------------------------------------------------- heavy strikes

    static readonly MeleeTuning.WeaponMap Knife = new("knife", "MP_Test", ["CK 1 Sequence"], ["ability_melee_ck1"])
    {
        HeavySequencers = ["Y1", "Y2", "Y4"],
        HeavyAbilities = ["ability_melee_y", "ability_melee_y2", ""],
    };

    static BehaviorTag Event(string name, float at) => new("Event", at, at, name, null);

    static BehaviorSequencer Attack(string name, params BehaviorTag[] extra) => new(name, ["A_3.5m"],
    [
        Event("ACTN_Hit_Targets_Multiplayer", 0.2f), Branch("BRANCH_Melee_Combo", 0.4f, 0.6f), Branch("BRANCH_ToRanged", 0.3f),
        Branch("BRANCH_Melee_Draw_Run", 0.65f), Branch("BRANCH_HitReact", 0f, 0.15f), Branch("BRANCH_HitReact", 0.25f, 1f), .. extra,
    ], [1f]);

    static (string, object)[] Ability(float damage, string primaryReaction, string secondaryReaction) =>
    [
        ("primary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[]
        {
            Trigger(Entry(0f, primaryReaction, 1.5f, 0.1f)),
            ("do_damage", new (string, object)[] { ("damage_general", damage) }),
        }) }) }),
        ("secondary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[]
        {
            Trigger(Entry(0f, secondaryReaction)),
            ("do_damage", new (string, object)[] { ("damage_general", damage / 2) }),
        }) }) }),
        ("ability_metadata", new (string, object)[] { ("generic_metadata", new (string, object)[] { ("max_range", 4f) }) }),
    ];

    static BafDocument Wargear() => BafDocument.Read(SyntheticBafWriter.Build(
        ("ability_data", new (string, object)[] { ("ability_definitions", new (string, object)[]
        {
            ("ability_melee_ck1", Ability(40f, "cs_default_1", "reaction_ranged_light")),
            ("ability_melee_y", Ability(35f, "reaction_mp_stun", "reaction_mp_melee")),
            ("ability_melee_y2", Ability(40f, "reaction_mp_stagger", "reaction_mp_melee")),
        }) }),
        ("action_data", new (string, object)[] { ("triggered_actions", new (string, object)[] { ("displace_footstomp", new (string, object)[] { ("commands", new (string, object)[]
        {
            ("actions_by_upgrade", new (string, object)[] { ("actions", new (string, object)[]
            {
                ("entry", new (string, object)[] { ("required_upgrade_name", new (string, object)[] { ("required_list", Array.Empty<(string, object)>()) }),
                    ("action", new (string, object)[] { ("commands", new (string, object)[] { ("area_of_effect", new (string, object)[]
                    {
                        ("hit_actions", new (string, object)[] { ("commands", new (string, object)[]
                        {
                            ("do_damage", new (string, object)[] { ("damage_general", 25f), ("damage_armourshield", 50f) }),
                            Trigger(Entry(0.8f, "reaction_mp_stagger"), Entry(0.61f, "reaction_mp_stun"), Entry(0f, "reaction_mp_melee")),
                        }) }),
                        ("shape", new (string, object)[] { ("$ref", "_base_templates\\shape\\sphere"), ("radius", 7f) }),
                        ("line_of_sight", true), ("ignore_self", true), ("target_filter", "target_all"),
                        ("fall_off", new (string, object)[]
                        {
                            ("fall_off_entry", new (string, object)[] { ("distance", 0f), ("intensity_value", 1f) }),
                            ("fall_off_entry", new (string, object)[] { ("distance", 4.5f), ("intensity_value", 1f) }),
                            ("fall_off_entry", new (string, object)[] { ("distance", 7f), ("intensity_value", 0.15f) }),
                        }),
                    }) }) }),
                }),
            }) }),
        }) }) }) })));

    [Fact]
    public void Heavy_strikes_are_read_with_their_reactions_windows_and_the_stomp_blast()
    {
        var graph = new BehaviorGraph("MP_Test", [],
        [
            Attack("CK 1 Sequence"), Attack("Y1"), Attack("Y2"),
            new BehaviorSequencer("Y4", ["Stomp"], [new("Action", 0.45f, 0.45f, MeleeTuning.StompTag, null), Event("ACTN_Hit_Primary_Target", 0.4f), Branch("BRANCH_Melee_Draw_Run", 0.8f),
                Branch("BRANCH_HitReact", 0f, 0.3f), Branch("BRANCH_HitReact", 0.7f, 1f)], [1f]),
        ]);
        var t = MeleeTuning.From([graph], _ => 2f, Wargear(), Knife, ReactionTuning.ReadMapping(Mapper));
        Assert.Single(t.Steps);
        var x = t.Steps[0];
        Assert.Equal((ReactionKind.Interrupt, 1.5f, ReactionKind.None), (x.PrimaryReaction.Kind, x.PrimaryReaction.Backward, x.SecondaryReaction.Kind));   // mapped; a flinch is none
        Assert.Equal((0.3f, 0.5f), (x.NoReactFrom, x.NoReactTo));
        Assert.Equal(3, t.Heavy.Count);
        Assert.Equal(["A_3.5m", "A_3.5m", "Stomp"], t.HeavyAnimations);
        Assert.Equal((35f, ReactionKind.Stun, ReactionKind.Interrupt), (t.Heavy[0].Damage, t.Heavy[0].PrimaryReaction.Kind, t.Heavy[0].SecondaryReaction.Kind));
        Assert.Equal((40f, ReactionKind.Stagger), (t.Heavy[1].Damage, t.Heavy[1].PrimaryReaction.Kind));
        var stomp = t.Heavy[2];
        Assert.Equal((0.9f, 0f, 0f), (stomp.HitTime, stomp.Damage, stomp.SecondaryDamage));   // lands at the stomp action, deals only the blast
        Assert.Equal((0.6f, 1.4f), (stomp.NoReactFrom, stomp.NoReactTo));
        var blast = stomp.Stomp!;
        Assert.Equal((7f, 25f, 50f, true, true), (blast.Radius, blast.Damage.Damage, blast.Damage.ShieldDamage, blast.LineOfSight, blast.IgnoreSelf));
        Assert.Equal([new ReactionEntry(0.8f, ReactionKind.Stagger), new ReactionEntry(0.61f, ReactionKind.Stun), new ReactionEntry(0f, ReactionKind.Interrupt)], blast.Reactions);
        Assert.Null(t.Steps[0].Stomp);

        // A heavy strike that cannot be read ends the chain before it; the light combo stays.
        var broken = MeleeTuning.From([graph], _ => 2f, Wargear(), Knife with { HeavySequencers = ["Y1", "missing", "Y4"] }, null);
        Assert.Single(broken.Heavy);
        Assert.Contains(broken.Notes, n => n.Contains("heavy strike 2"));
    }
}
