using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer.Model;
using SmContent.Customization;

namespace SmClient.FrontEnd;

/// <summary>
/// Test aid <c>--preview-weapon-cycle key:name,key:name,...</c> (customizer-weapons, owner 2026-11-12; added content, a test aid only): with the customizer open on a custom slot, chooses
/// the listed items in turn on that slot, as the rows do (<see cref="LoadoutEdits.SetItem"/> through the session), every few frames once the preview has drawn, and logs after each choice what
/// the preview's rig was configured with, how many weapon nodes it draws and Godot's object and node counts (a leak shows as counts that climb round after round). <c>--preview-weapon-rounds n</c>
/// repeats the list (default 1). The screenshot waits for the run to end. Example: <c>--slot 3 --preview-weapon-cycle melee:mp_melee_lightning_claw,melee:mp_melee_power_spear,melee:mp_melee_chainsword</c>.
/// </summary>
public sealed partial class FrontEndView
{
    List<(string Key, string Name)>? _weaponCycle;
    int _weaponCycleStep, _weaponCycleNext, _weaponCycleTotal;
    bool _weaponCycleDone = true, _viewSet;
    int _weaponCycleFailed;
    (string Key, string Name)? _weaponCycleExpect;

    partial void ExtrasScreenshotHold(ref bool hold) { if (_args.ContainsKey("preview-weapon-cycle") && !_weaponCycleDone) hold = true; }

    partial void ExtrasPreviewScript()
    {
        if (_scripted && !_viewSet && _frames > _scriptFrame + 18 && (_args.ContainsKey("preview-yaw") || _args.ContainsKey("preview-zoom")) && CurrentPreview is { Loaded: true } view)
        {
            _viewSet = true;   // test aids --preview-yaw deg / --preview-zoom f: the figure seen from another side or nearer, for stills
            float.TryParse(_args.GetValueOrDefault("preview-yaw"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var yaw);
            view.ViewFrom(yaw, float.TryParse(_args.GetValueOrDefault("preview-zoom"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var zoom) ? zoom : 1f);
        }
        if (!_scripted || _weaponCycleDone && _weaponCycle is not null || _args.GetValueOrDefault("preview-weapon-cycle") is not { Length: > 0 } spec) return;
        if (_weaponCycle is null)
        {
            _weaponCycle = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.Split(':', 2)).Where(p => p.Length == 2).Select(p => (p[0], p[1])).ToList();
            int rounds = int.TryParse(_args.GetValueOrDefault("preview-weapon-rounds"), out var r) && r > 0 ? r : 1;
            _weaponCycleTotal = _weaponCycle.Count * rounds;
            _weaponCycleDone = _weaponCycle.Count == 0;
            _weaponCycleNext = _frames + 20;
            return;
        }
        if (CurrentPreview is not { Loaded: true } preview || _frames < _weaponCycleNext || _creator is not { Open.Kind: SelectionKind.Custom } creator) return;
        // What the previous choice left on stage (it had 8 frames to draw): the rig carries the chosen wargear and draws nodes for it.
        if (_weaponCycleExpect is { } expect)
        {
            var shown = preview.ShownWeapons;
            string? got = expect.Key switch { "primary" => shown.Primary, "pistol" => shown.Pistol, _ => shown.Melee };
            bool ok = string.Equals(got, "sm_" + expect.Name, StringComparison.OrdinalIgnoreCase) && shown.Nodes > 0;
            if (!ok) _weaponCycleFailed++;
            GD.Print($"weapon cycle {_weaponCycleStep}/{_weaponCycleTotal}: {expect.Key}={expect.Name}: rig primary={shown.Primary} pistol={shown.Pistol} melee={shown.Melee}, {shown.Nodes} weapon nodes; " +
                $"objects {Performance.GetMonitor(Performance.Monitor.ObjectCount)}, nodes {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)}, orphans {Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount)}; {(ok ? "OK" : "FAIL: not on stage")}");
            _weaponCycleExpect = null;
            if (_weaponCycleStep >= _weaponCycleTotal) { _weaponCycleDone = true; GD.Print($"weapon cycle: {(_weaponCycleFailed == 0 ? "all shown" : _weaponCycleFailed + " not shown")}"); return; }
        }
        var (key, name) = _weaponCycle[_weaponCycleStep % _weaponCycle.Count];
        var result = LoadoutEdits.SetItem(_services.Session!.Working, creator.Open.Slot, key, name, _services.Catalog!);
        if (result.Changed) _services.Session.Edit(_ => result.Profile);
        else if (result.Notice is { Length: > 0 }) { GD.Print($"weapon cycle: {key}={name} refused: {result.Notice}"); _weaponCycleFailed++; }
        _weaponCycleStep++;
        _weaponCycleNext = _frames + 8;
        if (result.Changed || result.Notice is not { Length: > 0 }) _weaponCycleExpect = (key, name);   // a refused choice leaves the stage as it was
        else if (_weaponCycleStep >= _weaponCycleTotal) { _weaponCycleDone = true; GD.Print($"weapon cycle: {(_weaponCycleFailed == 0 ? "all shown" : _weaponCycleFailed + " not shown")}"); }
    }
}
