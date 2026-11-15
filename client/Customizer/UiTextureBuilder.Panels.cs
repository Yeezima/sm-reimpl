using System;
using System.Collections.Generic;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// Phase 4 of the UI concept redesign (owner, 2026-11-15: Options, Armoury and the HUD "in one pass, with the same method"): the screens' frames, plates, tabs and chips are cut from the concept's own panels
/// (panel 02 HUD, 03 Armoury, 04 Options; 1x JPEG and its Real-ESRGAN x4), back-projected onto the 1x pixels, with the painted labels moved between plates where our text matches. Every rectangle is in the
/// panel's own coordinates (the panel image is 800 x 480 inside the 1600 x 960 concept; panel 04 is the lower right, 03 the lower left, 02 the upper right).
/// </summary>
public static partial class UiTextureBuilder
{
    /// <summary>Origin of each panel in the 1600 x 960 concept.</summary>
    public static readonly Vector2I Panel02 = new(800, 0), Panel03 = new(0, 480), Panel04 = new(800, 480);

    /// <summary>The 4x back-projected region of a panel: <paramref name="rect"/> in panel coordinates.</summary>
    static Image PanelRegion(Image concept1x, Image concept4x, Vector2I panel, Rect2I rect)
    {
        var crop = concept1x.GetRegion(new Rect2I(panel + rect.Position, rect.Size)); crop.Convert(Image.Format.Rgba8);
        int k = concept4x.GetWidth() / concept1x.GetWidth();
        var up = concept4x.GetRegion(new Rect2I((panel + rect.Position) * k, rect.Size * k)); up.Convert(Image.Format.Rgba8);
        BackProject(up, crop);
        return up;
    }

    /// <summary>A plate's painted label, extracted as an alpha against the blank plate and laid on another plate.</summary>
    sealed class PlateSource
    {
        public Image Up = null!, Clean = null!;   // the region (4x) with and without the labels
        public Vector2I Origin; public float Sf = 4f;
        public Rect2 Label;                       // label box in region coordinates (1x)
        public bool LightInk;                     // light lettering on a dark plate (false: dark lettering on gold)
        public Color Ink;
    }

    /// <summary>The region with the label boxes painted out: each row of a box is the blend of the plate just left and right of it.</summary>
    static Image BlankLabels(Image up, float sf, IEnumerable<Rect2> boxes)
    {
        var clean = (Image)up.Duplicate();
        foreach (var t in boxes)
        {
            int tx0 = (int)(t.Position.X * sf), tx1 = (int)(t.End.X * sf), ty0 = (int)(t.Position.Y * sf), ty1 = (int)(t.End.Y * sf);
            for (int y = ty0; y < ty1; y++)
            {
                Color Avg(int xa) { Color c = default; for (int k = 0; k < 8; k++) { var q = up.GetPixel(xa + k, y); c.R += q.R; c.G += q.G; c.B += q.B; } return new Color(c.R / 8, c.G / 8, c.B / 8); }
                var left = Avg(tx0 - 12); var right = Avg(tx1 + 4);
                for (int x = tx0; x < tx1; x++) { var c = left.Lerp(right, (x - tx0) / (float)(tx1 - tx0)); clean.SetPixel(x, y, new Color(c.R, c.G, c.B, 1f)); }
            }
        }
        return clean;
    }

    static float Lum(Color c) => c.R * 0.3f + c.G * 0.59f + c.B * 0.11f;

    /// <summary>The ink of a label box: the brightest (light ink) or darkest (dark ink) few percent of its pixels, averaged.</summary>
    static Color InkOf(Image up, float sf, Rect2 box, bool light)
    {
        var px = new List<(float L, Color C)>();
        for (int y = (int)(box.Position.Y * sf); y < (int)(box.End.Y * sf); y++) for (int x = (int)(box.Position.X * sf); x < (int)(box.End.X * sf); x++) { var p = up.GetPixel(x, y); px.Add((Lum(p), p)); }
        px.Sort((a, b) => light ? b.L.CompareTo(a.L) : a.L.CompareTo(b.L));
        int n = Math.Max(1, px.Count / 25); double r = 0, g = 0, b = 0;
        for (int i = 0; i < n; i++) { r += px[i].C.R; g += px[i].C.G; b += px[i].C.B; }
        return new Color((float)(r / n), (float)(g / n), (float)(b / n));
    }

    /// <summary>Lays <paramref name="src"/>'s label on <paramref name="dst"/> (a region cut at <paramref name="dstOrigin"/>, 1x panel-region coordinates) centred at <paramref name="centre"/> with <paramref name="ink"/>.</summary>
    static void LayLabel(Image dst, Vector2 dstOrigin, float sf, PlateSource src, Vector2 centre, Color ink, bool inkLight)
    {
        var t = src.Label; int bx0 = (int)(t.Position.X * sf), by0 = (int)(t.Position.Y * sf), bw = (int)(t.Size.X * sf), bh = (int)(t.Size.Y * sf);
        float srcInk = Lum(src.Ink);
        int ox = (int)MathF.Round((centre.X - t.Size.X / 2f - dstOrigin.X) * sf), oy = (int)MathF.Round((centre.Y - t.Size.Y / 2f - dstOrigin.Y) * sf);
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                var p = src.Up.GetPixel(bx0 + x, by0 + y); var bg = src.Clean.GetPixel(bx0 + x, by0 + y);
                float lp = Lum(p), lb = Lum(bg);
                float a = src.LightInk ? (lp - lb) / MathF.Max(0.05f, srcInk - lb) : (lb - lp) / MathF.Max(0.05f, lb - srcInk);
                a = Mathf.SmoothStep(0.06f, 0.94f, Mathf.Clamp(a, 0f, 1f));
                int dx = ox + x, dy = oy + y;
                if (dx < 0 || dy < 0 || dx >= dst.GetWidth() || dy >= dst.GetHeight() || a <= 0f) continue;
                var d = dst.GetPixel(dx, dy);
                dst.SetPixel(dx, dy, new Color(d.R + (ink.R - d.R) * a, d.G + (ink.G - d.G) * a, d.B + (ink.B - d.B) * a, d.A));
            }
    }

    static Image Cut4(Image up, float sf, Rect2 r, float fadeSide)
    {
        var tex = up.GetRegion(new Rect2I((int)MathF.Round(r.Position.X * sf), (int)MathF.Round(r.Position.Y * sf), (int)MathF.Round(r.Size.X * sf), (int)MathF.Round(r.Size.Y * sf)));
        tex.Convert(Image.Format.Rgba8);
        if (fadeSide > 0f) SideFade(tex, fadeSide * sf);
        return tex;
    }

    // ---- panel 04: Options (panel coordinates) ----

    /// <summary>The region of panel 04 the Options overlay is cut from, and the panel's screen (the concept's own 780 x 440 inside the 800 x 480 sheet).</summary>
    public static readonly Rect2I OptionsRegion = new(56, 52, 736, 400);
    public static readonly Rect2I PanelScreen = new(10, 26, 780, 440);
    /// <summary>The live parts in panel 04 coordinates: tab plates (graphics, sound = the AUDIO plate, controls, interface), rows area, APPLY, REVERT, BACK, the toast chip.</summary>
    public static readonly Rect2[] OptionsTabs = [new(150, 128, 126, 22), new(280, 128, 117.5f, 22), new(403f, 128, 117.5f, 22), new(524f, 126.5f, 132.5f, 25)];
    public static readonly Rect2 OptionsRows = new(120, 160, 560, 138), OptionsApply = new(262.5f, 302.5f, 136.5f, 26.5f), OptionsRevert = new(414, 302.5f, 123.5f, 26.5f), OptionsBack = new(721, 429, 56, 19), OptionsToast = new(327.5f, 364, 144, 24.5f);
    static readonly Rect2[] OptionsLabelBox = [new(185, 131, 58, 17), new(321, 131, 38, 17), new(430, 131, 62, 17), new(556, 131, 68, 17)];
    public static readonly Vector2[] OptionsOutline = [new(58, 112), new(100, 100), new(300, 96), new(305, 74), new(330, 56), new(470, 56), new(495, 74), new(500, 96), new(700, 100), new(742, 112), new(742, 330), new(704, 340), new(690, 352), new(420, 352), new(416, 360), new(384, 360), new(380, 352), new(110, 352), new(96, 340), new(58, 330)];

    static void OptionsTextures(Image concept1x, Image concept4x, string? outDir, Action<string, Image> save)
    {
        var region = OptionsRegion; var up = PanelRegion(concept1x, concept4x, Panel04, region); float sf = up.GetWidth() / (float)region.Size.X;
        Vector2 org = new(region.Position.X, region.Position.Y);
        Rect2 R(Rect2 r) => new(r.Position - org, r.Size);   // panel coordinates -> region coordinates
        var labels = new List<Rect2>(); foreach (var l in OptionsLabelBox) labels.Add(R(l));
        var applyLabel = R(new Rect2(311, 308, 44, 16)); var revertLabel = R(new Rect2(451, 308, 50, 16)); var backLabel = R(new Rect2(733, 433, 33, 11));
        labels.Add(applyLabel); labels.Add(revertLabel); labels.Add(backLabel);
        var clean = BlankLabels(up, sf, labels);
        // ---- tab, apply, revert and back textures ----
        PlateSource Src(Rect2 box, bool light, Image from) => new() { Up = up, Clean = clean, Label = box, LightInk = light, Ink = InkOf(from, sf, box, light) };
        var gSrc = Src(R(OptionsLabelBox[0]), true, up); var cSrc = Src(R(OptionsLabelBox[2]), true, up); var iSrc = Src(R(OptionsLabelBox[3]), false, up);
        var aSrc = Src(applyLabel, false, up); var rSrc = Src(revertLabel, true, up); var bSrc = Src(backLabel, true, up);
        Color lightInk = gSrc.Ink, darkInk = iSrc.Ink;
        float mv = 3f, mh = 3f;   // texture margin around a plate (glow, bevel)
        Rect2 Pad(Rect2 r, float top, float bottom) => new(R(r).Position - new Vector2(mh, top), R(r).Size + new Vector2(2 * mh, top + bottom));
        // Tabs: the plate rectangles with a margin; the gold plate is the INTERFACE one.
        var goldTabRect = Pad(OptionsTabs[3], mv + 1, mv + 2);
        string[] tabId = ["graphics", "sound", "controls", "interface"];
        for (int i = 0; i < 4; i++)
        {
            var rect = Pad(OptionsTabs[i], mv, mv);
            Image dark = Cut4(clean, sf, rect, mh), darkWithLabel = Cut4(up, sf, rect, mh);
            Image goldBlank = Cut4(clean, sf, goldTabRect, mh);
            Image gold;
            if (i == 3) { gold = Cut4(up, sf, goldTabRect, mh); }
            else gold = goldBlank;
            if (i == 0 || i == 2) { dark = darkWithLabel; var s0 = i == 0 ? gSrc : cSrc; LayLabel(gold, goldTabRect.Position, sf, s0, R(OptionsTabs[3]).GetCenter() + new Vector2(0, -0.5f), darkInk, false); }
            if (i == 3)
            {
                // The dark INTERFACE plate: the blank AUDIO plate stretched to its width, with the label moved over.
                dark = Cut4(clean, sf, Pad(OptionsTabs[1], mv, mv), mh); dark.Resize((int)MathF.Round(rect.Size.X * sf), dark.GetHeight(), Image.Interpolation.Lanczos);
                LayLabel(dark, rect.Position, sf, iSrc, R(OptionsTabs[i]).GetCenter(), lightInk, true);
            }
            save("opt-tab-" + tabId[i] + "-dark", dark); save("opt-tab-" + tabId[i] + "-gold", gold);
        }
        // APPLY (gold) and REVERT (dark): the concept's own; the other state is the blank plate with the label moved over.
        var applyRect = Pad(OptionsApply, 4, 5); var revertRect = Pad(OptionsRevert, 4, 4);
        var applyGold = Cut4(up, sf, applyRect, mh); var applyDark = Cut4(clean, sf, revertRect, mh);
        var revertDark = Cut4(up, sf, revertRect, mh);
        var revertGold = Cut4(clean, sf, applyRect, mh);
        LayLabel(revertGold, applyRect.Position, sf, rSrc, R(OptionsApply).GetCenter() + new Vector2(0, -0.5f), darkInk, false);
        LayLabel(applyDark, revertRect.Position, sf, aSrc, R(OptionsRevert).GetCenter() + new Vector2(0, -0.5f), lightInk, true);
        save("opt-apply-gold", applyGold); save("opt-apply-dark", applyDark); save("opt-revert-dark", revertDark); save("opt-revert-gold", revertGold);
        var backRect = Pad(OptionsBack, 2, 2);
        save("opt-back-dark", Cut4(up, sf, backRect, 1.5f));
        // The toast chip (painted SETTINGS APPLIED) and the blank one.
        var toastRect = new Rect2(R(OptionsToast).Position - new Vector2(2, 2), R(OptionsToast).Size + new Vector2(4, 4));
        save("opt-toast", Cut4(up, sf, toastRect, 0f));
        var toastBox = new Rect2(R(new Rect2(356, 369, 104, 15)).Position, R(new Rect2(356, 369, 104, 15)).Size);
        var toastClean = BlankLabels(up, sf, [toastBox, R(new Rect2(336, 368, 22, 17))]);
        save("opt-toast-blank", Cut4(toastClean, sf, toastRect, 0f));
        // ---- the overlay: the frame, title plate, crest, stone and eagle pieces, the interior with its labels, tabs, rows and actions painted out ----
        var overlay = (Image)up.Duplicate();
        PaintOutZone(overlay, sf, R(new Rect2(132, 158, 536, 138)), 6f);   // the rows area (text, controls)
        PaintOutZone(overlay, sf, R(new Rect2(146, 123, 516, 27.5f)), 5f);    // the tabs
        PaintOutZone(overlay, sf, R(new Rect2(252, 298, 294, 36)), 5f);    // APPLY and REVERT
        OverlayAlpha(overlay, sf, org);
        save("overlay-options", overlay);
    }

    /// <summary>Paints a rectangle clean: each row is the blend of the pixels just left and right of it (the dark interior's own tone), so live controls can stand on it.</summary>
    static void PaintOutZone(Image img, float sf, Rect2 z, float edge)
    {
        int x0 = (int)(z.Position.X * sf), x1 = (int)(z.End.X * sf), y0 = (int)(z.Position.Y * sf), y1 = (int)(z.End.Y * sf);
        for (int y = y0; y < y1; y++)
        {
            Color Avg(int xa) { Color c = default; for (int k = 0; k < 8; k++) { var q = img.GetPixel(xa + k, y); c.R += q.R; c.G += q.G; c.B += q.B; } return new Color(c.R / 8, c.G / 8, c.B / 8); }
            var left = Avg(x0 - 12); var right = Avg(x1 + 4);
            for (int x = x0; x < x1; x++) { var c = left.Lerp(right, (x - x0) / (float)(x1 - x0)); img.SetPixel(x, y, new Color(c.R, c.G, c.B, 1f)); }
        }
    }

    /// <summary>The alpha of the Options assembly: inside the outline polygon, minus the scene's grey reached by a gradient-limited flood from the polygon's border (the steel's edge stops it).</summary>
    /// <summary>Parts the flood must never enter (inside the steel: the title plate's body, the dark interior).</summary>
    static readonly Vector2[] StoneLeft = [new(62, 121), new(104, 118), new(104, 322), new(96, 322), new(88, 302), new(78, 252), new(75, 216), new(62, 208)];
    static bool Protected(float x, float y, Vector2[] stoneRight) =>
        (x > 300f && x < 500f && y > 83f && y < 125f) || (x > 126f && x < 674f && y > 112f && y < 340f)
        || SignedDistance(new Vector2(x, y), StoneLeft) > 0f || SignedDistance(new Vector2(x, y), stoneRight) > 0f;

    static void OverlayAlpha(Image img, float sf, Vector2 org)
    {
        int W = img.GetWidth(), H = img.GetHeight();
        var poly = new Vector2[OptionsOutline.Length]; for (int i = 0; i < poly.Length; i++) poly[i] = OptionsOutline[i] - org;
        var lum = new float[W * H]; var inside = new bool[W * H]; var dist = new float[W * H];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { lum[y * W + x] = Lum(img.GetPixel(x, y)); float dd = SignedDistance(new Vector2(x / sf, y / sf), poly); dist[y * W + x] = dd; inside[y * W + x] = dd > 0f; }
        var stoneRight = new Vector2[StoneLeft.Length]; for (int i = 0; i < stoneRight.Length; i++) stoneRight[i] = new Vector2(800f - StoneLeft[i].X, StoneLeft[i].Y);
        var prot = new bool[W * H];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) prot[y * W + x] = Protected(x / sf + org.X, y / sf + org.Y, stoneRight);
        var bg = new bool[W * H]; var stack = new Stack<int>();
        // Seeds: inside pixels within 2 px of the polygon edge.
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { if (inside[y * W + x] && dist[y * W + x] < 3.5f) { bg[y * W + x] = true; stack.Push(y * W + x); } }
        const float step = 0.022f, lo = 0.08f, hi = 0.52f;
        while (stack.Count > 0)
        {
            int i = stack.Pop(); int x = i % W, y = i / W;
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int j = ny * W + nx;
                if (bg[j] || !inside[j] || prot[j]) continue;
                if (MathF.Abs(lum[j] - lum[i]) < step && lum[j] > lo && lum[j] < hi) { bg[j] = true; stack.Push(j); }
            }
        }
        float feather = 1.2f * sf;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x; var p = img.GetPixel(x, y);
                float d = dist[i] * sf;
                float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp(0.5f + d / feather, 0f, 1f)) * (bg[i] ? 0f : 1f);
                img.SetPixel(x, y, new Color(p.R, p.G, p.B, a));
            }
        // Close small holes of the keyed background (specks inside the steel): an opaque pixel with at least 6 transparent neighbours of 8 stays as it is; the reverse fills.
    }
}
