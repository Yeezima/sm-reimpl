using SmSim.Extras.Weather;

namespace SmSim.Extras.Warfront.Weather;

/// <summary>
/// The weather forecast of a Warfront run (owner brief 2026-11-21): one preset per regular lane slot (<see cref="Slots"/> = 9, in <c>WarfrontSlots.All</c> order), drawn once at campaign creation from a versioned,
/// curated pool with soft node (depth) weights and stored in the save. The draw is a pure function of the run seed and the pool version: it uses no roll source, no campaign state and no map or cache key, so
/// it cannot change an off-front roll or the generated maps. The HQ and the fortress are never drawn (their forecast is always None: their existing look). Presentation only.
/// </summary>
public static class WarfrontForecast
{
    /// <summary>The curated pool's version a NEW campaign draws from, saved with the forecast (<c>ForecastPool</c>). Raised with any change to a pool, its weights or the opener rule; an older pool stays in <see cref="PoolOf"/> so a draw at that version is reproducible.</summary>
    public const int PoolVersion = 2;
    public const int Slots = 9;

    /// <summary>One candidate: its preset and its soft weight on node 1, 2 and 3 (wasteland, outskirts, urban core). Weights are relative; 0 excludes.</summary>
    public readonly record struct Candidate(WeatherId Id, int Node1, int Node2, int Node3)
    {
        public int Weight(int node) => node switch { 1 => Node1, 2 => Node2, _ => Node3 };
    }

    /// <summary>
    /// Pool 1 (weather W1; ours, estimates for a playtest): the default look stays the most common; Clear is for the open wasteland, Low Fog and Rain gather toward the dense city. Frozen: a save made with pool 1
    /// keeps its stored forecast (the forecast is saved, never redrawn), and <c>Draw(seed, 1)</c> still gives what pool 1 gave.
    /// </summary>
    public static IReadOnlyList<Candidate> PoolV1 { get; } =
    [
        new(WeatherId.None, 3, 2, 2),
        new(WeatherId.Clear, 4, 2, 1),
        new(WeatherId.LowFog, 1, 3, 3),
        new(WeatherId.Rain, 1, 3, 3),
    ];

    /// <summary>
    /// Pool 2 (weather W2; ours, estimates for a playtest): pool 1 plus the Thunderstorm, which gathers toward the dense city (weight 0 on node 1, the open wasteland, so a first deployment never opens in a storm; 2 on
    /// node 2, 3 on node 3), where its darkening and rain read as a street battle. Ash Squall (a brief event on a map that already has the Ashfall storm) and Ember Wind (preview only, the owner has not reviewed it) are
    /// NOT in the pool: both are reachable only through the F6 panel. The HQ and the fortress are outside every pool.
    /// </summary>
    public static IReadOnlyList<Candidate> PoolV2 { get; } =
    [
        new(WeatherId.None, 3, 2, 2),
        new(WeatherId.Clear, 4, 2, 1),
        new(WeatherId.LowFog, 1, 3, 3),
        new(WeatherId.Rain, 1, 3, 3),
        new(WeatherId.Thunderstorm, 0, 2, 3),
    ];

    /// <summary>The pool a new campaign draws from (pool <see cref="PoolVersion"/>).</summary>
    public static IReadOnlyList<Candidate> Pool => PoolV2;

    /// <summary>The pool of a version, or null for one this build does not know (0 is the legacy forecast: the current look everywhere, no pool).</summary>
    public static IReadOnlyList<Candidate>? PoolOf(int version) => version switch { 1 => PoolV1, 2 => PoolV2, _ => null };

    /// <summary>The presets that read clearly at a glance: the opener rule needs one of them on node 1 (a first deployment must not open in fog or rain on every lane).</summary>
    public static bool ClearEnough(WeatherId id) => id is WeatherId.None or WeatherId.Clear;

    /// <summary>The forecast an older save gets: the current look (None) on every slot.</summary>
    public static byte[] Legacy() => new byte[Slots];

    /// <summary>The forecast of a new run (the current pool): one byte (a <see cref="WeatherId"/>) per slot, slot 1 first. Deterministic in <paramref name="runSeed"/>.</summary>
    public static byte[] Draw(ulong runSeed) => Draw(runSeed, PoolVersion);

    /// <summary>The forecast a run would have drawn from pool <paramref name="poolVersion"/> (1 or 2; any other gives the legacy forecast). The pool version is mixed into the hash exactly as before, so pool 1 reproduces W1's draws.</summary>
    public static byte[] Draw(ulong runSeed, int poolVersion)
    {
        if (PoolOf(poolVersion) is not { } pool) return Legacy();
        var result = new byte[Slots];
        var hashes = new ulong[Slots];
        for (int i = 0; i < Slots; i++)
        {
            int node = i % 3 + 1;   // slots 1 to 3 are lane A nodes 1 to 3, then B, then C
            hashes[i] = Mix(runSeed ^ ((ulong)(i + 1) * 0x9E3779B97F4A7C15UL) ^ ((ulong)poolVersion << 56) ^ 0x57EA7B3A11D00DUL);
            long total = 0;
            foreach (var c in pool) total += c.Weight(node);
            long pick = (long)(hashes[i] % (ulong)total);
            foreach (var c in pool)
            {
                int w = c.Weight(node);
                if (pick < w) { result[i] = (byte)c.Id; break; }
                pick -= w;
            }
        }
        // the opener rule: at least one node-1 slot (slots 1, 4, 7) reads clearly; else the node-1 slot with the lowest hash becomes Clear
        int opener = -1;
        for (int i = 0; i < Slots; i += 3)
        {
            if (ClearEnough((WeatherId)result[i])) return result;
            if (opener < 0 || hashes[i] < hashes[opener]) opener = i;
        }
        result[opener] = (byte)WeatherId.Clear;
        return result;
    }

    /// <summary>Whether a stored forecast is usable: nine known ids.</summary>
    public static bool IsValid(IReadOnlyList<byte>? forecast)
    {
        if (forecast is null || forecast.Count != Slots) return false;
        foreach (var b in forecast) if (!WeatherCatalog.IsKnown(b)) return false;
        return true;
    }

    /// <summary>The preset of a lane slot (<c>WarfrontSlot.Number</c>, 1 to 9); None for anything else (the HQ and the fortress).</summary>
    public static WeatherId ForSlot(IReadOnlyList<byte>? forecast, int slotNumber)
        => IsValid(forecast) && slotNumber >= 1 && slotNumber <= Slots ? (WeatherId)forecast![slotNumber - 1] : WeatherId.None;

    /// <summary>
    /// The baseline weather of a Warfront stage: the slot's forecast while a battle is on a lane slot, else None (the ship, travel, the HQ and the fortress keep the default look). A pure function of the saved forecast, the
    /// phase and the map name, so a wipe that returns to the same slot, a reconnect and a restarted stage all read the same preset.
    /// </summary>
    public static WeatherId Baseline(SmSim.Extras.Warfront.Campaign.WarfrontPhase phase, string? mapName, IReadOnlyList<byte>? forecast)
        => phase == SmSim.Extras.Warfront.Campaign.WarfrontPhase.Battle && SmSim.Extras.Warfront.Maps.WarfrontSlots.ByName(mapName) is { } slot ? ForSlot(forecast, slot.Number) : WeatherId.None;

    static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
