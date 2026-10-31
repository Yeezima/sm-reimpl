using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>
/// The events of the last <see cref="MusouSendRules.EventRepeat"/> ticks (design section C: each event goes out in 3 messages; a client plays each
/// (kind, tick, enemy) once). Server-wide: every client gets the same events. Engine-free and deterministic.
/// </summary>
public sealed class MusouEventWindow
{
    readonly List<MusouEvent> _window = new();
    readonly List<MusouEvent> _chosen = new();

    /// <summary>Event copies left out of a tick's sections (more than <see cref="MusouSendRules.MaxEventsPerTick"/> in the window), since the start.</summary>
    public int CopiesDropped { get; private set; }

    /// <summary>
    /// This tick's events (<paramref name="events"/>, of <paramref name="tick"/>) join the window, events older than the repeat leave it, and the events to send now
    /// come back: deaths first, then the boss's telegraphs, strikes, tracers and hits; within a kind the newest first; at most <see cref="MusouSendRules.MaxEventsPerTick"/>.
    /// </summary>
    public IReadOnlyList<MusouEvent> Step(uint tick, IReadOnlyList<MusouEvent> events)
    {
        _window.RemoveAll(e => tick - e.Tick >= MusouSendRules.EventRepeat || e.Tick > tick);
        for (int i = 0; i < events.Count; i++) _window.Add(events[i]);
        _chosen.Clear();
        _chosen.AddRange(_window);
        _chosen.Sort(static (a, b) =>
        {
            int c = Rank(a.Kind).CompareTo(Rank(b.Kind));
            if (c != 0) return c;
            c = b.Tick.CompareTo(a.Tick);
            return c != 0 ? c : a.Enemy.CompareTo(b.Enemy);
        });
        if (_chosen.Count > MusouSendRules.MaxEventsPerTick)
        {
            CopiesDropped += _chosen.Count - MusouSendRules.MaxEventsPerTick;
            _chosen.RemoveRange(MusouSendRules.MaxEventsPerTick, _chosen.Count - MusouSendRules.MaxEventsPerTick);
        }
        return _chosen;
    }

    public void Clear() => _window.Clear();

    static int Rank(MusouEventKind kind) => kind switch
    {
        MusouEventKind.Death => 0,
        MusouEventKind.Telegraph => 1,
        MusouEventKind.Strike => 2,
        MusouEventKind.Tracer => 3,
        _ => 4,
    };
}

/// <summary>The send rules of Musou (design section C; the values marked ours are estimates for the refine register).</summary>
public static class MusouSendRules
{
    /// <summary>Interest bands (design): within 15 m an enemy gains 1.0 a tick, 15-40 m 0.5, beyond 0.2.</summary>
    public const float NearMetres = 15f, MidMetres = 40f, NearRate = 1f, MidRate = 0.5f, FarRate = 0.2f;
    /// <summary>An enemy is due once its priority reaches this (ours: near every tick, mid every 2nd, far every 5th).</summary>
    public const float SendThreshold = 1f;
    /// <summary>Priority added when the state differs from the one this client was last sent (ours: a wind-up or a stagger shows within a tick).</summary>
    public const float StateChangeBoost = 1f;
    /// <summary>The boss's and the Nobs' least gain a tick, whatever the distance (ours: about 15 Hz far away), and they go first (design).</summary>
    public const float LeaderMinRate = 0.5f;
    /// <summary>Most envelope messages to one client in one tick (design).</summary>
    public const int MaxMessagesPerTick = 4;
    /// <summary>Each event is sent in this many messages (design).</summary>
    public const int EventRepeat = 3;
    /// <summary>Most event copies in one tick's sections (ours: two full sections).</summary>
    public const int MaxEventsPerTick = 2 * MusouWire.MaxEvents;
    /// <summary>The director is sent every this many ticks (and on any change; design).</summary>
    public const int DirectorEveryTicks = 10;
    /// <summary>A client's long-run budget for Musou (ours: under the 60 KB/s target with headroom), refilled each tick; up to <see cref="BurstBytes"/> saved.</summary>
    public const int BudgetBytesPerSecond = 55_000;
    /// <summary>Most budget a client saves (ours: one tick of <see cref="MaxMessagesPerTick"/> full messages).</summary>
    public const int BurstBytes = MaxMessagesPerTick * Protocol.MaxExtrasMessageBytes;

    /// <summary>What an enemy at <paramref name="distance"/> metres gains a tick (design's bands).</summary>
    public static float Rate(float distance) => distance <= NearMetres ? NearRate : distance <= MidMetres ? MidRate : FarRate;

    /// <summary>The boss and the Nobs go first and never fall below <see cref="LeaderMinRate"/>.</summary>
    public static bool IsLeader(MusouEnemyType type) => type is MusouEnemyType.Warboss or MusouEnemyType.Nob;

    /// <summary>The send order's first key: the boss, then the Nobs, then the rest.</summary>
    public static int Rank(MusouEnemyType type) => type switch { MusouEnemyType.Warboss => 2, MusouEnemyType.Nob => 1, _ => 0 };
}

/// <summary>
/// One client's interest in the Musou enemies (design section C): a priority per enemy slot that grows each tick by distance from the client's player
/// (<see cref="MusouSendRules.Rate"/>) and resets when the enemy is sent; the boss and the Nobs go first; a token bucket keeps the client's bytes under
/// <see cref="MusouSendRules.BudgetBytesPerSecond"/>. <see cref="Plan"/> packs a tick's sections (director, events, then the enemies due) into at most
/// <see cref="MusouSendRules.MaxMessagesPerTick"/> envelope messages of at most <see cref="Protocol.MaxExtrasMessageBytes"/> bytes. Engine-free and deterministic.
/// </summary>
public sealed class MusouInterest
{
    readonly float[] _priority = new float[MusouSim.Capacity];
    readonly byte[] _sentState = new byte[MusouSim.Capacity];
    readonly bool[] _known = new bool[MusouSim.Capacity];
    readonly List<int> _due = new();
    readonly List<MusouEntity> _entities = new();
    readonly List<MusouEnemy> _byIndex = new();
    float _tokens = MusouSend.Burst;
    byte[]? _lastDirector;
    uint _lastDirectorTick;

    /// <summary>Entity records sent to this client, and enemies due but held back by the budget or the message cap (summed over ticks).</summary>
    public long RecordsSent { get; private set; }
    public long RecordsHeld { get; private set; }
    /// <summary>The budget left (bytes; negative after a tick of events beyond it).</summary>
    public float Tokens => _tokens;

    /// <summary>The priority of the enemy with <paramref name="id"/> (test aid).</summary>
    public float PriorityOf(ushort id) => id >= 1 && id <= MusouSim.Capacity ? _priority[id - 1] : 0f;

    /// <summary>
    /// This tick's messages for the client: the director when it changed or every <see cref="MusouSendRules.DirectorEveryTicks"/> ticks (null: none to send),
    /// the window's events, then the living enemies of <paramref name="enemies"/> that are due, highest priority first, within the budget and the message cap.
    /// <paramref name="viewer"/> is the client's player (null: a spectator or a dead player without a place; every enemy counts as mid range).
    /// Each message is a list of sections; an empty list means nothing to send.
    /// </summary>
    public List<(byte Tag, byte[] Payload)[]> Plan(uint tick, int tickRate, ReadOnlySpan<MusouEnemy> enemies, Vector3? viewer, MusouBox box,
        byte[]? director, IReadOnlyList<MusouEvent> events)
    {
        _tokens = MathF.Min(MusouSend.Burst, _tokens + MusouSendRules.BudgetBytesPerSecond / (float)Math.Max(1, tickRate));

        // Interest: every living enemy gains by distance; a changed state is boosted; the dead are forgotten (their death is an event).
        _due.Clear();
        _byIndex.Clear();
        for (int k = 0; k < enemies.Length; k++)
        {
            var e = enemies[k];
            int i = e.Id - 1;
            if ((uint)i >= MusouSim.Capacity) continue;
            if (!e.Alive) { _priority[i] = 0f; _known[i] = false; continue; }
            float distance = viewer is { } v ? Vector2.Distance(new Vector2(e.Position.X, e.Position.Y), new Vector2(v.X, v.Y)) : MusouSendRules.MidMetres;
            float rate = MusouSendRules.Rate(distance);
            if (MusouSendRules.IsLeader(e.Type)) rate = MathF.Max(rate, MusouSendRules.LeaderMinRate);
            _priority[i] += rate;
            if (!_known[i] || _sentState[i] != (byte)e.State) _priority[i] += MusouSendRules.StateChangeBoost;
            if (_priority[i] >= MusouSendRules.SendThreshold - 1e-4f) _due.Add(_byIndex.Count);   // five 0.2 steps reach 1 despite the rounding
            _byIndex.Add(e);
        }
        var byIndex = _byIndex;
        var priority = _priority;
        _due.Sort((a, b) =>
        {
            var (ea, eb) = (byIndex[a], byIndex[b]);
            int c = MusouSendRules.Rank(eb.Type).CompareTo(MusouSendRules.Rank(ea.Type));
            if (c != 0) return c;
            c = priority[eb.Id - 1].CompareTo(priority[ea.Id - 1]);
            return c != 0 ? c : ea.Id.CompareTo(eb.Id);
        });

        // Fixed sections first: the director (on a change or when due) and the events.
        var fixedSections = new List<(byte, byte[])>();
        bool directorDue = director is not null && (_lastDirector is null || !_lastDirector.AsSpan().SequenceEqual(director)
                                                    || tick - _lastDirectorTick >= MusouSendRules.DirectorEveryTicks || tick < _lastDirectorTick);
        if (directorDue) { fixedSections.Add((MusouWire.DirectorTag, director!)); _lastDirector = director; _lastDirectorTick = tick; }
        for (int start = 0; start < events.Count; start += MusouWire.MaxEvents)
        {
            var chunk = new List<MusouEvent>(Math.Min(MusouWire.MaxEvents, events.Count - start));
            for (int k = start; k < events.Count && k < start + MusouWire.MaxEvents; k++) chunk.Add(events[k]);
            fixedSections.Add((MusouWire.EventsTag, MusouWire.WriteEvents(chunk, box)));
        }

        var messages = new List<List<(byte, byte[])>>();
        var sizes = new List<int>();
        foreach (var s in fixedSections)
        {
            int need = Protocol.ExtrasSectionHeaderBytes + s.Item2.Length;
            if (messages.Count == 0 || sizes[^1] + need > Protocol.MaxExtrasMessageBytes || messages[^1].Count >= Protocol.MaxExtrasSections)
            {
                if (messages.Count >= MusouSendRules.MaxMessagesPerTick) break;   // more than 4 messages of events: the rest waits for the window's next copy
                messages.Add(new()); sizes.Add(Protocol.ExtrasHeaderBytes);
            }
            messages[^1].Add(s); sizes[^1] += need;
        }

        // The enemies due, as far as the budget and the message cap allow (the budget pays for the fixed sections first).
        float allowance = _tokens - sizes.Sum();
        int next = 0;
        while (next < _due.Count)
        {
            int room = messages.Count == 0 ? 0 : Protocol.MaxExtrasMessageBytes - sizes[^1] - Protocol.ExtrasSectionHeaderBytes - 1;
            int fit = Math.Min(Math.Min(_due.Count - next, MusouWire.MaxEntities), Math.Min(room, (int)allowance - Protocol.ExtrasSectionHeaderBytes - 1) / MusouWire.EntityBytes);
            if (fit <= 0)
            {
                // A new message: only when the cap and the budget allow one more record with its headers.
                if (messages.Count >= MusouSendRules.MaxMessagesPerTick
                    || allowance < Protocol.ExtrasHeaderBytes + Protocol.ExtrasSectionHeaderBytes + 1 + MusouWire.EntityBytes) break;
                messages.Add(new()); sizes.Add(Protocol.ExtrasHeaderBytes);
                allowance -= Protocol.ExtrasHeaderBytes;
                continue;
            }
            _entities.Clear();
            for (int k = 0; k < fit; k++)
            {
                var e = byIndex[_due[next + k]];
                _entities.Add(new MusouEntity(e.Id, e.Position, e.Yaw, e.Type, e.State));
                int i = e.Id - 1;
                _priority[i] = 0f; _sentState[i] = (byte)e.State; _known[i] = true;
            }
            var payload = MusouWire.WriteEntities(_entities, box);
            messages[^1].Add((MusouWire.EntitiesTag, payload));
            int bytes = Protocol.ExtrasSectionHeaderBytes + payload.Length;
            sizes[^1] += bytes;
            allowance -= bytes;
            next += fit;
            RecordsSent += fit;
        }
        RecordsHeld += _due.Count - next;
        _tokens -= sizes.Sum();
        return messages.Where(m => m.Count > 0).Select(m => m.ToArray()).ToList();
    }

    /// <summary>Forgets every enemy (a new round, a cleared pool): each is sent again as new.</summary>
    public void Reset()
    {
        Array.Clear(_priority); Array.Clear(_known); Array.Clear(_sentState);
        _lastDirector = null;
    }
}

/// <summary>Shared helpers of the send side.</summary>
public static class MusouSend
{
    internal const float Burst = MusouSendRules.BurstBytes;

    /// <summary>
    /// The box to quantise over: the director's when it has a volume (MU1 sets it), else <paramref name="fallback"/> (the nav graph's bounds), else a box of
    /// 256 m by 256 m by 64 m around the origin (ours: the flat placeholder ground). Always <see cref="MusouBox.Sane"/>.
    /// </summary>
    public static MusouBox EffectiveBox(MusouBox director, MusouBox? fallback)
    {
        var d = director.Max - director.Min;
        if (d.X > 0f && d.Y > 0f && d.Z > 0f && float.IsFinite(d.X + d.Y + d.Z)) return director.Sane();
        if (fallback is { } f) return f.Sane();
        return new MusouBox(new Vector3(-128f, -128f, -16f), new Vector3(128f, 128f, 48f));
    }

    /// <summary>The bounds of a walking graph's nodes, 2 m wider on each side and 3 m higher (ours: room for a jump or a stagger); null for an empty graph.</summary>
    public static MusouBox? BoundsOf(NavGraph? nav)
    {
        if (nav is null || nav.NodeCount == 0) return null;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int n = 0; n < nav.NodeCount; n++) { var p = nav.Position(n); min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        return new MusouBox(min - new Vector3(2f, 2f, 2f), max + new Vector3(2f, 2f, 3f));
    }
}
