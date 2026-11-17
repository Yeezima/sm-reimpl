using System;
using Godot;
using SmClient.Customizer;

namespace SmClient.Extras.Warfront.Progression;

/// <summary>
/// Warfront's level, XP and card counts as a section of the Tab scoreboard (owner, 2026-11-17: the always-on box was "quite invasive"; it "should be integrated into the scoreboard and matched to blend in"). Drawn in the
/// scoreboard's own style, in the 1920 by 1080 design space: the same black panel, header rows (grey fill, dim 22 px names), table rows (alternating dark rows, a faint rule, white 26 px text) and the same Bahnschrift face, at the
/// scoreboard's single-column width, directly under its last row. Everything is painted with one font function (no live labels mixed in); frames are four whole-pixel rectangles. Shown only while the scoreboard is up
/// (<see cref="Shown"/>, set by the network view's frame hook) and once Progress has arrived. Layout ours, mirroring <c>ScoreboardView</c>'s constants.
/// </summary>
public sealed partial class WarfrontXpBoard : Control
{
    // ScoreboardView's constants (client/ScoreboardView.cs): kept equal so the section lines up with the table above it.
    const float StageW = 1920f, SingleWidth = 1100f, Top = 70f, ScoreBand = 150f, HeaderH = 46f, RowH = 64f, IconPad = 14f, Gap = 16f;
    const int HeaderSize = 22, RowSize = 26;
    static readonly Color Panel = new(0f, 0f, 0f, 0.82f), RowA = new(0.04f, 0.04f, 0.04f, 0.86f), RowB = new(0.09f, 0.09f, 0.09f, 0.86f),
        HeaderFill = new(0.16f, 0.16f, 0.16f, 0.92f), Rule = new(1f, 1f, 1f, 0.14f), White = Colors.White, Dim = new(1f, 1f, 1f, 0.62f), Edge = new(1f, 1f, 1f, 0.55f);

    readonly WarfrontProgressModel _model;
    readonly FontVariation _regular, _bold;

    /// <summary>The scoreboard is up.</summary>
    public bool Shown { get => _shown; set { if (_shown == value) return; _shown = value; Visible = value && _model.HasProgress; QueueRedraw(); } }
    bool _shown;
    /// <summary>The scoreboard's player rows (sets how far down the section sits).</summary>
    public int PlayerRows { get; set; } = 1;

    public WarfrontXpBoard(WarfrontProgressModel model)
    {
        Name = "WarfrontXpBoard";
        _model = model;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.Linear;
        Visible = false;
        model.Changed += () => { Visible = _shown && _model.HasProgress; QueueRedraw(); };
        var system = new SystemFont { FontNames = ["Bahnschrift", "DIN Alternate", "Arial Narrow"], Fallbacks = [ThemeDB.FallbackFont] };
        var ts = TextServerManager.GetPrimaryInterface();
        FontVariation Variant(int weight, int width) => new()
        {
            BaseFont = system,
            VariationOpentype = new Godot.Collections.Dictionary { { ts.NameToTag("wght"), weight }, { ts.NameToTag("wdth"), width } },
        };
        _regular = Variant(430, 92);
        _bold = Variant(650, 90);
    }

    public override void _Process(double delta) { if (Visible) QueueRedraw(); }

    public override void _Draw()
    {
        if (!_shown || !_model.HasProgress) return;
        var m = _model;
        float x0 = (StageW - SingleWidth) / 2f, w = SingleWidth;
        float y0 = Top + ScoreBand + 8f + HeaderH + Math.Max(1, PlayerRows) * RowH + Gap;   // under the scoreboard's last row
        float h = HeaderH + RowH + HeaderH + RowH;
        DrawRect(new Rect2(x0, y0, w, h), Panel);

        // Header: the section's name (dim, like the table's stat names) and the picks waiting.
        DrawRect(new Rect2(x0, y0, w, HeaderH), HeaderFill);
        Text(_regular, "PROGRESSION", x0 + IconPad, y0 + HeaderH / 2f, HeaderSize, Dim, -1);
        if (m.Pending > 0) Text(_regular, m.Pending == 1 ? "1 CARD TO PICK" : $"{m.Pending} CARDS TO PICK", x0 + w - 20f, y0 + HeaderH / 2f, HeaderSize, Dim, 1);

        // Row: LEVEL n, the XP bar, the numbers.
        float y = y0 + HeaderH;
        DrawRect(new Rect2(x0, y, w, RowH), RowA);
        DrawLine(new Vector2(x0, y), new Vector2(x0 + w, y), Rule, 1f);
        Text(_bold, m.LevelText, x0 + IconPad, y + RowH / 2f, RowSize, White, -1);
        string xp = m.XpText;
        float xpW = _regular.GetStringSize(xp, HorizontalAlignment.Left, -1, RowSize).X;
        float barX = x0 + 190f, barRight = x0 + w - 20f - xpW - 24f;
        var bar = new Rect2(barX, y + (RowH - 16f) / 2f, barRight - barX, 16f);
        DrawRect(bar, new Color(0.18f, 0.18f, 0.18f, 1f));
        float fill = Mathf.Floor((bar.Size.X - 4f) * Mathf.Clamp(m.XpFraction, 0f, 1f));
        if (fill > 0f) DrawRect(new Rect2(bar.Position.X + 2f, bar.Position.Y + 2f, fill, bar.Size.Y - 4f), new Color(1f, 1f, 1f, 0.9f));
        WarfrontProgressDraw.Frame(this, bar, 2f, Edge);
        Text(_regular, xp, x0 + w - 20f, y + RowH / 2f, RowSize, White, 1);

        // The six counters as a small table: names in a header row, counts under them.
        y += RowH;
        DrawRect(new Rect2(x0, y, w, HeaderH), HeaderFill);
        float cell = (w - 40f) / 6f;
        for (int i = 0; i < 6; i++) Text(_regular, WarfrontProgressModel.CounterLabels[i], x0 + 20f + (i + 0.5f) * cell, y + HeaderH / 2f, HeaderSize, Dim, 0);
        y += HeaderH;
        DrawRect(new Rect2(x0, y, w, RowH), RowB);
        DrawLine(new Vector2(x0, y), new Vector2(x0 + w, y), Rule, 1f);
        for (int i = 0; i < 6; i++)
            Text(_regular, (i < 5 ? m.CardCounts[i] : m.Retainers).ToString(), x0 + 20f + (i + 0.5f) * cell, y + RowH / 2f, RowSize, White, 0);
    }

    /// <summary>Text centred vertically on <paramref name="cy"/>; <paramref name="align"/> -1 left, 0 centre, 1 right (as the scoreboard's).</summary>
    void Text(Font font, string text, float x, float cy, int size, Color colour, int align)
    {
        if (text.Length == 0) return;
        float tw = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        float left = align < 0 ? x : align == 0 ? x - tw / 2f : x - tw;
        DrawString(font, new Vector2(MathF.Round(left), MathF.Round(cy + (font.GetAscent(size) - font.GetDescent(size)) / 2f)), text, HorizontalAlignment.Left, -1, size, colour);
    }
}
