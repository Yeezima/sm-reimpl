using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// M40-T6: cameras that never clip or cut. Everything the follow, death and spectator cameras share:
/// <list type="bullet">
/// <item><b>Sphere cast</b> (<see cref="CastClear"/>): a camera is a sphere, not a ray. The cast sweeps a sphere
/// (<see cref="StaticMesh.SweepSphere"/>; camera/hop-and-walls, before it a centre ray and a ring of eight parallel rays) through both collision meshes (the shots' and the
/// characters' layer), so a wall at a glancing angle, a railing or a ceiling edge beside the path stops the camera at the
/// right distance. Two radii: the <i>soft</i> one (<see cref="CameraSoftRadius"/>) is what the camera tries to keep, the
/// <i>hard</i> one (<see cref="CameraHardRadius"/>, the near plane's frustum corner distance with a margin) is never violated.</item>
/// <item><b>Damped pull-in</b> (<see cref="DampFraction"/>, <see cref="SmNet.CameraBoom"/>): the camera's place on its cast is a fraction that moves toward the
/// soft cast's free fraction fast but continuously, and back out slowly after a hold and only for a real gain; the hard cast then clamps it. <b>The hard clamp is the one place a pull-in is
/// instantaneous</b>: a wall the lookahead did not foresee (a corner crossing the camera's line as the view turns, a ceiling met
/// in a flight, a player's own sudden turn) is met by the hard cast the frame it reaches the camera, and then the camera moves by
/// whatever lies between its smoothed place and the wall, up to the leg's length (a few metres) in one frame. The lookahead
/// (rig camera 0.12 s and 0.25 s, spectator 0.2, 0.5 and 1 s) and the soft radius being larger than the hard one make that rare
/// (measured: spec "Cameras that never clip or cut"), not impossible. Moving along the cast keeps the camera from cutting through the
/// player when the view turns.</item>
/// <item><b>Spectator views</b> (<see cref="SpectateFollow"/>): the raw pose of <see cref="SpectateCamera"/> (a target and an
/// eye) moves two speed-limited critically damped springs (the target, and the eye's offset from it), so a change of subject
/// is a blend (about half a second for a few metres; the distance sets the time), pushed out of every living player's body
/// (<see cref="PushOutOfBodies"/>) and cast from the target against the level (with lookahead along the springs' velocities),
/// so the eye is inside the map shell and not in a model. A subject farther than <see cref="SpectateCutDistance"/> (a respawn) is a
/// cut by the scene itself and snaps; <c>--spectate-cuts</c> (capture aid) snaps every change.</item>
/// <item><b>Depenetration</b> (<see cref="Depenetrate"/>): the last step of every camera, a few short rays that push it off any
/// surface closer than the hard radius.</item>
/// </list>
/// Numbers are client-side preferences (ours); the near plane is 0.05 m. The casts see what <c>CollisionWorld</c> holds in both
/// meshes: the level's collision layers and the M39 props (the green ammo crates and door buttons). A prop with no collision shape
/// in the install's data, like the overhead girder in the audit frame, is not in either mesh and the camera does not see it.
/// </summary>
public partial class NetworkView
{
    const float CameraSoftRadius = 0.28f;          // m; the sphere the camera tries to keep clear of the level (ours)
    const float CameraHardRadius = 0.13f;          // m; never violated: the near plane's corner distance (about 0.08 m) and a margin (ours)
    const float CameraPullRate = 20f;              // 1/s; exponential rate of the pull-in (ours)
    const float CameraPullMaxSpeed = 28f;          // m/s; the pull-in's speed cap (ours)
    const float CameraReleaseRate = 3f;            // 1/s; growing back out (ours; the old value was 4)
    const float SpectateSmoothTime = 0.30f;        // s; the spring of the spectator eye (ours)
    const float SpectateTargetSmoothTime = 0.20f;  // s; the spring of the spectator's look target (ours)
    const float SpectateMaxSpeed = 20f;            // m/s; the spectator eye's offset from its target never changes faster than this (ours)
    const float SpectateTargetMaxSpeed = 30f;      // m/s; the look target never moves faster than this (0.5 m per frame at 60 fps; ours)
    const float SpectateCutDistance = 40f;         // m; a subject farther than this from the camera's target is a cut by the scene (a respawn), not a blend (ours)
    const float SpectatePullMaxSpeed = 30f;        // m/s; the spectator camera's pull-in cap (ours)
    static readonly float[] SpectateLookAheadSeconds = [0.2f, 0.5f, 1.0f];
    const float BodyKeepOut = 0.35f;              // m kept clear of a player's capsule by spectator and death cameras (ours)

    /// <summary><c>--old-camera-collision</c> (test aid, camera/hop-and-walls): every follow camera's M40-T6 behaviour: the ring of rays, no release hold or margin, no aim correction.</summary>
    static readonly bool OldCameraCollision = CmdFlag("old-camera-collision");

    bool? _spectateCuts;
    /// <summary><c>--spectate-cuts</c> (capture aid, read from the command line): spectator views jump to their raw pose instead of blending.</summary>
    bool SpectateCuts => _spectateCuts ??= OS.GetCmdlineUserArgs().Contains("--spectate-cuts");

    // Spectator state.
    Vector3 _specEye, _specTarget, _specOffset, _specOffsetVel, _specTargetVel;
    readonly SmNet.CameraBoom _specFraction = new();
    bool _specInit;
    // The rig camera's two booms (head to pivot, pivot to eye); _rigFraction (Camera.cs) is the second.
    readonly SmNet.CameraBoom _rigFractionA = new(), _rigFractionS = new();   // S: head to shoulder (camera/hop-and-walls), before the pivot leg
    // The rig camera's lookahead (see RigCamera): seconds ahead, scratch lines, and the view's turn rate.
    static readonly float[] RigLookAheadSeconds = [0.12f, 0.25f];
    readonly Vector3[] _aheadHead = new Vector3[2], _aheadShoulder = new Vector3[2], _aheadPivot = new Vector3[2], _aheadEye = new Vector3[2];
    readonly System.Collections.Generic.List<Vector3> _aheadFrom = new(2), _aheadTo = new(2);
    float _rigYaw, _rigYawRate;
    bool _rigYawInit;

    // ---- the sphere cast ----

    /// <summary>
    /// How far a sphere of <paramref name="radius"/> can travel from <paramref name="from"/> toward <paramref name="to"/> (Godot
    /// space) before touching the level's collision: the whole distance when clear, never negative. Without map collision the whole distance.
    /// </summary>
    float CastClear(Vector3 from, Vector3 to, float radius)
    {
        float length = from.DistanceTo(to);
        if (length < 1e-3f || _client?.Collision is not { } world || _gameSpace is null) return length;
        var inverse = _gameSpace.Transform.AffineInverse();
        var a3 = inverse * from;
        var d3 = (inverse * to - a3).Normalized();
        var origin = new Num.Vector3(a3.X, a3.Y, a3.Z);
        var dir = new Num.Vector3(d3.X, d3.Y, d3.Z);
        var up = MathF.Abs(dir.Z) < 0.9f ? Num.Vector3.UnitZ : Num.Vector3.UnitX;
        var u = Num.Vector3.Normalize(Num.Vector3.Cross(dir, up));
        var v = Num.Vector3.Cross(dir, u);
        // camera/hop-and-walls: a real swept sphere. The ring below starts its rays a radius beside the cast, so near a wall a ring ray began behind it and the cast read 0 (the camera
        // collapsed to its start and came back: the snapping strafing along walls and pillars).
        if (!OldCameraCollision)
            return Math.Clamp(MathF.Min(SweptHit(world.Weapons, origin, dir, radius, length), SweptHit(world.Character, origin, dir, radius, length)), 0f, length);
        float reach = length + radius;
        float best = length;
        // The centre ray: the sphere stops a radius short of the surface it meets head on.
        best = MathF.Min(best, NearestHit(world, origin, dir, reach) - radius);
        // The ring: a ray at the sphere's rim touches where the rim does.
        for (int i = 0; i < 8; i++)
        {
            float angle = i * (MathF.PI / 4f);
            var offset = (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * radius;
            best = MathF.Min(best, NearestHit(world, origin + offset, dir, reach) - 0.02f);
        }
        return Math.Clamp(best, 0f, length);
    }

    static float SweptHit(StaticMesh mesh, Num.Vector3 origin, Num.Vector3 dir, float radius, float length) =>
        mesh.SweepSphere(origin, dir, radius, length) is { } hit ? hit.Distance : length;

    static float NearestHit(CollisionWorld world, Num.Vector3 origin, Num.Vector3 dir, float reach)
    {
        float hit = float.MaxValue;
        if (world.Weapons.Raycast(origin, dir, reach) is { } a) hit = a.Distance;
        if (world.Character.Raycast(origin, dir, reach) is { } b) hit = MathF.Min(hit, b.Distance);
        return hit;
    }

    /// <summary>
    /// The camera's wanted <paramref name="eye"/> pulled back toward <paramref name="pivot"/> to where a soft sphere stays clear
    /// of the level (Godot space in and out; at once, no smoothing: callers smooth). Without collision, the eye unchanged.
    /// </summary>
    Vector3 Unblocked(Vector3 pivot, Vector3 eye)
    {
        float length = pivot.DistanceTo(eye);
        if (length < 0.02f || _client?.Collision is null) return eye;
        float clear = CastClear(pivot, eye, CameraSoftRadius);
        if (clear >= length) return eye;
        return pivot + (eye - pivot) * (MathF.Max(clear, MathF.Min(0.1f, length)) / length);
    }

    /// <summary>
    /// Damps <paramref name="fraction"/> (the camera's place along <paramref name="from"/> to <paramref name="to"/>) toward what
    /// the soft cast leaves free, then clamps it to what the hard cast leaves free; returns the position. See the class comment.
    /// </summary>
    Vector3 DampFraction(SmNet.CameraBoom boom, bool placed, Vector3 from, Vector3 to, float delta, List<Vector3>? aheadFrom = null, List<Vector3>? aheadTo = null, float pullMaxSpeed = CameraPullMaxSpeed)
    {
        float length = from.DistanceTo(to);
        if (length < 1e-3f || _client?.Collision is null) { boom.Reset(); return to; }
        float soft = Math.Clamp(CastClear(from, to, CameraSoftRadius) / length, 0f, 1f);
        // The lines the same leg will have shortly (the soft target only: the hard clamp is for the present pose).
        if (aheadFrom is not null && aheadTo is not null)
            for (int i = 0; i < aheadFrom.Count; i++)
            {
                float l = aheadFrom[i].DistanceTo(aheadTo[i]);
                if (l > 1e-3f) soft = MathF.Min(soft, Math.Clamp(CastClear(aheadFrom[i], aheadTo[i], CameraSoftRadius) / l, 0f, 1f));
            }
        float hard = Math.Clamp(CastClear(from, to, CameraHardRadius) / length, 0f, 1f);
        if (!OldCameraCollision) boom.Step(soft, hard, length, delta, placed);
        else
        {
            // M40-T6, for before captures: no hold, no margin, the release runs at once.
            float fraction = boom.Fraction;
            if (!placed) fraction = soft;
            else if (soft < fraction)
            {
                float next = Mathf.Lerp(fraction, soft, 1f - MathF.Exp(-CameraPullRate * delta));
                fraction = MathF.Max(next, fraction - pullMaxSpeed * delta / length);
                fraction = MathF.Max(fraction, soft);
            }
            else fraction = Mathf.Lerp(fraction, soft, 1f - MathF.Exp(-CameraReleaseRate * delta));
            boom.Fraction = MathF.Min(fraction, hard);
        }
        return from + (to - from) * boom.Fraction;
    }

    static readonly Num.Vector3[] FanDirs = BuildFanDirs();

    static Num.Vector3[] BuildFanDirs()
    {
        var list = new System.Collections.Generic.List<Num.Vector3>();
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                    if (x != 0 || y != 0 || z != 0) list.Add(Num.Vector3.Normalize(new Num.Vector3(x, y, z)));
        return list.ToArray();
    }

    /// <summary>
    /// The last safeguard of every camera: pushes <paramref name="p"/> (Godot space) out of any surface closer than
    /// <see cref="CameraHardRadius"/> (26 short rays around it; each hit moves it along the surface's normal by what is missing,
    /// two passes). The sphere casts keep the path clear; this catches what slips between their rays (a thin pole, a corner
    /// the camera grazes while turning). Small by construction, so it adds no jump.
    /// </summary>
    Vector3 Depenetrate(Vector3 p, float radius = CameraHardRadius, int passes = 2)
    {
        if (_client?.Collision is not { } world || _gameSpace is null) return p;
        var transform = _gameSpace.Transform;
        var inverse = transform.AffineInverse();
        for (int pass = 0; pass < passes; pass++)
        {
            var g = inverse * p;
            var origin = new Num.Vector3(g.X, g.Y, g.Z);
            var push = Num.Vector3.Zero;
            foreach (var dir in FanDirs)
            {
                RayHit? hit = world.Weapons.Raycast(origin, dir, radius);
                if (world.Character.Raycast(origin, dir, radius) is { } b && (hit is null || b.Distance < hit.Value.Distance)) hit = b;
                if (hit is { } h) push += h.Normal * (radius - h.Distance + 0.005f);
            }
            if (push.LengthSquared() < 1e-10f) break;
            if (push.Length() > radius) push = Num.Vector3.Normalize(push) * radius;   // (a pass never moves more than the radius)
            p = transform * new Vector3(g.X + push.X, g.Y + push.Y, g.Z + push.Z);
        }
        return p;
    }

    // ---- bodies ----

    // Per body (player id): the camera's current push away from it (Godot space) and the call that last updated it.
    readonly Dictionary<int, (Vector3 Push, int Stamp)> _bodyPush = new();
    readonly List<int> _bodyKeys = new();
    int _bodyStamp;

    /// <summary>
    /// Moves <paramref name="p"/> (Godot space) out of every living player's capsule padded by <see cref="BodyKeepOut"/> (the
    /// own one too when <paramref name="includeOwn"/>), horizontally away from its axis; a point on an axis goes away from
    /// <paramref name="awayFrom"/> (the camera's target). Call it once per frame and camera: it keeps state.
    /// <para>
    /// Continuous (M40-T6 review): each body's push is a vector that moves toward what the body now needs by an exponential
    /// rate (<see cref="BodyPushRate"/>) at no more than <see cref="BodyPushMaxSpeed"/>, and decays to nothing the same way when the
    /// body is out of reach, so a player crossing the camera's axis (melee circling, a chaser running through) flips the push over a
    /// few frames instead of moving the camera up to 2 x radius in one. The price is a few frames of the camera partly inside the
    /// padding (0.35 m, then the capsule), never a jump.
    /// </para>
    /// </summary>
    Vector3 PushOutOfBodies(Vector3 p, PlayerState me, bool includeOwn, Vector3 awayFrom, float delta)
    {
        if (_config is null || _gameSpace is null) return p;
        float radius = _config.CapsuleRadius + BodyKeepOut;
        _bodyStamp++;
        var transform = _gameSpace.Transform;
        void One(PlayerState s)
        {
            var f = transform * new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
            float y = Math.Clamp(p.Y, f.Y, f.Y + _config.CapsuleHeight);
            var axis = new Vector3(f.X, y, f.Z);
            var away = p - axis;
            away.Y = 0f;   // horizontal: the push never lifts or drops the camera
            float distance = away.Length();
            var target = Vector3.Zero;
            if (distance < radius)
            {
                if (distance < 1e-3f) { away = awayFrom - axis; away.Y = 0; distance = away.Length(); if (distance < 1e-3f) away = new Vector3(1, 0, 0); }
                target = axis + away.Normalized() * radius - new Vector3(p.X, axis.Y, p.Z);
                target.Y = 0f;
            }
            _bodyPush.TryGetValue(s.PlayerId, out var state);
            _bodyPush[s.PlayerId] = (MoveBodyPush(state.Push, target, delta), _bodyStamp);
        }
        for (int i = 0; i < _lastOthers.Count; i++) if (_lastOthers[i].Alive) One(_lastOthers[i]);
        if (includeOwn && me.Alive) One(me);
        // Bodies that were not asked about this call (dead, gone, or the own one when excluded) let go the same way.
        _bodyKeys.Clear();
        foreach (var key in _bodyPush.Keys) _bodyKeys.Add(key);
        var total = Vector3.Zero;
        for (int i = 0; i < _bodyKeys.Count; i++)
        {
            var (push, stamp) = _bodyPush[_bodyKeys[i]];
            if (stamp != _bodyStamp) push = MoveBodyPush(push, Vector3.Zero, delta);
            if (push.LengthSquared() < 1e-6f && stamp != _bodyStamp) { _bodyPush.Remove(_bodyKeys[i]); continue; }
            _bodyPush[_bodyKeys[i]] = (push, _bodyStamp);
            total += push;
        }
        if (total.Length() > 2f * radius) total = total.Normalized() * 2f * radius;
        return p + total;
    }

    const float BodyPushRate = 12f;        // 1/s; how fast a body's push follows what it needs (ours)
    const float BodyPushMaxSpeed = 15f;    // m/s; the most a push changes (0.25 m per frame at 60 fps; ours)

    static Vector3 MoveBodyPush(Vector3 current, Vector3 target, float delta)
    {
        var step = (target - current) * (1f - MathF.Exp(-BodyPushRate * MathF.Max(delta, 0f)));
        float limit = BodyPushMaxSpeed * MathF.Max(delta, 0f);
        if (step.LengthSquared() > limit * limit) step = step.Normalized() * limit;
        return current + step;
    }

    /// <summary>
    /// <see cref="PushOutOfBodies"/> for a camera already clear of the level: the push is shortened where a hard sphere cast
    /// meets the level on its way (the near plane stays out of walls; a body then wins over a wall by at most the pad).
    /// </summary>
    Vector3 ClearOfBodies(Vector3 p, PlayerState me, bool includeOwn, Vector3 awayFrom, float delta)
    {
        var pushed = PushOutOfBodies(p, me, includeOwn, awayFrom, delta);
        float distance = p.DistanceTo(pushed);
        if (distance < 1e-4f || _client?.Collision is null) return pushed;
        float clear = CastClear(p, pushed, CameraHardRadius);
        return clear < distance ? p + (pushed - p) * (clear / distance) : pushed;
    }

    // ---- spring ----

    /// <summary>A critically damped spring toward <paramref name="target"/> with a speed limit (after the usual SmoothDamp).</summary>
    static Vector3 SmoothDamp(Vector3 current, Vector3 target, ref Vector3 velocity, float smoothTime, float maxSpeed, float delta)
    {
        if (delta <= 0f) return current;
        smoothTime = MathF.Max(1e-4f, smoothTime);
        float omega = 2f / smoothTime, x = omega * delta;
        float e = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        var change = current - target;
        var original = target;
        float maxChange = maxSpeed * smoothTime;
        if (change.LengthSquared() > maxChange * maxChange) change = change.Normalized() * maxChange;
        target = current - change;
        var temp = (velocity + change * omega) * delta;
        velocity = (velocity - temp * omega) * e;
        var output = target + (change + temp) * e;
        if ((original - current).Dot(output - original) > 0f) { output = original; velocity = Vector3.Zero; }
        return output;
    }

    // ---- the follow camera ----

    /// <summary>
    /// The follow camera: the death camera (NetworkView.Death.cs) while dead, the original rig (NetworkView.Camera.cs), a spectator
    /// view, or the placeholder cameras (the flat-ground bot view, the bot chase camera on a map, the over-the-shoulder view when
    /// the rig failed to load). The rig, the spectator views and the death camera cast a sphere with a damped pull-in
    /// (<see cref="DampFraction"/>); the bot's chase camera and the placeholder shoulder camera only cut their wall rays short at
    /// once (<see cref="Unblocked"/>, then their own smoothing). A "cam:" spectator view is exact. See the class comment.
    /// </summary>
    void FollowCamera(PlayerState me, float delta)
    {
        if (_camera is null || _gameSpace is null) return;
        _camProbeSource = "placeholder";
        if (DeathCamera(me, delta)) return;   // dead: the view of the own ragdoll (NetworkView.Death.cs)
        if (RigCameraWanted && RigCamera(me, delta)) return;   // the original rig (NetworkView.Camera.cs); else the placeholder below
        _camera.Fov = DefaultFov;
        // Behind the player along its facing, in Godot space.
        var gamePos = new Vector3(me.Position.X, me.Position.Y, me.Position.Z);
        var forwardGame = new Vector3(-MathF.Sin(me.Yaw), MathF.Cos(me.Yaw), 0f);
        var pos = _gameSpace.Transform * gamePos;
        var forward = _gameSpace.Basis * forwardGame;
        var head = pos + Vector3.Up * (_config?.EyeHeight ?? 1.6f);   // the player's head: always a safe place to cast from
        if (SpectateFollow(me, pos, delta)) return;
        bool mapped = _mapNote is not null;
        Vector3 eye, target;
        if (_bot && mapped)
        {
            // On a map: follow the local player from behind and above, so screenshots show the arena around it.
            eye = Unblocked(head, pos - forward * 6f + Vector3.Up * 3.5f);
            target = pos + forward * 4f + Vector3.Up * 1.2f;
        }
        else if (_bot)
        {
            // Flat ground: frame every player: aim at their centre from behind the local player, farther when spread out.
            var points = _remotes.Values.Select(r => r.GlobalPosition).Append(pos).ToList();
            var centre = points.Aggregate(Vector3.Zero, (a, b) => a + b) / points.Count;
            float spread = points.Max(p => p.DistanceTo(centre));
            eye = centre - forward * (7f + 1.3f * spread) + Vector3.Up * (5f + 1.0f * spread);
            target = centre + Vector3.Up;
        }
        else
        {
            // Over the right shoulder, looking parallel to the aim. The crosshair is drawn where the simulation's shot ray
            // (from EyeHeight above the feet) actually goes (UpdateCrosshair), so the offset does not mislead.
            // Placeholder: used only when the original camera rig failed to load.
            const float back = 3.6f, side = 0.7f, lift = 0.3f;
            var aimGame = Combat.AimDirection(me.Yaw, me.Pitch);
            var aim = _gameSpace.Basis * new Vector3(aimGame.X, aimGame.Y, aimGame.Z);
            var right = forward.Cross(Vector3.Up).Normalized();
            var pivot = head + Vector3.Up * lift + right * side;
            if (mapped) pivot = Unblocked(head, pivot);   // a wall beside the head shortens the shoulder offset too
            // Come closer when aiming up, to stay above the player's feet (the ground when standing; in flight the
            // ground is farther down, so this is conservative).
            float distance = aim.Y > 1e-3f ? MathF.Min(back, (pivot.Y - (pos.Y + 0.3f)) / aim.Y) : back;
            eye = pivot - aim * distance;
            if (mapped) eye = Unblocked(pivot, eye);
            target = eye + aim;   // still parallel to the aim after the eye was pulled in
        }
        CamProbeNote("placeholder", eye);
        // Anchored views (on a map, and the over-the-shoulder view) smooth only the camera's offset from the player, so
        // a fast jump or dive (up to 50 m/s) does not leave the camera trailing behind (or under) the player (M14).
        bool anchored = !_bot || mapped;
        float k = _cameraPlaced ? 1f - MathF.Exp(-(_bot ? 3f : 12f) * delta) : 1f;
        _cameraPlaced = true;
        if (anchored)
        {
            // Lerp the direction, keep the distance lerped separately: a plain lerp across a fast turn would pull the
            // camera through (and into) the player.
            var wanted = eye - pos;
            float length = Mathf.Lerp(_cameraOffset.Length(), wanted.Length(), k);
            var direction = _cameraOffset.Lerp(wanted, k);
            _cameraOffset = direction.LengthSquared() > 1e-6f ? direction.Normalized() * length : wanted;
            _camera.GlobalPosition = pos + _cameraOffset;
        }
        else _camera.GlobalPosition = _camera.GlobalPosition.Lerp(eye, k);
        // The smoothed position can still be behind a wall (or the eye grew back out through one): shrinking is immediate,
        // and the stored offset is the clamped one, so only the growing back out is smoothed.
        if (mapped) _camera.GlobalPosition = Unblocked(head, _camera.GlobalPosition);
        _cameraOffset = _camera.GlobalPosition - pos;
        // Too close to the head: hide the own model (with a little hysteresis so it does not flicker at the limit).
        // Only in the player's own follow views on a map.
        if (_local is not null)
        {
            float closest = _camera.GlobalPosition.DistanceTo(head);
            if (mapped) UpdateOwnHidden(closest); else SetOwnHidden(false);
        }
        var look = _camera.GlobalTransform.LookingAt(target, Vector3.Up);
        _camera.GlobalBasis = _camera.GlobalBasis.Slerp(look.Basis.Orthonormalized(), k);
    }

    // ---- spectator views ----

    /// <summary>
    /// Places the camera for <c>--spectate</c>: false when there is no spectator view (the caller places the camera). The raw
    /// pose comes from <see cref="SpectateCamera"/>.
    /// </summary>
    bool SpectateFollow(PlayerState me, Vector3 pos, float delta)
    {
        if (_camera is null || SpectateCamera(me, pos, delta) is not { } view) return false;
        var (eyeRaw, targetRaw) = view;
        string mode = _spectateMode ?? "";
        // "cam:" is a measured view (matched lighting before/after, footage): the exact pose, no springs, cast, push-out or depenetration.
        if (mode.StartsWith("cam:", StringComparison.Ordinal) && _spectateFixed is not null)
        {
            if (!_specInit) { _specInit = true; CamProbeCut(); }
            if (CameraProbeOn) CamProbeNote("spectate cam", eyeRaw);
            _cameraPlaced = true;
            var look = targetRaw - eyeRaw;
            _camera.GlobalTransform = new Transform3D(look.LengthSquared() > 1e-8f ? Basis.LookingAt(look.Normalized(), Vector3.Up) : _camera.GlobalBasis, eyeRaw);
            _camera.Fov = DefaultFov;
            _cameraOffset = eyeRaw - pos;
            if (_local is not null) SetOwnHidden(false);
            return true;
        }
        // Two springs, on the look target and on the eye's offset from it (not on the eye itself: two springs of different
        // speeds would leave the eye far from its target during a long move, and every wall crossing that line would be a jump
        // of that length). A subject that is suddenly far away (a respawn, a shot view on the other side of the map: more than
        // SpectateCutDistance from where the camera looks) is a cut by the scene itself and snaps; with --spectate-cuts every
        // change snaps.
        bool first = !_specInit;
        bool teleported = !first && _specTarget.DistanceTo(targetRaw) > SpectateCutDistance;
        if (first || teleported || SpectateCuts)
        {
            _specTarget = targetRaw; _specOffset = eyeRaw - targetRaw; _specOffsetVel = Vector3.Zero; _specTargetVel = Vector3.Zero;
            _specInit = true;
            CamProbeCut();
        }
        else
        {
            _specTarget = SmoothDamp(_specTarget, targetRaw, ref _specTargetVel, SpectateTargetSmoothTime, SpectateTargetMaxSpeed, delta);
            _specOffset = SmoothDamp(_specOffset, eyeRaw - targetRaw, ref _specOffsetVel, SpectateSmoothTime, SpectateMaxSpeed, delta);
        }
        _specEye = _specTarget + _specOffset;
        if (CameraProbeOn) CamProbeNote("spectate " + mode, _specEye);
        // A sphere cast from the target (damped), then out of bodies (once; the target is often a player, so the cast can pull the
        // eye close to one), then depenetration. "overview" (aerial) is not cast; "inspect" (a close orbit around the own weapon) keeps
        // no distance from bodies.
        bool mapped = _mapNote is not null && mode != "overview";
        var eye = _specEye;
        if (mapped)
        {
            // Lookahead along the springs' own velocities (0.2, 0.5 and 1 s), so a wall about to cross the line starts the pull-in
            // early. The look target's extrapolated start is cut short where a soft sphere meets the level (a point 8 m ahead at a
            // run could be inside solid or outside the shell); the eye keeps its offset from it.
            _aheadFrom.Clear(); _aheadTo.Clear();
            foreach (float t in SpectateLookAheadSeconds)
            {
                var step = _specTargetVel * t;
                float stepLength = step.Length();
                if (stepLength > 0.05f) step *= CastClear(_specTarget, _specTarget + step, CameraSoftRadius) / stepLength;
                var start = _specTarget + step;
                _aheadFrom.Add(start);
                _aheadTo.Add(start + _specOffset + _specOffsetVel * t);
            }
            // The cast starts from the look target moved off any surface within the soft radius (a target between two players
            // fighting beside a railing, or in a wall between them, would otherwise start inside collision: free fraction 0 and the
            // camera collapses onto it).
            var castFrom = Depenetrate(_specTarget, CameraSoftRadius + 0.05f, 3);
            eye = DampFraction(_specFraction, !(first || teleported || SpectateCuts), castFrom, eye, delta, _aheadFrom, _aheadTo, SpectatePullMaxSpeed);
        }
        else _specFraction.Reset();
        if (mode != "inspect") eye = ClearOfBodies(eye, me, includeOwn: true, _specTarget, delta);
        eye = Depenetrate(eye);
        _cameraPlaced = true;
        var toTarget = _specTarget - eye;
        var basis = toTarget.LengthSquared() > 1e-4f ? Basis.LookingAt(toTarget.Normalized(), Vector3.Up) : _camera.GlobalBasis;
        _camera.GlobalTransform = new Transform3D(basis, eye);
        _camera.Fov = DefaultFov;
        _cameraOffset = eye - pos;
        if (_local is not null) SetOwnHidden(false);
        return true;
    }
}
