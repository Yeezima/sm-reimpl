using System;
using System.Collections.Generic;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// Phase 5 (owner, 2026-11-15): the main menu rebuilt from the panel 01 v2 reference (<c>owner-ref-panel01-v2.webp</c>), by the phase 3 method on the new image only:
/// <c>godot.cmd -- --build-panel01 &lt;panel01-v2.png&gt; --upscaled &lt;panel01-v2-x4.png&gt; [--out dir]</c>. The picture is upscaled 4x by Real-ESRGAN (model x4plus), the upscale is back-projected onto the picture's own pixels
/// (<see cref="BackProject"/>), and from that one image come <c>overlay-main.png</c> (the concept's own pixels, no grade; alpha = the owner's line, the wordmark's letters, the crest matte, the frame's steel) and the
/// <c>btn-&lt;id&gt;-dark/-gold</c> plate textures with the concept's own painted labels (moved between plates where the text matches). All numbers are screen px of the panel (<see cref="Panel01"/>). Ours.
/// </summary>
public static partial class UiTextureBuilder
{
    public static int BuildPanel01(string? panelPath, string? upscaledPath, string? outDir)
    {
        if (string.IsNullOrEmpty(panelPath) || !System.IO.File.Exists(panelPath) || string.IsNullOrEmpty(upscaledPath) || !System.IO.File.Exists(upscaledPath)) { GD.PrintErr("build-panel01: --build-panel01 <panel01-v2.png> --upscaled <panel01-v2-x4.png> [--out dir]"); return 2; }
        outDir ??= System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "assets", "ui"));
        System.IO.Directory.CreateDirectory(outDir);
        void Save(string name, Image img) { img.SavePng(System.IO.Path.Combine(outDir, name + ".png")); GD.Print($"ui texture {name}: {img.GetWidth()} x {img.GetHeight()}"); }
        var panel = Image.LoadFromFile(panelPath); panel.Convert(Image.Format.Rgba8);
        var up = Image.LoadFromFile(upscaledPath); up.Convert(Image.Format.Rgba8);
        if (up.GetWidth() != panel.GetWidth() * Panel01.Up) { GD.PrintErr("build-panel01: the upscale must be 4x the panel"); return 2; }
        // The overlay's region of the screen, 1x (the picture's own pixels) and 4x (the upscale, then pulled back onto the 1x).
        var scr = panel.GetRegion(new Rect2I(Panel01.Origin, new Vector2I(Panel01.OverlayW, Panel01.ScreenH)));
        var big = up.GetRegion(new Rect2I(Panel01.Origin * Panel01.Up, new Vector2I(Panel01.OverlayW * Panel01.Up, Panel01.ScreenH * Panel01.Up)));
        // The back-projection takes about a minute; its result is kept beside the upscale (deterministic; delete the .bp.png after changing the inputs).
        var cache = upscaledPath + ".bp.png";
        if (System.IO.File.Exists(cache) && System.IO.File.GetLastWriteTimeUtc(cache) > System.IO.File.GetLastWriteTimeUtc(upscaledPath) && System.IO.File.GetLastWriteTimeUtc(cache) > System.IO.File.GetLastWriteTimeUtc(panelPath))
        { big = Image.LoadFromFile(cache); big.Convert(Image.Format.Rgba8); GD.Print("back-projection: cached " + cache); }
        else { BackProject(big, scr); big.SavePng(cache); }
        P01Buttons((Image)big.Duplicate(), Save);
        Save("overlay-main", P01Overlay(big));
        return 0;
    }

    /// <summary>The recess is painted clean where the plates stood: each row between the plates' ends is the amber just left and right of the plates, blended across.</summary>
    static void P01PlatesOut(Image img)
    {
        const float sf = Panel01.Up;
        int x0 = (int)((Panel01.PlateX0 - Panel01.TexSide) * sf), x1 = (int)((Panel01.PlateX1 + Panel01.TexSide) * sf), y0 = (int)(Panel01.SrcTop[0] * sf), y1 = (int)(Panel01.Y(503f) * sf);
        for (int y = y0; y < y1; y++)
        {
            Color Avg(int xa) { Color c = default; for (int k = 0; k < 8; k++) { var q = img.GetPixel(xa + k, y); c.R += q.R; c.G += q.G; c.B += q.B; } return new Color(c.R / 8, c.G / 8, c.B / 8); }
            var left = Avg(x0 - 12); var right = Avg(x1 + 4);
            for (int x = x0; x < x1; x++) { var c = left.Lerp(right, (x - x0) / (float)(x1 - x0)); img.SetPixel(x, y, new Color(c.R, c.G, c.B, 1f)); }
        }
    }

    /// <summary>Makes <c>btn-&lt;id&gt;-dark.png</c> and <c>btn-&lt;id&gt;-gold.png</c> for the five labels from the 4x picture.</summary>
    static void P01Buttons(Image up, Action<string, Image> save)
    {
        const float sf = Panel01.Up;
        var SrcText = Panel01.SrcText; var SrcTop = Panel01.SrcTop;
        const float PlateX0 = Panel01.PlateX0, PlateX1 = Panel01.PlateX1, PlateH = Panel01.PlateH, TexSide = Panel01.TexSide, TexDarkV = Panel01.TexDarkV, TexGoldTop = Panel01.TexGoldTop, TexGoldBottom = Panel01.TexGoldBottom;
        // A copy with every label painted out of its plate (rows of the plate between the text's neighbours, blended).
        var clean = (Image)up.Duplicate();
        for (int i = 0; i < SrcText.Length; i++)
        {
            var t = SrcText[i];
            int tx0 = (int)(t.Position.X * sf), tx1 = (int)(t.End.X * sf), ty0 = (int)(t.Position.Y * sf), ty1 = (int)(t.End.Y * sf);
            for (int y = ty0; y < ty1; y++)
            {
                Color Avg(int xa) { Color c = default; for (int k = 0; k < 10; k++) { var q = up.GetPixel(xa + k, y); c.R += q.R; c.G += q.G; c.B += q.B; } return new Color(c.R / 10, c.G / 10, c.B / 10); }
                var left = Avg(tx0 - 14); var right = Avg(tx1 + 4);
                for (int x = tx0; x < tx1; x++) { var c = left.Lerp(right, (x - tx0) / (float)(tx1 - tx0)); clean.SetPixel(x, y, new Color(c.R, c.G, c.B, 1f)); }
            }
        }
        Rect2I Px(float x, float y, float w, float h) => new((int)MathF.Round(x * sf), (int)MathF.Round(y * sf), (int)MathF.Round(w * sf), (int)MathF.Round(h * sf));
        float tw = PlateX1 - PlateX0 + 2f * TexSide, darkH = PlateH + 2f * TexDarkV, goldH = PlateH + TexGoldTop + TexGoldBottom;
        Image Sheet(Image from, int row)
        {
            bool gold = row == 0;
            float top = gold ? SrcTop[0] : SrcTop[row] - TexDarkV;
            var tex = from.GetRegion(Px(PlateX0 - TexSide, top, tw, gold ? goldH : darkH));
            SideFade(tex, TexSide * sf);
            return tex;
        }
        Color Ink(int r)
        {
            var t = SrcText[r]; double tr = 0, tg = 0, tb = 0; int tn = 0;
            for (int y = (int)(t.Position.Y * sf); y < (int)(t.End.Y * sf); y++) for (int x = (int)(t.Position.X * sf); x < (int)(t.End.X * sf); x++) { var p = up.GetPixel(x, y); if (p.B > 0.82f && p.R > 0.82f) { tr += p.R; tg += p.G; tb += p.B; tn++; } }
            return tn > 0 ? new Color((float)(tr / tn), (float)(tg / tn), (float)(tb / tn)) : new Color(0.93f, 0.93f, 0.9f);
        }
        // The label's pixels (alpha from the blue channel against the blank plate) laid on a blank plate texture at the given text centre (screen px, relative to the texture's origin).
        void Lay(Image dst, Rect2 originPx, int srcRowIdx, Color ink, Vector2 centre)
        {
            var t = SrcText[srcRowIdx];
            int bx0 = (int)(t.Position.X * sf), by0 = (int)(t.Position.Y * sf), bw = (int)(t.Size.X * sf), bh = (int)(t.Size.Y * sf);
            float inkB = Ink(srcRowIdx).B;
            int ox = (int)MathF.Round((centre.X - t.Size.X / 2f - originPx.Position.X) * sf), oy = (int)MathF.Round((centre.Y - t.Size.Y / 2f - originPx.Position.Y) * sf);
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                {
                    var p = up.GetPixel(bx0 + x, by0 + y); var bg = clean.GetPixel(bx0 + x, by0 + y);
                    float a = Mathf.Clamp((p.B - bg.B) / MathF.Max(0.05f, inkB - bg.B), 0f, 1f);
                    a = Mathf.SmoothStep(0.15f, 0.72f, a);   // the glyph's silhouette only (a crisp antialiased edge): the concept's selected label is bright and flat, the others carry a bevel and a halo; as silhouettes in one flat ink all five are identical
                    int dx = ox + x, dy = oy + y;
                    if (dx < 0 || dy < 0 || dx >= dst.GetWidth() || dy >= dst.GetHeight() || a <= 0f) continue;
                    var d = dst.GetPixel(dx, dy);
                    dst.SetPixel(dx, dy, new Color(d.R + (ink.R - d.R) * a, d.G + (ink.G - d.G) * a, d.B + (ink.B - d.B) * a, d.A));
                }
        }
        // Owner rule (2026-11-15): every label of one level renders identically. All five labels are therefore made the same way: each is the concept's own painted label (alpha from its own pixels against the blank plate),
        // laid at the same centre, in the one ink of the dark plates (white of the concept's own dark-plate labels) or of the gold plate, on a blank plate. No label keeps its native pixels with the plate's own halo.
        var goldOrigin = new Rect2(PlateX0 - TexSide, SrcTop[0], tw, goldH);
        int[] srcRow = [1, 0, 2, 3, 4];   // classic = row 1 (second plate), musou = gold plate (row 0), armoury = 2, options = 3, exit = 4
        var darkInk = Ink(2); var goldInk = Ink(0);
        for (int k = 0; k < ButtonLabels.Length; k++)
        {
            var (id, _) = ButtonLabels[k];
            int row = srcRow[k], plateRow = row == 0 ? 1 : row;
            var dark = Sheet(clean, plateRow); var o = new Rect2(PlateX0 - TexSide, SrcTop[plateRow] - TexDarkV, tw, darkH);
            Lay(dark, o, row, darkInk, new Vector2(Panel01.LabelCx, SrcTop[plateRow] + PlateH / 2f + Panel01.DarkLabelDy));
            var gold = Sheet(clean, 0);
            Lay(gold, goldOrigin, row, goldInk, new Vector2(Panel01.LabelCx, Panel01.GoldLabelCy));
            save("btn-" + id + "-dark", dark);
            save("btn-" + id + "-gold", gold);
        }
    }

    /// <summary>The overlay: the picture's own pixels with an alpha (outline, wordmark letters and shadow, crest matte, frame steel), the plates painted out and the nameplate's words painted out.</summary>
    static Image P01Overlay(Image img)
    {
        const float sf = Panel01.Up;
        float X(float u) => Panel01.X(u); float Y(float v) => Panel01.Y(v); float Z(float l) => Panel01.Z(l);
        int W = img.GetWidth(), H = img.GetHeight();
        P01PlatesOut(img);
        var bandAlpha = new float[W * H]; Array.Fill(bandAlpha, 1f);
        // Behind the wordmark and the crest the concept's grey sky would show as a box on our video: keep only the letters (with their outline and shadow) and the crest.
        {
            int y0 = (int)(Y(98) * sf), y1 = (int)(Y(216) * sf), x0 = (int)(X(28) * sf), x1 = (int)(X(514) * sf);
            var mask = new float[W * H];
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    var p = img.GetPixel(x, y);
                    float max = Mathf.Max(p.R, Mathf.Max(p.G, p.B)), min = Mathf.Min(p.R, Mathf.Min(p.G, p.B));
                    float sat = max <= 0.001f ? 0f : (max - min) / max;
                    bool lPart = x / sf > X(436);   // the final L is lighter blue: a lower threshold keeps all of it, bar and stem
                    mask[y * W + x] = lPart ? (p.B >= p.R + 0.07f && sat > 0.22f ? 1f : 0f) : (p.B >= p.R + 0.12f && sat > 0.40f ? 1f : 0f);
                }
            int r = (int)Mathf.Round(Z(5) * sf);
            var tmp = new float[W * H]; var dil = new float[W * H];
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { float v = 0f; for (int d = -r; d <= r; d++) v = Mathf.Max(v, mask[y * W + Mathf.Clamp(x + d, 0, W - 1)]); tmp[y * W + x] = v; }
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { float v = 0f; for (int d = -r; d <= r; d++) v = Mathf.Max(v, tmp[Mathf.Clamp(y + d, 0, H - 1) * W + x]); dil[y * W + x] = v; }
            // The letters' drop shadow (the concept's own dark pixels within a wider reach of the letters) is kept too, so the wordmark sits on the video as it sits on the concept.
            int r2 = (int)Mathf.Round(Z(11) * sf);
            var t2 = new float[W * H]; var dil2 = new float[W * H];
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { float v = 0f; for (int d = -r2; d <= r2; d += 2) v = Mathf.Max(v, mask[y * W + Mathf.Clamp(x + d, 0, W - 1)]); t2[y * W + x] = v; }
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { float v = 0f; for (int d = -r2; d <= r2; d += 2) v = Mathf.Max(v, t2[Mathf.Clamp(y + d, 0, H - 1) * W + x]); dil2[y * W + x] = v; }
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    float a = dil[y * W + x];
                    if (a < 1f && dil2[y * W + x] > 0f)
                    {
                        var q2 = img.GetPixel(x, y); float mx = Mathf.Max(q2.R, Mathf.Max(q2.G, q2.B));
                        a = Mathf.Max(a, 1f - Mathf.SmoothStep(0.15f, 0.26f, mx));
                    }
                    if (x >= (int)(X(70) * sf) && x < (int)(X(102) * sf) && a < 1f)
                    {
                        // The wall's dark backing ends in a soft slope, not a step.
                        var q = img.GetPixel(x, y);
                        if (Mathf.Max(q.R, Mathf.Max(q.G, q.B)) < 0.31f && q.B < q.R + 0.1f) a = Mathf.Max(a, 1f - Mathf.SmoothStep(X(76), X(102), x / sf));
                    }
                    else if (x < (int)(X(94) * sf))
                    {
                        var q = img.GetPixel(x, y);
                        if (Mathf.Max(q.R, Mathf.Max(q.G, q.B)) < 0.31f && q.B < q.R + 0.1f) a = 1f;
                    }
                    bandAlpha[y * W + x] = a;
                }
            // Under the wordmark: the concept's sky is keyed out (flood from the sky's own grey); the letters' dark outline, the crest and the steel stay.
            var skyRect = new Rect2I((int)(X(102) * sf), (int)(Y(212) * sf), (int)(Z(396) * sf), (int)(Z(44) * sf));
            var sub = FloodKey(img.GetRegion(skyRect), 48f, 2, 2);
            for (int y = 0; y < sub.GetHeight(); y++)
                for (int x = 0; x < sub.GetWidth(); x++)
                {
                    float cxp = (skyRect.Position.X + x) / sf, cyp = (skyRect.Position.Y + y) / sf;
                    float a = sub.GetPixel(x, y).A;
                    if (cxp > X(148) && cxp < X(378) && cyp < Y(246))
                    {
                        float ex = (cxp - X(262)) / Z(112), ey = (cyp - Y(238)) / Z(26);
                        a *= Mathf.Clamp((1.08f - Mathf.Sqrt(ex * ex + ey * ey)) / 0.12f, 0f, 1f);
                    }
                    bandAlpha[(skyRect.Position.Y + y) * W + skyRect.Position.X + x] = a;
                }
            for (int y = y1; y < skyRect.Position.Y; y++) for (int x = x0; x < x1; x++) bandAlpha[y * W + x] = 1f;
            // The sky band between the wordmark and the frame's top is gone completely; the crest is cut with its own matte (below) and sits over the video.
            for (int y = (int)(Y(216) * sf); y < (int)(Y(243) * sf); y++) for (int x = (int)(X(102) * sf); x < Math.Min(W, (int)(X(500) * sf)); x++) bandAlpha[y * W + x] = 0f;
            P01Seat(img, bandAlpha, W);
            P01Crest(img, bandAlpha, W);
        }
        // The grey haze at the frame's top-right corner and the thin strip down its right side: flooded out from the background's own grey.
        foreach (var (rx, ry, rw, rh, bx, by, tol) in new[] { (410, 200, 100, 70, 95, 4, 34f), (452, 255, 60, 320, 57, 160, 28f) })
        {
            var rr = new Rect2I((int)(X(rx) * sf), (int)(Y(ry) * sf), (int)(Z(rw) * sf), (int)(Z(rh) * sf));
            if (rr.End.X > W) rr.Size = new Vector2I(W - rr.Position.X, rr.Size.Y);
            if (rr.End.Y > H) rr.Size = new Vector2I(rr.Size.X, H - rr.Position.Y);
            var keyed = FloodKey(img.GetRegion(rr), tol, (int)(Z(bx) * sf), (int)(Z(by) * sf));
            for (int y = 0; y < keyed.GetHeight(); y++)
                for (int x = 0; x < keyed.GetWidth(); x++)
                {
                    int i = (rr.Position.Y + y) * W + rr.Position.X + x;
                    bandAlpha[i] = Mathf.Min(bandAlpha[i], keyed.GetPixel(x, y).A);
                }
        }
        // Right of the frame's top-right shoulder the owner's line still holds some of the concept's grey haze beside the steel: outside the frame's outline (2 px margin) nothing is kept there.
        for (int y = (int)(Y(228) * sf); y < Math.Min(H, (int)(Panel01.Frame[14].Y * sf)); y++)
            for (int x = (int)(Panel01.Frame[8].X * sf + 4 * sf); x < W; x++)
                if (SignedDistance(new Vector2(x / sf, y / sf), Panel01.Frame) < -2f) bandAlpha[y * W + x] = 0f;
        // A grey chip of the concept's haze right of the final L, inside the owner's line.
        for (int y = (int)(185 * sf); y < Math.Min(H, (int)(216 * sf)); y++) for (int x = (int)(476 * sf); x < W; x++) bandAlpha[y * W + x] = 0f;
        // The alpha: inside the owner's outline, with a soft feather.
        float feather = Z(1.2f) * sf;   // crisp edge (owner rule: sharp outlines), antialiased over about one pixel
        var A = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float d = SignedDistance(new Vector2(x / sf, y / sf), Panel01.Outline) * sf;   // positive inside
                A[y * W + x] = Mathf.SmoothStep(0f, 1f, Mathf.Clamp(0.5f + d / feather, 0f, 1f)) * bandAlpha[y * W + x];
            }
        P01NameplateOut(img);
        FaithfulFinish(img, A, W, H);
        return img;
    }

    /// <summary>The nameplate's lettering painted out of the frame's bottom bar: each row of the plate between its ends is the blend of the steel just left and right of the words.</summary>
    static void P01NameplateOut(Image img)
    {
        const float sf = Panel01.Up;
        int y0 = (int)(Panel01.Y(547) * sf), y1 = (int)(Panel01.Y(568) * sf), x0 = (int)(Panel01.X(131) * sf), x1 = (int)(Panel01.X(386) * sf);
        for (int y = y0; y < y1; y++)
        {
            Color Avg(int xa) { Color c = default; for (int k = 0; k < 8; k++) { var q = img.GetPixel(xa + k, y); c.R += q.R; c.G += q.G; c.B += q.B; } return new Color(c.R / 8, c.G / 8, c.B / 8); }
            var left = Avg(x0 - 10); var right = Avg(x1 + 2);
            for (int x = x0; x < x1; x++)
            {
                float t = (x - x0) / (float)(x1 - x0);
                float grain = 1f + 0.035f * Mathf.Sin(x * 0.9f + y * 1.7f) * Mathf.Sin(x * 0.31f);   // a touch of grain so the patch is not a smooth band
                var c = left.Lerp(right, t);
                float edge = Mathf.Clamp(Mathf.Min(x - x0, x1 - 1 - x) / (6f * sf), 0f, 1f);
                var old = img.GetPixel(x, y);
                img.SetPixel(x, y, old.Lerp(new Color(c.R * grain, c.G * grain, c.B * grain, 1f), edge));
            }
        }
    }

    /// <summary>The crest's seat: between and above the frame's top shoulders the keyed sky is replaced by the frame's steel (everything inside the frame polygon) and by the concept's own dark backing of the notch.</summary>
    static void P01Seat(Image img, float[] alpha, int W)
    {
        const float sf = Panel01.Up;
        float X(float u) => Panel01.X(u); float Y(float v) => Panel01.Y(v);
        int x0 = (int)(X(60) * sf), x1 = Math.Min(W, (int)(X(498) * sf)), y0 = (int)(Y(216) * sf), y1 = (int)(Y(262) * sf);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                float cx = x / sf, cy = y / sf; int i = y * W + x;
                if (SignedDistance(new Vector2(cx, cy), Panel01.Frame) > 0f) { alpha[i] = 1f; continue; }
                if (cx < X(102)) { alpha[i] = Mathf.Min(alpha[i], 1f - Mathf.SmoothStep(X(76), X(102), cx)); continue; }
                if (cx > X(150) && cx < X(366) && cy > Y(222) && cy < Y(252))
                {
                    var p = img.GetPixel(x, y); float lum = p.R * 0.3f + p.G * 0.59f + p.B * 0.11f;
                    float dark = 1f - Mathf.SmoothStep(0.26f, 0.38f, lum);
                    float top = Mathf.SmoothStep(Y(222), Y(230), cy), side = Mathf.SmoothStep(X(150), X(156), cx) * (1f - Mathf.SmoothStep(X(360), X(366), cx));
                    alpha[i] = Mathf.Max(alpha[i], dark * top * side);
                }
            }
    }

    /// <summary>The skull and wings, matted from the picture: the crest is the warm or bright metal against the frame notch's dark grey backing, closed over the eye sockets and feather gaps and softened a pixel.</summary>
    static void P01Crest(Image img, float[] alpha, int W)
    {
        const float sf = Panel01.Up;
        float X(float u) => Panel01.X(u); float Y(float v) => Panel01.Y(v); float Z(float l) => Panel01.Z(l);
        int x0 = (int)(X(136) * sf), x1 = (int)(X(390) * sf), y0 = (int)(Y(208) * sf), y1 = (int)(Y(266) * sf);
        int w = x1 - x0, h = y1 - y0;
        var m = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = img.GetPixel(x0 + x, y0 + y);
                float cx = (x0 + x) / sf, cy = (y0 + y) / sf;
                float lum = p.R * 0.3f + p.G * 0.59f + p.B * 0.11f;
                bool warm = p.R >= p.B + 0.035f && lum > 0.2f, bright = lum > 0.42f && p.R >= p.B - 0.03f;
                bool on = warm || bright;
                if (cy > Y(247) && Mathf.Abs(cx - X(262)) > Z(15)) on = false;   // below the wings only the skull's chin; the frame's top bar is steel, not crest
                if (cy > Y(263)) on = false;
                m[y * w + x] = on ? 1f : 0f;
            }
        float[] Morph(float[] a, bool dilate, int r)
        {
            var o = new float[a.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float v = dilate ? 0f : 1f;
                    for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++)
                    { float q = a[Mathf.Clamp(y + dy, 0, h - 1) * w + Mathf.Clamp(x + dx, 0, w - 1)]; v = dilate ? Mathf.Max(v, q) : Mathf.Min(v, q); }
                    o[y * w + x] = v;
                }
            return o;
        }
        int rr = Math.Max(2, (int)(Z(1.4f) * sf));
        var closed = Morph(Morph(m, true, rr), false, rr);
        // Drop specks: keep only the part connected to the middle (the skull).
        var keep = new bool[w * h]; var stack = new Stack<int>();
        int seed = (h * 45 / 100) * w + (int)((X(262) - X(136)) * sf);
        if (closed[seed] > 0.5f) { stack.Push(seed); keep[seed] = true; }
        while (stack.Count > 0)
        {
            int i = stack.Pop(); int x = i % w, y = i / w;
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int j = ny * w + nx;
                if (!keep[j] && closed[j] > 0.5f) { keep[j] = true; stack.Push(j); }
            }
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float sum = 0f;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) sum += keep[Mathf.Clamp(y + dy, 0, h - 1) * w + Mathf.Clamp(x + dx, 0, w - 1)] ? 1f : 0f;
                float m9 = Mathf.SmoothStep(0.15f, 0.85f, sum / 9f); int ai = (y0 + y) * W + x0 + x; alpha[ai] = Mathf.Max(alpha[ai], m9);
            }
    }
}
