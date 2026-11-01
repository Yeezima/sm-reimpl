using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Added content "Musou": its HUD (design section D, MU3), drawn over the match HUD (<see cref="Hud"/>, as its child) in the same 1080-high virtual space and
/// with the same faces and colours, only in a Musou match on a client that draws (never headless or on a lean bot). Placed clear of the match HUD's widgets
/// (the armour and health bar top centre at y 116 to 171, the reward lines under it, the kill feed top right, the score panel bottom left, the weapon box bottom
/// right), at 16:9 and 16:10 alike:
/// <list type="bullet">
/// <item>top left, the run panel: WAVE n / 5 (FINAL WAVE with the boss), enemies left, the shared lives, the own KOs and points;</item>
/// <item>top centre, above the armour bar, the Warboss's health bar while it lives (ticks at its Waaagh! thresholds, 75, 50 and 25 %), ENRAGED beside its name;</item>
/// <item>right of centre, the KO chain: the multiplier, the chain and the time left to extend it;</item>
/// <item>under the reward lines, the break's countdown to the next wave; call-outs ("WAAAGH!", "ENRAGED") with a short screen flash;</item>
/// <item>the end-of-run panel for a victory or a defeat (the wave reached, the KOs and points, the next run's start) during the match end splash.</item>
/// </list>
/// In Musou Mode (<see cref="MusouPhase.Campaign"/>) there are no waves or shared lives: the run panel goes (its figures are on the M map, <see cref="TfwBattlePanel"/>) and the boss bar shows the battle's commander.
/// Layout, sizes and colours are ours (kind: ours; the original has no such mode). <see cref="NetworkView"/> fills <see cref="Data"/> every frame and calls
/// <see cref="Fed"/>; without that call for two frames (the view stops its added content while it is not connected) the HUD hides itself. No allocation per
/// frame: the texts are rebuilt only when what they show changes.
/// </summary>
public sealed partial class MusouHud : Control
{
    /// <summary>What the Musou HUD shows this frame (filled by the view every frame).</summary>
    public sealed class Frame
    {
        public MusouDirectorView? Director;
        /// <summary>Seconds of the break left (counted down between the director's updates).</summary>
        public float BreakLeft;
        public int Kos, Points;
        /// <summary>The KO chain: chained KOs (0: none), the multiplier the next KO in the window earns, and the window's fraction left (0..1).</summary>
        public int Chain;
        public float Multiplier = 1f, ChainLeft;
        public bool BossEnraged;
        public bool Alive = true;
        /// <summary>The lives the run started with (the pips; 0: unknown, the highest seen).</summary>
        public int LivesMax;
        /// <summary>Seconds until the server starts the next run (the ended match's restart time), negative when not known.</summary>
        public float NextRunIn = -1f;
        /// <summary>The boss bar's name (the war's commander; the Warboss otherwise).</summary>
        public string BossName = "ORK WARBOSS";
    }

    public Frame Data = new();

    const float RefHeight = 1080f;
    // The match HUD's colours (Hud.cs, sampled from the reference frames).
    static readonly Color Gold = new(0.98f, 0.8f, 0.12f);
    static readonly Color RedText = new(0.9f, 0.08f, 0.07f);
    static readonly Color RedFill = new(0.86f, 0.04f, 0.04f);
    static readonly Color Grey = new(0.62f, 0.62f, 0.64f);
    static readonly Color FrameGold = new(0.74f, 0.64f, 0.32f, 0.95f);
    static readonly Color PanelFill = new(0.02f, 0.02f, 0.025f, 0.84f);
    static readonly Color White = new(0.93f, 0.93f, 0.95f);
    static readonly Color Orange = new(1f, 0.45f, 0.05f);
    static readonly Color PipOff = new(0.25f, 0.25f, 0.26f, 0.9f);
    static readonly float[] BossMarks = [0.25f, 0.5f, 0.75f];

    sealed class CallOutLine { public string Text = ""; public Color Colour; public double At, Seconds; }

    /// <summary>A text rebuilt only when its key changes (the HUD's numbers change a few times a second at most).</summary>
    sealed class Cached
    {
        long _key = long.MinValue;
        string _text = "";
        public string Of(long key, Func<long, string> make)
        {
            if (key != _key) { _key = key; _text = make(key); }
            return _text;
        }
    }

    readonly List<CallOutLine> _callOuts = new();
    readonly Vector2[] _panel = new Vector2[6], _panelClosed = new Vector2[7], _diamond = new Vector2[4];
    readonly Cached _waveText = new(), _waveOf = new(), _enemies = new(), _lives = new(), _kos = new(), _points = new(), _bossPct = new(), _chainX = new(),
                    _chainN = new(), _breakWave = new(), _breakIn = new(), _endSub = new(), _nextRun = new();
    FontVariation? _font, _fontBold;
    double _time;
    double _flashAt = double.NegativeInfinity;
    Color _flash;
    float _bossShown = -1f, _bossTrail = -1f;
    ushort _bossId;
    double _endSeenAt = -1;
    ulong _fedFrame;

    public MusouHud()
    {
        Name = "MusouHud";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        TextureFilter = TextureFilterEnum.Linear;
    }

    public override void _Ready()
    {
        // The match HUD's faces (Hud._Ready): Bahnschrift for the original's "Bourgeois", Godot's default as the fallback.
        var system = new SystemFont { FontNames = ["Bahnschrift", "DIN Alternate", "Arial Narrow"], Fallbacks = [ThemeDB.FallbackFont] };
        var ts = TextServerManager.GetPrimaryInterface();
        FontVariation Variant(int weight, int width) => new()
        {
            BaseFont = system,
            VariationOpentype = new Godot.Collections.Dictionary { { ts.NameToTag("wght"), weight }, { ts.NameToTag("wdth"), width } },
        };
        _font = Variant(450, 92);
        _fontBold = Variant(600, 92);
    }

    /// <summary>The view filled <see cref="Data"/> this frame.</summary>
    public void Fed() => _fedFrame = Engine.GetProcessFrames();

    /// <summary>A call-out in the centre (the newest replaces an older one of the same text); <paramref name="flash"/> tints the screen for a moment.</summary>
    public void CallOut(string text, Color colour, double seconds = 2.2, Color? flash = null)
    {
        for (int i = _callOuts.Count - 1; i >= 0; i--) if (_callOuts[i].Text == text) _callOuts.RemoveAt(i);
        _callOuts.Add(new CallOutLine { Text = text, Colour = colour, At = _time, Seconds = seconds });
        while (_callOuts.Count > 2) _callOuts.RemoveAt(0);
        if (flash is { } f) { _flash = f; _flashAt = _time; }
    }

    /// <summary>The Waaagh! call-out (orange flash) and the enrage call-out (red flash).</summary>
    public void Waaagh() => CallOut("WAAAGH!", Orange, 2.4, new Color(1f, 0.4f, 0.02f, 0.32f));
    public void Enraged() => CallOut("ENRAGED", RedText, 2.4, new Color(0.9f, 0.02f, 0.02f, 0.26f));

    public override void _Process(double delta)
    {
        _time += delta;
        // Not fed for two frames: the view is not connected (or left Musou); nothing of a frozen run stays on screen.
        if (Engine.GetProcessFrames() - _fedFrame > 2) { Visible = false; return; }
        for (int i = _callOuts.Count - 1; i >= 0; i--) if (_time - _callOuts[i].At > _callOuts[i].Seconds) _callOuts.RemoveAt(i);
        var d = Data.Director;
        // The boss bar's fill eases to the health; the trail (the health just lost) follows after a moment.
        if (d is { BossId: > 0 } b && b.BossMaxHealth > 0f)
        {
            float f = Math.Clamp(b.BossHealth / b.BossMaxHealth, 0f, 1f);
            if (b.BossId != _bossId || _bossShown < 0f) { _bossId = b.BossId; _bossShown = _bossTrail = f; }
            _bossShown += (f - _bossShown) * (float)Math.Min(1.0, delta * 18.0);
            if (_bossTrail < _bossShown) _bossTrail = _bossShown;
            else _bossTrail = Math.Max(_bossShown, _bossTrail - (float)delta * 0.25f);
        }
        else { _bossShown = _bossTrail = -1f; _bossId = 0; }
        bool ended = d is { Phase: MusouPhase.Won or MusouPhase.Lost };
        if (ended && _endSeenAt < 0) _endSeenAt = _time;
        else if (!ended) _endSeenAt = -1;
        QueueRedraw();
    }

    // ---------------------------------------------------------------- drawing helpers (as Hud.cs)

    float TextWidth(string s, int size, bool bold) => (bold ? _fontBold : _font) is { } f ? f.GetStringSize(s, HorizontalAlignment.Left, -1, size).X : 0f;

    /// <summary>Text with a dark outline; (x, y) is the baseline, left (-1), centre (0) or right (1) aligned.</summary>
    void Txt(string s, float x, float y, int size, Color c, int align = -1, int outline = 4, bool bold = false)
    {
        if ((bold ? _fontBold : _font) is not { } font || s.Length == 0) return;
        float px = align < 0 ? x : align == 0 ? x - TextWidth(s, size, bold) / 2f : x - TextWidth(s, size, bold);
        var pos = new Vector2(px, y);
        if (outline > 0) DrawStringOutline(font, pos, s, HorizontalAlignment.Left, -1, size, outline, new Color(0f, 0f, 0f, c.A * 0.8f));
        DrawString(font, pos, s, HorizontalAlignment.Left, -1, size, c);
    }

    /// <summary>A box with two chamfered corners (top right, bottom left), like the match HUD's weapon box.</summary>
    void Panel(float x, float y, float w, float h, float alpha = 1f)
    {
        const float cut = 14f;
        _panel[0] = new(x, y); _panel[1] = new(x + w - cut, y); _panel[2] = new(x + w, y + cut);
        _panel[3] = new(x + w, y + h); _panel[4] = new(x + cut, y + h); _panel[5] = new(x, y + h - cut);
        DrawColoredPolygon(_panel, new Color(PanelFill, PanelFill.A * alpha));
        for (int i = 0; i < 6; i++) _panelClosed[i] = _panel[i];
        _panelClosed[6] = _panel[0];
        DrawPolyline(_panelClosed, new Color(FrameGold, FrameGold.A * alpha), 2f, true);
    }

    static string Thousands(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
    static string Plain(long v) => v.ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- draw

    public override void _Draw()
    {
        if (_font is null) return;
        float scale = Size.Y / RefHeight;
        if (scale <= 0f) return;
        DrawSetTransformMatrix(new Transform2D(0f, new Vector2(scale, scale), 0f, Vector2.Zero));
        var vs = Size / scale;
        DrawFlash(vs);
        if (Data.Director is not { } d) return;
        if (d.Phase != MusouPhase.Campaign) DrawRunPanel(d);
        DrawBossBar(vs, d);
        if (d.Phase is not (MusouPhase.Won or MusouPhase.Lost)) DrawChain(vs);
        DrawBreak(vs, d);
        DrawCallOuts(vs);
        DrawEndPanel(vs, d);
    }

    void DrawFlash(Vector2 vs)
    {
        const double seconds = 0.7;
        double since = _time - _flashAt;
        if (since < 0 || since > seconds) return;
        float a = (float)(1.0 - since / seconds);
        DrawRect(new Rect2(Vector2.Zero, vs), new Color(_flash, _flash.A * a * a));
    }

    /// <summary>The run panel, top left (below the "F1: info" hint): the wave, the enemies left, the lives, the KOs and points.</summary>
    void DrawRunPanel(MusouDirectorView d)
    {
        const float x = 36f, y = 60f, w = 318f, h = 168f;
        Panel(x, y, w, h);
        float lx = x + 20f, rx = x + w - 20f;
        long waveKey = (long)d.Phase << 16 | (long)d.Wave << 8 | d.Waves;
        string wave = _waveText.Of(waveKey, static k => (MusouPhase)(k >> 16) switch
        {
            MusouPhase.Waiting => "GET READY",
            MusouPhase.Boss => "FINAL WAVE",
            _ => $"WAVE {Math.Max(1, (k >> 8) & 0xFF)} / {Math.Max(1, k & 0xFF)}",
        });
        Txt(wave, lx, y + 44f, 34, Gold, -1, 4, true);
        if (d.Phase == MusouPhase.Boss) Txt(_waveOf.Of(waveKey & 0xFFFF, static k => $"{k >> 8} / {k & 0xFF}"), rx, y + 44f, 22, Grey, 1, 3);
        Txt("ENEMIES LEFT", lx, y + 78f, 18, Grey, -1, 3, true);
        Txt(d.Phase is MusouPhase.Break or MusouPhase.Waiting ? "-" : _enemies.Of(d.EnemiesLeft, Thousands), rx, y + 80f, 26, White, 1, 3, true);
        Txt("LIVES", lx, y + 112f, 18, Grey, -1, 3, true);
        int max = Math.Max(Data.LivesMax, d.Lives);
        if (max <= 9)
            for (int i = 0; i < max; i++)
            {
                var c = new Vector2(rx - 8f - i * 22f, y + 105f);
                bool left = max - 1 - i < d.Lives;
                _diamond[0] = c + new Vector2(0, -8); _diamond[1] = c + new Vector2(8, 0); _diamond[2] = c + new Vector2(0, 8); _diamond[3] = c + new Vector2(-8, 0);
                DrawColoredPolygon(_diamond, left ? Gold : PipOff);
            }
        else Txt(_lives.Of(d.Lives, Plain), rx, y + 114f, 26, d.Lives > 0 ? White : RedText, 1, 3, true);
        Txt("KOs", lx, y + 148f, 18, Grey, -1, 3, true);
        Txt(_kos.Of(Data.Kos, Thousands), lx + 160f, y + 150f, 26, White, 1, 3, true);
        Txt("PTS", lx + 180f, y + 148f, 18, Grey, -1, 3, true);
        Txt(_points.Of(Data.Points, Thousands), rx, y + 150f, 26, Gold, 1, 3, true);
    }

    /// <summary>The Warboss's bar, top centre above the armour bar, while it lives.</summary>
    void DrawBossBar(Vector2 vs, MusouDirectorView d)
    {
        if (d.BossId == 0 || d.BossMaxHealth <= 0f || _bossShown < 0f || d.Phase is MusouPhase.Won or MusouPhase.Lost) return;
        float cx = vs.X / 2f, w = Math.Min(720f, vs.X - 760f), x = cx - w / 2f, y = 52f, h = 22f;
        bool rage = Data.BossEnraged;
        float pulse = rage ? 0.75f + 0.25f * MathF.Sin((float)_time * 8f) : 1f;
        Txt(Data.BossName, cx, y - 10f, 24, rage ? RedText : Gold, 0, 4, true);
        if (rage) Txt("ENRAGED", x + w, y - 10f, 20, new Color(RedText, pulse), 1, 3, true);
        long pct = (long)MathF.Ceiling(Math.Clamp(d.BossHealth / d.BossMaxHealth, 0f, 1f) * 100f);
        Txt(_bossPct.Of(pct, static k => $"{k}%"), x, y - 10f, 20, White, -1, 3, true);
        DrawRect(new Rect2(x - 3f, y - 3f, w + 6f, h + 6f), PanelFill);
        if (_bossTrail > _bossShown) DrawRect(new Rect2(x + w * _bossShown, y, w * (_bossTrail - _bossShown), h), new Color(1f, 0.85f, 0.6f, 0.85f));
        var fill = rage ? new Color(1f * pulse, 0.12f, 0.04f) : RedFill;
        DrawRect(new Rect2(x, y, w * _bossShown, h), fill);
        DrawRect(new Rect2(x, y, w * _bossShown, 4f), new Color(1f, 0.55f, 0.45f, 0.55f));
        foreach (float mark in BossMarks) DrawLine(new Vector2(x + w * mark, y - 2f), new Vector2(x + w * mark, y + h + 2f), new Color(0f, 0f, 0f, 0.85f), 3f);
        DrawRect(new Rect2(x - 3f, y - 3f, w + 6f, h + 6f), FrameGold, false, 2f);
    }

    /// <summary>The KO chain, right of the crosshair and clear of the weapon-switch cards: the multiplier, the chain and the window left.</summary>
    void DrawChain(Vector2 vs)
    {
        if (Data.Chain <= 0 || Data.ChainLeft <= 0f || !Data.Alive) return;
        float rx = vs.X - 64f, y = 470f;
        float a = Math.Clamp(Data.ChainLeft * 4f, 0f, 1f);
        Txt(_chainX.Of((long)MathF.Round(Data.Multiplier * 10f), static k => $"x{k / 10}.{k % 10}"), rx, y, 56, new Color(Gold, a), 1, 5, true);
        Txt(_chainN.Of(Data.Chain + 1, static k => $"CHAIN {k}"), rx, y + 32f, 22, new Color(White, a), 1, 3, true);
        const float bw = 170f;
        DrawRect(new Rect2(rx - bw, y + 44f, bw, 6f), new Color(0f, 0f, 0f, 0.6f * a));
        DrawRect(new Rect2(rx - bw * Data.ChainLeft, y + 44f, bw * Data.ChainLeft, 6f), new Color(Gold, a));
    }

    /// <summary>Between waves: the wave cleared and the countdown to the next.</summary>
    void DrawBreak(Vector2 vs, MusouDirectorView d)
    {
        if (d.Phase != MusouPhase.Break) return;
        float cx = vs.X / 2f, y = Data.Alive ? 300f : 470f;
        Txt(_breakWave.Of(d.Wave, static k => $"WAVE {k} CLEARED"), cx, y, 26, Gold, 0, 4, true);
        Txt(_breakIn.Of((long)MathF.Ceiling(Math.Max(0f, Data.BreakLeft)), static k => $"NEXT WAVE IN {k}"), cx, y + 44f, 36, White, 0, 5, true);
    }

    void DrawCallOuts(Vector2 vs)
    {
        float cx = vs.X / 2f, y = Data.Alive ? 300f : 590f;   // dead: under the match HUD's KILLED and respawn lines and the break's lines (470 to 514)
        foreach (var c in _callOuts)
        {
            double since = _time - c.At;
            float pop = since < 0.15 ? 1.35f - (float)(since / 0.15) * 0.35f : 1f;
            float a = (float)Math.Clamp((c.Seconds - since) / 0.5, 0.0, 1.0);
            int size = (int)(70f * pop);
            Txt(c.Text, cx, y, size, new Color(c.Colour, a), 0, 7, true);
            y += 76f;
        }
    }

    /// <summary>
    /// The end of the run: victory or defeat, the wave reached, the KOs and points, and when the next run starts (the server restarts an ended Musou match after
    /// the match's restart time: warm-up, then the countdown into wave 1). Under the armour bar, on the match end splash's upper band and above its word
    /// (centred at about 0.62 of the height); the view hides the Musou HUD once the match's own end screens follow the splash.
    /// </summary>
    void DrawEndPanel(Vector2 vs, MusouDirectorView d)
    {
        if (d.Phase is not (MusouPhase.Won or MusouPhase.Lost) || _endSeenAt < 0) return;
        float a = (float)Math.Clamp((_time - _endSeenAt) / 0.4, 0.0, 1.0);
        bool won = d.Phase == MusouPhase.Won;
        const float w = 560f, h = 206f;
        float x = vs.X / 2f - w / 2f, y = 196f, cx = vs.X / 2f;
        Panel(x, y, w, h, a);
        Txt(won ? "THE WARBOSS IS DEAD" : "THE HORDE PREVAILS", cx, y + 48f, 38, new Color(won ? Gold : RedText, a), 0, 5, true);
        string sub = won ? "VICTORY" : _endSub.Of((long)d.Wave << 8 | d.Waves, static k => $"DEFEAT ON WAVE {Math.Max(1, k >> 8)} / {Math.Max(1, k & 0xFF)}");
        Txt(sub, cx, y + 80f, 22, new Color(White, a), 0, 3, true);
        Txt("KOs", cx - 120f, y + 116f, 20, new Color(Grey, a), 0, 3, true);
        Txt(_kos.Of(Data.Kos, Thousands), cx - 120f, y + 160f, 44, new Color(White, a), 0, 4, true);
        Txt("POINTS", cx + 120f, y + 116f, 20, new Color(Grey, a), 0, 3, true);
        Txt(_points.Of(Data.Points, Thousands), cx + 120f, y + 160f, 44, new Color(Gold, a), 0, 4, true);
        string next = Data.NextRunIn >= 0f ? _nextRun.Of((long)MathF.Ceiling(Data.NextRunIn), static k => $"Restart in {k} s, then the countdown to wave 1") : "A new run follows the match's restart";
        Txt(next, cx, y + 192f, 18, new Color(Grey, a), 0, 3);
    }
}
