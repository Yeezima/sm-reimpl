using System;
using System.Collections.Generic;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// Phase 4d of the UI concept redesign: the Armoury (panel 03). Same method as Options: the concept's own pixels (1x JPEG back-projected into its Real-ESRGAN x4), every rectangle in the concept's own 780 x 440 screen
/// coordinates (the panel's screen sits at (10, 26) in its 800 x 480 sheet), live content painted out of the plates, the words laid by the page in the one live face. Pieces (client/assets/ui):
/// <c>arm-title</c> (SM-REIMPL plate, ARMOURY lettering, wings and skull; fixed painted wordmark), <c>overlay-armoury</c> (the rail's frame with the weapon row, the header bar and the stone panels, interior rows and tiles
/// painted out), <c>arm-row-dark/-gold</c>, <c>arm-tile-dark/-gold</c>, <c>arm-icon-tile</c>, <c>arm-card</c> (+ <c>arm-meter-track/-fill</c>), <c>arm-confirm</c>, <c>arm-wings</c>, <c>arm-badge</c>, <c>arm-back</c> and the concept's icons
/// (<c>arm-ico-*</c>). Ours (spec "UI concept redesign").
/// </summary>
public static partial class UiTextureBuilder
{
    // ---- the concept's screen coordinates (panel 03 minus (10, 26)) ----
    public static readonly Rect2 ArmTitle = new(40, 0, 252, 104);
    public static readonly Rect2 ArmRail = new(10, 72, 288, 304);                        // the overlay's cut
    public static readonly Rect2 ArmHeader = new(50, 100, 208, 21);                       // the CLASS bar (the page's step pills stand here)
    public static readonly Rect2[] ArmRows = [new(52, 122.5f, 209, 28), new(58, 152, 198, 23.5f), new(58, 177, 198, 22.5f), new(58, 202, 198, 23)];
    public static readonly Rect2 ArmWeaponIcon = new(48, 238, 78, 36), ArmWeaponName = new(124, 245, 146, 23);
    public static readonly Rect2[] ArmTiles = [new(42, 280, 76, 68), new(122, 281, 72, 66), new(199, 281, 72, 66)];
    public static readonly Rect2 ArmConfirm = new(32, 371, 232, 42), ArmWings = new(98, 408, 112, 22), ArmBadge = new(632, 8, 138, 28), ArmBack = new(650, 382, 118, 36);
    public static readonly Rect2 ArmCard = new(549, 93, 214, 67);                          // the first card; the next ones come at ArmCardPitch
    public const float ArmCardPitch = 78f;
    public static readonly Rect2 ArmCardIcon = new(561, 100, 59, 50), ArmCardText = new(628, 101, 120, 48), ArmCardMeter = new(627, 282, 124, 22);   // card 3's meter: absolute screen coordinates

    static Image Region(Image up, Rect2 r) => Cut4(up, 4f, r, 0f);

    /// <summary>Paints <paramref name="r"/> flat: a vertical blend of two probe colours (taken at the given 4x pixel positions).</summary>
    static void FillVertical(Image img, float sf, Rect2 r, Color top, Color bottom)
    {
        int x0 = (int)(r.Position.X * sf), x1 = (int)(r.End.X * sf), y0 = (int)(r.Position.Y * sf), y1 = (int)(r.End.Y * sf);
        for (int y = y0; y < y1; y++) { var c = top.Lerp(bottom, (y - y0) / (float)Math.Max(1, y1 - y0 - 1)); for (int x = x0; x < x1; x++) img.SetPixel(x, y, new Color(c.R, c.G, c.B, 1f)); }
    }

    /// <summary>Paints <paramref name="r"/> row by row as the blend of the pixel columns at <paramref name="leftX"/> and <paramref name="rightX"/> (screen x), so scan lines and gradients of the plate carry on across it.</summary>
    static void FillScan(Image img, float sf, Rect2 r, float leftX, float rightX)
    {
        int x0 = (int)(r.Position.X * sf), x1 = (int)(r.End.X * sf), y0 = (int)(r.Position.Y * sf), y1 = (int)(r.End.Y * sf);
        int lx = (int)(leftX * sf), rx = (int)(rightX * sf);
        for (int y = y0; y < y1; y++)
        {
            Color Avg(int xa) { Color c = default; for (int k = 0; k < 6; k++) { var q = img.GetPixel(xa + k, y); c.R += q.R; c.G += q.G; c.B += q.B; } return new Color(c.R / 6, c.G / 6, c.B / 6); }
            var left = Avg(lx); var right = Avg(rx);
            for (int x = x0; x < x1; x++) { var c = left.Lerp(right, (x - x0) / (float)Math.Max(1, x1 - x0 - 1)); img.SetPixel(x, y, new Color(c.R, c.G, c.B, 1f)); }
        }
    }

    /// <summary>The mean colour of a small box (probe).</summary>
    static Color Probe(Image img, float sf, float x, float y, int half = 3)
    {
        double r = 0, g = 0, b = 0; int n = 0;
        for (int dy = -half; dy <= half; dy++) for (int dx = -half; dx <= half; dx++) { var p = img.GetPixel((int)(x * sf) + dx, (int)(y * sf) + dy); r += p.R; g += p.G; b += p.B; n++; }
        return new Color((float)(r / n), (float)(g / n), (float)(b / n));
    }

    /// <summary>A chamfered rectangle (the plates' shape): corners cut by <paramref name="cut"/>.</summary>
    static Vector2[] Chamfer(Rect2 r, float cut) =>
        [new(r.Position.X + cut, r.Position.Y), new(r.End.X - cut, r.Position.Y), new(r.End.X, r.Position.Y + cut), new(r.End.X, r.End.Y - cut), new(r.End.X - cut, r.End.Y), new(r.Position.X + cut, r.End.Y), new(r.Position.X, r.End.Y - cut), new(r.Position.X, r.Position.Y + cut)];

    /// <summary>Hard-edged alpha from a polygon (screen coordinates; texture at <paramref name="org"/>), 1 px antialiased edge, plus the cleaning of dark fringe pixels beside the cut.</summary>
    static void PolyAlpha(Image img, float sf, Vector2 org, Vector2[] poly)
    {
        int W = img.GetWidth(), H = img.GetHeight();
        var local = new Vector2[poly.Length]; for (int i = 0; i < poly.Length; i++) local[i] = poly[i] - org;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                float d = SignedDistance(new Vector2((x + 0.5f) / sf, (y + 0.5f) / sf), local) * sf;
                var p = img.GetPixel(x, y);
                img.SetPixel(x, y, new Color(p.R, p.G, p.B, Mathf.Clamp(0.5f + d / sf, 0f, 1f)));
            }
    }

    /// <summary>A hand-measured silhouette for the scene-facing pieces: alpha from the polygon, then the scene's dark grey (a gradient-limited flood from the polygon's border) removed from inside it.</summary>
    static void SceneAlpha(Image img, float sf, Vector2 org, Vector2[] poly, Func<float, float, bool>? protect = null, float lo = 0.05f, float hi = 0.5f, float step = 0.022f)
    {
        int W = img.GetWidth(), H = img.GetHeight();
        var local = new Vector2[poly.Length]; for (int i = 0; i < poly.Length; i++) local[i] = poly[i] - org;
        var lum = new float[W * H]; var dist = new float[W * H]; var prot = new bool[W * H]; var bg = new bool[W * H]; var stack = new Stack<int>();
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                int i = y * W + x; lum[i] = Lum(img.GetPixel(x, y)); dist[i] = SignedDistance(new Vector2((x + 0.5f) / sf, (y + 0.5f) / sf), local);
                prot[i] = protect?.Invoke(x / sf + org.X, y / sf + org.Y) ?? false;
            }
        for (int i = 0; i < W * H; i++) if (dist[i] > 0f && dist[i] < 1.2f && !prot[i] && lum[i] > lo && lum[i] < hi) { bg[i] = true; stack.Push(i); }
        while (stack.Count > 0)
        {
            int i = stack.Pop(); int x = i % W, y = i / W;
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int j = ny * W + nx;
                if (bg[j] || dist[j] <= 0f || prot[j]) continue;
                if (MathF.Abs(lum[j] - lum[i]) < step && lum[j] > lo && lum[j] < hi) { bg[j] = true; stack.Push(j); }
            }
        }
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                int i = y * W + x; var p = img.GetPixel(x, y);
                float a = Mathf.Clamp(0.5f + dist[i] * sf / sf, 0f, 1f) * (bg[i] ? 0f : 1f);
                img.SetPixel(x, y, new Color(p.R, p.G, p.B, a));
            }
        OpenAlpha(img, 2);
    }

    /// <summary>Erode then dilate the alpha (r px at 4x): crumbs and hairlines go, the shapes keep size and crisp edges.</summary>
    static void OpenAlpha(Image img, int r)
    {
        int W = img.GetWidth(), H = img.GetHeight();
        var a = new bool[W * H]; for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) a[y * W + x] = img.GetPixel(x, y).A >= 0.5f;
        bool[] Pass(bool[] src, bool erode)
        {
            var tmp = new bool[W * H]; var dst = new bool[W * H];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { bool v = erode; for (int k = -r; k <= r; k++) { int xx = Math.Clamp(x + k, 0, W - 1); if (src[y * W + xx] != erode) { v = !erode; break; } } tmp[y * W + x] = v; }
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { bool v = erode; for (int k = -r; k <= r; k++) { int yy = Math.Clamp(y + k, 0, H - 1); if (tmp[yy * W + x] != erode) { v = !erode; break; } } dst[y * W + x] = v; }
            return dst;
        }
        var opened = Pass(Pass(a, true), false);
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (!opened[y * W + x]) { var p = img.GetPixel(x, y); if (p.A > 0f) img.SetPixel(x, y, new Color(p.R, p.G, p.B, 0f)); }
    }

    /// <summary>The icon art cut from a rectangle of the concept: kept as a plain rectangle (the tile's own dark ground is part of it).</summary>
    static Image Icon(Image up, Rect2 r) => Region(up, r);

    /// <summary>A glyph on a flat ground as a white mask (alpha from the contrast with the ground: light glyph on dark ground, or dark on light when <paramref name="dark"/>), so the page tints it by the row's state.</summary>
    static Image Glyph(Image up, Rect2 r, bool dark)
    {
        var img = Region(up, r); int W = img.GetWidth(), H = img.GetHeight();
        float bg = 0; int n = 0; for (int x = 0; x < W; x++) { bg += Lum(img.GetPixel(x, 0)) + Lum(img.GetPixel(x, H - 1)); n += 2; } bg /= n;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { float l = Lum(img.GetPixel(x, y)); float a = dark ? (bg - l) / MathF.Max(0.1f, bg - 0.1f) : (l - bg) / MathF.Max(0.1f, 0.95f - bg); img.SetPixel(x, y, new Color(1, 1, 1, Mathf.SmoothStep(0.05f, 0.85f, Mathf.Clamp(a, 0f, 1f)))); }
        return img;
    }

    /// <summary>A coloured glyph on a dark ground: its own colours, alpha from the brightness of its brightest channel.</summary>
    static Image GlowGlyph(Image up, Rect2 r)
    {
        var img = Region(up, r); int W = img.GetWidth(), H = img.GetHeight();
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { var p = img.GetPixel(x, y); float m = MathF.Max(p.R, MathF.Max(p.G, p.B)); float a = Mathf.SmoothStep(0.16f, 0.6f, m); img.SetPixel(x, y, new Color(p.R, p.G, p.B, a)); }
        return img;
    }

    static void ArmouryTextures(Image concept1x, Image concept4x, Action<string, Image> save)
    {
        var region = PanelScreen;   // (10, 26, 780, 440) in panel 03
        var up = PanelRegion(concept1x, concept4x, Panel03, region); const float sf = 4f;
        Rect2 Pad(Rect2 r, float l, float t, float rr, float b) => new(r.Position - new Vector2(l, t), r.Size + new Vector2(l + rr, t + b));

        // ---- the concept's icons ----
        save("arm-ico-sword", Icon(up, new Rect2(565, 104, 51, 42)));
        save("arm-wico-sword", Icon(up, new Rect2(52, 242, 70, 28)));
        save("arm-ico-lightning", Icon(up, new Rect2(566, 177, 50, 50)));
        save("arm-ico-stress", Icon(up, new Rect2(566, 256, 50, 50)));
        save("arm-tico-lightning", GlowGlyph(up, new Rect2(54, 285, 48, 31)));
        save("arm-tico-warp", GlowGlyph(up, new Rect2(140, 287, 38, 36)));
        save("arm-tico-shock", GlowGlyph(up, new Rect2(214, 287, 42, 30)));
        save("arm-ico-librarian", Glyph(up, new Rect2(76, 125, 38, 24), true));
        save("arm-ico-tactical", Glyph(up, new Rect2(84, 154, 24, 22), false));
        save("arm-ico-assault", Glyph(up, new Rect2(75, 181, 40, 20), false));
        save("arm-ico-devastator", Glyph(up, new Rect2(84, 205, 24, 21), false));

        // ---- the title: SM-REIMPL plate, ARMOURY lettering, wings and skull (a fixed painted wordmark) ----
        {
            var rect = ArmTitle; var tex = Region(up, rect);
            Vector2[] poly = [new(112, 1), new(124, 1), new(128, 8), new(204, 8), new(209, 1), new(220, 1), new(226, 8), new(226, 26), new(284, 31), new(286, 38), new(276, 42), new(284, 56), new(270, 66), new(266, 76), new(247, 86), new(226, 90), new(210, 100), new(190, 102), new(172, 100), new(168, 97), new(158, 102), new(142, 102), new(136, 97), new(116, 98), new(100, 88), new(80, 82), new(72, 76), new(62, 70), new(60, 40), new(60, 30), new(104, 26), new(104, 8)];
            SceneAlpha(tex, sf, rect.Position, poly);
            save("arm-title", tex);
        }

        // ---- the CONFIRM plate (gold, chamfered steel) with its painted label out, and the wings and skull under it ----
        {
            var labels = new List<Rect2> { new(76, 380, 160, 26) };
            var clean = (Image)up.Duplicate();
            FillScan(clean, sf, labels[0], 71f, 240f);
            var tex = Region(clean, ArmConfirm);
            Vector2[] poly = Chamfer(ArmConfirm, 7f);
            PolyAlpha(tex, sf, ArmConfirm.Position, poly);
            save("arm-confirm", tex);
            var wings = Region(up, ArmWings);
            Vector2[] wp = [new(100, 410), new(205, 410), new(208, 414), new(190, 424), new(168, 430), new(142, 430), new(120, 424), new(98, 414)];
            SceneAlpha(wings, sf, ArmWings.Position, wp, null, 0.05f, 0.5f, 0.03f);
            save("arm-wings", wings);
        }

        // ---- the mode badge (MUSOU / CLASSIC PVP) blank and BACK blank ----
        {
            var clean = (Image)up.Duplicate();
            FillScan(clean, sf, new Rect2(672, 11, 58, 21), 666f, 736f);
            var tex = Region(clean, ArmBadge); PolyAlpha(tex, sf, ArmBadge.Position, Chamfer(ArmBadge, 8f)); save("arm-badge", tex);
            var clean2 = (Image)up.Duplicate();
            FillScan(clean2, sf, new Rect2(678, 390, 60, 20), 666f, 750f);
            var back = Region(clean2, ArmBack); PolyAlpha(back, sf, ArmBack.Position, Chamfer(ArmBack, 7f)); save("arm-back", back);
        }

        // ---- the cards: the first card with its icon and text painted out; the icon tile and the meter apart ----
        {
            var clean = (Image)up.Duplicate();
            var tileTop = Probe(up, sf, 590, 103, 2); var tileBot = Probe(up, sf, 590, 146, 2);
            FillVertical(clean, sf, new Rect2(564, 103, 54, 44), tileTop.Lerp(new Color(0.04f, 0.07f, 0.17f), 0.5f), tileBot.Lerp(new Color(0.03f, 0.05f, 0.12f), 0.5f));
            FillScan(clean, sf, ArmCardText, 624f, 750f);
            var tex = Region(clean, ArmCard);
            PolyAlpha(tex, sf, ArmCard.Position, ArmCardShape(ArmCard));
            save("arm-card", tex);
            // the meter on card 3: the channel blank and the rainbow fill separately
            var meterBlank = (Image)up.Duplicate();
            FillScan(meterBlank, sf, new Rect2(631, 286, 112, 11), 628f, 748f);
            save("arm-meter-track", Region(meterBlank, new Rect2(627, 282, 124, 22)));
            save("arm-meter-fill", Region(up, new Rect2(631, 286, 112, 11)));
        }

        // ---- plates inside the rail: dark and gold class rows, dark and gold ability tiles, the weapon row ----
        {
            var clean = (Image)up.Duplicate();
            FillScan(clean, sf, new Rect2(66, 154, 186, 20), 66f, 250f);    // dark row: icon and name
            var dark = Region(clean, Pad(ArmRows[1], 2, 1, 2, 1));
            save("arm-row-dark", dark);
            var gold = (Image)up.Duplicate();
            FillScan(gold, sf, new Rect2(72, 126, 176, 21), 66f, 250f);
            save("arm-row-gold", Region(gold, Pad(ArmRows[0], 4, 2, 4, 2)));
            // tiles: the dark ones (the second) and the gold one (the first): interiors painted flat
            var tClean = (Image)up.Duplicate();
            var dd = Probe(up, sf, 130, 296, 3); var dd2 = Probe(up, sf, 130, 338, 3);
            FillVertical(tClean, sf, new Rect2(128, 288, 60, 52), dd, dd2);
            save("arm-tile-dark", Region(tClean, Pad(ArmTiles[1], 2, 2, 2, 2)));
            var gClean = (Image)up.Duplicate();
            var gg = Probe(up, sf, 56, 296, 2); var gg2 = Probe(up, sf, 56, 338, 2);
            FillVertical(gClean, sf, new Rect2(50, 288, 60, 52), gg, gg2);
            save("arm-tile-gold", Region(gClean, Pad(ArmTiles[0], 2, 2, 2, 2)));
            // the weapon row's icon tile frame
            var wClean = (Image)up.Duplicate();
            FillVertical(wClean, sf, new Rect2(52, 242, 70, 28), new Color(0.05f, 0.08f, 0.18f), new Color(0.02f, 0.04f, 0.10f));
            save("arm-icon-tile", Region(wClean, Pad(ArmWeaponIcon, 2, 2, 2, 2)));
        }

        // ---- the overlay: the rail's frame with the header bar, weapon row and stone, interior rows and tiles painted out ----
        {
            var rect = ArmRail; var tex = Region(up, rect);
            var clean = (Image)up.Duplicate();
            FillScan(clean, sf, new Rect2(62, 103, 46, 14), 56f, 112f);   // CLASS label
            FillScan(clean, sf, new Rect2(205, 103, 54, 14), 200f, 261f); // the header's hash marks
            FillScan(clean, sf, new Rect2(52, 122, 214, 105), 46f, 268f);  // the rows
            FillVertical(clean, sf, new Rect2(51, 242, 71, 29), new Color(0.05f, 0.08f, 0.18f), new Color(0.02f, 0.04f, 0.10f));   // weapon icon
            FillScan(clean, sf, new Rect2(130, 247, 94, 19), 126f, 232f);  // weapon name
            FillScan(clean, sf, new Rect2(36, 276, 238, 74), 35f, 35f);   // the tiles (the margin's colour all across: the scan lines carry on)
            tex = Region(clean, rect);
            Vector2[] poly = [new(14, 99), new(37, 77), new(78, 77), new(84, 101), new(224, 101), new(230, 77), new(277, 77), new(292, 99), new(285, 110), new(285, 348), new(291, 353), new(279, 369), new(48, 369), new(24, 351), new(24, 112), new(14, 108)];
            SceneAlpha(tex, sf, rect.Position, poly, (x, y) => x > 30f && x < 280f && y > 98f && y < 350f);
            save("overlay-armoury", tex);
        }
    }

    /// <summary>The card's silhouette: a chamfered rectangle with the concept's notches (the caps at both ends stand out a little).</summary>
    public static Vector2[] ArmCardShape(Rect2 c) =>
        [new(c.Position.X, c.Position.Y + 9f), new(c.Position.X + 8f, c.Position.Y), new(c.Position.X + 68f, c.Position.Y), new(c.Position.X + 75f, c.Position.Y + 6f), new(c.End.X - 18f, c.Position.Y + 6f), new(c.End.X - 12f, c.Position.Y), new(c.End.X - 4f, c.Position.Y),
         new(c.End.X, c.Position.Y + 9f), new(c.End.X, c.End.Y - 9f), new(c.End.X - 4f, c.End.Y), new(c.End.X - 12f, c.End.Y), new(c.End.X - 18f, c.End.Y - 6f), new(c.Position.X + 75f, c.End.Y - 6f), new(c.Position.X + 68f, c.End.Y), new(c.Position.X + 8f, c.End.Y), new(c.Position.X, c.End.Y - 9f)];
}
