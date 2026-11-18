using System;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// Phase 4b: the row controls of panel 04 (the slider, the OFF / ON switch and the arrow pill) cut from the concept's own pixels, so the live rows can draw them (docs/ui-concept-textures.md "Phase 4b").
/// Every rectangle is in the panel's screen coordinates (the concept's 780 x 440 screen, panel coordinates minus (10, 26)); the text on a control (ADAPTIVE, OFF, ON) is painted out so the live row writes it.
/// </summary>
public static partial class UiTextureBuilder
{
    /// <summary>Screen rectangles of the concept's controls: the arrow pill (HUD VISIBILITY), the OFF switch (REDUCED MOTION), the ON switch (SUBTITLES), the slider (UI BRIGHTNESS) and its knob.</summary>
    public static readonly Rect2 CtlPill = new(250f, 176f, 117f, 22f), CtlSwitchOff = new(565f, 143f, 76f, 23f), CtlSwitchOn = new(267f, 235f, 79f, 24f), CtlSlider = new(506f, 242f, 126f, 17f), CtlKnob = new(593f, 241.5f, 16f, 17f);

    static void OptionsControlTextures(Image up, float sf, Action<string, Image> save)
    {
        Vector2 toRegion = new(-46f, -26f);   // screen -> region (panel coordinates minus the region's origin)
        Rect2 R(Rect2 r) => new(r.Position + toRegion, r.Size);
        var pillBlank = R(new Rect2(284f, 180f, 52f, 13f)); var offBlank = R(new Rect2(603f, 148f, 23f, 13f)); var onBlank = R(new Rect2(285f, 239f, 23f, 15f));
        var clean = BlankLabels(up, sf, [pillBlank, offBlank, onBlank]);
        save("opt-pill", Cut4(clean, sf, R(CtlPill), 0f));
        save("opt-switch-off", Cut4(clean, sf, R(CtlSwitchOff), 0f));
        save("opt-switch-on", Cut4(clean, sf, R(CtlSwitchOn), 0f));
        // The slider: the track with its gold fill replaced by the empty channel (the dark columns right of the knob, tiled), the gold fill on its own, and the knob.
        var trackRect = R(CtlSlider);
        var track = Cut4(up, sf, trackRect, 0f);
        int x0 = (int)MathF.Round(trackRect.Position.X * sf);
        int Lx(float screenX) => (int)MathF.Round((screenX - 46f) * sf) - x0;
        int srcA = Lx(613f), srcB = Lx(620f), dstA = Lx(514f), dstB = Lx(611f);
        var profile = new Color[track.GetHeight()];
        for (int y = 0; y < profile.Length; y++) { Color c = default; for (int x = srcA; x < srcB; x++) { var q = track.GetPixel(x, y); c.R += q.R; c.G += q.G; c.B += q.B; } int n = srcB - srcA; profile[y] = new Color(c.R / n, c.G / n, c.B / n); }
        for (int x = dstA; x < dstB; x++) for (int y = 0; y < profile.Length; y++) track.SetPixel(x, y, profile[y]);
        save("opt-slider-track", track);
        var full = Cut4(up, sf, trackRect, 0f);
        var fill = Image.CreateEmpty(full.GetWidth(), full.GetHeight(), false, Image.Format.Rgba8);
        int fy0 = (int)MathF.Round((245.2f - CtlSlider.Position.Y) * sf), fy1 = (int)MathF.Round((256.2f - CtlSlider.Position.Y) * sf);
        for (int y = fy0; y < fy1; y++) for (int x = Lx(513f); x < Lx(594f); x++) fill.SetPixel(x, y, full.GetPixel(x, y));
        save("opt-slider-fill", fill.GetRegion(new Rect2I(Lx(513f), 0, Lx(594f) - Lx(513f), fill.GetHeight())));  // cropped to the gold: its rounded left end is the nine-patch margin
        var knob = Cut4(up, sf, R(CtlKnob), 0f);
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
