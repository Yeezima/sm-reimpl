namespace SmSim.Extras.Warfront.Chaos.Finale;

/// <summary>Where the finale stands (the wire's phase byte).</summary>
public enum WarfrontDuelPhase : byte
{
    /// <summary>The Chosen still stand (or nobody was living when the last fell: the fortress-wipe rule decides).</summary>
    Bodyguard = 0,
    /// <summary>The queue was taken this tick; the first challenger enters at once.</summary>
    Snapshot = 1,
    /// <summary>A challenger is on the plaza with the Lord.</summary>
    Duel = 2,
    /// <summary>The last challenger's attempt ended; the next enters after the pause.</summary>
    Handoff = 3,
    /// <summary>A challenger killed the Lord: the fortress is won.</summary>
    Won = 4,
    /// <summary>The queue ran out with the Lord alive: the campaign is lost (<c>WarfrontOutcome.FinaleLost</c>).</summary>
    Lost = 5,
}

/// <summary>Why a challenger's attempt ended.</summary>
public enum WarfrontDuelEnd : byte { None = 0, Defeated = 1, Disconnected = 2, Skipped = 3 }

/// <summary>One attempt in the record: the challenger's key and how it ended.</summary>
public readonly record struct WarfrontDuelAttempt(ulong Key, WarfrontDuelEnd End);

/// <summary>
/// The duel relay (Chaos Warfront C5; brief: "one attempt each"): pure, keyed by campaign key. <see cref="Snapshot"/> takes the ranked queue once; <see cref="Begin"/> gives the next challenger who is
/// present (an absent one is skipped and his turn is gone); <see cref="Advance"/> ends the current attempt (a defeat or a disconnect). The queue never grows: a player who comes back after his turn spectates.
/// </summary>
public sealed class WarfrontDuel
{
    readonly List<ulong> _queue = new();
    readonly HashSet<ulong> _attempted = new();
    readonly List<WarfrontDuelAttempt> _record = new();

    /// <summary>The phase.</summary>
    public WarfrontDuelPhase Phase { get; private set; } = WarfrontDuelPhase.Bodyguard;
    /// <summary>The queue in ranked order (campaign keys).</summary>
    public IReadOnlyList<ulong> Queue => _queue;
    /// <summary>The index in <see cref="Queue"/> of the current (or last) challenger; -1 before the first.</summary>
    public int Index { get; private set; } = -1;
    /// <summary>The current challenger's key (null between challengers and before the first).</summary>
    public ulong? Current { get; private set; }
    /// <summary>The Lord's pool id (set at the snapshot).</summary>
    public ushort LordId { get; private set; }
    /// <summary>The attempts so far, in order (skips included).</summary>
    public IReadOnlyList<WarfrontDuelAttempt> Record => _record;
    /// <summary>The 0-based number of the current attempt among those really fought (skips not counted).</summary>
    public int AttemptIndex { get; private set; } = -1;
    /// <summary>Raised on every change of the state (the server sends on a change).</summary>
    public int Version { get; private set; }

    /// <summary>Whether <paramref name="key"/> has had (or lost) his turn.</summary>
    public bool Attempted(ulong key) => _attempted.Contains(key);

    /// <summary>Whether no challenger is left after the current one.</summary>
    public bool Exhausted => Remaining == 0 && Current is null;

    /// <summary>Queued players still to come after the current one (absent ones included until their turn).</summary>
    public int Remaining
    {
        get
        {
            int n = 0;
            foreach (ulong k in _queue) if (!_attempted.Contains(k) && k != Current) n++;
            return n;
        }
    }

    /// <summary>The next queued player still to come (null: none), the first present one when <paramref name="present"/> is given.</summary>
    public ulong? Next(Func<ulong, bool>? present = null)
    {
        foreach (ulong k in _queue)
            if (!_attempted.Contains(k) && k != Current && (present is null || present(k))) return k;
        return null;
    }

    /// <summary>
    /// Takes the queue (ranked keys, the first duels first) on the tick the last Chosen fell. False, and nothing changed, outside the bodyguard phase or for an empty queue (nobody living: the fortress-wipe
    /// rule decides). A key listed twice counts once.
    /// </summary>
    public bool Snapshot(IReadOnlyList<ulong> ranked, ushort lordId)
    {
        if (Phase != WarfrontDuelPhase.Bodyguard || ranked.Count == 0) return false;
        foreach (ulong k in ranked) if (!_queue.Contains(k)) _queue.Add(k);
        LordId = lordId;
        Phase = WarfrontDuelPhase.Snapshot;
        Version++;
        return true;
    }

    /// <summary>
    /// The next challenger (from the snapshot or a handoff): the first queued key not yet attempted for whom <paramref name="present"/> holds; every absent one before him is skipped (his turn is gone).
    /// The phase becomes Duel with him current, or Lost when nobody is left. Returns the key, or null (Lost, or not between challengers).
    /// </summary>
    public ulong? Begin(Func<ulong, bool> present)
    {
        if (Phase is not (WarfrontDuelPhase.Snapshot or WarfrontDuelPhase.Handoff)) return null;
        for (int i = 0; i < _queue.Count; i++)
        {
            ulong k = _queue[i];
            if (_attempted.Contains(k)) continue;
            if (!present(k))
            {
                _attempted.Add(k);
                _record.Add(new WarfrontDuelAttempt(k, WarfrontDuelEnd.Skipped));
                continue;
            }
            Index = i;
            Current = k;
            AttemptIndex++;
            Phase = WarfrontDuelPhase.Duel;
            Version++;
            return k;
        }
        Current = null;
        Phase = WarfrontDuelPhase.Lost;
        Version++;
        return null;
    }

    /// <summary>The current challenger's attempt is over (<paramref name="end"/>, a defeat or a disconnect): Handoff when anyone is left to come, else Lost. False outside a duel.</summary>
    public bool Advance(WarfrontDuelEnd end)
    {
        if (Phase != WarfrontDuelPhase.Duel || Current is not { } k) return false;
        _attempted.Add(k);
        _record.Add(new WarfrontDuelAttempt(k, end));
        Current = null;
        Phase = Remaining > 0 ? WarfrontDuelPhase.Handoff : WarfrontDuelPhase.Lost;
        Version++;
        return true;
    }

    /// <summary>The current challenger killed the Lord. False outside a duel.</summary>
    public bool Win()
    {
        if (Phase != WarfrontDuelPhase.Duel) return false;
        Phase = WarfrontDuelPhase.Won;
        Version++;
        return true;
    }

    /// <summary>Whether the relay is under way (a challenger on the plaza or the pause between two): the isolation holds.</summary>
    public bool Active => Phase is WarfrontDuelPhase.Snapshot or WarfrontDuelPhase.Duel or WarfrontDuelPhase.Handoff;
}
