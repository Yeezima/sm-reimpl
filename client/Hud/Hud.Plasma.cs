using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmFormats.Gfx;

namespace SmClient;

// fix/plasma-visuals (owner, 2026-11-06): the plasma weapons' gauges at the crosshair, from the HUD movie's reticle container (SmContent.PlasmaGaugeRules): the heat arc under the
// reticle (heat_mc: meter_mc with its vent_txt, overheat_mc's danger flash) and the charge arc over it (PlasmaCharge_mc / PlasmaCannonCharge_mc). Before, only the reticle itself
// (and its charge rotation) was drawn. --old-plasma-gauges draws none of them (before / after).
public sealed partial class Hud
{
    static readonly bool OldPlasmaGauges = Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-plasma-gauges") >= 0;
    /// <summary><c>--old-plasma-spin</c>: the plasma reticle plays its movie clip's charge frames (it turns while a charge builds), as before 2026-11-06; by default it stays on its first frame.</summary>
    static readonly bool OldPlasmaSpin = Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-plasma-spin") >= 0;

    /// <summary>The last piece drawn per gauge (heat, overheat, charge): drawn again while the next frame's piece renders.</summary>
    readonly Dictionary<string, HudMovieArt.Pic> _plasmaLast = new();
    double _overheatSince = double.NegativeInfinity;
    bool _wasOverheated;

    /// <summary>The plasma heat arc (drawn under the reticle, as the movie's depth 1 is under every reticle). Nothing for other families or with <c>--old-plasma-gauges</c>.</summary>
    void DrawPlasmaHeat(HudMovie hud, ReticleSpec spec, Vector2 c)
    {
        if (OldPlasmaGauges || _movieArt is null || !PlasmaGaugeRules.IsPlasma(spec.Family)) return;
        var d = Data;
        if (d.Overheated && !_wasOverheated) _overheatSince = _time;
        _wasOverheated = d.Overheated;
        string heatPath = $"{ReticleRules.Container}/{PlasmaGaugeRules.Heat}";
        if (hud.Find(heatPath) is not { } heat) return;
        int heatSprite = heat.Placement.Character;
        string label = PlasmaGaugeRules.HeatLabel(spec.Family);
        int top = hud.Labels(heatSprite).TryGetValue(label, out int lf) ? lf : 0;
        Func<int, int?> placeAt = id => id == heatSprite ? top : null;   // the label's frame places the meter and the flash (the cannon's larger)

        // The meter: its frame by the heat; on its last frame the movie's vent_txt shows, its own clip looping at 30 fps.
        string meterPath = $"{heatPath}/{PlasmaGaugeRules.Meter}";
        if (hud.Find(meterPath, placeAt) is { } meter)
        {
            int frames = hud.FrameCount(meter.Placement.Character);
            int frame = d.HasHeat ? PlasmaGaugeRules.MeterFrame(d.Heat, d.HeatMax, frames) : 0;
            int vent = frame >= frames - 1 && hud.Find($"{meterPath}/vent_txt") is { } vt ? PlasmaGaugeRules.Looped(_time, hud.FrameCount(vt.Placement.Character)) : 0;
            int ventSprite = frame >= frames - 1 ? hud.Find($"{meterPath}/vent_txt")?.Placement.Character ?? -1 : -1;
            DrawGauge(hud, $"pheat:{label}:{frame}:{vent}", "pheat", meterPath, placeAt, frame, ch => ch == ventSprite ? vent : 0, c, 80);
        }

        // The overheat flash: the danger label's frames while overheated, its nested clips looping at 30 fps from the overheat's start.
        string flashPath = $"{heatPath}/{PlasmaGaugeRules.Overheat}";
        if (d.Overheated && hud.Find(flashPath, placeAt) is { } flash)
        {
            int ch0 = flash.Placement.Character;
            int frames = hud.FrameCount(ch0);
            int danger = hud.Labels(ch0).TryGetValue(PlasmaGaugeRules.DangerLabel, out int df) ? df : 1;
            double since = _time - _overheatSince;
            int oh = PlasmaGaugeRules.OverheatFrame(true, since, danger, frames);
            int tick = PlasmaGaugeRules.Looped(since, 40);   // the nested clips' 8 and 10 frames repeat every 40
            DrawGauge(hud, $"poh:{label}:{oh}:{tick}", "poh", flashPath, placeAt, oh, ch => PlasmaGaugeRules.Looped(tick / ReticleRules.FramesPerSecond, hud.FrameCount(ch)), c, 48);
        }
    }

    /// <summary>The plasma charge arc over the reticle (the movie places it above the plasma gun's reticle): the dim arc, filling while a charge builds.</summary>
    void DrawPlasmaCharge(HudMovie hud, ReticleSpec spec, Vector2 c)
    {
        if (OldPlasmaGauges || _movieArt is null || !PlasmaGaugeRules.IsPlasma(spec.Family)) return;
        string name = PlasmaGaugeRules.ChargeArc(spec.Family);
        string path = $"{ReticleRules.Container}/{name}";
        if (hud.Find(path) is not { } arc) return;
        int frames = hud.FrameCount(arc.Placement.Character);
        int frame = PlasmaGaugeRules.ChargeFrame(Data.ChargeToFull, frames);
        DrawGauge(hud, $"pchg:{name}:{frame}", "pchg:" + name, path, null, frame, _ => 0, c, 64);
    }

    /// <summary>
    /// Draws one gauge piece (the placement at <paramref name="path"/> at frame <paramref name="top"/>, its nested clips at <paramref name="nested"/>'s frames) in the reticle container's
    /// space; until it is rendered, the gauge's last piece.
    /// </summary>
    void DrawGauge(HudMovie hud, string key, string group, string path, Func<int, int?>? placeAt, int top, Func<int, int> nested, Vector2 c, int groupMax)
    {
        var pic = _movieArt!.Get(key, (m, s) => RenderInContainer(m.Hud, path, placeAt, top, nested, s), group, groupMax);
        if (pic is not null) { _plasmaLast[group] = pic; _movieArt.Hold(group, key); }
        else if (_plasmaLast.TryGetValue(group, out var last)) pic = last;
        if (pic is not null) DrawReticlePic(pic, c);
    }

    /// <summary>A placement rendered in the reticle container's space (origin: the container's), with its own colour transform and blend; null when missing or empty.</summary>
    static HudPicture? RenderInContainer(HudMovie? hud, string path, Func<int, int?>? placeAt, int top, Func<int, int> nested, double scale)
    {
        if (hud?.Find(path, placeAt) is not { } found || hud.Find(ReticleRules.Container)?.ToStage.Inverse() is not { } inv) return null;
        var p = found.Placement;
        return hud.Render(p.Character, top, scale, inv.After(found.ToStage), nested, colour: p.Colour, add: p.Blend == 8);
    }

    // ---------------------------------------------------------------- --hud-plasma-demo <prefix>

    /// <summary>
    /// <c>--hud-plasma-demo &lt;prefix&gt;</c> (screenshot aid, fix/plasma-visuals): with a plasma weapon in hand, steps the HUD through cold, half heat, high heat, overheated, a quarter, half, three quarters
    /// and full charge (the values are shown, not simulated), saving the window as <c>&lt;prefix&gt;_&lt;state&gt;.png</c> for each, then quits.
    /// </summary>
    static readonly string? PlasmaDemo = ReadPlasmaDemo();
    double _plasmaDemoAt;
    int _plasmaDemoStep;
    static readonly (string Name, float Heat, bool Overheated, float Charge)[] PlasmaDemoStates =
        [("cold", 0f, false, -1f), ("mid", 0.5f, false, -1f), ("hot", 0.85f, false, -1f), ("overheated", 1f, true, -1f), ("charge_quarter", 0.2f, false, 0.25f), ("charge_half", 0.2f, false, 0.5f), ("charge_3q", 0.2f, false, 0.75f), ("charge_full", 0.2f, false, 1f)];

    static string? ReadPlasmaDemo()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--hud-plasma-demo");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    void RunPlasmaDemo()
    {
        if (PlasmaDemo is null || !Data.InGame || !Data.HasWeapon) return;
        var d = Data;
        _plasmaDemoAt += GetProcessDeltaTime();
        if (_plasmaDemoStep >= PlasmaDemoStates.Length) { GetTree().Quit(); return; }
        var (name, heat, overheated, charge) = PlasmaDemoStates[_plasmaDemoStep];
        d.Alive = true;
        if (d.HasHeat) { d.Heat = d.HeatMax * heat; d.Overheated = overheated; }
        d.Charge = charge; d.ChargeToFull = charge; d.ChargeReady = charge >= 1f;
        if (_plasmaDemoAt < 4.0) return;   // the first state waits for the HUD movies; each later one for its pieces to render
        string file = $"{PlasmaDemo}_{d.WeaponWargear}_{name}.png";
        GD.Print(GetViewport().GetTexture().GetImage().SavePng(file) == Error.Ok ? $"plasma hud shot {name} ({d.WeaponWargear}) -> {file}" : "plasma hud shot failed");
        _plasmaDemoStep++; _plasmaDemoAt = 2.5;
    }
}
