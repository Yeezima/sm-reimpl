using System.Numerics;

namespace SmSim;

/// <summary>How a waypoint or graph link is reached from the one before it.</summary>
public enum NavLinkKind : byte
{
    /// <summary>Walking (level ground, slopes, steps up or down within the step height).</summary>
    Walk = 0,
    /// <summary>Walking off a ledge and falling onto lower ground (one way).</summary>
    Drop = 1,
    /// <summary>A jump-pack flight from the previous waypoint's cell to this one (one way; <see cref="NavJumpSet"/>).</summary>
    Jump = 2,
}

/// <summary>
/// What the navigation grid treats as walkable (test-bot aid, not original data). <see cref="From"/> takes the capsule and
/// motor limits of a <see cref="SimConfig"/>; the clearance radius is a little under the capsule's, since the motor slides
/// a capsule that grazes a wall.
/// </summary>
public sealed record NavSettings
{
    /// <summary>Grid step in metres.</summary>
    public float CellSize { get; init; } = 0.5f;
    /// <summary>Horizontal clearance (metres) a cell needs around its centre at knee, chest and head height.</summary>
    public float Radius { get; init; } = 0.4f;
    /// <summary>Headroom (metres) a cell needs above its floor (the capsule's height).</summary>
    public float Height { get; init; } = 1.9f;
    /// <summary>Highest step (metres) walked up between neighbouring cells on level ground (the motor's step height).</summary>
    public float StepHeight { get; init; } = 0.4f;
    /// <summary>Steepest walkable floor (degrees; the motor's limit).</summary>
    public float MaxSlopeDegrees { get; init; } = 60f;
    /// <summary>Highest drop (metres) a drop link may fall (no fall damage exists; this only keeps bots off very long falls).</summary>
    public float MaxDrop { get; init; } = 8f;
    /// <summary>Farthest (metres, horizontally) past a ledge a drop link may land.</summary>
    public float MaxDropReach { get; init; } = 1.5f;

    /// <summary>Settings from the simulation's capsule and motor (radius x 0.9, height, step, slope).</summary>
    public static NavSettings From(SimConfig c) => new()
    {
        Radius = MathF.Max(0.1f, c.CapsuleRadius * 0.9f), Height = MathF.Max(0.5f, c.CapsuleHeight), StepHeight = MathF.Max(0f, c.StepHeight),
        MaxSlopeDegrees = Math.Clamp(c.MaxWalkableSlope, 1f, 85f),
    };

    internal ulong Hash()
    {
        ulong h = Geometry.FnvOffset;
        foreach (float f in new[] { CellSize, Radius, Height, StepHeight, MaxSlopeDegrees, MaxDrop, MaxDropReach })
            h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(f));
        return h;
    }
}

/// <summary>
/// Where to build a <see cref="NavGraph"/>: the ground-plane box (<see cref="Min"/>, <see cref="Max"/>) and the seeds (spawn
/// points, objectives) whose reachable cells are kept; cells no seed reaches by walking or dropping are left out. No seeds:
/// every cell in the box is kept.
/// </summary>
public sealed record NavArea(Vector2 Min, Vector2 Max, IReadOnlyList<Vector3> Seeds)
{
    /// <summary>The box around <paramref name="points"/> grown by <paramref name="margin"/> metres, with the points as seeds.</summary>
    public static NavArea Around(IReadOnlyList<Vector3> points, float margin)
    {
        if (points.Count == 0) throw new ArgumentException("No points.", nameof(points));
        var lo = new Vector2(points.Min(p => p.X), points.Min(p => p.Y)) - new Vector2(margin);
        var hi = new Vector2(points.Max(p => p.X), points.Max(p => p.Y)) + new Vector2(margin);
        return new NavArea(lo, hi, points.ToArray());
    }

    internal ulong Hash(ulong h)
    {
        foreach (float f in new[] { Min.X, Min.Y, Max.X, Max.Y }) h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(f));
        foreach (var p in Seeds)
            foreach (float f in new[] { p.X, p.Y, p.Z }) h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(f));
        return h;
    }
}

/// <summary>A point of a path and how it is reached from the previous one.</summary>
/// <param name="Jump">For a <see cref="NavLinkKind.Jump"/> waypoint: the flight (take-off node, heading, hold, power); else default.</param>
public readonly record struct NavWaypoint(Vector3 Position, NavLinkKind Kind, int Node, NavJumpMove Jump = default);

/// <summary>
/// A path from <see cref="NavGraph.FindPath"/>: smoothed waypoints (the first is the start's cell), its length along the
/// waypoints, and whether it ends at the goal's cell or only as near to it as the graph allows (<see cref="Partial"/>).
/// </summary>
public sealed record NavPath(IReadOnlyList<NavWaypoint> Waypoints, float Length, bool Partial, int Expanded, int Cells);

/// <summary>
/// Walkable grid over a level's character collision for the test bots' navigation (test aid; the original AI's navigation is
/// not reproduced). Built once per character mesh (so a per-side variant of the mesh gets its own graph): columns of
/// <see cref="NavSettings.CellSize"/>, each holding every floor a standing capsule fits on (walkable slope, headroom,
/// horizontal clearance at knee, chest and head height, outside every kill volume). Walk links join the 8 neighbours when
/// the height change is a step or the slope's rise and nothing is in the way at knee, chest and head height (diagonals only
/// past two walkable sides, so corners are not cut); drop links (one way) go off ledges onto lower ground within
/// <see cref="NavSettings.MaxDrop"/> when the fall crosses no kill volume (with the body's radius as margin). Drops are one
/// way: ground reached only by dropping, with no walk or drop leading out (a pit without stairs), is a dead end, and
/// paths from there to the rest of the map are partial; kill volumes themselves are never cells. <see cref="FindPath"/> runs A* and pulls the
/// string through straight walkable lines. Thread-safe for reading once built.
/// </summary>
public sealed class NavGraph
{
    // ************************************************************************************************************
    // Bump FormatVersion on ANY change to how the graph is built or written that the constants below do not capture
    // (a new test, a changed formula): cached graphs are keyed by it and would otherwise be reused stale.
    // ************************************************************************************************************
    const int FormatVersion = 3;

    // Build-rule constants (metres). All of them are hashed into the cache key (KeyFor), so changing one rebuilds caches.
    const float FloorSpacing = 0.2f;        // floors in one column closer than this are one floor
    const float ProbeStep = 0.02f;          // the downward floor probe continues this far below each surface
    const float HeadroomLift = 0.05f;       // headroom and kill tests start this far above the floor
    const float KneeAboveStep = 0.15f;      // clearance test height above the step height (plus the slope's rise)
    const float ChestHeight = 1.0f;         // clearance and link tests at chest height
    const float HeadBelowTop = 0.1f;        // ... and this far below the capsule's top
    const float StepSlack = 0.02f;          // a walk link may rise the step height plus this
    const float SlopeSlack = 0.05f;         // ... or the slope's rise over the link plus this
    const float LowRayAboveStep = 0.05f;    // the link's knee ray: this above the step over the lower floor
    const float LowRayAboveRise = 0.1f;     // ... or this above the link's own rise
    const float DiagonalTolerance = 0.3f;   // a diagonal's sides need a floor within the step height plus this of both ends
    const float DropProbeLift = 0.3f;       // the drop's ground probe starts this far above the ledge
    const float DropMinFall = 0.1f;         // a drop falls at least the step height plus this
    const float LandingTolerance = 0.3f;    // a landing cell's floor lies within this of the probed ground
    const float DropLift = 0.1f;            // kill tests of a drop's path run this far above the feet
    const float DropOvershoot = 1.0f;       // ... and this far past the landing
    static readonly float[] BuildRules =
    [
        FloorSpacing, ProbeStep, HeadroomLift, KneeAboveStep, ChestHeight, HeadBelowTop, StepSlack, SlopeSlack, LowRayAboveStep,
        LowRayAboveRise, DiagonalTolerance, DropProbeLift, DropMinFall, LandingTolerance, DropLift, DropOvershoot,
    ];
    static readonly (int X, int Y)[] Dirs = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];

    readonly float _x0, _y0, _cell;
    readonly int _w, _h;
    readonly int[] _colStart;          // per column (x + y * w): first node; length w*h + 1
    readonly float[] _z;               // per node: floor height
    readonly int[] _col;               // per node: column
    readonly int[] _linkStart;         // per node: first link; length N + 1
    readonly int[] _linkTo;
    readonly float[] _linkCost;
    readonly byte[] _linkKind;

    /// <summary>The settings it was built with.</summary>
    public NavSettings Settings { get; }
    /// <summary>Walkable cells (nodes).</summary>
    public int NodeCount => _z.Length;
    /// <summary>All links (walk links count once per direction).</summary>
    public int LinkCount => _linkTo.Length;
    /// <summary>Drop links.</summary>
    public int DropLinkCount { get; }
    /// <summary>Jump-pack links (null: none; paths never fly). Attached with <see cref="WithJumps"/>.</summary>
    public NavJumpSet? Jumps { get; private init; }
    /// <summary>Grid size in cells.</summary>
    public int Width => _w;
    public int Depth => _h;
    /// <summary>Milliseconds the build took (0 when read from a cache).</summary>
    public long BuildMilliseconds { get; init; }
    /// <summary>How the build went (phase times, cells before pruning), for logs.</summary>
    public string BuildNote { get; init; } = "";
    /// <summary>Content hash of the mesh and kill volumes it was built from, with the settings (the cache key).</summary>
    public ulong SourceHash { get; }

    NavGraph(NavSettings s, float x0, float y0, int w, int h, int[] colStart, float[] z, int[] col, int[] linkStart, int[] linkTo, float[] linkCost, byte[] linkKind, ulong sourceHash)
    {
        Settings = s; _x0 = x0; _y0 = y0; _cell = s.CellSize; _w = w; _h = h;
        _colStart = colStart; _z = z; _col = col; _linkStart = linkStart; _linkTo = linkTo; _linkCost = linkCost; _linkKind = linkKind;
        SourceHash = sourceHash;
        int drops = 0;
        foreach (byte k in linkKind) if (k == (byte)NavLinkKind.Drop) drops++;
        DropLinkCount = drops;
    }

    /// <summary>The key of the jump link from <paramref name="from"/> to <paramref name="to"/> (for <see cref="FindPath"/>'s exclusions).</summary>
    public static long JumpKey(int from, int to) => ((long)from << 32) | (uint)to;

    /// <summary>The same graph with <paramref name="jumps"/> (found for exactly this graph's nodes) usable by <see cref="FindPath"/>.</summary>
    public NavGraph WithJumps(NavJumpSet jumps)
    {
        ArgumentNullException.ThrowIfNull(jumps);
        if (jumps.NodeCount != NodeCount) throw new ArgumentException("The jump links are for another graph.", nameof(jumps));
        return new NavGraph(Settings, _x0, _y0, _w, _h, _colStart, _z, _col, _linkStart, _linkTo, _linkCost, _linkKind, SourceHash)
        {
            BuildMilliseconds = BuildMilliseconds, BuildNote = BuildNote, Jumps = jumps,
        };
    }

    /// <summary>The cache key of a graph built from <paramref name="character"/> and <paramref name="killVolumes"/> with <paramref name="settings"/> over <paramref name="area"/>.</summary>
    public static ulong KeyFor(StaticMesh character, IReadOnlyList<KillVolume>? killVolumes, NavSettings settings, NavArea? area = null)
    {
        ulong h = Geometry.Fnv(Geometry.Fnv(KillVolume.Hash(Geometry.Fnv(Geometry.FnvOffset, character.ContentHash), killVolumes), settings.Hash()), FormatVersion);
        foreach (float f in BuildRules) h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(f));
        return area is null ? h : area.Hash(h);
    }

    /// <summary>Position of node <paramref name="node"/> (cell centre on its floor).</summary>
    public Vector3 Position(int node)
    {
        int c = _col[node];
        return new Vector3(_x0 + (c % _w) * _cell, _y0 + (c / _w) * _cell, _z[node]);
    }

    /// <summary>The links of <paramref name="node"/>: (target node, cost, kind).</summary>
    public IEnumerable<(int To, float Cost, NavLinkKind Kind)> Links(int node)
    {
        for (int l = _linkStart[node]; l < _linkStart[node + 1]; l++) yield return (_linkTo[l], _linkCost[l], (NavLinkKind)_linkKind[l]);
    }

    // ------------------------------------------------------------------ build

    /// <summary>
    /// Builds the graph over <paramref name="character"/> (the mesh the motor walks on), avoiding <paramref name="killVolumes"/>,
    /// within <paramref name="area"/> (default: the mesh's bounds, every cell kept). Runs the columns in parallel; deterministic.
    /// </summary>
    public static NavGraph Build(StaticMesh character, IReadOnlyList<KillVolume>? killVolumes, NavSettings settings, NavArea? area = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(settings);
        if (!(settings.CellSize > 0.05f)) throw new ArgumentException("Cell size must be above 5 cm.", nameof(settings));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var kills = killVolumes?.ToArray() ?? [];
        float cell = settings.CellSize;
        ulong key = KeyFor(character, kills, settings, area);
        var lo = Vector2.Max(area?.Min ?? new Vector2(float.MinValue), new Vector2(character.Min.X, character.Min.Y));
        var hi = Vector2.Min(area?.Max ?? new Vector2(float.MaxValue), new Vector2(character.Max.X, character.Max.Y));
        if (character.TriangleCount == 0 || !(hi.X > lo.X) || !(hi.Y > lo.Y))
            return new NavGraph(settings, 0, 0, 1, 1, [0, 0], [], [], [0], [], [], [], key) { BuildMilliseconds = watch.ElapsedMilliseconds };
        float x0 = MathF.Floor(lo.X / cell) * cell, y0 = MathF.Floor(lo.Y / cell) * cell;
        int w = (int)MathF.Ceiling((hi.X - x0) / cell) + 1, h = (int)MathF.Ceiling((hi.Y - y0) / cell) + 1;
        if ((long)w * h > 40_000_000) throw new ArgumentException($"Navigation grid of {w} x {h} cells is too large.");
        float cosMax = MathF.Cos(settings.MaxSlopeDegrees * MathF.PI / 180f);
        float top = character.Max.Z + 1f, bottom = character.Min.Z - 1f;

        // 1. Floor candidates per column (cheap tests: walkable slope, headroom, outside every kill volume).
        var rows = new (float Z, float Nz)[h][][];
        Parallel.For(0, h, y =>
        {
            var row = new (float, float)[w][];
            var floors = new List<(float, float)>(4);
            for (int x = 0; x < w; x++)
            {
                floors.Clear();
                float px = x0 + x * cell, py = y0 + y * cell;
                var o = new Vector3(px, py, top);
                for (int k = 0; k < 32 && o.Z > bottom; k++)
                {
                    if (character.Raycast(o, -Vector3.UnitZ, o.Z - bottom) is not { } hit) break;
                    float z = hit.Point.Z;
                    if (hit.Normal.Z >= cosMax && (floors.Count == 0 || floors[^1].Item1 - z > FloorSpacing) && Roomy(character, kills, settings, px, py, z))
                        floors.Add((z, hit.Normal.Z));
                    o = new Vector3(px, py, z - ProbeStep);
                }
                row[x] = floors.Count == 0 ? [] : floors.ToArray();
            }
            rows[y] = row;
        });
        var colStart = new int[w * h + 1];
        int n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) { colStart[x + y * w] = n; n += rows[y][x].Length; }
        colStart[w * h] = n;
        var zs = new float[n];
        var nzs = new float[n];
        var cols = new int[n];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int c = x + y * w, i = colStart[c];
                foreach (var (z, nz) in rows[y][x]) { zs[i] = z; nzs[i] = nz; cols[i] = c; i++; }
            }
        rows = null!;
        long floorsMs = watch.ElapsedMilliseconds;
        int candidates = n;

        // 2. From the seeds outward, a frontier at a time (in parallel): each reached cell's links (walk to the 8 neighbours,
        //    drops where no walk link goes). A cell's clearance is tested once, when first needed.
        var state = new byte[n];   // 0 not tested, 1 standable, 2 not
        bool Clear(int i)
        {
            byte st = Volatile.Read(ref state[i]);
            if (st == 0)
            {
                int c = cols[i];
                st = Standable(character, settings, x0 + (c % w) * cell, y0 + (c / w) * cell, zs[i], nzs[i]) ? (byte)1 : (byte)2;
                Volatile.Write(ref state[i], st);
            }
            return st == 1;
        }
        var reached = new bool[n];
        var frontier = new List<int>();
        if (area is { Seeds.Count: > 0 })
        {
            foreach (var seed in area.Seeds)
            {
                // The standable floor nearest the seed within 2 cells and 3 m of height.
                int sx = (int)MathF.Round((seed.X - x0) / cell), sy = (int)MathF.Round((seed.Y - y0) / cell);
                int best = -1;
                float bestScore = float.MaxValue;
                for (int yy = sy - 2; yy <= sy + 2; yy++)
                    for (int xx = sx - 2; xx <= sx + 2; xx++)
                    {
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        for (int i = colStart[xx + yy * w]; i < colStart[xx + yy * w + 1]; i++)
                        {
                            float dz = MathF.Abs(seed.Z - zs[i]);
                            float score = dz + (MathF.Abs(xx - sx) + MathF.Abs(yy - sy)) * cell;
                            if (dz < 3f && score < bestScore && Clear(i)) { bestScore = score; best = i; }
                        }
                    }
                if (best >= 0 && !reached[best]) { reached[best] = true; frontier.Add(best); }
            }
        }
        else
        {
            Parallel.For(0, n, i => Clear(i));
            for (int i = 0; i < n; i++) if (state[i] == 1) { reached[i] = true; frontier.Add(i); }
        }
        var adjacency = new List<(int To, float Cost, byte Kind)>[n];
        while (frontier.Count > 0)
        {
            var current = frontier;
            var found = new List<(int To, float Cost, byte Kind)>[current.Count];
            Parallel.For(0, current.Count, f =>
            {
                int a = current[f], c = cols[a], x = c % w, y = c / w;
                var list = new List<(int To, float Cost, byte Kind)>(8);
                Span<bool> walked = stackalloc bool[8];
                for (int d = 0; d < 8; d++)
                {
                    int b = WalkTarget(character, settings, zs, nzs, colStart, w, h, x0, y0, cell, a, x, y, d, Clear);
                    if (b < 0) continue;
                    walked[d] = true;
                    float horizontal = (d % 2 == 1 ? MathF.Sqrt(2f) : 1f) * cell;
                    list.Add((b, MathF.Sqrt(horizontal * horizontal + (zs[b] - zs[a]) * (zs[b] - zs[a])), (byte)NavLinkKind.Walk));
                }
                for (int d = 0; d < 8; d++)
                    if (!walked[d] && DropTarget(character, kills, settings, zs, colStart, w, h, x0, y0, cell, a, x, y, d, Clear) is { } drop
                        && !list.Exists(l => l.To == drop.To))
                        list.Add((drop.To, drop.Cost, (byte)NavLinkKind.Drop));
                found[f] = list;
            });
            var next = new List<int>();
            for (int f = 0; f < current.Count; f++)
            {
                adjacency[current[f]] = found[f];
                foreach (var l in found[f])
                    if (!reached[l.To]) { reached[l.To] = true; next.Add(l.To); }
            }
            frontier = next;
        }
        long linksMs = watch.ElapsedMilliseconds;
        int tested = 0;
        foreach (byte st in state) if (st != 0) tested++;

        // 3. Keep the reached cells, renumbered in column order.
        var index = new int[n];
        int kept = 0;
        for (int a = 0; a < n; a++) index[a] = reached[a] ? kept++ : -1;
        {
            var colStart2 = new int[w * h + 1];
            var zs2 = new float[kept];
            var cols2 = new int[kept];
            var adjacency2 = new List<(int To, float Cost, byte Kind)>[kept];
            int k = 0;
            for (int c = 0; c < w * h; c++)
            {
                colStart2[c] = k;
                for (int a = colStart[c]; a < colStart[c + 1]; a++)
                {
                    if (index[a] < 0) continue;
                    zs2[k] = zs[a]; cols2[k] = c;
                    adjacency2[k] = adjacency[a].Select(l => (index[l.To], l.Cost, l.Kind)).ToList();
                    k++;
                }
            }
            colStart2[w * h] = k;
            (colStart, zs, cols, adjacency, n) = (colStart2, zs2, cols2, adjacency2, kept);
        }
        string note = $"{w} x {h} columns, {candidates} floor candidates, {tested} clearance-tested, {kept} kept" +
                      (area is { Seeds.Count: > 0 } ? $" (reached from {area.Seeds.Count} seeds)" : "") +
                      $"; floors {floorsMs} ms, links {linksMs - floorsMs} ms";

        var linkStart = new int[n + 1];
        int links = 0;
        for (int a = 0; a < n; a++) { linkStart[a] = links; links += adjacency[a].Count; }
        linkStart[n] = links;
        var linkTo = new int[links];
        var linkCost = new float[links];
        var linkKind = new byte[links];
        for (int a = 0; a < n; a++)
        {
            int i = linkStart[a];
            foreach (var (to, cost, kind) in adjacency[a].OrderBy(l => l.To)) { linkTo[i] = to; linkCost[i] = cost; linkKind[i] = kind; i++; }
        }
        return new NavGraph(settings, x0, y0, w, h, colStart, zs, cols, linkStart, linkTo, linkCost, linkKind, key)
        {
            BuildMilliseconds = watch.ElapsedMilliseconds,
            BuildNote = note + $", packing {watch.ElapsedMilliseconds - linksMs} ms",
        };
    }

    static float Tan(float nz) => MathF.Sqrt(MathF.Max(0f, 1f - nz * nz)) / MathF.Max(nz, 1e-3f);

    /// <summary>Headroom above the floor and no kill volume over the body's height.</summary>
    static bool Roomy(StaticMesh mesh, KillVolume[] kills, NavSettings s, float x, float y, float z)
    {
        if (mesh.Raycast(new Vector3(x, y, z + HeadroomLift), Vector3.UnitZ, s.Height - HeadroomLift) is not null) return false;
        foreach (var k in kills)
            if (k.Crosses(new Vector3(x, y, z + HeadroomLift), new Vector3(x, y, z + s.Height))) return false;
        return true;
    }

    /// <summary>Horizontal clearance of <see cref="NavSettings.Radius"/> in 8 directions at knee (above a step and the slope's rise), chest and head height.</summary>
    static bool Standable(StaticMesh mesh, NavSettings s, float x, float y, float z, float nz)
    {
        float low = s.StepHeight + KneeAboveStep + s.Radius * Tan(nz);
        float diagonal = MathF.Sqrt(0.5f);
        Span<float> heights = [low, ChestHeight, s.Height - HeadBelowTop];
        foreach (float hgt in heights)
        {
            if (hgt >= s.Height) continue;
            var o = new Vector3(x, y, z + hgt);
            for (int d = 0; d < 8; d++)
            {
                var (dx, dy) = Dirs[d];
                var dir = d % 2 == 1 ? new Vector3(dx * diagonal, dy * diagonal, 0f) : new Vector3(dx, dy, 0f);
                if (mesh.Raycast(o, dir, s.Radius) is not null) return false;
            }
        }
        return true;
    }

    /// <summary>The neighbour node in direction <paramref name="d"/> (0..7) node <paramref name="a"/> walks to, or -1.</summary>
    static int WalkTarget(StaticMesh mesh, NavSettings s, float[] zs, float[] nzs, int[] colStart, int w, int h, float x0, float y0, float cell, int a, int x, int y, int d, Func<int, bool> clear)
    {
        var (dx, dy) = Dirs[d];
        int nx = x + dx, ny = y + dy;
        if (nx < 0 || ny < 0 || nx >= w || ny >= h) return -1;
        float za = zs[a];
        float horizontal = (d % 2 == 1 ? MathF.Sqrt(2f) : 1f) * cell;
        int best = -1;
        float bestDz = float.MaxValue;
        for (int b = colStart[nx + ny * w]; b < colStart[nx + ny * w + 1]; b++)
        {
            float dz = MathF.Abs(zs[b] - za);
            float allowed = MathF.Max(s.StepHeight + StepSlack, horizontal * Tan(MathF.Min(nzs[a], nzs[b])) + SlopeSlack);
            if (dz <= allowed && dz < bestDz && clear(b)) { best = b; bestDz = dz; }
        }
        if (best < 0) return -1;
        float zb = zs[best];
        if (d % 2 == 1)
        {
            // Diagonal: both sides walkable at a height between the two (no corner cutting).
            if (!HasFloorNear(zs, colStart, (x + dx) + y * w, za, zb, s.StepHeight + DiagonalTolerance, clear) || !HasFloorNear(zs, colStart, x + (y + dy) * w, za, zb, s.StepHeight + DiagonalTolerance, clear)) return -1;
        }
        var from = new Vector2(x0 + x * cell, y0 + y * cell);
        var dir = Vector3.Normalize(new Vector3(dx, dy, 0f));
        float lowZ = MathF.Min(za, zb), highZ = MathF.Max(za, zb);
        float low = lowZ + MathF.Max(s.StepHeight + LowRayAboveStep, bestDz + LowRayAboveRise);
        foreach (float hz in new[] { low, highZ + ChestHeight, highZ + s.Height - HeadBelowTop })
        {
            if (mesh.Raycast(new Vector3(from.X, from.Y, hz), dir, horizontal) is not null) return -1;
        }
        return best;
    }

    static bool HasFloorNear(float[] zs, int[] colStart, int column, float za, float zb, float tolerance, Func<int, bool> clear)
    {
        for (int i = colStart[column]; i < colStart[column + 1]; i++)
            if (MathF.Abs(zs[i] - za) <= tolerance && MathF.Abs(zs[i] - zb) <= tolerance && clear(i)) return true;
        return false;
    }

    /// <summary>A drop from node <paramref name="a"/> in direction <paramref name="d"/> (0..7) onto a lower node, or null.</summary>
    static (int To, float Cost)? DropTarget(StaticMesh mesh, KillVolume[] kills, NavSettings s, float[] zs, int[] colStart, int w, int h, float x0, float y0, float cell, int a, int x, int y, int d, Func<int, bool> clear)
    {
        var (dx, dy) = Dirs[d];
        var dir = Vector3.Normalize(new Vector3(dx, dy, 0f));
        float za = zs[a];
        var from = new Vector3(x0 + x * cell, y0 + y * cell, za);
        for (float reach = cell * 0.5f; reach <= s.MaxDropReach + 1e-3f; reach += cell * 0.5f)
        {
            // Nothing in the way at knee, chest and head height up to here (a wall or railing ends the search).
            foreach (float hz in new[] { s.StepHeight + LowRayAboveStep, ChestHeight, s.Height - HeadBelowTop })
                if (mesh.Raycast(from + new Vector3(0, 0, hz), dir, reach + s.Radius) is not null) return null;
            var p = from + dir * reach;
            if (mesh.Raycast(new Vector3(p.X, p.Y, za + DropProbeLift), -Vector3.UnitZ, s.MaxDrop + DropProbeLift) is not { } ground) continue;
            float gz = ground.Point.Z;
            float fall = za - gz;
            if (fall <= s.StepHeight + DropMinFall) continue;   // ground at about the same height: not a drop
            int cx = (int)MathF.Round((p.X - x0) / cell), cy = (int)MathF.Round((p.Y - y0) / cell);
            if (cx < 0 || cy < 0 || cx >= w || cy >= h) return null;
            int landing = -1;
            for (int b = colStart[cx + cy * w]; b < colStart[cx + cy * w + 1]; b++)
                if (MathF.Abs(zs[b] - gz) < LandingTolerance && clear(b)) { landing = b; break; }
            if (landing < 0) continue;   // not standable there (a sloped roof, a narrow gap): try farther
            // The fall must stay clear of kill volumes however it goes: straight from the ledge cell to the landing cell, down
            // from the probe, the body standing on the landing, and an overshoot of a metre past it (a fall keeps its speed).
            var land = new Vector3(x0 + cx * cell, y0 + cy * cell, zs[landing]);
            var lift = new Vector3(0, 0, DropLift);
            var edge = new Vector3(p.X, p.Y, za);
            foreach (var volume in kills)
            {
                var k = new KillVolume(volume.Center, volume.HalfExtents + new Vector3(s.Radius), volume.Yaw);   // with the body's radius as margin
                if (k.Crosses(from + lift, edge + lift) || k.Crosses(from + lift, land + lift) || k.Crosses(edge, new Vector3(p.X, p.Y, gz) + lift)
                    || k.Crosses(land, land + new Vector3(0, 0, s.Height)) || k.Crosses(land + lift, land + dir * DropOvershoot + lift)) return null;
            }
            return (landing, reach + fall * 0.5f + 1f);
        }
        return null;
    }

    // ------------------------------------------------------------------ jump-link builder helpers

    /// <summary>The grid cell (column x, y) of a node.</summary>
    internal (int X, int Y) CellOf(int node) => (_col[node] % _w, _col[node] / _w);

    /// <summary>Walk links leaving a node (8 in the open).</summary>
    internal int WalkLinkCount(int node)
    {
        int n = 0;
        for (int l = _linkStart[node]; l < _linkStart[node + 1]; l++) if (_linkKind[l] == (byte)NavLinkKind.Walk) n++;
        return n;
    }

    internal sealed class DijkstraScratch(int n)
    {
        public readonly float[] Dist = new float[n];
        public readonly int[] Stamp = new int[n];
        public readonly int[] Done = new int[n];
        public int Generation;
        public readonly PriorityQueue<int, float> Open = new();
    }

    /// <summary>
    /// The cost of the cheapest walk-and-drop route from <paramref name="a"/> to each of <paramref name="targets"/>
    /// (infinity beyond <paramref name="limit"/>); jump links are not used. Stops when every target is settled.
    /// </summary>
    internal void WalkCosts(int a, int[] targets, float limit, DijkstraScratch sc, float[] costs)
    {
        int gen = ++sc.Generation;
        if (gen == int.MaxValue) { Array.Clear(sc.Stamp); Array.Clear(sc.Done); sc.Generation = gen = 1; }
        Array.Fill(costs, float.PositiveInfinity);
        sc.Open.Clear();
        sc.Dist[a] = 0f; sc.Stamp[a] = gen;
        sc.Open.Enqueue(a, 0f);
        var wanted = new HashSet<int>(targets);
        while (wanted.Count > 0 && sc.Open.TryDequeue(out int cur, out float d))
        {
            if (sc.Done[cur] == gen) continue;
            sc.Done[cur] = gen;
            if (d > limit) break;
            if (wanted.Remove(cur))
                for (int k = 0; k < targets.Length; k++)
                    if (targets[k] == cur) costs[k] = d;
            for (int l = _linkStart[cur]; l < _linkStart[cur + 1]; l++)
            {
                int nb = _linkTo[l];
                if (sc.Done[nb] == gen) continue;
                float g = d + _linkCost[l];
                if (sc.Stamp[nb] == gen && sc.Dist[nb] <= g) continue;
                sc.Stamp[nb] = gen; sc.Dist[nb] = g;
                sc.Open.Enqueue(nb, g);
            }
        }
    }

    // ------------------------------------------------------------------ queries

    /// <summary>The column of a position, or -1 outside the grid.</summary>
    int ColumnOf(float px, float py)
    {
        int x = (int)MathF.Round((px - _x0) / _cell), y = (int)MathF.Round((py - _y0) / _cell);
        return x < 0 || y < 0 || x >= _w || y >= _h ? -1 : x + y * _w;
    }

    /// <summary>
    /// The node nearest to <paramref name="p"/> (feet): preferably the floor just under the feet in its column, else the
    /// nearest within <paramref name="radius"/> metres horizontally whose height is within <paramref name="maxDz"/>; -1 if none.
    /// </summary>
    public int Nearest(Vector3 p, float radius = 2f, float maxDz = 2.5f)
    {
        if (NodeCount == 0) return -1;
        int cx = (int)MathF.Round((p.X - _x0) / _cell), cy = (int)MathF.Round((p.Y - _y0) / _cell);
        int rings = (int)MathF.Ceiling(radius / _cell);
        int best = -1;
        float bestScore = float.MaxValue;
        for (int r = 0; r <= rings; r++)
        {
            for (int y = cy - r; y <= cy + r; y++)
                for (int x = cx - r; x <= cx + r; x++)
                {
                    if (Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)) != r || x < 0 || y < 0 || x >= _w || y >= _h) continue;
                    int c = x + y * _w;
                    for (int i = _colStart[c]; i < _colStart[c + 1]; i++)
                    {
                        float dz = p.Z - _z[i];   // positive: the floor is under the feet
                        if (dz < -maxDz || dz > maxDz) continue;
                        float hx = _x0 + x * _cell - p.X, hy = _y0 + y * _cell - p.Y;
                        float score = MathF.Sqrt(hx * hx + hy * hy) + (dz >= -0.5f ? MathF.Abs(dz) * 0.5f : -dz * 2f);
                        if (score < bestScore) { bestScore = score; best = i; }
                    }
                }
            // A ring farther out cannot beat a found node by more than a cell.
            if (best >= 0 && bestScore < (r - 1) * _cell) break;
        }
        return best;
    }

    /// <summary>
    /// Whether a straight walk from node <paramref name="a"/> to node <paramref name="b"/> stays on walk links (every cell the
    /// line crosses is walkable and linked to the one before).
    /// </summary>
    public bool StraightWalk(int a, int b)
    {
        if (a == b) return true;
        var pa = Position(a); var pb = Position(b);
        float dx = pb.X - pa.X, dy = pb.Y - pa.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        int steps = Math.Max(1, (int)MathF.Ceiling(length / (_cell * 0.25f)));
        int cur = a;
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            int col = ColumnOf(pa.X + dx * t, pa.Y + dy * t);
            if (col < 0) return false;
            if (col == _col[cur]) continue;
            int next = -1;
            for (int l = _linkStart[cur]; l < _linkStart[cur + 1]; l++)
                if (_linkKind[l] == (byte)NavLinkKind.Walk && _col[_linkTo[l]] == col) { next = _linkTo[l]; break; }
            if (next < 0) return false;
            cur = next;
        }
        return cur == b;
    }

    /// <summary>
    /// Whether a short straight move from <paramref name="from"/> by <paramref name="offset"/> (ground plane) stays on walkable,
    /// linked cells (for strafing). False off the graph.
    /// </summary>
    public bool CanWalk(Vector3 from, Vector2 offset)
    {
        int a = Nearest(from, _cell * 1.5f, 1.5f);
        if (a < 0) return false;
        var to = new Vector3(from.X + offset.X, from.Y + offset.Y, _z[a]);
        int col = ColumnOf(to.X, to.Y);
        if (col < 0) return false;
        // The target node: the one the straight walk reaches.
        var pa = Position(a);
        float dx = to.X - pa.X, dy = to.Y - pa.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        int steps = Math.Max(1, (int)MathF.Ceiling(length / (_cell * 0.25f)));
        int cur = a;
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            int c = ColumnOf(pa.X + dx * t, pa.Y + dy * t);
            if (c < 0) return false;
            if (c == _col[cur]) continue;
            int next = -1;
            for (int l = _linkStart[cur]; l < _linkStart[cur + 1]; l++)
                if (_linkKind[l] == (byte)NavLinkKind.Walk && _col[_linkTo[l]] == c) { next = _linkTo[l]; break; }
            if (next < 0) return false;
            cur = next;
        }
        return true;
    }

    [ThreadStatic] static SearchScratch? t_search;

    sealed class SearchScratch(int n)
    {
        public readonly float[] G = new float[n];
        public readonly int[] Parent = new int[n];
        public readonly byte[] Via = new byte[n];
        public readonly int[] ViaJump = new int[n];
        public readonly int[] Stamp = new int[n];
        public readonly int[] Closed = new int[n];
        public int Generation;
        public readonly PriorityQueue<int, float> Open = new();
    }

    /// <summary>
    /// A path from <paramref name="from"/> to <paramref name="to"/> (feet positions): both snapped to the nearest node (the
    /// start within 3 m, the goal within <paramref name="goalSnap"/> m), A* over the links (cost: 3D length; drops as
    /// built), then string-pulled: each waypoint is the farthest node of the cell path still reached by a straight walk;
    /// a drop's or flight's take-off and landing are always kept (flights only with <see cref="Jumps"/> attached). When the goal's node cannot be reached the path ends at the reached node
    /// nearest to it (<see cref="NavPath.Partial"/>). Null when either end is off the graph. At most
    /// <paramref name="maxExpanded"/> nodes are expanded. Jump links whose <see cref="JumpKey"/> is in <paramref name="excludedJumps"/> are not used.
    /// </summary>
    public NavPath? FindPath(Vector3 from, Vector3 to, float goalSnap = 10f, int maxExpanded = 400_000, IReadOnlySet<long>? excludedJumps = null)
    {
        int start = Nearest(from, 3f, 3f);
        int goal = Nearest(to, goalSnap, 6f);
        if (start < 0 || goal < 0) return null;
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
        sc.G[start] = 0f; sc.Parent[start] = -1; sc.Stamp[start] = gen; sc.Via[start] = 0;
        sc.Open.Enqueue(start, H(start));
        int bestNode = start;
        float bestH = H(start);
        int expanded = 0;
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
                float g = gc + _linkCost[l];
                if (sc.Stamp[nb] == gen && sc.G[nb] <= g) continue;
                sc.Stamp[nb] = gen; sc.G[nb] = g; sc.Parent[nb] = cur; sc.Via[nb] = _linkKind[l]; sc.ViaJump[nb] = -1;
                sc.Open.Enqueue(nb, g + H(nb));
            }
            if (Jumps is { } js)
                for (int l = js.Start(cur); l < js.End(cur); l++)
                {
                    int nb = js.To(l);
                    if (sc.Closed[nb] == gen) continue;
                    if (excludedJumps is { Count: > 0 } && excludedJumps.Contains(JumpKey(cur, nb))) continue;
                    float g = gc + js.Cost(l);
                    if (sc.Stamp[nb] == gen && sc.G[nb] <= g) continue;
                    sc.Stamp[nb] = gen; sc.G[nb] = g; sc.Parent[nb] = cur; sc.Via[nb] = (byte)NavLinkKind.Jump; sc.ViaJump[nb] = l;
                    sc.Open.Enqueue(nb, g + H(nb));
                }
        }
        int end = found ? goal : bestNode;
        var cells = new List<(int Node, NavLinkKind Via, int Jump)>();
        for (int i = end; i >= 0; i = sc.Parent[i]) cells.Add((i, (NavLinkKind)sc.Via[i], (NavLinkKind)sc.Via[i] == NavLinkKind.Jump ? sc.ViaJump[i] : -1));
        cells.Reverse();
        var waypoints = Smooth(cells);
        float length = 0f;
        for (int i = 1; i < waypoints.Count; i++) length += Vector3.Distance(waypoints[i - 1].Position, waypoints[i].Position);
        return new NavPath(waypoints, length, !found, expanded, cells.Count);
    }

    /// <summary>String pulling over a cell path: walk stretches are cut to their farthest straight-walk node; drops are kept.</summary>
    List<NavWaypoint> Smooth(List<(int Node, NavLinkKind Via, int Jump)> cells)
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
            if (cells[anchor + 1].Via == NavLinkKind.Jump && Jumps is { } flights)
            {
                result.Add(new NavWaypoint(Position(cells[anchor + 1].Node), NavLinkKind.Jump, cells[anchor + 1].Node, flights.Move(cells[anchor + 1].Jump, cells[anchor].Node)));
                anchor++;
                continue;
            }
            int far = anchor + 1;
            for (int j = anchor + 2; j < cells.Count && cells[j].Via == NavLinkKind.Walk; j++)
            {
                if (StraightWalk(cells[anchor].Node, cells[j].Node)) far = j;
                else if (j - far > 6) break;   // well past the last straight walk: stop looking
            }
            result.Add(new NavWaypoint(Position(cells[far].Node), NavLinkKind.Walk, cells[far].Node));
            anchor = far;
        }
        return result;
    }

    // ------------------------------------------------------------------ cache

    const int Magic = 0x56414E53;   // "SNAV"
    const int HeaderBytes = 4 + 4 + 8 + 4 * 3 + 4 * 4;

    static ulong Checksum(ReadOnlySpan<byte> data)
    {
        ulong h = Geometry.FnvOffset;
        foreach (byte b in data) h = (h ^ b) * 1099511628211UL;
        return h;
    }

    /// <summary>
    /// Writes the graph: a versioned header with the cache key and sizes, the arrays, and a trailing FNV-1a 64 checksum of
    /// everything before it (read back with <see cref="Read"/>).
    /// </summary>
    public void Write(Stream stream)
    {
        using var buffer = new MemoryStream();
        using (var w = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic); w.Write(FormatVersion); w.Write(SourceHash);
            w.Write(_x0); w.Write(_y0); w.Write(_cell); w.Write(_w); w.Write(_h);
            w.Write(_z.Length); w.Write(_linkTo.Length);
            foreach (int v in _colStart) w.Write(v);
            foreach (float v in _z) w.Write(v);
            foreach (int v in _col) w.Write(v);
            foreach (int v in _linkStart) w.Write(v);
            foreach (int v in _linkTo) w.Write(v);
            foreach (float v in _linkCost) w.Write(v);
            w.Write(_linkKind);
        }
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        stream.Write(bytes);
        stream.Write(BitConverter.GetBytes(Checksum(bytes)));
    }

    /// <summary>
    /// Reads a graph written by <see cref="Write"/>; null when the format, the key (<paramref name="expectedKey"/>) or the
    /// cell size differs, or the data is damaged: wrong length for its sizes (checked before anything large is allocated),
    /// a checksum mismatch, a non-finite height or cost, or inconsistent columns or links. Reads the stream to its end.
    /// </summary>
    public static NavGraph? Read(Stream stream, NavSettings settings, ulong expectedKey)
    {
        try
        {
            var head = new byte[HeaderBytes];
            if (stream.ReadAtLeast(head, HeaderBytes, throwOnEndOfStream: false) < HeaderBytes) return null;
            int magic = BitConverter.ToInt32(head, 0), version = BitConverter.ToInt32(head, 4);
            ulong key = BitConverter.ToUInt64(head, 8);
            float x0 = BitConverter.ToSingle(head, 16), y0 = BitConverter.ToSingle(head, 20), cell = BitConverter.ToSingle(head, 24);
            int w = BitConverter.ToInt32(head, 28), h = BitConverter.ToInt32(head, 32), n = BitConverter.ToInt32(head, 36), links = BitConverter.ToInt32(head, 40);
            if (magic != Magic || version != FormatVersion || key != expectedKey || cell != settings.CellSize || !float.IsFinite(x0) || !float.IsFinite(y0)) return null;
            if (w <= 0 || h <= 0 || (long)w * h > 40_000_000 || n < 0 || links < 0 || n > 50_000_000 || links > 400_000_000) return null;
            long body = 4L * ((long)w * h + 1) + 4L * n + 4L * n + 4L * (n + 1) + 4L * links + 4L * links + links;
            if (stream.CanSeek && stream.Length - stream.Position != body + 8) return null;
            if (body + 8 > int.MaxValue) return null;
            var data = new byte[body + 8];
            if (stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false) != data.Length || stream.ReadByte() != -1) return null;
            var all = new byte[HeaderBytes + body];
            head.CopyTo(all, 0);
            Array.Copy(data, 0, all, HeaderBytes, body);
            if (Checksum(all) != BitConverter.ToUInt64(data, (int)body)) return null;
            int at = 0;
            int[] Ints(int count) { var a = new int[count]; Buffer.BlockCopy(data, at, a, 0, count * 4); at += count * 4; return a; }
            float[] Floats(int count) { var a = new float[count]; Buffer.BlockCopy(data, at, a, 0, count * 4); at += count * 4; return a; }
            var colStart = Ints(w * h + 1); var z = Floats(n); var col = Ints(n); var linkStart = Ints(n + 1);
            var linkTo = Ints(links); var linkCost = Floats(links);
            var linkKind = data.AsSpan(at, links).ToArray();
            if (colStart[0] != 0 || colStart[^1] != n || linkStart[0] != 0 || linkStart[^1] != links) return null;
            for (int c = 0; c < w * h; c++)
            {
                if (colStart[c] > colStart[c + 1]) return null;
                for (int i = colStart[c]; i < colStart[c + 1]; i++) if (col[i] != c) return null;   // nodes lie in their column
            }
            for (int i = 0; i < n; i++) if (linkStart[i] > linkStart[i + 1] || !float.IsFinite(z[i])) return null;
            for (int l = 0; l < links; l++)
                if (linkTo[l] < 0 || linkTo[l] >= n || !float.IsFinite(linkCost[l]) || linkCost[l] < 0f || linkKind[l] > (byte)NavLinkKind.Drop) return null;
            return new NavGraph(settings, x0, y0, w, h, colStart, z, col, linkStart, linkTo, linkCost, linkKind, expectedKey);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ArgumentException or OverflowException or OutOfMemoryException or NotSupportedException)
        {
            return null;
        }
    }
}
