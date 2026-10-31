using SmFormats.Baf;
using SmSim;

namespace SmContent;

/// <summary>
/// Reads a multiplayer firearm's wargear file into a <see cref="WeaponSpec"/>: the basic shot's ability (the primary, or
/// the secondary when the primary is charge-only, as on the plasma pistol), default entries (those with no required
/// upgrade). Hit-scan and projectile deliveries are read (<see cref="WeaponSpec.Delivery"/>); a projectile needs its
/// blueprint from <c>ebps</c> (<paramref name="blueprints"/> in <see cref="Read"/>). Heat (<see cref="HeatSpec"/>) and
/// charged fire (<see cref="ChargeSpec"/>) are read; so is a shapecast cone (the melta, whose primary is a <c>pump_ranged</c>
/// ability with its delivery in its first pump state; <see cref="ConeSpec"/>) and a self-recharging clip
/// (<see cref="RechargeSpec"/>). Fidelity gap 7: the heavy bolter's braced fire (<see cref="BraceSpec"/>, its timing from the graphs by
/// <see cref="BraceTuning"/>), the plasma weapons' heat vent (<see cref="VentSpec"/>) and the overheat's speed modifier (<see cref="HeatSpec.OverheatSpeed"/>).
/// </summary>
public static class WeaponTuning
{
    const string Primary = "ability_ranged_primary", Secondary = "ability_ranged_secondary";

    /// <param name="blueprints">Resolves a blueprint path as the wargear names it (e.g. <c>ebps\projectiles\plasma_altfire_mp</c>);
    /// null or a null result: projectile shots are refused (the weapon) or dropped (a charge stage) with a note.</param>
    public static WeaponSpec? Read(string name, BafDocument weapon, List<string> notes, Func<string, BafDocument?>? blueprints = null)
    {
        var defs = weapon.Root.Table("ability_data/ability_definitions");
        // The basic shot: the primary when it has a delivery of its own, else the secondary (the plasma pistol charges on its primary).
        string? basicKey = new[] { Primary, Secondary }.FirstOrDefault(k => defs?.Table(k) is { } a && DeliveryOf(a) is not null);
        if (basicKey is null || defs?.Table(basicKey) is not { } basic) { notes.Add($"{name}: no ranged primary ability"); return null; }
        if (basicKey != Primary) notes.Add($"{name}: the primary is charge-only; the basic shot is read from {basicKey} (input mapping not established)");
        var delivery = DeliveryOf(basic);
        var typeEntry = DefaultEntry(delivery?.Table("ranged_delivery_type"), "entry");
        string? deliveryType = typeEntry?.Table("type")?.String("$ref");
        var kind = KindOf(deliveryType);
        if (kind == DeliveryKind.Cone && typeEntry?.Table("type/shape")?.String("$ref")?.EndsWith("cone", StringComparison.OrdinalIgnoreCase) != true) kind = null;
        if (kind is null) { notes.Add($"{name}: delivery '{deliveryType ?? "none"}' is neither a raycast nor a projectile, nor a cone shapecast; not simulated yet"); return null; }

        ProjectileSpec? projectile = null;
        if (kind == DeliveryKind.Projectile)
        {
            projectile = Projectile(name, typeEntry!.Value, blueprints, notes);
            if (projectile is null) return null;
        }
        var damage = DamageTable(delivery?.Table("hit_sim"), UpgradeScope.Current);
        ConeSpec? cone = null;
        if (kind == DeliveryKind.Cone && (cone = Cone(name, typeEntry!.Value.Table("type/shape")!.Value, delivery!.Value, damage, notes)) is null) return null;
        float general = damage?.Float("damage_general") ?? (kind == DeliveryKind.Projectile ? 0f : float.NaN);
        if (!float.IsFinite(general) || general < 0f || (kind == DeliveryKind.Raycast && general <= 0f))
        { notes.Add($"{name}: no hit-scan damage found"); return null; }
        var health = damage?.Table("damage_material_multipliers");
        var shield = damage?.Table("armourshield_damage_material_multipliers");
        var tuning = DefaultEntry(delivery?.Table("basic_weapon_tuning"), "entry");
        var reload = weapon.Root.Table("ability_data/ability_definitions/ability_reload/reload_upgrade_info");
        var reloadInfo = reload is { } r ? DefaultEntry(r, "wargear\\reload_info") : null;
        var ammo = DefaultEntry(weapon.Root.Table("common_data/ammunition/entry/by_upgrade"), null);   // M43: perk entries are named alike or "wargear\ammunition_info"
        // A clip that refills itself (the melta: ammo_recharged_per_second > 0); it has no reload ability.
        RechargeSpec? recharge = ammo?.Float("ammo_recharged_per_second") is float rate && float.IsFinite(rate) && rate > 0f
            ? new RechargeSpec(NonNegative(ammo.Value.Float("recharge_delay")), rate) : null;
        string? slot = weapon.Root.Table("common_data")?.String("slot");
        string? trigger = basic.Table("trigger_data/trigger")?.String("$ref");
        if (delivery?.Table("ammo_used") is { } used && used.Entries.Where(e => e.Type == BafType.Table).Any(e => Int(e.AsTable(), "amount") is < 0))
            notes.Add($"{name}: ammo_used amount -1 (meaning not established); 1 round per shot");

        float Get(BafTable? table, string field, float fallback, bool allowZero = false, bool quiet = false)
        {
            if (table?.Float(field) is { } v && float.IsFinite(v) && (v > 0 || (allowZero && v == 0))) return v;
            if (!quiet) notes.Add($"{name}: {field} missing; using {fallback}");
            return fallback;
        }
        // A projectile weapon without direct-hit damage (the plasma cannon) has no multipliers to read.
        bool noDirectHit = damage is null;
        int GetInt(BafTable? table, string field, int fallback)
        {
            if (table?[field] is { } e && (e.Type == BafType.Int ? e.Raw : (int)e.AsFloat()) is var v and > 0) return v;
            notes.Add($"{name}: {field} missing; using {fallback}");
            return fallback;
        }
        float maxRange = Get(delivery, "max_range", 250f);
        var heat = Heat(weapon, delivery, name, notes);
        var charge = Charge(name, weapon, defs.Value, blueprints, notes);
        // The grenade launcher's ability_utility (detonate_projectile, on the reload input): one delay per active grenade.
        float[] detonationDelays = [];
        if (defs.Value.Table("ability_utility") is { } utility
            && utility.String("$ref")?.EndsWith("detonate_projectile", StringComparison.OrdinalIgnoreCase) == true)
            detonationDelays = utility.Table("detonation_delays")?.Entries.Where(e => e.Name == "time" && e.Type != BafType.Table)
                .Select(e => e.AsFloat()).Where(t => float.IsFinite(t) && t >= 0f).ToArray() ?? [];
        if (trigger is null || !(trigger.EndsWith("semi_auto", StringComparison.OrdinalIgnoreCase) || trigger.EndsWith("full_auto", StringComparison.OrdinalIgnoreCase)))
            notes.Add($"{name}: trigger '{trigger ?? "none"}' is neither full nor semi auto; treated as full auto");
        // A cone uses its whole fall-off table (ConeSpec.FallOff); these fields are only informational for it.
        var (fallStart, fallEnd, fallMultiplier) = FallOff(delivery?.Table("damage_fall_off"), maxRange, name, kind == DeliveryKind.Cone ? [] : notes);
        return new WeaponSpec(
            name,
            slot == "pistol" ? WeaponSlot.Pistol : WeaponSlot.Ranged,
            trigger is not null && trigger.EndsWith("semi_auto", StringComparison.OrdinalIgnoreCase) ? TriggerKind.SemiAuto : TriggerKind.FullAuto,
            general,
            // The grenade launcher's cooldown is 0 (its clip of 1 and the reload pace it); hit-scan weapons need one.
            Get(tuning, "shot_cooldown", 0.2f, allowZero: kind == DeliveryKind.Projectile),
            GetInt(ammo, "capacity_clip", 30),
            Reserve(ammo, name, notes),
            recharge is not null ? recharge.RoundTime : Get(reloadInfo, "triggered_reload_time", 1.5f),
            maxRange,
            fallStart, fallEnd, fallMultiplier,
            Get(health, "default_head", 1f, allowZero: true, quiet: noDirectHit), Get(health, "default_body", 1f, allowZero: true, quiet: noDirectHit),
            Get(shield, "default_head", 1f, allowZero: true, quiet: noDirectHit), Get(shield, "default_body", 1f, allowZero: true, quiet: noDirectHit))
        {
            Heat = heat, Delivery = kind.Value, Projectile = projectile, AmmoPerShot = ShotAmmo(name, delivery, UpgradeScope.Current, notes), Charge = charge,
            DetonationDelays = detonationDelays, Cone = cone, Recharge = recharge,
            Pierce = kind == DeliveryKind.Raycast ? Pierce(name, delivery, UpgradeScope.Current, notes) : null,
            PerkPierces = kind == DeliveryKind.Raycast ? PerkPierces(name, delivery, notes) : [],
            HitType = TypeOf(damage),
            // M43: a burst (the stalker bolter's Burst Fire entry: 2 rounds 0.1 s apart); 0 and 1 are single shots.
            BurstLength = Math.Max(0, Int(tuning, "burst_length") ?? 0),
            BurstInterval = (Int(tuning, "burst_length") ?? 0) > 1 ? NonNegative(tuning?.Float("time_between_shots")) : 0f,
            Brace = Brace(name, weapon, defs.Value, general, maxRange, notes),
            Vent = heat is not null ? Vent(name, defs.Value, notes) : null,
        };
    }

    /// <summary>The braced ability's key (the heavy bolter's secondary fire mode; fidelity gap 7).</summary>
    public const string BracedAbility = "ability_ranged_braced";

    /// <summary>
    /// The braced fire (fidelity gap 7): <see cref="BracedAbility"/>'s delivery, read like the basic shot for the granted upgrades: its
    /// <c>basic_weapon_tuning</c> <c>shot_cooldown</c> and its <c>fire_sim</c> <c>apply_heat</c>; the fire mode string from
    /// <c>common_data/fire_mode_secondary</c>; <see cref="BraceSpec.Expertise"/> when Heavy Bolter Expertise's string is granted. Its damage and
    /// range are the primary's on the install; a difference is noted (not simulated). Null without the ability or a usable cooldown.
    /// </summary>
    static BraceSpec? Brace(string name, BafDocument weapon, BafTable defs, float primaryDamage, float primaryRange, List<string> notes)
    {
        if (defs.Table(BracedAbility) is not { } ability || DeliveryOf(ability) is not { } delivery) return null;
        float cooldown = Entry(delivery.Table("basic_weapon_tuning"), "entry", UpgradeScope.Current)?.Float("shot_cooldown") ?? float.NaN;
        if (!(float.IsFinite(cooldown) && cooldown > 0f)) { notes.Add($"{name}: {BracedAbility} without a shot cooldown; no brace"); return null; }
        float heat = HeatOf(delivery.Table("fire_sim"), UpgradeScope.Current) ?? 0f;
        if (DamageTable(delivery.Table("hit_sim"), UpgradeScope.Current)?.Float("damage_general") is float d && MathF.Abs(d - primaryDamage) > 1e-4f)
            notes.Add($"{name}: braced damage {d} differs from the primary's {primaryDamage}; the primary's is used");
        if (delivery.Float("max_range") is float r && MathF.Abs(r - primaryRange) > 1e-4f)
            notes.Add($"{name}: braced range {r} differs from the primary's {primaryRange}; the primary's is used");
        string mode = weapon.Root.Table("common_data/fire_mode_secondary/entry")?.String("upgrade_granted") ?? "";
        return new BraceSpec(cooldown, heat) { Expertise = UpgradeScope.Current.Contains(BraceSpec.ExpertiseUpgrade), FireMode = mode };
    }

    /// <summary>
    /// The upgrade strings that select the plasma pistol's upgraded vent (<c>ability_utility_upgraded</c>, the pistol graph's "Upgraded Vent State"):
    /// <c>mp_plasma_pistol_upgraded_vent</c>, and the Plasma Heat Sink perk's own string, for which both MP player blueprints
    /// (<c>sm_space_marine_player</c>, <c>csm_chaos_marine_player</c>: <c>entity_actions_ext</c> <c>actions_by_upgrade</c>) apply it.
    /// </summary>
    public static readonly IReadOnlyList<string> UpgradedVentStrings = ["mp_plasma_pistol_upgraded_vent", "mp_perk_weap_plasma_pistol_less_heat"];

    /// <summary>
    /// The heat vent (fidelity gap 7): <c>ability_utility_upgraded</c> when an <see cref="UpgradedVentStrings"/> string is granted and the file has
    /// one, else <c>ability_utility</c>, of type <c>heat_vent</c>: <c>heat_vented</c> (rates by heat fraction),
    /// <c>overheat_heat_venting_multiplier</c> and the <c>vent_stages</c>' <c>heat_percent</c> (low, high). Null without one.
    /// </summary>
    static VentSpec? Vent(string name, BafTable defs, List<string> notes)
    {
        static bool IsVent(BafTable? a) => a?.String("$ref")?.EndsWith("heat_vent", StringComparison.OrdinalIgnoreCase) == true;
        string key = UpgradedVentStrings.Any(UpgradeScope.Current.Contains) && IsVent(defs.Table("ability_utility_upgraded")) ? "ability_utility_upgraded" : "ability_utility";
        if (defs.Table(key) is not { } vent || !IsVent(vent)) return null;
        var rates = vent.Table("heat_vented")?.Entries.Where(e => e.Type == BafType.Table && e.Name == "entry")
            .Select(e => new DistancePoint(e.AsTable().Float("current_heat_percentage") ?? float.NaN, e.AsTable().Float("heat_to_vent_per_second") ?? float.NaN))
            .Where(p => float.IsFinite(p.Distance) && float.IsFinite(p.Value) && p.Distance >= 0f && p.Value >= 0f).OrderBy(p => p.Distance).ToList() ?? [];
        if (rates.Count == 0) { notes.Add($"{name}: {key} without heat_vented rates; no vent"); return null; }
        float multiplier = vent.Float("overheat_heat_venting_multiplier") is float m && float.IsFinite(m) && m >= 0f ? m : 1f;
        float Stage(string part, float fallback)
        {
            var stage = vent.Table("vent_stages")?.Entries.FirstOrDefault(e => e.Type == BafType.Table && e.Name.EndsWith(part, StringComparison.OrdinalIgnoreCase));
            if (stage?.AsTable().Float("heat_percent") is float f && float.IsFinite(f) && f >= 0f) return f;
            notes.Add($"{name}: vent stage {part} without heat_percent; {fallback}");
            return fallback;
        }
        return new VentSpec(rates, multiplier, Stage("low_heat", 0.01f), Stage("high_heat", 0.5f)) { Ability = key };
    }

    /// <summary>A <c>do_damage</c>'s <c>damagetype</c> (M43): "ranged", "melee", "explosive"; Unknown otherwise.</summary>
    internal static DamageType TypeOf(BafTable? doDamage) => doDamage?.String("damagetype") switch
    {
        "ranged" => DamageType.Ranged,
        "melee" => DamageType.Melee,
        "explosive" => DamageType.Explosive,
        _ => DamageType.Unknown,
    };

    /// <summary>An ability's delivery: its own, or (a <c>pump_ranged</c> ability, the melta) its first pump state's.</summary>
    static BafTable? DeliveryOf(BafTable ability) =>
        ability.Table("delivery")
        ?? ability.Table("pump_states")?.Entries.Where(e => e.Type == BafType.Table).Select(e => e.AsTable().Table("delivery")).FirstOrDefault(d => d is not null);

    /// <summary>
    /// A shapecast cone (<c>type/shape</c> of <c>shape\cone</c>): length, angle, start length and offset; the delivery's
    /// damage (<c>hit_sim</c> default), distance fall-off (<c>damage_fall_off</c>) and coverage fall-off
    /// (<c>coverage_fall_off</c>). Null with a note when the shape or damage is unusable.
    /// </summary>
    static ConeSpec? Cone(string name, BafTable shape, BafTable delivery, BafTable? damageTable, List<string> notes)
    {
        float length = shape.Float("shape_length") ?? float.NaN, angle = shape.Float("angle") ?? float.NaN;
        if (!(float.IsFinite(length) && length > 0f && float.IsFinite(angle) && angle > 0f && angle < 180f))
        { notes.Add($"{name}: cone without a usable length or angle"); return null; }
        float start = NonNegative(shape.Float("start_length")), offset = shape.Float("shape_offset") is float o && float.IsFinite(o) ? o : 0f;
        if (MathF.Abs(offset + start) > 1e-4f) notes.Add($"{name}: cone offset {offset} is not -start_length ({start}); the apex is placed start_length behind the muzzle");
        if (DamageOf(damageTable, name, notes) is not { Damage: > 0f } damage) { notes.Add($"{name}: cone without damage"); return null; }
        var fallOff = Points(delivery.Table("damage_fall_off"), "fall_off_entry", "damage_multiplier").OrderBy(p => p.Distance).ToList();
        var coverage = delivery.Table("coverage_fall_off")?.Entries.Where(e => e.Type == BafType.Table && e.Name == "fall_off_entry")
            .Select(e => new DistancePoint(e.AsTable().Float("coverage") ?? float.NaN, e.AsTable().Float("damage_multiplier") ?? float.NaN))
            .Where(p => float.IsFinite(p.Distance) && float.IsFinite(p.Value) && p.Distance >= 0f && p.Value >= 0f).OrderBy(p => p.Distance).ToList() ?? [];
        return new ConeSpec(length, angle, start, offset, damage, fallOff, coverage);
    }

    /// <summary>
    /// A thrown equipment item (the frag grenade): <c>ability_utility</c> with a projectile delivery; null with a note
    /// when the file has none or its blueprint cannot be resolved.
    /// </summary>
    public static ThrownSpec? ReadThrown(string name, BafDocument item, List<string> notes, Func<string, BafDocument?>? blueprints)
    {
        var delivery = item.Root.Table("ability_data/ability_definitions/ability_utility/delivery");
        var typeEntry = DefaultEntry(delivery?.Table("ranged_delivery_type"), "entry");
        if (typeEntry is null || KindOf(typeEntry.Value.Table("type")?.String("$ref")) != DeliveryKind.Projectile)
        { notes.Add($"{name}: no thrown projectile (ability_utility)"); return null; }
        if (Projectile(name, typeEntry.Value, blueprints, notes) is not { } projectile) return null;
        // The default ammunition entry is keyed "wargear\ammunition_info" on the grenades (perk entries "ammunition_info").
        var ammo = DefaultEntry(item.Root.Table("common_data/ammunition/entry/by_upgrade"), null);
        int count = Int(ammo, "capacity_clip") ?? 0;
        if (count <= 0) { notes.Add($"{name}: no grenade count"); return null; }
        float cooldown = DefaultEntry(delivery?.Table("basic_weapon_tuning"), "entry")?.Float("shot_cooldown") ?? 0f;
        if (item.Root.Table("common_data/ammunition/entry")?.String("ammunition_type") == BlindAmmunition)
            projectile = BlindGrenade(name, projectile, notes);
        return new ThrownSpec(name, count, Int(ammo, "initial_clip_amount") ?? -1, cooldown, delivery?.Float("max_range") ?? 0f, projectile);
    }

    /// <summary>The <c>common_data/ammunition/entry/ammunition_type</c> of the blind grenade (both sides' "krak" files).</summary>
    public const string BlindAmmunition = "blind_grenade";

    /// <summary>
    /// A blind grenade (M37; the owner's knowledge of the original, 2026-10-28: the files' "krak" grenade is the blind grenade,
    /// whose blast does no damage and no stun and blinds everyone who sees it, even at medium or longish range). Its blasts
    /// that blind (<c>do_blindness</c>) are kept with their radius, <c>ignore_self</c> and blindness, as
    /// <see cref="SplashSpec.BlindOnly"/> blasts of no damage and no reaction; the others, the direct-hit damage, every
    /// <c>do_damage</c>, <c>trigger_reaction</c> and <c>apply_stun_damage</c> are dropped. On the install: r 8 → 3.5 s and
    /// r 30 → 2 s (so 3.5 s within 8 m, 2 s to 30 m); the r 8 blast with an unmapped <c>cs_2xy</c> reaction is dropped.
    /// </summary>
    static ProjectileSpec BlindGrenade(string name, ProjectileSpec projectile, List<string> notes)
    {
        var none = new DamageSpec(0f, 0f, 1f, 1f, 1f, 1f, false);
        var all = projectile.AllSplashes.ToList();
        var blinding = all.Where(s => s.Blindness > 0f)
            .Select(s => s with { Damage = none, FallOff = [], Reactions = [], Lifetime = 0f, Period = 0f, BlindOnly = true })
            .OrderBy(s => s.Radius).ToList();
        int dropped = all.Count - blinding.Count;
        bool damaged = projectile.DetonateDamage is not null || all.Any(s => s.Damage.Damage > 0f || s.Damage.ShieldDamage > 0f) || all.Any(s => s.Reactions.Count > 0);
        notes.Add($"{name}: blind grenade (ammunition '{BlindAmmunition}'): {blinding.Count} blinding blast(s) [{string.Join(", ", blinding.Select(s => $"r {s.Radius} m: {s.Blindness} s"))}]" +
            $"{(damaged ? "; the file's damage and reactions are not applied (owner's knowledge: no damage, no stun)" : "")}{(dropped > 0 ? $"; {dropped} non-blinding blast(s) left out" : "")}");
        if (blinding.Count == 0) notes.Add($"{name}: blind grenade without a do_blindness blast; it does nothing");
        return projectile with { DetonateDamage = null, Splash = blinding.FirstOrDefault(), ExtraSplashes = blinding.Skip(1).ToArray() };
    }

    static DeliveryKind? KindOf(string? type) =>
        type is null ? null
        : type.EndsWith("raycast", StringComparison.OrdinalIgnoreCase) ? DeliveryKind.Raycast
        : type.EndsWith("projectile", StringComparison.OrdinalIgnoreCase) ? DeliveryKind.Projectile
        : type.EndsWith("shapecast", StringComparison.OrdinalIgnoreCase) ? DeliveryKind.Cone
        : null;

    /// <summary>
    /// The charged ability (a primary or secondary whose <c>$ref</c> is <c>…\charge_ranged</c>). The fire mode's granted
    /// upgrade (<c>common_data/fire_mode_secondary|primary/entry/upgrade_granted</c>) selects entries that need it (the
    /// lascannon's charged shot cooldown) — an **interpretation** of how fire modes apply.
    /// </summary>
    static ChargeSpec? Charge(string name, BafDocument weapon, BafTable defs, Func<string, BafDocument?>? blueprints, List<string> notes)
    {
        string? key = new[] { Primary, Secondary }.FirstOrDefault(k => defs.Table(k)?.String("$ref") is { } t && t.EndsWith("charge_ranged", StringComparison.OrdinalIgnoreCase));
        if (key is null || defs.Table(key) is not { } ability) return null;
        string? granted = weapon.Root.Table($"common_data/{(key == Secondary ? "fire_mode_secondary" : "fire_mode_primary")}/entry")?.String("upgrade_granted");
        // The fire mode's string and (M43) the perks' strings granted for this read.
        string[] upgrades = [.. (string.IsNullOrEmpty(granted) ? [] : new[] { granted }), .. UpgradeScope.Current];
        var stages = new List<ChargeStage>();
        foreach (var e in ability.Table("charge_states")?.Entries.Where(e => e.Type == BafType.Table) ?? [])
        {
            var s = e.AsTable();
            // Unused stage slots (e.g. "2.charge_dead_zone") are empty tables whose $ref is "".
            if (string.IsNullOrEmpty(s.String("$ref")) && s.Entries.All(x => x.Name == "$ref")) continue;
            float minTime = s.Float("minimum_charge_time") ?? float.NaN;
            if (!float.IsFinite(minTime) || minTime < 0f) { notes.Add($"{name}: charge stage {e.Name} has no minimum_charge_time; skipped"); continue; }
            float move = 1f;
            foreach (var m in s.Table("charging_action/commands/apply_modifiers/modifiers")?.Entries.Where(m => m.Type == BafType.Table && m.Name == "modifier") ?? [])
                if (m.AsTable().String("modifier_type") == "speed_maximum" && m.AsTable().String("operation_type") == "multiplication" && m.AsTable().Float("amount") is float a && float.IsFinite(a))
                    move *= a;
            var shot = s.Table("delivery") is { } d ? Shot(name, e.Name, d, upgrades, blueprints, notes) : null;
            stages.Add(new ChargeStage(e.Name, minTime, NonNegative(s.Float("heat_applied_per_second")), move, shot));
        }
        if (stages.Count == 0 || stages.All(s => s.Shot is null)) { notes.Add($"{name}: charged fire ({key}) has no stage with a shot; not read"); return null; }
        if (stages.Count(s => s.Shot is not null) > 1) notes.Add($"{name}: charged fire has several firing stages");
        var limits = Entry(ability.Table("charge_upgrade_list"), "upgradeable", upgrades);
        float multiplier = limits?.Float("min_charge_time_multiplier") is float k && float.IsFinite(k) && k > 0f ? k : 1f;
        int toStart = Int(ability.Table("trigger_data/ammunition/wargear\\ammunition_entry"), "required_to_start") ?? 1;
        return new ChargeSpec(key, stages, NonNegative(limits?.Float("max_charge_time")), multiplier,
            ability["reload_button_cancels_charging"] is { Type: BafType.Bool } c && c.AsBool(), toStart);
    }

    /// <summary>A charge stage's shot; null when the stage fires nothing (no delivery type).</summary>
    static ChargeShot? Shot(string name, string stage, BafTable delivery, IReadOnlyCollection<string> upgrades, Func<string, BafDocument?>? blueprints, List<string> notes)
    {
        var typeEntry = Entry(delivery.Table("ranged_delivery_type"), "entry", upgrades);
        if (typeEntry is null) return null;
        string? type = typeEntry.Value.Table("type")?.String("$ref");
        // A cone (shapecast) charge stage is not simulated: the stage is dropped as before M13.
        if (KindOf(type) is not { } kind || kind == DeliveryKind.Cone) { notes.Add($"{name}: charge stage {stage} delivery '{type}' not supported"); return null; }

        ProjectileSpec? projectile = null;
        if (kind == DeliveryKind.Projectile && (projectile = Projectile(name, typeEntry.Value, blueprints, notes)) is null) return null;
        var damage = DamageOf(DamageTable(delivery.Table("hit_sim"), upgrades), name, notes);
        if (kind == DeliveryKind.Raycast && damage is not { Damage: > 0f }) { notes.Add($"{name}: charge stage {stage} has no hit-scan damage"); return null; }
        float cooldown = Entry(delivery.Table("basic_weapon_tuning"), "entry", upgrades)?.Float("shot_cooldown") ?? float.NaN;
        if (!float.IsFinite(cooldown) || cooldown < 0f) { notes.Add($"{name}: charge stage {stage} shot_cooldown missing; using 0"); cooldown = 0f; }
        var fire = delivery.Table("fire_sim");
        return new ChargeShot(kind, damage, NonNegative(delivery.Float("max_range")), cooldown, ShotAmmo($"{name} {stage}", delivery, upgrades, notes),
            HeatOf(fire, upgrades) ?? 0f, NonNegative(fire?.Table("actions/delayed_action")?.Float("delay")), projectile)
        { Pierce = kind == DeliveryKind.Raycast ? Pierce($"{name} {stage}", delivery, upgrades, notes) : null };
    }

    /// <summary>The simulation material a prop is read as for <c>material_penetration_costs</c> (M39; interpretation: props have no blueprint naming one).</summary>
    public const string PropMaterial = "default";

    /// <summary>
    /// A hit-scan delivery's penetration (M39): its <c>penetration/entry</c> for <paramref name="upgrades"/> (none: the default
    /// entry), read when its <c>points</c> are above 0 and cover the <see cref="PropMaterial"/> cost; the exit cone from
    /// <c>exit_environment_action/commands/area_of_effect</c> (<c>shape</c> a cone; its <c>hit_actions/commands/do_damage</c>;
    /// <c>target_filter</c>). Null otherwise (the weapon stops at every surface); a missing or odd exit is noted.
    /// </summary>
    static PierceSpec? Pierce(string name, BafTable? delivery, IReadOnlyCollection<string> upgrades, List<string> notes) =>
        Entry(delivery?.Table("penetration"), "entry", upgrades) is { } entry ? PierceOf(name, entry, notes) : null;

    /// <summary>
    /// The perk entries of a delivery's <c>penetration</c> (M39): every entry that requires exactly one upgrade string (the
    /// bolter's <c>mp_perk_weap_bolter_kraken_rounds</c>), read as <see cref="PierceOf"/>, in file order.
    /// </summary>
    static List<UpgradePierce> PerkPierces(string name, BafTable? delivery, List<string> notes)
    {
        var list = new List<UpgradePierce>();
        foreach (var t in delivery?.Table("penetration")?.Entries.Where(e => e.Type == BafType.Table && e.Name == "entry").Select(e => e.AsTable()) ?? [])
            if (t.Table("required_upgrade_name/required_list")?.Entries.ToList() is [{ Type: BafType.String } only]
                && PierceOf($"{name} ({only.AsString()})", t, notes) is { } spec)
                list.Add(new UpgradePierce(only.AsString(), spec));
        return list;
    }

    /// <summary>
    /// One penetration entry: null when its <c>points</c> are not above 0. Costs from <c>material_penetration_costs</c>:
    /// <see cref="PropMaterial"/> for props, <c>default_body</c> and <c>default_head</c> for players (the player blueprints'
    /// <c>material_simulation</c>: body, arms and legs <c>default_body</c>, head <c>default_head</c>); a missing or negative
    /// cost is +∞ (never passed) with a note.
    /// </summary>
    static PierceSpec? PierceOf(string name, BafTable entry, List<string> notes)
    {
        float points = entry.Float("points") ?? 0f;
        if (!(float.IsFinite(points) && points > 0f)) return null;
        float Cost(string material)
        {
            float c = entry.Table("material_penetration_costs")?.Float(material) ?? float.NaN;
            if (float.IsFinite(c) && c >= 0f) return c;
            notes.Add($"{name}: penetration without a '{material}' cost; never passed");
            return float.PositiveInfinity;
        }
        float cost = Cost(PropMaterial), body = Cost("default_body"), head = Cost("default_head");
        ConeSpec? exit = null;
        bool all = false;
        if (entry.Table("exit_environment_action/commands/area_of_effect") is { } area)
        {
            all = area.String("target_filter") == "target_all";
            if (area.Table("shape") is { } shape && shape.String("$ref")?.EndsWith("cone", StringComparison.OrdinalIgnoreCase) == true)
            {
                float length = shape.Float("shape_length") ?? float.NaN, angle = shape.Float("angle") ?? float.NaN;
                var damage = DamageOf(area.Table("hit_actions/commands/do_damage"), name, notes);
                if (float.IsFinite(length) && length > 0f && float.IsFinite(angle) && angle > 0f && angle < 180f && damage is { Damage: > 0f })
                    exit = new ConeSpec(length, angle, NonNegative(shape.Float("start_length")), shape.Float("shape_offset") is float o && float.IsFinite(o) ? o : 0f,
                        damage, [], []);
                else notes.Add($"{name}: penetration exit cone without a usable length, angle or damage; nothing is hit past a prop");
            }
            else notes.Add($"{name}: penetration exit area is not a cone; nothing is hit past a prop");
            if (area["line_of_sight"] is { Type: BafType.Bool } los && !los.AsBool()) notes.Add($"{name}: penetration exit without line of sight (it is checked anyway)");
        }
        else notes.Add($"{name}: penetration without an exit area of effect; nothing is hit past a prop");
        return new PierceSpec(points, cost, exit, all) { BodyCost = body, HeadCost = head };
    }

    /// <summary>A projectile delivery's blueprint (<c>type/ebp</c>) read from <c>extensions/projectile_ext</c>.</summary>
    static ProjectileSpec? Projectile(string name, BafTable typeEntry, Func<string, BafDocument?>? blueprints, List<string> notes)
    {
        string? path = typeEntry.Table("type")?.String("ebp");
        if (string.IsNullOrEmpty(path)) { notes.Add($"{name}: projectile delivery without a blueprint"); return null; }
        if (blueprints?.Invoke(path) is not { } doc) { notes.Add($"{name}: projectile blueprint '{path}' not found"); return null; }
        if (doc.Root.Table("extensions/projectile_ext") is not { } p) { notes.Add($"{name}: '{path}' has no projectile_ext"); return null; }
        float speed = p.Float("speed") ?? float.NaN;
        if (!float.IsFinite(speed) || speed <= 0f) { notes.Add($"{name}: '{path}' has no speed"); return null; }
        static bool Bool(BafTable t, string f) => t[f] is { Type: BafType.Bool } e && e.AsBool();
        var gravity = Points(p.Table("gravity_factor_over_distance"), "entry", "gravity_factor");
        var detonate = p.Table("detonate_action/commands");
        // Every damaging blast in file order (the melta bomb has a lingering one, then a burst), each with its multiplayer
        // reactions and blindness (M28); then the blasts without do_damage that still react or blind (the krak grenade's
        // first), as extra splashes of no damage. Blasts with neither (the frag grenade's second: an unmapped reaction) are
        // left out. apply_stun_damage is not simulated: the player blueprints' stun_threshold (1e6) is never reached.
        var splashes = Blasts(detonate, $"{name}: '{path}'", notes);
        string collision = p.String("collision_type") ?? "";
        return new ProjectileSpec(path, speed, NonNegative(p.Float("lifetime")), collision,
            NonNegative(p.Table("raycast_shape")?.Float("radius")), gravity,
            Bool(p, "detonate_on_collision"), Bool(p, "detonate_when_lifetime_over"), Bool(p, "sticky"),
            Math.Max(0, Int(p, "max_bounce_count") ?? 0), NonNegative(p.Float("ground_lifetime")), p.Float("extra_aim_angle") is float x && float.IsFinite(x) ? x : 0f,
            Math.Max(0, Int(typeEntry.Table("type"), "max_active_projectiles") ?? 0),
            DamageOf(detonate?.Table("do_damage"), name, notes), splashes.Count > 0 ? splashes[0] : null)
        {
            ExtraSplashes = splashes.Skip(1).ToArray(),
            Rigid = collision == "rigidbody" ? Rigid(p.Table("rigid_info")) with
            {
                // fix/grenades: the Havok body material, read for the owner's shorter bounce (RigidSpec.GroundMaterial; unused by the data's rule)
                BodyFriction = doc.Root.Table("extensions/rigid_body_options_ext")?.Float("friction_override") ?? float.NaN,
                BodyRestitution = doc.Root.Table("extensions/rigid_body_options_ext")?.Float("restituation_override") ?? float.NaN,
            } : null,
            StopAtMaxBounce = collision == "rigidbody" && Bool(p, "stop_at_max_bounce"),
        };
    }

    /// <summary>
    /// The blasts of a command list (its <c>area_of_effect</c> children; a projectile's <c>detonate_action/commands</c>, a perk's
    /// delayed or triggered action): every damaging one in file order, each with its multiplayer reactions and blindness (M28);
    /// then those without <c>do_damage</c> that still react or blind, as blasts of no damage. Blasts with neither are left out.
    /// <paramref name="what"/> prefixes the notes.
    /// </summary>
    internal static List<SplashSpec> Blasts(BafTable? commands, string what, List<string> notes)
    {
        static bool Bool(BafTable t, string f) => t[f] is { Type: BafType.Bool } e && e.AsBool();
        var splashes = new List<SplashSpec>();
        var effectsOnly = new List<SplashSpec>();
        var blasts = commands?.Entries.Where(e => e.Type == BafType.Table && e.Name == "area_of_effect").Select(e => e.AsTable()).ToList() ?? [];
        foreach (var aoe in blasts)
        {
            bool damaging = aoe.Table("hit_actions/commands/do_damage") is not null;
            var (reactions, blind) = ReactionTuning.BlastEffects(aoe);
            if (!damaging && reactions.Count == 0 && blind <= 0f) continue;
            if (aoe.Table("shape")?.String("$ref") is { } shape && !shape.EndsWith("sphere", StringComparison.OrdinalIgnoreCase))
                notes.Add($"{what} blast shape '{shape}' read as a sphere");
            float radius = aoe.Table("shape")?.Float("radius") ?? 0f;
            var blast = damaging ? DamageOf(aoe.Table("hit_actions/commands/do_damage"), what, notes) : new DamageSpec(0f, 0f, 1f, 1f, 1f, 1f, false);
            if (blast is not null && float.IsFinite(radius) && radius > 0f)
                (damaging ? splashes : effectsOnly).Add(new SplashSpec(radius, blast, Points(aoe.Table("fall_off"), "fall_off_entry", "intensity_value"),
                    Bool(aoe, "line_of_sight"), Bool(aoe, "ignore_self"), aoe.String("target_filter") ?? "")
                {
                    Lifetime = NonNegative(aoe.Float("lifetime")), Period = NonNegative(aoe.Float("apply_every_this_many_seconds")),
                    Reactions = reactions, Blindness = blind,
                });
            else notes.Add($"{what} blast without radius or damage; no splash");
        }
        if (effectsOnly.Count > 0) notes.Add($"{what} {effectsOnly.Count} blast(s) without damage that react or blind, kept after the damaging ones (blinds or reacts)");
        splashes.AddRange(effectsOnly);
        return splashes;
    }

    /// <summary>A rigid body's <c>rigid_info</c> (missing values read as 0; ground angle 90 when missing: every surface is ground).</summary>
    static RigidSpec Rigid(BafTable? info)
    {
        static float Fraction(float? v) => v is float f && float.IsFinite(f) ? Math.Clamp(f, 0f, 1f) : 0f;
        List<DistancePoint> Map(string field) =>
            info?.Table($"{field}/listing")?.Entries.Where(e => e.Type == BafType.Table && e.Name == "map")
                .Select(e => new DistancePoint(e.AsTable().Float("angle") ?? float.NaN, e.AsTable().Float("reduction") ?? float.NaN))
                .Where(q => float.IsFinite(q.Distance) && float.IsFinite(q.Value) && q.Distance >= 0f).Select(q => q with { Value = Math.Clamp(q.Value, 0f, 1f) })
                .OrderBy(q => q.Distance).ToList() ?? [];
        float groundAngle = info?.Float("ground_angle") is float g && float.IsFinite(g) && g >= 0f && g <= 180f ? g : 90f;
        return new RigidSpec(groundAngle, Fraction(info?.Float("ground_hit_horizontal_reduction")), Fraction(info?.Float("ground_hit_vertical_reduction")),
            Fraction(info?.Float("entity_hit_horizontal_reduction")), Fraction(info?.Float("entity_hit_vertical_reduction")),
            Map("wall_hit_horizontal_reduction"), Map("wall_hit_vertical_reduction"),
            NonNegative(info?.Float("delay_before_collision")), NonNegative(info?.Float("delay_between_collision")));
    }


    /// <summary>The <c>do_damage</c> of a <c>hit_sim</c>: the selected <c>actions_by_upgrade</c> (or <c>actions</c>) entry's, else <c>actions/do_damage</c>.</summary>
    static BafTable? DamageTable(BafTable? hitSim, IReadOnlyCollection<string> upgrades) =>
        (Entry(hitSim?.Table("actions_by_upgrade"), "entry", upgrades) ?? Entry(hitSim?.Table("actions"), "entry", upgrades))?.Table("action/commands/do_damage")
        ?? hitSim?.Table("actions/do_damage");

    internal static DamageSpec? DamageOf(BafTable? doDamage, string name, List<string> notes)
    {
        if (doDamage is not { } d) return null;
        float F(BafTable? t, string field)
        {
            if (t?.Float(field) is float v && float.IsFinite(v) && v >= 0f) return v;
            notes.Add($"{name}: do_damage {field} missing; using 1");
            return 1f;
        }
        var health = d.Table("damage_material_multipliers");
        var shield = d.Table("armourshield_damage_material_multipliers");
        return new DamageSpec(NonNegative(d.Float("damage_general")), NonNegative(d.Float("damage_armourshield")),
            F(health, "default_head"), F(health, "default_body"), F(shield, "default_head"), F(shield, "default_body"),
            d["armourshield_breaker"] is { Type: BafType.Bool } b && b.AsBool()) { Type = TypeOf(d) };
    }

    /// <summary>Heat one shot adds: the selected <c>fire_sim/actions_by_upgrade</c> entry, <c>actions/apply_heat</c>, or an <c>actions_by_upgrade</c> command.</summary>
    static float? HeatOf(BafTable? fire, IReadOnlyCollection<string> upgrades)
    {
        if (fire is not { } f) return null;
        var apply = Entry(f.Table("actions_by_upgrade"), "entry", upgrades)?.Table("action/commands/apply_heat")
            ?? f.Table("actions/apply_heat")
            ?? Entry(f.Table("actions/actions_by_upgrade/actions"), "entry", upgrades)?.Table("action/commands/apply_heat");
        return apply?.Table("heat_generated/entry")?.Float("heat_to_generate") is float v && float.IsFinite(v) && v >= 0f ? v : null;
    }

    /// <summary>
    /// Rounds one shot of a delivery spends: its <c>ammo_used</c> amount (1 when missing) plus every <c>consume_ammo</c> its
    /// <c>fire_sim</c> runs (<see cref="FireSimAmmo"/>; sim/stormbolter-ammo: the storm bolters' twin barrel).
    /// </summary>
    static int ShotAmmo(string name, BafTable? delivery, IReadOnlyCollection<string> upgrades, List<string> notes)
    {
        int used = AmmoCost(delivery) ?? 1;
        string? ammunition = delivery?.Table("ammo_used")?.Entries.Where(e => e.Type == BafType.Table).Select(e => e.AsTable().String("ammunition_name")).FirstOrDefault(s => !string.IsNullOrEmpty(s));
        var (extra, delays) = FireSimAmmo(delivery?.Table("fire_sim"), ammunition, upgrades);
        if (extra > 0)
            notes.Add($"{name}: fire_sim consume_ammo {extra} more per shot ({used + extra} in all){(delays.Any(d => d > 0f) ? $", spent at the shot (the data's delay {string.Join(", ", delays.Select(d => d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))} s is not kept)" : "")}");
        return used + extra;
    }

    /// <summary>
    /// The rounds the <c>consume_ammo</c> commands of a <c>fire_sim</c> spend on each shot (sim/stormbolter-ammo), on top of the
    /// delivery's <c>ammo_used</c>: those in <c>actions</c> and in the <c>actions_by_upgrade</c> entry selected for
    /// <paramref name="upgrades"/>, at any depth (a <c>delayed_action</c>'s commands included; its delay is returned, not applied).
    /// A command naming another <c>ammunition_name</c> than <paramref name="ammunition"/> is not counted. On the install only the
    /// storm bolters (<c>sm_</c>, <c>csm_mp_ranged_storm_bolter</c>) have one: 1 round after 0.05 s, so 2 rounds per 0.1 s shot.
    /// </summary>
    internal static (int Rounds, List<float> Delays) FireSimAmmo(BafTable? fireSim, string? ammunition, IReadOnlyCollection<string> upgrades)
    {
        int rounds = 0;
        var delays = new List<float>();
        void Walk(BafTable? t, float delay)
        {
            if (t is not { } table) return;
            foreach (var e in table.Entries.Where(e => e.Type == BafType.Table))
            {
                var child = e.AsTable();
                if (e.Name == "actions_by_upgrade") continue;   // a nested upgrade list is not selected here
                if (e.Name == "consume_ammo" || child.String("$ref")?.EndsWith("consume_ammo", StringComparison.OrdinalIgnoreCase) == true)
                {
                    string? own = child.String("ammunition_name");
                    if (!string.IsNullOrEmpty(ammunition) && !string.IsNullOrEmpty(own) && !string.Equals(own, ammunition, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Int(child, "amount") is int n and > 0) { rounds += n; delays.Add(delay); }
                    continue;
                }
                float inner = child.String("$ref")?.EndsWith("delayed_action", StringComparison.OrdinalIgnoreCase) == true ? delay + NonNegative(child.Float("delay")) : delay;
                Walk(child, inner);
            }
        }
        Walk(fireSim?.Table("actions"), 0f);
        Walk(Entry(fireSim?.Table("actions_by_upgrade"), "entry", upgrades)?.Table("action"), 0f);
        return (rounds, delays);
    }

    static int? AmmoCost(BafTable? delivery) =>
        delivery?.Table("ammo_used")?.Entries.Where(e => e.Type == BafType.Table).Select(e => Int(e.AsTable(), "amount")).FirstOrDefault(v => v is > 0);

    internal static List<DistancePoint> Points(BafTable? table, string entry, string value) =>
        table?.Entries.Where(e => e.Type == BafType.Table && e.Name == entry)
            .Select(e => new DistancePoint(e.AsTable().Float("distance") ?? float.NaN, e.AsTable().Float(value) ?? float.NaN))
            .Where(p => float.IsFinite(p.Distance) && float.IsFinite(p.Value) && p.Distance >= 0f).ToList() ?? [];

    static float NonNegative(float? v) => v is float f && float.IsFinite(f) && f > 0f ? f : 0f;

    internal static int? Int(BafTable? t, string field) => t?[field] switch
    {
        { Type: BafType.Int } e => e.Raw,
        { Type: BafType.Float } e when float.IsFinite(e.AsFloat()) => (int)e.AsFloat(),
        _ => null,
    };

    /// <summary>
    /// Heat from <c>common_data/heat/wargear\heat_info</c> (the pool) and the basic shot's <c>fire_sim</c> <c>apply_heat</c>
    /// default entry (heat per shot); null when the weapon has no heat pool or its basic shot adds none.
    /// </summary>
    static HeatSpec? Heat(BafDocument weapon, BafTable? delivery, string name, List<string> notes)
    {
        var info = weapon.Root.Table("common_data/heat/wargear\\heat_info");
        if (info is not { } pool) return null;
        float perShot = 0f;
        if (delivery?.Table("fire_sim") is { } fire)
        {
            perShot = HeatOf(fire, UpgradeScope.Current) ?? 0f;
            // Each entry is keyed by current_heat_percentage; the install's weapons have one (at 0%), so the first is used.
            var apply = DefaultEntry(fire.Table("actions_by_upgrade"), "entry")?.Table("action/commands/apply_heat") ?? fire.Table("actions/apply_heat");
            if (apply?.Table("heat_generated")?.Entries.Count(e => e.Type == BafType.Table && e.Name == "entry") > 1)
                notes.Add($"{name}: heat per shot varies with current heat; only the first entry is used");
        }
        if (perShot <= 0f || !float.IsFinite(perShot)) { notes.Add($"{name}: heat pool but no heat per primary shot"); return null; }
        var cool = pool.Table("cool_down_upgrade_list/upgradeable");
        if (pool.Table("cool_down_upgrade_list")?.Entries.Count(e => e.Type == BafType.Table) > 1)
            notes.Add($"{name}: several cool-down variants; only the first is used");
        float F(BafTable? t, string field, float fallback)
        {
            if (t?.Float(field) is float v && float.IsFinite(v) && v >= 0f) return v;
            notes.Add($"{name}: heat {field} missing; using {fallback}");
            return fallback;
        }
        var heat = new HeatSpec(perShot, F(pool, "overheat_amount", 100f), F(pool, "high_heat_amount", 75f), F(pool, "recovery_amount", 0f),
            F(cool, "cool_down_rate", 25f), F(cool, "cool_down_delay_after_heat_gain", 0f), F(cool, "cool_down_delay_after_overheat", 0f));
        if (heat.Overheat <= 0f || heat.Recovery >= heat.Overheat || heat.CoolRate <= 0f) { notes.Add($"{name}: heat values unusable"); return null; }
        return OverheatModifiers(name, pool, heat, notes);
    }

    /// <summary>
    /// The overheat's modifiers (fidelity gap 7): <c>actions/overheat/commands/apply_modifiers</c> <c>speed_maximum</c> multiplications (their
    /// product; the plasma cannon 0.5) for its <c>lifetime</c> (1.5 s). Other modifiers are noted and not applied.
    /// </summary>
    static HeatSpec OverheatModifiers(string name, BafTable pool, HeatSpec heat, List<string> notes)
    {
        float speed = 1f, time = 0f;
        foreach (var apply in pool.Table("actions/overheat/commands")?.Entries.Where(e => e.Type == BafType.Table && e.Name == "apply_modifiers").Select(e => e.AsTable()) ?? [])
        {
            foreach (var m in apply.Table("modifiers")?.Entries.Where(e => e.Type == BafType.Table && e.Name == "modifier").Select(e => e.AsTable()) ?? [])
            {
                if (m.String("modifier_type") == "speed_maximum" && m.String("operation_type") == "multiplication" && m.Float("amount") is float a && float.IsFinite(a) && a >= 0f)
                    speed *= a;
                else notes.Add($"{name}: overheat modifier {m.String("modifier_type")} ({m.String("operation_type")}) not applied");
            }
            if (apply.Float("lifetime") is float l && float.IsFinite(l) && l > 0f) time = MathF.Max(time, l);
            else notes.Add($"{name}: overheat modifiers without a positive lifetime; not applied");
        }
        return time > 0f && speed != 1f ? heat with { OverheatSpeed = speed, OverheatSpeedTime = time } : heat;
    }

    /// <summary>The bolter's fields copied onto a <see cref="SimConfig"/> (the single-weapon fields predating loadouts).</summary>
    public static SimConfig Apply(SimConfig c, WeaponSpec w) => c with
    {
        Damage = w.Damage, ShotCooldown = w.ShotCooldown, ClipSize = w.ClipSize, ReserveSize = w.ReserveSize, ReloadTime = w.ReloadTime,
        MaxRange = w.MaxRange, FallOffStart = w.FallOffStart, FallOffEnd = w.FallOffEnd, FallOffEndMultiplier = w.FallOffEndMultiplier,
        HeadMultiplier = w.HeadMultiplier, BodyMultiplier = w.BodyMultiplier, ShieldHeadMultiplier = w.ShieldHeadMultiplier, ShieldBodyMultiplier = w.ShieldBodyMultiplier,
    };

    /// <summary>Reserve ammunition; a weapon without it (heat weapons) has none, which means it never reloads.</summary>
    static int Reserve(BafTable? ammo, string name, List<string> notes)
    {
        if (ammo?["capacity_reserve"] is { } e) return Math.Max(0, e.Type == BafType.Int ? e.Raw : (int)e.AsFloat());
        notes.Add($"{name}: no capacity_reserve; 0 (the clip is all it gets per life)");
        return 0;
    }

    /// <summary>The file's fall-off points as (last ×1 point, final point, final multiplier); no fall-off when all are ×1.</summary>
    static (float Start, float End, float Multiplier) FallOff(BafTable? table, float maxRange, string name, List<string> notes)
    {
        var points = table?.Entries.Where(e => e.Type == BafType.Table && e.Name == "fall_off_entry")
            .Select(e => (Distance: e.AsTable().Float("distance") ?? -1f, Multiplier: e.AsTable().Float("damage_multiplier") ?? -1f))
            .Where(p => p.Distance >= 0 && p.Multiplier >= 0 && float.IsFinite(p.Distance) && float.IsFinite(p.Multiplier)).OrderBy(p => p.Distance).ToList() ?? [];
        if (points.Count == 0 || points.All(p => p.Multiplier >= 1f)) return (maxRange, maxRange, 1f);
        int flat = points.FindLastIndex(p => p.Multiplier >= 1f);
        if (flat < 0 || flat == points.Count - 1) { notes.Add($"{name}: fall-off without a ×1 start; none applied"); return (maxRange, maxRange, 1f); }
        if (points.Skip(flat + 1).SkipLast(1).Any()) notes.Add($"{name}: fall-off has {points.Count} points; only the last ×1 and the final point are used");
        return (points[flat].Distance, points[^1].Distance, points[^1].Multiplier);
    }

    /// <summary>The child table named <paramref name="name"/> whose required-upgrade list is empty (the default variant).</summary>
    internal static BafTable? DefaultEntry(BafTable? parent, string? name) => Entry(parent, name, UpgradeScope.Current);

    /// <summary>
    /// The child table named <paramref name="name"/> (any name when null) whose required upgrades are all in
    /// <paramref name="upgrades"/>, preferring the one that requires the most; with no upgrades, the first default variant.
    /// </summary>
    internal static BafTable? Entry(BafTable? parent, string? name, IReadOnlyCollection<string> upgrades)
    {
        if (parent is not { } p) return null;
        BafTable? best = null;
        int bestCount = -1;
        foreach (var t in p.Entries.Where(e => e.Type == BafType.Table && (name is null || e.Name == name)).Select(e => e.AsTable()))
        {
            var required = t.Table("required_upgrade_name/required_list")?.Entries.ToList() ?? [];
            if (!required.All(e => e.Type == BafType.String && upgrades.Contains(e.AsString()))) continue;
            if (required.Count > bestCount) { best = t; bestCount = required.Count; }
        }
        return best;
    }
}
