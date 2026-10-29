using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The camera follows the crosshair a moment late (ui/hud-fidelity; owner, 2026-10-29: "Feels like the camera is too locked to the crosshair. makes it feel stiff compared to the
/// original"). **Temporary (owner, likely to be removed)**: everything is in this file and <see cref="SmNet.CameraLag"/> apart from one-line hooks (<c>RigCamera</c>: the rig's yaw and
/// pitch from <see cref="LaggedView"/> and the eye an unlagged rig would have from <see cref="NoteLagEye"/>; <c>CenterRayTarget</c>: the aim's ray from <see cref="LagAimRay"/>;
/// <c>UpdateHud</c>: <see cref="CrosshairOffset"/>; the latency probe's lines), and <c>--camera-lag 0</c> is the old behaviour exactly.
/// <para>
/// What lags: only the rig. The camera's orbit and its lens follow the view's yaw and pitch through a critically damped spring (smooth time <see cref="DefaultLagMs"/> ms, ours);
/// the view itself (what the mouse turns, the input's yaw and pitch) is never smoothed, and the aim's ray (the crosshair, the aimed-enemy check, the converged shot) starts where an
/// unlagged rig would have put the lens (the rig's offsets at the view's yaw and pitch, with this frame's wall fractions and pushes) and runs along the view. The crosshair is drawn where
/// that ray lands on the screen: off the centre during a fast turn, back on it within about 0.15 s. Off while dead (the state resets: <see cref="SmNet.CameraLag"/> snaps after any frame
/// the rig did not run, a respawn or a teleport), scoped, for bots and spectators.
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary>The lag's smooth time when <c>--camera-lag</c> is not given (ms; ours, the owner's "tiniest of delays": 40 to 60 ms).</summary>
    public const float DefaultLagMs = 50f;

    /// <summary>The feet moving this far in one frame is a jump (a respawn, a teleport): the lag snaps (m).</summary>
    const float LagJump = 3f;

    float? _cameraLagMs;
    readonly CameraLag _lag = new();
    long _lagFrame = -1;
    Num.Vector3 _lagLastFeet;
    // The eye an unlagged rig would have had this frame (game space) and the view and feet it was computed for.
    Num.Vector3 _lagEye, _lagEyeFeet;
    float _lagEyeYaw, _lagEyePitch;
    long _lagEyeFrame = -2;

    /// <summary><c>--camera-lag ms</c> (0: off, the old camera).</summary>
    float CameraLagSeconds => (_cameraLagMs ??= ReadLag()) / 1000f;

    static float ReadLag()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--camera-lag");
        if (i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ms) && float.IsFinite(ms))
            return Math.Clamp(ms, 0f, 500f);
        return DefaultLagMs;
    }

    /// <summary>Whether the lag shaped this frame's camera: the rig ran this frame (or the last) with the lag on, for a living human player.</summary>
    bool CameraLagActive => _lag.Lagging && _lagFrame >= (long)Engine.GetProcessFrames() - 1 && _lagEyeFrame == _lagFrame
        && _client is { Predicted.Alive: true } && RigCameraWanted && !_bot && _camera is not null && _gameSpace is not null;

    /// <summary>The yaw and pitch the rig places the camera with this frame (<see cref="SmNet.CameraLag.Step"/>).</summary>
    (float Yaw, float Pitch) LaggedView(float yaw, float pitch, float delta, PlayerState me)
    {
        long frame = (long)Engine.GetProcessFrames();
        bool enabled = me.Alive && !_bot && Spectate is null && _scopeView is not { Visible: true };
        bool jumped = Num.Vector3.Distance(me.Position, _lagLastFeet) > LagJump;
        _lagLastFeet = me.Position;
        _lagFrame = frame;
        return _lag.Step(yaw, pitch, delta, CameraLagSeconds, enabled, frame, jumped);
    }

    /// <summary>
    /// From <c>RigCamera</c> with the lag on: the eye an unlagged rig would have this frame: the exploration and aim offsets at the view's own pitch (blended by <paramref name="w"/>; the
    /// evade camera's blend is not repeated: the view and the lens agree again before a roll ends), turned by the view's yaw, pulled in by this frame's wall fractions, plus the pushes the
    /// lens itself got (<paramref name="push"/>: bodies and depenetration).
    /// </summary>
    void NoteLagEye(CameraSet ex, CameraSet aim, float w, float pitch, PlayerState me, Vector3 head, Vector3 push)
    {
        if (!_lag.Lagging || _cameraRig is null || _gameSpace is null) return;
        if (_cameraRig.OffsetAtPitch(ex.AnimationCurve, pitch) is not { } e0 || _cameraRig.OffsetAtPitch(aim.AnimationCurve, pitch) is not { } e1) return;
        var cam = Num.Vector3.Lerp(e0.Camera, e1.Camera, w);
        var pivot = Num.Vector3.Lerp(e0.Pivot, e1.Pivot, w);
        float sin = MathF.Sin(_yaw), cos = MathF.Cos(_yaw);
        Vector3 ToWorld(Num.Vector3 v)
        {
            var g = me.Position + new Num.Vector3(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos, v.Z);
            return _gameSpace.Transform * new Vector3(g.X, g.Y, g.Z);
        }
        var pivotWorld = head + (ToWorld(pivot) - head) * _rigFractionA;
        var eye = pivotWorld + (ToWorld(cam) - pivotWorld) * _rigFraction + push;
        var g3 = _gameSpace.Transform.AffineInverse() * eye;
        _lagEye = new Num.Vector3(g3.X, g3.Y, g3.Z);
        _lagEyeYaw = _yaw; _lagEyePitch = pitch; _lagEyeFeet = me.Position; _lagEyeFrame = _lagFrame;
    }

    /// <summary>
    /// With the lag on: the aim's ray (game space) the crosshair, the aimed-enemy check and the shots use instead of the lens's: from the unlagged rig's eye (<see cref="NoteLagEye"/>),
    /// turned to the view of this moment about the feet (<see cref="FreshAim.Turn"/>, as the feel/controls fresh-aim ray), along the view. Null with the lag off (the lens's ray, as before).
    /// </summary>
    (Num.Vector3 Origin, Num.Vector3 Direction)? LagAimRay()
    {
        if (!CameraLagActive) return null;
        float pitch = Math.Clamp(_pitch, _pitchMin, _pitchMax);
        var (origin, _) = FreshAim.Turn(_lagEye, Combat.AimDirection(_lagEyeYaw, _lagEyePitch), _lagEyeFeet, ViewSmoothing.WrapPi(_yaw - _lagEyeYaw), pitch - _lagEyePitch, MaxPitch);
        return (origin, Combat.AimDirection(_yaw, pitch));
    }

    /// <summary>
    /// Where the crosshair goes (the HUD's 1080-high units from the screen centre): where the aim's ray meets the scene (<paramref name="point"/>, the HUD's own centre-ray result), seen
    /// through the lagged lens; zero with the lag off.
    /// </summary>
    Vector2 CrosshairOffset(Num.Vector3 point)
    {
        if (!CameraLagActive || _camera is null || _gameSpace is null) return Vector2.Zero;
        var world = _gameSpace.Transform * new Vector3(point.X, point.Y, point.Z);
        if (_camera.IsPositionBehind(world)) return Vector2.Zero;
        var size = GetViewport().GetVisibleRect().Size;
        if (size.Y < 1f) return Vector2.Zero;
        var offset = _camera.UnprojectPosition(world) - size / 2f;
        return offset * (1080f / size.Y);
    }

    readonly List<double> _lagProbeRay = new(), _lagProbeLens = new(), _lagProbeCaught = new(), _lagProbeTurned = new();

    /// <summary>
    /// <c>--latency-probe</c>: for every sampled input, (1) the angle between the converged shot's ray and the view (the input's yaw and pitch: what the shot is aimed with); (2) on inputs
    /// whose lens has caught up with the view (within 0.01 degrees: the lagged rig is then the unlagged one), the distance across the ray between the ray's origin and the lens itself
    /// turned to the view of this moment (what the fresh-aim ray starts from with the lag off): an independent check that the origin is where an unlagged rig puts the eye; (3) the same
    /// distance on every input (with the lag on, how far a lens-turned origin would have been off: what the review asked to remove); (4) the angle between the lens and the view (the
    /// lag itself). With the lag off all but (4) are 0 by construction.
    /// </summary>
    void LagProbeShot(Num.Vector3 rayOrigin, Num.Vector3 rayDirection)
    {
        if (_camera is null || _gameSpace is null || !_cameraPlacedValid) return;   // not before the first placed camera
        float pitch = Math.Clamp(_pitch, _pitchMin, _pitchMax);
        var view = Combat.AimDirection(_yaw, pitch);
        var inverse = _gameSpace.Transform.AffineInverse();
        var lens = inverse.Basis * (-_camera.GlobalBasis.Z);
        var o3 = inverse * _camera.GlobalPosition;
        var lensTurned = FreshAim.Turn(new Num.Vector3(o3.X, o3.Y, o3.Z), view, _cameraPlacedFeet, ViewSmoothing.WrapPi(_yaw - _cameraPlacedYaw), pitch - _cameraPlacedPitch, MaxPitch).Origin;
        var dir = Num.Vector3.Normalize(rayDirection);
        var rel = rayOrigin - lensTurned;
        double across = (rel - Num.Vector3.Dot(rel, dir) * dir).Length();
        float lensDeg = Mathf.RadToDeg(lens.AngleTo(new Vector3(view.X, view.Y, view.Z)));
        // "Caught up": the lens was placed with the view's own yaw and pitch (the lagged rig was the unlagged one), so the lens turned to this moment must be the origin.
        if (!CameraLagActive || MathF.Abs(ViewSmoothing.WrapPi(_cameraPlacedYaw - _lagEyeYaw)) < 1e-4f && MathF.Abs(_cameraPlacedPitch - _lagEyePitch) < 1e-4f) _lagProbeCaught.Add(across);
        _lagProbeTurned.Add(across);
        _lagProbeRay.Add(Mathf.RadToDeg(new Vector3(rayDirection.X, rayDirection.Y, rayDirection.Z).AngleTo(new Vector3(view.X, view.Y, view.Z))));
        _lagProbeLens.Add(lensDeg);
    }

    /// <summary>The probe's line (null without samples).</summary>
    string? LagProbeSummary() => _lagProbeRay.Count == 0 ? null :
        $"LATENCY aim with {CameraLagInfo}: shot ray against the view {LagStats(_lagProbeRay)} deg; origin off the lens where the lens is on the view {(_lagProbeCaught.Count > 0 ? LagStats(_lagProbeCaught) : "none")} m; "
        + $"origin off the lens turned to the view, every input {LagStats(_lagProbeTurned)} m; lens against the view {LagStats(_lagProbeLens)} deg (over {_lagProbeRay.Count} inputs)";

    static string LagStats(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return $"mean {s.Average():F3} median {s[s.Count / 2]:F3} p95 {s[(int)(s.Count * 0.95)]:F3} max {s[^1]:F3}, over 0.01: {s.Count(x => x > 0.01)}";
    }

    /// <summary>For the probe: the lag and how far the lens trails the aim now (degrees).</summary>
    string CameraLagInfo => CameraLagSeconds <= 0f ? "camera lag off" : $"camera lag {CameraLagSeconds * 1000f:F0} ms";
}
