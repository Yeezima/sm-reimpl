using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The backdrop of a menu page that lays its own content out by hand in the 1920 x 1080 UI space (the Musou Mode score, reward, run and war-map pages, the range confirmation, and the Warfront hub, travel, join and results pages):
/// a veil over the scene, a recessed steel board (the scoreboards' panel) round the content and the concept's rivetted plate under the page's title. The page keeps its own labels and rows; <see cref="Style"/> gives them the
/// redesigned lettering and plates. Ours (docs/ui-overhaul.md).
/// </summary>
public sealed partial class SteelBackdrop : Control
{
    /// <summary>The board's rectangle in the UI space and the title plate's margins.</summary>
    public static readonly Rect2 Board = new(64f, 24f, 1792f, 1032f);
    readonly Label? _title;
    readonly BoardStyle _style = new();
    readonly float _veil;
    readonly Rect2 _board;

    /// <param name="title">The page's title label (drawn by the page); the plate is laid under it.</param>
    public SteelBackdrop(string name, Label? title, float veil = 0.66f, Rect2? board = null)
    {
        _board = board ?? Board;
        Name = name;
        _title = title; _veil = veil;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0f, 0f, 0f, _veil));
        
        var o = Vector2.Zero;   // the pages lay out from the top left of the UI space, so the board does too
        var board = new Rect2(_board.Position + o, _board.Size);
        _style.Table(this, board, 1f);
        if (_title is null || !_title.Visible) return;
        var font = UiKit.ConceptFont();
        float w = font.GetStringSize(_title.Text, HorizontalAlignment.Left, -1, _title.GetThemeFontSize("font_size")).X;
        var plate = new Rect2(_title.Position.X + o.X - 40f, _title.Position.Y - 10f, w + 80f, _title.GetThemeFontSize("font_size") + 44f);
        _style.Plate(this, plate, 1f);
    }

    /// <summary>A page title in the plate's lettering: Oswald, light steel, at <paramref name="size"/> UI pixels.</summary>
    public static void StyleTitle(Label head, int size = 56)
    {
        head.AddThemeFontOverride("font", UiKit.ConceptFont());
        head.AddThemeFontSizeOverride("font_size", size);
        head.AddThemeColorOverride("font_color", new Color(0.86f, 0.85f, 0.81f));
    }

    /// <summary>A line of text of the page in the redesigned lettering (the size and colour stay the page's own).</summary>
    public static void StyleLine(Label l) => l.AddThemeFontOverride("font", UiKit.ConceptFont());

    /// <summary>A row of the page as the concept's plate: dark, gold under the focus, the page's size of lettering, centred.</summary>
    public static void StylePlate(Button b)
    {
        b.Alignment = HorizontalAlignment.Center;
        b.AddThemeFontOverride("font", UiKit.ConceptFont());
        b.AddThemeConstantOverride("outline_size", 0);
        b.SetMeta(FocusGlow.NoGlowMeta, true);
        PaintedButtons.Apply(b, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(3, 1, 3, 1), new PaintedButtons.Pad(3, 1, 3, 2), 2.4f, true, paintedText: false, noGlow: true);
        var light = new Color(0.84f, 0.83f, 0.80f); var dark = new Color(0.10f, 0.06f, 0.02f);
        foreach (var c in new[] { "font_color", "font_hover_color" }) b.AddThemeColorOverride(c, light);
        foreach (var c in new[] { "font_focus_color", "font_pressed_color", "font_hover_pressed_color" }) b.AddThemeColorOverride(c, dark);
        b.AddThemeColorOverride("font_disabled_color", Steel.TextOff);
    }
}
