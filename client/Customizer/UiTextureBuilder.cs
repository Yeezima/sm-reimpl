using System;
using System.Collections.Generic;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// Rebuilds the painted UI pieces (client/assets/ui/*.png) from the owner's four-panel concept image (owner-made art, 2026-11-15; the committed owner images in assets/menu are the precedent):
/// <c>godot.cmd -- --build-ui-textures &lt;concept-4panel.jpg&gt; [--out &lt;dir&gt;]</c> (default: client/assets/ui). Each piece is cut from a measured rectangle of the concept (never a whole screen or
/// panel), upscaled with Lanczos and sharpened, and given an alpha: a chamfer and a hole for the frame rings, a flood key against the flat background for the crest and title plate, a saturation key
/// for the blue wordmark. The labels of the buttons, tabs and the value pill are removed by tiling a blank stretch of the same plate over them (live Godot text goes on top). Frame corners are cut once
/// and mirrored. Ours; the rectangles are in the concept's own 1600 x 960 pixels (docs/ui-concept-textures.md).
/// </summary>
public static class UiTextureBuilder
{
    /// <summary>Output pixels per concept pixel: the concept's panels are 780 x 440 for a 1920 x 1080 screen (2.46x); the frames are drawn about 1.74x, so the pieces are made at that scale and drawn 1:1.</summary>
    public const float S = 1.74f;

    public static int Run(string? conceptPath, string? outDir, string? cropPath = null)
    {
        if (string.IsNullOrEmpty(conceptPath) || !System.IO.File.Exists(conceptPath)) { GD.PrintErr("build-ui-textures: give the concept image: --build-ui-textures <concept-4panel.jpg>"); return 2; }
        var src = Image.LoadFromFile(conceptPath);
        src.Convert(Image.Format.Rgba8);
        outDir ??= System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "assets", "ui"));
        System.IO.Directory.CreateDirectory(outDir);
        void Save(string name, Image img) { img.SavePng(System.IO.Path.Combine(outDir, name + ".png")); GD.Print($"ui texture {name}: {img.GetWidth()} x {img.GetHeight()}"); }

        // ---- frame rings: one corner and the edge strips cut, the rest mirrored; the hole and the outside of the chamfer are transparent ----
        // Main menu (panel 01): top-left corner 63 x 60, top strip, left strip; ring thickness 27 (sides) and 24 (top and bottom).
        Save("frame-main", Ring(src, new Rect2I(47, 200, 63, 60), new Rect2I(122, 200, 20, 60), new Rect2I(47, 300, 63, 30), 27, 24, 26, fromTopRight: false));
        // Options (panel 04): top-right corner 66 x 53, top strip, right strip; ring thickness 17 (sides), 14 (top and bottom).
        Save("frame-options", Ring(src, new Rect2I(1436, 582, 66, 53), new Rect2I(1380, 582, 40, 53), new Rect2I(1436, 670, 66, 40), 15, 14, 24, fromTopRight: true));

        // ---- the main menu's wall, frame and wordmark from the owner's sharper crop of panel 01 (522 x 606), when given ----
        if (!string.IsNullOrEmpty(cropPath) && System.IO.File.Exists(cropPath))
        {
            var crop = Image.LoadFromFile(cropPath);
            crop.Convert(Image.Format.Rgba8);
            Save("frame-wall", WallFrame(crop));
            Save("wall-main", Scaled(Cut(crop, new Rect2I(0, 300, 24, 250)), 1.25f));
            Save("wall-cap", Scaled(Cut(crop, new Rect2I(0, 0, 76, 112)), 1.35f));
            Save("wordmark", Scaled(WordmarkMatte(Cut(crop, new Rect2I(30, 122, 478, 94))), 1.2f));
        }

        // ---- buttons: the plate with its label removed ----
        Save("button-dark", Plate(src, new Rect2I(96, 266, 239, 27), new Rect2I(158, 269, 120, 19), new Rect2I(116, 269, 40, 19), 8));
        Save("button-gold", Plate(src, new Rect2I(96, 234, 239, 29), new Rect2I(154, 238, 126, 21), new Rect2I(112, 238, 40, 21), 8));
        Save("tab-dark", Plate(src, new Rect2I(1080, 610, 118, 22), new Rect2I(1116, 612, 52, 16), new Rect2I(1170, 612, 18, 16), 6));
        Save("tab-gold", Plate(src, new Rect2I(1324, 609, 132, 24), new Rect2I(1350, 611, 80, 18), new Rect2I(1332, 611, 16, 18), 6));
        Save("pill", Plate(src, new Rect2I(1060, 684, 116, 20), new Rect2I(1090, 687, 58, 15), new Rect2I(1077, 687, 12, 15), 6));
        Save("nameplate", Scaled(Chamfer(Cut(src, new Rect2I(114, 414, 200, 24)), 5f), S));

        // ---- ornaments ----
        Save("crest", Scaled(ClearBottomSides(FloodKey(Cut(src, new Rect2I(1127, 537, 146, 31)), 26f), 22, 24), S * 1.15f));
        Save("title-plate", Scaled(FloodKey(Cut(src, new Rect2I(1090, 566, 220, 37)), 26f), S));
        if (string.IsNullOrEmpty(cropPath)) Save("wordmark", Scaled(SaturationKey(Cut(src, new Rect2I(45, 110, 350, 80))), S * 1.2f));
        if (string.IsNullOrEmpty(cropPath)) Save("stone-main", Scaled(Cut(src, new Rect2I(10, 240, 37, 100)), S));
        Save("stone-options", Scaled(FloodKey(Cut(src, new Rect2I(1496, 600, 42, 150)), 22f, 41, 75), S));
        return 0;
    }

    // ---- the wall and frame of the owner's crop ----

    /// <summary>Crop pixels per output pixel for the wall frame (the crop's frame is 470 wide; ours is drawn about 590).</summary>
    public const float WallScale = 1.2f;
    /// <summary>The cut: x 0..495, y 236..581 of the crop. The hole (the amber recess) and the outside of the right-hand outline are transparent; the stone at the left stays opaque, so the frame is mounted on the wall.</summary>
    public static readonly Rect2I WallFrameRect = new(0, 236, 495, 345), WallHole = new(68, 275, 394, 228);

    static Image WallFrame(Image crop)
    {
        var img = Cut(crop, WallFrameRect);
        int w = img.GetWidth(), h = img.GetHeight();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = img.GetPixel(x, y);
                float a = 1f;
                int cx = x, cy = y + WallFrameRect.Position.Y;
                bool inHole = cx >= WallHole.Position.X && cx < WallHole.End.X && cy >= WallHole.Position.Y && cy < WallHole.End.Y;
                if (inHole) a = 0f;
                // Outside the frame's top and bottom edge (right of the wall) and its right edge: the scene, transparent.
                if (x > 70 && y < 6) a = 0f;
                if (x > 70 && y > h - 7) a = 0f;
                if (x > w - 4) a = 0f;
                // Chamfers of the two right-hand corners.
                float dTR = (w - x) + y - 26f, dBR = (w - x) + (h - y) - 26f;
                if (dTR < 0f) a = 0f; if (dBR < 0f) a = 0f;
                // Feather the hole's edge.
                if (!inHole)
                {
                    int dx = Math.Max(Math.Max(WallHole.Position.X - cx, cx - (WallHole.End.X - 1)), 0), dy = Math.Max(Math.Max(WallHole.Position.Y - cy, cy - (WallHole.End.Y - 1)), 0);
                    if (dx == 0 && dy == 0) a = 0f;
                    else if (Math.Max(dx, dy) <= 1) a = Math.Min(a, 0.55f);
                }
                p.A = a; img.SetPixel(x, y, p);
            }
        return Scaled(img, WallScale);
    }

    /// <summary>
    /// The wordmark's alpha matte from the owner's crop: saturation key, small holes closed (dilate then erode), the dark outline kept (dark pixels within 3 px of lettering), then the alpha
    /// softened with a 3 x 3 blur and eroded slightly with a contrast curve, so the JPEG's rough edge is a clean one.
    /// </summary>
    static Image WordmarkMatte(Image img)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        var a0 = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = img.GetPixel(x, y);
                float max = Mathf.Max(p.R, Mathf.Max(p.G, p.B)), min = Mathf.Min(p.R, Mathf.Min(p.G, p.B));
                float sat = max <= 0.001f ? 0f : (max - min) / max;
                a0[y * w + x] = p.B >= p.R + 0.05f ? Mathf.Clamp((sat - 0.2f) / 0.2f, 0f, 1f) : 0f;
            }
        float[] Morph(float[] src, bool dilate)
        {
            var o = new float[src.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float v = dilate ? 0f : 1f;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = Mathf.Clamp(x + dx, 0, w - 1), ny = Mathf.Clamp(y + dy, 0, h - 1);
                            v = dilate ? Mathf.Max(v, src[ny * w + nx]) : Mathf.Min(v, src[ny * w + nx]);
                        }
                    o[y * w + x] = v;
                }
            return o;
        }
        var closed = Morph(Morph(a0, true), false);
        var a1 = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = img.GetPixel(x, y);
                float a = closed[y * w + x], max = Mathf.Max(p.R, Mathf.Max(p.G, p.B));
                if (a < 1f && max < 0.3f)
                    for (int dy = -2; dy <= 2 && a < 1f; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < w && ny < h && closed[ny * w + nx] > 0.9f) { a = 1f; break; }
                        }
                a1[y * w + x] = a;
            }
        var blurred = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float sum = 0f;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) sum += a1[Mathf.Clamp(y + dy, 0, h - 1) * w + Mathf.Clamp(x + dx, 0, w - 1)];
                blurred[y * w + x] = sum / 9f;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = img.GetPixel(x, y);
                p.A = Mathf.SmoothStep(0.3f, 0.7f, blurred[y * w + x]);
                img.SetPixel(x, y, p);
            }
        return img;
    }

    // ---- primitives ----

    static Image Cut(Image src, Rect2I r) { var i = src.GetRegion(r); i.Convert(Image.Format.Rgba8); return i; }

    static Image Scaled(Image img, float scale)
    {
        int w = Mathf.RoundToInt(img.GetWidth() * scale), h = Mathf.RoundToInt(img.GetHeight() * scale);
        // Alpha is kept as it is: resize premultiplied would be cleaner, but the keyed edges are feathered already.
        img.Resize(w, h, Image.Interpolation.Lanczos);
        Sharpen(img, 0.55f);
        return img;
    }

    /// <summary>An unsharp mask (3 x 3 box blur subtracted) on the colour channels.</summary>
    static void Sharpen(Image img, float amount)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        var copy = (Image)img.Duplicate();
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                Color c = copy.GetPixel(x, y), sum = default;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) { var p = copy.GetPixel(x + dx, y + dy); sum.R += p.R; sum.G += p.G; sum.B += p.B; }
                var blur = new Color(sum.R / 9f, sum.G / 9f, sum.B / 9f);
                img.SetPixel(x, y, new Color(Mathf.Clamp(c.R + (c.R - blur.R) * amount, 0f, 1f), Mathf.Clamp(c.G + (c.G - blur.G) * amount, 0f, 1f), Mathf.Clamp(c.B + (c.B - blur.B) * amount, 0f, 1f), c.A));
            }
    }

    /// <summary>The four corners cut off by <paramref name="c"/> (the plates' pointed ends), feathered.</summary>
    static Image Chamfer(Image img, float c)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float dx = Mathf.Min(x + 0.5f, w - x - 0.5f), dy = Mathf.Min(y + 0.5f, h - y - 0.5f);
                float edge = dx + dy - c;   // below zero: outside the cut corner
                if (edge >= 1f) continue;
                var p = img.GetPixel(x, y);
                p.A *= Mathf.Clamp(edge + 1f, 0f, 1f);
                img.SetPixel(x, y, p);
            }
        return img;
    }

    /// <summary>Makes everything connected to the picture's border and close to its colour transparent (a flat background behind an ornament); the edge is feathered.</summary>
    static Image FloodKey(Image img, float tolerance, int bgX = 1, int bgY = 1)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        var bg = img.GetPixel(bgX, bgY);
        float t = tolerance / 255f;
        bool Near(Color c) => Mathf.Abs(c.R - bg.R) + Mathf.Abs(c.G - bg.G) + Mathf.Abs(c.B - bg.B) < t * 3f;
        var seen = new bool[w * h];
        var stack = new Stack<(int, int)>();
        for (int x = 0; x < w; x++) { stack.Push((x, 0)); stack.Push((x, h - 1)); }
        for (int y = 0; y < h; y++) { stack.Push((0, y)); stack.Push((w - 1, y)); }
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            if (x < 0 || y < 0 || x >= w || y >= h || seen[y * w + x] || !Near(img.GetPixel(x, y))) continue;
            seen[y * w + x] = true;
            stack.Push((x + 1, y)); stack.Push((x - 1, y)); stack.Push((x, y + 1)); stack.Push((x, y - 1));
        }
        var keyed = (bool[])seen.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!seen[y * w + x]) continue;
                var p = img.GetPixel(x, y); p.A = 0f; img.SetPixel(x, y, p);
            }
        // Feather: an opaque pixel touching a keyed one is half transparent.
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                if (keyed[y * w + x]) continue;
                if (keyed[y * w + x - 1] || keyed[y * w + x + 1] || keyed[(y - 1) * w + x] || keyed[(y + 1) * w + x]) { var p = img.GetPixel(x, y); p.A = 0.6f; img.SetPixel(x, y, p); }
            }
        return img;
    }

    /// <summary>Clears the rows from <paramref name="fromY"/> down except within <paramref name="keepHalf"/> px of the centre (the steel bar of the plate under the crest, which the key leaves).</summary>
    static Image ClearBottomSides(Image img, int fromY, int keepHalf)
    {
        for (int y = fromY; y < img.GetHeight(); y++)
            for (int x = 0; x < img.GetWidth(); x++)
                if (Math.Abs(x - img.GetWidth() / 2) > keepHalf) { var p = img.GetPixel(x, y); p.A = 0f; img.SetPixel(x, y, p); }
        return img;
    }

    /// <summary>Alpha from saturation: the blue lettering against the grey scene, and its dark outline (dark pixels within 2 px of solid lettering).</summary>
    static Image SaturationKey(Image img)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        var a0 = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = img.GetPixel(x, y);
                float max = Mathf.Max(p.R, Mathf.Max(p.G, p.B)), min = Mathf.Min(p.R, Mathf.Min(p.G, p.B));
                float sat = max <= 0.001f ? 0f : (max - min) / max;
                a0[y * w + x] = p.B >= p.R + 0.04f ? Mathf.Clamp((sat - 0.16f) / 0.2f, 0f, 1f) : 0f;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = img.GetPixel(x, y);
                float a = a0[y * w + x];
                float max = Mathf.Max(p.R, Mathf.Max(p.G, p.B));
                if (a < 1f && max < 0.36f)
                    for (int dy = -2; dy <= 2 && a < 1f; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < w && ny < h && a0[ny * w + nx] > 0.9f) { a = 1f; break; }
                        }
                p.A = a;
                img.SetPixel(x, y, p);
            }
        return img;
    }

    /// <summary>A plate (button, tab, pill) with its label removed: a blank stretch of the plate tiled over <paramref name="text"/>; pointed ends from the corner chamfer.</summary>
    static Image Plate(Image src, Rect2I plate, Rect2I text, Rect2I blank, float chamfer)
    {
        var img = Cut(src, plate);
        var blankImg = Cut(src, blank);
        int ox = text.Position.X - plate.Position.X, oy = text.Position.Y - plate.Position.Y;
        int bw = blankImg.GetWidth();
        // Per row: the blank stretch's own colour is replaced by a blend of the colours just left and right of the label, so the patch carries the plate's light (its glow, its gradient).
        for (int y = 0; y < text.Size.Y; y++)
        {
            int ty = oy + y;
            if (ty < 0 || ty >= img.GetHeight()) continue;
            int by = Mathf.Clamp(text.Position.Y - blank.Position.Y + y, 0, blankImg.GetHeight() - 1);
            Color mean = default;
            for (int x = 0; x < bw; x++) { var q = blankImg.GetPixel(x, by); mean.R += q.R; mean.G += q.G; mean.B += q.B; }
            mean = new Color(mean.R / bw, mean.G / bw, mean.B / bw);
            Color Edge(int x0) { Color e = default; for (int k = 0; k < 3; k++) { var q = img.GetPixel(Mathf.Clamp(x0 + k, 0, img.GetWidth() - 1), ty); e.R += q.R; e.G += q.G; e.B += q.B; } return new Color(e.R / 3, e.G / 3, e.B / 3); }
            var left = Edge(ox - 4); var right = Edge(ox + text.Size.X + 1);
            for (int x = 0; x < text.Size.X; x++)
            {
                int tx = ox + x;
                if (tx < 0 || tx >= img.GetWidth()) continue;
                int period = bw * 2;
                int m = x % period; if (m >= bw) m = period - 1 - m;
                var q = blankImg.GetPixel(Mathf.Clamp(m, 0, bw - 1), by);
                var target = left.Lerp(right, x / (float)Math.Max(1, text.Size.X - 1));
                var fill = new Color(Mathf.Clamp((q.R - mean.R) * 0.6f + target.R, 0f, 1f), Mathf.Clamp((q.G - mean.G) * 0.6f + target.G, 0f, 1f), Mathf.Clamp((q.B - mean.B) * 0.6f + target.B, 0f, 1f), 1f);
                // Feather the patch's left and right 3 px into the plate.
                float edge = Mathf.Min(x, text.Size.X - 1 - x);
                float wgt = Mathf.Clamp((edge + 1f) / 9f, 0f, 1f);
                img.SetPixel(tx, ty, img.GetPixel(tx, ty).Lerp(fill, wgt));
            }
        }
        return Scaled(Chamfer(img, chamfer), S);
    }

    /// <summary>A frame ring as a nine-patch image: corners (the cut one mirrored for the others), edge strips, a transparent hole. Ring thicknesses (concept px): sides, top, bottom.</summary>
    static Image Ring(Image src, Rect2I corner, Rect2I stripTop, Rect2I stripSide, int thickSide, int thickTop, float chamfer, bool fromTopRight)
    {
        var c = Cut(src, corner);
        // Normalise to a top-left corner.
        if (fromTopRight) c.FlipX();
        var tl = Mask(c, thickSide, thickTop, chamfer);
        var top = Cut(src, stripTop); var side = Cut(src, stripSide);
        if (fromTopRight) side.FlipX();
        top = MaskStrip(top, 0, thickTop); side = MaskStrip(side, thickSide, 0);
        tl = Scaled(tl, S); top = Scaled(top, S); side = Scaled(side, S);
        int cw = tl.GetWidth(), ch = tl.GetHeight(), ew = top.GetWidth(), eh = side.GetHeight();
        // Edge strips are resized to the corner's thickness band, so the pieces butt together.
        top.Resize(ew, ch, Image.Interpolation.Lanczos); side.Resize(cw, eh, Image.Interpolation.Lanczos);
        var outImg = Image.CreateEmpty(cw * 2 + ew, ch * 2 + eh, false, Image.Format.Rgba8);
        outImg.Fill(new Color(0, 0, 0, 0));
        void Put(Image piece, int x, int y, bool flipX, bool flipY)
        {
            var p = (Image)piece.Duplicate();
            if (flipX) p.FlipX();
            if (flipY) p.FlipY();
            outImg.BlitRect(p, new Rect2I(Vector2I.Zero, p.GetSize()), new Vector2I(x, y));
        }
        Put(tl, 0, 0, false, false); Put(tl, cw + ew, 0, true, false); Put(tl, 0, ch + eh, false, true); Put(tl, cw + ew, ch + eh, true, true);
        Put(top, cw, 0, false, false); Put(top, cw, ch + eh, false, true);
        Put(side, 0, ch, false, false); Put(side, cw + ew, ch, true, false);
        return outImg;
    }

    /// <summary>The corner's hole (inside the ring) and the outside of its chamfer made transparent.</summary>
    static Image Mask(Image c, int thickSide, int thickTop, float chamfer)
    {
        int w = c.GetWidth(), h = c.GetHeight();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = c.GetPixel(x, y);
                bool hole = x >= thickSide && y >= thickTop;
                float cut = (x + y + 1f) - chamfer;   // outside the chamfer line
                p.A = hole ? 0f : Mathf.Clamp(cut + 0.5f, 0f, 1f);
                c.SetPixel(x, y, p);
            }
        return c;
    }

    static Image MaskStrip(Image s, int thickSide, int thickTop)
    {
        for (int y = 0; y < s.GetHeight(); y++)
            for (int x = 0; x < s.GetWidth(); x++)
            {
                bool hole = (thickTop > 0 && y >= thickTop) || (thickSide > 0 && x >= thickSide);
                var p = s.GetPixel(x, y); p.A = hole ? 0f : 1f; s.SetPixel(x, y, p);
            }
        return s;
    }
}
