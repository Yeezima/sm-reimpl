using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>
/// The events waiting to go out (design section C: each event goes out in <see cref="MusouSendRules.EventRepeat"/> messages; a client plays each
/// (kind, tick, enemy, player) once). Server-wide: every client gets the same events. Each tick at most <c>cap</c> copies are chosen: deaths first, then the
/// boss's telegraphs, strikes, tracers and hits; within a kind the events sent the fewest times first (so an overflow rotates instead of starving), then the
/// newest. An event leaves after its 3 copies, or unsent after <see cref="MusouSendRules.MaxEventAgeTicks"/> ticks (counted). Engine-free and deterministic.
/// </summary>
public sealed class MusouEventWindow
{
    sealed class Entry { public MusouEvent Event; public int Copies; }
    readonly List<Entry> _window = new();
    readonly List<Entry> _order = new();
    readonly List<MusouEvent> _chosen = new();

    /// <summary>Copies never sent (events that aged out before their 3 copies), and events that never went out at all, since the start.</summary>
    public int CopiesDropped { get; private set; }
    public int EventsDropped { get; private set; }
    /// <summary>Events waiting (test aid).</summary>
    public int Waiting => _window.Count;
    /// <summary>Test aid (server env <c>SM_MUSOU_OLD_NEAR_SEND=1</c>): the order before the night batch of 2026-11-06, without <see cref="MusouSendRules.TelegraphFirst"/>.</summary>
    public bool OldOrder { get; init; }
    /// <summary>
    /// Test aid (server env <c>SM_MUSOU_OLD_HIT_RANK=1</c>): every hit after the tracers, as before musou-net-budget (the server then also sends no client its own events first). Since then
    /// (the owner, 2026-11-09: the players' attacks, then the allied Marines', first for gore) a human player's hit goes right after the strikes, an AI Marine's after it, both before the
    /// tracers; fire support's last.
    /// </summary>
    public bool OldHitRank { get; init; }

    /// <summary>
    /// This tick's events (<paramref name="events"/>, of <paramref name="tick"/>) join the window and the copies to send now come back (at most
    /// <paramref name="cap"/>; each counts as one of its event's copies).
    /// </summary>
    public IReadOnlyList<MusouEvent> Step(uint tick, IReadOnlyList<MusouEvent> events, int cap = MusouSendRules.MaxEventsPerTick)
    {
        _window.RemoveAll(e =>
        {
            if (e.Copies >= MusouSendRules.EventRepeat) return true;
            if (e.Event.Tick > tick) return true;   // a tick from the future: the match restarted
            if (tick - e.Event.Tick <= MusouSendRules.MaxEventAgeTicks) return false;
            CopiesDropped += MusouSendRules.EventRepeat - e.Copies;
            if (e.Copies == 0) EventsDropped++;
            return true;
        });
        for (int i = 0; i < events.Count; i++) _window.Add(new Entry { Event = events[i] });
        _order.Clear();
        _order.AddRange(_window);
        bool telegraphFirst = MusouSendRules.TelegraphFirst && !OldOrder;
        _order.Sort((a, b) =>
        {
            int c = telegraphFirst ? First(b).CompareTo(First(a)) : 0;   // a telegraph's first copy before everything: a grenade's 60 deaths held a near wind-up's back up to 4 ticks
            if (c != 0) return c;
            c = (OldHitRank ? Rank(a.Event.Kind) : Rank(a.Event)).CompareTo(OldHitRank ? Rank(b.Event.Kind) : Rank(b.Event));
            if (c != 0) return c;
            c = a.Copies.CompareTo(b.Copies);
            if (c != 0) return c;
            c = b.Event.Tick.CompareTo(a.Event.Tick);
            if (c != 0) return c;
            c = a.Event.Enemy.CompareTo(b.Event.Enemy);
            return c != 0 ? c : a.Event.Player.CompareTo(b.Event.Player);
        });
        _chosen.Clear();
        for (int i = 0; i < _order.Count && _chosen.Count < cap; i++) { _order[i].Copies++; _chosen.Add(_order[i].Event); }
        return _chosen;
    }

    public void Clear() => _window.Clear();

    static bool First(Entry e) => e.Copies == 0 && e.Event.Kind == MusouEventKind.Telegraph;

    /// <summary>
    /// The rank of <paramref name="e"/> (lower first): deaths, telegraphs, strikes (an Ork's blow on a player: gameplay feedback), a human's hit, an AI Marine's hit, tracers, fire support's
    /// hits (player 0).
    /// </summary>
    public static int Rank(in MusouEvent e) => e.Kind switch
    {
        MusouEventKind.Death => 0,
        MusouEventKind.Telegraph => 1,
        MusouEventKind.Strike => 2,
        MusouEventKind.Hit when e.Player > 0 && !SmSim.Extras.Musou.Campaign.TfwContracts.IsPuppet(e.Player) => 3,
        MusouEventKind.Hit when e.Player > 0 => 4,
        MusouEventKind.Tracer => 5,
        _ => 6,
    };

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
    /// <summary>
    /// Declared first: the other budget fields read it while the class initialises. Server env <c>SM_MUSOU_OLD_BUDGET=1</c> (read once at start): the budgets before musou-net-budget (Musou 55 KB/s in 4 messages a tick, the client's whole stream 58 KB/s,
    /// the war's 20 KB/s with an 8 KB/s floor in 2 messages). A test aid, and the switch for a server whose remote players cannot take about 1 Mbit/s.
    /// </summary>
    public static readonly bool OldBudget = Environment.GetEnvironmentVariable("SM_MUSOU_OLD_BUDGET") == "1";
    /// <summary>Interest bands (design): within 15 m an enemy gains 1.0 a tick, 15-40 m 0.5, beyond 0.2.</summary>
    public const float NearMetres = 15f, MidMetres = 40f, NearRate = 1f, MidRate = 0.5f, FarRate = 0.2f;
    /// <summary>An enemy is due once its priority reaches this (ours: near every tick, mid every 2nd, far every 5th).</summary>
    public const float SendThreshold = 1f;
    /// <summary>Priority added when the state differs from the one this client was last sent (ours: a wind-up or a stagger shows within a tick).</summary>
    public const float StateChangeBoost = 1f;
    /// <summary>The boss's and the Nobs' least gain a tick, whatever the distance (ours: about 15 Hz far away), and they go first (design).</summary>
    public const float LeaderMinRate = 0.5f;
    /// <summary>
    /// An enemy known to the client and not sent for this many ticks goes before every other due enemy but the leaders (ours, 2026-11-06): a crowd that fills the
    /// budget (1800 Orks, most within 40 m) otherwise left a far Ork 30 ticks and more without a record, and the client held him still between (the owner: "the orks
    /// movement at distance looks so choppy"). The server's <c>SM_MUSOU_OLD_FAR_SEND=1</c> restores the old order (<see cref="MusouInterest.OldFarSend"/>).
    /// </summary>
    public const uint MaxWaitTicks = 15;
    /// <summary>
    /// Night batch 2026-11-06 (ours): an enemy within <see cref="NearMetres"/> whose state differs from the one this client was last sent (a wind-up, a strike, a stagger) goes
    /// before every other due enemy but the leaders, the overdue far ones included: under a full budget (1800 Orks) a near wind-up's record came 0.8 ticks late on average,
    /// up to 2. With <see cref="TelegraphFirst"/>, a <see cref="MusouEventKind.Telegraph"/>'s first copy goes before every other event copy (a grenade's 60 deaths held one
    /// back up to 4 ticks). The server's <c>SM_MUSOU_OLD_NEAR_SEND=1</c> restores both old orders (<see cref="MusouInterest.OldNearSend"/>, <see cref="MusouEventWindow.OldOrder"/>).
    /// </summary>
    public const bool NearChangeFirst = true, TelegraphFirst = true;
    /// <summary>
    /// crowd-smooth (2026-11-08, ours): an enemy within <see cref="NearMetres"/> not sent for this many ticks goes before the overdue far ones (<see cref="MaxWaitTicks"/>), right after the near state
    /// changes. Under a full budget (1800 Orks) the overdue far ones took it all and a Boy fighting 3 m from the player had a record only every 6th tick, so the client, 3 ticks behind, ran past his
    /// newest record and his runs and stops were guessed. 2 keeps a near enemy's records within the view's 3-tick delay. Server <c>SM_MUSOU_OLD_NEAR_RATE=1</c>: the order before (<see cref="MusouInterest.OldNearRate"/>).
    /// </summary>
    public const uint NearMaxWaitTicks = 2;
    /// <summary>
    /// Most envelope messages to one client in one tick (design: 4; 6 since musou-net-budget, 2026-11-09, so the raised budget fits with room for a tick of events;
    /// <see cref="OldBudget"/> restores 4).
    /// </summary>
    public static readonly int MaxMessagesPerTick = OldBudget ? 4 : 6;
    /// <summary>Each event is sent in this many messages (design).</summary>
    public const int EventRepeat = 3;
    /// <summary>Most event copies in one tick's sections (ours: two full sections); the budget lowers it (<see cref="EventsPerTick"/>).</summary>
    public const int MaxEventsPerTick = 2 * MusouWire.MaxEvents;
    /// <summary>An event not sent 3 times within this many ticks leaves the window (ours: 1 s at 30 Hz; counted as dropped).</summary>
    public const int MaxEventAgeTicks = 30;
    /// <summary>The share of a tick's budget events may take (ours): the rest is the entities' (the crowd never freezes under a storm of hits).</summary>
    public const float EventShare = 0.35f;

    /// <summary>Event copies a tick at <paramref name="tickRate"/>: <see cref="EventShare"/> of the tick's budget in 16-byte records, at most <see cref="MaxEventsPerTick"/>.</summary>
    public static int EventsPerTick(int tickRate)
    {
        float bytes = BudgetBytesPerSecond / (float)Math.Max(1, tickRate) * EventShare - 2 * (Protocol.ExtrasSectionHeaderBytes + 1);
        return Math.Clamp((int)(bytes / MusouWire.EventBytes), 1, MaxEventsPerTick);
    }
    /// <summary>The director is sent every this many ticks (and on any change; design).</summary>
    public const int DirectorEveryTicks = 10;
    /// <summary>
    /// A client's long-run budget for Musou (ours), refilled each tick; up to <see cref="BurstBytes"/> saved. 55 KB/s until musou-net-budget; the owner, 2026-11-09, chose a
    /// 120 KB/s stream ("option 2 plus a 120 KB/s budget"): Musou 110 KB/s; the war gets what Musou leaves of <c>TfwSendRules.ClientBudgetBytesPerSecond</c> (104, set by measurement), at least its 15 KB/s floor, which Musou's next refill pays back, so the whole stream runs at about 120 KB/s. Measured before: the Ork records alone
    /// filled 39-42 KB/s and about 22,000 due records a second were held back at 1,371 Orks, so the extra goes mostly to a fresher crowd.
    /// </summary>
    public static readonly int BudgetBytesPerSecond = OldBudget ? 55_000 : 110_000;
    /// <summary>The least share of a tick's refill Musou keeps whatever the core messages took (ours, musou/perf-pass): the crowd never stops.</summary>
    public const float CoreFloor = 0.3f;
    /// <summary>Most budget a client saves (ours: one tick of <see cref="MaxMessagesPerTick"/> full messages).</summary>
    public static readonly int BurstBytes = MaxMessagesPerTick * Protocol.MaxExtrasMessageBytes;

    /// <summary>What an enemy at <paramref name="distance"/> metres gains a tick (design's bands).</summary>
    public static float Rate(float distance) => distance <= NearMetres ? NearRate : distance <= MidMetres ? MidRate : FarRate;

    /// <summary>The boss and the Nobs go first and never fall below <see cref="LeaderMinRate"/>.</summary>
    public static bool IsLeader(MusouEnemyType type) => type is MusouEnemyType.Warboss or MusouEnemyType.Nob || MusouBossOrks.Is(type);   // a Boss Ork too (2026-11-06): every client sees it from afar

    /// <summary>The send order's first key: the boss, then the Nobs, then the rest.</summary>
    public static int Rank(MusouEnemyType type) => type switch { MusouEnemyType.Warboss => 2, MusouEnemyType.Nob => 1, _ when MusouBossOrks.Is(type) => 2, _ => 0 };
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
    readonly uint[] _sentTick = new uint[MusouSim.Capacity];
    uint _tick;

    /// <summary>Test aid (server env <c>SM_MUSOU_OLD_FAR_SEND=1</c>): the send order before 2026-11-06, without <see cref="MusouSendRules.MaxWaitTicks"/>.</summary>
    public bool OldFarSend { get; init; }
    /// <summary>Test aid (server env <c>SM_MUSOU_OLD_NEAR_SEND=1</c>): the send order before the night batch of 2026-11-06, without <see cref="MusouSendRules.NearChangeFirst"/>.</summary>
    public bool OldNearSend { get; init; }
    readonly bool[] _urgent = new bool[MusouSim.Capacity];
    readonly bool[] _near = new bool[MusouSim.Capacity];
    /// <summary>Test aid (server env <c>SM_MUSOU_OLD_NEAR_RATE=1</c>): the send order before crowd-smooth, without <see cref="MusouSendRules.NearMaxWaitTicks"/>.</summary>
    public bool OldNearRate { get; init; }

    bool NearOverdue(int i) => !OldNearRate && _near[i] && _known[i] && _tick >= _sentTick[i] && _tick - _sentTick[i] >= MusouSendRules.NearMaxWaitTicks;

    bool Starving(int i) => !OldFarSend && _known[i] && _tick - _sentTick[i] >= MusouSendRules.MaxWaitTicks && _tick >= _sentTick[i];
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
    /// <summary>The core messages' bytes this client was sent since the last plan (the server sets it; 0: none counted, as before musou/perf-pass).</summary>
    public float CoreBytes { get; set; }

    public List<(byte Tag, byte[] Payload)[]> Plan(uint tick, int tickRate, ReadOnlySpan<MusouEnemy> enemies, Vector3? viewer, MusouBox box,
        byte[]? director, IReadOnlyList<MusouEvent> events, int own = 0)
    {
        events = OwnFirst(events, own);
        // musou/perf-pass, the owner's "count it" (2026-11-08): the core messages this client was sent since the last plan come out of this tick's refill (at most down to CoreFloor of it), so
        // the client's whole stream stays near the budget; the war's bucket then sees them as Musou's spending (TfwGuardInterest.MusouBytes) and keeps the total under its own.
        float refill = MusouSendRules.BudgetBytesPerSecond / (float)Math.Max(1, tickRate);
        _tokens = MathF.Min(MusouSend.Burst, _tokens + MathF.Max(refill * MusouSendRules.CoreFloor, refill - CoreBytes));
        CoreBytes = 0f;

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
            bool changed = !_known[i] || _sentState[i] != (byte)e.State;
            if (changed) _priority[i] += MusouSendRules.StateChangeBoost;
            _urgent[i] = MusouSendRules.NearChangeFirst && !OldNearSend && _known[i] && changed && distance <= MusouSendRules.NearMetres;
            _near[i] = distance <= MusouSendRules.NearMetres;
            if (_priority[i] >= MusouSendRules.SendThreshold - 1e-4f) _due.Add(_byIndex.Count);   // five 0.2 steps reach 1 despite the rounding
            _byIndex.Add(e);
        }
        var byIndex = _byIndex;
        var priority = _priority;
        _tick = tick;
        _due.Sort((a, b) =>
        {
            var (ea, eb) = (byIndex[a], byIndex[b]);
            int c = MusouSendRules.Rank(eb.Type).CompareTo(MusouSendRules.Rank(ea.Type));
            if (c != 0) return c;
            c = _urgent[eb.Id - 1].CompareTo(_urgent[ea.Id - 1]);   // a near Ork's new state (a wind-up) before the overdue far ones
            if (c != 0) return c;
            c = NearOverdue(eb.Id - 1).CompareTo(NearOverdue(ea.Id - 1));   // crowd-smooth: a near Ork 2 ticks unsent before the overdue far ones
            if (c != 0) return c;
            bool sa = Starving(ea.Id - 1), sb = Starving(eb.Id - 1);   // the overdue first, the longest unsent of them first: no far Ork waits much past MaxWaitTicks while the budget allows
            c = sb.CompareTo(sa);
            if (c != 0) return c;
            if (sa) { c = _sentTick[ea.Id - 1].CompareTo(_sentTick[eb.Id - 1]); if (c != 0) return c; }
            c = priority[eb.Id - 1].CompareTo(priority[ea.Id - 1]);
            return c != 0 ? c : ea.Id.CompareTo(eb.Id);
        });

        // Fixed sections first: the director (on a change or when due) and the events.
        var fixedSections = new List<(byte, byte[])>();
        bool directorDue = director is not null && (_lastDirector is null || !_lastDirector.AsSpan().SequenceEqual(director)
                                                    || tick - _lastDirectorTick >= MusouSendRules.DirectorEveryTicks || tick < _lastDirectorTick);
        if (directorDue) { fixedSections.Add((MusouWire.DirectorTag, director!)); _lastDirector = director; _lastDirectorTick = tick; }
        // Events take at most their share of the tick's budget (the window already chooses that many; a longer list is cut here): the rest is the entities'.
        int eventCount = Math.Min(events.Count, MusouSendRules.EventsPerTick(tickRate));
        for (int start = 0; start < eventCount; start += MusouWire.MaxEvents)
        {
            var chunk = new List<MusouEvent>(Math.Min(MusouWire.MaxEvents, eventCount - start));
            for (int k = start; k < eventCount && k < start + MusouWire.MaxEvents; k++) chunk.Add(events[k]);
            fixedSections.Add((MusouWire.EventsTag, MusouWire.WriteEvents(chunk, box)));
        }

        var messages = new List<List<(byte, byte[])>>();
        var sizes = new List<int>();
        foreach (var s in fixedSections)
        {
            int need = Protocol.ExtrasSectionHeaderBytes + s.Item2.Length;
            if (messages.Count == 0 || sizes[^1] + need > Protocol.MaxExtrasMessageBytes || messages[^1].Count >= Protocol.MaxExtrasSections)
            {
                if (messages.Count >= MusouSendRules.MaxMessagesPerTick) break;   // more than MaxMessagesPerTick messages of events: the rest waits for the window's next copy
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
                _priority[i] = 0f; _sentState[i] = (byte)e.State; _known[i] = true; _sentTick[i] = tick;
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
        _tokens = MathF.Max(0f, _tokens - sizes.Sum());   // a floor: a debt never starves the entities of later ticks (the director and the events fit a tick's refill)
        return messages.Where(m => m.Count > 0).Select(m => m.ToArray()).ToList();
    }

    readonly List<MusouEvent> _ownFirst = new();

    /// <summary>
    /// musou-net-budget: the client's own kills first, then every other death, telegraph and strike (gameplay comes before gore), then the client's own hits, then the rest, each part in
    /// the window's order: what the events' share or the message cap leaves out is never the client's own hit before another's (0: the order as given).
    /// </summary>
    IReadOnlyList<MusouEvent> OwnFirst(IReadOnlyList<MusouEvent> events, int own)
    {
        if (own <= 0) return events;
        _ownFirst.Clear();
        for (int part = 0; part < 4; part++)
            for (int i = 0; i < events.Count; i++)
                if (Part(events[i], own) == part) _ownFirst.Add(events[i]);
        return _ownFirst;
    }

    static int Part(in MusouEvent e, int own) =>
        e.Kind == MusouEventKind.Death && e.Player == own ? 0
        : e.Kind is MusouEventKind.Death or MusouEventKind.Telegraph or MusouEventKind.Strike ? 1
        : e.Kind == MusouEventKind.Hit && e.Player == own ? 2
        : 3;

    /// <summary>Forgets every enemy (a new round, a cleared pool): each is sent again as new.</summary>
    public void Reset()
    {
        Array.Clear(_priority); Array.Clear(_known); Array.Clear(_sentState); Array.Clear(_sentTick);
        _lastDirector = null;
    }
}

/// <summary>Shared helpers of the send side.</summary>
public static class MusouSend
{
    internal static float Burst => MusouSendRules.BurstBytes;

    /// <summary>
    /// The box to quantise over: the director's when it has a volume (MU1 sets it), else <paramref name="fallback"/> (the nav graph's bounds), else a box of
    /// 256 m by 256 m by 64 m around the origin (ours: the flat placeholder ground). Always <see cref="MusouBox.Sane"/>.
    /// </summary>
    public static MusouBox EffectiveBox(MusouBox director, MusouBox? fallback)
    {
        var d = director.Max - director.Min;
        if (d.X > 0f && d.Y > 0f && d.Z > 0f && float.IsFinite(d.X + d.Y + d.Z)) return Readable(director.Sane());
        if (fallback is { } f && float.IsFinite(f.Min.X + f.Min.Y + f.Min.Z + f.Max.X + f.Max.Y + f.Max.Z)) return Readable(f.Sane());
        return new MusouBox(new Vector3(-128f, -128f, -16f), new Vector3(128f, 128f, 48f));
    }

    /// <summary>A box a client accepts (<see cref="MusouWire.ReadDirector"/>): corners within <see cref="MusouWire.MaxBoxCoordinate"/>, each axis at least 1 m wide.</summary>
    static MusouBox Readable(MusouBox b)
    {
        var limit = new Vector3(MusouWire.MaxBoxCoordinate - 1f);
        var min = Vector3.Clamp(b.Min, -limit, limit);
        var max = Vector3.Clamp(b.Max, -limit, limit);
        return new MusouBox(min, Vector3.Max(max, min + Vector3.One));
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
