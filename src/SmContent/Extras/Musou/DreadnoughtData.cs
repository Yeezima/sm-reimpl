using SmFormats.Baf;
using SmFormats.Havok;
using SmFormats.Obpk;

namespace SmContent.Extras.Musou;

/// <summary>The Dreadnought's blueprint body (<c>sm_dreadnought</c>): health, speeds, turning, hit volume and the unit's own flags. Null: the field is not in the file.</summary>
public sealed record DreadnoughtBody(float? HealthMax, float? HealthRegenDelay, float? HealthRegenRate, float? ShieldMax,
    float? RunSpeed, float? StrafeSpeed, float? BackSpeed, float? SprintSpeed, float? WalkSpeed,
    float? AimRunSpeed, float? AimStrafeSpeed, float? AimBackSpeed,
    float? CrouchRunSpeed, float? CrouchStrafeSpeed, float? CrouchBackSpeed, float? CrouchSprintSpeed, float? CrouchWalkSpeed,
    float? RotationMaxDegreesPerSecond, float? GroundTimeTillMaxSpeed, float? MaxWalkableSlope,
    float? AimHorizontalSpeed, float? AimVerticalSpeed, float? HitPhantomRadius, float? HitPhantomLength,
    float? ReceivedMelee, float? ReceivedRanged, float? ReceivedExplosive, float? ReceivedWorld,
    float? DeathTime, string? UnitSize);

/// <summary>A hit-scan weapon's values (the assault cannon or the melta): damage, rate, clip, reload, range.</summary>
public sealed record DreadnoughtGun(string Wargear, float? Damage, float? ArmourDamage, float? ShotCooldown, float? TimeBetweenShots, float? Range,
    int? ClipSize, int? Reserve, bool? ReserveInfinite, int? LowAmmo, float? RechargeDelay, float? RechargePerSecond, float? ReloadTime, float? AutoReloadTime,
    float? HeatPerShot, string? HeatName);

/// <summary>The melta's extras beside <see cref="DreadnoughtGun"/>: the <c>is_in_coop</c> variant's damage and the cooldown while <c>fury_aim_mode</c> is on.</summary>
public sealed record DreadnoughtMeltaExtras(float? CoopDamage, float? CoopArmourDamage, float? FuryShotCooldown);

/// <summary>One strike's hit: damage, armour (shield) damage and the stun damage it applies.</summary>
public sealed record DreadnoughtHit(float? Damage, float? ArmourDamage, float? StunDamage);

/// <summary>
/// A tag on a strike's clip (kind: Event, Branch, Action ...; <see cref="Seconds"/> from the clip's start, the sequencer's window start as a share of the clip's length).
/// </summary>
public sealed record DreadnoughtClipTag(string Kind, string? Name, float Seconds);

/// <summary>An arm strike (<c>dread_melee_default</c>): its ability, primary and secondary hit, reach and the clip it plays (length and tags from the rig, when read).</summary>
public sealed record DreadnoughtStrike(string Ability, DreadnoughtHit? Primary, DreadnoughtHit? Secondary, float? Range, string? Clip, float? ClipSeconds,
    IReadOnlyList<DreadnoughtClipTag> Tags);

/// <summary>An area action (the ground slam, the stomp attack, a footfall): damage at its centre, the area's radius and the stun damage.</summary>
public sealed record DreadnoughtArea(string Action, float? Damage, float? ArmourDamage, float? StunDamage, float? Radius, float? DebrisRadius);

/// <summary>The sprint ability (<c>dread_sprint</c>).</summary>
public sealed record DreadnoughtSprint(float? MaxSeconds, float? GapSeconds, float? MaxTurnRate, float? WallHitAngle);

/// <summary>The buff (<c>dread_buff</c>): its length, cooldown, regeneration multiplier, the upgrade strings it applies and its one-use ammunition.</summary>
public sealed record DreadnoughtBuff(float? Seconds, float? CooldownSeconds, float? RegenMultiplier, IReadOnlyList<string> Upgrades, int? Clip, int? Reserve);

/// <summary>The entrance (<c>evt_spawn_in</c>): how long received damage is multiplied by 0, the clip's length, and the control point's heal (<c>evt_dread_cap</c>).</summary>
public sealed record DreadnoughtEntrance(float? InvulnerableSeconds, float? ReceivedMultiplier, float? ClipSeconds, float? ControlPointHeal);

/// <summary>
/// The Musou Dreadnought's data (<c>docs/musou-dreadnought-design.md</c>, <c>docs/discovery.md</c> "Dreadnought (Dreadnought Assault)"): the loyalist <c>sm_dreadnought</c> blueprint (<c>ebps.oppc</c>) and the <c>dread_*</c> wargear
/// (<c>wargear.oppc</c>) read with the existing BAF reader; the strike clips' lengths and tags from the rig (<c>havok_sm_dreadnought_basic__hkxx</c>) when it can be read. Read-only; nothing is cached or written. Every
/// number is **data** (kind: data, confidence: Verified-local); what Musou Mode does with it is <c>DreadnoughtRules</c>' (health, regeneration and capsule are the owner's estimates).
/// </summary>
public sealed record DreadnoughtData(DreadnoughtBody Body, DreadnoughtGun? Cannon, DreadnoughtGun? Melta, DreadnoughtMeltaExtras? MeltaExtras,
    IReadOnlyList<DreadnoughtStrike> Strikes, IReadOnlyList<DreadnoughtArea> Areas, DreadnoughtSprint? Sprint, DreadnoughtBuff? Buff, DreadnoughtEntrance Entrance,
    IReadOnlyList<string> Notes)
{
    public const string Blueprint = "sm_dreadnought";
    public const string Arm = "dread_melee_default", AssaultCannon = "dread_assault_cannon", MeltaGun = "dread_melta", SprintAbility = "dread_sprint", BuffGear = "dread_buff";
    public static IReadOnlyList<string> Wargear { get; } = [Arm, AssaultCannon, MeltaGun, SprintAbility, BuffGear];

    /// <summary>The rig the strike clips are read from, and the graph holding their sequencers.</summary>
    public const string RigArchive = "data/chunks/ebp_sets__mp_versus_set.oppc", RigEntry = "havok_sm_dreadnought_basic__hkxx", Graph = "sm_dreadnought_mp";
    /// <summary>The entrance clip (<c>DN_NAV_Entrance</c>).</summary>
    public const string EntranceClip = "DN_NAV_Entrance";

    /// <summary>The arm's strikes in the graph's order: ability name, the clip it plays (the 3.5 m variant).</summary>
    public static IReadOnlyList<(string Ability, string Clip)> StrikeClips { get; } =
    [
        ("melee_x01", "DN_MEL_XATK_01_3.5m"), ("melee_x02", "DN_MEL_XATK_02_3.5m"), ("melee_y01", "DN_MEL_YATK_3.5m"),
        ("melee_y02_stomp", "DN_MEL_Stomp_3.5m"), ("melee_x_rush", "DN_MEL_Bullrush_Attack"),
    ];

    /// <summary>
    /// Reads the data from the install (read-only). A part that cannot be read is null (or empty) with a note; the rig is optional (without it the strikes carry no clip length or tags).
    /// </summary>
    public static DreadnoughtData Read(string gameDir, List<string>? notes = null)
    {
        notes ??= [];
        BafDocument? blueprint = null;
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == Blueprint, (_, d) => blueprint = BafDocument.Read(d));
        var names = Wargear.ToHashSet(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && names.Contains(e.FileName), (e, d) => { if (!wargear.ContainsKey(e.FileName)) wargear[e.FileName] = BafDocument.Read(d); });   // the install holds two files of the melta and the buff (the Chaos twin's; same numbers): the first read is kept
        Func<string, float>? length = null;
        IReadOnlyList<BehaviorGraph> graphs = [];
        try
        {
            var character = new RigProfile("dreadnought", RigArchive, RigEntry, "").LoadCharacter(gameDir);
            length = clip => character.LoadAnimation(clip).Animation.Duration;
            graphs = HavokBehavior.Read(character.Pack);
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or FileNotFoundException or ArgumentException or IndexOutOfRangeException or NotSupportedException)
        { notes.Add($"dreadnought: rig: {e.Message}; no clip lengths or tags"); }
        return From(blueprint, wargear, length, graphs, notes);
    }

    /// <summary>The data from already-read files and rig accessors (testable without an install).</summary>
    public static DreadnoughtData From(BafDocument? blueprint, IReadOnlyDictionary<string, BafDocument> wargear, Func<string, float>? clipLength,
        IReadOnlyList<BehaviorGraph> graphs, List<string> notes)
    {
        if (blueprint is null) notes.Add($"dreadnought: blueprint {Blueprint} not found");
        var bp = blueprint is null ? BafFlat.Empty : new BafFlat(blueprint);
        var body = new DreadnoughtBody(
            bp.Num("health_ext/health_max"), bp.Num("health_ext/health_regen_delay"), bp.Num("health_ext/health_regen_rate"),
            bp.Num("health_ext/armourshield_info/armourshield_info/armourshield_max"),
            bp.Num("speed_standing/run_speed"), bp.Num("speed_standing/strafe_speed"), bp.Num("speed_standing/back_speed"), bp.Num("speed_standing/sprint_speed"), bp.Num("speed_standing/walk_speed"),
            bp.Num("speed_aiming/run_speed"), bp.Num("speed_aiming/strafe_speed"), bp.Num("speed_aiming/back_speed"),
            bp.Num("speed_crouching/run_speed"), bp.Num("speed_crouching/strafe_speed"), bp.Num("speed_crouching/back_speed"), bp.Num("speed_crouching/sprint_speed"), bp.Num("speed_crouching/walk_speed"),
            bp.Num("biped_locomotion_ext/rotation_max_degrees_per_second"), bp.Num("biped_locomotion_ext/ground_time_till_max_speed"), bp.Num("max_walkable_slope"),
            bp.Num("weapon_ext/aim_horizontal_speed"), bp.Num("weapon_ext/aim_vertical_speed"), bp.Num("animation/hit_phantom_radius"), bp.Num("animation/hit_phantom_length"),
            bp.Num("health_ext/take_damage_multiplier/melee"), bp.Num("health_ext/take_damage_multiplier/ranged"), bp.Num("health_ext/take_damage_multiplier/explosive"), bp.Num("health_ext/take_damage_multiplier/world"),
            bp.Num("health_ext/death_time"), bp.Str("entity_ext/unit_size") ?? bp.Str("unit_size"));

        BafFlat Gear(string name, string noteName)
        {
            if (wargear.TryGetValue(name, out var doc)) return new BafFlat(doc);
            notes.Add($"dreadnought: wargear {noteName} not found");
            return BafFlat.Empty;
        }

        var cannonFile = Gear(AssaultCannon, AssaultCannon);
        DreadnoughtGun? cannon = cannonFile.IsEmpty ? null : new DreadnoughtGun(AssaultCannon,
            cannonFile.Num("do_damage/damage_general", "ability_ranged_primary"), cannonFile.Num("do_damage/damage_armourshield", "ability_ranged_primary"),
            cannonFile.Num("basic_weapon_tuning/entry/shot_cooldown"), cannonFile.Num("basic_weapon_tuning/entry/time_between_shots"), cannonFile.Num("ability_ranged_primary/delivery/max_range"),
            cannonFile.Int("ammunition_info/capacity_clip"), cannonFile.Int("ammunition_info/capacity_reserve"), cannonFile.Bool("ammunition_info/capacity_reserve_infinite"),
            cannonFile.Int("ammunition_info/low_ammunition_amount"), cannonFile.Num("ammunition_info/recharge_delay"), cannonFile.Num("ammunition_info/ammo_recharged_per_second"),
            cannonFile.Num("triggered_reload_time"), cannonFile.Num("auto_reload_time"), cannonFile.Num("heat_generated/entry/heat_to_generate"), cannonFile.Str("apply_heat/heat_name"));

        var meltaFile = Gear(MeltaGun, MeltaGun);
        DreadnoughtGun? melta = meltaFile.IsEmpty ? null : new DreadnoughtGun(MeltaGun,
            meltaFile.Num("do_damage/damage_general", "pump_states"), meltaFile.Num("do_damage/damage_armourshield", "pump_states"),
            meltaFile.Num("basic_weapon_tuning/entry/shot_cooldown"), meltaFile.Num("basic_weapon_tuning/entry/time_between_shots"), meltaFile.Num("delivery/max_range", "pump_states"),
            meltaFile.Int("ammunition_info/capacity_clip"), meltaFile.Int("ammunition_info/capacity_reserve"), meltaFile.Bool("ammunition_info/capacity_reserve_infinite"),
            meltaFile.Int("ammunition_info/low_ammunition_amount"), meltaFile.Num("ammunition_info/recharge_delay"), meltaFile.Num("ammunition_info/ammo_recharged_per_second"),
            meltaFile.Num("triggered_reload_time"), meltaFile.Num("auto_reload_time"), null, null);
        var meltaExtras = meltaFile.IsEmpty ? null : new DreadnoughtMeltaExtras(
            meltaFile.Num("do_damage/damage_general", "actions_by_upgrade"), meltaFile.Num("do_damage/damage_armourshield", "actions_by_upgrade"),
            meltaFile.Num("basic_weapon_tuning/entry/shot_cooldown", nth: 1));

        var armFile = Gear(Arm, Arm);
        var strikes = new List<DreadnoughtStrike>();
        var areas = new List<DreadnoughtArea>();
        if (!armFile.IsEmpty)
        {
            foreach (var (ability, clip) in StrikeClips)
            {
                string under = "ability_definitions/" + ability + "/";
                DreadnoughtHit? Hit(string which)
                {
                    string at = under + which + "/";
                    var h = new DreadnoughtHit(armFile.Num("do_damage/damage_general", at), armFile.Num("do_damage/damage_armourshield", at), armFile.Num("apply_stun_damage/stun_damage", at));
                    return h.Damage is null && h.ArmourDamage is null && h.StunDamage is null ? null : h;
                }
                float? seconds = null;
                IReadOnlyList<DreadnoughtClipTag> tags = [];
                if (clipLength is not null)
                {
                    try { seconds = clipLength(clip); }
                    catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException or NotSupportedException)
                    { notes.Add($"dreadnought: {clip}: {e.Message}"); }
                    var seq = graphs.FirstOrDefault(g => g.Name.Equals(Graph, StringComparison.OrdinalIgnoreCase))?.Sequencers
                        .FirstOrDefault(s => s.Animations.Any(a => a.Equals(clip, StringComparison.OrdinalIgnoreCase)));
                    if (seq is not null && seconds is float len)
                        tags = seq.Tags.Select(t => new DreadnoughtClipTag(t.Kind, t.Name, Math.Clamp(t.Start, 0f, 1f) * len)).ToArray();
                }
                strikes.Add(new DreadnoughtStrike(ability, Hit("primary_target_actions"), Hit("secondary_target_actions"), armFile.Num("generic_metadata/max_range", under), clip, seconds, tags));
            }
            foreach (var action in new[] { "actn_pound_hit", "actn_stomp_hit" })
            {
                string at = "triggered_actions/" + action + "/";
                areas.Add(new DreadnoughtArea(action, armFile.Num("do_damage/damage_general", at), armFile.Num("do_damage/damage_armourshield", at), armFile.Num("apply_stun_damage/stun_damage", at),
                    armFile.Num("area_of_effect/shape/radius", at), armFile.Num("prop_debris_force/shape/radius", at)));
            }
        }
        foreach (var action in new[] { "evt_spawn_r_stomp", "evt_spawn_l_stomp" })
        {
            string at = "triggered_actions/" + action + "/";
            if (bp.IsEmpty) break;
            areas.Add(new DreadnoughtArea(action, bp.Num("do_damage/damage_general", at), bp.Num("do_damage/damage_armourshield", at), bp.Num("apply_stun_damage/stun_damage", at),
                bp.Num("area_of_effect/shape/radius", at), bp.Num("prop_debris_force/shape/radius", at)));
        }

        var sprintFile = Gear(SprintAbility, SprintAbility);
        var sprint = sprintFile.IsEmpty ? null : new DreadnoughtSprint(sprintFile.Num("special_nav_ability/max_time_limit"), sprintFile.Num("special_nav_ability/min_time_between_activations"),
            sprintFile.Num("ability_sprint/max_turn_rate"), sprintFile.Num("special_nav_ability/max_horz_angle_for_wall_hit"));

        var buffFile = Gear(BuffGear, BuffGear);
        var buff = buffFile.IsEmpty ? null : new DreadnoughtBuff(buffFile.Num("ability_buff/duration"), buffFile.Num("ability_buff/min_time_between_activations"), buffFile.Num("ability_buff/regen_multiplier"),
            buffFile.Strings("apply_upgrade_string/strings/entry"), buffFile.Int("ammunition_info/capacity_clip"), buffFile.Int("ammunition_info/capacity_reserve"));

        float? entranceClip = null;
        if (clipLength is not null)
        {
            try { entranceClip = clipLength(EntranceClip); }
            catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException or NotSupportedException)
            { notes.Add($"dreadnought: {EntranceClip}: {e.Message}"); }
        }
        var entrance = new DreadnoughtEntrance(bp.Num("apply_modifiers/lifetime", "triggered_actions/evt_spawn_in/"), bp.Num("apply_modifiers/modifiers/modifier/amount", "triggered_actions/evt_spawn_in/"),
            entranceClip, bp.Num("do_heal/health", "triggered_actions/evt_dread_cap/"));
        return new DreadnoughtData(body, cannon, melta, meltaExtras, strikes, areas, sprint, buff, entrance, notes);
    }

    /// <summary>A BAF file's leaves by path: the lookups the reader needs (first match in file order; a path suffix under a path prefix).</summary>
    sealed class BafFlat
    {
        public static readonly BafFlat Empty = new();
        readonly List<(string Path, BafEntry Entry)> _leaves = [];
        BafFlat() { }
        public BafFlat(BafDocument doc) => Walk(doc.Root, "", 0);
        public bool IsEmpty => _leaves.Count == 0;

        void Walk(BafTable table, string path, int depth)
        {
            if (depth > 40) return;
            foreach (var e in table.Entries)
            {
                string p = path.Length == 0 ? e.Name : path + "/" + e.Name;
                if (e.Type == BafType.Table) Walk(e.AsTable(), p, depth + 1);
                else _leaves.Add((p, e));
            }
        }

        /// <summary>Whether <paramref name="path"/> ends with <paramref name="suffix"/> on a segment boundary (a segment may carry a template prefix such as <c>wargearmmunition_info</c>).</summary>
        static bool Ends(string path, string suffix) =>
            path == suffix || (path.EndsWith(suffix, StringComparison.Ordinal) && path[path.Length - suffix.Length - 1] is '/' or '\\');

        /// <summary>The nth leaf whose path ends with <paramref name="suffix"/> (on a path boundary) and contains <paramref name="under"/>, or null.</summary>
        BafEntry? Find(string suffix, string? under, int nth)
        {
            int seen = 0;
            foreach (var (path, entry) in _leaves)
            {
                if (!Ends(path, suffix)) continue;
                if (under is not null && !path.Contains(under, StringComparison.Ordinal)) continue;
                if (seen++ == nth) return entry;
            }
            return null;
        }

        public float? Num(string suffix, string? under = null, int nth = 0) => Find(suffix, under, nth) switch
        {
            { Type: BafType.Float } e when float.IsFinite(e.AsFloat()) => e.AsFloat(),
            { Type: BafType.Int } e => e.Raw,
            _ => null,
        };
        public int? Int(string suffix, string? under = null, int nth = 0) => Num(suffix, under, nth) is float v ? (int)v : null;
        public bool? Bool(string suffix, string? under = null) => Find(suffix, under, 0) is { Type: BafType.Bool } e ? e.AsBool() : null;
        public string? Str(string suffix, string? under = null) => Find(suffix, under, 0) is { Type: BafType.String } e ? e.AsString() : null;
        public IReadOnlyList<string> Strings(string suffix) =>
            _leaves.Where(l => l.Entry.Type == BafType.String && Ends(l.Path, suffix)).Select(l => l.Entry.AsString()).ToArray();
    }
}
