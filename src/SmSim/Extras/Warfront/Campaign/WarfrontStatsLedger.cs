using System.Text.Json.Serialization;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Progression;
using SmSim.Extras.Warfront.Results;

namespace SmSim.Extras.Warfront.Campaign;

/// <summary>One human's running figures for the whole run (W4b): the human's OWN body only (a retainer's kills belong to its service record). <see cref="Kills"/> is all three classes.</summary>
public sealed class WarfrontPlayerLedger
{
    [JsonInclude] public ulong Key { get; internal set; }
    [JsonInclude] public int OrdinaryKills { get; internal set; }
    [JsonInclude] public int EliteKills { get; internal set; }
    [JsonInclude] public int BossKills { get; internal set; }
    [JsonInclude] public int Assists { get; internal set; }
    [JsonInclude] public int Deaths { get; internal set; }
    [JsonInclude] public int Revives { get; internal set; }
    /// <summary>Control points captured in A/B/C battles (HQ stages count here).</summary>
    [JsonInclude] public int CapturesAbc { get; internal set; }
    /// <summary>Control points captured in D battles.</summary>
    [JsonInclude] public int CapturesD { get; internal set; }
    [JsonInclude] public int Battles { get; internal set; }

    [JsonIgnore] public int Kills => OrdinaryKills + EliteKills + BossKills;
    [JsonIgnore] public int Captures => CapturesAbc + CapturesD;
}

/// <summary>
/// Per-player run stats (W4b), keyed by campaign key and saved with the run. Pure and deterministic. It holds each human's OWN figures only (kills by that human's retainers go to
/// the W9 service records, so TOTAL K does not show them twice). A nonzero <c>eventId</c> is counted once per (counter, key, id), so a shared event reported by several peers does
/// not double-count; 0 is unguarded (ordinary kills: the pool reports each once). Build ids with <see cref="EventId"/>. Persists across disconnects and rejoins.
/// </summary>
public sealed class WarfrontStatsLedger
{
    const byte CKill = 1, CAssist = 2, CDeath = 3, CRevive = 4, CCapture = 5, CBattle = 6;

    readonly Dictionary<ulong, WarfrontPlayerLedger> _players = new();
    readonly HashSet<(byte C, ulong K, long I)> _seen = new();

    public IReadOnlyCollection<WarfrontPlayerLedger> Players => _players.Values;
    public WarfrontPlayerLedger? Player(ulong key) => _players.GetValueOrDefault(key);

    /// <summary>
    /// A guard id unique across the whole run (the guard is saved across battles and pool ids are reused): <c>deployment &lt;&lt; 40 | (a &amp; 0xFFFFF) &lt;&lt; 20 | (b &amp; 0xFFFFF)</c>.
    /// Uses: elite or boss kill (deployment, enemy id, tick of death mod 2^20); death and revive (deployment, player slot, tick); capture (deployment, objective index); battle (deployment).
    /// </summary>
    public static long EventId(int deployment, int a, int b = 0) => ((long)deployment << 40) | ((long)(a & 0xFFFFF) << 20) | (long)(b & 0xFFFFF);

    WarfrontPlayerLedger Get(ulong key)
    {
        if (!_players.TryGetValue(key, out var p)) _players[key] = p = new WarfrontPlayerLedger { Key = key };
        return p;
    }

    bool Fresh(byte counter, ulong key, long eventId) => eventId == 0 || _seen.Add((counter, key, eventId));

    /// <summary>The human's own kill of an enemy of <paramref name="type"/> (never a retainer's). Returns false when a repeated event id was ignored.</summary>
    public bool OnKill(ulong key, MusouEnemyType type, long eventId = 0) => OnKill(key, WarfrontXpLedger.Classify(type), eventId);

    public bool OnKill(ulong key, WarfrontKillClass cls, long eventId = 0)
    {
        if (key == 0 || !Fresh(CKill, key, eventId)) return false;
        var p = Get(key);
        switch (cls)
        {
            case WarfrontKillClass.Boss: p.BossKills++; break;
            case WarfrontKillClass.Elite: p.EliteKills++; break;
            default: p.OrdinaryKills++; break;
        }
        return true;
    }

    public bool OnAssist(ulong key, long eventId = 0) => Bump(key, CAssist, eventId, p => p.Assists++);
    public bool OnDeath(ulong key, long eventId = 0) => Bump(key, CDeath, eventId, p => p.Deaths++);
    public bool OnRevive(ulong reviverKey, long eventId = 0) => Bump(reviverKey, CRevive, eventId, p => p.Revives++);
    /// <summary>A control point captured; <paramref name="dBattle"/> selects the D counter.</summary>
    public bool OnCapture(ulong key, bool dBattle = false, long eventId = 0) => Bump(key, CCapture, eventId, p => { if (dBattle) p.CapturesD++; else p.CapturesAbc++; });
    /// <summary>The player took part in a battle (pass <c>EventId(deployment, 0)</c> to count it once).</summary>
    public bool OnBattle(ulong key, long eventId = 0) => Bump(key, CBattle, eventId, p => p.Battles++);

    bool Bump(ulong key, byte counter, long eventId, Action<WarfrontPlayerLedger> f)
    {
        if (key == 0 || !Fresh(counter, key, eventId)) return false;
        f(Get(key));
        return true;
    }

    /// <summary>
    /// The leaderboard record of one key (zeros for a key never seen; the name is left empty and the run-record builder fills Name, Connected and JoinIndex). Elite and boss are disjoint
    /// subsets of Kills, as W15 scores them.
    /// </summary>
    public WarfrontPlayerStats ToPlayerStats(ulong key)
    {
        var p = Player(key);
        return p is null ? new WarfrontPlayerStats("") : new WarfrontPlayerStats("", Kills: p.Kills, EliteKills: p.EliteKills, BossKills: p.BossKills, Assists: p.Assists,
            Deaths: p.Deaths, Revives: p.Revives, Captures: p.Captures);
    }

    // ---- save ---------------------------------------------------------------------------------------------------------------------------------

    public sealed class Guard
    {
        public byte C { get; set; }
        public ulong K { get; set; }
        public long I { get; set; }
    }

    public sealed class State
    {
        public List<WarfrontPlayerLedger>? Players { get; set; } = new();
        public List<Guard>? Seen { get; set; } = new();
    }

    /// <summary>Deterministic (sorted) snapshot for run.json.</summary>
    public State ToState() => new()
    {
        Players = _players.Values.OrderBy(p => p.Key).ToList(),
        Seen = _seen.OrderBy(g => g.C).ThenBy(g => g.K).ThenBy(g => g.I).Select(g => new Guard { C = g.C, K = g.K, I = g.I }).ToList(),
    };

    /// <summary>Restores a ledger; null (an old save) gives an empty one. Null lists and a Key 0 entry are tolerated (skipped).</summary>
    public static WarfrontStatsLedger FromState(State? s)
    {
        var l = new WarfrontStatsLedger();
        if (s is null) return l;
        foreach (var p in s.Players ?? new()) if (p is not null && p.Key != 0) l._players[p.Key] = p;
        foreach (var g in s.Seen ?? new()) if (g is not null) l._seen.Add((g.C, g.K, g.I));
        return l;
    }
}
