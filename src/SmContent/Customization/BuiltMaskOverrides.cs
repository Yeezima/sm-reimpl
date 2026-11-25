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
public sealed record MaskRegionOverride(float U0, float V0, float U1, float V1, MaskRegion To, TexelFilter? Filter = null, bool Ellipse = false);

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
    ];

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
}
