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
    /// <summary>Test aid (client <c>--musou-old-crowd-motion</c>): the crowd's drawn motion before 2026-11-08 (crowd-smooth).</summary>
    public bool OldCrowdMotion { get; set; }

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
        public DrawnMotion Motion;   // crowd-smooth: the drawn place and facing (OldCrowdMotion: Drawn above)
    }

    /// <summary>A Stormboy's hop as its cue told it (crowd-smooth): the tick it took off, its flight in ticks and where it lands.</summary>
    readonly record struct Hop(uint Start, int Ticks, Vector3 To);
    readonly Dictionary<ushort, Hop> _hops = new();
    /// <summary>The server's tick rate (the hop cue of an older server carries no flight; a full hop's is assumed at it).</summary>
    public int TickRate { get; set; } = 30;
    /// <summary>Hops known now (test aid).</summary>
    public int HopsKnown => _hops.Count;

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
            _tracks.Clear(); _played.Clear(); _dead.Clear(); _hops.Clear();
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
            t.Id = e.Id; t.Count = 0; t.ShownState = e.State; t.StateSince = _now; t.HasDrawn = false; t.Motion.Has = false;
            _tracks[e.Id] = t;
        }
        else if (t.Type != e.Type || t.Count > 0 && (tick > t.Ticks[t.Count - 1] + NewEnemyGapTicks
                                                     || e.State == MusouEnemyState.Spawning && t.States[t.Count - 1] != MusouEnemyState.Spawning && tick > t.Ticks[t.Count - 1]))
        {
            // Another kind on the same id, a long gap or a fresh spawn (the pool was cleared without deaths and the id reused): a new enemy, not a streak across the map.
            t.Count = 0; t.ShownState = e.State; t.StateSince = _now; t.HasDrawn = false; t.Motion.Has = false;
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
        if (e.Kind == MusouEventKind.Telegraph && e.Value == MusouStormboy.HopCue) { _hops[e.Enemy] = new Hop(e.Tick, MusouStormboy.HopTicksOf(e.Player, TickRate), e.Vector); return; }
        if (e.Kind != MusouEventKind.Death) return;
        _dead[e.Enemy] = (e.Tick, _now);
        _hops.Remove(e.Enemy);
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
        foreach (var id in _scratch) { _spare.Push(_tracks[id]); _tracks.Remove(id); _hops.Remove(id); DroppedSilent++; }
        _scratch.Clear();
        foreach (var (id, h) in _hops) if (renderTick > h.Start + h.Ticks + NewEnemyGapTicks || h.Start > renderTick + RestartTicks) _scratch.Add(id);
        foreach (var id in _scratch) _hops.Remove(id);
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
                // crowd-smooth: also a runner whose last step started from a stand (a fight, a stagger; not a landing, whose last step was in the air), and an airborne Stormboy whose hop cue is not known yet.
                if (last >= 1 && (state == t.States[last - 1] || !OldCrowdMotion && state is MusouEnemyState.Seek && t.States[last - 1] is not (MusouEnemyState.Airborne or MusouEnemyState.Spawning or MusouEnemyState.Dying))
                    && (state is MusouEnemyState.Seek || !OldCrowdMotion && state is MusouEnemyState.Airborne)
                    && (OldFarMotion ? step <= ExtrapolateTicks : step <= MaxExtrapolateTicks))
                {
                    float span = step;
                    int most = OldFarMotion ? ExtrapolateTicks : Math.Clamp((int)step + ExtrapolateMarginTicks, ExtrapolateTicks, MaxExtrapolateTicks);
                    float ahead = (float)Math.Min(renderTick - t.Ticks[last], most);
                    var perTick = (t.Positions[last] - t.Positions[last - 1]) / span;
                    p += perTick * ahead;
                    if (ahead < most) velocity = perTick;
                }
                else if (!OldCrowdMotion && state is MusouEnemyState.Seek && last >= 1 && t.States[last - 1] is MusouEnemyState.Airborne && _hops.TryGetValue(t.Id, out var landed)
                         && t.Ticks[last] > landed.Start + (uint)landed.Ticks && CrowdMotionRules.CarryFor(t.Ticks[last] - landed.Start - (uint)landed.Ticks) is var carry and > 0)
                {
                    // crowd-smooth: his first record from the ground after a hop: he has run from the landing since it, so he runs on at that speed (not a stand until a second one).
                    float since = t.Ticks[last] - landed.Start - (uint)landed.Ticks;
                    var perTick = (t.Positions[last] - landed.To) / since;
                    float ahead = (float)Math.Min(renderTick - t.Ticks[last], carry);
                    p += perTick * ahead;
                    if (ahead < carry) velocity = perTick;
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
            if (OldCrowdMotion) p = Smooth(t, p, velocity, state, renderTick);
            else
            {
                // crowd-smooth: a Stormboy in a hop whose cue is known is drawn on the hop's own arc, between and past his records; then every state but a fresh spawn glides (place and facing).
                double back = t.Motion.Has && renderTick > t.Motion.Tick && renderTick - t.Motion.Tick <= CrowdMotionRules.MaxStepTicks ? renderTick - t.Motion.Tick : 0.5;
                if (ArcOf(t, renderTick, back, out var arc, out var arcVelocity, out float arcYaw) is var hop and > 0)
                {
                    p = arc; velocity = arcVelocity; yaw = arcYaw;
                    state = hop == 1 ? MusouEnemyState.Airborne : MusouEnemyState.Seek;   // 2: landed, no record from the ground yet
                }
                bool glide = state != MusouEnemyState.Spawning && t.ShownState != MusouEnemyState.Spawning;
                (p, yaw) = t.Motion.Step(p, velocity, yaw, renderTick, glide);
            }
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
        _tracks.Clear(); _hops.Clear();
    }

    /// <summary>Forgets everything (the match left Musou, or the view was rebuilt).</summary>
    public void Clear()
    {
        _directorTick = null;
        foreach (var t in _tracks.Values) _spare.Push(t);
        _tracks.Clear(); _played.Clear(); _dead.Clear(); _pending.Clear(); _newEvents.Clear(); _newDeaths.Clear(); _hops.Clear();
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

    /// <summary>
    /// crowd-smooth: where a Stormboy whose hop cue is known is at <paramref name="renderTick"/> within the flight, with his velocity (per tick, the secant over the last <paramref name="back"/> ticks) and facing: 1;
    /// 2 when he has landed and no record from the ground has come yet (on the landing, still); 0 outside the hop or without a cue. The hop is the
    /// pool's own arc (<c>MusouSim.ArcAt</c>: the straight line from take-off to landing plus 4 x <see cref="MusouStormboy.ApexHeight"/> x u(1 - u), u the share of the flight), so between any two of its
    /// places the arc is their chord plus 4 x apex x (u - ua)(ub - u): drawn exactly from the record at or before the tick to the next record in the flight, or to the landing past the newest.
    /// Before his first record in the flight, the take-off is solved from that record, or carried on from his run.
    /// </summary>
    int ArcOf(Track t, double renderTick, double back, out Vector3 place, out Vector3 velocity, out float yaw)
    {
        place = velocity = default; yaw = 0f;
        if (t.Count == 0 || t.Type != MusouEnemyType.Stormboy || !_hops.TryGetValue(t.Id, out var h)) return 0;
        double end = h.Start + (double)h.Ticks;
        if (renderTick < h.Start) return 0;
        if (renderTick >= end)
        {
            // Landed, but his newest record is still from the flight: he stands on the landing (carrying the flight on would draw him under the ground and past it).
            if (t.Ticks[t.Count - 1] >= end || renderTick > end + NewEnemyGapTicks) return 0;
            place = h.To; yaw = t.Yaws[t.Count - 1];
            return 2;
        }
        place = ArcAt(t, h, renderTick, out yaw);
        velocity = (place - ArcAt(t, h, Math.Max(h.Start, renderTick - back), out _)) / (float)Math.Max(1e-3, renderTick - Math.Max(h.Start, renderTick - back));   // the secant over this frame's step: the glide's guess then lies on the arc
        return 1;
    }

    static Vector3 ArcAt(Track t, in Hop h, double r, out float yaw)
    {
        double end = h.Start + (double)h.Ticks;
        int a = -1, b = -1, firstIn = -1;
        for (int k = 0; k < t.Count; k++)
        {
            double tick = t.Ticks[k];
            if (tick <= r) a = k;
            else if (b < 0 && tick <= end) b = k;
            if (firstIn < 0 && tick > h.Start && tick < end) firstIn = k;
        }
        double span = Math.Max(1, h.Ticks);
        uint start = h.Start;
        float U(double tick) => (float)Math.Clamp((tick - start) / span, 0.0, 1.0);
        double ta; Vector3 pa;
        if (a >= 0 && t.Ticks[a] >= h.Start) { ta = t.Ticks[a]; pa = t.Positions[a]; }
        else
        {
            // The take-off: solved from his first place in the flight (P = F + (To - F) u + bump), else his run carried on to the start, else his last place.
            ta = h.Start;
            float uf = firstIn >= 0 ? U(t.Ticks[firstIn]) : 1f;
            if (firstIn >= 0 && uf < 0.9f) pa = (t.Positions[firstIn] - new Vector3(0f, 0f, 4f * MusouStormboy.ApexHeight * uf * (1f - uf)) - h.To * uf) / (1f - uf);
            else if (a >= 1 && t.Ticks[a] > t.Ticks[a - 1] && h.Start - t.Ticks[a] <= CrowdMotionRules.MaxCarryTicks)
                pa = t.Positions[a] + (t.Positions[a] - t.Positions[a - 1]) / (t.Ticks[a] - t.Ticks[a - 1]) * (h.Start - t.Ticks[a]);
            else pa = a >= 0 ? t.Positions[a] : t.Positions[0];
        }
        double tb; Vector3 pb;
        if (b >= 0) { tb = t.Ticks[b]; pb = t.Positions[b]; } else { tb = end; pb = h.To; }
        float alpha = tb > ta ? (float)Math.Clamp((r - ta) / (tb - ta), 0.0, 1.0) : 1f;
        float ua = U(ta), ub = U(tb), u = Math.Clamp(U(r), ua, ub);
        var across = new Vector2(h.To.X - pa.X, h.To.Y - pa.Y);
        yaw = firstIn >= 0 ? t.Yaws[firstIn] : across.LengthSquared() > 1e-6f ? MathF.Atan2(-across.X, across.Y) : t.Yaws[Math.Max(a, 0)];
        return Vector3.Lerp(pa, pb, alpha) + new Vector3(0f, 0f, 4f * MusouStormboy.ApexHeight * (u - ua) * (ub - u));
    }

    static float LerpAngle(float a, float b, float t)
    {
        float d = MathF.IEEERemainder(b - a, 2f * MathF.PI);
        return a + d * t;
    }
}
