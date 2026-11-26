using System;
using Godot;
using SmNet;
using SmSim;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>
/// Executions X6: the own executor's camera (the seam <c>ExtrasExecutionCamera</c> in <c>FollowCamera</c>, after the death camera, so a dying executor gets the death view). Owner's brief: the normal
/// camera with a free 360-degree orbit above the ground, as the respawn-wait camera, camera collision kept, no forced cut, a clean return.
/// <list type="bullet">
/// <item><b>Orbit</b>: its own <see cref="DeathOrbit"/> (free yaw, elevation <see cref="DeathOrbit.MinElevation"/> to <see cref="DeathOrbit.MaxElevation"/>), started from the camera as it was (azimuth,
/// elevation and distance of the last camera around the executor's chest, the distance kept within <see cref="OrbitMinRadius"/>..<see cref="OrbitMaxRadius"/>), and blended in from that very transform
/// over <see cref="OrbitBlendSeconds"/>: no cut. The death camera's collision pipeline: the damped cast from the focus (<c>DampFraction</c>), the push out of living players (<c>ClearOfBodies</c>), the
/// depenetration (<c>Depenetrate</c>). The mouse and the right stick orbit it (<see cref="ExecutionsOrbitLook"/>, <see cref="ExecutionsPadLook"/>); the view does not turn.</item>
/// <item><b>Exit</b>: on the End the view takes the orbit camera's heading (and its pitch within the rig's range), the rig camera is placed fresh, and the camera blends into it over
/// <see cref="ExitBlendSeconds"/>. A death during the move goes to the death camera at once (it is checked first); nothing of the orbit carries over.</item>
/// </list>
/// <c>--no-execution-orbit</c> keeps the normal camera during a move. Spectator and observer views are never tied to an execution (they keep their own cameras).
/// </summary>
public partial class NetworkView
{
    /// <summary>The orbit's distance limits from the executor's chest (metres; the death camera's, ours).</summary>
    const float OrbitMinRadius = 2.5f, OrbitMaxRadius = 5f;
    /// <summary>The focus height over the executor's feet (metres; ours: the chest, as the death camera's standing focus).</summary>
    const float OrbitFocusHeight = 1.2f;
    /// <summary>The blend from the last camera into the orbit (seconds; the death camera's <c>DeathBlendSeconds</c>).</summary>
    const float OrbitBlendSeconds = 0.3f;
    /// <summary>The blend from the orbit back to the rig camera at the End (seconds; the design's 0.35 s, ours).</summary>
    public const float ExitBlendSeconds = 0.35f;

    static readonly bool NoExecutionOrbit = CmdFlag("no-execution-orbit");

    readonly DeathOrbit _execOrbit = new();
    readonly CameraBoom _execFraction = new();
    bool _execCamOn;
    double _execCamAge;
    Transform3D _execCamStart;
    float _execCamFov, _execAz, _execEl, _execRadius;
    Vector3 _execFocus;
    bool _execExitOn;
    double _execExitAge;
    Transform3D _execExitFrom;
    float _execExitFov;

    /// <summary>Whether the orbit took the camera this frame (the self-test and the log).</summary>
    public bool ExecutionOrbitActive => _execCamOn;
    /// <summary>Orbits started and exits blended (the self-test's counters).</summary>
    public int ExecutionOrbitsStarted { get; private set; }
    public int ExecutionOrbitExits { get; private set; }

    /// <summary>The own live run, while the own player is connected and alive.</summary>
    bool OwnExecuting(out ExecutionRun run)
    {
        run = default;
        return _client is { Status: ClientStatus.Connected } c && c.Predicted.Alive && Spectate is null && ExecutionsState.TryLive(c.PlayerId, out run)
            && _execPrediction?.Playing(run, c.Tick) != false;   // X3: a run whose three Ends were all lost is over at its own end tick
    }

    partial void ExecutionsCameraWanted(PlayerState me, float delta, ref bool handled)
    {
        if (_camera is null || _gameSpace is null) return;
        if (me.Alive && !NoExecutionOrbit && OwnExecuting(out _))
        {
            _execExitOn = false;
            handled = ExecutionOrbitCamera(me, delta);
            return;
        }
        if (_execCamOn) ExecutionOrbitLeave();
        if (_execExitOn) handled = ExecutionExitBlend(me, delta);
    }

    /// <summary>The orbit's frame: from the camera as it was, round the executor's chest, kept out of walls and bodies.</summary>
    bool ExecutionOrbitCamera(PlayerState me, float delta)
    {
        var feet = _gameSpace!.Transform * new Vector3(me.Position.X, me.Position.Y, me.Position.Z);
        var focus = feet + Vector3.Up * OrbitFocusHeight;
        if (!_execCamOn)
        {
            _execCamOn = true;
            _execCamAge = 0;
            _execCamStart = _camera!.GlobalTransform;
            _execCamFov = _camera.Fov;
            var offset = _execCamStart.Origin - focus;
            float length = MathF.Max(offset.Length(), 0.5f);
            _execAz = MathF.Atan2(offset.Z, offset.X);
            _execEl = Math.Clamp(MathF.Asin(Math.Clamp(offset.Y / length, -1f, 1f)), DeathOrbit.MinElevation, DeathOrbit.MaxElevation);
            _execRadius = Math.Clamp(length, OrbitMinRadius, OrbitMaxRadius);
            _execFocus = focus;
            _execFraction.Reset();
            _execOrbit.Reset();
            _aimBlend = 0f; _evadeBlend = 0f;
            ExecutionOrbitsStarted++;
        }
        _execCamAge += delta;
        _execFocus = _execFocus.Lerp(focus, 1f - MathF.Exp(-10f * delta));   // the slide and the clip's root move it; no shake
        float blend = Math.Clamp((float)_execCamAge / OrbitBlendSeconds, 0f, 1f);
        blend = blend * blend * (3f - 2f * blend);
        float az = _execOrbit.Azimuth(_execAz);
        float el = _execOrbit.Elevation(_execEl);
        var wanted = _execFocus + new Vector3(MathF.Cos(el) * MathF.Cos(az), MathF.Sin(el), MathF.Cos(el) * MathF.Sin(az)) * _execRadius;
        var raw = _execCamStart.Origin.Lerp(wanted, blend);
        CamProbeNote("execution", raw);
        var position = DampFraction(_execFraction, true, Depenetrate(_execFocus, CameraSoftRadius + 0.05f, 3), raw, delta);
        position = Depenetrate(ClearOfBodies(position, me, includeOwn: false, _execFocus, delta));
        var toFocus = _execFocus - position;
        var look = toFocus.LengthSquared() > 1e-4f ? Basis.LookingAt(toFocus.Normalized(), Vector3.Up) : _execCamStart.Basis;
        _camera!.GlobalTransform = new Transform3D(_execCamStart.Basis.Orthonormalized().Slerp(look, blend), position);
        _camera.Fov = _execCamFov;
        _cameraOffset = position - feet;
        _cameraInfo = $"execution orbit: azimuth {az:F2} rad, elevation {el:F2}, {_execRadius:F1} m, blend {blend:F2}, player orbit yaw {_execOrbit.Yaw:F2}{(_execOrbit.Moved ? $" elevation {_execOrbit.Pitch:F2}" : "")}";
        return true;
    }

    /// <summary>The move ended with the own player alive: the view takes the orbit's heading, the rig is placed fresh, the blend back starts.</summary>
    void ExecutionOrbitLeave()
    {
        _execCamOn = false;
        if (_camera is null || _gameSpace is null) return;
        var forward = -_camera.GlobalTransform.Basis.Z;
        var sim = _gameSpace.Transform.Basis.Inverse() * forward;   // Godot to game space (Z up)
        (_yaw, float pitch) = ExecutionCameraMath.ViewOf(new System.Numerics.Vector3(sim.X, sim.Y, sim.Z), _yaw);
        _pitch = Math.Clamp(pitch, _pitchMin, _pitchMax);
        _execExitOn = true;
        _execExitAge = 0;
        _execExitFrom = _camera.GlobalTransform;
        _execExitFov = _camera.Fov;
        // As the death camera's return: the rig snaps to its place for the new view (the blend below hides the snap).
        _cameraPlaced = false;
        _switchT = 1f;
        _aimBlend = 0f; _evadeBlend = 0f; _rigFraction.Reset(); _rigFractionA.Reset(); _rigFractionS.Reset(); _rigFractionU.Reset(); _rigFocus = 0f;
        ExecutionOrbitExits++;
    }

    /// <summary>The return: the rig camera computed as usual, the camera blended from the orbit's last transform into it.</summary>
    bool ExecutionExitBlend(PlayerState me, float delta)
    {
        _execExitAge += delta;
        float t = Math.Clamp((float)(_execExitAge / ExitBlendSeconds), 0f, 1f);
        if (t >= 1f || !RigCameraWanted || !RigCamera(me, delta)) { _execExitOn = false; return false; }
        float s = t * t * (3f - 2f * t);
        var target = _camera!.GlobalTransform;
        _camera.GlobalTransform = new Transform3D(_execExitFrom.Basis.Orthonormalized().Slerp(target.Basis.Orthonormalized(), s), _execExitFrom.Origin.Lerp(target.Origin, s));
        _camera.Fov = Mathf.Lerp(_execExitFov, _camera.Fov, s);
        return true;
    }

    /// <summary>A death or the view leaving: the orbit and the exit blend end without touching the view (the death camera starts from the camera as it is).</summary>
    void ExecutionCameraReset()
    {
        _execCamOn = false;
        _execExitOn = false;
    }

    /// <summary>Mouse look while alive: a locked executor's orbit takes it (the picker open takes nothing).</summary>
    partial void ExecutionsOrbitLook(float yaw, float pitch, ref bool used)
    {
        if (!_execCamOn || PickerBusy) return;
        _execOrbit.Look(yaw, pitch, _execOrbit.Moved ? _execOrbit.Pitch : _execEl);
        used = true;
    }

    /// <summary>The right stick while alive: the same turn the view would take (the pad's curves and rates) orbits the camera instead; the view is left as it was.</summary>
    partial void ExecutionsPadLook(double delta, ref bool used)
    {
        if (!_execCamOn || PickerBusy || _bot) return;
        float yaw0 = _yaw, pitch0 = _pitch;
        PadLook(delta);
        float dy = _yaw - yaw0, dp = _pitch - pitch0;
        _yaw = yaw0; _pitch = pitch0;
        if (dy != 0f || dp != 0f) _execOrbit.Look(dy, dp, _execOrbit.Moved ? _execOrbit.Pitch : _execEl);
        used = true;
    }
}
