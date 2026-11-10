namespace SmContent;

/// <summary>
/// Which effect a full effect player drops for a new one (the client's <c>FxPlayer.Play</c>; ours: the live-effect cap is our own, not the original's), engine-free so the order is
/// tested. Among the effects that are not ambient and whose priority is at or below the new one's, the lowest priority goes first, and the oldest only among equal priorities
/// (fix/fx-hud-leaks, polish item 27: the old pick, "lower priority, or older", let an older effect at 0 go before a younger one at -1, the weight pass's puffs). Also the slots of
/// the player's drop and refusal counters by priority.
/// </summary>
public static class FxEviction
{
    /// <summary>True when an effect of <paramref name="priority"/> and <paramref name="age"/> (seconds since its start) is dropped before one of <paramref name="thanPriority"/> and <paramref name="thanAge"/>.</summary>
    public static bool DropsBefore(int priority, double age, int thanPriority, double thanAge) =>
        priority < thanPriority || (priority == thanPriority && age > thanAge);

    /// <summary>
    /// The index in <paramref name="effects"/> of the effect to drop for a new one of <paramref name="priority"/>, or -1 when none may go (all ambient or above it). Of equal
    /// priority and age, the first in the list. <paramref name="read"/> gives an effect's ambient flag, priority and age (a static lambda: nothing is allocated).
    /// </summary>
    public static int Victim<T>(IReadOnlyList<T> effects, int priority, Func<T, (bool Ambient, int Priority, double Age)> read)
    {
        int victim = -1, victimPriority = 0;
        double victimAge = 0.0;
        for (int i = 0; i < effects.Count; i++)
        {
            var (ambient, p, age) = read(effects[i]);
            if (ambient || p > priority) continue;
            if (victim < 0 || DropsBefore(p, age, victimPriority, victimAge)) { victim = i; victimPriority = p; victimAge = age; }
        }
        return victim;
    }

    /// <summary>The lowest priority the counters keep apart (the weight pass's puffs), and the counters' slots: -1 to 3, lower counted as -1 and higher as 3.</summary>
    public const int LowestCounted = -1, Slots = 5;

    /// <summary>The counter slot of <paramref name="priority"/> (slot 0 is priority -1 and below, slot 4 is 3 and above).</summary>
    public static int Slot(int priority) =>
        priority <= LowestCounted ? 0 : priority >= LowestCounted + Slots - 1 ? Slots - 1 : priority - LowestCounted;   // compared first: no overflow at int.MaxValue
}
