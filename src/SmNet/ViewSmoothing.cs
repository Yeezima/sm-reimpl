using System.Numerics;

namespace SmNet;

/// <summary>
/// The pure rules behind the own marine's smooth presentation and the movement poses (M36; client presentation, ours): which yaw or pitch to
/// draw, where between two predicted ticks to draw the position, when a marine counts as sprinting, and which of the graph's directions a
/// velocity is nearest to. Engine-free so they can be tested (the Godot client calls them); the simulation and the protocol never use them.
/// </summary>
public static class ViewSmoothing
{
    /// <summary>The state's angle counts as the last input's when it is within this many radians of it (the wire's quantization is far finer).</summary>
    public const float FollowTolerance = 0.01f;
    /// <summary>A predicted step longer than this (metres in one tick: a respawn, a teleport) is not interpolated across; 3 m is 90 m/s.</summary>
    public const float TeleportDistance = 3f;
    /// <summary>Seconds in which a reconciliation correction fades out of the drawn position (exponential time constant).</summary>
    public const float CorrectionSeconds = 0.1f;

    /// <summary>The angle wrapped into [-pi, pi).</summary>
    public static float WrapPi(float angle) => angle - MathF.Tau * MathF.Floor((angle + MathF.PI) / MathF.Tau);

    /// <summary>
    /// The angle to draw: the view's while the simulation's follows the last sampled input (so it is the angle the next tick takes), else the
    /// state's (the simulation holds another heading: a charge's locked one). Works for yaw (the difference wraps) and pitch.
    /// </summary>
    /// <param name="stateAngle">The predicted state's angle.</param>
    /// <param name="sampledAngle">The view angle the last input sample took.</param>
    /// <param name="viewAngle">The view angle now.</param>
    public static float FollowAngle(float stateAngle, float sampledAngle, float viewAngle) =>
        float.IsFinite(stateAngle) && float.IsFinite(sampledAngle) && float.IsFinite(viewAngle) && MathF.Abs(WrapPi(stateAngle - sampledAngle)) <= FollowTolerance
            ? viewAngle : stateAngle;

    /// <summary>
    /// How far through the current tick the clock is: 0 at the tick that was just run, 1 when the next is due (clamped; a client that
    /// is late or paused shows the newest tick).
    /// </summary>
    public static float TickFraction(double now, double nextTickAt, double tickSeconds)
    {
        if (!(tickSeconds > 0)) return 1f;
        double f = 1.0 - (nextTickAt - now) / tickSeconds;
        return double.IsFinite(f) ? (float)Math.Clamp(f, 0.0, 1.0) : 1f;
    }

    /// <summary>
    /// The drawn position between the last two predicted ticks by <paramref name="fraction"/> (<see cref="TickFraction"/>): the previous
    /// tick's at 0, the newest at 1. A step longer than <see cref="TeleportDistance"/> is not crossed.
    /// </summary>
    public static Vector3 Interpolate(Vector3 previous, Vector3 current, float fraction)
    {
        if (!float.IsFinite(previous.X + previous.Y + previous.Z) || Vector3.Distance(previous, current) > TeleportDistance) return current;
        return Vector3.Lerp(previous, current, Math.Clamp(fraction, 0f, 1f));
    }

    /// <summary>
    /// Whether a marine moving like this is in the sprint, with hysteresis: forward (along the facing) faster than run + enter × (sprint - run)
    /// enters it, and it lasts until the speed is below run + leave × (sprint - run). <paramref name="allowed"/> false (not grounded, rolling,
    /// charging, reacting: speeds above the run speed that are not the sprint) ends it.
    /// </summary>
    public static bool Sprinting(bool was, bool allowed, float speed, float forward, float runSpeed, float sprintSpeed, float enterFraction, float leaveFraction)
    {
        float span = MathF.Max(0f, sprintSpeed - runSpeed);
        if (!allowed || span <= 0f || !(forward > 0f)) return false;
        float limit = runSpeed + (was ? leaveFraction : enterFraction) * span;
        return speed > limit;
    }

    /// <summary>
    /// The index of the direction nearest to <paramref name="angleDegrees"/> (degrees, the difference wrapped: -180 and +180 are the same
    /// direction), keeping <paramref name="previous"/> unless another is closer by more than <paramref name="holdDegrees"/> (hysteresis
    /// against flicker at the borders). -1 for an empty list.
    /// </summary>
    public static int NearestDirection(IReadOnlyList<float> anglesDegrees, float angleDegrees, int previous, float holdDegrees)
    {
        int best = -1;
        float bestDiff = float.MaxValue;
        for (int i = 0; i < anglesDegrees.Count; i++)
        {
            float diff = AngleDifference(anglesDegrees[i], angleDegrees);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        if (best >= 0 && previous >= 0 && previous < anglesDegrees.Count && previous != best
            && AngleDifference(anglesDegrees[previous], angleDegrees) <= bestDiff + holdDegrees) best = previous;
        return best;
    }

    /// <summary>The absolute difference of two angles in degrees, 0 to 180.</summary>
    public static float AngleDifference(float a, float b) => MathF.Abs(((a - b) % 360f + 540f) % 360f - 180f);
}

/// <summary>
/// A reconciliation correction faded out of the drawn position: when the prediction is corrected the drawn position keeps going from where
/// it was (the offset starts at minus the correction) and the offset decays with <see cref="ViewSmoothing.CorrectionSeconds"/>. Corrections
/// larger than <see cref="ViewSmoothing.TeleportDistance"/> are not eased.
/// </summary>
public sealed class CorrectionOffset
{
    /// <summary>The offset to add to the drawn position.</summary>
    public Vector3 Offset { get; private set; }

    /// <summary>Notes a correction: the predicted position moved by <paramref name="correction"/> (new minus old).</summary>
    public void Add(Vector3 correction)
    {
        if (!float.IsFinite(correction.X + correction.Y + correction.Z) || correction.Length() > ViewSmoothing.TeleportDistance) { Offset = Vector3.Zero; return; }
        Offset -= correction;
    }

    /// <summary>Advances the fade by <paramref name="seconds"/> and returns the offset.</summary>
    public Vector3 Step(float seconds)
    {
        if (seconds > 0f) Offset *= MathF.Exp(-seconds / ViewSmoothing.CorrectionSeconds);
        if (Offset.LengthSquared() < 1e-8f) Offset = Vector3.Zero;
        return Offset;
    }

    public void Reset() => Offset = Vector3.Zero;
}
