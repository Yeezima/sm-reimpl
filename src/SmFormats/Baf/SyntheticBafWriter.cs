using System.Text;

namespace SmFormats.Baf;

/// <summary>
/// Builds synthetic BAF attribute files from a nested description, following our documented interpretation of the layout.
/// For tests only; passing proves internal consistency, not compatibility with original files.
/// Values: <see cref="bool"/>, <see cref="float"/>, <see cref="int"/>, <see cref="string"/>, or a nested
/// <c>(string Name, object Value)[]</c> table.
/// </summary>
public static class SyntheticBafWriter
{
    public static byte[] Build(params (string Name, object Value)[] root)
    {
        var strings = new List<string>();
        var stringIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int S(string s) { if (!stringIndex.TryGetValue(s, out int i)) { i = strings.Count; strings.Add(s); stringIndex[s] = i; } return i; }

        // Tables are numbered in breadth-first order; each table's entries are contiguous.
        var tables = new List<(int Count, int First)>();
        var entries = new List<(int Type, int Name, int Value)>();
        var queue = new Queue<((string Name, object Value)[] Items, int Index)>();
        tables.Add((0, -1));
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (items, index) = queue.Dequeue();
            int first = entries.Count;
            foreach (var (name, value) in items)
            {
                switch (value)
                {
                    case bool b: entries.Add((0, S(name), b ? 1 : 0)); break;
                    case float f: entries.Add((1, S(name), BitConverter.SingleToInt32Bits(f))); break;
                    case int n: entries.Add((2, S(name), n)); break;
                    case string str: entries.Add((3, S(name), S(str))); break;
                    case (string, object)[] child:
                        tables.Add((0, -1));
                        entries.Add((4, S(name), tables.Count - 1));
                        queue.Enqueue((child, tables.Count - 1));
                        break;
                    default: throw new ArgumentException($"Unsupported value for '{name}': {value?.GetType().Name}.");
                }
            }
            tables[index] = items.Length == 0 ? (0, -1) : (items.Length, first);
        }

        var pool = new MemoryStream();
        var offsets = new List<int>();
        foreach (var s in strings) { offsets.Add((int)pool.Length); pool.Write(Encoding.ASCII.GetBytes(s)); pool.WriteByte(0); }

        int tablesAt = 45, entriesAt = tablesAt + 8 * tables.Count, offsetsAt = entriesAt + 12 * entries.Count, poolAt = offsetsAt + 4 * strings.Count;
        var o = new BinaryWriter(new MemoryStream());
        o.Write("BAF "u8); o.Write(100u); o.Write(0x5EED5EEDu); o.Write(0u); o.Write((byte)0);
        o.Write(tablesAt); o.Write(tables.Count); o.Write(entriesAt); o.Write(entries.Count); o.Write(strings.Count); o.Write(offsetsAt); o.Write(poolAt);
        foreach (var (count, first) in tables) { o.Write(count); o.Write(first); }
        foreach (var (type, name, value) in entries) { o.Write(type); o.Write(name); o.Write(value); }
        foreach (var off in offsets) o.Write(off);
        o.Write(pool.ToArray());
        return ((MemoryStream)o.BaseStream).ToArray();
    }

    /// <summary>A player blueprint shaped like the game's, with the given locomotion values.</summary>
    /// <param name="aimRun">The aiming table (<c>speed_aiming</c>; protocol 12): run, strafe and back speeds.</param>
    public static byte[] PlayerBlueprint(float run, float strafe, float back, float sprint, float walk, float groundTime, float maxAcceleration,
        float aimRun = 3f, float aimStrafe = 3f, float aimBack = 2f) => Build(
        ("extensions", new (string, object)[]
        {
            ("entity_ext", new (string, object)[] { ("$ref", "entity_extensions\\entity_ext") }),
            ("biped_locomotion_ext", new (string, object)[]
            {
                ("$ref", "entity_extensions\\biped_locomotion_ext"),
                ("speed_standing", new (string, object)[]
                {
                    ("$ref", "_base_templates\\speed_tuning"),
                    ("run_speed", run), ("strafe_speed", strafe), ("back_speed", back), ("sprint_speed", sprint), ("walk_speed", walk),
                }),
                ("speed_aiming", new (string, object)[]
                {
                    ("$ref", "_base_templates\\speed_tuning"),
                    ("run_speed", aimRun), ("strafe_speed", aimStrafe), ("back_speed", aimBack), ("sprint_speed", 9f), ("walk_speed", walk),
                }),
                ("ground_time_till_max_speed", groundTime),
                ("left_stick_turns_camera", false),
                ("max_acceleration", maxAcceleration),
                ("max_walkable_slope", 60f),
            }),
            ("health_ext", new (string, object)[]
            {
                ("health_max", 100f), ("health_regen_delay", 5f), ("health_regen_rate", 5f), ("level", 3),
                ("armourshield_info", new (string, object)[]
                {
                    Upgraded("armourshield_info", ("armourshield_max", 150f), ("armourshield_regen_delay", 7f), ("armourshield_regen_rate", 33f)),
                    Default("armourshield_info", ("armourshield_max", 100f), ("armourshield_regen_delay", 7f), ("armourshield_regen_rate", 33f)),
                }),
            }),
        }),
        ("max_recycle_pool_size", 0));

    /// <summary>
    /// A ranged weapon shaped like the game's wargear files (bolter layout): an upgraded variant listed before the
    /// default one wherever the game keys values by upgrade, so readers must pick the default entry.
    /// </summary>
    /// <param name="slot">common_data/slot ("ranged", "pistol", "melee").</param>
    /// <param name="trigger">Trigger policy template, e.g. "wargear\\trigger_policy\\full_auto".</param>
    /// <param name="delivery">Delivery type template ("…\\raycast" or "…\\projectile").</param>
    /// <param name="heat">A heat pool (<c>common_data/heat</c>) and heat per primary shot (<c>fire_sim</c>), or none.</param>
    /// <param name="blueprint">The projectile blueprint path a projectile delivery names (<c>type/ebp</c>); an upgraded
    /// variant naming <c>&lt;blueprint&gt;_upgraded</c> is listed first.</param>
    /// <param name="maxActive">The delivery's <c>max_active_projectiles</c>.</param>
    /// <param name="charge">A <c>charge_ranged</c> ability, or none.</param>
    /// <param name="chargeIsPrimary">The charge ability is the primary and the basic shot the secondary (plasma pistol layout).</param>
    public static byte[] RangedWeapon(float damage, float cooldown, int clip, int reserve, float reload, float range,
        (float Distance, float Multiplier)[] fallOff, float head, float body, float shieldHead, float shieldBody,
        string slot = "ranged", string trigger = "wargear\\trigger_policy\\full_auto",
        string delivery = "_base_templates\\ranged_delivery_types\\raycast",
        (float PerShot, float Overheat, float High, float Recovery, float Rate, float Delay, float DelayAfterOverheat)? heat = null,
        string? blueprint = null, int maxActive = 0, ChargeFixture? charge = null, bool chargeIsPrimary = false,
        float[]? detonationDelays = null, PierceFixture? pierce = null) => Build(
        ("ability_data", new (string, object)[]
        {
            ("ability_definitions", Join(charge is { } c ? new (string, object)[] { (chargeIsPrimary ? "ability_ranged_primary" : "ability_ranged_secondary", ChargeAbility(c)) } : [],
            detonationDelays is { } delays ? new (string, object)[] { ("ability_utility", new (string, object)[]
            {
                ("$ref", "wargear\\ability_types\\detonate_projectile"),
                ("detonation_delays", delays.Select(t => ("time", (object)t)).ToArray()),
                ("input_override", "reload"),
            }) } : [], new (string, object)[]
            {
                (chargeIsPrimary ? "ability_ranged_secondary" : "ability_ranged_primary", new (string, object)[]
                {
                    ("$ref", "wargear\\ability_types\\standard_ranged"),
                    ("trigger_data", new (string, object)[] { ("trigger", new (string, object)[] { ("$ref", trigger) }) }),
                    ("delivery", Join(heat is { } h ? new (string, object)[]
                        {
                            ("fire_sim", new (string, object)[] { ("actions_by_upgrade", new (string, object)[]
                            {
                                Upgraded("entry", ("action", new (string, object)[] { ("commands", new (string, object)[] { ("apply_heat", HeatPerShot(h.PerShot / 3)) }) })),
                                Default("entry", ("action", new (string, object)[] { ("commands", new (string, object)[] { ("apply_heat", HeatPerShot(h.PerShot)) }) })),
                            }) }),
                        } : [], pierce is { } pen ? new (string, object)[] { ("penetration", Penetration(pen)) } : [], new (string, object)[] {
                        ("ranged_delivery_type", blueprint is null
                            ? new (string, object)[] { Default("entry", ("type", new (string, object)[] { ("$ref", delivery) })) }
                            : new (string, object)[]
                            {
                                Upgraded("entry", ("type", new (string, object)[] { ("$ref", delivery), ("ebp", blueprint + "_upgraded"), ("max_active_projectiles", maxActive + 2) })),
                                Default("entry", ("type", new (string, object)[] { ("$ref", delivery), ("ebp", blueprint), ("max_active_projectiles", maxActive) })),
                            }),
                        ("ammo_used", new (string, object)[] { ("_base_templates\\ammo_use", new (string, object)[] { ("ammunition_name", "test_ammo"), ("amount", 1) }) }),
                        ("max_range", range),
                        ("damage_fall_off", fallOff.Select(p => ("fall_off_entry", (object)new (string, object)[] { ("distance", p.Distance), ("damage_multiplier", p.Multiplier) })).ToArray()),
                        ("basic_weapon_tuning", new (string, object)[]
                        {
                            Upgraded("entry", ("shot_cooldown", cooldown / 2)),
                            Default("entry", ("shot_cooldown", cooldown)),
                        }),
                        // Damage ≤ 0: no do_damage at all (a projectile weapon without direct-hit damage, like the plasma cannon).
                        ("hit_sim", damage <= 0f ? new (string, object)[] { ("$ref", "wargear\\wargear_action_filter") } : new (string, object)[]
                        {
                            ("actions_by_upgrade", new (string, object)[]
                            {
                                Upgraded("entry", ("action", DoDamage(damage * 2, head, body, shieldHead, shieldBody))),
                                Default("entry", ("action", DoDamage(damage, head, body, shieldHead, shieldBody))),
                            }),
                        }),
                    })),
                }),
                ("ability_reload", new (string, object)[]
                {
                    ("reload_upgrade_info", new (string, object)[]
                    {
                        Upgraded("wargear\\reload_info", ("triggered_reload_time", reload / 2)),
                        Default("wargear\\reload_info", ("triggered_reload_time", reload)),
                    }),
                }),
            })),
        }),
        ("common_data", Join(heat is { } pool ? new (string, object)[]
            {
                ("heat", new (string, object)[] { ("wargear\\heat_info", new (string, object)[]
                {
                    ("overheat_amount", pool.Overheat), ("high_heat_amount", pool.High), ("recovery_amount", pool.Recovery), ("heat_name", "test_heat"),
                    ("cool_down_upgrade_list", new (string, object)[] { ("upgradeable", new (string, object)[]
                    {
                        ("cool_down_delay_after_heat_gain", pool.Delay), ("cool_down_delay_after_overheat", pool.DelayAfterOverheat), ("cool_down_rate", pool.Rate),
                    }) }),
                }) }),
            } : [], charge?.FireModeUpgrade is { } mode ? new (string, object)[]
            {
                ("fire_mode_primary", new (string, object)[] { Default("entry", ("upgrade_granted", chargeIsPrimary ? mode : "")) }),
                ("fire_mode_secondary", new (string, object)[] { Default("entry", ("upgrade_granted", chargeIsPrimary ? "" : mode)) }),
            } : [], new (string, object)[] {
            ("slot", slot),
            ("ammunition", new (string, object)[]
            {
                ("entry", new (string, object)[]
                {
                    ("by_upgrade", new (string, object)[]
                    {
                        Upgraded("ammunition_info", ("capacity_clip", clip * 2), ("capacity_reserve", reserve * 2)),
                        Default("ammunition_info", ("capacity_clip", clip), ("capacity_reserve", reserve)),
                    }),
                }),
            }),
        })));

    /// <summary>
    /// A <c>charge_ranged</c> ability shaped like the game's: stage <c>1.charge_light</c> at 0 s with no shot, an empty
    /// <c>2.charge_dead_zone</c> slot, then <c>4.charge_heavy</c> firing after <see cref="ChargeFixture.MinTime"/>. Its
    /// cooldown has an upgrade-only entry (×10, needs "some_upgrade") listed first; with a fire mode it also has an entry
    /// needing the mode's upgrade (the given cooldown) after a default one (cooldown × 5); the charge limits have an
    /// upgraded variant (0.8 s, ×0.53) before the default.
    /// </summary>
    static (string, object)[] ChargeAbility(ChargeFixture c) =>
    [
        ("$ref", "wargear\\ability_types\\charge_ranged"),
        ("trigger_data", new (string, object)[] { ("ammunition", new (string, object)[] { ("wargear\\ammunition_entry", new (string, object)[] { ("required_to_start", c.AmmoToStart) }) }) }),
        ("charge_states", new (string, object)[]
        {
            ("1.charge_light", new (string, object)[] { ("$ref", "wargear\\ranged_charge_stage"), ("minimum_charge_time", 0f), ("delivery", new (string, object)[] { ("$ref", "") }), ("heat_applied_per_second", 0f) }),
            ("2.charge_dead_zone", new (string, object)[] { ("$ref", "") }),
            ("4.charge_heavy", new (string, object)[]
            {
                ("$ref", "wargear\\ranged_charge_stage"),
                ("minimum_charge_time", c.MinTime),
                ("charging_action", new (string, object)[] { ("commands", new (string, object)[] { ("apply_modifiers", new (string, object)[] { ("modifiers", new (string, object)[]
                {
                    ("modifier", new (string, object)[] { ("modifier_type", "speed_maximum"), ("operation_type", "multiplication"), ("amount", c.MoveSpeedMultiplier) }),
                    ("modifier", new (string, object)[] { ("modifier_type", "health_maximum"), ("operation_type", "multiplication"), ("amount", 9f) }),   // not a speed modifier
                }) }) }) }),
                ("heat_applied_per_second", c.HeatPerSecond),
                ("delivery", new (string, object)[]
                {
                    ("fire_sim", new (string, object)[] { ("actions", new (string, object)[]
                    {
                        ("actions_by_upgrade", new (string, object)[] { ("actions", new (string, object)[]
                        {
                            Upgraded("entry", ("action", new (string, object)[] { ("commands", new (string, object)[] { ("apply_heat", HeatPerShot(c.HeatPerShot * 3)) }) })),
                            Default("entry", ("action", new (string, object)[] { ("commands", new (string, object)[] { ("apply_heat", HeatPerShot(c.HeatPerShot)) }) })),
                        }) }),
                        ("delayed_action", new (string, object)[] { ("delay", c.FireDelay) }),
                    }) }),
                    ("ammo_used", new (string, object)[] { ("_base_templates\\ammo_use", new (string, object)[] { ("ammunition_name", "test_ammo"), ("amount", c.AmmoCost) }) }),
                    ("max_range", c.Range),
                    ("basic_weapon_tuning", Join(
                        [Upgraded("entry", ("shot_cooldown", c.Cooldown * 10))],
                        c.FireModeUpgrade is { } mode
                            ? [Default("entry", ("shot_cooldown", c.Cooldown * 5)), RequiredBy("entry", mode, ("shot_cooldown", c.Cooldown))]
                            : [Default("entry", ("shot_cooldown", c.Cooldown))])),
                    ("hit_sim", c.Damage > 0f
                        ? new (string, object)[] { ("actions", new (string, object)[] { ("do_damage", DamageCommand(c.Damage, 0f, 2f, 1f, 2f, 1f, false)) }) }
                        : new (string, object)[] { ("$ref", "wargear\\wargear_action_filter") }),
                    ("ranged_delivery_type", new (string, object)[] { Default("entry", ("type", c.Blueprint is { } b
                        ? new (string, object)[] { ("$ref", c.Delivery), ("ebp", b), ("max_active_projectiles", 0) }
                        : new (string, object)[] { ("$ref", c.Delivery) })) }),
                }),
            }),
        }),
        ("reload_button_cancels_charging", true),
        ("charge_upgrade_list", new (string, object)[]
        {
            Upgraded("upgradeable", ("max_charge_time", 0.8f), ("min_charge_time_multiplier", 0.53f)),
            Default("upgradeable", ("max_charge_time", c.MaxChargeTime), ("min_charge_time_multiplier", 1f)),
        }),
    ];

    /// <summary>
    /// A projectile blueprint shaped like the game's <c>ebps\projectiles\…</c> files (<c>extensions/projectile_ext</c>).
    /// The detonation lists a blast without damage (reactions only, radius 99) before the damaging one, as the frag
    /// grenade's file has a second, reaction-only blast.
    /// </summary>
    public static byte[] ProjectileBlueprint(float speed, float lifetime, float radius, float splashRadius, float splashDamage, float splashShieldDamage,
        (float Distance, float Intensity)[] fallOff, (float Distance, float Factor)[] gravity, bool sticky = false, bool detonateOnCollision = true,
        float? detonateDamage = null, string collisionType = "raycast", int bounces = 0, float groundLifetime = 0f, float extraAimAngle = 0f,
        RigidFixture? rigid = null, bool stopAtMaxBounce = false, (float Lifetime, float Period)? splashLingers = null,
        (float Radius, float Damage, float ShieldDamage, (float Distance, float Intensity)[] FallOff)? secondBlast = null, float? blindness = null) => Build(
        ("extensions", new (string, object)[]
        {
            ("projectile_ext", new (string, object)[]
            {
                ("$ref", "entity_extensions\\projectile_ext"),
                ("lifetime", lifetime), ("speed", speed),
                ("detonate_action", new (string, object)[] { ("commands", Join(new (string, object)[]
                {
                    ("area_of_effect", new (string, object)[]
                    {
                        ("hit_actions", new (string, object)[] { ("commands", new (string, object)[] { ("trigger_reaction", new (string, object)[] { ("$ref", "action_commands\\trigger_reaction") }) }) }),
                        ("shape", new (string, object)[] { ("$ref", "_base_templates\\shape\\sphere"), ("radius", 99f) }),
                    }),
                    ("area_of_effect", new (string, object)[]
                    {
                        ("hit_actions", new (string, object)[] { ("commands", new (string, object)[] { ("do_damage", DamageCommand(splashDamage, splashShieldDamage, 1f, 1f, 2f, 2f, true)) }) }),
                        ("target_filter", "target_all"),
                        ("shape", new (string, object)[] { ("$ref", "_base_templates\\shape\\sphere"), ("radius", splashRadius) }),
                        ("lifetime", splashLingers?.Lifetime ?? 0f), ("apply_every_this_many_seconds", splashLingers?.Period ?? 0f),
                        ("line_of_sight", true), ("ignore_self", false),
                        ("fall_off", fallOff.Select(p => ("fall_off_entry", (object)new (string, object)[] { ("distance", p.Distance), ("intensity_value", p.Intensity) })).ToArray()),
                    }),
                }, blindness is { } blind ? new (string, object)[] { ("area_of_effect", new (string, object)[]
                    {
                        // Blindness and stun only (the krak grenade's first blast): no damage, not simulated.
                        ("hit_actions", new (string, object)[] { ("commands", new (string, object)[] { ("do_blindness", new (string, object)[] { ("blindness_duration", blind) }) }) }),
                        ("shape", new (string, object)[] { ("$ref", "_base_templates\\shape\\sphere"), ("radius", 8f) }),
                    }) } : [],
                secondBlast is { } second ? new (string, object)[] { ("area_of_effect", new (string, object)[]
                    {
                        ("hit_actions", new (string, object)[] { ("commands", new (string, object)[] { ("do_damage", DamageCommand(second.Damage, second.ShieldDamage, 1f, 1f, 1f, 1f, false)) }) }),
                        ("target_filter", "target_all"),
                        ("shape", new (string, object)[] { ("$ref", "_base_templates\\shape\\sphere"), ("radius", second.Radius) }),
                        ("lifetime", 0f), ("apply_every_this_many_seconds", 0f),
                        ("line_of_sight", true), ("ignore_self", false),
                        ("fall_off", second.FallOff.Select(p => ("fall_off_entry", (object)new (string, object)[] { ("distance", p.Distance), ("intensity_value", p.Intensity) })).ToArray()),
                    }) } : [],
                detonateDamage is { } d ? new (string, object)[] { ("do_damage", DamageCommand(d, 0f, 0f, 0f, 1f, 1f, true)) } : [])) }),
                ("collision_type", collisionType),
                ("detonate_on_collision", detonateOnCollision),
                ("max_bounce_count", bounces),
                ("stop_at_max_bounce", stopAtMaxBounce),
                ("rigid_info", rigid is { } r ? new (string, object)[]
                {
                    ("ground_hit_horizontal_reduction", r.GroundHorizontal), ("ground_hit_vertical_reduction", r.GroundVertical),
                    ("entity_hit_horizontal_reduction", r.EntityHorizontal), ("entity_hit_vertical_reduction", r.EntityVertical),
                    ("wall_hit_horizontal_reduction", new (string, object)[] { ("$ref", "_base_templates\\angle_reduction_map"), ("listing", r.Wall.Reverse().Select(m => ("map", (object)new (string, object)[] { ("reduction", m.Reduction), ("angle", m.Angle) })).ToArray()) }),
                    ("wall_hit_vertical_reduction", new (string, object)[] { ("$ref", "_base_templates\\angle_reduction_map"), ("listing", r.Wall.Select(m => ("map", (object)new (string, object)[] { ("reduction", m.Reduction), ("angle", m.Angle) })).ToArray()) }),
                    ("delay_before_collision", r.DelayBefore), ("delay_between_collision", 0.1f), ("ground_angle", r.GroundAngle),
                } : new (string, object)[] { ("ground_angle", 33f) }),
                ("extra_aim_angle", extraAimAngle),
                ("gravity_factor_over_distance", gravity.Select(p => ("entry", (object)new (string, object)[] { ("distance", p.Distance), ("gravity_factor", p.Factor) })).ToArray()),
                ("sticky", sticky),
                ("detonate_when_lifetime_over", true),
                ("ground_lifetime", groundLifetime),
                ("raycast_shape", new (string, object)[] { ("$ref", "_base_templates\\shape\\sphere"), ("radius", radius) }),
            }),
        }),
        ("max_recycle_pool_size", 10));

    /// <summary>
    /// A thrown equipment item shaped like the game's grenades: <c>ability_utility</c> with a projectile delivery
    /// (an upgraded blueprint variant first) and a default ammunition entry keyed <c>wargear\ammunition_info</c> after an
    /// upgraded one keyed <c>ammunition_info</c>.
    /// </summary>
    /// <param name="visible">The item's <c>ui_data/visible_in_customizer</c> (the melta bomb's is false).</param>
    public static byte[] ThrownItem(string blueprint, int count, int initial, float cooldown, float range, bool visible = true) => Build(
        ("ui_data", new (string, object)[] { ("visible_in_customizer", visible) }),
        ("ability_data", new (string, object)[] { ("ability_definitions", new (string, object)[] { ("ability_utility", new (string, object)[]
        {
            ("$ref", "wargear\\ability_types\\standard_ranged"),
            ("delivery", new (string, object)[]
            {
                ("max_range", range),
                ("basic_weapon_tuning", new (string, object)[] { Default("entry", ("shot_cooldown", cooldown)) }),
                ("ranged_delivery_type", new (string, object)[]
                {
                    Upgraded("entry", ("type", new (string, object)[] { ("$ref", "_base_templates\\ranged_delivery_types\\projectile"), ("ebp", blueprint + "_upgraded") })),
                    Default("entry", ("type", new (string, object)[] { ("$ref", "_base_templates\\ranged_delivery_types\\projectile"), ("ebp", blueprint) })),
                }),
            }),
        }) }) }),
        ("common_data", new (string, object)[]
        {
            ("slot", "equipment"),
            ("ammunition", new (string, object)[] { ("entry", new (string, object)[] { ("by_upgrade", new (string, object)[]
            {
                Upgraded("ammunition_info", ("capacity_clip", count * 2), ("initial_clip_amount", initial * 2)),
                Default("wargear\\ammunition_info", ("capacity_clip", count), ("initial_clip_amount", initial)),
            }) }) }),
        }));

    /// <summary>
    /// A melta-shaped weapon: a <c>pump_ranged</c> primary whose delivery sits in its pump state (a shapecast cone; default
    /// damage with shield damage, and a bigger co-op-only variant listed first; distance and coverage fall-off; a cooldown with
    /// a fury-only variant), a decoy <c>ability_ranged_primary_old</c> with another cone, no reload ability, and a
    /// self-recharging clip (a perk variant listed first). <paramref name="shape"/> null: a shapecast without a cone shape.
    /// </summary>
    public static byte[] MeltaWeapon(float damage, float shieldDamage, float cooldown, int clip, int reserve, float rechargeDelay, float rechargePerSecond,
        (float Length, float Angle, float Start, float Offset)? shape, (float Distance, float Multiplier)[] fallOff, (float Coverage, float Multiplier)[] coverage,
        string slot = "ranged") => Build(
        ("ability_data", new (string, object)[] { ("ability_definitions", new (string, object)[]
        {
            ("ability_ranged_primary", new (string, object)[]
            {
                ("$ref", "wargear\\ability_types\\pump_ranged"),
                ("pump_states", new (string, object)[] { ("wargear\\ranged_pump_state", new (string, object)[]
                {
                    ("$ref", "wargear\\ranged_pump_state"), ("minimum_pump_amount", 1),
                    ("delivery", new (string, object)[]
                    {
                        ("hit_sim", new (string, object)[]
                        {
                            ("actions", new (string, object)[] { ("do_damage", DamageCommand(damage, shieldDamage, 1f, 1f, 1f, 1f, false)) }),
                            ("actions_by_upgrade", new (string, object)[] { RequiredBy("entry", "is_in_coop", ("action", new (string, object)[] { ("commands", new (string, object)[] { ("do_damage", DamageCommand(damage / 2, 300f, 1f, 1f, 1f, 1f, false)) }) })) }),
                        }),
                        ("ammo_used", new (string, object)[] { ("_base_templates\\ammo_use", new (string, object)[] { ("ammunition_name", "test_fuel"), ("amount", -1) }) }),
                        ("max_range", 250f),
                        ("damage_fall_off", fallOff.Select(p => ("fall_off_entry", (object)new (string, object)[] { ("distance", p.Distance), ("damage_multiplier", p.Multiplier) })).ToArray()),
                        ("ranged_delivery_type", new (string, object)[] { Default("entry", ("type", shape is { } s
                            ? new (string, object)[] { ("$ref", "_base_templates\\ranged_delivery_types\\shapecast"), ("shape", new (string, object)[]
                                { ("$ref", "_base_templates\\shape\\cone"), ("shape_length", s.Length), ("angle", s.Angle), ("start_length", s.Start), ("shape_offset", s.Offset) }) }
                            : new (string, object)[] { ("$ref", "_base_templates\\ranged_delivery_types\\shapecast") })) }),
                        ("basic_weapon_tuning", new (string, object)[]
                        {
                            Default("entry", ("shot_cooldown", cooldown)),
                            RequiredBy("entry", "fury_aim_mode", ("shot_cooldown", cooldown / 4)),
                        }),
                        ("coverage_fall_off", coverage.Select(p => ("fall_off_entry", (object)new (string, object)[] { ("coverage", p.Coverage), ("damage_multiplier", p.Multiplier) })).ToArray()),
                    }),
                }) }),
                ("trigger_data", new (string, object)[] { ("trigger", new (string, object)[] { ("$ref", "wargear\\trigger_policy\\semi_auto") }) }),
            }),
            ("ability_ranged_primary_old", new (string, object)[]
            {
                ("$ref", "wargear\\ability_types\\standard_ranged"),
                ("delivery", new (string, object)[] { ("ranged_delivery_type", new (string, object)[] { Default("entry", ("type", new (string, object)[]
                    { ("$ref", "_base_templates\\ranged_delivery_types\\shapecast"), ("shape", new (string, object)[] { ("$ref", "_base_templates\\shape\\cone"), ("shape_length", 99f), ("angle", 99f) }) })) }) }),
            }),
        }) }),
        ("common_data", new (string, object)[]
        {
            ("slot", slot),
            ("ammunition", new (string, object)[] { ("entry", new (string, object)[] { ("by_upgrade", new (string, object)[]
            {
                RequiredBy("ammunition_info", "mp_perk_weap_melta_gun_clip_size", ("capacity_clip", clip), ("capacity_reserve", reserve * 2), ("recharge_delay", rechargeDelay), ("ammo_recharged_per_second", rechargePerSecond * 2)),
                Default("ammunition_info", ("capacity_clip", clip), ("capacity_reserve", reserve), ("recharge_delay", rechargeDelay), ("ammo_recharged_per_second", rechargePerSecond), ("initial_clip_amount", -1)),
            }) }) }),
        }));

    static (string, object)[] DamageCommand(float damage, float shieldDamage, float head, float body, float shieldHead, float shieldBody, bool breaker) =>
    [
        ("damage_general", damage), ("damage_armourshield", shieldDamage), ("armourshield_breaker", breaker),
        ("damage_material_multipliers", new (string, object)[] { ("default_body", body), ("default_head", head) }),
        ("armourshield_damage_material_multipliers", new (string, object)[] { ("default_body", shieldBody), ("default_head", shieldHead) }),
    ];

    static (string, object)[] Join(params (string, object)[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>
    /// A jump pack shaped like the game's (<c>ability_jumppack/jump_info</c>: a perk variant first, then the default one;
    /// <c>ability_groundpound</c>; <c>actn_gp_hit</c> with a perk entry before the default one). Height map entries are
    /// written in the given order (the game's lists hold zero-height terminators and are unsorted).
    /// </summary>
    /// <param name="perkMaxPower">The Air-Cooled Thrusters entry's <c>max_power</c> (null: the default entry's).</param>
    /// <param name="poundDamage">The default <c>actn_gp_hit</c> blast's damage and shield damage (radius 7, fall-off (0,1) (4.5,1) (7,0.05)); null: no actn_gp_hit.</param>
    public static byte[] JumpPack((float Time, float Height)[] heights, (float Time, float Cost)[] costs, float maxPower, float recharge,
        float jumping, float hang, float hangTime, float falling, float forward, float moveGain, float driftGain,
        bool smash = true, bool poundAbility = true, (float Damage, float Shield)? poundDamage = null, float smashDelay = 0f,
        (float MinDrop, float Radius, float Damage)[]? landings = null, (float Damage, float Shield)? takeOffDamage = null, float? perkMaxPower = null)
    {
        (string, object)[] Info(float recharge2, float delay, float maxPower2) =>
        [
            ("$ref", "_base_templates\\jump_info"),
            ("jump_height_map", heights.Select(h => ("entry", (object)new (string, object)[] { ("time", h.Time), ("height", h.Height) })).ToArray()),
            ("in_air_drift_gain", driftGain), ("in_air_move_gain", moveGain),
            ("jumping_gravity", jumping), ("falling_gravity", falling), ("jump_hang_gravity", hang), ("jump_hang_time", hangTime), ("jump_forward_speed", forward),
            ("action_land_regular_by_height", (landings ?? []).Select(l => ("landing_action_entry", (object)new (string, object)[]
            {
                ("distance_from_apex_to_land", l.MinDrop),
                ("action", new (string, object)[] { ("commands", new (string, object)[] { ("area_of_effect", Blast(l.Radius, l.Damage, 0f, [(l.Radius - 1f, 1f), (l.Radius, 0.1f)])) }) }),
            })).ToArray()),
            ("smash_gravity", 75f), ("smash_can_be_used", smash), ("smash_initial_speed_boost", 50f),
            ("power_settings", new (string, object)[]
            {
                ("max_power", maxPower2), ("recharge_rate", recharge2),
                ("power_used", costs.Select(c => ("entry", (object)new (string, object)[] { ("time", c.Time), ("power_cost", c.Cost) })).ToArray()),
            }),
            ("smash_limit_angle", 50f), ("smash_available_delay", delay),
            ("action_land_smash_by_height", new (string, object)[] { ("landing_action_entry", new (string, object)[] { ("distance_from_apex_to_land", 0f), ("action", new (string, object)[] { ("commands", Array.Empty<(string, object)>()) }) }) }),
        ];
        static (string, object)[] Blast(float radius, float damage, float shield, (float, float)[] fallOff) =>
        [
            ("hit_actions", new (string, object)[] { ("commands", new (string, object)[]
            {
                ("trigger_reaction", new (string, object)[] { ("reaction_by_intensity", new (string, object)[]
                {
                    ("entry", new (string, object)[] { ("intensity", 0.81f), ("reaction_type", "reaction_mp_stun") }),
                    ("entry", new (string, object)[] { ("intensity", 0.8f), ("reaction_type", "reaction_mp_melee") }),
                }) }),
                ("do_damage", DamageCommand(damage, shield, 1f, 1f, 1f, 1f, false)),
            }) }),
            ("target_filter", "target_all"),
            ("shape", new (string, object)[] { ("$ref", "_base_templates\\shape\\sphere"), ("radius", radius) }),
            ("lifetime", 0f), ("line_of_sight", true), ("ignore_self", true),
            ("fall_off", fallOff.Select(p => ("fall_off_entry", (object)new (string, object)[] { ("distance", p.Item1), ("intensity_value", p.Item2) })).ToArray()),
        ];
        var abilities = new List<(string, object)>
        {
            ("ability_jumppack", new (string, object)[]
            {
                ("$ref", "wargear\\ability_types\\jump_pack"),
                ("jump_info", new (string, object)[]
                {
                    RequiredBy("_base_templates\\jump_info", "mp_perk_jump_pack_reduced_cooldown", Info(recharge * 1.5f, 2f, perkMaxPower ?? maxPower)),
                    Default("_base_templates\\jump_info", Info(recharge, smashDelay, maxPower)),
                }),
            }),
        };
        if (poundAbility) abilities.Add(("ability_groundpound", new (string, object)[] { ("$ref", "wargear\\ability_types\\ground_pound") }));
        var triggered = new List<(string, object)>();
        if (poundDamage is { } pd)
            triggered.Add(("actn_gp_hit", new (string, object)[] { ("commands", new (string, object)[] { ("actions_by_upgrade", new (string, object)[] { ("actions", new (string, object)[]
            {
                RequiredBy("entry", "mp_perk_jump_pack_ground_pound_damage", ("action", new (string, object)[] { ("commands", new (string, object)[] { ("area_of_effect", Blast(7f, pd.Damage * 3f, pd.Shield, [(0f, 1f), (4.5f, 1f), (7f, 0.05f)])) }) })),
                Default("entry", ("action", new (string, object)[] { ("commands", new (string, object)[] { ("area_of_effect", Blast(7f, pd.Damage, pd.Shield, [(0f, 1f), (4.5f, 1f), (7f, 0.05f)])) }) })),
            }) }) }) }));
        // M43: the take-off blast exists only in the Blast Off perk's entry (radius 4, fall-off (0,1) (3.5,1) (4,0.25)).
        if (takeOffDamage is { } td)
            triggered.Add(("actn_take_off", new (string, object)[] { ("commands", new (string, object)[] { ("actions_by_upgrade", new (string, object)[] { ("actions", new (string, object)[]
            {
                RequiredBy("entry", "mp_perk_jump_pack_take_off_damage", ("action", new (string, object)[] { ("commands", new (string, object)[] { ("area_of_effect", Blast(4f, td.Damage, td.Shield, [(0f, 1f), (3.5f, 1f), (4f, 0.25f)])) }) })),
            }) }) }) }));
        return Build(
            ("common_data", new (string, object)[] { ("slot", "coreability") }),
            ("ability_data", new (string, object)[] { ("ability_definitions", abilities.ToArray()) }),
            ("action_data", new (string, object)[] { ("triggered_actions", triggered.ToArray()) }));
    }

    static (string, object)[] HeatPerShot(float amount) =>
        [("heat_name", "test_heat"), ("heat_generated", new (string, object)[] { ("entry", new (string, object)[] { ("current_heat_percentage", 0f), ("heat_to_generate", amount) }) })];

    /// <summary>
    /// A <c>penetration</c> table shaped like the game's (M39): a perk entry (points 2, every cost 1, as the bolter's Kraken Bolts)
    /// and, when <see cref="PierceFixture.WithDefault"/>, a default entry with the fixture's points, costs and exit cone.
    /// </summary>
    static (string, object)[] Penetration(PierceFixture p)
    {
        (string, object)[] Costs(float d) => [("default", d), ("default_body", 4f), ("default_head", 3f), ("default_heavy_armor", 0f)];
        (string, object)[] Exit(float damage) =>
        [
            ("exit_environment_action", new (string, object)[]
            {
                ("$ref", "_base_templates\\action_entry"),
                ("commands", new (string, object)[]
                {
                    ("trigger_material_effect", new (string, object)[] { ("$ref", "action_commands\\trigger_material_effect"), ("material_source", "hit_material") }),
                    ("area_of_effect", new (string, object)[]
                    {
                        ("$ref", "action_commands\\area_of_effect"),
                        ("hit_actions", DoDamage(damage, 1f, 1f, 1f, 1f).Prepend(("$ref", "action")).ToArray()),
                        ("target_filter", p.TargetFilter),
                        ("shape", new (string, object)[] { ("$ref", p.ExitShape), ("angle", p.ExitAngle), ("shape_length", p.ExitLength), ("start_length", 0f), ("shape_offset", 0f) }),
                        ("line_of_sight", true),
                    }),
                }),
            }),
        ];
        var entries = new List<(string, object)> { Upgraded("entry", [("points", 2f), ("material_penetration_costs", Costs(1f)), .. Exit(16f)]) };
        if (p.WithDefault) entries.Add(Default("entry", [("points", p.Points), ("material_penetration_costs", Costs(p.DefaultCost)), .. (p.ExitDamage > 0f ? Exit(p.ExitDamage) : [])]));
        return [.. entries];
    }

    static (string, object)[] DoDamage(float damage, float head, float body, float shieldHead, float shieldBody) =>
    [
        ("commands", new (string, object)[]
        {
            ("do_damage", new (string, object)[]
            {
                ("damage_general", damage),
                ("damage_material_multipliers", new (string, object)[] { ("default_body", body), ("default_head", head) }),
                ("armourshield_damage_material_multipliers", new (string, object)[] { ("default_body", shieldBody), ("default_head", shieldHead) }),
            }),
        }),
    ];

    /// <summary>A variant with an empty required-upgrade list: the value used without upgrades.</summary>
    static (string, object) Default(string name, params (string, object)[] values) =>
        (name, values.Prepend(("required_upgrade_name", new (string, object)[] { ("required_list", Array.Empty<(string, object)>()) })).ToArray());

    /// <summary>A variant that needs an upgrade.</summary>
    /// <summary>
    /// A core-ability evade wargear shaped like <c>sm_mp_core_abil_evade</c> (M21): the <c>ability_evade</c> (a sprint-type
    /// ability with <c>special_nav_ability</c>), the fire mode's <c>animation_set</c>, the fall-protection lifetime upgrade,
    /// and optionally a second ability (the heavy evade's <c>ability_displace_kick</c>). <paramref name="wallAngle"/> and
    /// <paramref name="turnRate"/> are written as ints (the reader accepts ints and floats).
    /// </summary>
    public static byte[] EvadeAbility(string animationSet, string camera, float minInterval, int wallAngle, int turnRate, string? otherAbility = null) => Build(
        ("ability_data", new (string, object)[]
        {
            ("$ref", @"wargear\wargear_weapon_ability_info"),
            ("ability_definitions", ((string, object)[])
            [
                ("ability_evade", new (string, object)[]
                {
                    ("$ref", @"wargear\ability_types\sprint"),
                    ("camera_curve", @"camera_curve_settings\" + camera),
                    ("max_turn_rate", turnRate),
                    ("special_nav_ability", new (string, object)[]
                    {
                        ("$ref", @"wargear\ability_types\special_nav"),
                        ("max_time_limit", -1f), ("max_distance_limit", -1f), ("max_num_enemy_hits", -1f),
                        ("min_time_between_activations", minInterval), ("max_horz_angle_for_wall_hit", wallAngle),
                    }),
                }),
                .. (otherAbility is null ? Array.Empty<(string, object)>()
                    : new (string, object)[] { (otherAbility, new (string, object)[] { ("$ref", @"wargear\ability_types\melee_ability") }) }),
            ]),
        }),
        ("action_data", new (string, object)[]
        {
            ("lifetime_actions", new (string, object)[] { ("commands", new (string, object)[] { ("apply_upgrade_string", new (string, object)[]
                { ("strings", new (string, object)[] { ("entry", "mp_fall_protection") }) }) }) }),
        }),
        ("common_data", new (string, object)[]
        {
            ("slot", "coreability"),
            ("fire_mode_primary", new (string, object)[] { ("entry", new (string, object)[] { ("animation_set", animationSet), ("upgrade_granted", "") }) }),
        }));

    static (string, object) Upgraded(string name, params (string, object)[] values) => RequiredBy(name, "some_upgrade", values);

    /// <summary>A variant that needs <paramref name="upgrade"/>.</summary>
    static (string, object) RequiredBy(string name, string upgrade, params (string, object)[] values) =>
        (name, values.Prepend(("required_upgrade_name", new (string, object)[] { ("required_list", new (string, object)[] { ("upgrade_name", upgrade) }) })).ToArray());
}

/// <summary>
/// <c>rigid_info</c> for <see cref="SyntheticBafWriter.ProjectileBlueprint"/>: ground and entity reductions, wall reduction
/// map points (angle, reduction; written in the given order for the vertical map and reversed for the horizontal one, so
/// readers must sort), the collision delay and the ground angle.
/// </summary>
public sealed record RigidFixture(float GroundHorizontal, float GroundVertical, float EntityHorizontal, float EntityVertical,
    (float Angle, float Reduction)[] Wall, float DelayBefore, float GroundAngle);

/// <summary>
/// A <c>delivery/penetration</c> for <see cref="SyntheticBafWriter.RangedWeapon"/> (M39): the default entry's points and
/// <c>default</c> material cost (none when <see cref="WithDefault"/> is false: only the perk entry), its exit area of effect
/// (no exit action when <see cref="ExitDamage"/> ≤ 0).
/// </summary>
public sealed record PierceFixture(float Points, float DefaultCost, float ExitDamage = 50f, float ExitLength = 15f, float ExitAngle = 1f,
    string TargetFilter = "target_all", bool WithDefault = true, string ExitShape = "_base_templates\\shape\\cone");

/// <summary>A charged ability for <see cref="SyntheticBafWriter.RangedWeapon"/> (one firing stage).</summary>

/// <param name="Delivery">The firing stage's delivery type template ("…\\raycast" or "…\\projectile").</param>
/// <param name="Blueprint">The projectile blueprint path (projectile delivery), else null.</param>
/// <param name="Damage">Hit-scan damage of the firing stage (≤ 0: no do_damage).</param>
/// <param name="FireModeUpgrade">The upgrade the ability's fire mode grants (its cooldown entry needs it), or null.</param>
public sealed record ChargeFixture(float MinTime, float HeatPerSecond, float HeatPerShot, int AmmoCost, float Range, float Cooldown,
    string Delivery, string? Blueprint, float Damage, float MaxChargeTime = 0f, float MoveSpeedMultiplier = 1f, float FireDelay = 0.1f,
    int AmmoToStart = 1, string? FireModeUpgrade = null);
