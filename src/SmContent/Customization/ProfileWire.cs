using SmSim;

namespace SmContent.Customization;

/// <summary>
/// Maps a profile to the wire (docs/customizer-architecture.md sections 2 and 4): a <see cref="CustomClass"/> to the simulation's
/// <see cref="LoadoutChoice"/> (indices into the class's lists), an <see cref="AppearanceProfile"/> to the 62-byte <see cref="AppearanceCode"/> and back.
/// The profile stores names and the wire carries indices; both ends derive the indices from lists built from the same install.
/// </summary>
public static class ProfileWire
{
    // ---- names ----

    /// <summary>
    /// A wargear name without its side prefix ("sm_mp_ranged_bolter" and "csm_mp_ranged_bolter" → "mp_ranged_bolter"): the form a custom class stores,
    /// so one class serves both sides. Names with neither prefix (perks) are unchanged.
    /// </summary>
    public static string Neutral(string wargear) => LoadoutIndices.Neutral(wargear);

    /// <summary>The race of a side ("space_marine" / "chaos"); null for <see cref="Team.None"/>.</summary>
    public static string? RaceOf(Team side) => side switch { Team.SpaceMarines => CustomizationCatalog.SpaceMarine, Team.Chaos => CustomizationCatalog.Chaos, _ => null };

    /// <summary>The side of a race; <see cref="Team.None"/> for an unknown race.</summary>
    public static Team SideOf(string race) => race switch { CustomizationCatalog.SpaceMarine => Team.SpaceMarines, CustomizationCatalog.Chaos => Team.Chaos, _ => Team.None };

    // ---- the Ork (Ork O5, protocol 28; the customizer's faction step is O9) ----

    /// <summary>The Orks' race name ("ork", the Ork Boy's <see cref="OrkClass.Race"/>; the catalogue has no Ork race).</summary>
    public const string Ork = OrkClass.Race;

    /// <summary>The wire race of a race name: "space_marine", "chaos", "ork"; <see cref="SmSim.Race.None"/> for anything else.</summary>
    public static Race WireRaceOf(string? race) => race switch
    {
        CustomizationCatalog.SpaceMarine => SmSim.Race.SpaceMarines, CustomizationCatalog.Chaos => SmSim.Race.Chaos, Ork => SmSim.Race.Orks, _ => SmSim.Race.None,
    };

    /// <summary>The race name of a wire race; null for <see cref="SmSim.Race.None"/> or an unknown value.</summary>
    public static string? RaceName(Race race) => race switch
    {
        SmSim.Race.SpaceMarines => CustomizationCatalog.SpaceMarine, SmSim.Race.Chaos => CustomizationCatalog.Chaos, SmSim.Race.Orks => Ork, _ => null,
    };

    /// <summary>
    /// An Ork look as the wire byte (the Hello's and Setup's <c>OrkLook</c>): 0..<see cref="Races.OrkLooks"/>-1 for a look by index; null, negative or
    /// out of range: <see cref="Races.OrkLookAutomatic"/>.
    /// </summary>
    public static byte OrkLookByte(int? look) => look is >= 0 and < Races.OrkLooks ? (byte)look.Value : Races.OrkLookAutomatic;

    /// <summary>The data's slot name of an <see cref="ArmourSlot"/> ("head", "leftshoulder", ...).</summary>
    public static string SlotName(ArmourSlot slot) => CustomizationCatalog.ArmourSlots[(int)slot];

    /// <summary>The <see cref="ArmourSlot"/> of a slot name, or null.</summary>
    public static ArmourSlot? SlotOf(string name)
    {
        int i = CustomizationCatalog.ArmourSlots.ToList().FindIndex(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? null : (ArmourSlot)i;
    }

    // ---- loadout ----

    /// <summary>The index of the class of <paramref name="role"/> that plays for <paramref name="side"/>, or -1.</summary>
    public static int ClassIndex(SimConfig config, Team side, string role)
    {
        // ui/customizer-ork: the Ork Boy has no side (he fights for either, World.AssignSide); his class is the configuration's Ork class whatever the side.
        if (role.Equals(OrkClass.Role, StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < config.Classes.Count; i++)
                if (config.Classes[i].Race == SmSim.Race.Orks && string.Equals(config.Classes[i].Role, role, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        if (side == Team.None) return -1;
        for (int i = 0; i < config.Classes.Count; i++)
            if (config.Classes[i].Side == side && string.Equals(config.Classes[i].Role, role, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The DEFAULTS loadout of a role for a side: the class, every choice the default. <see cref="LoadoutChoice.Default"/> when no class matches.</summary>
    public static LoadoutChoice DefaultLoadout(string role, SimConfig config, Team side) =>
        ClassIndex(config, side, role) is var cls and >= 0 ? LoadoutChoice.Default with { Class = cls } : LoadoutChoice.Default;

    /// <summary>
    /// The loadout request of <paramref name="custom"/> for the class playing <paramref name="side"/>. Every item is found <b>by name</b> in the class's lists of
    /// <paramref name="config"/> (<see cref="Loadouts.Choices"/>, <see cref="ClassLoadout.MeleeChoices"/>, <see cref="ClassLoadout.EquipmentChoices"/>,
    /// <see cref="ClassLoadout.PerkChoices"/>), never by catalogue position: an item the simulation dropped (an unsimulated item; stims and the power weapons are simulated now) is not in the lists
    /// and becomes the default (-1). When the side's list has no item of that name but the other side's class of the same role does, the item at the
    /// same position of the side's list is taken (**interpretation**: Chaos has its own melee weapons and heavy weapon names). The class is the side's class
    /// of the custom class's role; with none, <see cref="LoadoutChoice.Default"/>.
    /// </summary>
    public static LoadoutChoice ToLoadout(CustomClass custom, SimConfig config, Team side)
    {
        int cls = ClassIndex(config, side, custom.Role);
        if (cls < 0) return LoadoutChoice.Default;
        int other = ClassIndex(config, side == Team.SpaceMarines ? Team.Chaos : Team.SpaceMarines, custom.Role);
        // Names become indices only through LoadoutIndices (by name, side prefixes ignored); the positional fallback picks the other side's index.
        int Pick(string? name, string slot, bool positional = true)
        {
            int i = LoadoutIndices.Choice(config, cls, slot, name);
            if (i >= 0 || name is null || !positional || other < 0) return i;
            int j = LoadoutIndices.Choice(config, other, slot, name);
            return j >= 0 && j < LoadoutIndices.Names(config, cls, slot).Count ? j : -1;
        }
        return new LoadoutChoice(cls,
            Pick(custom.Primary, "ranged"), Pick(custom.Pistol, "pistol"), Pick(custom.Melee, "melee"), Pick(custom.Equipment, "equipment"),
            Pick(custom.Perk(0), "perk", positional: false), Pick(custom.Perk(1), "perk", positional: false));
    }

    /// <summary>
    /// The loadout request for the profile's selection on <paramref name="side"/>: the selected custom class, or the DEFAULTS loadout of the selected role
    /// (an empty or missing slot reads as the first role's DEFAULTS).
    /// </summary>
    public static LoadoutChoice ToLoadout(CustomizationProfile profile, SimConfig config, Team side, CustomizationCatalog catalog)
    {
        if (profile.SelectedClass is { } custom) return ToLoadout(custom, config, side);
        string role = profile.Selected.Kind == SelectionKind.Default && profile.Selected.Role is { Length: > 0 } r ? r : ProfileStore.FirstRole(catalog);
        return DefaultLoadout(role, config, side);
    }

    // ---- appearance ----

    /// <summary>The catalogue sizes an <see cref="AppearanceCode"/> of <paramref name="race"/> indexes: the server's own (<see cref="CustomizationCatalog.LimitsFor"/>).</summary>
    public static AppearanceLimits LimitsFor(RaceCatalog race, CustomizationCatalog catalog) => catalog.LimitsFor(race.Race);

    /// <summary>
    /// The list a code's set index refers to (<see cref="CustomizationCatalog.CodeSets"/>: every set of the race, ordinal by name). Choosing any set there wears
    /// it whole (pieces, paint and decals locked); the customizer offers the predefined ones (<see cref="RaceCatalog.PredefinedSets"/>) for that.
    /// </summary>
    public static IReadOnlyList<ArmourSet> PredefinedSetsOf(RaceCatalog race) => CustomizationCatalog.CodeSets(race);

    /// <summary>
    /// The wire form of a look. Indices are positions in the catalogue's ordinal lists for the profile's race: sets (<see cref="CustomizationCatalog.CodeSets"/>),
    /// pieces (<see cref="RaceCatalog.PiecesFor"/>), the palette (<see cref="CustomizationCatalog.Palette"/>), badges (<see cref="RaceCatalog.Decals"/>). A name that is
    /// not in its list becomes the default (255; decals 253). A predefined set locks pieces, paint and decals (only the lens stays). A custom emblem is 254 with
    /// its hash and the fallback badge; a code carries one emblem, so a second pad with a different emblem shows its fallback. The pattern bit of a slot with
    /// no colour set is 0 (the scheme's own <c>pattern_enabled</c> applies).
    /// </summary>
    public static AppearanceCode ToCode(AppearanceProfile profile, CustomizationCatalog catalog)
    {
        if (catalog.Race(profile.Race) is not { } race) return AppearanceCode.Default;
        var code = AppearanceCode.Default;
        if (profile.Lens is { } lens) code = code.WithLens((lens.R, lens.G, lens.B));
        if (profile.PredefinedSet is { } setName)
        {
            int set = PredefinedSetsOf(race).ToList().FindIndex(s => s.Name.Equals(setName, StringComparison.OrdinalIgnoreCase));
            if (set >= 0) return code.WithPredefinedSet((byte)set);
        }
        for (int i = 0; i < AppearanceCode.Slots; i++)
        {
            var slot = (ArmourSlot)i;
            string name = SlotName(slot);
            if (profile.Pieces.TryGetValue(name, out var piece) && IndexOf(race.PiecesFor(name), p => p.Name, piece) is var pi and >= 0) code = code.WithPiece(slot, (byte)pi);
            if (profile.Paint.TryGetValue(name, out var paint))
            {
                byte P(string? colour) => colour is not null && IndexOf(catalog.Palette, c => c.Name, colour) is var k and >= 0 ? (byte)k : AppearanceCode.DefaultIndex;
                byte a = P(paint.Primary), b = P(paint.Pattern), c = P(paint.Detail);
                if (a != 255 || b != 255 || c != 255)
                {
                    code = code.WithPalette(slot, a, b, c);
                    if (paint.PatternOn) code = code.WithPattern(slot, true);
                }
            }
        }
        byte left = AppearanceCode.DecalSchemeDefault, right = AppearanceCode.DecalSchemeDefault, fallback = AppearanceCode.FallbackSchemeDefault;
        var hash = EmblemHash.None;
        byte Pad(string pad, ref byte fallbackOut, ref EmblemHash hashOut)
        {
            if (!profile.Decals.TryGetValue(pad, out var d)) return AppearanceCode.DecalSchemeDefault;
            if (d.None) return AppearanceCode.DecalNone;
            byte fb = d.Fallback is { } f && IndexOf(race.Decals, x => x.Name, f) is var fi and >= 0 ? (byte)fi : AppearanceCode.FallbackSchemeDefault;
            if (d.Emblem is not null && EmblemHash.TryParseHex(d.Emblem, out var h) && !h.IsNone)
            {
                if (hashOut.IsNone || hashOut == h)
                {
                    if (hashOut.IsNone) fallbackOut = fb;
                    hashOut = h;
                    return AppearanceCode.DecalEmblem;
                }
                return fb == AppearanceCode.FallbackSchemeDefault ? AppearanceCode.DecalSchemeDefault : fb;   // a second emblem does not fit: its fallback
            }
            if (d.Badge is { } badge && IndexOf(race.Decals, x => x.Name, badge) is var bi and >= 0) return (byte)bi;
            return AppearanceCode.DecalSchemeDefault;
        }
        left = Pad("leftshoulder", ref fallback, ref hash);
        right = Pad("rightshoulder", ref fallback, ref hash);
        code = code.WithDecals(left, right, fallback);
        if (!hash.IsNone) code = code.WithEmblem(hash);
        return code.Sanitized(LimitsFor(race, catalog));
    }

    /// <summary>
    /// The profile form of a wire code (others' looks; or to edit a code): the inverse of <see cref="ToCode"/>. Indices beyond the catalogue read as the default.
    /// <paramref name="race"/> is the code's race (the code does not carry it).
    /// </summary>
    public static AppearanceProfile FromCode(AppearanceCode code, string race, CustomizationCatalog catalog, string id = "", string name = "")
    {
        var result = AppearanceProfile.Empty(id, race, name);
        if (catalog.Race(race) is not { } rc) return result;
        code = code.Sanitized(LimitsFor(rc, catalog));
        string? set = code.HasPredefinedSet ? PredefinedSetsOf(rc)[code.PredefinedSet].Name : null;
        var pieces = new Dictionary<string, string>(StringComparer.Ordinal);
        var paint = new Dictionary<string, SlotPaint>(StringComparer.Ordinal);
        for (int i = 0; i < AppearanceCode.Slots; i++)
        {
            var slot = (ArmourSlot)i;
            string slotName = SlotName(slot);
            if (code.Piece(slot) != AppearanceCode.DefaultIndex) pieces[slotName] = rc.PiecesFor(slotName)[code.Piece(slot)].Name;
            byte a = code.Palette(slot, 0), b = code.Palette(slot, 1), c = code.Palette(slot, 2);
            if (a == 255 && b == 255 && c == 255) continue;
            string? N(byte k) => k == AppearanceCode.DefaultIndex ? null : catalog.Palette[k].Name;
            paint[slotName] = new SlotPaint(N(a), N(b), N(c), code.PatternOn(slot));
        }
        var decals = new Dictionary<string, DecalChoice>(StringComparer.Ordinal);
        void Pad(string pad, byte value)
        {
            if (value == AppearanceCode.DecalSchemeDefault) return;
            if (value == AppearanceCode.DecalNone) decals[pad] = DecalChoice.NoDecal;
            else if (value == AppearanceCode.DecalEmblem)
                decals[pad] = DecalChoice.ForEmblem(code.Emblem.ToHex(), code.FallbackBadge <= AppearanceCode.MaxBadge ? rc.Decals[code.FallbackBadge].Name : null);
            else decals[pad] = DecalChoice.ForBadge(rc.Decals[value].Name);
        }
        Pad("leftshoulder", code.LeftDecal);
        Pad("rightshoulder", code.RightDecal);
        LensColour? lens = code.HasLens ? new LensColour(code.Lens.R, code.Lens.G, code.Lens.B) : null;
        return result with { PredefinedSet = set, Pieces = pieces, Paint = paint, Decals = decals, Lens = lens };
    }

    /// <summary>
    /// The wire look of the profile's selected custom class for <paramref name="race"/> (its appearance profile of that race), or the default look when
    /// a DEFAULTS loadout is selected or the class names none.
    /// </summary>
    public static AppearanceCode SelectedAppearance(CustomizationProfile profile, string race, CustomizationCatalog catalog) =>
        profile.Appearance(profile.SelectedClass?.Appearance.For(race)) is { } look && look.Race == race ? ToCode(look, catalog) : AppearanceCode.Default;

    /// <summary>
    /// Applies a chapter / warband preset the way the customizer does: its colours (those in the visible palette) and pattern flag are copied into every
    /// armour slot's paint, its shoulder badges (those the race offers) into the pads, <see cref="AppearanceProfile.PresetApplied"/> remembers the preset and a
    /// predefined set is dropped. Pieces and the lens stay.
    /// </summary>
    public static AppearanceProfile ApplyPreset(AppearanceProfile profile, PaintPreset preset, CustomizationCatalog catalog)
    {
        var race = catalog.Race(profile.Race);
        var paint = new Dictionary<string, SlotPaint>(StringComparer.Ordinal);
        var decals = new Dictionary<string, DecalChoice>(profile.Decals, StringComparer.Ordinal);
        string? InPalette(PaintColour colour) => catalog.Palette.Any(p => p.Name.Equals(colour.Name, StringComparison.OrdinalIgnoreCase)) ? colour.Name : null;
        foreach (var slot in CustomizationCatalog.ArmourSlots)
        {
            if (preset.Scheme.For(slot) is not { } set) continue;
            paint[slot] = new SlotPaint(InPalette(set.Primary), InPalette(set.Secondary), InPalette(set.Tertiary), AppearanceResolver.EffectivePatternFlag(set.PatternEnabled));
            if (slot is "leftshoulder" or "rightshoulder")
            {
                if (set.Decal is { } decal && race?.Decals.FirstOrDefault(d => d.Name.Equals(decal, StringComparison.OrdinalIgnoreCase)) is { } badge) decals[slot] = DecalChoice.ForBadge(badge.Name);
                else decals.Remove(slot);
            }
        }
        return profile with { PredefinedSet = null, Paint = paint, Decals = decals, PresetApplied = preset.Name };
    }

    static int IndexOf<T>(IReadOnlyList<T> list, Func<T, string> name, string wanted)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(name(list[i]), wanted, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
