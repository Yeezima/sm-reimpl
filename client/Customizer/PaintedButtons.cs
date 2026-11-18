using Godot;

namespace SmClient.Customizer;

/// <summary>
/// Buttons that draw the concept's own painted plates (phase 3 and 4 of the UI concept redesign): a StyleBoxTexture per state from a dark and a gold texture, expanded past the button's rectangle by the textures' margins
/// (so the glow and bevel the texture carries meet the neighbours), the live text kept on the button (the tree, the tests, the screen reader) but drawn transparent where the label is in the picture. A focus ring over
/// the plate shows the keyboard and pad focus where the plate has no gold state of its own. Ours (spec "UI concept redesign").
/// </summary>
public static class PaintedButtons
{
    /// <summary>Margins (left, top, right, bottom) in the panel's own pixels.</summary>
    public readonly record struct Pad(float L, float T, float R, float B)
    {
        public Pad(float all) : this(all, all, all, all) { }
    }

    static StyleBoxTexture Box(Texture2D tex, Pad p, float s, Color mod) => new()
    {
        Texture = tex, ModulateColor = mod, DrawCenter = true,
        ExpandMarginLeft = p.L * s, ExpandMarginTop = p.T * s, ExpandMarginRight = p.R * s, ExpandMarginBottom = p.B * s,
        AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
    };

    /// <summary>The focus ring: a thin gold outline over the plate.</summary>
    public static StyleBoxFlat Ring(float s, float grow = 1.2f) => new()
    {
        BgColor = Colors.Transparent, DrawCenter = false, BorderColor = new Color(1f, 0.86f, 0.4f), CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
        BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
        ExpandMarginLeft = grow * s, ExpandMarginTop = grow * s, ExpandMarginRight = grow * s, ExpandMarginBottom = grow * s,
    };

    /// <summary>A tab that is already drawn as the gold plate shows its focus as that plate (no ring round it, which would not follow the plate's chamfers); a tab that is not selected keeps the ring.</summary>
    public static void FocusAsPlate(Button b, bool plate)
    {
        if (b.GetMeta("pb_focus_gold", default) is not { VariantType: Variant.Type.Object } g || b.GetMeta("pb_focus_ring", default) is not { VariantType: Variant.Type.Object } r) return;
        b.AddThemeStyleboxOverride("focus", (StyleBox)(plate ? g.AsGodotObject() : r.AsGodotObject()));
    }

    /// <summary>Paints <paramref name="b"/>: rest = dark, hover = dark warmed, pressed = gold; focus = gold when <paramref name="goldFocus"/> (a plate with a gold twin), else a ring over the rest plate.</summary>
    public static bool Apply(Button b, string dark, string gold, Pad darkPad, Pad goldPad, float s, bool goldFocus, bool paintedText, bool goldRest = false, bool noGlow = true)
    {
        if (ConceptArt.Get(dark) is not { } d || ConceptArt.Get(gold) is not { } g) return false;
        b.TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps;   // ui/text-scale: the 4x plate is shrunk once with its mipmaps, not point-skipped
        var rest = goldRest ? Box(g, goldPad, s, Colors.White) : Box(d, darkPad, s, Colors.White);
        b.AddThemeStyleboxOverride("normal", rest);
        b.AddThemeStyleboxOverride("hover", goldRest ? Box(g, goldPad, s, new Color(1.08f, 1.05f, 1f)) : Box(d, darkPad, s, ConceptArt.Warm));
        b.AddThemeStyleboxOverride("pressed", Box(g, goldPad, s, Colors.White));
        b.AddThemeStyleboxOverride("hover_pressed", Box(g, goldPad, s, Colors.White));
        var focusGold = Box(g, goldPad, s, Colors.White); var focusRing = Ring(s);
        b.AddThemeStyleboxOverride("focus", goldFocus && !goldRest ? focusGold : focusRing);
        b.SetMeta("pb_focus_gold", focusGold); b.SetMeta("pb_focus_ring", focusRing);
        b.AddThemeStyleboxOverride("disabled", Box(d, darkPad, s, new Color(0.5f, 0.5f, 0.5f)));
        if (noGlow) b.SetMeta(FocusGlow.NoGlowMeta, true); else if (b.HasMeta(FocusGlow.NoGlowMeta)) b.RemoveMeta(FocusGlow.NoGlowMeta);
        if (paintedText)
            foreach (var colour in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color", "font_disabled_color" }) b.AddThemeColorOverride(colour, Colors.Transparent);
        return true;
    }
}
