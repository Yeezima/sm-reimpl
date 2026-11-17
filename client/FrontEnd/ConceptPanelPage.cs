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
        SteelBackdrop.EmbossTitle(_plate);
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
        // The strip under the title (where Options keeps its tabs) carries the page's heading line: where this page is.
        var strip = R(new Rect2(RowsX, 88f, RowsW, 23f));
        bool show = Heading.Length > 0;
        _heading.Visible = show;
        if (show)
        {
            _heading.Position = strip.Position; _heading.Size = strip.Size;
            _heading.VerticalAlignment = VerticalAlignment.Center; _heading.HorizontalAlignment = HorizontalAlignment.Left;
            _heading.AddThemeFontOverride("font", UiKit.ConceptFont());
            _heading.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(UiKit.ConceptValueFont * s));
            _heading.AddThemeColorOverride("font_color", Steel.GoldHi);
            _heading.ClipText = true;
        }
        var rows = R(new Rect2(RowsX, RowsY, RowsW, RowsBottom - RowsY + ExtraV2));
        _rowsOrigin = rows.Position;
        Style(s, rows.Size);
        Dock(R, s);
        if (_foot is { } foot) { foot.Position = new Vector2(24f, Size.Y - foot.GetCombinedMinimumSize().Y - 14f); foot.Size = new Vector2(Mathf.Min(Size.X - 48f, 1100f), foot.GetCombinedMinimumSize().Y); }
    }

    /// <summary>Texts (any case) of the rows that become the page's main action in the bottom tray; the first one that is shown wins (the Host page's HOST A MATCH, then JOIN YOUR SERVER).</summary>
    public string[] PrimaryTexts { get; set; } = [];

    static void Collect(Node n, List<Button> into)
    {
        foreach (var c in n.GetChildren())
        {
            if (c is Button b) into.Add(b);
            else if (c is not Label and not LineEdit) Collect(c, into);
        }
    }

    float _dockScale;

    /// <summary>
    /// One BACK and one main action on every page (Opus review 1): BACK is the concept's own BACK plate at the screen's bottom right, exactly where Options puts it; the main action is the gold plate in the frame's bottom tray, where
    /// Options has APPLY. Both stay the page's own buttons (same handlers, same focus chain); they only leave the column (a top-level control is not laid out by it).
    /// </summary>
    void Dock(Func<Rect2, Rect2> R, float s)
    {
        var buttons = new List<Button>(); Collect(Rows, buttons);
        var back = buttons.FirstOrDefault(b => b.Text.Equals("BACK", StringComparison.OrdinalIgnoreCase));
        Button? main = null;
        foreach (var t in PrimaryTexts) { main = buttons.FirstOrDefault(b => b.Visible && b.Text.Equals(t, StringComparison.OrdinalIgnoreCase)); if (main is not null) break; }
        bool restyle = !Mathf.IsEqualApprox(_dockScale, s); _dockScale = s;
        if (back is not null)
        {
            var ps = UiTextureBuilder.V2Screen;
            float sS = Mathf.Min(Size.Y / ps.Size.Y, Size.X / ps.Size.X);
            var o2 = (Size - ps.Size * sS) / 2f;
            var g = UiTextureBuilder.G(UiTextureBuilder.V2Back);
            back.TopLevel = true; back.CustomMinimumSize = Vector2.Zero;
            back.Position = o2 + g.Position * sS; back.Size = g.Size * sS;
            if (restyle || back.GetMeta("dock_s", 0f).AsSingle() != sS)
            {
                back.SetMeta("dock_s", sS);
                PaintedButtons.Apply(back, "opt-back-dark", "opt-back-dark", new PaintedButtons.Pad(0, 0, 0, 0), new PaintedButtons.Pad(0, 0, 0, 0), sS, false, paintedText: false, noGlow: false);
                DockText(back, sS, goldRest: false);
            }
        }
        if (main is not null)
        {
            var r = R(new Rect2(295f, 274.5f + ExtraV2, 146.5f, 28f));
            main.TopLevel = true; main.CustomMinimumSize = Vector2.Zero;
            main.Position = r.Position; main.Size = r.Size;
            if (restyle || main.GetMeta("dock_s", 0f).AsSingle() != s)
            {
                main.SetMeta("dock_s", s);
                PaintedButtons.Apply(main, "opt-apply-dark", "opt-apply-gold", new PaintedButtons.Pad(3, 4, 3, 4), new PaintedButtons.Pad(3, 4, 3, 5), s, false, paintedText: false, goldRest: true, noGlow: true);
                DockText(main, s, goldRest: true);
            }
        }
    }

    static void DockText(Button b, float s, bool goldRest)
    {
        b.Alignment = HorizontalAlignment.Center; b.ClipText = false;
        b.AddThemeFontOverride("font", UiKit.ConceptFont());
        b.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(15f * s));
        b.AddThemeConstantOverride("outline_size", 0);
        var ink = goldRest ? Dark : Light;
        foreach (var c in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color" }) b.AddThemeColorOverride(c, ink);
        b.AddThemeColorOverride("font_disabled_color", Steel.TextOff);
        foreach (var l in b.GetChildren().OfType<Label>()) l.Visible = false;
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
    /// <summary>The rows' pitch on these pages in lettering units (the concept's Options rows are 31.2; a little tighter so the longest page, HOST, fits two columns without scrolling).</summary>
    const float PagePitch = 25.5f;
    bool _narrow;
    float _narrowW;
    Vector2 _rowsOrigin;

    /// <summary>Gives every row of the page the concept's look at scale <paramref name="s"/> (px per grid pixel): choices as the Options rows, buttons as the concept's plates, notes and fields in the one lettering.</summary>
    void Style(float s, Vector2 area)
    {
        foreach (var n in _notes.Keys.ToList()) FitNote(n);
        if (Mathf.IsEqualApprox(_styledScale, s) && Mathf.IsEqualApprox((float)GetMeta("styled_w", 0f), area.X)) { PlaceRows(area); return; }
        _styledScale = s; SetMeta("styled_w", area.X);
        float pitchG = PagePitch, pitch = pitchG * s;
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
                    case UiKit.MenuChoice ch: ch.ConceptPitch = pitchG; ch.FixedPill = true; ch.UseConcept(s, labelW, Mathf.Max(ctlW, Mathf.Min(colW - labelW, ctlW * 1.3f))); break;
                    case Button b when b.ThemeTypeVariation == UiKit.MenuButtonType: StylePlate(b, s, pitch); break;
                    case LineEdit le: StyleField(le, s, pitch, Mathf.Max(labelW, 120f * s), colW); break;
                    case Label l: StyleLabel(l, s, colW); break;
                }
                if (c.HasMeta(UiKit.MenuColumn.GapMeta)) c.CustomMinimumSize = new Vector2(0f, pitch * 0.3f);
            }
            foreach (var mc in columns) mc.UpdateMinimumSize();
            float total = Rows.ContentHeight();
            if (OS.GetCmdlineUserArgs().Contains("--panel-debug")) GD.Print($"panel {Name}: s {s:0.00} pass {pass} narrow {_narrow} total {total:0} area {area.Y:0} rows {all.Count}");
            _total = total;
            if (!_narrow || total <= area.Y - 2f * s) break;
            _narrow = false;
        }
        PlaceRows(area);
    }

    float _total;

    void PlaceRows(Vector2 area)
    {
        // One narrow column sits in the middle of the frame (across and down); two columns fill the width from the top.
        _total = Rows.ContentHeight();
        float h = _narrow ? Mathf.Min(area.Y, _total + 6f) : area.Y;
        float dy = _narrow ? Mathf.Max(0f, (area.Y - h) / 2f) : 0f;
        _scroll.Position = new Vector2(_rowsOrigin.X + (_narrow ? (area.X - _narrowW) / 2f : 0f), _rowsOrigin.Y + dy);
        _scroll.Size = new Vector2(_narrow ? _narrowW : area.X, h);
        // A scroll bar only when the rows really are taller than the room (a layout rounding never shows one).
        _scroll.VerticalScrollMode = Rows.ContentHeight() > h + 4f ? ScrollContainer.ScrollMode.Auto : ScrollContainer.ScrollMode.ShowNever;
    }

    void StylePlate(Button b, float s, float pitch)
    {
        b.Alignment = HorizontalAlignment.Center;
        b.ClipText = false;
        b.CustomMinimumSize = new Vector2(0f, pitch);
        b.AddThemeFontOverride("font", UiKit.ConceptFont());
        b.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(15f * s));
        b.AddThemeConstantOverride("outline_size", 0);
        // The plate hugs its row (a 1 px margin), so neighbouring plates meet without overlapping.
        if (!PaintedButtons.Apply(b, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(3, -2.5f, 3, -2.5f), new PaintedButtons.Pad(3, -2.5f, 3, -2f), s, true, paintedText: false, noGlow: true)) return;
        // Hover and focus are both the gold plate, so the ink is dark on both (the review: light ink on a gold plate under the pointer).
        if (b.GetThemeStylebox("pressed") is { } gold) { b.AddThemeStyleboxOverride("hover", gold); b.AddThemeStyleboxOverride("hover_pressed", gold); }
        b.AddThemeColorOverride("font_color", Light);
        foreach (var c in new[] { "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color" }) b.AddThemeColorOverride(c, Dark);
        b.AddThemeColorOverride("font_disabled_color", Steel.TextOff);
        // Counters and value labels a page hangs on a button (the Host panel's bot counts) keep the lettering.
        foreach (var l in b.GetChildren().OfType<Label>()) StyleLabel(l, s, 0f);
    }

    void StyleField(LineEdit le, float s, float pitch, float labelW, float colW)
    {
        le.AddThemeFontOverride("font", UiKit.ConceptFont());
        le.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(UiKit.ConceptValueFont * s));
        var recess = new Steel.SteelStyle(Steel.Kind.Recess, 5f, 10f * s, 2f * s);
        var focus = new Steel.SteelStyle(Steel.Kind.CardGold, 5f, 10f * s, 2f * s);
        le.AddThemeStyleboxOverride("normal", recess); le.AddThemeStyleboxOverride("focus", focus);
        le.AddThemeColorOverride("font_color", Light); le.AddThemeColorOverride("font_selected_color", Light); le.AddThemeColorOverride("selection_color", new Color(0.52f, 0.52f, 0.48f, 0.45f));
        le.AddThemeColorOverride("caret_color", Steel.GoldHi);
        le.OffsetTop = (pitch - UiKit.ConceptPillH * s) / 2f; le.OffsetBottom = -(pitch - UiKit.ConceptPillH * s) / 2f;
        if (le.GetParent() is Control row) { row.CustomMinimumSize = new Vector2(0f, pitch); le.OffsetLeft = labelW + 8f * s; le.OffsetRight = le.OffsetLeft + Mathf.Max(60f * s, Mathf.Min(150f * s, colW - labelW - 16f * s)); }
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
        _notes[l] = Mathf.Max(60f, width - 6f * s);
        FitNote(l);
    }

    /// <summary>The notes of the page and the width each wraps at (the text may change while the page is open).</summary>
    readonly Dictionary<Label, float> _notes = new();

    /// <summary>Gives a wrapping note the height its words need at its column's width (a label reports its wrapped height only after it has been laid out, too late for the page's measuring).</summary>
    void FitNote(Label l)
    {
        if (!_notes.TryGetValue(l, out float w) || !IsInstanceValid(l)) return;
        string up = l.Text.ToUpperInvariant();
        if (l.Text != up) l.Text = up;   // one case per level: notes are capitals like every other text
        l.HorizontalAlignment = _narrow ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        var font = UiKit.ConceptFont(); int size = l.GetThemeFontSize("font_size");
        float h = font.GetMultilineStringSize(l.Text, HorizontalAlignment.Left, w, size, -1, TextServer.LineBreakFlag.WordBound | TextServer.LineBreakFlag.Mandatory).Y;
        l.CustomMinimumSize = new Vector2(w, h + 4f);
    }
}
