using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer;
using SmContent.Extras.Warfront;

namespace SmClient.FrontEnd;

/// <summary>
/// The WARFRONT page in the concept's own frame (owner, 2026-11-20: the same style as the main menu, Options, Classic PvP and the Armoury): the shared <see cref="ConceptPanelPage"/> frame and title plate, a dark inset status strip
/// under the title (DEPLOYMENTS, FRONTS, LEVEL: a small gold label over a large value, three even cells), one centred column of plates (<see cref="ColumnW"/> by <see cref="PlateH"/>, <see cref="PlateGap"/> apart) and the
/// gold CONTINUE CAMPAIGN (or REJOIN CAMPAIGN) plate in the frame's tray, widened for its words; BACK is the concept's own plate at the bottom right, as on every page. No heading, no explainer text: the hints are hover tooltips.
/// The coordinates are in a 1920 by 1080 screen (the owner's proposal) and scale with the frame, like the other concept pages. Every plate is the page's own button (same handler, same focus), taken out of the rows' column
/// (as the base page does for BACK) and placed here; the plates are the dark and gold opt-plate art, their words one size (the longest step down together, never touching the plate's ends). Ours (spec "Warfront page layout").
/// </summary>
sealed partial class WarfrontPage : ConceptPanelPage
{
    // 1920 x 1080 design pixels (the owner's layout).
    public const float StripX = 280f, StripY = 262f, StripW = 1360f, StripH = 96f;
    public const float ColumnX = 600f, ColumnW = 720f, PlateH = 64f, PlateGap = 18f, ColumnY = 392f;
    /// <summary>The gold plate's width at 1920 (CONTINUE CAMPAIGN touched its ends at the frame's tray width).</summary>
    public const float MainW = 420f;
    /// <summary>Where the status line (the honor roll's counts, or that the server runs) sits under the column, at 1920.</summary>
    public const float NoteY = 740f, NoteH = 44f;

    readonly Panel _strip = new() { Name = "WarfrontStrip", MouseFilter = MouseFilterEnum.Pass };
    readonly Control _dividers = new() { Name = "WarfrontStripDividers", MouseFilter = MouseFilterEnum.Ignore };
    readonly Label[] _labels = new Label[3], _values = new Label[3];
    readonly Label _note = new() { Name = "WarfrontNote", MouseFilter = MouseFilterEnum.Pass, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly List<Button> _column = new();
    Button? _main;

    /// <summary>The strip's three cells (label, value) as shown now (the self-test reads them).</summary>
    public IReadOnlyList<(string Label, string Value)> Cells => Enumerable.Range(0, 3).Select(i => (_labels[i].Text, _values[i].Text)).ToList();
    public Label NoteLabel => _note;
    public Panel Strip => _strip;
    /// <summary>The column's plates, top to bottom.</summary>
    public IReadOnlyList<Button> Column => _column;
    public Button? MainPlate => _main;
    /// <summary>Every label the strip and the status line show (the text audit and the fit check).</summary>
    public IEnumerable<Label> OwnLabels() => _labels.Concat(_values).Append(_note);

    public WarfrontPage(string name, string title) : base(name, "", title)
    {
        _scroll.ClipContents = false;   // the plates are top-level children of the rows (as BACK is); nothing clips their clicks
        _scroll.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_strip);
        _strip.AddThemeStyleboxOverride("panel", new Steel.SteelStyle(Steel.Kind.Recess, 5f, 0f, 0f));
        _strip.AddChild(_dividers);
        for (int i = 0; i < 3; i++)
        {
            _labels[i] = Cell($"WarfrontStripLabel{i}", WarfrontStatusCells.Labels[i], Steel.GoldHi);
            _values[i] = Cell($"WarfrontStripValue{i}", "-", Steel.Text);
            _strip.AddChild(_labels[i]); _strip.AddChild(_values[i]);
        }
        _note.AddThemeFontOverride("font", UiKit.ConceptFont());
        _note.AddThemeColorOverride("font_color", Steel.TextDim);
        AddChild(_note);
        Visible = false;
    }

    static Label Cell(string name, string text, Color ink)
    {
        var l = new Label { Name = name, Text = text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore, ClipText = false };
        l.AddThemeFontOverride("font", UiKit.ConceptFont());
        l.AddThemeColorOverride("font_color", ink);
        return l;
    }

    /// <summary>Puts the run's figures in the strip (dashes with no run) and the hover hint that carries the honor roll's counts.</summary>
    public void SetStatus(WarfrontStatusCells? cells, string? hint)
    {
        var v = (cells ?? WarfrontStatusCells.None).Values;
        for (int i = 0; i < 3; i++) _values[i].Text = v[i];
        UiTooltip.Attach(_strip, hint ?? "");
    }

    /// <summary>The status line under the column ("THE WARFRONT SERVER IS RUNNING", the honor roll's counts); empty hides it.</summary>
    public void SetNote(string text, string? kind = null)
    {
        _note.Text = text;
        _note.Visible = text.Length > 0;
        if (kind is not null) UiKit.TextKind(_note, kind);
    }

    /// <summary>The plates of the column, top to bottom, and the gold main plate (CONTINUE CAMPAIGN or REJOIN CAMPAIGN; null when the page has none). All are children of the rows.</summary>
    public void SetPlates(IEnumerable<Button> column, Button? main)
    {
        _column.Clear(); _column.AddRange(column); _main = main;
        Place();
    }

    protected override void Place()
    {
        base.Place();
        if (UsesConcept is false || _strip is null || _note is null || PanelScale <= 0f) return;
        // The frame's scale at 1920 x 1080 is the design's; a window of another size scales the whole layout with the frame, centred.
        float sv0 = Mathf.Min(1080f * 0.95f / (342f + PanelFrame.ExtraV2(2)), 1920f * 0.96f / 736f);
        float f = PanelScale / sv0, s = PanelScale * 15f / 14f;
        var centre = Size / 2f;
        Rect2 R(float x, float y, float w, float h) => new(centre + (new Vector2(x, y) - new Vector2(960f, 540f)) * f, new Vector2(w, h) * f);

        var strip = R(StripX, StripY, StripW, StripH);
        _strip.Position = strip.Position; _strip.Size = strip.Size;
        float cell = strip.Size.X / 3f;
        int cap = UiType.Px(UiType.Caption), big = UiType.Px(UiType.Label);
        for (int i = 0; i < 3; i++)
        {
            _labels[i].AddThemeFontSizeOverride("font_size", cap);
            _values[i].AddThemeFontSizeOverride("font_size", big);
            _labels[i].Position = new Vector2(cell * i, strip.Size.Y * 0.08f); _labels[i].Size = new Vector2(cell, strip.Size.Y * 0.36f);
            _values[i].Position = new Vector2(cell * i, strip.Size.Y * 0.42f); _values[i].Size = new Vector2(cell, strip.Size.Y * 0.52f);
        }
        _dividers.Position = Vector2.Zero; _dividers.Size = strip.Size;
        foreach (var d in _dividers.GetChildren()) d.QueueFree();
        for (int i = 1; i < 3; i++)
            _dividers.AddChild(new ColorRect { Color = new Color(Steel.SteelMid, 0.7f), MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(cell * i - 1f, strip.Size.Y * 0.14f), Size = new Vector2(2f, strip.Size.Y * 0.72f) });

        var note = R(StripX, NoteY, StripW, NoteH);
        _note.Position = note.Position; _note.Size = note.Size;
        _note.AddThemeFontSizeOverride("font_size", UiType.Px(UiType.Caption));

        // The column: one size for every word (the longest sets it), the plates the opt-plate art.
        var plates = new List<(Button B, Rect2 Box, bool Gold)>();
        for (int i = 0; i < _column.Count; i++) plates.Add((_column[i], R(ColumnX, ColumnY + i * (PlateH + PlateGap), ColumnW, PlateH), false));
        if (_main is not null) { var m = R(960f - MainW / 2f, 888f, MainW, PlateH); plates.Add((_main, m, true)); }
        var font = UiKit.ConceptFont();
        int unit = UiType.Plate;
        foreach (var p in plates)
        {
            var (b, box, _) = p;
            b.TopLevel = true; b.CustomMinimumSize = Vector2.Zero; b.Position = box.Position; b.Size = box.Size;
            b.Alignment = HorizontalAlignment.Center; b.ClipText = false; b.AutowrapMode = TextServer.AutowrapMode.Off;
            if (!p.Gold) Paint(b, s);   // the main plate keeps the base page's own paint (the tray's apply plate: steel until it has the focus, then gold); only its size is ours
            float avail = box.Size.X - b.GetThemeStylebox("normal").GetMinimumSize().X;
            unit = Math.Min(unit, PaintedButtons.FitUnit(font, b.Text, UiType.Plate, avail, out _));
        }
        foreach (var p in plates) p.B.AddThemeFontSizeOverride("font_size", UiType.Px(unit));
    }

    /// <summary>The page's plate: dark with a gold twin for hover and focus (the other concept pages' plate), the ink dark on every gold state.</summary>
    static void Paint(Button b, float s)
    {
        b.AddThemeFontOverride("font", UiKit.ConceptFont());
        b.AddThemeConstantOverride("outline_size", 0);
        if (!PaintedButtons.Apply(b, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(3, -0.5f, 3, -0.5f), new PaintedButtons.Pad(3, -0.5f, 3, 0f), s, true, paintedText: false, goldRest: false, noGlow: true, cpx: s)) return;
        if (b.GetThemeStylebox("pressed") is { } on) { b.AddThemeStyleboxOverride("hover", on); b.AddThemeStyleboxOverride("hover_pressed", on); }
        var light = new Color(0.84f, 0.83f, 0.80f); var dark = new Color(0.10f, 0.06f, 0.02f);
        b.AddThemeColorOverride("font_color", light);
        foreach (var c in new[] { "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color" }) b.AddThemeColorOverride(c, dark);
        b.AddThemeColorOverride("font_disabled_color", Steel.TextOff);
    }
}
