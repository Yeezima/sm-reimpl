using System;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The capture tracker (the owner, 2026-11-06: "an on-screen capture-progress indicator that matches the existing UI theme and clearly communicates capture status"). Top left, one tile a position (A, B, C, then the
/// commander's HQ): the letter in its state's colour (the world letters' colours, <see cref="TfwMarkers"/>) in the HUD's dark panel with the gold frame, and under it a bar: the securing progress in gold, full green when
/// secured, full red while the Orks hold it, orange pulsing while it is being cleared, empty grey while locked. On a position (<see cref="Frame.OnPosition"/>) a wider bar under the prompt says what is happening there and how far
/// it is. Layout, sizes and colours are ours (kind: ours); every figure is tag 8's. <c>--tfw-no-capture-hud</c> (test aid) leaves it out.
/// </summary>
public sealed partial class TfwHud
{
    static readonly Color FrameGold = new(0.74f, 0.64f, 0.32f, 0.95f), PanelFill = new(0.02f, 0.02f, 0.025f, 0.84f);
    static readonly Color Locked = new(0.45f, 0.45f, 0.48f), Held = new(0.9f, 0.12f, 0.08f), Clearing = new(1f, 0.5f, 0.08f), Securing = new(1f, 0.85f, 0.2f), Secured = new(0.3f, 0.9f, 0.35f);
    readonly bool _noCaptureHud = Array.IndexOf(OS.GetCmdlineUserArgs(), "--tfw-no-capture-hud") >= 0;

    /// <summary>A position's colour by its state.</summary>
    public static Color StateColour(TfwPositionState s) => s switch
    {
        TfwPositionState.Held => Held, TfwPositionState.Clearing => Clearing, TfwPositionState.Securing => Securing, TfwPositionState.Secured => Secured, _ => Locked,
    };

    double _clock;

    void DrawCapture(Vector2 vs)
    {
        if (_noCaptureHud || Data.Objectives.Count == 0 || Data.War is not { } war || !TfwHudModel.InBattle(war.Phase)) return;
        _clock = Time.GetTicksMsec() / 1000.0;
        float pulse = 0.65f + 0.35f * MathF.Sin((float)_clock * 6f);
        const float x0 = 40f, y0 = 40f, tile = 56f, gap = 10f, barH = 7f;
        for (int k = 0; k < Data.Objectives.Count; k++)
        {
            var o = Data.Objectives[k];
            var c = StateColour(o.State);
            float x = x0 + k * (tile + gap);
            bool here = k == Data.OnPosition;
            DrawRect(new Rect2(x, y0, tile, tile), PanelFill);
            DrawRect(new Rect2(x, y0, tile, tile), here ? Gold : FrameGold, false, here ? 3f : 2f);
            Txt(TfwMarkerRules.TileName(k), x + tile / 2f, y0 + tile / 2f + (k < WarRules.Positions ? 14f : 9f), k < WarRules.Positions ? 38 : 24, c, 0, 4, true);
            float by = y0 + tile + 5f;
            DrawRect(new Rect2(x - 1f, by - 1f, tile + 2f, barH + 2f), PanelFill);
            DrawRect(new Rect2(x, by, tile, barH), PipOff);
            float fill = TfwMarkerRules.BarFill(o);
            var barColour = o.State == TfwPositionState.Clearing ? new Color(c, pulse) : c;
            if (o.State == TfwPositionState.Clearing && fill <= 0f) { fill = 1f; barColour = new Color(c, 0.35f * pulse); }
            if (fill > 0f) DrawRect(new Rect2(x, by, tile * fill, barH), barColour);
        }
        if (Data.OnPosition < 0 || Data.OnPosition >= Data.Objectives.Count) return;
        var on = Data.Objectives[Data.OnPosition];
        var col = StateColour(on.State);
        float cx = vs.X / 2f, w = 440f, h = 12f, y = 760f;
        int orks = Data.OnPosition < Data.OrksOn.Length ? Data.OrksOn[Data.OnPosition] : 0;
        Txt(TfwMarkerRules.OnPointLine(on, Data.OnPosition, orks), cx, y - 10f, 24, col, 0, 4, true);
        DrawRect(new Rect2(cx - w / 2f - 3f, y - 3f, w + 6f, h + 6f), PanelFill);
        DrawRect(new Rect2(cx - w / 2f, y, w, h), PipOff);
        float f = TfwMarkerRules.BarFill(on);
        if (f > 0f) DrawRect(new Rect2(cx - w / 2f, y, w * f, h), on.State == TfwPositionState.Clearing ? new Color(col, pulse) : col);
        DrawRect(new Rect2(cx - w / 2f - 3f, y - 3f, w + 6f, h + 6f), FrameGold, false, 2f);
    }
}
