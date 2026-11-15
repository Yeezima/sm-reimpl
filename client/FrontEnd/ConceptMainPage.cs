using Godot;
using SmClient.Customizer;

namespace SmClient.FrontEnd;

/// <summary>
/// The main menu in the concept's layout (ui/concept-redesign, panel 01): a stone pillar down the left edge, the large SM-REIMPL wordmark, the winged crest, and under them a heavy stepped
/// steel frame with an amber-lit recessed panel holding the dark-metal buttons, a nameplate on the frame's lower bar. The pieces are painted ones cut from the concept (<see cref="ConceptArt"/>;
/// the code-drawn look is the fallback). The battlefield and the Marine of the background video stay free on the right. The rows are the page's <see cref="UiKit.MenuPage.Rows"/> as before (the same
/// buttons and handlers); only their look and place changed. Ours (spec "UI concept redesign").
/// </summary>
public sealed partial class ConceptMainPage : UiKit.MenuPage
{
    /// <summary>The nameplate's words (the concept's, ours).</summary>
    public const string Tagline = "One soldier. Many battlefields.";

    /// <summary>The wordmark's width, the frame's width, the stone pillar's width, the frame ring's thickness and the padding of the recess, in the 1920 x 1080 UI space.</summary>
    public const float WordmarkWidth = 600f, FrameWidth = 594f, PillarWidth = 30f, RecessPad = 14f, FrameLeft = 26f;
    /// <summary>The button rows: their height and the gap between them.</summary>
    public const float RowHeight = 62f, RowGap = 12f;

    readonly UiKit.StonePillar _pillar = new(PillarWidth, 7, ConceptArt.Get("wall-main") is not null ? "wall-main" : "stone-main");
    readonly UiKit.Wordmark _wordmark = new(UiKit.MenuTitle.Replace(" ", ""), WordmarkWidth);
    readonly UiKit.WingedCrest _crest = new(300f, 60f);
    readonly PanelContainer _frame = new() { Name = "MenuFrame", MouseFilter = MouseFilterEnum.Ignore };
    readonly Label _nameplate = UiKit.Nameplate(Tagline);
    readonly TextureRect _cap = new() { Name = "WallCap", MouseFilter = MouseFilterEnum.Ignore, StretchMode = TextureRect.StretchModeEnum.Keep };

    public Control Frame => _frame;
    public Control Wordmark => _wordmark;

    public ConceptMainPage() : base("MainMenu")
    {
        _title.Visible = false;   // the wordmark is the title (the label stays, with the project's name, for the tests and the tree)
        _heading.Visible = false;
        _frame.AddThemeStyleboxOverride("panel", new Steel.SteelStyle(Steel.Kind.AmberFrame, 16f, 0f, 0f));
        _pillar.Name = "MenuPillar";
        if (ConceptArt.Get("wall-cap") is { } cap) { _cap.Texture = cap; AddChild(_cap); }
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
        bool wall = ConceptArt.Get("frame-wall") is not null;
        var ring = wall ? ConceptArt.WallRing : ConceptArt.MainRing;
        float rows = Rows.GetCombinedMinimumSize().Y;
        float frameH = rows + ring.Y + ring.W + RecessPad * 2f;
        float wordH = _wordmark.CustomMinimumSize.Y, crestH = _crest.CustomMinimumSize.Y;
        float stack = wordH + crestH - 10f + frameH + 20f;
        float top = Mathf.Max(8f, (h - stack) / 2f + h * 0.01f);
        float cx = FrameLeft + FrameWidth / 2f;
        _pillar.Position = Vector2.Zero; _pillar.Size = new Vector2(PillarWidth, Mathf.Max(h, GetViewportRect().Size.Y));
        _cap.Position = Vector2.Zero;
        _wordmark.Position = new Vector2(cx - WordmarkWidth / 2f, top); _wordmark.Size = new Vector2(WordmarkWidth, wordH);
        float crestTop = top + wordH - 8f;
        _crest.Position = new Vector2(cx - _crest.CustomMinimumSize.X / 2f, crestTop); _crest.Size = _crest.CustomMinimumSize;
        float frameTop = crestTop + crestH - 14f;
        _frame.Position = new Vector2(FrameLeft, frameTop); _frame.Size = new Vector2(FrameWidth, frameH);
        _scroll.Position = new Vector2(FrameLeft + ring.X + RecessPad, frameTop + ring.Y + RecessPad);
        _scroll.Size = new Vector2(FrameWidth - ring.X - ring.Z - RecessPad * 2f, rows);
        _nameplate.Visible = !wall;   // the wall frame has the nameplate inset in its bottom bar (painted, fixed text)
        var plate = _nameplate.GetThemeStylebox("normal") is StyleBoxTexture ? new Vector2(348f, 42f) : new Vector2(FrameWidth - 120f, 34f);
        _nameplate.Size = plate; _nameplate.Position = new Vector2(cx - plate.X / 2f, frameTop + frameH - plate.Y + 8f);
        if (_foot is { } foot) { foot.Position = new Vector2(FrameLeft, frameTop + frameH + 24f); foot.Size = new Vector2(FrameWidth, foot.GetCombinedMinimumSize().Y); }
    }
}
