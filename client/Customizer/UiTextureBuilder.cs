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
public static partial class UiTextureBuilder
{
    /// <summary>Output pixels per concept pixel: the concept's panels are 780 x 440 for a 1920 x 1080 screen (2.46x); the frames are drawn about 1.74x, so the pieces are made at that scale and drawn 1:1.</summary>
    public const float S = 1.74f;

        /// <summary>Resolution of the concept image used relative to the original 1600 x 960 (1 for the JPEG, 4 for a 4x upscale); rectangles are scaled by it.</summary>
    static float F = 1f;
    static Rect2I U(int x, int y, int w, int h) => new((int)MathF.Round(x * F), (int)MathF.Round(y * F), (int)MathF.Round(w * F), (int)MathF.Round(h * F));

    public static int Run(string? conceptPath, string? outDir, string? cropPath = null, string? upscaledPath = null, string? masonryPath = null)
    {
        if (string.IsNullOrEmpty(conceptPath) || !System.IO.File.Exists(conceptPath)) { GD.PrintErr("build-ui-textures: give the concept image: --build-ui-textures <concept-4panel.jpg>"); return 2; }
        var src = Image.LoadFromFile(conceptPath);
        src.Convert(Image.Format.Rgba8);
        F = src.GetWidth() / 1600f;   // an upscaled concept (Real-ESRGAN x4: 6400 wide) gives F = 4; every rectangle below is in concept pixels
        outDir ??= System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "assets", "ui"));
        System.IO.Directory.CreateDirectory(outDir);
        void Save(string name, Image img) { img.SavePng(System.IO.Path.Combine(outDir, name + ".png")); GD.Print($"ui texture {name}: {img.GetWidth()} x {img.GetHeight()}"); }

        // ---- frame rings: one corner and the edge strips cut, the rest mirrored; the hole and the outside of the chamfer are transparent ----
        // Main menu (panel 01): top-left corner 63 x 60, top strip, left strip; ring thickness 27 (sides) and 24 (top and bottom).
        Save("frame-main", Ring(src, U(47, 200, 63, 60), U(122, 200, 20, 60), U(47, 300, 63, 30), (int)(27 * F), (int)(24 * F), 26f * F, fromTopRight: false));
        // Options (panel 04): top-right corner 66 x 53, top strip, right strip; ring thickness 17 (sides), 14 (top and bottom).
        Save("frame-options", Ring(src, U(1436, 582, 66, 53), U(1380, 582, 40, 53), U(1436, 670, 66, 40), (int)(15 * F), (int)(14 * F), 24f * F, fromTopRight: true));

        // ---- the main menu's wall, frame and wordmark from the owner's sharper crop of panel 01 (522 x 606), when given ----
        if (!string.IsNullOrEmpty(cropPath) && System.IO.File.Exists(cropPath))
        {
            var crop = Image.LoadFromFile(cropPath);
            crop.Convert(Image.Format.Rgba8);
            if (string.IsNullOrEmpty(masonryPath) && !string.IsNullOrEmpty(upscaledPath) && System.IO.File.Exists(upscaledPath)) FaithfulButtons(LoadBackProjected(upscaledPath, crop), Save);   // phase 3 (faithful): the concept's own plates and labels as button textures
            Save("overlay-main", MainOverlay(crop, upscaledPath, masonryPath));
            Save("frame-wall", WallFrame(crop));
            Save("wall-main", WallSynth(crop));
            Save("wordmark", WordmarkBuild(Cut(crop, new Rect2I(30, 122, 478, 94))));
            if (string.IsNullOrEmpty(cropPath)) Save("crest", Scaled(ClearBottomSides(FloodKey(Cut(crop, new Rect2I(150, 212, 226, 44)), 34f, 2, 2), 36, 30), 1.2f));
        }

        // ---- buttons: the plate with its label removed ----
        Save("button-dark", Plate(src, U(96, 266, 239, 27), U(158, 269, 120, 19), U(116, 269, 40, 19), 8));
        Save("button-gold", Plate(src, U(96, 234, 239, 29), U(154, 238, 126, 21), U(112, 238, 40, 21), 8));
        Save("tab-dark", Plate(src, U(1080, 610, 118, 22), U(1116, 612, 52, 16), U(1170, 612, 18, 16), 6));
        Save("tab-gold", Plate(src, U(1324, 609, 132, 24), U(1350, 611, 80, 18), U(1332, 611, 16, 18), 6));
        Save("pill", Plate(src, U(1060, 684, 116, 20), U(1090, 687, 58, 15), U(1077, 687, 12, 15), 6));
        Save("nameplate", Scaled(Chamfer(Cut(src, U(114, 414, 200, 24)), 5f * F), S / F));

        // ---- ornaments ----
        Save("crest", Scaled(ClearBottomSides(FloodKey(Cut(src, U(1127, 537, 146, 31)), 26f), (int)(22 * F), (int)(24 * F)), S * 1.15f / F));
        Save("title-plate", Scaled(FloodKey(Cut(src, U(1090, 566, 220, 37)), 26f), S / F));
        if (string.IsNullOrEmpty(cropPath)) Save("wordmark", Scaled(SaturationKey(Cut(src, U(45, 110, 350, 80))), S * 1.2f / F));
        if (string.IsNullOrEmpty(cropPath)) Save("stone-main", Scaled(Cut(src, U(10, 240, 37, 100)), S / F));
        Save("stone-options", Scaled(FloodKey(Cut(src, U(1496, 600, 42, 150)), 22f, (int)(41 * F), (int)(75 * F)), S / F));
        return 0;
    }

    // ---- the main menu overlay (owner 2026-11-15: "simply paste in the concept art overlaid on the left side of the screen") ----

    /// <summary>The owner's crop is 522 x 606; every rectangle below is in its pixels (an upscaled input of the same framing is scaled to it).</summary>
    public const int CropW = 522, CropH = 606;
    /// <summary>Where the live buttons go (the plates' area; the baked plates are painted out there), the wordmark and the nameplate, in crop pixels.</summary>
    public static readonly Rect2I OverlayRows = new(92, 281, 341, 226), OverlayWordmark = new(30, 122, 478, 94), OverlayNameplate = new(118, 543, 265, 30);

    /// <summary>The owner's orange line (his marked image, 2026-11-15) in crop pixels: everything inside is kept, outside is transparent, the edge feathered.</summary>
    /// <summary>The outline's points (the comparison tool uses them).</summary>
    public static Vector2[] OverlayOutlinePoints => OverlayOutline;
    static readonly Vector2[] OverlayOutline =
    [
        new(0, 0), new(43, 0), new(99, 52), new(101, 106), new(505, 110), new(512, 205), new(416, 219), new(440, 237), new(462, 247), new(476, 262), new(478, 306), new(485, 312), new(485, 432), new(489, 446), new(489, 520),
        new(482, 565), new(433, 577), new(133, 589), new(133, 606), new(0, 606),
    ];

    /// <summary>
    /// The one image of the main menu's left side: the crop, upscaled (the pluggable stage: <paramref name="upscaledPath"/> is an already upscaled picture of the same framing, e.g. from an AI upscaler;
    /// without it, 2x with Lanczos limited to the local range of the source so it cannot ring, then an unsharp mask), the button plates painted out of the recess (clean amber with the concept's scan lines; the
    /// live buttons go there), and an alpha that follows the owner's line.
    /// </summary>
    static Image MainOverlay(Image crop, string? upscaledPath, string? masonryPath)
    {
        Image img;
        if (!string.IsNullOrEmpty(upscaledPath) && System.IO.File.Exists(upscaledPath))
        {
            img = string.IsNullOrEmpty(masonryPath) ? LoadBackProjected(upscaledPath, crop) : Image.LoadFromFile(upscaledPath);
            img.Convert(Image.Format.Rgba8);
            GD.Print($"overlay: pre-upscaled input {img.GetWidth()} x {img.GetHeight()}");
        }
        else img = SmartUpscale(crop, 2);
        int W = img.GetWidth(), H = img.GetHeight();
        float sf = W / (float)CropW, sfy = H / (float)CropH;
        bool faithful = string.IsNullOrEmpty(masonryPath);   // phase 3: the concept's own pixels (no grade, no regraded wall, painted plates as button textures); --masonry restores the graded pass 2h look
        if (faithful) { FaithfulPlatesOut(img, sf, sfy); }
        // Paint the plates out: the recess's own amber, with its scan lines, from the clean strips beside the plates.
        var rows = OverlayRows;
        Color amber = default; int n = 0;
        for (int y = (int)(300 * sfy); y < (int)(480 * sfy); y++)
            for (int x = (int)(72 * sf); x < (int)(88 * sf); x++) { var p = img.GetPixel(x, y); amber.R += p.R; amber.G += p.G; amber.B += p.B; n++; }
        amber = new Color(Mathf.Min(1f, amber.R / n * 1.45f), Mathf.Min(1f, amber.G / n * 1.4f), Mathf.Min(1f, amber.B / n * 1.2f));   // the strips beside the plates include the dark rim; the glow's own amber is brighter
        float top = rows.Position.Y * sfy, bottom = rows.End.Y * sfy;
        for (int y = (int)top; y < (int)bottom && !faithful; y++)
        {
            float edge = Mathf.Min(y - top, bottom - y) / (30f * sfy);
            float shade = Mathf.Lerp(0.72f, 1.04f, Mathf.SmoothStep(0f, 1f, edge));
            float scan = 1f;   // a smooth glow (the concept's scan lines came out as dark streaks after the upscale)
            for (int x = (int)(rows.Position.X * sf); x < (int)(rows.End.X * sf); x++)
            {
                // Soft sides so the patch meets the recess without a seam.
                float side = Mathf.Min(x - rows.Position.X * sf, rows.End.X * sf - x) / (16f * sf);
                var old = img.GetPixel(x, y);
                float gx = (x / sf - 262f) / 170f, gy = (y / sfy - 395f) / 115f;
                float glow = 1f + 0.16f * Mathf.Exp(-(gx * gx + gy * gy));
                var fill = new Color(Mathf.Clamp(amber.R * shade * scan * glow, 0f, 1f), Mathf.Clamp(amber.G * shade * scan, 0f, 1f), Mathf.Clamp(amber.B * shade * scan, 0f, 1f), 1f);
                img.SetPixel(x, y, old.Lerp(fill, Mathf.Clamp(side, 0f, 1f)));
            }
        }
        if (!faithful) SmoothRecess(img, new Rect2I((int)(68 * sf), (int)(274 * sfy), (int)(396 * sf), (int)(236 * sfy)), (int)(Mathf.Round(4f * sfy)));
        // Behind the wordmark and the crest the concept's grey sky would show as a box on our video: keep only the letters (with their outline and shadow) and the crest.
        var bandAlpha = new float[W * H];
        Array.Fill(bandAlpha, 1f);
        {
            int y0 = (int)(98 * sfy), y1 = (int)(216 * sfy), x0 = (int)(28 * sf), x1 = (int)(514 * sf);
            var mask = new float[W * H];
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    var p = img.GetPixel(x, y);
                    float max = Mathf.Max(p.R, Mathf.Max(p.G, p.B)), min = Mathf.Min(p.R, Mathf.Min(p.G, p.B));
                    float sat = max <= 0.001f ? 0f : (max - min) / max;
                    bool lPart = x / sf > 436f;   // the final L is lighter blue: a lower threshold keeps all of it, bar and stem
                    mask[y * W + x] = lPart ? (p.B >= p.R + 0.07f && sat > 0.22f ? 1f : 0f) : (p.B >= p.R + 0.12f && sat > 0.40f ? 1f : 0f);
                }
            int r = (int)(Mathf.Round(5f * sf));
            // Dilate by r (a square, in two passes), keeping the letters, the dark outline and the shadow beside them.
            var tmp = new float[W * H]; var dil = new float[W * H];
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { float v = 0f; for (int d = -r; d <= r; d++) v = Mathf.Max(v, mask[y * W + Mathf.Clamp(x + d, 0, W - 1)]); tmp[y * W + x] = v; }
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { float v = 0f; for (int d = -r; d <= r; d++) v = Mathf.Max(v, tmp[Mathf.Clamp(y + d, 0, H - 1) * W + x]); dil[y * W + x] = v; }
            // Faithful: the letters' drop shadow (the concept's own dark pixels within a wider reach of the letters) is kept too, so the wordmark sits on the video as it sits on the concept.
            float[]? dil2 = null;
            if (faithful)
            {
                int r2 = (int)Mathf.Round(11f * sf);
                var t2 = new float[W * H]; dil2 = new float[W * H];
                for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { float v = 0f; for (int d = -r2; d <= r2; d += 2) v = Mathf.Max(v, mask[y * W + Mathf.Clamp(x + d, 0, W - 1)]); t2[y * W + x] = v; }
                for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { float v = 0f; for (int d = -r2; d <= r2; d += 2) v = Mathf.Max(v, t2[Mathf.Clamp(y + d, 0, H - 1) * W + x]); dil2[y * W + x] = v; }
            }
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    // A one-pixel soft edge on the dilated mask.
                    float a = dil[y * W + x];
                    if (dil2 is not null && a < 1f && dil2[y * W + x] > 0f)
                    {
                        var q2 = img.GetPixel(x, y); float mx = Mathf.Max(q2.R, Mathf.Max(q2.G, q2.B));
                        a = Mathf.Max(a, 1f - Mathf.SmoothStep(0.15f, 0.26f, mx));
                    }
                    if (x < (int)(94 * sf))
                    {
                        // Against the wall: dark stone stays, the concept's lighter sky between the letters goes.
                        var q = img.GetPixel(x, y);
                        if (Mathf.Max(q.R, Mathf.Max(q.G, q.B)) < 0.31f && q.B < q.R + 0.1f) a = 1f;
                    }
                    bandAlpha[y * W + x] = a;
                }
            // Under the wordmark, round the crest and along the frame's top: the concept's sky is keyed out (flood from the sky's own grey, generous tolerance); the letters' dark outline, the crest and the steel stay.
            var skyRect = new Rect2I((int)(102 * sf), (int)(212 * sfy), (int)(396 * sf), (int)(44 * sfy));
            var sub = FloodKey(img.GetRegion(skyRect), 48f, 2, 2);
            for (int y = 0; y < sub.GetHeight(); y++)
                for (int x = 0; x < sub.GetWidth(); x++)
                {
                    float cxp = (skyRect.Position.X + x) / sf, cyp = (skyRect.Position.Y + y) / sfy;
                    float a = sub.GetPixel(x, y).A;
                    // Round the crest, an ellipse over it (centre 262, 238; radii 112 x 25 crop px) trims what the key leaves.
                    if (cxp > 148f && cxp < 378f && cyp < 246f)
                    {
                        float ex = (cxp - 262f) / 112f, ey = (cyp - 238f) / 26f;
                        a *= Mathf.Clamp((1.08f - Mathf.Sqrt(ex * ex + ey * ey)) / 0.12f, 0f, 1f);
                    }
                    bandAlpha[(skyRect.Position.Y + y) * W + skyRect.Position.X + x] = a;
                }
            for (int y = y1; y < skyRect.Position.Y; y++) for (int x = x0; x < x1; x++) bandAlpha[y * W + x] = 1f;
            // The sky band between the wordmark and the frame's top is gone completely; the crest is cut from the upscaled picture with its own matte (below) and sits over the video.
            for (int y = (int)(216 * sfy); y < (int)(243 * sfy); y++) for (int x = (int)(102 * sf); x < (int)(500 * sf); x++) bandAlpha[y * W + x] = 0f;
            if (faithful) FaithfulSeat(img, bandAlpha, W, sf, sfy);
            CrestMatte(img, bandAlpha, W, H, sf, sfy, faithful);
        }
        // The grey haze at the frame's top-right corner and the thin strip down its right side: flooded out from the background's own grey.
        foreach (var (rx, ry, rw, rh, bx, by, tol) in new[] { (410, 200, 100, 70, 95, 4, 34f), (452, 255, 60, 320, 57, 160, 28f) })
        {
            var rr = new Rect2I((int)(rx * sf), (int)(ry * sfy), (int)(rw * sf), (int)(rh * sfy));
            var keyed = FloodKey(img.GetRegion(rr), tol, (int)(bx * sf), (int)(by * sfy));
            for (int y = 0; y < keyed.GetHeight(); y++)
                for (int x = 0; x < keyed.GetWidth(); x++)
                {
                    int i = (rr.Position.Y + y) * W + rr.Position.X + x;
                    bandAlpha[i] = Mathf.Min(bandAlpha[i], keyed.GetPixel(x, y).A);
                }
        }
        // The alpha: inside the owner's outline, with a wide soft feather.
        float feather = (faithful ? 3f : 11f) * sf;
        var A = new float[W * H]; var D = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float d = SignedDistance(new Vector2(x / sf, y / sfy), OverlayOutline) * sf;   // positive inside
                D[y * W + x] = d;
                A[y * W + x] = Mathf.SmoothStep(0f, 1f, Mathf.Clamp(0.5f + d / feather, 0f, 1f)) * bandAlpha[y * W + x];
            }
        PaintOutNameplate(img, sf, sfy);
        if (!string.IsNullOrEmpty(masonryPath) && System.IO.File.Exists(masonryPath))
            GradeWall(img, A, bandAlpha, Image.LoadFromFile(masonryPath), sf, sfy);
        if (faithful) FaithfulFinish(img, A, W, H);
        else FinishOverlay(img, A, D, sf, sfy);
        return img;
    }

    /// <summary>The nameplate's lettering ("ONE SOLDIER. MANY BATTLEFIELDS.") painted out of the frame's bottom bar (owner 2026-11-15): each row of the plate between its ends is the blend of the steel just left and right of the words.</summary>
    static void PaintOutNameplate(Image img, float sf, float sfy)
    {
        int y0 = (int)(547 * sfy), y1 = (int)(568 * sfy), x0 = (int)(131 * sf), x1 = (int)(386 * sf);
        for (int y = y0; y < y1; y++)
        {
            Color Avg(int xa) { Color c = default; for (int k = 0; k < 8; k++) { var q = img.GetPixel(xa + k, y); c.R += q.R; c.G += q.G; c.B += q.B; } return new Color(c.R / 8, c.G / 8, c.B / 8); }
            var left = Avg(x0 - 10); var right = Avg(x1 + 2);
            for (int x = x0; x < x1; x++)
            {
                float t = (x - x0) / (float)(x1 - x0);
                // A touch of grain so the patch is not a smooth band.
                float grain = 1f + 0.035f * Mathf.Sin(x * 0.9f + y * 1.7f) * Mathf.Sin(x * 0.31f);
                var c = left.Lerp(right, t);
                float edge = Mathf.Clamp(Mathf.Min(x - x0, x1 - 1 - x) / (6f * sf), 0f, 1f);
                var old = img.GetPixel(x, y);
                img.SetPixel(x, y, old.Lerp(new Color(c.R * grain, c.G * grain, c.B * grain, 1f), edge));
            }
        }
    }

    /// <summary>
    /// The concept's own painted wall (left edge and top left) graded to the video's masonry instead of replaced (owner 2026-11-15): the pixels' mean and spread per channel are moved to those of the tower's
    /// masonry in the owner's video (<paramref name="masonry"/>, brightened and warmed for the sunset), a high-pass of that masonry is laid over as weathering at low strength, soot darkens the base, a warm
    /// rim lights the edges that face the sun, and the outer edge is made irregular: courses end at different places with chipped corners, so it is no straight line. Seeded and reproducible.
    /// </summary>
    static void GradeWall(Image img, float[] A, float[] letters, Image masonry, float sf, float sfy)
    {
        int W = img.GetWidth(), H = img.GetHeight();
        masonry.Convert(Image.Format.Rgba8);
        int mw = masonry.GetWidth(), mh = masonry.GetHeight();
        float WedgeAt(float yc) => yc < 232f ? 100f : yc < 252f ? Mathf.Lerp(100f, 21f, (yc - 232f) / 20f) : 21f;
        double[] mo = new double[3], so = new double[3], mm = new double[3], sm = new double[3]; long no = 0;
        for (int y = 0; y < H; y += 3)
        {
            int maxX = (int)(WedgeAt(y / sfy) * sf);
            for (int x = 0; x < maxX; x += 3)
            {
                if (A[y * W + x] <= 0.5f) continue;
                var c = img.GetPixel(x, y); no++;
                mo[0] += c.R; mo[1] += c.G; mo[2] += c.B; so[0] += c.R * c.R; so[1] += c.G * c.G; so[2] += c.B * c.B;
            }
        }
        long nm = 0;
        for (int y = 0; y < mh; y++) for (int x = 0; x < mw; x++) { var c = masonry.GetPixel(x, y); nm++; mm[0] += c.R; mm[1] += c.G; mm[2] += c.B; sm[0] += c.R * c.R; sm[1] += c.G * c.G; sm[2] += c.B * c.B; }
        if (no < 100) return;
        float[] mean_o = new float[3], std_o = new float[3], mean_m = new float[3], std_m = new float[3];
        for (int k = 0; k < 3; k++)
        {
            mean_o[k] = (float)(mo[k] / no); std_o[k] = Mathf.Max(0.02f, (float)Math.Sqrt(Math.Max(0.0, so[k] / no - mean_o[k] * mean_o[k])));
            mean_m[k] = (float)(mm[k] / nm); std_m[k] = Mathf.Max(0.02f, (float)Math.Sqrt(Math.Max(0.0, sm[k] / nm - mean_m[k] * mean_m[k])));
        }
        float[] lift = [1.30f * 1.06f, 1.30f, 1.30f * 0.92f];
        var blur = (Image)masonry.Duplicate();
        blur.Resize(Math.Max(1, mw / 6), Math.Max(1, mh / 6), Image.Interpolation.Bilinear);
        blur.Resize(mw, mh, Image.Interpolation.Bilinear);
        var rng = new Random(20261116);
        var edge = new float[H];
        // A natural broken outline: slow waves, a finer wobble and now and then a chipped bite, nothing stair-stepped (seeded).
        var bites = new List<(float Y, float R, float D)>();
        for (float by = 10f; by < 230f; by += rng.Next(14, 34)) bites.Add((by * sfy, rng.Next(5, 13) * sfy, rng.Next(6, 20) * sf));
        for (int y = 0; y < H; y++)
        {
            float yc = y / sfy;
            float amp = yc < 232f ? 1f : 0f;
            float e = WedgeAt(yc) * sf;
            float wob = (Mathf.Sin(y * 0.021f) * 9f + Mathf.Sin(y * 0.063f + 1.1f) * 5f + Mathf.Sin(y * 0.19f + 0.4f) * 2.2f) * sf * 0.5f - 8f * sf;
            float bite = 0f;
            foreach (var (by, br, bd) in bites) { float dy = Mathf.Abs(y - by) / (br * 2.4f); if (dy < 1f) bite = Mathf.Max(bite, bd * 0.55f * (1f - dy * dy)); }
            edge[y] = e + amp * (wob - bite);
        }
        for (int y = 0; y < H; y++)
        {
            float yc = y / sfy;
            float soot = 1f - 0.34f * Mathf.SmoothStep(0.25f, 1f, y / (float)H);
            int maxX = Math.Min(W - 1, (int)(WedgeAt(yc) * sf) + (int)(9 * sf));
            for (int x = 0; x < maxX; x++)
            {
                int i = y * W + x;
                bool letter = yc >= 98f && yc < 216f && x / sf >= 28f && letters[i] > 0.5f && NearLetter(img, x, y, (int)(5 * sf));
                if (letter) continue;
                if (yc >= 98f && yc < 252f && A[i] <= 0f) A[i] = 1f;   // behind the wordmark the wall runs on, whatever the key made of the concept's sky there
                if (A[i] <= 0f) continue;
                if (x > edge[y] + 1f) { A[i] = 0f; continue; }
                A[i] *= Mathf.Clamp((edge[y] - x) / (3f * sf) + 0.5f, 0f, 1f);
                var c = img.GetPixel(x, y);
                // Behind the wordmark the concept's pixels are the dark keep of the letters' surroundings: take the wall's own stone from above instead (mirrored down the rows), so the wall runs on unbroken.
                if (yc >= 98f && yc < 252f && x < (int)(WedgeAt(yc) * sf)) { float d = (yc - 98f) % 196f; float srcY = d < 98f ? 98f - d : d - 98f; var c1 = img.GetPixel(x, Math.Clamp((int)(srcY * sfy), 0, H - 1)); float d2 = (yc - 98f + 61f) % 196f; float srcY2 = d2 < 98f ? 98f - d2 : d2 - 98f; var c2 = img.GetPixel((x + (int)(23 * sf)) % Math.Max(1, (int)(WedgeAt(yc) * sf)), Math.Clamp((int)(srcY2 * sfy), 0, H - 1)); c = c1.Lerp(c2, 0.5f * Mathf.SmoothStep(0f, 1f, Mathf.Abs(Mathf.Sin(yc * 0.045f)))); }
                float[] v = [c.R, c.G, c.B];
                for (int k = 0; k < 3; k++)
                {
                    float z = (v[k] - mean_o[k]) / std_o[k];
                    v[k] = (mean_m[k] + z * std_m[k] * 1.9f) * lift[k];
                }
                int mx = (x * 5 / 4 + y / 3) % mw, my = (y * 5 / 4 + x / 3) % mh;
                var m = masonry.GetPixel(mx, my); var b = blur.GetPixel(mx, my);
                float hp = (m.R * 0.3f + m.G * 0.59f + m.B * 0.11f) - (b.R * 0.3f + b.G * 0.59f + b.B * 0.11f);
                float lm = v[0] * 0.3f + v[1] * 0.59f + v[2] * 0.11f;
                float[] tintK = [1.05f, 1.0f, 0.93f];
                for (int k = 0; k < 3; k++) v[k] = Mathf.Clamp(((lm + (v[k] - lm) * 0.30f) * tintK[k] + hp * 1.9f) * soot, 0f, 1f);
                float dEdge = edge[y] - x;
                if (dEdge < 7f * sf && dEdge >= 0f)
                {
                    float r = 1f - dEdge / (7f * sf);
                    v[0] = Mathf.Min(1f, v[0] + 0.12f * r); v[1] = Mathf.Min(1f, v[1] + 0.07f * r); v[2] = Mathf.Min(1f, v[2] + 0.02f * r);
                }
                img.SetPixel(x, y, new Color(v[0], v[1], v[2], 1f));
            }
        }
    }

    /// <summary>A pixel of the lettering: saturated blue, or (the outline and shadow) dark with blue lettering within <paramref name="r"/> pixels.</summary>
    static bool NearLetter(Image img, int x, int y, int r)
    {
        if (IsBlue(img.GetPixel(x, y))) return true;
        if (Mathf.Max(img.GetPixel(x, y).R, Mathf.Max(img.GetPixel(x, y).G, img.GetPixel(x, y).B)) > 0.3f) return false;
        for (int dy = -r; dy <= r; dy += 2)
            for (int dx = -r; dx <= r; dx += 2)
                if (IsBlue(img.GetPixel(Math.Clamp(x + dx, 0, img.GetWidth() - 1), Math.Clamp(y + dy, 0, img.GetHeight() - 1)))) return true;
        return false;
    }

    static bool IsBlue(Color p) { float max = Mathf.Max(p.R, Mathf.Max(p.G, p.B)), min = Mathf.Min(p.R, Mathf.Min(p.G, p.B)); return max > 0.25f && p.B >= p.R + 0.12f && (max - min) / max > 0.35f; }

    /// <summary>A pixel that belongs to the wordmark's letters (saturated blue) or their near-black outline.</summary>
    static bool IsLetterPixel(Color p) { float max = Mathf.Max(p.R, Mathf.Max(p.G, p.B)); return p.B >= p.R + 0.10f || max < 0.14f; }

    /// <summary>
    /// Replaces the concept's left-edge stone with a ruined wall built from the video's own masonry (owner 2026-11-15: "match the floor and buildings in the background shot"): course by course, blocks of random
    /// width and height, each filled with a random soft patch of the tower's masonry (a still of the owner's video, <paramref name="masonry"/>), tinted a little differently, with mortar, a chipped bevel,
    /// chipped and broken corners, cracks, soot and a warm light from the right; the right edge breaks off irregularly (blocks missing, courses ending at different places). The wall runs behind the
    /// wordmark (its letters stay on top) and as a column beside the frame. Seeded, so the builder gives the same wall every time.
    /// </summary>
    static void ReplaceWall(Image img, float[] A, float[] letters, Image masonry, float sf, float sfy)
    {
        int W = img.GetWidth(), H = img.GetHeight();
        int wallW = (int)(106 * sf);
        var wall = Image.CreateEmpty(wallW, H, false, Image.Format.Rgba8);
        wall.Fill(new Color(0, 0, 0, 0));
        masonry.Convert(Image.Format.Rgba8);
        int mw = masonry.GetWidth(), mh = masonry.GetHeight();
        var rng = new Random(20261115);
        float WedgeAt(float yc) => yc < 232f ? 100f : yc < 244f ? Mathf.Lerp(100f, 21f, (yc - 232f) / 12f) : 21f;
        // Courses.
        float y0 = -rng.Next(0, (int)(60 * sfy));
        while (y0 < H)
        {
            int ch = (int)(rng.Next(46, 74) * sfy);
            float yc = (y0 + ch / 2f) / sfy;
            float edge = WedgeAt(yc) * sf;
            // The course ends short of the edge by a random amount, more in the upper, broken part.
            float jag = yc < 110f ? rng.Next(0, 90) : yc < 232f ? rng.Next(0, 60) : rng.Next(0, 3);
            float limit = edge - jag * sf;
            float x0 = -rng.Next(0, (int)(40 * sf));
            while (x0 < limit)
            {
                int bw = (int)(rng.Next(70, 140) * sf);
                float bx1 = Mathf.Min(x0 + bw, limit);
                if (bx1 - x0 > 22 * sf) DrawBlock(wall, masonry, rng, (int)x0, (int)y0, (int)(bx1 - x0), ch, mw, mh, sf, bx1 < x0 + bw);
                x0 += bw;
            }
            y0 += ch;
        }
        // Soot and a long soft light/shade over the whole wall: darker lower down and to the left, warm at the right.
        for (int y = 0; y < H; y++)
            for (int x = 0; x < wallW; x++)
            {
                var c = wall.GetPixel(x, y);
                if (c.A <= 0f) continue;
                float n = 0.5f + 0.25f * Mathf.Sin(x * 0.011f + y * 0.017f) + 0.25f * Mathf.Sin(x * 0.043f - y * 0.009f + 1.7f);
                float soot = 1f - 0.30f * n * Mathf.SmoothStep(0.15f, 1f, y / (float)H);
                c = new Color(c.R * soot, c.G * soot, c.B * soot, c.A);
                wall.SetPixel(x, y, c);
            }
        // Composite: the wall replaces the original stone left of its edge, except where the wordmark's letters stand.
        for (int y = 0; y < H; y++)
        {
            float yc = y / sfy;
            int maxX = Math.Min(wallW, (int)(WedgeAt(yc) * sf) + 1);
            for (int x = 0; x < maxX; x++)
            {
                int i = y * W + x;
                bool letter = yc >= 98f && yc < 216f && x / sf >= 28f && letters[i] > 0.5f;
                if (letter) continue;
                var w = wall.GetPixel(x, y);
                A[i] = w.A;
                img.SetPixel(x, y, new Color(w.R, w.G, w.B, 1f));
            }
        }
    }

    static void DrawBlock(Image wall, Image masonry, Random rng, int bx, int by, int bw, int bh, int mw, int mh, float sf, bool broken)
    {
        // A soft patch of the masonry for this block: a random window of it, stretched to the block's size (so no two blocks look alike and the sample's own mortar lines stay out).
        int pw = rng.Next(46, 80), ph = Math.Max(18, (int)(pw * bh / (float)bw));
        int ox = rng.Next(0, Math.Max(1, mw - pw)), oy = rng.Next((int)(mh * 0.45f), Math.Max((int)(mh * 0.45f) + 1, mh - ph));
        bool flip = rng.Next(2) == 0;
        float tint = 0.86f + (float)rng.NextDouble() * 0.26f;
        float warm = (float)rng.NextDouble() * 0.08f;
        // Corner chips.
        float[] chip = [rng.Next(0, 4) == 0 ? rng.Next(14, 70) * sf / 4f : 0f, rng.Next(0, 4) == 0 ? rng.Next(14, 70) * sf / 4f : 0f, rng.Next(0, 3) == 0 ? rng.Next(14, 60) * sf / 4f : 0f, rng.Next(0, 3) == 0 ? rng.Next(14, 60) * sf / 4f : 0f];
        if (broken) chip[1] = Math.Max(chip[1], rng.Next(20, 90) * sf / 4f);   // a broken, ragged end towards the edge
        int mortar = Math.Max(4, (int)(2.4f * sf));
        for (int v = 0; v < bh; v++)
        {
            int yy = by + v;
            if (yy < 0 || yy >= wall.GetHeight()) continue;
            for (int u = 0; u < bw; u++)
            {
                int xx = bx + u;
                if (xx < 0 || xx >= wall.GetWidth()) continue;
                // Corners cut off: TL, TR, BL, BR.
                if (u + v < chip[0] || (bw - 1 - u) + v < chip[1] || u + (bh - 1 - v) < chip[2] || (bw - 1 - u) + (bh - 1 - v) < chip[3]) continue;
                int de = Math.Min(Math.Min(u, v), Math.Min(bw - 1 - u, bh - 1 - v));
                Color c;
                if (de < mortar) c = new Color(0.12f, 0.105f, 0.09f, 1f);
                else
                {
                    int sx = ox + (int)(u / (float)bw * pw), sy = oy + (int)(v / (float)bh * ph);
                    if (flip) sx = ox + pw - 1 - (sx - ox);
                    var m = masonry.GetPixel(Math.Clamp(sx, 0, mw - 1), Math.Clamp(sy, 0, mh - 1));
                    float lum = m.R * 0.3f + m.G * 0.59f + m.B * 0.11f;
                    // The video's masonry is a cool grey in haze; lit warm and a little brighter, with a fine grain.
                    float grain = 1f + 0.10f * (float)Math.Sin(u * 0.37 + v * 0.53) * (float)Math.Sin(u * 0.11 - v * 0.17);
                    float stain = 1f - 0.22f * (0.5f + 0.5f * (float)Math.Sin(u * 0.013 + v * 0.021 + tint * 9.0)) * (0.5f + 0.5f * (float)Math.Sin(u * 0.041 - v * 0.033));
                    float k = lum * 1.28f * tint * grain * stain;
                    c = new Color(Mathf.Clamp(k * (1.05f + warm), 0f, 1f), Mathf.Clamp(k * 1.0f, 0f, 1f), Mathf.Clamp(k * (0.92f - warm), 0f, 1f), 1f);
                    // Bevel: lit top and right (the sunset), shaded bottom and left; a broken block's edge is darker.
                    int bev = Math.Max(4, (int)(5f * sf));
                    if (v < bev) c = new Color(Mathf.Min(1f, c.R + 0.14f * (1f - v / (float)bev)), Mathf.Min(1f, c.G + 0.10f * (1f - v / (float)bev)), c.B, 1f);
                    if (bw - 1 - u < bev) c = new Color(Mathf.Min(1f, c.R + 0.16f * (1f - (bw - 1 - u) / (float)bev)), Mathf.Min(1f, c.G + 0.09f * (1f - (bw - 1 - u) / (float)bev)), c.B, 1f);
                    if (bh - 1 - v < bev) c = new Color(c.R * 0.72f, c.G * 0.72f, c.B * 0.72f, 1f);
                    if (u < bev) c = new Color(c.R * 0.8f, c.G * 0.8f, c.B * 0.8f, 1f);
                }
                wall.SetPixel(xx, yy, c);
            }
        }
        // Cracks: a jagged dark line or two.
        int cracks = rng.Next(0, 3);
        for (int k = 0; k < cracks; k++)
        {
            float cx = bx + rng.Next(bw / 5, bw * 4 / 5), cy = by + rng.Next(0, bh / 3);
            int steps = rng.Next(bh / 2, bh);
            for (int t = 0; t < steps; t++)
            {
                cx += rng.Next(-3, 4); cy += 1.2f;
                int px = (int)cx, py = (int)cy;
                if (px < 1 || py < 0 || px >= wall.GetWidth() - 1 || py >= wall.GetHeight() || py >= by + bh - 2) break;
                for (int d = 0; d < 2; d++) { var o = wall.GetPixel(px + d, py); if (o.A > 0f) wall.SetPixel(px + d, py, new Color(o.R * 0.35f, o.G * 0.33f, o.B * 0.3f, 1f)); }
            }
        }
    }

    /// <summary>
    /// The skull and wings (owner: "match the concept's crest exactly"), matted from the upscaled crop: the crest is the warm or bright metal against the frame notch's dark grey backing, so a warm / bright test,
    /// closed over the eye sockets and feather gaps and softened a pixel, is its alpha. Same size and place as in the concept (wings x 150 to 375, y 214 to 262 in crop pixels), over the video.
    /// </summary>
    static void CrestMatte(Image img, float[] alpha, int W, int H, float sf, float sfy, bool faithful = false)
    {
        int x0 = (int)(136 * sf), x1 = (int)(390 * sf), y0 = (int)(208 * sfy), y1 = (int)(266 * sfy);
        int w = x1 - x0, h = y1 - y0;
        var m = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = img.GetPixel(x0 + x, y0 + y);
                float cx = (x0 + x) / sf, cy = (y0 + y) / sfy;
                float lum = p.R * 0.3f + p.G * 0.59f + p.B * 0.11f;
                bool warm = p.R >= p.B + 0.035f && lum > 0.2f, bright = lum > 0.42f && p.R >= p.B - 0.03f;
                bool on = warm || bright;
                if (cy > 247f && Mathf.Abs(cx - 262f) > 15f) on = false;   // below the wings only the skull's chin; the frame's top bar is steel, not crest
                if (cy > 263f) on = false;
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
        int r = Math.Max(2, (int)(1.4f * sf));
        var closed = Morph(Morph(m, true, r), false, r);
        // Drop specks: keep only the part connected to the middle (the skull).
        var keep = new bool[w * h]; var stack = new Stack<int>();
        int seed = (h * 45 / 100) * w + (int)((262f - 136f) * sf);
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
                { float m9 = Mathf.SmoothStep(0.15f, 0.85f, sum / 9f); int ai = (y0 + y) * W + x0 + x; alpha[ai] = faithful ? Mathf.Max(alpha[ai], m9) : m9; }
            }
    }

    /// <summary>A vertical box blur over a rectangle (the recess's rim and sides, where the upscale left dark scan-line streaks).</summary>
    static void SmoothRecess(Image img, Rect2I r, int radius)
    {
        if (radius < 1) return;
        int w = r.Size.X, h = r.Size.Y;
        var col = new Color[h];
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++) col[y] = img.GetPixel(r.Position.X + x, r.Position.Y + y);
            for (int y = 0; y < h; y++)
            {
                float rr = 0, gg = 0, bb = 0; int n = 0;
                for (int k = -radius; k <= radius; k++) { var c = col[Mathf.Clamp(y + k, 0, h - 1)]; rr += c.R; gg += c.G; bb += c.B; n++; }
                img.SetPixel(r.Position.X + x, r.Position.Y + y, new Color(rr / n, gg / n, bb / n, 1f));
            }
        }
    }

    static float[] BoxBlur(float[] a, int W, int H, int r)
    {
        if (r < 1) return a;
        var tmp = new float[a.Length]; var o = new float[a.Length];
        for (int y = 0; y < H; y++)
        {
            float sum = 0f; int row = y * W;
            for (int x = -r; x <= r; x++) sum += a[row + Mathf.Clamp(x, 0, W - 1)];
            for (int x = 0; x < W; x++)
            {
                tmp[row + x] = sum / (2 * r + 1);
                sum += a[row + Mathf.Min(W - 1, x + r + 1)] - a[row + Mathf.Max(0, x - r)];
            }
        }
        for (int x = 0; x < W; x++)
        {
            float sum = 0f;
            for (int y = -r; y <= r; y++) sum += tmp[Mathf.Clamp(y, 0, H - 1) * W + x];
            for (int y = 0; y < H; y++)
            {
                o[y * W + x] = sum / (2 * r + 1);
                sum += tmp[Mathf.Min(H - 1, y + r + 1) * W + x] - tmp[Mathf.Max(0, y - r) * W + x];
            }
        }
        return o;
    }

    /// <summary>
    /// Sits the overlay in the video's scene (owner 2026-11-15: "blend the overlaid additions to the main menu better"): the concept's flat grey daylight is graded toward the video's dark, warm sunset
    /// (contrast up, haze down, a warm tint), the far left of the wall and the lower part darkened, a warm rim light on the edges that face the light (right and top), a little of the sunset's haze mixed
    /// into the outer edge, and one soft contact shadow under everything (wall, frame, wordmark, crest), offset down and to the right. Fixed parameters, so the builder gives the same picture.
    /// </summary>
    static void FinishOverlay(Image img, float[] A, float[] D, float sf, float sfy)
    {
        int W = img.GetWidth(), H = img.GetHeight();
        // Shadow: the alpha blurred and shifted down and right.
        int sx = (int)(2 * sf), sy = (int)(8 * sfy);
        var blurred = BoxBlur(A, W, H, (int)(9 * sf));
        var haze = new Color(0.62f, 0.44f, 0.30f);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                var p = img.GetPixel(x, y);
                float u = x / (float)W, v = y / (float)H;
                // Grade: darker mids, more contrast, less grey, a warm tint, a lower and a leftward fall-off.
                float lum = p.R * 0.3f + p.G * 0.59f + p.B * 0.11f;
                Color c = p;
                c = new Color(lum + (c.R - lum) * 1.10f, lum + (c.G - lum) * 1.05f, lum + (c.B - lum) * 0.92f);
                float Cur(float k) => Mathf.Clamp((k - 0.5f) * 1.22f + 0.5f, 0f, 1f);
                c = new Color(Cur(c.R) * 1.05f, Cur(c.G) * 0.99f, Cur(c.B) * 0.9f);
                float expo = Mathf.Lerp(0.72f, 1f, Mathf.SmoothStep(0f, 0.32f, u)) * Mathf.Lerp(1f, 0.72f, Mathf.SmoothStep(0.55f, 1f, v));
                c = new Color(c.R * expo, c.G * expo, c.B * expo);
                // Rim light on the edges facing the sun (right, and the top): warm, strongest at the edge itself.
                float d = D[i];
                float edge = Mathf.Clamp(1f - d / (9f * sf), 0f, 1f);
                if (d > 0f && edge > 0f)
                {
                    float face = Mathf.Clamp(Mathf.SmoothStep(0.45f, 0.95f, u) + 0.5f * (1f - Mathf.SmoothStep(0f, 0.3f, v)), 0f, 1f);
                    float rim = edge * edge * face;
                    c = new Color(Mathf.Min(1f, c.R + 0.55f * rim), Mathf.Min(1f, c.G + 0.34f * rim), Mathf.Min(1f, c.B + 0.12f * rim));
                    // The video's haze over the outer edge.
                    c = c.Lerp(haze, 0.22f * edge * face);
                }
                // Composite over a soft contact shadow.
                float a = A[i];
                int si = Mathf.Clamp(y - sy, 0, H - 1) * W + Mathf.Clamp(x - sx, 0, W - 1);
                float sh = 0.5f * blurred[si];
                float outA = a + sh * (1f - a);
                if (outA <= 0.0001f) { img.SetPixel(x, y, new Color(0, 0, 0, 0)); continue; }
                float k = a / outA;
                img.SetPixel(x, y, new Color(c.R * k, c.G * k, c.B * k, outA));
            }
    }

    /// <summary>Distance from a point to a polygon's outline, positive inside and negative outside.</summary>
    static float SignedDistance(Vector2 p, Vector2[] poly)
    {
        float best = float.MaxValue; bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[j]; var b = poly[i];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            var ab = b - a; float t = ab.LengthSquared() > 0f ? Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f) : 0f;
            best = Mathf.Min(best, (p - (a + ab * t)).Length());
        }
        return inside ? best : -best;
    }

    /// <summary>
    /// A clean enlargement by <paramref name="k"/>: Lanczos, each output pixel then limited to the range of the source pixels around it (no overshoot, so no ringing halos at the edges), and a mild unsharp
    /// mask. Without a neural upscaler this is the best that can be done from the crop alone.
    /// </summary>
    static Image SmartUpscale(Image src, int k)
    {
        int w = src.GetWidth(), h = src.GetHeight(), W = w * k, H = h * k;
        var sp = src.GetData();
        var big = (Image)src.Duplicate();
        big.Resize(W, H, Image.Interpolation.Lanczos);
        var bp = big.GetData();
        for (int y = 0; y < H; y++)
        {
            int sy = Math.Min(h - 1, y / k);
            for (int x = 0; x < W; x++)
            {
                int sx = Math.Min(w - 1, x / k);
                int o = (y * W + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    int lo = 255, hi = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int v = sp[(Math.Clamp(sy + dy, 0, h - 1) * w + Math.Clamp(sx + dx, 0, w - 1)) * 4 + c];
                            if (v < lo) lo = v; if (v > hi) hi = v;
                        }
                    bp[o + c] = (byte)Math.Clamp((int)bp[o + c], lo, hi);
                }
                bp[o + 3] = 255;
            }
        }
        var outImg = Image.CreateFromData(W, H, false, Image.Format.Rgba8, bp);
        Sharpen(outImg, 0.45f);
        return outImg;
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
                if (y < 7) a = 0f;
                // The wall above and beside the frame is the synthesised wall's job: the cut's own stone fades in over its first rows at the left, so no lit rectangle shows above the frame.
                if (x < 80 && y < 60) a = Math.Min(a, Mathf.SmoothStep(7f, 60f, y) * Mathf.SmoothStep(0f, 1f, 1f - Math.Max(0f, (x - 40f) / 40f)) * 0f + Mathf.SmoothStep(20f, 60f, y));
                if (y > h - 7 && x > 70) a = 0f;
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
    /// A continuous stone wall for the left edge, made from the crop's own stone (its top-left block): the sample is stacked down 1280 px with every second block upside down, shifted sideways
    /// and cross-faded at the seams; the right edge is irregular (two slow waves), shaded towards the edge, and casts a soft shadow onto the video.
    /// </summary>
    static Image WallSynth(Image crop)
    {
        var sample = Scaled(Cut(crop, new Rect2I(0, 0, 70, 100)), 1.6f);
        int sw = sample.GetWidth(), sh = sample.GetHeight();
        const int H = 1280;
        int W = sw;
        var wall = Image.CreateEmpty(W, H, false, Image.Format.Rgba8);
        const int fade = 16;
        for (int y = 0; y < H; y++)
        {
            int block = y / (sh - fade), within = y % (sh - fade);
            for (int x = 0; x < W; x++)
            {
                Color Pick(int b, int yy)
                {
                    int sy = b % 2 == 1 ? sh - 1 - yy : yy;
                    int sx = (x + (b * 37) % W) % sw;
                    return sample.GetPixel(sx, Mathf.Clamp(sy, 0, sh - 1));
                }
                var c = Pick(block, within);
                if (within >= sh - 2 * fade && y + fade < H)
                {
                    float t = (within - (sh - 2 * fade)) / (float)fade;
                    c = c.Lerp(Pick(block + 1, within - (sh - 2 * fade)), Mathf.Clamp(t, 0f, 1f));
                }
                wall.SetPixel(x, y, c);
            }
        }
        for (int y = 0; y < H; y++)
        {
            float edge = W * 0.80f + W * 0.12f * (Mathf.Sin(y * 0.021f) + Mathf.Sin(y * 0.057f + 1.3f) * 0.6f) / 1.6f;
            for (int x = 0; x < W; x++)
            {
                var c = wall.GetPixel(x, y);
                float shade = 1f - 0.5f * Mathf.SmoothStep(edge - 34f, edge, x);
                float a = Mathf.Clamp((edge - x) / 6f, 0f, 1f);
                if (a <= 0f) { c = new Color(0f, 0f, 0f, 0.4f * Mathf.Clamp(1f - (x - edge) / 16f, 0f, 1f)); }
                else c = new Color(c.R * shade, c.G * shade, c.B * shade, a);
                wall.SetPixel(x, y, c);
            }
        }
        return wall;
    }

    /// <summary>
    /// The wordmark rebuilt from the crop's letters: the lettering mask (saturation key, holes closed, one pixel bolder), the chrome of the letters raised in contrast with a bright rim, a deep navy
    /// outline round them and a soft drop shadow, all drawn here so no scrap of the JPEG's background is left.
    /// </summary>
    static Image WordmarkBuild(Image img)
    {
        const int pad = 8;
        int w = img.GetWidth(), h = img.GetHeight(), W = w + pad * 2, H = h + pad * 2;
        var m = new float[W * H];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = img.GetPixel(x, y);
                float max = Mathf.Max(p.R, Mathf.Max(p.G, p.B)), min = Mathf.Min(p.R, Mathf.Min(p.G, p.B));
                float sat = max <= 0.001f ? 0f : (max - min) / max;
                m[(y + pad) * W + x + pad] = p.B >= p.R + 0.05f && sat > 0.28f ? 1f : 0f;
            }
        float[] Dilate(float[] s, int r) { var o = new float[s.Length]; for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { float v = 0f; for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++) { if (dx * dx + dy * dy > r * r + 1) continue; v = Mathf.Max(v, s[Mathf.Clamp(y + dy, 0, H - 1) * W + Mathf.Clamp(x + dx, 0, W - 1)]); } o[y * W + x] = v; } return o; }
        float[] Erode(float[] s, int r) { var o = new float[s.Length]; for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { float v = 1f; for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++) { if (dx * dx + dy * dy > r * r + 1) continue; v = Mathf.Min(v, s[Mathf.Clamp(y + dy, 0, H - 1) * W + Mathf.Clamp(x + dx, 0, W - 1)]); } o[y * W + x] = v; } return o; }
        var closed = Erode(Dilate(m, 2), 2);                 // holes of the keyed JPEG closed
        var body = Dilate(closed, 1);                         // one pixel bolder
        var outline = Dilate(body, 3);
        var shadowMask = Dilate(body, 4);
        var outImg = Image.CreateEmpty(W, H, false, Image.Format.Rgba8);
        outImg.Fill(new Color(0, 0, 0, 0));
        // Shadow: the outline's shape shifted down and right, soft.
        const int sx = 4, sy = 5;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float sum = 0f;
                for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) sum += shadowMask[Mathf.Clamp(y - sy + dy, 0, H - 1) * W + Mathf.Clamp(x - sx + dx, 0, W - 1)];
                float a = 0.62f * sum / 25f;
                if (a > 0f) outImg.SetPixel(x, y, new Color(0.01f, 0.01f, 0.03f, a));
            }
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                if (outline[i] > 0.5f && body[i] < 0.5f) outImg.SetPixel(x, y, new Color(0.015f, 0.03f, 0.09f, 1f));
            }
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                if (body[i] < 0.5f) continue;
                int sxp = Mathf.Clamp(x - pad, 0, w - 1), syp = Mathf.Clamp(y - pad, 0, h - 1);
                var p = img.GetPixel(sxp, syp);
                // Levels: a deeper black point and more contrast; letters that came out dark in the crop's edge pixels are pulled to the body's blue.
                float L(float v) => Mathf.Clamp((v - 0.10f) * 1.5f + 0.05f, 0f, 1f);
                var c = new Color(L(p.R), L(p.G), L(p.B), 1f);
                bool rim = body[(y - 1) * W + x] < 0.5f || body[y * W + x - 1] < 0.5f;   // the lit top and left edge
                if (rim) c = new Color(Mathf.Min(1f, c.R + 0.32f), Mathf.Min(1f, c.G + 0.34f), Mathf.Min(1f, c.B + 0.3f), 1f);
                outImg.SetPixel(x, y, c);
            }
        return Scaled(outImg, 1.25f);
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
        return Scaled(Chamfer(img, chamfer * F), S / F);
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
        tl = Scaled(tl, S / F); top = Scaled(top, S / F); side = Scaled(side, S / F);
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
