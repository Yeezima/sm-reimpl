namespace SmNet;

/// <summary>
/// The camera lag's state machine (ui/hud-fidelity; **temporary, owner: likely to be removed**): the yaw and pitch the follow camera's rig is placed with, following the view through a
/// critically damped spring. It snaps to the view (no lag that frame) when it is off, on its first step, when a step was missed (the death camera, a spectator view, a scope ran
/// instead: the rig did not run last frame), when the player jumped (a respawn or a teleport) and when the view is more than <see cref="SnapRadians"/> away. Engine-free; the client's
/// <c>NetworkView.CameraLag.cs</c> drives it.
/// </summary>
public sealed class CameraLag
{
    /// <summary>A view this far (radians) from the lagged one is jumped to at once.</summary>
    public const float SnapRadians = 1.2f;

    /// <summary>The longest frame step the spring takes (s): a hitch does not fling it.</summary>
    public const float MaxStep = 0.1f;

    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public float YawRate { get; private set; }
    public float PitchRate { get; private set; }

    /// <summary>Whether the last step lagged (false: it returned the view itself).</summary>
    public bool Lagging { get; private set; }

    long _lastFrame = long.MinValue;
    bool _initialised;

    /// <summary>Forgets the state: the next step snaps to the view.</summary>
    public void Reset()
    {
        _initialised = false; Lagging = false; YawRate = PitchRate = 0f; _lastFrame = long.MinValue;
    }

    /// <summary>
    /// One frame: the yaw and pitch for the rig. <paramref name="smoothTime"/> (s) at or below 0, or <paramref name="enabled"/> false, is no lag (and resets). <paramref name="frame"/> is
    /// the frame counter: a step that does not follow the last one by exactly one frame snaps. <paramref name="jumped"/>: the player was moved far in one frame.
    /// </summary>
    public (float Yaw, float Pitch) Step(float viewYaw, float viewPitch, float dt, float smoothTime, bool enabled, long frame, bool jumped = false)
    {
        bool fresh = _initialised && frame == _lastFrame + 1;
        _lastFrame = frame;
        if (!enabled || !(smoothTime > 0f) || !fresh || jumped || !float.IsFinite(viewYaw) || !float.IsFinite(viewPitch)
            || MathF.Abs(ViewSmoothing.WrapPi(viewYaw - Yaw)) > SnapRadians || MathF.Abs(viewPitch - Pitch) > SnapRadians)
        {
            Yaw = viewYaw; Pitch = viewPitch; YawRate = PitchRate = 0f;
            _initialised = enabled && smoothTime > 0f;
            Lagging = false;
            return (viewYaw, viewPitch);
        }
        float step = Math.Clamp(float.IsFinite(dt) ? dt : 0f, 0f, MaxStep);
        float yawTarget = Yaw + ViewSmoothing.WrapPi(viewYaw - Yaw);
        float yr = YawRate, pr = PitchRate;
        Yaw = ViewSmoothing.WrapPi(SmoothDamp(Yaw, yawTarget, ref yr, smoothTime, step));
        Pitch = SmoothDamp(Pitch, viewPitch, ref pr, smoothTime, step);
        YawRate = yr; PitchRate = pr;
        Lagging = true;
        return (Yaw, Pitch);
    }

    /// <summary>A critically damped spring toward <paramref name="target"/> (the closed form Unity's SmoothDamp uses; stable for any step).</summary>
    public static float SmoothDamp(float current, float target, ref float rate, float smoothTime, float dt)
    {
        float omega = 2f / MathF.Max(1e-4f, smoothTime);
        float x = omega * dt;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        float change = current - target;
        float temp = (rate + omega * change) * dt;
        rate = (rate - omega * temp) * exp;
        return target + (change + temp) * exp;
    }
}
