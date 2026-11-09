using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The patrols and hunter columns of a battle (the owner, 2026-11-06: "there should be large formations of orks all around the map at all time ... patrols that both patrol on set routes and also hunt down the players no
/// matter where they are"; then, the same day: keep the number of Orks exactly as it is, the patrols and hunters come from the existing Orks). Every value is ours (spec "Musou Mode: patrols and hunters"). A change here
/// raises <see cref="TfwRules.Version"/>. Distances in metres, speeds in m/s, times in real seconds.
/// </summary>
public static class TfwPatrolRules
{
    /// <summary>Columns walking a loop of the battle's places, and columns walking down the flow field to the nearest player wherever he is.</summary>
    /// <remarks>Expansion S3 (owner brief 2026-11-07): the Stormboy squads replace the hunter columns (<see cref="TfwStormboyRules"/>), so <see cref="Hunters"/> is 0 (was 2); the hunter code stays for a later use.</remarks>
    public const int RoutePatrols = 4, Hunters = 0;
    /// <summary>Orks in one column (fodder only). They come out of the crowds' cap (<see cref="TfwBattleRules.AliveCap"/>): the map holds no more Orks than before, only some of them march instead of standing in a crowd.</summary>
    public const int Size = 32;   // 40 until 2026-11-09, then 80 % (the owner: "reduce the total amount of orks on the map to 80% of current")
    /// <summary>A route patrol's march and a hunter column's (both under every Ork's run speed, so the ranks keep up).</summary>
    public const float PatrolSpeed = 2.6f, HunterSpeed = 3.4f;
    /// <summary>A column's Orks notice a target this near their own places in the ranks; one that does wakes the whole column, which goes for the players as one pack.</summary>
    public const float Aggro = 25f;
    /// <summary>Ranks of this many abreast, this far apart (along the way, across it).</summary>
    public const int Abreast = 4;
    public const float RowSpacing = 1.6f, FileSpacing = 1.4f;
    /// <summary>A column woken or wiped out forms again this long after, at its source, if the source sends (a position Held, the HQ from the commander on; all four in the final assault) and the cap has room.</summary>
    public const float ReformSeconds = 30f;
    /// <summary>The column's head waits while its Orks are this far behind their places on average; one this far behind is let go alone (it hunts on its own).</summary>
    public const float WaitBehind = 4f, StragglerBehind = 20f;
    /// <summary>The longest a head waits without a break (s): then the ones more than <see cref="LateBehind"/> m behind are let go alone and the head marches on at <see cref="CreepFraction"/> of its pace, so no column stalls for good (ours).</summary>
    public const float MaxWaitSeconds = 2.5f, LateBehind = 9f, CreepFraction = 0.6f;
    /// <summary>A route patrol's first place on its loop is at least this far from the landing zone (the Stormbird lands among no column).</summary>
    public const float FromLanding = 45f;
    /// <summary>Nodes one route search may expand. The searches run off the tick from the battle's start; their ways are taken this long after it (the route patrols form then; the hunters at once).</summary>
    public const int RouteExpansions = 200_000;
    public const float RouteSeconds = 3f;
}

public sealed partial class TfwBattle
{
    /// <summary>A way the column's places are read off: points with their running length, a loop (a route) or a trail behind the head (a hunter's).</summary>
    sealed class Track(bool loop)
    {
        readonly List<Vector3> _points = new();
        readonly List<float> _length = new();
        public bool Loop { get; } = loop;
        public int Count => _points.Count;
        public float Length => _length.Count == 0 ? 0f : _length[^1];
        public Vector3 Last => _points[^1];

        public void Add(Vector3 p)
        {
            if (_points.Count > 0 && Vector3.DistanceSquared(_points[^1], p) < 1e-6f) return;
            _length.Add(_points.Count == 0 ? 0f : _length[^1] + Vector2.Distance(Flat(_points[^1]), Flat(p)));
            _points.Add(p);
        }

        /// <summary>Drops the trail's points more than <paramref name="keep"/> behind its end (a hunter's).</summary>
        public void Trim(float keep)
        {
            int drop = 0;
            while (drop < _points.Count - 2 && Length - _length[drop + 1] > keep) drop++;
            if (drop == 0) return;
            _points.RemoveRange(0, drop);
            float base0 = _length[drop];
            _length.RemoveRange(0, drop);
            for (int k = 0; k < _length.Count; k++) _length[k] -= base0;
        }

        /// <summary>The point at <paramref name="s"/> along the track (a loop wraps; a trail clamps to its ends) and the way's direction there (flat, unit).</summary>
        public (Vector3 At, Vector2 Way) Sample(float s)
        {
            if (_points.Count == 1) return (_points[0], Vector2.UnitY);
            float length = Length;
            if (Loop && length > 0f) { s %= length; if (s < 0f) s += length; }
            else s = Math.Clamp(s, 0f, length);
            int hi = _length.BinarySearch(s);
            if (hi < 0) hi = ~hi;
            hi = Math.Clamp(hi, 1, _points.Count - 1);
            int lo = hi - 1;
            float span = _length[hi] - _length[lo], t = span > 1e-5f ? (s - _length[lo]) / span : 0f;
            var way = Flat(_points[hi] - _points[lo]);
            float w = way.Length();
            return (Vector3.Lerp(_points[lo], _points[hi], t), w > 1e-5f ? way / w : Vector2.UnitY);
        }

        static Vector2 Flat(Vector3 v) => new(v.X, v.Y);
    }

    /// <summary>One column: a route patrol or a hunter, its Orks (front rank first), its group and where its head is.</summary>
    sealed class Column
    {
        public required int Index { get; init; }
        public required bool Hunter { get; init; }
        public required int Source { get; init; }
        public required int Group { get; init; }
        public required int[] Anchors { get; init; }   // a route's places in order (a loop); empty for a hunter
        public Track? Route;                            // a route patrol's loop, once found
        public bool RouteFailed;
        public Track? Trail;                            // a hunter's way behind its head
        public int HeadNode = -1;
        public float Head;                              // a route patrol's head along its loop
        public readonly List<ushort> Members = new();
        public bool Formed, EverFormed;
        public int ReformTicks;
        public bool Holding;
        public int HeldTicks;
        public bool Creeping;
    }

    const int PatrolGroupFirst = MusterGroupFirst + Positions + 1;   // after the musters' (9 to 12): 13 on
    Column[]? _columns;
    readonly Dictionary<(int, int), List<Vector3>?> _segments = new();

    /// <summary>Whether the battle sends patrols and hunter columns (ours; false only in tests that count the crowds alone, as <see cref="Trickle"/>).</summary>
    public bool Patrols { get; set; } = true;
    /// <summary>Test aid: called each time a column wakes (its index, a hunter or not, and its Orks).</summary>
    public Action<int, bool, ushort[]>? OnColumnWoke { get; set; }
    /// <summary>Route searches done so far and the longest one's wall-clock milliseconds (the cost report and the tests).</summary>
    public int RouteSearches { get; private set; }
    public double LongestRouteMilliseconds { get; private set; }
    /// <summary>Test aid: stragglers let go alone so far.</summary>
    public int StragglersReleased { get; private set; }

    /// <summary>Test aid: the columns as they are now: index, hunter, formed, Orks, the head's place, and whether the head is waiting for its ranks.</summary>
    public IReadOnlyList<(int Index, bool Hunter, bool Formed, ushort[] Ids, Vector3 Head, bool Holding)> Columns()
    {
        var list = new List<(int, bool, bool, ushort[], Vector3, bool)>();
        if (_columns is null) return list;
        foreach (var c in _columns) list.Add((c.Index, c.Hunter, c.Formed, c.Members.ToArray(), HeadOf(c), c.Holding));
        return list;
    }

    /// <summary>Test aid: a route patrol's loop as points about every <paramref name="step"/> m (empty: not found, or a hunter).</summary>
    public Vector3[] RouteOf(int index, float step = 2f)
    {
        if (_columns is null || index < 0 || index >= _columns.Length || _columns[index].Route is not { } route) return [];
        var at = new List<Vector3>();
        for (float s = 0f; s < route.Length; s += step) at.Add(route.Sample(s).At);
        return at.ToArray();
    }

    Vector3 HeadOf(Column c) => c.Hunter ? (c.Trail is { Count: > 0 } t ? t.Last : CrowdAnchor(c.Source)) : c.Route is { } r ? r.Sample(c.Head).At : CrowdAnchor(c.Source);

    /// <summary>A place of the battle by its index: 0 to 2 the positions, 3 the HQ, 4 on the sources.</summary>
    Vector3 Anchor(int a) => a < Positions ? _layout.Positions[a] : a == Positions ? _layout.Hq : _layout.Sources[Math.Min(a - Positions - 1, _layout.Sources.Length - 1)];

    /// <summary>The columns of this battle, with the route searches they need started (at the start).</summary>
    void PlanColumns()
    {
        if (!Patrols || _layout.Sources.Length == 0) return;
        int sources = _layout.Sources.Length, n = TfwPatrolRules.RoutePatrols + TfwPatrolRules.Hunters;
        _columns = new Column[n];
        var segments = new List<(int, int)>();
        for (int r = 0; r < TfwPatrolRules.RoutePatrols; r++)
        {
            int k = r % sources, source = Positions + 1 + k;
            int[] anchors = r % 2 == 0
                ? [source, (k + 1) % Positions, (k + 2) % Positions]   // the ring of the positions, from its source
                : [source, Positions, (k + 1) % Positions];            // by the HQ
            _columns[r] = new Column { Index = r, Hunter = false, Source = k, Group = PatrolGroupFirst + r, Anchors = anchors };
            for (int a = 0; a < anchors.Length; a++)
            {
                var segment = (anchors[a], anchors[(a + 1) % anchors.Length]);
                if (!segments.Contains(segment)) segments.Add(segment);
            }
        }
        for (int h = 0; h < TfwPatrolRules.Hunters; h++)
        {
            int index = TfwPatrolRules.RoutePatrols + h;
            _columns[index] = new Column { Index = index, Hunter = true, Source = (2 * h) % sources, Group = PatrolGroupFirst + index, Anchors = [] };
        }
        // The searches run off the tick (a search over the whole map takes up to a quarter of a second on a busy PC); their ways are taken at a fixed tick (RouteSeconds), so a battle goes the same however fast they ran.
        var ends = segments.Select(s => (s, Ground(Anchor(s.Item1), Anchor(s.Item1)), Ground(Anchor(s.Item2), Anchor(s.Item2)))).ToArray();
        var nav = _enemies.Nav;
        _routeTask = nav is null ? Task.FromResult(SearchAll(null, ends)) : Task.Run(() => SearchAll(nav, ends));
    }

    /// <summary>The ways of <paramref name="ends"/> in order (a way back along a found way with no drop is that way reversed); straight lines without a graph. Runs off the tick: it reads the graph only.</summary>
    static List<((int, int) Segment, List<Vector3>? Way, double Milliseconds)> SearchAll(NavGraph? nav, ((int, int) Segment, Vector3 From, Vector3 To)[] ends)
    {
        // musou/perf-pass: the searches that cannot be a way back run side by side (the graph's search scratch is per thread), so all of them are in long before RouteSeconds even on a busy
        // server (S6's open item: collected late, they stalled the tick); a way back is then taken in order as before, so the ways are the same as one search after the other.
        var ahead = new (List<Vector3>? Way, bool Reversible, double Ms)?[ends.Length];
        if (nav is not null)
        {
            var first = new HashSet<(int, int)>();
            var independent = new List<int>();
            for (int i = 0; i < ends.Length; i++)
            {
                if (!first.Contains((ends[i].Segment.Item2, ends[i].Segment.Item1))) independent.Add(i);
                first.Add(ends[i].Segment);
            }
            Parallel.ForEach(independent, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) }, i => ahead[i] = Search(nav, ends[i].From, ends[i].To));
        }
        var found = new List<((int, int), List<Vector3>?, double)>();
        var reversible = new Dictionary<(int, int), List<Vector3>>();
        for (int i = 0; i < ends.Length; i++)
        {
            var (segment, from, to) = ends[i];
            if (reversible.TryGetValue((segment.Item2, segment.Item1), out var back)) { var reversed = new List<Vector3>(back); reversed.Reverse(); found.Add((segment, reversed, 0.0)); continue; }
            if (nav is null) { found.Add((segment, [from, to], 0.0)); reversible[segment] = [from, to]; continue; }
            var (way, canReverse, ms) = ahead[i] ?? Search(nav, from, to);
            if (canReverse && way is not null) reversible[segment] = way;
            found.Add((segment, way, ms));
        }
        return found;
    }

    /// <summary>One route search: the way (null: none, partial or by a jump), whether it is walked all the way (so its reverse is a way back), and its wall-clock milliseconds.</summary>
    static (List<Vector3>? Way, bool Reversible, double Ms) Search(NavGraph nav, Vector3 from, Vector3 to)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        List<Vector3>? way = null;
        bool reversible = false;
        try
        {
            var path = nav.FindPath(from, to, 10f, TfwPatrolRules.RouteExpansions);
            if (path is { Partial: false, Waypoints.Count: >= 2 } && path.Waypoints.All(w => w.Kind != NavLinkKind.Jump))   // an Ork has no jump pack
            {
                way = path.Waypoints.Select(w => w.Position).ToList();
                reversible = path.Waypoints.All(w => w.Kind == NavLinkKind.Walk);
            }
        }
        catch (Exception) { way = null; }   // a failed search leaves its loop unwalked, never the battle
        return (way, reversible, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    Task<List<((int, int) Segment, List<Vector3>? Way, double Milliseconds)>>? _routeTask;
    int _routeTicks;

    /// <summary>At <see cref="TfwPatrolRules.RouteSeconds"/> after the start the searches' ways are taken (waiting for them there if they are not done) and each route patrol's loop is joined from them.</summary>
    void StepRoutes()
    {
        if (_routeTask is null) return;
        if (++_routeTicks < Ticks(TfwPatrolRules.RouteSeconds)) return;
        foreach (var (segment, way, ms) in _routeTask.Result)
        {
            _segments[segment] = way;
            if (ms > 0.0) { RouteSearches++; LongestRouteMilliseconds = Math.Max(LongestRouteMilliseconds, ms); }
        }
        _routeTask = null;
        foreach (var c in _columns!)
        {
            if (c.Hunter) continue;
            var route = new Track(loop: true);
            bool ready = true;
            for (int a = 0; a < c.Anchors.Length && ready; a++)
            {
                if (!_segments.TryGetValue((c.Anchors[a], c.Anchors[(a + 1) % c.Anchors.Length]), out var way) || way is null) { ready = false; break; }
                foreach (var p in way) route.Add(p);
            }
            if (ready && route.Length > TfwPatrolRules.Size / TfwPatrolRules.Abreast * TfwPatrolRules.RowSpacing * 2f) c.Route = route;
            else c.RouteFailed = true;
        }
    }

    /// <summary>Each tick, before the arrivals: the route searches, then every column: forgotten dead, woken as one, formed again, marched.</summary>
    void StepColumns(ReadOnlySpan<Vector3> players)
    {
        if (_columns is null) return;
        StepRoutes();
        foreach (var c in _columns)
        {
            if (c.Formed) TidyColumn(c);
            if (!c.Formed) { if (c.ReformTicks > 0) c.ReformTicks--; else TryForm(c); continue; }
            March(c, players);
        }
    }

    /// <summary>A column's dead are forgotten; one of it awake (a target came near its place, or one was hurt) wakes it all: it goes for the players as one pack, and forms again later.</summary>
    void TidyColumn(Column c)
    {
        bool woken = false;
        for (int n = c.Members.Count - 1; n >= 0; n--)
        {
            ushort id = c.Members[n];
            bool alive = _enemies.TryGet(id, out var e) && e.Alive;
            if (alive && _enemies.Leashed(id)) continue;
            woken |= alive;
            if (!alive) c.Members.RemoveAt(n);   // the woken stay listed until the column goes, so the watch sees all of it
        }
        if (!woken && c.Members.Count > 0) return;
        if (woken)
        {
            var ids = c.Members.ToArray();
            _enemies.ReleaseGroup(c.Group);
            OnColumnWoke?.Invoke(c.Index, c.Hunter, ids);
        }
        c.Members.Clear();
        c.Formed = false;
        c.Holding = false;
        c.ReformTicks = Ticks(TfwPatrolRules.ReformSeconds);
    }

    /// <summary>
    /// A column forms when its way is known, the crowds' cap and the pool have room for all of it, and (after its first time) its source sends: a route patrol at its source's end of the loop (its first time, spread round its loop
    /// and clear of the landing zone), a hunter at its source.
    /// </summary>
    void TryForm(Column c)
    {
        if (Won || (!c.Hunter && c.Route is null)) return;
        int size = TfwPatrolRules.Size;
        if (c.EverFormed && !SourceActive(c.Source)) return;
        if (Counted() - _trickled.Count + size > TfwBattleRules.AliveCap(_humansNow) || PoolUsed + size > TfwBattleRules.CrowdPoolRoom) return;
        float back = (size / TfwPatrolRules.Abreast) * TfwPatrolRules.RowSpacing;
        if (c.Hunter)
        {
            var start = Ground(_layout.Sources[c.Source], _layout.Sources[c.Source]);
            c.Trail = new Track(loop: false);
            c.Trail.Add(start);
            c.HeadNode = _enemies.Walk?.Nearest(start, 3f, 2.5f) ?? -1;
        }
        else
        {
            var route = c.Route!;
            c.Head = back;   // the source's end of the loop: the ranks stand behind it, round the loop's close
            if (!c.EverFormed)
            {
                c.Head = route.Length * (float)(c.Index * 0.6180339887498949 % 1.0);
                for (int tries = 0; tries < 64 && Vector2.Distance(Flat(route.Sample(c.Head).At), Flat(_layout.LandingZone)) < TfwPatrolRules.FromLanding; tries++) c.Head += route.Length / 64f;
            }
        }
        for (int m = 0; m < size; m++)
        {
            var (home, way) = Place(c, m);
            var at = Ground(home, home);
            var type = Fodder(_picked++);
            ushort id = _enemies.SpawnLeashed(type, at, MathF.Atan2(-way.X, way.Y), Health(type), c.Group, TfwPatrolRules.Aggro);
            if (id != 0) c.Members.Add(id);
        }
        if (c.Members.Count == 0) return;
        c.Formed = c.EverFormed = true;
    }

    static Vector2 Flat(Vector3 v) => new(v.X, v.Y);

    /// <summary>Member <paramref name="m"/>'s place in its column now (its rank and file behind the head) and the way's direction there.</summary>
    (Vector3 Home, Vector2 Way) Place(Column c, int m)
    {
        int row = m / TfwPatrolRules.Abreast, file = m % TfwPatrolRules.Abreast;
        float behind = row * TfwPatrolRules.RowSpacing, aside = (file - (TfwPatrolRules.Abreast - 1) * 0.5f) * TfwPatrolRules.FileSpacing;
        var (at, way) = c.Hunter ? c.Trail!.Sample(c.Trail.Length - behind) : c.Route!.Sample(c.Head - behind);
        var side = new Vector2(way.Y, -way.X);
        var spot = at + new Vector3(side * aside, 0f);
        if (_enemies.Nav is { } nav && nav.Nearest(spot, 0.6f, 2.5f) < 0) spot = at;   // a file whose place falls in a wall or off a ledge closes in on the way itself
        return (spot, way);
    }

    /// <summary>
    /// One tick of a formed column: its head goes on (a route patrol along its loop; a hunter down the flow field to the nearest target, or straight to the nearest player without a graph) unless its ranks are behind, and
    /// every Ork's place moves with it. A straggler too far behind is let go to hunt alone.
    /// </summary>
    void March(Column c, ReadOnlySpan<Vector3> players)
    {
        _behind.Clear();
        for (int m = c.Members.Count - 1; m >= 0; m--)
        {
            ushort id = c.Members[m];
            if (!_enemies.TryGet(id, out var e) || _enemies.HomeOf(id) is not { } home) continue;
            float d = Vector2.Distance(Flat(e.Position), Flat(home));
            if (d > TfwPatrolRules.StragglerBehind && e.State != MusouEnemyState.Spawning) { _enemies.Unleash(id); StragglersReleased++; c.Members.RemoveAt(m); continue; }
            _behind.Add(d);
        }
        if (c.Members.Count == 0) { TidyColumn(c); return; }
        _behind.Sort();
        c.Holding = _behind.Count > 0 && _behind[_behind.Count / 2] > TfwPatrolRules.WaitBehind;   // the middle Ork, so one caught on a corner does not stop the column
        float speed = c.Hunter ? TfwPatrolRules.HunterSpeed : TfwPatrolRules.PatrolSpeed;
        bool creep = false;
        if (!c.Holding) { c.HeldTicks = 0; c.Creeping = false; }
        else
        {
            c.HeldTicks++;
            if (!c.Creeping && c.HeldTicks > Ticks(TfwPatrolRules.MaxWaitSeconds))
            {
                // Waited long enough: the far laggards go on alone, and the head creeps on until the ranks are up, so the rest catch up while it walks.
                c.Creeping = true;
                for (int m = c.Members.Count - 1; m >= 0; m--)
                {
                    ushort id = c.Members[m];
                    if (_enemies.TryGet(id, out var e) && _enemies.HomeOf(id) is { } home && Vector2.Distance(Flat(e.Position), Flat(home)) > TfwPatrolRules.LateBehind && e.State != MusouEnemyState.Spawning)
                    { _enemies.Unleash(id); StragglersReleased++; c.Members.RemoveAt(m); }
                }
                if (c.Members.Count == 0) { TidyColumn(c); return; }
            }
            if (c.Creeping) { c.Holding = false; creep = true; }
        }
        if (creep) speed *= TfwPatrolRules.CreepFraction;
        if (!c.Holding)
        {
            float step = speed * Dt;
            if (!c.Hunter) c.Head += step;
            else
            {
                var trail = c.Trail!;
                var head = trail.Last;
                if (_enemies.Walk is { } walk && _enemies.Flow is { Ready: true } flow && c.HeadNode >= 0)
                {
                    for (int k = 0; k < 8 && step > 0f; k++)
                    {
                        int next = flow.Next(c.HeadNode);
                        if (next < 0) break;
                        var to = walk.Position(next);
                        float d = Vector2.Distance(Flat(head), Flat(to));
                        if (d <= step) { head = to; c.HeadNode = next; step -= d; }
                        else { head += (to - head) * (step / d); step = 0f; }
                    }
                }
                else if (Nearest(players, head) is { } goal && Vector2.Distance(Flat(goal), Flat(head)) > 1f)
                    head += new Vector3(Vector2.Normalize(Flat(goal - head)) * step, 0f);
                trail.Add(head);
                trail.Trim((c.Members.Count / TfwPatrolRules.Abreast + 2) * TfwPatrolRules.RowSpacing + 4f);
            }
        }
        for (int m = 0; m < c.Members.Count; m++)
        {
            var (home, way) = Place(c, m);
            _enemies.MoveHome(c.Members[m], home, c.Holding ? Vector2.Zero : way * speed);
        }
    }

    readonly List<float> _behind = new();

    static Vector3? Nearest(ReadOnlySpan<Vector3> feet, Vector3 from)
    {
        Vector3? best = null;
        float bestD = float.PositiveInfinity;
        foreach (var p in feet)
        {
            float d = Vector3.DistanceSquared(p, from);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }
}
