using System.Numerics;

namespace SmSim;

/// <summary>
/// Bots A2 (owner 2026-11-23; test script, **ours**, not the original AI; spec "Bot map plan: routes and vantages"): "make the bots fan out more. on maps that aren't
/// manufactorum they need to be able to navigate all around the map, not just walk in a straight line down the center. / tacticals and devastators look for covered
/// defensive positons and high ground, assault marines and tactical sometimes flank the enemy via alternate routes." Two more parts of the plan, built with it:
/// <list type="bullet">
/// <item><b>routes</b>: up to <see cref="MaxRoutes"/> spawn-to-spawn routes over the bot's own graph (walks, drops and its jump links). The first is the cheapest; each
/// further one is the cheapest after every cell within <see cref="RouteWidth"/> m of the routes tried so far costs <see cref="RoutePenalty"/> times more, kept when it is at
/// most <see cref="RouteMaxStretch"/> times the first's length and at least <see cref="RouteMinNovel"/> of its cells lie away from the kept ones. A route is the waypoints
/// every <see cref="RouteStep"/> m from <see cref="OwnStartClearance"/> m out of the own start to the edge of the no-loiter zone, ordered across the map;</item>
/// <item><b>vantages</b>: up to <see cref="MaxVantages"/> spots from the own side to just past the middle (<see cref="VantageFrom"/> to <see cref="VantageTo"/> of the way
/// between the starts) scored on high ground (height above the graph's median floor), cover (a wall or prop beside it) and sight (how many of
/// <see cref="VantageSamples"/> points on the middle of the line between the starts it sees from eye height), at least <see cref="VantageSpacing"/> m apart.</item>
/// </list>
/// </summary>
public sealed partial class BotMapPlan
{
    /// <summary>Most routes, their tries, the band (m) round a route that the next try avoids and how much more it costs there (ours).</summary>
    public const int MaxRoutes = 4, RouteTries = 6;
    public const float RouteWidth = 12f, RoutePenalty = 4f;
    /// <summary>A route is kept when at most this times the cheapest route's length and at least this share of its cells is more than <see cref="RouteWidth"/> m from the kept routes (ours).</summary>
    public const float RouteMaxStretch = 1.8f, RouteMinNovel = 0.4f;
    /// <summary>Metres of path between a route's waypoints (ours).</summary>
    public const float RouteStep = 10f;
    /// <summary>Most vantages, their band along the line between the starts (fractions from the own start), spacing (m), least sight count and the sight samples (ours).</summary>
    public const int MaxVantages = 8, VantageSamples = 8, VantageMinSight = 3;
    public const float VantageFrom = 0.2f, VantageTo = 0.7f, VantageSpacing = 15f;
    /// <summary>A life's route leg ends at the first route point at least this fraction of the way from the own start toward the enemy's (ours; review 2026-11-25: the owner's
    /// 2026-11-07 rule, hold the centre and never wait outside the enemy's spawn; the old lane leg ended 25 to 45 % of the way).</summary>
    public const float RouteLegTo = 0.55f;
    /// <summary>A route leg also ends at the first point within this many metres of the no-loiter zone's edge (re-review 2026-11-25: on a small map 55 % can already be next to the enemy's spawn; ours).</summary>
    public const float RouteLegZoneMargin = 15f;
    /// <summary>Metres of height a vantage candidate's floor band spans (one candidate per 4 m square and band, so stacked floors each get theirs; ours).</summary>
    public const float VantageFloorBand = 3f;

    /// <summary>The spawn-to-spawn routes (waypoints from the own side toward the enemy's), ordered from one side of the map to the other; empty before a build or without a route.</summary>
    public IReadOnlyList<IReadOnlyList<Vector3>> Routes { get; private set; } = [];
    /// <summary>Index into <see cref="Routes"/> of the cheapest (most direct) route, -1 without routes.</summary>
    public int DirectRoute { get; private set; } = -1;
    /// <summary>The vantages, best first.</summary>
    public IReadOnlyList<BotVantage> Vantages { get; private set; } = [];
    /// <summary>The graph's median floor height (m), the high-ground reference.</summary>
    public float MedianFloor { get; private set; }
    /// <summary>Per route: the index of the point where a life's route leg ends (<see cref="RouteLegTo"/>; the last point when none is that far).</summary>
    public IReadOnlyList<int> RouteLegEnds { get; private set; } = [];
    /// <summary>The own start area's centre the plan was built from.</summary>
    public Vector3 OwnStart { get; private set; }

    /// <summary>The fraction of the way from <see cref="OwnStart"/> toward <see cref="EnemyStart"/> that <paramref name="p"/> lies at (flat, along the line between them).</summary>
    public float Along(Vector3 p)
    {
        var axis = new Vector2(EnemyStart.X - OwnStart.X, EnemyStart.Y - OwnStart.Y);
        float l2 = axis.LengthSquared();
        return l2 < 1e-6f ? 0f : Vector2.Dot(new Vector2(p.X - OwnStart.X, p.Y - OwnStart.Y), axis) / l2;
    }

    /// <summary>
    /// A distance that tells stacked floors apart (review 2026-11-25): the flat distance plus 3 times the height difference beyond 1 m, so a point on the floor above or below is
    /// far even when it is straight overhead.
    /// </summary>
    public static float Near(Vector3 a, Vector3 b) => Flat(a, b) + 3f * MathF.Max(0f, MathF.Abs(a.Z - b.Z) - 1f);

    /// <summary><see cref="Near"/> with its height term capped at <paramref name="maxHeightTerm"/> metres.</summary>
    public static float Near(Vector3 a, Vector3 b, float maxHeightTerm) => Flat(a, b) + MathF.Min(maxHeightTerm, 3f * MathF.Max(0f, MathF.Abs(a.Z - b.Z) - 1f));

    /// <summary>Whether <paramref name="p"/> lies within <paramref name="margin"/> m (flat) of the no-loiter zone's edge or inside it.</summary>
    public bool NearNoLoiterZone(Vector3 p, float margin) => Flat(p, EnemyStart) < NoLoiterRadius + margin || EnemyDoor is { } d && Flat(p, d) < NoLoiterRadius + margin;

    /// <summary>
    /// A chase along <paramref name="route"/> from <paramref name="me"/> to <paramref name="enemy"/>: its cost (the distance to the route's nearest point, along the route to the
    /// point nearest the enemy, then on to the enemy; every distance by <see cref="Near"/>), the next route point to walk to (the first more than <paramref name="pass"/> m away
    /// toward that point) and that point's index. Infinite cost and no next point when the nearest point is the one nearest the enemy, or for an empty route.
    /// </summary>
    public static (float Cost, Vector3? Next, int Target) RouteChase(IReadOnlyList<Vector3> route, Vector3 me, Vector3 enemy, float pass)
    {
        if (route.Count == 0) return (float.PositiveInfinity, null, -1);
        int near = 0, target = 0;
        for (int i = 1; i < route.Count; i++)
        {
            if (Near(route[i], me) < Near(route[near], me)) near = i;
            if (Near(route[i], enemy) < Near(route[target], enemy)) target = i;
        }
        if (near == target) return (float.PositiveInfinity, null, target);
        int step = target > near ? 1 : -1;
        float cost = Near(me, route[near]);
        Vector3? next = null;
        for (int i = near; i != target; i += step)
        {
            cost += Vector3.Distance(route[i], route[i + step]);   // along the route: the real 3D length (a ramp or bridge is not a floor change)
            if (next is null && Near(route[i + step], me) > pass) next = route[i + step];
        }
        if (next is null && Near(route[near], me) > pass) next = route[near];
        cost += Near(route[target], enemy);
        return (cost, next, target);
    }

    /// <summary>Builds <see cref="Routes"/> and <see cref="Vantages"/> (called from <see cref="Build"/>).</summary>
    void BuildRoutesAndVantages(NavGraph graph, bool[] reach, int start, Vector3 ownStart, CollisionWorld? world, float eye, CancellationToken cancel)
    {
        OwnStart = ownStart;
        BuildRoutes(graph, reach, start, ownStart, cancel);
        BuildVantages(graph, reach, ownStart, world, eye, cancel);
    }

    void BuildRoutes(NavGraph graph, bool[] reach, int start, Vector3 ownStart, CancellationToken cancel)
    {
        // The goal: the reachable cell nearest the enemy's door (else its start), flat.
        var target = EnemyDoor ?? EnemyStart;
        int goal = -1; float bestGoal = float.MaxValue;
        for (int n = 0; n < reach.Length; n++)
        {
            if (!reach[n]) continue;
            var p = graph.Position(n);
            float d = Flat(p, target) + 0.25f * MathF.Abs(p.Z - target.Z);
            if (d < bestGoal) { bestGoal = d; goal = n; }
        }
        if (goal < 0 || goal == start) return;
        int count = graph.NodeCount;
        var penalty = new float[count];   // 0 = free; the cost multiplier is 1 + RoutePenalty * penalty
        var dist = new float[count];
        var parent = new int[count];
        var kept = new List<(List<int> Cells, float Length)>();
        float first = -1f;
        var keptNear = new bool[count];
        for (int attempt = 0; attempt < RouteTries && kept.Count < MaxRoutes; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            var cells = Cheapest(graph, start, goal, penalty, dist, parent, cancel);
            if (cells is null) break;
            float length = 0f;
            for (int i = 1; i < cells.Count; i++) length += Vector3.Distance(graph.Position(cells[i - 1]), graph.Position(cells[i]));
            if (first < 0f) first = length;
            bool keep = length <= RouteMaxStretch * first;
            if (keep && kept.Count > 0)
            {
                int counted = 0, novel = 0;
                foreach (int c in cells)
                {
                    var p = graph.Position(c);
                    if (Flat(p, ownStart) < OwnStartClearance || InNoLoiterZone(p)) continue;
                    counted++;
                    if (!keptNear[c]) novel++;
                }
                keep = counted > 0 && novel >= RouteMinNovel * counted;
            }
            if (keep)
            {
                kept.Add((cells, length));
                Mark(graph, reach, cells, keptNear, null);
            }
            Mark(graph, reach, cells, null, penalty);   // tried routes are avoided by the next try, kept or not
        }
        if (kept.Count == 0) return;
        var axis = new Vector2(EnemyStart.X - ownStart.X, EnemyStart.Y - ownStart.Y);
        float axisLength = axis.Length();
        if (axisLength > 1e-3f) axis /= axisLength;
        var routes = new List<(List<Vector3> Points, float Across, bool Direct)>();
        for (int k = 0; k < kept.Count; k++)
        {
            var (cells, _) = kept[k];
            var points = new List<Vector3>();
            float walked = 0f, next = 0f, across = 0f; int acrossN = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                var p = graph.Position(cells[i]);
                if (i > 0) walked += Vector3.Distance(graph.Position(cells[i - 1]), p);
                if (InNoLoiterZone(p)) break;
                if (Flat(p, ownStart) < OwnStartClearance) { next = walked + RouteStep * 0.5f; continue; }
                var rel = new Vector2(p.X - ownStart.X, p.Y - ownStart.Y);
                across += axis.X * rel.Y - axis.Y * rel.X; acrossN++;
                if (walked >= next) { points.Add(p); next = walked + RouteStep; }
            }
            if (points.Count > 0) routes.Add((points, acrossN > 0 ? across / acrossN : 0f, k == 0));
        }
        routes.Sort((a, b) => a.Across.CompareTo(b.Across));
        Routes = routes.Select(r => (IReadOnlyList<Vector3>)r.Points).ToList();
        DirectRoute = routes.FindIndex(r => r.Direct);
        RouteLegEnds = routes.Select(r => r.Points.FindIndex(q => Along(q) >= RouteLegTo || NearNoLoiterZone(q, RouteLegZoneMargin)) is var i and >= 0 ? i : r.Points.Count - 1).ToList();
    }

    /// <summary>Marks every reachable cell within <see cref="RouteWidth"/> m (flat; 4 m of height) of <paramref name="cells"/>: sets <paramref name="near"/> and/or raises <paramref name="penalty"/> by one.</summary>
    static void Mark(NavGraph graph, bool[] reach, List<int> cells, bool[]? near, float[]? penalty)
    {
        const float bucket = RouteWidth;
        var grid = new Dictionary<(int, int), List<Vector3>>();
        for (int i = 0; i < cells.Count; i += 2)
        {
            var p = graph.Position(cells[i]);
            var key = ((int)MathF.Floor(p.X / bucket), (int)MathF.Floor(p.Y / bucket));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = new();
            list.Add(p);
        }
        for (int n = 0; n < reach.Length; n++)
        {
            if (!reach[n]) continue;
            var p = graph.Position(n);
            int gx = (int)MathF.Floor(p.X / bucket), gy = (int)MathF.Floor(p.Y / bucket);
            bool hit = false;
            for (int dx = -1; dx <= 1 && !hit; dx++)
                for (int dy = -1; dy <= 1 && !hit; dy++)
                    if (grid.TryGetValue((gx + dx, gy + dy), out var list))
                        foreach (var q in list)
                            if (MathF.Abs(q.Z - p.Z) <= 4f && Flat(p, q) <= RouteWidth) { hit = true; break; }
            if (!hit) continue;
            if (near is not null) near[n] = true;
            if (penalty is not null) penalty[n] += 1f;
        }
    }

    /// <summary>Dijkstra from <paramref name="start"/> to <paramref name="goal"/> over walks, drops and jump links, each link's cost times 1 + <see cref="RoutePenalty"/> × the penalty of the cell it reaches; the cells, or null.</summary>
    static List<int>? Cheapest(NavGraph graph, int start, int goal, float[] penalty, float[] dist, int[] parent, CancellationToken cancel)
    {
        Array.Fill(dist, float.MaxValue);
        Array.Fill(parent, -1);
        var open = new PriorityQueue<int, float>();
        dist[start] = 0f; open.Enqueue(start, 0f);
        int popped = 0;
        while (open.TryDequeue(out int n, out float d))
        {
            if (d > dist[n]) continue;
            if (n == goal) break;
            if ((++popped & 8191) == 0) cancel.ThrowIfCancellationRequested();
            foreach (var (to, cost, _) in graph.Links(n)) Relax(to, cost);
            if (graph.Jumps is { } jumps) foreach (var (to, cost, _) in jumps.From(n)) Relax(to, cost);
            void Relax(int to, float cost)
            {
                float g = d + cost * (1f + RoutePenalty * MathF.Min(penalty[to], 3f));
                if (g < dist[to]) { dist[to] = g; parent[to] = n; open.Enqueue(to, g); }
            }
        }
        if (dist[goal] == float.MaxValue) return null;
        var cells = new List<int>();
        for (int i = goal; i >= 0; i = parent[i]) cells.Add(i);
        cells.Reverse();
        return cells;
    }

    void BuildVantages(NavGraph graph, bool[] reach, Vector3 ownStart, CollisionWorld? world, float eye, CancellationToken cancel)
    {
        var heights = new List<float>();
        for (int n = 0; n < reach.Length; n += 3) if (reach[n]) heights.Add(graph.Position(n).Z);
        if (heights.Count == 0) return;
        heights.Sort();
        MedianFloor = heights[heights.Count / 2];
        if (world is null) return;
        var axis = new Vector2(EnemyStart.X - ownStart.X, EnemyStart.Y - ownStart.Y);
        float length = axis.Length();
        if (length < 1f) return;
        axis /= length;
        // The sight samples: the reachable floor nearest each of VantageSamples points on the line from 0.35 to 0.75 of the way, at eye height.
        var samples = new List<Vector3>();
        for (int k = 0; k < VantageSamples; k++)
        {
            float f = 0.35f + 0.4f * k / (VantageSamples - 1);
            var at = new Vector2(ownStart.X, ownStart.Y) + axis * (length * f);
            int bestNode = -1; float bestD = 15f;
            for (int n = 0; n < reach.Length; n += 2)
            {
                if (!reach[n]) continue;
                var p = graph.Position(n);
                float d = MathF.Sqrt((p.X - at.X) * (p.X - at.X) + (p.Y - at.Y) * (p.Y - at.Y)) + 0.2f * MathF.Abs(p.Z - MedianFloor);
                if (d < bestD) { bestD = d; bestNode = n; }
            }
            if (bestNode >= 0) samples.Add(graph.Position(bestNode) + new Vector3(0, 0, eye));
        }
        if (samples.Count < VantageMinSight) return;
        // Candidates: the highest reachable cell of each 4 m square and floor band (VantageFloorBand) in the band along the line, so each of stacked floors has its own.
        var squares = new Dictionary<(int, int, int), Vector3>();
        for (int n = 0; n < reach.Length; n++)
        {
            if (!reach[n]) continue;
            if ((n & 4095) == 0) cancel.ThrowIfCancellationRequested();
            var p = graph.Position(n);
            var rel = new Vector2(p.X - ownStart.X, p.Y - ownStart.Y);
            float along = Vector2.Dot(rel, axis) / length;
            if (along < VantageFrom || along > VantageTo || InNoLoiterZone(p) || Flat(p, ownStart) < OwnStartClearance) continue;
            var key = ((int)MathF.Floor(p.X / 4f), (int)MathF.Floor(p.Y / 4f), (int)MathF.Floor(p.Z / VantageFloorBand));
            if (!squares.TryGetValue(key, out var old) || p.Z > old.Z) squares[key] = p;
        }
        var scored = new List<BotVantage>();
        foreach (var p in squares.Values)
        {
            cancel.ThrowIfCancellationRequested();
            var from = p + new Vector3(0, 0, eye);
            int sight = 0; var face = Vector3.Zero;
            foreach (var s in samples)
            {
                var line = s - from;
                float d = line.Length();
                if (d < 4f || d > 90f) continue;
                if (world.Weapons.Raycast(from, line / d, d - 0.3f) is null) { sight++; face += s; }
            }
            if (sight < VantageMinSight) continue;
            float rise = p.Z - MedianFloor;
            float cover = Cover(world, p);
            float score = Math.Clamp(rise, 0f, 6f) / 2f + 0.75f * cover + 3f * sight / samples.Count;
            scored.Add(new BotVantage(p, face / sight, rise, cover > 0f, sight, score));
        }
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var chosen = new List<BotVantage>();
        foreach (var v in scored)
        {
            if (chosen.Count >= MaxVantages) break;
            if (chosen.All(c => Near(c.Spot, v.Spot) >= VantageSpacing)) chosen.Add(v);
        }
        Vantages = chosen;
    }
}

/// <summary>
/// A vantage of a <see cref="BotMapPlan"/> (bots A2; ours): the spot (feet), the point it watches (eye height), its height above the median floor (m), whether a wall
/// or prop stands beside it, how many sight samples it sees and its score.
/// </summary>
public sealed record BotVantage(Vector3 Spot, Vector3 Face, float Rise, bool Covered, int Sight, float Score);
