using SmContent;
using SmFormats.Baf;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// weapons/spread: <see cref="SpreadTuning"/> reads <c>kick_back_and_scatter_values</c> per stance and movement, picks the perk's entry when
/// its upgrade string is granted, matches charge stages to <see cref="ChargeSpec.Stages"/>, and reads the <c>climb_sway_scatter</c> blocks.
/// Synthetic files shaped like the install's bolter, stalker and lascannon (no original bytes).
/// </summary>
public class SpreadTuningTests
{
    const string Perk = "mp_perk_weap_bolter_accuracy";

    static (string, object)[] Entry(string set, string? upgrade = null) =>
    [
        ("$ref", "_base_templates\\climb_sway_scatter"),
        ("required_upgrade_name", new (string, object)[]
        {
            ("$ref", "_base_templates\\required_upgrade_name"),
            ("required_list", upgrade is null ? Array.Empty<(string, object)>() : new (string, object)[] { ("upgrade_name", upgrade) }),
        }),
        ("tuning_set", "climb_sway_scatter\\" + set),
    ];

    /// <summary>One movement table: the default entry, plus a perk entry when given (named "entry" or, as the stalker's burst one, by its template).</summary>
    static (string, object)[] Move(string set, string? perkSet = null, string upgrade = Perk, string perkName = "entry") =>
        perkSet is null ? [("entry", Entry(set))] : [("entry", Entry(set)), (perkName, Entry(perkSet, upgrade))];

    static (string, object)[] Kick(string hip, string aim, string? hipPerk = null, string? aimPerk = null, string? running = null) =>
    [
        ("standing", new (string, object)[] { ("idle", Move(hip, hipPerk)), ("walking", Move(hip, hipPerk)), ("running", Move(running ?? hip, hipPerk)) }),
        ("aiming", new (string, object)[] { ("idle", Move(aim, aimPerk)), ("walking", Move(aim, aimPerk)), ("running", Move(aim, aimPerk)) }),
        ("crouching", new (string, object)[] { ("idle", Move("crouch")) }),
    ];

    static BafDocument Weapon((string, object)[] abilities) =>
        BafDocument.Read(SyntheticBafWriter.Build(("ability_data", new (string, object)[] { ("ability_definitions", abilities) })));

    static BafDocument SetFile(float initial, float increase, float max, float climb = 0.3f) => BafDocument.Read(SyntheticBafWriter.Build(
        ("climb", new (string, object)[] { ("permanent_climb_angle_per_shot", 0f), ("temporary_climb_angle_per_shot", climb), ("max_temporary_climb_angle", 0.65f), ("climb_rate", 50), ("climb_recovery_rate", 1.5f) }),
        ("sway", new (string, object)[] { ("min_temporary_sway_angle_per_shot", 0f), ("max_temporary_sway_angle_per_shot", 0.1f), ("sway_rate", 25f), ("sway_recovery_rate", 3f), ("max_temporary_sway_angle", 0.5f) }),
        ("scatter", new (string, object)[]
        {
            ("initial_horizontal_angle", initial), ("initial_vertical_angle", initial), ("increase_per_shot", increase), ("max_vertical_angle", max),
            ("max_horizontal_angle", max), ("decrease_per_second", 2.4f), ("decrease_delay", 0.1f),
            ("target_speed_to_scatter_horizontal_angle", Array.Empty<(string, object)>()),
        })));

    static readonly Dictionary<string, BafDocument> Sets = new()
    {
        ["hip"] = SetFile(0.25f, 0.35f, 1.5f), ["aim"] = SetFile(0.075f, 0.3f, 1.5f), ["hip_perk"] = SetFile(0.125f, 0.21f, 1.5f),
        ["aim_perk"] = SetFile(0.03f, 0.17f, 1.5f), ["run"] = SetFile(1f, 0.35f, 3f), ["crouch"] = SetFile(9f, 9f, 9f),
        ["charged"] = SetFile(0.5f, 0f, 0.5f, climb: 2f), ["charged_aim"] = SetFile(0.1f, 0f, 0.1f),
    };

    static readonly BafDocument Bolter = Weapon(
    [
        ("ability_ranged_primary", new (string, object)[] { ("delivery", new (string, object)[] { ("kick_back_and_scatter_values", Kick("hip", "aim", "hip_perk", "aim_perk", "run")) }) }),
    ]);

    [Fact]
    public void Each_stance_and_movement_names_its_set_and_the_values_are_read()
    {
        var notes = new List<string>();
        var spec = SpreadTuning.Read("bolter", Bolter, Sets.GetValueOrDefault, notes)!;
        Assert.Empty(notes);
        Assert.Equal(["hip", "hip", "run", "aim", "aim", "aim"], spec.Basic.Sets.Select(s => s!.Name));
        var hip = spec.Basic.SetFor(SpreadStance.Standing, SpreadMovement.Idle)!;
        Assert.Equal(new ScatterSet(0.25f, 0.25f, 0.35f, 1.5f, 1.5f, 2.4f, 0.1f), hip.Scatter);
        Assert.Equal((0.3f, 0.65f, 50f, 1.5f, 0.1f, 0.5f), (hip.Handling.TemporaryClimbPerShot, hip.Handling.MaxTemporaryClimb, hip.Handling.ClimbRate,
            hip.Handling.ClimbRecoveryRate, hip.Handling.MaxTemporarySwayPerShot, hip.Handling.MaxTemporarySway));   // climb_rate stored as an int
        Assert.Empty(spec.Stages);
        Assert.Equal(["aim", "aim_perk", "crouch", "hip", "hip_perk", "run"], SpreadTuning.SetNames(Bolter).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_granted_perk_picks_its_entry_and_becomes_a_variant()
    {
        using (UpgradeScope.Grant([Perk]))
        {
            var perk = SpreadTuning.Read("bolter", Bolter, Sets.GetValueOrDefault, [])!;
            Assert.Equal(["hip_perk", "hip_perk", "hip_perk", "aim_perk", "aim_perk", "aim_perk"], perk.Basic.Sets.Select(s => s!.Name));
        }
        var gun = new WeaponSpec("bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 22f, 0.155f, 30, 270, 1.5f, 250f, 45f, 70f, 0.8f, 2.55f, 1f, 0.5f, 0.505f);
        var spec = SpreadTuning.With(gun, Bolter, Sets.GetValueOrDefault, []);
        var variants = UpgradeScope.VariantsOf(spec, [Perk, "mp_perk_unrelated"], () => SpreadTuning.With(gun, Bolter, Sets.GetValueOrDefault, []), Variants.Bytes);
        var only = Assert.Single(variants);
        Assert.Equal([Perk], only.Upgrades);
        Assert.Equal(0.125f, only.Spec.Spread!.Basic.SetFor(SpreadStance.Standing, SpreadMovement.Idle)!.Scatter.InitialHorizontal);
        Assert.Null(SpreadTuning.With(gun, Bolter, null, []).Spread);   // no sets given: no spread (tests, old callers)
    }

    [Fact]
    public void Charge_stages_line_up_with_the_charge_spec_and_a_pump_ability_is_read()
    {
        static (string, object)[] Stage(string name, bool shot) => shot
            ? [("$ref", "wargear\\charge_state"), ("minimum_charge_time", 1.5f), ("delivery", new (string, object)[] { ("kick_back_and_scatter_values", Kick("charged", "charged_aim")) })]
            : [("$ref", "wargear\\charge_state"), ("minimum_charge_time", 0f)];
        var lascannon = Weapon(
        [
            ("ability_ranged_primary", new (string, object)[] { ("delivery", new (string, object)[] { ("kick_back_and_scatter_values", Kick("hip", "aim")) }) }),
            ("ability_ranged_secondary", new (string, object)[]
            {
                ("$ref", "abilities\\charge_ranged"),
                ("charge_states", new (string, object)[]
                {
                    ("1.charge_light", Stage("light", false)),
                    ("2.charge_dead_zone", new (string, object)[] { ("$ref", "") }),   // an unused slot: skipped as WeaponTuning skips it
                    ("3.charge_medium", Stage("medium", true)),
                }),
            }),
        ]);
        var spec = SpreadTuning.Read("lascannon", lascannon, Sets.GetValueOrDefault, [])!;
        Assert.Equal(2, spec.Stages.Count);
        Assert.Null(spec.Stages[0]);
        Assert.Equal("charged", spec.For(2).SetFor(SpreadStance.Standing, SpreadMovement.Idle)!.Name);
        Assert.Equal("charged_aim", spec.For(2).SetFor(SpreadStance.Aiming, SpreadMovement.Running)!.Name);
        Assert.Equal("hip", spec.For(1).SetFor(SpreadStance.Standing, SpreadMovement.Idle)!.Name);   // a stage without sets uses the basic shot's

        var melta = Weapon([("ability_ranged_primary", new (string, object)[]
        {
            ("pump_states", new (string, object)[] { ("wargear\\ranged_pump_state", new (string, object)[] { ("delivery", new (string, object)[] { ("kick_back_and_scatter_values", Kick("hip", "hip")) }) }) }),
        })]);
        Assert.Equal("hip", SpreadTuning.Read("melta", melta, Sets.GetValueOrDefault, [])!.Basic.SetFor(SpreadStance.Aiming, SpreadMovement.Idle)!.Name);
    }

    [Fact]
    public void A_set_without_its_scatter_values_is_noted_once_not_read_silently_as_zero()
    {
        var notes = new List<string>();
        var noBlock = BafDocument.Read(SyntheticBafWriter.Build(("climb", new (string, object)[] { ("climb_rate", 50f) })));
        var set = SpreadTuning.ReadSet("broken", noBlock.Root, notes);
        SpreadTuning.ReadSet("broken", noBlock.Root, notes);
        Assert.Equal(ScatterSet.None, set.Scatter);
        Assert.Single(notes, n => n.Contains("broken") && n.Contains("no scatter block"));
        var noInitial = BafDocument.Read(SyntheticBafWriter.Build(("scatter", new (string, object)[] { ("max_horizontal_angle", 1.5f) })));
        SpreadTuning.ReadSet("partial", noInitial.Root, notes);
        Assert.Contains(notes, n => n.Contains("partial") && n.Contains("initial_horizontal_angle"));
        notes.Clear();
        SpreadTuning.ReadSet("hip", Sets["hip"].Root, notes);
        Assert.Empty(notes);
    }

    [Fact]
    public void A_missing_set_is_noted_and_a_weapon_without_kick_back_has_no_spread()
    {
        var notes = new List<string>();
        var spec = SpreadTuning.Read("bolter", Bolter, n => n == "hip" ? Sets[n] : null, notes)!;
        Assert.Contains(notes, n => n.Contains("aim"));
        Assert.Equal("hip", spec.Basic.SetFor(SpreadStance.Aiming, SpreadMovement.Idle)!.Name);   // aim missing: the standing set of that movement
        var bare = Weapon([("ability_ranged_primary", new (string, object)[] { ("delivery", new (string, object)[] { ("max_range", 250f) }) })]);
        Assert.Null(SpreadTuning.Read("bare", bare, Sets.GetValueOrDefault, []));
    }
}
