using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The war's HUD (design section G), over the match HUD as its child in the same 1080-high space and faces as <see cref="MusouHud"/>, whose run panel it replaces in the Campaign phase. Top left, always: a
/// strip per front (pips and progress), the HQ, the transport and supplies, the war's clock and scale. Under it in a battle: the objective tracker, the Guardsmen, the retainer and its order, the own KOs
/// and the order keys. In the middle: the station's prompt, or what the run asks now (travel, pickup countdown, revive, extraction, the end) and the hold of P. Every figure comes from tags 7 and 8.
/// Layout, sizes and colours are ours (kind: ours). <see cref="NetworkView"/> fills <see cref="Data"/> every frame and calls <see cref="Fed"/>; without that for two frames the HUD hides itself.
/// </summary>
public sealed partial class TfwHud : Control
{
    /// <summary>What the HUD shows this frame.</summary>
    public sealed class Frame
    {
        public TfwWarView? War;
        public IReadOnlyList<TfwObjective> Objectives = Array.Empty<TfwObjective>();
        public bool Alive = true;
        /// <summary>The station's prompt ("" away from one).</summary>
        public string Station = "";
        public float PickupHold;
        public int Kos;
        /// <summary>The map's points and the run's total (tag 12), and the line of keys 3 to 5.</summary>
        public uint MapPoints, RunTotal;
        public string Slots = "";
    }

    public Frame Data = new();

    const float RefHeight = 1080f, PanelX = 36f, PanelY = 60f, PanelW = 372f, RowH = 28f;
    static readonly Color Gold = new(0.98f, 0.8f, 0.12f), RedText = new(0.9f, 0.08f, 0.07f), Grey = new(0.62f, 0.62f, 0.64f), White = new(0.93f, 0.93f, 0.95f),
        Green = new(0.35f, 0.85f, 0.4f), FrameGold = new(0.74f, 0.64f, 0.32f, 0.95f), PanelFill = new(0.02f, 0.02f, 0.025f, 0.84f), PipOff = new(0.25f, 0.25f, 0.26f, 0.9f), RedFill = new(0.86f, 0.04f, 0.04f);

    readonly Vector2[] _panel = new Vector2[6], _closed = new Vector2[7];
    FontVariation? _font, _fontBold;
    ulong _fedFrame;
    TfwWarView? _textFor;
    IReadOnlyList<TfwObjective>? _objectivesFor;
    bool _aliveFor;
    string _hq = "", _reserves = "", _clock = "", _prompt = "", _guards = "", _retainer = "", _kosText = "", _pickupText = "";
    int _kosFor = -1;
    uint _pointsFor = uint.MaxValue, _totalFor = uint.MaxValue;
    string _pointsText = "";
    readonly string[] _lanes = new string[3], _objectiveNames = new string[4], _objectiveStates = new string[4];

    /// <summary>The big line now (the tests read it).</summary>
    public string Prompt => _prompt;

    public TfwHud()
    {
        Name = "TfwHud";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        TextureFilter = TextureFilterEnum.Linear;
    }

    public override void _Ready()
    {
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

    /// <summary>The view filled <see cref="Data"/> this frame: the texts follow, only when what they show changed.</summary>
    public void Fed()
    {
        _fedFrame = Engine.GetProcessFrames();
        if (Data.War is not { } war) return;
        if (_textFor is { } was && was == war && ReferenceEquals(_objectivesFor, Data.Objectives) && _aliveFor == Data.Alive && _kosFor == Data.Kos && _pointsFor == Data.MapPoints && _totalFor == Data.RunTotal) return;
        _textFor = war;
        _objectivesFor = Data.Objectives;
        _aliveFor = Data.Alive;
        _kosFor = Data.Kos;
        _pointsFor = Data.MapPoints;
        _totalFor = Data.RunTotal;
        _pointsText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"POINTS {Data.MapPoints:N0}    RUN {Data.RunTotal:N0}");
        _hq = TfwHudModel.HqText(war);
        _reserves = $"TRANSPORT {war.Transport}    SUPPLIES {war.Supplies}";
        _clock = $"WAR {TfwHudModel.ClockText(war.Clock)}    {TfwHudModel.ScaleText(war.TimeScale)}";
        _prompt = TfwHudModel.Prompt(war, Data.Alive);
        _pickupText = TfwHudModel.PickupKeyText(war);
        _guards = TfwHudModel.GuardsText(war.Own);
        _retainer = TfwHudModel.RetainerText(war.Own);
        _kosText = $"KOs {Data.Kos}";
        for (int l = 0; l < 3; l++) _lanes[l] = $"{(TfwLane)l}  {TfwHudModel.FrontText(TfwHudModel.Lane(war, (TfwLane)l).Front)}";
        bool final = war.Phase == TfwRunPhase.FinalAssault;
        for (int k = 0; k < Math.Min(_objectiveNames.Length, Data.Objectives.Count); k++)
        {
            var o = Data.Objectives[k];
            _objectiveNames[k] = TfwHudModel.ObjectiveName(o.Index, final);
            _objectiveStates[k] = TfwHudModel.ObjectiveState(o);
        }
    }

    public override void _Process(double delta)
    {
        if (Engine.GetProcessFrames() - _fedFrame > 2) { Visible = false; return; }
        QueueRedraw();
    }

    void Txt(string s, float x, float y, int size, Color c, int align = -1, int outline = 3, bool bold = false)
    {
        if ((bold ? _fontBold : _font) is not { } font || s.Length == 0) return;
        float width = font.GetStringSize(s, HorizontalAlignment.Left, -1, size).X;
        var pos = new Vector2(align < 0 ? x : align == 0 ? x - width / 2f : x - width, y);
        if (outline > 0) DrawStringOutline(font, pos, s, HorizontalAlignment.Left, -1, size, outline, new Color(0f, 0f, 0f, c.A * 0.8f));
        DrawString(font, pos, s, HorizontalAlignment.Left, -1, size, c);
    }

    void Panel(float x, float y, float w, float h)
    {
        const float cut = 14f;
        _panel[0] = new(x, y); _panel[1] = new(x + w - cut, y); _panel[2] = new(x + w, y + cut);
        _panel[3] = new(x + w, y + h); _panel[4] = new(x + cut, y + h); _panel[5] = new(x, y + h - cut);
        DrawColoredPolygon(_panel, PanelFill);
        for (int i = 0; i < 6; i++) _closed[i] = _panel[i];
        _closed[6] = _panel[0];
        DrawPolyline(_closed, FrameGold, 2f, true);
    }

    public override void _Draw()
    {
        if (_font is null || Data.War is not { } war) return;
        float scale = Size.Y / RefHeight;
        if (scale <= 0f) return;
        DrawSetTransformMatrix(new Transform2D(0f, new Vector2(scale, scale), 0f, Vector2.Zero));
        var vs = Size / scale;
        DrawWar(war);
        if (TfwHudModel.InBattle(war.Phase) && Data.Objectives.Count > 0) DrawBattle();
        DrawPrompts(vs);
    }

    /// <summary>The strips, the HQ, the reserves and the clock.</summary>
    void DrawWar(in TfwWarView war)
    {
        const float h = 3 * RowH + 3 * 26f + 22f;
        Panel(PanelX, PanelY, PanelW, h);
        float lx = PanelX + 18f, rx = PanelX + PanelW - 18f, y = PanelY + 26f;
        for (int l = 0; l < 3; l++, y += RowH)
        {
            var lane = TfwHudModel.Lane(war, (TfwLane)l);
            bool here = TfwHudModel.InBattle(war.Phase) && war.BattleLane == (TfwLane)l;
            Txt(_lanes[l], lx, y, 18, here ? Gold : lane.Front == WarRules.FrontBesieged ? RedText : White, -1, 3, true);
            float barX = rx - 120f, barW = 120f, mid = barX + barW / 2f, top = y - 12f;
            int pips = TfwHudModel.Pips(lane.Front);
            for (int i = 0; i < 5 - 1; i++) DrawRect(new Rect2(barX - 56f + i * 12f, top, 9f, 12f), i < pips ? Gold : PipOff);
            DrawRect(new Rect2(barX, top + 3f, barW, 7f), PipOff);
            float fill = Math.Clamp(lane.Progress, -1f, 1f) * barW / 2f;
            if (fill >= 0f) DrawRect(new Rect2(mid, top + 3f, fill, 7f), Gold);
            else DrawRect(new Rect2(mid + fill, top + 3f, -fill, 7f), RedFill);
        }
        y += 2f;
        Txt(_hq, lx, y, 18, _hq.StartsWith("HQ HOLDS", StringComparison.Ordinal) ? Grey : RedText, -1, 3, true);
        Txt(_reserves, lx, y + 26f, 18, White, -1, 3, true);
        Txt(_clock, lx, y + 52f, 16, Grey);
    }

    /// <summary>The objectives, the Guardsmen, the retainer, the KOs and the keys.</summary>
    void DrawBattle()
    {
        float y0 = PanelY + 3 * RowH + 3 * 26f + 22f + 12f;
        int rows = Math.Min(_objectiveNames.Length, Data.Objectives.Count);
        float h = rows * 24f + 6 * 24f + 44f;
        Panel(PanelX, y0, PanelW, h);
        float lx = PanelX + 18f, rx = PanelX + PanelW - 18f, y = y0 + 26f;
        for (int k = 0; k < rows; k++, y += 24f)
        {
            var o = Data.Objectives[k];
            var c = o.State switch { TfwPositionState.Secured => Green, TfwPositionState.Locked => Grey, TfwPositionState.Held => RedText, _ => Gold };
            Txt(_objectiveNames[k], lx, y, 17, c, -1, 3, true);
            Txt(_objectiveStates[k], rx, y, 17, c, 1, 3, true);
        }
        y += 6f;
        Txt(_guards, lx, y, 16, White);
        Txt(_retainer, lx, y + 24f, 16, White);
        Txt(_kosText, rx, y + 24f, 16, Gold, 1);
        Txt(_pointsText, lx, y + 48f, 16, Gold);
        Txt(Data.Slots, lx, y + 72f, 14, White);
        if (Data.War is { } war) Txt(TfwHudModel.OrbitalText, lx, y + 96f, 16, Gold);
        Txt("Z FOLLOW   X HOLD   V ATTACK   HOLD P PICKUP", lx, y + 122f, 14, Grey);
    }

    void DrawPrompts(Vector2 vs)
    {
        float cx = vs.X / 2f;
        if (!Data.Alive && Data.War is { } war && TfwHudModel.RunHoldsRespawn(war)) Txt("KILLED", cx, 330f, 52, RedText, 0, 6, true);
        if (Data.Station.Length > 0) Txt(Data.Station, cx, 640f, 30, Gold, 0, 4, true);
        else Txt(_prompt, cx, 640f, 34, _prompt.StartsWith("THE WAR IS LOST", StringComparison.Ordinal) ? RedText : Gold, 0, 5, true);
        if (Data.PickupHold <= 0f) return;
        const float w = 260f, y = 672f;
        Txt(_pickupText, cx, y - 6f, 18, White, 0, 3, true);
        DrawRect(new Rect2(cx - w / 2f, y, w, 8f), PipOff);
        DrawRect(new Rect2(cx - w / 2f, y, w * Math.Clamp(Data.PickupHold, 0f, 1f), 8f), Gold);
    }
}
