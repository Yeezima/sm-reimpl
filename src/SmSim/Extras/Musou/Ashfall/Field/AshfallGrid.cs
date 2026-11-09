using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// A plan grid over Ashfall's square (draft 11's checks; layout-agnostic, so draft 10 is measured the same way): which cells the walking band of the final collision blocks, each free cell's clearance (metres to the
/// nearest blocked cell, exact Euclidean transform) and the top of the solid over each cell (for sightlines). Built from the level's solid parts after every rule has run. Army ground is the ground itself: stair ramps,
/// platform decks and rooftops are obstacles here (the brief: stairways and rooftops never count as army routes).
/// </summary>
public sealed class AshfallGrid
{
    public const float DefaultCell = 0.5f;

    /// <summary>The walking band (m over the ground): a solid whose span meets it blocks the cell (the 0.4 m step to an Ork's 1.9 m head).</summary>
    public const float BandLow = 0.4f, BandHigh = 1.9f;

    public readonly float Cell, Origin;
    public readonly int N;
    public readonly bool[] Blocked;
    public readonly float[] Clearance;
    public readonly float[] Top;
    readonly Func<float, float, float> _ground;

    public AshfallGrid(AshfallLevel level, float cell = DefaultCell)
    {
        Cell = cell; Origin = -AshfallMap.Half; N = (int)MathF.Round(2f * AshfallMap.Half / cell);
        _ground = AshfallMap.GroundOf(level.Layout);
        Blocked = new bool[N * N]; Top = new float[N * N]; Clearance = new float[N * N];
        for (int j = 0; j < N; j++) for (int i = 0; i < N; i++) Top[j * N + i] = _ground(X(i), X(j));
        foreach (var part in level.Parts)
        {
            if (!part.Solid || part.Kind is AshfallKind.Ground or AshfallKind.Road or AshfallKind.Paving or AshfallKind.Curb or AshfallKind.Objective) continue;
            var c = part.Corners;
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var v in c) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
            var box = AshfallBox.Of(part);
            int i0 = Math.Max(0, I(mn.X) - 1), i1 = Math.Min(N - 1, I(mx.X) + 1), j0 = Math.Max(0, I(mn.Y) - 1), j1 = Math.Min(N - 1, I(mx.Y) + 1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var p = new Vector2(X(i), X(j));
                    if (box is { } b ? !AshfallAtmosphere.Inside(b, p, cell * 0.5f) : (p.X < mn.X - cell * 0.5f || p.X > mx.X + cell * 0.5f || p.Y < mn.Y - cell * 0.5f || p.Y > mx.Y + cell * 0.5f)) continue;
                    int k = j * N + i;
                    float g = _ground(p.X, p.Y);
                    if (mx.Z > Top[k]) Top[k] = mx.Z;
                    if (mn.Z < g + BandHigh && mx.Z > g + BandLow) Blocked[k] = true;
                }
        }
        for (int k = 0; k < N; k++) { Blocked[k] = true; Blocked[(N - 1) * N + k] = true; Blocked[k * N] = true; Blocked[k * N + N - 1] = true; }
        Transform();
    }

    public float X(int i) => Origin + (i + 0.5f) * Cell;
    public int I(float x) => Math.Clamp((int)MathF.Floor((x - Origin) / Cell), 0, N - 1);
    public int Index(Vector2 p) => I(p.Y) * N + I(p.X);
    public Vector2 At(int k) => new(X(k % N), X(k / N));
    public float ClearanceAt(Vector2 p) => Clearance[Index(p)];
    public float GroundAt(Vector2 p) => _ground(p.X, p.Y);

    // Felzenszwalb & Huttenlocher's exact squared distance transform, two 1-D passes; clearance is the centre distance less half a cell (to the blocked cell's edge).
    void Transform()
    {
        const float Inf = 1e20f;
        var f = new float[N * N];
        for (int k = 0; k < f.Length; k++) f[k] = Blocked[k] ? 0f : Inf;
        var line = new float[N]; var outp = new float[N]; var v = new int[N]; var z = new float[N + 1];
        for (int j = 0; j < N; j++) { for (int i = 0; i < N; i++) line[i] = f[j * N + i]; Dt(line, outp, v, z); for (int i = 0; i < N; i++) f[j * N + i] = outp[i]; }
        for (int i = 0; i < N; i++) { for (int j = 0; j < N; j++) line[j] = f[j * N + i]; Dt(line, outp, v, z); for (int j = 0; j < N; j++) f[j * N + i] = outp[j]; }
        for (int k = 0; k < f.Length; k++) Clearance[k] = Blocked[k] ? 0f : MathF.Max(0f, MathF.Sqrt(f[k]) * Cell - Cell * 0.5f);
    }

    static void Dt(float[] f, float[] d, int[] v, float[] z)
    {
        int n = f.Length, k = 0;
        v[0] = 0; z[0] = float.NegativeInfinity; z[1] = float.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            float s;
            while (true)
            {
                int p = v[k];
                s = ((f[q] + q * q) - (f[p] + p * p)) / (2f * q - 2f * p);
                if (s <= z[k]) { k--; if (k < 0) { k = 0; break; } } else break;
            }
            k++; v[k] = q; z[k] = s; z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++) { while (z[k + 1] < q) k++; int p = v[k]; d[q] = (q - p) * (q - p) + f[p]; }
    }

    static readonly (int Dx, int Dy, float Cost)[] Steps = [(1, 0, 1f), (-1, 0, 1f), (0, 1, 1f), (0, -1, 1f), (1, 1, 1.41421356f), (1, -1, 1.41421356f), (-1, 1, 1.41421356f), (-1, -1, 1.41421356f)];

    /// <summary>The shortest walk (m) from <paramref name="a"/> to <paramref name="b"/> over cells with at least <paramref name="clearance"/> m (a body of that radius, or an army of twice that width), or null; with the cells.</summary>
    public (float Length, List<int> Path)? Shortest(Vector2 a, Vector2 b, float clearance, Func<int, bool>? forbid = null) => Shortest(a, b, clearance, forbid, 0f, 0f);

    /// <summary>
    /// The war's army route rule on this grid (owner, 2026-11-09, "route on avenues"; as <c>NavGraph.FindPathWide</c>): the shortest walk for a body of <paramref name="clearance"/> with each step's cost raised where
    /// the narrower of its two cells has less clearance than <paramref name="halfWidth"/> (cost times 1 + <paramref name="penalty"/> times the shortfall's share). The length is the walked metres.
    /// </summary>
    public (float Length, List<int> Path)? ShortestWide(Vector2 a, Vector2 b, float clearance, float halfWidth, float penalty)
    {
        var r = Shortest(a, b, clearance, null, halfWidth, penalty);
        if (r is not { } p) return null;
        float len = 0f;
        for (int i = 1; i < p.Path.Count; i++) len += Vector2.Distance(At(p.Path[i - 1]), At(p.Path[i]));
        return (len, p.Path);
    }

    (float Length, List<int> Path)? Shortest(Vector2 a, Vector2 b, float clearance, Func<int, bool>? forbid, float halfWidth, float penalty)
    {
        int s = Snap(a, clearance), t = Snap(b, clearance);
        if (s < 0 || t < 0) return null;
        var g = new float[N * N]; Array.Fill(g, float.MaxValue);
        var from = new int[N * N];
        var open = new PriorityQueue<int, float>();
        g[s] = 0f; open.Enqueue(s, 0f);
        int tx = t % N, ty = t / N;
        while (open.TryDequeue(out int k, out float pri))
        {
            if (k == t) break;
            int x = k % N, y = k / N;
            float h = Octile(x, y, tx, ty);
            if (pri > g[k] + h + 1e-3f) continue;
            foreach (var (dx, dy, cost) in Steps)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= N || ny >= N) continue;
                int m = ny * N + nx;
                if (Clearance[m] < clearance || (forbid is not null && forbid(m))) continue;
                float ng = g[k] + (penalty > 0f ? cost * (1f + penalty * MathF.Max(0f, halfWidth - MathF.Min(Clearance[k], Clearance[m])) / halfWidth) : cost);
                if (ng >= g[m]) continue;
                g[m] = ng; from[m] = k; open.Enqueue(m, ng + Octile(nx, ny, tx, ty));
            }
        }
        if (g[t] == float.MaxValue) return null;
        var path = new List<int>();
        for (int k = t; k != s; k = from[k]) path.Add(k);
        path.Add(s); path.Reverse();
        return (g[t] * Cell, path);
    }

    static float Octile(int x, int y, int tx, int ty) { int dx = Math.Abs(x - tx), dy = Math.Abs(y - ty); return Math.Max(dx, dy) + 0.41421356f * Math.Min(dx, dy); }

    /// <summary>The widest way from <paramref name="a"/> to <paramref name="b"/>: the path whose narrowest clearance is largest (twice it is the route's width at its narrowest point), that width, and where it is.</summary>
    public (float Width, Vector2 Pinch, List<int> Path) Widest(Vector2 a, Vector2 b)
    {
        int s = Index(a), t = Index(b);
        var best = new float[N * N]; Array.Fill(best, -1f);
        var from = new int[N * N];
        var open = new PriorityQueue<int, float>();
        best[s] = Clearance[s]; open.Enqueue(s, -best[s]);
        while (open.TryDequeue(out int k, out float pri))
        {
            if (k == t) break;
            if (-pri < best[k] - 1e-6f) continue;
            int x = k % N, y = k / N;
            foreach (var (dx, dy, _) in Steps)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= N || ny >= N) continue;
                int m = ny * N + nx;
                float w = MathF.Min(best[k], Clearance[m]);
                if (w <= best[m]) continue;
                best[m] = w; from[m] = k; open.Enqueue(m, -w);
            }
        }
        var path = new List<int>();
        if (best[t] < 0f) return (0f, a, path);
        int pinch = t;
        for (int k = t; k != s; k = from[k]) { path.Add(k); if (Clearance[k] < Clearance[pinch]) pinch = k; }
        path.Add(s); path.Reverse();
        return (2f * best[t], At(pinch), path);
    }

    /// <summary>The nearest cell to <paramref name="p"/> (within 30 m) with at least <paramref name="clearance"/>, or -1.</summary>
    public int Snap(Vector2 p, float clearance)
    {
        int c = Index(p);
        if (Clearance[c] >= clearance) return c;
        int cx = c % N, cy = c / N, reach = (int)(30f / Cell);
        int best = -1; float bd = float.MaxValue;
        for (int dy = -reach; dy <= reach; dy++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= N || y >= N) continue;
                int k = y * N + x; float d = dx * dx + dy * dy;
                if (Clearance[k] >= clearance && d < bd) { bd = d; best = k; }
            }
        return best;
    }

    /// <summary>Whether an eye at <paramref name="from"/> sees <paramref name="to"/> (both world points): no solid's top over the line between them, sampled every half cell (the ends' own cells skipped).</summary>
    public bool Sees(Vector3 from, Vector3 to)
    {
        var d = to - from; float len = new Vector2(d.X, d.Y).Length();
        int steps = Math.Max(1, (int)(len / (Cell * 0.5f)));
        int a = Index(new Vector2(from.X, from.Y)), b = Index(new Vector2(to.X, to.Y));
        for (int s = 1; s < steps; s++)
        {
            var p = from + d * (s / (float)steps);
            int k = Index(new Vector2(p.X, p.Y));
            if (k == a || k == b) continue;
            if (Top[k] > p.Z) return false;
        }
        return true;
    }

    /// <summary>The perpendicular clear width at a point of a route (metres left plus right of <paramref name="p"/> across <paramref name="dir"/> to the first blocked cell, each side capped at <paramref name="cap"/>).</summary>
    public float WidthAcross(Vector2 p, Vector2 dir, float cap = 60f)
    {
        var n = new Vector2(-dir.Y, dir.X);
        float Side(Vector2 s) { for (float t = 0f; t <= cap; t += Cell * 0.5f) { var q = p + s * t; if (MathF.Abs(q.X) >= AshfallMap.Half || MathF.Abs(q.Y) >= AshfallMap.Half || Blocked[Index(q)]) return t; } return cap; }
        return Side(n) + Side(-n);
    }
}
