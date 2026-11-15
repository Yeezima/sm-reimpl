using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The 1:1 check of a redesigned screen against its concept panel (phase 4): <c>godot.cmd -- --ui-compare-panel &lt;panel screen crop png&gt; --ours &lt;capture&gt; --screen options|armoury|hud --alpha &lt;overlay png&gt;
/// [--alpha-origin x,y --alpha-scale k] [--out dir] [--tag name]</c>. The target is the concept's own 780 x 440 screen (panel coordinates minus (10, 26)); the capture (screen centred, scale = height / 440) is
/// area-averaged onto that grid. Regions are the painted parts (frame, title and crest, side pieces, tabs, plates, chips); text and values that are live in our build (the rows, numbers, class names) and the
/// background the screen sits on are left out, as are labels whose words differ from the concept's. A region is only the part of its polygon where our overlay is opaque, and the coverage column says how much
/// of the polygon that is (so a part our overlay cut away shows as low coverage, not as a hidden miss). Deterministic.
/// </summary>
public static partial class UiComparePanel
{
    static int W = 780, H = 440;
    sealed record Region(string Name, bool[] Mask, double Ssim = 0, double DeMean = 0, double DePixel = 0, int N = 0, double Cover = 1);

    static bool[] Rect(float x0, float y0, float x1, float y1)
    {
        var m = new bool[W * H];
        for (int y = Math.Max(0, (int)MathF.Ceiling(y0 - 26)); y < Math.Min(H, y1 - 26); y++) for (int x = Math.Max(0, (int)MathF.Ceiling(x0 - 10)); x < Math.Min(W, x1 - 10); x++) m[y * W + x] = true;
        return m;
    }

    static bool[] Poly(params (float X, float Y)[] pts)
    {
        var m = new bool[W * H]; var v = pts.Select(p => new Vector2(p.X - 10, p.Y - 26)).ToArray();
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { bool inside = false; for (int i = 0, j = v.Length - 1; i < v.Length; j = i++) if ((v[i].Y > y + .5f) != (v[j].Y > y + .5f) && x + .5f < (v[j].X - v[i].X) * (y + .5f - v[i].Y) / (v[j].Y - v[i].Y) + v[i].X) inside = !inside; m[y * W + x] = inside; }
        return m;
    }

    static bool[] And(bool[] a, bool[] b) { var m = new bool[a.Length]; for (int i = 0; i < m.Length; i++) m[i] = a[i] && b[i]; return m; }
    static bool[] Or(params bool[][] a) { var m = new bool[W * H]; foreach (var x in a) for (int i = 0; i < m.Length; i++) m[i] |= x[i]; return m; }
    static bool[] Minus(bool[] a, params bool[][] b) { var m = (bool[])a.Clone(); foreach (var x in b) for (int i = 0; i < m.Length; i++) if (x[i]) m[i] = false; return m; }

    static bool[] Erode(bool[] a, int r)
    {
        var m = new bool[a.Length];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                if (!a[y * W + x]) continue; bool ok = true;
                for (int dy = -r; dy <= r && ok; dy++) for (int dx = -r; dx <= r; dx++) { int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= W || yy >= H || !a[yy * W + xx]) { ok = false; break; } }
                m[y * W + x] = ok;
            }
        return m;
    }

    public static int Run(string targetPath, string? oursPath, string screen, string? alphaPath, string? outDir, string? tag, Vector2I alphaOrigin, int alphaScale)
    {
        if (!System.IO.File.Exists(targetPath) || oursPath is null || !System.IO.File.Exists(oursPath)) { GD.PrintErr("ui-compare-panel: --ui-compare-panel <target> --ours <capture> --screen options"); return 2; }
        outDir ??= "."; System.IO.Directory.CreateDirectory(outDir); tag ??= "cmp";
        var t = Image.LoadFromFile(targetPath); t.Convert(Image.Format.Rgba8);
        var o = Image.LoadFromFile(oursPath); o.Convert(Image.Format.Rgba8);
        W = t.GetWidth(); H = t.GetHeight();
        float s = o.GetHeight() / (float)H;
        float ox = (o.GetWidth() - W * s) / 2f, oy = 0f;   // the screen is centred
        var ua = OS.GetCmdlineUserArgs(); int si = Array.IndexOf(ua, "--ours-shift");
        if (si >= 0 && si + 1 < ua.Length) { var sh = ua[si + 1].Split(','); ox += float.Parse(sh[0], System.Globalization.CultureInfo.InvariantCulture); oy += float.Parse(sh[1], System.Globalization.CultureInfo.InvariantCulture); }
        var T = new float[W * H * 3]; var O = new float[W * H * 3];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var p = t.GetPixel(x, y); int i = (y * W + x) * 3; T[i] = p.R; T[i + 1] = p.G; T[i + 2] = p.B;
                float ax = ox + x * s, bx = ox + (x + 1) * s, ay = oy + y * s, by = oy + (y + 1) * s; double r = 0, g = 0, b = 0, wsum = 0;
                for (int yy = Math.Max(0, (int)ay); yy < Math.Min(o.GetHeight(), (int)MathF.Ceiling(by)); yy++)
                {
                    float wy = Math.Min(yy + 1, by) - Math.Max(yy, ay);
                    for (int xx = Math.Max(0, (int)ax); xx < Math.Min(o.GetWidth(), (int)MathF.Ceiling(bx)); xx++)
                    { float w = wy * (Math.Min(xx + 1, bx) - Math.Max(xx, ax)); var q = o.GetPixel(xx, yy); r += q.R * w; g += q.G * w; b += q.B * w; wsum += w; }
                }
                O[i] = (float)(r / wsum); O[i + 1] = (float)(g / wsum); O[i + 2] = (float)(b / wsum);
            }
        float[] Lum(float[] c) { var l = new float[W * H]; for (int i = 0; i < l.Length; i++) l[i] = c[i * 3] * 0.2126f + c[i * 3 + 1] * 0.7152f + c[i * 3 + 2] * 0.0722f; return l; }
        var LT = Lum(T); var ssim = SsimMap(LT, Lum(O));
        // Where our overlay is opaque (the overlay's own alpha; panel coordinates = overlay origin + pixel / scale).
        bool[] opaque = Rect(-1000, -1000, 5000, 5000);
        if (alphaPath is not null && System.IO.File.Exists(alphaPath))
        {
            var ov = Image.LoadFromFile(alphaPath); ov.Convert(Image.Format.Rgba8);
            opaque = new bool[W * H];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                {
                    int px = (int)((x + 10 - alphaOrigin.X + 0.5f) * alphaScale), py = (int)((y + 26 - alphaOrigin.Y + 0.5f) * alphaScale);
                    opaque[y * W + x] = px >= 0 && py >= 0 && px < ov.GetWidth() && py < ov.GetHeight() && ov.GetPixel(px, py).A > 0.9f;
                }
        }
        var regions = new List<(string, bool[], bool)>();   // name, polygon mask, uses the overlay's alpha (false: a plate texture, always opaque)
        switch (screen)
        {
            case "options": OptionsRegions(regions); break;
            case "armoury": ArmouryRegions(regions); break;
            default: GD.PrintErr("ui-compare-panel: unknown screen " + screen); return 2;
        }
        var report = new List<Region>();
        void Measure(string name, bool[] mask, int polyN)
        {
            int n = 0; double sSum = 0, dePix = 0; double[] mt = new double[3], mo = new double[3];
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i]) continue; n++; sSum += ssim[i];
                for (int c = 0; c < 3; c++) { mt[c] += T[i * 3 + c]; mo[c] += O[i * 3 + c]; }
                dePix += DeltaE(Lab(T[i * 3], T[i * 3 + 1], T[i * 3 + 2]), Lab(O[i * 3], O[i * 3 + 1], O[i * 3 + 2]));
            }
            if (n == 0) { report.Add(new Region(name, mask, 0, 0, 0, 0, 0)); return; }
            report.Add(new Region(name, mask, sSum / n, DeltaE(Lab((float)(mt[0] / n), (float)(mt[1] / n), (float)(mt[2] / n)), Lab((float)(mo[0] / n), (float)(mo[1] / n), (float)(mo[2] / n))), dePix / n, n, polyN == 0 ? 1 : n / (double)polyN));
        }
        foreach (var (name, poly, usesAlpha) in regions) Measure(name, Erode(usesAlpha ? And(poly, opaque) : poly, 1), poly.Count(b => b));
        var full = Or(report.Select(r => r.Mask).ToArray());
        Measure("FULL", full, full.Count(b => b));
        var lines = new List<string> { $"UICOMPARE {screen} {tag} scale={s:0.0000}", "region | pixels | SSIM | dE mean-colour | dE per-pixel | coverage of polygon" };
        foreach (var r in report) lines.Add($"{r.Name,-24} | {r.N,6} | {r.Ssim:0.000} | {r.DeMean,5:0.00} | {r.DePixel,5:0.00} | {r.Cover * 100,4:0}%");
        int ri = Array.IndexOf(ua, "--rows");
        if (ri >= 0 && ri + 1 < ua.Length && System.IO.File.Exists(ua[ri + 1])) ControlRows(System.IO.File.ReadAllLines(ua[ri + 1]), T, O, lines);
        foreach (var l in lines) GD.Print(l);
        System.IO.File.WriteAllLines(System.IO.Path.Combine(outDir, tag + "-metrics.txt"), lines);
        var heat = Image.CreateEmpty(W, H, false, Image.Format.Rgba8); var ours = Image.CreateEmpty(W, H, false, Image.Format.Rgba8);
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                int i = y * W + x; float d = Math.Min(1f, (Math.Abs(T[i * 3] - O[i * 3]) + Math.Abs(T[i * 3 + 1] - O[i * 3 + 1]) + Math.Abs(T[i * 3 + 2] - O[i * 3 + 2])) / 3f * 4f);
                heat.SetPixel(x, y, full[i] ? new Color(d, d * 0.35f, 0.05f) : new Color(0.04f, 0.05f + 0.1f * LT[i] * 0.3f, 0.16f + 0.2f * d));
                ours.SetPixel(x, y, new Color(O[i * 3], O[i * 3 + 1], O[i * 3 + 2]));
            }
        heat.SavePng(System.IO.Path.Combine(outDir, tag + "-heatmap.png")); ours.SavePng(System.IO.Path.Combine(outDir, tag + "-ours-at-panel-scale.png"));
        var side = Image.CreateEmpty(W, H * 3, false, Image.Format.Rgba8);
        side.BlitRect(t, new Rect2I(0, 0, W, H), new Vector2I(0, 0)); side.BlitRect(ours, new Rect2I(0, 0, W, H), new Vector2I(0, H)); side.BlitRect(heat, new Rect2I(0, 0, W, H), new Vector2I(0, H * 2));
        side.SavePng(System.IO.Path.Combine(outDir, tag + "-side-by-side.png"));
        return 0;
    }

    /// <summary>
    /// The concept's row controls, in its screen pixels: a whole control (x, y, w, h) and the segments scored against ours. A segment is anchored to the left end, the right end or the middle of our control (no stretching
    /// of caps: a wider or narrower control of ours is compared where the concept's art is, end to end); a text box inside a segment (the pill's value) is left out.
    /// </summary>
    sealed record Ctl(string Kind, string Segment, float X, float Y, float W, float H, char Anchor, float Cx, float Cy, float Cw, float Ch, (float X0, float X1)? Text = null);

    static readonly Ctl[] ConceptControls =
    [
        new("slider", "left cap", 231.5f, 152.5f, 140.5f, 16f, 'L', 231.5f, 152.5f, 20f, 16f), new("slider", "middle", 231.5f, 152.5f, 140.5f, 16f, 'M', 285f, 152.5f, 30f, 16f), new("slider", "knob and right cap", 231.5f, 152.5f, 140.5f, 16f, 'R', 338f, 152.5f, 34f, 16f),
        new("pill", "left arrow", 250.5f, 176.5f, 116f, 21f, 'L', 250.5f, 176.5f, 30f, 21f), new("pill", "right arrow", 250.5f, 176.5f, 116f, 21f, 'R', 336.5f, 176.5f, 30f, 21f), new("pill", "top and bottom rim", 250.5f, 176.5f, 116f, 21f, 'M', 290f, 176.5f, 36f, 4f),
        new("switch0", "OFF switch", 565f, 143f, 76f, 23f, 'L', 565f, 143f, 76f, 23f), new("switch1", "ON switch", 267f, 235f, 79f, 24f, 'L', 267f, 235f, 79f, 24f),
    ];

    /// <summary>The row controls (--rows &lt;dump from --dump-rows&gt;): the first of our controls of each kind, compared segment by segment with the concept's own.</summary>
    static void ControlRows(string[] dump, float[] T, float[] O, List<string> lines)
    {
        lines.Add("row control segment (ours -> concept) | pixels | SSIM | dE mean-colour | dE per-pixel");
        int fullW = W, fullH = H;
        foreach (var c in ConceptControls)
        {
            var hit = dump.Select(l => l.Split('|')).FirstOrDefault(p => p[0] == c.Kind);
            string name = $"{c.Kind} {c.Segment}";
            if (hit is null) { lines.Add($"{name,-34} | not on this page"); continue; }
            float F(int i) => float.Parse(hit[i], System.Globalization.CultureInfo.InvariantCulture);
            float ox = F(2), oy = F(3), ow = F(4);
            int cx0 = (int)MathF.Round(c.Cx), cy0 = (int)MathF.Round(c.Cy), cw = (int)MathF.Round(c.Cw), ch = (int)MathF.Round(c.Ch);
            // The same offset inside the control for ours: from the left end, the right end or the middle (sub-pixel shifts keep the concept's art on ours).
            float dx = c.Anchor switch { 'L' => ox + (c.Cx - c.X), 'R' => ox + ow - (c.X + c.W - c.Cx), _ => ox + ow / 2f + (c.Cx - (c.X + c.W / 2f)) };
            float dy = oy + (c.Cy - c.Y);
            // Registration: the dump's rectangle and the drawn pixels differ by a fraction of a pixel; the shift (within 1.5 px) that scores best is used and reported.
            double bestSsim = double.NegativeInfinity, bestDe = 0, bestPx = 0, bestMean = 0; float bsx = 0, bsy = 0; int bn = 0;
            for (float jy = -1.5f; jy <= 1.5f; jy += 0.5f)
                for (float jx = -1.5f; jx <= 1.5f; jx += 0.5f)
                {
                    var t = new float[cw * ch * 3]; var o = new float[cw * ch * 3]; var mask = new bool[cw * ch];
                    float shx = dx - cx0 + jx, shy = dy - cy0 + jy;
                    for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++)
                        {
                            int gx = cx0 + x, gy = cy0 + y;
                            for (int k = 0; k < 3; k++) t[(y * cw + x) * 3 + k] = T[(gy * fullW + gx) * 3 + k];
                            float sx = cx0 + x + shx, sy = cy0 + y + shy;
                            int ax = (int)MathF.Floor(sx), ay = (int)MathF.Floor(sy); float fx = sx - ax, fy = sy - ay;
                            for (int k = 0; k < 3; k++)
                            {
                                float G(int px, int py) => O[(Math.Clamp(py, 0, fullH - 1) * fullW + Math.Clamp(px, 0, fullW - 1)) * 3 + k];
                                o[(y * cw + x) * 3 + k] = (G(ax, ay) * (1 - fx) + G(ax + 1, ay) * fx) * (1 - fy) + (G(ax, ay + 1) * (1 - fx) + G(ax + 1, ay + 1) * fx) * fy;
                            }
                            mask[y * cw + x] = !(c.Text is { } tc && gx >= tc.X0 && gx < tc.X1);
                        }
                    W = cw; H = ch;
                    var lt = new float[cw * ch]; var lo = new float[cw * ch];
                    for (int i = 0; i < lt.Length; i++) { lt[i] = t[i * 3] * 0.2126f + t[i * 3 + 1] * 0.7152f + t[i * 3 + 2] * 0.0722f; lo[i] = o[i * 3] * 0.2126f + o[i * 3 + 1] * 0.7152f + o[i * 3 + 2] * 0.0722f; }
                    var sm = SsimMap(lt, lo);
                    W = fullW; H = fullH;
                    int n = 0; double ss = 0, dep = 0; double[] mt = new double[3], mo = new double[3];
                    for (int i = 0; i < mask.Length; i++)
                    {
                        if (!mask[i]) continue; n++; ss += sm[i];
                        for (int k = 0; k < 3; k++) { mt[k] += t[i * 3 + k]; mo[k] += o[i * 3 + k]; }
                        dep += DeltaE(Lab(t[i * 3], t[i * 3 + 1], t[i * 3 + 2]), Lab(o[i * 3], o[i * 3 + 1], o[i * 3 + 2]));
                    }
                    if (ss / n > bestSsim) { bestSsim = ss / n; bn = n; bestPx = dep / n; bestMean = DeltaE(Lab((float)(mt[0] / n), (float)(mt[1] / n), (float)(mt[2] / n)), Lab((float)(mo[0] / n), (float)(mo[1] / n), (float)(mo[2] / n))); bsx = jx; bsy = jy; }
                }
            lines.Add($"{name + " (" + hit[1] + ")",-34} | {bn,6} | {bestSsim:0.000} | {bestMean,5:0.00} | {bestPx,5:0.00} | shift {bsx:0.0},{bsy:0.0}");
        }
    }

    static void OptionsRegions(List<(string, bool[], bool)> r)
    {
        var interior = Rect(118, 150, 684, 300);
        var tabs = new[] { new Rect2(150, 128, 126, 22), new Rect2(280, 128, 117.5f, 22), new Rect2(403f, 128, 117.5f, 22), new Rect2(524f, 126.5f, 132.5f, 25) };
        var applyRevert = Rect(258, 300, 542, 332);
        var title = Rect(290, 52, 512, 126);
        var tabStrip = Rect(146, 124, 662, 154);
        r.Add(("title plate and crest", And(title, Poly((300, 56), (500, 56), (512, 126), (290, 126))), true));
        r.Add(("frame steel", Minus(Rect(98, 92, 704, 352), interior, title, tabStrip, applyRevert, Rect(60, 110, 100, 330), Rect(700, 110, 745, 330)), true));
        r.Add(("stone and eagle, left", Rect(60, 112, 100, 326), true));
        r.Add(("stone and eagle, right", Rect(700, 112, 742, 326), true));
        string[] names = ["tab GRAPHICS", "tab AUDIO (SOUND)", "tab CONTROLS", "tab INTERFACE (gold)"];
        // Owner 2026-11-15 (text uniformity): every label is live lettering of one face now, so the concept's own lettering is masked out; only the plates are scored.
        var tabLabels = new[] { Rect(180, 129, 246, 149), Rect(316, 129, 364, 149), Rect(425, 129, 498, 149), Rect(552, 129, 632, 149) };
        for (int i = 0; i < 4; i++) r.Add((names[i], Minus(Rect(tabs[i].Position.X + 1, tabs[i].Position.Y + 1, tabs[i].End.X - 1, tabs[i].End.Y - 1), tabLabels[i]), false));
        r.Add(("APPLY (gold)", Minus(Rect(264, 304, 398, 328), Rect(305, 306, 362, 326)), false));
        r.Add(("REVERT", Minus(Rect(416, 304, 536, 328), Rect(446, 306, 506, 326)), false));
        r.Add(("BACK", Minus(Rect(723, 431, 775, 446), Rect(729, 432, 770, 445)), false));
        r.Add(("interior bands", Or(Minus(Rect(130, 154, 670, 160), Rect(0, 0, 0, 0)), Minus(Rect(130, 296, 670, 302), Rect(250, 290, 550, 305))), true));
    }

    static float[] SsimMap(float[] a, float[] b)
    {
        double[] Integral(Func<int, double> f) { var I = new double[(W + 1) * (H + 1)]; for (int y = 0; y < H; y++) { double row = 0; for (int x = 0; x < W; x++) { row += f(y * W + x); I[(y + 1) * (W + 1) + x + 1] = I[y * (W + 1) + x + 1] + row; } } return I; }
        var ia = Integral(i => a[i]); var ib = Integral(i => b[i]); var iaa = Integral(i => (double)a[i] * a[i]); var ibb = Integral(i => (double)b[i] * b[i]); var iab = Integral(i => (double)a[i] * b[i]);
        double Box(double[] I, int x0, int y0, int x1, int y1) => I[y1 * (W + 1) + x1] - I[y0 * (W + 1) + x1] - I[y1 * (W + 1) + x0] + I[y0 * (W + 1) + x0];
        var m = new float[W * H]; const double C1 = 0.01 * 0.01, C2 = 0.03 * 0.03; const int rr = 3;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                int x0 = Math.Max(0, x - rr), x1 = Math.Min(W, x + rr + 1), y0 = Math.Max(0, y - rr), y1 = Math.Min(H, y + rr + 1); double n = (x1 - x0) * (y1 - y0);
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
        double fx = F(X), fy = F(Y), fz = F(Z); return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    static double DeltaE((double L, double A, double B) p, (double L, double A, double B) q) => Math.Sqrt((p.L - q.L) * (p.L - q.L) + (p.A - q.A) * (p.A - q.A) + (p.B - q.B) * (p.B - q.B));
}
