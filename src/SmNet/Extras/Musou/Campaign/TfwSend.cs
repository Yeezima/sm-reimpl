using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Extras.Musou.Campaign;

/// <summary>The send rules of the war (design sections H and I; the values marked ours are estimates for the refine register).</summary>
public static class TfwSendRules
{
    /// <summary>The objectives go out every this many ticks (design).</summary>
    public const int ObjectivesEveryTicks = 10;
    /// <summary>The war goes out every this many seconds, and on any change (design).</summary>
    public const int WarEverySeconds = 1;
    /// <summary>The most a client gets for the Guardsmen and the war's other sections (design: 20 KB/s).</summary>
    public const int GuardBudgetBytesPerSecond = 20_000;
    /// <summary>The most a client gets for Musou's and the war's sections together (ours: under the design's 60 KB/s). Musou spends first, the war gets what is left, up to its own budget.</summary>
    public const int ClientBudgetBytesPerSecond = 58_000;
    /// <summary>Most envelope messages of the war to one client in one tick, on top of Musou's (ours).</summary>
    public const int MaxMessagesPerTick = 2;
    /// <summary>Most budget a client saves (ours: one tick of full messages).</summary>
    public const int BurstBytes = MaxMessagesPerTick * Protocol.MaxExtrasMessageBytes;
    /// <summary>The share of a tick's budget the events may take (ours, as Musou's): the rest is the Guardsmen's.</summary>
    public const float EventShare = 0.35f;
    /// <summary>A death goes in this many messages (ours, as Musou's events); a shot or a hurt man once: a lost tracer is not missed, and the shots are many.</summary>
    public const int DeathRepeat = 3;
    /// <summary>An event not sent within this many ticks leaves the window (ours: 1 s at 30 Hz).</summary>
    public const int MaxEventAgeTicks = 30;
    /// <summary>Events waiting in the feed at most, should nothing take them (ours).</summary>
    public const int MaxFedEvents = 1024;

    /// <summary>Event copies a tick at <paramref name="tickRate"/>: <see cref="EventShare"/> of the tick's budget.</summary>
    public static int EventsPerTick(int tickRate) =>
        Math.Clamp((int)(GuardBudgetBytesPerSecond / (float)Math.Max(1, tickRate) * EventShare / TfwWire.GuardEventBytes), 1, TfwWire.MaxGuardEvents);

    /// <summary>How many messages a fire record goes in (tag 11, M8; ours): a lost shell or run is missed, a lost tracer is not.</summary>
    public static int FireCopies(TfwFireKind kind) => kind == TfwFireKind.Round ? 1 : DeathRepeat;

    /// <summary>How many messages an event of <paramref name="kind"/> goes in.</summary>
    public static int Copies(TfwGuardEventKind kind) => kind == TfwGuardEventKind.Death ? DeathRepeat : 1;
}

/// <summary>
/// What the war shows the clients this tick, as the run leaves it for the server's send (<c>GameServer.TfwFeed</c>): the war, the reserves, the battle's objectives, the Guardsmen and their
/// events. The run owns the contents: <see cref="War"/> stays set, the lists hold the current tick's; a section with nothing to show is not sent.
/// </summary>
public sealed class TfwFeed
{
    readonly List<TfwGuardEvent> _events = new();

    /// <summary>The war (null: no tag 7).</summary>
    public WarSim? War { get; set; }
    public byte Transport { get; set; }
    public byte Supplies { get; set; }
    /// <summary>The battle's objectives, all of them (empty outside a battle).</summary>
    public List<TfwObjective> Objectives { get; } = new();
    /// <summary>The battle's sites: the landing zone, then the sources (empty outside a battle).</summary>
    public List<Vector3> Sites { get; } = new();
    /// <summary>The Guardsmen in the first <see cref="GuardCount"/> places (<c>GuardSim.CopyTo</c> fills it).</summary>
    public TfwGuard[] Guards { get; } = new TfwGuard[TfwWire.GuardCapacity];
    public int GuardCount { get; set; }

    /// <summary>An event of this tick, until the next send; false beyond <see cref="TfwSendRules.MaxFedEvents"/> (nothing is taking them).</summary>
    public bool AddEvent(in TfwGuardEvent e)
    {
        if (_events.Count >= TfwSendRules.MaxFedEvents) return false;
        _events.Add(e);
        return true;
    }

    /// <summary>The events added since the last send.</summary>
    public IReadOnlyList<TfwGuardEvent> Events => _events;

    /// <summary>The send took the events.</summary>
    public void ClearEvents() => _events.Clear();
}

/// <summary>
/// The Guard events waiting to go out, server-wide. Each tick at most <c>cap</c> copies are chosen: deaths first, then the hurt, then the shots; within a kind the events sent the fewest times,
/// then the newest. An event leaves after its copies (<see cref="TfwSendRules.Copies"/>), or after <see cref="TfwSendRules.MaxEventAgeTicks"/> ticks, whichever comes first. Deterministic.
/// </summary>
public sealed class TfwEventWindow
{
    sealed class Entry { public TfwGuardEvent Event; public int Copies; }
    readonly List<Entry> _window = new();
    readonly List<TfwGuardEvent> _chosen = new();

    /// <summary>Events that never went out (aged out unsent), since the start.</summary>
    public int EventsDropped { get; private set; }

    /// <summary>This tick's events join the window and the copies to send now come back (the list is reused by the next call).</summary>
    public IReadOnlyList<TfwGuardEvent> Step(uint tick, IReadOnlyList<TfwGuardEvent> events, int cap)
    {
        _window.RemoveAll(e =>
        {
            if (e.Copies >= TfwSendRules.Copies(e.Event.Kind) || e.Event.Tick > tick) return true;   // a tick from the future: the match restarted
            if (tick - e.Event.Tick <= TfwSendRules.MaxEventAgeTicks) return false;
            if (e.Copies == 0) EventsDropped++;
            return true;
        });
        for (int i = 0; i < events.Count; i++) _window.Add(new Entry { Event = events[i] });
        _window.Sort(static (a, b) =>
        {
            int c = b.Event.Kind.CompareTo(a.Event.Kind);
            if (c != 0) return c;
            c = a.Copies.CompareTo(b.Copies);
            if (c != 0) return c;
            c = b.Event.Tick.CompareTo(a.Event.Tick);
            if (c != 0) return c;
            c = a.Event.Guard.CompareTo(b.Event.Guard);
            return c != 0 ? c : a.Event.Enemy.CompareTo(b.Event.Enemy);
        });
        _chosen.Clear();
        for (int i = 0; i < _window.Count && _chosen.Count < cap; i++) { _window[i].Copies++; _chosen.Add(_window[i].Event); }
        return _chosen;
    }
}

/// <summary>
/// One client's share of the war (design section H, the second interest): the Guardsmen by distance as <see cref="MusouInterest"/> does the enemies (same bands, a changed state at once) and the war's
/// other sections, within <see cref="TfwSendRules.MaxMessagesPerTick"/> messages and a token bucket. The bucket refills with what the client's Musou sections left of
/// <see cref="TfwSendRules.ClientBudgetBytesPerSecond"/>, up to <see cref="TfwSendRules.GuardBudgetBytesPerSecond"/>, so the two together stay under the design's 60 KB/s. Engine-free and deterministic.
/// </summary>
public sealed class TfwGuardInterest
{
    readonly float[] _priority = new float[TfwWire.GuardCapacity];
    readonly byte[] _sentState = new byte[TfwWire.GuardCapacity];
    readonly bool[] _known = new bool[TfwWire.GuardCapacity];
    readonly bool[] _present = new bool[TfwWire.GuardCapacity];
    readonly List<int> _due = new();
    readonly List<TfwGuard> _records = new();
    readonly List<TfwGuard> _alive = new();
    readonly List<TfwGuardEvent> _events = new();
    float _tokens = TfwSendRules.BurstBytes;
    float _musouTokens = MusouSend.Burst;
    byte[]? _lastWar;
    uint _lastWarTick, _lastObjectivesTick;
    bool _objectivesSent;

    /// <summary>Guardsman records sent to this client, and men due but held back by the budget or the message cap (summed over ticks).</summary>
    public long RecordsSent { get; private set; }
    public long RecordsHeld { get; private set; }
    /// <summary>The budget left (bytes; negative after sections beyond it).</summary>
    public float Tokens => _tokens;

    /// <summary>
    /// What Musou spent on this client since the last call, from its bucket's level (<see cref="MusouInterest.Tokens"/>; null: no such client yet). A tick when Musou planned nothing reads as a full
    /// refill spent unless its bucket was full: the war then gets a little less, never more.
    /// </summary>
    public float MusouBytes(float? tokens, int tickRate)
    {
        if (tokens is not { } now) return 0f;
        float before = MathF.Min(MusouSend.Burst, _musouTokens + MusouSendRules.BudgetBytesPerSecond / (float)Math.Max(1, tickRate));
        _musouTokens = now;
        return MathF.Max(0f, before - now);
    }

    /// <summary>
    /// This tick's messages for the client: the war when it changed or a second passed, the objectives every 10 ticks, the events as far as the budget goes, then the Guardsmen due, highest
    /// priority first. <paramref name="war"/>, <paramref name="objectives"/>: this tick's payloads (null: none to send). <paramref name="viewer"/>: the client's player (null: every man counts as mid range).
    /// <paramref name="musouBytes"/>: what Musou's sections to this client cost this tick. Each message is a list of sections; an empty list means nothing to send.
    /// </summary>
    public List<(byte Tag, byte[] Payload)[]> Plan(uint tick, int tickRate, byte[]? war, byte[]? objectives, ReadOnlySpan<TfwGuard> guards, Vector3? viewer, MusouBox box,
        IReadOnlyList<TfwGuardEvent> events, float musouBytes)
    {
        float second = Math.Max(1, tickRate);
        float left = MathF.Max(0f, TfwSendRules.ClientBudgetBytesPerSecond / second - musouBytes);
        _tokens = MathF.Min(TfwSendRules.BurstBytes, _tokens + MathF.Min(TfwSendRules.GuardBudgetBytesPerSecond / second, left));

        var messages = new List<List<(byte, byte[])>>();
        var sizes = new List<int>();
        void Add(byte tag, byte[] payload)
        {
            int need = Protocol.ExtrasSectionHeaderBytes + payload.Length;
            if (messages.Count == 0 || sizes[^1] + need > Protocol.MaxExtrasMessageBytes) { messages.Add(new()); sizes.Add(Protocol.ExtrasHeaderBytes); }
            messages[^1].Add((tag, payload)); sizes[^1] += need;
        }

        if (war is not null && (_lastWar is null || !_lastWar.AsSpan().SequenceEqual(war) || tick - _lastWarTick >= (uint)(TfwSendRules.WarEverySeconds * tickRate) || tick < _lastWarTick))
        {
            Add(TfwWire.WarTag, war);
            _lastWar = war; _lastWarTick = tick;
        }
        if (objectives is not null && (!_objectivesSent || tick - _lastObjectivesTick >= TfwSendRules.ObjectivesEveryTicks || tick < _lastObjectivesTick))
        {
            Add(TfwWire.ObjectivesTag, objectives);
            _objectivesSent = true; _lastObjectivesTick = tick;
        }

        // The events, as far as the budget goes (a section's headers first). War, objectives and at most 79 events always fit two messages.
        int room = (int)((_tokens - sizes.Sum() - Protocol.ExtrasHeaderBytes - Protocol.ExtrasSectionHeaderBytes - 1) / TfwWire.GuardEventBytes);
        int eventCount = Math.Clamp(room, 0, Math.Min(events.Count, TfwWire.MaxGuardEvents));
        if (eventCount > 0)
        {
            _events.Clear();
            for (int k = 0; k < eventCount; k++) _events.Add(events[k]);
            Add(TfwWire.GuardEventsTag, TfwWire.WriteGuardEvents(_events, box));
        }

        // Interest: every man gains by distance; a changed state is boosted; a man who left the list is forgotten.
        Array.Clear(_present);
        _due.Clear();
        _alive.Clear();
        for (int k = 0; k < guards.Length; k++)
        {
            var g = guards[k];
            int i = g.Id - 1;
            if ((uint)i >= TfwWire.GuardCapacity) continue;
            _present[i] = true;
            float distance = viewer is { } v ? Vector2.Distance(new Vector2(g.Position.X, g.Position.Y), new Vector2(v.X, v.Y)) : MusouSendRules.MidMetres;
            _priority[i] += MusouSendRules.Rate(distance);
            if (!_known[i] || _sentState[i] != g.State) _priority[i] += MusouSendRules.StateChangeBoost;
            if (_priority[i] >= MusouSendRules.SendThreshold - 1e-4f) _due.Add(_alive.Count);
            _alive.Add(g);
        }
        for (int i = 0; i < TfwWire.GuardCapacity; i++)
            if (!_present[i]) { _priority[i] = 0f; _known[i] = false; }
        var alive = _alive;
        var priority = _priority;
        _due.Sort((a, b) =>
        {
            var (ga, gb) = (alive[a], alive[b]);
            int c = priority[gb.Id - 1].CompareTo(priority[ga.Id - 1]);
            return c != 0 ? c : ga.Id.CompareTo(gb.Id);
        });

        // The men due, as far as the budget and the message cap allow.
        float allowance = _tokens - sizes.Sum();
        int next = 0;
        while (next < _due.Count)
        {
            int free = messages.Count == 0 ? 0 : Protocol.MaxExtrasMessageBytes - sizes[^1] - Protocol.ExtrasSectionHeaderBytes - 1;
            int fit = Math.Min(Math.Min(_due.Count - next, TfwWire.MaxGuards), Math.Min(free, (int)allowance - Protocol.ExtrasSectionHeaderBytes - 1) / TfwWire.GuardBytes);
            if (fit <= 0)
            {
                if (messages.Count >= TfwSendRules.MaxMessagesPerTick || allowance < Protocol.ExtrasHeaderBytes + Protocol.ExtrasSectionHeaderBytes + 1 + TfwWire.GuardBytes) break;
                messages.Add(new()); sizes.Add(Protocol.ExtrasHeaderBytes);
                allowance -= Protocol.ExtrasHeaderBytes;
                continue;
            }
            _records.Clear();
            for (int k = 0; k < fit; k++)
            {
                var g = alive[_due[next + k]];
                _records.Add(g);
                int i = g.Id - 1;
                _priority[i] = 0f; _sentState[i] = g.State; _known[i] = true;
            }
            var payload = TfwWire.WriteGuards(_records, box);
            messages[^1].Add((TfwWire.GuardsTag, payload));
            int bytes = Protocol.ExtrasSectionHeaderBytes + payload.Length;
            sizes[^1] += bytes;
            allowance -= bytes;
            next += fit;
            RecordsSent += fit;
        }
        RecordsHeld += _due.Count - next;
        _tokens -= sizes.Sum();   // a debt stays: the war's and the objectives' sections go out whatever the bucket holds, and the next ticks pay for them
        return messages.Where(m => m.Count > 0).Select(m => m.ToArray()).ToList();
    }
}
