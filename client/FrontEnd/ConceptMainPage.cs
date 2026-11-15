using System.Linq;
using Godot;
using SmClient.Customizer;

namespace SmClient.FrontEnd;

/// <summary>
/// The main menu in the concept's layout (ui/concept-redesign, panel 01). Since 2026-11-15 (owner: "simply paste in the concept art overlaid on the left side of the screen, then fix the buttons to match
/// the ones needed for sm-reimpl menu operation") the whole left side is ONE overlay picture (<c>overlay-main.png</c>, cut from the concept and the owner's sharper crop of it by
/// <see cref="UiTextureBuilder"/>; its alpha follows the owner's orange line so the video shows outside it): the stone wall, the SM-REIMPL wordmark, the crest, the framed window with its amber recess
/// and the nameplate. The concept's own button plates are painted out of the recess; our live, focusable buttons (the page's <see cref="UiKit.MenuPage.Rows"/>: the same buttons and handlers as before)
/// stand on the plates' places, in the dark and gold plate textures, spaced to fill the recess (tighter when STOP MUSOU SERVER shows). Without the picture the older pieces (wall, frame, wordmark, crest) are
/// used. The overlay spans the window's height at the concept's proportion. Ours (spec "UI concept redesign").
/// </summary>
public sealed partial class ConceptMainPage : UiKit.MenuPage
{
    /// <summary>The nameplate's words (the concept's, ours).</summary>
    public const string Tagline = "One soldier. Many battlefields.";

    // The older pieces' layout (used when the overlay picture is missing).
    public const float WordmarkWidth = 620f, FrameWidth = 594f, PillarWidth = 112f, RecessPad = 30f, FrameLeft = 26f;
    public const float RowHeight = 62f, RowGap = 12f;

    readonly TextureRect _overlay = new() { Name = "MenuFrame", MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, TextureFilter = TextureFilterEnum.LinearWithMipmaps };
    readonly Control _wordmarkBox = new() { Name = "Wordmark", MouseFilter = MouseFilterEnum.Ignore };
    readonly Label _nameplate = UiKit.Nameplate(Tagline);
    // The older pieces.
    readonly UiKit.StonePillar _pillar = new(PillarWidth, 7, ConceptArt.Get("wall-main") is not null ? "wall-main" : "stone-main");
    readonly UiKit.Wordmark _wordmark = new(UiKit.MenuTitle.Replace(" ", ""), WordmarkWidth);
    readonly UiKit.WingedCrest _crest = new(330f, 66f);
    readonly PanelContainer _frame = new() { Name = "MenuFrame", MouseFilter = MouseFilterEnum.Ignore };
    readonly bool _useOverlay = ConceptArt.Get("overlay-main") is not null;

    /// <summary>The window of the page: the overlay (or the old frame).</summary>
    public Control Frame => _useOverlay ? _overlay : _frame;
    /// <summary>The wordmark's place (the overlay's lettering, or the old wordmark).</summary>
    public Control Wordmark => _useOverlay ? _wordmarkBox : _wordmark;

    public ConceptMainPage() : base("MainMenu")
    {
        _title.Visible = false;   // the wordmark is the title (the label stays, with the project's name, for the tests and the tree)
        _heading.Visible = false;
        _nameplate.Name = "Nameplate";
        if (_useOverlay)
        {
            _overlay.Texture = ConceptArt.Get("overlay-main");
            AddChild(_overlay); AddChild(_wordmarkBox);
            _nameplate.Modulate = Colors.Transparent;   // the words are in the picture; the label stays for the tree
            AddChild(_nameplate);
        }
        else
        {
            _frame.AddThemeStyleboxOverride("panel", new Steel.SteelStyle(Steel.Kind.AmberFrame, 16f, 0f, 0f));
            _pillar.Name = "MenuPillar";
            AddChild(_pillar); AddChild(_wordmark); AddChild(_crest); AddChild(_frame);
            AddChild(_nameplate);
        }
        MoveChild(_scroll, -1);   // the rows stand on the frame
        _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever;   // the page always has room (the window is never under 1080 UI pixels high)
        Place();
    }

    int VisibleRows() => Rows.GetChildren().OfType<Control>().Count(c => c.Visible);

    protected override void Place()
    {
        if (_frame is null || _nameplate is null) return;   // the base class's constructor asks before these exist
        float h = Size.Y;
        if (_useOverlay) { PlaceOverlay(h); return; }
        Rows.RowHeight = RowHeight; Rows.RowGap = RowGap;
        bool wall = ConceptArt.Get("frame-wall") is not null;
        var ring = wall ? ConceptArt.WallRing : ConceptArt.MainRing;
        float rows = Rows.GetCombinedMinimumSize().Y;
        float frameH = rows + ring.Y + ring.W + RecessPad * 2f;
        float wordH = _wordmark.CustomMinimumSize.Y, crestH = _crest.CustomMinimumSize.Y;
        float stack = wordH + crestH - 10f + frameH + 20f;
        float top = Mathf.Max(8f, (h - stack) / 2f + h * 0.01f);
        float cx = FrameLeft + FrameWidth / 2f;
        _pillar.Position = Vector2.Zero; _pillar.Size = new Vector2(PillarWidth, Mathf.Max(h, GetViewportRect().Size.Y));
        _wordmark.Position = new Vector2(cx - WordmarkWidth / 2f, top); _wordmark.Size = new Vector2(WordmarkWidth, wordH);
        float crestTop = top + wordH - 8f;
        _crest.Position = new Vector2(cx - _crest.CustomMinimumSize.X / 2f, crestTop); _crest.Size = _crest.CustomMinimumSize;
        float frameTop = crestTop + crestH - 14f;
        _frame.Position = new Vector2(FrameLeft, frameTop); _frame.Size = new Vector2(FrameWidth, frameH);
        _scroll.Position = new Vector2(FrameLeft + ring.X + RecessPad, frameTop + ring.Y + RecessPad);
        _scroll.Size = new Vector2(FrameWidth - ring.X - ring.Z - RecessPad * 2f, rows);
        _nameplate.Visible = !wall;
        var plate = new Vector2(FrameWidth - 120f, 34f);
        _nameplate.Size = plate; _nameplate.Position = new Vector2(cx - plate.X / 2f, frameTop + frameH - plate.Y + 8f);
        if (_foot is { } foot) { foot.Position = new Vector2(FrameLeft, frameTop + frameH + 24f); foot.Size = new Vector2(FrameWidth, foot.GetCombinedMinimumSize().Y); }
    }

    /// <summary>The overlay at the window's height; the rows spread over the recess where the plates were (pitch 43 crop pixels, tighter for more rows).</summary>
    void PlaceOverlay(float h)
    {
        // Full height at 16:9; on a narrower window no wider than half of it (the video's battlefield must stay visible).
        float s = System.Math.Min(h / UiTextureBuilder.CropH, 0.5f * Size.X / UiTextureBuilder.CropW);
        _overlay.Position = Vector2.Zero; _overlay.Size = new Vector2(UiTextureBuilder.CropW * s, UiTextureBuilder.CropH * s);
        var wm = UiTextureBuilder.OverlayWordmark; var np = UiTextureBuilder.OverlayNameplate; var area = UiTextureBuilder.OverlayRows;
        _wordmarkBox.Position = new Vector2(wm.Position.X * s, wm.Position.Y * s); _wordmarkBox.Size = new Vector2(wm.Size.X * s, wm.Size.Y * s);
        _nameplate.Position = new Vector2(np.Position.X * s, np.Position.Y * s); _nameplate.Size = new Vector2(np.Size.X * s, np.Size.Y * s);
        int n = System.Math.Max(1, VisibleRows());
        float pitch = System.Math.Min(43f * s, area.Size.Y * s / n);
        Rows.RowHeight = pitch * 0.84f; Rows.RowGap = pitch * 0.16f;
        float used = n * pitch - Rows.RowGap;
        _scroll.Position = new Vector2(area.Position.X * s, area.Position.Y * s + (area.Size.Y * s - used) / 2f);
        _scroll.Size = new Vector2(area.Size.X * s, used);
        if (_foot is { } foot) { foot.Position = new Vector2(24f, UiTextureBuilder.CropH * s - 60f); foot.Size = new Vector2(UiTextureBuilder.CropW * s, foot.GetCombinedMinimumSize().Y); }
    }
}
