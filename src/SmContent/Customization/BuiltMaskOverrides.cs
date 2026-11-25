using SmFormats.Dds;

namespace SmContent.Customization;

/// <summary>The paint region an override puts texels in: one of the mask's three channels, or none (the texel keeps its baked look).</summary>
public enum MaskRegion { Primary = 0, Secondary = 1, Tertiary = 2, Baked = 3, SwapPrimarySecondary = 4 }

/// <summary>
/// Which texels an override region takes, by their baked colour (sRGB luminance 0..255, CIELAB hue in radians and chroma); every bound is optional.
/// </summary>
public sealed record TexelFilter(float? MinLum = null, float? MaxLum = null, float? MinHue = null, float? MaxHue = null, float? MinChroma = null, float? MaxChroma = null)
{
    public static readonly TexelFilter Any = new();

    /// <remarks>A hue band with both bounds wraps through pi when <see cref="MinHue"/> is above <see cref="MaxHue"/>; with one bound only (round 4, finding 5: it
    /// was ignored) it is a plain lower or upper bound on the hue (-pi..pi).</remarks>
    public bool Matches(float lum, float hue, float chroma) =>
        (MinLum is not { } a || lum >= a) && (MaxLum is not { } b || lum <= b) && (MinChroma is not { } c || chroma >= c) && (MaxChroma is not { } d || chroma <= d)
        && (MinHue, MaxHue) switch
        {
            ({ } h0, { } h1) => h0 <= h1 ? hue >= h0 && hue <= h1 : hue >= h0 || hue <= h1,
            ({ } h0, null) => hue >= h0,
            (null, { } h1) => hue <= h1,
            _ => true,
        };
}

/// <summary>
/// One hand-placed region of a built mask (armour-pieces-1125 round 3, owner: "3rd round, no stopgap": per-piece region data where the rules cannot reach the 92 %
/// gate). A rectangle (or the ellipse inside it) in texture space, 0..1 with V down as the textures are stored; the texels in it that pass <see cref="Filter"/>
/// are put wholly in <see cref="To"/>. Ours; data only (no original bytes): the regions are applied at load time to the install's own textures.
/// </summary>
/// <remarks><see cref="From"/> (round 3, second worker) narrows it to the texels the mask so far puts mostly in that region (<see cref="MaskRegion.Baked"/>: texels
/// painted under half, the baked holes and blotches a plate kept). <see cref="Outside"/> with <see cref="Ellipse"/>: the texels of the rectangle outside the ellipse.</remarks>
public sealed record MaskRegionOverride(float U0, float V0, float U1, float V1, MaskRegion To, TexelFilter? Filter = null, bool Ellipse = false, MaskRegion? From = null, bool Outside = false);

/// <summary>Per-piece data for a built mask: an optional channel map (instead of <see cref="BakedArmourPaint.ChannelMap"/>) and regions applied in order.</summary>
/// <param name="Material">The material it applies to, or null for every built material of the package.</param>
/// <param name="Channels">Where the builder's primary, secondary and tertiary regions go (paint channel per region), or null for the rule's map.</param>
/// <param name="Brightness">Scale on the piece's luminance targets (<see cref="BakedArmourPaint.LuminanceTargets"/>), for a piece whose baked texture reads lighter or
/// darker than its neighbours after the per-slot targets (round 3, second worker); 1 = the slot's targets.</param>
/// <param name="Contrast">Round 4: scale on the shading's distance from each channel's target (1 = as shaded; 0.5 halves it), for a piece whose baked wear (light
/// scratches and rubbed paint on a black plate) reads as blotches under the paint where the regular armour's grey diffuse is even.</param>
public sealed record BuiltMaskOverride(string Package, string? Material, int[]? Channels, IReadOnlyList<MaskRegionOverride> Regions, string Why, float Brightness = 1f, float Contrast = 1f);

/// <summary>
/// The per-piece table (armour-pieces-1125 round 3). Every entry names its package and why (the gate's defect it answers); see docs/changes-from-original.md.
/// Regions are applied after the channel remap, so <see cref="MaskRegion"/> names the final region. Kept small: rules first (<see cref="PaintMaskBuilder"/>).
/// </summary>
public static class BuiltMaskOverrides
{
    /// <summary>
    /// The Space Marine pads drawn on the standard Mk 7 pad layout (the dome in the top 31 % of the texture, the rim pieces below it). The regular Mk 7 pad paints its
    /// dome primary and its rim secondary (round 3 test_rgb still: red dome, green rim, blue aquila); the rules cannot tell a rim from a dome by colour, so these pads'
    /// rims are put in the secondary region by data. Seen on the gate's sheets: Blood Ravens DLC and campaign pads (rim baked black), Blood Angels Vet right pad
    /// (rim baked dark), Ultramarines campaign (rim baked olive), Dark Angels campaign (rim primary), Corvus.
    /// </summary>
    static readonly string[] Mk7PadLayout =
    [
        "sm_armour_basic_001_shldrl", "sm_armour_basic_001_shldrr", "sm_armour_blood_ravens_shldrl", "sm_armour_blood_ravens_shldrr", "sm_armour_corvus_shldrr",
        "sm_armour_dark_angels_shldrl", "sm_armour_dark_angels_shldrr", "sm_armour_mp_bloodravens_shldrl", "sm_armour_mp_bloodravens_shldrr",
        "sm_armour_mp_dlc1_set1_shldrl", "sm_armour_mp_dlc1_set1_shldrr", "sm_armour_mp_dlc2_set1_shldrl", "sm_armour_mp_dlc2_set1_shldrr",
        "sm_armour_mp_dlc2_set2_shldrl", "sm_armour_mp_dlc2_set2_shldrr", "sm_armour_mp_dlc3_set1_shldrl", "sm_armour_mp_dlc3_set1_shldrr",
    ];

    /// <summary>
    /// Round 3, second worker: the Black Templars (DLC and campaign) and Leandros pads use another layout: two rim arcs above the dome and a deeper dome reaching
    /// V 0.385 (the Mk 7 cut at 0.31 painted the dome's lower part as rim). Their dome is the ellipse centred at (0.5, 0.385), radii 0.48 x 0.285; outside it, and
    /// below V 0.385, the rim pieces are secondary.
    /// </summary>
    static readonly string[] ArcPadLayout =
    [
        "sm_armour_black_temp_shldrl", "sm_armour_black_temp_shldrr", "sm_armour_blacktemp_shldrl", "sm_armour_blacktemp_shldrr", "sm_armour_leandros_shldrl",
        "sm_armour_leandros_shldrr",
    ];

    static MaskRegionOverride ArcDome(MaskRegion to, TexelFilter? f = null, MaskRegion? from = null) => new(0.02f, 0.10f, 0.98f, 0.67f, to, f, Ellipse: true, From: from);

    public static readonly IReadOnlyList<BuiltMaskOverride> All =
    [
        .. Mk7PadLayout.Select(p => new BuiltMaskOverride(p, null, null,
            [new MaskRegionOverride(0f, 0.31f, 1f, 1f, MaskRegion.Secondary)], "Mk 7 pad layout: the rim pieces are secondary, as on the regular Mk 7 pad")),
        .. ArcPadLayout.Select(p => new BuiltMaskOverride(p, null, null,
        [
            new MaskRegionOverride(0f, 0f, 1f, 0.10f, MaskRegion.Secondary),
            new MaskRegionOverride(0.02f, 0.10f, 0.98f, 0.67f, MaskRegion.Secondary, Ellipse: true, Outside: true),
            new MaskRegionOverride(0f, 0f, 0.02f, 1f, MaskRegion.Secondary),
            new MaskRegionOverride(0.98f, 0f, 1f, 1f, MaskRegion.Secondary),
            new MaskRegionOverride(0f, 0.385f, 1f, 1f, MaskRegion.Secondary),
        ], "Arc pad layout: the rim arcs and pieces are secondary, as on the regular Mk 7 pad")),
        // The black rims are the largest glossy colour, so the red dome field took the secondary region (gate: "left pad field lands in SECONDARY"); in the dome the
        // field is the plate (primary), as on the regular pad.
        new("sm_armour_mp_dlc2_set1_shldrl", null, null, [new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.SwapPrimarySecondary)],
            "Blood Angels Vet left pad: the dome field is primary"),

        // ---- Round 3, second worker (gate 5607e9e5 defects; coordinates read on built-mask-bench --grid previews, colours probed on the install's textures) ----

        // Salamanders Veteran (dlc2_set2). The dark gold trims (L* about 10 to 20, chroma 7 to 17) are too dark for the warm-ornament rule and stayed baked; the
        // green plate's shadowed and stained texels kept their baked green under the paint ("leg green stains").
        .. Each(["sm_armour_mp_dlc2_set2_torso", "sm_armour_mp_dlc2_set2_legl", "sm_armour_mp_dlc2_set2_legr", "sm_armour_mp_dlc2_set2_arml", "sm_armour_mp_dlc2_set2_armr",
                 "sm_armour_mp_dlc2_set2_helmet", "sm_armour_mp_dlc2_set2_bp"],
            [Whole(MaskRegion.Tertiary, DarkGold), Whole(MaskRegion.Primary, Hue(2.0f, 3.1f, 6f))],
            "Salamanders: dark gold trim is tertiary; baked green under the paint is plate"),
        new("sm_armour_mp_dlc2_set2_torso", null, null,
        [
            // The chest medallion: silver dragon and rim tertiary, its black field secondary (it was all baked: "dark collar" and blotch on the chest).
            new MaskRegionOverride(0.215f, 0.165f, 0.455f, 0.425f, MaskRegion.Secondary, Ellipse: true),
            new MaskRegionOverride(0.215f, 0.165f, 0.455f, 0.425f, MaskRegion.Tertiary, new TexelFilter(MinLum: 60f), Ellipse: true),
            new MaskRegionOverride(0.215f, 0.165f, 0.455f, 0.425f, MaskRegion.Tertiary, DarkGold, Ellipse: true),
            // The winged aquila and the gorget: wholly tertiary (dark gold, partly read as plate).
            new MaskRegionOverride(0.55f, 0.36f, 0.88f, 0.58f, MaskRegion.Tertiary, new TexelFilter(MinLum: 12f, MinHue: 0.9f, MaxHue: 1.95f, MinChroma: 4f)),
            new MaskRegionOverride(0.60f, 0.29f, 0.76f, 0.39f, MaskRegion.Tertiary, new TexelFilter(MinLum: 10f, MinHue: 0.9f, MaxHue: 1.95f, MinChroma: 3f)),
        ], "Salamanders torso: chest medallion and aquila"),
        new("sm_armour_mp_dlc2_set2_shldrl", null, null,
        [
            // The dragon skull on the dome is tertiary (light texels); the dark cracks and shadows round it are the dome's plate (they stayed baked: "blotch round the
            // emblem", "dark streaks").
            new MaskRegionOverride(0.24f, 0.08f, 0.74f, 0.25f, MaskRegion.Tertiary, new TexelFilter(MinLum: 55f), Ellipse: true),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary, From: MaskRegion.Baked),
        ], "Salamanders left pad: emblem tertiary, dome holes plate"),

        // ---- Round 4 (re-gate 2ec46b20 per-piece defects; coordinates read on built-mask-bench --grid previews) ----

        // Salamanders Veteran (dlc2_set2) helmet: its bright and its shadowed green are two clusters, so most of the helmet was secondary and drew another colour
        // than the plate ("helmet mostly secondary"); both greens are the plate (each keeps its own brightness, PaintMaskBuilder.WithRegionStats).
        new("sm_armour_mp_dlc2_set2_helmet", null, [0, 0, 2], [new MaskRegionOverride(0.6f, 0.55f, 1f, 1f, MaskRegion.Primary, new TexelFilter(MinLum: 4f), From: MaskRegion.Baked)],
            "Salamanders helmet: both greens are the plate (primary); the dark baked patches of the back plate too"),
        // Salamanders right pad: the dome's dark baked patches are plate, as on the left pad.
        new("sm_armour_mp_dlc2_set2_shldrr", null, null, [new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary, From: MaskRegion.Baked)],
            "Salamanders right pad: dome holes plate"),
        // Salamanders backpack: the gold dragon emblem was split between plate and ornament ("emblem mottled"): its gold is wholly tertiary.
        new("sm_armour_mp_dlc2_set2_bp", null, null,
            [new MaskRegionOverride(0.03f, 0.45f, 0.31f, 0.71f, MaskRegion.Tertiary, new TexelFilter(MinLum: 20f, MinHue: 0.9f, MaxHue: 1.95f, MinChroma: 10f))],
            "Salamanders backpack: gold emblem tertiary"),
        // Blood Angels Veteran (dlc2_set1) helmet: a gilded helmet, so the warm-ornament rule made it wholly tertiary ("helmet never takes the plate colour"); its gold
        // is the plate (primary) and the few red texels the builder took as plate go to tertiary.
        new("sm_armour_mp_dlc2_set1_helmet", null, [2, 1, 0], [], "Blood Angels Vet helmet: the gold is the plate (primary)"),
        // Campaign Chaos torsos (basic, berserker, raptor; havoc on its own layout): the ribbed armour band round the back of the waist stayed baked ("back waist grey
        // unpainted"); it is plate, as the regular Chaos torso's waist (test_rgb: painted).
        .. Each(["csm_armour_basic_torso", "csm_armour_berserker_torso", "csm_armour_raptor_torso"],
            [new MaskRegionOverride(0f, 0.89f, 0.84f, 1f, MaskRegion.Primary, From: MaskRegion.Baked)], "Campaign Chaos torso: the back waist band is plate"),
        new("csm_armour_havoc_torso", null, null, [new MaskRegionOverride(0.42f, 0.61f, 1f, 0.71f, MaskRegion.Primary, From: MaskRegion.Baked)],
            "Campaign havoc torso: the back waist band is plate"),

        // Round 4, second pass (lead 41's list after the first round-4 capture).
        // Black Templars legs and backpack (DLC sm_armour_blacktemp, campaign sm_armour_black_temp: the same textures): the black plates' light wear drew as
        // blotches under the paint ("leg wear blotches"); their shading keeps half its distance from the target.
        .. Each(["sm_armour_blacktemp_legl", "sm_armour_blacktemp_legr", "sm_armour_blacktemp_backpack", "sm_armour_black_temp_legl", "sm_armour_black_temp_legr",
                 "sm_armour_black_temp_backpack"], [], "Black Templars legs and backpack: wear blotches softened", contrast: 0.5f),
        // Salamanders left pad: the dragon's dark shadow outline shaded as a dark halo on the dome; the dome's shading keeps 60 % of its distance from the target.
        new("sm_armour_mp_dlc2_set2_shldrl", null, null, [], "Salamanders left pad: softer dome shading (dragon halo)", Contrast: 0.6f),
        // Sidonus left pad: the white omega on the dome was ragged (part plate): white texels in its ellipse are tertiary, the blue field round it primary.
        new("sm_armour_sidonus_shldrl", null, null,
        [
            new MaskRegionOverride(0.37f, 0.07f, 0.59f, 0.24f, MaskRegion.Primary, Hue(-2.3f, -1.0f, 6f, 6f), Ellipse: true),
            new MaskRegionOverride(0.37f, 0.07f, 0.59f, 0.24f, MaskRegion.Tertiary, new TexelFilter(MinLum: 70f, MaxChroma: 22f), Ellipse: true),
        ], "Sidonus left pad: omega tertiary, the field round it primary"),
        // Blood Ravens DLC torso: the silver chest aquila was plate ("chest aquila primary"); it is tertiary (its red drop stays plate).
        new("sm_armour_mp_bloodravens_torso", null, null,
            [new MaskRegionOverride(0.58f, 0.35f, 0.85f, 0.52f, MaskRegion.Tertiary, new TexelFilter(MinLum: 15f, MaxChroma: 16f))],
            "Blood Ravens torso: silver chest aquila tertiary"),
        // Alpha Legion Champion left pad (chaos dlc1_set1): the dome's blue field was split between plate and ornament, leaving baked blue blotches; the blue field
        // and the dome's baked holes are plate (primary), the green serpents ornament (tertiary).
        new("csm_armour_mp_dlc1_set1_shldrl", null, null,
        [
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary, AlBlueTeal),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Tertiary, Hue(1.6f, 2.6f, 10f)),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary, new TexelFilter(MinLum: 4f), From: MaskRegion.Baked),
        ], "Alpha Legion left pad: blue field primary, serpents tertiary"),
        // Iron Warriors pads: the frame ring was half primary, half tertiary (a mixed colour: "trims half-painted") and the field flat; the ring is tertiary like the
        // regular Chaos trims, the dark field primary, the silver emblem (left) and the gold star (right) tertiary.
        new("csm_armour_mp_ironwarr_shldrl", null, null,
        [
            new MaskRegionOverride(0f, 0.01f, 0.91f, 0.85f, MaskRegion.Tertiary, Ellipse: true),
            new MaskRegionOverride(0.23f, 0.16f, 0.73f, 0.73f, MaskRegion.Primary, Ellipse: true),
            new MaskRegionOverride(0.23f, 0.16f, 0.73f, 0.73f, MaskRegion.Tertiary, new TexelFilter(MinLum: 80f, MaxChroma: 20f), Ellipse: true),
        ], "Iron Warriors left pad: frame tertiary, field primary, emblem tertiary"),
        new("csm_armour_mp_ironwarr_shldrr", null, null,
        [
            new MaskRegionOverride(0f, 0.02f, 0.97f, 0.80f, MaskRegion.Tertiary, Ellipse: true),
            new MaskRegionOverride(0.19f, 0.12f, 0.80f, 0.69f, MaskRegion.Primary, Ellipse: true),
            new MaskRegionOverride(0.19f, 0.12f, 0.80f, 0.69f, MaskRegion.Tertiary, DarkGold, Ellipse: true),
        ], "Iron Warriors right pad: frame tertiary, field primary, star tertiary"),
        // Death Guard Champion pads (chaos dlc3_set2): rust spots and the bare metal patch mottled the dome; inside the rust band the dome is plate, the grey metal patch
        // tertiary.
        .. Each(["csm_armour_mp_dlc3_set2_shldrl", "csm_armour_mp_dlc3_set2_shldrr"],
        [
            new MaskRegionOverride(0.06f, 0.03f, 0.94f, 0.57f, MaskRegion.Primary, new TexelFilter(MinLum: 4f), Ellipse: true),
            new MaskRegionOverride(0.28f, 0.03f, 0.70f, 0.24f, MaskRegion.Tertiary, new TexelFilter(MinLum: 40f, MaxChroma: 10f), Ellipse: true),
        ], "Death Guard pads: dome plate, metal patch tertiary"),
        // Campaign Chaos left pads (basic, berserker, raptor): the whole pad, gold frame and emblem included, was one plate region ("left pad flat and pale"); the gold
        // frame and the silver emblem are tertiary like the regular Chaos trims, the field primary.
        .. Each(["csm_armour_basic_shldrl", "csm_armour_berserker_shldrl", "csm_armour_raptor_shldrl"],
        [
            new MaskRegionOverride(0f, 0.01f, 0.98f, 0.85f, MaskRegion.Tertiary, DarkGold, Ellipse: true),
            new MaskRegionOverride(0.18f, 0.21f, 0.76f, 0.73f, MaskRegion.Tertiary, new TexelFilter(MinLum: 60f, MaxChroma: 14f), Ellipse: true),
        ], "Campaign Chaos left pad: gold frame and silver emblem tertiary"),
        // Campaign raptor and basic torsos: the ribbed belt strip at the texture's right edge (round the back of the waist, under the pack) stayed baked grey on the
        // raptor ("back waist grey"); it is trim (tertiary), as the berserker torso's builder mask already makes it.
        .. Each(["csm_armour_raptor_torso", "csm_armour_basic_torso"], [new MaskRegionOverride(0.91f, 0f, 1f, 1f, MaskRegion.Tertiary, new TexelFilter(MinLum: 6f))],
            "Campaign raptor and basic torsos: the back belt strip is tertiary trim"),
        // Legion of the Damned legs (dlc1_set1): the boot toe caps' light ridges were split between plate and ornament ("boot toes half-painted"): the toe cap is plate.
        new("sm_armour_mp_dlc1_set1_legl", null, null, [new MaskRegionOverride(0.25f, 0.18f, 0.55f, 0.32f, MaskRegion.Primary)], "Legion of the Damned left boot toe: plate"),
        new("sm_armour_mp_dlc1_set1_legr", null, null, [new MaskRegionOverride(0.45f, 0.18f, 0.75f, 0.32f, MaskRegion.Primary)], "Legion of the Damned right boot toe: plate"),
        // Campaign havoc legs: the dark plates of the shin stayed partly baked ("shin speckle"); they are the leg plate (secondary).
        .. Each(["csm_armour_havoc_legl", "csm_armour_havoc_legr"],
            [new MaskRegionOverride(0f, 0.40f, 0.48f, 0.64f, MaskRegion.Secondary, new TexelFilter(MinLum: 6f), From: MaskRegion.Baked)],
            "Campaign havoc legs: shin plates secondary"),

        // Titus (campaign, sm_armour_basic_upres). The largest colour cluster is the near-black joints, so the black-plate rules ran and took the lit blue plates
        // (L* 26 to 33, chroma 16: "light compact areas") for ornaments: the arms were mostly tertiary and the backpack mottled. Blue is the plate.
        .. Each(["sm_armour_basic_upres_helmet", "sm_armour_basic_upres_torso", "sm_armour_basic_upres_arml", "sm_armour_basic_upres_armr", "sm_armour_basic_upres_legl",
                 "sm_armour_basic_upres_legr", "sm_armour_basic_upres_backpack"],
            [Whole(MaskRegion.Primary, UmBlue, MaskRegion.Tertiary), Whole(MaskRegion.Primary, UmBlue)],
            "Titus: the blue plates are primary, not light ornaments"),
        new("sm_armour_basic_upres_backpack", null, null,
        [
            // The large brass top plate: secondary, as the upper box of the regular Mk 7 backpack (test_rgb: green); its dark grille slots stay baked.
            new MaskRegionOverride(0.25f, 0.76f, 0.49f, 0.98f, MaskRegion.Secondary, new TexelFilter(MinLum: 28f)),
            new MaskRegionOverride(0.25f, 0.76f, 0.49f, 0.98f, MaskRegion.Baked, new TexelFilter(MaxLum: 28f)),
        ], "Titus backpack: the brass top plate is secondary"),

        // Iron Warriors right leg: the hazard-stripe panel. Its yellow stripes were mostly tertiary and its black stripes baked ("hazard stripes half baked"): the yellow
        // stripes are tertiary (the Chaos trim channel) and the black stripes the leg plate (secondary on Chaos legs), so the panel paints in two scheme colours.
        new("csm_armour_mp_ironwarr_legr", null, null,
        [
            new MaskRegionOverride(0.12f, 0.24f, 0.58f, 0.83f, MaskRegion.Tertiary, Hue(1.25f, 1.95f, 25f, 40f)),
            new MaskRegionOverride(0.12f, 0.24f, 0.58f, 0.83f, MaskRegion.Secondary, new TexelFilter(MaxLum: 75f, MaxChroma: 20f)),
        ], "Iron Warriors right leg: hazard stripes yellow tertiary, black leg plate"),

        // Campaign raptor (csm_armour_raptor). Its yellow plate paint (hue 1.2 to 1.65, chroma 60 to 80) was partly taken for warm ornament and partly left baked
        // ("baked orange edges torso / thigh"); on the legs it was tertiary and the dark trim the leg plate. Yellow is the plate; brass trim (chroma about 47) is not.
        .. Each(["csm_armour_raptor_helmet", "csm_armour_raptor_torso", "csm_armour_raptor_shldrl", "csm_armour_raptor_shldrr", "csm_armour_raptor_arml",
                 "csm_armour_raptor_armr", "csm_armour_raptor_backpack"],
            [Whole(MaskRegion.Primary, RaptorYellow, from: null)], "Campaign raptor: the yellow paint is the plate"),
        .. Each(["csm_armour_raptor_legl", "csm_armour_raptor_legr"],
            [Whole(MaskRegion.Tertiary, from: MaskRegion.Secondary), Whole(MaskRegion.Secondary, RaptorYellow, from: null)],
            "Campaign raptor legs: the yellow paint is the leg plate (secondary), the dark trim tertiary"),
        // The dark red heart-shaped panel on the jump pack stayed baked: it is the pack's second colour.
        new("csm_armour_raptor_backpack", null, null,
            [new MaskRegionOverride(0.33f, 0.37f, 0.49f, 0.66f, MaskRegion.Secondary, Hue(0.2f, 1.05f, 18f, 15f))], "Campaign raptor jump pack: red panel secondary"),

        // Emperor's Children. Left pad: the upper half of the texture is the pad's ornament frame (brass, face mask, bone skull, grille); the skull and the grille
        // were read as plate ("pad skull takes plate colour") and are tertiary like the rest of the frame. Helmet: its light bone plate drew brighter than the
        // purple body beside it after shading, so its targets are scaled down.
        new("csm_armour_mp_children_shldrl", null, null,
            [new MaskRegionOverride(0f, 0f, 1f, 0.66f, MaskRegion.Tertiary, From: MaskRegion.Primary)], "Emperor's Children left pad: skull and grille tertiary"),
        new("csm_armour_mp_children_helmet", null, null, [], "Emperor's Children helmet: as bright as the body", Brightness: 0.75f),
        // Both pads' domes (lower third): the grey vent grilles beside the purple field were plate; they are trim (tertiary).
        .. Each(["csm_armour_mp_children_shldrl", "csm_armour_mp_children_shldrr"],
            [new MaskRegionOverride(0f, 0.66f, 1f, 1f, MaskRegion.Tertiary, new TexelFilter(MaxChroma: 9f), From: MaskRegion.Primary)],
            "Emperor's Children pads: vent grilles tertiary"),

        // Death Guard Champion pads (csm_armour_mp_dlc3_set2, Mk 7 pad layout). The dome mixed khaki field, rust bands and a bare-metal plate in one partly painted
        // primary region (baked khaki blotches) and the rims were plate. As on the regular Chaos pad (test_rgb): the field is primary (wholly, khaki hue 1.5 to 2.3), the rust
        // bands (hue 0.6 to 1.3) and bare metal are tertiary, and the rim pieces below the dome are tertiary trim.
        .. Each(["csm_armour_mp_dlc3_set2_shldrl", "csm_armour_mp_dlc3_set2_shldrr"],
        [
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary, Hue(1.45f, 2.4f, 5f)),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Tertiary, Hue(0.55f, 1.3f, 20f, 40f)),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Tertiary, new TexelFilter(MinLum: 90f, MaxChroma: 7f)),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary, From: MaskRegion.Baked),
            new MaskRegionOverride(0f, 0.31f, 1f, 1f, MaskRegion.Tertiary),
        ], "Death Guard pads: field primary, rust and metal tertiary, rims tertiary"),

        // Sidonus pads (campaign). The gilded wings and rims are the largest colour, so the blue dome field took the secondary region ("field all secondary") and the
        // white omega was split ("omega smear"); the right pad's blue field edge stayed baked. As on the regular Mk 7 pad: the dome field is primary, the wings and
        // the omega tertiary (the aquila's place), and the rim pieces below the dome (top 31 % of the texture) secondary.
        .. Each(["sm_armour_sidonus_shldrl", "sm_armour_sidonus_shldrr"],
        [
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary, UmBlue),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Tertiary, Hue(0.9f, 1.95f, 8f, 12f)),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Tertiary, new TexelFilter(MinLum: 105f, MaxChroma: 15f)),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary, From: MaskRegion.Baked),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary, From: MaskRegion.Secondary),
            new MaskRegionOverride(0f, 0.31f, 1f, 1f, MaskRegion.Secondary),
        ], "Sidonus pads: blue field primary, wings and omega tertiary, rims secondary"),

        // Black Templars (DLC and campaign share textures). Left pad: the bone field was split between primary and tertiary ("ragged tertiary blotch") and the black cross
        // was plate: in the dome the bone field is primary and the black cross tertiary (the emblem's place on the regular pad). Right pad: the plain bone dome is wholly
        // primary. Helmet crown and gauntlets: matte black plate texels the black-plate rules took for undersuit stayed baked ("baked black"); there they are plate.
        .. Each(["sm_armour_blacktemp_shldrl", "sm_armour_black_temp_shldrl"],
            [ArcDome(MaskRegion.Primary, new TexelFilter(MinLum: 55f)), ArcDome(MaskRegion.Tertiary, new TexelFilter(MaxLum: 55f)),
             new MaskRegionOverride(0f, 0.385f, 1f, 1f, MaskRegion.Secondary)], "Black Templars left pad: bone field primary, cross tertiary"),
        .. Each(["sm_armour_blacktemp_shldrr", "sm_armour_black_temp_shldrr"],
            [ArcDome(MaskRegion.Primary), new MaskRegionOverride(0f, 0.385f, 1f, 1f, MaskRegion.Secondary)], "Black Templars right pad: bone dome primary"),
        .. Each(["sm_armour_blacktemp_helmet", "sm_armour_black_temp_helmet", "sm_armour_blacktemp_arml", "sm_armour_black_temp_arml", "sm_armour_blacktemp_armr",
                 "sm_armour_black_temp_armr"],
            [Whole(MaskRegion.Primary, new TexelFilter(MinLum: 2f, MaxChroma: 12f))], "Black Templars helmet and gauntlets: matte black plate is plate"),
        // Leandros pads (same arc layout): the dome's baked holes are plate.
        .. Each(["sm_armour_leandros_shldrl", "sm_armour_leandros_shldrr"],
            [ArcDome(MaskRegion.Primary, from: MaskRegion.Baked), ArcDome(MaskRegion.Tertiary, new TexelFilter(MinLum: 95f, MaxChroma: 14f)),
             new MaskRegionOverride(0f, 0.385f, 1f, 1f, MaskRegion.Secondary)],
            "Leandros pads: dome holes plate; the white insignia wholly tertiary (its thin strokes fail the insignia rule's 60 % neighbourhood test: white fringes)"),
        // Iron Hands Veteran pads: the same white fringe round the gauntlet and cog emblems (thin fingers and teeth).
        .. Each(["sm_armour_mp_dlc3_set1_shldrl", "sm_armour_mp_dlc3_set1_shldrr"],
            [new MaskRegionOverride(0.3f, 0.04f, 0.7f, 0.31f, MaskRegion.Tertiary, new TexelFilter(MinLum: 60f, MaxChroma: 14f), Ellipse: true)], "Iron Hands pads: white emblems wholly tertiary"),

        // Space Wolves pads: a heraldic disc (V 0.3 to 0.86) inside a grey metal ring. The ring took the primary region and the disc the secondary, the reverse of the
        // regular pad's dome and rim; the left disc's black wolf was split ("mottled field") and the right disc's edge kept baked specks ("rim stain"). The disc field
        // is primary, its marking (black wolf, red chevrons) tertiary as the regular pad's emblem, the ring secondary.
        .. Each(["sm_armour_mp_spacewolves_shldrl", "sm_armour_mp_spacewolves_shldrr"],
        [
            new MaskRegionOverride(0f, 0.24f, 1f, 1f, MaskRegion.SwapPrimarySecondary),
            new MaskRegionOverride(0.17f, 0.31f, 0.83f, 0.86f, MaskRegion.Primary, Ellipse: true, From: MaskRegion.Baked),
        ], "Space Wolves pads: disc primary, ring secondary"),
        new("sm_armour_mp_spacewolves_shldrl", null, null,
            [new MaskRegionOverride(0.17f, 0.30f, 0.83f, 0.81f, MaskRegion.Tertiary, new TexelFilter(MaxLum: 40f, MaxChroma: 14f), Ellipse: true)], "Space Wolves left pad: black wolf tertiary"),
        new("sm_armour_mp_spacewolves_shldrr", null, null,
            [new MaskRegionOverride(0.17f, 0.32f, 0.83f, 0.87f, MaskRegion.Tertiary, Hue(-0.4f, 1.05f, 28f, 15f), Ellipse: true)], "Space Wolves right pad: red chevrons tertiary"),

        // Ultramarines campaign (sm_armour_basic_001): the olive-gold aquila and skull were the second colour (secondary) and small gold trims stayed baked ("trim baked
        // olive-yellow", "chest aquila primary"): gold is tertiary, as the regular chest aquila. The pads keep their gold rims secondary (Mk 7 layout).
        .. Each(["sm_armour_basic_001_helmet", "sm_armour_basic_001_torso", "sm_armour_basic_001_arml", "sm_armour_basic_001_armr", "sm_armour_basic_001_legl",
                 "sm_armour_basic_001_legr", "sm_armour_basic_001_backpack", "sm_armour_dark_angels_helmet", "sm_armour_dark_angels_torso", "sm_armour_dark_angels_arml",
                 "sm_armour_dark_angels_armr", "sm_armour_dark_angels_legl", "sm_armour_dark_angels_legr", "sm_armour_dark_angels_backpack"],
            [Whole(MaskRegion.Tertiary, DarkGold, MaskRegion.Secondary), Whole(MaskRegion.Tertiary, DarkGold), Whole(MaskRegion.Tertiary, DarkGold, MaskRegion.Primary)],
            "Ultramarines and Dark Angels campaign: gold trim and aquila tertiary"),
        // Dark Angels campaign pads: the rims are wholly secondary by the Mk 7 entry (their orange specks with them); the dome's baked holes are plate.
        .. Each(["sm_armour_dark_angels_shldrl", "sm_armour_dark_angels_shldrr"], [new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary, From: MaskRegion.Baked)],
            "Dark Angels campaign pads: dome holes plate"),

        // Campaign havoc pads (Mk 7 layout): the bone skull below the dome was split between plate and trim ("pad skulls split") and the rim pieces were partly plate;
        // below the dome everything is tertiary, as the regular Chaos pad's trims and ornaments.
        .. Each(["csm_armour_havoc_shldrl", "csm_armour_havoc_shldrr"], [new MaskRegionOverride(0f, 0.31f, 1f, 1f, MaskRegion.Tertiary)],
            "Campaign havoc pads: skull and rims tertiary"),

        // Blood Ravens, DLC (sm_armour_mp_bloodravens) and campaign (sm_armour_blood_ravens). Pads: the domes are bone with a black marking (winged raven with a red
        // stone, the campaign right pad's chevron); the marking was split and partly baked ("blotches round the raven", "baked black marking", "brown smear"). In the
        // dome the bone field is primary and the marking tertiary (the emblem's place); the DLC right pad's plain dome is wholly primary. Backpacks: the large bone top
        // plate (the Titus layout) is secondary like the regular pack's upper box. Baked red-orange plate texels left at panel edges ("red edge lines") are plate.
        .. Each(["sm_armour_mp_bloodravens_shldrl", "sm_armour_blood_ravens_shldrl", "sm_armour_blood_ravens_shldrr"],
        [
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Tertiary, new TexelFilter(MaxLum: 45f)),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Tertiary, new TexelFilter(MinChroma: 25f)),
        ], "Blood Ravens pads: bone dome primary, marking tertiary"),
        new("sm_armour_mp_bloodravens_shldrr", null, null, [new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary)], "Blood Ravens DLC right pad: bone dome primary"),
        .. Each(["sm_armour_mp_bloodravens_bp", "sm_armour_blood_ravens_backpack"],
        [
            new MaskRegionOverride(0.25f, 0.76f, 0.49f, 0.98f, MaskRegion.Secondary, new TexelFilter(MinLum: 28f)),
            new MaskRegionOverride(0.25f, 0.76f, 0.49f, 0.98f, MaskRegion.Baked, new TexelFilter(MaxLum: 28f)),
        ], "Blood Ravens backpacks: the bone top plate is secondary"),
        .. Each(["sm_armour_mp_bloodravens_helmet", "sm_armour_mp_bloodravens_torso", "sm_armour_mp_bloodravens_legl", "sm_armour_mp_bloodravens_legr", "sm_armour_mp_bloodravens_bp",
                 "sm_armour_blood_ravens_helmet", "sm_armour_blood_ravens_torso", "sm_armour_blood_ravens_arml", "sm_armour_blood_ravens_armr", "sm_armour_blood_ravens_legl",
                 "sm_armour_blood_ravens_legr", "sm_armour_blood_ravens_backpack"],
            [Whole(MaskRegion.Primary, Hue(0.3f, 1.1f, 15f, 12f))], "Blood Ravens: baked red plate texels are plate"),

        // Corvus right pad (campaign): in the dome the red base plate took the secondary region ("pad base plate takes secondary") beside the silver wings. The red
        // plate is primary, the silver wings tertiary (the emblem's place), the rest of the dome primary.
        new("sm_armour_corvus_shldrr", null, null,
        [
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary, From: MaskRegion.Secondary),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary, From: MaskRegion.Baked),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary, Hue(0.2f, 1.1f, 15f)),
            new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Tertiary, new TexelFilter(MinLum: 45f, MaxChroma: 12f)),
        ], "Corvus right pad: red base plate primary, silver wings tertiary"),

        // Legion of the Damned (dlc1_set1; owner: flames secondary). The flames' dark red outlines (chroma 8 to 20, too dull for the secondary-hue rule) stayed baked
        // between the black field and the flame ("flame bases fade into the field"): in the flame bands warm texels with some chroma are flame (secondary) and the
        // remaining baked texels field (primary). Right pad: the bone skull below the dome was rim (secondary, drawn in the bright rim colour: "over-saturated"); it is
        // tertiary like the scroll.
        .. Each(["sm_armour_mp_dlc1_set1_shldrl"],
            [new MaskRegionOverride(0f, 0.08f, 1f, 0.31f, MaskRegion.Secondary, Hue(0.3f, 1.4f, 8f, 8f), From: MaskRegion.Baked),
             new MaskRegionOverride(0f, 0f, 1f, 0.31f, MaskRegion.Primary, From: MaskRegion.Baked)], "Legion of the Damned left pad: flame outlines secondary"),
        .. Each(["sm_armour_mp_dlc1_set1_legl", "sm_armour_mp_dlc1_set1_legr"],
            [new MaskRegionOverride(0f, 0.82f, 1f, 1f, MaskRegion.Secondary, Hue(0.3f, 1.4f, 8f, 8f), From: MaskRegion.Baked),
             new MaskRegionOverride(0f, 0.82f, 1f, 1f, MaskRegion.Primary, From: MaskRegion.Baked)], "Legion of the Damned legs: flame outlines secondary"),
        new("sm_armour_mp_dlc1_set1_shldrr", null, null,
            [new MaskRegionOverride(0f, 0.55f, 0.36f, 0.9f, MaskRegion.Tertiary, new TexelFilter(MinLum: 50f, MaxChroma: 30f)), Whole(MaskRegion.Tertiary, new TexelFilter(MinLum: 50f, MaxChroma: 30f))],
            "Legion of the Damned right pad: skull tertiary"),

        // Alpha Legion Champion (chaos dlc1_set1) legs and arms: the teal scale panels (hue past -2.3 rad, beyond the plate hue match) and parts of the blue plates
        // stayed baked ("right leg teal scale baked"; the "faint baked blue" in the Word Bearers row), and the silver trim bands were unpainted. Blue and teal are the
        // plate (legs: secondary, arms: primary, the regular Chaos channels); the grey trim is tertiary like the regular Chaos trim.
        .. Each(["csm_armour_mp_dlc1_set1_legl", "csm_armour_mp_dlc1_set1_legr"],
            [Whole(MaskRegion.Secondary, AlBlueTeal, from: null), Whole(MaskRegion.Tertiary, ChaosSilver)], "Alpha Legion legs: blue and teal plate, silver trim tertiary"),
        .. Each(["csm_armour_mp_dlc1_set1_arml", "csm_armour_mp_dlc1_set1_armr", "csm_armour_mp_dlc1_set1_torso", "csm_armour_mp_dlc1_set1_bp"],
            [Whole(MaskRegion.Primary, AlBlueTeal, from: null), Whole(MaskRegion.Tertiary, ChaosSilver)], "Alpha Legion arms, torso and backpack: blue and teal plate, silver trim tertiary"),

        // dlc2_set3 / dlc3_set2 helmet (byte-identical textures): its baked plate is a light, even grey, so the shaded crown drew flat and brighter than the regular
        // pieces beside it ("helmet crown flat / over-bright"); its targets are scaled down. Partial: the texture has little shading to keep.
        .. Each(["csm_armour_mp_dlc2_set1_helmet", "csm_armour_mp_dlc3_set1_helmet"], [], "dlc2_set3 / dlc3_set2 helmet: as bright as the body (round 4: 0.72, was 0.82)", brightness: 0.72f),
    ];

    /// <summary>Alpha Legion blue and teal plate paint (hue from 2.6 round through pi to -1.0 rad).</summary>
    static TexelFilter AlBlueTeal => new(MinLum: 10f, MinHue: 2.6f, MaxHue: -1.0f, MinChroma: 8f);

    /// <summary>Grey silver trim (light, nearly colourless).</summary>
    static TexelFilter ChaosSilver => new(MinLum: 45f, MaxChroma: 8f);

    /// <summary>The campaign raptor's yellow and orange plate paint.</summary>
    static TexelFilter RaptorYellow => Hue(1.0f, 1.8f, 55f, 60f);

    /// <summary>Ultramarines blue plate paint (hue about -1.7 rad).</summary>
    static TexelFilter UmBlue => Hue(-2.3f, -1.1f, 6f);

    /// <summary>Dark gold, brass and bronze trim (warm hue, some chroma, not black).</summary>
    static TexelFilter DarkGold => new(MinLum: 14f, MinHue: 0.9f, MaxHue: 1.85f, MinChroma: 7f);

    /// <summary>Texels of a hue band (radians) with at least <paramref name="minChroma"/>.</summary>
    static TexelFilter Hue(float min, float max, float minChroma, float minLum = 6f) => new(MinLum: minLum, MinHue: min, MaxHue: max, MinChroma: minChroma);

    /// <summary>The whole texture: the texels the mask so far leaves unpainted (by default) that pass <paramref name="f"/> go to <paramref name="to"/>.</summary>
    static MaskRegionOverride Whole(MaskRegion to, TexelFilter? f = null, MaskRegion? from = MaskRegion.Baked) => new(0f, 0f, 1f, 1f, to, f, From: from);

    /// <summary>The same regions (and brightness scale) for each of <paramref name="packages"/>.</summary>
    static IEnumerable<BuiltMaskOverride> Each(string[] packages, MaskRegionOverride[] regions, string why, float brightness = 1f, float contrast = 1f) =>
        packages.Select(p => new BuiltMaskOverride(p, null, null, regions, why, brightness, contrast));

    /// <summary>
    /// Round 4 (finding 6 of the round-3 re-gate): the packages the table names that <paramref name="exists"/> says are not in the install (a misspelt or missing
    /// package's entry would silently never apply). <c>smtool built-mask-bench</c> prints them as warnings (not the catalogue load: an install without the campaign
    /// packages lacks them by design).
    /// </summary>
    public static IReadOnlyList<string> MissingPackages(Func<string, bool> exists) =>
        All.Select(o => o.Package).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !exists(p)).ToList();

    /// <summary>The overrides for a package's material (package-wide ones first).</summary>
    public static IEnumerable<BuiltMaskOverride> For(string? package, string? material) =>
        package is null ? [] : All.Where(o => o.Package.Equals(package, StringComparison.OrdinalIgnoreCase)
                                             && (o.Material is null || string.Equals(o.Material, material, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The mask with <paramref name="regions"/> applied over <paramref name="diffuse"/> (the baked colours the filters read); the input is not changed.</summary>
    /// <param name="covered">The texels the mesh's UVs cover (null: every texel with any paint counts as covered).</param>
    public static PaintMaskBuilder.MaskOnly Apply(PaintMaskBuilder.MaskOnly m, DdsImage diffuse, IReadOnlyList<MaskRegionOverride> regions, bool[]? covered = null)
    {
        if (regions.Count == 0) return m;
        var mask = (byte[])m.Mask.Clone();
        int w = m.Width, h = m.Height;
        foreach (var r in regions)
        {
            int x0 = Math.Clamp((int)MathF.Floor(r.U0 * w), 0, w), x1 = Math.Clamp((int)MathF.Ceiling(r.U1 * w), 0, w);
            int y0 = Math.Clamp((int)MathF.Floor(r.V0 * h), 0, h), y1 = Math.Clamp((int)MathF.Ceiling(r.V1 * h), 0, h);
            float cx = (r.U0 + r.U1) * 0.5f * w, cy = (r.V0 + r.V1) * 0.5f * h, rx = MathF.Max(0.5f, (r.U1 - r.U0) * 0.5f * w), ry = MathF.Max(0.5f, (r.V1 - r.V0) * 0.5f * h);
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                if (r.Ellipse) { float dx = (x + 0.5f - cx) / rx, dy = (y + 0.5f - cy) / ry; if (dx * dx + dy * dy > 1f != r.Outside) continue; }
                int i = y * w + x, o = i * 4;
                bool isCovered = covered is not null && covered.Length == w * h ? covered[i] : mask[o] + mask[o + 1] + mask[o + 2] > 0;
                if (!isCovered) continue;   // not drawn by the mesh: left alone
                if (r.From is { } from && RegionOf(mask[o], mask[o + 1], mask[o + 2]) != from) continue;
                if (r.Filter is { } f && f != TexelFilter.Any)
                {
                    byte cr = diffuse.Rgba[o], cg = diffuse.Rgba[o + 1], cb = diffuse.Rgba[o + 2];
                    var (_, a, b) = PaintMaskBuilder.LabOf(cr, cg, cb);
                    if (!f.Matches(0.2126f * cr + 0.7152f * cg + 0.0722f * cb, MathF.Atan2(b, a), MathF.Sqrt(a * a + b * b))) continue;
                }
                if (r.To == MaskRegion.SwapPrimarySecondary) { (mask[o], mask[o + 1]) = (mask[o + 1], mask[o]); continue; }
                mask[o] = mask[o + 1] = mask[o + 2] = 0;
                if (r.To != MaskRegion.Baked) mask[o + (int)r.To] = 255;
            }
        }
        return m with { Mask = mask };
    }

    /// <summary>The region a mask texel is mostly in: <see cref="MaskRegion.Baked"/> when painted under half, else its largest channel (primary first on a tie).</summary>
    public static MaskRegion RegionOf(byte p, byte s, byte t) =>
        p + s + t < 128 ? MaskRegion.Baked : p >= s && p >= t ? MaskRegion.Primary : s >= t ? MaskRegion.Secondary : MaskRegion.Tertiary;
}
