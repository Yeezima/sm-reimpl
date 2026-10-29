using System;
using System.Linq;
using Godot;
using SmNet;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The camera follows the crosshair a moment late (ui/hud-fidelity; owner, 2026-10-29: "Feels like the camera is too locked to the crosshair. makes it feel stiff compared to the
/// original"). **Temporary (owner, likely to be removed)**: everything is in this file apart from three one-line hooks (<c>RigCamera</c>: the rig's yaw and pitch come from
/// <see cref="LaggedView"/>; <c>CenterRayTarget</c>: the aim's ray from <see cref="LagAimRay"/>; <c>UpdateHud</c>: <see cref="CrosshairOffset"/>), and <c>--camera-lag 0</c> is the old
/// behaviour exactly.
/// <para>
/// What lags: only the rig. The camera's orbit and its lens follow the view's yaw and pitch through a critically damped spring (Unity's SmoothDamp form, smooth time
/// <see cref="DefaultLagMs"/> ms, ours); the view itself (what the mouse turns, the input's yaw and pitch, the converged shot's ray) is never smoothed. Because the lens now trails
/// the aim, the crosshair is drawn where the aim's ray lands on the screen (a few degrees off the centre during a fast turn, back on it within about 0.15 s), so the crosshair,
/// the aimed-enemy check and the shots all keep using the aim's ray from the camera (the feel/controls fresh-aim ray: the camera turned to the view of this moment). Off for bots,
/// spectators and the scope overlay (the scope's reticle is fixed at the screen centre).
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary>The lag's smooth time when <c>--camera-lag</c> is not given (ms; ours, the owner's "tiniest of delays": 40 to 60 ms).</summary>
    public const float DefaultLagMs = 50f;

    /// <summary>A view this far (radians) from the lagged one is jumped to at once (a respawn, a teleport, the death camera's hand-back).</summary>
    const float LagSnap = 1.2f;

    float? _cameraLagMs;
    float _lagYaw, _lagPitch, _lagYawRate, _lagPitchRate;
    bool _lagInit, _lagUsed;

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

    /// <summary>Whether the lag shaped this frame's camera (a human's rig camera with a lag above 0, not scoped).</summary>
    bool CameraLagActive => _lagUsed && CameraLagSeconds > 0f && RigCameraWanted && !_bot && _camera is not null;

    /// <summary>
    /// The yaw and pitch the rig places the camera with this frame: the view's, followed by the spring (<see cref="DefaultLagMs"/>); the view's itself when the lag is off, the player
    /// is dead, a scope is up, or the view jumped more than <see cref="LagSnap"/>.
    /// </summary>
    (float Yaw, float Pitch) LaggedView(float yaw, float pitch, float delta, bool alive)
    {
        float smooth = CameraLagSeconds;
        bool scoped = _scopeView is { Visible: true };
        _lagUsed = smooth > 0f && alive && !scoped && !_bot && Spectate is null;
        if (!_lagUsed || !_lagInit || MathF.Abs(ViewSmoothing.WrapPi(yaw - _lagYaw)) > LagSnap || MathF.Abs(pitch - _lagPitch) > LagSnap)
        {
            _lagYaw = yaw; _lagPitch = pitch; _lagYawRate = _lagPitchRate = 0f; _lagInit = true;
            return (yaw, pitch);
        }
        float dt = Math.Clamp(delta, 0f, 0.1f);
        float yawTarget = _lagYaw + ViewSmoothing.WrapPi(yaw - _lagYaw);
        _lagYaw = SmoothDamp(_lagYaw, yawTarget, ref _lagYawRate, smooth, dt);
        _lagPitch = SmoothDamp(_lagPitch, pitch, ref _lagPitchRate, smooth, dt);
        _lagYaw = ViewSmoothing.WrapPi(_lagYaw);
        return (_lagYaw, _lagPitch);
    }

    /// <summary>A critically damped spring toward <paramref name="target"/> (the closed form Unity's SmoothDamp uses; stable for any frame time).</summary>
    static float SmoothDamp(float current, float target, ref float rate, float smoothTime, float dt)
    {
        float omega = 2f / MathF.Max(1e-4f, smoothTime);
        float x = omega * dt;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        float change = current - target;
        float temp = (rate + omega * change) * dt;
        rate = (rate - omega * temp) * exp;
        return target + (change + temp) * exp;
    }

    /// <summary>
    /// With the lag on: the aim's ray (game space) that the crosshair, the aimed-enemy check and the shots use instead of the lens's: from the camera along the view of this moment
    /// (the input's yaw and pitch, exactly; it also stands in for the feel/controls fresh-aim turn, which assumes a lens on the view). Null with the lag off (the lens's ray, as before).
    /// </summary>
    (Num.Vector3 Origin, Num.Vector3 Direction)? LagAimRay()
    {
        if (!CameraLagActive || _gameSpace is null || _camera is null) return null;
        var inverse = _gameSpace.Transform.AffineInverse();
        var o3 = inverse * _camera.GlobalPosition;
        return (new Num.Vector3(o3.X, o3.Y, o3.Z), Combat.AimDirection(_yaw, Math.Clamp(_pitch, _pitchMin, _pitchMax)));
    }

    /// <summary>Where the crosshair goes (the HUD's 1080-high units from the screen centre): where the aim's ray meets the scene, seen through the lagged lens; zero with the lag off.</summary>
    Vector2 CrosshairOffset(PlayerState me, SimConfig c)
    {
        if (!CameraLagActive || _camera is null || _gameSpace is null) return Vector2.Zero;
        var (point, _, _, _) = CenterRayTarget(me, c);
        var world = _gameSpace.Transform * new Vector3(point.X, point.Y, point.Z);
        if (_camera.IsPositionBehind(world)) return Vector2.Zero;
        var size = GetViewport().GetVisibleRect().Size;
        if (size.Y < 1f) return Vector2.Zero;
        var offset = _camera.UnprojectPosition(world) - size / 2f;
        return offset * (1080f / size.Y);
    }

    readonly System.Collections.Generic.List<double> _lagProbeRay = new(), _lagProbeLens = new();

    /// <summary>
    /// <c>--latency-probe</c>: for every sampled input, the angle between the converged shot's ray and the view (the input's yaw and pitch: what the shot is aimed with) and between the
    /// lens and the view. With the lag the first must stay what it is without it (the aim is not smoothed); the second is the lag itself.
    /// </summary>
    void LagProbeShot(Num.Vector3 rayDirection)
    {
        if (_camera is null || _gameSpace is null || !_cameraPlacedValid) return;   // not before the first placed camera
        var view = Combat.AimDirection(_yaw, Math.Clamp(_pitch, _pitchMin, _pitchMax));
        var lens = _gameSpace.Transform.AffineInverse().Basis * (-_camera.GlobalBasis.Z);
        _lagProbeRay.Add(Mathf.RadToDeg(new Vector3(rayDirection.X, rayDirection.Y, rayDirection.Z).AngleTo(new Vector3(view.X, view.Y, view.Z))));
        _lagProbeLens.Add(Mathf.RadToDeg(lens.AngleTo(new Vector3(view.X, view.Y, view.Z))));
    }

    /// <summary>The probe's line (null without samples).</summary>
    string? LagProbeSummary() => _lagProbeRay.Count == 0 ? null :
        $"LATENCY aim with {CameraLagInfo}: shot ray against the view {LagStats(_lagProbeRay)}; lens against the view {LagStats(_lagProbeLens)} (degrees, over {_lagProbeRay.Count} inputs)";

    static string LagStats(System.Collections.Generic.List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return $"mean {s.Average():F3} median {s[s.Count / 2]:F3} p95 {s[(int)(s.Count * 0.95)]:F3} max {s[^1]:F3}, over 0.01: {s.Count(x => x > 0.01)}";
    }

    /// <summary>For the info text and probes: the lag and how far the lens trails the aim now (degrees).</summary>
    string CameraLagInfo => CameraLagSeconds <= 0f ? "camera lag off" :
        $"camera lag {CameraLagSeconds * 1000f:F0} ms{(CameraLagActive ? $" (lens behind the aim by {Mathf.RadToDeg(ViewSmoothing.WrapPi(_yaw - _lagYaw)):F1} yaw, {Mathf.RadToDeg(Math.Clamp(_pitch, _pitchMin, _pitchMax) - _lagPitch):F1} pitch)" : "")}";
}
