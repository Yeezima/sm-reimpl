using System;
using System.Collections.Generic;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The main menu's one lettering (owner rule, 2026-11-15: every text of one level renders identically). A glyph atlas is cut from the concept's own lettering on its DARK plates (CLASSIC PVP, ARMOURY, OPTIONS, EXIT,
/// from the back-projected 4x picture, each glyph with its bevel and halo as an alpha map against the blank plate) and every label (the five rows and STOP MUSOU SERVER) is laid out from it with the concept's own
/// measured gaps (pairs that occur in the concept keep their gap, other pairs the mean, words the concept's word space), one cap line and one ink. The letter D exists only on the gold plate (MUSOU MODE): it is
/// scaled and re-weighted until the gold plate's E matches the dark plates' E in height and coverage, and the same scale and weight are applied to the D. Ours.
/// </summary>
sealed class P01Glyph
{
    public char Ch; public int W, H; public float[] A = []; public float Ink0, Ink1, Cy;   // pixels at 4x: image size, ink extent inside the image, the cap box's centre row
}

sealed class P01Atlas
{
    public readonly Dictionary<char, P01Glyph> Glyphs = new();
    readonly Dictionary<string, (float Sum, int N)> _gaps = new();
    public float MeanGap, Space;

    /// <summary>Cuts one source label: its letters (in order) from the alpha map of its text box.</summary>
    public Dictionary<char, P01Glyph> Cut(string text, float[] a, int bw, int bh, bool record)
    {
        var found = new Dictionary<char, P01Glyph>();
        int top = bh, bot = -1; var colmax = new float[bw];
        for (int y = 0; y < bh; y++) for (int x = 0; x < bw; x++) { float v = a[y * bw + x]; if (v > 0.35f) { top = Math.Min(top, y); bot = Math.Max(bot, y); } colmax[x] = Math.Max(colmax[x], v); }
        float cy = (top + bot + 1) / 2f;
        var runs = new List<(int S, int E)>(); int s = -1;
        for (int x = 0; x <= bw; x++)
        {
            bool on = x < bw && colmax[x] > 0.35f;
            if (on && s < 0) s = x;
            if (!on && s >= 0) { runs.Add((s, x)); s = -1; }
        }
        var letters = new List<(char C, int Word)>(); int word = 0;
        foreach (char c in text) { if (c == ' ') word++; else letters.Add((c, word)); }
        // Specks (narrow runs) go; a glyph split by its own dim bar (the T) is joined again: the closest pair of runs merges until the count is the letters'.
        runs.RemoveAll(r => r.E - r.S < 8 && runs.Count > letters.Count);
        while (runs.Count > letters.Count)
        {
            int best = 0; for (int i = 1; i + 1 < runs.Count; i++) if (runs[i + 1].S - runs[i].E < runs[best + 1].S - runs[best].E) best = i;
            runs[best] = (runs[best].S, runs[best + 1].E); runs.RemoveAt(best + 1);
        }
        if (runs.Count != letters.Count) { GD.PrintErr($"atlas: '{text}' has {letters.Count} letters but {runs.Count} glyph runs"); return found; }
        for (int i = 0; i < runs.Count; i++)
        {
            int left = i == 0 ? 0 : (runs[i - 1].E + runs[i].S) / 2, right = i == runs.Count - 1 ? bw : (runs[i].E + runs[i + 1].S) / 2;
            var g = new P01Glyph { Ch = letters[i].C, W = right - left, H = bh, Ink0 = runs[i].S - left, Ink1 = runs[i].E - left, Cy = cy };
            g.A = new float[g.W * bh];
            for (int y = 0; y < bh; y++) for (int x = 0; x < g.W; x++) g.A[y * g.W + x] = a[y * bw + left + x];
            if (!found.ContainsKey(g.Ch)) found[g.Ch] = g;
            if (i + 1 < runs.Count && record)
            {
                float gap = runs[i + 1].S - runs[i].E;
                if (letters[i].Word != letters[i + 1].Word) Space = gap;
                else { var key = "" + letters[i].C + letters[i + 1].C; _gaps.TryGetValue(key, out var e); _gaps[key] = (e.Sum + gap, e.N + 1); }
            }
        }
        return found;
    }

    public void Finish()
    {
        float sum = 0; int n = 0;
        foreach (var (_, e) in _gaps) { sum += e.Sum; n += e.N; }
        MeanGap = n > 0 ? sum / n : 4f;
    }

    float GapOf(char a, char b) => _gaps.TryGetValue("" + a + b, out var e) ? e.Sum / e.N : MeanGap;

    /// <summary>Lays a text out: returns the alpha canvas (the cap line at its middle row; the ink centred horizontally).</summary>
    public float[]? Compose(string text, int canvasH, out int cw)
    {
        cw = 0;
        var pos = new List<(P01Glyph G, float X)>(); float x = 0; char prev = '\0'; bool space = false;
        foreach (char c in text)
        {
            if (c == ' ') { space = true; continue; }
            if (!Glyphs.TryGetValue(c, out var g)) { GD.PrintErr("atlas: no glyph for " + c); return null; }
            float inkStart = prev == '\0' ? 0f : x + (space ? Space : GapOf(prev, c));
            pos.Add((g, inkStart - g.Ink0)); x = inkStart + (g.Ink1 - g.Ink0); prev = c; space = false;
        }
        float minX = float.MaxValue, maxX = float.MinValue;
        foreach (var (g, px) in pos) { minX = Math.Min(minX, px); maxX = Math.Max(maxX, px + g.W); }
        float inkW = x;
        cw = (int)MathF.Ceiling(inkW + 2f * (-minX > 0 ? -minX : 0) + 2f * (maxX - inkW > 0 ? maxX - inkW : 0)) + 4;
        float margin = Math.Max(-minX, maxX - inkW) + 2f;   // the ink is centred: the same room on both sides
        cw = (int)MathF.Ceiling(inkW + 2f * margin);
        var canvas = new float[cw * canvasH];
        foreach (var (g, px) in pos)
        {
            int ox = (int)MathF.Round(px + margin), oy = (int)MathF.Round(canvasH / 2f - g.Cy);
            for (int y = 0; y < g.H; y++)
                for (int gx = 0; gx < g.W; gx++)
                {
                    int dx = ox + gx, dy = oy + y;
                    if (dx < 0 || dy < 0 || dx >= cw || dy >= canvasH) continue;
                    float a = g.A[y * g.W + gx]; if (a <= 0f) continue;
                    float d = canvas[dy * cw + dx]; canvas[dy * cw + dx] = d + a - d * a;
                }
        }
        return canvas;
    }

    /// <summary>Resamples a glyph by <paramref name="k"/> (bilinear) and applies an alpha power.</summary>
    public static P01Glyph Scaled(P01Glyph g, float k, float power)
    {
        int w = Math.Max(1, (int)MathF.Round(g.W * k)), h = Math.Max(1, (int)MathF.Round(g.H * k));
        var o = new P01Glyph { Ch = g.Ch, W = w, H = h, Ink0 = g.Ink0 * k, Ink1 = g.Ink1 * k, Cy = g.Cy * k, A = new float[w * h] };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float fx = (x + 0.5f) / k - 0.5f, fy = (y + 0.5f) / k - 0.5f; int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy); float tx = fx - x0, ty = fy - y0;
                float V(int xx, int yy) => g.A[Math.Clamp(yy, 0, g.H - 1) * g.W + Math.Clamp(xx, 0, g.W - 1)];
                float v = Mathf.Lerp(Mathf.Lerp(V(x0, y0), V(x0 + 1, y0), tx), Mathf.Lerp(V(x0, y0 + 1), V(x0 + 1, y0 + 1), tx), ty);
                o.A[y * w + x] = MathF.Pow(Math.Clamp(v, 0f, 1f), power);
            }
        return o;
    }

    public static float Coverage(P01Glyph g) { float s = 0; foreach (var v in g.A) s += v; return s; }
    public static float CapHeight(P01Glyph g)
    {
        int top = g.H, bot = -1;
        for (int y = 0; y < g.H; y++) for (int x = 0; x < g.W; x++) if (g.A[y * g.W + x] > 0.35f) { top = Math.Min(top, y); bot = Math.Max(bot, y); }
        return bot - top + 1;
    }
}
