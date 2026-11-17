using System;
using System.Linq;
using Godot;
using SmClient.Customizer;

namespace SmClient.FrontEnd;

/// <summary>
/// Phase 4b: the rows of the concept's Options page (panel 04). Every row is laid out on the concept's own grid (row pitch 31.2, label column from x 128, columns 284 apart, the concept's label lettering size and its
/// own slider, switch and pill art), in two columns when the labels fit and one when they do not; the page scrolls inside the frame as before. All numbers are the concept's screen pixels times the window's scale.
/// </summary>
public sealed partial class OptionsView
{
    /// <summary>The concept's row geometry (screen pixels): content left, column width and stride, the content's width, the first row's top below the rows area.</summary>
    /// <summary>The Options rows are a little tighter than the concept's 31.2 so the RENDERER line fits under the twelve controls without a scroll bar (Opus review 1).</summary>
    const float RowPitch = 29f;
    const float RowsLeft = 8f, ColWidth = 272f, ColStride = 284f, ContentWidth = 542f, RowsTop = 4.4f, SliderValueWidth = 32f;

    /// <summary>The last layout the rows were given (the comparison tool and the self-test read it): columns, label column and control width in UI pixels.</summary>
    public (int Columns, float LabelWidth, float ColumnWidth) RowLayout { get; private set; }

    void StyleRows(float s)
    {
        if (!_concept || Mathf.IsEqualApprox(_rowsScale, s)) return;
        _rowsScale = s;
        var rows = _grid.GetChildren().OfType<Control>().ToList();
        float LabelOf(Control r) => r switch { UiKit.MenuChoice c => c.ConceptLabelWidth(s), UiKit.MenuSlider sl => sl.ConceptLabelWidth(s), _ => 0f };
        float CtlNeed(Control r) => r switch { UiKit.MenuChoice c => c.ConceptControlWidth(s), UiKit.MenuSlider => (60f + SliderValueWidth + 4f) * s, _ => 0f };
        // Two columns when both fit the rows area. Each column has its own label width (the concept's first is 104 wide) and the control width its widest row needs; the second column starts at the concept's stride (284)
        // when it leaves room, else as far left as the first column allows.
        float[] Col(int n) => Enumerable.Range(0, n).Select(c => Mathf.Max(104f * s, rows.Where((_, i) => i % n == c).Select(LabelOf).DefaultIfEmpty(0f).Max() + 6f * s)).ToArray();
        float[] Need(int n, float[] lab) => Enumerable.Range(0, n).Select(c => lab[c] + rows.Where((_, i) => i % n == c).Select(CtlNeed).DefaultIfEmpty(0f).Max()).ToArray();
        var two2 = Col(2); var need2 = Need(2, two2);
        float avail = (ContentWidth - RowsLeft - 6f) * s, gap = 8f * s;
        bool two = rows.Count > 1 && need2[0] + gap + need2[1] <= avail;
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--dump-rows") >= 0) GD.Print($"options rows: need {need2[0] / s:0.0} + {need2[1] / s:0.0} + gap vs {avail / s:0.0}; labels {two2[0] / s:0.0} {two2[1] / s:0.0}");
        float stride = two ? Mathf.Clamp(avail - need2[1], need2[0] + gap, ColStride * s) : 0f;
        var labelWs = two ? two2 : [Mathf.Max(Col(1)[0], 150f * s)];
        var colWs = two ? new[] { need2[0], Mathf.Min(need2[1], avail - stride) } : [(ContentWidth - RowsLeft - 12f) * s];
        _grid.Columns = two ? 2 : 1;
        _grid.AddThemeConstantOverride("h_separation", two ? Mathf.RoundToInt(stride - colWs[0]) : 0); _grid.AddThemeConstantOverride("v_separation", 0);
        _rowsMargin.AddThemeConstantOverride("margin_left", Mathf.RoundToInt(RowsLeft * s)); _rowsMargin.AddThemeConstantOverride("margin_top", Mathf.RoundToInt(RowsTop * s));
        _rowsMargin.AddThemeConstantOverride("margin_right", 0); _rowsMargin.AddThemeConstantOverride("margin_bottom", Mathf.RoundToInt(6f * s));
        RowLayout = (two ? 2 : 1, labelWs[0], colWs[0]);
        // One pill width, one slider width and one start x for every control of a column (Opus review 1): the widest pill the tab needs, a track as long as the tightest column leaves.
        float pillW = rows.OfType<UiKit.MenuChoice>().Select(c => c.ConceptControlWidth(s)).DefaultIfEmpty(0f).Max();
        float trackW = (two ? 140f : 220f) * s;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; float labelW = labelWs[i % labelWs.Length], colW = colWs[i % colWs.Length], ctlW = colW - labelW;
            r.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            switch (r)
            {
                case UiKit.MenuChoice c: c.ConceptPitch = RowPitch; c.FixedPill = true; c.UseConcept(s, labelW, Mathf.Min(ctlW, Mathf.Max(pillW, 117f * s))); break;
                case UiKit.MenuSlider sl:
                    float value = (sl.Slider.Name == "FovSlider" ? 88f : SliderValueWidth) * s;
                    sl.ConceptPitch = RowPitch; sl.UseConcept(s, labelW, Mathf.Min(trackW, Mathf.Max(56f * s, ctlW - value - 8f * s)), value);
                    break;
            }
            r.CustomMinimumSize = new Vector2(colW, RowPitch * s);
        }
        float labelW0 = labelWs[0];
        foreach (var w in _wide.GetChildren().OfType<Control>()) StyleWide(w, s, labelW0);
        _grid.UpdateMinimumSize(); _wide.UpdateMinimumSize();
        DumpRows();
    }

    /// <summary>Screenshot and measuring aid (<c>--dump-rows &lt;file&gt;</c>): once the page has settled, writes each row control's rectangle in the concept's screen pixels (kind, label, x, y, w, h), for <c>--ui-compare-panel ... --rows</c>.</summary>
    void DumpRows()
    {
        var args = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(args, "--dump-rows");
        if (i < 0 || i + 1 >= args.Length || GetTree() is not { } tree) return;
        string path = args[i + 1];
        tree.CreateTimer(1.2).Timeout += () =>
        {
            if (!IsInstanceValid(this)) return;
            var (s, o) = PanelMap();   // written in v2 pixels (the reference's own)
            Rect2 Sc(Control row, Rect2 local) { var p = (row.GlobalPosition - GlobalPosition + local.Position - o) / (s * UiTextureBuilder.GridPerV2); return new Rect2(p, local.Size / (s * UiTextureBuilder.GridPerV2)); }
            var lines = new System.Collections.Generic.List<string>();
            foreach (var r in _grid.GetChildren().OfType<Control>())
            {
                switch (r)
                {
                    case UiKit.MenuChoice c: { var q = Sc(c, c.ConceptControlRect); lines.Add(FormattableString.Invariant($"{(c.Values.Count == 2 && c.Values[0] == "OFF" ? "switch" + c.Index : "pill")}|{c.Text}|{q.Position.X:0.##}|{q.Position.Y:0.##}|{q.Size.X:0.##}|{q.Size.Y:0.##}")); break; }
                    case UiKit.MenuSlider sl: { var q = Sc(sl, sl.ConceptTrackRect); lines.Add(FormattableString.Invariant($"slider|{sl.LabelText}|{q.Position.X:0.##}|{q.Position.Y:0.##}|{q.Size.X:0.##}|{q.Size.Y:0.##}")); break; }
                }
            }
            System.IO.File.WriteAllLines(path, lines);
        };
    }

    /// <summary>The one lettering of the plates (tabs, APPLY, REVERT, BACK): the row face at <paramref name="size"/> (concept pixels), light on a dark plate, dark ink on gold (the pressed tab, the gold APPLY).</summary>
    static void PlateText(Button b, float s, float size, bool goldPressed, bool goldRest = false)
    {
        b.AddThemeFontOverride("font", UiKit.ConceptFont());
        b.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(size * s));
        b.AddThemeConstantOverride("outline_size", 0);
        var light = new Color(0.84f, 0.83f, 0.80f); var dark = new Color(0.10f, 0.06f, 0.02f);
        foreach (var c in new[] { "font_color", "font_hover_color", "font_focus_color", "font_disabled_color" }) b.AddThemeColorOverride(c, goldRest ? dark : light);
        foreach (var c in new[] { "font_pressed_color", "font_hover_pressed_color" }) b.AddThemeColorOverride(c, goldPressed ? dark : light);
    }

    /// <summary>A row under the columns: the RENDERER note in the row lettering, KEYBINDS as one of the concept's own plates.</summary>
    void StyleWide(Control w, float s, float labelW)
    {
        _wide.AddThemeConstantOverride("separation", 0);
        if (w is Button b)
        {
            b.CustomMinimumSize = new Vector2(Mathf.Max(190f * s, 150f * s), 26.5f * s);
            b.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            b.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(15.5f * s));
            if (ConceptArt.Get("opt-plate-blank") is not null && ConceptArt.Get("opt-plate-gold-blank") is not null)
            {
                PaintedButtons.Apply(b, "opt-plate-blank", "opt-plate-gold-blank", new PaintedButtons.Pad(3, 4, 3, 4), new PaintedButtons.Pad(3, 4, 3, 5), s, true, paintedText: false, noGlow: false);
                foreach (var c in new[] { "font_color", "font_hover_color", "font_focus_color" }) b.AddThemeColorOverride(c, new Color(0.84f, 0.83f, 0.80f));
                foreach (var c in new[] { "font_pressed_color", "font_hover_pressed_color" }) b.AddThemeColorOverride(c, new Color(0.10f, 0.06f, 0.02f));
            }
            return;
        }
        w.CustomMinimumSize = new Vector2(0f, RowPitch * s);
        foreach (var l in w.GetChildren().OfType<Label>())
        {
            bool caption = l.Text == "RENDERER";
            l.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt((caption ? UiKit.ConceptLabelFont : UiKit.ConceptValueFont) * s)); l.AddThemeFontOverride("font", UiKit.ConceptFont());
            l.VerticalAlignment = VerticalAlignment.Center;
            if (caption) { l.OffsetRight = labelW; l.AddThemeColorOverride("font_color", new Color(0.93f, 0.92f, 0.89f)); }
            else { l.OffsetLeft = labelW; l.AutowrapMode = TextServer.AutowrapMode.Off; }
        }
    }
}
