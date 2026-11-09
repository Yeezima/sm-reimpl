using System.Numerics;

namespace SmSim;

// Musou Mode (added content): the armies' width-aware route search on the walking graph (owner, 2026-11-09: "Yes, route on avenues"). The graph's build, cache and FindPath are untouched.
public sealed partial class NavGraph
{
    float[]? _clearance;
    readonly object _clearanceLock = new();

    /// <summary>
    /// Each node's clearance (metres): the cheapest walk to the nearest node at a wall or an edge (a node with fewer than 8 walk links), by a Dijkstra from all of them at once over the walk links. Built on first use
    /// (deterministic), kept with the graph. A node in the middle of a 22 m avenue has about 11 m; one in a 3 m alley about 1.5 m.
    /// </summary>
    public float[] Clearance()
    {
        if (_clearance is { } done) return done;
        lock (_clearanceLock)
        {
            if (_clearance is { } again) return again;
            int n = NodeCount;
            var dist = new float[n];
            Array.Fill(dist, float.PositiveInfinity);
            var open = new PriorityQueue<int, float>();
            for (int i = 0; i < n; i++)
                if (WalkLinkCount(i) < 8) { dist[i] = 0f; open.Enqueue(i, 0f); }
            while (open.TryDequeue(out int cur, out float d))
            {
                if (d > dist[cur]) continue;
                for (int l = _linkStart[cur]; l < _linkStart[cur + 1]; l++)
                {
                    if (_linkKind[l] != (byte)NavLinkKind.Walk) continue;
                    int nb = _linkTo[l];
                    float g = d + _linkCost[l];
                    if (g < dist[nb]) { dist[nb] = g; open.Enqueue(nb, g); }
                }
            }
            for (int i = 0; i < n; i++) if (float.IsPositiveInfinity(dist[i])) dist[i] = 0f;
            _clearance = dist;
            return dist;
        }
    }

    /// <summary>
    /// A route for a formation <paramref name="halfWidth"/> metres either side of its line: the search of <see cref="FindPath"/> (walk and drop links, no flights) with every link's cost raised where the narrower of
    /// its two nodes has less clearance than <paramref name="halfWidth"/> (cost times 1 + <paramref name="penalty"/> times the shortfall's share of the half width), so the route keeps to ways as wide as the
    /// formation and takes a narrow one only where no wide way is near (a gate). The string pulling keeps a shortcut only where the straight line's clearance stays at the narrowest of the cells it replaces (it
    /// never cuts a corner through an alley). Null when either end is off the graph.
    /// </summary>
    public NavPath? FindPathWide(Vector3 from, Vector3 to, float halfWidth, float penalty, float goalSnap = 10f, int maxExpanded = 400_000)
    {
        int start = Nearest(from, 3f, 3f);
        int goal = Nearest(to, goalSnap, 6f);
        if (start < 0 || goal < 0) return null;
        var clear = Clearance();
        var sc = t_search;
        if (sc is null || sc.G.Length != NodeCount) t_search = sc = new SearchScratch(NodeCount);
        int gen = ++sc.Generation;
        if (gen == int.MaxValue) { Array.Clear(sc.Stamp); Array.Clear(sc.Closed); sc.Generation = gen = 1; }
        sc.Open.Clear();
        var goalPos = Position(goal);
        float H(int i)
        {
            var p = Position(i);
            float dx = MathF.Abs(p.X - goalPos.X), dy = MathF.Abs(p.Y - goalPos.Y);
            return MathF.Max(dx, dy) + (MathF.Sqrt(2f) - 1f) * MathF.Min(dx, dy);
        }
        float need = MathF.Max(halfWidth, 0.01f);
        sc.G[start] = 0f; sc.Parent[start] = -1; sc.Stamp[start] = gen; sc.Via[start] = 0;
        sc.Open.Enqueue(start, H(start));
        int bestNode = start, expanded = 0;
        float bestH = H(start);
        bool found = false;
        while (sc.Open.TryDequeue(out int cur, out _))
        {
            if (sc.Closed[cur] == gen) continue;
            sc.Closed[cur] = gen;
            if (cur == goal) { found = true; break; }
            float hc = H(cur);
            if (hc < bestH) { bestH = hc; bestNode = cur; }
            if (++expanded > maxExpanded) break;
            float gc = sc.G[cur];
            for (int l = _linkStart[cur]; l < _linkStart[cur + 1]; l++)
            {
                int nb = _linkTo[l];
                if (sc.Closed[nb] == gen) continue;
                float shortfall = MathF.Max(0f, need - MathF.Min(clear[cur], clear[nb])) / need;
                float g = gc + _linkCost[l] * (1f + penalty * shortfall);
                if (sc.Stamp[nb] == gen && sc.G[nb] <= g) continue;
                sc.Stamp[nb] = gen; sc.G[nb] = g; sc.Parent[nb] = cur; sc.Via[nb] = _linkKind[l]; sc.ViaJump[nb] = -1;
                sc.Open.Enqueue(nb, g + H(nb));
            }
        }
        int end = found ? goal : bestNode;
        var cells = new List<(int Node, NavLinkKind Via)>();
        for (int i = end; i >= 0; i = sc.Parent[i]) cells.Add((i, (NavLinkKind)sc.Via[i]));
        cells.Reverse();
        var waypoints = SmoothWide(cells, clear);
        float length = 0f;
        for (int i = 1; i < waypoints.Count; i++) length += Vector3.Distance(waypoints[i - 1].Position, waypoints[i].Position);
        return new NavPath(waypoints, length, !found, expanded, cells.Count);
    }

    /// <summary>The string pulling of <see cref="FindPathWide"/>: a straight walk replaces cells only where the line's clearance stays at the narrowest of those cells less a cell.</summary>
    List<NavWaypoint> SmoothWide(List<(int Node, NavLinkKind Via)> cells, float[] clear)
    {
        var result = new List<NavWaypoint> { new(Position(cells[0].Node), NavLinkKind.Walk, cells[0].Node) };
        int anchor = 0;
        while (anchor < cells.Count - 1)
        {
            if (cells[anchor + 1].Via == NavLinkKind.Drop)
            {
                result.Add(new NavWaypoint(Position(cells[anchor + 1].Node), NavLinkKind.Drop, cells[anchor + 1].Node));
                anchor++;
                continue;
            }
            int far = anchor + 1;
            float narrowest = MathF.Min(clear[cells[anchor].Node], clear[cells[anchor + 1].Node]);
            for (int j = anchor + 2; j < cells.Count && cells[j].Via == NavLinkKind.Walk; j++)
            {
                narrowest = MathF.Min(narrowest, clear[cells[j].Node]);
                if (StraightWalk(cells[anchor].Node, cells[j].Node) && LineClearance(cells[anchor].Node, cells[j].Node, clear) >= narrowest - _cell) far = j;
                else if (j - far > 6) break;
            }
            result.Add(new NavWaypoint(Position(cells[far].Node), NavLinkKind.Walk, cells[far].Node));
            anchor = far;
        }
        return result;
    }

    /// <summary>The least clearance of the nodes a straight walk from <paramref name="a"/> to <paramref name="b"/> crosses (walk links only, as <see cref="StraightWalk"/>); 0 where the line leaves the links.</summary>
    float LineClearance(int a, int b, float[] clear)
    {
        var pa = Position(a); var pb = Position(b);
        float dx = pb.X - pa.X, dy = pb.Y - pa.Y, least = clear[a];
        float length = MathF.Sqrt(dx * dx + dy * dy);
        int steps = Math.Max(1, (int)MathF.Ceiling(length / (_cell * 0.25f)));
        int cur = a;
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            int col = ColumnOf(pa.X + dx * t, pa.Y + dy * t);
            if (col < 0) return 0f;
            if (col == _col[cur]) continue;
            int next = -1;
            for (int l = _linkStart[cur]; l < _linkStart[cur + 1]; l++)
                if (_linkKind[l] == (byte)NavLinkKind.Walk && _col[_linkTo[l]] == col) { next = _linkTo[l]; break; }
            if (next < 0) return 0f;
            cur = next;
            least = MathF.Min(least, clear[cur]);
        }
        return least;
    }
}
