using System.Threading;

namespace SmFormats.Gfx;

/// <summary>A bitmap a shape fill refers to: straight RGBA, 4 bytes a pixel (the sub-image of an atlas, copied out).</summary>
public sealed class GfxBitmap(int width, int height, byte[] rgba)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Rgba { get; } = rgba;
}

/// <summary>
/// A canvas of premultiplied RGBA, 16 bits a channel (8 bytes a pixel: 17 MB at 1080p), the target of <see cref="GfxRaster"/>. A canvas made <see cref="Additive"/> collects what the movie
/// draws with the Flash "add" blend mode: colour is summed (saturating) and the alpha channel is not used.
/// </summary>
public sealed class GfxCanvas(int width, int height, bool additive = false)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public bool Additive { get; } = additive;
    /// <summary>Premultiplied RGBA, 4 values a pixel, 0 to 65535.</summary>
    public ushort[] Pixels { get; } = new ushort[checked(width * height * 4)];

    /// <summary>The alpha (0 to 1) of a pixel.</summary>
    public float AlphaAt(int x, int y) => Pixels[(y * Width + x) * 4 + 3] / 65535f;

    /// <summary>A pixel's premultiplied colour and alpha, 0 to 1.</summary>
    public (float R, float G, float B, float A) At(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (Pixels[i] / 65535f, Pixels[i + 1] / 65535f, Pixels[i + 2] / 65535f, Pixels[i + 3] / 65535f);
    }

    /// <summary>Copies column <paramref name="from"/> over column <paramref name="to"/>.</summary>
    public void CopyColumn(int from, int to) { for (int y = 0; y < Height; y++) Array.Copy(Pixels, (y * Width + from) * 4, Pixels, (y * Width + to) * 4, 4); }

    /// <summary>Copies row <paramref name="from"/> over row <paramref name="to"/>.</summary>
    public void CopyRow(int from, int to) => Array.Copy(Pixels, from * Width * 4, Pixels, to * Width * 4, Width * 4);

    /// <summary>Straight RGBA bytes (for an image); for an additive canvas the sum, with alpha 255.</summary>
    public byte[] ToRgba8()
    {
        var o = new byte[Width * Height * 4];
        for (int i = 0; i < Width * Height; i++)
        {
            int p = i * 4;
            if (Additive)
            {
                o[p] = (byte)((Pixels[p] + 128) / 257); o[p + 1] = (byte)((Pixels[p + 1] + 128) / 257); o[p + 2] = (byte)((Pixels[p + 2] + 128) / 257); o[p + 3] = 255;
                continue;
            }
            float a = Pixels[p + 3] / 65535f;
            float inv = a > 1e-5f ? 1f / a : 0f;
            o[p] = (byte)Math.Clamp(Pixels[p] / 65535f * inv * 255f + 0.5f, 0f, 255f);
            o[p + 1] = (byte)Math.Clamp(Pixels[p + 1] / 65535f * inv * 255f + 0.5f, 0f, 255f);
            o[p + 2] = (byte)Math.Clamp(Pixels[p + 2] / 65535f * inv * 255f + 0.5f, 0f, 255f);
            o[p + 3] = (byte)Math.Clamp(a * 255f + 0.5f, 0f, 255f);
        }
        return o;
    }
}

/// <summary>
/// A small software renderer for the parts of a Flash movie the HUD's scope overlay uses: solid, gradient and bitmap fills (anti-aliased, non-zero winding over the
/// edges' left and right fill styles), thin lines, sprites and their placements with matrices and colour transforms. Engine-free; no text, masks or filters.
/// </summary>
public static class GfxRaster
{
    /// <summary>
    /// Draws frame <paramref name="frame"/> of sprite <paramref name="spriteId"/> (0: the root) onto <paramref name="canvas"/>. <paramref name="root"/> maps the sprite's
    /// pixels (twips / 20) to canvas pixels. <paramref name="bitmaps"/> returns the bitmap of a movie bitmap id (null: nothing is drawn for that fill).
    /// <paramref name="frameOf"/> picks the frame of a nested sprite (null: its first). What the movie places with the "add" blend mode (and what is inside it) goes to
    /// <paramref name="additive"/> when one is given (a canvas made additive), else it is drawn as normal. <paramref name="cancel"/> stops the drawing (an <see cref="OperationCanceledException"/>).
    /// </summary>
    public static void Draw(GfxMovie movie, int spriteId, int frame, GfxMatrix root, GfxCanvas canvas, Func<int, GfxBitmap?> bitmaps, Func<int, int>? frameOf = null,
        GfxCanvas? additive = null, CancellationToken cancel = default)
        => DrawSprite(movie, spriteId == 0 ? movie.Root : movie.Sprites[spriteId], frame, root, GfxColourTransform.Identity, canvas, additive, false, bitmaps, frameOf, 0, cancel);

    static void DrawSprite(GfxMovie movie, GfxSprite sprite, int frame, GfxMatrix m, GfxColourTransform cx, GfxCanvas canvas, GfxCanvas? additive, bool add, Func<int, GfxBitmap?> bitmaps, Func<int, int>? frameOf, int depth, CancellationToken cancel)
    {
        if (depth > 12 || sprite.Frames.Count == 0) return;
        var f = sprite.Frames[Math.Clamp(frame, 0, sprite.Frames.Count - 1)];
        foreach (var p in f.Display.Values)
        {
            cancel.ThrowIfCancellationRequested();
            if (p.Character < 0) continue;
            // Placement matrices are in twips for the translation: convert to pixels.
            var pm = p.Matrix is { } pmx ? new GfxMatrix(pmx.A, pmx.B, pmx.C, pmx.D, pmx.Tx / 20.0, pmx.Ty / 20.0) : GfxMatrix.Identity;
            var child = m.After(pm);
            var childCx = p.Colour is { } pc ? cx.After(pc) : cx;
            bool childAdd = add || p.Blend == 8;   // Flash blend mode 8 is "add"; a multiply (3) of black, which the scope's masks use, is the normal alpha blend
            if (movie.Sprites.TryGetValue(p.Character, out var sub)) DrawSprite(movie, sub, frameOf?.Invoke(p.Character) ?? 0, child, childCx, canvas, additive, childAdd, bitmaps, frameOf, depth + 1, cancel);
            else if (movie.Shapes.TryGetValue(p.Character, out var shape)) DrawShape(shape, child, childCx, childAdd && additive is not null ? additive : canvas, bitmaps, cancel);
        }
    }

    // ---- shapes ----

    readonly record struct Edge(double X0, double Y0, double X1, double Y1);

    static void Flatten(GfxSegment s, GfxMatrix m, List<(double X0, double Y0, double X1, double Y1)> into, bool reverse)
    {
        // Twips to pixels, then the matrix.
        (double, double) T(double x, double y) => m.Apply(x / 20.0, y / 20.0);
        var p0 = T(s.X0, s.Y0); var p1 = T(s.X1, s.Y1);
        if (!s.Curved)
        {
            into.Add(reverse ? (p1.Item1, p1.Item2, p0.Item1, p0.Item2) : (p0.Item1, p0.Item2, p1.Item1, p1.Item2));
            return;
        }
        var c = T(s.CX, s.CY);
        double len = Math.Abs(p0.Item1 - c.Item1) + Math.Abs(p0.Item2 - c.Item2) + Math.Abs(c.Item1 - p1.Item1) + Math.Abs(c.Item2 - p1.Item2);
        int n = Math.Clamp((int)(len / 2.5) + 2, 4, 48);
        double px = p0.Item1, py = p0.Item2;
        for (int i = 1; i <= n; i++)
        {
            double t = (double)i / n, u = 1 - t;
            double x = u * u * p0.Item1 + 2 * u * t * c.Item1 + t * t * p1.Item1, y = u * u * p0.Item2 + 2 * u * t * c.Item2 + t * t * p1.Item2;
            into.Add(reverse ? (x, y, px, py) : (px, py, x, y));
            px = x; py = y;
        }
    }

    static void DrawShape(GfxShape shape, GfxMatrix m, GfxColourTransform cx, GfxCanvas canvas, Func<int, GfxBitmap?> bitmaps, CancellationToken cancel)
    {
        // Fills in style order; each fill's edges: right side as drawn, left side reversed.
        for (int fi = 1; fi <= shape.Fills.Count; fi++)
        {
            var edges = new List<(double X0, double Y0, double X1, double Y1)>();
            foreach (var s in shape.Segments)
            {
                if (s.FillRight == fi) Flatten(s, m, edges, false);
                if (s.FillLeft == fi) Flatten(s, m, edges, true);
            }
            if (edges.Count == 0) continue;
            FillEdges(edges, shape.Fills[fi - 1], m, cx, canvas, bitmaps, cancel);
        }
        // Lines on top.
        foreach (var s in shape.Segments)
        {
            if (s.Line <= 0 || s.Line > shape.Lines.Count) continue;
            var ls = shape.Lines[s.Line - 1];
            var list = new List<(double X0, double Y0, double X1, double Y1)>();
            Flatten(s, m, list, false);
            // Width in pixels under the matrix's scale (at least 1 pixel: Flash's hairlines).
            double scale = Math.Sqrt(Math.Abs(m.A * m.D - m.B * m.C));
            double width = Math.Max(1.0, ls.Width / 20.0 * scale);
            foreach (var e in list) StrokeSegment(e.X0, e.Y0, e.X1, e.Y1, width, Tint(ls.Colour, cx), canvas);
        }
    }

    static (float R, float G, float B, float A) Tint((float R, float G, float B, float A) c, GfxColourTransform cx) =>
        (Math.Clamp(c.R * cx.MulR + cx.AddR, 0f, 1f), Math.Clamp(c.G * cx.MulG + cx.AddG, 0f, 1f), Math.Clamp(c.B * cx.MulB + cx.AddB, 0f, 1f), Math.Clamp(c.A * cx.MulA + cx.AddA, 0f, 1f));

    static void Blend(GfxCanvas canvas, int x, int y, float r, float g, float b, float a)
    {
        if (a <= 0f) return;
        int i = (y * canvas.Width + x) * 4;
        var px = canvas.Pixels;
        if (canvas.Additive)
        {
            px[i] = Sat(px[i] + r * a * 65535f); px[i + 1] = Sat(px[i + 1] + g * a * 65535f); px[i + 2] = Sat(px[i + 2] + b * a * 65535f);
            return;
        }
        float inv = 1f - a;
        px[i] = Sat(r * a * 65535f + px[i] * inv); px[i + 1] = Sat(g * a * 65535f + px[i + 1] * inv); px[i + 2] = Sat(b * a * 65535f + px[i + 2] * inv); px[i + 3] = Sat(a * 65535f + px[i + 3] * inv);
    }

    static ushort Sat(float v) => v >= 65535f ? (ushort)65535 : v <= 0f ? (ushort)0 : (ushort)(v + 0.5f);

    static void StrokeSegment(double x0, double y0, double x1, double y1, double width, (float R, float G, float B, float A) c, GfxCanvas canvas)
    {
        double half = width * 0.5;
        int minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, x1) - half - 1)), maxX = Math.Min(canvas.Width - 1, (int)Math.Ceiling(Math.Max(x0, x1) + half + 1));
        int minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, y1) - half - 1)), maxY = Math.Min(canvas.Height - 1, (int)Math.Ceiling(Math.Max(y0, y1) + half + 1));
        double dx = x1 - x0, dy = y1 - y0, l2 = dx * dx + dy * dy;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double t = l2 < 1e-9 ? 0 : Math.Clamp(((px - x0) * dx + (py - y0) * dy) / l2, 0, 1);
                double d = Math.Sqrt((px - (x0 + dx * t)) * (px - (x0 + dx * t)) + (py - (y0 + dy * t)) * (py - (y0 + dy * t)));
                float cov = (float)Math.Clamp(half + 0.5 - d, 0, 1);
                Blend(canvas, x, y, c.R, c.G, c.B, c.A * cov);
            }
    }

    // ---- fills ----

    const int Sub = 4;

    static void FillEdges(List<(double X0, double Y0, double X1, double Y1)> edges, GfxFill fill, GfxMatrix shapeMatrix, GfxColourTransform cx, GfxCanvas canvas, Func<int, GfxBitmap?> bitmaps, CancellationToken cancel)
    {
        double minY = double.MaxValue, maxY = double.MinValue, minX = double.MaxValue, maxX = double.MinValue;
        foreach (var e in edges) { minY = Math.Min(minY, Math.Min(e.Y0, e.Y1)); maxY = Math.Max(maxY, Math.Max(e.Y0, e.Y1)); minX = Math.Min(minX, Math.Min(e.X0, e.X1)); maxX = Math.Max(maxX, Math.Max(e.X0, e.X1)); }
        int y0 = Math.Max(0, (int)Math.Floor(minY)), y1 = Math.Min(canvas.Height - 1, (int)Math.Ceiling(maxY));
        int xa = Math.Max(0, (int)Math.Floor(minX)), xb = Math.Min(canvas.Width - 1, (int)Math.Ceiling(maxX));
        if (y0 > y1 || xa > xb) return;
        GfxBitmap? bitmap = fill.Kind == GfxFillKind.Bitmap ? bitmaps(fill.BitmapId) : null;
        if (fill.Kind == GfxFillKind.Bitmap && bitmap is null) return;
        // The fill's own space: bitmaps and gradients map through (fill matrix) after the shape's (twips); the inverse takes a canvas pixel back into the fill's space.
        GfxMatrix? inverse = null;
        if (fill.Kind != GfxFillKind.Solid)
        {
            var toCanvas = shapeMatrix.After(new GfxMatrix(1 / 20.0, 0, 0, 1 / 20.0, 0, 0));   // twips -> canvas pixels
            var full = toCanvas.After(fill.Matrix);
            inverse = full.Inverse();
            if (inverse is null) return;
        }
        var cov = new float[xb - xa + 1];
        var xs = new List<(double X, int Dir)>(16);
        for (int y = y0; y <= y1; y++)
        {
            if ((y & 15) == 0) cancel.ThrowIfCancellationRequested();
            Array.Clear(cov);
            bool any = false;
            for (int s = 0; s < Sub; s++)
            {
                double yy = y + (s + 0.5) / Sub;
                xs.Clear();
                foreach (var e in edges)
                {
                    if (e.Y0 == e.Y1) continue;
                    bool up = e.Y0 < e.Y1;
                    double ya = up ? e.Y0 : e.Y1, yb = up ? e.Y1 : e.Y0;
                    if (yy < ya || yy >= yb) continue;
                    double x = e.X0 + (yy - e.Y0) / (e.Y1 - e.Y0) * (e.X1 - e.X0);
                    xs.Add((x, up ? 1 : -1));
                }
                if (xs.Count < 2) continue;
                xs.Sort((p, q) => p.X.CompareTo(q.X));
                int wind = 0;
                for (int k = 0; k < xs.Count - 1; k++)
                {
                    wind += xs[k].Dir;
                    if (wind == 0) continue;
                    double a = Math.Max(xs[k].X, xa), b = Math.Min(xs[k + 1].X, xb + 1);
                    if (b <= a) continue;
                    any = true;
                    int ia = (int)Math.Floor(a), ib = (int)Math.Floor(b);
                    if (ia == ib) { cov[ia - xa] += (float)(b - a) / Sub; continue; }
                    cov[ia - xa] += (float)(ia + 1 - a) / Sub;
                    for (int x = ia + 1; x < ib; x++) cov[x - xa] += 1f / Sub;
                    if (ib <= xb) cov[ib - xa] += (float)(b - ib) / Sub;
                }
            }
            if (!any) continue;
            for (int x = xa; x <= xb; x++)
            {
                float c = Math.Min(1f, cov[x - xa]);
                if (c <= 0.002f) continue;
                var col = ColourAt(fill, inverse, bitmap, x + 0.5, y + 0.5);
                col = Tint(col, cx);
                Blend(canvas, x, y, col.R, col.G, col.B, col.A * c);
            }
        }
    }

    static (float R, float G, float B, float A) ColourAt(GfxFill fill, GfxMatrix? inverse, GfxBitmap? bitmap, double px, double py)
    {
        switch (fill.Kind)
        {
            case GfxFillKind.Solid: return fill.Colour;
            case GfxFillKind.Bitmap:
                {
                    var (u, v) = inverse!.Value.Apply(px, py);   // the fill matrix maps bitmap pixels to the shape's twips (a scale of 20 for a 1:1 picture)
                    return SampleBilinear(bitmap!, u, v, fill.Repeat);
                }
            default:
                {
                    var (gx, gy) = inverse!.Value.Apply(px, py);   // gradient space: -16384..16384 twips
                    double t = fill.Kind == GfxFillKind.LinearGradient ? (gx + 16384.0) / 32768.0 : Math.Sqrt(gx * gx + gy * gy) / 16384.0;
                    return GradientAt(fill.Stops, (float)Math.Clamp(t, 0, 1));
                }
        }
    }

    static (float R, float G, float B, float A) GradientAt((float Ratio, float R, float G, float B, float A)[] stops, float t)
    {
        if (stops.Length == 0) return (0, 0, 0, 0);
        if (t <= stops[0].Ratio) return (stops[0].R, stops[0].G, stops[0].B, stops[0].A);
        for (int i = 1; i < stops.Length; i++)
        {
            if (t > stops[i].Ratio) continue;
            var a = stops[i - 1]; var b = stops[i];
            float f = b.Ratio > a.Ratio ? (t - a.Ratio) / (b.Ratio - a.Ratio) : 0f;
            return (a.R + (b.R - a.R) * f, a.G + (b.G - a.G) * f, a.B + (b.B - a.B) * f, a.A + (b.A - a.A) * f);
        }
        var l = stops[^1];
        return (l.R, l.G, l.B, l.A);
    }

    static (float R, float G, float B, float A) SampleBilinear(GfxBitmap bmp, double u, double v, bool repeat)
    {
        double fx = u - 0.5, fy = v - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        float tx = (float)(fx - x0), ty = (float)(fy - y0);
        float r = 0, g = 0, b = 0, a = 0;
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                int x = x0 + i, y = y0 + j;
                if (repeat) { x = ((x % bmp.Width) + bmp.Width) % bmp.Width; y = ((y % bmp.Height) + bmp.Height) % bmp.Height; }
                else { x = Math.Clamp(x, 0, bmp.Width - 1); y = Math.Clamp(y, 0, bmp.Height - 1); }
                float w = (i == 0 ? 1 - tx : tx) * (j == 0 ? 1 - ty : ty);
                int o = (y * bmp.Width + x) * 4;
                float pa = bmp.Rgba[o + 3] / 255f;
                r += bmp.Rgba[o] / 255f * pa * w; g += bmp.Rgba[o + 1] / 255f * pa * w; b += bmp.Rgba[o + 2] / 255f * pa * w; a += pa * w;
            }
        if (a < 1e-5f) return (0, 0, 0, 0);
        return (r / a, g / a, b / a, a);
    }
}
