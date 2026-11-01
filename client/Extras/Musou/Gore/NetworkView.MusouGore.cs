using System;
using System.Collections.Generic;
using Godot;
using SmNet;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Musou Mode's gore in the networked view (Musou pass 2 M10, owner items 8, 22 and 23; design section D). Only under <see cref="ContentExtras.Musou"/>, on a client that draws,
/// and not with <c>--no-musou-gore</c>. No blood on the screen: everything is in the world (<see cref="MusouGoreView"/>).
/// <list type="bullet">
/// <item>Who makes gore: the death event's killer is a player or a retainer (id above 0; Guardsmen's kills carry 0, the Guard events of tag 10 never reach here), or a Thunderhawk
/// strafe. Melee and charges weigh heaviest, shots least (<see cref="MusouGoreRules.Plan"/>).</item>
/// <item>Item 23: an enemy killed by a blast (a frag grenade, a launcher), by artillery (the Barrage kind, overriding the design's "no gore") or by a lascannon shot (the killer's
/// weapon in hand, read from its state) bursts in a bloody explosion, whoever the killer, and leaves no corpse.</item>
/// <item>Item 22: each melee hit (marker 3) by a player or retainer draws a slash across the enemy hit (<see cref="MusouCleaveSlash"/>, the call a cleave's developer can use
/// for any other source; the hit events that exist drive it already: a swing that hits 20 draws 20 within <see cref="MusouGoreRules.SlashesPerFrame"/> a frame). A charge's
/// contact is a marker 3 too and draws the same slash.</item>
/// <item>Budget: 12 deaths a frame (the rest wait, at most 512), full detail under 15 m, half to 35 m, one spray to 60 m, nothing beyond; the counts thin while the pools fill.</item>
/// </list>
/// Test aids: <c>--no-musou-gore</c>, <c>--musou-gore-demo [N]</c> (<see cref="MusouGoreDemoFeed"/>, at <c>--musou-gore-at x,y,z</c> with z the floor, else the own place).
/// A line every 10 s: <c>musou gore:</c> with the cost of the frame (mean and worst ms), the particles drawn, the spawned and recycled ones and the queue.
/// </summary>
public partial class NetworkView
{
    readonly record struct GoreWork(MusouDeathShown Death, bool Lascannon, float Metres);

    static readonly bool GoreOff = CmdFlag("no-musou-gore");
    MusouGoreView? _gore;
    readonly GoreQueue<GoreWork> _goreQueue = new();
    readonly List<GoreWork> _goreBatch = new();
    MusouGoreDemoFeed? _goreDemo;
    bool _goreDemoChecked;
    double _goreLogAt;
    int _goreDeaths, _goreExplosions, _goreSlashes, _goreSprays, _goreGibbed, _goreFrameSlashes, _goreFrameSprays;
    long _goreSpawnedLogged;

    /// <summary>The sections the Musou frame applies: the server's, or with <c>--musou-gore-demo</c> only the scripted feed's (its ticks are its own).</summary>
    IEnumerable<ExtrasSection> MusouGoreFeed(PlayerState me, IReadOnlyList<ExtrasSection> real, double delta)
    {
        if (!_goreDemoChecked)
        {
            _goreDemoChecked = true;
            if (CmdFlag("musou-gore-demo") && !GoreOff)
            {
                var at = me.Position;
                var args = OS.GetCmdlineUserArgs();
                int i = Array.IndexOf(args, "--musou-gore-at");
                if (i >= 0 && i + 1 < args.Length)
                {
                    var p = args[i + 1].Split(',');
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    if (p.Length == 3 && float.TryParse(p[0], inv, out float x) && float.TryParse(p[1], inv, out float y) && float.TryParse(p[2], inv, out float z)) at = new Num.Vector3(x, y, z);
                }
                _goreDemo = new MusouGoreDemoFeed(MusouArg("musou-gore-demo") ?? 60, at, CmdArg("musou-gore-case"), MusouArg("musou-gore-window") ?? 8);
                GD.Print($"musou gore: scripted feed of {_goreDemo.Box.Max.X - _goreDemo.Box.Min.X:F0} m box at ({at.X:F1} {at.Y:F1} {at.Z:F1}) (--musou-gore-demo)");
            }
        }
        return _goreDemo is { } demo ? demo.Step(delta) : real;
    }

    /// <summary>The render tick of the scripted feed, else the given one.</summary>
    double MusouGoreRenderTick(double renderTick) => _goreDemo?.RenderTick ?? renderTick;

    /// <summary>Whether the killer's weapon in hand is the lascannon (item 23: its shot bursts the enemy). The scripted feed's <see cref="MusouGoreDemoFeed.LascannonKiller"/> is one.</summary>
    bool MusouKillerHasLascannon(ushort killer)
    {
        if (killer == 0 || _config is null || _client is null) return false;
        if (_goreDemo is not null && killer == MusouGoreDemoFeed.LascannonKiller) return true;
        PlayerState? state = null;
        if (killer == _client.PlayerId) state = _client.Predicted;
        else foreach (var o in _lastOthers) if (o.PlayerId == killer) { state = o; break; }
        if (state is not { } s || Loadouts.InHand(_config, s)?.Name is not { } name) return false;
        return name.Contains("lascannon", StringComparison.OrdinalIgnoreCase) || name.Contains("las_cannon", StringComparison.OrdinalIgnoreCase);
    }

    MusouGoreView? GoreView()
    {
        if (GoreOff || _gameSpace is null) return null;
        if (_gore is null)
        {
            _gore = new MusouGoreView();
            _gameSpace.AddChild(_gore);
            GD.Print("musou gore: view added");
        }
        return _gore;
    }

    /// <summary>
    /// A death the crowd is about to draw as a body: queues its burst and says whether the body is gibbed (an explosion, or a gore death rolled to leave nothing): then no
    /// corpse is drawn. False when this death makes no gore (nothing queued, the body stays).
    /// </summary>
    bool MusouGoreRegister(in MusouDeathShown d, Vector3 camera)
    {
        if (GoreOff || _gameSpace is null) return false;
        bool lascannon = MusouKillerHasLascannon(d.Killer);
        float metres = Num.Vector3.Distance(d.Position, new Num.Vector3(camera.X, camera.Y, camera.Z));
        var plan = MusouGoreRules.Plan(d.Cause, d.Type, d.Killer, lascannon, metres, 1f, d.Id, d.Tick);
        if (plan.Class == GoreClass.None) return false;
        GoreView();
        if (plan.Lod != GoreLod.None) _goreQueue.Add(new GoreWork(d, lascannon, metres));
        if (plan.Gibbed) _goreGibbed++;
        return plan.Gibbed;
    }

    static float GoreChestHeight(MusouEnemyType type) => type switch
    {
        MusouEnemyType.Gretchin => 0.7f,
        MusouEnemyType.Nob => 1.4f,
        MusouEnemyType.Warboss => 1.9f,
        MusouEnemyType.ChaosMarine => 1.3f,
        _ => 1.1f,
    };

    /// <summary>Item 22: a slash across the enemy at <paramref name="chest"/> (game space) struck from <paramref name="from"/>; the budget and the distance apply. False when none was drawn.</summary>
    internal bool MusouCleaveSlash(Num.Vector3 chest, Num.Vector3 from)
    {
        if (GoreView() is not { } view || _goreFrameSlashes >= MusouGoreRules.SlashesPerFrame) return false;
        var cam = _camera is null ? Num.Vector3.Zero : new Num.Vector3(_camera.GlobalPosition.X, -_camera.GlobalPosition.Z, _camera.GlobalPosition.Y);
        if (!MusouGoreRules.SlashDrawn(1, Num.Vector3.Distance(chest, cam))) return false;
        _goreFrameSlashes++; _goreSlashes++;
        view.Field.Cleave(chest, from, cam);
        return true;
    }

    /// <summary>This frame's gore (the client draws): the cleave slashes and shot sprays from the hit events, the queued bursts, the field's step. Before the crowd state's events are taken.</summary>
    void MusouGoreFrame(PlayerState me, IReadOnlyList<MusouShown> shown, float delta)
    {
        if (GoreView() is not { } view) return;
        if (_goreDemo?.TakeReset() == true) { view.Field.Clear(); _goreQueue.Clear(); }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cam = _camera is null ? Num.Vector3.Zero : new Num.Vector3(_camera.GlobalPosition.X, -_camera.GlobalPosition.Z, _camera.GlobalPosition.Y);
        _goreFrameSlashes = 0; _goreFrameSprays = 0;
        int ownId = _client?.PlayerId ?? 0;
        var events = _musouState.NewEvents;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Kind != MusouEventKind.Hit || e.Player == 0) continue;
            int at = MusouFind(shown, e.Enemy);
            var type = at >= 0 ? shown[at].Type : MusouEnemyType.Boy;
            var feet = at >= 0 ? shown[at].Position : e.Vector;
            var chest = new Num.Vector3(feet.X, feet.Y, feet.Z + GoreChestHeight(type));
            if (e.Value == 3)
            {
                // The attacker's place: the own player's, another's from the last snapshot, else a metre and a half in front of the enemy as seen from the camera.
                var from = cam;
                if (e.Player == ownId) from = me.Position;
                else foreach (var o in _lastOthers) if (o.PlayerId == e.Player) { from = o.Position; break; }
                MusouCleaveSlash(chest, from);
            }
            else if (_goreFrameSprays < MusouGoreRules.SpraysPerFrame && Num.Vector3.DistanceSquared(chest, cam) <= MusouGoreRules.HalfMetres * MusouGoreRules.HalfMetres)
            {
                _goreFrameSprays++; _goreSprays++;
                view.Field.Spray(chest, chest - cam, e.Value == 2 ? 10 : 6, 1f);
            }
        }
        _goreBatch.Clear();
        _goreQueue.Take(_goreBatch);
        float load = view.Field.LoadScale();
        foreach (var w in _goreBatch)
        {
            var d = w.Death;
            var plan = MusouGoreRules.Plan(d.Cause, d.Type, d.Killer, w.Lascannon, w.Metres, load, d.Id, d.Tick);
            if (plan.Class == GoreClass.None) continue;
            _goreDeaths++;
            if (plan.Class == GoreClass.Explosion) _goreExplosions++;
            view.Field.Spawn(plan, new Num.Vector3(d.Position.X, d.Position.Y, d.Position.Z + GoreChestHeight(d.Type)), d.Push, d.Position.Z, d.Type != MusouEnemyType.ChaosMarine);
        }
        watch.Stop();
        view.Frame(delta);
        view.PreMs += watch.Elapsed.TotalMilliseconds;
        GoreLog(view);
    }

    void GoreLog(MusouGoreView view)
    {
        if (_connectedFor < _goreLogAt || view.Frames == 0) return;
        _goreLogAt = _connectedFor + 10.0;
        var f = view.Field;
        GD.Print($"musou gore: t {_connectedFor:F0} s: frame {(view.TotalMs + view.PreMs) / view.Frames:F3} ms mean (events and bursts {view.PreMs / view.Frames:F3}, step and upload {view.TotalMs / view.Frames:F3}), {view.MaxMs:F2} ms worst of the step and upload, over {view.Frames} frames; drawn {view.Describe()}; " +
                 $"{_goreDeaths} bursts ({_goreExplosions} explosions, {_goreGibbed} bodies gibbed), {_goreSlashes} slashes, {_goreSprays} sprays; spawned {f.Spawned}, recycled {f.Recycled}; " +
                 $"queue {_goreQueue.Count} ({_goreQueue.Dropped} dropped)");
        view.TotalMs = 0; view.PreMs = 0; view.MaxMs = 0; view.Frames = 0;
    }
}
