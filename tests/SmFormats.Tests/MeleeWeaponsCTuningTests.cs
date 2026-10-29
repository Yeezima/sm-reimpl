using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// Weapons C, the readers: the power axe's combo windows (<c>BRANCH_Melee_Combo_MISS</c>) and armour-shield damage, the string end and the
/// light-to-heavy targets, the Y,Y finishers (appended heavy strikes, <c>BRANCH_Melee_Y_Combo</c> / <c>BRANCH_To_Slam</c> windows) with their
/// <c>DISPLACE_*</c> blasts (any triggered action of the tag's name; the co-op copies skipped), the slams' reaction immunity from their
/// ability's start actions, the hammer's 200 per swing, the kick-and-stomp map, Fast Stomp's graph speed while the perk is granted, the
/// middle clip of three without a distance name, and the real maps' shapes. Synthetic graphs and wargear only.
/// </summary>
public class MeleeWeaponsCTuningTests
{
    static BehaviorTag Branch(string name, float start, float end) => new("Branch", start, end, name, null);
    static BehaviorTag Event(string name, float at) => new("Event", at, at, name, null);
    static BehaviorTag Action(string name, float at) => new("Action", at, at, name, null);

    /// <summary>An attack: hit at 0.2, the given combo branch 0.4–0.6, fire from 0.3, moves from 0.65.</summary>
    static BehaviorSequencer Attack(string name, string combo = "BRANCH_Melee_Combo", params BehaviorTag[] extra) => new(name, ["A_1m", "A_3.5m", "A_7m"],
    [
        Event("ACTN_Hit_Targets_Multiplayer", 0.2f), Branch(combo, 0.4f, 0.6f), Branch("BRANCH_ToRanged", 0.3f, 1f), Branch("BRANCH_Melee_Run", 0.65f, 1f), .. extra,
    ], [1f, 1f, 1f]);

    static (string, object) Damage(float general, float shield) => ("do_damage", new (string, object)[]
    {
        ("damage_general", general), ("damage_armourshield", shield),
        ("damage_material_multipliers", new (string, object)[] { ("default_body", 1f) }),
        ("armourshield_damage_material_multipliers", new (string, object)[] { ("default_body", 1f) }),
    });

    static (string, object)[] Ability(float damage, float shield = 0f) =>
    [
        ("primary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[] { Damage(damage, shield) }) }) }),
        ("secondary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[] { Damage(damage / 2, shield / 2) }) }) }),
        ("ability_metadata", new (string, object)[] { ("generic_metadata", new (string, object)[] { ("max_range", 4f) }) }),
    ];

    /// <summary>An ability without damage that applies <c>mp_reaction_immunity</c> when it starts (the slams' <c>ability_displace_slam</c>).</summary>
    static (string, object)[] Slam() =>
    [
        ("primary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", Array.Empty<(string, object)>()) }) }),
        ("start_actions", new (string, object)[] { ("start_sim", new (string, object)[] { ("actions", new (string, object)[]
        {
            ("apply_upgrade_string", new (string, object)[] { ("lifetime", 0f), ("strings", new (string, object)[] { ("entry", "mp_reaction_immunity") }) }),
        }) }) }),
    ];

    static (string, object) Blast(string action, float radius, float damage, float shield) => (action, new (string, object)[] { ("commands", new (string, object)[]
    {
        ("area_of_effect", new (string, object)[]
        {
            ("hit_actions", new (string, object)[] { ("commands", new (string, object)[] { ("do_damage", new (string, object)[] { ("damage_general", damage), ("damage_armourshield", shield) }) }) }),
            ("shape", new (string, object)[] { ("radius", radius) }),
            ("ignore_self", true), ("target_filter", "target_all"),
        }),
    }) });

    static BafDocument Wargear(params (string, object)[] abilities) => BafDocument.Read(SyntheticBafWriter.Build(
        ("ability_data", new (string, object)[] { ("ability_definitions", abilities) }),
        ("action_data", new (string, object)[] { ("triggered_actions", new (string, object)[]
        {
            Blast("displace_ax_slam", 7f, 50f, 50f), Blast("displace_quickstomp", 5f, 25f, 25f), Blast("displace_hammerslam", 7f, 100f, 125f),
            Blast("displace_footstomp", 7f, 25f, 50f), Blast("displace_ax_slam_coop", 3f, 1f, 1f),
            ("displace_kick", new (string, object)[] { ("commands", new (string, object)[] { ("play_fx", new (string, object)[] { ("fx_name", "kick") }) }) }),   // effects only
        }) })));

    static BehaviorSequencer Finisher(string name, string tag, float at) => new(name, ["F_3.5m"],
    [
        Action(tag + "_Coop", at - 0.05f), Action(tag, at), Branch("BRANCH_Melee_Run", 0.8f, 1f), Branch("BRANCH_ToRanged", 0.7f, 1f),
    ], [1f]);

    [Fact]
    public void An_axe_style_combo_reads_miss_windows_armour_damage_the_string_end_and_a_y_chained_slam()
    {
        var map = new MeleeTuning.WeaponMap("axe", "MP_Axe", ["X1", "X2"], ["ability_melee_x1", "ability_melee_x2"])
        {
            HeavySequencers = ["Y1", "Y2"], HeavyAbilities = ["ability_melee_y", "ability_melee_y2"],
            ComboBranches = ["BRANCH_Melee_Combo", "BRANCH_Melee_Combo_MISS"], LoopLights = false, LightHeavyNext = [0, -1],
            FinisherSequencers = ["YY"], FinisherAbilities = [""], YNext = [3, 3, 0], ExtendedRules = true,
        };
        var graph = new BehaviorGraph("MP_Axe", [],
        [
            Attack("X1", "BRANCH_Melee_Combo_MISS"), Attack("X2", "BRANCH_Melee_Combo_MISS"),
            Attack("Y1", "BRANCH_Melee_Combo", Branch("BRANCH_Melee_Y_Combo", 0.5f, 0.8f)), Attack("Y2", "BRANCH_Melee_Combo"),
            Finisher("YY", "DISPLACE_AX_Slam", 0.4f),
        ]);
        var wargear = Wargear(("ability_melee_x1", Ability(70f, 250f)), ("ability_melee_x2", Ability(90f, 250f)), ("ability_melee_y", Ability(25f)), ("ability_melee_y2", Ability(35f)));
        var t = MeleeTuning.From([graph], _ => 2f, wargear, map);

        Assert.Equal(2, t.Steps.Count);
        var x1 = t.Steps[0];
        Assert.Equal((0.8f, 1.2f), (x1.ComboOpen, x1.ComboClose));                        // the MISS window (2 s clip)
        Assert.Equal((70f, 250f, 35f, 125f), (x1.Damage, x1.ShieldDamage, x1.SecondaryDamage, x1.SecondaryShieldDamage));
        Assert.Equal((false, 0), (x1.StringEnd, x1.HeavyNext));
        Assert.Equal((true, -1), (t.Steps[1].StringEnd, t.Steps[1].HeavyNext));          // X2: no light or heavy transition out
        Assert.Equal(3, t.Heavy.Count);
        Assert.Equal((3, 1f, 1.6f), (t.Heavy[0].YNext, t.Heavy[0].YComboOpen, t.Heavy[0].YComboClose));
        Assert.Equal((0, 0f, 0f), (t.Heavy[1].YNext, t.Heavy[1].YComboOpen, t.Heavy[1].YComboClose));   // YNext 3 asked, but no Y window: dropped
        Assert.Contains(t.Notes, n => n.Contains("Y2") && n.Contains("does not chain into a finisher"));
        var slam = t.Heavy[2];
        Assert.Equal((0.8f, 0f), (slam.HitTime, slam.Damage));                                 // lands at DISPLACE_AX_Slam, not at its co-op copy
        Assert.Equal((7f, 50f, 50f), (slam.Stomp!.Radius, slam.Stomp.Damage.Damage, slam.Stomp.Damage.ShieldDamage));
        Assert.Equal("F_3.5m", t.HeavyAnimations[2]);
        Assert.Equal("A_3.5m", t.Animations[0]);
    }

    [Fact]
    public void A_hammer_style_combo_deals_200_and_its_slams_take_their_blast_and_reaction_immunity_from_the_slam_ability()
    {
        var map = new MeleeTuning.WeaponMap("hammer", "MP_Hammer", ["X1"], ["ability_melee_x1"])
        {
            HeavySequencers = ["Y1"], HeavyAbilities = ["ability_melee_y"],
            FinisherSequencers = ["Slam"], FinisherAbilities = ["ability_displace_slam"], YNext = [2, 0], YComboBranches = ["BRANCH_To_Slam"], ExtendedRules = true,
        };
        var y1 = new BehaviorSequencer("Y1", ["Ytap_1m", "Ytap", "Ytap_7m"],
            [Event("ACTN_Hit_Targets_Multiplayer", 0.26f), Branch("BRANCH_To_Slam", 0.4f, 0.6f), Branch("BRANCH_Melee_Y_Combo", 0.1f, 0.2f)], [1f, 1.25f, 1f]);
        var graph = new BehaviorGraph("MP_Hammer", [], [Attack("X1"), y1, Finisher("Slam", "DISPLACE_HammerSlam", 0.5f)]);
        var wargear = Wargear(("ability_melee_x1", Ability(200f)), ("ability_melee_y", Ability(25f)), ("ability_displace_slam", Slam()));
        var t = MeleeTuning.From([graph], _ => 2f, wargear, map);

        Assert.Equal((200f, 100f, 0f), (t.Steps[0].Damage, t.Steps[0].SecondaryDamage, t.Steps[0].ShieldDamage));
        Assert.Equal("Ytap", t.HeavyAnimations[0]);                                              // the middle of three without a distance name …
        Assert.Equal(1.6f, t.Heavy[0].Duration, 4);                                              // … at its own playback speed
        Assert.Equal((2, 0.64f, 0.96f), (t.Heavy[0].YNext, MathF.Round(t.Heavy[0].YComboOpen, 4), MathF.Round(t.Heavy[0].YComboClose, 4)));   // BRANCH_To_Slam, not the Y-combo branch
        var slam = t.Heavy[1];
        Assert.Equal((1f, 0f, 2f), (slam.HitTime, slam.NoReactFrom, slam.NoReactTo));         // at the slam action; immune for the whole strike
        Assert.Equal((100f, 125f), (slam.Stomp!.Damage.Damage, slam.Stomp.Damage.ShieldDamage));
        Assert.Equal((0f, 0f), (t.Heavy[0].NoReactFrom, t.Heavy[0].NoReactTo));               // a strike without the immunity keeps its HitReact window (none here)
    }

    [Fact]
    public void The_kick_and_stomp_map_reads_the_kick_as_a_string_end_that_chains_to_the_stomp_and_fast_stomp_speeds_the_stomp_up()
    {
        var kick = new BehaviorSequencer("Sequencer13", ["Kick_1m", "Kick_3.5m", "Kick_7m"],
        [
            Event("ACTN_Hit_Primary_Target", 0.2f), Event("ACTN_Hit_Targets_Multiplayer", 0.22f), Branch("BRANCH_Melee_Combo", 0.34f, 0.6f),
            Branch("BRANCH_ToRanged", 0.5f, 1f), Branch("BRANCH_Melee_Run", 0.6f, 1f), Action("DISPLACE_Kick", 0.18f), new("AttackDistance", 0f, 0.16f, null, 2f),
        ], [1f, 1f, 1f]);
        var stomp = new BehaviorSequencer("Sequencer15a", ["Stomp_3.5m"],
        [
            Event("ACTN_Hit_Targets_Multiplayer", 0.46f), Action("DISPLACE_FootStomp", 0.46f), Action("DISPLACE_Footstomp_Coop", 0.48f),
            Branch("BRANCH_ToRanged", 0.54f, 1f), Branch("BRANCH_Melee_Run", 0.8f, 1f), Branch("BRANCH_HitReact", 0f, 1f),
        ], [1f]);
        var graph = new BehaviorGraph(MeleeTuning.Displace.Graph, [], [kick, stomp]);
        var wargear = Wargear(("ability_displace_kick", Ability(30f)), ("ability_displace_slam", Slam()));
        IReadOnlyList<MeleeTuning.SpeedUpgrade> speeds = [new(MeleeTuning.Displace.Graph, "Stomp_3.5m", "mp_perk_speed_increase", 1.5f)];
        var t = MeleeTuning.From([graph], _ => 1.5f, wargear, MeleeTuning.Displace, null, speeds);

        var k = Assert.Single(t.Steps);
        Assert.Equal((true, 1, 30f, 2f), (k.StringEnd, k.HeavyNext, k.Damage, k.Lunge));
        Assert.Null(k.Stomp);                                                                     // displace_kick has no blast
        Assert.Equal(0.33f, k.HitTime, 4);
        var s = Assert.Single(t.Heavy);
        Assert.Equal((1.5f, 0.69f, 0f, 1.5f), (s.Duration, MathF.Round(s.HitTime, 4), s.NoReactFrom, s.NoReactTo));
        Assert.Equal((7f, 25f, 50f), (s.Stomp!.Radius, s.Stomp.Damage.Damage, s.Stomp.Damage.ShieldDamage));

        // Fast Stomp: the graph's speed only while the variant read grants the perk's upgrade.
        MeleeTuning fast;
        using (UpgradeScope.Grant(["mp_perk_speed_increase"])) fast = MeleeTuning.From([graph], _ => 1.5f, wargear, MeleeTuning.Displace, null, speeds);
        Assert.Equal((1f, 0.46f), (fast.Heavy[0].Duration, MathF.Round(fast.Heavy[0].HitTime, 4)));
        Assert.Equal(1.5f, fast.Steps[0].Duration);                                               // the kick's clip is not bound
        using (UpgradeScope.Grant(["mp_perk_other"])) Assert.Equal(1.5f, MeleeTuning.From([graph], _ => 1.5f, wargear, MeleeTuning.Displace, null, speeds).Heavy[0].Duration);
    }

    [Fact]
    public void A_malformed_Y_window_drops_that_chain_with_a_note_and_one_ending_late_is_clamped_and_the_config_still_loads()
    {
        var map = new MeleeTuning.WeaponMap("axe", "MP_Axe", ["X1"], ["ability_melee_x1"])
        {
            HeavySequencers = ["Y1", "Y2"], HeavyAbilities = ["ability_melee_y", "ability_melee_y2"],
            FinisherSequencers = ["YY"], FinisherAbilities = [""], YNext = [3, 3, 0], ExtendedRules = true,
        };
        var graph = new BehaviorGraph("MP_Axe", [],
        [
            Attack("X1"),
            Attack("Y1", "BRANCH_Melee_Combo", Branch("BRANCH_Melee_Y_Combo", 0.8f, 0.5f)),    // opens after it closes
            Attack("Y2", "BRANCH_Melee_Combo", Branch("BRANCH_Melee_Y_Combo", 0.5f, 1.4f)),    // ends after the strike
            Finisher("YY", "DISPLACE_AX_Slam", 0.4f),
        ]);
        var wargear = Wargear(("ability_melee_x1", Ability(70f, 250f)), ("ability_melee_y", Ability(25f)), ("ability_melee_y2", Ability(35f)));
        var t = MeleeTuning.From([graph], _ => 2f, wargear, map);

        Assert.Equal((0, 0f, 0f), (t.Heavy[0].YNext, t.Heavy[0].YComboOpen, t.Heavy[0].YComboClose));
        Assert.Contains(t.Notes, n => n.Contains("Y1") && n.Contains("not usable"));
        Assert.Equal((3, 1f, 2f), (t.Heavy[1].YNext, t.Heavy[1].YComboOpen, t.Heavy[1].YComboClose));   // clamped to the 2 s strike
        Assert.Contains(t.Notes, n => n.Contains("Y2") && n.Contains("clamped"));
        Assert.Equal(3, t.Heavy.Count);
        // The combo passes the config check: the server starts.
        var combo = new MeleeCombo("axe", t.Steps) { Heavy = t.Heavy };
        var loadout = new ClassLoadout("assault", -1, -1, t.Steps) { MeleeHeavy = t.Heavy, MeleeChoices = [combo] };
        Assert.NotNull(GameplayTuning.WithClasses(new SimConfig(), [], [loadout]));
    }

    [Fact]
    public void The_new_maps_name_the_graph_states_the_audit_read_off_the_install()
    {
        Assert.Equal(("MP_SM_MEL_PowerSword", 4, 5), (MeleeTuning.PowerSword.Graph, MeleeTuning.PowerSword.Sequencers.Length,
            MeleeTuning.PowerSword.HeavySequencers.Length + MeleeTuning.PowerSword.FinisherSequencers.Length));
        Assert.Equal([0, 0, 0, -1], MeleeTuning.PowerSword.LightHeavyNext);
        Assert.Equal(["BRANCH_Melee_Combo", "BRANCH_Melee_Combo_MISS"], MeleeTuning.PowerAxe.ComboBranches);
        Assert.Equal(["BRANCH_To_Slam"], MeleeTuning.ThunderHammer.YComboBranches);
        Assert.Equal([4, 4, 5, 0, 0], MeleeTuning.ThunderHammer.YNext);
        Assert.Equal(("sm_mp_core_abil_heavy_evade", "MP_SM_MEL_TB_Displace"), (MeleeTuning.Displace.Wargear, MeleeTuning.Displace.Graph));
        Assert.Equal([1], MeleeTuning.Displace.LightHeavyNext);
        // Every finisher target is in its map's heavy list.
        foreach (var m in new[] { MeleeTuning.PowerSword, MeleeTuning.PowerAxe, MeleeTuning.ThunderHammer })
            Assert.All(m.YNext, y => Assert.InRange(y, 0, m.HeavySequencers.Length + m.FinisherSequencers.Length));
        // The customizer and the loader share one list: the new weapons and both races' twins are simulated.
        foreach (var name in new[] { "sm_mp_melee_powersword", "csm_mp_melee_powersword", "sm_mp_melee_power_axe", "csm_mp_melee_chain_axe",
                     "sm_mp_melee_2hand_thunder_hammer", "csm_mp_melee_2hand_power_mace" })
            Assert.Contains(MpClasses.SimulatedMeleeSuffixes, s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(MpClasses.SimulatedMeleeSuffixes, s => s.Contains("heavy_evade"));   // a core ability, not a melee slot item
    }
}
