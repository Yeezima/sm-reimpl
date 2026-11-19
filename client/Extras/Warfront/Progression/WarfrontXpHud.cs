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
/// Warfront's in-play progression cue: only the one-line LEVEL N banner for two seconds after a level rises (or the picker-off countdown line). The level, XP bar, counters and "+N" picks no longer sit on the
/// field: they live in the Tab scoreboard (<see cref="WarfrontXpBoard"/>; owner, 2026-11-17: the always-on box was too invasive while playing). A waiting pick needs no standing cue: the card picker opens by itself
/// for every pick (and the banner names the countdown when the picker is switched off), so nothing else is drawn in play.
/// </summary>
public sealed partial class WarfrontXpHud : Control
{
    public const int BannerSize = 44;

    readonly WarfrontProgressModel _model;
    readonly Label _banner;

    public Label BannerLabel => _banner;
    public WarfrontProgressModel Model => _model;

    public System.Collections.Generic.IEnumerable<Label> Labels()
    {
        foreach (var n in GetChildren()) if (n is Label l) yield return l;
    }

    public WarfrontXpHud(WarfrontProgressModel model)
    {
        Name = "WarfrontXpHud";
        _model = model;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _banner = WarfrontProgressDraw.Text(this, "WarfrontBanner", (UiKit.VirtualWidth - 800f) / 2f, 180f, 800f, BannerSize, WarfrontProgressDraw.Gold, HorizontalAlignment.Center);
        _banner.Visible = false;
        model.Changed += Refresh;
        TreeExited += () => model.Changed -= Refresh;
        Refresh();
    }

    public void Refresh()
    {
        var m = _model;
        bool banner = m.BannerShowing;
        if (_banner.Visible != banner) _banner.Visible = banner;
        if (banner) WarfrontProgressDraw.Set(_banner, m.BannerText);
    }
}
