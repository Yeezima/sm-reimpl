using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The walking graph as the Musou enemies use it (MU1): the <see cref="NavGraph"/>'s nodes and links copied once into flat arrays (forward links for moving,
/// reverse links for the flow field) and a column index, so a move to the next cell and a gradient step are a few array reads. Walk and drop links only (no
/// jump-pack links: enemies do not fly). Deterministic.
/// </summary>
public sealed class MusouNav
{
    readonly float[] _x, _y, _z;
    readonly int[] _col;                 // per node: its column (gx + gy * Width)
    readonly int[] _colStart, _colNodes; // per column: its nodes (CSR)
    readonly int[] _fwdStart, _fwdTo;    // forward links (CSR)
    readonly float[] _fwdCost;
    readonly int[] _revStart, _revFrom;  // reverse links (CSR): who links to a node, and at what cost
    readonly float[] _revCost;
    readonly float _ox, _oy, _cell;

    public MusouNav(NavGraph graph)
    {
        Graph = graph;
        int n = graph.NodeCount;
        _x = new float[n]; _y = new float[n]; _z = new float[n];
        _cell = graph.Settings.CellSize;
        float ox = float.MaxValue, oy = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var p = graph.Position(i);
            _x[i] = p.X; _y[i] = p.Y; _z[i] = p.Z;
            ox = MathF.Min(ox, p.X); oy = MathF.Min(oy, p.Y);
        }
        _ox = n > 0 ? ox : 0f; _oy = n > 0 ? oy : 0f;
        int w = 1, h = 1;
        _col = new int[n];
        var gx = new int[n]; var gy = new int[n];
        for (int i = 0; i < n; i++)
        {
            gx[i] = (int)MathF.Round((_x[i] - _ox) / _cell); gy[i] = (int)MathF.Round((_y[i] - _oy) / _cell);
            w = Math.Max(w, gx[i] + 1); h = Math.Max(h, gy[i] + 1);
        }
        Width = w; Depth = h;
        _colStart = new int[w * h + 1];
        for (int i = 0; i < n; i++) { _col[i] = gx[i] + gy[i] * w; _colStart[_col[i] + 1]++; }
        for (int c = 0; c < w * h; c++) _colStart[c + 1] += _colStart[c];
        _colNodes = new int[n];
        var fill = (int[])_colStart.Clone();
        for (int i = 0; i < n; i++) _colNodes[fill[_col[i]]++] = i;

        var fwdStart = new int[n + 1];
        var to = new List<int>(n * 8); var cost = new List<float>(n * 8);
        for (int i = 0; i < n; i++)
        {
            foreach (var (t, c, kind) in graph.Links(i))
                if (kind is NavLinkKind.Walk or NavLinkKind.Drop) { to.Add(t); cost.Add(c); }
            fwdStart[i + 1] = to.Count;
        }
        _fwdStart = fwdStart; _fwdTo = to.ToArray(); _fwdCost = cost.ToArray();
        _revStart = new int[n + 1];
        foreach (int t in _fwdTo) _revStart[t + 1]++;
        for (int i = 0; i < n; i++) _revStart[i + 1] += _revStart[i];
        _revFrom = new int[_fwdTo.Length]; _revCost = new float[_fwdTo.Length];
        var at = (int[])_revStart.Clone();
        for (int i = 0; i < n; i++)
            for (int l = _fwdStart[i]; l < _fwdStart[i + 1]; l++) { int k = at[_fwdTo[l]]++; _revFrom[k] = i; _revCost[k] = _fwdCost[l]; }
    }

    public NavGraph Graph { get; }
    public int NodeCount => _x.Length;
    /// <summary>The column grid's size in cells.</summary>
    public int Width { get; }
    public int Depth { get; }
    public float CellSize => _cell;

    public Vector3 Position(int node) => new(_x[node], _y[node], _z[node]);
    public float Z(int node) => _z[node];
    public float X(int node) => _x[node];
    public float Y(int node) => _y[node];
    public int ForwardStart(int node) => _fwdStart[node];
    public int ForwardEnd(int node) => _fwdStart[node + 1];
    public int ForwardTo(int link) => _fwdTo[link];
    public float ForwardCost(int link) => _fwdCost[link];
    internal int ReverseStart(int node) => _revStart[node];
    internal int ReverseEnd(int node) => _revStart[node + 1];
    internal int ReverseFrom(int link) => _revFrom[link];
    internal float ReverseCost(int link) => _revCost[link];

    /// <summary>Whether the cells of two nodes touch (the same or one of the eight around it): a move between them is a step, else a drop's fall.</summary>
    public bool Adjacent(int a, int b)
    {
        int ca = _col[a], cb = _col[b];
        return Math.Abs(ca % Width - cb % Width) <= 1 && Math.Abs(ca / Width - cb / Width) <= 1;
    }

    /// <summary>The column of a ground-plane point (-1 outside the grid).</summary>
    public int ColumnOf(float x, float y)
    {
        int cx = (int)MathF.Round((x - _ox) / _cell), cy = (int)MathF.Round((y - _oy) / _cell);
        return cx < 0 || cy < 0 || cx >= Width || cy >= Depth ? -1 : cx + cy * Width;
    }

    /// <summary>The node of <paramref name="column"/> whose floor is nearest <paramref name="z"/> within <paramref name="maxDz"/> (-1 none).</summary>
    public int NodeIn(int column, float z, float maxDz)
    {
        if (column < 0) return -1;
        int best = -1; float bestDz = maxDz;
        for (int k = _colStart[column]; k < _colStart[column + 1]; k++)
        {
            int i = _colNodes[k];
            float dz = MathF.Abs(_z[i] - z);
            if (dz <= bestDz) { bestDz = dz; best = i; }
        }
        return best;
    }

    /// <summary>
    /// The node a body standing on <paramref name="node"/> stands on after moving to <paramref name="to"/> (ground plane): the same node within its cell,
    /// else the neighbour in the new cell that <paramref name="node"/> links to (walk or drop): <paramref name="prefer"/> when it is one of them (the flow's
    /// next node: a cell can hold a floor and, under it, the ground a drop lands on), else the one nearest in height; else -1 (a wall, a ledge it cannot
    /// climb, off the graph).
    /// </summary>
    public int MoveTo(int node, float x, float y, int prefer = -1)
    {
        int col = ColumnOf(x, y);
        if (col < 0) return -1;
        if (col == _col[node]) return node;
        int best = -1; float bestDz = float.MaxValue;
        for (int l = _fwdStart[node]; l < _fwdStart[node + 1]; l++)
        {
            int t = _fwdTo[l];
            if (_col[t] != col) continue;
            if (t == prefer) return t;
            float dz = MathF.Abs(_z[t] - _z[node]);
            if (dz < bestDz) { bestDz = dz; best = t; }
        }
        return best;
    }

    /// <summary>The node under or nearest <paramref name="p"/> (the graph's own search: its cell first, then rings out to <paramref name="radius"/>).</summary>
    public int Nearest(Vector3 p, float radius = 2f, float maxDz = 2.5f)
    {
        int quick = NodeIn(ColumnOf(p.X, p.Y), p.Z, 0.6f);
        return quick >= 0 ? quick : Graph.Nearest(p, radius, maxDz);
    }

    /// <summary>The box the nodes span (feet), with headroom above the highest floor.</summary>
    public MusouBox Box()
    {
        if (NodeCount == 0) return new MusouBox(Vector3.Zero, Vector3.One).Sane();
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        for (int i = 0; i < NodeCount; i++) { var p = Position(i); lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        return new MusouBox(lo - new Vector3(1f, 1f, 1f), hi + new Vector3(1f, 1f, 3f)).Sane();
    }
}

/// <summary>
/// The shared flow field (design section B): one multi-source reverse Dijkstra from the nodes of every living player over the reverse links, so each node
/// holds the walking cost to the nearest player and an enemy steps to the linked neighbour with the lowest. Incremental and double-buffered: a refresh
/// expands at most a budget of nodes a tick into the back buffer while the enemies read the front one, and swaps when it is done; a new refresh starts
/// <see cref="Step"/>'s refresh interval after the last one started. The open set is a bucket queue (<see cref="BucketWidth"/> m buckets, first in first out
/// within one; every link costs more than a bucket, so the buckets are taken in order): a node's cost can be up to a bucket's width above the true shortest,
/// and every reached node keeps a neighbour strictly lower (the one it was reached from), so the gradient has no false minimum. Deterministic (the same
/// sources and order give the same field).
/// </summary>
public sealed class FlowField
{
    /// <summary>Metres of walking cost a bucket of the open set spans (ours; under the cheapest link, a cell's 0.5 m).</summary>
    public const float BucketWidth = 0.25f;

    readonly MusouNav _nav;
    float[] _front, _back;
    int[] _frontOwner, _backOwner;   // per node: the tag of the source it is nearest (-1: none)
    readonly List<List<int>> _buckets = new();
    readonly List<int> _bucketRead = new();
    int _bucket, _openCount;
    readonly int[] _next, _nextStamp;
    bool _running;
    int _sinceStart;

    public FlowField(MusouNav nav)
    {
        _nav = nav;
        int n = nav.NodeCount;
        _front = new float[n]; _back = new float[n];
        Array.Fill(_front, float.PositiveInfinity);
        _frontOwner = new int[n]; _backOwner = new int[n];
        Array.Fill(_frontOwner, -1);
        _next = new int[n]; _nextStamp = new int[n];
    }

    /// <summary>Completed fields so far (each swap counts one; a <see cref="Reset"/> counts one too).</summary>
    public int Generation { get; private set; }
    /// <summary>A field exists (until the first one is done, or after a <see cref="Reset"/>, every distance is infinite).</summary>
    public bool Ready { get; private set; }
    /// <summary>A refresh is under way in the back buffer.</summary>
    public bool Running => _running;
    /// <summary>Node expansions of the last <see cref="Step"/> (for the cost report).</summary>
    public int LastExpansions { get; private set; }

    /// <summary>The walking cost from <paramref name="node"/> to the nearest player in the current field (infinite: none reached it).</summary>
    public float Distance(int node) => _front[node];

    /// <summary>The tag of the source <paramref name="node"/> is nearest by walking in the current field (the caller's; -1: unreached).</summary>
    public int Owner(int node) => _frontOwner[node];

    /// <summary>Forgets the field and any refresh under way (a new round).</summary>
    public void Reset()
    {
        Array.Fill(_front, float.PositiveInfinity);
        Array.Fill(_frontOwner, -1);
        _running = false; Ready = false; _sinceStart = 0;
        _openCount = 0;
        Generation++;   // the gradient cache is stale
    }

    /// <summary>
    /// One tick: starts a refresh from <paramref name="sources"/> (each tagged by <paramref name="owners"/>, same length; empty: the tags are the
    /// indices) when none runs and <paramref name="refreshTicks"/> have passed since the last start (or no field exists yet), then expands at most
    /// <paramref name="budget"/> nodes; swaps the buffers when the refresh is done. No sources (every player off the graph or dead): no refresh starts and
    /// the last field stays, so the horde keeps walking to where the players were.
    /// </summary>
    public void Step(ReadOnlySpan<int> sources, ReadOnlySpan<int> owners, int budget, int refreshTicks)
    {
        _sinceStart++;
        LastExpansions = 0;
        if (!_running && (_sinceStart >= refreshTicks || !Ready) && !Start(sources, owners)) _sinceStart = 0;
        if (_running) Expand(Math.Max(1, budget));
    }

    /// <summary>A whole field from <paramref name="sources"/> at once (set-up, tests), replacing any refresh under way; none: the field stays as it is.</summary>
    public void Complete(ReadOnlySpan<int> sources, ReadOnlySpan<int> owners = default)
    {
        if (Start(sources, owners)) Expand(int.MaxValue);
    }

    bool Start(ReadOnlySpan<int> sources, ReadOnlySpan<int> owners)
    {
        bool any = false;
        foreach (int s in sources) any |= s >= 0 && s < _back.Length;
        if (!any) return false;
        _sinceStart = 0;
        for (int b = 0; b < _buckets.Count; b++) { _buckets[b].Clear(); _bucketRead[b] = 0; }
        _bucket = 0; _openCount = 0;
        Array.Fill(_back, float.PositiveInfinity);
        Array.Fill(_backOwner, -1);
        for (int k = 0; k < sources.Length; k++)
        {
            int s = sources[k];
            if (s >= 0 && s < _back.Length && _back[s] > 0f) { _back[s] = 0f; _backOwner[s] = k < owners.Length ? owners[k] : k; Push(s, 0f); }
        }
        _running = true;
        return true;
    }

    static int BucketOf(float d) => (int)(d / BucketWidth);

    void Push(int node, float d)
    {
        int b = BucketOf(d);
        while (_buckets.Count <= b) { _buckets.Add(new List<int>()); _bucketRead.Add(0); }
        _buckets[b].Add(node);
        _openCount++;
    }

    void Expand(int budget)
    {
        int done = 0;
        while (done < budget && _openCount > 0)
        {
            while (_bucketRead[_bucket] >= _buckets[_bucket].Count) _bucket++;
            int node = _buckets[_bucket][_bucketRead[_bucket]++];
            _openCount--;
            done++;
            float d = _back[node];
            if (BucketOf(d) != _bucket) continue;   // a stale entry (the node was reached cheaper since)
            for (int l = _nav.ReverseStart(node); l < _nav.ReverseEnd(node); l++)
            {
                int from = _nav.ReverseFrom(l);
                float nd = d + MathF.Max(_nav.ReverseCost(l), BucketWidth * 1.01f);
                if (nd < _back[from]) { _back[from] = nd; _backOwner[from] = _backOwner[node]; Push(from, nd); }
            }
        }
        LastExpansions += done;
        if (_openCount > 0) return;
        (_front, _back) = (_back, _front);
        (_frontOwner, _backOwner) = (_backOwner, _frontOwner);
        _running = false;
        Ready = true;
        Generation++;
    }

    /// <summary>The linked neighbour of <paramref name="node"/> one step down the field (-1: the node is a source, unreached, or a local minimum).</summary>
    public int Next(int node)
    {
        if (_nextStamp[node] == Generation + 1) return _next[node];
        float here = _front[node], best = float.PositiveInfinity;
        int next = -1;
        if (!float.IsPositiveInfinity(here))
            for (int l = _nav.ForwardStart(node); l < _nav.ForwardEnd(node); l++)
            {
                int t = _nav.ForwardTo(l);
                float d = _front[t];
                if (d >= here) continue;
                float via = d + _nav.ForwardCost(l);
                if (via < best) { best = via; next = t; }
            }
        _next[node] = next; _nextStamp[node] = Generation + 1;   // + 1: a fresh array's zeros never match
        return next;
    }
}
