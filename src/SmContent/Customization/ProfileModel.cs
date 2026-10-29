namespace SmContent.Customization;

// The player's customization profile (docs/customizer-architecture.md section 3): custom classes (loadouts, names stored, race-neutral)
// and per-race appearance profiles. Plain immutable records; the store (ProfileStore) reads, repairs and writes them, ProfileWire maps them
// to the simulation's choice indices and to the wire's AppearanceCode, AppearanceResolver turns them into what a renderer draws.
// Collections inside the records compare by reference: compare profiles through ProfileStore.Serialize.

/// <summary>Which loadout the player plays with.</summary>
public enum SelectionKind { Default, Custom }

/// <summary>
/// The selected loadout: one of the three DEFAULTS (<see cref="SelectionKind.Default"/>, by <see cref="Role"/>) or a custom class slot
/// (<see cref="SelectionKind.Custom"/>, <see cref="Slot"/> 1..<see cref="CustomizationProfile.MaxCustomClasses"/>).
/// </summary>
public sealed record Selection(SelectionKind Kind, string? Role, int Slot)
{
    public static Selection DefaultFor(string role) => new(SelectionKind.Default, role, 0);
    public static Selection CustomSlot(int slot) => new(SelectionKind.Custom, null, slot);
}

/// <summary>The appearance profile ids a custom class wears per race (null: the race's default look).</summary>
public sealed record AppearanceRefs(string? SpaceMarine, string? Chaos)
{
    public static readonly AppearanceRefs None = new(null, null);

    public string? For(string race) => race switch { CustomizationCatalog.SpaceMarine => SpaceMarine, CustomizationCatalog.Chaos => Chaos, _ => null };

    public AppearanceRefs With(string race, string? id) => race switch
    {
        CustomizationCatalog.SpaceMarine => this with { SpaceMarine = id },
        CustomizationCatalog.Chaos => this with { Chaos = id },
        _ => throw new ArgumentException($"Unknown race '{race}'.", nameof(race)),
    };
}

/// <summary>
/// A custom class. The class is per <see cref="Role"/> ("tactical", "heavy", "assault") and shared by both sides: item names are stored race-neutral
/// (<see cref="ProfileWire.Neutral"/>: "mp_ranged_bolter" for sm_mp_ranged_bolter and csm_mp_ranged_bolter) and resolved for the side played;
/// null means the class default. <see cref="Perks"/> are the two perk slots (wargear names; null: the class default perk for slot 1, none for slot 2).
/// </summary>
public sealed record CustomClass(int Slot, string Name, string Role, string? Primary, string? Pistol, string? Melee, string? Equipment,
    IReadOnlyList<string?> Perks, AppearanceRefs Appearance)
{
    public string? Perk(int index) => index >= 0 && index < Perks.Count ? Perks[index] : null;
}

/// <summary>
/// The three colours of one armour slot as palette names (<see cref="CustomizationCatalog.Palette"/>); null: the scheme's own colour.
/// <see cref="Pattern"/> is the "pattern colour" (the paint mask's secondary channel), <see cref="Detail"/> the "detail colour" (tertiary).
/// <see cref="PatternOn"/> false: the pattern shows in the primary colour (secondary equals primary).
/// </summary>
public sealed record SlotPaint(string? Primary, string? Pattern, string? Detail, bool PatternOn)
{
    /// <summary>No colour set: the scheme's paint applies (and <see cref="PatternOn"/> does not matter).</summary>
    public bool IsEmpty => Primary is null && Pattern is null && Detail is null;
}

/// <summary>
/// What a shoulder pad shows: a badge by name (<see cref="CustomizationCatalog"/> decals of the race; the blank badge means none), a custom emblem
/// by hash (<see cref="Emblem"/>, 32 hex digits; the file is <c>emblems/&lt;hex&gt;.bc3</c>) with a fallback badge shown until it arrives or when it
/// is missing, or nothing (<see cref="None"/>). A pad without an entry shows the scheme's own decal.
/// </summary>
/// <remarks>Named DecalChoice, not ShoulderDecal: <c>SmContent.ShoulderDecal</c> is the catalogue's badge.</remarks>
public sealed record DecalChoice(string? Badge, string? Emblem, string? Fallback, bool None = false)
{
    public static DecalChoice ForBadge(string badge) => new(badge, null, null);
    public static DecalChoice ForEmblem(string hex, string? fallback) => new(null, hex, fallback);
    public static readonly DecalChoice NoDecal = new(null, null, null, true);
}

/// <summary>An eye-lens colour (our addition; null in a profile means the original look).</summary>
public readonly record struct LensColour(byte R, byte G, byte B);

/// <summary>
/// One look for one race. <see cref="Pieces"/> maps an armour slot (<see cref="CustomizationCatalog.ArmourSlots"/>) to an offered piece's wargear name
/// (an absent slot wears the class's default set's piece); <see cref="Paint"/> maps a slot to its colours (absent: the scheme's); <see cref="Decals"/>
/// maps "leftshoulder"/"rightshoulder" to the pad's decal (absent: the scheme's). <see cref="PredefinedSet"/> (a fixed full set, its scheme locks the
/// paint and the decals) overrides pieces, paint and decals. <see cref="PresetApplied"/> only remembers which chapter / warband preset the paint came from
/// (<see cref="ProfileWire.ApplyPreset"/> copies the preset's colours into <see cref="Paint"/>).
/// </summary>
public sealed record AppearanceProfile(string Id, string Race, string Name, string? PredefinedSet,
    IReadOnlyDictionary<string, string> Pieces, IReadOnlyDictionary<string, SlotPaint> Paint, string? PresetApplied,
    IReadOnlyDictionary<string, DecalChoice> Decals, LensColour? Lens)
{
    public static AppearanceProfile Empty(string id, string race, string name) =>
        new(id, race, name, null, new Dictionary<string, string>(), new Dictionary<string, SlotPaint>(), null, new Dictionary<string, DecalChoice>(), null);
}

/// <summary>
/// The whole profile. <see cref="ReadOnly"/> is set when the file on disk has a newer schema than this build knows: the store never overwrites it.
/// </summary>
public sealed record CustomizationProfile(int Schema, Selection Selected, IReadOnlyList<CustomClass> Classes, IReadOnlyList<AppearanceProfile> Appearances)
{
    public const int CurrentSchema = 1, MaxCustomClasses = 10, MaxAppearancesPerRace = 10;
    public const int MaxNameLength = 24, MaxIdLength = 32;

    public bool ReadOnly { get; init; }

    /// <summary>
    /// The look the player wears as the Ork Boy (ui/customizer-ork, Ork O9; added content): 0..<see cref="SmSim.Races.OrkLooks"/>-1 (<see cref="OrkLookRules.Names"/>), null =
    /// automatic (chosen by player id). One for the profile (docs/ork-faction-design.md section 7: <c>orkLook</c> in the profile), shared by the Ork DEFAULTS and every Ork
    /// custom loadout: the Ork has no armour, colours or badges. An optional field: schema 1 stays; written only when set.
    /// </summary>
    public int? OrkLook { get; init; }

    /// <summary>The profile of a player who never opened the customizer: the Tactical DEFAULTS loadout, no custom class, no appearance.</summary>
    public static CustomizationProfile Default(CustomizationCatalog catalog) =>
        new(CurrentSchema, Selection.DefaultFor(ProfileStore.FirstRole(catalog)), [], []);

    public CustomClass? ClassAt(int slot) => Classes.FirstOrDefault(c => c.Slot == slot);
    public AppearanceProfile? Appearance(string? id) => id is null ? null : Appearances.FirstOrDefault(a => a.Id == id);
    public IEnumerable<AppearanceProfile> AppearancesFor(string race) => Appearances.Where(a => a.Race == race);

    /// <summary>The selected custom class, or null when a DEFAULTS loadout is selected (or the slot is empty).</summary>
    public CustomClass? SelectedClass => Selected.Kind == SelectionKind.Custom ? ClassAt(Selected.Slot) : null;

    /// <summary>The lowest unused custom class slot (1..<see cref="MaxCustomClasses"/>), or 0 when all are used.</summary>
    public int FirstFreeSlot()
    {
        for (int slot = 1; slot <= MaxCustomClasses; slot++) if (ClassAt(slot) is null) return slot;
        return 0;
    }

    /// <summary>An unused appearance id for <paramref name="race"/> ("sm-1", "csm-2", ...).</summary>
    public string NewAppearanceId(string race)
    {
        string prefix = race == CustomizationCatalog.Chaos ? "csm-" : "sm-";
        for (int n = 1; ; n++)
            if (Appearance(prefix + n) is null) return prefix + n;
    }
}
