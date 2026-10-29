using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;

namespace SmFormats.Tests;

/// <summary>Melee timing from behavior tags and wargear: tag choice, seconds, damage and multipliers, fallbacks, refusals.</summary>
public class MeleeTuningTests
{
    static readonly MeleeTuning.WeaponMap Weapon = new("knife", "MP_Test", ["CK 1 Sequence", "CK 2 Sequence"], ["ability_melee_ck1", "ability_melee_ck2"]);

    static BehaviorTag Branch(string name, float start, float end) => new("Branch", start, end, name, null);
    static BehaviorTag Event(string name, float at) => new("Event", at, at, name, null);

    static BehaviorSequencer Full(string name, params BehaviorTag[] extra) => new(name, ["A_1m", "A_3.5m", "A_7m"],
    [
        Event("ACTN_Hit_Primary_Target", 0.25f),
        Event("ACTN_Hit_Targets_Multiplayer", 0.2f),
        Branch("BRANCH_Melee_Combo", 0.4f, 0.6f),
        Branch("BRANCH_Melee_Combo", 0.7f, 0.8f),
        Branch("BRANCH_ToRanged", 0.3f, 1f),
        Branch("BRANCH_Melee_Run", 0.7f, 1f),
        Branch("BRANCH_Melee_Draw_Run", 0.65f, 1f),
        new("AttackDistance", 0f, 0.2f, null, 3.5f),
        .. extra,
    ], [1f, 2f, 1f]);

    /// <summary>A wargear file with two abilities: an upgraded entry listed first, then the default one.</summary>
    static BafDocument Wargear(float damage = 40f, float maxRange = 4f) => BafDocument.Read(SyntheticBafWriter.Build(
        ("ability_data", new (string, object)[]
        {
            ("ability_definitions", new (string, object)[]
            {
                ("ability_melee_ck1", Ability(damage, maxRange)),
                ("ability_melee_ck2", Ability(damage + 5, maxRange)),
            }),
        })));

    static (string, object)[] Ability(float damage, float maxRange) =>
    [
        ("primary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[] { ("actions_by_upgrade", new (string, object)[] { ("actions", new (string, object)[]
        {
            Entry(damage * 2, upgraded: true),
            Entry(damage, upgraded: false),
        }) }) }) }) }),
        ("secondary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[]
        {
            ("do_damage", new (string, object)[] { ("damage_general", damage / 2) }),
        }) }) }),
        ("ability_metadata", new (string, object)[] { ("generic_metadata", new (string, object)[] { ("max_range", maxRange) }) }),
    ];

    static (string, object) Entry(float damage, bool upgraded) => ("entry", new (string, object)[]
    {
        ("required_upgrade_name", new (string, object)[] { ("required_list", upgraded ? new (string, object)[] { ("upgrade", "x") } : Array.Empty<(string, object)>()) }),
        ("action", new (string, object)[] { ("commands", new (string, object)[] { ("do_damage", new (string, object)[]
        {
            ("damage_general", damage),
            ("damage_material_multipliers", new (string, object)[] { ("default_body", 1f) }),
            ("armourshield_damage_material_multipliers", new (string, object)[] { ("default_body", 0.75f) }),
        }) }) }),
    });

    static MeleeTuning Tune(params BehaviorSequencer[] sequencers) =>
        MeleeTuning.From([new BehaviorGraph("MP_Test", [], sequencers)], _ => 2f, Wargear(), Weapon);

    [Fact]
    public void Tags_become_seconds_with_the_chosen_clip_and_its_playback_speed()
    {
        var t = Tune(Full("CK 1 Sequence"), Full("CK 2 Sequence"));
        var s = t.Steps[0];
        // The 3.5 m variant plays at speed 2: 2 s of animation last 1 s.
        Assert.Equal("A_3.5m", t.Animations[0]);
        Assert.Equal(1f, s.Duration, 4);
        Assert.Equal(0.2f, s.HitTime, 4);            // the multiplayer hit event, not the primary-target one
        Assert.Equal((0.4f, 0.6f), (s.ComboOpen, s.ComboClose));   // the first combo window
        Assert.Equal(0.3f, s.RangedAt, 4);
        Assert.Equal(0.65f, s.MoveAt, 4);            // the earliest run branch
        Assert.Equal(3.5f, s.Lunge);
        Assert.Contains(t.Notes, n => n.Contains("2 combo windows"));
        Assert.Contains(t.Notes, n => n.Contains("playback speed 2"));
    }

    [Fact]
    public void Damage_multipliers_and_reach_come_from_the_default_wargear_entries()
    {
        var t = Tune(Full("CK 1 Sequence"), Full("CK 2 Sequence"));
        Assert.Equal((40f, 20f), (t.Steps[0].Damage, t.Steps[0].SecondaryDamage));   // not the upgraded 80
        Assert.Equal(45f, t.Steps[1].Damage);
        Assert.Equal((1f, 0.75f), (t.Steps[0].HealthMultiplier, t.Steps[0].ShieldMultiplier));
        Assert.Equal(4f, t.Range);
    }

    [Fact]
    public void Missing_tags_fall_back_and_a_missing_combo_window_means_no_combo()
    {
        var bare = new BehaviorSequencer("CK 1 Sequence", ["B"], [Event("ACTN_Hit_Primary_Target", 0.25f)], [1f]);
        var t = Tune(bare, Full("CK 2 Sequence"));
        var s = t.Steps[0];
        Assert.Equal(0.5f, s.HitTime, 4);            // falls back to the primary-target event
        Assert.Equal((0f, 0f), (s.ComboOpen, s.ComboClose));
        Assert.Equal((2f, 2f), (s.RangedAt, s.MoveAt));
        Assert.Contains(t.Notes, n => n.Contains("does not chain"));
        // Control: with the window present, the attack chains.
        Assert.True(Tune(Full("CK 1 Sequence"), Full("CK 2 Sequence")).Steps[0].ComboClose > 0f);
    }

    [Fact]
    public void Inconsistent_timing_is_refused()
    {
        // A combo window opening before the hit would let a chained attack skip the hit.
        var early = new BehaviorSequencer("CK 1 Sequence", ["B"], [Event("ACTN_Hit_Targets_Multiplayer", 0.5f), Branch("BRANCH_Melee_Combo", 0.2f, 0.6f)], [1f]);
        Assert.Throws<InvalidDataException>(() => Tune(early, Full("CK 2 Sequence")));
        Assert.Throws<InvalidDataException>(() => MeleeTuning.From([new BehaviorGraph("MP_Test", [], [Full("CK 1 Sequence"), Full("CK 2 Sequence")])], _ => float.NaN, Wargear(), Weapon));
        Assert.Throws<InvalidDataException>(() => Tune(Full("CK 1 Sequence")));   // a sequencer of the combo is missing
    }
}
