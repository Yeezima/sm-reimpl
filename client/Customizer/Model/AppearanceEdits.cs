using System;
using System.Collections.Generic;
using System.Linq;
using SmContent;
using SmContent.Customization;
using SmSim;

namespace SmClient.Customizer.Model;

/// <summary>Which of an armour slot's three colours (the paint mask's red / green / blue).</summary>
public enum PaintChannel { Primary = 0, Pattern = 1, Detail = 2 }

/// <summary>
/// Pure edits of an <see cref="AppearanceProfile"/> (docs/customizer-design.md F6) plus the lookups the appearance screens need. Colours are palette names,
/// pieces wargear names, pads "leftshoulder" / "rightshoulder". A predefined set locks pieces, paint and decals: the editing calls refuse until
/// <see cref="DropSet"/>. Engine-free.
/// </summary>
public static class AppearanceEdits
{
    public static readonly IReadOnlyList<string> Slots = CustomizationCatalog.ArmourSlots;
    public static readonly string[] Pads = ["leftshoulder", "rightshoulder"];

    /// <summary>The names shown for the nine slots (the design's part names).</summary>
    public static string SlotTitle(string slot) => slot switch
    {
        "head" => "Helmet", "torso" => "Torso", "leftshoulder" => "Left shoulder", "rightshoulder" => "Right shoulder",
        "leftarm" => "Left arm", "rightarm" => "Right arm", "leftleg" => "Left leg", "rightleg" => "Right leg", "backpack" => "Backpack", _ => slot,
    };

    /// <summary>The wire index of a slot name.</summary>
    public static ArmourSlot SlotOf(string slot) => ProfileWire.SlotOf(slot) ?? throw new ArgumentException($"Unknown armour slot '{slot}'.", nameof(slot));

    // ---- resolving the current look ----

    /// <summary>The class key (e.g. "sm - tactical") of a role on a race, or null.</summary>
    public static string? ClassKey(CustomizationCatalog catalog, string role, string race) => LoadoutRows.Options(catalog, role, race)?.Key;

    /// <summary>
    /// What the look draws for the class of <paramref name="role"/>: the resolver on the wire code (so the preview and what others see agree).
    /// <paramref name="custom"/> supplies the carried wargear (the Devastator's weapon pack needs the heavy weapon).
    /// </summary>
    public static ResolvedAppearance? Resolve(CustomizationCatalog catalog, AppearanceProfile look, string role, CustomClass? custom, Func<ResolvedPiece, bool>? padTakesDecal = null)
    {
        if (ClassKey(catalog, role, look.Race) is not { } key) return null;
        var options = LoadoutRows.Options(catalog, role, look.Race)!;
        var carried = LoadoutRows.CarriedWeapons(catalog, options, custom).Concat(new[] { custom?.Perk(0) ?? options.DefaultPerk?.Name, custom?.Perk(1) }.OfType<string>()).ToList();
        return AppearanceResolver.Resolve(look, key, catalog, new ResolveOptions { Carried = carried, PadTakesDecal = padTakesDecal });
    }

    // ---- editing ----

    public static bool Locked(AppearanceProfile p) => p.PredefinedSet is not null;

    /// <summary>Wears the offered piece <paramref name="piece"/> (wargear name) in a slot; null is the class's default set's piece.</summary>
    public static AppearanceProfile SetPiece(AppearanceProfile p, string slot, string? piece)
    {
        if (Locked(p)) return p;
        var pieces = new Dictionary<string, string>(p.Pieces, StringComparer.Ordinal);
        if (piece is null) pieces.Remove(slot); else pieces[slot] = piece;
        return p with { Pieces = pieces };
    }

    static SlotPaint Current(AppearanceProfile p, ResolvedAppearance? resolved, string slot) =>
        p.Paint.GetValueOrDefault(slot) ?? new SlotPaint(null, null, null, resolved?.Paint.GetValueOrDefault(slot)?.PatternEnabled ?? false);

    static AppearanceProfile WithPaint(AppearanceProfile p, string slot, SlotPaint paint)
    {
        var all = new Dictionary<string, SlotPaint>(p.Paint, StringComparer.Ordinal);
        if (paint.IsEmpty) all.Remove(slot); else all[slot] = paint;
        return p with { Paint = all, PresetApplied = null };
    }

    /// <summary>
    /// Sets one channel of the given slots to a palette colour (null: back to the scheme's colour). Choosing a pattern colour turns the pattern on for that slot.
    /// <paramref name="resolved"/> (the look before the edit) gives the slot's current pattern flag when it has no colours of its own yet.
    /// </summary>
    public static AppearanceProfile SetColour(AppearanceProfile p, IEnumerable<string> slots, PaintChannel channel, string? colour, ResolvedAppearance? resolved)
    {
        if (Locked(p)) return p;
        foreach (var slot in slots)
        {
            var c = Current(p, resolved, slot);
            c = channel switch
            {
                PaintChannel.Primary => c with { Primary = colour },
                PaintChannel.Pattern => c with { Pattern = colour, PatternOn = colour is not null || c.PatternOn },
                _ => c with { Detail = colour },
            };
            p = WithPaint(p, slot, c);
        }
        return p;
    }

    /// <summary>
    /// Pattern ON / OFF for the given slots (OFF: the pattern shows in the primary colour). A slot with no colours of its own takes the palette-named colours of
    /// the look as drawn (<paramref name="resolved"/>) first, so the flag has something to hold on to; if none maps to the palette the slot is left alone.
    /// </summary>
    public static AppearanceProfile SetPattern(AppearanceProfile p, IEnumerable<string> slots, bool on, ResolvedAppearance? resolved, CustomizationCatalog catalog)
    {
        if (Locked(p)) return p;
        foreach (var slot in slots)
        {
            var c = Current(p, resolved, slot);
            if (c.IsEmpty && resolved?.Paint.GetValueOrDefault(slot) is { } drawn)
            {
                string? Named(ResolvedColour? colour) => colour is not null && catalog.Palette.Any(x => x.Name.Equals(colour.Name, StringComparison.OrdinalIgnoreCase)) ? colour.Name : null;
                // The pattern colour of an "off" slot equals the primary; the original scheme's pattern colour is not recoverable, so it stays the scheme's.
                c = new SlotPaint(Named(drawn.Primary), drawn.PatternEnabled ? Named(drawn.Secondary) : null, Named(drawn.Tertiary), c.PatternOn);
            }
            if (c.IsEmpty) continue;
            p = WithPaint(p, slot, c with { PatternOn = on });
        }
        return p;
    }

    /// <summary>Applies a chapter / warband preset (colours, pattern flags and shoulder badges of the scheme).</summary>
    public static AppearanceProfile SetPreset(AppearanceProfile p, PaintPreset preset, CustomizationCatalog catalog) =>
        Locked(p) ? p : ProfileWire.ApplyPreset(p, preset, catalog);

    /// <summary>Wears a whole predefined set (<see cref="AppearanceProfile.PredefinedSet"/>): pieces, paint and badges locked.</summary>
    public static AppearanceProfile SetSet(AppearanceProfile p, string setName) => p with { PredefinedSet = setName, PresetApplied = null };

    /// <summary>Leaves the predefined set: the default look, editable again.</summary>
    public static AppearanceProfile DropSet(AppearanceProfile p) => p with { PredefinedSet = null };

    /// <summary>Sets a pad's decal (null: the scheme's own).</summary>
    public static AppearanceProfile SetDecal(AppearanceProfile p, string pad, DecalChoice? decal)
    {
        if (Locked(p)) return p;
        var decals = new Dictionary<string, DecalChoice>(p.Decals, StringComparer.Ordinal);
        if (decal is null) decals.Remove(pad); else decals[pad] = decal;
        return p with { Decals = decals };
    }

    /// <summary>
    /// Puts a custom emblem on the given pads. A look carries one emblem on the wire, so a pad outside <paramref name="pads"/> that shows a different custom emblem
    /// gets this one too (<c>ReplacedOther</c> says so, for the status line); a pad with a badge or nothing is left alone. Each pad keeps its current badge as the
    /// fallback shown until the emblem arrives.
    /// </summary>
    public static (AppearanceProfile Look, bool ReplacedOther) ApplyEmblem(AppearanceProfile p, IEnumerable<string> pads, string hex)
    {
        if (Locked(p)) return (p, false);
        var targets = pads.ToHashSet(StringComparer.Ordinal);
        var decals = new Dictionary<string, DecalChoice>(p.Decals, StringComparer.Ordinal);
        bool replaced = false;
        foreach (var pad in Pads)
        {
            decals.TryGetValue(pad, out var current);
            if (targets.Contains(pad)) decals[pad] = DecalChoice.ForEmblem(hex, current?.Badge ?? current?.Fallback);
            else if (current?.Emblem is { } other && !other.Equals(hex, StringComparison.OrdinalIgnoreCase))
            {
                decals[pad] = DecalChoice.ForEmblem(hex, current.Fallback ?? current.Badge);
                replaced = true;
            }
        }
        return (p with { Decals = decals }, replaced);
    }

    public static AppearanceProfile SetLens(AppearanceProfile p, LensColour? lens) => p with { Lens = lens };

    /// <summary>Back to the race's default look: no pieces, paint, badges, set or lens.</summary>
    public static AppearanceProfile ResetAll(AppearanceProfile p) => AppearanceProfile.Empty(p.Id, p.Race, p.Name);

    public static AppearanceProfile ResetPieces(AppearanceProfile p) => p with { Pieces = new Dictionary<string, string>() };
    public static AppearanceProfile ResetPaint(AppearanceProfile p) => p with { Paint = new Dictionary<string, SlotPaint>(), PresetApplied = null };
    public static AppearanceProfile ResetDecals(AppearanceProfile p) => p with { Decals = new Dictionary<string, DecalChoice>() };

    // ---- lookups for the screens ----

    /// <summary>A piece to choose in a slot.</summary>
    public sealed record PieceRow(string? Id, string Title, string Description, bool Selected);

    /// <summary>The slot's offered pieces, "Default" first; <c>Selected</c> marks the worn one.</summary>
    public static IReadOnlyList<PieceRow> PieceRows(CustomizationCatalog catalog, AppearanceProfile p, string slot, ResolvedAppearance? resolved)
    {
        var race = catalog.Race(p.Race);
        var rows = new List<PieceRow>();
        string? worn = p.Pieces.GetValueOrDefault(slot);
        string defaultName = resolved?.Piece(slot)?.Name ?? "";
        var offered = race?.PiecesFor(slot) ?? [];
        string DefaultTitle() => offered.FirstOrDefault(x => x.Name.Equals(defaultName, StringComparison.OrdinalIgnoreCase))?.DisplayName is { } t ? $"Default ({t})" : "Default";
        rows.Add(new PieceRow(null, DefaultTitle(), "The class's own armour piece.", worn is null));
        foreach (var piece in offered)
            rows.Add(new PieceRow(piece.Name, piece.DisplayName, piece.Description, worn is not null && worn.Equals(piece.Name, StringComparison.OrdinalIgnoreCase)));
        return rows;
    }

    /// <summary>The colour a channel of a slot is drawn with (a palette name when the look set it, else the scheme's colour name); null when none.</summary>
    public static ResolvedColour? ColourOf(ResolvedAppearance? resolved, string slot, PaintChannel channel)
    {
        var paint = resolved?.Paint.GetValueOrDefault(slot);
        return paint is null ? null : channel switch { PaintChannel.Primary => paint.Primary, PaintChannel.Pattern => paint.Secondary, _ => paint.Tertiary };
    }

    /// <summary>Whether a slot's colour channel is set by the look (not the scheme).</summary>
    public static bool IsCustomColour(AppearanceProfile p, string slot, PaintChannel channel) =>
        p.Paint.GetValueOrDefault(slot) is { } c && (channel switch { PaintChannel.Primary => c.Primary, PaintChannel.Pattern => c.Pattern, _ => c.Detail }) is not null;

    /// <summary>Whether the pattern is on for a slot as drawn.</summary>
    public static bool PatternOn(ResolvedAppearance? resolved, string slot) => resolved?.Paint.GetValueOrDefault(slot)?.PatternEnabled ?? false;

    /// <summary>The badge families of a race in order, each with its variants (white, black, ...); the blank badge is left out (it is "None").</summary>
    public static IReadOnlyList<(string Family, IReadOnlyList<ShoulderDecal> Variants)> BadgeFamilies(CustomizationCatalog catalog, string race) =>
        (catalog.Race(race)?.Decals ?? []).Where(d => !d.IsBlank).GroupBy(d => d.Family, StringComparer.Ordinal).Select(g => (g.Key, (IReadOnlyList<ShoulderDecal>)g.ToList())).ToList();

    /// <summary>The wire index of a palette colour (for tests and tooltips); -1 when absent.</summary>
    public static int PaletteIndex(CustomizationCatalog catalog, string name)
    {
        for (int i = 0; i < catalog.Palette.Count; i++) if (catalog.Palette[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The slot set an edit targets: all nine ("All parts") or the one.</summary>
    public static IReadOnlyList<string> Targets(string? slotOrAll) => slotOrAll is null ? Slots : [slotOrAll];

    // ---- appearance library ----

    /// <summary>Adds an empty appearance for a race (at most <see cref="CustomizationProfile.MaxAppearancesPerRace"/>); returns its id or null when full.</summary>
    public static (CustomizationProfile Profile, string? Id) AddAppearance(CustomizationProfile profile, string race)
    {
        if (profile.AppearancesFor(race).Count() >= CustomizationProfile.MaxAppearancesPerRace) return (profile, null);
        string id = profile.NewAppearanceId(race);
        int n = 1;
        while (profile.AppearancesFor(race).Any(a => a.Name.Equals($"Appearance {n}", StringComparison.OrdinalIgnoreCase))) n++;   // never two looks of one name
        var look = AppearanceProfile.Empty(id, race, $"Appearance {n}");
        return (profile with { Appearances = profile.Appearances.Append(look).ToList() }, id);
    }

    public static CustomizationProfile ReplaceAppearance(CustomizationProfile profile, AppearanceProfile look) =>
        profile with { Appearances = profile.Appearances.Select(a => a.Id == look.Id ? look : a).ToList() };

    /// <summary>Removes an appearance; classes that wore it fall back to the race's default look.</summary>
    public static CustomizationProfile RemoveAppearance(CustomizationProfile profile, string id) =>
        profile with
        {
            Appearances = profile.Appearances.Where(a => a.Id != id).ToList(),
            Classes = profile.Classes.Select(c => c with
            {
                Appearance = new AppearanceRefs(c.Appearance.SpaceMarine == id ? null : c.Appearance.SpaceMarine, c.Appearance.Chaos == id ? null : c.Appearance.Chaos),
            }).ToList(),
        };

    /// <summary>Makes a custom class wear an appearance of a race (null: the default look).</summary>
    public static CustomizationProfile Assign(CustomizationProfile profile, int slot, string race, string? id) =>
        profile.ClassAt(slot) is { } c ? profile with { Classes = profile.Classes.Select(x => x.Slot == slot ? c with { Appearance = c.Appearance.With(race, id) } : x).ToList() } : profile;

    /// <summary>Gives the class an appearance to edit: its own for the race, else a new one assigned to it (null when the race's library is full).</summary>
    public static (CustomizationProfile Profile, AppearanceProfile? Look) EnsureLook(CustomizationProfile profile, int slot, string race)
    {
        if (profile.ClassAt(slot) is not { } c) return (profile, null);
        if (profile.Appearance(c.Appearance.For(race)) is { } existing && existing.Race == race) return (profile, existing);
        var (added, id) = AddAppearance(profile, race);
        if (id is null) return (profile, null);
        added = Assign(added, slot, race, id);
        return (added, added.Appearance(id));
    }

    public static AppearanceProfile Rename(AppearanceProfile look, string? input) =>
        look with { Name = LoadoutEdits.SanitizeName(input, 0) is var n && n.StartsWith("Custom 0", StringComparison.Ordinal) ? look.Name : n };
}
