namespace SmContent;

/// <summary>
/// anim/jump-pack-blend (owner, 2026-11-14: "assault marine / raptor flying movement feels janky, lots of snapping and odd movements"): client-only rules that keep the
/// jump-pack flight's clip choice steady. The server's hit volumes and <see cref="SmSim.FlightPoseRules.Candidates"/> stay stateless; these only decide when the
/// DRAWN flight clip changes. Every value is ours (the graph's own selection code is not in the data). Engine-free.
/// </summary>
public static class JumpAirBlend
{
    /// <summary>The shortest time a drawn flight direction / aim stays before another may replace it (s; ours): a steering stick or a mouse sweep crossing a threshold flipped the clip every few frames.</summary>
    public const float MinKeyHold = 0.22f;
    /// <summary>The neutral (U) direction is left only above this fraction of the threshold's speed and entered only below the inverse (ours).</summary>
    public const float NeutralBand = 0.3f;
    /// <summary>A new dominant direction must beat the shown one's speed by this factor (ours).</summary>
    public const float DirectionMargin = 1.3f;
    /// <summary>Radians past the aim threshold before the aim key leaves level, and back inside it before it returns (ours).</summary>
    public const float AimHysteresis = 0.1f;
    /// <summary>How far ahead of ground contact the landing clip begins to fade in (s; ours: the shortest entry blend, so the pose has arrived at the touchdown).</summary>
    public const float LandingLead = 0.19f;
    /// <summary>A remote player's jump flag that drops for less than this (s; ours) is a dropped snapshot, not a landing.</summary>
    public const float RemoteDropDebounce = 0.07f;

    /// <summary>The running choice of one player's flight clip keys.</summary>
    public sealed class KeyState
    {
        public char Dir = 'U', Aim = 'F';
        public double ChangedAt = double.NegativeInfinity;
        public bool Valid;
        public void Reset() { Valid = false; ChangedAt = double.NegativeInfinity; }
    }

    /// <summary>
    /// The stable (direction, aim) keys for a marine whose velocity is (<paramref name="forward"/>, <paramref name="right"/>) m/s relative to its facing and whose pitch is
    /// <paramref name="pitch"/> radians at connected time <paramref name="now"/>. The first call of a flight (or after <see cref="KeyState.Reset"/>) takes the plain rule.
    /// </summary>
    public static (char Dir, char Aim) Choose(KeyState st, float forward, float right, float pitch, double now,
        float neutralSpeed = SmSim.FlightPoseRules.NeutralSpeed, float aimThreshold = SmSim.FlightPoseRules.AimThreshold)
    {
        float af = MathF.Abs(forward), ar = MathF.Abs(right), top = MathF.Max(af, ar);
        char rawDir = top < neutralSpeed ? 'U' : af >= ar ? (forward > 0f ? 'F' : 'B') : (right > 0f ? 'R' : 'L');
        char rawAim = pitch > aimThreshold ? 'U' : pitch < -aimThreshold ? 'D' : 'F';
        if (!st.Valid) { st.Dir = rawDir; st.Aim = rawAim; st.ChangedAt = now; st.Valid = true; return (st.Dir, st.Aim); }

        // Direction, with a band round the neutral speed and a margin between the four.
        char dir = st.Dir;
        if (st.Dir == 'U') { if (top >= neutralSpeed * (1f + NeutralBand)) dir = rawDir; }
        else
        {
            float shown = st.Dir switch { 'F' => forward, 'B' => -forward, 'R' => right, _ => -right };   // speed along the shown direction (negative: the other way)
            if (top < neutralSpeed * (1f - NeutralBand)) dir = 'U';
            else if (rawDir != st.Dir && rawDir != 'U' && (shown <= 0f || top >= MathF.Max(shown, 0f) * DirectionMargin)) dir = rawDir;
        }
        // Aim, with hysteresis round the threshold.
        char aim = st.Aim;
        if (st.Aim == 'U') { if (pitch < aimThreshold - AimHysteresis) aim = rawAim == 'D' && pitch < -aimThreshold ? 'D' : 'F'; }
        else if (st.Aim == 'D') { if (pitch > -aimThreshold + AimHysteresis) aim = rawAim == 'U' && pitch > aimThreshold ? 'U' : 'F'; }
        else aim = rawAim;   // level: leaves at the plain threshold (the band is on the way back)

        if ((dir != st.Dir || aim != st.Aim) && now - st.ChangedAt >= MinKeyHold) { st.Dir = dir; st.Aim = aim; st.ChangedAt = now; }
        return (st.Dir, st.Aim);
    }

    /// <summary>
    /// Seconds until the feet reach the ground <paramref name="aboveGround"/> m below, falling at <paramref name="vertical"/> m/s (negative: down) under
    /// <paramref name="gravity"/> m/s² (positive): the positive root of h = v t + g t² / 2. Infinity when not falling, the ground is unknown or the numbers are not finite.
    /// </summary>
    public static float TimeToContact(float aboveGround, float vertical, float gravity)
    {
        if (!float.IsFinite(aboveGround) || !float.IsFinite(vertical) || !float.IsFinite(gravity) || vertical >= 0f) return float.PositiveInfinity;
        float v = -vertical, h = MathF.Max(0f, aboveGround), g = MathF.Max(0f, gravity);
        if (g < 1e-3f) return h / v;
        return (-v + MathF.Sqrt(v * v + 2f * g * h)) / g;
    }

    /// <summary>Whether the landing clip should begin fading in now: falling (not a pound's dive) and the contact within <see cref="LandingLead"/>.</summary>
    public static bool LandingImminent(float aboveGround, float vertical, float gravity) => TimeToContact(aboveGround, vertical, gravity) <= LandingLead;

    /// <summary>
    /// A remote player's raw jump view held through a short drop: <paramref name="lastAirAt"/> is when the flight was last seen; a flight that vanishes is kept for
    /// <see cref="RemoteDropDebounce"/> s. True: keep showing the air.
    /// </summary>
    public static bool HoldsAir(double now, double lastAirAt) => now - lastAirAt < RemoteDropDebounce;
}
