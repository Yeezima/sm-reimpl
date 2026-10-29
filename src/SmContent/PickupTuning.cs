using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>
/// How a pickup looks and sounds (client side; fidelity gap 1), from its blueprint: the model (<c>renderable_ext</c>), the prompt
/// (<c>interactive_ext</c> state <c>message</c>, a locale id), the take effect and sound (the first state's <c>self_exit_action</c>
/// <c>play_fx</c> and <c>post_sound_event</c>) and an effect played while it lies there (<c>entity_actions_ext/lifetime_action</c> <c>play_fx</c>).
/// </summary>
public sealed record PickupLook(string Blueprint, string VisualPackage, string Visual, int Message, string ActionIcon,
    string TakeFx, float TakeFxScale, string TakeSound, string IdleFx, float IdleFxScale)
{
    /// <summary>
    /// The archive holding <see cref="Visual"/>, relative to the game folder (<see cref="PickupTuning.FindVisual"/>): <c>data/packages/&lt;package&gt;.oppc</c>
    /// when it exists, else the versus entity set chunk (on the install the drops' <c>ammocase_bolter_small</c> lies in
    /// <c>data/chunks/ebp_sets__mp_versus_set.oppc</c>); empty when not found.
    /// </summary>
    public string Archive { get; init; } = "";
}

/// <summary>
/// Reads the multiplayer drops (fidelity gap 1) from the install: the player blueprints' <c>health_ext/death_action</c>
/// <c>random_actions</c> entries (<c>spawn_entity</c> blueprint and <c>weight</c>), and each spawned pickup blueprint
/// (<c>data/packages/ebps.oppc</c>): its <c>interactive_ext</c> (reach <c>interaction_style/interact_distance</c>, the press rule, the
/// prompt, the <c>replenish_wargear_ammunition</c> commands of the state it moves to) and its lifetime (<c>entity_actions_ext/lifetime_action</c>
/// <c>delayed_action</c> holding <c>kill_entity</c>). Amounts are matched to the config's weapons and thrown items by their wargear
/// <c>common_data/ammunition/entry/name</c>. Read-only; nothing is copied out of the install.
/// </summary>
public static class PickupTuning
{
    /// <summary>The config with its <see cref="SimConfig.Pickups"/> read from the install (unchanged, with a note, when they cannot be read).</summary>
    public static SimConfig Apply(SimConfig config, string gameDir, BafDocument marine, BafDocument? chaos, List<string> notes, List<string> placeholders)
    {
        try
        {
            var (spec, _) = Read(config, gameDir, marine, chaos, notes);
            if (spec is null) { placeholders.Add(nameof(SimConfig.Pickups)); return config; }
            return config with { Pickups = spec };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            notes.Add($"pickups: {e.GetType().Name}: {e.Message}; no drops");
            placeholders.Add(nameof(SimConfig.Pickups));
            return config;
        }
    }

    /// <summary>The drops and their looks from the install (the player blueprints are read again when not given). Null spec: none usable (noted).</summary>
    public static (PickupSpec? Spec, IReadOnlyList<PickupLook> Looks) Read(SimConfig config, string gameDir, BafDocument? marine, BafDocument? chaos, List<string> notes)
    {
        var docs = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        if (marine is null)
        {
            using var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage));
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName is GameplayTuning.MarineBlueprint or GameplayTuning.ChaosBlueprint,
                (e, d) => docs[e.FileName] = BafDocument.Read(d));
            marine = docs.GetValueOrDefault(GameplayTuning.MarineBlueprint);
            chaos = docs.GetValueOrDefault(GameplayTuning.ChaosBlueprint);
            if (marine is null) { notes.Add($"pickups: {GameplayTuning.MarineBlueprint} not found; no drops"); return (null, []); }
        }
        var drops = DeathDrops(marine);
        if (chaos is not null && !DeathDrops(chaos).SequenceEqual(drops)) notes.Add("pickups: the Chaos player's death drops differ from the Space Marine's; the Space Marine's are used");
        if (drops.Count == 0) { notes.Add("pickups: the player blueprint's death_action spawns nothing; no drops"); return (null, []); }

        var wanted = drops.Select(d => Leaf(d.Blueprint)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pickups = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && wanted.Contains(e.FileName), (e, d) => pickups[e.FileName] = BafDocument.Read(d));

        var names = config.Weapons.Select(w => w.Name).Concat(config.Thrown.Select(t => t.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        string wargearPath = Path.Combine(gameDir, GameplayTuning.WargearPackage);
        if (File.Exists(wargearPath))
            using (var archive = ObpkArchive.Open(wargearPath))
                archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && names.Contains(e.FileName), (e, d) => wargear[e.FileName] = BafDocument.Read(d));
        var ammunition = wargear.ToDictionary(kv => kv.Key, kv => AmmunitionName(kv.Value), StringComparer.OrdinalIgnoreCase);
        var (spec, looks) = Build(config, drops, pickups, ammunition, notes);
        return (spec, looks.Select(l => l with { Archive = FindVisual(gameDir, l.VisualPackage, l.Visual, notes) }).ToList());
    }

    /// <summary>
    /// Where a blueprint's visual lives (relative to the game folder): its package, else the first entity-set chunk (<c>data/chunks/ebp_sets__*.oppc</c>,
    /// the multiplayer versus set first) with an object tree of that name; "" with a note when none has it. Reads only the archives' tables.
    /// </summary>
    public static string FindVisual(string gameDir, string package, string visual, List<string> notes)
    {
        if (visual.Length == 0) return "";
        string packagePath = Path.Combine("data", "packages", package + ".oppc");
        if (package.Length > 0 && File.Exists(Path.Combine(gameDir, packagePath))) return packagePath;
        string chunks = Path.Combine(gameDir, "data", "chunks");
        if (Directory.Exists(chunks))
            foreach (var path in Directory.EnumerateFiles(chunks, "ebp_sets__*.oppc", SearchOption.AllDirectories)
                         .OrderBy(p => Path.GetFileName(p).StartsWith("ebp_sets__mp_versus_set", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                using var archive = ObpkArchive.Open(path);
                if (archive.Entries.Any(e => archive.GroupName(e) == "O3d" && e.FileName.Equals(visual, StringComparison.OrdinalIgnoreCase)))
                    return Path.GetRelativePath(gameDir, path);
            }
        notes.Add($"pickups: visual {package}/{visual} found in no package or entity-set chunk; drawn as a placeholder");
        return "";
    }

    /// <summary>The death action's weighted spawns: (blueprint path as written, weight), in file order.</summary>
    public static IReadOnlyList<(string Blueprint, int Weight)> DeathDrops(BafDocument player)
    {
        var result = new List<(string, int)>();
        var commands = player.Root.Table("extensions/health_ext/death_action/commands");
        foreach (var c in commands?.Entries ?? [])
        {
            if (c.Type != BafType.Table || c.Name != "random_actions") continue;
            foreach (var entry in c.AsTable().Table("action_entries")?.Entries ?? [])
            {
                if (entry.Type != BafType.Table) continue;
                var t = entry.AsTable();
                int weight = (int)(Number(t["weight"]) ?? 0f);
                foreach (var spawn in t.Table("actions/commands")?.Entries ?? [])
                    if (spawn.Type == BafType.Table && spawn.Name == "spawn_entity" && spawn.AsTable().String("entity_to_spawn") is { Length: > 0 } bp)
                        result.Add((bp, weight));
            }
        }
        return result;
    }

    /// <summary>A wargear file's ammunition name (<c>common_data/ammunition/entry/name</c>, the first entry), or null.</summary>
    public static string? AmmunitionName(BafDocument wargear) => wargear.Root.Table("common_data/ammunition/entry")?.String("name");

    /// <summary>
    /// The spec and looks from read documents (testable without an install). A drop whose blueprint is missing, has no interaction or no
    /// lifetime is left out with a note; replenish types other than "absolute" are noted and not applied.
    /// </summary>
    public static (PickupSpec? Spec, IReadOnlyList<PickupLook> Looks) Build(SimConfig config, IReadOnlyList<(string Blueprint, int Weight)> drops,
        IReadOnlyDictionary<string, BafDocument> pickups, IReadOnlyDictionary<string, string?> ammunitionByWargear, List<string> notes)
    {
        var types = new List<PickupType>();
        var looks = new List<PickupLook>();
        foreach (var (path, weight) in drops)
        {
            string name = Leaf(path);
            if (!pickups.TryGetValue(name, out var doc)) { notes.Add($"pickups: {name} not found in {GameplayTuning.BlueprintPackage}; left out"); continue; }
            var interactive = doc.Root.Table("extensions/interactive_ext");
            if (interactive is not { } ia) { notes.Add($"pickups: {name} has no interactive_ext; left out"); continue; }
            string initial = ia.String("initial_state") ?? "";
            var first = ia.Table("states/" + initial);
            if (first is not { } state) { notes.Add($"pickups: {name}: initial state '{initial}' not found; left out"); continue; }
            float radius = Number(ia.Table("interaction_style")?["interact_distance"]) ?? float.NaN;
            if (!(float.IsFinite(radius) && radius > 0f)) { notes.Add($"pickups: {name}: no interact_distance; left out"); continue; }
            if (state["progress_need_button_press"] is { Type: BafType.Bool } press && !press.AsBool())
                notes.Add($"pickups: {name}: progress_need_button_press is false; the simulation still needs a press");
            float perPress = Number(state["progress_per_button_press"]) ?? 1f;
            if (perPress < 1f) notes.Add($"pickups: {name}: progress_per_button_press {perPress} (one press is taken to collect)");
            // The amounts: the state the first one moves to (operator_enter_action), and the first state's own (none on the install).
            var amounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            void Replenish(BafTable? action)
            {
                foreach (var c in action?.Table("commands")?.Entries ?? [])
                {
                    if (c.Type != BafType.Table || c.Name != "replenish_wargear_ammunition") continue;
                    var t = c.AsTable();
                    string ammo = t.String("ammunition_name") ?? "", kind = t.String("replenish_type") ?? "";
                    int amount = (int)(Number(t["amount"]) ?? 0f);
                    if (!kind.Equals("absolute", StringComparison.OrdinalIgnoreCase)) { notes.Add($"pickups: {name}: replenish type '{kind}' for {ammo} not applied"); continue; }
                    if (ammo.Length > 0 && amount > 0) amounts[ammo] = amounts.GetValueOrDefault(ammo) + amount;
                }
            }
            Replenish(state.Table("operator_enter_action"));
            if (state.String("next_state") is { Length: > 0 } next && ia.Table("states/" + next) is { } collected) Replenish(collected.Table("operator_enter_action"));
            float? lifetime = Lifetime(doc.Root.Table("extensions/entity_actions_ext/lifetime_action"));
            if (lifetime is not { } life) { notes.Add($"pickups: {name}: no lifetime (delayed kill_entity); left out"); continue; }

            int[] weaponAmounts = config.Weapons.Select(w => ammunitionByWargear.GetValueOrDefault(w.Name) is { } a ? amounts.GetValueOrDefault(a) : 0).ToArray();
            int[] thrownAmounts = config.Thrown.Select(t => ammunitionByWargear.GetValueOrDefault(t.Name) is { } a ? amounts.GetValueOrDefault(a) : 0).ToArray();
            var used = config.Weapons.Select(w => ammunitionByWargear.GetValueOrDefault(w.Name)).Concat(config.Thrown.Select(t => ammunitionByWargear.GetValueOrDefault(t.Name)))
                .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unused = amounts.Keys.Where(a => !used.Contains(a)).Order(StringComparer.Ordinal).ToList();
            if (unused.Count > 0) notes.Add($"pickups: {name}: ammunition no simulated item uses: {string.Join(", ", unused.Select(a => $"{a} +{amounts[a]}"))}");
            var kind2 = thrownAmounts.Any(a => a > 0) && weaponAmounts.All(a => a == 0) ? PickupKind.Grenades : PickupKind.Ammo;
            types.Add(new PickupType(name, kind2, Math.Max(0, weight), radius, life, weaponAmounts, thrownAmounts));

            var renderable = doc.Root.Table("extensions/renderable_ext");
            var takeFx = FirstCommand(state.Table("self_exit_action"), "play_fx");
            var takeSound = FirstCommand(state.Table("self_exit_action"), "post_sound_event");
            var idleFx = FirstCommand(doc.Root.Table("extensions/entity_actions_ext/lifetime_action"), "play_fx");
            looks.Add(new PickupLook(name, renderable?.String("visual_package") ?? "", renderable?.String("visual") ?? "", (int)(Number(state["message"]) ?? 0f),
                ia.String("action_icon") ?? "",
                takeFx?.String("fx_name") ?? "", Number(takeFx?["scale"]) ?? 1f, takeSound?.String("start_event_name") ?? "",
                idleFx?.String("fx_name") ?? "", Number(idleFx?["scale"]) ?? 1f));
        }
        if (types.Count == 0) { notes.Add("pickups: no usable pickup; no drops"); return (null, []); }
        if (types.Count > PickupSpec.MaxTypes) { notes.Add($"pickups: {types.Count} types, more than {PickupSpec.MaxTypes}; no drops"); return (null, []); }
        return (new PickupSpec(types), looks);
    }

    /// <summary>The delay (s) of a lifetime action's <c>delayed_action</c> whose action holds <c>kill_entity</c>, or null.</summary>
    static float? Lifetime(BafTable? action)
    {
        foreach (var c in action?.Table("commands")?.Entries ?? [])
        {
            if (c.Type != BafType.Table || c.Name != "delayed_action") continue;
            var t = c.AsTable();
            if (t.Table("action/commands")?.Entries.Any(e => e.Type == BafType.Table && e.Name == "kill_entity") == true && Number(t["delay"]) is float d && float.IsFinite(d) && d > 0f) return d;
        }
        return null;
    }

    static BafTable? FirstCommand(BafTable? action, string command) =>
        action?.Table("commands")?.Entries.Where(e => e.Type == BafType.Table && e.Name == command).Select(e => (BafTable?)e.AsTable()).FirstOrDefault();

    static float? Number(BafEntry? e) => e switch
    {
        { Type: BafType.Int } i => i.Raw,
        { Type: BafType.Float } f => f.AsFloat(),
        _ => null,
    };

    static string Leaf(string path) => path.Split('\\', '/')[^1];
}
