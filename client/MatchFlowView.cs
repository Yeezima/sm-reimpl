using System;
using System.Collections.Generic;
using Godot;
using SmFormats.Locale;
using SmSim;

namespace SmClient;

/// <summary>
/// The texts of the match flow, read at runtime from the install's locale (never stored in the repository): 119813 "MATCH BEGINS:", 110550
/// "VICTORY", 106136 "DEFEAT", 120706 "Draw", 104299 "%1TEAM% is victorious!", 111880 "NEXT MAP - %1TIMER%" and the team names 104333 /
/// 104334. Without the install's text each falls back to a plain word of ours.
/// </summary>
public sealed class MatchFlowText
{
    public string MatchBegins = "MATCH BEGINS:", Victory = "VICTORY", Defeat = "DEFEAT", Draw = "DRAW";
    public string Victorious = "%1TEAM% is victorious!", NextMap = "NEXT MAP - %1TIMER%";
    public string MarinesName = "Space Marines", ChaosName = "Chaos";
    /// <summary>Whether the install's text was found (false: the fallback words above).</summary>
    public bool FromInstall;

    public const int IdMatchBegins = 119813, IdVictory = 110550, IdDefeat = 106136, IdDraw = 120706, IdVictorious = 104299, IdNextMap = 111880,
        IdMarines = 104333, IdChaos = 104334;

    public static MatchFlowText Load(string gameDir)
    {
        var text = new MatchFlowText();
        try
        {
            string path = System.IO.Path.Combine(gameDir, "data", "locale", "english", "ucs.pc");
            if (!System.IO.File.Exists(path)) return text;
            var locale = LocaleTable.Open(path);
            string Get(int id, string fallback) => locale.Get(id) is { Length: > 0 } t ? t : fallback;
            text.MatchBegins = Get(IdMatchBegins, text.MatchBegins);
            text.Victory = Get(IdVictory, text.Victory);
            text.Defeat = Get(IdDefeat, text.Defeat);
            text.Draw = Get(IdDraw, text.Draw);
            text.Victorious = Get(IdVictorious, text.Victorious);
            text.NextMap = Get(IdNextMap, text.NextMap);
            text.MarinesName = Get(IdMarines, text.MarinesName);
            text.ChaosName = Get(IdChaos, text.ChaosName);
            text.FromInstall = true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            GD.PrintErr("match flow: locale unreadable (" + ex.Message + "); plain words are used");
        }
        return text;
    }

    /// <summary>The team's name (the locale's, else ours).</summary>
    public string NameOf(Team side) => side == Team.Chaos ? ChaosName : MarinesName;

    public string VictoriousOf(Team winner) => Victorious.Replace("%1TEAM%", winner == Team.Chaos ? ChaosName : MarinesName);

    /// <summary>"NEXT MAP - 0:12": the timer as m:ss.</summary>
    public string NextMapIn(double seconds)
    {
        int s = (int)Math.Ceiling(Math.Max(0.0, seconds));
        return NextMap.Replace("%1TIMER%", $"{s / 60}:{s % 60:00}");
    }
}

/// <summary>
/// What the match flow draws this frame: the "MATCH BEGINS" countdown, the victory / defeat / draw splash, or the "NEXT MAP" timer
/// under the scoreboard (<see cref="MatchFlowView"/>).
/// </summary>
public readonly record struct MatchFlowFrame(double CountdownSeconds = -1, double SplashTime = -1, MatchResult Result = MatchResult.Draw,
    Team OwnTeam = Team.None, Team Winner = Team.None, double NextMapSeconds = -1)
{
    /// <summary>
    /// Nothing to draw. fix/connect-timeout: the view starts with it (a <c>default</c> frame has every time at 0, which drew the splash's first frame and
    /// "NEXT MAP - 0:00" while the client was still connecting) and returns to it whenever no match state has arrived.
    /// </summary>
    public static readonly MatchFlowFrame None = new(-1, -1, MatchResult.Draw, Team.None, Team.None, -1);

    public bool Active => CountdownSeconds >= 0 || SplashTime >= 0 || NextMapSeconds >= 0;
}

/// <summary>
/// Match flow on screen, drawn above the HUD (match flow, spec/behavior.md "Match flow"). **Countdown**: the original's clip
/// (<c>countdown_mc</c> of HudText.gfx) has a gold 24 pt line ("MATCH BEGINS:") over a white 40 pt number, in the 1280x720 stage's
/// upper middle; the number's format in the data is mm:ss (<c>00:00</c>), so it counts "00:05" down to "00:01" (the pulse on each
/// second and the dark panel behind are ours). **Splash**: the clips <c>WinLose_MarinesWin_mc</c> / <c>_ChaosWins_Mc</c> / <c>_Failure_mc</c> /
/// <c>_Draw_mc</c> run 195 frames at 30 fps (6.5 s): two bands slide in from above and below and fade up (frames 4 to 26), the 72 pt
/// word flies in from 5.5 times its size with a white flash (26 to 34), holds, and everything fades out (153 to 172), then the clip calls
/// the scoreboard. The word's colour is the clip's (Marines victory gold, Chaos victory orange, defeat red, draw gold). Positions and
/// the bands' look are **approximate** (the bands are bitmap fills that are not reproduced; drawn as dark gradients); the line "%1TEAM% is
/// victorious!" under the word is ours (the text is in the data; where it is shown is not).
/// </summary>
public partial class MatchFlowView : Control
{
    const float Stage = 720f;   // the HUD clip's stage height; everything below is in these units
    const double Fps = 30.0;

    static readonly Color Gold = new(1f, 0.8f, 0f), Orange = new(1f, 0.6f, 0f), Red = new(1f, 0.2f, 0f), White = new(1f, 1f, 1f);

    FontVariation? _font;
    MatchFlowFrame _frame = MatchFlowFrame.None;
    MatchFlowText _text = new();

    public MatchFlowView()
    {
        Name = "MatchFlow";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Ready()
    {
        _font = new FontVariation { BaseFont = ThemeDB.FallbackFont, VariationEmbolden = 0.55f, SpacingGlyph = 1 };
    }

    public void SetText(MatchFlowText text) => _text = text;

    /// <summary>The frame to draw (redraws only when it changed).</summary>
    public void SetFrame(MatchFlowFrame frame)
    {
        if (frame == _frame) return;
        _frame = frame;
        QueueRedraw();
    }

    // ------------------------------------------------------------------ timeline (pure; tested by the frame numbers of the clip)

    /// <summary>Alpha and slide (0 at the start, 1 once in place) of the bands at <paramref name="frame"/> (the clip's frames 4 to 26, eased out).</summary>
    public static float BandProgress(double frame) => Ease(Math.Clamp((frame - 4.0) / 22.0, 0.0, 1.0));

    /// <summary>The clip's scale of the word on frames 26 to 34 (the word flies in from 5.5 times its size).</summary>
    static readonly float[] WordScales = [5.47f, 4.42f, 3.51f, 2.74f, 2.12f, 1.63f, 1.28f, 1.07f, 1.0f];

    /// <summary>The word's scale at <paramref name="frame"/>: the clip's keyframes between frames 26 and 34 (interpolated), 1 after, 0 before it appears.</summary>
    public static float WordScale(double frame)
    {
        if (frame < 26.0) return 0f;
        if (frame >= 34.0) return 1f;
        int i = (int)(frame - 26.0);
        float t = (float)(frame - 26.0 - i);
        return WordScales[i] + (WordScales[i + 1] - WordScales[i]) * t;
    }

    /// <summary>The white flash over the word (1 at frame 26 fading to 0 at 34).</summary>
    public static float WordFlash(double frame) => frame < 26.0 || frame >= 34.0 ? 0f : MathF.Pow(1f - (float)((frame - 26.0) / 8.0), 2f);

    /// <summary>Everything's alpha: 1 until frame 153, then fading out to 0 at frame 172.</summary>
    public static float FadeOut(double frame) => (float)Math.Clamp(1.0 - (frame - 153.0) / 19.0, 0.0, 1.0);

    static float Ease(double t) => (float)(1.0 - Math.Pow(1.0 - t, 3.0));

    public static Color ResultColour(MatchResult result, Team winner) => result switch
    {
        MatchResult.Defeat => Red,
        MatchResult.Victory when winner == Team.Chaos => Orange,
        _ => Gold,
    };

    // ------------------------------------------------------------------ drawing

    public override void _Draw()
    {
        if (_font is null || !_frame.Active) return;
        float s = Size.Y / Stage;
        float cx = Size.X / 2f;
        if (_frame.SplashTime >= 0) DrawSplash(s, cx);
        else if (_frame.CountdownSeconds >= 0) DrawCountdown(s, cx);
        if (_frame.NextMapSeconds >= 0)
        {
            string line = _text.NextMapIn(_frame.NextMapSeconds);
            Text(line, new Vector2(0, Size.Y * 0.93f), Size.X, (int)(30 * s), Gold);
        }
    }

    void Text(string text, Vector2 baseline, float width, int size, Color colour)
    {
        // Dark outline for legibility on any scene, then the text.
        var outline = new Color(0, 0, 0, colour.A * 0.8f);
        int o = Math.Max(1, size / 20);
        foreach (var d in new[] { new Vector2(o, o), new Vector2(-o, o), new Vector2(o, -o), new Vector2(-o, -o) })
            DrawString(_font, baseline + d, text, HorizontalAlignment.Center, width, size, outline);
        DrawString(_font, baseline, text, HorizontalAlignment.Center, width, size, colour);
    }

    void DrawCountdown(float s, float cx)
    {
        double left = _frame.CountdownSeconds;
        int shown = MatchFlow.DisplaySeconds(left);
        double phase = shown - left;   // 0 just as the number changes, up to 1 just before the next
        float pulse = 1f + 0.35f * (float)Math.Max(0.0, 1.0 - phase / 0.3);   // ours: the number swells as it changes
        // Dark panel behind the two lines (ours).
        var panel = new Rect2(cx - 250 * s, 196 * s, 500 * s, 100 * s);
        DrawRect(panel, new Color(0, 0, 0, 0.38f));
        Text(_text.MatchBegins, new Vector2(0, 232 * s), Size.X, (int)(24 * s), Gold);
        Text($"00:{shown:00}", new Vector2(0, 232 * s + 46 * s * pulse), Size.X, (int)(40 * s * pulse), White);
    }

    void DrawSplash(float s, float cx)
    {
        double frame = _frame.SplashTime * Fps;
        float fade = FadeOut(frame);
        if (fade <= 0f || frame < 4.0) return;
        float band = BandProgress(frame);
        // Two bands (the clip's bitmap-filled strips: 1296 x 228 after scaling), sliding 94 px toward the middle.
        float bandHeight = 228f * s;
        float topY = (46f - 94f * (1f - band)) * s, bottomY = (767f + 93f * (1f - band)) * s;
        DrawBand(topY, topY + bandHeight, topAlpha: 0.88f * band * fade, bottomAlpha: 0.15f * band * fade);
        DrawBand(bottomY - bandHeight, bottomY, topAlpha: 0.15f * band * fade, bottomAlpha: 0.88f * band * fade);

        float scale = WordScale(frame);
        if (scale <= 0f) return;
        string word = _frame.Result switch { MatchResult.Victory => _text.Victory, MatchResult.Defeat => _text.Defeat, _ => _text.Draw };
        var colour = ResultColour(_frame.Result, _frame.Winner);
        float flash = WordFlash(frame);
        colour = colour.Lerp(White, flash);
        colour.A = fade;
        int size = (int)(120f * s * scale);
        float y = 420f * s + 40f * s * scale;
        Text(word, new Vector2(0, y), Size.X, size, colour);
        // The line under the word (ours), fading in over frames 40 to 60 and out with everything.
        if (_frame.Winner != Team.None && frame >= 40.0)
        {
            float a = (float)Math.Clamp((frame - 40.0) / 20.0, 0.0, 1.0) * fade;
            Text(_text.VictoriousOf(_frame.Winner), new Vector2(0, 420f * s + 100f * s), Size.X, (int)(28 * s), new Color(1, 1, 1, a));
        }
    }

    /// <summary>A horizontal strip whose alpha runs from <paramref name="topAlpha"/> at its top edge to <paramref name="bottomAlpha"/> at its bottom edge, full width.</summary>
    void DrawBand(float top, float bottom, float topAlpha, float bottomAlpha)
    {
        var points = new[] { new Vector2(0, top), new Vector2(Size.X, top), new Vector2(Size.X, bottom), new Vector2(0, bottom) };
        var colours = new[] { new Color(0, 0, 0, topAlpha), new Color(0, 0, 0, topAlpha), new Color(0, 0, 0, bottomAlpha), new Color(0, 0, 0, bottomAlpha) };
        DrawPolygon(points, colours);
    }
}
