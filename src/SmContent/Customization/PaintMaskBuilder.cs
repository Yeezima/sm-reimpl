using SmFormats.Dds;

namespace SmContent.Customization;

/// <summary>
/// What <see cref="PaintMaskBuilder.Build"/> made from a baked armour texture: a paint mask in the game's layout (R primary, G secondary, B tertiary,
/// A dirt = 0) and a matching diffuse whose painted regions are neutral grey shading (the original pixels elsewhere), both RGBA8 at the diffuse's size.
/// </summary>
/// <param name="Primary">Share of the texture's covered pixels in each region (0..1), for logs and tests.</param>
/// <remarks>PlateMedian, SecondaryMedian and OrnamentMedian: the regions' median luminances (sRGB 0..255) the grey diffuse is normalised by (kept with a cached mask).</remarks>
public sealed record BuiltPaintMask(int Width, int Height, byte[] Mask, byte[] Diffuse, float Primary, float Secondary, float Tertiary)
{
    public float PlateMedian { get; init; }
    public float SecondaryMedian { get; init; }
    public float OrnamentMedian { get; init; }
}

/// <summary>
/// Builds a paint mask for an armour piece whose textures have none (armour-pieces-1125: the DLC and campaign-reward sets bake their colours into the
/// diffuse). **Ours** (the original game never paints these pieces; docs/changes-from-original.md). Engine-free and deterministic: the same textures give
/// the same bytes (no randomness, no threads, fixed iteration counts).
/// <para>
/// Method. Pixels are compared in CIELAB (D65, sRGB input). Background (black, no specular) is left out. Ornaments (the tertiary region) are the light
/// property map's reflection mask (B, the gold ornaments; docs/discovery.md "Light property maps") plus pixels much lighter than the plates in a warm
/// bone or gold hue. The plate colours are the two largest clusters of the remaining specular-bearing pixels in the (a*, b*) plane with a coarse
/// lightness term (a deterministic histogram mode, then a few k-means refinements): the largest is the primary region, the second (when it is large
/// and far enough away) the secondary. Membership falls off smoothly with the colour distance, and pixels without specular (undersuit, cables, rubber)
/// are kept out. The diffuse of a painted pixel becomes grey shading: its luminance relative to its region's median, mapped onto the median plate
/// luminance of the game's own grey (masked) armour, so the existing paint shader (<c>MapMaterials.Paint</c>: albedo = gain x luminance x colour) paints
/// it as bright as regular armour; very dark baked plates (black schemes) are compressed toward the median to hide block noise.
/// </para>
/// </summary>
public static class PaintMaskBuilder
{
    /// <summary>Median plate luminance (sRGB 0..255) of the game's masked multiplayer armour (measured on nine basic/veteran/havoc pieces: 83).</summary>
    public const float RegularPlateLuminance = 83f;
    /// <summary>Median ornament luminance (sRGB 0..255) of the masked armour's tertiary region (measured: 62).</summary>
    public const float RegularOrnamentLuminance = 62f;

    /// <summary>
    /// Builds the mask and grey diffuse. <paramref name="specular"/> and <paramref name="lightProp"/> may be null or of another size (they are sampled at the
    /// diffuse's pixel positions).
    /// </summary>
    /// <param name="used">Optional: which texels the mesh's UVs cover (width x height, row-major); others are ignored when the colours are clustered (an
    /// unused grey texture background must not become the "plate" colour).</param>
    public static BuiltPaintMask Build(DdsImage diffuse, DdsImage? specular, DdsImage? lightProp, bool[]? used = null)
    {
        int w = diffuse.Width, h = diffuse.Height, n = w * h;
        var L = new float[n]; var A = new float[n]; var B = new float[n]; var spec = new float[n]; var lpB = new float[n]; var lum = new float[n];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            byte r = diffuse.Rgba[i * 4], g = diffuse.Rgba[i * 4 + 1], b = diffuse.Rgba[i * 4 + 2];
            (L[i], A[i], B[i]) = Lab(r, g, b);
            lum[i] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            spec[i] = specular is null ? 40f : Sample(specular, x, y, w, h, -1);
            lpB[i] = lightProp is null ? 0f : Sample(lightProp, x, y, w, h, 2);
        }

        var covered = new bool[n];
        int coveredCount = 0;
        if (used is not null && used.Length != n) used = null;
        // With the mesh's UV coverage known, a covered texel counts whatever its colour (repair attempt 1: matte black plates under 4 levels of luminance and
        // specular were taken for unused black background and stayed baked); without it, black, non-specular texels are taken as background.
        for (int i = 0; i < n; i++)
            if (used is not null && !OldMaskRules ? used[i] : (used is null || used[i]) && (lum[i] > 4f || spec[i] > 4f)) { covered[i] = true; coveredCount++; }

        // Plate clusters: specular-bearing, not ornament-marked pixels.
        var candidates = new List<int>();
        // Repair attempt 1 (ours): the specular gates scale with the texture's own median specular (k = median / 25, 0.2 to 1), so a matte black plate (the Salamanders
        // backpack: median specular about 6) is not dropped as undersuit by gates set for glossy paint.
        float k = 1f;
        if (!OldMaskRules)
        {
            var specs = new List<float>();
            for (int i = 0; i < n; i++) if (covered[i] && lum[i] > 4f) specs.Add(spec[i]);
            float median = Median(specs, 25f);
            k = Math.Clamp(median / 25f, 0.2f, 1f);
            // A specular map that is nearly empty (median under 5 of 255) says nothing about plate and undersuit: it is read as absent (as with no map).
            if (median < 5f) { for (int i = 0; i < n; i++) spec[i] = 40f; k = 1f; }
        }
        for (int i = 0; i < n; i++) if (covered[i] && spec[i] > 10f * k && lpB[i] < 8f) candidates.Add(i);
        if (candidates.Count < 16) for (int i = 0; i < n; i++) if (covered[i]) candidates.Add(i);
        var (c1, c2, has2) = PlateClusters(candidates, L, A, B);
        if (Environment.GetEnvironmentVariable("SM_MASK_DEBUG") == "1")
            Console.WriteLine($"mask debug {w}x{h}: spec scale {k:0.00}, covered {coveredCount}, candidates {candidates.Count}, c1 L{c1.L:0} a{c1.A:0} b{c1.B:0}, c2 {(has2 ? $"L{c2.L:0} a{c2.A:0} b{c2.B:0}" : "-")}");

        // Warm, light ornaments (bone, gold, brass) when the plates are not themselves warm and light.
        float plateHue = MathF.Atan2(c1.B, c1.A), plateChroma = MathF.Sqrt(c1.A * c1.A + c1.B * c1.B);

        var tert = new float[n]; var prim = new float[n]; var sec = new float[n];
        var plateLum = new List<float>(); var secLum = new List<float>(); var ornLum = new List<float>(); var warmOnly = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (!covered[i]) continue;
            float chroma = MathF.Sqrt(A[i] * A[i] + B[i] * B[i]), hue = MathF.Atan2(B[i], A[i]);
            float t = Smooth(6f, 30f, lpB[i]);
            bool warm = chroma > 8f && hue is > 0.75f and < 1.75f && (plateChroma < 12f || HueGap(hue, plateHue) > 0.45f);
            if (warm && L[i] > c1.L + 18f) { float wt = Smooth(c1.L + 18f, c1.L + 32f, L[i]); if (wt > t) { warmOnly[i] = wt - t; t = wt; } }
            float d1 = Distance(L[i], A[i], B[i], c1), d2 = has2 ? Distance(L[i], A[i], B[i], c2) : float.MaxValue;
            float specGate = Smooth(3f * k, 12f * k, spec[i]);
            float p = Smooth(26f, 12f, d1) * specGate, s = has2 ? Smooth(22f, 10f, d2) * specGate : 0f;
            // Chromatic plates: darker, lighter or duller texels of the same hue (shadowed or worn paint) are the same plate whatever their lightness.
            p = MathF.Max(p, HueMatch(hue, chroma, L[i], c1) * Smooth(1f * k, 6f * k, spec[i]));
            if (has2) s = MathF.Max(s, HueMatch(hue, chroma, L[i], c2) * Smooth(1f * k, 6f * k, spec[i]));
            if (has2 && s > 0f && p > 0f) { if (d2 < d1) p *= Math.Clamp(d2 / MathF.Max(d1, 1e-3f), 0f, 1f); else s *= Math.Clamp(d1 / MathF.Max(d2, 1e-3f), 0f, 1f); }
            float rest = MathF.Max(0f, 1f - t);
            p *= rest; s *= rest;
            prim[i] = p; sec[i] = s; tert[i] = t;
            if (p > 0.6f) plateLum.Add(lum[i]);
            else if (s > 0.6f) secLum.Add(lum[i]);
            else if (t > 0.6f) ornLum.Add(lum[i]);
        }

        // A light warm colour covering much of the piece (a bone or white shoulder pad) is a plate colour, not ornaments: it becomes the secondary region.
        int warmCount = 0;
        for (int i = 0; i < n; i++) if (warmOnly[i] > 0.6f) warmCount++;
        if (warmCount > coveredCount * 0.15f)
        {
            ornLum.Clear();
            for (int i = 0; i < n; i++)
            {
                if (warmOnly[i] <= 0f) { if (tert[i] > 0.6f) ornLum.Add(lum[i]); continue; }
                sec[i] += warmOnly[i]; tert[i] -= warmOnly[i];
                if (sec[i] > 0.6f) secLum.Add(lum[i]);
            }
        }

        // Closing: dirt patches, scratches and stains inside a painted plate belong to the plate (they stay as darker or lighter shading); a covered,
        // specular-bearing pixel that is not an ornament and is mostly surrounded (9x9) by one region joins it.
        var nearP = Box(prim, w, h, 4); var nearS = Box(sec, w, h, 4);
        for (int i = 0; i < n; i++)
        {
            if (!covered[i] || tert[i] > 0.3f || spec[i] < 4f * k) continue;
            if (nearP[i] > 0.6f && prim[i] < nearP[i]) { float add = nearP[i] - prim[i]; prim[i] = nearP[i]; if (add > 0.6f) plateLum.Add(lum[i]); }
            else if (nearS[i] > 0.6f && sec[i] < nearS[i]) { float add = nearS[i] - sec[i]; sec[i] = nearS[i]; if (add > 0.6f) secLum.Add(lum[i]); }
            float sum = prim[i] + sec[i] + tert[i];
            if (sum > 1f) { prim[i] /= sum; sec[i] /= sum; tert[i] /= sum; }
        }

        // Same-hue growth (repair attempt 1, ours): shadowed, worn or darker-edged paint of a plate's hue that the colour distance left out (it kept its baked
        // colour and showed through the new paint as blotches) joins the plate when it is connected to the plate's core.
        if (!OldMaskRules)
        {
            GrowSameHue(prim, sec, tert, covered, L, A, B, spec, w, h, c1, has2 ? c2 : null, plateLum, lum);
            if (has2) GrowSameHue(sec, prim, tert, covered, L, A, B, spec, w, h, c2, c1, secLum, lum);
        }

        float plateMedian = Median(plateLum, 60f);
        if (plateMedian < DarkPlateLuminance && !OldDarkPlates) SeparateDarkPlates(prim, tert, covered, lum, spec, w, h, plateMedian, ornLum, k);
        else if (plateMedian < DarkPlateLuminance)
        {
            // Before 2026-11-25 (old-dark-plates): light texels of a black plate are worn bare metal and stay baked. The median is floored at 1: a plate of pure
            // black texels made Smooth divide 0 by 0 (finding 7).
            float med = MathF.Max(plateMedian, 1f);
            for (int i = 0; i < n; i++)
                if (prim[i] > 0f && lum[i] > med * 2.5f)
                {
                    float keep = Smooth(med * 4.5f, med * 2.5f, lum[i]);
                    prim[i] *= keep;
                }
        }
        float secMedian = Median(secLum, plateMedian), ornMedian = Median(ornLum, 90f);

        // Light denoising of the regions (3x3 average) so DXT1 block noise does not speckle the edges.
        prim = Box(prim, w, h); sec = Box(sec, w, h); tert = Box(tert, w, h);
        var mask = new byte[n * 4];
        double sp = 0, ss = 0, st = 0;
        for (int i = 0; i < n; i++)
        {
            float p = prim[i], s = sec[i], t = tert[i], total = p + s + t;
            if (total > 1f) { p /= total; s /= total; t /= total; }
            if (!covered[i]) p = s = t = 0f;
            mask[i * 4] = ToByte(p); mask[i * 4 + 1] = ToByte(s); mask[i * 4 + 2] = ToByte(t); mask[i * 4 + 3] = 0;
            sp += p; ss += s; st += t;
        }
        float cov = Math.Max(1, coveredCount);
        return WithGrey(diffuse, new MaskOnly(w, h, mask, plateMedian, secMedian, ornMedian, (float)(sp / cov), (float)(ss / cov), (float)(st / cov)));
    }

    /// <summary>A built mask without its grey diffuse (what the cache keeps: <see cref="BakedArmourPaint.CacheDirectory"/>).</summary>
    public sealed record MaskOnly(int Width, int Height, byte[] Mask, float PlateMedian, float SecondaryMedian, float OrnamentMedian, float Primary, float Secondary, float Tertiary);

    /// <summary>
    /// The grey paint diffuse of <paramref name="m"/> over <paramref name="diffuse"/>: a painted texel's luminance normalised by its region's median
    /// (<see cref="Grey"/>), mixed with the original pixel by the mask's coverage. Computed from the stored (8-bit) mask, so a built and a cached mask give the
    /// same bytes.
    /// </summary>
    public static BuiltPaintMask WithGrey(DdsImage diffuse, MaskOnly m)
    {
        int n = m.Width * m.Height;
        if (diffuse.Width != m.Width || diffuse.Height != m.Height) throw new ArgumentException("the mask and the diffuse differ in size", nameof(m));
        var outDif = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            byte r = diffuse.Rgba[i * 4], g = diffuse.Rgba[i * 4 + 1], b = diffuse.Rgba[i * 4 + 2];
            float p = m.Mask[i * 4] / 255f, s = m.Mask[i * 4 + 1] / 255f, t = m.Mask[i * 4 + 2] / 255f, total = p + s + t;
            outDif[i * 4 + 3] = 255;
            if (total <= 0f) { outDif[i * 4] = r; outDif[i * 4 + 1] = g; outDif[i * 4 + 2] = b; continue; }
            if (total > 1f) { p /= total; s /= total; t /= total; total = 1f; }
            float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            float grey = (Grey(lum, m.PlateMedian, RegularPlateLuminance) * p + Grey(lum, m.SecondaryMedian, RegularPlateLuminance) * s + Grey(lum, m.OrnamentMedian, RegularOrnamentLuminance) * t) / total;
            outDif[i * 4] = ToByte((r + (grey - r) * total) / 255f);
            outDif[i * 4 + 1] = ToByte((g + (grey - g) * total) / 255f);
            outDif[i * 4 + 2] = ToByte((b + (grey - b) * total) / 255f);
        }
        return new BuiltPaintMask(m.Width, m.Height, m.Mask, outDif, m.Primary, m.Secondary, m.Tertiary)
            { PlateMedian = m.PlateMedian, SecondaryMedian = m.SecondaryMedian, OrnamentMedian = m.OrnamentMedian };
    }

    /// <summary>Plate median luminance (sRGB 0..255) below which a piece counts as black-schemed (Black Templars, Legion of the Damned, Iron Hands, campaign Titus).</summary>
    public const float DarkPlateLuminance = 25f;

    /// <summary>
    /// Restore switch (old-dark-plates): true builds black plates' masks as before 2026-11-25 (every texel 2.5 x the plate median kept baked; shading by luminance
    /// ratio). Test and comparison aid; process-wide, set before pieces load.
    /// </summary>
    public static bool OldDarkPlates
    {
        get => _oldDarkPlates ?? (Environment.GetEnvironmentVariable("SM_OLD_DARK_PLATES") == "1" || Array.IndexOf(Environment.GetCommandLineArgs(), OldDarkPlatesSwitch) >= 0);
        set => _oldDarkPlates = value;
    }
    static bool? _oldDarkPlates;

    /// <summary>The client's command-line form of <see cref="OldDarkPlates"/> (or SM_OLD_DARK_PLATES=1); a look only, so the server needs nothing.</summary>
    public const string OldDarkPlatesSwitch = "--old-dark-plates";

    /// <summary>
    /// Black plates (armour-pieces-1125, third step; ours). In a black texture the plate, its trim and the undersuit differ by lightness and specular, not colour, so
    /// the colour clusters take them all as one plate. Undersuit, rubber and cables (specular well below the plates': under 0.35 to 0.6 of the plates' median) leave
    /// the plate. Texels clearly lighter than the plate (18 to 34 above its median luminance) are either trim or wear: trim when they are as specular as the plate
    /// or more (1 to 1.5 x) and lie in a light band (35 to 60 % of their 7x7 neighbourhood light), then they become the tertiary region and are painted like
    /// regular armour's trim; scratches and chips (isolated light texels: under 35 to 60 % of the neighbourhood light) stay baked, as worn bare metal, and wide
    /// light patches that are not trim stay plate (lighter shading, <see cref="Grey"/>), so the wear is kept without blotches.
    /// </summary>
    /// <param name="specScale">The texture's specular scale (median / 25, 0.2 to 1): on a matte texture (under 0.5) undersuit and plate cannot be told apart by
    /// specular, so the undersuit cut is skipped (repair attempt 1: the Salamanders backpack's matte black plates were all cut as undersuit and stayed baked).</param>
    static void SeparateDarkPlates(float[] prim, float[] tert, bool[] covered, float[] lum, float[] spec, int w, int h, float plateMedian, List<float> ornLum, float specScale = 1f)
    {
        int n = w * h;
        var specs = new List<float>();
        for (int i = 0; i < n; i++) if (covered[i] && prim[i] > 0.6f) specs.Add(spec[i]);
        float plateSpec = MathF.Max(8f, Median(specs, 20f));
        var light = new float[n];
        for (int i = 0; i < n; i++) if (covered[i] && prim[i] > 0f) light[i] = Smooth(plateMedian + 18f, plateMedian + 34f, lum[i]);
        var nearLight = Box(light, w, h, 3);
        for (int i = 0; i < n; i++)
        {
            if (prim[i] <= 0f) continue;
            if (specScale >= 0.5f || OldMaskRules) prim[i] *= 1f - Smooth(plateSpec * 0.6f, plateSpec * 0.35f, spec[i]);
            if (light[i] <= 0f) continue;
            float trim = light[i] * Smooth(0.35f, 0.6f, nearLight[i]) * Smooth(plateSpec, plateSpec * 1.5f, spec[i]);
            float move = prim[i] * trim;
            prim[i] -= move; tert[i] += move;
            if (tert[i] > 0.6f) ornLum.Add(lum[i]);
            // Isolated light texels (scratches, chips) stay baked; wide light patches that are not trim (rubbed, dusty paint) stay plate, lighter shaded.
            prim[i] *= 1f - light[i] * (1f - trim) * Smooth(0.6f, 0.35f, nearLight[i]);
        }
    }

    readonly record struct Centre(float L, float A, float B);

    static (Centre First, Centre Second, bool HasSecond) PlateClusters(List<int> pixels, float[] L, float[] A, float[] B)
    {
        // Histogram mode in (a, b, L/3) bins of 6 units: deterministic seeds.
        var bins = new Dictionary<(int, int, int), int>();
        foreach (int i in pixels)
        {
            var k = ((int)MathF.Floor(A[i] / 6f), (int)MathF.Floor(B[i] / 6f), (int)MathF.Floor(L[i] / 18f));
            bins[k] = bins.GetValueOrDefault(k) + 1;
        }
        var ordered = bins.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2).ThenBy(kv => kv.Key.Item3).ToList();
        // No covered texel (finding 5: a material whose UVs miss its texture, or a black texture): no plate colour, and the mask paints nothing.
        if (ordered.Count == 0) return (new Centre(50f, 0f, 0f), new Centre(50f, 0f, 0f), false);
        Centre FromBin((int, int, int) k) => new(k.Item3 * 18f + 9f, k.Item1 * 6f + 3f, k.Item2 * 6f + 3f);
        var first = FromBin(ordered[0].Key);
        // Dark undersuit, rubber and cables can outnumber the plates on a texture (arms): when the largest bin is dark and colourless and a coloured bin
        // has at least a quarter of its texels, the coloured one is the plate colour (black-schemed sets have no such bin and keep the dark one).
        static bool DarkGrey(Centre c) => c.L < 32f && MathF.Sqrt(c.A * c.A + c.B * c.B) < 9f;
        // Repair attempt 1: gold, brass and bone (warm and light: ornaments, emblems) never stand in for a dark plate (the Salamanders, Titus and Blood Ravens
        // backpacks: their dark plates stayed baked while the emblem took the primary colour).
        static bool WarmLight(float l, float a, float b) => !OldMaskRules && l > 38f && MathF.Atan2(b, a) is > 0.75f and < 1.75f;
        if (DarkGrey(first))
        {
            int coloured = pixels.Count(i => A[i] * A[i] + B[i] * B[i] >= 144f && !WarmLight(L[i], A[i], B[i]));
            if (coloured * 4 >= pixels.Count)
                foreach (var (k, _) in ordered.Skip(1))
                {
                    var c = FromBin(k);
                    if (MathF.Sqrt(c.A * c.A + c.B * c.B) >= 12f && !WarmLight(c.L, c.A, c.B)) { first = c; break; }
                }
        }
        Centre? second = null;
        foreach (var (k, count) in ordered.Skip(1))
        {
            var c = FromBin(k);
            if (Distance(c.L, c.A, c.B, first) > 30f) { if (count * 12 >= ordered[0].Value) second = c; break; }
        }
        // A few k-means refinements (2 or 1 clusters plus an outlier sink).
        for (int iter = 0; iter < 6; iter++)
        {
            double l1 = 0, a1 = 0, b1 = 0, n1 = 0, l2 = 0, a2 = 0, b2 = 0, n2 = 0;
            foreach (int i in pixels)
            {
                float d1 = Distance(L[i], A[i], B[i], first), d2 = second is { } s ? Distance(L[i], A[i], B[i], s) : float.MaxValue;
                if (d1 <= d2 && d1 < 24f) { l1 += L[i]; a1 += A[i]; b1 += B[i]; n1++; }
                else if (d2 < 24f) { l2 += L[i]; a2 += A[i]; b2 += B[i]; n2++; }
            }
            if (n1 > 0) first = new((float)(l1 / n1), (float)(a1 / n1), (float)(b1 / n1));
            if (second is not null)
            {
                if (n2 * 16 < n1) second = null;
                else if (n2 > 0) second = new((float)(l2 / n2), (float)(a2 / n2), (float)(b2 / n2));
            }
        }
        if (second is { } sc && Distance(sc.L, sc.A, sc.B, first) < 20f) second = null;
        // A second plate colour must look like paint, not like the dark undersuit, rubber or worn edges every set has: a distinct hue with some chroma, or a
        // clearly different lightness that is not dark (yellow pads on grey armour, bone on red).
        if (second is { } s2)
        {
            float c2 = MathF.Sqrt(s2.A * s2.A + s2.B * s2.B);
            bool distinctHue = c2 > 10f && HueGap(s2, first) > 0.35f;
            bool lighter = s2.L > 35f && MathF.Abs(s2.L - first.L) > 25f;
            if (!distinctHue && !lighter) second = null;
        }
        return (first, second ?? first, second is not null);
    }

    /// <summary>
    /// How far a texel of <paramref name="hue"/> and <paramref name="chroma"/> is the plate colour <paramref name="c"/> by hue alone (0 for an achromatic plate colour).
    /// The texel must be saturated enough: by absolute chroma, or (repair attempt 1) by its chroma relative to its lightness, so a shadowed or dark-edged texel of the
    /// plate's paint, whose chroma falls with its lightness, still counts.
    /// </summary>
    static float HueMatch(float hue, float chroma, float l, Centre c)
    {
        float cc = MathF.Sqrt(c.A * c.A + c.B * c.B);
        if (cc < 12f) return 0f;
        float gate = Smooth(cc * 0.22f, cc * 0.45f, chroma);
        if (!OldMaskRules)
        {
            float sat = chroma / (l + 12f), plateSat = cc / (c.L + 12f);
            gate = MathF.Max(gate, Smooth(plateSat * 0.25f, plateSat * 0.5f, sat) * Smooth(3f, 6f, chroma));
        }
        return Smooth(0.55f, 0.3f, HueGap(hue, MathF.Atan2(c.B, c.A))) * gate;
    }

    /// <summary>
    /// Grows a chromatic region <paramref name="mine"/> over the texels connected to its core (membership over 0.6) that have its hue (within 0.7 rad, some
    /// saturation), carry specular and are not ornaments, and are nearer its hue than <paramref name="other"/>'s. Such texels take full membership (less the
    /// ornament share), so no baked tint of the plate's own colour is left under the paint. Four-connected flood fill; deterministic.
    /// </summary>
    static void GrowSameHue(float[] mine, float[] theirs, float[] tert, bool[] covered, float[] L, float[] A, float[] B, float[] spec, int w, int h, Centre c, Centre? other,
        List<float> lumList, float[] lum)
    {
        float cc = MathF.Sqrt(c.A * c.A + c.B * c.B);
        if (cc < 12f) return;
        float plateHue = MathF.Atan2(c.B, c.A), plateSat = cc / (c.L + 12f);
        float? otherHue = other is { } o && MathF.Sqrt(o.A * o.A + o.B * o.B) >= 12f ? MathF.Atan2(o.B, o.A) : null;
        int n = w * h;
        bool Candidate(int i)
        {
            if (!covered[i] || tert[i] > 0.3f || spec[i] < 2f) return false;
            float chroma = MathF.Sqrt(A[i] * A[i] + B[i] * B[i]);
            if (chroma < 4f || chroma / (L[i] + 12f) < plateSat * 0.2f) return false;
            float hue = MathF.Atan2(B[i], A[i]), gap = HueGap(hue, plateHue);
            return gap < 0.7f && (otherHue is not { } oh || gap < HueGap(hue, oh));
        }
        var seen = new bool[n];
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++) if (mine[i] > 0.6f && Candidate(i)) { seen[i] = true; queue.Enqueue(i); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            float full = MathF.Max(0f, 1f - tert[i]);
            if (mine[i] < full)
            {
                if (mine[i] <= 0.6f) lumList.Add(lum[i]);
                mine[i] = full; theirs[i] = 0f;
            }
            int x = i % w, y = i / w;
            if (x > 0 && !seen[i - 1] && Candidate(i - 1)) { seen[i - 1] = true; queue.Enqueue(i - 1); }
            if (x < w - 1 && !seen[i + 1] && Candidate(i + 1)) { seen[i + 1] = true; queue.Enqueue(i + 1); }
            if (y > 0 && !seen[i - w] && Candidate(i - w)) { seen[i - w] = true; queue.Enqueue(i - w); }
            if (y < h - 1 && !seen[i + w] && Candidate(i + w)) { seen[i + w] = true; queue.Enqueue(i + w); }
        }
    }

    /// <summary>
    /// Restore switch (old-mask-rules, repair attempt 1): true builds masks as at 1257b950: no relative-saturation hue match, no same-hue growth, cloth materials
    /// painted. Test and comparison aid (<c>--old-mask-rules</c> or SM_OLD_MASK_RULES=1).
    /// </summary>
    public static bool OldMaskRules
    {
        get => _oldMaskRules ?? (Environment.GetEnvironmentVariable("SM_OLD_MASK_RULES") == "1" || Array.IndexOf(Environment.GetCommandLineArgs(), "--old-mask-rules") >= 0);
        set => _oldMaskRules = value;
    }
    static bool? _oldMaskRules;

    /// <summary>The angle between two colours' hues in the (a*, b*) plane, 0..pi.</summary>
    static float HueGap(Centre x, Centre y) => HueGap(MathF.Atan2(x.B, x.A), MathF.Atan2(y.B, y.A));
    static float HueGap(float h1, float h2)
    {
        float d = MathF.Abs(h1 - h2) % (2f * MathF.PI);
        return d > MathF.PI ? 2f * MathF.PI - d : d;
    }

    static float Distance(float l, float a, float b, Centre c)
    {
        float dl = (l - c.L) * 0.35f, da = a - c.A, db = b - c.B;
        return MathF.Sqrt(dl * dl + da * da + db * db);
    }

    static float Grey(float lum, float regionMedian, float target)
    {
        // Black regions (armour-pieces-1125 third step): their detail is a few levels of a near-black texture, so a ratio to the median turns block noise into
        // blotches; the difference to the median is kept instead, as if the plate were 40 levels bright (dirt and scratches stay visible, noise stays small).
        if (regionMedian < DarkPlateLuminance && !OldDarkPlates) return Math.Clamp(target * (1f + (lum - regionMedian) / 40f), target * 0.6f, target * 1.5f);
        float ratio = lum / MathF.Max(regionMedian, 1f);
        // Dark regions carry more compression noise relative to their signal: compress the ratio toward 1.
        float gamma = regionMedian < 25f ? 0.3f : regionMedian < 40f ? 0.7f : 1f;
        ratio = MathF.Pow(Math.Clamp(ratio, 0.05f, 3f), gamma);
        if (regionMedian < 25f) ratio = Math.Clamp(ratio, 0.55f, 1.35f);
        return Math.Clamp(target * ratio, 0f, 255f);
    }

    static float Median(List<float> values, float fallback)
    {
        if (values.Count == 0) return fallback;
        values.Sort();
        return values[values.Count / 2];
    }

    static float[] Box(float[] v, int w, int h, int radius = 1)
    {
        var o = new float[v.Length];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float s = 0; int c = 0;
            for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                s += v[yy * w + xx]; c++;
            }
            o[y * w + x] = s / c;
        }
        return o;
    }

    static float Sample(DdsImage img, int x, int y, int w, int h, int channel)
    {
        int sx = Math.Min(img.Width - 1, x * img.Width / w), sy = Math.Min(img.Height - 1, y * img.Height / h);
        int o = (sy * img.Width + sx) * 4;
        return channel < 0 ? (img.Rgba[o] + img.Rgba[o + 1] + img.Rgba[o + 2]) / 3f : img.Rgba[o + channel];
    }

    static float Smooth(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    static float Lin(byte c)
    {
        float v = c / 255f;
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    static (float L, float A, float B) Lab(byte r8, byte g8, byte b8)
    {
        float r = Lin(r8), g = Lin(g8), b = Lin(b8);
        float x = (0.4124f * r + 0.3576f * g + 0.1805f * b) / 0.95047f, y = 0.2126f * r + 0.7152f * g + 0.0722f * b, z = (0.0193f * r + 0.1192f * g + 0.9505f * b) / 1.08883f;
        static float F(float t) => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;
        float fx = F(x), fy = F(y), fz = F(z);
        return (116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
    }
}
