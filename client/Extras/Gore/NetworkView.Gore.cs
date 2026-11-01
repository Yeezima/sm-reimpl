using System;
using System.Collections.Generic;
using Godot;
using SmNet;
using SmNet.Extras.Gore;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The dark gore in classic PvP (owner, 2026-11-01: "the new gore effects should be implemented only into musou mode and classic pvp mode with the 'extended' toggle on"):
/// blood clouds and ink on every damaging hit and a burst on every death of another player, drawn by <see cref="GoreView"/> (the same look as Musou Mode's). Client only, no wire
/// and no protocol change, no gameplay change: it reads what the client already has, the health a snapshot shows dropping and the alive-to-dead edge with the kill feed's cause.
/// It runs only when the match's flags say so (<see cref="PvpGoreRules.Enabled"/>: <see cref="ContentExtras.Gore"/>, never with Musou); a Faithful match never creates the view.
/// The own player's hits show no cloud (it would hang on the camera). <c>--no-gore</c> turns it off. Every size is ours.
/// </summary>
public partial class NetworkView
{
    static readonly bool PvpGoreOff = CmdFlag("no-gore");
    GoreView? _pvpGore;
    readonly Dictionary<int, (float Health, bool Alive)> _pvpGoreSeen = new();
    int _pvpGoreHits, _pvpGoreDeaths;
    double _pvpGoreLogAt;

    /// <summary>The frame of the PvP gore: hooked from <c>ExtrasFrame</c>; returns at once unless the match runs the gore on PvP.</summary>
    void PvpGoreFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta)
    {
        if (_config is null || _client is null || _gameSpace is null || PvpGoreOff || !PvpGoreRules.Enabled(_config.Extras)) return;
        if (_pvpGore is null)
        {
            _pvpGore = new GoreView();
            _gameSpace.AddChild(_pvpGore);
            GD.Print("gore: view added (classic PvP, extended)");
        }
        var cam = _camera is null ? Num.Vector3.Zero : new Num.Vector3(_camera.GlobalPosition.X, -_camera.GlobalPosition.Z, _camera.GlobalPosition.Y);
        int ownId = _client.PlayerId;
        int clouds = 0;
        PvpGoreWatch(me, ownId, cam, ref clouds);
        for (int i = 0; i < others.Count; i++) PvpGoreWatch(others[i], ownId, cam, ref clouds);
        _pvpGore.Frame((float)delta);
        if (_connectedFor >= _pvpGoreLogAt && _pvpGore.Frames > 0)
        {
            _pvpGoreLogAt = _connectedFor + 10.0;
            GD.Print($"gore: t {_connectedFor:F0} s: frame {_pvpGore.TotalMs / _pvpGore.Frames:F3} ms mean, {_pvpGore.MaxMs:F2} ms worst, over {_pvpGore.Frames} frames; drawn {_pvpGore.Describe()}; {_pvpGoreHits} hits, {_pvpGoreDeaths} deaths");
            _pvpGore.TotalMs = 0; _pvpGore.MaxMs = 0; _pvpGore.Frames = 0;
        }
    }

    void PvpGoreWatch(PlayerState p, int ownId, Num.Vector3 cam, ref int clouds)
    {
        bool known = _pvpGoreSeen.TryGetValue(p.PlayerId, out var before);
        _pvpGoreSeen[p.PlayerId] = (p.Health, p.Alive);
        if (!known || p.PlayerId == ownId || _pvpGore is not { } view) return;
        var chest = new Num.Vector3(p.Position.X, p.Position.Y, p.Position.Z + 1.1f);
        float metres = Num.Vector3.Distance(chest, cam);
        if (before.Alive && !p.Alive)
        {
            KillView? kill = _deathCauses.TryGetValue(p.PlayerId, out var c) && _connectedFor - c.At < 3.0 ? c.Kill : null;
            bool splash = kill is { Cause: KillCause.Weapon } k && _config is not null && k.Index >= 0 && k.Index < _config.Weapons.Count
                && (Shots.ProjectileOf(_config.Weapons[k.Index], 0)?.Splash is not null || _config.Weapons[k.Index].Delivery == DeliveryKind.Cone);
            var plan = PvpGoreRules.Death(kill?.Cause ?? KillCause.Weapon, splash, metres, view.Field.LoadScale());
            if (plan.Class != GoreClass.None && plan.Lod != GoreLod.None)
            {
                var push = kill is { HasDirection: true } d ? new Num.Vector3(d.Direction.X, d.Direction.Y, d.Direction.Z) : Num.Vector3.Zero;
                view.Field.Spawn(plan, chest, push, p.Position.Z, false);
                _pvpGoreDeaths++;
            }
            return;
        }
        float lost = before.Health - p.Health;
        if (p.Alive && before.Alive && lost > 0.5f && metres <= GoreRules.HitMetres && clouds < GoreRules.HitsPerFrame)
        {
            clouds++; _pvpGoreHits++;
            view.Field.HitCloud(chest, Num.Vector3.Zero, PvpGoreRules.HitKindOf(lost), view.Field.LoadScale());
        }
    }
}
