using SmContent;
using SmContent.Extras.Musou;
using SmFormats.Baf;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// Musou Dreadnought D0 (docs/musou-dreadnought-design.md): <see cref="DreadnoughtData"/> reads the body, the weapons, the arm's strikes, the areas, the sprint, the buff and the entrance from files shaped like the
/// install's. The parsing tests use synthetic files with made-up values (no original bytes); the parity test reads the owner's install and pins each number the design uses (skipped without it, never in CI).
/// </summary>
public class DreadnoughtDataTests
{
    /// <summary>The leaves <paramref name="leaves"/> under the nested tables of <paramref name="path"/> (a root entry; the file may hold the same table name more than once, the reader walks them all).</summary>
    static (string, object) At(string path, params (string, object)[] leaves)
    {
        var parts = path.Split('/');
        (string, object) node = (parts[^1], leaves);
        for (int i = parts.Length - 2; i >= 0; i--) node = (parts[i], new (string, object)[] { node });
        return node;
    }

    static (string, object) V(string name, float value) => (name, value);

    static BafDocument Doc(params (string, object)[] root) => BafDocument.Read(SyntheticBafWriter.Build(root));

    const string Pump = "ability_data/ability_definitions/ability_ranged_primary/pump_states/wargear\\ranged_pump_state/delivery";

    static (string, object)[] AreaRows(string under, float damage, float armour, float stun, float radius, float debris) =>
    [
        At(under + "/commands/prop_debris_force/shape", V("radius", debris)),
        At(under + "/commands/area_of_effect/hit_actions/commands/do_damage", V("damage_general", damage), V("damage_armourshield", armour)),
        At(under + "/commands/area_of_effect/hit_actions/commands/apply_stun_damage", V("stun_damage", stun)),
        At(under + "/commands/area_of_effect/shape", V("radius", radius)),
    ];

    static IEnumerable<(string, object)> HitRows(string under, float damage, float armour, float? stun)
    {
        yield return At(under + "/hit_sim/actions/do_damage", V("damage_general", damage), V("damage_armourshield", armour));
        if (stun is not null) yield return At(under + "/hit_sim/actions/apply_stun_damage", V("stun_damage", stun.Value));
    }

    static BafDocument Blueprint() => Doc(
    [
        At("extensions/entity_ext", ("unit_size", "really_big")),
        At("extensions/health_ext", V("health_max", 7777f), V("health_regen_delay", 4f), V("health_regen_rate", 111f), V("death_time", 9f)),
        At("extensions/health_ext/take_damage_multiplier", V("melee", 1f), V("ranged", 2f), V("explosive", 3f), V("world", 4f)),
        At("extensions/health_ext/armourshield_info/armourshield_info", V("armourshield_max", 12f)),
        At("extensions/biped_locomotion_ext/speed_standing", V("run_speed", 6f), V("strafe_speed", 6.25f), V("back_speed", 4f), V("sprint_speed", 12f), V("walk_speed", 2.5f)),
        At("extensions/biped_locomotion_ext/speed_crouching", V("run_speed", 5f), V("strafe_speed", 4f), V("back_speed", 3.5f), V("sprint_speed", 9f), V("walk_speed", 3.6f)),
        At("extensions/biped_locomotion_ext/speed_aiming", V("run_speed", 4.5f), V("strafe_speed", 4.75f), V("back_speed", 3.75f), V("sprint_speed", 11f), V("walk_speed", 3f)),
        At("extensions/biped_locomotion_ext", V("rotation_max_degrees_per_second", 300f), V("ground_time_till_max_speed", 0.7f), V("max_walkable_slope", 55f)),
        At("extensions/animated_character_ext/animation", V("hit_phantom_radius", 1.5f), V("hit_phantom_length", 2.5f)),
        At("extensions/weapon_ext", V("aim_horizontal_speed", 70f), V("aim_vertical_speed", 700f)),
        At("extensions/entity_actions_ext/triggered_actions/evt_spawn_in/commands/apply_modifiers", V("lifetime", 6f)),
        At("extensions/entity_actions_ext/triggered_actions/evt_spawn_in/commands/apply_modifiers/modifiers/modifier", ("modifier_type", "received_melee_damage"), V("amount", 0.5f)),
        At("extensions/entity_actions_ext/triggered_actions/evt_dread_cap/commands/target_self_action/action/commands/do_heal", V("health", 1234f)),
        .. AreaRows("extensions/entity_actions_ext/triggered_actions/evt_spawn_r_stomp", 20f, 21f, 900f, 9f, 4f),
        .. AreaRows("extensions/entity_actions_ext/triggered_actions/evt_spawn_l_stomp", 20f, 21f, 900f, 9f, 4f),
    ]);

    static BafDocument Cannon() => Doc(
        At("ability_data/ability_definitions/ability_ranged_primary/delivery", V("max_range", 240f)),
        At("ability_data/ability_definitions/ability_ranged_primary/delivery/hit_sim/actions/do_damage", V("damage_general", 31f), V("damage_armourshield", 6f)),
        At("ability_data/ability_definitions/ability_ranged_primary/delivery/fire_sim/actions/apply_heat", ("heat_name", "heat_x")),
        At("ability_data/ability_definitions/ability_ranged_primary/delivery/fire_sim/actions/apply_heat/heat_generated/entry", V("heat_to_generate", 0.25f)),
        At("ability_data/ability_definitions/ability_ranged_primary/delivery/basic_weapon_tuning/entry", V("shot_cooldown", 0.07f), V("time_between_shots", 0.08f)),
        At("ability_data/ability_definitions/ability_reload/reload_upgrade_info/wargear\\reload_info", V("triggered_reload_time", 2.75f), V("auto_reload_time", 2.6f)),
        At("common_data/ammunition/entry/by_upgrade/ammunition_info", ("capacity_clip", 141), ("capacity_reserve", 5), ("capacity_reserve_infinite", true), ("low_ammunition_amount", 16),
            V("recharge_delay", 0f), V("ammo_recharged_per_second", 0f)));

    static BafDocument Melta() => Doc(
        At(Pump, V("max_range", 251f)),
        At(Pump + "/hit_sim/actions/do_damage", V("damage_general", 201f), V("damage_armourshield", 202f)),
        At(Pump + "/hit_sim/actions_by_upgrade/entry/action/commands/do_damage", V("damage_general", 101f), V("damage_armourshield", 301f)),
        At(Pump + "/basic_weapon_tuning/entry", V("shot_cooldown", 0.34f), V("time_between_shots", 0f)),
        At(Pump + "/basic_weapon_tuning/entry", V("shot_cooldown", 0.1f), V("time_between_shots", 0f)),
        At("common_data/ammunition/entry/by_upgrade/ammunition_info", ("capacity_clip", 1), ("capacity_reserve", 12), ("capacity_reserve_infinite", true), V("recharge_delay", 0.35f), V("ammo_recharged_per_second", 1.5f)));

    static BafDocument Arm()
    {
        const string defs = "ability_data/ability_definitions/";
        List<(string, object)> rows =
        [
            .. HitRows(defs + "melee_x01/primary_target_actions", 101f, 251f, 151f), .. HitRows(defs + "melee_x01/secondary_target_actions", 51f, 101f, null),
            .. HitRows(defs + "melee_x02/primary_target_actions", 126f, 252f, 152f), .. HitRows(defs + "melee_x02/secondary_target_actions", 76f, 102f, null),
            .. HitRows(defs + "melee_y01/primary_target_actions", 52f, 202f, null),
            .. HitRows(defs + "melee_x_rush/primary_target_actions", 501f, 1001f, 153f), .. HitRows(defs + "melee_x_rush/secondary_target_actions", 126f, 76f, null),
            .. AreaRows("action_data/triggered_actions/actn_pound_hit", 501f, 1f, 1001f, 11f, 5f), .. AreaRows("action_data/triggered_actions/actn_stomp_hit", 51f, 251f, 1002f, 12f, 6f),
        ];
        foreach (string strike in new[] { "melee_x01", "melee_x02", "melee_y01", "melee_y02_stomp", "melee_x_rush" })
            rows.Add(At(defs + strike + "/ability_metadata/generic_metadata", V("max_range", 4.5f)));
        return Doc([.. rows]);
    }

    static BafDocument Sprint() => Doc(
        At("ability_data/ability_definitions/ability_sprint", V("max_turn_rate", 55f)),
        At("ability_data/ability_definitions/ability_sprint/special_nav_ability", V("max_time_limit", 7f), V("min_time_between_activations", 1.5f), V("max_horz_angle_for_wall_hit", 25f)));

    static BafDocument Buff() => Doc(
        At("ability_data/ability_definitions/ability_buff", V("duration", 11f), V("min_time_between_activations", 12f), V("regen_multiplier", 2.5f)),
        At("action_data/triggered_actions/evt_activatebuff/commands/apply_upgrade_string/strings", ("entry", "fury_mode"), ("entry", "mp_reaction_immunity")),
        At("common_data/ammunition/entry/by_upgrade/wargear\\ammunition_info", ("capacity_clip", 1), ("capacity_reserve", 0), ("capacity_reserve_infinite", false)));

    static Dictionary<string, BafDocument> Wargear() => new(StringComparer.OrdinalIgnoreCase)
    {
        [DreadnoughtData.Arm] = Arm(), [DreadnoughtData.AssaultCannon] = Cannon(), [DreadnoughtData.MeltaGun] = Melta(),
        [DreadnoughtData.SprintAbility] = Sprint(), [DreadnoughtData.BuffGear] = Buff(),
    };

    [Fact]
    public void The_body_is_read_from_the_blueprint_with_every_field()
    {
        var d = DreadnoughtData.From(Blueprint(), Wargear(), null, [], []);
        var b = d.Body;
        Assert.Equal((7777f, 4f, 111f, 12f), (b.HealthMax, b.HealthRegenDelay, b.HealthRegenRate, b.ShieldMax));
        Assert.Equal((6f, 6.25f, 4f, 12f, 2.5f), (b.RunSpeed, b.StrafeSpeed, b.BackSpeed, b.SprintSpeed, b.WalkSpeed));
        Assert.Equal((4.5f, 4.75f, 3.75f), (b.AimRunSpeed, b.AimStrafeSpeed, b.AimBackSpeed));
        Assert.Equal((5f, 4f, 3.5f, 9f, 3.6f), (b.CrouchRunSpeed, b.CrouchStrafeSpeed, b.CrouchBackSpeed, b.CrouchSprintSpeed, b.CrouchWalkSpeed));
        Assert.Equal((300f, 0.7f, 55f, 70f, 700f), (b.RotationMaxDegreesPerSecond, b.GroundTimeTillMaxSpeed, b.MaxWalkableSlope, b.AimHorizontalSpeed, b.AimVerticalSpeed));
        Assert.Equal((1.5f, 2.5f, 9f, "really_big"), (b.HitPhantomRadius, b.HitPhantomLength, b.DeathTime, b.UnitSize));
        Assert.Equal((1f, 2f, 3f, 4f), (b.ReceivedMelee, b.ReceivedRanged, b.ReceivedExplosive, b.ReceivedWorld));
    }

    [Fact]
    public void The_entrance_and_the_control_point_heal_come_from_the_blueprints_actions()
    {
        var e = DreadnoughtData.From(Blueprint(), Wargear(), null, [], []).Entrance;
        Assert.Equal((6f, 0.5f, 1234f), (e.InvulnerableSeconds, e.ReceivedMultiplier, e.ControlPointHeal));
        Assert.Null(e.ClipSeconds);   // no rig given
    }

    [Fact]
    public void The_cannon_and_the_melta_are_read_with_their_own_files_values()
    {
        var d = DreadnoughtData.From(Blueprint(), Wargear(), null, [], []);
        var c = d.Cannon!;
        Assert.Equal((31f, 6f, 0.07f, 0.08f, 240f), (c.Damage, c.ArmourDamage, c.ShotCooldown, c.TimeBetweenShots, c.Range));
        Assert.Equal((141, 5, true, 16), (c.ClipSize, c.Reserve, c.ReserveInfinite, c.LowAmmo));
        Assert.Equal((2.75f, 2.6f, 0.25f, "heat_x"), (c.ReloadTime, c.AutoReloadTime, c.HeatPerShot, c.HeatName));
        var m = d.Melta!;
        Assert.Equal((201f, 202f, 0.34f, 251f), (m.Damage, m.ArmourDamage, m.ShotCooldown, m.Range));
        Assert.Equal((1, 12, 0.35f, 1.5f), (m.ClipSize, m.Reserve, m.RechargeDelay, m.RechargePerSecond));
        Assert.Equal((101f, 301f, 0.1f), (d.MeltaExtras!.CoopDamage, d.MeltaExtras.CoopArmourDamage, d.MeltaExtras.FuryShotCooldown));
    }

    [Fact]
    public void The_five_strikes_carry_their_primary_and_secondary_hits_and_reach()
    {
        var strikes = DreadnoughtData.From(Blueprint(), Wargear(), null, [], []).Strikes;
        Assert.Equal(["melee_x01", "melee_x02", "melee_y01", "melee_y02_stomp", "melee_x_rush"], strikes.Select(s => s.Ability));
        Assert.All(strikes, s => Assert.Equal(4.5f, s.Range));
        Assert.Equal((101f, 251f, 151f), (strikes[0].Primary!.Damage, strikes[0].Primary!.ArmourDamage, strikes[0].Primary!.StunDamage));
        Assert.Equal((51f, 101f, (float?)null), (strikes[0].Secondary!.Damage, strikes[0].Secondary!.ArmourDamage, strikes[0].Secondary!.StunDamage));
        Assert.Equal(126f, strikes[1].Primary!.Damage);
        Assert.Equal(52f, strikes[2].Primary!.Damage);
        Assert.Null(strikes[2].Secondary);
        Assert.Null(strikes[3].Primary);
        Assert.Equal((501f, 1001f, 153f), (strikes[4].Primary!.Damage, strikes[4].Primary!.ArmourDamage, strikes[4].Primary!.StunDamage));
        Assert.Equal(126f, strikes[4].Secondary!.Damage);
    }

    [Fact]
    public void The_clip_lengths_and_tags_come_from_the_rig_and_a_missing_clip_is_a_note()
    {
        var notes = new List<string>();
        var graph = new SmFormats.Havok.BehaviorGraph(DreadnoughtData.Graph, [], [new SmFormats.Havok.BehaviorSequencer("seq", ["DN_MEL_XATK_01_3.5m"],
            [new SmFormats.Havok.BehaviorTag("Event", 0.5f, 0.6f, "ACTN_Hit_Primary_Target", null)], [1f])]);
        var d = DreadnoughtData.From(Blueprint(), Wargear(), clip => clip == "DN_MEL_Stomp_3.5m" ? throw new KeyNotFoundException("no such clip") : 4f, [graph], notes);
        var x01 = d.Strikes[0];
        Assert.Equal(4f, x01.ClipSeconds);
        Assert.Equal(new DreadnoughtClipTag("Event", "ACTN_Hit_Primary_Target", 2f), Assert.Single(x01.Tags));
        Assert.Null(d.Strikes[3].ClipSeconds);
        Assert.Contains(notes, n => n.Contains("DN_MEL_Stomp_3.5m"));
        Assert.Equal(4f, d.Entrance.ClipSeconds);
    }

    [Fact]
    public void A_clips_tags_are_in_the_seconds_it_plays_at_the_generators_speed()
    {
        var graph = new SmFormats.Havok.BehaviorGraph(DreadnoughtData.Graph, [], [new SmFormats.Havok.BehaviorSequencer("seq", ["DN_MEL_XATK_01_3.5m"],
            [new SmFormats.Havok.BehaviorTag("Event", 0.5f, 0.6f, "ACTN_Hit_Primary_Target", null)], [2f])]);
        var x01 = DreadnoughtData.From(Blueprint(), Wargear(), _ => 4f, [graph], []).Strikes[0];
        Assert.Equal(2f, x01.PlaybackSpeed);
        Assert.Equal(1f, Assert.Single(x01.Tags).Seconds);   // half of 4 s at double speed
    }

    [Fact]
    public void The_areas_report_the_damage_area_radius_apart_from_the_debris_force_radius()
    {
        var areas = DreadnoughtData.From(Blueprint(), Wargear(), null, [], []).Areas;
        Assert.Equal(["actn_pound_hit", "actn_stomp_hit", "evt_spawn_r_stomp", "evt_spawn_l_stomp"], areas.Select(a => a.Action));
        Assert.Equal(new DreadnoughtArea("actn_pound_hit", 501f, 1f, 1001f, 11f, 5f), areas[0]);
        Assert.Equal(new DreadnoughtArea("evt_spawn_r_stomp", 20f, 21f, 900f, 9f, 4f), areas[2]);
    }

    [Fact]
    public void The_sprint_and_the_buff_are_read_with_the_upgrades_the_buff_applies_and_its_one_use()
    {
        var d = DreadnoughtData.From(Blueprint(), Wargear(), null, [], []);
        Assert.Equal((7f, 1.5f, 55f, 25f), (d.Sprint!.MaxSeconds, d.Sprint.GapSeconds, d.Sprint.MaxTurnRate, d.Sprint.WallHitAngle));
        var b = d.Buff!;
        Assert.Equal((11f, 12f, 2.5f), (b.Seconds, b.CooldownSeconds, b.RegenMultiplier));
        Assert.Equal(["fury_mode", "mp_reaction_immunity"], b.Upgrades);
        Assert.Equal((1, 0), (b.Clip, b.Reserve));
    }

    [Fact]
    public void A_missing_blueprint_or_wargear_file_is_a_note_and_a_null_part_never_a_throw()
    {
        var notes = new List<string>();
        var d = DreadnoughtData.From(null, new Dictionary<string, BafDocument>(), null, [], notes);
        Assert.Null(d.Body.HealthMax);
        Assert.Null(d.Cannon);
        Assert.Null(d.Melta);
        Assert.Null(d.Sprint);
        Assert.Null(d.Buff);
        Assert.Empty(d.Strikes);
        Assert.Empty(d.Areas);
        Assert.Equal(6, notes.Count);   // the blueprint and the five wargear files
        Assert.Contains(notes, n => n.Contains(DreadnoughtData.Blueprint));
    }

    // ---- the install ----

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the Dreadnought's data is read from it; never in CI)";
        }
    }

    [InstallFact]
    public void The_installs_numbers_are_the_ones_the_design_uses()
    {
        var notes = new List<string>();
        var d = DreadnoughtData.Read(Game.Value!, notes);
        Assert.Empty(notes);
        var b = d.Body;
        Assert.Equal((8000f, 5f, 250f, 0f), (b.HealthMax, b.HealthRegenDelay, b.HealthRegenRate, b.ShieldMax));
        Assert.Equal((6.5f, 6.5f, 4.5f, 11f, 3f), (b.RunSpeed, b.StrafeSpeed, b.BackSpeed, b.SprintSpeed, b.WalkSpeed));
        Assert.Equal((5f, 5f, 4f), (b.AimRunSpeed, b.AimStrafeSpeed, b.AimBackSpeed));
        Assert.Equal((360f, 0.6f, 60f, 60f, 720f, 1.8f, 2.3f, "really_big"), (b.RotationMaxDegreesPerSecond, b.GroundTimeTillMaxSpeed, b.MaxWalkableSlope, b.AimHorizontalSpeed, b.AimVerticalSpeed, b.HitPhantomRadius, b.HitPhantomLength, b.UnitSize));
        Assert.Equal((1f, 1f, 1f, 1f), (b.ReceivedMelee, b.ReceivedRanged, b.ReceivedExplosive, b.ReceivedWorld));
        var c = d.Cannon!;
        Assert.Equal((30f, 5f, 0.06f, 250f), (c.Damage, c.ArmourDamage, c.ShotCooldown, c.Range));
        Assert.Equal((140, true, 15, 2.5f, 0.5f), (c.ClipSize, c.ReserveInfinite, c.LowAmmo, c.ReloadTime, c.HeatPerShot));
        var m = d.Melta!;
        Assert.Equal((200f, 200f, 0.33f, 1, 0.33f, 1f), (m.Damage, m.ArmourDamage, m.ShotCooldown, m.ClipSize, m.RechargeDelay, m.RechargePerSecond));
        Assert.Equal((100f, 300f, 0.095f), (d.MeltaExtras!.CoopDamage, d.MeltaExtras.CoopArmourDamage, d.MeltaExtras.FuryShotCooldown));
        Assert.Equal([100f, 125f, 50f, 0f, 500f], d.Strikes.Select(s => s.Primary?.Damage ?? 0f));
        Assert.Equal([50f, 75f, 0f, 0f, 125f], d.Strikes.Select(s => s.Secondary?.Damage ?? 0f));
        Assert.Equal([150f, 150f, 0f, 0f, 150f], d.Strikes.Select(s => s.Primary?.StunDamage ?? 0f));
        Assert.All(d.Strikes, s => Assert.Equal(4f, s.Range));
        Assert.Equal([3.067f, 2.267f, 3.833f, 3.833f, 4.2f], d.Strikes.Select(s => MathF.Round(s.ClipSeconds ?? 0f, 3)));
        Assert.All(d.Strikes, s => Assert.Contains(s.Tags, t => t.Name == "ACTN_Hit_Primary_Target"));
        Assert.Equal((500f, 10f, 4.5f), (d.Areas[0].Damage, d.Areas[0].Radius, d.Areas[0].DebrisRadius));   // the slam
        Assert.Equal((50f, 10f), (d.Areas[1].Damage, d.Areas[1].Radius));   // the stomp attack
        Assert.All(d.Areas.Skip(2), a => Assert.Equal((25f, 1000f, 10f), (a.Damage, a.StunDamage, a.Radius)));   // each footfall
        Assert.Equal((6f, 1f, 50f, 20f), (d.Sprint!.MaxSeconds, d.Sprint.GapSeconds, d.Sprint.MaxTurnRate, d.Sprint.WallHitAngle));
        Assert.Equal((10f, 10f, 2f, 1, 0), (d.Buff!.Seconds, d.Buff.CooldownSeconds, d.Buff.RegenMultiplier, d.Buff.Clip, d.Buff.Reserve));
        Assert.Equal(["fury_mode", "mp_reaction_immunity"], d.Buff.Upgrades);
        Assert.Equal((5f, 0f, 1500f), (d.Entrance.InvulnerableSeconds, d.Entrance.ReceivedMultiplier, d.Entrance.ControlPointHeal));
        Assert.Equal(4.833f, MathF.Round(d.Entrance.ClipSeconds ?? 0f, 3));
    }

    [InstallFact]
    public void The_rules_constants_agree_with_the_installs_data_where_they_are_the_data()
    {
        var d = DreadnoughtData.Read(Game.Value!);
        var b = d.Body;
        Assert.Equal((SmSim.Extras.Musou.DreadnoughtRules.RunSpeed, SmSim.Extras.Musou.DreadnoughtRules.StrafeSpeed, SmSim.Extras.Musou.DreadnoughtRules.BackSpeed, SmSim.Extras.Musou.DreadnoughtRules.SprintSpeed, SmSim.Extras.Musou.DreadnoughtRules.WalkSpeed),
            (b.RunSpeed!.Value, b.StrafeSpeed!.Value, b.BackSpeed!.Value, b.SprintSpeed!.Value, b.WalkSpeed!.Value));
        Assert.Equal((SmSim.Extras.Musou.DreadnoughtRules.AimRunSpeed, SmSim.Extras.Musou.DreadnoughtRules.AimStrafeSpeed, SmSim.Extras.Musou.DreadnoughtRules.AimBackSpeed),
            (b.AimRunSpeed!.Value, b.AimStrafeSpeed!.Value, b.AimBackSpeed!.Value));
        Assert.Equal((SmSim.Extras.Musou.DreadnoughtRules.DataHealth, SmSim.Extras.Musou.DreadnoughtRules.DataRegenDelay, SmSim.Extras.Musou.DreadnoughtRules.DataRegenRate), (b.HealthMax!.Value, b.HealthRegenDelay!.Value, b.HealthRegenRate!.Value));
        Assert.Equal((SmSim.Extras.Musou.DreadnoughtRules.InvulnerableSeconds, SmSim.Extras.Musou.DreadnoughtRules.EntranceSeconds), (d.Entrance.InvulnerableSeconds!.Value, MathF.Round(d.Entrance.ClipSeconds!.Value, 3)));
    }
}
