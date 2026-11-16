using System;
using System.Collections.Generic;
using Godot;
using SmNet;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The predicted own plasma ball (stream B of the owner's batch of 2026-11-06 late: "the plasma gun/cannon/pistol charged shot release is still delayed - let the projectile fire out
/// of the barrel instantly when letting go of left mouse button, not starting charging by the initial left mouse button press"; client presentation only, no simulation, wire or
/// config-hash change). The simulation fires the charged shot on the release tick (sim/instant-fire) and the own ball was drawn from the newest snapshot (night/weapon-fx), but that
/// snapshot only holds it after the input reached the server and the snapshot came back: half a round trip and a tick or two after the release (about 70 ms on a LAN, 100 to 150 ms
/// online). Now the own plasma shot (charged or not, plasma gun, pistol and cannon, Space Marine and Chaos) launches a ball of the client's own on the frame the prediction fires,
/// from the shot's origin along its aim (drawn from the barrel by <see cref="BarrelDrawn"/>), flying by the server's rules (<see cref="OwnBallFlight"/>) and stopping at the level or
/// at the player the own trace met. When the server's projectile of that shot shows up it is not drawn: the predicted ball stands for it until the server's is gone (its detonation
/// then plays as before), so nothing pops or doubles. A ball the server never confirms is dropped after <see cref="OwnBallUnconfirmedSeconds"/>. <c>--old-own-ball</c> (test aid):
/// the night/weapon-fx drawing (the newest snapshot's ball).
/// </summary>
public partial class NetworkView
{
    bool? _oldOwnBall;
    /// <summary><c>--old-own-ball</c> (test aid): no predicted ball; the own ball is the newest snapshot's (night/weapon-fx).</summary>
    bool OldOwnBall => _oldOwnBall ??= CmdFlag("old-own-ball");

    /// <summary>A predicted ball the server has not confirmed within this long (seconds) is dropped (the shot did not happen on the server).</summary>
    const double OwnBallUnconfirmedSeconds = 0.75;
    /// <summary>A predicted ball is never drawn longer than this (seconds), whatever the server says (a lost detonation).</summary>
    const double OwnBallMaxSeconds = 8.0;

    sealed class OwnBall
    {
        public int Id, Weapon, Stage, ServerId = -1;
        public ProjectileSpec Spec = null!;
        public Num.Vector3 Position, Velocity;
        public float Travelled, MaxTravel;
        public bool Stopped;
        public double At, StepAt;
    }

    readonly List<OwnBall> _ownBalls = new();
    int _nextOwnBallId = -2;   // negative: never a server id (0..65535)
    int _ownBallsLaunched, _ownBallsConfirmed, _ownBallsDropped;

    /// <summary>From <see cref="FxPlayOwnShot"/>: an own projectile shot of a plasma weapon launches a predicted ball at <paramref name="origin"/> along <paramref name="aim"/> (game space).</summary>
    void OwnBallLaunch(int weaponIndex, int stage, Num.Vector3 origin, Num.Vector3 aim, Num.Vector3 traceEnd, bool traceMetPlayer)
    {
        if (OldOwnBall || OldBarrel || OldPlasmaRelease || _config is null || !IsPlasmaShot(weaponIndex, false)) return;
        if (Shots.ProjectileOf(_config, weaponIndex, stage, false) is not { } spec || !(spec.Speed > 0f)) return;
        var velocity = OwnBallFlight.LaunchVelocity(aim, spec);
        if (velocity == Num.Vector3.Zero) return;
        // The own trace met a player along the aim: the ball stops there (the server's ball strikes it; players are not tested in the flight). Straight-line, so only without gravity.
        float maxTravel = traceMetPlayer && spec.Gravity.Count == 0 ? Num.Vector3.Distance(origin, traceEnd) : float.PositiveInfinity;
        _ownBalls.Add(new OwnBall
        {
            Id = _nextOwnBallId--, Weapon = weaponIndex, Stage = stage, Spec = spec, Position = origin, Velocity = velocity, MaxTravel = maxTravel,
            At = _connectedFor, StepAt = _connectedFor,
        });
        if (_nextOwnBallId < -1_000_000) _nextOwnBallId = -2;
        if (_ownBalls.Count > 32) _ownBalls.RemoveAt(0);
        _ownBallsLaunched++;
    }

    /// <summary>
    /// From <see cref="WithOwnPlasmaAhead"/>: moves the predicted balls on to now, links each own server projectile to the oldest unlinked ball of its weapon and stage, takes the
    /// linked server projectiles out of <paramref name="merged"/>, drops balls whose server projectile is gone (or never came), and adds the balls as projectile views.
    /// </summary>
    void OwnBallsMerge(List<ProjectileView> merged, IReadOnlyCollection<int> ownServerIds, HashSet<int> newestIds)
    {
        if (_ownBalls.Count == 0 || _config is null) return;
        var collision = _client?.Collision;
        Func<Num.Vector3, Num.Vector3, float, RayHit?>? raycast = collision is null ? null : (o, d, l) => collision.Weapons.Raycast(o, d, l);
        foreach (var b in _ownBalls)
        {
            // Fixed sub-steps of at most one tick, so a slow frame flies the same path as the server's ticks.
            float left = (float)(_connectedFor - b.StepAt);
            b.StepAt = _connectedFor;
            while (left > 1e-5f && !b.Stopped)
            {
                float dt = MathF.Min(left, _config.TickSeconds);
                left -= dt;
                (b.Position, b.Velocity, b.Travelled, b.Stopped) = OwnBallFlight.Step(b.Position, b.Velocity, b.Travelled, b.Spec, _config.Gravity, dt, b.MaxTravel, raycast);
            }
        }
        // Link: an own server projectile not yet linked takes the oldest unlinked ball of the same weapon.
        foreach (int id in ownServerIds)
        {
            if (!newestIds.Contains(id) || _ownBalls.Exists(b => b.ServerId == id)) continue;
            var q = merged.Find(x => x.Id == id);
            if (q.Id != id) continue;
            var ball = _ownBalls.Find(b => b.ServerId < 0 && b.Weapon == q.Weapon);   // by weapon (the effects' stage guess may differ); the server's stage is taken
            if (ball is null) continue;
            ball.ServerId = id; ball.Stage = q.Stage;
            _ownBallsConfirmed++;
        }
        for (int i = _ownBalls.Count - 1; i >= 0; i--)
        {
            var b = _ownBalls[i];
            double age = _connectedFor - b.At;
            bool gone = b.ServerId >= 0 ? !newestIds.Contains(b.ServerId) : age > OwnBallUnconfirmedSeconds;
            if (gone || age > OwnBallMaxSeconds || (b.Spec.Lifetime > 0f && age > b.Spec.Lifetime + 0.5))
            {
                if (b.ServerId < 0) _ownBallsDropped++;
                _ownBalls.RemoveAt(i);
            }
        }
        foreach (var b in _ownBalls)
        {
            if (b.ServerId >= 0) merged.RemoveAll(x => x.Id == b.ServerId);
            merged.Add(new ProjectileView(b.Id, b.Weapon, b.Stage, b.Position, ProjectileFlags.None));
        }
    }

    /// <summary>The bot log's counts.</summary>
    string OwnBallStats() => $"own plasma balls predicted {_ownBallsLaunched}, confirmed by the server {_ownBallsConfirmed}, dropped unconfirmed {_ownBallsDropped}{(OldOwnBall ? " (--old-own-ball)" : "")}";
}
