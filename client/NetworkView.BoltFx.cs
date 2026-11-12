using System;
using Godot;
using SmClient.Bullets;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// fx/bolt-tracer-impact (owner, 2026-10-31: "the existing bolt weapon tracers are still wrong. make them less blocky and more like this reference image from the original game"; client only, no
/// protocol change): the bolt rounds' pooled streaks (the muzzle leg, the fallback bullet) were flat hard-edged ribbons of one width ("blocky"); the original's round is a thin white-gold
/// line inside a soft glow. <see cref="SoftStreak"/> draws it so: a wide glow ribbon that is the glow colour along its middle and clear at both edges, and a thin hot core on it, both
/// fading along the length from the tail to the head. Sizes (<see cref="StreakGlowWidth"/>, <see cref="StreakCoreWidth"/>) are ours (estimates, refine register). <c>--old-bolt-fx</c> restores
/// the hard ribbon (and the earlier armour-hit look, <see cref="OldBoltFx"/>).
/// </summary>
public partial class NetworkView
{
    /// <summary>Full widths in metres of a streak's soft glow and its hot core (ours).</summary>
    const float StreakGlowWidth = 0.16f, StreakCoreWidth = 0.022f;

    /// <summary>The width share of the streak being drawn (the plasma bolt's pooled streak in safe mode is thinner).</summary>
    float _streakWidth = 1f;

    /// <summary><c>--old-bolt-fx</c> (before / after aid): the hard flat streak ribbons and the armour hit as it was.</summary>
    bool OldBoltFx => _oldBoltFx ??= CmdFlag("old-bolt-fx");
    bool? _oldBoltFx;

    /// <summary>
    /// A bolt's streak from <paramref name="a"/> (tail) to <paramref name="b"/> (head): the glow ribbon (the colour's RGB, alpha 0 at both edges, <paramref name="tail"/>'s / <paramref name="head"/>'s alpha
    /// times 0.55 in the middle) and the hot core ribbon (the colours lightened toward white). Camera-facing; both ends share the width (the head is not pointed: the streak is short).
    /// </summary>
    void SoftStreak(Vector3 camera, Vector3 a, Vector3 b, Color tail, Color head)
    {
        if (OldBoltFx) { Ribbon(camera, a, b, 0.05f, tail, head); return; }
        var axis = b - a;
        if (axis.LengthSquared() < 1e-8f) return;
        var side = axis.Cross(camera - (a + b) * 0.5f);
        if (side.LengthSquared() < 1e-10f) return;
        side = side.Normalized();
        if (!_bulletSurfaceOpen) { _bulletMesh!.SurfaceBegin(Mesh.PrimitiveType.Triangles); _bulletSurfaceOpen = true; }
        var m = _bulletMesh!;
        void V(Vector3 p, Color c) { m.SurfaceSetColor(c); m.SurfaceAddVertex(p); }
        Color Clear(Color c) => new(c.R, c.G, c.B, 0f);
        Color Glow(Color c) => new(c.R, c.G, c.B, c.A * 0.55f);
        Color Hot(Color c) => new(c.R + (1f - c.R) * 0.6f, c.G + (1f - c.G) * 0.6f, c.B + (1f - c.B) * 0.6f, c.A);
        var g = side * (StreakGlowWidth * _streakWidth * 0.5f);
        V(a - g, Clear(tail)); V(a, Glow(tail)); V(b, Glow(head));
        V(a - g, Clear(tail)); V(b, Glow(head)); V(b - g, Clear(head));
        V(a, Glow(tail)); V(a + g, Clear(tail)); V(b + g, Clear(head));
        V(a, Glow(tail)); V(b + g, Clear(head)); V(b, Glow(head));
        var c0 = side * (StreakCoreWidth * _streakWidth * 0.5f);
        V(a - c0, Clear(Hot(tail))); V(a, Hot(tail)); V(b, Hot(head));
        V(a - c0, Clear(Hot(tail))); V(b, Hot(head)); V(b - c0, Clear(Hot(head)));
        V(a, Hot(tail)); V(a + c0, Clear(Hot(tail))); V(b + c0, Clear(Hot(head)));
        V(a, Hot(tail)); V(b + c0, Clear(Hot(head))); V(b, Hot(head));
    }
}

public partial class NetworkView
{
    // ---- --bolt-fx-demo [wargear]: a test aid for the before / after sheet ----

    /// <summary>
    /// <c>--bolt-fx-demo [weapon]</c> (observer only; test aid): the camera stands 5 m from the first living player and a synthetic shot of the named weapon (default <c>sm_mp_ranged_bolter</c>;
    /// <c>--bolt-fx-armour 0</c>: on broken armour) flies to his chest every 2.4 s, through the real <c>FxShot</c> (tracer, muzzle, impact, the victim's armour hit and flash).
    /// </summary>
    bool BoltFxDemoOn => _boltFxDemo ??= CmdFlag("bolt-fx-demo");
    bool? _boltFxDemo;
    double _boltFxDemoAt = 1.0;
    int _boltFxDemoShots;

    void BoltFxDemo(System.Collections.Generic.IReadOnlyList<PlayerState> others)
    {
        if (!BoltFxDemoOn || _camera is null || _config is null || _client is null) return;
        if (CmdFloat("time-scale") is var ts && float.IsFinite(ts) && ts > 0f)
        {
            // slow motion for the tracer capture (the head crosses a shot in 0.1 s); fx/tracers-impact: --bolt-fx-slow-shot slows only the 0.25 s after each demo shot
            double want = !CmdFlag("bolt-fx-slow-shot") || (_boltFxDemoShots > 0 && _connectedFor - _boltFxDemoAt < 0.25) ? ts : 1.0;
            if (Engine.TimeScale != want) Engine.TimeScale = want;
        }
        PlayerState? victim = null;
        foreach (var o in others) if (o.Alive) { victim = o; break; }
        if (victim is not { } v) return;
        var chest = ToGodot(v.Position + new System.Numerics.Vector3(0f, 0f, 1.45f));
        // The camera: 5 m away, 28 degrees off the line to the shooter's side, a little above the chest, looking at it.
        float turn = CmdFloat("bolt-fx-angle") is var deg && float.IsFinite(deg) ? 0.6f + deg * MathF.PI / 180f : 0.6f;   // fx/tracers-impact: --bolt-fx-angle <deg> turns the shooter about the victim (a clear line in the capture)
        var toShooter = new Vector3(MathF.Sin(turn), 0f, -MathF.Cos(turn));
        var eye = chest + toShooter * 5.2f + new Vector3(0f, 0.25f, 0f) + new Vector3(-toShooter.Z, 0f, toShooter.X) * 1.6f;
        _camera.GlobalTransform = new Transform3D(Basis.LookingAt((chest - eye).Normalized(), Vector3.Up), eye);
        _camera.Fov = 38f;
        if (CmdFlag("bolt-fx-side"))
        {
            // fx/tracers-impact (capture aid): the camera square to the shot's line, 6 m off its middle, so the whole bolt from the muzzle to the impact is in view.
            var shooterAt = chest + toShooter * 7.5f + new Vector3(0f, -0.15f, 0f);
            var middle = (shooterAt + chest) * 0.5f;
            var side = new Vector3(-toShooter.Z, 0f, toShooter.X).Normalized();
            var sideEye = middle + side * 6f + new Vector3(0f, 0.4f, 0f);
            _camera.GlobalTransform = new Transform3D(Basis.LookingAt((middle - sideEye).Normalized(), Vector3.Up), sideEye);
            _camera.Fov = 70f;
        }
        float demoRange = CmdFloat("bolt-fx-range");   // onhit-range (2026-11-13): --bolt-fx-range <m> [--bolt-fx-fov <deg>]: the shooter stands that far off and the camera is at his eye (a scoped view), for the on-hit effects at range
        bool atRange = float.IsFinite(demoRange) && demoRange > 1f;
        if (atRange)
        {
            var shooterFar = chest + toShooter * demoRange + new Vector3(0f, -0.15f, 0f);
            var scopeEye = shooterFar + new Vector3(0f, 0.2f, 0f);
            _camera.GlobalTransform = new Transform3D(Basis.LookingAt((chest - scopeEye).Normalized(), Vector3.Up), scopeEye);
            _camera.Fov = CmdFloat("bolt-fx-fov") is var fv && float.IsFinite(fv) && fv > 0f ? fv : 20f;
        }
        if (_connectedFor - _boltFxDemoAt < 2.4) return;
        _boltFxDemoAt = _connectedFor;
        string name = CmdArg("bolt-fx-demo") is { Length: > 0 } a ? a : "sm_mp_ranged_bolter";
        int index = -1;
        for (int i = 0; i < _config.Weapons.Count; i++) if (_config.Weapons[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        if (index < 0 || FxWeapon(name) is not { } w) { if (_boltFxDemoShots++ == 0) GD.Print($"  bolt-fx-demo: no weapon '{name}'"); return; }
        var spec = _config.Weapons[index];
        // --bolt-fx-armour 0: armour down; seq (fx/red-glow-blood): the first shot on armour, every later one on none (the break, then the re-flashes and the blood)
        bool armoured = CmdArg("bolt-fx-armour") is "seq" ? _boltFxDemoShots == 0 : !(CmdArg("bolt-fx-armour") is "0");
        // The shot: from 3.2 m to the shooter's side of the camera line, 1.3 m up, to the chest; the shooter does not exist (the effects use the fallback muzzle just ahead of the eye).
        var from = chest + toShooter * (atRange ? demoRange : 7.5f) + new Vector3(0f, -0.15f, 0f);
        var dir = (chest - from).Normalized();
        var end = chest;
        var normal = -dir;
        FxShot(-999, spec, w, index, 0, end, ShotHit.Body, -1, normal, dir, ToNum(from - dir * 0.9f), own: false, v.PlayerId, armoured,
               rayOrigin: from, rayDir: dir);
        if (CmdFlag("bolt-fx-charged") && PlasmaLightShot.ChargedStage(spec) > 0) LightBallLaunch(-999, spec, w, index, from - dir * 0.9f, end, 1f);   // fix/plasma-light-tracer capture aid: the charged ball (1.0) instead of the light one
        _boltFxDemoShots++;
        GD.Print($"t {_connectedFor:F1} s: bolt-fx-demo shot {_boltFxDemoShots} of {name} at player {v.PlayerId} ({(armoured ? "armour up" : "armour down")})");
    }

    static System.Numerics.Vector3 ToNum(Vector3 v) => new(v.X, -v.Z, v.Y);
}

public partial class NetworkView
{
    /// <summary>
    /// The data's generic ranged-armour hit (<c>hit_ranged_armour_generic</c>: a warm flash, a fan of thin <c>metal_sparks_beam</c> streaks, a spray of coarse sparks, a light), played on top of the weapon's own
    /// armour effect for a bullet that strikes raised armour: the reference (original, a scoped bolter shot on an armoured marine) shows a white-gold flash with long thin streaks fanned sideways, which the
    /// weapon's effect alone draws as short orange dots. The scale (<see cref="ArmourSparksScale"/>) is ours; the axis is the surface normal read as back toward the shooter. <c>--old-bolt-fx</c> leaves it out.
    /// </summary>
    const string ArmourSparksEffect = "hit_ranged_armour_generic";
    const float ArmourSparksScale = 0.8f;

    void ArmourSparks(Vector3 end, Vector3 dir, double delay, int victim = -1)
    {
        if (OldBoltFx || _fx is null) return;
        var axes = AxesFromY(-dir);
        _fx.Play(ArmourSparksEffect, end, axes, ArmourSparksScale * HitFxParticleScale(end), null, null, 0f, delay, false, 0);
        // Its spark fan is a flat card (not a billboard): a second copy turned a quarter about the axis, so that one of the two always faces the camera.
        var axes2 = new Basis((-dir).Normalized(), Mathf.Pi / 2f) * axes;
        _fx.Play(ArmourSparksEffect, end, axes2, ArmourSparksScale * 0.8f * HitFxParticleScale(end), null, null, 0f, delay, false, 0);
    }
}
