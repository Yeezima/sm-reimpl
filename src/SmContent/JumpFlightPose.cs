namespace SmContent;

/// <summary>
/// M45 (the owner's request: "he should appear less like he's rising through the air and more like falling"): how the jump-pack flight clips are posed.
/// <para>
/// Evidence (Verified-local, <c>smtool graph-dump MP_SM_NAV_JP_Sim</c> and <c>smtool graph-dump --jump-flight</c>, 2026-10-28): the graph's flight clips
/// <c>SM_JMP_Zero_&lt;dir&gt;_Aim_&lt;aim&gt;</c> and <c>SM_JMP_ZeroLo_*</c> (3.333 s) are ClipGenerators in user-controlled mode (mode 2) whose time fraction
/// is bound to <c>FLT_Nav_Vertical_Angle_NORMALIZED</c> (the straight-up clips to <c>FLT_Nav_Vertical_Angle_DAMPED</c>): the clip is not played, it is
/// posed by the vertical angle of the motion. There is no separate falling clip: the falling pose is the far end of the same clips. Read along the clip
/// (model space, relative to the pelvis): at 0 the legs trail down and back, extended (rising under thrust); at 0.5 the knees come up, the pelvis
/// lowest (the apex tuck); at 1 one knee forward and up, the other leg reaching down (falling, braced for the landing). The graph blends the low
/// (<c>ZeroLo</c>, weight 9) and high (<c>Zero</c>, weight 18) sets by <c>FLT_Distance_Above_Ground_DAMPED</c>.
/// </para>
/// <para>
/// The game code that computes the two variables is not in the data, so these are **interpretation / ours**: the fraction is
/// 0.5 − elevation / π with elevation = atan2(vertical speed, horizontal speed) (straight up 0, level 0.5, straight down 1), damped toward that
/// target with <see cref="DampSeconds"/>; the low or high set is the nearer of the graph's two weights (switch at <see cref="LowHighSwitch"/> m above
/// the ground, with <see cref="LowHighHysteresis"/> either way), one clip shown at a time rather than the graph's blend.
/// </para>
/// </summary>
public static class JumpFlightPose
{
    // fix/hitboxes (S4 of the review): the rules live in SmSim.FlightPoseRules, shared by the client that draws the flight and the server that hits it.
    /// <summary>The graph's blend weights for the low and high flight sets (metres above the ground; extracted from 'Jump Move Blend').</summary>
    public const float LowWeight = SmSim.FlightPoseRules.LowWeight, HighWeight = SmSim.FlightPoseRules.HighWeight;
    /// <summary>Where the nearer weight changes (the midpoint of the two weights; ours).</summary>
    public const float LowHighSwitch = SmSim.FlightPoseRules.LowHighSwitch;
    /// <summary>Metres either side of <see cref="LowHighSwitch"/> before the set changes (ours, so a marine near the midpoint does not flicker).</summary>
    public const float LowHighHysteresis = SmSim.FlightPoseRules.LowHighHysteresis;
    /// <summary>Time constant (s) of the fraction's damping (ours; the code's damping of the <c>_DAMPED</c> variables is unknown).</summary>
    public const float DampSeconds = SmSim.FlightPoseRules.DampSeconds;

    /// <summary><see cref="SmSim.FlightPoseRules.Target(float, float)"/>: 0 straight up, 0.5 level, 1 straight down.</summary>
    public static float Target(float vertical, float horizontal) => SmSim.FlightPoseRules.Target(vertical, horizontal);

    /// <summary><see cref="SmSim.FlightPoseRules.Damp"/>.</summary>
    public static float Damp(float current, float target, float dt, float tau = DampSeconds) => SmSim.FlightPoseRules.Damp(current, target, dt, tau);

    /// <summary><see cref="SmSim.FlightPoseRules.Low"/>.</summary>
    public static bool Low(float aboveGround, bool wasLow) => SmSim.FlightPoseRules.Low(aboveGround, wasLow);

    /// <summary>The clip time (s) for <paramref name="fraction"/> of a clip <paramref name="duration"/> s long (clamped to the clip).</summary>
    public static float Time(float fraction, float duration) =>
        !(duration > 0f) ? 0f : Math.Clamp(float.IsFinite(fraction) ? fraction : 0.5f, 0f, 1f) * duration;
}

/// <summary>
/// M45: when the jump pack's thrust ends for the sound (the data's <c>action_apex</c> after <c>action_ascending</c>; the effects switch the same way in
/// <c>NetworkView.JumpFx</c>). Without a cut: the tick the vertical speed stops being positive (the apex). With a cut (the own player's predicted
/// <c>JumpCut</c>; others' records do not carry it): at the release, so the thrust stops when Space is let go, and not again at the apex that follows (ours).
/// </summary>
public static class ThrustEndRules
{
    /// <summary>Whether the thrust ends between the previous state (<paramref name="prevVz"/>, <paramref name="prevCut"/>) and this one.</summary>
    public static bool EndsNow(bool jumping, float prevVz, float vz, bool prevCut, bool cut)
    {
        if (!jumping) return false;
        if (cut && !prevCut) return true;          // released while rising: the cut exists only in the ascent
        if (prevCut) return false;                 // ended at the release already
        return prevVz > 0f && vz <= 0f;
    }
}
