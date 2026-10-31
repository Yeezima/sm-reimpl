using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmFormats.Gfx;

namespace SmClient;

// ui/score-box-center (owner, 2026-10-31): the score panel's opaque box holds the scores, the bars, the time left and the score limit as one block centred in it with even padding,
// and nothing touches or crosses its edges. Everything here is in the panel's own frame (movie pixels of HudText's mp_score_mc).
public sealed partial class Hud
{
    /// <summary>Even padding between the block and the box (movie pixels; ours), the left edge's slant (as before, 12) and the least clearance the fit check demands.</summary>
    const float ScoreBoxPadX = 20f, ScoreBoxPadY = 10f, ScoreBoxSlant = 12f;

    /// <summary>How far (movie pixels) the Seize Ground markers are lifted so that they stand above the box's top edge instead of crossing it (measured in a screenshot; ours).</summary>
    const float ScoreMarkerLift = 19f;

    /// <summary>How much nearer the slanted left edge is than its middle at the block's corners (add it to the left gap to compare with the right gap).</summary>
    public const float ScoreBoxHalfSlant = ScoreBoxSlant / 2f;

    /// <summary>The strings the box is sized for (a timer such as 8:16, a two-digit limit and two-digit scores); longer ones (10:00, three digits) are centred just the same and shrink the padding a little.</summary>
    const string ScoreNominalTimer = "8:88", ScoreNominalLimit = "88", ScoreNominalScore = "88";

    /// <summary>The measured layout of the box and its block (panel frame).</summary>
    readonly record struct ScoreBoxLayout(
        float BoxLeft, float BoxRight, float BoxTop, float BoxBottom,   // the box: left edge's middle, right edge, top and bottom
        Rect2 Content,                                                  // the block before it is moved (scores, bars, column)
        Vector2 Shift,                                                  // what centres the block in the box
        float ColumnCentre, float TimerBase, float LimitBase, int TimerSize, int LimitSize, int TimerStyle, int LimitStyle);

    /// <summary><c>--old-score-box</c> (before / after aid): the box and the texts as they were (the timer and the limit at the movie's own places, past the box's right edge).</summary>
    static readonly bool OldScoreBox = Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-score-box") >= 0;

    Rect2? _scoreBars;

    /// <summary>The two bars' extent in the panel's frame at full fill (their pictures' bounds), measured once; null while they are being built.</summary>
    Rect2? ScoreBarsLocal(HudMovie text)
    {
        if (_scoreBars is { } known) return known;
        if (_movieArt is null) return null;
        if (text.Find("mp_score_mc") is not { } panel) return null;
        int ownSprite = text.Find("mp_score_mc/sm_bar_mc")?.Placement.Character ?? -1, enemySprite = text.Find("mp_score_mc/chaos_bar_mc")?.Placement.Character ?? -1;
        if (ownSprite < 0 || enemySprite < 0) return null;
        Rect2? union = null;
        bool pending = false;
        text.Walk(panel.Placement.Character, 0, (path, _) => path is "sm_bar_mc" or "chaos_bar_mc" ? 100 : null, ch => ch == ownSprite || ch == enemySprite, item =>
        {
            if (item.Text is not null || item.Path is not ("sm_bar_mc" or "chaos_bar_mc")) return;
            string key = $"{text.Name}:{item.Character}:{item.Frame}";
            int ch = item.Character, fr = item.Frame;
            var pic = _movieArt.Get(key, (m, s) => Of(m, text.Name)?.Render(ch, fr, s));
            if (pic is null) { pending = true; return; }
            // The picture's drawn part (its alpha), not its canvas: the masked fill's clip reaches far beyond what shows.
            var img = pic.Normal.GetImage();
            int x0 = img.GetWidth(), y0 = img.GetHeight(), x1 = -1, y1 = -1;
            for (int y = 0; y < img.GetHeight(); y++)
                for (int x = 0; x < img.GetWidth(); x++)
                    if (img.GetPixel(x, y).A > 0.1f) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
            if (x1 < 0) return;
            var xf = ToXf(item.Matrix);
            var a = xf * ((new Vector2(x0, y0) - pic.Origin) / pic.RenderScale);
            var b = xf * ((new Vector2(x1 + 1, y1 + 1) - pic.Origin) / pic.RenderScale);
            var r = new Rect2(a, Vector2.Zero).Expand(b);
            union = union is { } u ? u.Merge(r) : r;
        });
        if (pending || union is null) return null;
        return _scoreBars = union;
    }

    /// <summary>A text field's glyph box for <paramref name="s"/> (movie pixels): width, the digits' top and bottom relative to the field's origin, and the font's size (virtual units) and style.</summary>
    (float Width, float Top, float Bottom, int Size, int Style) ScoreGlyphs(HudMovie text, GfxEditText t, string s)
    {
        int style = text.Movie.FontNames.GetValueOrDefault(t.FontId, "").Contains("Bold", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        int size = Math.Max(1, (int)MathF.Round((float)t.FontHeight * StageToVirtual));
        float ascent = (FontOf(style)?.GetAscent(size) ?? size * 0.8f) / StageToVirtual;
        float baseline = (float)t.Y0 + 2f + ascent;
        return (TextWidth(s, size, style) / StageToVirtual, baseline - 0.72f * size / StageToVirtual, baseline, size, style);   // digits' height 0.72 of the size (ours)
    }

    /// <summary>
    /// Lays the box out for the current scores, limit and clock: the block's extent (scores right-aligned in their fields, the bars, and the right column where the timer sits over the
    /// limit, both centred on one line), the box sized for the nominal block (two-digit scores, the widest timer and limit) plus even padding, and the shift that centres the actual block in it.
    /// Null while the bars are being built or the font is missing (the caller keeps the earlier drawing).
    /// </summary>
    ScoreBoxLayout? LayoutScoreBox(HudMovie text, int own, int enemy, int limit, string clock)
    {
        // Kept while the strings and the viewport size stay the same (the measuring allocates and measures text).
        var key = (own, enemy, limit, clock, Size.Y);
        if (_scoreLayoutKey == key && _scoreLayout is { } kept) return kept;
        var made = MeasureScoreBox(text, own.ToString(), enemy.ToString(), limit > 0 ? limit.ToString() : "-", clock);
        if (made is not null) { _scoreLayoutKey = key; _scoreLayout = made; }
        return made;
    }

    (int, int, int, string, float) _scoreLayoutKey;
    ScoreBoxLayout? _scoreLayout;

    ScoreBoxLayout? MeasureScoreBox(HudMovie text, string own, string enemy, string limit, string clock)
    {
        if (OldScoreBox || FontOf(2) is null) return null;
        if (text.Find("mp_score_mc") is not { } panel) return null;
        if (ScoreBarsLocal(text) is not { } bars) return null;
        var toPanel = ToXf(panel.ToStage).AffineInverse();
        if (text.Find("mp_score_mc/marine_kills") is not { } ownField || text.Find("mp_score_mc/chaos_kills") is not { } enemyField
            || text.Find("mp_score_mc/kill_number") is not { } limitField || text.Find("mp_score_mc/mp_timer_txt") is not { } timerField) return null;
        var ownT = text.Movie.EditTexts[ownField.Placement.Character]; var enemyT = text.Movie.EditTexts[enemyField.Placement.Character];
        var limitT = text.Movie.EditTexts[limitField.Placement.Character]; var timerT = text.Movie.EditTexts[timerField.Placement.Character];
        Vector2 At(GfxMatrix m) => toPanel * ToXf(m).Origin;
        var ownAt = At(ownField.ToStage); var enemyAt = At(enemyField.ToStage); var limitAt = At(limitField.ToStage); var timerAt = At(timerField.ToStage);

        // Scores: right-aligned in their fields.
        Rect2 Score(GfxEditText t, Vector2 at, string s)
        {
            var g = ScoreGlyphs(text, t, s);
            float right = at.X + (float)t.X1 - 2f;
            return new Rect2(right - g.Width, at.Y + g.Top, g.Width, g.Bottom - g.Top);
        }
        // The right column: left where the original's timer starts, the timer over the limit, both centred on the column's middle line.
        var timerNow = ScoreGlyphs(text, timerT, clock.Length > 0 ? clock : "0:00"); var limitNow = ScoreGlyphs(text, limitT, limit);
        float colLeft = timerAt.X + (float)timerT.X0 + 2f;
        float timerBase = timerAt.Y + timerNow.Bottom, limitBase = limitAt.Y + limitNow.Bottom;
        float timerTop = timerAt.Y + timerNow.Top, limitBottom = limitAt.Y + limitNow.Bottom;
        Rect2 Column(string tm, string lm)
        {
            float w = MathF.Max(ScoreGlyphs(text, timerT, tm).Width, ScoreGlyphs(text, limitT, lm).Width);
            return new Rect2(colLeft, timerTop, w, limitBottom - timerTop);
        }
        var column = Column(clock.Length > 0 ? clock : "0:00", limit);
        float colCentre = colLeft + column.Size.X / 2f;

        string ownS = own.Length > 0 ? own : "0", enemyS = enemy.Length > 0 ? enemy : "0";
        var actual = bars.Merge(column).Merge(Score(ownT, ownAt, ownS)).Merge(Score(enemyT, enemyAt, enemyS));
        // The nominal block: the same, with two-digit scores.
        var nominal = bars.Merge(Column(ScoreNominalTimer, ScoreNominalLimit)).Merge(Score(ownT, ownAt, ScoreNominalScore)).Merge(Score(enemyT, enemyAt, ScoreNominalScore));
        float left = nominal.Position.X - ScoreBoxPadX, right = nominal.End.X + ScoreBoxPadX, top = nominal.Position.Y - ScoreBoxPadY, bottom = nominal.End.Y + ScoreBoxPadY;
        var shift = new Vector2((left + right) / 2f - (actual.Position.X + actual.End.X) / 2f, (top + bottom) / 2f - (actual.Position.Y + actual.End.Y) / 2f);
        return new ScoreBoxLayout(left, right, top, bottom, actual, shift, colCentre, timerBase, limitBase, timerNow.Size, limitNow.Size, timerNow.Style, limitNow.Style);
    }

    /// <summary>The box's corners (panel frame): the left edge slants as it always did, around its middle.</summary>
    static Vector2[] ScoreBoxCorners(ScoreBoxLayout l) => [new(l.BoxLeft + ScoreBoxSlant / 2f, l.BoxTop), new(l.BoxRight, l.BoxTop), new(l.BoxRight, l.BoxBottom), new(l.BoxLeft - ScoreBoxSlant / 2f, l.BoxBottom)];

    /// <summary>
    /// The fit check (<c>--ui-selftest</c>): with the HUD's current data (scores, limit, clock), the gaps between the moved block and each edge of the box, in movie pixels, or null while
    /// the movie's pieces are not ready. The block is centred when the right gap equals the left gap taken at the slanted edge's middle (LeftMiddle) and the top gap the bottom one; Left is the nearer gap at the block's top or bottom corner.
    /// </summary>
    public (float Left, float Right, float Top, float Bottom, float LeftMiddle)? ScoreBoxGaps()
    {
        if (OldHud || _movieArt?.Movies?.Text is not { } text) return null;
        var d = Data;
        if (LayoutScoreBox(text, d.OwnScore, d.EnemyScore, d.ScoreLimit, d.Clock) is not { } l) return null;
        var c = new Rect2(l.Content.Position + l.Shift, l.Content.Size);
        float EdgeX(float y) => Mathf.Lerp(l.BoxLeft + ScoreBoxSlant / 2f, l.BoxLeft - ScoreBoxSlant / 2f, Mathf.Clamp((y - l.BoxTop) / (l.BoxBottom - l.BoxTop), 0f, 1f));
        float leftGap = MathF.Min(c.Position.X - EdgeX(c.Position.Y), c.Position.X - EdgeX(c.End.Y));
        return (leftGap, l.BoxRight - c.End.X, c.Position.Y - l.BoxTop, l.BoxBottom - c.End.Y, c.Position.X - l.BoxLeft);
    }

    /// <summary><c>--hud-score-demo own,enemy,limit,clock[,file.png]</c> (screenshot aid): shows these scores, limit and clock in the score panel; with a file, saves the window's picture once the panel is drawn, then logs <c>score box gaps:</c>.</summary>
    static readonly string[]? ScoreDemo = ReadScoreDemo();
    double _scoreDemoDrawn;
    bool _scoreDemoShot;

    static string[]? ReadScoreDemo()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--hud-score-demo");
        return i >= 0 && i + 1 < args.Length ? args[i + 1].Split(',', 5) : null;
    }

    void RunScoreDemo()
    {
        if (ScoreDemo is not { Length: >= 4 } a) return;
        var d = Data;
        if (!d.Teams) return;
        int.TryParse(a[0], out d.OwnScore); int.TryParse(a[1], out d.EnemyScore); int.TryParse(a[2], out d.ScoreLimit); d.Clock = a[3];
        var gaps = ScoreBoxGaps();
        if (gaps is null && !OldScoreBox) return;
        _scoreDemoDrawn += GetProcessDeltaTime();
        if (_scoreDemoDrawn < 1.5 || _scoreDemoShot) return;
        _scoreDemoShot = true;
        if (gaps is { } g) GD.Print($"score box gaps: left {g.Left:F1} (middle {g.LeftMiddle:F1}) right {g.Right:F1} top {g.Top:F1} bottom {g.Bottom:F1} (movie px; the left edge's nearest corner)");
        else GD.Print("score box gaps: old layout");
        if (a.Length >= 5) GD.Print(GetViewport().GetTexture().GetImage().SavePng(a[4]) == Error.Ok ? $"score box shot -> {a[4]}" : "score box shot failed");
        if (a.Length >= 5) GetTree().Quit();
    }

    /// <summary>Whether the HUD movies are still being read (the self-test waits for them).</summary>
    public bool HudMoviesLoading() => _movieArt is { Loading: true } || (_movieArt is { Failed: false, Movies: null });
}
