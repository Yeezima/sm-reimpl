using SmFormats.Obpk;
using SmFormats.Baf;
using SmSim;

namespace SmContent;

/// <summary>
/// Reads a weapon's spread (weapons/spread; <see cref="SpreadSpec"/>, rules in <see cref="Spread"/>): the firing ability's
/// <c>delivery/kick_back_and_scatter_values/{standing|aiming}/{idle|walking|running}</c> entry chosen for the granted upgrades
/// (<see cref="UpgradeScope.Pick"/>: Bolter Targeter, Improved Twin-Link, Burst Fire and Lascannon Stabilizers select their own sets that way)
/// names a <c>tuning_set</c> <c>climb_sway_scatter\&lt;name&gt;</c>, whose <c>scatter</c>, <c>climb</c> and <c>sway</c> blocks live in
/// <see cref="Package"/>. The basic shot's ability is the one <see cref="WeaponTuning"/> fires (the primary, or the secondary when the primary
/// only charges; a pump ability's first pump state); a charge ability's stages are matched to <see cref="ChargeSpec.Stages"/> by the same
/// order and skipping rule. The <c>crouching</c> stance is not read (multiplayer has no crouch). Kept out of <see cref="WeaponTuning"/> on purpose.
/// </summary>
public static class SpreadTuning
{
    /// <summary>The package of the tuning sets.</summary>
    public const string Package = "data/packages/climb_sway_scatter.oppc";
    const string Primary = "ability_ranged_primary", Secondary = "ability_ranged_secondary";
    static readonly string[] Stances = ["standing", "aiming"];
    static readonly string[] Movements = ["idle", "walking", "running"];

    /// <summary>The tuning-set names (the part after <c>climb_sway_scatter\</c>) every <c>kick_back_and_scatter_values</c> in a weapon file names.</summary>
    public static IEnumerable<string> SetNames(BafDocument weapon)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(BafTable t, int depth, bool inside)
        {
            if (depth > 32) return;
            foreach (var e in t.Entries)
            {
                if (inside && e.Type == BafType.String && e.Name == "tuning_set" && Leaf(e.AsString()) is { Length: > 0 } leaf) found.Add(leaf);
                else if (e.Type == BafType.Table) Walk(e.AsTable(), depth + 1, inside || e.Name == "kick_back_and_scatter_values");
            }
        }
        Walk(weapon.Root, 0, false);
        return found;
    }

    /// <summary>
    /// Reads the named sets (group <c>attr_pc</c>) from the install's <see cref="Package"/> (read-only), by name; missing names are left out with a
    /// note, and so is a set that does not parse (the weapons naming it then have no set for that slot).
    /// </summary>
    public static Dictionary<string, BafDocument> LoadSets(string gameDir, IEnumerable<string> names, List<string> notes)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sets = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return sets;
        string path = Path.Combine(gameDir, Package);
        if (!File.Exists(path)) { notes.Add($"spread: {Package} not found; weapons have no scatter"); return sets; }
        using (var archive = ObpkArchive.Open(path))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && wanted.Contains(e.FileName), (e, d) =>
            {
                try { sets[e.FileName] = BafDocument.Read(d); }
                catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException or EndOfStreamException)
                { notes.Add($"spread: tuning set {e.FileName} does not parse ({ex.GetType().Name}: {ex.Message}); left out"); }
            });
        foreach (var missing in wanted.Where(n => !sets.ContainsKey(n))) notes.Add($"spread: tuning set {missing} not found in {Package}");
        return sets;
    }

    /// <summary><paramref name="spec"/> with its <see cref="WeaponSpec.Spread"/> read from <paramref name="weapon"/> (unchanged without <paramref name="sets"/>).</summary>
    public static WeaponSpec With(WeaponSpec spec, BafDocument weapon, Func<string, BafDocument?>? sets, List<string> notes) =>
        sets is null ? spec : spec with { Spread = Read(spec.Name, weapon, sets, notes) };

    /// <summary>The weapon's spread, or null when its firing ability names no set (with a note).</summary>
    public static SpreadSpec? Read(string name, BafDocument weapon, Func<string, BafDocument?> sets, List<string> notes)
    {
        var defs = weapon.Root.Table("ability_data/ability_definitions");
        if (defs is not { } d) return null;
        string? basicKey = new[] { Primary, Secondary }.FirstOrDefault(k => d.Table(k) is { } a && DeliveryOf(a) is not null);
        SpreadStances? basic = basicKey is not null && d.Table(basicKey) is { } ability ? ReadStances(name, DeliveryOf(ability), sets, notes) : null;
        var stages = new List<SpreadStances?>();
        string? chargeKey = new[] { Primary, Secondary }.FirstOrDefault(k => d.Table(k)?.String("$ref") is { } t && t.EndsWith("charge_ranged", StringComparison.OrdinalIgnoreCase));
        if (chargeKey is not null && d.Table(chargeKey) is { } charge)
            foreach (var e in charge.Table("charge_states")?.Entries.Where(e => e.Type == BafType.Table) ?? [])
            {
                var s = e.AsTable();
                // The same skipping rule as WeaponTuning.Charge, so the indices match ChargeSpec.Stages.
                if (string.IsNullOrEmpty(s.String("$ref")) && s.Entries.All(x => x.Name == "$ref")) continue;
                if (s.Float("minimum_charge_time") is not float min || !float.IsFinite(min) || min < 0f) continue;
                stages.Add(s.Table("delivery") is { } sd ? ReadStances(name, sd, sets, notes) : null);
            }
        if (basic is null && stages.All(s => s is null)) return null;
        while (stages.Count > 0 && stages[^1] is null) stages.RemoveAt(stages.Count - 1);
        return new SpreadSpec(basic ?? new SpreadStances(new SpreadSet?[6]), stages);
    }

    /// <summary>An ability's delivery: its own, or (a <c>pump_ranged</c> ability, the melta) its first pump state's (as WeaponTuning).</summary>
    static BafTable? DeliveryOf(BafTable ability) =>
        ability.Table("delivery")
        ?? ability.Table("pump_states")?.Entries.Where(e => e.Type == BafType.Table).Select(e => e.AsTable().Table("delivery")).FirstOrDefault(x => x is not null);

    static SpreadStances? ReadStances(string name, BafTable? delivery, Func<string, BafDocument?> sets, List<string> notes)
    {
        if (delivery?.Table("kick_back_and_scatter_values") is not { } kick) return null;
        var list = new SpreadSet?[Stances.Length * Movements.Length];
        for (int s = 0; s < Stances.Length; s++)
            for (int m = 0; m < Movements.Length; m++)
            {
                if (UpgradeScope.Pick(kick.Table($"{Stances[s]}/{Movements[m]}"), null)?.String("tuning_set") is not { } set) continue;
                string leaf = Leaf(set);
                if (sets(leaf) is not { } doc) { notes.Add($"{name}: scatter set {leaf} not found"); continue; }
                list[s * Movements.Length + m] = ReadSet(leaf, doc.Root, notes);
            }
        return list.Any(x => x is not null) ? new SpreadStances(list) : null;
    }

    /// <summary>
    /// One <c>climb_sway_scatter</c> file's blocks (a missing value reads as 0; a missing <c>scatter</c> block or <c>initial_horizontal_angle</c> is noted
    /// in <paramref name="notes"/>, once per set, since every MP set carries both).
    /// </summary>
    public static SpreadSet ReadSet(string name, BafTable root, List<string>? notes = null)
    {
        void Note(string text) { string line = $"spread: tuning set {name}: {text}; read as 0"; if (notes is not null && !notes.Contains(line)) notes.Add(line); }
        if (root.Table("scatter") is not { } scatterBlock) Note("no scatter block");
        else if (scatterBlock["initial_horizontal_angle"] is null) Note("no scatter/initial_horizontal_angle");
        float F(string path)
        {
            int slash = path.LastIndexOf('/');
            var t = slash < 0 ? root : root.Table(path[..slash]);
            string field = path[(slash + 1)..];
            if (t?[field] is not { } e) return 0f;
            float v = e.Type switch { BafType.Float => e.AsFloat(), BafType.Int => e.Raw, _ => 0f };
            return float.IsFinite(v) ? v : 0f;
        }
        var scatter = new ScatterSet(F("scatter/initial_horizontal_angle"), F("scatter/initial_vertical_angle"), F("scatter/increase_per_shot"),
            F("scatter/max_horizontal_angle"), F("scatter/max_vertical_angle"), F("scatter/decrease_per_second"), F("scatter/decrease_delay"));
        var handling = new HandlingSet(F("climb/permanent_climb_angle_per_shot"), F("climb/temporary_climb_angle_per_shot"), F("climb/max_temporary_climb_angle"),
            F("climb/climb_rate"), F("climb/climb_recovery_rate"),
            F("sway/min_temporary_sway_angle_per_shot"), F("sway/max_temporary_sway_angle_per_shot"), F("sway/sway_rate"), F("sway/sway_recovery_rate"),
            F("sway/max_temporary_sway_angle"), F("sway/min_permanent_sway_angle_per_shot"), F("sway/max_permanent_sway_angle_per_shot"),
            F("sway/temporary_sway_increase_per_shot"), F("sway/temporary_sway_decrease_per_second"), F("sway/temporary_sway_decrease_delay"));
        return new SpreadSet(name, scatter, handling);
    }

    static string Leaf(string path)
    {
        int i = path.LastIndexOfAny(['\\', '/']);
        return i < 0 ? path : path[(i + 1)..];
    }
}
