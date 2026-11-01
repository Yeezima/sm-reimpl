using System;
using Godot;

namespace SmClient;

/// <summary>
/// The gore's textures, made in code once (no file is read, nothing of the install or of any reference is stored): an atlas of ink shapes (the trailer's black-red splatter: irregular, jagged,
/// stringy, with holes, tendrils and satellite droplets), an atlas of soft cloud puffs for the mist, and a tiling noise for the ground pools' ragged edges. Deterministic. Ours (kind: ours).
/// </summary>
public static class GoreTextures
{
    const int Cell = 128;

    // value noise on a lattice that wraps every `period` cells
    sealed class Noise
    {
        readonly float[] _v;
        readonly int _n;
        public Noise(int n, int seed)
        {
            _n = n; _v = new float[n * n];
            var r = new Random(seed);
            for (int i = 0; i < _v.Length; i++) _v[i] = r.NextSingle();
        }
        public float At(float x, float y)
        {
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
            int a = ((x0 % _n) + _n) % _n, b = (a + 1) % _n, c = ((y0 % _n) + _n) % _n, d = (c + 1) % _n;
            float top = _v[c * _n + a] + (_v[c * _n + b] - _v[c * _n + a]) * fx;
            float bot = _v[d * _n + a] + (_v[d * _n + b] - _v[d * _n + a]) * fx;
            return top + (bot - top) * fy;
        }
        public float Fbm(float x, float y, int octaves)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            for (int o = 0; o < octaves; o++) { sum += amp * At(x, y); norm += amp; x *= 2f; y *= 2f; amp *= 0.5f; }
            return sum / norm;
        }
    }

    static ImageTexture Make(byte[] pixels, int size)
    {
        var image = Image.CreateFromData(size, size, false, Image.Format.L8, pixels);
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    static float Edge(float inside, float width) => Math.Clamp(inside / width * 0.5f + 0.5f, 0f, 1f);

    /// <summary>4 x 4 cells of 128 px: 0 to 5 splats, 6 to 9 streaks, 10 to 13 jagged shards, 14 and 15 round droplets. The value is the thickness (1 thick, 0 none).</summary>
    public static ImageTexture Ink()
    {
        const int cols = 4, size = Cell * cols;
        var px = new byte[size * size];
        var noise = new Noise(64, 5);
        for (int c = 0; c < 16; c++)
        {
            var rng = new Random(1000 + c * 37);
            var cell = c < 6 ? Splat(rng, noise, c) : c < 10 ? Streak(rng, noise, c) : c < 14 ? Shard(rng, noise, c) : Dot(rng, noise, c);
            int ox = c % cols * Cell, oy = c / cols * Cell;
            for (int y = 0; y < Cell; y++)
                for (int x = 0; x < Cell; x++) px[(oy + y) * size + ox + x] = (byte)Math.Clamp((int)(cell[y * Cell + x] * 255f + 0.5f), 0, 255);
        }
        return Make(px, size);
    }

    static float R(Random r, float a, float b) => a + (b - a) * r.NextSingle();

    // Cell coordinates: x, y in [-1, 1].
    static float[] Raster(Func<float, float, float> f)
    {
        var o = new float[Cell * Cell];
        for (int y = 0; y < Cell; y++)
            for (int x = 0; x < Cell; x++)
            {
                float u = (x + 0.5f) / Cell * 2f - 1f, v = (y + 0.5f) / Cell * 2f - 1f;
                // keep a margin of 6 % so mip levels never bleed into the next cell
                o[y * Cell + x] = MathF.Abs(u) > 0.94f || MathF.Abs(v) > 0.94f ? 0f : f(u, v);
            }
        return o;
    }

    static float Dist(float x, float y, float cx, float cy) => MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));

    /// <summary>A splat: a core with irregular lobes, thin tapering tendrils (some ending in a bead), satellite droplets and a few holes.</summary>
    static float[] Splat(Random rng, Noise noise, int index)
    {
        float core = R(rng, 0.17f, 0.3f);
        int lobes = 3;
        var lk = new float[lobes]; var la = new float[lobes]; var lp = new float[lobes];
        for (int i = 0; i < lobes; i++) { lk[i] = 2 + i * 2 + rng.Next(2); la[i] = R(rng, 0.12f, 0.3f) / (1 + i * 0.6f); lp[i] = R(rng, 0, MathF.Tau); }
        int tn = 6 + rng.Next(6);
        var ta = new float[tn]; var tl = new float[tn]; var tw = new float[tn]; var bead = new bool[tn];
        for (int i = 0; i < tn; i++) { ta[i] = R(rng, -MathF.PI, MathF.PI); tl[i] = R(rng, 0.45f, 0.88f); tw[i] = R(rng, 0.05f, 0.17f); bead[i] = rng.NextSingle() < 0.55f; }
        int dn = 4 + rng.Next(5);
        var dx = new float[dn]; var dy = new float[dn]; var dr = new float[dn];
        for (int i = 0; i < dn; i++) { float a = R(rng, -MathF.PI, MathF.PI), d = R(rng, 0.5f, 0.88f); dx[i] = MathF.Cos(a) * d; dy[i] = MathF.Sin(a) * d; dr[i] = R(rng, 0.018f, 0.055f); }
        int hn = 2 + rng.Next(3);
        var hx = new float[hn]; var hy = new float[hn]; var hr = new float[hn];
        for (int i = 0; i < hn; i++) { float a = R(rng, -MathF.PI, MathF.PI), d = R(rng, 0.0f, core * 0.7f); hx[i] = MathF.Cos(a) * d; hy[i] = MathF.Sin(a) * d; hr[i] = R(rng, 0.025f, 0.07f); }
        float ox = index * 7.3f;
        return Raster((x, y) =>
        {
            float r = MathF.Sqrt(x * x + y * y), th = MathF.Atan2(y, x);
            float rim = core;
            for (int i = 0; i < lobes; i++) rim *= 1f + la[i] * MathF.Sin(lk[i] * th + lp[i]);
            for (int i = 0; i < tn; i++)
            {
                float d = MathF.Abs(MathF.IEEERemainder(th - ta[i], MathF.Tau));
                if (d < tw[i]) rim = MathF.Max(rim, tl[i] * MathF.Pow(1f - d / tw[i], 1.6f));
            }
            rim += (noise.Fbm(x * 9f + ox, y * 9f, 3) - 0.5f) * 0.1f * (0.4f + r);   // ragged
            float a = Edge(rim - r, 0.035f);
            for (int i = 0; i < tn; i++) if (bead[i]) a = MathF.Max(a, Edge(0.035f - Dist(x, y, MathF.Cos(ta[i]) * (tl[i] + 0.06f), MathF.Sin(ta[i]) * (tl[i] + 0.06f)), 0.035f));
            for (int i = 0; i < dn; i++) a = MathF.Max(a, Edge(dr[i] - Dist(x, y, dx[i], dy[i]), 0.035f));
            for (int i = 0; i < hn; i++) a *= Edge(Dist(x, y, hx[i], hy[i]) - hr[i], 0.04f);
            return a;
        });
    }

    /// <summary>A streak along x: a tapering lens with jagged spikes off both sides, a few holes and droplets trailing along it.</summary>
    static float[] Streak(Random rng, Noise noise, int index)
    {
        float half = R(rng, 0.8f, 0.9f), thick = R(rng, 0.05f, 0.11f);
        int sn = 7 + rng.Next(6);
        var su = new float[sn]; var ss = new float[sn]; var sl = new float[sn]; var sw = new float[sn];
        for (int i = 0; i < sn; i++) { su[i] = R(rng, -0.7f, 0.7f); ss[i] = rng.Next(2) == 0 ? 1f : -1f; sl[i] = R(rng, 0.07f, 0.3f); sw[i] = R(rng, 0.035f, 0.1f); }
        int dn = 3 + rng.Next(4);
        var dx = new float[dn]; var dy = new float[dn]; var dr = new float[dn];
        for (int i = 0; i < dn; i++) { dx[i] = R(rng, -0.85f, 0.85f); dy[i] = R(rng, -0.3f, 0.3f); dr[i] = R(rng, 0.015f, 0.045f); }
        int hn = 2 + rng.Next(2);
        var hx = new float[hn]; var hr = new float[hn];
        for (int i = 0; i < hn; i++) { hx[i] = R(rng, -0.5f, 0.5f); hr[i] = R(rng, 0.02f, 0.04f); }
        float bend = R(rng, -0.12f, 0.12f), ox = index * 5.1f;
        return Raster((x, y) =>
        {
            float v = y - bend * (1f - (x / half) * (x / half));
            float t = thick * MathF.Pow(MathF.Max(0f, 1f - MathF.Abs(x) / half), 0.7f) * (0.7f + 0.6f * noise.Fbm(x * 7f + ox, 3f, 3));
            float inside = t - MathF.Abs(v);
            for (int i = 0; i < sn; i++)
            {
                float d = MathF.Abs(x - su[i]);
                if (d < sw[i] && ss[i] * v > 0f) inside = MathF.Max(inside, t + sl[i] * MathF.Pow(1f - d / sw[i], 1.3f) - MathF.Abs(v));
            }
            float a = Edge(inside, 0.03f);
            for (int i = 0; i < dn; i++) a = MathF.Max(a, Edge(dr[i] - Dist(x, y, dx[i], dy[i]), 0.03f));
            for (int i = 0; i < hn; i++) a *= Edge(Dist(x, y, hx[i], bend) - hr[i], 0.03f);
            return a;
        });
    }

    /// <summary>A jagged shard: an angular outline of random radii with a few sharp spikes.</summary>
    static float[] Shard(Random rng, Noise noise, int index)
    {
        int n = 7 + rng.Next(3);
        var radii = new float[n]; var angles = new float[n];
        for (int i = 0; i < n; i++) { angles[i] = (i + R(rng, -0.3f, 0.3f)) / n * MathF.Tau - MathF.PI; radii[i] = (i % 3 == 0 ? R(rng, 0.5f, 0.85f) : R(rng, 0.2f, 0.4f)); }
        int dn = 2 + rng.Next(3);
        var dx = new float[dn]; var dy = new float[dn]; var dr = new float[dn];
        for (int i = 0; i < dn; i++) { float a = R(rng, -MathF.PI, MathF.PI), d = R(rng, 0.55f, 0.85f); dx[i] = MathF.Cos(a) * d; dy[i] = MathF.Sin(a) * d; dr[i] = R(rng, 0.02f, 0.045f); }
        float ox = index * 3.7f;
        return Raster((x, y) =>
        {
            float r = MathF.Sqrt(x * x + y * y), th = MathF.Atan2(y, x);
            int k = 0;
            for (int i = 0; i < n; i++) if (angles[i] <= th) k = i;
            int k2 = (k + 1) % n;
            float a0 = angles[k], a1 = angles[k2] + (k2 == 0 ? MathF.Tau : 0f);
            float tt = a1 > a0 ? Math.Clamp((th - a0) / (a1 - a0), 0f, 1f) : 0f;
            float rim = radii[k] + (radii[k2] - radii[k]) * tt;
            rim += (noise.Fbm(x * 10f + ox, y * 10f, 3) - 0.5f) * 0.12f;
            float a = Edge(rim - r, 0.03f);
            for (int i = 0; i < dn; i++) a = MathF.Max(a, Edge(dr[i] - Dist(x, y, dx[i], dy[i]), 0.03f));
            return a;
        });
    }

    /// <summary>A small round droplet with a slightly uneven edge.</summary>
    static float[] Dot(Random rng, Noise noise, int index)
    {
        float rad = R(rng, 0.36f, 0.44f), ox = index * 2.9f;
        return Raster((x, y) =>
        {
            float r = MathF.Sqrt(x * x + y * y);
            float rim = rad * (1f + (noise.Fbm(x * 4f + ox, y * 4f, 2) - 0.5f) * 0.3f);
            return Edge(rim - r, 0.06f) * (0.75f + 0.25f * Math.Clamp(1f - r / rad, 0f, 1f));
        });
    }

    /// <summary>2 x 2 soft cloud puffs of 256 px: billowing, blotchy and soft at the edge (value is the density).</summary>
    public static ImageTexture Cloud()
    {
        const int cell = 256, size = cell * 2;
        var px = new byte[size * size];
        var noise = new Noise(32, 21);
        for (int c = 0; c < 4; c++)
        {
            float ox = c * 9.7f, oy = c * 4.3f;
            for (int y = 0; y < cell; y++)
                for (int x = 0; x < cell; x++)
                {
                    float u = (x + 0.5f) / cell * 2f - 1f, v = (y + 0.5f) / cell * 2f - 1f;
                    float r = MathF.Sqrt(u * u + v * v);
                    // domain warp for the billowing, then a dense heart falling off to nothing before the cell's edge
                    float wx = noise.Fbm(u * 1.6f + ox, v * 1.6f + oy, 3) - 0.5f, wy = noise.Fbm(u * 1.6f + oy + 5f, v * 1.6f + ox + 3f, 3) - 0.5f;
                    float n = noise.Fbm((u + wx * 0.9f) * 2.6f + ox, (v + wy * 0.9f) * 2.6f + oy, 5);
                    float radial = Math.Clamp(1f - r / 0.95f, 0f, 1f);
                    radial = radial * radial * (3f - 2f * radial);
                    float a = Math.Clamp((n - 0.4f) * 2.3f, 0f, 1f) * MathF.Pow(radial, 0.7f) * (0.55f + 0.45f * noise.Fbm(u * 5f + oy, v * 5f + ox, 2));
                    px[((c / 2) * cell + y) * size + (c % 2) * cell + x] = (byte)(a * 255f + 0.5f);
                }
        }
        return Make(px, size);
    }

    /// <summary>A tiling 256 px noise (fractal value noise) for the pools' ragged edges.</summary>
    public static ImageTexture Ground()
    {
        const int size = 256;
        var px = new byte[size * size];
        var noise = new Noise(8, 77);
        var fine = new Noise(32, 78);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size, v = y / (float)size;
                float n = 0.6f * noise.At(u * 8f, v * 8f) + 0.3f * fine.At(u * 32f, v * 32f) + 0.1f * fine.At(u * 32f + 0.5f, v * 32f + 0.5f);
                px[y * size + x] = (byte)(Math.Clamp(n, 0f, 1f) * 255f + 0.5f);
            }
        return Make(px, size);
    }
}
