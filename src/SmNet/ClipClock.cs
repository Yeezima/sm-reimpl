namespace SmNet;

/// <summary>
/// A client-side clip clock for one remote player's clip-driven pose (M28 reactions, M29 charge rush and stop, melee attacks).
/// Snapshots arrive at the snapshot rate and the interpolated view holds each record's clip time until the next one, so
/// posing the clip at that value steps. The clock instead runs on at real time between snapshots and is eased onto each new
/// snapshot value, restarting cleanly when the clip changes. Pure client presentation (spec/behavior.md: remote clip-time
/// smoothing, ours); the simulation and the protocol are untouched, and the own player's prediction never uses it.
/// </summary>
public sealed class ClipClock
{
    /// <summary>Seconds the shown time may run ahead of the last snapshot value (about one snapshot interval plus a frame).</summary>
    public const float Ahead = 0.15f;
    /// <summary>A shown time further than this from the snapshot's is replaced rather than eased (seconds).</summary>
    public const float Snap = 0.2f;
    /// <summary>The share of the error to the snapshot value taken up when a new value arrives.</summary>
    public const float Ease = 0.5f;

    long _key;
    float _target;

    /// <summary>True while a clip is being followed.</summary>
    public bool Active { get; private set; }
    /// <summary>The clip seconds to pose (valid while <see cref="Active"/>).</summary>
    public float Time { get; private set; }

    /// <summary>
    /// Advances the clock one frame and returns the time to pose.
    /// </summary>
    /// <param name="key">Identity of the clip (kind, step, phase, direction): a change restarts the clock at the snapshot value.</param>
    /// <param name="target">The clip time the interpolated snapshot record carries (a step function of the snapshot rate).</param>
    /// <param name="delta">Real seconds since the last frame.</param>
    /// <param name="length">The clip's length; the time never passes it.</param>
    public float Step(long key, float target, float delta, float length)
    {
        length = MathF.Max(length, 0f);
        // A restart: nothing followed, another clip, or the same clip's time went back (it began again).
        if (!Active || key != _key || target < _target - 1e-4f)
        {
            Time = target;
        }
        else
        {
            Time += MathF.Max(delta, 0f);
            if (target != _target)
            {
                float error = target - Time;
                Time = MathF.Abs(error) > Snap ? target : Time + error * Ease;
            }
            Time = MathF.Min(Time, target + Ahead);
        }
        Time = Math.Clamp(Time, 0f, length);
        Active = true; _key = key; _target = target;
        return Time;
    }

    /// <summary>The player left the clip: the next <see cref="Step"/> starts afresh.</summary>
    public void Stop() => Active = false;

    /// <summary>A key for <see cref="Step"/> from the clip's kind and up to three small identifying numbers.</summary>
    public static long Key(int kind, int a = 0, int b = 0, int c = 0) => ((long)kind << 48) | ((long)(a & 0xFFFF) << 32) | ((long)(b & 0xFFFF) << 16) | (long)(c & 0xFFFF);
}
