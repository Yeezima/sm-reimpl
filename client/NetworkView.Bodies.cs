using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// sim/player-collision (protocol 43) in the networked view: the remote players are drawn moved apart from each other and from the own player
/// (<see cref="DrawnBodies"/>, ours: interpolated bodies in the past and the predicted own body in the present can overlap although the simulation
/// keeps them apart), and a bot logs every 10 s how close the drawn bodies came and how large its prediction corrections were.
/// Test aids: <c>--no-player-collision</c> (as a server started with it: players pass through each other; config hash), <c>--no-body-separation</c>
/// (draw the interpolated places as they are).
/// </summary>
public partial class NetworkView
{
    static readonly bool NoPlayerCollision = OS.GetCmdlineUserArgs().Contains("--no-player-collision");
    static readonly bool NoBodySeparation = OS.GetCmdlineUserArgs().Contains("--no-body-separation");

    readonly DrawnBodies _drawnBodies = new();   // its buffers are reused every frame
    readonly OverlapProbe _drawnOverlaps = new();
    readonly List<System.Numerics.Vector3> _drawnFeet = new();
    double _bodiesLoggedAt;
    int _correctionsLogged;

    /// <summary>The config as the server's: player collision off with <c>--no-player-collision</c> (the config hashes then agree).</summary>
    static SimConfig WithPlayerCollisionArg(SimConfig c) => (NoPlayerCollision ? c with { PlayerBodies = false } : c) is var b && NoChargeLockArg ? b with { ChargeLock = false } : b;
    /// <summary>anim/melee-interaction test aid <c>--no-charge-lock</c> (the server needs it too: config hash): the M29 charge contact and stop.</summary>
    static readonly bool NoChargeLockArg = OS.GetCmdlineUserArgs().Contains("--no-charge-lock");

    /// <summary>The remote players where they are drawn this frame (moved apart unless <c>--no-body-separation</c>), and the bot's overlap sample.</summary>
    IReadOnlyList<PlayerState> DrawnRemotes(IReadOnlyList<PlayerState> others, PlayerState me, PlayerState shown)
    {
        var drawn = NoBodySeparation ? others : _drawnBodies.Separate(others, me.Alive ? shown.Position : null, me.PlayerId, _config!);
        drawn = LockDrawn(drawn);   // anim/melee-interaction: the own charge's victim at the lock spot (NetworkView.MeleeInteraction.cs)
        if (_bot)
        {
            _drawnFeet.Clear();
            foreach (var p in drawn) if (p.Alive) _drawnFeet.Add(p.Position);
            if (me.Alive) _drawnFeet.Add(shown.Position);
            _drawnOverlaps.Add(_drawnFeet, _config!);
            if (_connectedFor - _bodiesLoggedAt >= 10.0)
            {
                _bodiesLoggedAt = _connectedFor;
                var errors = _client!.CorrectionErrors;
                var recent = errors.Skip(System.Math.Min(_correctionsLogged, errors.Count)).ToList();
                _correctionsLogged = errors.Count;
                var sorted = errors.OrderBy(e => e).ToList();
                string p99 = sorted.Count == 0 ? "-" : sorted[System.Math.Clamp((int)System.Math.Ceiling(0.99 * sorted.Count) - 1, 0, sorted.Count - 1)].ToString("F3");
                GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} bodies: drawn {_drawnOverlaps.Summary()}; prediction corrections {errors.Count} sampled, " +
                         $"{errors.Count(e => e > 0.001f)} above 1 mm, {errors.Count(e => e > 0.05f)} above 5 cm, p99 {p99} m, max {(errors.Count == 0 ? 0f : errors.Max()):F3} m " +
                         $"(last 10 s: {recent.Count(e => e > 0.001f)} above 1 mm{(recent.Count > 0 ? $", max {recent.Max():F3} m" : "")}); collision {(_config!.PlayerBodies ? "on" : "OFF")}, " +
                         $"separation {(NoBodySeparation ? "off" : "on")}");
            }
        }
        return drawn;
    }
}
