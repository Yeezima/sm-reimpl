using Godot;
using SmClient.Customizer;

namespace SmClient.Extras.Warfront;

/// <summary>
/// The one look of Warfront's in-match and ship screens (owner, 2026-11-20: "the exact style of the existing main menu, options, classic pvp, armory UI"): the concept's lettering (<see cref="UiKit.ConceptFont"/>), the sizes of
/// the one table (<see cref="UiType"/>: siblings share a size), the Steel palette, and the painted pieces the Tab scoreboard already draws with (<see cref="BoardStyle"/>: the opt-plate art, the recessed table, the dark strip).
/// Every Warfront panel laid out in the 1920 by 1080 design space asks here instead of drawing flat rectangles or choosing a colour. Sizes are design pixels (the panels sit in <see cref="WarfrontUiFit"/> or in the menu's virtual space,
/// both of which scale them), so they are the table's own numbers, not <see cref="UiType.Px"/>. Ours (spec "Warfront panel style").
/// </summary>
public static class WarfrontStyle
{
    /// <summary>Headings, the readouts' main line, row labels (the table's value level).</summary>
    public const int Heading = UiType.Value;
    /// <summary>Body lines, cells and notes (the caption level).</summary>
    public const int Body = UiType.Caption;
    /// <summary>The centre banners (the primary level).</summary>
    public const int Banner = UiType.Primary;
    /// <summary>The smallest level: counters and the heading of a table's columns.</summary>
    public const int Small = UiType.Small;

    public static readonly Color Ink = Steel.Text, Dim = Steel.TextDim, Gold = Steel.GoldHi, GoldFill = Steel.Gold, Alert = UiKit.LightRed;

    /// <summary>One board painter for the whole mode (its art and styles are cached per scale).</summary>
    public static readonly BoardStyle Board = new();

    /// <summary>Puts the concept lettering, the level <paramref name="size"/> and the ink on a label; <paramref name="outline"/> adds the dark outline of a readout that lies over the field.</summary>
    public static T Skin<T>(T l, int size, Color? ink = null, bool outline = false) where T : Label
    {
        l.AddThemeFontOverride("font", UiKit.ConceptFont());
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", ink ?? Ink);
        if (outline)
        {
            l.AddThemeConstantOverride("outline_size", 7);
            l.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.92f));
        }
        return l;
    }

    /// <summary>A concept plate button in design pixels: dark at rest, gold under the pointer and the focus (dark ink on gold), one size; the text steps down a level (never an ellipsis) when it does not fit <paramref name="width"/>.</summary>
    public static T Plate<T>(T b, int size, float width) where T : Button
    {
        float cpx = b.Size.Y > 0f ? b.Size.Y / 26f : 1.6f;   // the plate art is 26 concept px high: the text inset (4 x 2 concept px past the bevel, R1) scales with the plate's height
        b.TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps;
        b.Alignment = HorizontalAlignment.Center;
        b.AddThemeFontOverride("font", UiKit.ConceptFont());
        b.AddThemeConstantOverride("outline_size", 0);
        if (PaintedButtons.Apply(b, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(3, -0.5f, 3, -0.5f), new PaintedButtons.Pad(3, -0.5f, 3, 0f), 1f, true, paintedText: false, noGlow: true, cpx: cpx))
        {
            if (b.GetThemeStylebox("pressed") is { } on) { b.AddThemeStyleboxOverride("hover", on); b.AddThemeStyleboxOverride("hover_pressed", on); }
            foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" })   // a short plate keeps its text inset at the sides (R1); the height is the plate's own
                if (b.GetThemeStylebox(state) is { } box) { box.ContentMarginTop = 1f; box.ContentMarginBottom = 1f; }
            var dark = new Color(0.10f, 0.06f, 0.02f);
            b.AddThemeColorOverride("font_color", Ink);
            foreach (var c in new[] { "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color" }) b.AddThemeColorOverride(c, dark);
            b.AddThemeColorOverride("font_disabled_color", Steel.TextOff);
        }
        b.AddThemeFontSizeOverride("font_size", size);
        PaintedButtons.FitButton(b, size, width);
        return b;
    }

    /// <summary>A table's panel: the recessed steel lip and the dark body (the scoreboard's).</summary>
    public static void Table(Control c, Rect2 r) => Board.Table(c, r, 1f);

    /// <summary>A card: the kit's recessed steel panel, with the gold rim when <paramref name="lit"/> (the Armoury's selected item).</summary>
    public static void Card(Control c, Rect2 r, bool lit) => Board.Box(c, lit ? Steel.Kind.CardGold : Steel.Kind.Recess, r, 8f, 1f);

    /// <summary>A bar: a recessed track and a gold fill on whole pixels.</summary>
    public static void Bar(Control c, Rect2 r, float fraction, Color? fill = null)
    {
        Board.Box(c, Steel.Kind.Recess, r, 4f, 1f);
        float inner = r.Size.X - 6f;
        float w = Mathf.Floor(inner * Mathf.Clamp(fraction, 0f, 1f));
        if (w > 0f) c.DrawRect(new Rect2(r.Position.X + 3f, r.Position.Y + 3f, w, r.Size.Y - 6f), fill ?? GoldFill);
    }

    /// <summary>A dark strip behind a readout that lies over the field: the dark steel strip of a table's header.</summary>
    public static void Strip(Control c, Rect2 r) => Board.HeaderStrip(c, r, 1f);
}
