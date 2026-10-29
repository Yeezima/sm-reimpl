namespace SmNet;

/// <summary>
/// The smoothed server-tick clock remote players and projectiles are drawn at (M40 T2; client presentation, ours).
/// <para>
/// Before: the shown tick was re-derived from the newest snapshot's processing time on every call, so each 20 Hz snapshot moved it
/// by that frame's arrival jitter (a late packet froze remote players, then the next jumped them: +-0.11 m steps at 60 fps).
/// Now the clock advances by the frame delta and follows the raw target only gently: the correction per step is proportional to the
/// error (time constant <see cref="Smoothing"/>) and never more than <see cref="MaxSlew"/> of the step, so the rate stays within
/// +-5 % of real time and the clock never runs backwards. A target further away than <see cref="SnapSeconds"/> (a stall, a server
/// that restarted its ticks, the first snapshot) is taken at once. After a latency spike the clock may not trail the raw estimate by more than
/// <see cref="MaxBehindSeconds"/> (it jumps forward to that distance): the view tick sent for lag compensation depends on it.
/// </para>
/// Engine-free; the simulation and the protocol never use it.
/// </summary>
public sealed class RenderClock
{
    /// <summary>Largest correction as a fraction of the advance in one step (the clock runs at 0.95 to 1.05 of real time).</summary>
    public const double MaxSlew = 0.05;
    /// <summary>Time constant of the gentle pull toward the target, seconds.</summary>
    public const double Smoothing = 0.5;
    /// <summary>The clock never trails the raw estimate by more than this many seconds (about 3 ticks at 30 Hz; the server accepts a view tick down to 7 ticks before the snapshot acknowledged, and the raw estimate is already 3 ticks of interpolation delay behind it).</summary>
    public const double MaxBehindSeconds = 0.1;
    /// <summary>The clock never runs ahead of the raw estimate by more than this many seconds (it waits instead).</summary>
    public const double MaxAheadSeconds = 0.2;
    /// <summary>An error beyond this many seconds is not slewed but taken at once.</summary>
    public const double SnapSeconds = 0.5;

    /// <summary>The shown tick (fractional), meaningful once <see cref="Started"/>.</summary>
    public double Value { get; private set; }
    public bool Started { get; private set; }

    /// <summary>Forgets the position; the next update takes its target.</summary>
    public void Reset() { Started = false; Value = 0; }

    /// <summary>
    /// Advances by <paramref name="deltaSeconds"/> of real time toward <paramref name="targetTick"/> and returns the new value.
    /// </summary>
    /// <param name="targetTick">Where the raw estimate says the clock should be now (ticks).</param>
    /// <param name="deltaSeconds">Time since the previous update (negative or non-finite counts as 0).</param>
    /// <param name="tickRate">Ticks per second.</param>
    public double Update(double targetTick, double deltaSeconds, double tickRate)
    {
        if (!double.IsFinite(targetTick)) return Value;
        if (!Started || Math.Abs(targetTick - Value) > SnapSeconds * tickRate)
        {
            Value = targetTick; Started = true;
            return Value;
        }
        double previous = Value;
        double advance = double.IsFinite(deltaSeconds) && deltaSeconds > 0 ? deltaSeconds * tickRate : 0;
        double error = targetTick - (Value + advance);
        double gain = deltaSeconds > 0 ? 1.0 - Math.Exp(-deltaSeconds / Smoothing) : 0;
        double correction = Math.Clamp(error * gain, -MaxSlew * advance, MaxSlew * advance);
        Value += advance + correction;
        // The divergence cap (review S1): behind the raw estimate by more than MaxBehindSeconds the clock jumps forward to that distance
        // (lag compensation: the view tick the client sends must stay within the server's bound of the snapshot it acknowledged, which a
        // 5 % slew cannot keep after a latency spike); ahead of it by more than MaxAheadSeconds it stands still until the estimate catches
        // up (never backwards).
        Value = Math.Max(Value, targetTick - MaxBehindSeconds * tickRate);
        if (Value > targetTick + MaxAheadSeconds * tickRate) Value = Math.Max(previous, targetTick + MaxAheadSeconds * tickRate);
        return Value;
    }
}
