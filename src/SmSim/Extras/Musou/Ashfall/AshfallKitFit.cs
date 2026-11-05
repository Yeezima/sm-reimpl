using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>What a collision fit measured about one model (cells are voxels of <see cref="CellSize"/> metres on a grid of <see cref="Nx"/> x <see cref="Ny"/> x <see cref="Nz"/>, Y up).</summary>
/// <param name="SolidCells">Solid voxels (surface plus enclosed interior).</param>
/// <param name="CoveredCells">Voxels covered by at least one box.</param>
/// <param name="Iou">Intersection over union of the covered cells and the solid cells.</param>
/// <param name="DroppedPercent">Share of the solid volume in boxes dropped as dust, in per cent.</param>
/// <param name="DroppedBoxes">How many boxes were dropped as dust.</param>
/// <param name="CapReached">The box count had to be reduced to the cap.</param>
/// <param name="OverCap">The tolerance or an opening kept the count above the cap.</param>
/// <param name="WalkableTops">Heights (raw frame Y) of horizontal surfaces large enough to stand on (4 x 4 cells at least).</param>
public sealed record AshfallKitFitMetrics(int SolidCells, int CoveredCells, double Iou, double DroppedPercent, int DroppedBoxes, int BoxCount, bool CapReached, bool OverCap,
    float[] WalkableTops, int Nx, int Ny, int Nz, float CellSize);

public sealed record AshfallKitFitResult(IReadOnlyList<(Vector3 Min, Vector3 Max)> Boxes, AshfallKitFitMetrics Metrics);

/// <summary>
/// Offline collision fit (Ashfall kit, draft 4): a model's triangles become a short list of axis-aligned boxes that approximate its solid form. Pure and deterministic: voxelise on a grid anchored
/// to the raw bounds (exact triangle/cell overlap), fill the solid (everything the exterior cannot reach, so doorways, windows and gaps under spans stay open), merge greedily into boxes, then
/// reduce to a cap by the cheapest merges, never past a fill tolerance and never over a vertical opening (2 cells wide, 3 tall, open to the grid's outside).
/// Numbers only: the output is committed as <c>AshfallKitCollision.g.cs</c> by <c>smtool ashfall-kit-collision</c>.
/// </summary>
public static class AshfallKitFit
{
    /// <summary>Cells on the longest axis for the boxes. Finer grids (the first try used 40) trace every chip of the Meshy surfaces and leave 50 to 130 boxes that no tolerance merges; 16 keeps the fit smooth.</summary>
    public const int DefaultResolution = 16;
    /// <summary>Cells on the longest axis for the walkable-top search (a stand-on surface needs 4 x 4 cells, which a coarse box grid cannot show).</summary>
    public const int DefaultTopResolution = 40;
    public const int DefaultMaxBoxes = 32;
    public const float DefaultFillTolerance = 0.15f;

    sealed class Grid
    {
        public int Nx, Ny, Nz;
        public bool[] Solid = [];
        public int Index(int x, int y, int z) => x + Nx * (z + Nz * y);
        public bool IsSolid(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < Nx && y < Ny && z < Nz && Solid[Index(x, y, z)];
    }

    struct Box(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        public int X0 = x0, Y0 = y0, Z0 = z0, X1 = x1, Y1 = y1, Z1 = z1;   // inclusive
        public readonly long Volume => (long)(X1 - X0 + 1) * (Y1 - Y0 + 1) * (Z1 - Z0 + 1);
        public static Box Union(Box a, Box b) => new(Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0), Math.Min(a.Z0, b.Z0), Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1), Math.Max(a.Z1, b.Z1));
    }

    /// <summary>Fits boxes to a triangle soup. <paramref name="positions"/>/<paramref name="indices"/> are in the raw glTF frame (Y up).</summary>
    public static AshfallKitFitResult Fit(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, Vector3 rawMin, Vector3 rawMax,
        int resolution = DefaultResolution, int maxBoxes = DefaultMaxBoxes, float fillTolerance = DefaultFillTolerance, int topResolution = DefaultTopResolution)
    {
        var size = rawMax - rawMin;
        float longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (longest <= 0f) return new(Array.Empty<(Vector3, Vector3)>(), new(0, 0, 0, 0, 0, 0, false, false, [], 0, 0, 0, 0f));
        float cell = longest / resolution;
        var g = SolidGrid(positions, indices, rawMin, size, cell);
        int solidCells = g.Solid.Count(b => b);

        var boxes = GreedyMerge(g);
        // Dust: single-cell-thick flecks below 0.2 % of the solid volume.
        int dropped = 0; long droppedVol = 0;
        double dustLimit = solidCells * 0.002;
        var kept = new List<Box>();
        foreach (var b in boxes)
        {
            int minDim = Math.Min(b.X1 - b.X0, Math.Min(b.Y1 - b.Y0, b.Z1 - b.Z0)) + 1;
            if (minDim <= 1 && b.Volume < dustLimit && boxes.Count > 1) { dropped++; droppedVol += b.Volume; }
            else kept.Add(b);
        }
        if (kept.Count == 0 && boxes.Count > 0) kept.Add(boxes.OrderByDescending(b => b.Volume).First());

        bool capReached = false;
        if (kept.Count > maxBoxes) { capReached = true; kept = Reduce(g, kept, maxBoxes, fillTolerance); }
        bool overCap = kept.Count > maxBoxes;

        var covered = new bool[g.Solid.Length];
        foreach (var b in kept)
            for (int y = b.Y0; y <= b.Y1; y++) for (int z = b.Z0; z <= b.Z1; z++) for (int x = b.X0; x <= b.X1; x++) covered[g.Index(x, y, z)] = true;
        long inter = 0, uni = 0; int coveredCells = 0;
        for (int i = 0; i < covered.Length; i++)
        {
            if (covered[i]) coveredCells++;
            if (covered[i] && g.Solid[i]) inter++;
            if (covered[i] || g.Solid[i]) uni++;
        }
        double iou = uni == 0 ? 1.0 : (double)inter / uni;
        float topCell = longest / topResolution;
        var tg = topResolution == resolution ? g : SolidGrid(positions, indices, rawMin, size, topCell);
        var tops = WalkableTops(tg, rawMin.Y, topCell);

        var outBoxes = kept.Select(b => (
            new Vector3(rawMin.X + b.X0 * cell, rawMin.Y + b.Y0 * cell, rawMin.Z + b.Z0 * cell),
            Vector3.Min(rawMax, new Vector3(rawMin.X + (b.X1 + 1) * cell, rawMin.Y + (b.Y1 + 1) * cell, rawMin.Z + (b.Z1 + 1) * cell)))).ToList();
        var m = new AshfallKitFitMetrics(solidCells, coveredCells, iou, solidCells == 0 ? 0 : 100.0 * droppedVol / solidCells, dropped, kept.Count, capReached, overCap, tops, g.Nx, g.Ny, g.Nz, cell);
        return new(outBoxes, m);
    }

    static Grid SolidGrid(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, Vector3 rawMin, Vector3 size, float cell)
    {
        var g = new Grid { Nx = Cells(size.X, cell), Ny = Cells(size.Y, cell), Nz = Cells(size.Z, cell) };
        g.Solid = new bool[g.Nx * g.Ny * g.Nz];
        var exterior = FloodExterior(g, Voxelise(g, positions, indices, rawMin, cell));
        for (int i = 0; i < g.Solid.Length; i++) g.Solid[i] = !exterior[i];
        return g;
    }

    static int Cells(float extent, float cell) => Math.Max(1, (int)MathF.Ceiling(extent / cell - 1e-4f));

    // ---------------------------------------------------------------- voxelisation

    static bool[] Voxelise(Grid g, IReadOnlyList<Vector3> pos, IReadOnlyList<int> idx, Vector3 origin, float cell)
    {
        var surf = new bool[g.Nx * g.Ny * g.Nz];
        var half = new Vector3(cell / 2f);
        for (int t = 0; t + 2 < idx.Count; t += 3)
        {
            Vector3 a = pos[idx[t]] - origin, b = pos[idx[t + 1]] - origin, c = pos[idx[t + 2]] - origin;
            var lo = Vector3.Min(a, Vector3.Min(b, c)) / cell; var hi = Vector3.Max(a, Vector3.Max(b, c)) / cell;
            int x0 = Math.Clamp((int)MathF.Floor(lo.X), 0, g.Nx - 1), x1 = Math.Clamp((int)MathF.Floor(hi.X), 0, g.Nx - 1);
            int y0 = Math.Clamp((int)MathF.Floor(lo.Y), 0, g.Ny - 1), y1 = Math.Clamp((int)MathF.Floor(hi.Y), 0, g.Ny - 1);
            int z0 = Math.Clamp((int)MathF.Floor(lo.Z), 0, g.Nz - 1), z1 = Math.Clamp((int)MathF.Floor(hi.Z), 0, g.Nz - 1);
            for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
            {
                int i = g.Index(x, y, z);
                if (surf[i]) continue;
                var centre = new Vector3((x + 0.5f) * cell, (y + 0.5f) * cell, (z + 0.5f) * cell);
                if (TriBox(a - centre, b - centre, c - centre, half)) surf[i] = true;
            }
        }
        return surf;
    }

    /// <summary>Triangle against an axis-aligned box centred at the origin (separating axis test).</summary>
    static bool TriBox(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 h)
    {
        const float eps = 1e-6f;
        if (Math.Min(v0.X, Math.Min(v1.X, v2.X)) > h.X + eps || Math.Max(v0.X, Math.Max(v1.X, v2.X)) < -h.X - eps) return false;
        if (Math.Min(v0.Y, Math.Min(v1.Y, v2.Y)) > h.Y + eps || Math.Max(v0.Y, Math.Max(v1.Y, v2.Y)) < -h.Y - eps) return false;
        if (Math.Min(v0.Z, Math.Min(v1.Z, v2.Z)) > h.Z + eps || Math.Max(v0.Z, Math.Max(v1.Z, v2.Z)) < -h.Z - eps) return false;
        var e0 = v1 - v0; var e1 = v2 - v1; var e2 = v0 - v2;
        var n = Vector3.Cross(e0, e1);
        float r = h.X * MathF.Abs(n.X) + h.Y * MathF.Abs(n.Y) + h.Z * MathF.Abs(n.Z);
        if (MathF.Abs(Vector3.Dot(n, v0)) > r + eps * MathF.Max(1f, n.Length())) return false;
        foreach (var e in new[] { e0, e1, e2 })
        {
            for (int axis = 0; axis < 3; axis++)
            {
                Vector3 ax = axis == 0 ? new Vector3(0, -e.Z, e.Y) : axis == 1 ? new Vector3(e.Z, 0, -e.X) : new Vector3(-e.Y, e.X, 0);
                if (ax.LengthSquared() < 1e-14f) continue;
                float p0 = Vector3.Dot(ax, v0), p1 = Vector3.Dot(ax, v1), p2 = Vector3.Dot(ax, v2);
                float rr = h.X * MathF.Abs(ax.X) + h.Y * MathF.Abs(ax.Y) + h.Z * MathF.Abs(ax.Z);
                float e2eps = eps * MathF.Max(1f, ax.Length());
                if (Math.Min(p0, Math.Min(p1, p2)) > rr + e2eps || Math.Max(p0, Math.Max(p1, p2)) < -rr - e2eps) return false;
            }
        }
        return true;
    }

    /// <summary>6-connected flood fill through non-surface cells from every empty cell on the grid's outer layer.</summary>
    static bool[] FloodExterior(Grid g, bool[] surface)
    {
        var ext = new bool[surface.Length];
        var stack = new Stack<int>();
        void Seed(int x, int y, int z) { int i = g.Index(x, y, z); if (!surface[i] && !ext[i]) { ext[i] = true; stack.Push(i); } }
        for (int y = 0; y < g.Ny; y++) for (int z = 0; z < g.Nz; z++) for (int x = 0; x < g.Nx; x++)
            if (x == 0 || y == 0 || z == 0 || x == g.Nx - 1 || y == g.Ny - 1 || z == g.Nz - 1) Seed(x, y, z);
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            int x = i % g.Nx, z = i / g.Nx % g.Nz, y = i / (g.Nx * g.Nz);
            if (x > 0) Seed(x - 1, y, z);
            if (x < g.Nx - 1) Seed(x + 1, y, z);
            if (y > 0) Seed(x, y - 1, z);
            if (y < g.Ny - 1) Seed(x, y + 1, z);
            if (z > 0) Seed(x, y, z - 1);
            if (z < g.Nz - 1) Seed(x, y, z + 1);
        }
        return ext;
    }

    // ---------------------------------------------------------------- boxes

    static List<Box> GreedyMerge(Grid g)
    {
        var claimed = new bool[g.Solid.Length];
        bool Free(int x, int y, int z) => g.IsSolid(x, y, z) && !claimed[g.Index(x, y, z)];
        bool RowFree(int xa, int xb, int yy, int zz) { for (int i = xa; i <= xb; i++) if (!Free(i, yy, zz)) return false; return true; }
        bool PlaneFree(int xa, int xb, int za, int zb, int yy) { for (int k = za; k <= zb; k++) if (!RowFree(xa, xb, yy, k)) return false; return true; }
        var result = new List<Box>();
        for (int y = 0; y < g.Ny; y++) for (int z = 0; z < g.Nz; z++) for (int x = 0; x < g.Nx; x++)
        {
            if (!Free(x, y, z)) continue;
            int x1 = x; while (Free(x1 + 1, y, z)) x1++;
            int z1 = z; while (z1 + 1 < g.Nz && RowFree(x, x1, y, z1 + 1)) z1++;
            int y1 = y; while (y1 + 1 < g.Ny && PlaneFree(x, x1, z, z1, y1 + 1)) y1++;
            for (int yy = y; yy <= y1; yy++) for (int zz = z; zz <= z1; zz++) for (int xx = x; xx <= x1; xx++) claimed[g.Index(xx, yy, zz)] = true;
            result.Add(new Box(x, y, z, x1, y1, z1));
        }
        return result;
    }

    /// <summary>Inclusive 3D prefix sums of a boolean grid, for box counts in O(1).</summary>
    sealed class Prefix
    {
        readonly int[] s; readonly int nx, ny, nz;
        public Prefix(Grid g, Func<int, bool> cell)
        {
            nx = g.Nx; ny = g.Ny; nz = g.Nz;
            s = new int[(nx + 1) * (ny + 1) * (nz + 1)];
            for (int y = 1; y <= ny; y++) for (int z = 1; z <= nz; z++) for (int x = 1; x <= nx; x++)
            {
                int v = cell(g.Index(x - 1, y - 1, z - 1)) ? 1 : 0;
                s[I(x, y, z)] = v + s[I(x - 1, y, z)] + s[I(x, y - 1, z)] + s[I(x, y, z - 1)]
                    - s[I(x - 1, y - 1, z)] - s[I(x - 1, y, z - 1)] - s[I(x, y - 1, z - 1)] + s[I(x - 1, y - 1, z - 1)];
            }
        }
        int I(int x, int y, int z) => x + (nx + 1) * (z + (nz + 1) * y);
        public int Count(Box b)
        {
            int x0 = b.X0, y0 = b.Y0, z0 = b.Z0, x1 = b.X1 + 1, y1 = b.Y1 + 1, z1 = b.Z1 + 1;
            return s[I(x1, y1, z1)] - s[I(x0, y1, z1)] - s[I(x1, y0, z1)] - s[I(x1, y1, z0)]
                + s[I(x0, y0, z1)] + s[I(x0, y1, z0)] + s[I(x1, y0, z0)] - s[I(x0, y0, z0)];
        }
    }

    /// <summary>Empty cells that belong to a vertical opening: a window 2 cells wide and 3 tall (across X or across Z) whose extrusion along the other horizontal axis is empty to the grid's outside.</summary>
    static bool[] ProtectedOpenings(Grid g)
    {
        var prot = new bool[g.Solid.Length];
        var negZ = new bool[g.Solid.Length]; var posZ = new bool[g.Solid.Length]; var negX = new bool[g.Solid.Length]; var posX = new bool[g.Solid.Length];
        for (int y = 0; y < g.Ny; y++)
        {
            for (int x = 0; x < g.Nx; x++)
            {
                bool run = true;
                for (int z = 0; z < g.Nz; z++) { run &= !g.Solid[g.Index(x, y, z)]; negZ[g.Index(x, y, z)] = run; }
                run = true;
                for (int z = g.Nz - 1; z >= 0; z--) { run &= !g.Solid[g.Index(x, y, z)]; posZ[g.Index(x, y, z)] = run; }
            }
            for (int z = 0; z < g.Nz; z++)
            {
                bool run = true;
                for (int x = 0; x < g.Nx; x++) { run &= !g.Solid[g.Index(x, y, z)]; negX[g.Index(x, y, z)] = run; }
                run = true;
                for (int x = g.Nx - 1; x >= 0; x--) { run &= !g.Solid[g.Index(x, y, z)]; posX[g.Index(x, y, z)] = run; }
            }
        }
        for (int y = 0; y + 2 < g.Ny; y++) for (int z = 0; z < g.Nz; z++) for (int x = 0; x + 1 < g.Nx; x++)
        {
            bool neg = true, pos = true;
            for (int dy = 0; dy < 3; dy++) for (int dx = 0; dx < 2; dx++) { int i = g.Index(x + dx, y + dy, z); neg &= negZ[i]; pos &= posZ[i]; }
            if (neg || pos) for (int dy = 0; dy < 3; dy++) for (int dx = 0; dx < 2; dx++) prot[g.Index(x + dx, y + dy, z)] = true;
        }
        for (int y = 0; y + 2 < g.Ny; y++) for (int x = 0; x < g.Nx; x++) for (int z = 0; z + 1 < g.Nz; z++)
        {
            bool neg = true, pos = true;
            for (int dy = 0; dy < 3; dy++) for (int dz = 0; dz < 2; dz++) { int i = g.Index(x, y + dy, z + dz); neg &= negX[i]; pos &= posX[i]; }
            if (neg || pos) for (int dy = 0; dy < 3; dy++) for (int dz = 0; dz < 2; dz++) prot[g.Index(x, y + dy, z + dz)] = true;
        }
        return prot;
    }

    static List<Box> Reduce(Grid g, List<Box> start, int maxBoxes, float tolerance)
    {
        var solid = new Prefix(g, i => g.Solid[i]);
        var prot = ProtectedOpenings(g);
        var protectedCount = new Prefix(g, i => prot[i]);
        var boxes = new List<Box>(start);
        var alive = new List<bool>();
        foreach (var _ in start) alive.Add(true);

        long Empty(Box b) => b.Volume - solid.Count(b);
        var queue = new PriorityQueue<(int A, int B), (long Cost, int A, int B)>();
        void Enqueue(int a, int b)
        {
            var m = Box.Union(boxes[a], boxes[b]);
            long vol = m.Volume, empty = vol - solid.Count(m);
            if (empty > tolerance * vol || protectedCount.Count(m) > 0) return;
            queue.Enqueue((a, b), (empty - Empty(boxes[a]) - Empty(boxes[b]), a, b));
        }
        for (int a = 0; a < boxes.Count; a++) for (int b = a + 1; b < boxes.Count; b++) Enqueue(a, b);

        int count = boxes.Count;
        while (count > maxBoxes && queue.Count > 0)
        {
            var (a, b) = queue.Dequeue();
            if (!alive[a] || !alive[b]) continue;
            alive[a] = alive[b] = false;
            boxes.Add(Box.Union(boxes[a], boxes[b])); alive.Add(true);
            int n = boxes.Count - 1;
            count--;
            for (int i = 0; i < n; i++) if (alive[i]) Enqueue(i, n);
        }
        var result = new List<Box>();
        for (int i = 0; i < boxes.Count; i++) if (alive[i]) result.Add(boxes[i]);
        result.Sort((p, q) =>
        {
            int c = p.Y0.CompareTo(q.Y0); if (c != 0) return c;
            c = p.Z0.CompareTo(q.Z0); if (c != 0) return c;
            c = p.X0.CompareTo(q.X0); if (c != 0) return c;
            c = p.Y1.CompareTo(q.Y1); if (c != 0) return c;
            c = p.Z1.CompareTo(q.Z1); if (c != 0) return c;
            return p.X1.CompareTo(q.X1);
        });
        return result;
    }

    // ---------------------------------------------------------------- walkable tops

    static float[] WalkableTops(Grid g, float minY, float cell)
    {
        var heights = new SortedSet<float>();
        var top = new bool[g.Nx * g.Nz];
        var dirs = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        for (int y = 0; y < g.Ny; y++)
        {
            bool any = false;
            for (int z = 0; z < g.Nz; z++) for (int x = 0; x < g.Nx; x++)
            {
                bool t = g.IsSolid(x, y, z) && !g.IsSolid(x, y + 1, z);
                top[x + g.Nx * z] = t; any |= t;
            }
            if (!any) continue;
            var seen = new bool[top.Length];
            for (int z = 0; z < g.Nz; z++) for (int x = 0; x < g.Nx; x++)
            {
                if (!top[x + g.Nx * z] || seen[x + g.Nx * z]) continue;
                var stack = new Stack<(int, int)>(); stack.Push((x, z)); seen[x + g.Nx * z] = true;
                var comp = new List<(int X, int Z)>();
                while (stack.Count > 0)
                {
                    var (cx, cz) = stack.Pop(); comp.Add((cx, cz));
                    foreach (var (dx, dz) in dirs)
                    {
                        int nx = cx + dx, nz = cz + dz;
                        if (nx < 0 || nz < 0 || nx >= g.Nx || nz >= g.Nz || !top[nx + g.Nx * nz] || seen[nx + g.Nx * nz]) continue;
                        seen[nx + g.Nx * nz] = true; stack.Push((nx, nz));
                    }
                }
                if (comp.Count < 16) continue;
                bool block = false;
                foreach (var (cx, cz) in comp)
                {
                    bool ok = cx + 3 < g.Nx && cz + 3 < g.Nz;
                    for (int dz = 0; ok && dz < 4; dz++) for (int dx = 0; ok && dx < 4; dx++) ok = top[cx + dx + g.Nx * (cz + dz)];
                    if (ok) { block = true; break; }
                }
                if (block) heights.Add(MathF.Round(minY + (y + 1) * cell, 4));
            }
        }
        return heights.ToArray();
    }
}
