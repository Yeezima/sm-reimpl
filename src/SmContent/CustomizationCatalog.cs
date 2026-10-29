using SmFormats.Baf;
using SmFormats.Locale;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>
/// An armour piece (a wargear file such as <c>sm_mk7aquila_torso</c>).
/// </summary>
/// <param name="Slot">The data's slot: one of <see cref="CustomizationCatalog.ArmourSlots"/> for offered pieces; other values (e.g. "perk") occur in class overrides.</param>
/// <param name="Packages">The visual packages of its attachments (<c>visual_data/attachments/*/visual_package</c>), in the data's order (a torso may carry a belt).</param>
/// <param name="HelmetOverride">The data's <c>helmet_override</c>: "helmet", "no_helmet" or "no_override" (empty when absent).</param>
/// <param name="ChallengeText">The armour challenge that unlocks it ("Group: description"), or null. Not applied (<c>UnlockMode.All</c>).</param>
/// <param name="SetNames">The names of the customizable armour sets the piece belongs to (several sets share pieces).</param>
public sealed record ArmourPiece(string Name, string Race, string Slot, IReadOnlyList<string> Packages, string DisplayName, string Description,
    int Level, string HelmetOverride, string? ChallengeText, IReadOnlyList<string> SetNames);

/// <summary>
/// An armour set (<c>mp_armor_set</c> entry). A customizable set contributes its pieces to the mix-and-match lists; a predefined one is a fixed
/// full set with its own paint scheme (DLC and reward sets).
/// </summary>
/// <param name="Name">The entry name, e.g. "mk_7_aquila".</param>
/// <param name="SetId">The data's <c>set_id</c> ("sm_default" / "chaos_default" for the defaults; often empty). Classes name their default by it.</param>
/// <param name="Items">Wargear file name by armour slot.</param>
/// <param name="Scheme">The paint scheme name of a predefined set, or null.</param>
public sealed record ArmourSet(string Name, string SetId, string Race, bool Customizable, string DisplayName, string Description,
    IReadOnlyDictionary<string, string> Items, string? Scheme)
{
    /// <summary>The set's pieces by armour slot (from <see cref="Items"/>; an item whose wargear file is missing is left out), so a predefined set resolves on its own.</summary>
    public IReadOnlyDictionary<string, ArmourPiece> Pieces { get; init; } = new Dictionary<string, ArmourPiece>();
}

/// <summary>A visible palette colour (<c>custom_paint_colour</c>).</summary>
/// <param name="Group">The customizer's group (<c>colour_type</c>).</param>
/// <param name="Diffuse">Red, green, blue 0..1 as stored (colour space unknown).</param>
public sealed record PaletteEntry(string Name, string DisplayName, string Group, float[] Diffuse, float[] Specular);

/// <summary>The palette's colours of one group, ordinal by name.</summary>
public sealed record PaletteGroup(string Name, IReadOnlyList<PaletteEntry> Colours);

/// <summary>
/// A visible shoulder badge (<c>custom_paint_decal</c>). A family groups the variants of one badge by the name's suffix: "orig", "white", "black", "bw",
/// "bw_inverted", "inverted", or empty for a badge with no variants.
/// </summary>
public sealed record ShoulderDecal(string Name, string DisplayName, string Race, string Family, string Variant, bool IsBlank, PaintDecal Decal);

/// <summary>A visible chapter / warband preset (<c>custom_paint_scheme</c> with <c>visible_in_customizer</c>).</summary>
public sealed record PaintPreset(string Name, string DisplayName, string Race, bool ClassBadgeEnabled, PaintScheme Scheme);

/// <summary>A piece the class forces (<c>override_wargear/gear_and_req</c>), when <paramref name="RequiredWargear"/> (a wargear name) is carried, or always when null.</summary>
public sealed record ClassOverride(ArmourPiece Piece, string? RequiredWargear);

/// <summary>The customizer side of a class: default armour set, forced pieces and the class's own unlock level.</summary>
/// <param name="DefaultArmourSet">The set name (<see cref="ArmourSet.Name"/>) the class's <c>default_armor_set</c> id resolves to; the raw id when unresolved.</param>
public sealed record ClassCustomization(string Key, string Role, string Race, int Level, string DefaultArmourSet, IReadOnlyList<ClassOverride> Overrides)
{
    /// <summary>
    /// The class carries a jump pack (a base wargear file with an <c>ability_jumppack</c>; <c>JumpPackTuning.IsJumpPack</c>): its pack takes the back, so the
    /// backpack piece is not drawn (how pack and backpack share the back is not in the data: **interpretation**).
    /// </summary>
    public bool JumpPack { get; init; }
}

/// <summary>Player-level gates from <c>profile_progression_info/unlocked_func_info</c>. A missing entry reads as level 1 (always available).</summary>
/// <param name="LoadoutSlots">The levels of the numbered loadouts in order (4, 11, 17, 23, 29, 35 in the install).</param>
public sealed record UnlockLevels(int Customizer, int CustomAppearance, int SecondPerkSlot, IReadOnlyList<int> LoadoutSlots, int Prestige, int DevastatorClass, int AssaultClass)
{
    /// <summary>The class's level by role: heavy (Devastator / Havoc) and assault (Assault / Raptor) have gates; tactical has none (**interpretation**: level 1).</summary>
    public int ClassLevel(string role) => role switch { "heavy" => DevastatorClass, "assault" => AssaultClass, _ => 1 };
    /// <summary>The level perk slot <paramref name="slot"/> opens at; slots are 1-based as <see cref="SmSim.Loadouts.PerkIndex"/>'s (1: always, 2: <see cref="SecondPerkSlot"/>).</summary>
    public int PerkSlotLevel(int slot) => slot == 2 ? SecondPerkSlot : 1;
}

/// <summary>One of the customizer's camera framings (<c>character_customization/camera_settings</c>: tabs initial, weapons, equipment, perks, honors).</summary>
public sealed record CustomizerCamera(string Tab, float HorizontalAngle, float VerticalAngle, float Distance, float VerticalOffset, float HorizontalOffset);

/// <summary>What the customizer offers one race: piece lists per armour slot, sets, badges and chapter presets.</summary>
public sealed record RaceCatalog(string Race, IReadOnlyDictionary<string, IReadOnlyList<ArmourPiece>> Pieces, IReadOnlyList<ArmourSet> Sets,
    IReadOnlyList<ShoulderDecal> Decals, IReadOnlyList<PaintPreset> Presets, ArmourSet? DefaultSet, string DefaultScheme)
{
    /// <summary>The offered pieces of a slot (union of the customizable sets, ordinal by name); empty for an unknown slot.</summary>
    public IReadOnlyList<ArmourPiece> PiecesFor(string slot) => Pieces.TryGetValue(slot, out var p) ? p : [];
    public IEnumerable<ArmourSet> CustomizableSets => Sets.Where(s => s.Customizable);
    public IEnumerable<ArmourSet> PredefinedSets => Sets.Where(s => !s.Customizable);
    public ArmourSet? Set(string name) => Sets.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Everything the character customizer and the custom-class creator offer, read once from the install (engine-free): armour pieces per race and slot,
/// armour sets, the visible palette, shoulder badges and chapter presets (via <see cref="PaintSchemes"/>), unlock levels, class overrides, the
/// customizer cameras, and the creator's weapons, equipment and perks (<see cref="Perks"/>). Display names come from the locale (English), in memory only.
/// Every list is ordinal by name, so the catalogue is deterministic. Sources and counts: docs/customizer-and-perks-data.md.
/// </summary>
public sealed class CustomizationCatalog
{
    public const string SpaceMarine = "space_marine", Chaos = "chaos";
    public const string ArmourSetPackage = "data/packages/mp_armor_set.oppc";

    /// <summary>The nine armour slots of the data.</summary>
    public static readonly IReadOnlyList<string> ArmourSlots = ["head", "torso", "leftshoulder", "rightshoulder", "leftarm", "rightarm", "leftleg", "rightleg", "backpack"];

    CustomizationCatalog(IReadOnlyDictionary<string, RaceCatalog> races, IReadOnlyList<PaletteEntry> palette, IReadOnlyList<ClassCustomization> classes,
        UnlockLevels unlocks, IReadOnlyList<CustomizerCamera> cameras, PerkCatalog perks, PaintSchemes paint, IReadOnlyList<string> notes)
    {
        _races = races; Palette = palette; Classes = classes; Unlocks = unlocks; Cameras = cameras; Perks = perks; Paint = paint; Notes = notes;
        PaletteGroups = palette.GroupBy(c => c.Group).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new PaletteGroup(g.Key, g.ToList())).ToList();
    }

    readonly IReadOnlyDictionary<string, RaceCatalog> _races;

    public RaceCatalog SpaceMarines => _races[SpaceMarine];
    public RaceCatalog ChaosMarines => _races[Chaos];
    /// <summary>The catalogue of a race ("space_marine" or "chaos"), or null.</summary>
    public RaceCatalog? Race(string race) => _races.GetValueOrDefault(race);

    /// <summary>The visible palette (60 in the install), ordinal by name; shared by both races.</summary>
    public IReadOnlyList<PaletteEntry> Palette { get; }
    /// <summary>The palette by group (8 in the install), groups and colours ordinal.</summary>
    public IReadOnlyList<PaletteGroup> PaletteGroups { get; }
    /// <summary>The classes' customizer side, in the data's order.</summary>
    public IReadOnlyList<ClassCustomization> Classes { get; }
    public UnlockLevels Unlocks { get; }
    public IReadOnlyList<CustomizerCamera> Cameras { get; }
    /// <summary>Weapons, equipment and perks per class.</summary>
    public PerkCatalog Perks { get; }
    /// <summary>Colours, decals and schemes (also the source of the decal textures).</summary>
    public PaintSchemes Paint { get; }
    public IReadOnlyList<string> Notes { get; }

    public ClassCustomization? Class(string key) => Classes.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    public CustomizerCamera? Camera(string tab) => Cameras.FirstOrDefault(c => c.Tab.Equals(tab, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the catalogue. <paramref name="paint"/>: an already loaded <see cref="PaintSchemes"/> (loading it reads the whole blueprint archive);
    /// null loads it. Missing optional parts (armour sets, wargear, locale, paint packages) leave empty lists or file names and add notes; only an
    /// unreadable <c>global</c> throws.
    /// </summary>
    public static CustomizationCatalog Load(string gameDir, List<string>? notes = null, PaintSchemes? paint = null, string language = "english")
    {
        notes ??= [];
        var global = CatalogData.ReadGlobal(gameDir);
        var sets = ReadSets(Path.Combine(gameDir, ArmourSetPackage), notes);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in MpClasses.ReadClasses(global))
        {
            names.UnionWith(c.BaseWargear); names.UnionWith(c.StartupWargear); names.UnionWith(c.Perks);
            if (c.DefaultPerk is { Length: > 0 } d) names.Add(d);
        }
        foreach (var set in sets.Values) names.UnionWith(ItemNames(set));
        names.UnionWith(OverrideNames(global));
        var wargear = CatalogData.ReadWargear(gameDir, names, notes);
        var locale = CatalogData.ReadLocale(gameDir, language, notes);
        return Build(global, sets, wargear.GetValueOrDefault, paint ?? PaintSchemes.Load(gameDir, notes), locale, notes);
    }

    /// <summary>
    /// The catalogue as far as the appearance code's ranges need it (<see cref="LimitsFor"/>; the server's path): the armour sets, the wargear files
    /// they name (slots), the paint colours, decals and schemes; no locale, no class wargear, no blueprint scan. The lists are built by
    /// <see cref="Build"/> itself, so their order and filters are the full catalogue's. Notes about what the limits do not need are dropped.
    /// </summary>
    public static CustomizationCatalog LoadLimits(string gameDir, List<string>? notes = null)
    {
        notes ??= [];
        var global = CatalogData.ReadGlobal(gameDir);
        var sets = ReadSets(Path.Combine(gameDir, ArmourSetPackage), notes);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in sets.Values) names.UnionWith(ItemNames(set));
        var wargear = CatalogData.ReadWargear(gameDir, names, notes);
        return Build(global, sets, wargear.GetValueOrDefault, PaintSchemes.Load(gameDir, notes, readDefaults: false), null, []);
    }

    /// <summary>
    /// The sizes of a race's lists an <see cref="AppearanceCode"/> indexes (the wire's meaning of its indices): predefined set = index into
    /// <see cref="RaceCatalog.Sets"/> (every set of the race, ordinal); piece = index into <see cref="RaceCatalog.PiecesFor"/> of the slot (slots in
    /// <see cref="ArmourSlots"/> order = <see cref="ArmourSlot"/>); palette = index into <see cref="Palette"/>; badge = index into
    /// <see cref="RaceCatalog.Decals"/>.
    /// </summary>
    public AppearanceLimits LimitsFor(string race)
    {
        var r = Race(race) ?? throw new ArgumentException($"Unknown race '{race}'.", nameof(race));
        return new AppearanceLimits(CodeSets(r).Count, ArmourSlots.Select(s => r.PiecesFor(s).Count).ToArray(), Palette.Count, r.Decals.Count);
    }

    /// <summary>The list an <see cref="AppearanceCode"/>'s set index refers to: every set of the race, ordinal by name (<see cref="RaceCatalog.Sets"/>).</summary>
    public static IReadOnlyList<ArmourSet> CodeSets(RaceCatalog race) => race.Sets;

    /// <summary>The catalogue from already-read data (testable without an install). <paramref name="armourSets"/>: <c>mp_armor_set</c> entries by name.</summary>
    public static CustomizationCatalog Build(BafDocument global, IReadOnlyDictionary<string, BafDocument> armourSets, Func<string, BafDocument?> wargear,
        PaintSchemes paint, LocaleTable? locale, List<string> notes)
    {
        var challenges = UnlockChallenges.Read(global, locale);
        var perks = PerkCatalog.Build(global, wargear, locale, notes);
        var unlocks = ReadUnlocks(global, notes);
        var classes = MpClasses.ReadClasses(global);
        var classTable = global.Root.Table("extensions/multiplayer_settings/ingame_progression/classes");

        ArmourPiece? Piece(string name, string race, List<string>? setNames = null)
        {
            if (wargear(name) is not { } doc) { notes.Add($"armour: wargear file {name} not found; skipped"); return null; }
            var meta = CatalogData.Meta(doc);
            var packages = new List<string>();
            if (doc.Root.Table("visual_data/attachments") is { } attachments)
                foreach (var a in attachments.Entries.Where(e => e.Type == BafType.Table))
                    if (a.AsTable().String("visual_package") is { Length: > 0 } p) packages.Add(p);
            return new ArmourPiece(name, race, meta.Slot, packages, CatalogData.Text(locale, meta.NameId) ?? name, CatalogData.Text(locale, meta.DescriptionId) ?? "",
                meta.Level, meta.HelmetOverride, challenges.For(name), setNames ?? []);
        }

        // Sets, with the slot of each item from its wargear file.
        var allSets = new List<ArmourSet>();
        foreach (var (name, doc) in armourSets.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string race = doc.Root.String("usable_by_race") ?? "";
            if (race.Length == 0) { notes.Add($"armour set {name}: no usable_by_race; skipped"); continue; }
            var items = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in ItemNames(doc))
            {
                if (wargear(item) is not { } w) { notes.Add($"armour set {name}: wargear file {item} not found"); continue; }
                string slot = CatalogData.Meta(w).Slot;
                if (!ArmourSlots.Contains(slot)) { notes.Add($"armour set {name}: {item} has slot '{slot}', not an armour slot; skipped"); continue; }
                if (!items.TryAdd(slot, item)) notes.Add($"armour set {name}: two items for slot {slot}; the first is kept");
            }
            string? scheme = CatalogData.Leaf(doc.Root.String("custom_paint_scheme") ?? "") is { Length: > 0 } s ? s : null;
            var setPieces = new Dictionary<string, ArmourPiece>(StringComparer.Ordinal);
            foreach (var (slot, item) in items) if (Piece(item, race, [name]) is { } piece) setPieces[slot] = piece;
            allSets.Add(new ArmourSet(name, doc.Root.String("set_id") ?? "", race, !string.Equals(doc.Root.String("usage"), "predefined", StringComparison.OrdinalIgnoreCase),
                CatalogData.Text(locale, CatalogData.IntOf(doc.Root, "set_name")) ?? name, CatalogData.Text(locale, CatalogData.IntOf(doc.Root, "set_description")) ?? "", items, scheme)
            { Pieces = setPieces });
        }

        // Palette: the visible colours.
        var palette = paint.Colours.Where(c => c.VisibleInCustomizer).OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new PaletteEntry(c.Name, CatalogData.Text(locale, c.NameId) ?? c.Name, c.Group, c.Diffuse, c.Specular)).ToList();

        var races = new Dictionary<string, RaceCatalog>(StringComparer.Ordinal);
        foreach (var race in new[] { SpaceMarine, Chaos })
        {
            var sets = allSets.Where(s => s.Race == race).ToList();
            // Pieces: the union of the customizable sets' items per slot, each once, ordinal by name.
            var pieces = ArmourSlots.ToDictionary(s => s, _ => new List<ArmourPiece>(), StringComparer.Ordinal);
            foreach (var slot in ArmourSlots)
            {
                var wanted = sets.Where(s => s.Customizable && s.Items.ContainsKey(slot)).GroupBy(s => s.Items[slot], StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.Ordinal);
                foreach (var g in wanted)
                    if (Piece(g.Key, race, g.Select(s => s.Name).Order(StringComparer.Ordinal).ToList()) is { } piece) pieces[slot].Add(piece);
            }
            var decals = paint.Decals.Where(d => d.VisibleInCustomizer && string.Equals(d.Race, race, StringComparison.Ordinal)).OrderBy(d => d.Name, StringComparer.Ordinal)
                .Select(d => { var (family, variant) = DecalFamily(d.Name); return new ShoulderDecal(d.Name, CatalogData.Text(locale, d.NameId) ?? d.Name, race, family, variant, d.IsBlank, d); }).ToList();
            var presets = paint.Schemes.Where(s => s.VisibleInCustomizer && string.Equals(s.Race, race, StringComparison.Ordinal)).OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new PaintPreset(s.Name, CatalogData.Text(locale, s.NameId) ?? s.Name, race, s.ClassBadgeEnabled, s)).ToList();
            string defaultId = classTable is { } ct ? classes.Where(c => c.Race == race).Select(c => ct.Table(c.Key)?.String("default_armor_set")).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "" : "";
            var defaultSet = ResolveSet(sets, defaultId);
            races[race] = new RaceCatalog(race, pieces.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ArmourPiece>)kv.Value, StringComparer.Ordinal), sets, decals, presets,
                defaultSet, race == SpaceMarine ? paint.MarineDefault : paint.ChaosDefault);
        }

        // Class overrides and defaults.
        var classCustomizations = new List<ClassCustomization>();
        foreach (var c in classes)
        {
            var table = classTable?.Table(c.Key);
            var overrides = new List<ClassOverride>();
            if (table?.Table("override_wargear") is { } ov)
                foreach (var gr in ov.Entries.Where(e => e.Type == BafType.Table))
                {
                    var t = gr.AsTable();
                    if (t.String("wargear") is not { Length: > 0 } w) continue;
                    if (Piece(CatalogData.Leaf(w), c.Race) is not { } piece) continue;
                    overrides.Add(new ClassOverride(piece, t.String("required_wargear") is { Length: > 0 } req ? CatalogData.Leaf(req) : null));
                }
            string raw = table?.String("default_armor_set") ?? "";
            var set = ResolveSet(allSets.Where(s => s.Race == c.Race).ToList(), raw);
            bool jumpPack = c.BaseWargear.Any(n => wargear(n) is { } d && JumpPackTuning.IsJumpPack(d));
            classCustomizations.Add(new ClassCustomization(c.Key, c.Role, c.Race, unlocks.ClassLevel(c.Role), set?.Name ?? raw, overrides) { JumpPack = jumpPack });
        }

        return new CustomizationCatalog(races, palette, classCustomizations, unlocks, ReadCameras(global), perks, paint, notes);
    }

    /// <summary>The set a class's <c>default_armor_set</c> names: matched by <see cref="ArmourSet.SetId"/>, then by entry name.</summary>
    static ArmourSet? ResolveSet(IReadOnlyList<ArmourSet> sets, string id) =>
        id.Length == 0 ? null : sets.FirstOrDefault(s => s.SetId.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? sets.FirstOrDefault(s => s.Name.Equals(id, StringComparison.OrdinalIgnoreCase));

    static readonly string[] DecalSuffixes = ["bw_inverted", "inverted", "bw", "orig", "white", "black"];

    /// <summary>Splits a badge name into its family and variant by the suffix ("sm_ultramarines_black" → "sm_ultramarines", "black").</summary>
    public static (string Family, string Variant) DecalFamily(string name)
    {
        foreach (var s in DecalSuffixes)
            if (name.Length > s.Length + 1 && name.EndsWith("_" + s, StringComparison.OrdinalIgnoreCase)) return (name[..^(s.Length + 1)], s);
        return (name, "");
    }

    static IEnumerable<string> ItemNames(BafDocument set) =>
        set.Root.Table("item_list")?.Entries.Where(e => e.Type == BafType.String).Select(e => CatalogData.Leaf(e.AsString())).Where(n => n.Length > 0) ?? [];

    static IEnumerable<string> OverrideNames(BafDocument global)
    {
        if (global.Root.Table("extensions/multiplayer_settings/ingame_progression/classes") is not { } classes) yield break;
        foreach (var c in classes.Entries.Where(e => e.Type == BafType.Table))
            if (c.AsTable().Table("override_wargear") is { } ov)
                foreach (var g in ov.Entries.Where(e => e.Type == BafType.Table))
                    foreach (var key in new[] { "wargear", "required_wargear" })
                        if (g.AsTable().String(key) is { Length: > 0 } w) yield return CatalogData.Leaf(w);
    }

    static Dictionary<string, BafDocument> ReadSets(string path, List<string> notes)
    {
        var sets = new Dictionary<string, BafDocument>(StringComparer.Ordinal);
        if (!File.Exists(path)) { notes.Add($"{ArmourSetPackage} not found; no armour sets"); return sets; }
        try
        {
            using var archive = ObpkArchive.Open(path);
            archive.ReadEntries(_ => true, (e, data) =>
            {
                if (!BafDocument.HasMagic(data)) return;
                try { sets.TryAdd(e.FileName, BafDocument.Read(data)); }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { notes.Add($"armour set {e.FileName} unreadable ({ex.Message})"); }
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            notes.Add($"{ArmourSetPackage} unreadable ({ex.Message}); what was read before is kept");
        }
        return sets;
    }

    static readonly string[] LoadoutSlotKeys = ["first", "second", "third", "fourth", "fifth", "sixth"];

    static UnlockLevels ReadUnlocks(BafDocument global, List<string> notes)
    {
        var info = global.Root.Table("extensions/profile_progression_info/unlocked_func_info");
        if (info is null) notes.Add("global: no profile_progression_info/unlocked_func_info; every level gate reads as 1");
        int Level(string name)
        {
            if (info?.Table(name) is { } t && t["level_required"] is { Type: BafType.Int } e) return e.Raw;
            if (info is not null) notes.Add($"global: unlocked_func_info/{name} not found; reads as level 1");
            return 1;
        }
        // In key order, stopping at the first missing key: a later slot never moves into a missing one's place.
        var slots = new List<int>();
        foreach (var k in LoadoutSlotKeys)
        {
            if (info?.Table(k + "_equipment_loadout_unlocked") is not { } t || t["level_required"] is not { Type: BafType.Int } e)
            {
                if (info is { } table && LoadoutSlotKeys.Skip(Array.IndexOf(LoadoutSlotKeys, k) + 1).Any(later => table.Table(later + "_equipment_loadout_unlocked") is not null))
                    notes.Add($"global: {k}_equipment_loadout_unlocked missing before later loadout slots; the later ones are not read");
                break;
            }
            slots.Add(e.Raw);
        }
        return new UnlockLevels(Level("customizer_unlocked"), Level("custom_appearance_unlocked"), Level("perk_slot_2_unlocked"), slots, Level("prestige_unlocked"),
            Level("devastator_marine_unlocked"), Level("assault_marine_unlocked"));
    }

    static List<CustomizerCamera> ReadCameras(BafDocument global)
    {
        var result = new List<CustomizerCamera>();
        if (global.Root.Table("extensions/character_customization/camera_settings") is not { } cameras) return result;
        foreach (var e in cameras.Entries.Where(e => e.Type == BafType.Table))
        {
            var t = e.AsTable();
            result.Add(new CustomizerCamera(e.Name, CatalogData.NumberOf(t, "horizontal_angle"), CatalogData.NumberOf(t, "vertical_angle"),
                CatalogData.NumberOf(t, "distance"), CatalogData.NumberOf(t, "vertical_offset"), CatalogData.NumberOf(t, "horizontal_offset")));
        }
        return result;
    }
}
