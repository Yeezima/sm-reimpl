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

    public bool Matches(float lum, float hue, float chroma) =>
        (MinLum is not { } a || lum >= a) && (MaxLum is not { } b || lum <= b) && (MinChroma is not { } c || chroma >= c) && (MaxChroma is not { } d || chroma <= d)
        && (MinHue is not { } h0 || MaxHue is not { } h1 || (h0 <= h1 ? hue >= h0 && hue <= h1 : hue >= h0 || hue <= h1));
}

/// <summary>
/// One hand-placed region of a built mask (armour-pieces-1125 round 3, owner: "3rd round, no stopgap": per-piece region data where the rules cannot reach the 92 %
/// gate). A rectangle (or the ellipse inside it) in texture space, 0..1 with V down as the textures are stored; the texels in it that pass <see cref="Filter"/>
/// are put wholly in <see cref="To"/>. Ours; data only (no original bytes): the regions are applied at load time to the install's own textures.
/// </summary>
/// <remarks><see cref="From"/> (round 3, second worker) narrows it to the texels the mask so far puts mostly in that region (<see cref="MaskRegion.Baked"/>: texels
/// painted under half, the baked holes and blotches a plate kept).</remarks>
public sealed record MaskRegionOverride(float U0, float V0, float U1, float V1, MaskRegion To, TexelFilter? Filter = null, bool Ellipse = false, MaskRegion? From = null);

/// <summary>Per-piece data for a built mask: an optional channel map (instead of <see cref="BakedArmourPaint.ChannelMap"/>) and regions applied in order.</summary>
/// <param name="Material">The material it applies to, or null for every built material of the package.</param>
/// <param name="Channels">Where the builder's primary, secondary and tertiary regions go (paint channel per region), or null for the rule's map.</param>
public sealed record BuiltMaskOverride(string Package, string? Material, int[]? Channels, IReadOnlyList<MaskRegionOverride> Regions, string Why);

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
        "sm_armour_basic_001_shldrl", "sm_armour_basic_001_shldrr", "sm_armour_black_temp_shldrl", "sm_armour_black_temp_shldrr", "sm_armour_blacktemp_shldrl",
        "sm_armour_blacktemp_shldrr", "sm_armour_blood_ravens_shldrl", "sm_armour_blood_ravens_shldrr", "sm_armour_corvus_shldrr", "sm_armour_dark_angels_shldrl",
        "sm_armour_dark_angels_shldrr", "sm_armour_leandros_shldrl", "sm_armour_leandros_shldrr", "sm_armour_mp_bloodravens_shldrl", "sm_armour_mp_bloodravens_shldrr",
        "sm_armour_mp_dlc1_set1_shldrl", "sm_armour_mp_dlc1_set1_shldrr", "sm_armour_mp_dlc2_set1_shldrl", "sm_armour_mp_dlc2_set1_shldrr",
        "sm_armour_mp_dlc2_set2_shldrl", "sm_armour_mp_dlc2_set2_shldrr", "sm_armour_mp_dlc3_set1_shldrl", "sm_armour_mp_dlc3_set1_shldrr",
    ];

    public static readonly IReadOnlyList<BuiltMaskOverride> All =
    [
        .. Mk7PadLayout.Select(p => new BuiltMaskOverride(p, null, null,
            [new MaskRegionOverride(0f, 0.31f, 1f, 1f, MaskRegion.Secondary)], "Mk 7 pad layout: the rim pieces are secondary, as on the regular Mk 7 pad")),
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
    ];

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

    static IEnumerable<BuiltMaskOverride> Each(string[] packages, MaskRegionOverride[] regions, string why) =>
        packages.Select(p => new BuiltMaskOverride(p, null, null, regions, why));

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
                if (r.Ellipse) { float dx = (x + 0.5f - cx) / rx, dy = (y + 0.5f - cy) / ry; if (dx * dx + dy * dy > 1f) continue; }
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
