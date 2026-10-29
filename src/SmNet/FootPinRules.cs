namespace SmNet;

/// <summary>
/// Engine-free decisions of the foot pins and the run start (M40-T5; client presentation only, so ours throughout): when a pinned foot is dropped, when the body has
/// jumped, how much of the aim the spine twists past the aim-offset idle's range, and what an armed run start does each frame. The client calls these; the tests pin them.
/// </summary>
public static class FootPinRules
{
    /// <summary>A pinned ankle farther than this beyond the leg's full length from the hip is dropped (m): the pin cannot be reached, so holding it would stretch the leg (aim-walk, a push, a teleport).</summary>
    public const float ReachMargin = 0.03f;

    /// <summary>The drawn body moving farther than this in one frame is a respawn, a round restart or another teleport, not a step (m): the pins are dropped.</summary>
    public const float TeleportDistance = 1f;

    /// <summary>True when the ankle cannot reach <paramref name="hipToAnkle"/> metres away with a leg of <paramref name="legLength"/> (thigh plus calf) within <see cref="ReachMargin"/>.</summary>
    public static bool OutOfReach(float hipToAnkle, float legLength) => !(hipToAnkle <= legLength + ReachMargin);

    /// <summary>True when the body moved <paramref name="dx"/>, <paramref name="dy"/>, <paramref name="dz"/> metres in one frame by more than <see cref="TeleportDistance"/> (also for non-finite input).</summary>
    public static bool Teleported(float dx, float dy, float dz) => !(dx * dx + dy * dy + dz * dz <= TeleportDistance * TeleportDistance);

    /// <summary>
    /// The spine twist (radians, positive: the aim is to the left of the body) the upper body adds to the aim-offset idle. The idle's clips reach <paramref name="rangeRadians"/> either way (the
    /// graph clamps the angle there), so a marine whose body lags the aim by more keeps the end pose and the spine twists the rest, up to <paramref name="capRadians"/>. The families
    /// with a procedural twist (<paramref name="proceduralTwist"/>: the pistol and the heavy bolter) twist the whole angle.
    /// </summary>
    public static float SpineTwist(float twist, bool proceduralTwist, float rangeRadians, float capRadians)
    {
        if (!float.IsFinite(twist)) return 0f;
        float capped = System.Math.Clamp(twist, -capRadians, capRadians);
        if (proceduralTwist) return capped;
        return capped - System.Math.Clamp(capped, -rangeRadians, rangeRadians);
    }
}

/// <summary>What an armed or playing run start does this frame.</summary>
public enum RunStartVerdict : byte
{
    /// <summary>Keep waiting with the idle pose (the speed has not reached the clip's yet).</summary>
    Hold,
    /// <summary>Drop the start (and the pins armed for it): a stop, a walk, a sprint, a turn away or a posture change.</summary>
    Reset,
    /// <summary>Begin the clip.</summary>
    Play,
}

/// <summary>The run start's arm / cancel / begin decision, engine-free (M40-T5; thresholds ours, the clip's entry speed from the graph).</summary>
public static class RunStartRules
{
    /// <summary>A start needs the speed to reach the clip's threshold within this many seconds of leaving standing.</summary>
    public const double ArmSeconds = 0.3;
    /// <summary>A start is played for a run within this many degrees of the facing.</summary>
    public const float ForwardCone = 45f;
    /// <summary>A marine waiting for the clip's speed is still accelerating when its speed rises faster than this, m/s squared.</summary>
    public const float MinAcceleration = 15f;
    /// <summary>The average acceleration is judged after this long, seconds.</summary>
    public const double MinWait = 0.08;

    /// <summary>Whether a marine that just left standing at <paramref name="speed"/> arms a start.</summary>
    public static bool ShouldArm(bool wasStanding, bool standingNow, float speed, float runThreshold) => wasStanding && !standingNow && speed > runThreshold;

    /// <summary>The decision for an armed start that has not begun its clip.</summary>
    public static RunStartVerdict Armed(float speed, double elapsed, float acceleration, bool postureChanged, float sprintAbove, float headingDegrees, float enterSpeed, float runThreshold)
    {
        if (elapsed > ArmSeconds || speed < runThreshold || postureChanged) return RunStartVerdict.Reset;
        if (speed > sprintAbove || headingDegrees > ForwardCone) return RunStartVerdict.Reset;
        if (speed < enterSpeed)
            return acceleration < MinAcceleration && elapsed > MinWait ? RunStartVerdict.Reset : RunStartVerdict.Hold;
        return RunStartVerdict.Play;
    }

    /// <summary>Whether a playing start is over early: the body stopped or turned away, the sprint began or the posture changed.</summary>
    public static bool Aborted(float speed, float headingDegrees, float sprintAbove, bool postureChanged, float runThreshold) =>
        speed < runThreshold || headingDegrees > ForwardCone * 2f || speed > sprintAbove || postureChanged;
}
