using System;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// ui/text-scale (owner, 2026-11-18: "all text consistent in size and scale, no blurry text, buttons and sliders scaled proportionally"): the one size table of the menus and the one UI scale of the window.
///
/// <b>Scale.</b> The menus are laid out in a virtual space (<see cref="UiKit.VirtualWidth"/> x <see cref="UiKit.VirtualHeight"/> at least) that the window scales by <see cref="WindowScale"/>. That factor is
/// snapped to whole sixths (<see cref="SnapScale"/>), so every size in the table, a multiple of 6 virtual pixels, is a whole number of window pixels: live text is rasterised at the size it is drawn, never resampled.
/// In a match (the virtual space is off, one virtual pixel is one window pixel) the same table is multiplied by <see cref="TextScale"/>, the window's height over 1080 in sixths.
///
/// <b>Levels.</b> One row of the table per kind of text, the same on every screen: the page title on its plate, the main action, plates (buttons, tabs, BACK, APPLY), row labels, row values (and notes, headings,
/// fields, table cells), captions. Pages ask <see cref="Px"/> and never compute a size of their own.
/// </summary>
public static class UiType
{
    /// <summary>The sizes at UI scale 1 (virtual pixels, multiples of 6).</summary>
    public const int Title = 54, Primary = 48, Plate = 36, Label = 36, Value = 30, Caption = 24, Score = 72, Small = 18;

    static SceneTree? Tree => Engine.GetMainLoop() as SceneTree;

    /// <summary>Whether the menus' virtual space is on (the window scales the 1920 x 1080 canvas).</summary>
    public static bool Virtual => Tree?.Root is { } root && root.ContentScaleMode == Window.ContentScaleModeEnum.CanvasItems && root.ContentScaleSize.X > 0;

    /// <summary>Window pixels per virtual pixel (1 when the virtual space is off).</summary>
    public static float WindowScale
    {
        get
        {
            if (!Virtual || Tree?.Root is not { } root) return 1f;
            return Mathf.Min(root.Size.X / (float)root.ContentScaleSize.X, root.Size.Y / (float)root.ContentScaleSize.Y);
        }
    }

    /// <summary>The factor the table is multiplied by: 1 in the virtual space (the window scales it), the window's own snapped scale in a match.</summary>
    public static float TextScale
    {
        get
        {
            if (Virtual || Tree?.Root is not { } root) return 1f;
            return Mathf.Max(0.5f, SnapScale(Mathf.Min(root.Size.X / (float)UiKit.VirtualWidth, root.Size.Y / (float)UiKit.VirtualHeight)));
        }
    }

    /// <summary>Changes whenever the scale a size depends on changes (pages that cache styled sizes compare it).</summary>
    public static int Key => Mathf.RoundToInt(WindowScale * 600f) * 7919 + Mathf.RoundToInt(TextScale * 600f);

    /// <summary>A window scale as a whole number of sixths (the nearest, never under a half).</summary>
    public static float SnapScale(float raw) => raw <= 0f ? 1f : Mathf.Max(3f, Mathf.Round(raw * 6f)) / 6f;

    /// <summary>
    /// Puts the window's content scale on a whole number of sixths (the virtual space is then the window divided by it, at least about 1800 x 1000), and the viewport's 2D drawing on whole window pixels (text and art
    /// are not placed between pixels). Called when the menus take the window and whenever the window changes size.
    /// </summary>
    public static void ApplyWindow(Window window)
    {
        var size = window.Size;
        if (size.X < 8 || size.Y < 8) return;
        float raw = Mathf.Min(size.X / (float)UiKit.VirtualWidth, size.Y / (float)UiKit.VirtualHeight);
        float w = SnapScale(raw);
        // Never leave the pages less than 94 % of the 1920 x 1080 space they are laid out for.
        while (w > 0.5f && (size.X / w < UiKit.VirtualWidth * 0.94f || size.Y / w < UiKit.VirtualHeight * 0.94f)) w -= 1f / 6f;
        var virt = new Vector2I(Mathf.RoundToInt(size.X / w), Mathf.RoundToInt(size.Y / w));
        if (window.ContentScaleMode != Window.ContentScaleModeEnum.CanvasItems) window.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
        if (window.ContentScaleAspect != Window.ContentScaleAspectEnum.Expand) window.ContentScaleAspect = Window.ContentScaleAspectEnum.Expand;
        if (window.ContentScaleSize != virt) window.ContentScaleSize = virt;
        window.Snap2DTransformsToPixel = true; window.Snap2DVerticesToPixel = true;
    }

    /// <summary>The menus give the window back (a match draws in window pixels, the HUD's snapping is its own).</summary>
    public static void ReleaseWindow(Window window)
    {
        window.Snap2DTransformsToPixel = false; window.Snap2DVerticesToPixel = false;
    }

    /// <summary>A size of the table at the current scale, as a font size whose drawn size is a whole number of window pixels.</summary>
    public static int Px(int unit) => Snap(Mathf.RoundToInt(unit * TextScale));

    /// <summary>
    /// A size in virtual pixels moved to the nearest one (within 3) that is a whole number of window pixels at the current <see cref="WindowScale"/>; sizes of the table already are, at every sixth.
    /// </summary>
    public static int Snap(int virtualPx, int atLeast = 6)
    {
        float w = WindowScale;
        if (w <= 0f || Mathf.Abs(w - 1f) < 0.0001f) return virtualPx;
        for (int d = 0; d <= 3; d++)
            foreach (int v in d == 0 ? new[] { virtualPx } : new[] { virtualPx - d, virtualPx + d })
            {
                if (v < atLeast) continue;
                float p = v * w;
                if (Mathf.Abs(p - Mathf.Round(p)) < 0.02f) return v;
            }
        return virtualPx;
    }

    /// <summary>
    /// The level of the table a hand-laid page's own size belongs to (the nearest by role: 18 small, 24 caption, 30 value, 36 label, 48 primary, 54 title, 72 score); with <see cref="Px"/> it gives the font size to draw.
    /// </summary>
    public static int Level(int size) => size <= 20 ? Small : size <= 27 ? Caption : size <= 33 ? Value : size <= 41 ? Label : size <= 51 ? Primary : size <= 62 ? Title : Score;

    /// <summary>The same for a size computed by a page from its own art scale (a custom-drawn board's row): at least 8 window pixels, a whole number of them.</summary>
    public static int Fit(float virtualPx) => Snap(Math.Max(1, Mathf.RoundToInt(virtualPx)));
}
