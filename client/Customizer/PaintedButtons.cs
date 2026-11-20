using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// Buttons that draw the concept's own painted plates (phase 3 and 4 of the UI concept redesign): a style per state from a dark and a gold texture, expanded past the button's rectangle by the textures' margins
/// (so the glow and bevel the texture carries meet the neighbours), the live text kept on the button (the tree, the tests, the screen reader) but drawn transparent where the label is in the picture. A focus ring over
/// the plate shows the keyboard and pad focus where the plate has no gold state of its own. Ours (spec "UI concept redesign").
/// </summary>
public static partial class PaintedButtons
{
    /// <summary>Margins (left, top, right, bottom) in the panel's own pixels.</summary>
    public readonly record struct Pad(float L, float T, float R, float B)
    {
        public Pad(float all) : this(all, all, all, all) { }
    }

    /// <summary>Screen pixels per concept pixel at 1080p (the Armoury's page scale); callers on another scale pass their own.</summary>
    public const float ConceptPx = 2.45f;

    /// <summary>
    /// What a painted piece's nine-slice keeps whole: the cap sizes (left, top, right, bottom) in the picture's own pixels, measured from the PNGs in client/assets/ui (the chamfered end, the rim and the corner studs), and the
    /// inner bevel's thickness (x, y) in concept pixels, from which the text inset starts. Ours (spec "Text inset and nine-slice").
    /// </summary>
    public readonly record struct CapArt(float L, float T, float R, float B, float RimX, float RimY);

    static readonly Dictionary<string, CapArt> CapTable = new(StringComparer.Ordinal)
    {
        ["opt-plate-blank"] = new(40f, 40f, 40f, 40f, 2.5f, 2f), ["opt-plate-gold-blank"] = new(44f, 44f, 44f, 44f, 2.5f, 2f),
        ["opt-apply-dark"] = new(40f, 40f, 40f, 40f, 2.5f, 2f), ["opt-apply-gold"] = new(44f, 44f, 44f, 44f, 2.5f, 2f),
        ["opt-revert-dark"] = new(40f, 40f, 40f, 40f, 2.5f, 2f), ["opt-revert-gold"] = new(44f, 44f, 44f, 44f, 2.5f, 2f),
        ["opt-back-dark"] = new(30f, 26f, 30f, 26f, 2.5f, 2f),
        ["opt-tab-graphics-dark"] = new(40f, 34f, 40f, 34f, 2.5f, 2f), ["opt-tab-graphics-gold"] = new(44f, 40f, 44f, 40f, 2.5f, 2f),
        ["opt-tab-controls-dark"] = new(40f, 34f, 40f, 34f, 2.5f, 2f), ["opt-tab-controls-gold"] = new(44f, 40f, 44f, 40f, 2.5f, 2f),
        ["opt-tab-interface-dark"] = new(40f, 34f, 40f, 34f, 2.5f, 2f), ["opt-tab-interface-gold"] = new(44f, 40f, 44f, 40f, 2.5f, 2f),
        ["opt-tab-sound-dark"] = new(40f, 34f, 40f, 34f, 2.5f, 2f), ["opt-tab-sound-gold"] = new(44f, 40f, 44f, 40f, 2.5f, 2f),
        ["arm-row-dark"] = new(40f, 20f, 40f, 20f, 3f, 1.5f), ["arm-row-gold"] = new(56f, 34f, 56f, 34f, 3f, 1.5f),
        ["arm-tile-dark"] = new(64f, 64f, 64f, 64f, 4f, 3f), ["arm-tile-gold"] = new(68f, 68f, 68f, 68f, 4f, 3f),
        ["arm-confirm"] = new(96f, 48f, 96f, 48f, 5f, 3f), ["arm-back"] = new(84f, 40f, 84f, 40f, 5f, 3f),
        ["arm-card"] = new(300f, 44f, 70f, 44f, 4f, 3f),
    };

    /// <summary>The caps of a piece that is not in the table: a third of its height each way (never more than a quarter of its width).</summary>
    public static CapArt DefaultCaps(Texture2D tex) { float c = Mathf.Min(tex.GetHeight() * 0.33f, tex.GetWidth() * 0.25f); return new(c, c, c, c, 2.5f, 2f); }

    public static CapArt CapsOf(string name, Texture2D tex) => CapTable.TryGetValue(name, out var c) ? c : DefaultCaps(tex);

    /// <summary>
    /// A nine-slice whose end caps keep the proportion of the picture: the caps are drawn at the scale that fits the plate's height (the plate is the picture squeezed to its own height, as before) and only the middle stretches
    /// with the width. (StyleBoxTexture draws its margins one to one on the screen, so a 4x picture shown small cannot use it.)
    /// </summary>
    public sealed partial class CapStyle : StyleBox
    {
        public Texture2D? Texture { get; set; }
        public Color ModulateColor { get; set; } = Colors.White;
        public CapArt Caps { get; set; }
        /// <summary>How far the picture is drawn past the rectangle: left, top, right, bottom, in screen pixels.</summary>
        public Vector4 Expand { get; set; }
        /// <summary>A flat fill under the picture, inset from the rectangle (a plate whose middle is see-through, such as a hover hint over a busy page); transparent for none.</summary>
        public Color Backing { get; set; } = Colors.Transparent;

        public override Rect2 _GetDrawRect(Rect2 rect) => new(rect.Position - new Vector2(Expand.X, Expand.Y), rect.Size + new Vector2(Expand.X + Expand.Z, Expand.Y + Expand.W));

        public override void _Draw(Rid canvas, Rect2 rect)
        {
            if (Texture is null) return;
            rect = _GetDrawRect(rect);
            float tw = Texture.GetWidth(), th = Texture.GetHeight();
            if (Backing.A > 0f)
            {
                // The flat fill follows the plate's chamfered ends (owner, 2026-11-20: no square corners behind the plate): an octagon inside the rim.
                var inner = rect.Grow(-Mathf.Max(2f, rect.Size.Y * 0.125f));   // inside the art's own transparent margin and its rim
                RenderingServer.CanvasItemAddPolygon(canvas, Steel.Chamfered(inner, Mathf.Max(3f, rect.Size.Y * 0.15f)), [Backing]);
            }
            float k = Mathf.Min(rect.Size.Y / th, rect.Size.X / Mathf.Max(1f, Caps.L + Caps.R));
            float dl = Caps.L * k, dr = Caps.R * k, dt = Caps.T * k, db = Caps.B * k;
            if (rect.Size.Y < dt + db) { float f = rect.Size.Y / (dt + db); dt *= f; db *= f; }
            float x0 = rect.Position.X, y0 = rect.Position.Y;
            float[] sx = [0f, Caps.L, tw - Caps.R, tw], sy = [0f, Caps.T, th - Caps.B, th];
            float[] dx = [x0, x0 + dl, x0 + rect.Size.X - dr, x0 + rect.Size.X], dy = [y0, y0 + dt, y0 + rect.Size.Y - db, y0 + rect.Size.Y];
            for (int j = 0; j < 3; j++)
                for (int i = 0; i < 3; i++)
                {
                    var d = new Rect2(dx[i], dy[j], dx[i + 1] - dx[i], dy[j + 1] - dy[j]); var src = new Rect2(sx[i], sy[j], sx[i + 1] - sx[i], sy[j + 1] - sy[j]);
                    if (d.Size.X <= 0f || d.Size.Y <= 0f || src.Size.X <= 0f || src.Size.Y <= 0f) continue;
                    RenderingServer.CanvasItemAddTextureRectRegion(canvas, d, Texture.GetRid(), src, ModulateColor);
                }
        }

        public CapStyle Copy() => new()
        {
            Texture = Texture, ModulateColor = ModulateColor, Caps = Caps, Expand = Expand, Backing = Backing,
            ContentMarginLeft = ContentMarginLeft, ContentMarginRight = ContentMarginRight, ContentMarginTop = ContentMarginTop, ContentMarginBottom = ContentMarginBottom,
        };
    }

    /// <summary>The inset of text from a plate's inner bevel (owner, 2026-11-20): 4 concept pixels at the sides, 2 above and below, so no letter touches a border.</summary>
    public static readonly Vector2 Inset = new(4f, 2f);

    /// <summary>A painted plate as a style: <see cref="CapStyle"/> from the table, the text's margins the bevel plus <see cref="Inset"/> (concept pixels times <paramref name="cpx"/>), drawn expanded by <paramref name="p"/>.</summary>
    public static CapStyle Box(Texture2D tex, string name, Pad p, float s, Color mod, float cpx = ConceptPx)
    {
        var caps = CapsOf(name, tex);
        var box = new CapStyle { Texture = tex, ModulateColor = mod, Caps = caps, Expand = new Vector4(p.L * s, p.T * s, p.R * s, p.B * s) };
        SetInset(box, caps, cpx);
        return box;
    }

    /// <summary>Gives a style the default text margins of its art (the bevel plus <see cref="Inset"/>).</summary>
    public static void SetInset(StyleBox box, CapArt caps, float cpx)
    {
        float x = (caps.RimX + Inset.X) * cpx, y = (caps.RimY + Inset.Y) * cpx;
        box.ContentMarginLeft = x; box.ContentMarginRight = x; box.ContentMarginTop = y; box.ContentMarginBottom = y;
    }

    /// <summary>
    /// Text too long for its plate steps down one level of the size table, then wraps: never an ellipsis, never a clipped letter. <paramref name="unit"/> is the level (<see cref="UiType"/>), <paramref name="avail"/> the room in
    /// pixels. Returns the unit used; the caller sets the font size and, when <paramref name="wrap"/> comes back true, word wrap.
    /// </summary>
    public static int FitUnit(Font font, string text, int unit, float avail, out bool wrap)
    {
        wrap = false;
        if (text.Length == 0 || avail <= 0f || Width(font, text, unit) <= avail) return unit;
        int down = UiType.StepDown(unit);
        if (Width(font, text, down) <= avail) return down;
        wrap = true;
        return down;
    }

    public static float Width(Font font, string text, int unit) => font.GetStringSize(text, HorizontalAlignment.Left, -1, UiType.Px(unit)).X;

    /// <summary>Fits a button's own text to its plate (see <see cref="FitUnit"/>); the button's text margins are the room taken off <paramref name="width"/> (its own width when null).</summary>
    public static void FitButton(Button b, int unit, float? width = null)
    {
        var font = b.GetThemeFont("font");
        float avail = (width ?? b.Size.X) - b.GetThemeStylebox("normal").GetMinimumSize().X;
        int u = FitUnit(font, b.Text, unit, avail, out bool wrap);
        b.AddThemeFontSizeOverride("font_size", UiType.Px(u));
        b.ClipText = false;
        b.AutowrapMode = wrap ? TextServer.AutowrapMode.Word : TextServer.AutowrapMode.Off;
    }

    /// <summary>
    /// A group of siblings (the tabs, the class rows, the tiles, the cards of one list) shares one size (owner: one text size per level in a menu): the smallest that any member needs after its one step down; a member that is
    /// still too wide at that size wraps.
    /// </summary>
    public static void FitGroup(IEnumerable<(Control Control, string Text, float Avail)> items, int unit)
    {
        var list = items.ToList(); int u = unit;
        foreach (var i in list) u = Math.Min(u, FitUnit(i.Control.GetThemeFont("font"), i.Text, unit, i.Avail, out _));
        foreach (var i in list)
        {
            bool wrap = Width(i.Control.GetThemeFont("font"), i.Text, u) > i.Avail;
            i.Control.AddThemeFontSizeOverride("font_size", UiType.Px(u));
            var mode = wrap ? TextServer.AutowrapMode.Word : TextServer.AutowrapMode.Off;
            if (i.Control is Label l) { l.ClipText = false; l.AutowrapMode = mode; }
            else if (i.Control is Button b) { b.ClipText = false; b.AutowrapMode = mode; }
        }
    }

    /// <summary>The same for a label laid out on a plate: <paramref name="avail"/> is its width in pixels.</summary>
    public static void FitLabel(Label l, int unit, float avail)
    {
        int u = FitUnit(l.GetThemeFont("font"), l.Text, unit, avail, out bool wrap);
        l.AddThemeFontSizeOverride("font_size", UiType.Px(u));
        l.ClipText = false;
        l.AutowrapMode = wrap ? TextServer.AutowrapMode.Word : TextServer.AutowrapMode.Off;
    }

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
    public static bool Apply(Button b, string dark, string gold, Pad darkPad, Pad goldPad, float s, bool goldFocus, bool paintedText, bool goldRest = false, bool noGlow = true, float cpx = ConceptPx)
    {
        if (ConceptArt.Get(dark) is not { } d || ConceptArt.Get(gold) is not { } g) return false;
        b.TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps;   // ui/text-scale: the 4x plate is shrunk once with its mipmaps, not point-skipped
        var rest = goldRest ? Box(g, gold, goldPad, s, Colors.White, cpx) : Box(d, dark, darkPad, s, Colors.White, cpx);
        b.AddThemeStyleboxOverride("normal", rest);
        b.AddThemeStyleboxOverride("hover", goldRest ? Box(g, gold, goldPad, s, new Color(1.08f, 1.05f, 1f), cpx) : Box(d, dark, darkPad, s, ConceptArt.Warm, cpx));
        b.AddThemeStyleboxOverride("pressed", Box(g, gold, goldPad, s, Colors.White, cpx));
        b.AddThemeStyleboxOverride("hover_pressed", Box(g, gold, goldPad, s, Colors.White, cpx));
        var focusGold = Box(g, gold, goldPad, s, Colors.White, cpx); var focusRing = Ring(s);
        b.AddThemeStyleboxOverride("focus", goldFocus && !goldRest ? focusGold : focusRing);
        b.SetMeta("pb_focus_gold", focusGold); b.SetMeta("pb_focus_ring", focusRing);
        b.AddThemeStyleboxOverride("disabled", Box(d, dark, darkPad, s, new Color(0.5f, 0.5f, 0.5f), cpx));
        if (noGlow) b.SetMeta(FocusGlow.NoGlowMeta, true); else if (b.HasMeta(FocusGlow.NoGlowMeta)) b.RemoveMeta(FocusGlow.NoGlowMeta);
        if (paintedText)
            foreach (var colour in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color", "font_disabled_color" }) b.AddThemeColorOverride(colour, Colors.Transparent);
        return true;
    }
}
