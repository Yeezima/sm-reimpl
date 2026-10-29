namespace SmContent;

/// <summary>
/// feel/jump-pack (**ours**; the owner, 2026-10-29: "add a hit effect that shows roughly where the attack hitbox landed similar to the ground pound landing"): the pixels of
/// the sword slash's ground mark. The install has no decal for it, so it is procedural like the pound's ring (<see cref="PoundRingTexture"/>): a wedge of the slash's
/// hit cone (half-angle and reach given: the world's melee cone, 60 degrees and 4 m on the install) with the apex at the square's centre and the cone toward the top of the
/// image (row 0), filled with a faint scorch that thins with the distance, three curved sword scrapes across it and a darker rim at the reach. Brownish black, never bright.
/// Engine-free (a worker thread makes it; tested).
/// </summary>
public static class SlashMarkTexture
{
    public const int Pixels = 512;
    /// <summary>Margin beyond the reach (m) the square covers.</summary>
    public const float Margin = 0.5f;
    /// <summary>The largest alpha and colour component (0..1): a dark mark, never bright.</summary>
    public const float MaxAlpha = 0.85f, MaxShade = 0.09f;

    /// <summary>The square's side (m) for a reach (clamped to 1 to 10 m).</summary>
    public static float SizeFor(float reach) => 2f * (Math.Clamp(float.IsFinite(reach) ? reach : 4f, 1f, 10f) + Margin);

    /// <summary>The mark's pixels (RGBA8, <see cref="Pixels"/> squared, row 0 at the top = the cone's heading) for a reach (m) and a half-angle (degrees).</summary>
    public static byte[] Render(float reach, float halfAngleDegrees)
    {
        reach = Math.Clamp(float.IsFinite(reach) ? reach : 4f, 1f, 10f);
        float half = Math.Clamp(float.IsFinite(halfAngleDegrees) ? halfAngleDegrees : 60f, 5f, 90f) * MathF.PI / 180f;
        float size = SizeFor(reach), centre = size / 2f;
        var data = new byte[Pixels * Pixels * 4];
        for (int py = 0; py < Pixels; py++)
        {
            float y = ((py + 0.5f) / Pixels) * size - centre;
            for (int px = 0; px < Pixels; px++)
            {
                float x = ((px + 0.5f) / Pixels) * size - centre;
                float ahead = -y, r = MathF.Sqrt(x * x + y * y);
                if (r < 0.25f || r > reach + 0.35f) continue;
                float angle = MathF.Atan2(x, ahead);   // 0 along the heading
                float edge = half - MathF.Abs(angle);   // radians inside the cone's side
                if (edge < -0.12f) continue;
                float side = Smooth(-0.12f, 0.1f, edge);
                float grain = Noise(x * 6f, y * 6f), coarse = Noise(x * 1.3f + 7f, y * 1.3f - 3f);
                // A faint scorch, strongest near the attacker, thinning toward the reach.
                float fill = 0.28f * (1f - Smooth(0.4f * reach, reach + 0.2f, r)) * (0.6f + 0.8f * coarse);
                // Three scrapes: arcs across the cone at 45, 70 and 92 % of the reach, sweeping with the blade (narrow, broken).
                float scrape = 0f;
                foreach (var (at, width) in new[] { (0.45f, 0.06f), (0.7f, 0.05f), (0.92f, 0.07f) })
                {
                    float d = r - at * reach - 0.12f * MathF.Sin(angle * 2.3f + at * 5f);
                    scrape = MathF.Max(scrape, MathF.Exp(-(d * d) / (width * width)) * Smooth(0.25f, 0.6f, grain + 0.3f * coarse));
                }
                // A darker rim at the reach, where the hit ends.
                float rd = r - reach;
                float rim = MathF.Exp(-(rd * rd) / (0.09f * 0.09f)) * 0.7f * Smooth(0.2f, 0.55f, coarse + 0.2f * grain);
                float alpha = Math.Clamp((fill + scrape * 0.85f + rim) * side, 0f, MaxAlpha);
                if (alpha <= 0.004f) continue;
                float shade = Math.Min(MaxShade, 0.03f + 0.05f * grain);
                int o = (py * Pixels + px) * 4;
                data[o] = (byte)(255f * Math.Min(MaxShade, shade * 1.08f)); data[o + 1] = (byte)(255f * shade); data[o + 2] = (byte)(255f * shade * 0.85f); data[o + 3] = (byte)(255f * alpha);
            }
        }
        return data;
    }

    static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0f, 1f); return t * t * (3f - 2f * t); }

    static float Hash(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + 977);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777216f;
        }
    }

    static float Noise(float x, float y)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float fx = x - xi, fy = y - yi;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
        float a = Hash(xi, yi), b = Hash(xi + 1, yi), c = Hash(xi, yi + 1), d = Hash(xi + 1, yi + 1);
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }
}
