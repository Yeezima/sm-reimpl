using System;
using System.Collections.Generic;
using Godot;
using SmClient.Customizer;

namespace SmClient;

/// <summary>
/// The Tab scoreboard of every mode. Its first form (the owner, 2026-11-07: "the scoreboard needs to look like the reference image except using only black and white colors ... remove any unimportant text that
/// isn't direct game statistics") was black, white and grey; the owner's request of 2026-11-18 ("make all the smaller ui elements match the existing UI overhaul work - scoreboard, musou/warfront mode UI") supersedes the
/// colour rule: the board now wears the overhaul's steel look (<see cref="BoardStyle"/>: painted score plates, a steel table, warm-black rows, light steel text) with gold only for the headers, the "VS" and the own row.
/// The layout, the columns and the numbers are the 2026-11-07 ones: each side's big score at the top (with the side's name and, when its picture is here, its emblem) and "VS" between them, a header row of the
/// stats' names, and a row per player: the player's icon (its shoulder emblem as it joined, grey), name, then the numbers. One column when the match has one side (Musou Mode, free-for-all), with a label
/// over its total. Laid out on a 1920 x 1080 stage scaled to the window. What it shows comes in <see cref="Frame"/>, built by the networked view each frame.
/// </summary>
public sealed partial class ScoreboardView : Control
{
    public sealed class Row
    {
        public string Name = "";
        public Texture2D? Icon;
        public string[] Cells = [];
        public bool Me;
    }

    public sealed class Side
    {
        public int Score;
        /// <summary>The side's name on its score plate (SPACE MARINES, CHAOS) and its emblem when the picture is here.</summary>
        public string Name = "";
        public Texture2D? Emblem;
        public List<Row> Rows = new();
    }

    public sealed class ScoreboardFrame
    {
        /// <summary>One column (Musou Mode, no teams): <see cref="Sides"/> has one entry and no "VS" is drawn.</summary>
        public bool Single;
        /// <summary>Show the big score over a single column (Musou: the side's total).</summary>
        public bool SingleScore = true;
        public string[] Headers = [];
        /// <summary>The words over the single column's total (RUN SCORE in Musou Mode); empty draws none.</summary>
        public string SingleLabel = "";
        public List<Side> Sides = new();
    }

    /// <summary>What to draw (null: nothing).</summary>
    public ScoreboardFrame? Frame { get; set; }

    const float StageW = 1920f, StageH = 1080f;
    const float Top = 70f, ScoreBand = 150f, HeaderH = 46f, RowH = 64f, Gap = 24f, PairWidth = 1720f, SingleWidth = 1100f;
    const float IconSize = 48f, IconPad = 14f, NameX = IconPad + IconSize + 16f;

    // The steel look (ui/overhaul) is BoardStyle's: the same plates, panels, header strips and rows for every board drawn on a stage.
    static readonly Color White = BoardStyle.Text, Dim = BoardStyle.Dim;
    readonly BoardStyle _style = new();

    FontVariation? _regular, _bold;

    public ScoreboardView()
    {
        Name = "Scoreboard";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        TextureFilter = TextureFilterEnum.Linear;
    }

    public override void _Ready()
    {
        // The menus' lettering (ui/overhaul): Oswald, the face of every redesigned screen; the HUD keeps its own.
        _regular = UiKit.ConceptFont();
        _bold = UiKit.ConceptFont();
    }

    public override void _Process(double delta) => QueueRedraw();

    float _scale = 1f;
    Vector2 _origin;

    Vector2 S(float x, float y) => _origin + new Vector2(x, y) * _scale;
    Rect2 R(float x, float y, float w, float h) => new(S(x, y), new Vector2(w, h) * _scale);

    public override void _Draw()
    {
        if (Frame is not { Sides.Count: > 0 } f || _regular is null || _bold is null) return;
        var size = GetViewportRect().Size;
        _scale = MathF.Min(size.X / StageW, size.Y / StageH);
        _origin = (size - new Vector2(StageW, StageH) * _scale) / 2f;

        bool pair = !f.Single && f.Sides.Count >= 2;
        float width = pair ? (PairWidth - Gap) / 2f : SingleWidth;
        float left0 = pair ? (StageW - PairWidth) / 2f : (StageW - SingleWidth) / 2f;
        int most = 0;
        foreach (var side in f.Sides) most = Math.Max(most, side.Rows.Count);
        float bodyH = HeaderH + Math.Max(1, most) * RowH;
        float totalW = pair ? PairWidth : SingleWidth;

        // The score band: each side's score on a steel plate under its name (and emblem), "VS" between them.
        float plateW = MathF.Min(width, 600f);
        if (pair)
        {
            for (int k = 0; k < 2; k++)
            {
                float cx = left0 + k * (width + Gap) + width / 2f;
                ScorePlate(cx, plateW, f.Sides[k].Name, f.Sides[k].Emblem, f.Sides[k].Score.ToString());
            }
            Text(_bold, "VS", StageW / 2f, Top + ScoreBand / 2f + 10f, 70, BoardStyle.Gold, 0);
        }
        else if (f.SingleScore) ScorePlate(StageW / 2f, MathF.Min(totalW, 600f), f.SingleLabel, null, f.Sides[0].Score.ToString());
        else _style.Plate(this, R(left0, Top, totalW, ScoreBand * 0.5f), _scale);

        float y0 = Top + ScoreBand + 14f;
        for (int s = 0; s < (pair ? 2 : 1); s++)
        {
            float x0 = left0 + s * (width + Gap);
            DrawColumn(f, f.Sides[s], x0, y0, width, bodyH);
        }
    }

    /// <summary>A score on the concept's plate: the name (small, gold) and the emblem at the top, the number big under them.</summary>
    void ScorePlate(float cx, float w, string name, Texture2D? emblem, string score)
    {
        _style.Plate(this, R(cx - w / 2f, Top, w, ScoreBand), _scale);
        bool named = name.Length > 0;
        if (named) Text(_regular!, name, cx + (emblem is null ? 0f : 28f), Top + 30f, 28, BoardStyle.Header, 0);
        if (emblem is not null && named)
        {
            float tw = _regular!.GetStringSize(name, HorizontalAlignment.Left, -1, Math.Max(8, (int)MathF.Round(28 * _scale))).X / _scale;
            var r = R(cx - tw / 2f - 30f, Top + 12f, 36f, 36f);
            DrawTextureRect(emblem, r, false);
        }
        Text(_bold, score, cx, Top + (named ? 98f : ScoreBand / 2f), 96, White, 0);
    }

    void DrawColumn(ScoreboardFrame f, Side side, float x0, float y0, float width, float bodyH)
    {
        _style.Table(this, R(x0, y0, width, bodyH), _scale);
        // The header row: the stats' names over their columns (no header over the names), on a dark steel strip.
        _style.HeaderStrip(this, R(x0, y0, width, HeaderH), _scale);
        int cells = f.Headers.Length;
        float statsW = width - NameX - 250f;
        float cellW = cells > 0 ? MathF.Min(118f, statsW / cells) : 0f;
        float CellX(int i) => x0 + width - 20f - (cells - i - 0.5f) * cellW;
        for (int i = 0; i < cells; i++) Text(_regular!, f.Headers[i], CellX(i), y0 + HeaderH / 2f, 22, BoardStyle.Header, 0);

        float y = y0 + HeaderH;
        for (int r = 0; r < side.Rows.Count; r++, y += RowH)
        {
            var row = side.Rows[r];
            _style.Row(this, R(x0, y, width, RowH), r % 2 == 1, row.Me, _scale);
            // The icon: the emblem in grey, in a thin steel frame; an empty frame while its picture is not here.
            var iconRect = R(x0 + IconPad, y + (RowH - IconSize) / 2f, IconSize, IconSize);
            DrawRect(iconRect, new Color(0.10f, 0.10f, 0.10f, 1f));
            if (row.Icon is { } icon) DrawTextureRect(icon, iconRect, false);
            DrawRect(iconRect, Steel.SteelMid, false, MathF.Max(1f, 1.5f * _scale));
            float nameRoom = width - NameX - cells * cellW - 30f;
            var ink = BoardStyle.Ink(row.Me);
            Text(_regular!, Fit(row.Name, nameRoom, 28), x0 + NameX, y + RowH / 2f, 28, ink, -1);
            for (int i = 0; i < cells && i < row.Cells.Length; i++) Text(_regular!, row.Cells[i], CellX(i), y + RowH / 2f, 28, ink, 0);
        }
    }

    /// <summary>The name shortened with "..." to fit <paramref name="room"/> stage pixels.</summary>
    string Fit(string text, float room, int size)
    {
        if (Width(_regular!, text, size) <= room) return text;
        for (int n = text.Length - 1; n > 1; n--)
            if (Width(_regular!, text[..n] + "...", size) <= room) return text[..n] + "...";
        return text;
    }

    float Width(Font font, string text, int size) => font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;

    /// <summary>Text centred vertically on <paramref name="cy"/> (stage pixels); <paramref name="align"/> -1 left, 0 centre, 1 right.</summary>
    void Text(Font font, string text, float x, float cy, int size, Color colour, int align)
    {
        if (text.Length == 0) return;
        int px = Math.Max(8, (int)MathF.Round(size * _scale));
        float w = font.GetStringSize(text, HorizontalAlignment.Left, -1, px).X;
        float ascent = font.GetAscent(px), descent = font.GetDescent(px);
        var at = S(x, cy);
        float left = align < 0 ? at.X : align == 0 ? at.X - w / 2f : at.X - w;
        DrawString(font, new Vector2(left, at.Y + (ascent - descent) / 2f), text, HorizontalAlignment.Left, -1, px, colour);
    }
}
