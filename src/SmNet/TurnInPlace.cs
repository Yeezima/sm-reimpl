namespace SmNet;

/// <summary>Which way a marine turns in place: the sign of the yaw change (the simulation's yaw grows counter-clockwise seen from above, a left turn).</summary>
public enum TurnSide : sbyte { Right = -1, None = 0, Left = 1 }

/// <summary>
/// One turn-in-place clip as the rules see it (M40-T5; the client builds it from <c>SmContent.TurnClip</c>): the root rotation the clip makes over time, in radians
/// with the sign of its direction (positive for the left turn), and how long the turn runs before its <c>BRANCH_Turn_End</c> window ends it.
/// </summary>
/// <param name="EndSeconds">Seconds into the clip at which the turn ends (the start of the <c>BRANCH_Turn_End</c> window: 0.92 of the clip for the rifle).</param>
/// <param name="TotalSeconds">The clip's length.</param>
/// <param name="YawRadians">The root rotation, evenly spaced over <paramref name="YawSeconds"/>, the first sample 0.</param>
public sealed record TurnProfile(float EndSeconds, float TotalSeconds, IReadOnlyList<float> YawRadians, float YawSeconds)
{
    /// <summary>The root rotation after <paramref name="time"/> seconds (radians; linear between the samples, held after the last).</summary>
    public float YawAt(float time)
    {
        if (YawRadians.Count == 0 || YawSeconds <= 0f) return 0f;
        if (YawRadians.Count == 1 || time <= 0f) return YawRadians[0];
        if (time >= YawSeconds) return YawRadians[^1];
        float position = time / YawSeconds * (YawRadians.Count - 1);
        int i = (int)position;
        return YawRadians[i] + (YawRadians[Math.Min(i + 1, YawRadians.Count - 1)] - YawRadians[i]) * (position - i);
    }

    /// <summary>The whole turn: the root rotation at <see cref="EndSeconds"/>.</summary>
    public float TurnRadians => YawAt(EndSeconds);
}

/// <summary>
/// The body heading of a standing marine, apart from the heading it aims along (M40-T5; client presentation, ours; engine-free so the rules can be tested; the
/// simulation and the protocol never use it). The simulation turns a marine with its view at once; the original keeps the lower body planted while the upper body
/// follows the aim (the graphs' aim-offset idle, <c>FLT_Aim_Horizontal_Deg</c> from -90 to +90) and plays <c>SM_NAV_AimTurn_Left</c> / <c>_Right</c> when the aim is
/// more than 90 degrees (75 for the pistol and the heavy bolter) off the body; the clip's root motion turns the body (135 degrees over 0.833 s) until its
/// <c>BRANCH_Turn_End</c> window. The rules:
/// <list type="bullet">
/// <item>Standing (<c>canTwist</c>): the body heading holds. When the aim is more than the threshold off it a turn starts toward the aim; the body then follows the
/// clip's root rotation until the turn ends, and the turn is evaluated again (a long turn plays several).</item>
/// <item>The twist is never more than <c>maxTwist</c> (ours): a view that outruns the turn drags the body at <c>followRate</c>.</item>
/// <item>Anything else (moving, an action clip, aim mode): the body follows the aim at <c>followRate</c> radians per second at most (ours: the audit asked for
/// at most 15 degrees of body yaw per frame), and a turn is dropped.</item>
/// </list>
/// </summary>
public sealed class BodyYaw
{
    /// <summary>The body heading, radians (the simulation's yaw convention).</summary>
    public float Yaw { get; private set; }
    /// <summary>False until the first <see cref="Snap"/> or <see cref="Step"/>.</summary>
    public bool Valid { get; private set; }
    /// <summary>The turn being made (the body rotates with its clip), or none.</summary>
    public TurnSide Side { get; private set; }
    /// <summary>Seconds into the turn clip while a turn is made or fades out.</summary>
    public float TurnTime { get; private set; }
    /// <summary>The side of the turn clip the layer still shows (a turn that ended fades out), or none.</summary>
    public TurnSide LayerSide { get; private set; }
    /// <summary>The turn clip layer's weight (0 to 1), faded in and out over the blend the caller passes to <see cref="Step"/>.</summary>
    public float LayerWeight { get; private set; }
    /// <summary>Turns started so far (for the probes and the tests).</summary>
    public int TurnsStarted { get; private set; }

    /// <summary>The body is put at <paramref name="yaw"/> with no turn (a respawn, a pose that has no turn).</summary>
    public void Snap(float yaw)
    {
        Yaw = ViewSmoothing.WrapPi(yaw); Valid = true; Side = LayerSide = TurnSide.None; TurnTime = 0f; LayerWeight = 0f;
    }

    /// <summary>The aim against the body: positive when the aim is to the left (counter-clockwise) of the body, radians in [-pi, pi).</summary>
    public float Twist(float aimYaw) => ViewSmoothing.WrapPi(aimYaw - Yaw);

    /// <summary>
    /// One frame of <paramref name="dt"/> seconds. <paramref name="canTwist"/>: the marine stands in a posture that keeps the body planted. <paramref name="enterTwist"/>:
    /// the aim-to-body angle (radians) beyond which a turn starts. <paramref name="left"/> / <paramref name="right"/>: the turn clips (null: no turn, the body then
    /// follows). <paramref name="blend"/>: the turn layer's fade in and out, seconds. <paramref name="maxTwist"/> and <paramref name="followRate"/> as in the class
    /// comment. A frame longer than <see cref="MaxFrameSeconds"/> or an aim that is not finite snaps the body to the aim.
    /// </summary>
    public void Step(float aimYaw, float dt, bool canTwist, float enterTwist, TurnProfile? left, TurnProfile? right, float blend, float maxTwist, float followRate)
    {
        if (!float.IsFinite(aimYaw)) return;
        if (!Valid || dt > MaxFrameSeconds) { Snap(aimYaw); return; }
        if (!(dt > 0f)) return;
        float step = followRate * dt;
        float twist = Twist(aimYaw);
        if (!canTwist || left is null || right is null)
        {
            Side = TurnSide.None;
            Yaw = ViewSmoothing.WrapPi(Yaw + Math.Clamp(twist, -step, step));
            FadeLayer(dt, blend, active: false);
            return;
        }
        float turned = 0f;
        if (Side == TurnSide.None && MathF.Abs(twist) > enterTwist) StartTurn(twist > 0f ? TurnSide.Left : TurnSide.Right);
        if (Side != TurnSide.None)
        {
            var profile = Side == TurnSide.Left ? left : right;
            float from = TurnTime, to = from + dt;
            // The body follows the clip's root rotation until the turn ends; a frame that crosses the end counts the part before it.
            float before = profile.YawAt(MathF.Min(from, profile.EndSeconds)), after = profile.YawAt(MathF.Min(to, profile.EndSeconds));
            turned = MathF.Abs(after - before);
            Yaw = ViewSmoothing.WrapPi(Yaw + (after - before));
            TurnTime = to;
            if (to >= profile.EndSeconds)
            {
                Side = TurnSide.None;
                // The aim may still be beyond the threshold: the next turn starts at once (the graph's BRANCH_Turn_End to the same turn state).
                twist = Twist(aimYaw);
                if (MathF.Abs(twist) > enterTwist) { StartTurn(twist > 0f ? TurnSide.Left : TurnSide.Right); }
            }
        }
        // A view that outruns the turn drags the body: the twist is never more than maxTwist.
        twist = Twist(aimYaw);
        if (MathF.Abs(twist) > maxTwist)
        {
            float excess = twist - MathF.Sign(twist) * maxTwist, room = MathF.Max(0f, step - turned);   // the clip's own rotation counts toward the follow rate
            Yaw = ViewSmoothing.WrapPi(Yaw + Math.Clamp(excess, -room, room));
        }
        FadeLayer(dt, blend, active: Side != TurnSide.None);
    }

    /// <summary>No frame longer than this is stepped through (a stall: the body snaps to the aim instead of turning for a second).</summary>
    public const float MaxFrameSeconds = 0.5f;

    void StartTurn(TurnSide side)
    {
        Side = side; LayerSide = side; TurnTime = 0f; TurnsStarted++;
        // A turn that follows another continues the layer's weight; the clip restarts.
    }

    void FadeLayer(float dt, float blend, bool active)
    {
        if (LayerSide == TurnSide.None) { LayerWeight = 0f; return; }
        float rate = blend > 1e-3f ? dt / blend : 1f;
        if (active) LayerWeight = MathF.Min(1f, LayerWeight + rate);
        else
        {
            LayerWeight = MathF.Max(0f, LayerWeight - rate);
            TurnTime += dt;   // the clip runs on while it fades out
            if (LayerWeight <= 0f) LayerSide = TurnSide.None;
        }
    }

    /// <summary>Builds a profile from a turn clip's data: the root rotation in degrees (sign = direction), its duration, the clip's length and the fraction at which the turn ends.</summary>
    public static TurnProfile Profile(IReadOnlyList<float> rootYawDegrees, float rootSeconds, float clipSeconds, float endFraction)
    {
        var radians = new float[rootYawDegrees.Count];
        for (int i = 0; i < radians.Length; i++) radians[i] = rootYawDegrees[i] * MathF.PI / 180f;
        return new TurnProfile(Math.Clamp(endFraction, 0.05f, 1f) * clipSeconds, clipSeconds, radians, rootSeconds);
    }
}
