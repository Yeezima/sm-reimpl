using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>An enemy as a client shows it at one render tick: interpolated place and facing, its kind, its state and how long it has been in it (s).</summary>
public readonly record struct MusouShown(ushort Id, Vector3 Position, float Yaw, MusouEnemyType Type, MusouEnemyState State, float StateSeconds);

/// <summary>A death a client has to show: the enemy, its kind and last place and facing, the push (a direction), the cause and the killer.</summary>
public readonly record struct MusouDeathShown(ushort Id, MusouEnemyType Type, Vector3 Position, float Yaw, Vector3 Push, MusouDeathKind Cause, ushort Killer, uint Tick);

/// <summary>
/// The Musou crowd on a client (design sections C and D), engine-free: the routed sections are read (<see cref="Apply"/>), each enemy keeps its last few
/// records by server tick and is interpolated at the render tick (<see cref="Sample"/>); an id is dropped after its death event or after
/// <see cref="SilenceSeconds"/> without a record. Entities and events wait (up to <see cref="MaxPending"/> sections) until a director has given the box.
/// Each event is played once by (kind, tick, enemy, player); a record older than its enemy's death is ignored (a late datagram cannot raise the dead).
/// </summary>
public sealed class MusouClientState
{
    /// <summary>An id without a record for this long is dropped (design: 2 s).</summary>
    public const double SilenceSeconds = 2.0;
    /// <summary>Sections held before the first director (ours).</summary>
    public const int MaxPending = 64;
    /// <summary>Records kept per enemy (ours: enough to bridge a far enemy's 5-tick gaps behind the 100 ms interpolation delay).</summary>
    public const int History = 4;
    /// <summary>How long an event's identity and a death's tick are remembered (ours: past the 3-message repeat and any late copy).</summary>
    public const double MemorySeconds = 5.0;
    /// <summary>A record this many ticks after an enemy's newest one starts a new enemy on that id (ours: 1 s; a far enemy is sent every 5 ticks).</summary>
    public const uint NewEnemyGapTicks = 30;
    /// <summary>A section this many ticks older than the newest seen means the server restarted its clock (ours: 10 s; no datagram is that late).</summary>
    public const uint RestartTicks = 300;
    /// <summary>
    /// Least ticks an enemy that runs is carried on past its newest record (ours: a far enemy's 5-tick gap plus one); beyond it, its last step's span plus
    /// <see cref="ExtrapolateMarginTicks"/>, at most <see cref="MaxExtrapolateTicks"/> (2026-11-06: a crowd that fills the budget sends a far Ork every 13 ticks
    /// at 1800 Orks, and he stood still between). <see cref="OldFarMotion"/>: 6 ticks, and only after a step of at most 6.
    /// </summary>
    public const int ExtrapolateTicks = 6, ExtrapolateMarginTicks = 3, MaxExtrapolateTicks = (int)NewEnemyGapTicks;
    /// <summary>
    /// The drawn place of a running enemy moves on at the sampled speed and closes on the sampled place with this time constant (ticks; ours: 0.2 s), so a new
    /// record that corrects a carried-on runner is a quick glide, not a jump; further off than <see cref="SnapMetres"/>, after a gap of more than
    /// <see cref="SmoothMaxStepTicks"/> between two samples, or in any other state it is the sampled place (ours).
    /// </summary>
    public const float SmoothTicks = 6f, SnapMetres = 4f, SmoothMaxStepTicks = 2f;

    sealed class Track
    {
        public ushort Id;
        public MusouEnemyType Type;
        public readonly uint[] Ticks = new uint[History];
        public readonly Vector3[] Positions = new Vector3[History];
        public readonly float[] Yaws = new float[History];
        public readonly MusouEnemyState[] States = new MusouEnemyState[History];
        public int Count;
        public double Heard;
        public MusouEnemyState ShownState;
        public double StateSince;
        public bool HasDrawn;      // the place drawn last sample (the smoothing's), at that render tick
        public Vector3 Drawn;
        public double DrawnTick;
    }

    readonly Dictionary<ushort, Track> _tracks = new();
    readonly Stack<Track> _spare = new();
    readonly Dictionary<(MusouEventKind, uint, ushort, ushort), double> _played = new();
    readonly Dictionary<ushort, (uint Tick, double At)> _dead = new();
    readonly List<ExtrasSection> _pending = new();
    readonly List<MusouEvent> _newEvents = new();
    readonly List<MusouDeathShown> _newDeaths = new();
    readonly List<MusouShown> _shown = new();
    readonly List<ushort> _scratch = new();
    readonly List<(MusouEventKind, uint, ushort, ushort)> _scratchKeys = new();
    double _now;

    /// <summary>Test aid (client <c>--musou-old-far-motion</c>): the far crowd's motion before 2026-11-06.</summary>
    public bool OldFarMotion { get; set; }

    public MusouDirectorView? Director { get; private set; }
    /// <summary>Directors ignored as older than the one shown (late or reordered datagrams).</summary>
    public int StaleDirectors { get; private set; }
    uint? _directorTick;
    /// <summary>Enemies known now.</summary>
    public int Count => _tracks.Count;
    /// <summary>The kind of enemy <paramref name="id"/> as its newest record has it; null when the client holds no track of it (the Boss Orks' HUD, 2026-11-06).</summary>
    public MusouEnemyType? TypeOf(ushort id) => _tracks.TryGetValue(id, out var t) ? t.Type : null;
    /// <summary>The server tick of enemy <paramref name="id"/>'s newest record (null: no track; the motion log's test aid).</summary>
    public uint? NewestRecordTick(ushort id) => _tracks.TryGetValue(id, out var t) && t.Count > 0 ? t.Ticks[t.Count - 1] : null;
    public int SectionsRead { get; private set; }
    public int SectionsMalformed { get; private set; }
    /// <summary>Ids dropped for silence (no death seen), and records ignored as older than a death.</summary>
    public int DroppedSilent { get; private set; }
    public int StaleRecords { get; private set; }
    /// <summary>Events played (new), and copies ignored as already played.</summary>
    public int EventsPlayed { get; private set; }
    public int EventCopies { get; private set; }
    /// <summary>New enemies found on a known id (a reused id without a death seen), and server clock restarts seen.</summary>
    public int NewOnSameId { get; private set; }
    public int Restarts { get; private set; }
    /// <summary>The newest server tick of any section read.</summary>
    public uint NewestTick { get; private set; }

    /// <summary>Events seen for the first time since the last <see cref="TakeNew"/> (each once), in arrival order; deaths also come with the enemy's last pose.</summary>
    public IReadOnlyList<MusouEvent> NewEvents => _newEvents;
    public IReadOnlyList<MusouDeathShown> NewDeaths => _newDeaths;

    /// <summary>Test aid: the middle of the enemies known within <paramref name="within"/> of <paramref name="from"/> (null: none), at their newest records.</summary>
    public Vector3? MiddleNear(Vector3 from, float within)
    {
        var sum = Vector3.Zero;
        int n = 0;
        foreach (var track in _tracks.Values)
        {
            if (track.Count == 0) continue;
            var p = track.Positions[(track.Count - 1) % History];
            if (Vector3.DistanceSquared(p, from) > within * within) continue;
            sum += p; n++;
        }
        return n > 0 ? sum / n : null;
    }

    /// <summary>The enemies known within <paramref name="radius"/> of <paramref name="centre"/> (flat) and <paramref name="vertical"/> above or below it, at their newest records: what a capture position still has standing on it, as far as this client knows.</summary>
    public int CountWithin(Vector3 centre, float radius, float vertical)
    {
        int n = 0;
        float r2 = radius * radius;
        foreach (var track in _tracks.Values)
        {
            if (track.Count == 0) continue;
            var p = track.Positions[(track.Count - 1) % History];
            float dx = p.X - centre.X, dy = p.Y - centre.Y;
            if (dx * dx + dy * dy <= r2 && MathF.Abs(p.Z - centre.Z) <= vertical) n++;
        }
        return n;
    }

    /// <summary>Test aid: the enemy known nearest to <paramref name="from"/> within <paramref name="within"/> (null: none), at its newest record.</summary>
    public Vector3? NearestTo(Vector3 from, float within)
    {
        Vector3? best = null;
        float bestD = within * within;
        foreach (var track in _tracks.Values)
        {
            if (track.Count == 0) continue;
            var p = track.Positions[(track.Count - 1) % History];
            float d = Vector3.DistanceSquared(p, from);
            if (d <= bestD) { bestD = d; best = p; }
        }
        return best;
    }

    /// <summary>
    /// musou/boss-collision: the solid big Orks (<see cref="MusouSolids"/>) as this client knows them at server tick <paramref name="tick"/>: each one's newest record at or before
    /// it, no older than <see cref="MusouSolids.MaxRecordAgeTicks"/>, in a state that counts; in id order, into <paramref name="into"/> (cleared first). The server meets the
    /// same Orks at that tick (<c>World.MusouSolidsAt</c>).
    /// </summary>
    public void SolidsAt(uint tick, List<MusouSolid> into)
    {
        into.Clear();
        foreach (var t in _tracks.Values)
        {
            if (!MusouSolids.Is(t.Type)) continue;
            int k = t.Count - 1;
            while (k >= 0 && t.Ticks[k] > tick) k--;
            if (k < 0 || tick - t.Ticks[k] > MusouSolids.MaxRecordAgeTicks || !MusouSolids.Counts(t.States[k])) continue;
            into.Add(new MusouSolid(t.Id, t.Type, t.Positions[k]));
        }
        into.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    /// <summary>Clears <see cref="NewEvents"/> and <see cref="NewDeaths"/> (the view took them).</summary>
    public void TakeNew() { _newEvents.Clear(); _newDeaths.Clear(); }

    /// <summary>Reads one section (Musou's tags) at local time <paramref name="now"/> (seconds); false for a malformed one.</summary>
    public bool Apply(in ExtrasSection section, double now)
    {
        if (section.Tag == MusouWire.DropsTag) return false;   // MU-D: the drops (tag 6) are the client's MusouDrops', not the crowd's: skipped, not malformed
        _now = Math.Max(_now, now);
        if (NewestTick > RestartTicks && section.ServerTick + RestartTicks < NewestTick)
        {
            // The server's tick went back (a restarted server on the same address): every track, death and event of the old clock is forgotten; the box stays.
            foreach (var t in _tracks.Values) _spare.Push(t);
            _tracks.Clear(); _played.Clear(); _dead.Clear();
            NewestTick = section.ServerTick;
            _directorTick = null;   // the new clock's first director is taken whatever its tick
            Restarts++;
        }
        if (section.Tag == MusouWire.DirectorTag)
        {
            var d = MusouWire.ReadDirector(section.Payload);
            if (d is null) { SectionsMalformed++; return false; }
            SectionsRead++;
            NewestTick = Math.Max(NewestTick, section.ServerTick);
            // A late or repeated datagram's director older than the one shown is ignored (it would turn a run back: a wave-2 director after wave 3, a
            // waiting or boss one after the win); one of the same tick is the same director (a copy). A restarted clock is the restart path above.
            if (Director is not null && _directorTick is { } shownTick && section.ServerTick < shownTick) { StaleDirectors++; return true; }
            _directorTick = section.ServerTick;
            bool first = Director is null;
            Director = d;
            if (first && _pending.Count > 0)
            {
                var held = _pending.ToArray();
                _pending.Clear();
                foreach (var s in held) Apply(s, now);
            }
            return true;
        }
        if (Director is not { } director)
        {
            // No box yet: hold it (the oldest goes first when full), after the length checks.
            bool shaped = section.Tag == MusouWire.EntitiesTag
                ? MusouWire.ReadEntities(section.Payload, new MusouBox(default, Vector3.One)) is not null
                : MusouWire.ReadEvents(section.Payload, new MusouBox(default, Vector3.One)) is not null;
            if (!shaped) { SectionsMalformed++; return false; }
            if (_pending.Count >= MaxPending) _pending.RemoveAt(0);
            _pending.Add(section);
            return true;
        }
        var box = director.Box.Sane();
        if (section.Tag == MusouWire.EntitiesTag)
        {
            var list = MusouWire.ReadEntities(section.Payload, box);
            if (list is null) { SectionsMalformed++; return false; }
            SectionsRead++;
            NewestTick = Math.Max(NewestTick, section.ServerTick);
            foreach (var e in list) Record(e, section.ServerTick);
            return true;
        }
        if (section.Tag == MusouWire.EventsTag)
        {
            var list = MusouWire.ReadEvents(section.Payload, box);
            if (list is null) { SectionsMalformed++; return false; }
            SectionsRead++;
            NewestTick = Math.Max(NewestTick, section.ServerTick);
            foreach (var e in list) Event(e);
            return true;
        }
        SectionsMalformed++;
        return false;
    }

    void Record(in MusouEntity e, uint tick)
    {
        if (_dead.TryGetValue(e.Id, out var death))
        {
            if (tick <= death.Tick) { StaleRecords++; return; }
            _dead.Remove(e.Id);   // a later record: the id was reused for a new enemy
        }
        if (!_tracks.TryGetValue(e.Id, out var t))
        {
            t = _spare.Count > 0 ? _spare.Pop() : new Track();
            t.Id = e.Id; t.Count = 0; t.ShownState = e.State; t.StateSince = _now; t.HasDrawn = false;
            _tracks[e.Id] = t;
        }
        else if (t.Type != e.Type || t.Count > 0 && (tick > t.Ticks[t.Count - 1] + NewEnemyGapTicks
                                                     || e.State == MusouEnemyState.Spawning && t.States[t.Count - 1] != MusouEnemyState.Spawning && tick > t.Ticks[t.Count - 1]))
        {
            // Another kind on the same id, a long gap or a fresh spawn (the pool was cleared without deaths and the id reused): a new enemy, not a streak across the map.
            t.Count = 0; t.ShownState = e.State; t.StateSince = _now; t.HasDrawn = false;
            NewOnSameId++;
        }
        t.Type = e.Type;
        t.Heard = _now;
        // Insert by tick (newest last); a duplicate tick replaces; older than the oldest kept is dropped when full.
        int at = t.Count;
        while (at > 0 && t.Ticks[at - 1] > tick) at--;
        if (at > 0 && t.Ticks[at - 1] == tick) { Set(t, at - 1, tick, e); return; }
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
        Set(t, at, tick, e);
    }

    static void Set(Track t, int k, uint tick, in MusouEntity e) { t.Ticks[k] = tick; t.Positions[k] = e.Position; t.Yaws[k] = e.Yaw; t.States[k] = e.State; }
    static void Copy(Track t, int from, int to) { t.Ticks[to] = t.Ticks[from]; t.Positions[to] = t.Positions[from]; t.Yaws[to] = t.Yaws[from]; t.States[to] = t.States[from]; }

    void Event(in MusouEvent e)
    {
        var key = (e.Kind, e.Tick, e.Enemy, e.Player);   // the player too: two shooters' hits on one enemy in one tick are two events
        if (_played.ContainsKey(key)) { EventCopies++; return; }
        _played[key] = _now;
        EventsPlayed++;
        _newEvents.Add(e);
        if (e.Kind != MusouEventKind.Death) return;
        _dead[e.Enemy] = (e.Tick, _now);
        // A late copy of an old death never removes a newer enemy on the reused id (its track starts after the death).
        if (_tracks.TryGetValue(e.Enemy, out var t) && (t.Count == 0 || t.Ticks[0] <= e.Tick))
        {
            _tracks.Remove(e.Enemy);
            int last = t.Count - 1;
            _newDeaths.Add(new MusouDeathShown(e.Enemy, t.Type, last >= 0 ? t.Positions[last] : default, last >= 0 ? t.Yaws[last] : 0f, e.Vector, (MusouDeathKind)e.Value, e.Player, e.Tick));
            _spare.Push(t);
        }
    }

    /// <summary>
    /// The enemies at <paramref name="renderTick"/> (fractional server ticks; the view's render clock) at local time <paramref name="now"/>: each interpolated
    /// between its records around the tick (held at the newest past it, the oldest before it); ids silent for <see cref="SilenceSeconds"/> are dropped first.
    /// The list is reused by the next call.
    /// </summary>
    public IReadOnlyList<MusouShown> Sample(double renderTick, double now)
    {
        _now = Math.Max(_now, now);
        _scratch.Clear();
        foreach (var (id, t) in _tracks) if (_now - t.Heard > SilenceSeconds) _scratch.Add(id);
        foreach (var id in _scratch) { _spare.Push(_tracks[id]); _tracks.Remove(id); DroppedSilent++; }
        _scratchKeys.Clear();
        foreach (var (key, at) in _played) if (_now - at > MemorySeconds) _scratchKeys.Add(key);
        foreach (var key in _scratchKeys) _played.Remove(key);
        _scratch.Clear();
        foreach (var (id, d) in _dead) if (_now - d.At > MemorySeconds) _scratch.Add(id);
        foreach (var id in _scratch) _dead.Remove(id);

        _shown.Clear();
        foreach (var t in _tracks.Values)
        {
            if (t.Count == 0) continue;
            Vector3 p, velocity = Vector3.Zero;   // velocity: the sampled place's, per tick
            float yaw; MusouEnemyState state;
            int after = 0;
            while (after < t.Count && t.Ticks[after] < renderTick) after++;
            if (after == 0) { p = t.Positions[0]; yaw = t.Yaws[0]; state = t.States[0]; }
            else if (after == t.Count)
            {
                int last = t.Count - 1;
                p = t.Positions[last]; yaw = t.Yaws[last]; state = t.States[last];
                // Past the newest record (a far enemy is sent every 5th tick, or rarer when the crowd fills the budget; the view runs 3 behind): carry on along
                // the last step for about one more such gap, not a stop-and-go.
                uint step = last >= 1 ? t.Ticks[last] - t.Ticks[last - 1] : 0;
                if (last >= 1 && state == t.States[last - 1] && state is MusouEnemyState.Seek
                    && (OldFarMotion ? step <= ExtrapolateTicks : step <= MaxExtrapolateTicks))
                {
                    float span = step;
                    int most = OldFarMotion ? ExtrapolateTicks : Math.Clamp((int)step + ExtrapolateMarginTicks, ExtrapolateTicks, MaxExtrapolateTicks);
                    float ahead = (float)Math.Min(renderTick - t.Ticks[last], most);
                    var perTick = (t.Positions[last] - t.Positions[last - 1]) / span;
                    p += perTick * ahead;
                    if (ahead < most) velocity = perTick;
                }
            }
            else
            {
                int before = after - 1;
                float span = Math.Max(1f, t.Ticks[after] - t.Ticks[before]);
                float alpha = (float)((renderTick - t.Ticks[before]) / span);
                alpha = Math.Clamp(alpha, 0f, 1f);
                p = Vector3.Lerp(t.Positions[before], t.Positions[after], alpha);
                yaw = LerpAngle(t.Yaws[before], t.Yaws[after], alpha);
                state = alpha < 1f ? t.States[before] : t.States[after];
                velocity = (t.Positions[after] - t.Positions[before]) / span;
            }
            p = Smooth(t, p, velocity, state, renderTick);
            if (state != t.ShownState) { t.ShownState = state; t.StateSince = _now; }
            if (t.Type == MusouEnemyType.BombSquig && state == MusouEnemyState.Spawning) p.Z += MusouSquigRules.FallHeightAt((float)(_now - t.StateSince));   // expansion S2: a dropped Squig is drawn falling
            _shown.Add(new MusouShown(t.Id, p, yaw, t.Type, state, (float)(_now - t.StateSince)));
        }
        _shown.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return _shown;
    }

    /// <summary>
    /// Forgets every enemy (a new run: the server cleared its pool without death events), keeping the director, the events not yet taken and what was
    /// already played or died (copies stay ignored).
    /// </summary>
    public void ForgetEnemies()
    {
        foreach (var t in _tracks.Values) _spare.Push(t);
        _tracks.Clear();
    }

    /// <summary>Forgets everything (the match left Musou, or the view was rebuilt).</summary>
    public void Clear()
    {
        _directorTick = null;
        foreach (var t in _tracks.Values) _spare.Push(t);
        _tracks.Clear(); _played.Clear(); _dead.Clear(); _pending.Clear(); _newEvents.Clear(); _newDeaths.Clear();
        Director = null;
    }

    /// <summary>
    /// The place to draw a track at <paramref name="renderTick"/>, given the sampled place <paramref name="sampled"/> moving at <paramref name="velocity"/> (per tick):
    /// for a runner seen last sample too, the last drawn place moved on at that speed and pulled toward the sampled one (<see cref="SmoothTicks"/>); else the
    /// sampled place (<see cref="SnapMetres"/>, <see cref="SmoothMaxStepTicks"/>, any state but running, a render clock gone back, <see cref="OldFarMotion"/>).
    /// </summary>
    Vector3 Smooth(Track t, Vector3 sampled, Vector3 velocity, MusouEnemyState state, double renderTick)
    {
        bool running = state is MusouEnemyState.Seek && t.ShownState is MusouEnemyState.Seek;
        double step = renderTick - t.DrawnTick;
        var drawn = sampled;
        if (!OldFarMotion && running && t.HasDrawn)
        {
            if (step == 0) return t.Drawn;   // a second sample at the same render tick draws the same place
            if (step > 0 && step <= SmoothMaxStepTicks)
            {
                var guess = t.Drawn + velocity * (float)step;
                var off = sampled - guess;
                if (off.LengthSquared() <= SnapMetres * SnapMetres) drawn = guess + off * (float)(1.0 - Math.Exp(-step / SmoothTicks));
            }
        }
        t.Drawn = drawn; t.DrawnTick = renderTick; t.HasDrawn = true;
        return drawn;
    }

    static float LerpAngle(float a, float b, float t)
    {
        float d = MathF.IEEERemainder(b - a, 2f * MathF.PI);
        return a + d * t;
    }
}
