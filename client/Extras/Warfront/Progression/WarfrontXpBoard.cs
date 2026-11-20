using System;
using Godot;
using SmClient.Customizer;

namespace SmClient.Extras.Warfront.Progression;

/// <summary>
/// Warfront's level, XP and card counts as a section of the Tab scoreboard (owner, 2026-11-17: the always-on box was "quite invasive"; it "should be integrated into the scoreboard and matched to blend in"). Drawn with the
/// scoreboard's own pieces (<see cref="BoardStyle"/> through <see cref="WarfrontStyle"/>: the recessed steel table, the dark header strip, warm-black rows, the concept lettering and the sizes of the one table), in the 1920 by 1080
/// design space, at the scoreboard's single-column width, directly under its last row. Everything is painted with one font (no live labels mixed in). Shown only while the scoreboard is up (<see cref="Shown"/>, set by the network
/// view's frame hook) and once Progress has arrived. Layout ours, mirroring <c>ScoreboardView</c>'s constants.
/// </summary>
public sealed partial class WarfrontXpBoard : Control
{
    // ScoreboardView's constants (client/ScoreboardView.cs): kept equal so the section lines up with the table above it.
    const float StageW = 1920f, SingleWidth = 1100f, Top = 70f, ScoreBand = 150f, HeaderH = 46f, RowH = 64f, IconPad = 14f, Gap = 24f;

    readonly WarfrontProgressModel _model;
    readonly FontVariation _font = UiKit.ConceptFont();

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
    }

    public override void _Process(double delta) { if (Visible) QueueRedraw(); }

    public override void _Draw()
    {
        if (!_shown || !_model.HasProgress) return;
        var m = _model;
        float x0 = (StageW - SingleWidth) / 2f, w = SingleWidth;
        float y0 = Top + ScoreBand + 14f + HeaderH + Math.Max(1, PlayerRows) * RowH + Gap;   // under the scoreboard's last row
        float h = HeaderH + RowH + HeaderH + RowH;
        var board = WarfrontStyle.Board;
        WarfrontStyle.Table(this, new Rect2(x0, y0, w, h));

        // Header: the section's name and the picks waiting, on the dark steel strip (the table's header).
        WarfrontStyle.Strip(this, new Rect2(x0, y0, w, HeaderH));
        Text("PROGRESSION", x0 + IconPad, y0 + HeaderH / 2f, WarfrontStyle.Body, BoardStyle.Header, -1);
        if (m.Pending > 0) Text(m.Pending == 1 ? "1 CARD TO PICK" : $"{m.Pending} CARDS TO PICK", x0 + w - 20f, y0 + HeaderH / 2f, WarfrontStyle.Body, BoardStyle.Header, 1);

        // Row: LEVEL n, the XP bar, the numbers.
        float y = y0 + HeaderH;
        board.Row(this, new Rect2(x0, y, w, RowH), false, false, 1f);
        Text(m.LevelText, x0 + IconPad, y + RowH / 2f, WarfrontStyle.Heading, BoardStyle.Text, -1);
        string xp = m.XpText;
        float xpW = _font.GetStringSize(xp, HorizontalAlignment.Left, -1, WarfrontStyle.Heading).X;
        float barX = x0 + 190f, barRight = x0 + w - 20f - xpW - 24f;
        WarfrontProgressDraw.Bar(this, new Rect2(barX, y + (RowH - 20f) / 2f, barRight - barX, 20f), m.XpFraction, BoardStyle.Gold);
        Text(xp, x0 + w - 20f, y + RowH / 2f, WarfrontStyle.Heading, BoardStyle.Text, 1);

        // The six counters as a small table: names in a header strip, counts under them.
        y += RowH;
        WarfrontStyle.Strip(this, new Rect2(x0, y, w, HeaderH));
        float cell = (w - 40f) / 6f;
        for (int i = 0; i < 6; i++) Text(WarfrontProgressModel.CounterLabels[i], x0 + 20f + (i + 0.5f) * cell, y + HeaderH / 2f, WarfrontStyle.Body, BoardStyle.Header, 0);
        y += HeaderH;
        board.Row(this, new Rect2(x0, y, w, RowH), true, false, 1f);
        for (int i = 0; i < 6; i++)
            Text((i < 5 ? m.CardCounts[i] : m.Retainers).ToString(), x0 + 20f + (i + 0.5f) * cell, y + RowH / 2f, WarfrontStyle.Heading, BoardStyle.Text, 0);
    }

    /// <summary>Text centred vertically on <paramref name="cy"/>; <paramref name="align"/> -1 left, 0 centre, 1 right (as the scoreboard's).</summary>
    void Text(string text, float x, float cy, int size, Color colour, int align)
    {
        if (text.Length == 0) return;
        float tw = _font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        float left = align < 0 ? x : align == 0 ? x - tw / 2f : x - tw;
        DrawString(_font, new Vector2(MathF.Round(left), MathF.Round(cy + (_font.GetAscent(size) - _font.GetDescent(size)) / 2f)), text, HorizontalAlignment.Left, -1, size, colour);
    }
}