using System.Numerics;
using SmFormats.Baf;
using SmFormats.Bod;
using SmFormats.Fx;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>
/// A <c>play_fx</c> command (<c>action_commands\play_fx</c>, M25a): the effect (<see cref="Package"/>, <see cref="Name"/>: a
/// PSystem), its scale and length, where it starts (<see cref="TriggerPoint"/> "self", "hitpoint" or "other", and the
/// <see cref="Marker"/> it hangs on, e.g. <c>fx_muzzle</c>; <see cref="Attached"/>: it follows the marker), where it ends
/// (<see cref="TargetPoint"/>, "hitpoint" for tracers; empty: no target), the rotation source and direction
/// (<c>negative_hit_direction</c>, <c>hitpoint_normal</c>, <c>up</c>), lifetime (s; 0: the effect's own), stop policy and
/// the player-only flags. Values as the data has them.
/// </summary>
public sealed record FxPlay(string Package, string Name, float Scale, float Length, bool Attached, string TriggerPoint, string Marker, string TargetPoint,
    string TargetMarker, string RotationSource, string Direction, float Lifetime, string OnStop, bool OnlyIfOnPlayer, bool ScaleIfFromPlayer)
{
    public bool HasTarget => TargetPoint.Length > 0;

    /// <summary>Reads a <c>play_fx</c> table (missing fields: empty, 0 or false; scale and length default to 1).</summary>
    public static FxPlay Read(BafTable t) => new(t.String("fx_package") ?? "", t.String("fx_name") ?? "", t.Float("scale") ?? 1f, t.Float("length") ?? 1f,
        Flag(t, "attach_to_marker"), t.String("trigger_point") ?? "", t.Table("marker")?.String("marker") ?? "", t.String("trigger_point_target") ?? "",
        t.Table("marker_target")?.String("marker") ?? "", t.String("rotation_source") ?? "", t.String("trigger_direction") ?? "", t.Float("lifetime") ?? 0f,
        t.String("on_stop_policy") ?? "", Flag(t, "apply_only_if_on_player"), Flag(t, "scale_if_from_player"));

    internal static bool Flag(BafTable t, string name) => t[name] is { Type: BafType.Bool } e && e.AsBool();
}

/// <summary>A <c>trigger_material_effect</c> command: a material effect container (by path) and where the material comes from.</summary>
public sealed record FxMaterialTrigger(string Container, string MaterialSource)
{
    /// <summary>The container's file name (the path's last part; the archive keeps only base names).</summary>
    public string ContainerName => Container.Split('\\', '/')[^1];
}

/// <summary>
/// One effect command found in a wargear or blueprint file: its table <see cref="Path"/> (from the file's root), the event it
/// belongs to (<see cref="Event"/>: the nearest enclosing table named <c>*_fx</c> or <c>*_action</c>, e.g. <c>fire_fx</c>,
/// <c>hit_fx</c>, <c>stop_fire_fx</c>, <c>initial_tracer_fx</c>, <c>detonate_action</c>, <c>lifetime_action</c>; else the
/// table that holds its <c>commands</c>/<c>actions</c>, e.g. <c>high_heat</c>), the ability it sits in (<see cref="Ability"/>,
/// empty outside <c>ability_definitions</c>), the upgrades it needs (empty: the default variant) and the command.
/// </summary>
public sealed record FxBinding(string Path, string Event, string Ability, IReadOnlyList<string> RequiredUpgrades, FxPlay? Play, FxMaterialTrigger? Material)
{
    public bool IsDefault => RequiredUpgrades.Count == 0;
    public bool InDelivery => Path.Contains("/delivery/", StringComparison.Ordinal);
    /// <summary>The charge state the command sits in (the table under <c>charge_states</c>, e.g. "3.charge_medium"), empty outside one.</summary>
    public string ChargeStage
    {
        get
        {
            var parts = Path.Split('/');
            int at = Array.IndexOf(parts, "charge_states");
            return at >= 0 && at + 1 < parts.Length - 1 ? parts[at + 1] : "";
        }
    }

    static readonly HashSet<string> Structural = new(StringComparer.Ordinal) { "actions", "commands", "actions_by_upgrade", "entry", "action" };

    /// <summary>Every <c>play_fx</c> and <c>trigger_material_effect</c> command in a file, in file order.</summary>
    public static IReadOnlyList<FxBinding> Collect(BafTable root)
    {
        var found = new List<FxBinding>();
        Walk(root, [], [], found, 0);
        return found;
    }

    static void Walk(BafTable table, List<string> path, List<string> upgrades, List<FxBinding> found, int depth)
    {
        if (depth > 40) return;
        foreach (var e in table.Entries)
        {
            if (e.Type != BafType.Table) continue;
            var child = e.AsTable();
            string kind = child.String("$ref") ?? "";
            path.Add(e.Name);
            var required = child.Table("required_upgrade_name/required_list")?.Entries.Where(x => x.Type == BafType.String).Select(x => x.AsString()).ToList() ?? [];
            upgrades.AddRange(required);
            if (kind.EndsWith("action_commands\\play_fx", StringComparison.OrdinalIgnoreCase))
                found.Add(new FxBinding(string.Join("/", path), EventOf(path), AbilityOf(path), upgrades.Distinct().ToList(), FxPlay.Read(child), null));
            else if (kind.EndsWith("action_commands\\trigger_material_effect", StringComparison.OrdinalIgnoreCase))
                found.Add(new FxBinding(string.Join("/", path), EventOf(path), AbilityOf(path), upgrades.Distinct().ToList(), null,
                    new FxMaterialTrigger(child.String("effect_container") ?? "", child.String("material_source") ?? "")));
            else Walk(child, path, upgrades, found, depth + 1);
            upgrades.RemoveRange(upgrades.Count - required.Count, required.Count);
            path.RemoveAt(path.Count - 1);
        }
    }

    static string EventOf(List<string> path)
    {
        for (int i = path.Count - 2; i >= 0; i--)
            if (path[i].EndsWith("_fx", StringComparison.Ordinal) || path[i].EndsWith("_action", StringComparison.Ordinal)) return path[i];
        for (int i = path.Count - 2; i >= 0; i--)
            if (!Structural.Contains(path[i])) return path[i];
        return "";
    }

    static string AbilityOf(List<string> path)
    {
        int at = path.IndexOf("ability_definitions");
        return at >= 0 && at + 1 < path.Count - 1 ? path[at + 1] : "";
    }
}

/// <summary>
/// A material effect container (<c>material_effect_container.oppc</c>, M25a): per hit surface and damage class
/// (<c>fx/&lt;surface&gt;/&lt;damage class&gt;</c>) the effects that play, and the hit sound event (<c>sfx/start_event_name</c>).
/// </summary>
public sealed record MaterialEffectTable(string Name, string SoundEvent, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<FxPlay>>> Effects)
{
    public const string DefaultKey = "default";

    public static MaterialEffectTable Read(string name, BafDocument doc)
    {
        var effects = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<FxPlay>>>(StringComparer.Ordinal);
        if (doc.Root.Table("fx") is { } fx)
            foreach (var surface in fx.Entries.Where(e => e.Type == BafType.Table))
            {
                var classes = new Dictionary<string, IReadOnlyList<FxPlay>>(StringComparer.Ordinal);
                foreach (var cls in surface.AsTable().Entries.Where(e => e.Type == BafType.Table))
                    classes[cls.Name] = FxBinding.Collect(cls.AsTable()).Where(b => b.Play is not null && b.IsDefault).Select(b => b.Play!).ToList();
                effects[surface.Name] = classes;
            }
        return new MaterialEffectTable(name, doc.Root.Table("sfx")?.String("start_event_name") ?? "", effects);
    }

    /// <summary>
    /// The effects for a hit on <paramref name="surface"/> with <paramref name="damageClass"/>; **interpretation** of the
    /// fall-backs: the first non-empty of (surface, class), (surface, default), (default, class), (default, default).
    /// </summary>
    public IReadOnlyList<FxPlay> Resolve(string surface, string damageClass)
    {
        foreach (var (s, c) in new[] { (surface, damageClass), (surface, DefaultKey), (DefaultKey, damageClass), (DefaultKey, DefaultKey) })
            if (Effects.TryGetValue(s, out var classes) && classes.TryGetValue(c, out var plays) && plays.Count > 0) return plays;
        return [];
    }
}

/// <summary>
/// The surface names (M25a). The level collision's material ids (<see cref="StaticMesh.MaterialOf"/>) are read as indices
/// into this list (**interpretation**, supported by evidence: every material table in the install, e.g. global's
/// <c>collision_fx</c> material table and the weapon hit containers, lists its surface keys in this order; the ids on the 11
/// MP maps are 0, 1, 2, 3, 4 and 7: default, dirt, gravel, stone, metal and air, never the character surfaces 5, 6 or 10 to 11).
/// </summary>
public static class Surfaces
{
    /// <summary>The order the data's tables use (global's <c>collision_fx</c> table on the install).</summary>
    public static readonly IReadOnlyList<string> Default =
        ["default", "dirt", "gravel", "stone", "metal", "armour", "flesh", "air", "energy_shield", "sand", "ceramite_armour", "broken_ceramite"];

    /// <summary>Where the order is read from (global; its <c>fx/default</c> table lists every surface).</summary>
    public const string OrderTable = "extensions/fx_environment_manager_ext/collision_fx/collision_small/material_table/fx/default";

    /// <summary>The surface keys of global's collision material table, in order; null when absent.</summary>
    public static IReadOnlyList<string>? ReadOrder(BafDocument global) =>
        global.Root.Table(OrderTable)?.Entries.Where(e => e.Type == BafType.Table).Select(e => e.Name).ToList() is { Count: > 0 } names ? names : null;

    /// <summary>The surface name of a level material id: <paramref name="order"/>[id]; "default" for -1 or an id outside the list.</summary>
    public static string NameOf(int id, IReadOnlyList<string>? order = null)
    {
        order ??= Default;
        return id >= 0 && id < order.Count ? order[id] : MaterialEffectTable.DefaultKey;
    }
}

/// <summary>
/// How a character reads to hit effects (the player blueprint's <c>entity_ext</c>): its surface
/// (<c>material_audio_visual</c>, "broken_ceramite" for the MP marine) and damage class (<c>material_simulation</c>), with
/// per-zone overrides (<c>material_overrides</c>: hit zone → class and surface).
/// <para>
/// M38 (hit feedback): the blueprint's <c>health_ext/armourshield_info</c> also says what a hit reads as <b>while the armour shield is up</b>:
/// <see cref="ArmourSurface"/> (<c>armourshield_material</c>, "ceramite_armour" for the MP marines; the entity's own <c>broken_ceramite</c> is
/// the shield-down, flesh-penetrating surface), and the effects played on the victim for a hit on raised armour
/// (<see cref="ArmourHit"/>, <c>armourshield_hit</c>: <c>sm_armor_dmg</c>, shoulder pads, body glows) or on broken armour
/// (<see cref="ArmourInactiveHit"/>, <c>armourshield_inactive_hit</c>: the red variants).
/// </para>
/// </summary>
public sealed record CharacterSurface(string Surface, string DamageClass, IReadOnlyDictionary<string, (string Surface, string DamageClass)> Zones,
    string ArmourSurface = "ceramite_armour", IReadOnlyList<FxPlay>? ArmourHitEffects = null, IReadOnlyList<FxPlay>? ArmourInactiveHitEffects = null)
{
    public static readonly CharacterSurface Unknown = new("broken_ceramite", "default_body", new Dictionary<string, (string, string)>());

    /// <summary>Effects on the victim of a hit that struck the raised shield (empty when the blueprint names none).</summary>
    public IReadOnlyList<FxPlay> ArmourHit => ArmourHitEffects ?? [];
    /// <summary>Effects on the victim of a hit that struck broken armour (empty when the blueprint names none).</summary>
    public IReadOnlyList<FxPlay> ArmourInactiveHit => ArmourInactiveHitEffects ?? [];

    public static CharacterSurface? Read(BafDocument blueprint)
    {
        if (blueprint.Root.Table("extensions/entity_ext") is not { } e) return null;
        string surface = e.String("material_audio_visual") ?? MaterialEffectTable.DefaultKey, cls = e.String("material_simulation") ?? MaterialEffectTable.DefaultKey;
        var zones = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var o in e.Table("material_overrides")?.Entries.Where(x => x.Type == BafType.Table).Select(x => x.AsTable()) ?? [])
            if (o.String("hit_zone") is { Length: > 0 } zone)
                zones.TryAdd(zone, (o.String("material_audio_visual") ?? surface, o.String("material_simulation") ?? cls));
        // health_ext/armourshield_info holds one entry (named armourshield_info again; the default variant is the first).
        string armour = "ceramite_armour";
        IReadOnlyList<FxPlay> hit = [], inactive = [];
        if (blueprint.Root.Table("extensions/health_ext/armourshield_info") is { } info
            && info.Entries.FirstOrDefault(x => x.Type == BafType.Table) is { } first)
        {
            var entry = first.AsTable();
            armour = entry.String("armourshield_material") is { Length: > 0 } m ? m : armour;
            hit = Plays(entry.Table("armourshield_hit"));
            inactive = Plays(entry.Table("armourshield_inactive_hit"));
        }
        return new CharacterSurface(surface, cls, zones, armour, hit, inactive);

        static IReadOnlyList<FxPlay> Plays(BafTable? t) => t is not { } table ? [] : FxBinding.Collect(table).Where(b => b.Play is not null && b.IsDefault).Select(b => b.Play!).ToList();
    }

    /// <summary>
    /// The surface and damage class for a simulated hit zone (Head: "head", Body and others: "body"). <paramref name="armourUp"/> (M38): true takes the
    /// blueprint's armour-shield surface instead of the entity's own (shield up: ceramite armour; shield down or unknown (null): the entity's, "broken_ceramite").
    /// </summary>
    public (string Surface, string DamageClass) For(HitZone zone, bool? armourUp = null)
    {
        var (surface, cls) = Zones.TryGetValue(zone == HitZone.Head ? "head" : "body", out var z) ? z : (Surface, DamageClass);
        return armourUp == true ? (ArmourSurface, cls) : (surface, cls);
    }
}

/// <summary>A projectile's effects (its blueprint): the trail while it flies (<c>lifetime_action</c>) and the detonation (<c>detonate_action</c>).</summary>
/// <param name="Blueprint">The blueprint path the wargear names.</param>
public sealed record ProjectileEffects(string Blueprint, IReadOnlyList<FxBinding> Bindings)
{
    public IReadOnlyList<FxPlay> Trail => Bindings.Where(b => b.IsDefault && b.Event == "lifetime_action" && b.Play is not null).Select(b => b.Play!).ToList();
    public IReadOnlyList<FxPlay> Detonation => Bindings.Where(b => b.IsDefault && b.Event == "detonate_action" && b.Play is not null).Select(b => b.Play!).ToList();
    /// <summary>The detonation's material effect container (the grenade launcher's: explosion by the surface it lands on), or null.</summary>
    public FxMaterialTrigger? DetonationMaterial => Bindings.FirstOrDefault(b => b.IsDefault && b.Event == "detonate_action" && b.Material is not null)?.Material;
}

/// <summary>
/// A weapon's effects (M25a), from its wargear file: every effect command (<see cref="Bindings"/>) and, for the basic shot
/// (the ability <c>WeaponTuning</c> simulates: the primary when it has a delivery, else the secondary), the muzzle flash,
/// shell ejection, tracer, barrel smoke and hit container; plus the projectile's effects for a projectile weapon.
/// Chosen among default variants (no upgrade needed), first in file order.
/// </summary>
public sealed record WeaponEffects(string Wargear, string BasicAbility, string MuzzleMarker, float MaxRange, IReadOnlyList<FxBinding> Bindings, ProjectileEffects? Projectile)
{
    /// <summary>
    /// Every projectile the file launches (any delivery: the basic shot's and the charged stages', e.g. the plasma gun's
    /// charged shot), by the blueprint path the wargear names, in file order.
    /// </summary>
    public IReadOnlyList<ProjectileEffects> Projectiles { get; init; } = [];

    /// <summary>The basic shot's effects (<see cref="BasicAbility"/>, outside charge stages).</summary>
    public ShotEffects Basic => Shot(BasicAbility, null);

    /// <summary>
    /// A shot's effects: the default delivery commands of <paramref name="ability"/> in charge stage <paramref name="chargeStage"/>
    /// (a <see cref="ChargeStage.Name"/>, e.g. "3.charge_medium"; null: outside charge stages). Shot stage k of the simulation
    /// (<see cref="Shots"/>) is charge stage k − 1 of the weapon's <see cref="ChargeSpec"/>.
    /// </summary>
    public ShotEffects Shot(string ability, string? chargeStage)
    {
        var bindings = Bindings.Where(b => b.IsDefault && b.Ability == ability && b.InDelivery && b.ChargeStage == (chargeStage ?? "")).ToList();
        return ShotEffects.From(bindings, MuzzleMarker);
    }

    /// <summary>The basic shot's fire effects (<c>fire_fx</c>): muzzle flash, shell, and for the melta its blast cone.</summary>
    public IReadOnlyList<FxPlay> Fire => Basic.Fire;
    /// <summary>The basic shot's muzzle flash (<see cref="ShotEffects.Muzzle"/>).</summary>
    public FxPlay? Muzzle => Basic.Muzzle;
    /// <summary>The basic shot's shell casing.</summary>
    public FxPlay? ShellEject => Basic.ShellEject;
    /// <summary>The basic shot's tracer (<see cref="ShotEffects.Tracer"/>).</summary>
    public FxPlay? Tracer => Basic.Tracer;
    /// <summary>Barrel smoke when firing stops (<c>stop_fire_fx</c>).</summary>
    public IReadOnlyList<FxPlay> StopFire => Basic.StopFire;
    /// <summary>The container of the basic shot's impacts (its <c>hit_fx</c> material effect), or null.</summary>
    public FxMaterialTrigger? HitMaterial => Basic.HitMaterial;

    public static WeaponEffects Read(string wargear, BafDocument doc, Func<string, BafDocument?>? blueprints, List<string> notes)
    {
        var bindings = FxBinding.Collect(doc.Root);
        var defs = doc.Root.Table("ability_data/ability_definitions");
        string basic = new[] { "ability_ranged_primary", "ability_ranged_secondary" }.FirstOrDefault(k => defs?.Table(k) is { } a && DeliveryOf(a) is not null) ?? "";
        var delivery = basic.Length > 0 ? DeliveryOf(defs!.Value.Table(basic)!.Value) : null;
        string marker = delivery?.String("muzzle_marker") ?? "";
        float range = delivery?.Float("max_range") ?? 0f;
        var projectiles = new List<ProjectileEffects>();
        foreach (var path in EffectCatalogue.Strings(doc.Root, "ebp").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (blueprints?.Invoke(path) is { } bp) projectiles.Add(new ProjectileEffects(path, FxBinding.Collect(bp.Root)));
            else notes.Add($"{wargear}: projectile blueprint '{path}' not found; no effects for it");
        }
        var typeEntry = WeaponTuning.DefaultEntry(delivery?.Table("ranged_delivery_type"), "entry");
        string? basicPath = typeEntry?.Table("type")?.String("ebp");
        var projectile = projectiles.FirstOrDefault(p => p.Blueprint.Equals(basicPath, StringComparison.OrdinalIgnoreCase));
        if (basic.Length == 0) notes.Add($"{wargear}: no ranged ability with a delivery; only its effect commands are listed");
        return new WeaponEffects(wargear, basic, marker, range, bindings, projectile) { Projectiles = projectiles };
    }

    /// <summary>The ability's delivery (the melta's sits in its pump state), as <c>WeaponTuning</c> finds it.</summary>
    static BafTable? DeliveryOf(BafTable ability) =>
        ability.Table("delivery")
        ?? ability.Table("pump_states")?.Entries.Where(e => e.Type == BafType.Table).Select(e => e.AsTable().Table("delivery")).FirstOrDefault(d => d is not null);
}

/// <summary>
/// One shot's effects (a delivery's default commands): fire effects, muzzle flash, shell casing, tracer, barrel smoke on
/// stopping, and the impact container.
/// </summary>
public sealed record ShotEffects(IReadOnlyList<FxPlay> Fire, FxPlay? Muzzle, FxPlay? ShellEject, FxPlay? Tracer, IReadOnlyList<FxPlay> StopFire, FxMaterialTrigger? HitMaterial)
{
    /// <summary>
    /// From a delivery's bindings. Muzzle: the first fire effect named <c>*muzzle*</c>, else the first without a target on
    /// <paramref name="muzzleMarker"/> (none for the melta, whose fire effects are vents, a light and its blast). Shell:
    /// <c>shell_casing</c> or a fire effect on a <c>*shell*</c> marker. Tracer: the first hit effect with a target
    /// (<c>hit_fx</c>; the lascannon's <c>initial_tracer_fx</c>), else a fire effect with a target (the melta's blast).
    /// Impacts: the first material effect of <c>hit_fx</c> or <c>initial_tracer_fx</c>.
    /// </summary>
    public static ShotEffects From(IReadOnlyList<FxBinding> bindings, string muzzleMarker)
    {
        IEnumerable<FxBinding> Of(string evt) => bindings.Where(b => b.Event == evt);
        var fire = Of("fire_fx").Where(b => b.Play is not null).Select(b => b.Play!).ToList();
        var muzzle = fire.FirstOrDefault(p => p.Name.Contains("muzzle", StringComparison.OrdinalIgnoreCase))
            ?? fire.FirstOrDefault(p => !p.HasTarget && !IsShell(p) && p.Marker.Length > 0 && p.Marker.Equals(muzzleMarker, StringComparison.OrdinalIgnoreCase));
        var hits = Of("hit_fx").Concat(Of("initial_tracer_fx")).ToList();
        var tracer = hits.Select(b => b.Play).FirstOrDefault(p => p is { HasTarget: true }) ?? fire.FirstOrDefault(p => p.HasTarget);
        return new ShotEffects(fire, muzzle, fire.FirstOrDefault(IsShell), tracer, Of("stop_fire_fx").Where(b => b.Play is not null).Select(b => b.Play!).ToList(),
            hits.FirstOrDefault(b => b.Material is not null)?.Material);
    }

    static bool IsShell(FxPlay p) => p.Name.Equals("shell_casing", StringComparison.OrdinalIgnoreCase) || p.Marker.Contains("shell", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The thrown item's effects (grenades: the item's own commands and its projectile's trail and detonation), from the
/// equipment wargear and the blueprint its <c>ability_utility</c> delivery launches.
/// </summary>
public sealed record ThrownEffects(string Wargear, IReadOnlyList<FxBinding> Bindings, ProjectileEffects? Projectile)
{
    public static ThrownEffects Read(string wargear, BafDocument doc, Func<string, BafDocument?>? blueprints, List<string> notes)
    {
        var delivery = doc.Root.Table("ability_data/ability_definitions/ability_utility/delivery");
        var typeEntry = WeaponTuning.DefaultEntry(delivery?.Table("ranged_delivery_type"), "entry");
        ProjectileEffects? projectile = null;
        if (typeEntry?.Table("type")?.String("ebp") is { Length: > 0 } path)
        {
            if (blueprints?.Invoke(path) is { } bp) projectile = new ProjectileEffects(path, FxBinding.Collect(bp.Root));
            else notes.Add($"{wargear}: projectile blueprint '{path}' not found; no projectile effects");
        }
        return new ThrownEffects(wargear, FxBinding.Collect(doc.Root), projectile);
    }
}

/// <summary>
/// Weapon and impact effects for the multiplayer classes of both sides (M25a; read at runtime from the install, nothing
/// copied): per wargear file its <see cref="WeaponEffects"/> or <see cref="ThrownEffects"/>, the material effect containers
/// they name, the surface order level material ids index, and how a player reads to hit effects. The effects themselves
/// (PSystems, atlas) are in <see cref="FxLibrary"/>.
/// </summary>
public sealed class EffectCatalogue
{
    public const string MaterialEffectPackage = "data/packages/material_effect_container.oppc";

    public required IReadOnlyDictionary<string, WeaponEffects> Weapons { get; init; }
    public required IReadOnlyDictionary<string, ThrownEffects> Thrown { get; init; }
    /// <summary>
    /// Containers by the path the files name them (e.g. <c>material_effect_container\weapon_hits\space_marines\ranged_weapons\bolter</c>;
    /// matched ignoring case). The archive keeps only base names, and some repeat (a Space Marine and a Chaos variant): see
    /// <see cref="ChooseContainer"/>.
    /// </summary>
    public required IReadOnlyDictionary<string, MaterialEffectTable> Containers { get; init; }
    public required IReadOnlyList<string> SurfaceOrder { get; init; }
    public required CharacterSurface Player { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    /// <summary>The jump packs' ground effects by wargear name (M41: the ground pound's impact and the landings).</summary>
    public IReadOnlyDictionary<string, JumpPackEffects> JumpPacks { get; init; } = new Dictionary<string, JumpPackEffects>();

    public JumpPackEffects? JumpPack(string wargear) => JumpPacks.GetValueOrDefault(wargear);

    /// <summary>M44: the jump-pack dash's effects by jump evade file (<see cref="JumpPackEffects.ReadDash"/>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<FxPlay>> Dashes { get; init; } = new Dictionary<string, IReadOnlyList<FxPlay>>();

    /// <summary>What the player blueprint's <c>finish_falling_actions</c> plays and shakes when a fall ends (M-anim P1: <c>jump_land</c> at scale 0.5 and a small camera shake).</summary>
    public FallLanding FinishFalling { get; init; } = FallLanding.None;
    public WeaponEffects? Weapon(string wargear) => Weapons.GetValueOrDefault(wargear);
    public MaterialEffectTable? Container(FxMaterialTrigger? trigger) => trigger is null ? null : Containers.GetValueOrDefault(trigger.Container);

    /// <summary>
    /// Which of the containers sharing a base name a path means (**interpretation**; the folders are lost in the archive): for
    /// a path with a <c>chaos</c> folder, the candidate with the most effects named <c>chaos_*</c> or <c>csm_*</c>; otherwise the
    /// one with the fewest; ties go to the first in archive order. Checked on the install: each repeated name is a Space Marine
    /// and a Chaos variant whose effects differ only by those prefixes, except <c>stalker_pattern_bolter</c> and
    /// <c>stalker_pattern_bolter_enter</c>, <c>power_axe</c> and <c>foot_fist</c>, where the first is taken.
    /// </summary>
    public static int ChooseContainer(string path, IReadOnlyList<MaterialEffectTable> candidates)
    {
        if (candidates.Count <= 1) return 0;
        bool chaos = path.Split('\\', '/').Any(p => p.Equals("chaos", StringComparison.OrdinalIgnoreCase));
        var counts = candidates.Select(t => t.Effects.Values.SelectMany(c => c.Values).SelectMany(p => p)
            .Count(p => p.Name.StartsWith("chaos_", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("csm_", StringComparison.OrdinalIgnoreCase))).ToList();
        int best = 0;
        for (int i = 1; i < counts.Count; i++)
            if (chaos ? counts[i] > counts[best] : counts[i] < counts[best]) best = i;
        return best;
    }

    /// <summary>The surface name of a level material id (<see cref="Surfaces.NameOf"/> with the install's order).</summary>
    public string SurfaceName(int id) => Surfaces.NameOf(id, SurfaceOrder);

    /// <summary>
    /// The impact effects of a weapon's shot that hit <paramref name="hit"/>: a world hit by its surface with damage class
    /// "default" (interpretation), a player hit by the player's surface and the zone's class; none for a miss.
    /// </summary>
    public IReadOnlyList<FxPlay> Impact(string wargear, ShotHit hit, int surface, bool? armourUp = null) => Resolve(Weapon(wargear)?.HitMaterial, hit, surface, armourUp);

    /// <summary>
    /// The effects a material trigger plays for a hit (as <see cref="Impact"/>; e.g. a projectile's
    /// <see cref="ProjectileEffects.DetonationMaterial"/> with the surface it went off on). Empty for none or an unknown container.
    /// </summary>
    /// <param name="armourUp">
    /// M38, for a player hit: true = the victim's armour shield was up (the container's <see cref="CharacterSurface.ArmourSurface"/> entry, "ceramite_armour":
    /// bullets breaking armour); false = it was down (the entity's own "broken_ceramite" entry: flesh-penetrating); null = unknown (as false: the choice before M38).
    /// </param>
    public IReadOnlyList<FxPlay> Resolve(FxMaterialTrigger? trigger, ShotHit hit, int surface, bool? armourUp = null)
    {
        if (hit == ShotHit.None || Container(trigger) is not { } table) return [];
        var (name, cls) = hit == ShotHit.World ? (SurfaceName(surface), MaterialEffectTable.DefaultKey) : Player.For(hit == ShotHit.Head ? HitZone.Head : HitZone.Body, armourUp);
        return table.Resolve(name, cls);
    }

    /// <summary>
    /// The catalogue for every wargear file the MP classes name (both sides), with the projectile blueprints, containers,
    /// surface order and the player blueprint from the install.
    /// </summary>
    public static EffectCatalogue Load(string gameDir)
    {
        BafDocument? global = null;
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, MpClasses.GlobalPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == "global", (_, d) => global = BafDocument.Read(d));
        if (global is null) throw new FileNotFoundException($"global not found in {MpClasses.GlobalPackage}.");
        var names = MpClasses.ReadClasses(global).SelectMany(c => c.BaseWargear.Concat(c.StartupWargear)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && names.Contains(e.FileName), (e, d) => wargear[e.FileName] = BafDocument.Read(d));

        // Projectile blueprints the wargear names (any "ebp" string), and the player blueprint.
        var wanted = wargear.Values.SelectMany(d => Strings(d.Root, "ebp")).Select(Leaf).Append(GameplayTuning.MarineBlueprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blueprints = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && wanted.Contains(e.FileName), (e, d) =>
            {
                if (!blueprints.TryAdd(e.FileName, BafDocument.Read(d))) notes.Add($"ebps: several blueprints named {e.FileName}; the first is used");
            });

        // Every container any of those files names, by base name (the archive keeps no folders: every file of a name is a candidate).
        var containerNames = wargear.Values.Concat(blueprints.Values).SelectMany(d => Strings(d.Root, "effect_container")).Select(Leaf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var containers = new Dictionary<string, List<BafDocument>>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, MaterialEffectPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && containerNames.Contains(e.FileName), (e, d) =>
            {
                if (!containers.TryGetValue(e.FileName, out var list)) containers[e.FileName] = list = [];
                list.Add(BafDocument.Read(d));
            });
        return Build(wargear, path => blueprints.GetValueOrDefault(Leaf(path)), containers.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<BafDocument>)kv.Value, StringComparer.OrdinalIgnoreCase),
            global, blueprints.GetValueOrDefault(GameplayTuning.MarineBlueprint), notes);
    }

    /// <summary>The catalogue from already-read files (testable without an install).</summary>
    /// <param name="blueprints">Projectile blueprints by the path the wargear names.</param>
    /// <param name="containers">Material effect containers by file name, each name with every file of that name in archive order.</param>
    /// <param name="global">For the surface order (null: <see cref="Surfaces.Default"/>).</param>
    /// <param name="player">The MP player blueprint (null: <see cref="CharacterSurface.Unknown"/>).</param>
    public static EffectCatalogue Build(IReadOnlyDictionary<string, BafDocument> wargear, Func<string, BafDocument?>? blueprints,
        IReadOnlyDictionary<string, IReadOnlyList<BafDocument>> containers, BafDocument? global, BafDocument? player, List<string>? notes = null)
    {
        notes ??= [];
        var weapons = new Dictionary<string, WeaponEffects>(StringComparer.OrdinalIgnoreCase);
        var thrown = new Dictionary<string, ThrownEffects>(StringComparer.OrdinalIgnoreCase);
        var jumpPacks = new Dictionary<string, JumpPackEffects>(StringComparer.OrdinalIgnoreCase);
        var dashes = new Dictionary<string, IReadOnlyList<FxPlay>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in wargear.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (JumpPackEffects.Read(name, doc) is { } pack) jumpPacks[name] = pack;
            if (JumpPackEffects.ReadDash(doc) is { Count: > 0 } dash) dashes[name] = dash;
            string? slot = doc.Root.Table("common_data")?.String("slot");
            bool utility = doc.Root.Table("ability_data/ability_definitions/ability_utility/delivery") is not null;
            if (slot is "ranged" or "pistol") weapons[name] = WeaponEffects.Read(name, doc, blueprints, notes);
            else if ((utility || StimTuning.IsStim(doc)) && slot is not "melee") thrown[name] = ThrownEffects.Read(name, doc, blueprints, notes);   // perks/inert-2: the stims' effects too
        }
        // Every container path any binding names (weapons, their projectiles, thrown items), resolved to one file.
        var candidates = containers.ToDictionary(kv => kv.Key, kv => kv.Value.Select(d => MaterialEffectTable.Read(kv.Key, d)).ToList(), StringComparer.OrdinalIgnoreCase);
        var paths = weapons.Values.SelectMany(w => w.Bindings.Concat(w.Projectiles.SelectMany(p => p.Bindings)))
            .Concat(thrown.Values.SelectMany(t => t.Bindings.Concat(t.Projectile?.Bindings ?? [])))
            .Select(b => b.Material?.Container).OfType<string>().Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);
        var tables = new Dictionary<string, MaterialEffectTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var trigger = new FxMaterialTrigger(path, "");
            if (!candidates.TryGetValue(trigger.ContainerName, out var list) || list.Count == 0) { notes.Add($"container '{path}' not found"); continue; }
            int chosen = ChooseContainer(path, list);
            if (list.Count > 1) notes.Add($"container '{path}': {list.Count} files named {trigger.ContainerName}; number {chosen + 1} chosen (ChooseContainer)");
            tables[path] = list[chosen];
        }
        var order = global is null ? null : Surfaces.ReadOrder(global);
        if (global is not null && order is null) notes.Add("global has no collision material table; the default surface order is used");
        if (order is not null && !order.SequenceEqual(Surfaces.Default)) notes.Add($"surface order differs from the documented one: {string.Join(", ", order)}");
        var surface = player is null ? null : CharacterSurface.Read(player);
        if (surface is null) notes.Add("no player blueprint surface; broken_ceramite / default_body assumed");
        return new EffectCatalogue
        {
            Weapons = weapons, Thrown = thrown, Containers = tables, SurfaceOrder = order ?? Surfaces.Default, Player = surface ?? CharacterSurface.Unknown, Notes = notes,
            JumpPacks = jumpPacks, Dashes = dashes, FinishFalling = player is null ? FallLanding.None : FallLanding.Read(player),
        };
    }

    static string Leaf(string path) => path.Split('\\', '/')[^1];

    internal static IEnumerable<string> Strings(BafTable table, string name, int depth = 0)
    {
        if (depth > 40) yield break;
        foreach (var e in table.Entries)
        {
            if (e.Type == BafType.String && e.Name == name && e.AsString().Length > 0) yield return e.AsString();
            else if (e.Type == BafType.Table) foreach (var s in Strings(e.AsTable(), name, depth + 1)) yield return s;
        }
    }
}

/// <summary>
/// The effects package (<c>master_fx.oppc</c>, M25a): its PSystems by name (parsed on demand), the atlas page and, for the
/// client, the raw bytes of the atlas texture and loose decal textures (read at runtime from the install; never stored).
/// </summary>
public sealed class FxLibrary : IDisposable
{
    public const string Package = "data/packages/master_fx.oppc";

    readonly ObpkArchive _archive;
    readonly Dictionary<string, ObpkEntry> _systems, _textures;
    readonly Dictionary<string, FxSystem> _parsed = new(StringComparer.Ordinal);

    public FxTexturePage Page { get; }
    public IReadOnlyCollection<string> SystemNames => _systems.Keys;
    public IReadOnlyCollection<string> TextureNames => _textures.Keys;

    FxLibrary(ObpkArchive archive, FxTexturePage page)
    {
        _archive = archive; Page = page;
        _systems = new(StringComparer.Ordinal); _textures = new(StringComparer.OrdinalIgnoreCase);
        foreach (var e in archive.Entries)
        {
            string group = archive.GroupName(e);
            if (group == "PSystem") _systems.TryAdd(e.FileName, e);
            else if (group == "dds") _textures.TryAdd(e.FileName, e);
        }
    }

    public static FxLibrary Load(string gameDir) => Open(Path.Combine(gameDir, Package));

    /// <summary>Opens an effects archive (read only); the page is its <c>tpage</c> entry, named with the archive's PSystem strings.</summary>
    public static FxLibrary Open(string path)
    {
        var archive = ObpkArchive.Open(path);
        try
        {
            FxTexturePage? page = null;
            var strings = new Dictionary<ulong, string>();
            archive.ReadEntries(e => archive.GroupName(e) is "tpage" or "PSystem", (e, d) =>
            {
                if (archive.GroupName(e) == "tpage") page ??= FxTexturePage.Read(d, e.FileName);
                else foreach (var kv in BodDocument.Read(d).Strings) strings.TryAdd(kv.Key, kv.Value);
            });
            if (page is null) throw new InvalidDataException($"{Path.GetFileName(path)} has no texture page.");
            return new FxLibrary(archive, page.WithNames(strings));
        }
        catch { archive.Dispose(); throw; }
    }

    // The package is one zlib stream: reading any entry inflates everything before it (about a tenth of a second each on a quiet
    // machine, many seconds on a busy one). Reads share one stream position, so they are serialised, and callers who need several
    // entries use Systems / Textures (one pass) off the main thread instead of one System / Texture call each.
    readonly object _gate = new();

    /// <summary>A PSystem by name (parsed once), or null when the package has none of that name. Inflates the package up to the entry: not for a frame loop.</summary>
    public FxSystem? System(string name)
    {
        lock (_gate)
        {
            if (_parsed.TryGetValue(name, out var fx)) return fx;
            if (!_systems.TryGetValue(name, out var entry)) return null;
            return _parsed[name] = FxSystem.Read(name, _archive.ReadEntry(entry));
        }
    }

    /// <summary>
    /// Several PSystems in one pass over the package (names the package has none of are left out; an entry that does not parse is left out and
    /// described in <paramref name="failures"/>, if given). Thread-safe against the other reads.
    /// </summary>
    public Dictionary<string, FxSystem> Systems(IEnumerable<string> names, List<string>? failures = null)
    {
        var result = new Dictionary<string, FxSystem>(StringComparer.Ordinal);
        var wanted = new Dictionary<int, string>();   // entry index -> name
        lock (_gate)
        {
            foreach (var name in names)
            {
                if (_parsed.TryGetValue(name, out var done)) result[name] = done;
                else if (_systems.TryGetValue(name, out var entry)) wanted[entry.Index] = name;
            }
            if (wanted.Count == 0) return result;
            _archive.ReadEntries(e => wanted.ContainsKey(e.Index), (e, data) =>
            {
                string name = wanted[e.Index];
                try { result[name] = _parsed[name] = FxSystem.Read(name, data.ToArray()); }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or NotSupportedException or EndOfStreamException)
                {
                    failures?.Add($"{name} ({ex.Message})");
                }
            });
        }
        return result;
    }

    /// <summary>A texture's raw DDS bytes (the atlas: <see cref="FxTexturePage.AtlasTexture"/>; decals by name), or null. Inflates the package up to the entry.</summary>
    public byte[]? Texture(string name)
    {
        lock (_gate) return _textures.TryGetValue(name, out var e) ? _archive.ReadEntry(e) : null;
    }

    /// <summary>Several textures' DDS bytes in one pass over the package (names it has none of are left out). Thread-safe against the other reads.</summary>
    public Dictionary<string, byte[]> Textures(IEnumerable<string> names)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var wanted = new Dictionary<int, List<string>>();   // entry index -> the names asked for
        lock (_gate)
        {
            foreach (var name in names)
                if (_textures.TryGetValue(name, out var entry)) { if (!wanted.TryGetValue(entry.Index, out var list)) wanted[entry.Index] = list = []; list.Add(name); }
            if (wanted.Count == 0) return result;
            _archive.ReadEntries(e => wanted.ContainsKey(e.Index), (e, data) =>
            {
                var bytes = data.ToArray();
                foreach (var name in wanted[e.Index]) result[name] = bytes;
            });
        }
        return result;
    }

    public void Dispose() => _archive.Dispose();
}

/// <summary>
/// An effect placed in a map layer (<c>base/fxobject</c>, M25a), e.g. Manufactorum's god rays in <c>l_lights</c>: the effect
/// (<see cref="Fx"/> in <see cref="Package"/>), its length and scale, its world transform (position, rotation in degrees
/// about X then Y then Z as <see cref="MapScene"/> reads them, object scale; composed with enclosing groups), a target
/// marker, whether it starts by itself, and an audio event. Values as the layer has them.
/// </summary>
public sealed record MapFxObject(string Name, string Layer, string Fx, string Package, float Length, float Scale, Vector3 Position, Vector3 RotationDegrees,
    Vector3 ObjectScale, Matrix4x4 Transform, string TargetMarker, bool AutoTrigger, float PreComputeTime, string AudioEvent);

public static class MapEffects
{
    /// <summary>Every enabled <c>base/fxobject</c> in the region's layers of a map (<c>__wrld</c> or <c>__r000_main</c>).</summary>
    public static IReadOnlyList<MapFxObject> Load(string gameDir, string map, List<string>? notes = null)
    {
        if (map.Length == 0 || map.Length > 64 || map.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException($"'{map}' is not a map name.", nameof(map));
        foreach (var region in new[] { "wrld", "r000_main" })
        {
            string path = Path.Combine(gameDir, "data", "chunks", $"{map}__{region}.oppc");
            if (File.Exists(path)) return Read(path, notes);
        }
        throw new FileNotFoundException($"No region archive for '{map}'.");
    }

    /// <summary>The fx objects of every layer in an archive, by layer name then file order; a layer that does not parse is noted and skipped.</summary>
    public static IReadOnlyList<MapFxObject> Read(string archivePath, List<string>? notes = null)
    {
        var found = new List<MapFxObject>();
        var layers = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        using (var archive = ObpkArchive.Open(archivePath))
            archive.ReadEntries(e => archive.GroupName(e) == "Layer", (e, d) => layers.TryAdd(e.FileName, d.ToArray()));
        foreach (var (name, bytes) in layers)
        {
            BodDocument doc;
            try { doc = BodDocument.Read(bytes); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { notes?.Add($"layer {name}: {ex.Message}"); continue; }
            Collect(doc.Root, name, Matrix4x4.Identity, found, 0);
        }
        return found;
    }

    /// <summary>From one layer document (testable).</summary>
    public static IReadOnlyList<MapFxObject> Read(BodDocument layer, string layerName)
    {
        var found = new List<MapFxObject>();
        Collect(layer.Root, layerName, Matrix4x4.Identity, found, 0);
        return found;
    }

    static void Collect(BodValue value, string layer, Matrix4x4 parent, List<MapFxObject> found, int depth)
    {
        if (depth > 64) return;
        switch (value)
        {
            case BodStruct s:
            {
                if (s["Disabled"] is BodBool { Value: true }) return;   // a disabled object hides its children too
                var position = Vec(s["Position"]) ?? Vector3.Zero;
                var rotation = Vec(s["Rotation"]) ?? Vector3.Zero;
                var scale = Vec(s["Scale"]) ?? Vector3.One;
                var world = MapScene.Transform(position, rotation, scale) * parent;
                if (string.Equals(s.Class.Text, "base/fxobject", StringComparison.OrdinalIgnoreCase) && s["FXName"] is BodString { Text: { Length: > 0 } fx })
                    found.Add(new MapFxObject(Text(s, "Name"), layer, fx, Text(s, "FXPackage"), Float(s, "FXLength", 1f), Float(s, "FXScale", 1f), world.Translation,
                        rotation, scale, world, Text(s, "FXTargetMarker"), s["FXAutoTrigger"] is BodBool { Value: true }, Float(s, "FXPreComputeTime", 0f), Text(s, "FXAudioEventName")));
                foreach (var f in s.Fields)
                    if (f.Name.Text is "Root" or "Objects" or "ChildObjects")
                        Collect(f.Value, layer, f.Name.Text == "ChildObjects" ? world : parent, found, depth + 1);
                break;
            }
            case BodList l: foreach (var item in l.Items) Collect(item.Value, layer, parent, found, depth + 1); break;
            case BodRef r: Collect(r.Value, layer, parent, found, depth + 1); break;
        }
    }

    static string Text(BodStruct s, string field) => s[field] is BodString { Text: { } t } ? t : "";
    static float Float(BodStruct s, string field, float fallback) => s[field] is BodFloat { Value: var v } && float.IsFinite(v) ? v : fallback;
    static Vector3? Vec(BodValue? v) => v is BodArray { Items: [BodFloat x, BodFloat y, BodFloat z, ..] } && float.IsFinite(x.Value + y.Value + z.Value)
        ? new Vector3(x.Value, y.Value, z.Value) : null;
}
