using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;
using SmSim.Extras.Musou;

namespace SmContent;

/// <summary>One hit zone's row of a dismemberment type: the chance (0..100) and the point it plays (empty: none).</summary>
public readonly record struct OrkBloodZone(float Chance, string Point);

/// <summary>
/// One row of the Ork blueprint's <c>extensions/dismemberment_ext/dismemberment_by_type</c>: the zones in <see cref="OrkBlood.Zones"/> order and the
/// <c>nothing_left</c> point (played when the whole body goes).
/// </summary>
public sealed record OrkBloodType(string Name, IReadOnlyList<OrkBloodZone> Zones, string NothingLeft);

/// <summary>A weapon's dismemberment types as its own wargear names them in each <c>do_damage</c> (empty: none named).</summary>
/// <param name="Ranged">The shot's: the first type under <c>ability_ranged_primary</c> (its delivery or pump states), else <c>ability_ranged_secondary</c>; penetration and world hits left out.</param>
/// <param name="Melee">Each melee ability's (by ability name: <c>ability_melee_x1</c>, <c>ability_melee_y</c>, <c>ability_bullrush_01</c>, ...): the first type under its primary target's actions.</param>
public sealed record OrkBloodWeapon(string Ranged, IReadOnlyDictionary<string, string> Melee)
{
    /// <summary>The charge's ability (the bull rush), whose type a charging kill uses.</summary>
    public const string ChargeAbility = "ability_bullrush_01";

    /// <summary>A melee ability's type (empty when the weapon names none for it).</summary>
    public string Of(string ability) => ability.Length > 0 && Melee.TryGetValue(ability, out var t) ? t : "";
}

/// <summary>Where a hit or a kill landed on the body: the data's zones (<c>shield</c> and <c>character_weakspot</c> are not used).</summary>
public enum OrkBloodZoneKind { Head = 0, Body = 1, LeftArm = 2, RightArm = 3, LeftLeg = 4, RightLeg = 5 }

/// <summary>
/// Musou Mode (added content), the original blood (owner 2026-11-07: "use this video from the original game as a reference for the gore effects of all melee and ranged weapons";
/// his choice 1: Musou's dark trailer gore replaced by the original's own Ork blood, scaled 1.5x). What is **data** (read from the install at run time, read-only): the Ork
/// blueprint's dismemberment table (<see cref="Types"/>: per damage type and hit zone the chance and the point), the points' effects (<see cref="Points"/>: each point's
/// <c>play_fx</c> commands from <c>dismemberment_point.oppc</c>, with their marker, scale and length) and the dismemberment type every multiplayer weapon names in its
/// <c>do_damage</c> (<see cref="Weapons"/>). What is ours is in <see cref="OrkBloodRules"/>. Evidence: docs/discovery.md "The Ork's own blood (gore-original)".
/// </summary>
public sealed class OrkBlood
{
    /// <summary>The package of the dismemberment points (one attribute file per point, named after it).</summary>
    public const string PointPackage = "data/packages/dismemberment_point.oppc";

    /// <summary>Blueprints that carry the table, in the order tried (the first with a table wins; all Ork co-op blueprints share it).</summary>
    public static readonly IReadOnlyList<string> TableBlueprints = ["ork_coop_boy", "ork_boy", "ork_coop_nob"];

    /// <summary>The table's zones in <see cref="OrkBloodZoneKind"/> order.</summary>
    public static readonly IReadOnlyList<string> Zones = ["head", "body", "left_arm", "right_arm", "left_leg", "right_leg"];

    public required IReadOnlyDictionary<string, OrkBloodType> Types { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<FxPlay>> Points { get; init; }
    public required IReadOnlyDictionary<string, OrkBloodWeapon> Weapons { get; init; }
    /// <summary>The blueprint the table was read from (empty: none).</summary>
    public string Source { get; init; } = "";
    public IReadOnlyList<string> Notes { get; init; } = [];

    public static readonly OrkBlood Empty = new() { Types = new Dictionary<string, OrkBloodType>(), Points = new Dictionary<string, IReadOnlyList<FxPlay>>(), Weapons = new Dictionary<string, OrkBloodWeapon>() };

    /// <summary>Every effect a point plays, for preloading.</summary>
    public IEnumerable<string> EffectNames => Points.Values.SelectMany(p => p).Select(p => p.Name).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal);

    /// <summary>The weapon's row, or null for a wargear file not read.</summary>
    public OrkBloodWeapon? Weapon(string wargear) => Weapons.TryGetValue(wargear, out var w) ? w : null;

    /// <summary>The effects of a point (empty for an unknown or empty one).</summary>
    public IReadOnlyList<FxPlay> PointEffects(string point) => point.Length > 0 && Points.TryGetValue(point, out var p) ? p : [];

    /// <summary>The weapons read by default: every multiplayer wargear file (<c>sm_mp_*</c>, <c>csm_mp_*</c>).</summary>
    public static bool IsMultiplayerWeapon(string file) => file.StartsWith("sm_mp_", StringComparison.OrdinalIgnoreCase) || file.StartsWith("csm_mp_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the install (read-only; a few hundred milliseconds, for a background thread): the table, the points and every multiplayer weapon's types.</summary>
    public static OrkBlood Load(string gameDir, List<string>? notes = null)
    {
        notes ??= [];
        var blueprints = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && TableBlueprints.Contains(e.FileName, StringComparer.OrdinalIgnoreCase), (e, d) => blueprints.TryAdd(e.FileName, BafDocument.Read(d)));
        var points = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        string pointPath = Path.Combine(gameDir, PointPackage);
        if (File.Exists(pointPath))
            using (var archive = ObpkArchive.Open(pointPath))
                archive.ReadEntries(e => archive.GroupName(e) == "attr_pc", (e, d) => points.TryAdd(e.FileName, BafDocument.Read(d)));
        else notes.Add($"ork blood: {PointPackage} not found; no point plays an effect");
        var weapons = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && IsMultiplayerWeapon(e.FileName), (e, d) => weapons.TryAdd(e.FileName, BafDocument.Read(d)));
        return Build(blueprints, points, weapons, notes);
    }

    /// <summary>The data from already-read files (testable with synthetic documents).</summary>
    public static OrkBlood Build(IReadOnlyDictionary<string, BafDocument> blueprints, IReadOnlyDictionary<string, BafDocument> points, IReadOnlyDictionary<string, BafDocument> weapons, List<string>? notes = null)
    {
        notes ??= [];
        IReadOnlyDictionary<string, OrkBloodType> types = new Dictionary<string, OrkBloodType>();
        string source = "";
        foreach (string name in TableBlueprints)
        {
            if (!blueprints.TryGetValue(name, out var doc) || TypesOf(doc) is not { Count: > 0 } read) continue;
            types = read; source = name;
            break;
        }
        if (source.Length == 0) notes.Add($"ork blood: none of {string.Join(", ", TableBlueprints)} has a dismemberment table");
        var plays = new Dictionary<string, IReadOnlyList<FxPlay>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in points) plays[name] = PointPlays(doc);
        foreach (var t in types.Values)
            foreach (string p in t.Zones.Select(z => z.Point).Append(t.NothingLeft))
                if (p.Length > 0 && points.Count > 0 && !plays.ContainsKey(p)) notes.Add($"ork blood: type '{t.Name}' names point '{p}', which {PointPackage} lacks");
        var rows = new Dictionary<string, OrkBloodWeapon>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in weapons) rows[name] = WeaponOf(doc);
        return new OrkBlood { Types = types, Points = plays, Weapons = rows, Source = source, Notes = notes };
    }

    /// <summary>The rows of a blueprint's table (null when it has none).</summary>
    public static IReadOnlyDictionary<string, OrkBloodType>? TypesOf(BafDocument blueprint)
    {
        if (blueprint.Root.Table("extensions/dismemberment_ext/dismemberment_by_type") is not { } table) return null;
        var types = new Dictionary<string, OrkBloodType>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in table.Entries.Where(e => e.Type == BafType.Table))
        {
            var row = entry.AsTable();
            var zones = new OrkBloodZone[Zones.Count];
            for (int i = 0; i < Zones.Count; i++)
            {
                var zone = row.Table($"hit_zone/{Zones[i]}");
                float chance = zone?["chance"] is { } c ? c.Type switch { BafType.Float => c.AsFloat(), BafType.Int => c.Raw, _ => 0f } : 0f;
                zones[i] = new OrkBloodZone(float.IsFinite(chance) ? Math.Clamp(chance, 0f, 100f) : 0f, PointName(zone?.String("dismemberment")));
            }
            types.TryAdd(entry.Name, new OrkBloodType(entry.Name, zones, PointName(row.String("nothing_left"))));
        }
        return types;
    }

    /// <summary>The <c>play_fx</c> commands of a point's actions (its <c>actions/*/commands</c>, nested delayed actions followed by <see cref="DeathAction.Read"/>).</summary>
    public static IReadOnlyList<FxPlay> PointPlays(BafDocument point)
    {
        var plays = new List<FxPlay>();
        if (point.Root.Table("actions") is not { } actions) return plays;
        foreach (var a in actions.Entries.Where(e => e.Type == BafType.Table))
            if (a.AsTable().Table("commands") is { } commands)
                plays.AddRange(DeathAction.Read(a.Name, "point", commands).Effects.Select(s => s.Fx!));
        return plays;
    }

    /// <summary>The point's own name from a reference such as <c>dismemberment\dismemberment_point\explode_head</c> (empty: none).</summary>
    public static string PointName(string? reference) =>
        string.IsNullOrWhiteSpace(reference) ? "" : reference.Trim().Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    /// <summary>A weapon file's types (see <see cref="OrkBloodWeapon"/>; a missing ability: empty).</summary>
    public static OrkBloodWeapon WeaponOf(BafDocument doc)
    {
        var abilities = doc.Root.Table("ability_data/ability_definitions");
        string ranged = "";
        foreach (string name in new[] { "ability_ranged_primary", "ability_ranged_secondary" })
            if (ranged.Length == 0 && abilities?.Table(name) is { } a) ranged = FirstType(a, 0);
        var melee = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (abilities is { } defs)
            foreach (var e in defs.Entries.Where(x => x.Type == BafType.Table && (x.Name.StartsWith("ability_melee", StringComparison.OrdinalIgnoreCase) || x.Name.StartsWith("ability_bullrush", StringComparison.OrdinalIgnoreCase))))
                if (e.AsTable().Table("primary_target_actions") is { } primary && FirstType(primary, 0) is { Length: > 0 } t) melee[e.Name] = t;
        return new OrkBloodWeapon(ranged, melee);
    }

    /// <summary>Tables never searched for a weapon's own type: a shot's penetration, a strike's world hits and other targets (not the struck body).</summary>
    static readonly string[] SkipTables = ["penetration", "world_hit_trace_actions", "secondary_target_actions", "collision_entity_actions"];

    /// <summary>The first <c>dismemberment_type</c> in file order under <paramref name="t"/> (depth first; empty: none).</summary>
    static string FirstType(BafTable t, int depth)
    {
        if (depth > 16) return "";
        if (t["dismemberment_type"] is { Type: BafType.String } v && v.AsString().Trim() is { Length: > 0 } s) return s;
        foreach (var e in t.Entries)
        {
            if (e.Type != BafType.Table || SkipTables.Contains(e.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (FirstType(e.AsTable(), depth + 1) is { Length: > 0 } found) return found;
        }
        return "";
    }
}

/// <summary>What a Musou kill plays from the Ork's own table (<see cref="OrkBloodRules.Kill"/>).</summary>
/// <param name="Type">The dismemberment type used (empty: none).</param>
/// <param name="Zone">The zone rolled.</param>
/// <param name="Point">The point played (empty: no blood at the death).</param>
/// <param name="WholeBody">The point takes the whole body (a <c>*_body</c> point): the corpse is not drawn.</param>
public readonly record struct OrkBloodKill(string Type, OrkBloodZoneKind Zone, string Point, bool WholeBody);

/// <summary>
/// The engine-free rules of Musou Mode's original blood (added content; the data is <see cref="OrkBlood"/>). **Ours** (kind: ours or interpretation, spec "Musou Mode: the original
/// blood"): which dismemberment type a Musou death uses (<see cref="TypeOf"/>), the zone of a kill (<see cref="ZoneOf"/>), reading the zone's chance as the chance that a kill plays
/// the zone's point and <c>nothing_left</c> as the point of a death that takes the whole body (<see cref="Kill"/>), the marker offsets (<see cref="MarkerOffset"/>) and the Musou
/// factor <see cref="MusouScale"/> (the owner's 1.5x).
/// </summary>
public static class OrkBloodRules
{
    /// <summary>
    /// The owner's factor for Musou Mode (2026-11-07: "since it is musou mode multiply the volume and magnitude of the blood effects by 1.5x"): the blood effects' size (scale) and
    /// their amount (the particles and splats of every spray that throws blood) are multiplied by it. Kind: ours. Client <c>--ork-blood-scale f</c> tunes it.
    /// </summary>
    public const float MusouScale = 1.5f;

    /// <summary>The plasma gun's shot names no type (<c>dismemberment_type = ""</c>); its kills use the table's light plasma row, as the plasma pistol names (interpretation).</summary>
    public const string PlasmaFallback = "plasma_light";

    /// <summary>
    /// Whether the original blood draws at all: only in Musou Mode (<paramref name="musou"/>), with the gore not switched off and the old dark gore not chosen. Classic PvP
    /// (Annihilation, Seize Ground; no Musou) never draws it.
    /// </summary>
    public static bool Active(bool musou, bool goreOff, bool oldGore) => musou && !goreOff && !oldGore;

    /// <summary>
    /// The dismemberment type of a Musou death (interpretation): a shot by the killer's weapon in hand (its shot's type; the plasma gun's empty type is <see cref="PlasmaFallback"/>),
    /// a melee kill by its melee weapon: the bull rush's type when charging, else the type of the strike's own ability (<paramref name="meleeAbility"/>, from the weapon's combo
    /// lists: <c>MeleeTuning.WeaponMap.AbilityOf</c>; unknown: the first light strike's), a blast (frag grenade, launcher, the jump pack's
    /// landing) <c>explosive_heavy</c>, artillery <c>explosive_heavy</c>, the Thunderhawk's strafe <c>heavy_bolter</c> (its guns are heavy bolters), a world removal none.
    /// </summary>
    public static string TypeOf(MusouDeathKind cause, string? rangedWargear, OrkBloodWeapon? ranged, OrkBloodWeapon? melee, string meleeAbility, bool charging) => cause switch
    {
        MusouDeathKind.Shot => ranged is null ? "" : ranged.Ranged.Length > 0 ? ranged.Ranged
            : rangedWargear?.Contains("plasma", StringComparison.OrdinalIgnoreCase) == true ? PlasmaFallback : "",
        MusouDeathKind.Melee => melee is null ? "" : charging && melee.Of(OrkBloodWeapon.ChargeAbility) is { Length: > 0 } c ? c
            : melee.Of(meleeAbility) is { Length: > 0 } a ? a : melee.Of("ability_melee_x1"),
        MusouDeathKind.Blast or MusouDeathKind.Barrage or MusouDeathKind.OrkBlast => "explosive_heavy",
        MusouDeathKind.Strafe => "heavy_bolter",
        _ => "",
    };

    /// <summary>A blast or artillery death takes the whole body: <c>nothing_left</c> plays (interpretation: the original's explosions gib Orks).</summary>
    public static bool TakesWholeBody(MusouDeathKind cause) => cause is MusouDeathKind.Blast or MusouDeathKind.Barrage or MusouDeathKind.OrkBlast;

    /// <summary>
    /// The zone of a kill (interpretation: the server sends no hit zone for a body hit): the head for a head shot, else one of the body, the arms and the legs, evenly by the
    /// seeded roll <paramref name="roll"/> (0..1).
    /// </summary>
    public static OrkBloodZoneKind ZoneOf(bool headShot, float roll) =>
        headShot ? OrkBloodZoneKind.Head : (OrkBloodZoneKind)(1 + Math.Clamp((int)(roll * 5f), 0, 4));

    /// <summary>
    /// What a kill plays: the zone's point when the second seeded roll (0..1) falls under its chance; <c>nothing_left</c> for a death that takes the whole body; otherwise none
    /// (interpretation of the table's fields). Unknown type: none.
    /// </summary>
    public static OrkBloodKill Kill(OrkBlood data, string type, MusouDeathKind cause, bool headShot, float zoneRoll, float chanceRoll)
    {
        var zone = ZoneOf(headShot, zoneRoll);
        if (type.Length == 0 || !data.Types.TryGetValue(type, out var row)) return new OrkBloodKill(type, zone, "", false);
        string point = "";
        if (TakesWholeBody(cause)) point = row.NothingLeft;
        else if ((int)zone < row.Zones.Count && row.Zones[(int)zone] is var z && z.Point.Length > 0 && chanceRoll * 100f < z.Chance) point = z.Point;
        return new OrkBloodKill(type, zone, point, IsWholeBody(point));
    }

    /// <summary>A point that takes the whole body (<c>*_body</c>, <c>*_small_body</c>, an <c>_ig</c> variant of them).</summary>
    public static bool IsWholeBody(string point)
    {
        string n = point.ToLowerInvariant();
        if (n.EndsWith("_ig", StringComparison.Ordinal)) n = n[..^3];
        return n.EndsWith("_body", StringComparison.Ordinal);
    }

    /// <summary>
    /// Where a point's marker sits from the body's chest, in the body's frame (x right, y forward, z up; metres for a Boy, times the body's size). Ours: the crowd draws no bones
    /// (animated by vertex textures), so the data's markers (<c>fx_gore_head</c>, <c>fx_gore_l_side</c>, <c>fx_gore_r_side</c>, <c>rig_world</c>, the rig's bones) are placed by name.
    /// </summary>
    public static System.Numerics.Vector3 MarkerOffset(string marker)
    {
        string m = marker.ToLowerInvariant();
        if (m.Contains("head") || m.Contains("neck")) return new(0f, 0.05f, 0.5f);
        if (m.Contains("world") || m.Contains("root")) return new(0f, 0f, -0.6f);
        bool left = m.Contains("_l_") || m.EndsWith("_l") || m.Contains("left") || m.Contains("_lhand") || m.Contains("_lcalf");
        bool right = m.Contains("_r_") || m.EndsWith("_r") || m.Contains("right") || m.Contains("_rhand") || m.Contains("_rthigh");
        float x = left ? -0.35f : right ? 0.35f : 0f;
        if (m.Contains("calf") || m.Contains("thigh") || m.Contains("foot") || m.Contains("leg")) return new(x * 0.6f, 0f, -0.55f);
        if (m.Contains("hand") || m.Contains("arm")) return new(x * 1.3f, 0.1f, 0.05f);
        return new(x, 0f, 0f);
    }
}
