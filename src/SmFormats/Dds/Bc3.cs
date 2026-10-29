namespace SmFormats.Dds;

/// <summary>
/// BC3 (DXT5) block codec: 16 bytes per 4x4 pixel block, row-major block order, RGBA8 pixels in and out. Independent
/// implementation written from the public format description (the D3D BC3 layout: an 8-byte alpha block of two endpoints and
/// sixteen 3-bit indices, then an 8-byte colour block of two RGB565 endpoints and sixteen 2-bit indices). No code from other
/// encoders is used. Deterministic: no randomness, no threads, fixed iteration counts.
/// <para>
/// Encoder: colour endpoints start from the principal axis of the block's colours (power iteration on the covariance) and from the
/// bounding-box diagonal, then are refined a few times by assigning every pixel to the nearest of the four palette colours and
/// re-fitting the endpoints by least squares, keeping the lowest squared-error result; flat-colour blocks use an exhaustive
/// RGB565 table so that colours the format can hold are reproduced exactly. Alpha tries the 8-value mode (endpoints = min/max,
/// refined the same way) and the 6-value mode with the extra entries 0 and 255 (endpoints from the pixels that are neither),
/// and keeps the lower error. The colour block is always written with the first endpoint greater than the second, and the
/// decoder always reads four colours (BC3 has no 1-bit-alpha colour mode), so both readings of the block agree.
/// </para>
/// </summary>
public static class Bc3
{
    /// <summary>Bytes per 4x4 block.</summary>
    public const int BlockBytes = 16;

    /// <summary>The encoded size of a <paramref name="width"/> x <paramref name="height"/> image (128 x 128: 16,384 bytes).</summary>
    public static int EncodedLength(int width, int height)
    {
        CheckSize(width, height);
        return checked(width / 4 * (height / 4) * BlockBytes);
    }

    static void CheckSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width & 3) != 0 || (height & 3) != 0)
            throw new ArgumentException($"BC3 needs positive width and height that are multiples of 4, got {width}x{height}.");
        if ((long)width * height * 4 > int.MaxValue)
            throw new ArgumentException($"BC3 image {width}x{height} is too large.");
    }

    // ---------------------------------------------------------------- decode

    /// <summary>Decodes BC3 data to RGBA8 (R, G, B, A per pixel, rows top to bottom).</summary>
    public static byte[] Decode(ReadOnlySpan<byte> bc3, int width, int height)
    {
        int expected = EncodedLength(width, height);
        if (bc3.Length != expected)
            throw new ArgumentException($"BC3 data for {width}x{height} must be {expected} bytes, got {bc3.Length}.");
        var rgba = new byte[width * height * 4];
        Span<int> alpha = stackalloc int[8];
        Span<int> colour = stackalloc int[12];
        int blocksX = width / 4;
        int blocksY = height / 4;
        for (int by = 0; by < blocksY; by++)
        for (int bx = 0; bx < blocksX; bx++)
        {
            var block = bc3.Slice((by * blocksX + bx) * BlockBytes, BlockBytes);
            AlphaPalette(block[0], block[1], alpha);
            ulong aBits = 0;
            for (int i = 0; i < 6; i++) aBits |= (ulong)block[2 + i] << (8 * i);
            int c0 = block[8] | block[9] << 8;
            int c1 = block[10] | block[11] << 8;
            ColourPalette(c0, c1, colour);
            uint cBits = (uint)(block[12] | block[13] << 8 | block[14] << 16 | block[15] << 24);
            for (int p = 0; p < 16; p++)
            {
                int ci = (int)((cBits >> (2 * p)) & 3) * 3;
                int ai = (int)((aBits >> (3 * p)) & 7);
                int o = ((by * 4 + (p >> 2)) * width + bx * 4 + (p & 3)) * 4;
                rgba[o] = (byte)colour[ci];
                rgba[o + 1] = (byte)colour[ci + 1];
                rgba[o + 2] = (byte)colour[ci + 2];
                rgba[o + 3] = (byte)alpha[ai];
            }
        }
        return rgba;
    }

    static void AlphaPalette(int a0, int a1, Span<int> p)
    {
        p[0] = a0;
        p[1] = a1;
        if (a0 > a1)
            for (int i = 1; i <= 6; i++) p[1 + i] = ((7 - i) * a0 + i * a1) / 7;
        else
        {
            for (int i = 1; i <= 4; i++) p[1 + i] = ((5 - i) * a0 + i * a1) / 5;
            p[6] = 0;
            p[7] = 255;
        }
    }

    static int Expand5(int v) => v << 3 | v >> 2;
    static int Expand6(int v) => v << 2 | v >> 4;

    // out: 4 entries x (r, g, b); always the four-colour mode.
    static void ColourPalette(int c0, int c1, Span<int> p)
    {
        int r0 = Expand5(c0 >> 11), g0 = Expand6((c0 >> 5) & 63), b0 = Expand5(c0 & 31);
        int r1 = Expand5(c1 >> 11), g1 = Expand6((c1 >> 5) & 63), b1 = Expand5(c1 & 31);
        p[0] = r0; p[1] = g0; p[2] = b0;
        p[3] = r1; p[4] = g1; p[5] = b1;
        p[6] = (2 * r0 + r1) / 3; p[7] = (2 * g0 + g1) / 3; p[8] = (2 * b0 + b1) / 3;
        p[9] = (r0 + 2 * r1) / 3; p[10] = (g0 + 2 * g1) / 3; p[11] = (b0 + 2 * b1) / 3;
    }

    // ---------------------------------------------------------------- encode

    /// <summary>
    /// Encodes RGBA8 pixels (R, G, B, A per pixel, rows top to bottom) to BC3. Width and height must be positive multiples of 4
    /// and <paramref name="rgba"/> exactly width x height x 4 bytes.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        int length = EncodedLength(width, height);
        if (rgba.Length != width * height * 4)
            throw new ArgumentException($"RGBA data for {width}x{height} must be {width * height * 4} bytes, got {rgba.Length}.");
        var result = new byte[length];
        Span<int> rgb = stackalloc int[48];
        Span<int> alpha = stackalloc int[16];
        int blocksX = width / 4;
        int blocksY = height / 4;
        for (int by = 0; by < blocksY; by++)
        for (int bx = 0; bx < blocksX; bx++)
        {
            for (int p = 0; p < 16; p++)
            {
                int o = ((by * 4 + (p >> 2)) * width + bx * 4 + (p & 3)) * 4;
                rgb[p * 3] = rgba[o];
                rgb[p * 3 + 1] = rgba[o + 1];
                rgb[p * 3 + 2] = rgba[o + 2];
                alpha[p] = rgba[o + 3];
            }
            var dst = result.AsSpan((by * blocksX + bx) * BlockBytes, BlockBytes);
            EncodeAlpha(alpha, dst);
            EncodeColour(rgb, dst.Slice(8));
        }
        return result;
    }

    // ---- alpha

    static void EncodeAlpha(ReadOnlySpan<int> a, Span<byte> dst)
    {
        int lo = 255, hi = 0, lo6 = 255, hi6 = 0;
        bool any6 = false;
        foreach (int v in a)
        {
            if (v < lo) lo = v;
            if (v > hi) hi = v;
            if (v != 0 && v != 255)
            {
                any6 = true;
                if (v < lo6) lo6 = v;
                if (v > hi6) hi6 = v;
            }
        }
        int best0, best1;
        ulong bestIdx;
        if (lo == hi)
        {
            // Flat alpha: the six-value mode with equal endpoints holds any value exactly at index 0.
            best0 = best1 = lo;
            bestIdx = 0;
        }
        else
        {
            long errA = RefineAlpha(a, hi, lo, true, out int a0A, out int a1A, out ulong idxA);
            if (!any6) { lo6 = 0; hi6 = 0; }
            long errB = RefineAlpha(a, lo6, hi6, false, out int a0B, out int a1B, out ulong idxB);
            if (errB < errA) { best0 = a0B; best1 = a1B; bestIdx = idxB; }
            else { best0 = a0A; best1 = a1A; bestIdx = idxA; }
        }
        dst[0] = (byte)best0;
        dst[1] = (byte)best1;
        for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(bestIdx >> (8 * i));
    }

    // Iterated nearest-index assignment and least-squares endpoint refit; returns the best squared error seen.
    static long RefineAlpha(ReadOnlySpan<int> a, int a0, int a1, bool eightValue, out int bestA0, out int bestA1, out ulong bestIdx)
    {
        Span<int> palette = stackalloc int[8];
        Span<int> assign = stackalloc int[16];
        long bestErr = long.MaxValue;
        bestA0 = a0;
        bestA1 = a1;
        bestIdx = 0;
        for (int iter = 0; iter < 6; iter++)
        {
            AlphaPalette(a0, a1, palette);
            long err = 0;
            ulong idx = 0;
            for (int p = 0; p < 16; p++)
            {
                int bi = 0, bd = int.MaxValue;
                for (int k = 0; k < 8; k++)
                {
                    int d = a[p] - palette[k];
                    d *= d;
                    if (d < bd) { bd = d; bi = k; }
                }
                assign[p] = bi;
                err += bd;
                idx |= (ulong)bi << (3 * p);
            }
            if (err < bestErr) { bestErr = err; bestA0 = a0; bestA1 = a1; bestIdx = idx; }
            if (err == 0) break;

            // least squares: value ~ w0*a0 + w1*a1 per index
            double s00 = 0, s01 = 0, s11 = 0, t0 = 0, t1 = 0;
            for (int p = 0; p < 16; p++)
            {
                int k = assign[p];
                double w0, w1;
                if (k == 0) { w0 = 1; w1 = 0; }
                else if (k == 1) { w0 = 0; w1 = 1; }
                else if (eightValue) { w0 = (8 - k) / 7.0; w1 = (k - 1) / 7.0; }
                else if (k <= 5) { w0 = (6 - k) / 5.0; w1 = (k - 1) / 5.0; }
                else continue; // the fixed 0 and 255 entries say nothing about the endpoints
                s00 += w0 * w0; s01 += w0 * w1; s11 += w1 * w1;
                t0 += w0 * a[p]; t1 += w1 * a[p];
            }
            double det = s00 * s11 - s01 * s01;
            if (det < 1e-9) break;
            int n0 = Math.Clamp((int)Math.Round((t0 * s11 - t1 * s01) / det), 0, 255);
            int n1 = Math.Clamp((int)Math.Round((t1 * s00 - t0 * s01) / det), 0, 255);
            if (eightValue ? n0 <= n1 : n0 > n1) break;
            if (n0 == a0 && n1 == a1) break;
            a0 = n0;
            a1 = n1;
        }
        return bestErr;
    }

    // ---- colour

    static int Pack565(int r, int g, int b) => ((r * 31 + 127) / 255) << 11 | ((g * 63 + 127) / 255) << 5 | ((b * 31 + 127) / 255);

    static long Sq(int v) => (long)v * v;

    static void EncodeColour(ReadOnlySpan<int> px, Span<byte> dst)
    {
        bool flat = true;
        for (int p = 1; p < 16 && flat; p++)
            flat = px[p * 3] == px[0] && px[p * 3 + 1] == px[1] && px[p * 3 + 2] == px[2];
        int best0, best1;
        uint bestBits;
        if (flat) EncodeFlat(px[0], px[1], px[2], out best0, out best1, out bestBits);
        else
        {
            long bestErr = long.MaxValue;
            best0 = best1 = 0;
            bestBits = 0;
            Span<int> start = stackalloc int[6];
            for (int cand = 0; cand < 2; cand++)
            {
                if (cand == 0) PrincipalAxisEndpoints(px, start); else BoundingBoxEndpoints(px, start);
                Fit(px, start, ref bestErr, ref best0, ref best1, ref bestBits);
            }
        }
        dst[0] = (byte)best0; dst[1] = (byte)(best0 >> 8);
        dst[2] = (byte)best1; dst[3] = (byte)(best1 >> 8);
        dst[4] = (byte)bestBits; dst[5] = (byte)(bestBits >> 8); dst[6] = (byte)(bestBits >> 16); dst[7] = (byte)(bestBits >> 24);
    }

    // 'ends' holds two 8-bit RGB triples; iterates quantise -> assign -> least-squares refit, keeping the lowest error.
    static void Fit(ReadOnlySpan<int> px, Span<int> ends, ref long bestErr, ref int best0, ref int best1, ref uint bestBits)
    {
        Span<int> palette = stackalloc int[12];
        Span<int> assign = stackalloc int[16];
        Span<double> t0 = stackalloc double[3];
        Span<double> t1 = stackalloc double[3];
        Span<int> next = stackalloc int[6];
        for (int iter = 0; iter < 5; iter++)
        {
            int c0 = Pack565(ends[0], ends[1], ends[2]);
            int c1 = Pack565(ends[3], ends[4], ends[5]);
            if (c0 < c1) (c0, c1) = (c1, c0); // the first endpoint is always the larger
            ColourPalette(c0, c1, palette);
            long err = 0;
            uint bits = 0;
            for (int p = 0; p < 16; p++)
            {
                int bi = 0;
                long bd = long.MaxValue;
                // identical endpoints: every index reads as that colour; index 0 is the only safe one
                int count = c0 == c1 ? 1 : 4;
                for (int k = 0; k < count; k++)
                {
                    long d = Sq(px[p * 3] - palette[k * 3]) + Sq(px[p * 3 + 1] - palette[k * 3 + 1]) + Sq(px[p * 3 + 2] - palette[k * 3 + 2]);
                    if (d < bd) { bd = d; bi = k; }
                }
                assign[p] = bi;
                bits |= (uint)bi << (2 * p);
                err += bd;
            }
            if (err < bestErr) { bestErr = err; best0 = c0; best1 = c1; bestBits = bits; }
            if (err == 0 || c0 == c1) break;

            // least squares for the palette c0 * w + c1 * (1 - w), with w = 1, 0, 2/3, 1/3 for indices 0..3
            double s00 = 0, s01 = 0, s11 = 0;
            t0.Clear();
            t1.Clear();
            for (int p = 0; p < 16; p++)
            {
                double w0 = assign[p] switch { 0 => 1.0, 1 => 0.0, 2 => 2.0 / 3, _ => 1.0 / 3 };
                double w1 = 1 - w0;
                s00 += w0 * w0; s01 += w0 * w1; s11 += w1 * w1;
                for (int c = 0; c < 3; c++) { t0[c] += w0 * px[p * 3 + c]; t1[c] += w1 * px[p * 3 + c]; }
            }
            double det = s00 * s11 - s01 * s01;
            if (det < 1e-9) break;
            for (int c = 0; c < 3; c++)
            {
                next[c] = Math.Clamp((int)Math.Round((t0[c] * s11 - t1[c] * s01) / det), 0, 255);
                next[3 + c] = Math.Clamp((int)Math.Round((t1[c] * s00 - t0[c] * s01) / det), 0, 255);
            }
            bool same = true;
            for (int i = 0; i < 6; i++) same &= next[i] == ends[i];
            if (same) break;
            next.CopyTo(ends);
        }
    }

    static void BoundingBoxEndpoints(ReadOnlySpan<int> px, Span<int> ends)
    {
        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        double mr = 0, mg = 0, mb = 0;
        for (int p = 0; p < 16; p++)
        {
            int r = px[p * 3], g = px[p * 3 + 1], b = px[p * 3 + 2];
            minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
            minG = Math.Min(minG, g); maxG = Math.Max(maxG, g);
            minB = Math.Min(minB, b); maxB = Math.Max(maxB, b);
            mr += r; mg += g; mb += b;
        }
        mr /= 16; mg /= 16; mb /= 16;
        double crg = 0, crb = 0;
        for (int p = 0; p < 16; p++)
        {
            double dr = px[p * 3] - mr;
            crg += dr * (px[p * 3 + 1] - mg);
            crb += dr * (px[p * 3 + 2] - mb);
        }
        // the box diagonal that follows the correlation of green and blue with red
        ends[0] = minR; ends[1] = crg < 0 ? maxG : minG; ends[2] = crb < 0 ? maxB : minB;
        ends[3] = maxR; ends[4] = crg < 0 ? minG : maxG; ends[5] = crb < 0 ? minB : maxB;
    }

    static void PrincipalAxisEndpoints(ReadOnlySpan<int> px, Span<int> ends)
    {
        double m0 = 0, m1 = 0, m2 = 0;
        for (int p = 0; p < 16; p++) { m0 += px[p * 3]; m1 += px[p * 3 + 1]; m2 += px[p * 3 + 2]; }
        m0 /= 16; m1 /= 16; m2 /= 16;
        double c00 = 0, c01 = 0, c02 = 0, c11 = 0, c12 = 0, c22 = 0;
        for (int p = 0; p < 16; p++)
        {
            double a = px[p * 3] - m0, b = px[p * 3 + 1] - m1, c = px[p * 3 + 2] - m2;
            c00 += a * a; c01 += a * b; c02 += a * c; c11 += b * b; c12 += b * c; c22 += c * c;
        }
        // power iteration from the covariance's row sums (deterministic)
        double v0 = c00 + c01 + c02, v1 = c01 + c11 + c12, v2 = c02 + c12 + c22;
        if (Math.Abs(v0) + Math.Abs(v1) + Math.Abs(v2) < 1e-9) { v0 = c00; v1 = c11; v2 = c22; }
        for (int i = 0; i < 8; i++)
        {
            double n0 = c00 * v0 + c01 * v1 + c02 * v2;
            double n1 = c01 * v0 + c11 * v1 + c12 * v2;
            double n2 = c02 * v0 + c12 * v1 + c22 * v2;
            double len = Math.Sqrt(n0 * n0 + n1 * n1 + n2 * n2);
            if (len < 1e-12) break;
            v0 = n0 / len; v1 = n1 / len; v2 = n2 / len;
        }
        double vl = Math.Sqrt(v0 * v0 + v1 * v1 + v2 * v2);
        if (vl < 1e-12) { v0 = 1; v1 = 0; v2 = 0; } else { v0 /= vl; v1 /= vl; v2 /= vl; }
        double tmin = double.MaxValue, tmax = double.MinValue;
        for (int p = 0; p < 16; p++)
        {
            double t = (px[p * 3] - m0) * v0 + (px[p * 3 + 1] - m1) * v1 + (px[p * 3 + 2] - m2) * v2;
            tmin = Math.Min(tmin, t); tmax = Math.Max(tmax, t);
        }
        ends[0] = Clamp8(m0 + v0 * tmin); ends[1] = Clamp8(m1 + v1 * tmin); ends[2] = Clamp8(m2 + v2 * tmin);
        ends[3] = Clamp8(m0 + v0 * tmax); ends[4] = Clamp8(m1 + v1 * tmax); ends[5] = Clamp8(m2 + v2 * tmax);
    }

    static int Clamp8(double v) => Math.Clamp((int)Math.Round(v), 0, 255);

    // ---- flat colours

    // T[k][channel][value] = the RGB565 channel endpoints (lo, hi) whose palette entry k lands nearest to value, and that distance.
    sealed class FlatTables
    {
        public readonly (byte lo, byte hi, byte err)[][][] T = new (byte, byte, byte)[4][][];

        public FlatTables()
        {
            for (int k = 0; k < 4; k++)
            {
                T[k] = new (byte, byte, byte)[3][];
                for (int ch = 0; ch < 3; ch++)
                {
                    int bits = ch == 1 ? 6 : 5;
                    int n = 1 << bits;
                    var table = new (byte, byte, byte)[256];
                    var bestErr = new int[256];
                    Array.Fill(bestErr, int.MaxValue);
                    for (int lo = 0; lo < n; lo++)
                    for (int hi = 0; hi < n; hi++)
                    {
                        int e0 = bits == 6 ? Expand6(lo) : Expand5(lo);
                        int e1 = bits == 6 ? Expand6(hi) : Expand5(hi);
                        int val = k switch { 0 => e0, 1 => e1, 2 => (2 * e0 + e1) / 3, _ => (e0 + 2 * e1) / 3 };
                        for (int v = 0; v < 256; v++)
                        {
                            int err = Math.Abs(v - val);
                            if (err < bestErr[v])
                            {
                                bestErr[v] = err;
                                table[v] = ((byte)lo, (byte)hi, (byte)err);
                            }
                        }
                    }
                    T[k][ch] = table;
                }
            }
        }
    }

    static readonly Lazy<FlatTables> Flat = new(() => new FlatTables());

    static void EncodeFlat(int r, int g, int b, out int c0, out int c1, out uint bits)
    {
        var tables = Flat.Value.T;
        int bestK = 0;
        long bestErr = long.MaxValue;
        for (int k = 0; k < 4; k++)
        {
            long e = Sq(tables[k][0][r].err) + Sq(tables[k][1][g].err) + Sq(tables[k][2][b].err);
            if (e < bestErr) { bestErr = e; bestK = k; }
        }
        var tr = tables[bestK][0][r];
        var tg = tables[bestK][1][g];
        var tb = tables[bestK][2][b];
        c0 = tr.lo << 11 | tg.lo << 5 | tb.lo;
        c1 = tr.hi << 11 | tg.hi << 5 | tb.hi;
        int k2 = bestK;
        if (c0 < c1) { (c0, c1) = (c1, c0); k2 = bestK ^ 1; } // swapping the endpoints mirrors the indices 0<->1 and 2<->3
        if (c0 == c1) k2 = 0;
        uint all = 0;
        for (int p = 0; p < 16; p++) all |= (uint)k2 << (2 * p);
        bits = all;
    }
}
