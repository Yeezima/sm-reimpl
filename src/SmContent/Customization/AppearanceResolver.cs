using SmSim;

namespace SmContent.Customization;

/// <summary>A resolved palette colour: diffuse and specular RGB 0..1 as stored (colour space unknown).</summary>
public sealed record ResolvedColour(string Name, float[] Diffuse, float[] Specular)
{
    public static ResolvedColour Of(PaletteEntry c) => new(c.Name, c.Diffuse, c.Specular);
    public static ResolvedColour Of(PaintColour c) => new(c.Name, c.Diffuse, c.Specular);
}

/// <summary>
/// The paint of one armour slot (mask red / green / blue). A null colour is one the scheme does not define (draw the shader's neutral).
/// <see cref="PatternEnabled"/> is the effective flag; when it is false <see cref="Secondary"/> already equals <see cref="Primary"/>.
/// </summary>
public sealed record ResolvedPaint(ResolvedColour? Primary, ResolvedColour? Secondary, ResolvedColour? Tertiary, bool PatternEnabled);

public enum DecalKind { None, Badge, Emblem }

/// <summary>
/// A shoulder pad's decal: nothing, a badge (<see cref="Badge"/>, whose texture is read with <see cref="PaintSchemes.ReadDecalTexture"/>) or the custom
/// emblem <see cref="Emblem"/> (the pad shows <see cref="Badge"/>, the fallback, until the emblem's 16,384 bytes have arrived and verified; null: nothing meanwhile).
/// </summary>
public sealed record ResolvedDecal(DecalKind Kind, PaintDecal? Badge, EmblemHash Emblem)
{
    public static readonly ResolvedDecal None = new(DecalKind.None, null, EmblemHash.None);
}

/// <summary>An armour piece to draw: its slot, wargear name and visual packages (a torso carries its belt). <see cref="Forced"/>: the class forces it.</summary>
/// <remarks>Paint the piece with the paint of <see cref="Slot"/> (the belt too: **interpretation**, the data's torso slot includes the belt). Slot "perk" is a class's forced extra item (the Havoc's Iron Halo icon).</remarks>
public sealed record ResolvedPiece(string Slot, string Name, IReadOnlyList<string> Packages, bool Forced, string HelmetOverride = "");

/// <summary>
/// What a renderer needs to dress a marine. Pieces are in <see cref="CustomizationCatalog.ArmourSlots"/> order (hidden slots left out; forced extras last),
/// <see cref="Paint"/> has an entry for every armour slot the scheme or the look paints.
/// </summary>
/// <param name="PredefinedSet">The fixed full set worn (its scheme gave paint and decals), or null.</param>
/// <param name="HiddenSlots">Slots not drawn: the backpack of the jump-pack classes (the pack is a separate model).</param>
/// <param name="ForcedSlots">Slots the class dictates (the customizer locks them): Raptor head and legs, the Devastator's weapon pack, the Havoc's pack.</param>
/// <param name="HelmetOverride">The head piece's <c>helmet_override</c> ("helmet", "no_helmet", "no_override" or ""), or "" without a head.</param>
/// <param name="Notes">Why something is missing (a predefined set's pieces without packages, unknown pieces).</param>
public sealed record ResolvedAppearance(string Race, string ClassKey, string? PredefinedSet, IReadOnlyList<ResolvedPiece> Pieces,
    IReadOnlyDictionary<string, ResolvedPaint> Paint, ResolvedDecal LeftDecal, ResolvedDecal RightDecal, (byte R, byte G, byte B)? Lens,
    IReadOnlySet<string> HiddenSlots, IReadOnlySet<string> ForcedSlots, string HelmetOverride, IReadOnlyList<string> Notes)
{
    public ResolvedPiece? Piece(string slot) => Pieces.FirstOrDefault(p => p.Slot == slot);
}

/// <summary>Optional inputs of <see cref="AppearanceResolver"/>.</summary>
public sealed record ResolveOptions
{
    /// <summary>
    /// Wargear the player carries (weapon and perk names): a class override that needs wargear (the Devastator's weapon pack needs the chosen heavy weapon,
    /// the Havoc's Iron Halo icon the Iron Halo perk) applies only when it is listed (side prefixes ignored). Null: only overrides without a requirement apply.
    /// </summary>
    public IReadOnlyCollection<string>? Carried { get; init; }

    /// <summary>
    /// Whether a shoulder piece can take a decal (the renderer's <see cref="PaintSchemes.HasDecalUv"/> on the piece's loaded mesh). A pad that cannot gets
    /// <see cref="ResolvedDecal.None"/>. Null: every pad can.
    /// </summary>
    public Func<ResolvedPiece, bool>? PadTakesDecal { get; init; }

    /// <summary>
    /// Finds a piece the catalogue does not list per slot (the pieces of a predefined set; a default set's piece that no customizable set offers) by wargear name.
    /// Without it such a piece is returned with no packages and a note (the catalogue keeps only the customizable sets' pieces).
    /// </summary>
    public Func<string, ArmourPiece?>? PieceLookup { get; init; }
}

/// <summary>
/// Turns a look into what is drawn (docs/customizer-architecture.md sections 3 and 5), from the same <see cref="AppearanceCode"/> the wire carries, so a player's own
/// preview and what others see agree. Rules: a piece index names the slot's offered piece, 255 the class's default set's piece; palette 255 the scheme's colour;
/// pattern off (by the bit, or the scheme's <c>pattern_enabled</c> for an unpainted slot) makes secondary equal primary; a predefined set fills every slot
/// with its scheme (pieces, paint and decals locked, the lens stays); decals 0..252 name a badge, 253 the scheme's, 254 the custom emblem, 255 none (the blank
/// badge is none); class overrides apply last; jump-pack classes (<see cref="ClassCustomization.JumpPack"/>, from the data's base wargear; **interpretation**:
/// the data does not say how pack and backpack share the back) hide the backpack.
/// </summary>
public static class AppearanceResolver
{
    static readonly string[] Pads = ["leftshoulder", "rightshoulder"];

    /// <summary>
    /// The one switch for how a scheme's <c>pattern_enabled</c> flag is read (the owner decided on 2026-10-28 to follow the data: true, the default): a scheme slot with the flag false draws its
    /// secondary (pattern) region in the primary colour, so the Chaos default's left shoulder is red; false, the flag is ignored and the secondary colour always shows
    /// (as the drawing before the customizer did: that pad is gold). Read here and by <see cref="ProfileWire.ApplyPreset"/>; set it once at start-up.
    /// </summary>
    public static bool SchemePatternFlagHidesSecondary { get; set; } = true;

    /// <summary>A scheme slot's effective pattern flag under <see cref="SchemePatternFlagHidesSecondary"/>.</summary>
    public static bool EffectivePatternFlag(bool schemeFlag) => schemeFlag || !SchemePatternFlagHidesSecondary;

    /// <summary>Resolves a stored look for the class <paramref name="classKey"/> (e.g. "sm - tactical"); a look of the other race resolves as the default look.</summary>
    /// <exception cref="ArgumentException">The class is not in the catalogue.</exception>
    public static ResolvedAppearance Resolve(AppearanceProfile look, string classKey, CustomizationCatalog catalog, ResolveOptions? options = null)
    {
        var cls = catalog.Class(classKey) ?? throw new ArgumentException($"Unknown class '{classKey}'.", nameof(classKey));
        return Resolve(look.Race == cls.Race ? ProfileWire.ToCode(look, catalog) : AppearanceCode.Default, classKey, catalog, options);
    }

    /// <summary>Resolves a wire code for the class <paramref name="classKey"/> (its race decides which catalogue the indices refer to).</summary>
    /// <exception cref="ArgumentException">The class is not in the catalogue.</exception>
    public static ResolvedAppearance Resolve(AppearanceCode code, string classKey, CustomizationCatalog catalog, ResolveOptions? options = null)
    {
        var cls = catalog.Class(classKey) ?? throw new ArgumentException($"Unknown class '{classKey}'.", nameof(classKey));
        var race = catalog.Race(cls.Race) ?? throw new ArgumentException($"The catalogue has no {cls.Race} race.", nameof(classKey));
        options ??= new ResolveOptions();
        code = code.Sanitized(ProfileWire.LimitsFor(race, catalog));
        var notes = new List<string>();

        var predefined = code.HasPredefinedSet ? ProfileWire.PredefinedSetsOf(race)[code.PredefinedSet] : null;
        var scheme = (predefined?.Scheme ?? race.DefaultScheme) is { } schemeName ? catalog.Paint.Scheme(schemeName) : null;
        if (scheme is null) notes.Add($"paint scheme '{predefined?.Scheme ?? race.DefaultScheme}' not found; unpainted");

        // Pieces.
        var defaultSet = predefined ?? race.Set(cls.DefaultArmourSet) ?? race.DefaultSet;
        var pieces = new List<ResolvedPiece>();
        var slotPieces = new Dictionary<string, ResolvedPiece>(StringComparer.Ordinal);
        for (int i = 0; i < AppearanceCode.Slots; i++)
        {
            string slot = ProfileWire.SlotName((ArmourSlot)i);
            ArmourPiece? piece = null;
            string? wanted = null;
            if (predefined is null && code.Piece((ArmourSlot)i) != AppearanceCode.DefaultIndex) piece = race.PiecesFor(slot)[code.Piece((ArmourSlot)i)];
            else if (defaultSet is not null && defaultSet.Items.TryGetValue(slot, out wanted))
            {
                piece = race.PiecesFor(slot).FirstOrDefault(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? defaultSet.Pieces.GetValueOrDefault(slot) ?? options.PieceLookup?.Invoke(wanted);
                if (piece is null)
                {
                    notes.Add($"{slot}: piece {wanted} of set {defaultSet.Name} is not in the catalogue's lists; no packages");
                    slotPieces[slot] = new ResolvedPiece(slot, wanted, [], false);
                    continue;
                }
            }
            if (piece is not null) slotPieces[slot] = new ResolvedPiece(slot, piece.Name, piece.Packages, false, piece.HelmetOverride);
        }

        // Class overrides: forced pieces.
        var forced = new HashSet<string>(StringComparer.Ordinal);
        var extras = new List<ResolvedPiece>();
        foreach (var ov in cls.Overrides)
        {
            if (ov.RequiredWargear is { } need && !Carries(options.Carried, need)) continue;
            var piece = new ResolvedPiece(ov.Piece.Slot, ov.Piece.Name, ov.Piece.Packages, true, ov.Piece.HelmetOverride);
            if (CustomizationCatalog.ArmourSlots.Contains(ov.Piece.Slot)) { slotPieces[ov.Piece.Slot] = piece; forced.Add(ov.Piece.Slot); }
            else extras.Add(piece);
        }

        // Jump-pack classes wear the pack instead of a backpack piece.
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (cls.JumpPack) hidden.Add("backpack");
        foreach (var slot in CustomizationCatalog.ArmourSlots)
            if (slotPieces.TryGetValue(slot, out var p) && !hidden.Contains(slot)) pieces.Add(p);
        pieces.AddRange(extras);

        // Paint.
        var paint = new Dictionary<string, ResolvedPaint>(StringComparer.Ordinal);
        for (int i = 0; i < AppearanceCode.Slots; i++)
        {
            var slot = (ArmourSlot)i;
            string slotName = ProfileWire.SlotName(slot);
            var set = scheme?.For(slotName);
            ResolvedColour? Channel(int channel, PaintColour? fromScheme)
            {
                byte index = predefined is null ? code.Palette(slot, channel) : AppearanceCode.DefaultIndex;
                return index != AppearanceCode.DefaultIndex ? ResolvedColour.Of(catalog.Palette[index]) : fromScheme is null ? null : ResolvedColour.Of(fromScheme);
            }
            var primary = Channel(0, set?.Primary);
            var secondary = Channel(1, set?.Secondary);
            var tertiary = Channel(2, set?.Tertiary);
            if (primary is null && secondary is null && tertiary is null) continue;
            bool customized = predefined is null && Enumerable.Range(0, 3).Any(c => code.Palette(slot, c) != AppearanceCode.DefaultIndex);
            bool pattern = customized ? code.PatternOn(slot) : set is not null && EffectivePatternFlag(set.PatternEnabled);
            paint[slotName] = new ResolvedPaint(primary, pattern ? secondary : primary, tertiary, pattern);
        }

        // Decals.
        ResolvedDecal Decal(string pad, byte value)
        {
            var piece = slotPieces.GetValueOrDefault(pad);
            if (piece is null || (options.PadTakesDecal is { } takes && !takes(piece))) return ResolvedDecal.None;
            ResolvedDecal Badge(PaintDecal? d) => d is null || d.IsBlank ? ResolvedDecal.None : new ResolvedDecal(DecalKind.Badge, d, EmblemHash.None);
            PaintDecal? SchemeDecal() => scheme?.For(pad)?.Decal is { } name ? catalog.Paint.Decal(name) : null;
            if (predefined is not null || value == AppearanceCode.DecalSchemeDefault) return Badge(SchemeDecal());
            if (value == AppearanceCode.DecalNone) return ResolvedDecal.None;
            if (value == AppearanceCode.DecalEmblem)
            {
                var fallback = code.FallbackBadge <= AppearanceCode.MaxBadge ? race.Decals[code.FallbackBadge].Decal : SchemeDecal();
                return new ResolvedDecal(DecalKind.Emblem, fallback is { IsBlank: false } ? fallback : null, code.Emblem);
            }
            return Badge(race.Decals[value].Decal);
        }
        var left = Decal("leftshoulder", code.LeftDecal);
        var right = Decal("rightshoulder", code.RightDecal);

        string helmet = slotPieces.TryGetValue("head", out var head) ? head.HelmetOverride : "";
        (byte, byte, byte)? lens = code.HasLens ? code.Lens : null;
        return new ResolvedAppearance(cls.Race, classKey, predefined?.Name, pieces, paint, left, right, lens, hidden, forced, helmet, notes);
    }

    static bool Carries(IReadOnlyCollection<string>? carried, string need) =>
        carried is not null && carried.Any(c => ProfileWire.Neutral(c).Equals(ProfileWire.Neutral(need), StringComparison.OrdinalIgnoreCase));
}
