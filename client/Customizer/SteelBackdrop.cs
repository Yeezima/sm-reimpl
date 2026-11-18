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
    public static readonly Rect2 Board = new(40f, 8f, 1840f, 1064f);
    readonly Label? _title;
    readonly BoardStyle _style = new();
    readonly Steel.SteelStyle _ring = new(Steel.Kind.PaintedRecess, 10f, 0f, 0f) { Scale = 0.5f };
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

    /// <summary>When set, the board is cut to the page's content (the visible siblings' extent plus a margin) instead of filling the screen, so a short page is a compact steel board, not a mostly empty slab.</summary>
    public bool FitContent { get; set; }
    Rect2 _fitted;
    Vector2 _applied;   // how far the page's content has been moved to the middle of the window

    Rect2 Fitted()
    {
        if (!FitContent || GetParent() is not Control parent) return _board;
        float right = 0f, bottom = 0f;
        foreach (var c in parent.GetChildren())
            if (c is Control k && k != this && k.Visible && k.Size.X > 0f && k.Size.Y > 0f)
            {
                float kw = k is Label lab && lab.Text.Length > 0 ? Mathf.Min(k.Size.X, lab.GetThemeFont("font").GetStringSize(lab.Text, HorizontalAlignment.Left, -1, lab.GetThemeFontSize("font_size")).X + 8f) : k.Size.X;
                right = Mathf.Max(right, k.Position.X - _applied.X + kw); bottom = Mathf.Max(bottom, k.Position.Y - _applied.Y + k.Size.Y);
            }
        float w = Mathf.Clamp(right + 54f - _board.Position.X, 700f, _board.Size.X), h = Mathf.Clamp(bottom + 46f - _board.Position.Y, 320f, _board.Size.Y);
        return new Rect2(_board.Position, new Vector2(w, h));
    }

    public override void _Process(double delta)
    {
        if (!FitContent || GetParent() is not Control parent) return;
        var f = Fitted();
        // The fitted board and its content stand in the middle of the window (a short page is a compact board, not a left-hand slab).
        var want = new Vector2(Mathf.Max(0f, (Size.X - f.Size.X) / 2f - f.Position.X), Mathf.Max(0f, (Size.Y - f.Size.Y) / 2f - f.Position.Y));
        var move = want - _applied;
        if (move.LengthSquared() > 0.25f)
        {
            foreach (var c in parent.GetChildren()) if (c is Control k && k != this) k.Position += move;
            _applied = want;
        }
        if (f != _fitted) { _fitted = f; QueueRedraw(); }
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0f, 0f, 0f, _veil));

        var o = Vector2.Zero;   // the pages lay out from the top left of the UI space, so the board does too
        var b0 = FitContent ? Fitted() : _board;
        var board = new Rect2(b0.Position + o + (FitContent ? _applied : Vector2.Zero), b0.Size);
        _ring.Draw(GetCanvasItem(), board);   // the painted steel ring round a dark floor (Opus review 3)
        if (_title is null || !_title.Visible) return;
        var font = UiKit.ConceptFont();
        float w = font.GetStringSize(_title.Text, HorizontalAlignment.Left, -1, _title.GetThemeFontSize("font_size")).X;
        var plate = new Rect2(_title.Position.X + o.X - 40f, _title.Position.Y - 10f, w + 80f, _title.GetThemeFontSize("font_size") + 44f);
        _style.Plate(this, plate, 1f);
    }

    /// <summary>A page title in the plate's lettering: Oswald, light steel, at <paramref name="size"/> UI pixels.</summary>
    public static void StyleTitle(Label head, int size = UiType.Title)
    {
        head.AddThemeFontOverride("font", UiKit.ConceptFont());
        head.AddThemeFontSizeOverride("font_size", UiType.Px(UiType.Level(size)));
        EmbossTitle(head);
    }

    /// <summary>The treatment of the painted OPTIONS plate on live titles: light steel letters, a dark outline and a shadow under them.</summary>
    public static void EmbossTitle(Label l)
    {
        l.AddThemeColorOverride("font_color", new Color(0.88f, 0.87f, 0.83f));
        l.AddThemeColorOverride("font_outline_color", new Color(0.09f, 0.085f, 0.08f));
        l.AddThemeConstantOverride("outline_size", 4);
        l.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.65f));
        l.AddThemeConstantOverride("shadow_offset_x", 0); l.AddThemeConstantOverride("shadow_offset_y", 3);
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
        PaintedButtons.Apply(b, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(3, -0.5f, 3, -0.5f), new PaintedButtons.Pad(3, -0.5f, 3, 0f), 2.4f, true, paintedText: false, noGlow: true);
        var light = new Color(0.84f, 0.83f, 0.80f); var dark = new Color(0.10f, 0.06f, 0.02f);
        if (b.GetThemeStylebox("pressed") is { } gold) { b.AddThemeStyleboxOverride("hover", gold); b.AddThemeStyleboxOverride("hover_pressed", gold); }   // hover is the gold plate too, with dark ink
        b.AddThemeColorOverride("font_color", light);
        foreach (var c in new[] { "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color" }) b.AddThemeColorOverride(c, dark);
        b.AddThemeColorOverride("font_disabled_color", Steel.TextOff);
    }
}
