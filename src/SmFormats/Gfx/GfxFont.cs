using System.Text;

namespace SmFormats.Gfx;

/// <summary>
/// One font of a Flash font movie (DefineFont2 / DefineFont3), read for its outlines (classic-score-hud: the HUD movies import their faces from the install's font movie, <c>fonts_en.gfx</c>; the
/// glyphs are vector shapes in an em square, so a digit can be drawn at any size). Coordinates are font units: <see cref="EmSquare"/> to the em.
/// </summary>
public sealed class GfxFontFace
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Font units to the em square (1024 in DefineFont2, 20480 in DefineFont3).</summary>
    public double EmSquare { get; init; }
    public double Ascent { get; init; }
    public double Descent { get; init; }
    public double Leading { get; init; }
    /// <summary>The glyph of a character code, or -1.</summary>
    public IReadOnlyDictionary<int, int> GlyphOf { get; init; } = new Dictionary<int, int>();
    /// <summary>Advance width per glyph (font units; 0 without a layout table).</summary>
    public double[] Advance { get; init; } = [];
    /// <summary>Closed contours per glyph (flattened quadratic curves, font units, y down as the movie stores it).</summary>
    public IReadOnlyList<(double X, double Y)[]>[] Contours { get; init; } = [];

    public bool Has(char c) => GlyphOf.ContainsKey(c);

    /// <summary>Whether the face's name says bold ("XBourgeoix OT Bold Alternate").</summary>
    public bool IsBold => Name.Contains("Bold", StringComparison.OrdinalIgnoreCase);

    /// <summary>The advance of a character in em units (0 when the face lacks it).</summary>
    public double AdvanceEm(char c) => GlyphOf.TryGetValue(c, out int g) && g < Advance.Length ? Advance[g] / EmSquare : 0.0;

    /// <summary>The width of <paramref name="s"/> in em units (the advances, no kerning).</summary>
    public double WidthEm(string s)
    {
        double w = 0;
        foreach (char c in s) w += AdvanceEm(c);
        return w;
    }

    /// <summary>
    /// Fills a character's outline into an 8-bit coverage map at <paramref name="pxPerEm"/> pixels to the em (non-zero winding, 4 sub-rows per row, exact across a row): the map's size and the
    /// pixel offset of its top-left corner from the glyph origin (x to the right, y down from the baseline). Null for a character without an outline (a space) or one the face lacks.
    /// </summary>
    public GfxGlyphMap? Rasterize(char c, double pxPerEm)
    {
        if (!GlyphOf.TryGetValue(c, out int g) || g >= Contours.Length || Contours[g].Count == 0 || pxPerEm <= 0) return null;
        double k = pxPerEm / EmSquare;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var contour in Contours[g])
            foreach (var (x, y) in contour) { minX = Math.Min(minX, x * k); maxX = Math.Max(maxX, x * k); minY = Math.Min(minY, y * k); maxY = Math.Max(maxY, y * k); }
        if (minX > maxX) return null;
        int pad = 1;
        int left = (int)Math.Floor(minX) - pad, top = (int)Math.Floor(minY) - pad;
        int w = (int)Math.Ceiling(maxX) + pad - left, h = (int)Math.Ceiling(maxY) + pad - top;
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;
        const int Sub = 4;
        var cover = new float[w * h];
        var xs = new List<(double X, int Dir)>();
        for (int row = 0; row < h; row++)
            for (int s = 0; s < Sub; s++)
            {
                double y = top + row + (s + 0.5) / Sub;
                xs.Clear();
                foreach (var contour in Contours[g])
                    for (int i = 0; i < contour.Length; i++)
                    {
                        var (x0, y0) = contour[i];
                        var (x1, y1) = contour[(i + 1) % contour.Length];
                        x0 *= k; y0 *= k; x1 *= k; y1 *= k;
                        if (y0 == y1 || y < Math.Min(y0, y1) || y >= Math.Max(y0, y1)) continue;
                        xs.Add((x0 + (y - y0) / (y1 - y0) * (x1 - x0), y1 > y0 ? 1 : -1));
                    }
                xs.Sort((a, b) => a.X.CompareTo(b.X));
                int winding = 0;
                for (int i = 0; i + 1 < xs.Count; i++)
                {
                    winding += xs[i].Dir;
                    if (winding == 0) continue;
                    double a = xs[i].X - left, b = xs[i + 1].X - left;
                    if (b <= 0 || a >= w) continue;
                    a = Math.Max(a, 0); b = Math.Min(b, w);
                    int pa = (int)a, pb = (int)Math.Min(b, w - 1e-9);
                    for (int px = pa; px <= pb; px++) cover[row * w + px] += (float)((Math.Min(b, px + 1) - Math.Max(a, px)) / Sub);
                }
            }
        var alpha = new byte[w * h];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = (byte)Math.Clamp((int)MathF.Round(cover[i] * 255f), 0, 255);
        return new GfxGlyphMap(w, h, left, top, alpha);
    }
}

/// <summary>A rasterised glyph: 8-bit coverage, and where its top-left pixel lies from the glyph's origin on the baseline (pixels).</summary>
public sealed record GfxGlyphMap(int Width, int Height, int Left, int Top, byte[] Alpha);

/// <summary>Reads the fonts (DefineFont2, tag 48, and DefineFont3, tag 75) of a Flash font movie.</summary>
public static class GfxFontReader
{
    public static IReadOnlyList<GfxFontFace> Read(byte[] data)
    {
        if (data.Length < 12 || data[0] != 'G' || data[1] != 'F' || data[2] != 'X') throw new InvalidDataException("not an uncompressed GFX movie");
        var faces = new List<GfxFontFace>();
        try
        {
            int pos = 8;
            int nbits = data[pos] >> 3;
            pos += (5 + nbits * 4 + 7) / 8 + 4;
            while (pos + 2 <= data.Length)
            {
                int hdr = BitConverter.ToUInt16(data, pos); pos += 2;
                int code = hdr >> 6, len = hdr & 0x3f;
                if (len == 0x3f) { if (pos + 4 > data.Length) break; len = BitConverter.ToInt32(data, pos); pos += 4; }
                if (len < 0 || len > data.Length - pos) break;
                if (code is 48 or 75) { var face = ReadFont(data, pos, pos + len, code == 75); if (face is not null) faces.Add(face); }
                pos += len;
                if (code == 0) break;
            }
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException)
        {
            throw new InvalidDataException("truncated or malformed font movie: " + e.Message, e);
        }
        return faces;
    }

    static GfxFontFace? ReadFont(byte[] d, int start, int end, bool three)
    {
        int p = start;
        int id = BitConverter.ToUInt16(d, p); p += 2;
        int flags = d[p++]; p++;   // flags, language
        int nameLen = d[p++];
        string name = Encoding.UTF8.GetString(d, p, nameLen).TrimEnd('\0'); p += nameLen;
        bool layout = (flags & 0x80) != 0, wideOffsets = (flags & 0x08) != 0, wideCodes = (flags & 0x04) != 0;
        int count = BitConverter.ToUInt16(d, p); p += 2;
        if (count == 0 || count > 20000) return null;
        int tableAt = p;
        int Offset(int i) => wideOffsets ? (int)BitConverter.ToUInt32(d, tableAt + i * 4) : BitConverter.ToUInt16(d, tableAt + i * 2);
        int offsetSize = wideOffsets ? 4 : 2;
        p = tableAt + count * offsetSize;
        int codeTableOffset = wideOffsets ? (int)BitConverter.ToUInt32(d, p) : BitConverter.ToUInt16(d, p);
        p += offsetSize;
        int shapesBase = tableAt;   // glyph offsets count from the start of the offset table
        var contours = new IReadOnlyList<(double X, double Y)[]>[count];
        for (int i = 0; i < count; i++)
        {
            int at = shapesBase + Offset(i);
            int stop = i + 1 < count ? shapesBase + Offset(i + 1) : shapesBase + codeTableOffset;
            contours[i] = at >= start && at < end && stop <= end && stop >= at ? ReadGlyph(d, at, stop) : [];
        }
        int q = shapesBase + codeTableOffset;
        var glyphOf = new Dictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            int code = wideCodes ? BitConverter.ToUInt16(d, q) : d[q];
            q += wideCodes ? 2 : 1;
            glyphOf.TryAdd(code, i);
        }
        double ascent = 0, descent = 0, leading = 0;
        double[] advance = new double[count];
        if (layout)
        {
            ascent = BitConverter.ToInt16(d, q); q += 2;
            descent = BitConverter.ToInt16(d, q); q += 2;
            leading = BitConverter.ToInt16(d, q); q += 2;
            for (int i = 0; i < count; i++) { advance[i] = BitConverter.ToInt16(d, q); q += 2; }
        }
        return new GfxFontFace { Id = id, Name = name, EmSquare = three ? 20480.0 : 1024.0, Ascent = ascent, Descent = descent, Leading = leading, GlyphOf = glyphOf, Advance = advance, Contours = contours };
    }

    /// <summary>A glyph's SHAPE: the fill and line bit counts, then edge records (no style arrays); every contour is closed and flattened.</summary>
    static IReadOnlyList<(double X, double Y)[]> ReadGlyph(byte[] d, int at, int stop)
    {
        var bits = new BitCursor(d, at);
        int fillBits = (int)bits.U(4), lineBits = (int)bits.U(4);
        var all = new List<(double, double)[]>();
        var path = new List<(double X, double Y)>();
        double x = 0, y = 0;
        bool reverse = false;
        void Close()
        {
            if (path.Count >= 3)
            {
                var arr = path.ToArray();
                if (reverse) Array.Reverse(arr);
                all.Add(arr);
            }
            path.Clear();
        }
        int fill0 = 0, fill1 = 0;
        while (bits.Position < stop * 8)
        {
            if (bits.U(1) == 0)
            {
                uint f = bits.U(5);
                if (f == 0) break;
                bool move = (f & 1) != 0, f0 = (f & 2) != 0, f1 = (f & 4) != 0, line = (f & 8) != 0, newStyles = (f & 16) != 0;
                if (move)
                {
                    Close();
                    int nb = (int)bits.U(5);
                    x = bits.S(nb); y = bits.S(nb);
                    path.Add((x, y));
                }
                if (f0) fill0 = (int)bits.U(fillBits);
                if (f1) fill1 = (int)bits.U(fillBits);
                if (line) bits.U(lineBits);
                if (newStyles) break;   // not in a glyph
                // A contour that only has a left fill runs the other way round: reversed, it is a right-filled one like the rest.
                if (f0 || f1) reverse = fill1 == 0 && fill0 != 0;
                if (path.Count == 0) path.Add((x, y));
            }
            else if (bits.U(1) == 1)
            {
                int nb = (int)bits.U(4) + 2;
                double dx = 0, dy = 0;
                if (bits.U(1) == 1) { dx = bits.S(nb); dy = bits.S(nb); }
                else if (bits.U(1) == 1) dy = bits.S(nb);
                else dx = bits.S(nb);
                x += dx; y += dy;
                path.Add((x, y));
            }
            else
            {
                int nb = (int)bits.U(4) + 2;
                double cx = x + bits.S(nb), cy = y + bits.S(nb);
                double ax = cx + bits.S(nb), ay = cy + bits.S(nb);
                const int Steps = 6;
                for (int s = 1; s <= Steps; s++)
                {
                    double t = s / (double)Steps, u = 1 - t;
                    path.Add((u * u * x + 2 * u * t * cx + t * t * ax, u * u * y + 2 * u * t * cy + t * t * ay));
                }
                x = ax; y = ay;
            }
        }
        Close();
        return all;
    }

    sealed class BitCursor(byte[] d, int byteAt)
    {
        int _bit = byteAt * 8;
        public int Position => _bit;
        public uint U(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++) { v = (v << 1) | (uint)((d[_bit >> 3] >> (7 - (_bit & 7))) & 1); _bit++; }
            return v;
        }
        public int S(int n)
        {
            if (n == 0) return 0;
            int v = (int)U(n);
            if ((v & (1 << (n - 1))) != 0) v -= 1 << n;
            return v;
        }
    }
}
