using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// Phase 4b-2 (owner, 2026-11-15: "1:1 match of the background stone and metal outlines and borders to the reference", reference = owner-ref-panel04-v2.png, 825 x 465): the Options frame, plates, tabs and row
/// controls are cut from the v2 reference's OWN pixels (its Real-ESRGAN x4, back-projected onto v2, so scaled back down to v2's size it is v2). Every rectangle below is in v2 pixels. The alpha of the frame is a measured
/// silhouette (polygons, 1 px antialiased at x4: straight, crisp, no haze): the steel, title plate and crest are one mirror-symmetric polygon (measured on the left half of v2's flood-keyed outline, the right half is its
/// mirror), the two stone pieces are v2's own silhouettes (left and right stones are near-mirror images; the measured edge is the inner one of the two, mirrored, so neither shows background). Rebuild:
/// <c>godot.cmd -- --build-ui-textures &lt;concept-4panel.jpg&gt; --panel04 &lt;owner-ref-panel04-v2.png&gt; [--panel04-x4 &lt;its Real-ESRGAN x4&gt;] --panels options</c>.
/// </summary>
public static partial class UiTextureBuilder
{
    /// <summary>The page's concept pixels (its row sizes, pads and lettering are in these) per v2 pixel: the v2 reference is the old 780 x 440 grid at 14/15.</summary>
    public const float GridPerV2 = 14f / 15f;
    /// <summary>The v2 reference's size, and the whole of it as the page's screen (in the page's grid units).</summary>
    public static readonly Vector2 V2Size = new(825f, 465f);
    public static readonly Rect2 V2Screen = new(0f, 0f, 825f * GridPerV2, 465f * GridPerV2);
    /// <summary>A v2 rectangle in the page's grid units.</summary>
    public static Rect2 G(Rect2 r) => new(r.Position * GridPerV2, r.Size * GridPerV2);

    /// <summary>The live parts, in v2 pixels (measured on v2): tab plates (GRAPHICS, AUDIO = SOUND, CONTROLS, INTERFACE gold; each its own width), the rows area, APPLY, REVERT, BACK, the toast chip.</summary>
    public static readonly Rect2[] V2Tabs = [new(144.5f, 108f, 135f, 22.8f), new(284.5f, 108f, 126.5f, 22.8f), new(413.5f, 108f, 127f, 22.8f), new(544.5f, 106.5f, 141.5f, 25f)];
    public static readonly Rect2 V2Rows = new(123.4f, 140.6f, 580f, 147f), V2Apply = new(264.5f, 294.5f, 146.5f, 28f), V2Revert = new(427.5f, 294.5f, 132f, 27f), V2Back = new(755f, 429f, 60f, 20.6f), V2Toast = new(334.5f, 359.5f, 155.3f, 26.5f);
    /// <summary>The overlay's region of v2 (the frame's bounding box with room for the crest).</summary>
    public static readonly Rect2I V2OverlayRegion = new(44, 20, 736, 342);

    /// <summary>The steel, title plate and crest: one polygon, mirror-symmetric about x = 412 (measured on the left half, mirrored).</summary>
    static readonly Vector2[] V2Steel =
    [
        new(412.0f, 33.62f), new(406.88f, 34.38f), new(401.88f, 38.62f), new(401.38f, 42.62f), new(403.12f, 47.62f), new(399.88f, 50.38f),
        new(397.62f, 49.88f), new(394.62f, 46.62f), new(394.62f, 45.38f), new(397.88f, 42.38f), new(396.12f, 40.38f), new(336.88f, 40.38f),
        new(335.88f, 41.38f), new(336.12f, 42.38f), new(337.62f, 43.62f), new(340.12f, 44.12f), new(340.88f, 44.88f), new(340.38f, 46.12f),
        new(343.62f, 48.62f), new(355.38f, 48.62f), new(355.62f, 50.12f), new(348.62f, 50.12f), new(347.88f, 51.12f), new(351.38f, 53.62f),
        new(360.62f, 52.88f), new(360.12f, 54.38f), new(355.88f, 54.62f), new(355.88f, 55.62f), new(361.12f, 57.62f), new(362.12f, 59.12f),
        new(358.38f, 62.38f), new(352.88f, 59.38f), new(349.88f, 62.62f), new(344.12f, 62.88f), new(340.88f, 61.12f), new(340.88f, 58.62f),
        new(339.62f, 57.12f), new(307.38f, 56.88f), new(297.12f, 69.38f), new(294.38f, 71.62f), new(248.62f, 71.88f), new(247.12f, 72.62f),
        new(237.38f, 83.12f), new(234.62f, 84.38f), new(159.38f, 84.62f), new(153.38f, 80.88f), new(123.12f, 80.88f), new(121.38f, 81.88f),
        new(116.62f, 87.38f), new(114.62f, 88.38f), new(94.62f, 88.88f), new(92.62f, 91.38f), new(92.88f, 100.12f), new(100.12f, 100.38f),
        new(102.88f, 98.88f), new(103.62f, 99.62f), new(92.62f, 111.62f), new(93.62f, 114.88f), new(93.88f, 128.62f), new(92.62f, 135.88f),
        new(97.62f, 139.38f), new(99.62f, 142.12f), new(99.38f, 267.38f), new(98.62f, 272.12f), new(92.62f, 277.88f), new(93.12f, 305.62f),
        new(122.12f, 334.38f), new(141.62f, 333.38f), new(152.62f, 333.88f), new(156.88f, 332.88f), new(181.88f, 333.12f), new(190.62f, 341.12f),
        new(208.88f, 341.38f), new(213.88f, 337.38f), new(377.38f, 337.38f), new(382.88f, 342.12f), new(395.62f, 341.88f), new(397.12f, 340.88f),
        new(400.38f, 341.88f), new(401.62f, 340.88f), new(401.62f, 333.12f), new(403.12f, 332.12f), new(406.12f, 334.88f), new(403.62f, 337.88f),
        new(404.12f, 339.38f), new(405.62f, 340.38f), new(408.62f, 338.62f), new(409.62f, 339.62f), new(409.88f, 343.88f), new(412.0f, 344.88f),
        new(414.12f, 343.88f), new(414.38f, 339.62f), new(415.38f, 338.62f), new(418.38f, 340.38f), new(419.88f, 339.38f), new(420.38f, 337.88f),
        new(417.88f, 334.88f), new(420.88f, 332.12f), new(422.38f, 333.12f), new(422.38f, 340.88f), new(423.62f, 341.88f), new(426.88f, 340.88f),
        new(428.38f, 341.88f), new(441.12f, 342.12f), new(446.62f, 337.38f), new(610.12f, 337.38f), new(615.12f, 341.38f), new(633.38f, 341.12f),
        new(642.12f, 333.12f), new(667.12f, 332.88f), new(671.38f, 333.88f), new(682.38f, 333.38f), new(701.88f, 334.38f), new(730.88f, 305.62f),
        new(731.38f, 277.88f), new(725.38f, 272.12f), new(724.62f, 267.38f), new(724.38f, 142.12f), new(726.38f, 139.38f), new(731.38f, 135.88f),
        new(730.12f, 128.62f), new(730.38f, 114.88f), new(731.38f, 111.62f), new(720.38f, 99.62f), new(721.12f, 98.88f), new(723.88f, 100.38f),
        new(731.12f, 100.12f), new(731.38f, 91.38f), new(729.38f, 88.88f), new(709.38f, 88.38f), new(707.38f, 87.38f), new(702.62f, 81.88f),
        new(700.88f, 80.88f), new(670.62f, 80.88f), new(664.62f, 84.62f), new(589.38f, 84.38f), new(586.62f, 83.12f), new(576.88f, 72.62f),
        new(575.38f, 71.88f), new(529.62f, 71.62f), new(526.88f, 69.38f), new(516.62f, 56.88f), new(484.38f, 57.12f), new(483.12f, 58.62f),
        new(483.12f, 61.12f), new(479.88f, 62.88f), new(474.12f, 62.62f), new(471.12f, 59.38f), new(465.62f, 62.38f), new(461.88f, 59.12f),
        new(462.88f, 57.62f), new(468.12f, 55.62f), new(468.12f, 54.62f), new(463.88f, 54.38f), new(463.38f, 52.88f), new(472.62f, 53.62f),
        new(476.12f, 51.12f), new(475.38f, 50.12f), new(468.38f, 50.12f), new(468.62f, 48.62f), new(480.38f, 48.62f), new(483.62f, 46.12f),
        new(483.12f, 44.88f), new(483.88f, 44.12f), new(486.38f, 43.62f), new(487.88f, 42.38f), new(488.12f, 41.38f), new(487.12f, 40.38f),
        new(427.88f, 40.38f), new(426.12f, 42.38f), new(429.38f, 45.38f), new(429.38f, 46.62f), new(426.38f, 49.88f), new(424.12f, 50.38f),
        new(420.88f, 47.62f), new(422.62f, 42.62f), new(422.12f, 38.62f), new(417.12f, 34.38f),
    ];

    /// <summary>The left stone piece (v2's measured silhouette; the right is its mirror about x = 412). The inner side hides under the steel.</summary>
    static readonly Vector2[] V2StoneLeft =
    [
        new(74.2f, 97.5f), new(88.8f, 97.5f), new(89.5f, 93f), new(91.5f, 89.6f), new(101.5f, 89.6f), new(101.5f, 100f), new(106f, 110f), new(106f, 317f), new(89.2f, 316.8f), new(88f, 313.2f), new(81f, 305.2f), new(80f, 296.8f), new(75.2f, 290.2f), new(75f, 253.2f),
        new(69.8f, 246.5f), new(65.8f, 222.2f), new(59.2f, 213.5f), new(57.5f, 204.8f), new(51.8f, 201f), new(53.5f, 193.8f), new(51.8f, 192f), new(51.8f, 132.2f), new(57.8f, 131f), new(60.5f, 124f), new(65.8f, 122f), new(67f, 118.8f), new(69.8f, 118.2f), new(70.2f, 109.5f), new(74f, 103.2f),
    ];

    static Vector2[] MirrorV2(Vector2[] p) => p.Reverse().Select(v => new Vector2(824f - v.X, v.Y)).ToArray();

    /// <summary>Antialiased coverage of the union of polygons (v2 pixels) on a grid of <paramref name="sf"/> pixels per v2 pixel starting at <paramref name="origin"/>: exact horizontal coverage, 4 sub-rows per pixel.</summary>
    static float[] PolygonCoverage(int w, int h, Vector2 origin, float sf, IEnumerable<Vector2[]> polys)
    {
        var cov = new float[w * h]; const int SS = 4;
        foreach (var poly in polys)
        {
            var q = poly.Select(p => (p - origin) * sf).ToArray(); int n = q.Length; var acc = new float[w * h]; var xs = new List<float>();
            for (int y = 0; y < h; y++)
                for (int s = 0; s < SS; s++)
                {
                    float yy = y + (s + 0.5f) / SS; xs.Clear();
                    for (int i = 0, j = n - 1; i < n; j = i++) { var a = q[j]; var b = q[i]; if ((a.Y <= yy) != (b.Y <= yy)) xs.Add(a.X + (yy - a.Y) * (b.X - a.X) / (b.Y - a.Y)); }
                    xs.Sort();
                    for (int k = 0; k + 1 < xs.Count; k += 2)
                    {
                        float x0 = Mathf.Clamp(xs[k], 0f, w), x1 = Mathf.Clamp(xs[k + 1], 0f, w); int i0 = (int)x0, i1 = (int)x1;
                        if (i0 >= w) continue;
                        if (i0 == i1) acc[y * w + i0] += (x1 - x0) / SS;
                        else { acc[y * w + i0] += (i0 + 1 - x0) / SS; for (int i = i0 + 1; i < i1; i++) acc[y * w + i] += 1f / SS; if (i1 < w) acc[y * w + i1] += (x1 - i1) / SS; }
                    }
                }
            for (int i = 0; i < cov.Length; i++) cov[i] = Math.Max(cov[i], Math.Min(1f, acc[i]));
        }
        return cov;
    }

    /// <summary>
    /// Paints the zone <paramref name="z"/> (v2 pixels) clean: each row fades from the plain interior left of the zone (columns <paramref name="l"/>) to the plain interior right of it (columns <paramref name="r"/>), both sampled away from the live parts'
    /// own shadows. Rows between <paramref name="skipY"/> (a line of the art that crosses the sample columns but not the zone) take the baseline of the rows just outside that band, interpolated.
    /// </summary>
    static void PaintLerp(Image img, float sf, Rect2 z, (float X0, float X1) l, (float X0, float X1) r, (float Y0, float Y1)? skipY = null)
    {
        int x0 = (int)MathF.Round(z.Position.X * sf), x1 = (int)MathF.Round(z.End.X * sf), y0 = (int)MathF.Round(z.Position.Y * sf), y1 = (int)MathF.Round(z.End.Y * sf);
        Color Base(int y, (float X0, float X1) c) { int a = (int)MathF.Round(c.X0 * sf), b = (int)MathF.Round(c.X1 * sf); double rr = 0, gg = 0, bb = 0; for (int x = a; x < b; x++) { var q = img.GetPixel(x, y); rr += q.R; gg += q.G; bb += q.B; } int n = b - a; return new Color((float)(rr / n), (float)(gg / n), (float)(bb / n)); }
        int sa = skipY is { } sk ? (int)MathF.Round(sk.Y0 * sf) : -1, sb = skipY is { } sk2 ? (int)MathF.Round(sk2.Y1 * sf) : -1;
        for (int y = y0; y < y1; y++)
        {
            Color lc, rc;
            if (y >= sa && y < sb) { float t = (y - sa) / (float)(sb - sa); lc = Base(sa - 1, l).Lerp(Base(sb, l), t); rc = Base(sa - 1, r).Lerp(Base(sb, r), t); }
            else { lc = Base(y, l); rc = Base(y, r); }
            for (int x = x0; x < x1; x++) { var c = lc.Lerp(rc, (x - x0) / (float)(x1 - x0)); img.SetPixel(x, y, new Color(c.R, c.G, c.B, 1f)); }
        }
    }

    /// <summary>Copies the pixels of the rectangle <paramref name="z"/> (v2 pixels) from <paramref name="dx"/> v2 pixels to the left (a stretch of the art that is plain where the active tab's glow is not).</summary>
    static void CopyShifted(Image img, float sf, Rect2 z, float dx)
    {
        int x0 = (int)MathF.Round(z.Position.X * sf), x1 = (int)MathF.Round(z.End.X * sf), y0 = (int)MathF.Round(z.Position.Y * sf), y1 = (int)MathF.Round(z.End.Y * sf), d = (int)MathF.Round(dx * sf);
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { var q = img.GetPixel(x - d, y); img.SetPixel(x, y, new Color(q.R, q.G, q.B, 1f)); }
    }

    /// <summary>
    /// The lettering of a plate (the boxes in v2 pixels; <paramref name="lightInk"/>: light letters on a dark plate, else dark letters on gold) painted out by diffusion: the letter pixels (past a threshold between the plate and the ink, grown by <paramref name="grow"/> px at x4 for the antialiasing) are refilled
    /// from the plate's own pixels around them (Laplace fill), so the plate's gradient and glow run through the place of the letters; nothing else in the box is touched. A row blend of the neighbours would flatten the glow.
    /// </summary>
    static Image BlankInk(Image src, float sf, IEnumerable<Rect2> boxes, int grow, bool lightInk)
    {
        var dst = (Image)src.Duplicate();
        foreach (var b in boxes)
        {
            int x0 = (int)MathF.Floor(b.Position.X * sf), x1 = (int)MathF.Ceiling(b.End.X * sf), y0 = (int)MathF.Floor(b.Position.Y * sf), y1 = (int)MathF.Ceiling(b.End.Y * sf);
            int w = x1 - x0, h = y1 - y0; var lum = new float[w * h]; var rgb = new float[w * h * 3];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var q = src.GetPixel(x0 + x, y0 + y); int i = y * w + x; rgb[i * 3] = q.R; rgb[i * 3 + 1] = q.G; rgb[i * 3 + 2] = q.B; lum[i] = Lum(q); }
            var sorted = (float[])lum.Clone(); Array.Sort(sorted);
            float med = sorted[sorted.Length / 2], hi = sorted[(int)(sorted.Length * 0.995f)], t = lightInk ? med + 0.22f * (hi - med) : 0.5f * sorted[(int)(sorted.Length * 0.9f)];
            var mask = new bool[w * h]; for (int i = 0; i < mask.Length; i++) mask[i] = lightInk ? lum[i] > t : lum[i] < t;
            var grown = new bool[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { if (!mask[y * w + x]) continue; for (int dy = -grow; dy <= grow; dy++) for (int dx = -grow; dx <= grow; dx++) { int xx = x + dx, yy = y + dy; if (xx >= 0 && yy >= 0 && xx < w && yy < h && dx * dx + dy * dy <= grow * grow) grown[yy * w + xx] = true; } }
            // Start from the mean of the untouched pixels, then relax the masked ones towards their neighbours' average (the unmasked ones are fixed boundary values).
            double mr = 0, mg = 0, mb = 0; int n = 0; for (int i = 0; i < lum.Length; i++) if (!grown[i]) { mr += rgb[i * 3]; mg += rgb[i * 3 + 1]; mb += rgb[i * 3 + 2]; n++; }
            if (n == 0) continue;
            for (int i = 0; i < lum.Length; i++) if (grown[i]) { rgb[i * 3] = (float)(mr / n); rgb[i * 3 + 1] = (float)(mg / n); rgb[i * 3 + 2] = (float)(mb / n); }
            for (int it = 0; it < 900; it++)
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int i = y * w + x; if (!grown[i]) continue;
                    for (int c = 0; c < 3; c++)
                    {
                        float sum = 0; int k = 0;
                        if (x > 0) { sum += rgb[(i - 1) * 3 + c]; k++; } if (x < w - 1) { sum += rgb[(i + 1) * 3 + c]; k++; } if (y > 0) { sum += rgb[(i - w) * 3 + c]; k++; } if (y < h - 1) { sum += rgb[(i + w) * 3 + c]; k++; }
                        rgb[i * 3 + c] = sum / k;
                    }
                }
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int i = y * w + x; if (grown[i]) dst.SetPixel(x0 + x, y0 + y, new Color(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], 1f)); }
        }
        return dst;
    }

    /// <summary>Fades the alpha of a plate texture's top and bottom margins to 0 over <paramref name="top"/> and <paramref name="bottom"/> v2 pixels (the plate's glow and shadow end softly, no rectangle shows round it).</summary>
    static Image VFade(Image tex, float sf, float top, float bottom)
    {
        int w = tex.GetWidth(), h = tex.GetHeight();
        for (int y = 0; y < h; y++)
        {
            float f = Mathf.SmoothStep(0f, 1f, Mathf.Clamp((y + 0.5f) / Math.Max(1f, top * sf), 0f, 1f)) * Mathf.SmoothStep(0f, 1f, Mathf.Clamp((h - y - 0.5f) / Math.Max(1f, bottom * sf), 0f, 1f));
            if (f >= 1f) continue;
            for (int x = 0; x < w; x++) { var q = tex.GetPixel(x, y); tex.SetPixel(x, y, new Color(q.R, q.G, q.B, q.A * f)); }
        }
        return tex;
    }

    /// <summary>Loads the v2 reference and its x4 (given, or a Lanczos enlargement), back-projected onto v2's pixels.</summary>
    static Image LoadV2Up(string v2Path, string? x4Path, out Image one)
    {
        one = Image.LoadFromFile(v2Path); one.Convert(Image.Format.Rgba8);
        Image up;
        if (!string.IsNullOrEmpty(x4Path) && System.IO.File.Exists(x4Path)) { up = Image.LoadFromFile(x4Path); up.Convert(Image.Format.Rgba8); if (up.GetWidth() != one.GetWidth() * 4) up.Resize(one.GetWidth() * 4, one.GetHeight() * 4, Image.Interpolation.Lanczos); }
        else { GD.PrintErr("options v2: no --panel04-x4 given; using a Lanczos enlargement (run Real-ESRGAN x4 on the reference for the exact art)"); up = (Image)one.Duplicate(); up.Resize(one.GetWidth() * 4, one.GetHeight() * 4, Image.Interpolation.Lanczos); }
        BackProject(up, one);
        return up;
    }

    static void OptionsTexturesV2(string v2Path, string? x4Path, Action<string, Image> save)
    {
        var up = LoadV2Up(v2Path, x4Path, out _); const float sf = 4f;
        const float V = 1f / GridPerV2;   // grid px -> v2 px (the pads below are in the page's grid px)
        // Painted labels to blank (v2 px).
        var tabBox = new Rect2[] { new(172f, 111.5f, 80f, 15.5f), new(322f, 111.5f, 76f, 15.5f), new(440f, 111.5f, 76f, 15.5f), new(576f, 111.4f, 80f, 15.4f) };
        var applyBox = new Rect2(312f, 301.8f, 56f, 16.2f); var revertBox = new Rect2(434.5f, 301.5f, 78f, 16.5f); var backBox = new Rect2(768f, 433.5f, 40f, 13f);
        var clean = BlankInk(up, sf, [tabBox[0], tabBox[1], tabBox[2], revertBox, backBox], 6, true);   // dark plates: the light letters refilled from the plate round them
        clean = BlankInk(clean, sf, [tabBox[3], applyBox], 5, false);   // gold plates: the dark letters refilled from the plate round them (keeps the glow)
        float mh = 3f * V;   // texture margin round a plate (glow, bevel): 3 grid px
        Rect2 Pad(Rect2 r, float top, float bottom) => new(r.Position - new Vector2(mh, top * V), r.Size + new Vector2(2 * mh, (top + bottom) * V));
        // Tabs: every tab is a BLANK plate of its own width; the gold plate is INTERFACE's, the dark one AUDIO's (v2 has no other states), stretched by the nine-patch margins on the page.
        string[] tabId = ["graphics", "sound", "controls", "interface"];
        var goldTabRect = Pad(V2Tabs[3], 4f, 5f);
        for (int i = 0; i < 4; i++)
        {
            var rect = Pad(V2Tabs[i], 3f, 3f);
            Image dark = i == 3 ? Cut4(clean, sf, Pad(V2Tabs[1], 3f, 3f), mh) : Cut4(clean, sf, rect, mh);
            if (i == 3) dark.Resize((int)MathF.Round(rect.Size.X * sf), dark.GetHeight(), Image.Interpolation.Lanczos);
            Image gold = Cut4(clean, sf, goldTabRect, mh);
            if (i != 3) gold.Resize((int)MathF.Round(Pad(V2Tabs[i], 4f, 5f).Size.X * sf), gold.GetHeight(), Image.Interpolation.Lanczos);
            save("opt-tab-" + tabId[i] + "-dark", VFade(dark, sf, mh * 0.9f, mh * 0.9f)); save("opt-tab-" + tabId[i] + "-gold", VFade(gold, sf, 4f * V * 0.9f, 5f * V * 0.9f));
        }
        // APPLY (gold) and REVERT (dark) plates, and the blank ones the page lays its live text on.
        var applyRect = Pad(V2Apply, 4f, 5f); var revertRect = Pad(V2Revert, 4f, 4f);
        Image GoldPlate() => VFade(Cut4(clean, sf, applyRect, mh), sf, 4f * V * 0.9f, 5f * V * 0.9f);
        Image DarkPlate() => VFade(Cut4(clean, sf, revertRect, mh), sf, 4f * V * 0.9f, 4f * V * 0.9f);
        save("opt-apply-gold", GoldPlate()); save("opt-apply-dark", DarkPlate());
        save("opt-revert-dark", DarkPlate()); save("opt-revert-gold", GoldPlate());
        save("opt-plate-blank", DarkPlate()); save("opt-plate-gold-blank", GoldPlate());
        // BACK sits on the scene: cut exactly to its plate, alpha = its chamfered outline (hard, antialiased).
        {
            var plate = Cut4(clean, sf, V2Back, 0f); int bw = plate.GetWidth(), bh = plate.GetHeight();
            var oct = new Vector2[] { new(760.2f, 429.1f), new(809.8f, 429.1f), new(815f, 434.4f), new(815f, 444.2f), new(809.8f, 449.6f), new(760.2f, 449.6f), new(755f, 444.2f), new(755f, 434.4f) };
            var cov = PolygonCoverage(bw, bh, V2Back.Position, sf, [oct]);
            for (int y = 0; y < bh; y++) for (int x = 0; x < bw; x++) { var q = plate.GetPixel(x, y); plate.SetPixel(x, y, new Color(q.R, q.G, q.B, cov[y * bw + x])); }
            save("opt-back-dark", plate); save("opt-back-blank", plate);
        }
        // The toast chip (painted SETTINGS APPLIED) and the blank one.
        var toastRect = new Rect2(V2Toast.Position - new Vector2(2f, 2f), V2Toast.Size + new Vector2(4f, 4f));
        save("opt-toast", Cut4(up, sf, toastRect, 0f));
        var toastClean = BlankLabels(up, sf, [new Rect2(347f, 362f, 25f, 22f), new Rect2(372f, 365f, 106f, 15f)]);
        save("opt-toast-blank", Cut4(toastClean, sf, toastRect, 0f));
        OptionsControlTexturesV2(up, sf, save);
        // ---- the overlay: v2's own frame, title plate, crest, stone and eagle pieces, the interior with its tabs, rows and plates painted out; alpha = the measured silhouettes ----
        var full = (Image)up.Duplicate();
        PaintOutZone(full, sf, new Rect2(126f, 137f, 576.5f, 150f), 6f);   // the rows area (text, controls)
        PaintLerp(full, sf, new Rect2(133.5f, 105.8f, 563.5f, 26.8f), (126f, 133f), (699f, 705f));   // the tabs (the separator line below stays)
        PaintLerp(full, sf, new Rect2(538f, 103f, 159f, 2.8f), (699f, 705f), (699f, 705f));   // the active tab's glow above its plate
        CopyShifted(full, sf, new Rect2(541f, 132.6f, 154f, 6.4f), 251f);   // the separator line under the gold tab: the plain stretch under AUDIO and CONTROLS stands in for it
        PaintLerp(full, sf, new Rect2(262f, 289.6f, 150f, 3.4f), (240f, 250f), (574f, 584f));   // the APPLY plate's glow up to the bezel line
        PaintLerp(full, sf, new Rect2(261.2f, 293f, 301.8f, 33f), (240f, 250f), (574f, 584f), (297f, 304.8f));   // APPLY and REVERT: the plain interior of the band either side (its bezel lines stay)
        var reg = V2OverlayRegion;
        var overlay = full.GetRegion(new Rect2I(reg.Position.X * 4, reg.Position.Y * 4, reg.Size.X * 4, reg.Size.Y * 4)); overlay.Convert(Image.Format.Rgba8);
        var coverage = PolygonCoverage(overlay.GetWidth(), overlay.GetHeight(), new Vector2(reg.Position.X, reg.Position.Y), sf, [V2Steel, V2StoneLeft, MirrorV2(V2StoneLeft)]);
        for (int y = 0; y < overlay.GetHeight(); y++) for (int x = 0; x < overlay.GetWidth(); x++) { var p = overlay.GetPixel(x, y); overlay.SetPixel(x, y, new Color(p.R, p.G, p.B, coverage[y * overlay.GetWidth() + x])); }
        save("overlay-options", overlay);
    }

    /// <summary>The row controls cut from v2 (rectangles measured on v2; the text on a control is painted out, the slider's gold fill replaced by the empty channel, the fill and the knob cut apart).</summary>
    static void OptionsControlTexturesV2(Image up, float sf, Action<string, Image> save)
    {
        var pillRect = new Rect2(262f, 187f, 124.5f, 22.5f); var offRect = new Rect2(600f, 152.5f, 80f, 23.5f); var onRect = new Rect2(283f, 252.5f, 81.5f, 22.5f);
        var sliderRect = new Rect2(538.5f, 257f, 129.5f, 19f); var knobRect = new Rect2(629.3f, 257f, 16.5f, 19f);
        var clean = BlankInk(up, sf, [new Rect2(292f, 191f, 68f, 14f), new Rect2(636f, 156.5f, 30f, 15.5f)], 6, true);
        clean = BlankInk(clean, sf, [new Rect2(296f, 257.2f, 25f, 11.8f)], 5, false);
        save("opt-pill", Cut4(clean, sf, pillRect, 0f)); save("opt-switch-off", Cut4(clean, sf, offRect, 0f)); save("opt-switch-on", Cut4(clean, sf, onRect, 0f));
        // The slider: the track with its gold fill replaced by the empty channel (the dark columns right of the knob, tiled), the gold fill on its own, and the knob.
        var track = Cut4(up, sf, sliderRect, 0f); int x0 = (int)MathF.Round(sliderRect.Position.X * sf);
        int Lx(float x) => (int)MathF.Round(x * sf) - x0;
        int srcA = Lx(648f), srcB = Lx(660f), dstA = Lx(545f), dstB = Lx(629.3f);
        var profile = new Color[track.GetHeight()];
        for (int y = 0; y < profile.Length; y++) { Color c = default; for (int x = srcA; x < srcB; x++) { var q = track.GetPixel(x, y); c.R += q.R; c.G += q.G; c.B += q.B; } int n = srcB - srcA; profile[y] = new Color(c.R / n, c.G / n, c.B / n); }
        for (int x = dstA; x < dstB; x++) for (int y = 0; y < profile.Length; y++) track.SetPixel(x, y, profile[y]);
        // The track's right end: at 100 % (the default) the knob sits flush against the end, whose tip is solid in v2's full slider (HUD SCALE at 100 %); that tip replaces the empty channel's hollow end.
        {
            int hx = (int)MathF.Round(278f * sf), hy = (int)MathF.Round(93.5f * sf);   // the full slider's offset from this one (v2 pixels x4): 278 left, 93.5 up
            int xa = Lx(660.5f), yTop = (int)MathF.Round(sliderRect.Position.Y * sf);
            for (int y = 0; y < track.GetHeight(); y++) for (int x = xa; x < track.GetWidth(); x++) { var q = up.GetPixel(x0 + x - hx, yTop + y - hy); track.SetPixel(x, y, new Color(q.R, q.G, q.B, track.GetPixel(x, y).A)); }
        }
        save("opt-slider-track", track);
        var fullArt = Cut4(up, sf, sliderRect, 0f);
        var fill = Image.CreateEmpty(fullArt.GetWidth(), fullArt.GetHeight(), false, Image.Format.Rgba8);
        // The fill is the lit part of the track: the gold and the rails round it, over the full height (the rails glow tan next to the gold, grey in the empty part).
        for (int y = 0; y < fullArt.GetHeight(); y++) for (int x = Lx(545f); x < Lx(629.3f); x++) fill.SetPixel(x, y, fullArt.GetPixel(x, y));
        save("opt-slider-fill", fill.GetRegion(new Rect2I(Lx(545f), 0, Lx(629.3f) - Lx(545f), fill.GetHeight())));
        var knob = Cut4(up, sf, knobRect, 0f);
        int kw = knob.GetWidth(), kh = knob.GetHeight(); float rad = 2f * sf;
        for (int y = 0; y < kh; y++) for (int x = 0; x < kw; x++)
            {
                float dx = Math.Max(Math.Max(rad - x, x - (kw - 1 - rad)), 0f), dy = Math.Max(Math.Max(rad - y, y - (kh - 1 - rad)), 0f);
                float a = Mathf.Clamp(rad + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
                var p = knob.GetPixel(x, y); knob.SetPixel(x, y, new Color(p.R, p.G, p.B, a));
            }
        save("opt-slider-knob", knob);
    }
}
