using System;
using System.Collections.Generic;
using Godot;
using SmClient.Customizer;

namespace SmClient;

/// <summary>
/// The Tab scoreboard (the owner, 2026-11-07: "the scoreboard needs to look like the reference image except using only black and white colors ... remove any unimportant text that
/// isn't direct game statistics"). The reference's layout, drawn in black, white and greys only: each side's big score at the top with "VS" between them, a header row of the stats'
/// names, and a row per player: the player's icon (its shoulder emblem as it joined, grey), name, then the numbers. The own row has a subtle white accent. One column when the match
/// has one side (Musou Mode, free-for-all). Laid out on a 1920 x 1080 stage scaled to the window. What it shows comes in <see cref="Frame"/>, built by the networked view each frame.
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
        public List<Row> Rows = new();
    }

    public sealed class ScoreboardFrame
    {
        /// <summary>One column (Musou Mode, no teams): <see cref="Sides"/> has one entry and no "VS" is drawn.</summary>
        public bool Single;
        /// <summary>Show the big score over a single column (Musou: the side's total).</summary>
        public bool SingleScore = true;
        public string[] Headers = [];
        public List<Side> Sides = new();
    }

    /// <summary>What to draw (null: nothing).</summary>
    public ScoreboardFrame? Frame { get; set; }

    const float StageW = 1920f, StageH = 1080f;
    const float Top = 70f, ScoreBand = 150f, HeaderH = 46f, RowH = 64f, Gap = 24f, PairWidth = 1720f, SingleWidth = 1100f;
    const float IconSize = 48f, IconPad = 14f, NameX = IconPad + IconSize + 16f;

    // The steel palette (ui/overhaul): dark recessed rows in warm black, light steel text, gold for the own row and the "VS".
    static readonly Color Panel = new(0.03f, 0.03f, 0.03f, 0.9f), RowA = new(0.045f, 0.043f, 0.04f, 0.9f), RowB = new(0.075f, 0.072f, 0.066f, 0.9f),
        Rule = new(0.55f, 0.55f, 0.52f, 0.16f), White = Steel.Text, Dim = Steel.TextDim,
        MeFill = new(0.97f, 0.76f, 0.24f, 0.16f), MeBar = Steel.Gold;

    readonly System.Collections.Generic.Dictionary<(Steel.Kind, int), Steel.SteelStyle> _styles = new();

    readonly System.Collections.Generic.Dictionary<int, StyleBoxTexture> _plates = new();

    /// <summary>The concept's painted plate (rivets, bevel; the title plate's steel) stretched over <paramref name="r"/>, at the buttons' scale; the steel box when the art is missing.</summary>
    void PaintedPlate(Rect2 r)
    {
        int key = (int)MathF.Round(_scale * 100f);
        if (!_plates.TryGetValue(key, out var box))
        {
            float k = 0.62f * _scale;
            var art = UiKit.ConceptScaled("opt-plate-blank", k);
            if (art is null) { Box(Steel.Kind.Plate, r, 14f); return; }
            box = _plates[key] = new StyleBoxTexture { Texture = art, TextureMarginLeft = 70f * k, TextureMarginRight = 70f * k, TextureMarginTop = 50f * k, TextureMarginBottom = 50f * k, DrawCenter = true };
        }
        box.Draw(GetCanvasItem(), r);
    }

    /// <summary>A steel box (cached per kind and scale); its edge and chamfer follow the stage's scale.</summary>
    void Box(Steel.Kind kind, Rect2 r, float chamfer)
    {
        var key = (kind, (int)MathF.Round(_scale * 100f));
        if (!_styles.TryGetValue(key, out var style)) _styles[key] = style = new Steel.SteelStyle(kind, MathF.Max(2f, chamfer * _scale), 0f, 0f);
        style.Draw(GetCanvasItem(), r);
    }

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

        // The score band: the big scores on steel plates, "VS" between them.
        if (pair)
        {
            PaintedPlate(R(left0, Top, width, ScoreBand));
            PaintedPlate(R(left0 + width + Gap, Top, width, ScoreBand));
            Text(_bold, f.Sides[0].Score.ToString(), left0 + width / 2f, Top + ScoreBand / 2f, 104, White, 0);
            Text(_bold, f.Sides[1].Score.ToString(), left0 + width + Gap + width / 2f, Top + ScoreBand / 2f, 104, White, 0);
            Text(_bold, "VS", StageW / 2f, Top + ScoreBand / 2f, 70, Steel.Gold, 0);
        }
        else
        {
            PaintedPlate(R(left0, Top, totalW, ScoreBand));
            if (f.SingleScore) Text(_bold, f.Sides[0].Score.ToString(), StageW / 2f, Top + ScoreBand / 2f, 104, White, 0);
        }

        float y0 = Top + ScoreBand + 14f;
        for (int s = 0; s < (pair ? 2 : 1); s++)
        {
            float x0 = left0 + s * (width + Gap);
            DrawColumn(f, f.Sides[s], x0, y0, width, bodyH);
        }
    }

    void DrawColumn(ScoreboardFrame f, Side side, float x0, float y0, float width, float bodyH)
    {
        Box(Steel.Kind.Recess, R(x0 - 6f, y0 - 6f, width + 12f, bodyH + 12f), 10f);
        DrawRect(R(x0, y0, width, bodyH), Panel);
        // The header row: the stats' names over their columns (no header over the names), on a dark steel strip.
        Box(Steel.Kind.Dark, R(x0 + 2f, y0 + 2f, width - 4f, HeaderH - 2f), 6f);
        int cells = f.Headers.Length;
        float statsW = width - NameX - 250f;
        float cellW = cells > 0 ? MathF.Min(118f, statsW / cells) : 0f;
        float CellX(int i) => x0 + width - 20f - (cells - i - 0.5f) * cellW;
        for (int i = 0; i < cells; i++) Text(_regular!, f.Headers[i], CellX(i), y0 + HeaderH / 2f, 22, Steel.GoldHi, 0);

        float y = y0 + HeaderH;
        for (int r = 0; r < side.Rows.Count; r++, y += RowH)
        {
            var row = side.Rows[r];
            DrawRect(R(x0 + 2f, y, width - 4f, RowH), r % 2 == 0 ? RowA : RowB);
            DrawLine(S(x0 + 2f, y), S(x0 + width - 2f, y), Rule, 1f);
            if (row.Me)
            {
                DrawRect(R(x0 + 2f, y, width - 4f, RowH), MeFill);
                DrawRect(R(x0 + 2f, y, 5f, RowH), MeBar);
            }
            // The icon: the emblem in grey, in a thin steel frame; an empty frame while its picture is not here.
            var iconRect = R(x0 + IconPad, y + (RowH - IconSize) / 2f, IconSize, IconSize);
            DrawRect(iconRect, new Color(0.10f, 0.10f, 0.10f, 1f));
            if (row.Icon is { } icon) DrawTextureRect(icon, iconRect, false);
            DrawRect(iconRect, Steel.SteelMid, false, MathF.Max(1f, 1.5f * _scale));
            float nameRoom = width - NameX - cells * cellW - 30f;
            var ink = row.Me ? Steel.GoldHi : White;
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
