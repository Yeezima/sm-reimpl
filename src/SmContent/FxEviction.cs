namespace SmContent;

/// <summary>
/// The effect player's priority tiers, high to low (fx-cap-priority, owner 2026-11-15: "audit priority"): what matters to the player goes last when the live-effect cap is full.
/// A tier is an <c>int</c>; the numbers between keep working (the 0, 1 and 2 of the older sources are the three middle tiers).
/// </summary>
public static class FxPriority
{
    /// <summary>Distant crowd fire (0) and the weight pass's small puffs (-1): the first to go.</summary>
    public const int Crowd = 0, Puff = -1;
    /// <summary>Other players' and allies' muzzles and tracers, the held effects of a nearby fight, Musou allies' hits.</summary>
    public const int Ally = 1;
    /// <summary>Bosses, elites, abilities and specials: the Weirdboy, the Dreadnought, the drop pod, blasts.</summary>
    public const int Special = 2;
    /// <summary>Hits on the player and by the player (Musou: the own blood and hits), blasts near the camera, power-weapon flashes.</summary>
    public const int Hit = 3;
    /// <summary>The player's own weapon effects (muzzle, tracer, impact).</summary>
    public const int Own = 4;
    /// <summary>A blast within this many metres of the camera is a hit tier effect, not a special.</summary>
    public const float NearBlastMetres = 15f;
}

/// <summary>
/// Which effect a full effect player drops for a new one (the client's <c>FxPlayer.Play</c>; ours: the live-effect cap is our own, not the original's), engine-free so the order is
/// tested. Among the effects that are not ambient and whose priority is at or below the new one's, the lowest priority goes first, then the farthest from the camera (fx-cap-priority, owner 2026-11-15), and the oldest only among equal priorities and distances
/// (fix/fx-hud-leaks, polish item 27: the old pick, "lower priority, or older", let an older effect at 0 go before a younger one at -1, the weight pass's puffs). Also the slots of
/// the player's drop and refusal counters by priority.
/// </summary>
public static class FxEviction
{
    /// <summary>Metres of distance from the camera that count as one step in the pick (effects within one step of each other are told apart by age: a burst's sprays share a place).</summary>
    public const float DistanceStep = 2f;

    /// <summary>The distance step of an effect <paramref name="metres"/> from the camera (NaN or negative: 0).</summary>
    public static int Bucket(float metres) => metres > 0f && float.IsFinite(metres) ? (int)MathF.Min(metres / DistanceStep, 1_000_000f) : 0;

    /// <summary>
    /// True when an effect of <paramref name="priority"/>, <paramref name="bucket"/> (<see cref="Bucket"/>) and <paramref name="age"/> (seconds since its start) is dropped before one of
    /// <paramref name="thanPriority"/>, <paramref name="thanBucket"/> and <paramref name="thanAge"/>: the lowest priority first, within one priority the farthest from the camera, within one
    /// distance step the oldest (fx-cap-priority, owner 2026-11-15).
    /// </summary>
    public static bool DropsBefore(int priority, int bucket, double age, int thanPriority, int thanBucket, double thanAge) =>
        priority < thanPriority || (priority == thanPriority && (bucket > thanBucket || (bucket == thanBucket && age > thanAge)));

    /// <summary>The pick without distances (every effect at the camera): the lowest priority, then the oldest.</summary>
    public static bool DropsBefore(int priority, double age, int thanPriority, double thanAge) => DropsBefore(priority, 0, age, thanPriority, 0, thanAge);

    /// <summary>
    /// The index in <paramref name="effects"/> of the effect to drop for a new one of <paramref name="priority"/>, or -1 when none may go (all ambient or above it). Of equal
    /// priority and age, the first in the list. <paramref name="read"/> gives an effect's ambient flag, priority and age (a static lambda: nothing is allocated).
    /// </summary>
    public static int Victim<T>(IReadOnlyList<T> effects, int priority, Func<T, (bool Ambient, int Priority, double Age)> read) =>
        Victim(effects, priority, 0f, e => { var (a, p, age) = read(e); return (a, p, 0f, age); });

    /// <summary>
    /// As above with each effect's distance from the camera: among the droppable effects the lowest priority goes, then the farthest (by <see cref="DistanceStep"/>), then the oldest. When the
    /// pick has the new effect's own priority and is nearer than the new effect (<paramref name="newMetres"/>), the new one is the one that would go first: -1, it is refused and
    /// nothing nearer is dropped for it.
    /// </summary>
    public static int Victim<T>(IReadOnlyList<T> effects, int priority, float newMetres, Func<T, (bool Ambient, int Priority, float Metres, double Age)> read)
    {
        int victim = -1, victimPriority = 0, victimBucket = 0;
        double victimAge = 0.0;
        for (int i = 0; i < effects.Count; i++)
        {
            var (ambient, p, metres, age) = read(effects[i]);
            if (ambient || p > priority) continue;
            int b = Bucket(metres);
            if (victim < 0 || DropsBefore(p, b, age, victimPriority, victimBucket, victimAge)) { victim = i; victimPriority = p; victimBucket = b; victimAge = age; }
        }
        if (victim >= 0 && victimPriority == priority && newMetres > 0f && Bucket(newMetres) > victimBucket) return -1;   // the new one is the farthest of its priority: it, not a nearer one, goes
        return victim;
    }

    /// <summary>The lowest priority the counters keep apart (the weight pass's puffs), and the counters' slots: -1 to 4, lower counted as -1 and higher as 4.</summary>
    public const int LowestCounted = -1, Slots = 6;

    /// <summary>The counter slot of <paramref name="priority"/> (slot 0 is priority -1 and below, slot 5 is 4 and above).</summary>
    public static int Slot(int priority) =>
        priority <= LowestCounted ? 0 : priority >= LowestCounted + Slots - 1 ? Slots - 1 : priority - LowestCounted;   // compared first: no overflow at int.MaxValue
}
