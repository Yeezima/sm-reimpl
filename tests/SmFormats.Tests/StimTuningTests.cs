using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// perks/inert-2: combat stims read from a synthetic wargear file in the install's layout (no original bytes): the count by upgrade, the activation
/// spacing, the triggered action's modifiers and immunity by upgrade, and the use's timing from a synthetic player graph.
/// </summary>
public class StimTuningTests
{
    const string Armory = "mp_perk_equipment_double", Master = "mp_perk_equipment_effectiveness";

    static (string, object) N(string name, params (string, object)[] items) => (name, items);
    static (string, object) Required(string? upgrade) => N("required_upgrade_name", upgrade is null ? N("required_list") : N("required_list", ("upgrade_name", upgrade)));
    static (string, object) Modifier(string type, float amount, bool exclusive = false) =>
        N("modifier", ("exclusive", exclusive), ("modifier_type", type), ("operation_type", "multiplication"), ("amount", amount));
    static (string, object) Entry(string? upgrade, float lifetime, float ranged, float melee, float explosive, float meleeDamage) => N("entry", Required(upgrade),
        N("action", N("commands",
            N("apply_modifiers", N("modifiers",
                Modifier("received_world_damage_health_only", 0.75f), Modifier("received_ranged_damage_health_only", ranged),
                Modifier("received_melee_damage_health_only", melee), Modifier("received_explosive_damage_health_only", explosive),
                Modifier("melee_damage", meleeDamage, exclusive: upgrade is not null)), ("lifetime", lifetime), ("stop_policy", "wait_life_time")),
            N("apply_upgrade_string", ("lifetime", lifetime), N("strings", ("entry", "mp_reaction_immunity")), ("remove_upgrade_on_deactivation", true)))));
    static (string, object) Ammo(string? upgrade, int count) => N("wargear\\ammunition_info", Required(upgrade), ("capacity_clip", count), ("initial_clip_amount", -1));

    static BafDocument Stimpack() => BafDocument.Read(SyntheticBafWriter.Build(
        N("common_data", ("slot", "equipment"), N("ammunition", N("entry", ("ammunition_type", "stimpack"), N("by_upgrade", Ammo(null, 1), Ammo(Armory, 2))))),
        N("ability_data", N("ability_definitions", N("ability_stimpack", ("min_time_between_activations", 6f), ("duration", 6f), ("regen_multiplier", 1f)))),
        N("action_data", N("triggered_actions", N("actn_stims_activated", N("commands",
            N("actions_by_upgrade", N("actions", Entry(null, 5f, 0.75f, 0.5f, 0.75f, 1.35f), Entry(Master, 7f, 0.6f, 0.35f, 0.6f, 2f))))))),
        N("ui_data", ("visible_in_customizer", true))));

    static BafDocument Perk(string upgrade) => BafDocument.Read(SyntheticBafWriter.Build(
        N("common_data", ("slot", "perk"), N("unlock_info", N("level_requirement", ("level_required", 1)))),
        N("action_data", N("lifetime_actions", N("commands", N("apply_upgrade_string", N("strings", ("entry", upgrade))))))));

    /// <summary>The player graph's stim sequencer: the release event at 0.2 and the end branch at 0.6 of a 1.5 s clip.</summary>
    static readonly BehaviorGraph Graph = new(StimTuning.Graph, [], [new BehaviorSequencer("SequencerGenerator00", ["SM_RNG_Idle_Bolter_Reload_UB_S_01"],
        [new BehaviorTag("Branch", 0.6f, 1f, StimTuning.EndBranch, null), new BehaviorTag("Event", 0.2f, 0.2f, StimTuning.ReleaseEvent, null)], [1f])]);

    static MpClasses Build(IReadOnlyList<BehaviorGraph> graphs, List<string> notes)
    {
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["sm_mp_eq_stimpack"] = Stimpack(), [Armory] = Perk(Armory), [Master] = Perk(Master),
        };
        var classes = new[] { new MpClass("sm - tactical", "tactical", "space_marine", [], ["sm_mp_eq_stimpack"], [Armory, Master], Armory) };
        return MpClasses.Build(classes, wargear, graphs, _ => 1.5f, notes);
    }

    [Fact]
    public void The_stim_reads_its_count_spacing_modifiers_immunity_and_timing()
    {
        var notes = new List<string>();
        var built = Build([Graph], notes);
        var stim = Assert.Single(built.Thrown);
        Assert.Equal(("sm_mp_eq_stimpack", 1, 1, 6f), (stim.Name, stim.Count, stim.SpawnCount, stim.Cooldown));
        Assert.Same(ThrownSpec.NoProjectile, stim.Projectile);
        var s = stim.Stim!;
        Assert.Equal(5f, s.Lifetime);
        Assert.Equal(0.3f, s.UseRelease, 4);
        Assert.Equal(0.9f, s.UseDuration, 4);
        Assert.Equal((0.75f, 0.75f, 0.5f, 0.75f, 1.35f, true), (s.ReceivedWorld, s.ReceivedRanged, s.ReceivedMelee, s.ReceivedExplosive, s.MeleeDamage, s.ReactionImmune));
        Assert.Equal("SM_RNG_Idle_Bolter_Reload_UB_S_01", s.Animation);
        // The perks' variants: Master-Crafted's entry, Favor of the Armory's count, and both.
        var master = stim.With(new PerkUpgrades(Master, null));
        Assert.Equal((1, 7f, 0.6f, 0.35f, 0.6f, 2f), (master.Count, master.Stim!.Lifetime, master.Stim.ReceivedRanged, master.Stim.ReceivedMelee, master.Stim.ReceivedExplosive, master.Stim.MeleeDamage));
        Assert.Equal((2, 5f), (stim.With(new PerkUpgrades(Armory, null)).Count, stim.With(new PerkUpgrades(Armory, null)).Stim!.Lifetime));
        var both = stim.With(new PerkUpgrades(Armory, Master));
        Assert.Equal((2, 7f), (both.Count, both.Stim!.Lifetime));
        Assert.DoesNotContain(notes, n => n.Contains("stim", StringComparison.OrdinalIgnoreCase) && n.Contains("not simulated"));
        // The class offers it.
        Assert.Equal([0], built.Loadouts[0].EquipmentChoices);
    }

    [Fact]
    public void Without_the_graph_s_timing_the_stim_is_not_offered_and_a_note_says_why()
    {
        var notes = new List<string>();
        var built = Build([], notes);
        Assert.Empty(built.Thrown);
        Assert.Contains(notes, n => n.StartsWith("stims:") && n.Contains("not simulated"));
    }

    [Fact]
    public void A_stim_passes_the_config_checks_and_a_broken_one_does_not()
    {
        var notes = new List<string>();
        var built = Build([Graph], notes);
        var classes = built.Loadouts;
        Assert.NotNull(GameplayTuning.WithClasses(new SimConfig(), [], classes, built.Thrown, perks: built.Perks));
        var broken = built.Thrown[0] with { Stim = built.Thrown[0].Stim! with { Lifetime = float.NaN }, Variants = [] };
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [], classes, [broken], perks: built.Perks));
        Assert.True(StimTuning.IsStim(Stimpack()));
    }
}
