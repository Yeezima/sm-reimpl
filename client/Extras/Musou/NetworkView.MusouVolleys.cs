using System.Collections.Generic;
using Godot;
using SmContent.Extras.Musou;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The client's own volleys in Musou Mode (musou-net-budget; the owner, 2026-11-09: "maximize client sided rendering/drawing effects to make the battlefield feel real and intense and alive"):
/// <see cref="MusouVolleyPlanner"/> gives the standing gun Orks near the camera that the server left without a shooter token bursts of their own, misses only. Each round joins this frame's
/// rounds (<c>_musouRoundsNow</c>, NetworkView.MusouGuns.cs) as a server or burst-cue round would, so its flash, tracer, impact and sound follow under the guns' own caps. No bytes, no server
/// time; another client sees other volleys. Once every 10 s a <c>musou volleys</c> log line. <c>--no-musou-volleys</c>: none (the behaviour before).
/// </summary>
public partial class NetworkView
{
    readonly MusouVolleyPlanner _musouVolleys = new();
    readonly List<MusouVolleyOrk> _volleyOrks = new();
    readonly List<Num.Vector3> _volleyTargets = new();
    readonly List<Num.Vector3> _volleyGuardFeet = new();
    double _volleyAt = -1, _volleyLogAt;
    bool? _musouVolleysOn;

    bool MusouVolleysOn => _musouVolleysOn ??= MusouGunsOn && !CmdFlag("no-musou-volleys");

    /// <summary>The Guardsmen's feet as this frame shows them (the Guards' frame hands them over): targets for the volleys.</summary>
    void MusouVolleyGuards(IReadOnlyList<TfwGuardShown> shown)
    {
        _volleyGuardFeet.Clear();
        if (!MusouVolleysOn) return;
        foreach (var g in shown) _volleyGuardFeet.Add(g.Position);
    }

    /// <summary>This frame's volley rounds, after the server's and the burst cues' (<see cref="MusouRoundsFrame"/>).</summary>
    void MusouVolleysFrame(PlayerState me, IReadOnlyList<PlayerState> others, IReadOnlyList<MusouShown> shown)
    {
        if (!MusouVolleysOn) return;
        float delta = _volleyAt < 0 ? 0f : (float)(_connectedFor - _volleyAt);
        _volleyAt = _connectedFor;
        foreach (var e in _musouRoundsNow) _musouVolleys.RealRound(e.Enemy);   // an Ork firing for the server keeps to the server's rounds
        _volleyOrks.Clear();
        foreach (var s in shown) if (MusouVolleyRules.Fires(s.Type)) _volleyOrks.Add(new MusouVolleyOrk(s.Id, s.Type, s.State, s.Position, s.Yaw));
        _volleyTargets.Clear();
        if (me.Alive) _volleyTargets.Add(me.Position);
        foreach (var p in others) if (p.Alive) _volleyTargets.Add(p.Position);
        _volleyTargets.AddRange(_volleyGuardFeet);
        var camera = _camera is null ? Num.Vector3.Zero : new Num.Vector3(_camera.GlobalPosition.X, -_camera.GlobalPosition.Z, _camera.GlobalPosition.Y);   // Godot to game space
        var world = _client?.Collision;
        var rounds = _musouVolleys.Step(delta, _volleyOrks, _volleyTargets, camera, world is null ? null : (from, to) =>
        {
            var way = to - from;
            float length = way.Length();
            return length < 0.5f || world.Weapons.Raycast(from, way / length, length) is not { } h || h.Distance >= length - 0.5f;
        });
        uint tick = (uint)(_connectedFor * System.Math.Max(1, _config?.TickRate ?? 30));
        foreach (var r in rounds) _musouRoundsNow.Add(new MusouEvent(MusouEventKind.Tracer, tick, r.Enemy, 0, 0, r.End));
        if (_connectedFor >= _volleyLogAt)
        {
            _volleyLogAt = _connectedFor + 10.0;
            int guns = 0, seeking = 0, near = 0;
            foreach (var o in _volleyOrks)
            {
                guns++;
                if (o.State == MusouEnemyState.Seek) seeking++;
                if (Num.Vector3.Distance(o.Position, camera) <= MusouVolleyRules.CameraMetres) near++;
            }
            GD.Print($"musou volleys: t {_connectedFor:F0} s: shooters now {_musouVolleys.Shooters}, bursts {_musouVolleys.Bursts}, rounds {_musouVolleys.Rounds}, blocked {_musouVolleys.Blocked}, targets {_volleyTargets.Count}; " +
                     $"gun orks shown {guns} ({seeking} in seek, {near} within {MusouVolleyRules.CameraMetres:F0} m), crowd shown {shown.Count}");
        }
    }
}
