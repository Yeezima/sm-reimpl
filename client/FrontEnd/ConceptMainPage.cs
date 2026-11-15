using Godot;
using SmClient.Customizer;

namespace SmClient.FrontEnd;

/// <summary>
/// The main menu in the concept's layout (ui/concept-redesign, panel 01): a stone pillar down the left edge, the large SM-REIMPL wordmark, the winged crest, and under them a heavy stepped
/// steel frame with an amber-lit recessed panel holding the dark-metal buttons, a nameplate on the frame's lower bar. The battlefield and the Marine of the background video stay free on the right.
/// The rows are the page's <see cref="UiKit.MenuPage.Rows"/> as before (the same buttons, order and handlers); only their look and place changed. Ours (spec "UI concept redesign").
/// </summary>
public sealed partial class ConceptMainPage : UiKit.MenuPage
{
    /// <summary>The nameplate's words (the concept's, ours).</summary>
    public const string Tagline = "One soldier. Many battlefields.";

    /// <summary>The wordmark's width, the frame's width, the stone pillar's width and the frame's inner margins, in the 1920 x 1080 UI space.</summary>
    public const float WordmarkWidth = 640f, FrameWidth = 540f, PillarWidth = 120f, FrameMargin = 46f, FrameBottom = 80f, LeftEdge = 70f, RecessPad = 28f;
    /// <summary>The button rows: their height and the gap between them.</summary>
    public const float RowHeight = 62f, RowGap = 12f;

    readonly UiKit.StonePillar _pillar = new(PillarWidth);
    readonly UiKit.Wordmark _wordmark = new(UiKit.MenuTitle.Replace(" ", ""), WordmarkWidth);
    readonly UiKit.WingedCrest _crest = new(WordmarkWidth * 0.8f, 92f);
    readonly PanelContainer _frame = new() { Name = "MenuFrame", MouseFilter = MouseFilterEnum.Ignore };
    readonly Label _nameplate = UiKit.Nameplate(Tagline);

    public Control Frame => _frame;
    public Control Wordmark => _wordmark;

    public ConceptMainPage() : base("MainMenu")
    {
        _title.Visible = false;   // the wordmark is the title (the label stays, with the project's name, for the tests and the tree)
        _heading.Visible = false;
        var style = new Steel.SteelStyle(Steel.Kind.AmberFrame, 16f, FrameMargin, FrameMargin);
        style.SetContentMargin(Side.Bottom, FrameBottom);
        _frame.AddThemeStyleboxOverride("panel", style);
        _pillar.Name = "MenuPillar";
        AddChild(_pillar); AddChild(_wordmark); AddChild(_crest); AddChild(_frame);
        AddChild(_nameplate);
        MoveChild(_scroll, -1);   // the rows stand on the frame
        Rows.RowHeight = RowHeight;
        Rows.RowGap = RowGap;
        _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever;   // the page always has room (the window is never under 1080 UI pixels high); a taller row set only makes the frame taller
        Place();
    }

    protected override void Place()
    {
        if (_frame is null || _nameplate is null) return;   // the base class's constructor asks before these exist
        float h = Size.Y;
        float rows = Rows.GetCombinedMinimumSize().Y;
        float frameH = rows + FrameMargin + FrameBottom + RecessPad * 2f;
        float wordH = _wordmark.CustomMinimumSize.Y, crestH = _crest.CustomMinimumSize.Y;
        float stack = wordH + crestH - 8f + frameH;
        float top = Mathf.Max(16f, (h - stack) / 2f + h * 0.02f);
        float cx = PillarWidth + 24f + WordmarkWidth / 2f - 30f;
        _pillar.Position = Vector2.Zero; _pillar.Size = new Vector2(PillarWidth, h);
        _wordmark.Position = new Vector2(cx - WordmarkWidth / 2f, top); _wordmark.Size = new Vector2(WordmarkWidth, wordH);
        _crest.Position = new Vector2(cx - _crest.CustomMinimumSize.X / 2f, top + wordH - 8f); _crest.Size = _crest.CustomMinimumSize;
        float frameTop = top + wordH + crestH - 8f;
        _frame.Position = new Vector2(cx - FrameWidth / 2f, frameTop); _frame.Size = new Vector2(FrameWidth, frameH);
        _scroll.Position = new Vector2(cx - FrameWidth / 2f + FrameMargin + RecessPad, frameTop + FrameMargin + RecessPad);
        _scroll.Size = new Vector2(FrameWidth - (FrameMargin + RecessPad) * 2f, rows);
        _nameplate.Position = new Vector2(cx - FrameWidth / 2f + FrameMargin + 6f, frameTop + frameH - FrameBottom + 22f);
        _nameplate.Size = new Vector2(FrameWidth - FrameMargin * 2f - 12f, 34f);
        if (_foot is { } foot) { foot.Position = new Vector2(cx - FrameWidth / 2f, frameTop + frameH + 12f); foot.Size = new Vector2(FrameWidth, foot.GetCombinedMinimumSize().Y); }
    }
}
