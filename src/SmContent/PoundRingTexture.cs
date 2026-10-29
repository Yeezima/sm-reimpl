namespace SmContent;

/// <summary>
/// The pixels of the thin dark ring at the ground pound's blast radius (M41; **ours**: the install has no ring or shockwave decal, and its crack and crater decals mark damage, not reach).
/// A procedural scorch band (a narrow broken core with a soft, faint scorch beside it, brownish black, never bright) on a square of side radius * 2 + 2 m, RGBA8. The band is centred on
/// the radius, where the blast's fall-off ends (<c>fall_off</c> 7 m: intensity 0.05), so it shows where the hit reach was. Engine-free, so that it can be made on a worker thread and tested.
/// </summary>
public static class PoundRingTexture
{
    public const int Pixels = 768;
    /// <summary>Margin beyond the radius (m) the square covers, and the radii the ring is made for (m; others are clamped).</summary>
    public const float Margin = 1.0f, MinRadius = 1f, MaxRadius = 30f;
    const float CoreSigma = 0.09f, ScorchSigma = 0.5f, PeakAlpha = 0.78f;
    /// <summary>The largest alpha any pixel gets, and the largest colour component (0..1): a dark ring, never bright.</summary>
    public const float MaxAlpha = 0.85f, MaxShade = 0.09f;

    /// <summary>The radius the ring is made for: the blast's radius clamped to 1 to 30 m; NaN and infinities give 0 (no ring).</summary>
    public static float ClampRadius(float radius) => float.IsFinite(radius) && radius > 0f ? Math.Clamp(radius, MinRadius, MaxRadius) : 0f;

    /// <summary>The square's side (m) for a (clamped) blast radius.</summary>
    public static float SizeFor(float radius) => 2f * (radius + Margin);

    /// <summary>The ring's pixels (RGBA8, <see cref="Pixels"/> squared, row 0 at the top) for a blast radius (clamped; a radius that is no number above 0 gives an empty array).</summary>
    public static byte[] Render(float radius)
    {
        radius = ClampRadius(radius);
        if (radius <= 0f) return [];
        float size = SizeFor(radius), half = size / 2f;
        var data = new byte[Pixels * Pixels * 4];
        for (int py = 0; py < Pixels; py++)
        {
            float y = ((py + 0.5f) / Pixels) * size - half;
            for (int px = 0; px < Pixels; px++)
            {
                float x = ((px + 0.5f) / Pixels) * size - half;
                float r = MathF.Sqrt(x * x + y * y), d = r - radius;
                int o = (py * Pixels + px) * 4;
                if (MathF.Abs(d) > 2.2f) continue;   // transparent (the array starts at 0)
                float coarse = Fbm(x * 1.6f, y * 1.6f, 3, 11), fine = Fbm(x * 7f, y * 7f, 3, 29);
                // The narrow core, broken up into patches and worn at the edges, and a faint wider scorch (stronger inside, where the blast was).
                float core = MathF.Exp(-(d * d) / (CoreSigma * CoreSigma)) * Smooth(0.18f, 0.55f, coarse + 0.25f * (fine - 0.5f)) * (0.7f + 0.5f * fine);
                float scorch = MathF.Exp(-(d * d) / (ScorchSigma * ScorchSigma)) * (d < 0f ? 0.32f : 0.16f) * (0.55f + 0.9f * fine) * Smooth(0.2f, 0.6f, coarse);
                float alpha = Math.Clamp((core * 0.9f + scorch) * PeakAlpha, 0f, MaxAlpha);
                // Brownish black with a little grain: no bright colour.
                float shade = 0.035f + 0.05f * fine;
                data[o] = (byte)(255f * (shade * 1.05f)); data[o + 1] = (byte)(255f * shade); data[o + 2] = (byte)(255f * shade * 0.9f); data[o + 3] = (byte)(255f * alpha);
            }
        }
        return data;
    }

    static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0f, 1f); return t * t * (3f - 2f * t); }

    static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777216f;
        }
    }

    static float Value(float x, float y, int seed)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float fx = x - xi, fy = y - yi;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
        float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed), c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
        return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fy;
    }

    static float Fbm(float x, float y, int octaves, int seed)
    {
        float sum = 0f, amp = 0.5f, norm = 0f;
        for (int i = 0; i < octaves; i++) { sum += amp * Value(x, y, seed + i * 17); norm += amp; x *= 2.03f; y *= 2.03f; amp *= 0.5f; }
        return sum / norm;
    }
}
