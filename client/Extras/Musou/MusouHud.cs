using System;
using System.Collections.Generic;
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
/// <item>the end-of-run panel for a victory or a defeat (the wave reached, the KOs and points) during the match end splash.</item>
/// </list>
/// Layout, sizes and colours are ours (kind: ours; the original has no such mode). <see cref="NetworkView"/> fills <see cref="Data"/> every frame.
/// </summary>
public sealed partial class MusouHud : Control
{
    /// <summary>What the Musou HUD shows this frame (replaced by the view every frame).</summary>
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

    sealed record CallOutLine(string Text, Color Colour, double At, double Seconds);

    readonly List<CallOutLine> _callOuts = new();
    FontVariation? _font, _fontBold;
    double _time;
    double _flashAt = double.NegativeInfinity;
    Color _flash;
    float _bossShown = -1f, _bossTrail = -1f;
    ushort _bossId;
    double _endSeenAt = -1;

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

    /// <summary>A call-out in the centre (the newest replaces an older one of the same text); <paramref name="flash"/> tints the screen for a moment.</summary>
    public void CallOut(string text, Color colour, double seconds = 2.2, Color? flash = null)
    {
        _callOuts.RemoveAll(c => c.Text == text);
        _callOuts.Add(new CallOutLine(text, colour, _time, seconds));
        while (_callOuts.Count > 2) _callOuts.RemoveAt(0);
        if (flash is { } f) { _flash = f; _flashAt = _time; }
    }

    /// <summary>The Waaagh! call-out (orange flash) and the enrage call-out (red flash).</summary>
    public void Waaagh() => CallOut("WAAAGH!", Orange, 2.4, new Color(1f, 0.4f, 0.02f, 0.32f));
    public void Enraged() => CallOut("ENRAGED", RedText, 2.4, new Color(0.9f, 0.02f, 0.02f, 0.26f));

    public override void _Process(double delta)
    {
        _time += delta;
        _callOuts.RemoveAll(c => _time - c.At > c.Seconds);
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
        float w = TextWidth(s, size, bold);
        float px = align < 0 ? x : align == 0 ? x - w / 2f : x - w;
        var pos = new Vector2(px, y);
        if (outline > 0) DrawStringOutline(font, pos, s, HorizontalAlignment.Left, -1, size, outline, new Color(0f, 0f, 0f, c.A * 0.8f));
        DrawString(font, pos, s, HorizontalAlignment.Left, -1, size, c);
    }

    /// <summary>A box with two chamfered corners (top right, bottom left), like the match HUD's weapon box.</summary>
    void Panel(float x, float y, float w, float h, float alpha = 1f)
    {
        const float cut = 14f;
        Vector2[] pts = [new(x, y), new(x + w - cut, y), new(x + w, y + cut), new(x + w, y + h), new(x + cut, y + h), new(x, y + h - cut)];
        DrawColoredPolygon(pts, new Color(PanelFill, PanelFill.A * alpha));
        var closed = new Vector2[pts.Length + 1];
        Array.Copy(pts, closed, pts.Length);
        closed[^1] = pts[0];
        DrawPolyline(closed, new Color(FrameGold, FrameGold.A * alpha), 2f, true);
    }

    static string Thousands(int v) => v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

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
        DrawRunPanel(d);
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
        string wave = d.Phase switch
        {
            MusouPhase.Waiting => "GET READY",
            MusouPhase.Boss => "FINAL WAVE",
            _ => $"WAVE {Math.Max(1, (int)d.Wave)} / {Math.Max(1, (int)d.Waves)}",
        };
        Txt(wave, lx, y + 44f, 34, Gold, -1, 4, true);
        if (d.Phase == MusouPhase.Boss) Txt($"{d.Wave} / {d.Waves}", rx, y + 44f, 22, Grey, 1, 3);
        Txt("ENEMIES LEFT", lx, y + 78f, 18, Grey, -1, 3, true);
        Txt(d.Phase is MusouPhase.Break or MusouPhase.Waiting ? "-" : Thousands(d.EnemiesLeft), rx, y + 80f, 26, White, 1, 3, true);
        Txt("LIVES", lx, y + 112f, 18, Grey, -1, 3, true);
        int max = Math.Max(Data.LivesMax, d.Lives);
        if (max <= 9)
            for (int i = 0; i < max; i++)
            {
                var c = new Vector2(rx - 8f - i * 22f, y + 105f);
                bool left = max - 1 - i < d.Lives;
                Vector2[] diamond = [c + new Vector2(0, -8), c + new Vector2(8, 0), c + new Vector2(0, 8), c + new Vector2(-8, 0)];
                DrawColoredPolygon(diamond, left ? Gold : new Color(0.25f, 0.25f, 0.26f, 0.9f));
            }
        else Txt(d.Lives.ToString(System.Globalization.CultureInfo.InvariantCulture), rx, y + 114f, 26, d.Lives > 0 ? White : RedText, 1, 3, true);
        Txt("KOs", lx, y + 148f, 18, Grey, -1, 3, true);
        Txt(Thousands(Data.Kos), lx + 160f, y + 150f, 26, White, 1, 3, true);
        Txt("PTS", lx + 180f, y + 148f, 18, Grey, -1, 3, true);
        Txt(Thousands(Data.Points), rx, y + 150f, 26, Gold, 1, 3, true);
    }

    /// <summary>The Warboss's bar, top centre above the armour bar, while it lives.</summary>
    void DrawBossBar(Vector2 vs, MusouDirectorView d)
    {
        if (d.BossId == 0 || d.BossMaxHealth <= 0f || _bossShown < 0f || d.Phase is MusouPhase.Won or MusouPhase.Lost) return;
        float cx = vs.X / 2f, w = Math.Min(720f, vs.X - 760f), x = cx - w / 2f, y = 52f, h = 22f;
        bool rage = Data.BossEnraged;
        float pulse = rage ? 0.75f + 0.25f * MathF.Sin((float)_time * 8f) : 1f;
        Txt("ORK WARBOSS", cx, y - 10f, 24, rage ? RedText : Gold, 0, 4, true);
        if (rage) Txt("ENRAGED", x + w, y - 10f, 20, new Color(RedText, pulse), 1, 3, true);
        Txt($"{MathF.Ceiling(Math.Clamp(d.BossHealth / d.BossMaxHealth, 0f, 1f) * 100f):F0}%", x, y - 10f, 20, White, -1, 3, true);
        DrawRect(new Rect2(x - 3f, y - 3f, w + 6f, h + 6f), PanelFill);
        if (_bossTrail > _bossShown) DrawRect(new Rect2(x + w * _bossShown, y, w * (_bossTrail - _bossShown), h), new Color(1f, 0.85f, 0.6f, 0.85f));
        var fill = rage ? new Color(1f * pulse, 0.12f, 0.04f) : RedFill;
        DrawRect(new Rect2(x, y, w * _bossShown, h), fill);
        DrawRect(new Rect2(x, y, w * _bossShown, 4f), new Color(1f, 0.55f, 0.45f, 0.55f));
        foreach (float mark in new[] { 0.25f, 0.5f, 0.75f }) DrawLine(new Vector2(x + w * mark, y - 2f), new Vector2(x + w * mark, y + h + 2f), new Color(0f, 0f, 0f, 0.85f), 3f);
        DrawRect(new Rect2(x - 3f, y - 3f, w + 6f, h + 6f), FrameGold, false, 2f);
    }

    /// <summary>The KO chain, right of the crosshair and clear of the weapon-switch cards: the multiplier, the chain and the window left.</summary>
    void DrawChain(Vector2 vs)
    {
        if (Data.Chain <= 0 || Data.ChainLeft <= 0f || !Data.Alive) return;
        float rx = vs.X - 64f, y = 470f;
        float a = Math.Clamp(Data.ChainLeft * 4f, 0f, 1f);
        Txt($"x{Data.Multiplier:0.0}", rx, y, 56, new Color(Gold, a), 1, 5, true);
        Txt($"CHAIN {Data.Chain + 1}", rx, y + 32f, 22, new Color(White, a), 1, 3, true);
        const float bw = 170f;
        DrawRect(new Rect2(rx - bw, y + 44f, bw, 6f), new Color(0f, 0f, 0f, 0.6f * a));
        DrawRect(new Rect2(rx - bw * Data.ChainLeft, y + 44f, bw * Data.ChainLeft, 6f), new Color(Gold, a));
    }

    /// <summary>Between waves: the wave cleared and the countdown to the next.</summary>
    void DrawBreak(Vector2 vs, MusouDirectorView d)
    {
        if (d.Phase != MusouPhase.Break) return;
        float cx = vs.X / 2f, y = Data.Alive ? 300f : 470f;
        Txt($"WAVE {d.Wave} CLEARED", cx, y, 26, Gold, 0, 4, true);
        Txt($"NEXT WAVE IN {MathF.Ceiling(Math.Max(0f, Data.BreakLeft)):F0}", cx, y + 44f, 36, White, 0, 5, true);
    }

    void DrawCallOuts(Vector2 vs)
    {
        float cx = vs.X / 2f, y = 300f;
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
    /// The end of the run: victory or defeat, the wave reached, the KOs and points. Under the armour bar, on the match end splash's upper band and above its word
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
        Txt(won ? "VICTORY" : $"DEFEAT ON WAVE {Math.Max(1, (int)d.Wave)} / {Math.Max(1, (int)d.Waves)}", cx, y + 80f, 22, new Color(White, a), 0, 3, true);
        Txt("KOs", cx - 120f, y + 116f, 20, new Color(Grey, a), 0, 3, true);
        Txt(Thousands(Data.Kos), cx - 120f, y + 160f, 44, new Color(White, a), 0, 4, true);
        Txt("POINTS", cx + 120f, y + 116f, 20, new Color(Grey, a), 0, 3, true);
        Txt(Thousands(Data.Points), cx + 120f, y + 160f, 44, new Color(Gold, a), 0, 4, true);
        Txt("A new run starts shortly", cx, y + 192f, 18, new Color(Grey, a), 0, 3);
    }
}
