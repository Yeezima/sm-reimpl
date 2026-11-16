using Godot;
using SmClient.Customizer;

namespace SmClient.Extras.Warfront.Progression;

/// <summary>Shapes shared by the XP HUD and the picker: boxes and bars are drawn, every label is a live label (owner's UI rule).</summary>
public static class WarfrontProgressDraw
{
    public static readonly Color Gold = new(0.98f, 0.8f, 0.12f);

    /// <summary>A frame of equal width on all four sides, on whole pixels: four rectangles, never an outline stroke.</summary>
    public static void Frame(CanvasItem c, Rect2 r, float w, Color colour)
    {
        c.DrawRect(new Rect2(r.Position.X, r.Position.Y, r.Size.X, w), colour);
        c.DrawRect(new Rect2(r.Position.X, r.End.Y - w, r.Size.X, w), colour);
        c.DrawRect(new Rect2(r.Position.X, r.Position.Y + w, w, r.Size.Y - 2f * w), colour);
        c.DrawRect(new Rect2(r.End.X - w, r.Position.Y + w, w, r.Size.Y - 2f * w), colour);
    }

    /// <summary>A bar: a 2 px frame and a fill inside it, both on whole pixels.</summary>
    public static void Bar(CanvasItem c, Rect2 r, float fraction, Color fill)
    {
        Frame(c, r, 2f, new Color(1f, 1f, 1f, 0.8f));
        float inner = r.Size.X - 4f;
        float w = Mathf.Floor(inner * Mathf.Clamp(fraction, 0f, 1f));
        if (w > 0f) c.DrawRect(new Rect2(r.Position.X + 2f, r.Position.Y + 2f, w, r.Size.Y - 4f), fill);
    }

    public static Label Text(Control parent, string name, float x, float y, float w, int size, Color? colour = null, HorizontalAlignment align = HorizontalAlignment.Left, int lines = 1)
    {
        var l = UiKit.MenuText("", colour, clip: false);
        l.Name = name;
        l.HorizontalAlignment = align;
        l.AddThemeFontSizeOverride("font_size", size);
        l.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        l.Position = new Vector2(x, y);
        l.Size = new Vector2(w, lines * (size + 8f));
        parent.AddChild(l);
        return l;
    }

    public static void Set(Label l, string text) { if (l.Text != text) l.Text = text; }
}

/// <summary>
/// Warfront's XP HUD (slice W8; plan section 5, brief "Behaviour" 1 and 2): the level, a thin XP bar to the next level, "+N" while picks are pending, one row of five small counters (attack speed, max HP,
/// damage, cooldown, max armour) and the retainer count, and the one-line LEVEL N banner for two seconds after a level rises. In the 1920 by 1080 space like the other Warfront panels, on the right edge at
/// mid height: clear of Musou's corners (capture tiles top left, boss and vitals bottom left, kill feed top right, KO chain and weapon box bottom right) and its top call-out and bottom prompt strips. Layout ours,
/// to check against the live Musou HUD. Two text levels (heading 24, body 16) plus the banner's own; fed by a <see cref="WarfrontProgressModel"/>, hidden until Progress arrives.
/// </summary>
public sealed partial class WarfrontXpHud : Control
{
    public const int HeadingSize = 24, BodySize = 16, BannerSize = 40;
    public const float Width = 372f, Height = 164f, Right = 44f, Top = 420f, Pad = 15f, BarHeight = 12f;
    /// <summary>Whole pixels: six cells of 52 at a step of 58 span 342, the box less its two 15 px margins.</summary>
    public const float CellWidth = 52f, CellStep = 58f;
    public static float Left => UiKit.VirtualWidth - Right - Width;

    readonly WarfrontProgressModel _model;
    readonly Label _level, _pending, _xp, _banner;
    readonly Label[] _cells = new Label[6];
    readonly Draw _draw;

    public Label LevelLabel => _level;
    public Label PendingLabel => _pending;
    public Label XpLabel => _xp;
    public Label BannerLabel => _banner;
    public Label Cell(int i) => _cells[i];
    public WarfrontProgressModel Model => _model;

    public System.Collections.Generic.IEnumerable<Label> Labels()
    {
        foreach (var n in GetChildren()) if (n is Label l) yield return l;
    }

    sealed partial class Draw : Control
    {
        readonly WarfrontProgressModel _m;
        public Draw(WarfrontProgressModel m) { _m = m; Name = "WarfrontXpDraw"; MouseFilter = MouseFilterEnum.Ignore; }

        public override void _Draw()
        {
            if (!_m.HasProgress) return;
            var box = new Rect2(Left, Top, Width, Height);
            DrawRect(box, UiKit.PanelColour);
            WarfrontProgressDraw.Frame(this, box, 2f, new Color(1f, 1f, 1f, 0.8f));
            WarfrontProgressDraw.Bar(this, new Rect2(Left + Pad, Top + 50f, Width - 2f * Pad, BarHeight), _m.XpFraction, WarfrontProgressDraw.Gold);
            for (int i = 0; i < 6; i++)
            {
                var cell = new Rect2(Left + Pad + i * CellStep, Top + 96f, CellWidth, 56f);
                int count = i < 5 ? _m.CardCounts[i] : _m.Retainers;
                WarfrontProgressDraw.Frame(this, cell, 2f, count > 0 ? WarfrontProgressDraw.Gold : new Color(1f, 1f, 1f, 0.35f));
            }
        }
    }

    public WarfrontXpHud(WarfrontProgressModel model)
    {
        Name = "WarfrontXpHud";
        _model = model;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _draw = new Draw(model);
        _draw.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_draw);
        var white = UiKit.White;
        _level = WarfrontProgressDraw.Text(this, "WarfrontLevel", Left + Pad, Top + 8f, 220f, HeadingSize, white);
        _pending = WarfrontProgressDraw.Text(this, "WarfrontPending", Left + Width - Pad - 120f, Top + 8f, 120f, HeadingSize, WarfrontProgressDraw.Gold, HorizontalAlignment.Right);
        _xp = WarfrontProgressDraw.Text(this, "WarfrontXpText", Left + Pad, Top + 66f, Width - 2f * Pad, BodySize);
        for (int i = 0; i < 6; i++)
            _cells[i] = WarfrontProgressDraw.Text(this, "WarfrontCell" + i, Left + Pad + i * CellStep, Top + 98f, CellWidth, BodySize, white, HorizontalAlignment.Center, lines: 2);
        _banner = WarfrontProgressDraw.Text(this, "WarfrontBanner", (UiKit.VirtualWidth - 640f) / 2f, 190f, 640f, BannerSize, WarfrontProgressDraw.Gold, HorizontalAlignment.Center);
        _banner.Visible = false;
        model.Changed += Refresh;
        TreeExited += () => model.Changed -= Refresh;
        Refresh();
    }

    public void Refresh()
    {
        var m = _model;
        bool show = m.HasProgress && !m.OfferOpen;   // the picker shows the owned counts itself and covers this corner
        _draw.Visible = show;
        _level.Visible = _pending.Visible = _xp.Visible = show;
        foreach (var c in _cells) c.Visible = show;
        WarfrontProgressDraw.Set(_level, m.LevelText);
        WarfrontProgressDraw.Set(_pending, m.PendingText);
        WarfrontProgressDraw.Set(_xp, m.XpText);
        for (int i = 0; i < 6; i++) WarfrontProgressDraw.Set(_cells[i], m.CounterText(i));
        bool banner = m.BannerShowing;
        if (_banner.Visible != banner) _banner.Visible = banner;
        if (banner) WarfrontProgressDraw.Set(_banner, m.BannerText);
        _draw.QueueRedraw();
    }
}
