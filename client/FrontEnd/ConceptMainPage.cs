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
    /// <summary>The nameplate's words: where builds and issues are (it was an empty plate; Opus review 1).</summary>
    readonly LinkButton _link = new() { Name = "NameplateLink", Uri = SmContent.ServerLaunch.ReleasesUrl, Text = SmContent.ServerLaunch.ReleasesUrl.Replace("https://", "").ToUpperInvariant(), Underline = LinkButton.UnderlineMode.Never, FocusMode = FocusModeEnum.None };

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
            _link.AddThemeFontOverride("font", UiKit.ConceptFont());
            _link.AddThemeColorOverride("font_color", new Color(0.70f, 0.68f, 0.62f)); _link.AddThemeColorOverride("font_hover_color", Steel.GoldHi);
            AddChild(_link);
            // The lower edge of the picture fades into the scene (a window narrower than 16:9 ends the picture above the window's bottom).
            _overlay.Material = new ShaderMaterial { Shader = new Shader { Code = "shader_type canvas_item;\nvoid fragment() { vec4 c = texture(TEXTURE, UV); c.a *= 1.0 - smoothstep(0.93, 1.0, UV.y); COLOR = c; }" } };
        }
        else
        {
            _frame.AddThemeStyleboxOverride("panel", new Steel.SteelStyle(Steel.Kind.AmberFrame, 16f, 0f, 0f));
            _pillar.Name = "MenuPillar";
            AddChild(_pillar); AddChild(_wordmark); AddChild(_crest); AddChild(_frame);
            AddChild(_nameplate);
        }
        _scroll.ClipContents = false;   // the painted plates reach past their rows (glow, the gold plate's rim)
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

    /// <summary>
    /// The overlay at the window's height. The rows stand on the recess where the concept's plates stood: with five rows exactly where the plates are (pitch 42.85 crop pixels), otherwise the same block
    /// centred and tighter. Each of the five known labels draws the concept's own plate with its own painted label (<see cref="UiTextureBuilder.ButtonLabels"/>); other rows (STOP MUSOU SERVER) keep the blank plate and live text.
    /// </summary>
    /// <summary>The whole left group (wordmark, frame, plates) is drawn at this fraction of its size, anchored at the top left: 0.7 by default (<see cref="DefaultMenuScale"/>); <c>--menu-scale &lt;f&gt;</c> (0.4 to 1) lets the owner shrink it to clear the Marine when the video is shown whole (<c>--menu-zoom 1.0</c>; the Marine's backpack begins at about 0.375 of the picture's width, the frame ends at 0.46, so 0.78 clears it in the 0.5, 3, 5 and 7 s frames).</summary>
    public const float DefaultMenuScale = 0.7f;
    /// <summary>True: the group's bottom left corner is the window's (the stone wall and the frame flush with both edges); false: its top left corner is.</summary>
    public const bool AnchorBottom = true;
    public static float MenuScale { get; } = ParseScale(OS.GetCmdlineUserArgs());

    static float ParseScale(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string value = args[i] == "--menu-scale" && i + 1 < args.Length ? args[i + 1] : args[i].StartsWith("--menu-scale=", System.StringComparison.Ordinal) ? args[i]["--menu-scale=".Length..] : "";
            if (value.Length > 0 && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)) return Mathf.Clamp(f, 0.4f, 1f);
        }
        return DefaultMenuScale;
    }

    void PlaceOverlay(float h)
    {
        // Full height at 16:9; on a narrower window no wider than half of it (the video's battlefield must stay visible).
        float s = System.Math.Min(h / Panel01.ScreenH, 0.5f * Size.X / Panel01.OverlayW); s *= MenuScale;
        float oy = AnchorBottom ? h - Panel01.ScreenH * s : 0f;
        _overlay.Position = new Vector2(0f, oy); _overlay.Size = new Vector2(Panel01.OverlayW * s, Panel01.ScreenH * s);
        var wm = Panel01.Wordmark; var np = Panel01.Nameplate;
        _wordmarkBox.Position = new Vector2(wm.Position.X * s, wm.Position.Y * s + oy); _wordmarkBox.Size = new Vector2(wm.Size.X * s, wm.Size.Y * s);
        _nameplate.Position = new Vector2(np.Position.X * s, np.Position.Y * s + oy); _nameplate.Size = new Vector2(np.Size.X * s, np.Size.Y * s);
        int linkSize = UiType.Px(UiType.Small);
        _link.AddThemeFontSizeOverride("font_size", linkSize);
        // The address sits in the middle of the plate (Opus review 2).
        float linkW = UiKit.ConceptFont().GetStringSize(_link.Text, HorizontalAlignment.Left, -1, linkSize).X + 4f;
        _link.Size = new Vector2(linkW, _nameplate.Size.Y);
        _link.Position = _nameplate.Position + new Vector2((_nameplate.Size.X - linkW) / 2f, 0f);
        int n = System.Math.Max(1, VisibleRows());
        const float areaTop = Panel01.RowsAreaTop, areaH = Panel01.RowsAreaH, centre = Panel01.PlateTop1 + (4f * Panel01.PlatePitch + Panel01.PlateH) / 2f;
        float pitch = System.Math.Min(Panel01.PlatePitch, (areaH - 4f) / n);
        float k = pitch / Panel01.PlatePitch, rowH = Panel01.PlateH * k, used = (n - 1) * pitch + rowH;
        Rows.RowHeight = rowH * s; Rows.RowGap = (pitch - rowH) * s;
        float top = n == 5 ? Panel01.PlateTop1 : System.Math.Max(areaTop, centre - used / 2f);
        _scroll.Position = new Vector2(Panel01.PlateX0 * s, top * s + oy);
        _scroll.Size = new Vector2((Panel01.PlateX1 - Panel01.PlateX0) * s, used * s);
        PaintButtons(s, k);
        if (_foot is { } foot) { foot.Position = new Vector2(24f, Panel01.ScreenH * s - 60f + oy); foot.Size = new Vector2(Panel01.OverlayW * s, foot.GetCombinedMinimumSize().Y); }
    }

    /// <summary>
    /// Gives the five known rows their painted plates: a StyleBoxTexture per state from <c>btn-&lt;id&gt;-dark</c> (rest, hover warmed, disabled grey) and <c>-gold</c> (focus, pressed), expanded past the row's rectangle by the
    /// textures' margins so neighbouring rows' glow meets (scaled by <paramref name="k"/> when the rows are tighter). The live text stays on the button (the tree, the tests and the screen reader) but is drawn transparent: the label is
    /// in the picture.
    /// </summary>
    void PaintButtons(float s, float k)
    {
        foreach (var b in Rows.GetChildren().OfType<Button>())
        {
            var id = UiTextureBuilder.ButtonLabels.FirstOrDefault(l => l.Text == b.Text).Id;
            if (id is null || ConceptArt.Get("btn-" + id + "-dark") is not { } dark || ConceptArt.Get("btn-" + id + "-gold") is not { } gold) continue;
            StyleBoxTexture Box(Texture2D tex, bool isGold, Color mod)
            {
                float top = isGold ? Panel01.TexGoldTop : Panel01.TexDarkV, bottom = isGold ? Panel01.TexGoldBottom : Panel01.TexDarkV;
                // ui/text-scale: the plate carries its label, so it is resampled once to the window pixels it covers and drawn 1:1 (a mipmap or a point sample of the 4x art blurred or jagged the words).
                var fitted = CrispArt.Fitted("btn-" + id + (isGold ? "-gold" : "-dark"), new Vector2((Panel01.PlateX1 - Panel01.PlateX0) * s + 2f * Panel01.TexSide * s, Rows.RowHeight + (top + bottom) * k * s));
                return new StyleBoxTexture
                {
                    Texture = fitted ?? tex, ModulateColor = mod, DrawCenter = true,
                    ExpandMarginLeft = Panel01.TexSide * s, ExpandMarginRight = Panel01.TexSide * s, ExpandMarginTop = top * k * s, ExpandMarginBottom = bottom * k * s,
                    AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
                };
            }
            // The live text is transparent (the plate carries the words), so its size must not set the row's minimum height when the group is scaled down.
            b.AddThemeFontSizeOverride("font_size", 8);
            b.CustomMinimumSize = Vector2.Zero;
            b.SetMeta(FocusGlow.NoGlowMeta, true);   // the gold plate is the focus: no white halo washing it out
            b.TextureFilter = TextureFilterEnum.Linear;   // 1:1 plates: no mipmap blend
            b.AddThemeStyleboxOverride("normal", Box(dark, false, Colors.White));
            b.AddThemeStyleboxOverride("hover", Box(dark, false, ConceptArt.Warm));
            b.AddThemeStyleboxOverride("pressed", Box(gold, true, Colors.White));
            b.AddThemeStyleboxOverride("hover_pressed", Box(gold, true, Colors.White));
            b.AddThemeStyleboxOverride("focus", Box(gold, true, Colors.White));
            b.AddThemeStyleboxOverride("disabled", Box(dark, false, new Color(0.5f, 0.5f, 0.5f)));
            foreach (var colour in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color", "font_disabled_color" }) b.AddThemeColorOverride(colour, Colors.Transparent);
        }
    }
}
