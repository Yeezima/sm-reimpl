using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The 1:1 check of the main menu against the owner's concept crop (phase 3, owner 2026-11-15): <c>godot.cmd -- --ui-compare &lt;owner-ref-main-menu-crop.png&gt; --ours &lt;1080p capture&gt; [--out dir] [--tag name]</c>.
/// The capture (a 1920 x 1080 main menu, overlay at the window's left edge, scale = height / 606) is area-averaged down onto the crop's 522 x 606 grid (a fixed transform, no fitting), then per region: SSIM (7 x 7 window on
/// luminance), the difference of the regions' mean colours in Lab (dE76) and the mean per-pixel dE76. Regions leave out what the owner ordered different: the button order (the two swapped labels), the motto, the final L,
/// and the background around the overlay (his video replaces the concept's grey ruins). Writes a heat map (|difference| x 4, excluded areas dimmed) and a side-by-side (target | ours | heat map, 2x). Deterministic.
/// </summary>
public static class UiCompare
{
    const int W = UiTextureBuilder.CropW, H = UiTextureBuilder.CropH;

    sealed record Region(string Name, bool[] Mask, double Ssim = 0, double DeMean = 0, double DePixel = 0, int N = 0);

    static bool[] Rect(float x0, float y0, float x1, float y1)
    {
        var m = new bool[W * H];
        for (int y = Math.Max(0, (int)MathF.Ceiling(y0)); y < Math.Min(H, y1); y++) for (int x = Math.Max(0, (int)MathF.Ceiling(x0)); x < Math.Min(W, x1); x++) m[y * W + x] = true;
        return m;
    }

    static bool[] Poly(params (float X, float Y)[] pts)
    {
        var m = new bool[W * H]; var v = pts.Select(p => new Vector2(p.X, p.Y)).ToArray();
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { bool inside = false; for (int i = 0, j = v.Length - 1; i < v.Length; j = i++) if ((v[i].Y > y + .5f) != (v[j].Y > y + .5f) && x + .5f < (v[j].X - v[i].X) * (y + .5f - v[i].Y) / (v[j].Y - v[i].Y) + v[i].X) inside = !inside; m[y * W + x] = inside; }
        return m;
    }

    static bool[] And(bool[] a, bool[] b) { var m = new bool[a.Length]; for (int i = 0; i < m.Length; i++) m[i] = a[i] && b[i]; return m; }
    static bool[] Or(params bool[][] a) { var m = new bool[W * H]; foreach (var x in a) for (int i = 0; i < m.Length; i++) m[i] |= x[i]; return m; }
    static bool[] Not(bool[] a) { var m = new bool[a.Length]; for (int i = 0; i < m.Length; i++) m[i] = !a[i]; return m; }
    static bool[] Minus(bool[] a, params bool[][] b) { var m = (bool[])a.Clone(); foreach (var x in b) for (int i = 0; i < m.Length; i++) if (x[i]) m[i] = false; return m; }

    static bool[] Erode(bool[] a, int r)
    {
        var m = new bool[a.Length];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                if (!a[y * W + x]) continue;
                bool ok = true;
                for (int dy = -r; dy <= r && ok; dy++) for (int dx = -r; dx <= r; dx++) { int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= W || yy >= H || !a[yy * W + xx]) { ok = false; break; } }
                m[y * W + x] = ok;
            }
        return m;
    }

    static bool[] Dilate(bool[] a, int r)
    {
        var m = new bool[a.Length];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                if (!a[y * W + x]) continue;
                for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++) { int xx = x + dx, yy = y + dy; if (xx >= 0 && yy >= 0 && xx < W && yy < H) m[yy * W + xx] = true; }
            }
        return m;
    }

    // Plate rectangles in the crop (the concept's own): the gold plate and the four dark ones.
    static readonly (float Y0, float Y1)[] PlateY = [(287f, 327.5f), (334.6f, 370f), (376.7f, 412f), (419.7f, 455f), (462.9f, 499f)];
    static readonly Rect2[] LabelBox = [new(190, 296, 152, 26), new(194, 339, 142, 24), new(213, 382, 109, 24), new(219, 424, 95, 24), new(239, 468, 53, 25)];

    public static int Run(string targetPath, string? oursPath, string? outDir, string? tag)
    {
        if (!System.IO.File.Exists(targetPath) || oursPath is null || !System.IO.File.Exists(oursPath)) { GD.PrintErr("ui-compare: --ui-compare <target crop> --ours <capture png>"); return 2; }
        outDir ??= "."; System.IO.Directory.CreateDirectory(outDir); tag ??= "cmp";
        var t = Image.LoadFromFile(targetPath); t.Convert(Image.Format.Rgba8);
        var o = Image.LoadFromFile(oursPath); o.Convert(Image.Format.Rgba8);
        if (t.GetWidth() != W || t.GetHeight() != H) { GD.PrintErr($"ui-compare: target must be {W} x {H}"); return 2; }
        float s = o.GetHeight() / (float)H;
        // Ours onto the crop's grid: each target pixel is the area average of the capture pixels it covers (fractional weights).
        var T = new float[W * H * 3]; var O = new float[W * H * 3];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var p = t.GetPixel(x, y); int i = (y * W + x) * 3; T[i] = p.R; T[i + 1] = p.G; T[i + 2] = p.B;
                float ax = x * s, bx = (x + 1) * s, ay = y * s, by = (y + 1) * s; double r = 0, g = 0, b = 0, wsum = 0;
                for (int yy = (int)ay; yy < Math.Min(o.GetHeight(), (int)MathF.Ceiling(by)); yy++)
                {
                    float wy = Math.Min(yy + 1, by) - Math.Max(yy, ay);
                    for (int xx = (int)ax; xx < Math.Min(o.GetWidth(), (int)MathF.Ceiling(bx)); xx++)
                    { float w = wy * (Math.Min(xx + 1, bx) - Math.Max(xx, ax)); var q = o.GetPixel(xx, yy); r += q.R * w; g += q.G * w; b += q.B * w; wsum += w; }
                }
                O[i] = (float)(r / wsum); O[i + 1] = (float)(g / wsum); O[i + 2] = (float)(b / wsum);
            }
        float[] Lum(float[] c) { var l = new float[W * H]; for (int i = 0; i < l.Length; i++) l[i] = c[i * 3] * 0.2126f + c[i * 3 + 1] * 0.7152f + c[i * 3 + 2] * 0.0722f; return l; }
        var LT = Lum(T); var LO = Lum(O);
        var ssim = SsimMap(LT, LO);

        // ---- the regions ----
        var outline = Poly(UiTextureBuilder.OverlayOutlinePoints.Select(p => (p.X, p.Y)).ToArray());
        var motto = Rect(128, 545, 390, 570);
        var labelRows12 = Or(Rect(188, 294, 344, 324), Rect(192, 337, 338, 365));
        var letterL = Rect(436, 118, 512, 216);
        var frameShape = Poly((43, 265), (67, 254), (90, 256), (100, 242), (150, 242), (157, 251), (358, 251), (362, 242), (405, 242), (427, 253), (443, 265), (470, 292), (488, 310), (488, 568), (478, 576), (76, 576), (27, 568), (25, 282));
        var recessRect = Rect(70, 276, 460, 508);
        var plates = Or(PlateY.Select(p => Rect(94, p.Y0, 430, p.Y1)).ToArray());
        var wallShape = Or(Poly((0, 0), (43, 0), (99, 52), (101, 106), (0, 106)), Rect(0, 106, 26, 242), Rect(0, 282, 23, 576), Rect(0, 580, 130, 606));
        var wall = Erode(And(And(wallShape, outline), Not(frameShape)), 2);
        // The wordmark: the target's own letter pixels (blue or the dark outline), left of the final L.
        // The letters (blue) and their dark outline (within 2 px of the blue); the soft drop shadow further out lies on the concept's grey ruins, which our video replaces (ordered difference d), so it is not compared.
        var blue = new bool[W * H];
        for (int y = 118; y < 216; y++) for (int x = 30; x < 436; x++) { int i = (y * W + x) * 3; float r = T[i], b = T[i + 2]; blue[y * W + x] = b >= r + 0.10f; }
        var letter = new bool[W * H]; var near = Dilate(blue, 2);
        for (int y = 118; y < 216; y++) for (int x = 30; x < 436; x++) { int i = (y * W + x) * 3; float mx = Math.Max(T[i], Math.Max(T[i + 1], T[i + 2])); letter[y * W + x] = blue[y * W + x] || (near[y * W + x] && mx < 0.14f); }
        var wordmark = Erode(Dilate(letter, 1), 1);
        var crestMetal = new bool[W * H];
        for (int y = 212; y < 264; y++) for (int x = 148; x < 380; x++) { int i = (y * W + x) * 3; float r = T[i], b = T[i + 2], lum = LT[y * W + x]; float ex = (x - 262f) / 118f, ey = (y - 238f) / 27f; crestMetal[y * W + x] = ex * ex + ey * ey < 1f && ((r >= b + 0.035f && lum > 0.2f) || lum > 0.42f) && !(y > 249 && Math.Abs(x - 262) > 20); }
        var crest = Erode(Dilate(crestMetal, 1), 1);
        var frame = Erode(Minus(And(frameShape, outline), recessRect, motto), 2);
        var recess = Minus(recessRect, plates, Dilate(plates, 0));
        var recessOnly = Erode(Minus(recessRect, Rect(90, 284, 434, 503)), 1);   // the amber the plates stand in (strips beside and above and below the plate zone)
        var regions = new List<(string, bool[])>
        {
            ("wall", wall), ("wordmark", Minus(wordmark, letterL)), ("crest", crest), ("frame", frame), ("recess", recessOnly),
        };
        for (int i = 0; i < 5; i++)
        {
            var rect = Rect(96, PlateY[i].Y0 + 1, 428, PlateY[i].Y1 - 1);
            var lb = Rect(LabelBox[i].Position.X - 2, LabelBox[i].Position.Y - 2, LabelBox[i].End.X + 2, LabelBox[i].End.Y + 2);
            string[] names = ["gold plate (row 1)", "plate row 2", "plate ARMOURY", "plate OPTIONS", "plate EXIT"];
            regions.Add((names[i], Minus(rect, lb)));
            if (i >= 2) regions.Add(("label " + new[] { "", "", "ARMOURY", "OPTIONS", "EXIT" }[i], And(lb, Rect(96, PlateY[i].Y0, 428, PlateY[i].Y1))));
        }
        var full = Minus(Or(regions.Select(r => r.Item2).ToArray()), motto, labelRows12, letterL);
        regions.Add(("FULL", full));

        // ---- the numbers ----
        var report = new List<Region>();
        foreach (var (name, mask) in regions)
        {
            int n = 0; double sSum = 0, dePix = 0; double[] mt = new double[3], mo = new double[3];
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i]) continue;
                n++; sSum += ssim[i];
                for (int c = 0; c < 3; c++) { mt[c] += T[i * 3 + c]; mo[c] += O[i * 3 + c]; }
                dePix += DeltaE(Lab(T[i * 3], T[i * 3 + 1], T[i * 3 + 2]), Lab(O[i * 3], O[i * 3 + 1], O[i * 3 + 2]));
            }
            if (n == 0) { report.Add(new Region(name, mask, 0, 0, 0, 0)); continue; }
            var lt = Lab((float)(mt[0] / n), (float)(mt[1] / n), (float)(mt[2] / n)); var lo = Lab((float)(mo[0] / n), (float)(mo[1] / n), (float)(mo[2] / n));
            report.Add(new Region(name, mask, sSum / n, DeltaE(lt, lo), dePix / n, n));
        }
        var lines = new List<string> { $"UICOMPARE {tag} scale={s:0.0000}", "region | pixels | SSIM | dE mean-colour | dE per-pixel" };
        foreach (var r in report) lines.Add($"{r.Name,-20} | {r.N,6} | {r.Ssim:0.000} | {r.DeMean,5:0.00} | {r.DePixel,5:0.00}");
        foreach (var l in lines) GD.Print(l);
        System.IO.File.WriteAllLines(System.IO.Path.Combine(outDir, tag + "-metrics.txt"), lines);

        // ---- the pictures ----
        var heat = Image.CreateEmpty(W, H, false, Image.Format.Rgba8); var ours = Image.CreateEmpty(W, H, false, Image.Format.Rgba8);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x; float d = (Math.Abs(T[i * 3] - O[i * 3]) + Math.Abs(T[i * 3 + 1] - O[i * 3 + 1]) + Math.Abs(T[i * 3 + 2] - O[i * 3 + 2])) / 3f * 4f;
                d = Math.Min(1f, d);
                heat.SetPixel(x, y, full[i] ? new Color(d, d * 0.35f, 0.05f) : new Color(0.04f, 0.05f + 0.1f * LT[i] * 0.3f, 0.16f + 0.2f * d));
                ours.SetPixel(x, y, new Color(O[i * 3], O[i * 3 + 1], O[i * 3 + 2]));
            }
        heat.SavePng(System.IO.Path.Combine(outDir, tag + "-heatmap.png"));
        ours.SavePng(System.IO.Path.Combine(outDir, tag + "-ours-at-crop-scale.png"));
        var side = Image.CreateEmpty(W * 3, H, false, Image.Format.Rgba8);
        side.BlitRect(t, new Rect2I(0, 0, W, H), new Vector2I(0, 0)); side.BlitRect(ours, new Rect2I(0, 0, W, H), new Vector2I(W, 0)); side.BlitRect(heat, new Rect2I(0, 0, W, H), new Vector2I(W * 2, 0));
        side.Resize(W * 6, H * 2, Image.Interpolation.Lanczos);
        side.SavePng(System.IO.Path.Combine(outDir, tag + "-side-by-side.png"));
        return 0;
    }

    static float[] SsimMap(float[] a, float[] b)
    {
        // 7 x 7 uniform window through integral images (zero padded outside the picture is avoided by clamping the window).
        double[] Integral(Func<int, double> f) { var I = new double[(W + 1) * (H + 1)]; for (int y = 0; y < H; y++) { double row = 0; for (int x = 0; x < W; x++) { row += f(y * W + x); I[(y + 1) * (W + 1) + x + 1] = I[y * (W + 1) + x + 1] + row; } } return I; }
        var ia = Integral(i => a[i]); var ib = Integral(i => b[i]); var iaa = Integral(i => (double)a[i] * a[i]); var ibb = Integral(i => (double)b[i] * b[i]); var iab = Integral(i => (double)a[i] * b[i]);
        double Box(double[] I, int x0, int y0, int x1, int y1) => I[y1 * (W + 1) + x1] - I[y0 * (W + 1) + x1] - I[y1 * (W + 1) + x0] + I[y0 * (W + 1) + x0];
        var m = new float[W * H]; const double C1 = 0.01 * 0.01, C2 = 0.03 * 0.03; const int r = 3;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(W, x + r + 1), y0 = Math.Max(0, y - r), y1 = Math.Min(H, y + r + 1); double n = (x1 - x0) * (y1 - y0);
                double ma = Box(ia, x0, y0, x1, y1) / n, mb = Box(ib, x0, y0, x1, y1) / n;
                double va = Box(iaa, x0, y0, x1, y1) / n - ma * ma, vb = Box(ibb, x0, y0, x1, y1) / n - mb * mb, cov = Box(iab, x0, y0, x1, y1) / n - ma * mb;
                m[y * W + x] = (float)(((2 * ma * mb + C1) * (2 * cov + C2)) / ((ma * ma + mb * mb + C1) * (va + vb + C2)));
            }
        return m;
    }

    static (double L, double A, double B) Lab(float r, float g, float b)
    {
        static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        double R = Lin(r), G = Lin(g), B = Lin(b);
        double X = (0.4124 * R + 0.3576 * G + 0.1805 * B) / 0.95047, Y = 0.2126 * R + 0.7152 * G + 0.0722 * B, Z = (0.0193 * R + 0.1192 * G + 0.9505 * B) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        double fx = F(X), fy = F(Y), fz = F(Z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    static double DeltaE((double L, double A, double B) p, (double L, double A, double B) q) => Math.Sqrt((p.L - q.L) * (p.L - q.L) + (p.A - q.A) * (p.A - q.A) + (p.B - q.B) * (p.B - q.B));
}
