using System;
using System.Collections.Generic;
using Godot;
using SmFormats.Locale;
using SmNet;

namespace SmClient;

/// <summary>
/// The texts of the end-of-match screens, read at runtime from the install's locale (never stored in the repository; without it each falls back to
/// a plain word of ours). Ids from the strings of <c>EndOfArena.gfx</c> and <c>SummaryScreen.gfx</c> (docs/discovery.md, "End-of-match screens").
/// </summary>
public sealed class MatchSummaryText
{
    public string MatchResults = "MATCH RESULTS", TeamScore = "Team Score", Kills = "KILLS", Deaths = "DEATHS", Player = "PLAYER", Level = "LEVEL",
        Ratio = "RATIO", Assists = "ASSISTS", Seized = "SEIZED", Defended = "DEFENDED",
        BattleRecord = "BATTLE RECORD", Scoreboard = "SCOREBOARD", BestClass = "BEST CLASS", BestWeapon = "BEST WEAPON", EnemyKills = "ENEMY KILLS",
        PlayerDeaths = "PLAYER DEATHS", KillRatio = "KILL RATIO", TeamAssists = "TEAM ASSISTS", BestKillstreak = "BEST KILLSTREAK", Nemesis = "NEMESIS";
    /// <summary>Whether the install's text was found.</summary>
    public bool FromInstall;

    public static MatchSummaryText Load(string gameDir)
    {
        var text = new MatchSummaryText();
        try
        {
            string path = System.IO.Path.Combine(gameDir, "data", "locale", "english", "ucs.pc");
            if (!System.IO.File.Exists(path)) return text;
            var locale = LocaleTable.Open(path);
            string Get(int id, string fallback) => locale.Get(id) is { Length: > 0 } t ? t : fallback;
            text.MatchResults = Get(110552, text.MatchResults);
            text.TeamScore = Get(122315, text.TeamScore);
            text.Kills = Get(106098, text.Kills);
            text.Deaths = Get(106100, text.Deaths);
            text.Player = Get(110548, text.Player);
            text.Level = Get(106140, text.Level);
            text.Ratio = Get(106101, text.Ratio);
            text.Assists = Get(106099, text.Assists);
            text.Seized = Get(120334, text.Seized);
            text.Defended = Get(120335, text.Defended);
            text.BattleRecord = Get(106116, text.BattleRecord);
            text.Scoreboard = Get(106117, text.Scoreboard);
            text.BestClass = Get(106085, text.BestClass);
            text.BestWeapon = Get(106086, text.BestWeapon);
            text.EnemyKills = Get(106087, text.EnemyKills);
            text.PlayerDeaths = Get(106088, text.PlayerDeaths);
            text.KillRatio = Get(106089, text.KillRatio);
            text.TeamAssists = Get(106090, text.TeamAssists);
            text.BestKillstreak = Get(106091, text.BestKillstreak);
            text.Nemesis = Get(106094, text.Nemesis);
            text.FromInstall = true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            GD.PrintErr("match summary: locale unreadable (" + ex.Message + "); plain words are used");
        }
        return text;
    }
}

/// <summary>One player of the count-up's list.</summary>
/// <summary>A count-up row; <paramref name="Name"/> without the viewer's " (you)", which the view adds after fitting the name (names/bot-lore).</summary>
public sealed record CountUpRow(string Name, string Class, int Kills, int Deaths, bool IsViewer);

/// <summary>What the count-up shows: the viewer's team score and, with teams, the other side's, the viewer's kills, and the best players of the viewer's side.</summary>
public sealed record CountUpData(string TeamLabel, int TeamScore, string? OtherLabel, int OtherScore, int ViewerKills, IReadOnlyList<CountUpRow> Rows, Color SideColour);

/// <summary>One row of the battle record. A null <see cref="Value"/> is drawn as a dash (not tracked or nothing yet).</summary>
public sealed record SummaryLine(string Label, string? Value, Color? Colour = null);

/// <summary>The battle record screen: the rows and an optional small note under them.</summary>
public sealed record SummaryData(IReadOnlyList<SummaryLine> Lines, string? Note = null);

/// <summary>
/// The end-of-match screens after the splash (fidelity gap 4; spec/behavior.md "Match flow: the score count-up and the summary"), drawn above the HUD in the
/// 1280x720 stage of the original's movies. **Count-up** after <c>EndOfArena.gfx</c>: a panel eases in, "Team Score" counts up from zero, then the viewer's
/// kills do, over the list of the side's best players (the movie's own number blocks are <c>teamscore_mc</c>, <c>playerscore_mc</c>, <c>playerxp_mc</c> and four
/// <c>player_n_mc</c> rows; the per-player score and experience are not in our data, so those are left out). **Summary** after <c>SummaryScreen.gfx</c>'s
/// <c>battle_record</c> tab: the scoreboard and battle record tab names, and the record's rows under the original's labels. Panel look, positions, fonts,
/// the counting curve and the stage lengths are **ours** (the movies' timers are script values that are not in their constant pools); the colours are the
/// movies' text colours (gold #ffcc00, white, grey #c0c0c0, red #ff3300 for the nemesis).
/// </summary>
public partial class MatchSummaryView : Control
{
    const float Stage = 720f;

    static readonly Color Gold = new(1f, 0.8f, 0f), Grey = new(0.75f, 0.75f, 0.75f), Dim = new(0.6f, 0.6f, 0.6f), White = new(1f, 1f, 1f), Red = new(1f, 0.2f, 0f);

    FontVariation? _font;
    MatchSummaryText _text = new();
    CountUpData? _countUp;
    SummaryData? _summary;
    double _time;

    public MatchSummaryView()
    {
        Name = "MatchSummary";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        Visible = false;
    }

    public override void _Ready()
    {
        _font = new FontVariation { BaseFont = ThemeDB.FallbackFont, VariationEmbolden = 0.55f, SpacingGlyph = 1 };
    }

    public void SetText(MatchSummaryText text) => _text = text;

    /// <summary>What the view shows: the count-up (<paramref name="time"/> seconds into it), the summary, or nothing.</summary>
    public void SetScreen(CountUpData? countUp, SummaryData? summary, double time)
    {
        _countUp = countUp; _summary = summary; _time = time;
        bool any = countUp is not null || summary is not null;
        Visible = any;
        if (any) QueueRedraw();
    }

    public override void _Draw()
    {
        if (_font is null) return;
        float s = Math.Min(Size.Y / Stage, Size.X / 1280f);   // the 1280x720 stage fitted inside the window (a narrow window shrinks, never overflows)
        if (_countUp is { } up) DrawCountUp(up, s);
        else if (_summary is { } sum) DrawSummary(sum, s);
    }

    // ------------------------------------------------------------------ drawing

    void Text(string text, float x, float baseline, float width, int size, Color colour, HorizontalAlignment align)
    {
        if (size < 4) return;
        text = Fit(text, size, width);
        var outline = new Color(0, 0, 0, colour.A * 0.8f);
        int o = Math.Max(1, size / 22);
        foreach (var d in new[] { new Vector2(o, o), new Vector2(-o, o), new Vector2(o, -o), new Vector2(-o, -o) })
            DrawString(_font, new Vector2(x, baseline) + d, text, align, width, size, outline);
        DrawString(_font, new Vector2(x, baseline), text, align, width, size, colour);
    }

    /// <summary>
    /// names/bot-lore: a row's name, and for the viewer " (you)" after it; a name too long for <paramref name="width"/> is cut (<see cref="Fit"/>) so the
    /// suffix always shows.
    /// </summary>
    string Named(string name, bool viewer, int size, float width)
    {
        if (!viewer) return name;
        const string you = " (you)";
        float suffix = _font is null ? 0f : _font.GetStringSize(you, HorizontalAlignment.Left, -1, size).X;
        return Fit(name, size, width - suffix) + you;
    }

    /// <summary>The text cut with "..." so that it is no wider than <paramref name="width"/> at this size (a class or weapon name that is too long).</summary>
    string Fit(string text, int size, float width)
    {
        if (_font is null || width <= 0f || _font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X <= width) return text;
        int n = text.Length;
        while (n > 1 && _font.GetStringSize(text[..n] + "...", HorizontalAlignment.Left, -1, size).X > width) n--;
        return text[..n] + "...";
    }

    /// <summary>The dark panel with the gold edge, <paramref name="alpha"/> scaling all of it.</summary>
    void Panel(Rect2 rect, float alpha)
    {
        DrawRect(rect, new Color(0.03f, 0.03f, 0.03f, 0.82f * alpha));
        DrawRect(rect, new Color(1f, 0.8f, 0f, 0.9f * alpha), false, 2f);
    }

    void DrawCountUp(CountUpData data, float s)
    {
        float cx = Size.X / 2f;
        var (stage, stageTime) = CountUp.StageAt(_time);
        float alpha = stage == 0 ? (float)CountUp.Intro(stageTime) : 1f;   // the panel eases in once, with the first stage
        float slide = (1f - alpha) * 60f * s;   // the panel rises into place while it fades in (the movie's block moves 80 px over its intro)
        float w = 860f * s, left = cx - w / 2f, top = 130f * s + slide;   // names/bot-lore: wider for names of up to 24 characters (720 before)
        Panel(new Rect2(left, top, w, 440f * s), alpha);
        var gold = new Color(Gold, alpha);
        Text(_text.MatchResults, left, top + 52f * s, w, (int)(30 * s), gold, HorizontalAlignment.Center);

        // The big number: the team score, then the viewer's kills (the movie's stages 'teamscore' and 'playerscore').
        double stageAlpha = stage == 0 ? 1.0 : CountUp.Intro(stageTime);
        string label = stage == 0 ? _text.TeamScore : _text.Kills;
        int target = stage == 0 ? data.TeamScore : data.ViewerKills;
        var colour = stage == 0 ? data.SideColour : White;
        Text(label, left, top + 100f * s, w, (int)(22 * s), new Color(Grey, alpha * (float)stageAlpha), HorizontalAlignment.Center);
        Text(CountUp.Value(target, stageTime).ToString(), left, top + 168f * s, w, (int)(64 * s), new Color(colour, alpha * (float)stageAlpha), HorizontalAlignment.Center);
        if (stage == 0 && data.OtherLabel is { } other)
            Text($"{other}  {CountUp.Value(data.OtherScore, stageTime)}", left, top + 204f * s, w, (int)(22 * s), new Color(Dim, alpha), HorizontalAlignment.Center);

        // The side's best players, their kills and deaths counting up with the first stage.
        float colName = left + 40f * s, colClass = left + 340f * s, colKills = left + w - 170f * s, colDeaths = left + w - 60f * s;
        float y = top + 262f * s;
        var head = new Color(Grey, alpha);
        Text(_text.Player, colName, y, 290f * s, (int)(18 * s), head, HorizontalAlignment.Left);
        Text(_text.Kills, colKills - 70f * s, y, 70f * s, (int)(18 * s), head, HorizontalAlignment.Right);
        Text(_text.Deaths, colDeaths - 80f * s, y, 80f * s, (int)(18 * s), head, HorizontalAlignment.Right);
        DrawRect(new Rect2(left + 30f * s, y + 8f * s, w - 60f * s, 1.5f), new Color(1f, 0.8f, 0f, 0.5f * alpha));
        y += 40f * s;
        double firstStage = stage == 0 ? stageTime : CountUp.StageSeconds;   // the rows count with the first stage and stay
        foreach (var row in data.Rows)
        {
            var c = row.IsViewer ? new Color(0.6f, 1f, 0.6f, alpha) : new Color(White, alpha);
            Text(Named(row.Name, row.IsViewer, (int)(22 * s), 290f * s), colName, y, 290f * s, (int)(22 * s), c, HorizontalAlignment.Left);
            Text(row.Class, colClass, y, 255f * s, (int)(16 * s), new Color(Dim, alpha), HorizontalAlignment.Left);
            Text(CountUp.Value(row.Kills, firstStage).ToString(), colKills - 70f * s, y, 70f * s, (int)(22 * s), c, HorizontalAlignment.Right);
            Text(CountUp.Value(row.Deaths, firstStage).ToString(), colDeaths - 80f * s, y, 80f * s, (int)(22 * s), c, HorizontalAlignment.Right);
            y += 36f * s;
        }
    }

    void DrawSummary(SummaryData data, float s)
    {
        float cx = Size.X / 2f;
        float alpha = (float)Math.Clamp(_time / 0.3, 0.0, 1.0);
        float w = 860f * s, left = cx - w / 2f, top = 130f * s;   // names/bot-lore: room for a long NEMESIS name (720 before)
        Panel(new Rect2(left, top, w, 440f * s), alpha);
        // The tab strip of the original's summary: the scoreboard tab, then the battle record, the active one gold.
        float tabW = w / 2f;
        Text(_text.Scoreboard, left, top + 50f * s, tabW, (int)(24 * s), new Color(Dim, alpha), HorizontalAlignment.Center);
        Text(_text.BattleRecord, left + tabW, top + 50f * s, tabW, (int)(24 * s), new Color(Gold, alpha), HorizontalAlignment.Center);
        DrawRect(new Rect2(left + tabW + 40f * s, top + 62f * s, tabW - 80f * s, 3f), new Color(Gold, alpha));
        DrawRect(new Rect2(left + 30f * s, top + 68f * s, w - 60f * s, 1.5f), new Color(1f, 0.8f, 0f, 0.35f * alpha));

        float y = top + 112f * s;
        foreach (var line in data.Lines)
        {
            Text(line.Label, left + 44f * s, y, 256f * s, (int)(22 * s), new Color(Grey, alpha), HorizontalAlignment.Left);
            bool has = line.Value is not null;
            var c = has ? line.Colour ?? White : Dim;
            Text(has ? line.Value! : "—", left + 300f * s, y, w - 344f * s, (int)(22 * s), new Color(c, alpha), HorizontalAlignment.Right);
            y += 34f * s;   // nine rows (Seize Ground adds SEIZED) end at y 384, above the note at 424
        }
        if (data.Note is { } note)
            Text(note, left, top + 424f * s, w, (int)(15 * s), new Color(Dim, alpha), HorizontalAlignment.Center);
    }
}
