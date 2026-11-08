using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Bullets;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// fix/plasma-light-tracer (owner, 2026-11-08, all modes: "plasma pistol has no tracers - all plasma weapons (not plasma cannon) should have a larger tracer than they currently do. model the light shot
/// tracer after the charged shot tracer shot exactly, but scale it down to .75% of the charged shot size"; client presentation only, no simulation, wire or config-hash change). Read as 75 %
/// (<see cref="PlasmaLightShot.Scale"/>). The light shot of the plasma gun and pistol (either side) is drawn as a small copy of the weapon's charged shot: the same solid roiling ball
/// (<see cref="ProjectileLook"/> of the charged stage) and the same data trail effects the charged projectile plays (<see cref="FxProjectile"/>: glow, streamers, light; the side's tint), each at
/// 0.75 of its size, flying from the muzzle straight to the shot's end point; the impact arrives with it. It replaces the thin blue bolt of fx/tracers-impact (<see cref="PlasmaBolt"/>), and
/// gives the pistol, whose basic shot names no tracer of its own in the data, one. Normal and Compatibility renderers draw it the same way (the ball is a plain unshaded mesh).
/// <c>--old-light-bolt</c> (test aid) restores the thin bolt. The ball flies at <see cref="PlasmaLightShot.FlightSpeed"/> (owner, 2026-11-09: "real speed, faster than 90 if possible, no drop; hit and damage still instant"): the impact effect waits for it. The charged shot and the plasma cannon are untouched.
/// </summary>
public partial class NetworkView
{
    bool? _oldLightBolt;
    bool? _oldTrailSlab;
    /// <summary><c>--old-trail-slab</c> (test aid, fix/plasma-trail-slab): the plasma ball trail's hard-edged head, as before.</summary>
    bool OldTrailSlab => _oldTrailSlab ??= CmdFlag("old-trail-slab");
    bool OldLightBolt => _oldLightBolt ??= CmdFlag("old-light-bolt");

    /// <summary>Whether this shot of this weapon is drawn as a small charged shot.</summary>
    bool LightBallApplies(WeaponEffects w, WeaponSpec spec, int stage, bool projectileShot) =>
        !OldLightBolt && PlasmaBoltOn && stage == 0 && !projectileShot && PlasmaLightShot.Applies(w.Wargear) && PlasmaLightShot.ChargedStage(spec) > 0;

    sealed class LightBall
    {
        public int Shooter, Index, Stage;
        public WeaponEffects W = null!;
        public double At;            // client time the ball leaves (later than the shot for a pistol that is still coming up)
        public Vector3 From, End;
        public float Speed, Age, Distance, Size = PlasmaLightShot.Scale;
        public bool Started, Live;
        public int Frames;
        public Vector3 Pos, Dir = Vector3.Down;
        public MeshInstance3D? Node;
    }

    readonly List<LightBall> _lightBalls = new();
    readonly Stack<MeshInstance3D> _lightBallPool = new();
    int _lightBallsLaunched, _lightBallTrails;

    /// <summary>From <see cref="FxShot"/>: launches the light shot's ball from <paramref name="from"/> to <paramref name="end"/>. Returns the seconds until it arrives (the impact's delay).</summary>
    float LightBallLaunch(int shooter, WeaponSpec spec, WeaponEffects w, int index, Vector3 from, Vector3 end, float size = PlasmaLightShot.Scale)
    {
        if (size < 1f && CmdFlag("bolt-fx-charged")) return 0.083f;   // capture aid: the demo draws the charged reference only
        int stage = PlasmaLightShot.ChargedStage(spec);
        float distance = from.DistanceTo(end);
        float chargedSpeed = Shots.ProjectileOf(spec, stage)?.Speed ?? 0f;
        float speed = size >= 1f ? MathF.Max(chargedSpeed, 1f) : PlasmaLightShot.FlightSpeed(chargedSpeed);   // size 1: the --bolt-fx-charged capture reference, at the charged ball's own speed
        float wait = PistolRaiseWait(shooter, spec);   // fx/visuals-2: a pistol still coming up releases the ball from the raised muzzle
        _lightBalls.Add(new LightBall { Shooter = shooter, Index = index, Stage = stage, W = w, At = _connectedFor + wait, From = from, End = end, Speed = speed, Distance = distance, Size = size });
        if (_lightBalls.Count > 48) RetireLightBall(_lightBalls[0], true);
        _lightBallsLaunched++;
        return wait + distance / speed;
    }

    void RetireLightBall(LightBall b, bool removeNow)
    {
        b.Live = false;
        if (b.Node is { } n) { n.Visible = false; _lightBallPool.Push(n); b.Node = null; }
        if (removeNow) _lightBalls.Remove(b);
    }

    /// <summary>Each frame: starts the balls that are due (ball node plus the charged projectile's trail effects at 0.75) and flies them on.</summary>
    void UpdateLightBalls(double delta)
    {
        if (_lightBalls.Count == 0) return;
        if (_client is null || _client.Status != SmNet.ClientStatus.Connected || _lightBalls.Exists(b => b.At > _connectedFor + 2.0)) { foreach (var b in _lightBalls.ToArray()) RetireLightBall(b, true); return; }
        for (int i = _lightBalls.Count - 1; i >= 0; i--)
        {
            var b = _lightBalls[i];
            if (_connectedFor < b.At) continue;
            if (!b.Started && !StartLightBall(b)) { _lightBalls.RemoveAt(i); continue; }
            b.Age += (float)delta;
            float travelled = b.Speed * b.Age;
            if (b.Frames++ == 0) travelled = MathF.Min(travelled, b.Distance * 0.5f);   // a hitch frame must not skip the ball: it is drawn at least once, half way
            if (travelled >= b.Distance)
            {
                b.Pos = b.End;
                RetireLightBall(b, false);   // the trail's follow now returns null: its effects stop emitting and fade
                _lightBalls.RemoveAt(i);
                continue;
            }
            b.Pos = b.From + b.Dir * travelled;
            if (b.Node is { } n && _projectileRoot is not null) n.Position = _projectileRoot.ToLocal(b.Pos);
        }
    }

    bool StartLightBall(LightBall b)
    {
        b.Started = true;
        if (_fx is null || _projectileRoot is null || _config is null || _catalogue is null) return false;
        // The muzzle where it is now (a pistol that was coming up), else where the shot was seen.
        var from = FxPlace(b.Shooter, b.W.MuzzleMarker)?.Position ?? b.From;
        if (from.DistanceTo(b.End) < 0.4f) return false;
        b.From = from; b.Pos = from;
        b.Distance = from.DistanceTo(b.End);
        b.Dir = (b.End - from).Normalized();
        float cs = Shots.ProjectileOf(_config.Weapons[b.Index], b.Stage)?.Speed ?? 0f;
        b.Speed = b.Size >= 1f ? MathF.Max(cs, 1f) : PlasmaLightShot.FlightSpeed(cs);
        var (mesh, material) = ProjectileLook(b.Index, b.Stage);   // the charged ball's own mesh and material
        var node = _lightBallPool.Count > 0 ? _lightBallPool.Pop() : NewProjectileNode();
        node.Mesh = mesh; node.MaterialOverride = material;
        node.Scale = Vector3.One * b.Size;
        node.Position = _projectileRoot.ToLocal(from); node.Visible = true;   // the projectile root is in game space, the ball's place is in world space
        b.Node = node; b.Live = true;
        // The charged projectile's trail effects (FxProjectile), at 0.75 of their size, following the ball.
        var spec = _config.Weapons[b.Index];
        var trail = _catalogue.ProjectileFor(spec.Name, Shots.ProjectileOf(spec, b.Stage)?.Blueprint)?.Trail;
        if (trail is not null)
            foreach (var p in trail)
            {
                var e = _fx.Play(p.Name, from, AxesFromY(Vector3.Down), (p.Scale <= 0f ? 1f : p.Scale) * b.Size, null,
                    () => b.Live ? (b.Pos, AxesFromY(-b.Dir)) : null,
                    p.Lifetime > 0f ? p.Lifetime : 8f, 0.0, false, 1, tint: RaceTint(b.W.Wargear, p.Name));
                if (e is not null) { e.StopOnLost = true; e.SoftHead = !OldTrailSlab; _lightBallTrails++; }
            }
        return true;
    }

    string LightBallLog() => $"plasma light shots: balls {_lightBallsLaunched}, trail effects {_lightBallTrails}{(OldLightBolt ? " (--old-light-bolt)" : "")}";
}
