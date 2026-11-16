using System.Text.Json.Serialization;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Progression;

namespace SmSim.Extras.Warfront.Campaign;

/// <summary>One human's running figures for the whole run (W4b). Kills count by class; <see cref="Kills"/> includes the elite and boss subsets (the leaderboard's convention).</summary>
public sealed class WarfrontPlayerLedger
{
    [JsonInclude] public ulong Key { get; internal set; }
    [JsonInclude] public int OrdinaryKills { get; internal set; }
    [JsonInclude] public int EliteKills { get; internal set; }
    [JsonInclude] public int BossKills { get; internal set; }
    [JsonInclude] public int Assists { get; internal set; }
    [JsonInclude] public int Deaths { get; internal set; }
    [JsonInclude] public int Revives { get; internal set; }
    /// <summary>Control points captured in A/B/C battles.</summary>
    [JsonInclude] public int CapturesAbc { get; internal set; }
    /// <summary>Control points captured in D battles.</summary>
    [JsonInclude] public int CapturesD { get; internal set; }
    [JsonInclude] public int Battles { get; internal set; }

    [JsonIgnore] public int Kills => OrdinaryKills + EliteKills + BossKills;
    [JsonIgnore] public int Captures => CapturesAbc + CapturesD;
}

/// <summary>Local equivalent of the leaderboard's per-player record (W15's <c>WarfrontPlayerStats</c>, same shape and field order); the integration swaps in W15's type.</summary>
public sealed record WarfrontLedgerStats(string Name, bool Connected = true, int JoinIndex = 0, int Kills = 0, int EliteKills = 0, int BossKills = 0, int Assists = 0,
    int Deaths = 0, int Revives = 0, int Captures = 0);

/// <summary>
/// Per-player run stats (W4b), keyed by campaign key and saved with the run. Pure and deterministic. Every method takes an optional <c>eventId</c>: a nonzero id is
/// counted once per (counter, key, id), so a shared event reported by several peers does not double-count; 0 means unguarded. The stage flow feeds it
/// (WarfrontKill, the downed rule and revive, battle changes, WarfrontDamaged for assists). A player's figures persist across disconnects and rejoins.
/// </summary>
public sealed class WarfrontStatsLedger
{
    readonly Dictionary<ulong, WarfrontPlayerLedger> _players = new();
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public IReadOnlyCollection<WarfrontPlayerLedger> Players => _players.Values;
    public WarfrontPlayerLedger? Player(ulong key) => _players.GetValueOrDefault(key);

    WarfrontPlayerLedger Get(ulong key)
    {
        if (!_players.TryGetValue(key, out var p)) _players[key] = p = new WarfrontPlayerLedger { Key = key };
        return p;
    }

    bool Fresh(string counter, ulong key, long eventId) => eventId == 0 || _seen.Add(counter + ":" + key + ":" + eventId);

    /// <summary>An enemy of <paramref name="type"/> was killed by (or for) <paramref name="key"/>. Returns false when a repeated event id was ignored.</summary>
    public bool OnKill(ulong key, MusouEnemyType type, long eventId = 0) => OnKill(key, WarfrontXpLedger.Classify(type), eventId);

    public bool OnKill(ulong key, WarfrontKillClass cls, long eventId = 0)
    {
        if (key == 0 || !Fresh("k", key, eventId)) return false;
        var p = Get(key);
        switch (cls)
        {
            case WarfrontKillClass.Boss: p.BossKills++; break;
            case WarfrontKillClass.Elite: p.EliteKills++; break;
            default: p.OrdinaryKills++; break;
        }
        return true;
    }

    public bool OnAssist(ulong key, long eventId = 0) => Bump(key, "a", eventId, p => p.Assists++);
    public bool OnDeath(ulong key, long eventId = 0) => Bump(key, "d", eventId, p => p.Deaths++);
    public bool OnRevive(ulong reviverKey, long eventId = 0) => Bump(reviverKey, "r", eventId, p => p.Revives++);
    /// <summary>A control point captured; <paramref name="dBattle"/> selects the D counter.</summary>
    public bool OnCapture(ulong key, bool dBattle = false, long eventId = 0) => Bump(key, "c", eventId, p => { if (dBattle) p.CapturesD++; else p.CapturesAbc++; });
    /// <summary>The player took part in a battle (pass the battle number as <paramref name="eventId"/> to count it once).</summary>
    public bool OnBattle(ulong key, long eventId = 0) => Bump(key, "b", eventId, p => p.Battles++);

    bool Bump(ulong key, string counter, long eventId, Action<WarfrontPlayerLedger> f)
    {
        if (key == 0 || !Fresh(counter, key, eventId)) return false;
        f(Get(key));
        return true;
    }

    /// <summary>The leaderboard figures of one key (zeros for a key never seen).</summary>
    public WarfrontLedgerStats ToLeaderboard(ulong key, string name, bool connected, int joinIndex)
    {
        var p = Player(key) ?? new WarfrontPlayerLedger { Key = key };
        return new WarfrontLedgerStats(name, connected, joinIndex, p.Kills, p.EliteKills + p.BossKills, p.BossKills, p.Assists, p.Deaths, p.Revives, p.Captures);
    }

    /// <summary>
    /// Every player of the campaign in join order. W15's record has an <c>EliteKills</c> and a <c>BossKills</c>, both subsets of <c>Kills</c> ("Kills include the elite and boss
    /// subsets"); here EliteKills is elite+boss and BossKills the boss subset (assumption: the lead confirms W15's meaning on integration).
    /// </summary>
    public IReadOnlyList<WarfrontLedgerStats> ToLeaderboard(WarfrontCampaign c) =>
        c.JoinOrder.Select((k, i) => ToLeaderboard(k, c.Player(k)?.Name ?? "", c.IsConnected(k), i)).ToList();

    // ---- save ---------------------------------------------------------------------------------------------------------------------------------

    public sealed class State
    {
        public List<WarfrontPlayerLedger> Players { get; set; } = new();
        public List<string> Seen { get; set; } = new();
    }

    /// <summary>Deterministic (sorted) snapshot for run.json.</summary>
    public State ToState() => new()
    {
        Players = _players.Values.OrderBy(p => p.Key).ToList(),
        Seen = _seen.OrderBy(s => s, StringComparer.Ordinal).ToList(),
    };

    /// <summary>Restores a ledger; null (an old save) gives an empty one.</summary>
    public static WarfrontStatsLedger FromState(State? s)
    {
        var l = new WarfrontStatsLedger();
        if (s is null) return l;
        foreach (var p in s.Players) l._players[p.Key] = p;
        foreach (var e in s.Seen) l._seen.Add(e);
        return l;
    }
}
