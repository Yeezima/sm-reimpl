using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Faction;
using SmSim.Extras.Warfront.Progression;

namespace SmSim.Extras.Warfront.Campaign;

/// <summary>A save written by a newer build (its version is above <see cref="WarfrontCampaign.SaveVersion"/>). Not an <see cref="InvalidDataException"/> (that type is sealed) and not an IOException (a locked file): a loader catches it first, never treats the file as damaged and never touches it.</summary>
public sealed class WarfrontSaveNewerException : NotSupportedException
{
    public int FoundVersion { get; }
    public WarfrontSaveNewerException(int found) : base("warfront save version " + found + " is newer than this build reads (" + WarfrontCampaign.SaveVersion + ")") => FoundVersion = found;
}

public enum WarfrontPhase { Lobby, Ship, Travel, Battle, Won, Lost }

public enum WarfrontTargetKind { Node, Hq, Fortress }

/// <summary>How a battle ended. FinaleLost (Chaos Warfront C1, for C5's finale): the duel relay against the Chaos Lord ran out of challengers; the campaign is lost (in place of the fortress-wipe rule).</summary>
public enum WarfrontOutcome { Win, Wipe, FinaleLost }

/// <summary>Where a deployment goes. For <see cref="WarfrontTargetKind.Node"/> the lane's current node is fought; for the fortress <see cref="Lane"/> names the cleared lane it is entered through (the lane that drops back on a wipe); the HQ has no lane (-1).</summary>
public readonly record struct WarfrontTarget(WarfrontTargetKind Kind, int Lane)
{
    public static WarfrontTarget Node(int lane) => new(WarfrontTargetKind.Node, lane);
    public static WarfrontTarget Hq => new(WarfrontTargetKind.Hq, -1);
    public static WarfrontTarget Fortress(int lane) => new(WarfrontTargetKind.Fortress, lane);
}

/// <summary>One roll against one other lane after a deployment. <see cref="HeldAtFloor"/>: lost, but the lane was never advanced and sat at node 1.</summary>
public sealed record WarfrontRollRecord(int Deployment, int Lane, bool Lost, bool HeldAtFloor, int Before, int After);

/// <summary>One finished deployment. <see cref="Before"/>, <see cref="After"/> and <see cref="HeldAtFloor"/> describe the played lane (HQ: lane -1, both 0); HeldAtFloor = a wipe the never-advanced node-1 floor absorbed.</summary>
public sealed record WarfrontHistoryEntry(int Deployment, WarfrontTarget Target, WarfrontOutcome Outcome, int Before = 0, int After = 0, bool HeldAtFloor = false);

/// <summary>Source of the end-of-deployment lane rolls. <see cref="Lost"/> true = the lane loses a node. Tests swap in <see cref="ForcedRolls"/>.</summary>
public interface IWarfrontRollSource
{
    bool Lost(ulong runSeed, int deployment, int lane);
}

/// <summary>Plan section 3: 50/50, deterministic from (run seed, deployment number, lane).</summary>
public sealed class HashedRolls : IWarfrontRollSource
{
    public static readonly HashedRolls Instance = new();

    public bool Lost(ulong runSeed, int deployment, int lane)
    {
        ulong x = runSeed ^ 0x9E3779B97F4A7C15UL;
        x = Mix(x + (ulong)deployment * 0xBF58476D1CE4E5B9UL);
        x = Mix(x + (ulong)(lane + 1) * 0x94D049BB133111EBUL);
        return ((x >> 33) & 1UL) == 1UL;
    }

    static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

/// <summary>Test seam: answers the rolls from a fixed sequence, in call order; running dry throws (a test that rolls more than it planned is wrong).</summary>
public sealed class ForcedRolls : IWarfrontRollSource
{
    readonly Queue<bool> _q;
    public ForcedRolls(params bool[] lost) => _q = new Queue<bool>(lost);
    public int Remaining => _q.Count;
    public bool Lost(ulong runSeed, int deployment, int lane) =>
        _q.Count > 0 ? _q.Dequeue() : throw new InvalidOperationException("ForcedRolls ran out");
}

public sealed class WarfrontRetainerRecord
{
    public string Id { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>Per-player in-run state, keyed by the campaign key. Nothing here is granted twice: <see cref="JoinGrantApplied"/> is one-shot and picks are counted per level.</summary>
public sealed class WarfrontPlayerState
{
    [JsonInclude] public ulong Key { get; internal set; }
    /// <summary>Display name for the leaderboard (disconnected players are listed too). The campaign key must never appear in an export.</summary>
    [JsonInclude] public string Name { get; internal set; } = "";
    [JsonInclude] public long Xp { get; internal set; }
    [JsonInclude] public int Level { get; internal set; } = 1;
    [JsonInclude] public int PicksTaken { get; internal set; }
    [JsonInclude] public List<int> Cards { get; internal set; } = new();
    [JsonInclude] public List<WarfrontRetainerRecord> Squad { get; internal set; } = new();
    [JsonInclude] public bool JoinGrantApplied { get; internal set; }
    /// <summary>W12: the number of completed deployments when this key first joined (0: a founder who joined before the first battle).</summary>
    [JsonInclude] public int JoinedAtDeployment { get; internal set; }
    /// <summary>W12: the key has confirmed its class through the customizer (or the 90 s gate confirmed it). True for a founder and for every key of a save older than version 3 (the default; a missing field keeps it).</summary>
    [JsonInclude] public bool ClassConfirmed { get; internal set; } = true;

    /// <summary>Level-ups earned and not yet picked (one pick per level above 1).</summary>
    public int PendingPicks => Math.Max(0, Level - 1 - PicksTaken);
}

public enum WarfrontJoinKind { New, Returning }

/// <summary>
/// Warfront's campaign state machine (plan section 3 and 4): pure and deterministic, no World and no networking. Per lane <c>Front</c> 0..4 and <c>Advanced</c>,
/// the mandatory-HQ flag, the deployment counter, the phase, the join order (never reordered) and each player's in-run state.
/// </summary>
public sealed class WarfrontCampaign
{
    /// <summary>Local save-format version (W1's WarfrontRules.Version is separate).</summary>
    public const int SaveVersion = 7;   // 7: the run's Faction (Chaos Warfront C1; older saves load as Orks; 6 is left to the retainer rules, T0, and renumbered at integration); 5: the weather forecast (weather W1; older saves load with the legacy forecast: the current look on every slot); 4: co-op fields (W12); 3: PlaySeconds (W5a; older saves load with 0)
    /// <summary>The first format (no stats ledger); still loads, with an empty ledger. Version 2 (no co-op fields) loads too, every key with <c>ClassConfirmed</c> true.</summary>
    public const int OldestLoadableVersion = 1;
    public const int Lanes = 3;
    /// <summary>Same cap as Musou's co-op (a test holds them equal).</summary>
    public const int MaxHumans = TfwCoop.MaxHumans;

    readonly int[] _front = { 1, 1, 1 };
    readonly bool[] _advanced = new bool[Lanes];
    readonly List<ulong> _joinOrder = new();
    readonly Dictionary<ulong, WarfrontPlayerState> _players = new();
    readonly HashSet<ulong> _connected = new();
    readonly List<WarfrontRollRecord> _rolls = new();
    readonly List<WarfrontHistoryEntry> _history = new();

    public WarfrontCampaign(ulong runSeed, IWarfrontRollSource? rolls = null, WarfrontFaction faction = WarfrontFaction.Orks)
    {
        if (!WarfrontFactionPolicy.IsKnown((int)faction)) throw new ArgumentOutOfRangeException(nameof(faction));
        RunSeed = runSeed;
        Faction = faction;
        RollSource = rolls ?? HashedRolls.Instance;
        _forecast = Weather.WarfrontForecast.Draw(runSeed);
        ForecastPool = Weather.WarfrontForecast.PoolVersion;
    }

    byte[] _forecast;

    /// <summary>
    /// The weather forecast (weather W1): one <c>WeatherId</c> byte per lane slot, slot 1 first, drawn at creation from the run seed alone (<see cref="Weather.WarfrontForecast"/>) and saved. Presentation only; a legacy save
    /// (version 4 or older) holds the current look (None) on every slot. Independent of the off-front rolls and of the maps.
    /// </summary>
    public IReadOnlyList<byte> Forecast => _forecast;

    /// <summary>The version of the pool the forecast was drawn from (0: a legacy forecast).</summary>
    public int ForecastPool { get; private set; }

    public ulong RunSeed { get; }
    /// <summary>The enemy army of this run (Chaos Warfront C1): chosen once when the run is built (<c>WarfrontRunFiles.BuildNewRun</c>), saved, never changed; a save older than version 7 loads as Orks.</summary>
    public WarfrontFaction Faction { get; }
    /// <summary>Per-player run stats (W4b), saved in run.json.</summary>
    public WarfrontStatsLedger Stats { get; private set; } = new();
    public IWarfrontRollSource RollSource { get; set; }
    public WarfrontPhase Phase { get; private set; } = WarfrontPhase.Lobby;
    public bool HqMandatory { get; private set; }
    /// <summary>Completed deployments. The one in progress is <c>Deployments + 1</c>.</summary>
    public int Deployments { get; private set; }
    public WarfrontTarget? CurrentTarget { get; private set; }

    public int Front(int lane) => _front[lane];
    public bool Advanced(int lane) => _advanced[lane];
    public IReadOnlyList<ulong> JoinOrder => _joinOrder;
    public IReadOnlyList<WarfrontRollRecord> Rolls => _rolls;
    public IReadOnlyList<WarfrontHistoryEntry> History => _history;
    public IReadOnlyCollection<WarfrontPlayerState> Players => _players.Values;
    public WarfrontPlayerState? Player(ulong key) => _players.GetValueOrDefault(key);
    public bool IsConnected(ulong key) => _connected.Contains(key);
    /// <summary>W12: the run's first deployment has begun (one is finished, or one is in Travel or Battle): a first-time key that joins from now on must confirm its class and take its catch-up cards before the squad deploys.</summary>
    public bool FirstDeploymentBegun => Deployments > 0 || Phase is WarfrontPhase.Travel or WarfrontPhase.Battle;
    public bool IsOver => Phase is WarfrontPhase.Won or WarfrontPhase.Lost;

    /// <summary>Seconds of the run with someone connected (ours: "time with someone connected"): added while the phase is Ship, Travel or Battle and at least one human is connected. Saved; older saves load with 0.</summary>
    public double PlaySeconds { get; private set; }

    /// <summary>Adds <paramref name="seconds"/> of play when the run is not over and a human is connected; returns whether it counted.</summary>
    public bool AddPlaySeconds(double seconds)
    {
        if (seconds <= 0 || !double.IsFinite(seconds) || _connected.Count == 0 || Phase is not (WarfrontPhase.Ship or WarfrontPhase.Travel or WarfrontPhase.Battle)) return false;
        PlaySeconds += seconds;
        return true;
    }

    /// <summary>Lobby to the ship. Does nothing in any other phase.</summary>
    public void Embark()
    {
        if (Phase == WarfrontPhase.Lobby) Phase = WarfrontPhase.Ship;
    }

    // ---- players and leadership -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A player connects. A first-time key is appended to the join order (once, ever) and gets state (no XP grant, level 1: see <see cref="ApplyJoinGrant(ulong)"/>); a returning
    /// key gets exactly its saved state back. The cap is <see cref="MaxHumans"/> CONNECTED humans, for new and returning keys alike; every key ever joined stays in the run
    /// (the leaderboard lists disconnected players). Returns false when the cap refuses.
    /// </summary>
    public bool Join(ulong key, string name, out WarfrontJoinKind kind)
    {
        bool known = _players.TryGetValue(key, out var existing);
        kind = known ? WarfrontJoinKind.Returning : WarfrontJoinKind.New;
        if (_connected.Contains(key)) return true;
        if (_connected.Count >= MaxHumans) return false;
        if (known)
        {
            if (!string.IsNullOrEmpty(name)) existing!.Name = name;
        }
        else
        {
            _players[key] = new WarfrontPlayerState { Key = key, Name = name ?? "", JoinedAtDeployment = Deployments, ClassConfirmed = !FirstDeploymentBegun };
            _joinOrder.Add(key);
        }
        _connected.Add(key);
        return true;
    }

    public bool Join(ulong key, out WarfrontJoinKind kind) => Join(key, "", out kind);

    /// <summary>
    /// Formerly the one-shot median-XP catch-up grant. Dropped (owner 2026-11-22: a brand-new player starts at level 1, a returning one brings his own progress file): it only sets
    /// <see cref="WarfrontPlayerState.JoinGrantApplied"/> (kept in the schema) and always returns false (no XP changes). A second call, or an unknown key, is a no-op.
    /// </summary>
    public bool ApplyJoinGrant(ulong key) => ApplyJoinGrant(key, null);

    /// <summary><see cref="ApplyJoinGrant(ulong)"/>; <paramref name="ignore"/> is kept for the callers and no longer read.</summary>
    public bool ApplyJoinGrant(ulong key, ICollection<ulong>? ignore)
    {
        if (!_players.TryGetValue(key, out var p) || p.JoinGrantApplied) return false;
        p.JoinGrantApplied = true;
        return false;
    }

    /// <summary>
    /// Applies a player's persistent progression (<see cref="WarfrontProgressFile"/>, sanitized here again so a caller cannot skip it) to his in-run state: XP and the level derived from it, picks taken, the cards and the
    /// retainers beyond the first (<see cref="WarfrontPlayerState.Squad"/>, ids <c>p1</c>, <c>p2</c>, ...). Which copy to apply (<see cref="WarfrontProgressFile.Better"/>) is the caller's call; the host applies it at the Ship
    /// phase, or queues it to the next ship stop, always before the class-confirm and deploy gates. False for an unknown key.
    /// </summary>
    public bool ApplyProgress(ulong key, WarfrontProgressFile state)
    {
        if (!_players.TryGetValue(key, out var p)) return false;
        var s = WarfrontProgressFile.Sanitize(state);
        p.Xp = s.Xp;
        p.Level = LevelForXp(s.Xp);
        p.PicksTaken = s.PicksTaken;
        p.Cards = new List<int>(s.Cards);
        p.Squad = s.ExtraRetainers.Select((r, i) => new WarfrontRetainerRecord { Id = "p" + (i + 1), ClassName = r.ClassName, Name = r.Name }).ToList();
        return true;
    }

    /// <summary>The player's state as a progress record (without key and reset time), for the host's mirror and the wire.</summary>
    public WarfrontProgressFile? ProgressOf(ulong key) => !_players.TryGetValue(key, out var p) ? null : WarfrontProgressFile.Sanitize(new WarfrontProgressFile
    {
        Xp = p.Xp, PicksTaken = p.PicksTaken, Cards = new List<int>(p.Cards),
        ExtraRetainers = p.Squad.Select(r => new WarfrontExtraRetainer { ClassName = r.ClassName, Name = r.Name }).ToList(),
    });

    public void Leave(ulong key) => _connected.Remove(key);

    /// <summary>Earliest-joined connected player; null when nobody is connected.</summary>
    public ulong? Leader
    {
        get
        {
            foreach (var k in _joinOrder) if (_connected.Contains(k)) return k;
            return null;
        }
    }

    static long MedianXp(IEnumerable<WarfrontPlayerState> others)
    {
        var xs = others.Select(o => o.Xp).OrderBy(x => x).ToArray();
        int n = xs.Length;
        return n % 2 == 1 ? xs[n / 2] : (xs[n / 2 - 1] + xs[n / 2]) / 2;
    }

    /// <summary>Total XP to reach <paramref name="level"/>: each step L to L+1 costs 1500 + 500*(L-1).</summary>
    public static long XpForLevel(int level)
    {
        long total = 0;
        for (int l = 1; l < level; l++) total += 1500 + 500L * (l - 1);
        return total;
    }

    public static int LevelForXp(long xp)
    {
        int l = 1;
        while (xp >= XpForLevel(l + 1)) l++;
        return l;
    }

    public bool AddXp(ulong key, long xp)
    {
        if (!_players.TryGetValue(key, out var p)) return false;
        p.Xp += xp;
        p.Level = LevelForXp(p.Xp);
        return true;
    }

    /// <summary>Takes one pending pick with the chosen card. False when nothing is pending (so a repeated pick is never granted twice).</summary>
    public bool TryTakePick(ulong key, int card)
    {
        if (!_players.TryGetValue(key, out var p) || p.PendingPicks <= 0) return false;
        p.PicksTaken++;
        p.Cards.Add(card);
        return true;
    }

    // ---- deployment -------------------------------------------------------------------------------------------------------------------------

    /// <summary>What the leader may deploy to from the ship: only HQ defense when mandatory; else each lane at node 1-3 and the fortress through each cleared lane.</summary>
    public IReadOnlyList<WarfrontTarget> AvailableTargets()
    {
        var list = new List<WarfrontTarget>();
        if (Phase != WarfrontPhase.Ship) return list;
        if (HqMandatory) { list.Add(WarfrontTarget.Hq); return list; }
        for (int l = 0; l < Lanes; l++) if (_front[l] is >= 1 and <= 3) list.Add(WarfrontTarget.Node(l));
        for (int l = 0; l < Lanes; l++) if (_front[l] == 4) list.Add(WarfrontTarget.Fortress(l));
        return list;
    }

    /// <summary>Leader-only deploy from the ship. On success the phase is Travel.</summary>
    public bool TryDeploy(ulong key, WarfrontTarget target, out string? error)
    {
        error = null;
        if (Phase != WarfrontPhase.Ship) { error = "not on the ship"; return false; }
        if (Leader != key) { error = "only the leader deploys"; return false; }
        if (!AvailableTargets().Contains(target)) { error = "target not available"; return false; }
        CurrentTarget = target;
        Phase = WarfrontPhase.Travel;
        return true;
    }

    public void BeginBattle()
    {
        if (Phase == WarfrontPhase.Travel) Phase = WarfrontPhase.Battle;
    }

    /// <summary>The Warboss died in the fortress battle: the campaign is won.</summary>
    public void WarbossKilled()
    {
        if (Phase == WarfrontPhase.Battle && CurrentTarget?.Kind == WarfrontTargetKind.Fortress) Resolve(WarfrontOutcome.Win);
    }

    /// <summary>The battle ended: win (objective secured / HQ held / Warboss dead) or wipe. Applies lane changes, the floor, rolls and the breakthrough rule.</summary>
    public void Resolve(WarfrontOutcome outcome)
    {
        if (Phase != WarfrontPhase.Battle || CurrentTarget is not { } t) return;
        int dep = Deployments + 1;
        int playedBefore = t.Lane >= 0 ? _front[t.Lane] : 0;
        bool heldAtFloor = false;
        int historyAt = _history.Count;
        _history.Add(new WarfrontHistoryEntry(dep, t, outcome));
        Deployments = dep;
        CurrentTarget = null;

        switch (t.Kind)
        {
            case WarfrontTargetKind.Hq:
                if (outcome == WarfrontOutcome.Win)
                {
                    for (int l = 0; l < Lanes; l++) _front[l] = 2; // Advanced is kept
                    HqMandatory = false;
                    Phase = WarfrontPhase.Ship;
                }
                else Phase = WarfrontPhase.Lost;
                return;

            case WarfrontTargetKind.Fortress:
                if (outcome == WarfrontOutcome.Win) { Phase = WarfrontPhase.Won; return; }
                if (outcome == WarfrontOutcome.FinaleLost) { Phase = WarfrontPhase.Lost; return; }   // Chaos Warfront C5: the duel relay ran out of challengers
                _front[t.Lane] = 3; // the fortress locks until a lane is cleared again
                break;

            default:
                if (outcome == WarfrontOutcome.Win)
                {
                    _front[t.Lane] = Math.Min(4, _front[t.Lane] + 1);
                    _advanced[t.Lane] = true;
                }
                else heldAtFloor = !DropLane(t.Lane);
                break;
        }
        if (t.Lane >= 0) _history[historyAt] = _history[historyAt] with { Before = playedBefore, After = _front[t.Lane], HeldAtFloor = heldAtFloor };

        for (int l = 0; l < Lanes; l++)
        {
            if (l == t.Lane) continue;
            bool lost = RollSource.Lost(RunSeed, dep, l);
            int before = _front[l];
            bool floor = false;
            if (lost) floor = !DropLane(l);
            _rolls.Add(new WarfrontRollRecord(dep, l, lost, floor, before, _front[l]));
        }

        for (int l = 0; l < Lanes; l++) if (_advanced[l] && _front[l] == 0) HqMandatory = true;
        Phase = WarfrontPhase.Ship;
    }

    /// <summary>Lowers a lane by one unless the floor holds it; returns whether it dropped.</summary>
    bool DropLane(int lane)
    {
        if (_front[lane] == 1 && !_advanced[lane]) return false;
        _front[lane] = Math.Max(0, _front[lane] - 1);
        return true;
    }

    /// <summary>
    /// After a server restart: nobody is connected. A Battle is replayed from the ship (nothing rolled or granted for it). A Travel save keeps its phase and target: each
    /// deployment runs in a new stage process that loads the save in Travel. W5: call <see cref="BeginBattle"/> and then save, so a crash mid-battle is seen as Battle.
    /// </summary>
    public void ResumeAfterRestart()
    {
        _connected.Clear();
        if (Phase == WarfrontPhase.Battle) { Phase = WarfrontPhase.Ship; CurrentTarget = null; }
    }

    // ---- snapshot, hash ---------------------------------------------------------------------------------------------------------------------

    internal sealed class Snapshot
    {
        public int Version { get; set; } = SaveVersion;
        public ulong RunSeed { get; set; }
        public WarfrontPhase Phase { get; set; }
        public int[] Fronts { get; set; } = Array.Empty<int>();
        public bool[] Advanced { get; set; } = Array.Empty<bool>();
        public bool HqMandatory { get; set; }
        public int Deployments { get; set; }
        public double PlaySeconds { get; set; }
        public WarfrontTarget? CurrentTarget { get; set; }
        public List<ulong> JoinOrder { get; set; } = new();
        public List<WarfrontPlayerState> Players { get; set; } = new();
        public List<WarfrontRollRecord> Rolls { get; set; } = new();
        public List<WarfrontHistoryEntry> History { get; set; } = new();
        public WarfrontStatsLedger.State? Stats { get; set; }
        /// <summary>Version 5: the weather forecast (one preset id per lane slot) and the pool it came from.</summary>
        public List<int>? Forecast { get; set; }
        public int ForecastPool { get; set; }
        /// <summary>Version 7: the run's army (Chaos Warfront C1); absent in older saves (Orks).</summary>
        public WarfrontFaction? Faction { get; set; }
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        WriteIndented = false,
        IncludeFields = false,
    };

    internal Snapshot ToSnapshot() => new()
    {
        RunSeed = RunSeed, Phase = Phase, Fronts = (int[])_front.Clone(), Advanced = (bool[])_advanced.Clone(), HqMandatory = HqMandatory,
        Deployments = Deployments, PlaySeconds = PlaySeconds, CurrentTarget = CurrentTarget, JoinOrder = new(_joinOrder),
        Players = _joinOrder.Select(k => _players[k]).ToList(), Rolls = new(_rolls), History = new(_history), Stats = Stats.ToState(),
        Forecast = _forecast.Select(b => (int)b).ToList(), ForecastPool = ForecastPool, Faction = Faction,
    };

    internal static WarfrontCampaign FromSnapshot(Snapshot s, IWarfrontRollSource? rolls)
    {
        if (s.Version > SaveVersion) throw new WarfrontSaveNewerException(s.Version);
        if (s.Version < OldestLoadableVersion) throw new InvalidDataException("unsupported warfront save version " + s.Version);
        if (s.Fronts.Length != Lanes || s.Advanced.Length != Lanes) throw new InvalidDataException("bad lane arrays");
        // Chaos Warfront C1: a version 7 save names its army; an older one is an Orks run. An unknown army is a damaged file.
        var faction = s.Version >= 7 && s.Faction is { } sf ? sf : WarfrontFaction.Orks;
        if (!WarfrontFactionPolicy.IsKnown((int)faction)) throw new InvalidDataException("unknown warfront faction " + (int)faction);
        var c = new WarfrontCampaign(s.RunSeed, rolls, faction) { Phase = s.Phase, HqMandatory = s.HqMandatory, Deployments = s.Deployments, CurrentTarget = s.CurrentTarget };
        for (int l = 0; l < Lanes; l++) { c._front[l] = s.Fronts[l]; c._advanced[l] = s.Advanced[l]; }
        foreach (var k in s.JoinOrder) c._joinOrder.Add(k);
        foreach (var p in s.Players)
        {
            if (s.Version < 4) p.ClassConfirmed = true;   // saves before version 4 know no gate: nobody is asked again
            c._players[p.Key] = p;
        }
        c._rolls.AddRange(s.Rolls);
        c._history.AddRange(s.History);
        c.Stats = WarfrontStatsLedger.FromState(s.Stats);
        // weather W1: a version 5 save keeps its stored forecast; an older (or unreadable) one gets the legacy forecast, the current look on every slot
        var stored = s.Version >= 5 && s.Forecast is { Count: Weather.WarfrontForecast.Slots } f && f.All(v => v >= 0 && v <= 255) ? f.Select(v => (byte)v).ToArray() : null;
        if (stored is not null && Weather.WarfrontForecast.IsValid(stored)) { c._forecast = stored; c.ForecastPool = s.ForecastPool; }
        else { c._forecast = Weather.WarfrontForecast.Legacy(); c.ForecastPool = 0; }
        c.PlaySeconds = s.Version >= 3 && double.IsFinite(s.PlaySeconds) && s.PlaySeconds > 0 ? s.PlaySeconds : 0;
        return c;
    }

    /// <summary>The save JSON. Connection state is not part of it (a restart has nobody connected).</summary>
    public string ToJson() => JsonSerializer.Serialize(ToSnapshot(), Json);

    /// <summary>SHA-256 (hex) of the canonical state JSON; equal states give equal hashes.</summary>
    public string StateHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())));
}
