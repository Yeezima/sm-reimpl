using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// <c>--camera-probe</c> (M40-T6 test aid; read from the command line, so no other file is touched): checks the final camera of
/// every frame against the level collision and prints what it finds, nothing else changes.
/// <list type="bullet">
/// <item><b>Near plane in collision.</b> The near plane's rectangle (<c>Near</c>, the field of view and the viewport's aspect)
/// is tested as segments against both collision meshes: from the camera to the corners, the edge middles and the centre, and
/// the four edges of the rectangle; plus a 26-direction ray fan around the camera point, whose nearest hit (capped at 1 m) is
/// the <i>clearance</i>. A frame is flagged when a segment hits or the clearance is below the near plane's corner distance
/// (the camera point itself is in or at a surface).</item>
/// <item><b>Jump.</b> The camera's displacement from the previous frame, and the part of it the camera's own wanted (uncollided)
/// position does not explain (the "collision" part: a pull-in or push-out, as opposed to the rig turning with the mouse). A
/// frame is flagged when the jump exceeds 0.5 m and also 0.5 m at 60 frames per second (frame hitches of 60 to 150 ms are common
/// on the loaded test machine); a jump while the rig itself moved over 1.5 m is a view turn (a bot's instant turn), not a collision effect; respawn teleports and an explicit <c>--spectate-cuts</c> are not counted.</item>
/// <item><b>Body.</b> The distance from the camera to the axis of every other living player's capsule (and the own one in a
/// spectate view); flagged when inside the capsule radius.</item>
/// </list>
/// A line per flagged frame (<c>CAMFLAG</c>) and a cumulative summary every 5 s (<c>CAMPROBE</c>) with the frame rate (a
/// loaded machine makes the per-frame jump larger: judge the jump together with the frame time).
/// </summary>
public partial class NetworkView
{
    const float CamProbeJumpLimit = 0.5f;
    const float CamProbeTurnLimit = 1.5f;   // m the rig's own wanted position moved in one frame: a view turn, not a collision jump

    bool? _cameraProbe;
    bool CameraProbeOn => _cameraProbe ??= OS.GetCmdlineUserArgs().Contains("--camera-probe");

    Vector3 _camProbePrev, _camProbePrevWanted, _camProbePrevFeet;
    bool _camProbePrevValid;
    string _camProbeSource = "";
    Vector3? _camProbeWanted;
    bool _camProbeCut;                 // set by the camera code for a scripted snap (respawn, first placement)
    int _camProbeRawJumps, _camProbeFrames, _camProbeNear, _camProbeJumps, _camProbeCollisionJumps, _camProbeTurnJumps, _camProbeBody, _camProbeCuts;
    float _camProbeMaxJump, _camProbeMaxCollisionJump, _camProbeMinClearance = float.MaxValue, _camProbeMinBody = float.MaxValue;
    double _camProbeLogAt, _camProbeFrameTime, _camProbeMaxFrame;

    bool? _camSelfTest;
    bool _camSelfTestDone;
    bool CameraSelfTestOn => _camSelfTest ??= OS.GetCmdlineUserArgs().Contains("--camera-selftest");

    /// <summary>
    /// <c>--camera-selftest</c> (with <c>--camera-probe</c>; runs once, on the first frame): feeds <see cref="PushOutOfBodies"/>
    /// synthetic players against a camera that stands still and prints the largest camera step per frame at 60 fps, for the body
    /// push of this build and for the first version (the push recomputed from the camera's side each frame). Cases: a player
    /// running through the camera's axis at 8 m/s (a chaser), a player circling it at 0.5 m radius at 3 rad/s (melee).
    /// Output: <c>CAMSELFTEST</c> lines.
    /// </summary>
    void CameraSelfTest(PlayerState me)
    {
        if (_config is null || _gameSpace is null) return;
        var savedOthers = _lastOthers;
        var camera = _gameSpace.Transform * new Vector3(0f, 0f, 1.5f);   // game (0, 0, 1.5)
        float radius = _config.CapsuleRadius + BodyKeepOut;
        const float dt = 1f / 60f;
        string[] names = ["chaser crossing the axis at 8 m/s", "melee circling at 0.5 m, 3 rad/s"];
        for (int c = 0; c < 2; c++)
        {
            _bodyPush.Clear();
            float worstNew = 0f, worstOld = 0f, deepest = float.MaxValue;
            Vector3 prevNew = camera, prevOld = camera;
            for (int step = 0; step < 240; step++)
            {
                float t = step * dt;
                var pos = c == 0 ? new System.Numerics.Vector3(-8f * 2f + 8f * t, 0.02f, 0f)   // 2 s of approach, then through and out
                                 : new System.Numerics.Vector3(0.5f * MathF.Cos(3f * t), 0.5f * MathF.Sin(3f * t), 0f);
                var body = me with { PlayerId = 9001, Position = pos, Alive = true };
                _lastOthers = [body];
                var pushed = PushOutOfBodies(camera, me with { Alive = false }, includeOwn: false, camera + new Vector3(0, 0, 3), dt);
                // The first version: from the camera's side, instantly.
                var f = _gameSpace.Transform * new Vector3(pos.X, pos.Y, pos.Z);
                var axis = new Vector3(f.X, Math.Clamp(camera.Y, f.Y, f.Y + _config.CapsuleHeight), f.Z);
                var away = camera - axis; away.Y = 0f;
                float d = away.Length();
                var old = camera;
                if (d < radius) { if (d < 1e-3f) away = new Vector3(1, 0, 0); old = new Vector3(axis.X, camera.Y, axis.Z) + away.Normalized() * radius; }
                if (step > 0) { worstNew = MathF.Max(worstNew, pushed.DistanceTo(prevNew)); worstOld = MathF.Max(worstOld, old.DistanceTo(prevOld)); }
                deepest = MathF.Min(deepest, new Vector3(pushed.X - f.X, 0, pushed.Z - f.Z).Length());
                prevNew = pushed; prevOld = old;
            }
            GD.Print($"CAMSELFTEST {names[c]}: largest camera step per frame at 60 fps: this build {worstNew:F3} m, first version {worstOld:F3} m; nearest the camera got to the body's axis {deepest:F2} m (capsule radius {_config.CapsuleRadius:F2}, keep-out {radius:F2})");
        }
        _lastOthers = savedOthers;
        _bodyPush.Clear();
    }

    /// <summary>The camera code names where the camera wanted to be this frame (before collision) and which view it is, for the probe.</summary>
    void CamProbeNote(string source, Vector3 wanted) { _camProbeSource = source; _camProbeWanted = wanted; }

    /// <summary>The camera was placed by a scripted snap this frame (not counted as a jump).</summary>
    void CamProbeCut() => _camProbeCut = true;

    static readonly Vector3[] FanDirections = BuildFan();

    static Vector3[] BuildFan()
    {
        var list = new List<Vector3>();
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                    if (x != 0 || y != 0 || z != 0) list.Add(new Vector3(x, y, z).Normalized());
        return list.ToArray();
    }

    /// <summary>Called right after the camera is placed (the end of <c>FollowCamera</c>).</summary>
    void CameraProbeFrame(PlayerState me, double delta)
    {
        if (!CameraProbeOn || _camera is null || _gameSpace is null || _client?.Collision is not { } world || _config is null) return;
        var pos = _camera.GlobalPosition;
        var wanted = _camProbeWanted ?? pos;
        var feet = _gameSpace.Transform * new Vector3(me.Position.X, me.Position.Y, me.Position.Z);
        double frame = delta;
        if (CameraSelfTestOn && !_camSelfTestDone) { _camSelfTestDone = true; CameraSelfTest(me); }
        _camProbeFrameTime += frame; _camProbeMaxFrame = Math.Max(_camProbeMaxFrame, frame);
        _camProbeFrames++;

        // Near plane and clearance, in game space (the collision's).
        var inverse = _gameSpace.Transform.AffineInverse();
        var basis = _camera.GlobalBasis;
        float near = _camera.Near;
        var size = _camera.GetViewport().GetVisibleRect().Size;
        float aspect = size.Y > 0 ? size.X / size.Y : 16f / 9f;
        float hh = near * MathF.Tan(Mathf.DegToRad(_camera.Fov) * 0.5f), hw = hh * aspect;
        Vector3 Plane(float x, float y) => pos + basis * new Vector3(x * hw, y * hh, -near);
        var corners = new[] { Plane(-1, -1), Plane(1, -1), Plane(1, 1), Plane(-1, 1) };
        var targets = new List<Vector3>(corners) { Plane(0, 0), Plane(0, -1), Plane(1, 0), Plane(0, 1), Plane(-1, 0) };
        bool nearHit = false;
        string nearHow = "";
        bool Segment(Vector3 a, Vector3 b)
        {
            var a3 = inverse * a; var b3 = inverse * b;
            var o = new Num.Vector3(a3.X, a3.Y, a3.Z);
            var d = new Num.Vector3(b3.X - a3.X, b3.Y - a3.Y, b3.Z - a3.Z);
            float l = d.Length();
            if (l < 1e-6f) return false;
            d /= l;
            return world.Weapons.Raycast(o, d, l) is not null || world.Character.Raycast(o, d, l) is not null;
        }
        foreach (var t in targets) if (Segment(pos, t)) { nearHit = true; nearHow = "camera to near plane"; break; }
        if (!nearHit)
            for (int i = 0; i < 4; i++) if (Segment(corners[i], corners[(i + 1) % 4])) { nearHit = true; nearHow = "near plane edge"; break; }
        float clearance = 1f;
        var origin3 = inverse * pos;
        var origin = new Num.Vector3(origin3.X, origin3.Y, origin3.Z);
        foreach (var dir in FanDirections)
        {
            var d3 = inverse.Basis * dir;
            var d = Num.Vector3.Normalize(new Num.Vector3(d3.X, d3.Y, d3.Z));
            float h = float.MaxValue;
            if (world.Weapons.Raycast(origin, d, 1f) is { } a) h = a.Distance;
            if (world.Character.Raycast(origin, d, 1f) is { } b) h = MathF.Min(h, b.Distance);
            clearance = MathF.Min(clearance, h);
        }
        float corner = MathF.Sqrt(near * near + hw * hw + hh * hh);
        bool tooClose = clearance < corner;
        _camProbeMinClearance = MathF.Min(_camProbeMinClearance, clearance);

        // Bodies.
        float bodyDistance = float.MaxValue;
        string bodyWho = "";
        void Body(PlayerState p)
        {
            var f = _gameSpace.Transform * new Vector3(p.Position.X, p.Position.Y, p.Position.Z);
            float y = Math.Clamp(pos.Y, f.Y, f.Y + _config.CapsuleHeight);
            float dist = new Vector3(pos.X - f.X, pos.Y - y, pos.Z - f.Z).Length();
            if (dist < bodyDistance) { bodyDistance = dist; bodyWho = "P" + p.PlayerId; }
        }
        foreach (var o in _lastOthers) if (o.Alive) Body(o);
        if (me.Alive && Spectate is not null) Body(me);
        bool inBody = bodyDistance < _config.CapsuleRadius;
        _camProbeMinBody = MathF.Min(_camProbeMinBody, bodyDistance);

        // Jump.
        float jump = 0f, collisionJump = 0f;
        bool cut = _camProbeCut || (Spectate is not null && SpectateCuts) || !_camProbePrevValid;
        if (_camProbePrevValid && feet.DistanceTo(_camProbePrevFeet) > 2f) cut = true;   // the player teleported (respawn)
        if (_camProbePrevValid && !cut)
        {
            jump = pos.DistanceTo(_camProbePrev);
            collisionJump = ((pos - _camProbePrev) - (wanted - _camProbePrevWanted)).Length();
            _camProbeMaxJump = MathF.Max(_camProbeMaxJump, jump);
            if (jump > CamProbeJumpLimit) _camProbeRawJumps++;   // raw: metres per frame, nothing normalised or excluded but scripted cuts
            if ((wanted - _camProbePrevWanted).Length() < CamProbeTurnLimit) _camProbeMaxCollisionJump = MathF.Max(_camProbeMaxCollisionJump, collisionJump);
        }
        else if (_camProbePrevValid) _camProbeCuts++;
        // A jump counts when it is over 0.5 m and also over 0.5 m at 60 frames per second (speed above 30 m/s): this machine runs
        // some frames at 60 to 150 ms, where an ordinary 10 m/s glide is already 0.6 to 1.5 m per frame.
        double speed60 = frame > 1e-4 ? jump * (1.0 / 60.0) / frame : jump;
        bool jumped = jump > CamProbeJumpLimit && speed60 > CamProbeJumpLimit;
        bool nearBad = nearHit || tooClose;
        if (nearBad) _camProbeNear++;
        float rigMove = (wanted - _camProbePrevWanted).Length();
        bool viewTurn = rigMove >= CamProbeTurnLimit;   // the rig itself moved a lot (a bot's instant 180 degree turn): not a collision effect
        if (jumped) { _camProbeJumps++; if (viewTurn) _camProbeTurnJumps++; else if (collisionJump > CamProbeJumpLimit * 0.5f) _camProbeCollisionJumps++; }
        if (inBody) _camProbeBody++;
        if (nearBad || jumped || inBody)
            GD.Print($"CAMFLAG t {_connectedFor:F2} s [{_camProbeSource}] frame {frame * 1000:F0} ms" +
                     (jumped ? $" JUMP {jump:F2} m (collision part {collisionJump:F2}, wanted moved {rigMove:F2}{(viewTurn ? ", view turn" : "")})" : "") +
                     (nearBad ? $" NEAR {(nearHit ? nearHow : "camera point at a surface")} clearance {clearance:F3} m (corner {corner:F3})" : "") +
                     (inBody ? $" BODY inside {bodyWho} at {bodyDistance:F2} m" : "") +
                     $" camera {pos.X:F1},{pos.Y:F1},{pos.Z:F1} alive {me.Alive}");

        _camProbePrev = pos; _camProbePrevWanted = wanted; _camProbePrevFeet = feet; _camProbePrevValid = true;
        _camProbeWanted = null; _camProbeCut = false;
        if (_connectedFor - _camProbeLogAt >= 5.0)
        {
            _camProbeLogAt = _connectedFor;
            GD.Print($"CAMPROBE t {_connectedFor:F0} s: {_camProbeFrames} frames (mean {_camProbeFrameTime / Math.Max(_camProbeFrames, 1) * 1000:F1} ms, worst {_camProbeMaxFrame * 1000:F0} ms), near plane in collision {_camProbeNear}, " +
                     $"RAW frames with a jump over {CamProbeJumpLimit} m {_camProbeRawJumps}; normalised to 60 fps {_camProbeJumps} (view turns {_camProbeTurnJumps}, collision-driven {_camProbeCollisionJumps}; cuts skipped {_camProbeCuts}), max jump {_camProbeMaxJump:F2} m, max collision part with the view moving under {CamProbeTurnLimit} m {_camProbeMaxCollisionJump:F2} m, " +
                     $"min clearance {_camProbeMinClearance:F3} m, inside a body {_camProbeBody} (nearest {_camProbeMinBody:F2} m); view {_camProbeSource}");
        }
    }
}
