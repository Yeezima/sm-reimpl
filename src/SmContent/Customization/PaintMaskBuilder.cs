using SmFormats.Dds;

namespace SmContent.Customization;

/// <summary>
/// What <see cref="PaintMaskBuilder.Build"/> made from a baked armour texture: a paint mask in the game's layout (R primary, G secondary, B tertiary,
/// A dirt = 0) and a matching diffuse whose painted regions are neutral grey shading (the original pixels elsewhere), both RGBA8 at the diffuse's size.
/// </summary>
/// <param name="Primary">Share of the texture's covered pixels in each region (0..1), for logs and tests.</param>
public sealed record BuiltPaintMask(int Width, int Height, byte[] Mask, byte[] Diffuse, float Primary, float Secondary, float Tertiary);

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
    public static BuiltPaintMask Build(DdsImage diffuse, DdsImage? specular, DdsImage? lightProp)
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
        for (int i = 0; i < n; i++) if (lum[i] > 4f || spec[i] > 4f) { covered[i] = true; coveredCount++; }

        // Plate clusters: specular-bearing, not ornament-marked pixels.
        var candidates = new List<int>();
        for (int i = 0; i < n; i++) if (covered[i] && spec[i] > 10f && lpB[i] < 8f) candidates.Add(i);
        if (candidates.Count < 16) for (int i = 0; i < n; i++) if (covered[i]) candidates.Add(i);
        var (c1, c2, has2) = PlateClusters(candidates, L, A, B);

        // Warm, light ornaments (bone, gold, brass) when the plates are not themselves warm and light.
        float plateHue = MathF.Atan2(c1.B, c1.A), plateChroma = MathF.Sqrt(c1.A * c1.A + c1.B * c1.B);

        var tert = new float[n]; var prim = new float[n]; var sec = new float[n];
        var plateLum = new List<float>(); var ornLum = new List<float>();
        for (int i = 0; i < n; i++)
        {
            if (!covered[i]) continue;
            float chroma = MathF.Sqrt(A[i] * A[i] + B[i] * B[i]), hue = MathF.Atan2(B[i], A[i]);
            float t = Smooth(6f, 30f, lpB[i]);
            bool warm = chroma > 8f && hue is > 0.75f and < 1.75f && (plateChroma < 12f || HueGap(hue, plateHue) > 0.45f);
            if (warm && L[i] > c1.L + 18f) t = MathF.Max(t, Smooth(c1.L + 18f, c1.L + 32f, L[i]));
            float d1 = Distance(L[i], A[i], B[i], c1), d2 = has2 ? Distance(L[i], A[i], B[i], c2) : float.MaxValue;
            float specGate = Smooth(3f, 12f, spec[i]);
            float p = Smooth(26f, 12f, d1) * specGate, s = has2 ? Smooth(22f, 10f, d2) * specGate : 0f;
            if (has2 && s > 0f && p > 0f) { if (d2 < d1) p *= Math.Clamp(d2 / MathF.Max(d1, 1e-3f), 0f, 1f); else s *= Math.Clamp(d1 / MathF.Max(d2, 1e-3f), 0f, 1f); }
            float rest = MathF.Max(0f, 1f - t);
            p *= rest; s *= rest;
            prim[i] = p; sec[i] = s; tert[i] = t;
            if (p + s > 0.6f) plateLum.Add(lum[i]);
            else if (t > 0.6f) ornLum.Add(lum[i]);
        }

        float plateMedian = Median(plateLum, 60f), ornMedian = Median(ornLum, 90f);
        // Dark (black) plates: their light scratches are worn bare metal, not paint; keep them as baked so a painted plate does not blotch.
        if (plateMedian < 25f)
            for (int i = 0; i < n; i++)
                if (prim[i] + sec[i] > 0f && lum[i] > plateMedian * 2.5f)
                {
                    float keep = Smooth(plateMedian * 4.5f, plateMedian * 2.5f, lum[i]);
                    prim[i] *= keep; sec[i] *= keep;
                }

        // Light denoising of the regions (3x3 average) so DXT1 block noise does not speckle the edges.
        prim = Box(prim, w, h); sec = Box(sec, w, h); tert = Box(tert, w, h);
        var mask = new byte[n * 4]; var outDif = new byte[n * 4];
        double sp = 0, ss = 0, st = 0;
        for (int i = 0; i < n; i++)
        {
            float p = prim[i], s = sec[i], t = tert[i], total = p + s + t;
            if (total > 1f) { p /= total; s /= total; t /= total; total = 1f; }
            if (!covered[i]) { p = s = t = 0f; total = 0f; }
            mask[i * 4] = ToByte(p); mask[i * 4 + 1] = ToByte(s); mask[i * 4 + 2] = ToByte(t); mask[i * 4 + 3] = 0;
            sp += p; ss += s; st += t;
            byte r = diffuse.Rgba[i * 4], g = diffuse.Rgba[i * 4 + 1], b = diffuse.Rgba[i * 4 + 2];
            if (total <= 0f) { outDif[i * 4] = r; outDif[i * 4 + 1] = g; outDif[i * 4 + 2] = b; outDif[i * 4 + 3] = 255; continue; }
            float plateGrey = Grey(lum[i], plateMedian, RegularPlateLuminance), ornGrey = Grey(lum[i], ornMedian, RegularOrnamentLuminance);
            float grey = (plateGrey * (p + s) + ornGrey * t) / total;
            outDif[i * 4] = ToByte((r + (grey - r) * total) / 255f);
            outDif[i * 4 + 1] = ToByte((g + (grey - g) * total) / 255f);
            outDif[i * 4 + 2] = ToByte((b + (grey - b) * total) / 255f);
            outDif[i * 4 + 3] = 255;
        }
        float cov = Math.Max(1, coveredCount);
        return new BuiltPaintMask(w, h, mask, outDif, (float)(sp / cov), (float)(ss / cov), (float)(st / cov));
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
        Centre FromBin((int, int, int) k) => new(k.Item3 * 18f + 9f, k.Item1 * 6f + 3f, k.Item2 * 6f + 3f);
        var first = FromBin(ordered[0].Key);
        Centre? second = null;
        foreach (var (k, count) in ordered.Skip(1))
        {
            var c = FromBin(k);
            if (Distance(c.L, c.A, c.B, first) > 30f) { if (count * 6 >= ordered[0].Value) second = c; break; }
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
                if (n2 * 8 < n1) second = null;
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

    static float[] Box(float[] v, int w, int h)
    {
        var o = new float[v.Length];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float s = 0; int c = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
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
