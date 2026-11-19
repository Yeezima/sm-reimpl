using System;
using System.Collections.Generic;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// Phase 3 of the UI concept redesign (owner, 2026-11-15: "a perfect 1:1 rendition" of the main menu concept). The overlay keeps the concept's own pixels (no grade, no regraded wall), the five button plates are
/// painted out of its recess and come back as live buttons that draw the concept's own plates with the concept's own painted labels (<c>btn-&lt;id&gt;-dark</c> / <c>-gold</c>, cut from the 4x upscaled crop; the two
/// labels that swap rows and the gold versions of the others are the concept's label pixels moved onto the blank plate). Every number is in the owner's 522 x 606 crop pixels. Ours (spec "UI concept redesign").
/// </summary>
public static partial class UiTextureBuilder
{
    /// <summary>The button plates in the crop: x range, a dark plate's height, the pitch of the rows and the first row's top (where the live rows stand when there are five).</summary>
    public const float PlateX0 = 94f, PlateX1 = 430f, PlateH = 35.3f, PlatePitch = 42.85f, PlateTop1 = 291.7f;
    /// <summary>Margins of the button textures around the plate rectangle: sides, a dark plate's top and bottom, the gold plate's top and bottom (the gold plate is taller; its glow reaches above and below).</summary>
    public const float TexSide = 4f, TexDarkV = 3.85f, TexGoldTop = 7.7f, TexGoldBottom = 4f;
    /// <summary>The five labels, in the order of the live rows; <c>Id</c> names the texture files.</summary>
    public static readonly (string Id, string Text)[] ButtonLabels = [("classic", "CLASSIC PVP"), ("musou", "WARFRONT"), ("armoury", "ARMOURY"), ("options", "OPTIONS"), ("exit", "EXIT"), ("stop", "STOP WARFRONT SERVER")];

    // Where each label stands in the concept: the plate's top (measured on the crop) and the painted text's box (x, y, w, h), crop pixels. Row 0 is the gold MUSOU MODE plate (top is its glow's top).
    static readonly float[] SrcTop = [284f, 334.6f, 376.7f, 419.7f, 462.9f];
    static readonly Rect2[] SrcText = [new(190, 296, 152, 26), new(194, 339, 142, 24), new(213, 382, 109, 24), new(219, 424, 95, 24), new(239, 468, 53, 25)];
    // Source row of each label id: classic = row 1 (second plate), musou = gold plate (row 0), armoury = 2, options = 3, exit = 4.
    static readonly int[] SrcRow = [1, 0, 2, 3, 4];
    const float LabelCx = 266f;                 // the painted labels are centred 4 px right of the plate's middle (262)
    const float GoldLabelCy = 308.5f, DarkLabelDy = -1.3f;   // the text's middle: on the gold plate, and a dark plate's offset from its middle

    /// <summary>The recess is painted clean where the plates stood: each row between the plates' ends is the amber just left and right of the plates, blended across. The live buttons draw the plates.</summary>
    static void FaithfulPlatesOut(Image img, float sf, float sfy)
    {
        int x0 = (int)((PlateX0 - TexSide) * sf), x1 = (int)((PlateX1 + TexSide) * sf), y0 = (int)(284f * sfy), y1 = (int)(503f * sfy);
        for (int y = y0; y < y1; y++)
        {
            Color Avg(int xa) { Color c = default; for (int k = 0; k < 8; k++) { var q = img.GetPixel(xa + k, y); c.R += q.R; c.G += q.G; c.B += q.B; } return new Color(c.R / 8, c.G / 8, c.B / 8); }
            var left = Avg(x0 - 12); var right = Avg(x1 + 4);
            for (int x = x0; x < x1; x++) { var c = left.Lerp(right, (x - x0) / (float)(x1 - x0)); img.SetPixel(x, y, new Color(c.R, c.G, c.B, 1f)); }
        }
    }

    /// <summary>The faithful finish: the pixels stay the concept's own; only the alpha (the owner's line, the matte) is applied.</summary>
    static void FaithfulFinish(Image img, float[] A, int W, int H)
    {
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var p = img.GetPixel(x, y);
                img.SetPixel(x, y, new Color(p.R, p.G, p.B, A[y * W + x]));
            }
    }

    /// <summary>Makes <c>btn-&lt;id&gt;-dark.png</c> and <c>btn-&lt;id&gt;-gold.png</c> for the five labels from the 4x upscaled crop.</summary>
    static void FaithfulButtons(Image up, Action<string, Image> save)
    {
        up.Convert(Image.Format.Rgba8);
        float sf = up.GetWidth() / (float)CropW;
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
        // Row 2's plate carries a patch of scratches near its right end that the others lack (owner review): that patch is replaced by the same stretch of the OPTIONS plate (same offset from the plate's top), in the plate and its blank copy.
        foreach (var img2 in new[] { up, clean })
            for (int y = (int)(2f * sf); y < (int)(20f * sf); y++)
                for (int x = (int)(377f * sf); x < (int)(417f * sf); x++)
                {
                    float wx = Mathf.SmoothStep(377f, 392f, x / sf) * (1f - Mathf.SmoothStep(410f, 417f, x / sf)), wy = Mathf.SmoothStep(0f, 5f, y / sf) * (1f - Mathf.SmoothStep(14f, 20f, y / sf));
                    int yy = (int)(SrcTop[1] * sf) + y; var a = img2.GetPixel(x, yy); var b = up.GetPixel(x, (int)(SrcTop[3] * sf) + y);
                    img2.SetPixel(x, yy, a.Lerp(b, wx * wy));
                }
        Rect2I Px(float x, float y, float w, float h) => new((int)MathF.Round(x * sf), (int)MathF.Round(y * sf), (int)MathF.Round(w * sf), (int)MathF.Round(h * sf));
        float tw = PlateX1 - PlateX0 + 2f * TexSide, darkH = PlateH + 2f * TexDarkV, goldH = PlateH + TexGoldTop + TexGoldBottom - 0.0f;
        Image Sheet(Image from, int row)
        {
            bool gold = row == 0;
            float top = gold ? SrcTop[0] : SrcTop[row] - TexDarkV;
            var tex = from.GetRegion(Px(PlateX0 - TexSide, top, tw, gold ? goldH + 0f : darkH));
            SideFade(tex, TexSide * sf);
            return tex;
        }
        Color Ink(int r)
        {
            var t = SrcText[r]; double tr = 0, tg = 0, tb = 0; int tn = 0;
            for (int y = (int)(t.Position.Y * sf); y < (int)(t.End.Y * sf); y++) for (int x = (int)(t.Position.X * sf); x < (int)(t.End.X * sf); x++) { var p = up.GetPixel(x, y); if (p.B > 0.82f && p.R > 0.82f) { tr += p.R; tg += p.G; tb += p.B; tn++; } }
            return tn > 0 ? new Color((float)(tr / tn), (float)(tg / tn), (float)(tb / tn)) : new Color(0.93f, 0.93f, 0.9f);
        }
        // The label's pixels (alpha from the blue channel against the blank plate) laid on a blank plate texture at the given text centre (crop pixels, relative to the texture's crop origin).
        void Lay(Image dst, Rect2 originCrop, int srcRowIdx, int destRow, Vector2 centre)
        {
            var t = SrcText[srcRowIdx];
            int bx0 = (int)(t.Position.X * sf), by0 = (int)(t.Position.Y * sf), bw = (int)(t.Size.X * sf), bh = (int)(t.Size.Y * sf);
            // The text colour of the plate it goes onto (the white of the concept's own label there: MUSOU MODE on the gold plate, CLASSIC PVP on a dark one), the alpha from this label's own pixels.
            var ink = Ink(destRow); float inkB = Ink(srcRowIdx).B;
            int ox = (int)MathF.Round((centre.X - t.Size.X / 2f - originCrop.Position.X) * sf), oy = (int)MathF.Round((centre.Y - t.Size.Y / 2f - originCrop.Position.Y) * sf);
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                {
                    var p = up.GetPixel(bx0 + x, by0 + y); var bg = clean.GetPixel(bx0 + x, by0 + y);
                    float a = Mathf.Clamp((p.B - bg.B) / MathF.Max(0.05f, inkB - bg.B), 0f, 1f);
                    // Soft in, hard out: a little of the antialiasing is kept, the plate's own grain under the letters' faint halo is not.
                    a = Mathf.SmoothStep(0.06f, 0.94f, a);
                    int dx = ox + x, dy = oy + y;
                    if (dx < 0 || dy < 0 || dx >= dst.GetWidth() || dy >= dst.GetHeight() || a <= 0f) continue;
                    var d = dst.GetPixel(dx, dy);
                    dst.SetPixel(dx, dy, new Color(d.R + (ink.R - d.R) * a, d.G + (ink.G - d.G) * a, d.B + (ink.B - d.B) * a, d.A));
                }
        }
        var goldOrigin = new Rect2(PlateX0 - TexSide, SrcTop[0], tw, goldH);
        for (int k = 0; k < SrcRow.Length; k++)
        {
            var (id, _) = ButtonLabels[k];
            int row = SrcRow[k];
            // Dark: the concept's own plate with its label, or (MUSOU MODE has no dark plate) the second row's blank plate with the gold plate's label moved onto it.
            Image dark;
            if (row == 0) { dark = Sheet(clean, 1); var o = new Rect2(PlateX0 - TexSide, SrcTop[1] - TexDarkV, tw, darkH); Lay(dark, o, 0, 1, new Vector2(LabelCx, SrcTop[1] + PlateH / 2f + DarkLabelDy)); }
            else dark = Sheet(up, row);
            // Gold: the concept's own gold plate (MUSOU MODE), or the blank gold plate with this label moved onto it.
            Image gold;
            if (row == 0) gold = Sheet(up, 0);
            else { gold = Sheet(clean, 0); Lay(gold, goldOrigin, row, 0, new Vector2(LabelCx, GoldLabelCy)); }
            save("btn-" + id + "-dark", dark);
            save("btn-" + id + "-gold", gold);
        }
    }

    /// <summary>Fades the alpha of a texture's left and right <paramref name="px"/> columns to 0 (the plate meets the recess's amber without a seam).</summary>
    static void SideFade(Image tex, float px)
    {
        int w = tex.GetWidth(), h = tex.GetHeight(), n = Math.Max(1, (int)px);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < n; x++)
            {
                float a = Mathf.SmoothStep(0f, 1f, x / (float)n);
                var l = tex.GetPixel(x, y); tex.SetPixel(x, y, new Color(l.R, l.G, l.B, a));
                var r = tex.GetPixel(w - 1 - x, y); tex.SetPixel(w - 1 - x, y, new Color(r.R, r.G, r.B, a));
            }
    }

    /// <summary>The frame's outer outline in the crop (the notch between the shoulders is open at the top, the crest sits in it); the comparison tool uses the same polygon.</summary>
    public static readonly Vector2[] FramePolygon = [new(43, 265), new(67, 254), new(90, 256), new(100, 242), new(150, 242), new(157, 251), new(358, 251), new(362, 242), new(405, 242), new(427, 253), new(443, 265), new(470, 292), new(488, 310), new(488, 568), new(478, 576), new(76, 576), new(27, 568), new(25, 282)];

    /// <summary>
    /// The crest's seat (owner 2026-11-15): between and above the frame's top shoulders the keyed sky is replaced by the frame's steel (everything inside <see cref="FramePolygon"/>, which the sky key had eaten) and by the
    /// concept's own dark backing of the notch (its dark pixels between the shoulders, feathered at the top), so the crest sits on the frame as in the concept; the grey sky and ruins beyond stay out.
    /// </summary>
    static void FaithfulSeat(Image img, float[] alpha, int W, float sf, float sfy)
    {
        int x0 = (int)(60 * sf), x1 = (int)(498 * sf), y0 = (int)(216 * sfy), y1 = (int)(262 * sfy);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                float cx = x / sf, cy = y / sfy;
                int i = y * W + x;
                if (SignedDistance(new Vector2(cx, cy), FramePolygon) > 0f) { alpha[i] = 1f; continue; }
                if (cx < 102f) { alpha[i] = Mathf.Min(alpha[i], 1f - Mathf.SmoothStep(76f, 102f, cx)); continue; }   // the wall's dark backing fades out instead of ending in a vertical cut
                if (cx > 150f && cx < 366f && cy > 222f && cy < 252f)
                {
                    var p = img.GetPixel(x, y); float lum = p.R * 0.3f + p.G * 0.59f + p.B * 0.11f;
                    float dark = 1f - Mathf.SmoothStep(0.26f, 0.38f, lum);
                    float top = Mathf.SmoothStep(222f, 230f, cy), side = Mathf.SmoothStep(150f, 156f, cx) * (1f - Mathf.SmoothStep(360f, 366f, cx));
                    alpha[i] = Mathf.Max(alpha[i], dark * top * side);
                }
            }
    }

    static Image? _bpCache; static string? _bpKey;

    /// <summary>
    /// The upscaled crop made consistent with the crop (iterative back-projection): the upscaler's detail is kept, but the picture is corrected until its k x k block averages equal the owner's own pixels, so scaled back down
    /// to the concept's size it IS the concept (the upscale's drift in colour and tone is removed). A copy is returned (callers change it).
    /// </summary>
    static Image LoadBackProjected(string path, Image crop)
    {
        if (_bpCache is null || _bpKey != path)
        {
            var up = Image.LoadFromFile(path); up.Convert(Image.Format.Rgba8);
            BackProject(up, crop);
            _bpCache = up; _bpKey = path;
        }
        return (Image)_bpCache.Duplicate();
    }

    /// <summary>Iterative back-projection of <paramref name="up"/> (k times the size of <paramref name="crop"/>) onto the crop, in place.</summary>
    public static void BackProject(Image up, Image crop)
    {
        int w = crop.GetWidth(), h = crop.GetHeight(), k = up.GetWidth() / w, W = up.GetWidth(), H = up.GetHeight();
        var u = new float[W * H * 3]; var c = new float[w * h * 3];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { var p = up.GetPixel(x, y); int i = (y * W + x) * 3; u[i] = p.R; u[i + 1] = p.G; u[i + 2] = p.B; }
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var p = crop.GetPixel(x, y); int i = (y * w + x) * 3; c[i] = p.R; c[i + 1] = p.G; c[i + 2] = p.B; }
        var d = new float[w * h * 3];
        for (int it = 0; it < 4; it++)
        {
            double worst = 0;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                for (int ch = 0; ch < 3; ch++)
                {
                    float sum = 0; for (int dy = 0; dy < k; dy++) for (int dx = 0; dx < k; dx++) sum += u[((y * k + dy) * W + x * k + dx) * 3 + ch];
                    float diff = c[(y * w + x) * 3 + ch] - sum / (k * k); d[(y * w + x) * 3 + ch] = diff; worst = Math.Max(worst, Math.Abs(diff));
                }
            GD.Print($"back-projection pass {it}: largest block error {worst:0.0000}");
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float fx = (x + 0.5f) / k - 0.5f, fy = (y + 0.5f) / k - 0.5f; int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy); float tx = fx - x0, ty = fy - y0;
                    int xa = Math.Clamp(x0, 0, w - 1), xb = Math.Clamp(x0 + 1, 0, w - 1), ya = Math.Clamp(y0, 0, h - 1), yb = Math.Clamp(y0 + 1, 0, h - 1);
                    for (int ch = 0; ch < 3; ch++)
                    {
                        float v = Mathf.Lerp(Mathf.Lerp(d[(ya * w + xa) * 3 + ch], d[(ya * w + xb) * 3 + ch], tx), Mathf.Lerp(d[(yb * w + xa) * 3 + ch], d[(yb * w + xb) * 3 + ch], tx), ty);
                        u[(y * W + x) * 3 + ch] = Mathf.Clamp(u[(y * W + x) * 3 + ch] + v, 0f, 1f);
                    }
                }
        }
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i = (y * W + x) * 3; up.SetPixel(x, y, new Color(u[i], u[i + 1], u[i + 2], 1f)); }
    }
}
