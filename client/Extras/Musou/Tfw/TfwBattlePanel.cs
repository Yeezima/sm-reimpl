using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The battle's panel on the M map (<see cref="WarMapPanel"/>; until 2026-11-02 it stood top left on <see cref="TfwHud"/>, the owner moved it): the objective tracker, the Guardsmen, the retainer and its
/// order, the own KOs, the points, the keys 3 to 5 and the order keys. It shows only in a battle. <see cref="NetworkView"/> feeds it from the HUD's <see cref="TfwHud.Frame"/> while M is held. Layout, sizes
/// and colours are ours (kind: ours); the panel's top left corner is its own origin, in the map's 1080-high space.
/// </summary>
public sealed partial class TfwBattlePanel : Control
{
    public const float PanelWidth = 372f, PanelMaxHeight = 10 * RowH + 44f;
    const float RowH = 24f;
    static readonly Color Gold = new(0.98f, 0.8f, 0.12f), RedText = new(0.9f, 0.08f, 0.07f), Grey = new(0.62f, 0.62f, 0.64f), White = new(0.93f, 0.93f, 0.95f),
        Green = new(0.35f, 0.85f, 0.4f), FrameGold = new(0.74f, 0.64f, 0.32f, 0.95f), PanelFill = new(0.02f, 0.02f, 0.025f, 0.84f);

    readonly Vector2[] _panel = new Vector2[6], _closed = new Vector2[7];
    readonly string[] _objectiveNames = new string[4], _objectiveStates = new string[4];
    FontVariation? _font, _fontBold;
    TfwHud.Frame? _data;
    TfwWarView? _textFor;
    IReadOnlyList<TfwObjective>? _objectivesFor;
    int _orksFor, _kosFor = -1;
    uint _pointsFor = uint.MaxValue, _totalFor = uint.MaxValue;
    string _guards = "", _retainer = "", _kosText = "", _pointsText = "";

    /// <summary>The tracker's rows and the retainer's line now (the tests read them).</summary>
    public string Tracker => string.Join(" | ", _objectiveStates);
    public string Retainer => _retainer;

    public TfwBattlePanel()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.Linear;
    }

    public override void _Ready() => (_font, _fontBold) = TfwHud.MakeFonts();

    /// <summary>This frame's data: the texts follow, only when what they show changed.</summary>
    public void Feed(TfwHud.Frame data)
    {
        _data = data;
        Visible = data.War is { } shown && TfwHudModel.InBattle(shown.Phase) && data.Objectives.Count > 0;
        if (!Visible || data.War is not { } war) return;
        QueueRedraw();
        int orksSig = 0;
        foreach (int n in data.OrksOn) orksSig = orksSig * 31 + n;
        if (_textFor is { } was && was == war && ReferenceEquals(_objectivesFor, data.Objectives) && _kosFor == data.Kos && _orksFor == orksSig && _pointsFor == data.MapPoints && _totalFor == data.RunTotal) return;
        _orksFor = orksSig;
        _textFor = war;
        _objectivesFor = data.Objectives;
        _kosFor = data.Kos;
        _pointsFor = data.MapPoints;
        _totalFor = data.RunTotal;
        _pointsText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"POINTS {data.MapPoints:N0}    RUN {data.RunTotal:N0}");
        _guards = TfwHudModel.GuardsText(war.Own);
        _retainer = TfwHudModel.RetainerText(war.Own);
        _kosText = $"KOs {data.Kos}";
        bool final = war.Phase == TfwRunPhase.FinalAssault;
        for (int k = 0; k < Math.Min(_objectiveNames.Length, data.Objectives.Count); k++)
        {
            var o = data.Objectives[k];
            _objectiveNames[k] = TfwHudModel.ObjectiveName(o.Index, final);
            _objectiveStates[k] = TfwHudModel.ObjectiveState(o) + TfwHudModel.ClearingNote(o, k < data.OrksOn.Length ? data.OrksOn[k] : 0);
        }
        // The one to work on next: the first position not secured, then the HQ (the battle's active position; the retainer's Attack goes there too).
        int next = -1;
        for (int k = 0; k < Math.Min(_objectiveStates.Length, data.Objectives.Count) && next < 0; k++) if (data.Objectives[k].State != TfwPositionState.Secured) next = k;
        if (next >= 0 && data.Objectives[next].State != TfwPositionState.Locked) _objectiveStates[next] += "   < NEXT";
    }

    void Txt(string s, float x, float y, int size, Color c, int align = -1, bool bold = false) => TfwHud.Text(this, bold ? _fontBold : _font, s, x, y, size, c, align, 3);

    void Frame(float w, float h)
    {
        const float cut = 14f;
        _panel[0] = new(0f, 0f); _panel[1] = new(w - cut, 0f); _panel[2] = new(w, cut);
        _panel[3] = new(w, h); _panel[4] = new(cut, h); _panel[5] = new(0f, h - cut);
        DrawColoredPolygon(_panel, PanelFill);
        for (int i = 0; i < 6; i++) _closed[i] = _panel[i];
        _closed[6] = _panel[0];
        DrawPolyline(_closed, FrameGold, 2f, true);
    }

    public override void _Draw()
    {
        if (_data is not { } data) return;
        int rows = Math.Min(_objectiveNames.Length, data.Objectives.Count);
        Frame(PanelWidth, rows * RowH + 6 * RowH + 44f);
        float lx = 18f, rx = PanelWidth - 18f, y = 26f;
        for (int k = 0; k < rows; k++, y += RowH)
        {
            var c = data.Objectives[k].State switch { TfwPositionState.Secured => Green, TfwPositionState.Locked => Grey, TfwPositionState.Held => RedText, _ => Gold };
            Txt(_objectiveNames[k], lx, y, 17, c, -1, true);
            Txt(_objectiveStates[k], rx, y, 17, c, 1, true);
        }
        y += 6f;
        Txt(_guards, lx, y, 16, White);
        Txt(_retainer, lx, y + RowH, 16, White);
        Txt(_kosText, rx, y + RowH, 16, Gold, 1);
        Txt(_pointsText, lx, y + 2 * RowH, 16, Gold);
        Txt(data.Slots, lx, y + 3 * RowH, 14, White);
        Txt(TfwHudModel.OrbitalText, lx, y + 4 * RowH, 16, Gold);
        Txt("Z FOLLOW   X HOLD   V ATTACK   HOLD P PICKUP", lx, y + 122f, 14, Grey);
    }
}
