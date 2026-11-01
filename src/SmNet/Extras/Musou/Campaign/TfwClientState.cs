using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Extras.Musou.Campaign;

/// <summary>A Guardsman as a client shows it at one render tick: interpolated place and facing, and his state.</summary>
public readonly record struct TfwGuardShown(ushort Id, Vector3 Position, float Yaw, byte State);

/// <summary>
/// A client's commands on their way to the server (design section H): one at a time, oldest first, so they arrive in order; each is sent again every <see cref="ResendSeconds"/> until tag 7 echoes
/// its sequence, and never more than one in <see cref="MinGapSeconds"/> (the server's 8 a second). The view asks <see cref="Due"/> each frame and passes the payload to
/// <c>GameClient.SendExtrasCommand</c> under <see cref="TfwWire.CommandTag"/>. Engine-free.
/// </summary>
public sealed class TfwCommandSender
{
    /// <summary>A command unanswered for this long is sent again (design).</summary>
    public const double ResendSeconds = 0.25;
    /// <summary>The least time between two sends (design: at most 8 a second).</summary>
    public const double MinGapSeconds = 0.125;
    /// <summary>Commands waiting at most (ours: a player gives no more orders than this in a round trip).</summary>
    public const int MaxPending = 8;

    readonly Queue<TfwCommandRecord> _pending = new();
    ushort _sequence;
    double _lastSent = double.NegativeInfinity, _dueAt = double.NegativeInfinity;

    /// <summary>Commands waiting for their echo.</summary>
    public int Pending => _pending.Count;

    /// <summary>Queues a command; its sequence, or null when <see cref="MaxPending"/> wait. <paramref name="point"/>: the crosshair's point, for Hold and Attack. Sequence 0 is never used (the echo before any command).</summary>
    public ushort? Issue(TfwCommand command, byte argument = 0, Vector3 point = default, ushort offer = 0)
    {
        if (_pending.Count >= MaxPending) return null;
        if (++_sequence == 0) _sequence = 1;
        if (_pending.Count == 0) _dueAt = _lastSent + MinGapSeconds;
        _pending.Enqueue(new TfwCommandRecord(_sequence, command, argument, point, offer));
        return _sequence;
    }

    /// <summary>The payload to send now, if any.</summary>
    public byte[]? Due(double now)
    {
        if (_pending.Count == 0 || now < _dueAt) return null;
        _lastSent = now;
        _dueAt = now + ResendSeconds;
        return TfwWire.WriteCommand(_pending.Peek());
    }

    /// <summary>The server applied every command up to <paramref name="echo"/>: the next one need not wait for the resend time.</summary>
    public void Acknowledge(ushort echo)
    {
        bool any = false;
        while (_pending.Count > 0 && !TfwWire.IsNewer(_pending.Peek().Sequence, echo)) { _pending.Dequeue(); any = true; }
        if (any) _dueAt = _lastSent + MinGapSeconds;
    }

    /// <summary>Forgets the waiting commands (a new connection: the server's sequences start over with a new player).</summary>
    public void Clear()
    {
        _pending.Clear();
        _sequence = 0;
        _lastSent = _dueAt = double.NegativeInfinity;
    }
}

/// <summary>
/// The war on a client (design sections G and H), engine-free: the routed sections are read (<see cref="Apply"/>): the war and the objectives (the newest by server tick), the Guardsmen (a few records
/// per man, interpolated at the render tick by <see cref="Sample"/>; a man is dropped after his death event or <see cref="SilenceSeconds"/> without a record) and the Guard events (each played once). Tags 8 to 10 need the
/// director's box and wait for it (up to <see cref="MaxPending"/> sections). Tag 7's echo answers <see cref="Commands"/>.
/// </summary>
public sealed class TfwClientState
{
    /// <summary>An id without a record for this long is dropped (as Musou's: 2 s).</summary>
    public const double SilenceSeconds = 2.0;
    /// <summary>Sections held before the first box (as Musou's).</summary>
    public const int MaxPending = 64;
    /// <summary>Records kept per man (as Musou's).</summary>
    public const int History = 4;
    /// <summary>How long an event's identity and a death's tick are remembered (as Musou's).</summary>
    public const double MemorySeconds = 5.0;
    /// <summary>A section this many ticks older than the newest seen means the server restarted its clock (as Musou's).</summary>
    public const uint RestartTicks = 300;

    sealed class Track
    {
        public ushort Id;
        public readonly uint[] Ticks = new uint[History];
        public readonly Vector3[] Positions = new Vector3[History];
        public readonly float[] Yaws = new float[History];
        public readonly byte[] States = new byte[History];
        public int Count;
        public double Heard;
    }

    readonly Dictionary<ushort, Track> _tracks = new();
    readonly Dictionary<(TfwGuardEventKind, uint, ushort, ushort), double> _played = new();
    readonly Dictionary<ushort, (uint Tick, double At)> _dead = new();
    readonly List<ExtrasSection> _pending = new();
    readonly List<TfwGuardEvent> _newEvents = new();
    readonly List<TfwGuardShown> _shown = new();
    TfwObjective[] _objectives = [];
    Vector3[] _sites = [];
    uint? _warTick, _objectivesTick, _scoreOwnTick, _scoreOthersTick, _scoreHistoryTick, _barsTick, _arsenalTick;
    double _now;
    readonly Dictionary<(TfwFireKind, uint, ushort), double> _firePlayed = new();
    readonly List<TfwFireEvent> _newFire = new();

    /// <summary>Fire records seen for the first time since the last <see cref="TakeNew"/> (each once), in arrival order.</summary>
    public IReadOnlyList<TfwFireEvent> NewFire => _newFire;
    /// <summary>The newest score parts heard (tag 12); null or empty before the first.</summary>
    public TfwScoreOwn? ScoreOwn { get; private set; }
    public IReadOnlyList<TfwScorePlayer> ScoreOthers { get; private set; } = [];
    public IReadOnlyList<TfwMapScore> ScoreHistory { get; private set; } = [];
    /// <summary>The newest elite bars heard (tag 13).</summary>
    public IReadOnlyList<TfwBar> Bars { get; private set; } = [];
    /// <summary>The newest reward offer heard (tag 14); null before the first.</summary>
    public TfwRewardOffer? Reward { get; private set; }
    /// <summary>The server tick of the last copy of <see cref="Reward"/>: a standing offer repeats every second, one no longer heard is gone.</summary>
    public uint RewardTick { get; private set; }
    /// <summary>The newest extra slots heard (tag 15).</summary>
    public TfwArsenalView Arsenal { get; private set; } = TfwArsenalView.Empty;

    /// <summary>The newest war heard; null before the first.</summary>
    public TfwWarView? War { get; private set; }
    /// <summary>The newest objectives heard (empty outside a battle).</summary>
    public IReadOnlyList<TfwObjective> Objectives => _objectives;
    /// <summary>The battle's sites as tag 8 gave them: the landing zone, then the Ork sources (empty outside a battle).</summary>
    public IReadOnlyList<Vector3> Sites => _sites;
    /// <summary>The commands on their way; tag 7's echo answers them.</summary>
    public TfwCommandSender Commands { get; } = new();
    /// <summary>Guardsmen known now.</summary>
    public int GuardCount => _tracks.Count;
    public int SectionsRead { get; private set; }
    public int SectionsMalformed { get; private set; }
    /// <summary>Sections ignored as older than the one shown (late or reordered datagrams), and records older than a man's death.</summary>
    public int StaleSections { get; private set; }
    public int StaleRecords { get; private set; }
    public int EventsPlayed { get; private set; }
    public int EventCopies { get; private set; }
    public int Restarts { get; private set; }
    /// <summary>The newest server tick of any section read.</summary>
    public uint NewestTick { get; private set; }
    /// <summary>Events seen for the first time since the last <see cref="TakeNew"/> (each once), in arrival order.</summary>
    public IReadOnlyList<TfwGuardEvent> NewEvents => _newEvents;

    /// <summary>Clears <see cref="NewEvents"/> (the view took them).</summary>
    public void TakeNew() { _newEvents.Clear(); _newFire.Clear(); }

    /// <summary>
    /// Reads one section (the war's tags) at local time <paramref name="now"/> (seconds); false for a malformed one. <paramref name="box"/>: the current director's box (<c>MusouClientState.Director</c>,
    /// <see cref="MusouBox.Sane"/>; null: none yet, tags 8 to 10 wait). Tag 7 needs none.
    /// </summary>
    public bool Apply(in ExtrasSection section, MusouBox? box, double now)
    {
        _now = Math.Max(_now, now);
        if (NewestTick > RestartTicks && section.ServerTick + RestartTicks < NewestTick)
        {
            _tracks.Clear(); _played.Clear(); _dead.Clear();
            NewestTick = section.ServerTick;
            _warTick = _objectivesTick = null;
            ForgetBoxless();
            Restarts++;
        }
        if (box is { } known && _pending.Count > 0)
        {
            var held = _pending.ToArray();
            _pending.Clear();
            foreach (var s in held) Read(s, known);
        }
        if (section.Tag == TfwWire.WarTag) return ReadWar(section);
        if (section.Tag is >= TfwWire.FireTag and <= TfwWire.ArsenalTag) return ReadBoxless(section);
        if (box is { } ready) return Read(section, ready);

        // No box yet: hold it (the oldest goes first when full), after the length checks.
        var any = new MusouBox(default, Vector3.One);
        bool shaped = section.Tag switch
        {
            TfwWire.ObjectivesTag => TfwWire.ReadObjectives(section.Payload, any) is not null,
            TfwWire.GuardsTag => TfwWire.ReadGuards(section.Payload, any) is not null,
            TfwWire.GuardEventsTag => TfwWire.ReadGuardEvents(section.Payload, any) is not null,
            _ => false,
        };
        if (!shaped) { SectionsMalformed++; return false; }
        if (_pending.Count >= MaxPending) _pending.RemoveAt(0);
        _pending.Add(section);
        return true;
    }

    bool ReadWar(in ExtrasSection section)
    {
        if (TfwWire.ReadWar(section.Payload) is not { } war) { SectionsMalformed++; return false; }
        Heard(section);
        if (_warTick is { } shown && section.ServerTick < shown) { StaleSections++; return true; }
        _warTick = section.ServerTick;
        War = war;
        Commands.Acknowledge(war.CommandEcho);
        return true;
    }

    /// <summary>Tags 11 to 14 (pass 2): read and kept for the views, which come with their milestones.</summary>
    bool ReadBoxless(in ExtrasSection section)
    {
        switch (section.Tag)
        {
            case TfwWire.FireTag:
                if (TfwWire.ReadFire(section.Payload) is not { } fire) break;
                Heard(section);
                foreach (var f in fire)
                {
                    var key = (f.Kind, f.Tick, f.Subject);
                    if (_firePlayed.ContainsKey(key)) { EventCopies++; continue; }
                    _firePlayed[key] = _now;
                    _newFire.Add(f);
                }
                return true;
            case TfwWire.ScoreTag:
                if (TfwWire.ReadScore(section.Payload) is not { } score) break;
                Heard(section);
                if (score.Own is { } own && !Stale(ref _scoreOwnTick, section.ServerTick)) ScoreOwn = own;
                if (score.Others is { } others && !Stale(ref _scoreOthersTick, section.ServerTick)) ScoreOthers = others;
                if (score.History is { } history && !Stale(ref _scoreHistoryTick, section.ServerTick)) ScoreHistory = history;
                return true;
            case TfwWire.BarsTag:
                if (TfwWire.ReadBars(section.Payload) is not { } bars) break;
                Heard(section);
                if (!Stale(ref _barsTick, section.ServerTick)) Bars = bars;
                return true;
            case TfwWire.RewardTag:
                if (TfwWire.ReadReward(section.Payload) is not { } offer) break;
                Heard(section);
                if (Reward is { } held && held.Sequence == offer.Sequence) { RewardTick = Math.Max(RewardTick, section.ServerTick); return true; }
                if (Reward is { } older && !TfwWire.IsNewer(offer.Sequence, older.Sequence)) { StaleSections++; return true; }
                Reward = offer;
                RewardTick = section.ServerTick;
                return true;
            case TfwWire.ArsenalTag:
                if (TfwWire.ReadArsenal(section.Payload) is not { } arsenal) break;
                Heard(section);
                if (!Stale(ref _arsenalTick, section.ServerTick)) Arsenal = arsenal;
                return true;
        }
        SectionsMalformed++;
        return false;
    }

    /// <summary>Whether a section is older than the one shown of its kind; else it becomes the newest.</summary>
    bool Stale(ref uint? shown, uint tick)
    {
        if (shown is { } newest && tick < newest) { StaleSections++; return true; }
        shown = tick;
        return false;
    }

    void ForgetBoxless()
    {
        _firePlayed.Clear(); _newFire.Clear();
        _scoreOwnTick = _scoreOthersTick = _scoreHistoryTick = _barsTick = _arsenalTick = null;
        ScoreOwn = null; ScoreOthers = []; ScoreHistory = []; Bars = []; Reward = null; RewardTick = 0; Arsenal = TfwArsenalView.Empty;
    }

    bool Read(in ExtrasSection section, MusouBox box)
    {
        switch (section.Tag)
        {
            case TfwWire.ObjectivesTag:
                if (TfwWire.ReadObjectives(section.Payload, box) is not { } objectives) break;
                Heard(section);
                if (_objectivesTick is { } shown && section.ServerTick < shown) { StaleSections++; return true; }
                _objectivesTick = section.ServerTick;
                _objectives = objectives;
                _sites = TfwWire.ReadSites(section.Payload, box);
                return true;
            case TfwWire.GuardsTag:
                if (TfwWire.ReadGuards(section.Payload, box) is not { } guards) break;
                Heard(section);
                foreach (var g in guards) Record(g, section.ServerTick);
                return true;
            case TfwWire.GuardEventsTag:
                if (TfwWire.ReadGuardEvents(section.Payload, box) is not { } events) break;
                Heard(section);
                foreach (var e in events) Event(e);
                return true;
        }
        SectionsMalformed++;
        return false;
    }

    void Heard(in ExtrasSection section)
    {
        SectionsRead++;
        NewestTick = Math.Max(NewestTick, section.ServerTick);
    }

    void Record(in TfwGuard g, uint tick)
    {
        if (_dead.TryGetValue(g.Id, out var death))
        {
            if (tick <= death.Tick) { StaleRecords++; return; }
            _dead.Remove(g.Id);   // a later record: a new man in that slot
        }
        if (!_tracks.TryGetValue(g.Id, out var t)) _tracks[g.Id] = t = new Track { Id = g.Id };
        t.Heard = _now;
        // Insert by tick (newest last); a duplicate tick replaces; older than the oldest kept is dropped when full.
        int at = t.Count;
        while (at > 0 && t.Ticks[at - 1] > tick) at--;
        if (at > 0 && t.Ticks[at - 1] == tick) { Set(t, at - 1, tick, g); return; }
        if (t.Count == History)
        {
            if (at == 0) return;
            for (int k = 1; k < at; k++) Copy(t, k, k - 1);
            at--;
        }
        else
        {
            for (int k = t.Count; k > at; k--) Copy(t, k - 1, k);
            t.Count++;
        }
        Set(t, at, tick, g);
    }

    static void Set(Track t, int k, uint tick, in TfwGuard g) { t.Ticks[k] = tick; t.Positions[k] = g.Position; t.Yaws[k] = g.Yaw; t.States[k] = g.State; }
    static void Copy(Track t, int from, int to) { t.Ticks[to] = t.Ticks[from]; t.Positions[to] = t.Positions[from]; t.Yaws[to] = t.Yaws[from]; t.States[to] = t.States[from]; }

    void Event(in TfwGuardEvent e)
    {
        var key = (e.Kind, e.Tick, e.Guard, e.Enemy);
        if (_played.ContainsKey(key)) { EventCopies++; return; }
        _played[key] = _now;
        EventsPlayed++;
        _newEvents.Add(e);
        if (e.Kind != TfwGuardEventKind.Death) return;
        _dead[e.Guard] = (e.Tick, _now);
        if (_tracks.TryGetValue(e.Guard, out var t) && (t.Count == 0 || t.Ticks[0] <= e.Tick)) _tracks.Remove(e.Guard);   // a late copy of an old death leaves a newer man alone
    }

    /// <summary>
    /// The Guardsmen at <paramref name="renderTick"/> (fractional server ticks) at local time <paramref name="now"/>, by id: each interpolated between his records around the tick (held at the newest past it,
    /// the oldest before it); ids silent for <see cref="SilenceSeconds"/> are dropped first. The list is reused by the next call.
    /// </summary>
    public IReadOnlyList<TfwGuardShown> Sample(double renderTick, double now)
    {
        _now = Math.Max(_now, now);
        foreach (ushort id in _tracks.Where(t => _now - t.Value.Heard > SilenceSeconds).Select(t => t.Key).ToArray()) _tracks.Remove(id);
        foreach (var key in _played.Where(p => _now - p.Value > MemorySeconds).Select(p => p.Key).ToArray()) _played.Remove(key);
        foreach (ushort id in _dead.Where(d => _now - d.Value.At > MemorySeconds).Select(d => d.Key).ToArray()) _dead.Remove(id);
        foreach (var key in _firePlayed.Where(p => _now - p.Value > MemorySeconds).Select(p => p.Key).ToArray()) _firePlayed.Remove(key);

        _shown.Clear();
        foreach (var t in _tracks.Values)
        {
            if (t.Count == 0) continue;
            int after = 0;
            while (after < t.Count && t.Ticks[after] < renderTick) after++;
            if (after == 0 || after == t.Count)
            {
                int only = after == 0 ? 0 : t.Count - 1;
                _shown.Add(new TfwGuardShown(t.Id, t.Positions[only], t.Yaws[only], t.States[only]));
                continue;
            }
            int before = after - 1;
            float alpha = Math.Clamp((float)((renderTick - t.Ticks[before]) / Math.Max(1.0, t.Ticks[after] - t.Ticks[before])), 0f, 1f);
            float turn = MathF.IEEERemainder(t.Yaws[after] - t.Yaws[before], 2f * MathF.PI);
            _shown.Add(new TfwGuardShown(t.Id, Vector3.Lerp(t.Positions[before], t.Positions[after], alpha), t.Yaws[before] + turn * alpha, alpha < 1f ? t.States[before] : t.States[after]));
        }
        _shown.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return _shown;
    }

    /// <summary>Forgets everything (the match left the war, or the view was rebuilt).</summary>
    public void Clear()
    {
        _tracks.Clear(); _played.Clear(); _dead.Clear(); _pending.Clear(); _newEvents.Clear();
        _objectives = [];
        _warTick = _objectivesTick = null;
        ForgetBoxless();
        War = null;
        NewestTick = 0;
        Commands.Clear();
    }
}
