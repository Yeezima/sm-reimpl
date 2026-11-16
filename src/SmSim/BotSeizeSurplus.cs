namespace SmSim;

/// <summary>
/// Which Seize Ground bots are surplus on a point and hunt (owner 2026-11-16: hunting should "clear space for their teammates to safely capture points"; the rule and its
/// numbers are **ours**, estimates). The choice is per bot and deterministic: a bot is surplus only when at least <see cref="Quota"/> living teammates with a LOWER player id
/// stand inside the point's capture zone, so the <see cref="Quota"/> lowest ids always stay and the point is never emptied (three on a point: exactly the highest id leaves).
/// A bot hunts while a threat exists (an enemy outside the spawn area, near a point), for at least <see cref="Latch"/> s so it does not flicker; the latch drops at once when
/// the threat is gone, and the bot goes back to its point.
/// </summary>
public sealed class BotSeizeSurplus
{
    /// <summary>Teammates (lower ids) that stay on a point, and the least length of a hunt (s; ours, estimates).</summary>
    public const int Quota = 2;
    public const double Latch = 8.0;

    double _until = double.NegativeInfinity;
    /// <summary>Hunts started.</summary>
    public int Starts { get; private set; }

    /// <summary>Whether the bot hunts now: <paramref name="lowerIdsOnPoint"/> is the number of living teammates with a lower id inside its point's zone; <paramref name="threat"/> whether an enemy outside the spawn area is near a point.</summary>
    public bool Step(double now, int lowerIdsOnPoint, bool threat)
    {
        if (!threat) { _until = double.NegativeInfinity; return false; }
        if (now < _until) return true;
        if (lowerIdsOnPoint < Quota) return false;
        _until = now + Latch; Starts++;
        return true;
    }

    /// <summary>The bot stops hunting (it died or retreats).</summary>
    public void End() => _until = double.NegativeInfinity;
}
