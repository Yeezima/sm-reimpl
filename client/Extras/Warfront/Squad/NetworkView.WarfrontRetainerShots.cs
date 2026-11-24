using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// Test aid (Phase B T5): <c>--warfront-retainer-shots &lt;dir&gt;</c> on a windowed human client in a Warfront battle (a server with <c>SM_WF_ROSTERS</c> so the squad lands): once the squad's World players are
/// in the snapshot it prints one <c>WFRET</c> line per retainer (World id, class, weapons, alive, whether the view draws it as a character and with which look), turns the camera to each in turn and saves a still
/// (<c>retainer-&lt;index&gt;-&lt;class&gt;</c>), then a still of the squad HUD. Retainers stand until Phase B T4's brain; nothing here moves or fires them. Never shipped behaviour: it acts only with the flag.
/// </summary>
public partial class NetworkView
{
    static readonly string? WfRetShots = CmdArg("warfront-retainer-shots");
    int _wfRetStep = -1;
    double _wfRetAt;
    List<int>? _wfRetQueue;

    void WarfrontRetainerShotsFrame(PlayerState me)
    {
        if (WfRetShots is null || _wfSquad is not { } squad || _client is not { } client || _config is null || WarfrontSquadAboard || !me.Alive) return;
        double now = _connectedFor;
        if (_wfRetStep < 0)
        {
            var ids = squad.Model.Indices.Select(squad.Model.WorldIdOf).Where(id => _lastOthers.Any(o => o.PlayerId == id) && _remotes.ContainsKey(id)).ToList();
            if (ids.Count == 0) return;
            if (_wfRetAt == 0) { _wfRetAt = now; return; }
            if (now - _wfRetAt < 4.0) return;   // the looks and weapons settle
            _wfRetQueue = ids;
            foreach (int id in ids) GD.Print("WFRET " + WarfrontRetainerLine(id));
            _wfRetStep = 0; _wfRetAt = now;
        }
        if (_wfRetQueue is null) return;
        if (_wfRetStep < _wfRetQueue.Count)
        {
            int id = _wfRetQueue[_wfRetStep];
            if (_lastOthers.FirstOrDefault(o => o.PlayerId == id) is { PlayerId: > 0 } r)
            {
                _yaw = MathF.Atan2(-(r.Position.X - me.Position.X), r.Position.Y - me.Position.Y);
                _pitch = -0.12f;
            }
            if (now - _wfRetAt < 1.5) return;
            string cls = _lastOthers.FirstOrDefault(o => o.PlayerId == id) is { PlayerId: > 0 } s && s.Class >= 0 && s.Class < _config.Classes.Count ? _config.Classes[s.Class].Role : "unknown";
            WfRetShot($"retainer-{squad.Model.IndexOfWorld(id)}-{cls}");
            _wfRetStep++; _wfRetAt = now;
            return;
        }
        if (_wfRetStep == _wfRetQueue.Count && now - _wfRetAt > 1.0)
        {
            WfRetShot("squad-hud");
            GD.Print($"WFRET hud line '{squad.Hud.Line}', panel rows {string.Join(", ", squad.Model.PanelRows(true).Select(r => $"{r.Name}:{r.State}"))}");
            _wfRetStep++;
        }
    }

    string WarfrontRetainerLine(int id)
    {
        var s = _lastOthers.First(o => o.PlayerId == id);
        string W(WeaponSlot slot) => Loadouts.WeaponIndex(_config!, s, slot) is var w and >= 0 && w < _config!.Weapons.Count ? _config.Weapons[w].Name : "-";
        string look = _playerLooks.TryGetValue(id, out var pl) ? $"{pl.Entry.Look.ClassKey}, torso {(pl.Entry.Look.Paint.TryGetValue("torso", out var tp) ? tp.Primary?.Name : null)}" : "no look";
        string drawn = _remotes.TryGetValue(id, out var viewer) ? (viewer.HasMeta("look_key") ? "character" : "character (placeholder look)") : "not drawn";
        return $"id {id} index {_wfSquad!.Model.IndexOfWorld(id)} name '{HudName(id)}' class {(s.Class >= 0 && s.Class < _config!.Classes.Count ? _config.Classes[s.Class].Name : "?")} " +
               $"primary {W(WeaponSlot.Ranged)} pistol {W(WeaponSlot.Pistol)} melee {MeleeWargear(s) ?? "-"} alive {s.Alive}; {drawn}; look {look}; owner {_wfSquad.Model.OwnerPlayerOf(id)}";
    }

    void WfRetShot(string name)
    {
        Directory.CreateDirectory(WfRetShots!);
        var size = GetViewport().GetVisibleRect().Size;
        string path = Path.Combine(WfRetShots!, $"{name}-{(int)size.X}x{(int)size.Y}.png");
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"WFRET shot {name}: {err}");
    }
}
