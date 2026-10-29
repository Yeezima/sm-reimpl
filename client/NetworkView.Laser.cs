using System;
using Godot;
using SmContent;
using SmNet;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The lascannon's shot, as the owner wants it (feel/scope-lascannon; 2026-10-29: "the lascannon shot effect needs to be a solid beam of red light, not a hazy cloud. sharp."; and
/// "the shot and firing effects should stay where the shot happened, not follow the barrel ... as the player moves").
/// <para>
/// What the data's beam is: <c>lascannon_secondary_tracer</c> (and the Chaos and charged variants) is five <c>mycore</c> beams (texture <c>laser_beam</c>, a dashed and a smooth strip; width 0.24 to
/// 0.56 m before the x0.5 scale, 0.4 s), a <c>laser_beam_alpha</c> beam (1.3 s), an <c>edges</c> beam (<c>power_swing</c>), a wide additive <c>flash</c> beam (<c>beam_gradient</c>, 0.31 to 0.78 m),
/// a <c>redoff</c> beam (<c>laser</c>), a <c>smokeoff</c> beam (<c>alpha_trail_soft</c>, alpha blended, 0.55 to 0.93 m wide) and a flash ring. Read as the player's renderer reads them they
/// stack into a soft, hazy orange streak. Nothing was misread (widths are drawn as full widths, as for every beam: whether a <c>Width</c> is a full or a half width is not established).
/// </para>
/// <para>
/// The owner's change (ours): for those tracers the data's beams are not played; the beam is drawn as a hard-edged bright red core with a hot white line inside it and a thin soft glow,
/// additive, along the shot's true path (muzzle, join point, end: <see cref="BulletPaths"/>), at least <see cref="MinCorePixels"/> pixels wide so that it stays a sharp line at range, in
/// world space where it was fired (it does not follow the barrel). Timing follows the data: the core shows for 0.4 s (<c>mycore</c>'s duration), the glow for 0.6 s; the beam is there at once
/// (M31). The muzzle effect and the impacts are the data's. <c>--old-laser</c> restores the data's beam.
/// </para>
/// <para>
/// Effects a shot attaches to the weapon (muzzle residue, casings, LEDs, the stop-fire smoke: the data's <c>attached</c> flag) follow the marker only for
/// <see cref="ShotAttachSeconds"/> after the shot and then stay where they are (<c>--old-follow</c>: for as long as they live, as before).
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary>How long an effect the data attaches to the weapon follows its marker after a shot (the first frames' flash), seconds; ours (owner's direction).</summary>
    public const float ShotAttachSeconds = 0.08f;

    /// <summary>The core's shortest width on screen, pixels (so that the beam is a sharp line at range).</summary>
    public const float MinCorePixels = 2.2f;

    /// <summary>The core is shown this long, seconds (the data's <c>mycore</c> duration).</summary>
    const float LaserCoreSeconds = 0.4f;
    /// <summary>The glow is shown this long, seconds.</summary>
    const float LaserGlowSeconds = 0.6f;

    bool? _oldLaser, _oldFollow;
    bool OldLaser => _oldLaser ??= CmdFlag("old-laser");
    float AttachFollowSeconds => (_oldFollow ??= CmdFlag("old-follow")) ? float.PositiveInfinity : ShotAttachSeconds;

    /// <summary>Whether an effect is a lascannon beam (the Space Marine and the Chaos ones, basic and charged).</summary>
    static bool IsLaserTracer(string effect) =>
        effect.Contains("lascannon", StringComparison.OrdinalIgnoreCase) && effect.Contains("tracer", StringComparison.OrdinalIgnoreCase);

    struct LaserBeam { public Vector3 From, Via, To; public bool Bent; public float Scale; public double Start; public bool Live; }
    readonly LaserBeam[] _lasers = new LaserBeam[12];
    int _laserNext, _lasersFired;

    void LaserAdd(Vector3 from, Vector3? via, Vector3 to, float scale)
    {
        _lasers[_laserNext] = new LaserBeam { From = from, Via = via ?? to, To = to, Bent = via is not null, Scale = scale <= 0f ? 1f : scale, Start = _connectedFor, Live = true };
        _laserNext = (_laserNext + 1) % _lasers.Length;
        _lasersFired++;
    }

    bool AnyLaser()
    {
        for (int i = 0; i < _lasers.Length; i++) if (_lasers[i].Live) return true;
        return false;
    }

    /// <summary>The core's brightness over its life (1 for the first quarter, then a fall to 0 at <see cref="LaserCoreSeconds"/>).</summary>
    static float CoreAlpha(float age)
    {
        if (age >= LaserCoreSeconds) return 0f;
        float f = Math.Clamp((age / LaserCoreSeconds - 0.25f) / 0.75f, 0f, 1f);
        return 1f - f * f;
    }

    static float GlowAlpha(float age) => age >= LaserGlowSeconds ? 0f : 0.55f * (1f - age / LaserGlowSeconds) * (1f - age / LaserGlowSeconds);

    /// <summary>Draws the live beams (called from <see cref="UpdateBulletLines"/> with the surface of its mesh).</summary>
    void DrawLasers(Vector3 camera)
    {
        float pixel = 2f * MathF.Tan(Mathf.DegToRad(Math.Clamp(_camera!.Fov, 5f, 170f)) * 0.5f) / MathF.Max(1f, GetViewport().GetVisibleRect().Size.Y);   // metres per pixel at 1 m
        for (int i = 0; i < _lasers.Length; i++)
        {
            ref var b = ref _lasers[i];
            if (!b.Live) continue;
            float age = (float)(_connectedFor - b.Start);
            if (age > LaserGlowSeconds) { b.Live = false; continue; }
            float core = CoreAlpha(age), glow = GlowAlpha(age);
            // Full width in metres: 0.2 m at the data's x1 scale (the basic shot's x0.5: 0.1 m), narrowing a little as it fades; never under MinCorePixels on screen.
            float w = 0.2f * b.Scale * (1f - 0.35f * Math.Clamp(age / LaserCoreSeconds, 0f, 1f));
            Leg(camera, b.From, b.Via, w, pixel, core, glow);
            if (b.Bent) Leg(camera, b.Via, b.To, w, pixel, core, glow);
        }
    }

    void Leg(Vector3 camera, Vector3 a, Vector3 b, float width, float pixel, float core, float glow)
    {
        // The width is kept at least MinCorePixels on screen at each end separately (the beam tapers with the distance of its ends: a long shot is a thin line at the muzzle
        // and as wide as it must be to stay 2 px at the far end, not a fat bar from the middle's distance).
        float wa = MathF.Max(width, MinCorePixels * pixel * Math.Max(0.5f, camera.DistanceTo(a)));
        float wb = MathF.Max(width, MinCorePixels * pixel * Math.Max(0.5f, camera.DistanceTo(b)));
        if (glow > 0f) RibbonSoft(camera, a, b, wa * 3.4f, wb * 3.4f, new Color(1f, 0.12f, 0.06f, glow), new Color(1f, 0.12f, 0.06f, 0f));
        if (core > 0f)
        {
            RibbonTapered(camera, a, b, wa, wb, new Color(1f, 0.16f, 0.1f, core));          // the hard red body
            RibbonTapered(camera, a, b, wa * 0.4f, wb * 0.4f, new Color(1f, 0.82f, 0.78f, core));   // the hot line inside it
        }
    }

    /// <summary>A ribbon of one colour, <paramref name="widthA"/> wide at a and <paramref name="widthB"/> at b (full widths, facing the camera).</summary>
    void RibbonTapered(Vector3 camera, Vector3 a, Vector3 b, float widthA, float widthB, Color c)
    {
        var axis = b - a;
        if (axis.LengthSquared() < 1e-8f) return;
        var side = axis.Cross(camera - (a + b) * 0.5f);
        if (side.LengthSquared() < 1e-10f) return;
        side = side.Normalized();
        var sa = side * (widthA * 0.5f); var sb = side * (widthB * 0.5f);
        if (!_bulletSurfaceOpen) { _bulletMesh!.SurfaceBegin(Mesh.PrimitiveType.Triangles); _bulletSurfaceOpen = true; }
        var m = _bulletMesh!;
        void V(Vector3 p) { m.SurfaceSetColor(c); m.SurfaceAddVertex(p); }
        V(a - sa); V(a + sa); V(b + sb);
        V(a - sa); V(b + sb); V(b - sb);
    }

    /// <summary>A ribbon that is <paramref name="centre"/> along its middle and <paramref name="edge"/> at both sides (a soft glow); the full widths are <paramref name="widthA"/> at a and <paramref name="widthB"/> at b.</summary>
    void RibbonSoft(Vector3 camera, Vector3 a, Vector3 b, float widthA, float widthB, Color centre, Color edge)
    {
        var axis = b - a;
        if (axis.LengthSquared() < 1e-8f) return;
        var side = axis.Cross(camera - (a + b) * 0.5f);
        if (side.LengthSquared() < 1e-10f) return;
        side = side.Normalized();
        var sa = side * (widthA * 0.5f); var sb = side * (widthB * 0.5f);
        if (!_bulletSurfaceOpen) { _bulletMesh!.SurfaceBegin(Mesh.PrimitiveType.Triangles); _bulletSurfaceOpen = true; }
        var m = _bulletMesh!;
        void V(Vector3 p, Color c) { m.SurfaceSetColor(c); m.SurfaceAddVertex(p); }
        // Two strips: left edge to the middle and the middle to the right edge.
        V(a - sa, edge); V(a, centre); V(b, centre);
        V(a - sa, edge); V(b, centre); V(b - sb, edge);
        V(a, centre); V(a + sa, edge); V(b + sb, edge);
        V(a, centre); V(b + sb, edge); V(b, centre);
    }

    // ---- --laser-test: a lascannon shot every 3 s along the view, 60 m ahead (a test aid: the shot's effects as the real path draws them, no hit needed) ----

    double _laserTestAt = 1.2;   // the first shot at 4.2 s: the screenshot series starts at 4 s
    bool? _laserTest;
    bool LaserTest => _laserTest ??= CmdFlag("laser-test");

    /// <summary>
    /// <c>--laser-test</c> (test aid, with the lascannon in hand): every 3 s fires the weapon's effects (muzzle, beam, impact) as a shot along the camera ray to 60 m or the first wall,
    /// through the same code as a real shot (<c>FxShot</c>), so that the beam's look and its staying in the world while the player moves can be captured.
    /// </summary>
    void FxLaserTest(PlayerState me)
    {
        if (!LaserTest || _config is null || _camera is null || !me.Alive || _connectedFor - _laserTestAt < 3.0) return;
        var weapon = Loadouts.InHand(_config, me);
        if (weapon is null || ScopeArt.KindOf(weapon.Name) != ScopeKind.Lascannon) return;   // the lascannon only (not "las" in "plasma")
        var w = FxWeapon(weapon.Name);
        if (w is null) return;
        _laserTestAt = _connectedFor;
        var (_, _, start, dir) = CenterRayTarget(me, _config);
        float range = 60f;
        var end = start + dir * range;
        if (_client?.Collision?.Weapons.Raycast(start, dir, range) is { } wall) end = wall.Point;
        int index = Loadouts.WeaponIndex(_config, me, me.Slot);
        FxShot(_client!.PlayerId, weapon, w, index, 0, ToGodot(end), end == start + dir * range ? ShotHit.None : ShotHit.World, -1, -DirToGodot(dir).Normalized(), DirToGodot(dir).Normalized(), Combat.EyePosition(me, _config), own: true,
               victim: -1, armourUp: null, rayOrigin: ToGodot(start), rayDir: DirToGodot(dir).Normalized());
        GD.Print($"t {_connectedFor:F1} s: laser test shot to {end.X:F1},{end.Y:F1},{end.Z:F1}; beams fired {_lasersFired}");
    }
}
