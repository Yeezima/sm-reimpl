using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmClient.Bullets;
using SmContent;
using SmNet;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Visible bullets on the damaging path (branch feel/bullets; owner, 2026-10-29: "there should be bullet projectiles that go exactly where the damaging
/// projectile went when firing a gun"). Client only: the simulation, the server and the wire are as they were.
/// <para>
/// What was wrong. A hit-scan shot damages along one straight ray: its origin is the eye moved by the input's origin offset (for a human the point of the
/// camera ray nearest the eye) and its direction the view turned by the convergence offsets and the scatter. The tracer was drawn as a straight line from the
/// weapon's <c>fx_Muzzle</c> to where the shot ended. The two meet at the end only: along the way the drawn bullet was off the damaging ray by the muzzle's distance
/// from it (the muzzle is about half a metre beside and below the camera ray) at the start, closing to nothing at the end, so a bullet at 5 m was visibly
/// to the side of the ray that hit. The own picture also took its ray from the camera of the frame before the shot was sent, not from the input actually sent.
/// </para>
/// <para>
/// What is drawn now (<c>--no-bullets</c> restores the straight tracer): the bullet leaves the muzzle, meets the true ray <see cref="BulletPaths.ConvergeDistance"/>
/// (3 m, ours) past the muzzle's own distance along it and flies along it to the end of the shot, so everything past that point is exactly the damaging
/// path, scatter and penetration included (the end is where the simulation stopped the ray: a wall, the farthest player it pierced, a prop's exit cone). It is
/// the weapon's own tracer effect (<c>bolter_tracer</c> and the others) played from the join point (starting when the head gets there: one pooled effect per shot,
/// as many as the straight tracer cost, so the effect pool is not pressed harder than before; feel/bullets-2 replaced the second beam of the first version, which
/// doubled them and left 6 in 10 shots straight under pool pressure) plus the muzzle leg, a few metres crossed in a few milliseconds, as a pooled bright streak outside
/// the effect pool (32 slots, no allocation). The impact still arrives with the head, whose speed is the data's (<see cref="TracerHeadSpeed"/>, at least crossing in
/// <c>MaxTracerFlight</c>, M31) unless <c>--bullet-speed m/s</c> sets it. A beam without a head (the lascannon) is there at once (drawn as one straight static beam since wfx; the old data-beam path below still bends as two). A hit-scan
/// weapon without a tracer effect gets the pooled streak for the whole path.
/// </para>
/// <para>
/// The ray. Own shots: the input that was sent (a ring of the last 64, <see cref="NoteSentInput"/>) run through the server's own rules
/// (<see cref="Combat.ShotOrigin"/>, <see cref="Combat.ShotDirection(PlayerInput, SimConfig, PlayerState)"/>), so the picture's origin, direction, scatter and end are what the
/// server resolves (before: the camera ray of the previous frame). Other players' shots: shot records carry the end only, so the origin is rebuilt by the
/// shooter's own rule: the point of its camera ray (the original rig for its weapon and aim state, <see cref="NetworkView.RemoteShotOrigin"/>) nearest its eye, bounded as the server bounds
/// it, and the direction runs from there to the end. That is an **interpretation**: the real origin differs by the camera's wall pull-in and aim blend, a few centimetres
/// to a few decimetres at the muzzle and nothing at the end (spec "Visible bullets on the damaging path").
/// </para>
/// Test aids: <c>--no-bullets</c>, <c>--show-shot-rays</c> (the true ray in green for own shots and cyan for others', the drawn bullet path in orange, for 2 s),
/// <c>--bullet-speed m/s</c>, <c>--bullet-probe</c> (log the offsets; a bot with <c>--aim-test</c> then sends the converged shots of a human).
/// </summary>
public partial class NetworkView
{
    /// <summary>Live shot effects above which an instant beam (the data's lascannon beam under --old-laser, which needs two effects to bend) is drawn from the join point only (the effect player holds 160 and drops the oldest of the lowest priority; ours).</summary>
    const int BentPoolLimit = 110;

    /// <summary>wfx (owner's item 27; ours): how long the muzzle leg of a bullet stays drawn after its head has crossed it, seconds (about seven frames at 60 fps).</summary>
    const float MuzzleLegGlowSeconds = 0.12f;
    bool? _oldMuzzleLeg;
    /// <summary><c>--old-muzzle-leg</c> (test aid): the muzzle leg is gone as soon as the head has crossed it, as before wfx.</summary>
    bool OldMuzzleLeg => _oldMuzzleLeg ??= CmdFlag("old-muzzle-leg");

    bool? _noBullets, _showShotRays, _bulletProbe;
    float? _bulletSpeedFlag;
    /// <summary>False with <c>--no-bullets</c>: the tracer is the straight line from the muzzle to the end, as before feel/bullets.</summary>
    bool BulletsOn => !(_noBullets ??= CmdFlag("no-bullets"));
    bool ShowShotRays => _showShotRays ??= CmdFlag("show-shot-rays");
    /// <summary><c>--bullet-streaks</c> (test aid): the pooled streak instead of the weapon's tracer effect for every hit-scan shot (what a weapon without a tracer gets).</summary>
    bool ForceStreaks => _forceStreaks ??= CmdFlag("bullet-streaks");
    bool? _forceStreaks;
    /// <summary><c>--bullet-probe</c>: logs each shot's offsets (bots with <c>--aim-test</c> send human-converged shots).</summary>
    bool BulletProbe => _bulletProbe ??= CmdFlag("bullet-probe");
    /// <summary><c>--bullet-speed m/s</c>: the bright head's speed for every bullet tracer (0: the data's, at least crossing in <c>MaxTracerFlight</c>).</summary>
    float BulletSpeedOverride => _bulletSpeedFlag ??= FloatArg("bullet-speed") ?? 0f;

    int _bulletsBent, _bulletsFromJoin, _bulletsOffRay, _bulletPeakLive;

    // ---- the input that was sent, by tick ----

    readonly SentInputRing _sentInputs = new();

    /// <summary>From <c>SampleInput</c>: remembers the input of this tick as the wire will carry it, so that the shot that comes of it is drawn on the ray the server uses.</summary>
    void NoteSentInput(PlayerInput input) => _sentInputs.Note(input);

    /// <summary>A point in Godot world space as game space.</summary>
    Num.Vector3 ToGame(Vector3 world)
    {
        var g = _gameSpace is null ? new Vector3(world.X, -world.Z, world.Y) : _gameSpace.GlobalTransform.AffineInverse() * world;
        return new Num.Vector3(g.X, g.Y, g.Z);
    }

    /// <summary>
    /// The ray the own shot of this frame went along (game space), by the server's rules from the input sent for it, or null when this frame
    /// has no fired event or the input is not in the ring (the shot is then drawn from the camera ray as before).
    /// </summary>
    (Num.Vector3 Origin, Num.Vector3 Direction)? SentShotRay(PlayerState me, SimConfig c, int nth = 0, int of = 1)
    {
        // The nth of the `of` shots drawn this frame belongs to the nth of the last `of` fired events (a slow frame runs several ticks); the origin, the
        // scatter and the eye are the predicted state of that tick, not the frame's last one (GameClient.TryPredictedAfter; `me` when it is gone).
        if (!ShotTicks.TryPick(_frameOwnEvents, nth, of, out uint tick) || !_sentInputs.TryGet(tick, out var sent)) return null;
        var state = _client is not null && _client.TryPredictedAfter(tick, out var at) ? at : me;
        return (Combat.ShotOrigin(state, sent, c, _client?.Collision), Combat.ShotDirection(sent, c, state));
    }

    // ---- other players' shots ----

    Num.Vector3 _rebuiltCamera;   // the camera RemoteShotOrigin last placed (--bullet-probe)

    /// <summary>
    /// The origin another player's shot is taken to have had (game space): the point of its camera ray nearest its eye, bounded as the server bounds an offset,
    /// the eye itself when level weapon collision lies between (the server's rule, <see cref="Combat.ShotOrigin"/>) or when the rig is not loaded. The camera is the
    /// original rig's place for the shooter's weapon, pitch and aim state (the settled blend: the aim blend is not on the wire) behind the shooter's feet
    /// rotated by its yaw; the camera ray runs along its view. **Interpretation**: without the camera's wall pull-in. A weapon the camera tuning does not know (the
    /// Ork's) takes the rifle's camera, as <see cref="CameraFor"/> does for every weapon but the sniper rifle and lascannon.
    /// </summary>
    Num.Vector3 RemoteShotOrigin(PlayerState s)
    {
        var c = _config!;
        var eye = Combat.EyePosition(s, c);
        if (_cameraRig is null || _cameraTuning is null || (CameraFor(s) ?? _cameraTuning.For(FallbackWeapon)) is not { } wc) return eye;
        var ex = wc.Exploration; var aim = wc.Aim;
        float pitchEx = Math.Clamp(s.Pitch, CameraRig.SimRadians(ex.PitchMinimum), CameraRig.SimRadians(ex.PitchMaximum));
        float pitchAim = Math.Clamp(s.Pitch, CameraRig.SimRadians(aim.PitchMinimum), CameraRig.SimRadians(aim.PitchMaximum));
        if (_cameraRig.OffsetAtPitch(ex.AnimationCurve, pitchEx) is not { } o0 || _cameraRig.OffsetAtPitch(aim.AnimationCurve, pitchAim) is not { } o1) return eye;
        var cam = s.Alive && s.Aiming ? o1.Camera : o0.Camera;
        float sin = MathF.Sin(s.Yaw), cos = MathF.Cos(s.Yaw);
        var camera = s.Position + new Num.Vector3(cam.X * cos - cam.Y * sin, cam.X * sin + cam.Y * cos, cam.Z);
        _rebuiltCamera = camera;
        var origin = BulletPaths.OriginOnCameraRay(camera, Combat.AimDirection(s.Yaw, s.Pitch), eye, c.MaxShotOriginOffset);
        var offset = origin - eye;
        float length = offset.Length();
        if (length > 1e-6f && _client?.Collision?.Weapons.Raycast(eye, offset / length, length) is { } wall && wall.Distance <= length) return eye;
        return origin;
    }

    // ---- playing a bullet ----

    /// <summary>
    /// Plays the tracer effect <paramref name="p"/> of a hit-scan shot from the muzzle <paramref name="from"/> to <paramref name="end"/> (Godot space). With a ray
    /// (<paramref name="rayOrigin"/>, <paramref name="rayDir"/>, Godot space) the bullet joins it at the join point (<see cref="BulletPaths.JoinPoint"/>): the tracer
    /// effect plays from there (one effect) and the muzzle leg is a pooled streak. Returns the seconds the bright head takes from the muzzle to the end (0: the beam is there at once).
    /// </summary>
    float PlayTracer(FxPlay p, WeaponEffects w, Vector3 from, Vector3 end, Vector3? rayOrigin, Vector3? rayDir, bool own, int shooter)
    {
        var fx = _fx!;
        _bulletPeakLive = Math.Max(_bulletPeakLive, fx.ShotEffectCount);
        float scale = p.Scale <= 0f ? 1f : p.Scale;
        var tint = RaceTint(w.Wargear, p.Name);
        float headSpeed = TracerHeadSpeed(p.Name);
        Vector3? join = null;
        if (BulletsOn && rayOrigin is { } ro && rayDir is { } rd && BulletPaths.JoinPoint(N(from), N(ro), N(rd), N(end)) is { } j) join = G(j);
        float d1 = join is { } j1 ? from.DistanceTo(j1) : from.DistanceTo(end), d2 = join is { } j2 ? j2.DistanceTo(end) : 0f;
        float total = d1 + d2;
        float speed = 0f;
        if (headSpeed > 1f) speed = BulletSpeedOverride > 0f ? BulletSpeedOverride : MathF.Max(headSpeed, total / MaxTracerFlight);
        float travel = speed > 0f ? total / speed : 0f;
        // wfx (owner's item 25): the melta's blast is a distinct cone, not the data's haze of rings (NetworkView.WeaponFx.cs).
        if (IsMeltaBlast(p.Name) && !OldMeltaFx) { MeltaConeAdd(from, end); BulletDebugAdd(own, rayOrigin, end, from, null); return 0f; }
        // The lascannon's beam: the owner's hard red beam instead of the data's soft stack of beams, in world space where it was fired (NetworkView.Laser.cs).
        if (IsLaserTracer(p.Name) && !OldLaser)
        {
            LaserAdd(from, null, end, scale);   // wfx: one straight beam, muzzle to end, both points snapshotted now (the muzzle-join-end bend read as a V once the shooter moved)
            BulletDebugAdd(own, rayOrigin, end, from, join);
            return 0f;
        }
        if (join is null) { _bulletsOffRay++; }
        else if (speed > 0f)
        {
            // A bullet with a head (feel/bullets-2): ONE pooled tracer effect from the join point (on the true ray) to the end, starting when the head gets there, as
            // many effects as the straight tracer of before cost (a second beam per bullet doubled them and made the effect pool evict impacts), and the short muzzle
            // leg, a few metres crossed in a few milliseconds, as a pooled streak outside the effect pool.
            _bulletsBent++;
            var e1 = fx.Play(p.Name, join.Value, AxesFromY((end - join.Value).Normalized()), scale, target: end, lifetime: p.Lifetime, delay: d1 / speed, priority: 1, tint: tint, smokeRein: SmokeRein);
            if (e1 is not null) { _fxTracers++; e1.HeadSpeed = speed; ThinBoltTracer(e1, p.Name); }
            StreakAdd(from, join.Value, join.Value, speed);
            BulletDebugAdd(own, rayOrigin, end, from, join);
            return travel;
        }
        else
        {
            // A beam without a head (the lascannon's laser) is there at once, so its muzzle leg must be a beam too: two effects (rare weapon), and one from the join
            // point alone when the pool is crowded.
            Vector3 start = from;
            if (fx.ShotEffectCount > BentPoolLimit) { _bulletsFromJoin++; start = join.Value; join = null; }
            else _bulletsBent++;
            var first = join ?? end;
            var e = fx.Play(p.Name, start, AxesFromY((first - start).Normalized()), scale, target: first, lifetime: p.Lifetime, priority: 1, tint: tint, smokeRein: SmokeRein);
            if (e is not null) _fxTracers++;
            if (join is { } at) fx.Play(p.Name, at, AxesFromY((end - at).Normalized()), scale, target: end, lifetime: p.Lifetime, priority: 1, tint: tint, smokeRein: SmokeRein);
            BulletDebugAdd(own, rayOrigin, end, start, join);
            return 0f;
        }
        var straight = fx.Play(p.Name, from, AxesFromY((end - from).Normalized()), scale, target: end, lifetime: p.Lifetime, priority: 1, tint: tint, smokeRein: SmokeRein);
        if (straight is not null) { _fxTracers++; if (speed > 0f) straight.HeadSpeed = speed; ThinBoltTracer(straight, p.Name); }
        BulletDebugAdd(own, rayOrigin, end, from, null);
        return travel;
    }

    /// <summary>
    /// A hit-scan shot of a weapon whose effects name no tracer: a short bright streak flies the same path (muzzle, join, end) at <see cref="FallbackStreakSpeed"/>
    /// (ours; or <c>--bullet-speed</c>). Pooled like the debug rays; returns the seconds until it arrives.
    /// </summary>
    float PlayFallbackBullet(Vector3 from, Vector3 end, Vector3? rayOrigin, Vector3? rayDir)
    {
        Vector3? join = null;
        if (BulletsOn && rayOrigin is { } ro && rayDir is { } rd && BulletPaths.JoinPoint(N(from), N(ro), N(rd), N(end)) is { } j) join = G(j);
        float total = join is { } j1 ? from.DistanceTo(j1) + j1.DistanceTo(end) : from.DistanceTo(end);
        float speed = BulletSpeedOverride > 0f ? BulletSpeedOverride : MathF.Max(FallbackStreakSpeed, total / MaxTracerFlight);   // the M31 cap: across a shot in at most 0.1 s, like a tracer head
        StreakAdd(from, join ?? end, end, speed);
        return total / speed;
    }

    /// <summary>Metres per second of the pooled fallback streak (ours; used only for a weapon whose effects name no tracer).</summary>
    const float FallbackStreakSpeed = 600f;

    static Num.Vector3 N(Vector3 v) => new(v.X, v.Y, v.Z);
    static Vector3 G(Num.Vector3 v) => new(v.X, v.Y, v.Z);

    // ---- pooled streaks and debug rays: one immediate mesh, rebuilt each frame while something is on ----

    struct Streak { public Vector3 From, Via, To; public float Speed; public double Start; public bool Live, Shown; public Color? Tint; }
    struct DebugRay { public bool Own; public Vector3 Origin, End, Muzzle, Join; public bool HasOrigin, HasJoin; public double Start; public bool Live; }
    readonly Streak[] _streaks = new Streak[32];
    readonly DebugRay[] _debugRays = new DebugRay[24];
    int _streakNext, _debugNext;
    bool _bulletSurfaceOpen;
    MeshInstance3D? _bulletLines;
    ImmediateMesh? _bulletMesh;
    int _fallbackStreaks;

    void StreakAdd(Vector3 from, Vector3 via, Vector3 to, float speed, Color? tint = null)
    {
        _streaks[_streakNext] = new Streak { From = from, Via = via, To = to, Speed = speed, Start = _connectedFor, Live = true, Tint = tint };
        _streakNext = (_streakNext + 1) % _streaks.Length;
        _fallbackStreaks++;
    }

    void BulletDebugAdd(bool own, Vector3? rayOrigin, Vector3 end, Vector3 muzzle, Vector3? join)
    {
        if (!ShowShotRays) return;
        _debugRays[_debugNext] = new DebugRay { Own = own, Origin = rayOrigin ?? muzzle, HasOrigin = rayOrigin is not null, End = end, Muzzle = muzzle, Join = join ?? default, HasJoin = join is not null, Start = _connectedFor, Live = true };
        _debugNext = (_debugNext + 1) % _debugRays.Length;
    }

    /// <summary>Each frame (from <c>UpdateFx</c>): draws the live fallback streaks and the debug rays.</summary>
    void UpdateBulletLines()
    {
        bool any = false;
        for (int i = 0; i < _streaks.Length && !any; i++) any = _streaks[i].Live;
        for (int i = 0; i < _debugRays.Length && !any; i++) any = _debugRays[i].Live;
        any |= AnyLaser();   // feel/scope-lascannon (NetworkView.Laser.cs)
        if (!any)
        {
            if (_bulletMesh is not null && _bulletMesh.GetSurfaceCount() > 0) _bulletMesh.ClearSurfaces();
            return;
        }
        if (_bulletLines is null)
        {
            _bulletMesh = new ImmediateMesh();
            var material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add, NoDepthTest = false, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            _bulletLines = new MeshInstance3D { Name = "BulletLines", Mesh = _bulletMesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 4096f };
            AddChild(_bulletLines);
        }
        var camera = _camera!.GlobalPosition;
        _bulletMesh!.ClearSurfaces();
        _bulletSurfaceOpen = false;
        DrawLasers(camera);   // feel/scope-lascannon: the lascannon beams (NetworkView.Laser.cs)
        for (int i = 0; i < _streaks.Length; i++)
        {
            ref var s = ref _streaks[i];
            if (!s.Live) continue;
            float legA = s.From.DistanceTo(s.Via), legB = s.Via.DistanceTo(s.To);
            float head = s.Speed * (float)(_connectedFor - s.Start);
            const float tail = 2.5f;
            // Gone once its tail has left the path, but never before it has been drawn once (a leg crossed in a few milliseconds is shown whole in the first frame).
            // wfx (owner's item 27, ours): the muzzle leg stays in the world as a fading line for MuzzleLegGlowSeconds after the head has crossed it, so that the tracer is seen to start at the barrel
            // (before: the leg was crossed in a few milliseconds and the tracer effect, which starts at the join point 3 m out, looked as if it left the air in front of the gun).
            if (s.Shown && head - tail > legA + legB)
            {
                float since = (head - tail - legA - legB) / MathF.Max(1f, s.Speed);
                float glow = OldMuzzleLeg ? 0f : 1f - since / MuzzleLegGlowSeconds;
                if (glow <= 0f || _connectedFor - s.Start > 2.0) { s.Live = false; continue; }
                var warm = s.Tint is { } gt ? new Color(gt.R, gt.G, gt.B, 0.9f * glow) : new Color(1f, 0.82f, 0.4f, 0.9f * glow);
                Ribbon(camera, s.From, s.Via, 0.05f, new Color(warm.R, warm.G, warm.B, 0.35f * glow), warm);
                if (legB > 1e-3f) Ribbon(camera, s.Via, s.To, 0.05f, warm, new Color(warm.R, warm.G, warm.B, 0.35f * glow));
                continue;
            }
            if (_connectedFor - s.Start > 2.0) { s.Live = false; continue; }
            // Head and tail as distances along the path, clipped to it; the bent path is drawn as its two legs.
            float h = MathF.Min(head, legA + legB), t = MathF.Max(0f, head - tail);
            if (!s.Shown) { s.Shown = true; if (head > legA + legB) { h = legA + legB; t = 0f; } }
            var cold = s.Tint is { } ct ? new Color(ct.R, ct.G, ct.B, 0f) : new Color(1f, 0.85f, 0.45f, 0.0f);
            var hot = s.Tint is { } ht ? new Color(ht.Lerp(Colors.White, 0.5f), 1f) : new Color(1f, 0.95f, 0.7f, 1f);
            if (t < legA) Ribbon(camera, PointAt(s, t, legA, legB), PointAt(s, MathF.Min(h, legA), legA, legB), 0.05f, cold, hot);
            if (h > legA) Ribbon(camera, PointAt(s, MathF.Max(t, legA), legA, legB), PointAt(s, h, legA, legB), 0.05f, cold, hot);
        }
        for (int i = 0; i < _debugRays.Length; i++)
        {
            ref var r = ref _debugRays[i];
            if (!r.Live) continue;
            if (_connectedFor - r.Start > 2.0) { r.Live = false; continue; }
            if (r.HasOrigin) Ribbon(camera, r.Origin, r.End, 0.015f, r.Own ? new Color(0.1f, 1f, 0.2f, 1f) : new Color(0.1f, 0.9f, 1f, 1f), r.Own ? new Color(0.1f, 1f, 0.2f, 1f) : new Color(0.1f, 0.9f, 1f, 1f));
            var orange = new Color(1f, 0.5f, 0.1f, 1f);
            if (r.HasJoin) { Ribbon(camera, r.Muzzle, r.Join, 0.03f, orange, orange); Ribbon(camera, r.Join, r.End, 0.03f, orange, orange); }
            else Ribbon(camera, r.Muzzle, r.End, 0.03f, orange, orange);
        }
        if (_bulletSurfaceOpen) _bulletMesh.SurfaceEnd();   // an empty surface is an engine error (a frame where every live streak was skipped)
        _bulletSurfaceOpen = false;
    }

    static Vector3 PointAt(in Streak s, float distance, float legA, float legB) =>
        distance <= legA ? s.From.Lerp(s.Via, legA > 1e-4f ? distance / legA : 0f) : s.Via.Lerp(s.To, legB > 1e-4f ? Math.Min(1f, (distance - legA) / legB) : 0f);

    /// <summary>A camera-facing ribbon of <paramref name="width"/> metres from a to b; the colours blend from the tail (a) to the head (b).</summary>
    void Ribbon(Vector3 camera, Vector3 a, Vector3 b, float width, Color tail, Color head)
    {
        var axis = b - a;
        if (axis.LengthSquared() < 1e-8f) return;
        var side = axis.Cross(camera - (a + b) * 0.5f);
        if (side.LengthSquared() < 1e-10f) return;
        side = side.Normalized() * (width * 0.5f);
        if (!_bulletSurfaceOpen) { _bulletMesh!.SurfaceBegin(Mesh.PrimitiveType.Triangles); _bulletSurfaceOpen = true; }
        _bulletMesh!.SurfaceSetColor(tail); _bulletMesh.SurfaceAddVertex(a - side);
        _bulletMesh.SurfaceSetColor(tail); _bulletMesh.SurfaceAddVertex(a + side);
        _bulletMesh.SurfaceSetColor(head); _bulletMesh.SurfaceAddVertex(b + side);
        _bulletMesh.SurfaceSetColor(tail); _bulletMesh.SurfaceAddVertex(a - side);
        _bulletMesh.SurfaceSetColor(head); _bulletMesh.SurfaceAddVertex(b + side);
        _bulletMesh.SurfaceSetColor(head); _bulletMesh.SurfaceAddVertex(b - side);
    }

    /// <summary>The F1 and log text.</summary>
    string BulletStats() => $"bullets {(BulletsOn ? "on" : "off")}: bent {_bulletsBent}, from the join (pool pressure) {_bulletsFromJoin}, off the ray {_bulletsOffRay} (no ray or the end within the convergence), fallback streaks {_fallbackStreaks}, peak live shot effects {_bulletPeakLive}/160";

    // ---- probe (--bullet-probe) ----

    readonly float[] _probeDistances = [5f, 15f, 40f];

    /// <summary>
    /// <c>--bullet-probe</c>: for a shot with a known ray (origin, unit direction, end; game space) and the muzzle as drawn (game space), logs how far the
    /// straight tracer of before (muzzle to end) and the bullet path of now (muzzle, join, end) are from the ray at 5, 15 and 40 m along it, and the angle of the old tracer
    /// to the ray. <paramref name="rebuilt"/> is the origin the rule for other players' shots gives for the same shooter, to measure that rule against the sent one (own shots).
    /// </summary>
    void BulletProbeShot(string kind, int shooter, string weapon, Num.Vector3 origin, Num.Vector3 direction, Num.Vector3 end, Num.Vector3 muzzle, Num.Vector3? rebuilt, Num.Vector3 eye, string extra = "")
    {
        if (!BulletProbe) return;
        var join = BulletPaths.JoinPoint(muzzle, origin, direction, end);
        float length = Num.Vector3.Dot(end - origin, direction);
        var inv = CultureInfo.InvariantCulture;
        var parts = new List<string>();
        foreach (float d in _probeDistances)
        {
            if (d > length) { parts.Add($"{d:F0} m: past the end"); continue; }
            var point = origin + direction * d;
            float old = BulletPaths.DistanceToPath(point, muzzle, end);
            float now = join is { } j ? BulletPaths.DistanceToPath(point, muzzle, j, end) : old;
            string rebuiltText = "";
            if (rebuilt is { } rb)
            {
                var rd = BulletPaths.DirectionTo(rb, end, direction);
                float lateral = Num.Vector3.Distance(rb + rd * d, point);
                float eyeLateral = Num.Vector3.Distance(eye + BulletPaths.DirectionTo(eye, end, direction) * d, point);
                rebuiltText = $", rebuilt ray {lateral:F3} m (eye ray {eyeLateral:F3} m)";
            }
            parts.Add($"{d:F0} m: old tracer {old:F3} m, bullet {now:F3} m{rebuiltText}");
        }
        float angle = BulletPaths.AngleDegrees(end - muzzle, direction);
        float off = Num.Vector3.Distance(muzzle, origin + direction * Num.Vector3.Dot(muzzle - origin, direction));
        GD.Print($"bullet-probe {kind} P{shooter} {weapon}: ray from {V(origin)} dir {V(direction)} end {V(end)} ({length:F1} m); muzzle {off:F3} m off the ray, old tracer {angle:F2} deg off the ray, join {(join is { } jp ? V(jp) : "none")}; deviation from the ray at {string.Join("; ", parts)}" +
                 (rebuilt is { } r2 ? $"; rebuilt origin {Num.Vector3.Distance(r2, origin):F3} m from the sent one (eye {Num.Vector3.Distance(eye, origin):F3} m)" : "") + extra);
    }

    /// <summary>A shot of another player (<c>--bullet-probe</c>): the same lines against the ray rebuilt from the shooter's camera (the real one is the shooter's own log's).</summary>
    void BulletProbeRemote(ShotView shot, string weapon, Num.Vector3 origin, Num.Vector3 direction, Num.Vector3 muzzle, Num.Vector3 eye) =>
        BulletProbeShot("remote", shot.Shooter, weapon, origin, direction, shot.End, muzzle, null, eye);

    static string V(Num.Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X:F3},{v.Y:F3},{v.Z:F3})");
}
