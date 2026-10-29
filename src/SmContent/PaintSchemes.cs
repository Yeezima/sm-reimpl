using SmFormats.Baf;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>One paint colour (<c>custom_paint_colour</c>): the diffuse and specular colour, each RGB 0..1 as stored (colour space unknown).</summary>
public sealed record PaintColour(string Name, float[] Diffuse, float[] Specular, bool VisibleInCustomizer)
{
    /// <summary>The customizer's colour group (<c>colour_type</c>: blue, metallics, ...); empty when absent.</summary>
    public string Group { get; init; } = "";
    /// <summary>The locale id of the display name (<c>name</c>); 0 when absent.</summary>
    public int NameId { get; init; }
}

/// <summary>
/// A decal of <c>custom_paint_decal</c>: the texture (<see cref="Texture"/>, a 128x128 DXT5 ".dds" with its alpha as the shape) and the package it
/// is in (<see cref="Package"/>, <c>data/packages/&lt;Package&gt;.oppc</c>; "blank" has none). <see cref="Race"/> is "space_marine" or "chaos".
/// </summary>
public sealed record PaintDecal(string Name, string Texture, string Package, string? Race)
{
    /// <summary><c>visible_in_customizer</c> (false for class identifiers, rank marks, test and AI badges); true when absent.</summary>
    public bool VisibleInCustomizer { get; init; } = true;
    /// <summary>The locale id of the display name (<c>name</c>); 0 when absent.</summary>
    public int NameId { get; init; }

    /// <summary>The "blank" decal (five entries in the install): no texture, no package; it means no decal.</summary>
    public bool IsBlank => Package.Equals("blank", StringComparison.OrdinalIgnoreCase) || Texture.Equals("blank", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The three colours of one armour piece: the paint mask's red (primary), green (secondary) and blue (tertiary) regions.</summary>
public sealed record PaintSet(PaintColour Primary, PaintColour Secondary, PaintColour Tertiary, bool PatternEnabled, string? Decal);

/// <summary>
/// A <c>custom_paint_scheme</c>: a paint set per armour slot (<see cref="PaintSchemes.Slots"/>) and a default for the slots it leaves
/// empty (<c>default_paint</c>). <see cref="Race"/> is "space_marine" or "chaos".
/// </summary>
public sealed record PaintScheme(string Name, string? Race, IReadOnlyDictionary<string, PaintSet> Slots, PaintSet? Default)
{
    /// <summary><c>visible_in_customizer</c> (the chapter / warband presets; false for tests, AI and DLC-set schemes); true when absent.</summary>
    public bool VisibleInCustomizer { get; init; } = true;
    /// <summary><c>class_badge_enabled</c>.</summary>
    public bool ClassBadgeEnabled { get; init; }
    /// <summary>The locale id of the display name (<c>name</c>); 0 when absent.</summary>
    public int NameId { get; init; }

    /// <summary>The paint of an armour slot: its own set when the scheme gives one, else <c>default_paint</c> (null when neither exists).</summary>
    public PaintSet? For(string slot) => Slots.TryGetValue(slot, out var set) ? set : Default;
}

/// <summary>
/// Paint schemes and colours from the install (<c>data/packages/custom_paint_scheme.oppc</c>, <c>custom_paint_colour.oppc</c>)
/// and the default scheme of each multiplayer side (<c>ebps</c> player blueprints, <c>extensions/wargear_ext/default_custom_paint</c>).
/// Evidence (Verified-local): the Space Marine player blueprint <c>sm_space_marine_player</c> names <c>sm_ultramarines</c>, the Chaos
/// player <c>csm_chaos_marine_player</c> names <c>chaos_multiplayer</c>. A scheme's <c>paints</c> table has one entry per armour slot
/// (head, leftshoulder, rightshoulder, leftarm, rightarm, torso, leftleg, rightleg, backpack, plus empty weapon slots), each with
/// <c>primary_colour</c>, <c>secondary_colour</c>, <c>tertiary_colour</c> (paths ending in a colour name), <c>decals</c> and
/// <c>pattern_enabled</c>; an empty slot falls back to <c>default_paint</c>.
/// </summary>
public sealed class PaintSchemes
{
    public const string SchemePackage = "data/packages/custom_paint_scheme.oppc";
    public const string ColourPackage = "data/packages/custom_paint_colour.oppc";
    public const string DecalPackage = "data/packages/custom_paint_decal.oppc";
    public const string BlueprintPackage = GameplayTuning.BlueprintPackage;
    public const string MarinePlayer = "sm_space_marine_player", ChaosPlayer = "csm_chaos_marine_player";

    readonly Dictionary<string, PaintScheme> _schemes;
    readonly Dictionary<string, PaintDecal> _decals;
    readonly Dictionary<string, PaintColour> _colours;

    PaintSchemes(Dictionary<string, PaintScheme> schemes, Dictionary<string, PaintDecal> decals, Dictionary<string, PaintColour> colours, string marineDefault, string chaosDefault)
    {
        _schemes = schemes; _decals = decals; _colours = colours; MarineDefault = marineDefault; ChaosDefault = chaosDefault;
    }

    /// <summary>Every paint colour read (the customizer-visible one when a name occurs twice).</summary>
    public IEnumerable<PaintColour> Colours => _colours.Values;
    /// <summary>Every decal read.</summary>
    public IEnumerable<PaintDecal> Decals => _decals.Values;
    /// <summary>Every scheme read.</summary>
    public IEnumerable<PaintScheme> Schemes => _schemes.Values;

    /// <summary>The decal a scheme's slot names (<see cref="PaintSet.Decal"/>), or null when it is unknown.</summary>
    public PaintDecal? Decal(string? name) => name is not null && _decals.TryGetValue(name, out var d) ? d : null;
    public int DecalCount => _decals.Count;

    /// <summary>
    /// The decal of an armour piece under a scheme (its slot's set, else the scheme's <c>default_paint</c>), or null: no decal named, or the
    /// name is not in <c>custom_paint_decal</c>. A "blank" decal is returned (<see cref="PaintDecal.IsBlank"/>: the caller draws none, silently).
    /// </summary>
    public PaintDecal? DecalFor(PaintScheme scheme, string pieceName) => For(scheme, pieceName) is { Decal: { } name } ? Decal(name) : null;

    /// <summary>
    /// The decal's texture bytes (DDS), or null when it is blank, its package or entry is not in the install, or the package is unreadable (also
    /// corrupt data). A package name with a path separator, a drive colon or ".." is refused (it must stay in <c>data/packages</c>).
    /// </summary>
    public static byte[]? ReadDecalTexture(string gameDir, PaintDecal decal)
    {
        if (decal.IsBlank || decal.Package.Length == 0 || decal.Package.IndexOfAny(['\\', '/', ':']) >= 0 || decal.Package.Contains("..", StringComparison.Ordinal)) return null;
        string path = Path.Combine(gameDir, "data", "packages", decal.Package + ".oppc");
        if (!File.Exists(path)) return null;
        try
        {
            byte[]? bytes = null;
            using var archive = ObpkArchive.Open(path);
            archive.ReadEntries(e => bytes is null && archive.GroupName(e) == "dds" && e.FileName.Equals(decal.Texture, StringComparison.OrdinalIgnoreCase), (_, data) => bytes = data.ToArray());
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Whether a mesh block's second UV set (<c>Uv1</c>) can carry a decal: it spans more than 0.5 in both axes and stays inside -0.5..1.5, as
    /// the Space Marine and basic Chaos shoulder pads' single island over the unit square does. A degenerate set (all zero; the Havoc and
    /// Raptor pads), a thin one and a tiled one are refused. It is a sanity check, not the gate: other layouts of that size (the Space Marine
    /// torso's, up to 1.25) pass it too, so the scheme slot (decals exist on the shoulder slots only) decides which pieces are asked.
    /// </summary>
    public static bool HasDecalUv(IReadOnlyList<System.Numerics.Vector2> uv1)
    {
        if (uv1.Count == 0) return false;
        float minX = uv1.Min(v => v.X), maxX = uv1.Max(v => v.X), minY = uv1.Min(v => v.Y), maxY = uv1.Max(v => v.Y);
        return maxX - minX > 0.5f && maxY - minY > 0.5f && minX > -0.5f && minY > -0.5f && maxX < 1.5f && maxY < 1.5f;
    }

    public string MarineDefault { get; }
    public string ChaosDefault { get; }
    public IEnumerable<string> Names => _schemes.Keys.Order(StringComparer.OrdinalIgnoreCase);
    public PaintScheme? Scheme(string name) => _schemes.TryGetValue(name, out var s) ? s : null;

    /// <summary>The armour slots of a scheme, in the data's order.</summary>
    public static readonly string[] Slots = ["head", "leftshoulder", "rightshoulder", "leftarm", "rightarm", "torso", "leftleg", "rightleg", "backpack"];

    /// <summary>
    /// The paint slot of an armour piece or backpack weapon by its package name's suffix (e.g. "..._shldrl" → "leftshoulder");
    /// null for pieces with no slot (a belt): they take <see cref="PaintScheme.Default"/>.
    /// </summary>
    public static string? SlotOf(string pieceName)
    {
        string n = pieceName.ToLowerInvariant();
        if (n.EndsWith("_helmet") || n.EndsWith("_head")) return "head";
        if (n.EndsWith("_shldrl")) return "leftshoulder";
        if (n.EndsWith("_shldrr")) return "rightshoulder";
        if (n.EndsWith("_arml")) return "leftarm";
        if (n.EndsWith("_armr")) return "rightarm";
        if (n.EndsWith("_torso")) return "torso";
        if (n.EndsWith("_legl")) return "leftleg";
        if (n.EndsWith("_legr")) return "rightleg";
        if (n.EndsWith("_backpack") || n.EndsWith("_bp") || n.Contains("_bp_") || n.Contains("jumppack") || n.Contains("jumpack")) return "backpack";
        return null;
    }

    /// <summary>The paint of a piece: the scheme's slot (or its default for a slot-less piece); null when the scheme has neither.</summary>
    public static PaintSet? For(PaintScheme scheme, string pieceName) =>
        SlotOf(pieceName) is { } slot ? scheme.For(slot) : scheme.Default;

    /// <summary>The scheme names used when the blueprints cannot be read (Verified-local values of the install).</summary>
    public const string FallbackMarineScheme = "sm_ultramarines", FallbackChaosScheme = "chaos_multiplayer";

    /// <summary>How long <see cref="Load"/> took, for the client's log (the blueprint archive is large: the whole archive stream is read).</summary>
    public TimeSpan LoadTime { get; private set; }

    /// <summary>
    /// Reads the colours, the schemes and each side's default scheme. Every part is read on its own: a missing or unreadable package
    /// leaves the others (and the hard-coded defaults <see cref="FallbackMarineScheme"/> / <see cref="FallbackChaosScheme"/>) in place and adds
    /// a note; an unreadable entry is skipped. Never throws for bad data.
    /// </summary>
    /// <param name="readDefaults">Read the sides' default schemes from the player blueprints (a scan of the whole blueprint archive); false keeps
    /// the fallback names (the server's limits-only path, <see cref="CustomizationCatalog.LoadLimits"/>).</param>
    public static PaintSchemes Load(string gameDir, List<string>? notes = null, bool readDefaults = true)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var colours = new Dictionary<string, PaintColour>(StringComparer.OrdinalIgnoreCase);
        Guarded("paint colours", notes, () => ReadAll(Path.Combine(gameDir, ColourPackage), (name, doc) =>
        {
            if (ParseColour(name, doc) is not { } c) return;
            // One name occurs twice in the install (ultramarines_blue); the customizer-visible one is the "citadel_paints" colour the schemes
            // name (an interpretation: the archive keeps no folder). Otherwise the first wins.
            if (!colours.TryGetValue(name, out var old) || (!old.VisibleInCustomizer && c.VisibleInCustomizer)) colours[name] = c;
        }));
        var schemes = new Dictionary<string, PaintScheme>(StringComparer.OrdinalIgnoreCase);
        Guarded("paint schemes", notes, () => ReadAll(Path.Combine(gameDir, SchemePackage), (name, doc) =>
        {
            if (ParseScheme(name, doc, colours, notes) is { } s) schemes[name] = s;
        }));
        var decals = new Dictionary<string, PaintDecal>(StringComparer.OrdinalIgnoreCase);
        Guarded("paint decals", notes, () => ReadAll(Path.Combine(gameDir, DecalPackage), (name, doc) =>
        {
            if (ParseDecal(name, doc) is { } d) decals.TryAdd(name, d);
        }));
        string marine = FallbackMarineScheme, chaos = FallbackChaosScheme;
        if (readDefaults) Guarded("default paint schemes (ebps)", notes, () =>
        {
            string blueprints = Path.Combine(gameDir, BlueprintPackage);
            if (!File.Exists(blueprints)) return;
            using var archive = ObpkArchive.Open(blueprints);
            archive.ReadEntries(e => e.FileName is MarinePlayer or ChaosPlayer, (e, data) =>
            {
                try
                {
                    if (!BafDocument.HasMagic(data) || DefaultSchemeName(BafDocument.Read(data)) is not { } name) return;
                    if (e.FileName == MarinePlayer) marine = name; else chaos = name;
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { notes?.Add($"default paint scheme of '{e.FileName}' unreadable ({ex.Message}); the built-in default is used"); }
            });
        });
        return new PaintSchemes(schemes, decals, colours, marine, chaos) { LoadTime = watch.Elapsed };
    }

    static void Guarded(string what, List<string>? notes, Action read)
    {
        try { read(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            notes?.Add($"{what} unreadable ({ex.Message}); what was read before is kept");
        }
    }

    /// <summary>The last path segment of <c>extensions/wargear_ext/default_custom_paint</c> of a player blueprint, or null.</summary>
    public static string? DefaultSchemeName(BafDocument blueprint) =>
        Leaf(blueprint.Root.Table("extensions/wargear_ext")?.String("default_custom_paint"));

    /// <summary>The colour a paint path names (the last segment, e.g. "...\citadel_paints\burnished_gold" → "burnished_gold"), or null when empty.</summary>
    public static string? Leaf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        int i = path.LastIndexOfAny(['\\', '/']);
        return i < 0 ? path : path[(i + 1)..];
    }

    public static PaintColour? ParseColour(string name, BafDocument doc)
    {
        float[]? Rgb(string table) => doc.Root.Table(table) is { } t && t.Float("red") is { } r && t.Float("green") is { } g && t.Float("blue") is { } b ? [r, g, b] : null;
        if (Rgb("diffuse") is not { } diffuse) return null;
        // True when absent, as decals, schemes and wargear (every one of the install's 65 colours, 122 decals and 80 schemes has the field).
        bool visible = doc.Root["visible_in_customizer"] is not { Type: BafType.Bool } v || v.AsBool();
        return new PaintColour(name, diffuse, Rgb("specular") ?? diffuse, visible) { Group = doc.Root.String("colour_type") ?? "", NameId = IntOf(doc.Root, "name") };
    }

    public static PaintDecal? ParseDecal(string name, BafDocument doc) =>
        doc.Root.String("decal") is { Length: > 0 } texture
            ? new PaintDecal(name, texture, doc.Root.String("decal_package") is { Length: > 0 } p ? p : texture, doc.Root.String("race"))
            { VisibleInCustomizer = doc.Root["visible_in_customizer"] is not { Type: BafType.Bool } v || v.AsBool(), NameId = IntOf(doc.Root, "name") }
            : null;

    /// <summary>An integer entry of a table (the BAF reader has no accessor for them); 0 when absent or of another type.</summary>
    static int IntOf(BafTable table, string name) => table[name] is { Type: BafType.Int } e ? e.Raw : 0;

    public static PaintScheme? ParseScheme(string name, BafDocument doc, IReadOnlyDictionary<string, PaintColour> colours, List<string>? notes = null)
    {
        PaintColour? Colour(BafTable t, string key)
        {
            string? leaf = Leaf(t.String(key));
            if (leaf is null) return null;
            if (colours.TryGetValue(leaf, out var c)) return c;
            notes?.Add($"paint scheme '{name}': colour '{leaf}' not found");
            return null;
        }
        PaintSet? Set(BafTable t)
        {
            if (Colour(t, "primary_colour") is not { } p) return null;
            string? decal = t.Table("decals") is { } d ? Leaf(d.String("decal")) : null;
            bool pattern = t["pattern_enabled"] is { Type: BafType.Bool } e && e.AsBool();
            return new PaintSet(p, Colour(t, "secondary_colour") ?? p, Colour(t, "tertiary_colour") ?? p, pattern, decal);
        }
        var slots = new Dictionary<string, PaintSet>(StringComparer.Ordinal);
        if (doc.Root.Table("paints") is { } paints)
            foreach (var slot in Slots)
                if (paints.Table(slot) is { } t && Set(t) is { } set) slots[slot] = set;
        var def = doc.Root.Table("default_paint") is { } dp ? Set(dp) : null;
        if (slots.Count == 0 && def is null) return null;
        return new PaintScheme(name, doc.Root.String("race"), slots, def)
        {
            VisibleInCustomizer = doc.Root["visible_in_customizer"] is not { Type: BafType.Bool } v || v.AsBool(),
            ClassBadgeEnabled = doc.Root["class_badge_enabled"] is { Type: BafType.Bool } b && b.AsBool(),
            NameId = IntOf(doc.Root, "name"),
        };
    }

    static void ReadAll(string path, Action<string, BafDocument> visit)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"{Path.GetFileName(path)} not found");
        using var archive = ObpkArchive.Open(path);
        archive.ReadEntries(_ => true, (e, data) =>
        {
            if (!BafDocument.HasMagic(data)) return;
            try { visit(e.FileName, BafDocument.Read(data)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }   // one bad entry is skipped
        });
    }
}
