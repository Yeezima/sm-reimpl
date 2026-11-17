using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer;

namespace SmClient.FrontEnd;

/// <summary>
/// The shared page of every menu that is not the main menu, Options or the Armoury (ui/overhaul): the Options frame of the concept (panel 04, <c>overlay-panel.png</c>: the same painted steel, stone and crest, the title plate
/// blank) with the page's title in live text on the plate, taller than the concept's (a vertical slice of the plain interior is mirrored in, so the stone and steel stay continuous) so a long page fits in two columns, and the page's
/// own rows inside it. The rows are the old pages' (same controls, same handlers): <see cref="Style"/> gives them the concept's lettering (<see cref="UiKit.ConceptFont"/>, one size per level), its pill, switch and plate art.
/// Ours (docs/ui-overhaul.md).
/// </summary>
public partial class ConceptPanelPage : UiKit.MenuPage
{
    /// <summary>Overlay geometry in v2 pixels, overlay-local: the picture's size, the rows area's left, top and width, the bottom of the rows area at the concept's height, the title plate.</summary>
    const float OverlayW = 736f, OverlayH = 342f, RowsX = 79.4f, RowsY = 124f, RowsW = 580f, RowsBottom = 262f;
    static readonly Rect2 TitleRect = new(268f, 41f, 198f, 33f);

    readonly TextureRect _overlay = new() { Name = "PanelOverlay", MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, TextureFilter = TextureFilterEnum.Linear };
    readonly ColorRect _dim = new() { Name = "PanelDim", Color = new Color(0f, 0f, 0f, 0.38f), MouseFilter = MouseFilterEnum.Ignore };
    readonly Label _plate = new() { Name = "PanelTitle", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
    readonly int _slices;   // inserted copies of the slice: 0 or 2 (even, so the mirrored joins stay continuous)
    readonly bool _concept;
    float _styledScale;
    string _titleText = "";

    /// <summary>The frame at the window's scale the last time it was placed (the self-test and the capture read it): px per v2 pixel.</summary>
    public float PanelScale { get; private set; }
    public Rect2 RowsRect => _scroll.GetRect();
    public bool UsesConcept => _concept;

    public ConceptPanelPage(string name, string heading, string? title = null, int slices = 2, float dim = 0.38f) : base(name, heading)
    {
        _slices = slices <= 0 ? 0 : 2;
        _concept = ConceptArt.Get("overlay-panel") is not null;
        _titleText = (title ?? heading).ToUpperInvariant();
        if (!_concept) return;   // without the art the old page stays as it was
        _title.Visible = false; _heading.Visible = false;
        _dim.Color = new Color(0f, 0f, 0f, dim);
        AddChild(_dim); MoveChild(_dim, 0);
        AddChild(_overlay); MoveChild(_overlay, 1);
        _overlay.Texture = PanelFrame.Texture("overlay-panel", _slices);
        _plate.Text = _titleText;
        _plate.AddThemeFontOverride("font", UiKit.ConceptFont());
        _plate.AddThemeColorOverride("font_color", new Color(0.86f, 0.85f, 0.81f));
        AddChild(_plate);
        _scroll.ClipContents = true;
        _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
        Rows.CompactGaps = true; Rows.ColumnGap = 0f;
        MoveChild(_scroll, -1);
    }

    float ExtraV2 => PanelFrame.ExtraV2(_slices);

    protected override void Place()
    {
        if (_concept is false || _plate is null || _overlay is null) { base.Place(); return; }
        float totalH = OverlayH + ExtraV2;
        float sv = Mathf.Max(0.2f, Mathf.Min(Size.Y * 0.95f / totalH, Size.X * 0.96f / OverlayW));
        PanelScale = sv;
        var o = new Vector2((Size.X - OverlayW * sv) / 2f, (Size.Y - totalH * sv) / 2f);
        _dim.Position = Vector2.Zero; _dim.Size = Size;
        _overlay.Position = o; _overlay.Size = new Vector2(OverlayW, totalH) * sv;
        Rect2 R(Rect2 r) => new(o + r.Position * sv, r.Size * sv);
        var t = R(TitleRect);
        _plate.Position = t.Position; _plate.Size = t.Size;
        float s = sv * 15f / 14f;   // the lettering's units ("grid" pixels: v2 x 14/15)
        FitPlate(s, t.Size.X * 0.92f);
        var rows = R(new Rect2(RowsX, RowsY, RowsW, RowsBottom - RowsY + ExtraV2));
        _rowsOrigin = rows.Position;
        Style(s, rows.Size);
        if (_foot is { } foot) { foot.Position = new Vector2(24f, Size.Y - foot.GetCombinedMinimumSize().Y - 14f); foot.Size = new Vector2(Mathf.Min(Size.X - 48f, 1100f), foot.GetCombinedMinimumSize().Y); }
    }

    void FitPlate(float s, float room)
    {
        var font = UiKit.ConceptFont();
        int size = Mathf.RoundToInt(21f * s);
        while (size > 8 && font.GetStringSize(_plate.Text, HorizontalAlignment.Left, -1, size).X > room) size--;
        _plate.AddThemeFontSizeOverride("font_size", size);
    }

    /// <summary>The page's title (the plate's live text).</summary>
    public string PlateTitle { get => _titleText; set { _titleText = value.ToUpperInvariant(); if (_plate is not null) { _plate.Text = _titleText; QueueSortPlate(); } } }
    void QueueSortPlate() => CallDeferred(nameof(PlaceDeferred));
    void PlaceDeferred() => Place();

    static readonly Color Light = new(0.84f, 0.83f, 0.80f), Dark = new(0.10f, 0.06f, 0.02f);

    /// <summary>One column narrower than the rows area and centred in it (the page's rows fit one column), or the whole width in two columns.</summary>
    bool _narrow;
    float _narrowW;
    Vector2 _rowsOrigin;

    /// <summary>Gives every row of the page the concept's look at scale <paramref name="s"/> (px per grid pixel): choices as the Options rows, buttons as the concept's plates, notes and fields in the one lettering.</summary>
    void Style(float s, Vector2 area)
    {
        if (Mathf.IsEqualApprox(_styledScale, s) && Mathf.IsEqualApprox((float)GetMeta("styled_w", 0f), area.X)) { PlaceRows(area); return; }
        _styledScale = s; SetMeta("styled_w", area.X);
        float pitch = UiKit.ConceptRowPitch * s;
        var all = new List<Control>(); var columns = new List<UiKit.MenuColumn> { Rows };
        void Walk(Node n)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is UiKit.MenuColumn mc) columns.Add(mc);
                if (c is Control ctl) all.Add(ctl);
                if (c is not Button and not Label and not LineEdit) Walk(c);
            }
        }
        Walk(Rows);
        var choices = all.OfType<UiKit.MenuChoice>().ToList();
        float labelW = choices.Count == 0 ? 0f : Mathf.Max(104f * s, choices.Max(c => c.ConceptLabelWidth(s)) + 10f * s);
        float ctlW = choices.Count == 0 ? 0f : choices.Max(c => c.ConceptControlWidth(s));
        float gap = 18f * s, pad = 14f * s;
        float narrowW = Mathf.Min(area.X - pad, Mathf.Max(330f * s, labelW + ctlW + 24f * s));
        // First try one narrow column; when its rows are taller than the area they flow into two columns across the whole width.
        _narrow = true; _narrowW = narrowW;
        for (int pass = 0; pass < 2; pass++)
        {
            float colW = _narrow ? narrowW : (area.X - gap - pad) / 2f;
            foreach (var mc in columns) { mc.RowHeight = pitch; mc.RowGap = 0f; mc.CompactGaps = true; mc.ColumnGap = gap; mc.WrapHeight = _narrow ? 0f : Mathf.Max(pitch * 4f, area.Y - 4f * s); }
            foreach (var c in all)
            {
                switch (c)
                {
                    case UiKit.MenuChoice ch: ch.UseConcept(s, labelW, Mathf.Max(ctlW, Mathf.Min(colW - labelW, ctlW * 1.3f))); break;
                    case Button b when b.ThemeTypeVariation == UiKit.MenuButtonType: StylePlate(b, s, pitch); break;
                    case LineEdit le: StyleField(le, s, pitch, Mathf.Max(labelW, 120f * s)); break;
                    case Label l: StyleLabel(l, s, colW); break;
                }
                if (c.HasMeta(UiKit.MenuColumn.GapMeta)) c.CustomMinimumSize = new Vector2(0f, pitch * 0.45f);
            }
            foreach (var mc in columns) mc.UpdateMinimumSize();
            float total = Rows.ContentHeight();
            if (OS.GetCmdlineUserArgs().Contains("--panel-debug")) GD.Print($"panel {Name}: s {s:0.00} pass {pass} narrow {_narrow} total {total:0} area {area.Y:0} rows {all.Count}");
            if (!_narrow || total <= area.Y - 2f * s) break;
            _narrow = false;
        }
        PlaceRows(area);
    }

    void PlaceRows(Vector2 area)
    {
        _scroll.Position = new Vector2(_rowsOrigin.X + (_narrow ? (area.X - _narrowW) / 2f : 0f), _rowsOrigin.Y);
        _scroll.Size = new Vector2(_narrow ? _narrowW : area.X, area.Y);
    }

    void StylePlate(Button b, float s, float pitch)
    {
        b.Alignment = HorizontalAlignment.Center;
        b.ClipText = false;
        b.CustomMinimumSize = new Vector2(0f, pitch);
        b.AddThemeFontOverride("font", UiKit.ConceptFont());
        b.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(15f * s));
        b.AddThemeConstantOverride("outline_size", 0);
        float inset = 2f * s;
        // The plate is a little shorter than its row and as wide as the column, so the rows keep a breath between them.
        if (!PaintedButtons.Apply(b, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(3, 4, 3, 4), new PaintedButtons.Pad(3, 4, 3, 5), s, true, paintedText: false, noGlow: true)) return;
        foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" })
            if (b.GetThemeStylebox(st) is StyleBoxTexture tb) { tb = (StyleBoxTexture)tb.Duplicate(); tb.ExpandMarginTop = Mathf.Max(0f, tb.ExpandMarginTop + 0f) - 0f; b.AddThemeStyleboxOverride(st, tb); }
        foreach (var c in new[] { "font_color", "font_hover_color", "font_focus_color" }) b.AddThemeColorOverride(c, Light);
        foreach (var c in new[] { "font_pressed_color", "font_hover_pressed_color" }) b.AddThemeColorOverride(c, Dark);
        b.AddThemeColorOverride("font_disabled_color", Steel.TextOff);
        // A focused plate is the gold one: its dark ink.
        b.AddThemeColorOverride("font_focus_color", Dark);
        _ = inset;
        // Counters and value labels a page hangs on a button (the Host panel's bot counts) keep the lettering.
        foreach (var l in b.GetChildren().OfType<Label>()) StyleLabel(l, s, 0f);
    }

    void StyleField(LineEdit le, float s, float pitch, float labelW)
    {
        le.AddThemeFontOverride("font", UiKit.ConceptFont());
        le.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(UiKit.ConceptValueFont * s));
        var flat = new StyleBoxFlat { BgColor = new Color(0.035f, 0.033f, 0.032f), BorderColor = new Color(0.42f, 0.41f, 0.39f), BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2, ContentMarginLeft = 10f * s, ContentMarginRight = 10f * s, ContentMarginTop = 2f * s, ContentMarginBottom = 2f * s };
        var focus = (StyleBoxFlat)flat.Duplicate(); focus.BorderColor = Steel.Gold;
        le.AddThemeStyleboxOverride("normal", flat); le.AddThemeStyleboxOverride("focus", focus);
        le.AddThemeColorOverride("font_color", Light); le.AddThemeColorOverride("font_selected_color", Dark); le.AddThemeColorOverride("selection_color", Steel.Gold);
        le.AddThemeColorOverride("caret_color", Steel.GoldHi);
        le.OffsetTop = (pitch - UiKit.ConceptPillH * s) / 2f; le.OffsetBottom = -(pitch - UiKit.ConceptPillH * s) / 2f;
        if (le.GetParent() is Control row) { row.CustomMinimumSize = new Vector2(0f, pitch); le.OffsetLeft = labelW + 8f * s; le.OffsetRight = le.OffsetLeft + 170f * s; }
    }

    void StyleLabel(Label l, float s, float width)
    {
        bool onButton = l.GetParent() is Button;
        bool rowLabel = !onButton && l.GetParent() is Control p && p is not UiKit.MenuColumn;
        l.AddThemeFontOverride("font", UiKit.ConceptFont());
        l.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt((rowLabel ? UiKit.ConceptLabelFont : UiKit.ConceptValueFont) * s));
        l.VerticalAlignment = VerticalAlignment.Center;
        if (onButton) { l.ClipText = true; l.OffsetLeft = 0; l.HorizontalAlignment = HorizontalAlignment.Right; l.OffsetRight = -14f * s; return; }
        if (rowLabel) { l.ClipText = true; return; }
        // A note: wraps inside its column; its row grows to the lines it needs.
        l.ClipText = false; l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(Mathf.Max(60f, width - 6f * s), 0f);
    }
}
