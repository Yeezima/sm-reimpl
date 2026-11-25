using Godot;
using SmClient.Customizer;

namespace SmClient.Extras.Warfront.Chaos.Finale;

/// <summary>
/// The duel HUD (Chaos Warfront C5c; ours): for everyone during the finale's relay, a dark steel strip across the top of the 1920 by 1080 space (the spectator HUD's strip) with <c>CHAMPION: NAME</c> (heading level,
/// gold), <c>NEXT: NAME</c> or <c>LAST CHALLENGER</c> and the queue count (body level), the Lord's bar labelled <c>CHAOS LORD</c> with its percent (body level; the kit's recessed bar, the alert red fill) from the
/// wire's permille, which the server carries across every handoff; under the strip <c>SPECTATING NAME</c> for everyone but the duelist (body level); and one centre line at the banner level (the duelist's entry
/// line, the handoff's countdown, the outcome). Live labels only, in the concept's lettering (<see cref="WarfrontStyle"/>): one style per level (owner 2026-11-15). Fed by <see cref="ChaosDuelModel.Lines"/>.
/// </summary>
public sealed partial class DuelHud : Control
{
    public const float StripTop = 70f, StripTopUnderBattle = 190f, StripHeight = 152f, BarWidth = 900f, BarHeight = 20f, CentreTop = 380f;

    readonly Label _champion, _next, _lordLabel, _percent, _spectating, _centre;
    readonly StripDraw _strip;
    float _top = -1f, _fraction;
    bool _bar;

    /// <summary>The strip and the Lord's bar (painted pieces of the kit; the text is all live labels).</summary>
    sealed partial class StripDraw : Control
    {
        public DuelHud? Hud;
        public StripDraw() { Name = "ChaosDuelStrip"; MouseFilter = MouseFilterEnum.Ignore; }
        public override void _Draw()
        {
            WarfrontStyle.Strip(this, new Rect2(Vector2.Zero, Size));
            if (Hud is not { _bar: true } h) return;
            float x = Mathf.Round((Size.X - BarWidth) / 2f);
            WarfrontStyle.Bar(this, new Rect2(x, 116f, BarWidth, BarHeight), h._fraction, WarfrontStyle.Alert);
        }
    }

    public Label ChampionLabel => _champion;
    public Label NextLabel => _next;
    public Label LordLabel => _lordLabel;
    public Label PercentLabel => _percent;
    public Label SpectatingLabel => _spectating;
    public Label CentreLabel => _centre;
    /// <summary>The strip's top in the design space (for the stills).</summary>
    public float Top => _top;

    public DuelHud()
    {
        Name = "ChaosDuelHud";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _strip = new StripDraw { Hud = this, Size = new Vector2(UiKit.VirtualWidth, StripHeight) };
        AddChild(_strip);
        _champion = Line("ChaosDuelChampion", WarfrontStyle.Heading, WarfrontStyle.Gold, HorizontalAlignment.Center, 0f, UiKit.VirtualWidth);
        _next = Line("ChaosDuelNext", WarfrontStyle.Body, null, HorizontalAlignment.Center, 0f, UiKit.VirtualWidth);
        float barX = Mathf.Round((UiKit.VirtualWidth - BarWidth) / 2f);
        _lordLabel = Line("ChaosDuelLordLabel", WarfrontStyle.Body, null, HorizontalAlignment.Left, barX, BarWidth / 2f);
        _percent = Line("ChaosDuelLordPercent", WarfrontStyle.Body, null, HorizontalAlignment.Right, barX + BarWidth / 2f, BarWidth / 2f);
        _spectating = Line("ChaosDuelSpectating", WarfrontStyle.Body, null, HorizontalAlignment.Center, 0f, UiKit.VirtualWidth);
        _centre = Line("ChaosDuelCentre", WarfrontStyle.Banner, WarfrontStyle.Gold, HorizontalAlignment.Center, 0f, UiKit.VirtualWidth);
        _centre.Position = new Vector2(0f, CentreTop);
        Visible = false;
    }

    Label Line(string name, int size, Color? ink, HorizontalAlignment align, float x, float width)
    {
        var l = UiKit.MenuText("", null, clip: false);
        l.Name = name;
        WarfrontStyle.Skin(l, size, ink, outline: true);
        l.HorizontalAlignment = align;
        l.Position = new Vector2(x, 0f);
        l.Size = new Vector2(width, size + 12f);
        AddChild(l);
        return l;
    }

    /// <summary>This frame's lines; <paramref name="underBattle"/>: the Battle HUD's line and clock are on screen (the strip goes under them, as the spectator HUD's does).</summary>
    public void Show(in ChaosDuelLines lines, bool underBattle)
    {
        Visible = lines.Shown;
        if (!lines.Shown) return;
        float top = underBattle ? StripTopUnderBattle : StripTop;
        if (top != _top)
        {
            _top = top;
            _strip.Position = new Vector2(0f, top);
            _champion.Position = new Vector2(0f, top + 8f);
            _next.Position = new Vector2(0f, top + 50f);
            _lordLabel.Position = new Vector2(_lordLabel.Position.X, top + 84f);
            _percent.Position = new Vector2(_percent.Position.X, top + 84f);
            _spectating.Position = new Vector2(0f, top + StripHeight + 12f);
        }
        Set(_champion, lines.Champion);
        Set(_next, lines.Queue.Length == 0 ? lines.Next : lines.Next.Length == 0 ? lines.Queue : lines.Next + "      " + lines.Queue);
        Set(_lordLabel, lines.Bar ? ChaosDuelLook.LordLabel : "");
        Set(_percent, lines.Bar ? lines.BarPercent : "");
        Set(_spectating, lines.Spectating);
        Set(_centre, lines.Centre);
        _centre.AddThemeColorOverride("font_color", lines.CentreWarn ? WarfrontStyle.Alert : WarfrontStyle.Gold);
        bool strip = lines.Champion.Length > 0 || lines.Next.Length > 0 || lines.Bar;
        _strip.Visible = strip;
        if (lines.Bar != _bar || lines.BarFraction != _fraction) { _bar = lines.Bar; _fraction = lines.BarFraction; _strip.QueueRedraw(); }
    }

    static void Set(Label l, string text)
    {
        if (l.Text != text) l.Text = text;
        l.Visible = text.Length > 0;
    }
}
