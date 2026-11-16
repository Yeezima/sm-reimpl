using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// A through-opening found in a kit model's wall (a window, an arch, a door-sized hole; the window pass of 2026-11-06), in the model's raw glTF frame (Y up). Rays along <see cref="Axis"/> (0: raw X, 2: raw Z) pass through
/// the wall there: the segment from <see cref="Start"/> to <see cref="End"/> (the opening's most central pixel, through the wall's thickness) is free in the mesh and must stay free of collision boxes.
/// <see cref="Wall"/> is a point of the wall beside it (mid depth) whose ray through the same thickness meets the mesh's solid and the boxes (a shot there must stop); null when none was found.
/// </summary>
/// <param name="Axis">0 when the opening's rays run along raw X, 2 along raw Z.</param>
/// <param name="Centre">The most central pixel of the opening, at mid depth (raw frame).</param>
/// <param name="DepthMin">Where the free run through the wall starts along the axis (raw units, a quarter fine cell inside).</param>
/// <param name="DepthMax">Where it ends.</param>
/// <param name="Width">The opening's horizontal extent across the axis (raw units).</param>
/// <param name="Height">Its vertical extent (raw units).</param>
/// <param name="ClearPercent">Share of the opening's core (its pixels more than <see cref="AshfallKitFit.BandCells"/> fine cells inside its edge, or the centre alone) whose rays through the wall no box touches, in per cent.</param>
public sealed record AshfallKitOpening(int Axis, Vector3 Centre, float DepthMin, float DepthMax, float Width, float Height, double ClearPercent)
{
    public Vector3? Wall { get; init; }

    /// <summary>The centre's ray at <see cref="DepthMin"/>.</summary>
    public Vector3 Start => At(Centre, DepthMin);

    /// <summary>The centre's ray at <see cref="DepthMax"/>.</summary>
    public Vector3 End => At(Centre, DepthMax);

    /// <summary><paramref name="p"/> with its coordinate along <see cref="Axis"/> set to <paramref name="depth"/>.</summary>
    public Vector3 At(Vector3 p, float depth) => Axis == 0 ? p with { X = depth } : p with { Z = depth };

    /// <summary>The opening passes the gate: its core at least <see cref="AshfallKitFit.Gate.MinOpeningClearPercent"/> % clear.</summary>
    public bool Open => ClearPercent >= AshfallKitFit.Gate.MinOpeningClearPercent;
}

/// <summary>A model's through-openings on the gate's fine grid (<see cref="AshfallKitFit.FindOpenings"/>): what the fit carves and the gate checks.</summary>
public sealed class AshfallKitOpeningMap
{
    internal AshfallKitOpeningMap(int nx, int ny, int nz, float cell, Vector3 origin, bool[] solid, List<AshfallKitFit.FoundOpening> found)
    {
        Nx = nx; Ny = ny; Nz = nz; Cell = cell; Origin = origin; Solid = solid; Found = found;
    }

    internal readonly int Nx, Ny, Nz;
    internal readonly float Cell;
    internal readonly Vector3 Origin;
    internal readonly bool[] Solid;
    internal readonly List<AshfallKitFit.FoundOpening> Found;

    /// <summary>How many openings were found.</summary>
    public int Count => Found.Count;
}

public static partial class AshfallKitFit
{
    /// <summary>Longest solid run along a ray (fine cells; a quarter of the measuring grid) that still counts as a wall crossing the ray: a window's frame. Longer runs are walls along the ray, floors and roofs.</summary>
    public const int OpeningMaxWallCells = MeasureResolution / 4;

    /// <summary>How far (fine cells; half the measuring grid) a free cell looks left, right and up in its slice for the wall that frames it.</summary>
    public const int OpeningFrameReach = MeasureResolution / 2;

    /// <summary>Smallest opening (fine cells across and up, about 3 % of the model's longest side; smaller gaps are cracks the seal closes or slits no shot aims through).</summary>
    public const int OpeningMinCells = 4;

    internal sealed record FoundOpening(int Axis, List<(int U, int Y, int D0, int D1)> Runs, List<int> Core, int CentreRun, int D0, int D1, int MinU, int MaxU, int MinY, int MaxY);

    /// <summary>
    /// The model's through-openings (window pass, 2026-11-06; ours). On the gate's fine grid (<see cref="MeasureResolution"/> cells on the longest side, cracks sealed as in <see cref="Measure"/>), for rays along raw X and raw Z:
    /// a wall crossing the ray is a solid run of at most <see cref="OpeningMaxWallCells"/>; a free cell is framed when, in its slice across the ray, such wall lies left, right and above it within <see cref="OpeningFrameReach"/>
    /// (a window, an arch or a doorway: open below is allowed); a run of framed free cells along a ray is an opening's tunnel when neither end meets the mesh's solid (the ray passes out of the wall on both sides:
    /// a dent in a wall's face ends at the wall and is not an opening). Tunnels are grouped into openings (6-connected); an opening is kept when it is at least <see cref="OpeningMinCells"/> across and up.
    /// </summary>
    public static AshfallKitOpeningMap FindOpenings(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, Vector3 rawMin, Vector3 rawMax)
    {
        var size = rawMax - rawMin;
        float longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (longest <= 0f) return new(1, 1, 1, 1f, rawMin, [false], []);
        float cell = longest / MeasureResolution;
        var g = SealedSolidGrid(positions, indices, rawMin, size, cell, SealCells);
        var found = new List<FoundOpening>();
        foreach (int axis in new[] { 0, 2 }) found.AddRange(OpeningsAlong(g, axis));
        return new(g.Nx, g.Ny, g.Nz, cell, rawMin, g.Solid, found);
    }

    /// <summary>Grid index of (u across, y up, d along the axis).</summary>
    static int AxisIndex(Grid g, int axis, int u, int y, int d) => axis == 0 ? g.Index(d, y, u) : g.Index(u, y, d);

    static List<FoundOpening> OpeningsAlong(Grid g, int axis)
    {
        int nd = axis == 0 ? g.Nx : g.Nz, nu = axis == 0 ? g.Nz : g.Nx, ny = g.Ny;
        bool S(int u, int y, int d) => g.Solid[AxisIndex(g, axis, u, y, d)];
        // walls crossing the ray: solid runs no longer than the limit
        var cross = new bool[g.Solid.Length];
        for (int y = 0; y < ny; y++) for (int u = 0; u < nu; u++)
        {
            int d = 0;
            while (d < nd)
            {
                if (!S(u, y, d)) { d++; continue; }
                int e = d; while (e + 1 < nd && S(u, y, e + 1)) e++;
                if (e - d + 1 <= OpeningMaxWallCells) for (int k = d; k <= e; k++) cross[AxisIndex(g, axis, u, y, k)] = true;
                d = e + 1;
            }
        }
        // framed free cells: wall left, right and above within the reach, in the same slice
        var framed = new bool[g.Solid.Length];
        int reach = OpeningFrameReach;
        var left = new int[nu]; var right = new int[nu];
        for (int d = 0; d < nd; d++)
        {
            var above = new int[nu];   // distance (rows) up to the nearest wall above, scanning down from the top
            for (int u = 0; u < nu; u++) above[u] = int.MaxValue;
            for (int y = ny - 1; y >= 0; y--)
            {
                int last = int.MinValue / 2;
                for (int u = 0; u < nu; u++) { if (cross[AxisIndex(g, axis, u, y, d)]) last = u; left[u] = u - last; }
                last = int.MaxValue / 2;
                for (int u = nu - 1; u >= 0; u--) { if (cross[AxisIndex(g, axis, u, y, d)]) last = u; right[u] = last - u; }
                for (int u = 0; u < nu; u++)
                {
                    int i = AxisIndex(g, axis, u, y, d);
                    if (!g.Solid[i] && left[u] <= reach && right[u] <= reach && above[u] <= reach) framed[i] = true;
                }
                for (int u = 0; u < nu; u++) above[u] = cross[AxisIndex(g, axis, u, y, d)] ? 1 : above[u] == int.MaxValue ? int.MaxValue : above[u] + 1;
            }
        }
        // tunnels: runs of framed cells whose ends do not meet the solid
        var tunnel = new bool[g.Solid.Length];
        for (int y = 0; y < ny; y++) for (int u = 0; u < nu; u++)
        {
            int d = 0;
            while (d < nd)
            {
                if (!framed[AxisIndex(g, axis, u, y, d)]) { d++; continue; }
                int e = d; while (e + 1 < nd && framed[AxisIndex(g, axis, u, y, e + 1)]) e++;
                bool openBefore = d == 0 || !S(u, y, d - 1), openAfter = e == nd - 1 || !S(u, y, e + 1);
                if (openBefore && openAfter) for (int k = d; k <= e; k++) tunnel[AxisIndex(g, axis, u, y, k)] = true;
                d = e + 1;
            }
        }
        // openings: 6-connected groups of tunnel cells
        var result = new List<FoundOpening>();
        var seen = new bool[tunnel.Length];
        var stack = new Stack<(int U, int Y, int D)>();
        for (int y0 = 0; y0 < ny; y0++) for (int u0 = 0; u0 < nu; u0++) for (int d0 = 0; d0 < nd; d0++)
        {
            int s = AxisIndex(g, axis, u0, y0, d0);
            if (!tunnel[s] || seen[s]) continue;
            var cells = new List<(int U, int Y, int D)>();
            seen[s] = true; stack.Push((u0, y0, d0));
            while (stack.Count > 0)
            {
                var (u, y, d) = stack.Pop(); cells.Add((u, y, d));
                void Try(int uu, int yy, int dd)
                {
                    if (uu < 0 || yy < 0 || dd < 0 || uu >= nu || yy >= ny || dd >= nd) return;
                    int j = AxisIndex(g, axis, uu, yy, dd);
                    if (!tunnel[j] || seen[j]) return;
                    seen[j] = true; stack.Push((uu, yy, dd));
                }
                Try(u - 1, y, d); Try(u + 1, y, d); Try(u, y - 1, d); Try(u, y + 1, d); Try(u, y, d - 1); Try(u, y, d + 1);
            }
            int minU = cells.Min(c => c.U), maxU = cells.Max(c => c.U), minY = cells.Min(c => c.Y), maxY = cells.Max(c => c.Y);
            if (maxU - minU + 1 < OpeningMinCells || maxY - minY + 1 < OpeningMinCells) continue;
            // the opening's pixels and each pixel's run through the wall (the longest of the group's cells on that pixel's ray)
            var byPixel = new SortedDictionary<int, (int D0, int D1)>();
            foreach (var (u, y, d) in cells)
            {
                int key = u + nu * y;
                byPixel[key] = byPixel.TryGetValue(key, out var r) ? (Math.Min(r.D0, d), Math.Max(r.D1, d)) : (d, d);
            }
            var runs = byPixel.Select(kv => (U: kv.Key % nu, Y: kv.Key / nu, kv.Value.D0, kv.Value.D1)).ToList();
            // depth of each pixel inside the opening (Chebyshev distance to the nearest pixel outside it): 8-connected breadth-first steps from the outside, over the bounds padded by one
            int bw = maxU - minU + 3, bh = maxY - minY + 3;
            var inMask = new bool[bw * bh];
            foreach (var r in runs) inMask[r.U - minU + 1 + bw * (r.Y - minY + 1)] = true;
            var dist = new int[bw * bh];
            var queue = new Queue<int>();
            for (int k = 0; k < dist.Length; k++) if (!inMask[k]) { dist[k] = 0; queue.Enqueue(k); } else dist[k] = int.MaxValue;
            while (queue.Count > 0)
            {
                int k = queue.Dequeue(), ku = k % bw, ky = k / bw;
                for (int dy = -1; dy <= 1; dy++) for (int du = -1; du <= 1; du++)
                {
                    int u2 = ku + du, y2 = ky + dy;
                    if (u2 < 0 || y2 < 0 || u2 >= bw || y2 >= bh) continue;
                    int j = u2 + bw * y2;
                    if (dist[j] != int.MaxValue) continue;
                    dist[j] = dist[k] + 1; queue.Enqueue(j);
                }
            }
            var depth = runs.Select(r => dist[r.U - minU + 1 + bw * (r.Y - minY + 1)]).ToArray();
            var core = Enumerable.Range(0, runs.Count).Where(k => depth[k] > BandCells).ToList();
            float cu = (minU + maxU) / 2f, cy = (minY + maxY) / 2f;
            int centre = Enumerable.Range(0, runs.Count).OrderByDescending(k => depth[k])
                .ThenBy(k => (runs[k].U - cu) * (runs[k].U - cu) + (runs[k].Y - cy) * (runs[k].Y - cy)).ThenBy(k => k).First();
            if (core.Count == 0) core.Add(centre);
            result.Add(new FoundOpening(axis, runs, core, centre, cells.Min(c => c.D), cells.Max(c => c.D), minU, maxU, minY, maxY));
        }
        return result;
    }

    /// <summary>
    /// Clears the fit's cells that lie in an opening (before merging; the window pass): a coarse cell goes when at least half of its fine pixels across an opening's axis are that opening's and at least half of its fine cells
    /// are free, and the coarse cells on each opening's centre ray through the wall always go. Returns the cleared cells (no merge in <see cref="Reduce"/> may cover one).
    /// </summary>
    static bool[] CarveOpenings(Grid g, float cell, AshfallKitOpeningMap map)
    {
        var carved = new bool[g.Solid.Length];
        if (map.Count == 0) return carved;
        float f = map.Cell;
        int Lo(int c) => Math.Max(0, (int)MathF.Ceiling(c * cell / f - 0.5f - 1e-4f));
        int Hi(int c, int n) => Math.Min(n - 1, (int)MathF.Floor((c + 1) * cell / f - 0.5f - 1e-4f));
        int Coarse(int fi, int n) => Math.Clamp((int)((fi + 0.5f) * f / cell), 0, n - 1);
        int FineIndex(int x, int y, int z) => x + map.Nx * (z + map.Nz * y);
        foreach (int axis in new[] { 0, 2 })
        {
            int fnu = axis == 0 ? map.Nz : map.Nx;
            var mask = new HashSet<long>();   // (u, y, d) of the axis' opening cells
            foreach (var o in map.Found.Where(o => o.Axis == axis))
                foreach (var (u, y, d0, d1) in o.Runs) for (int d = d0; d <= d1; d++) mask.Add(((long)d * map.Ny + y) * fnu + u);
            if (mask.Count == 0) continue;
            for (int y = 0; y < g.Ny; y++) for (int z = 0; z < g.Nz; z++) for (int x = 0; x < g.Nx; x++)
            {
                int i = g.Index(x, y, z);
                if (!g.Solid[i]) continue;
                int fx0 = Lo(x), fx1 = Hi(x, map.Nx), fy0 = Lo(y), fy1 = Hi(y, map.Ny), fz0 = Lo(z), fz1 = Hi(z, map.Nz);
                if (fx1 < fx0 || fy1 < fy0 || fz1 < fz0) continue;
                int u0 = axis == 0 ? fz0 : fx0, u1 = axis == 0 ? fz1 : fx1, dd0 = axis == 0 ? fx0 : fz0, dd1 = axis == 0 ? fx1 : fz1;
                int pixels = 0, open = 0;
                for (int fy = fy0; fy <= fy1; fy++) for (int fu = u0; fu <= u1; fu++)
                {
                    pixels++;
                    for (int fd = dd0; fd <= dd1; fd++) if (mask.Contains(((long)fd * map.Ny + fy) * fnu + fu)) { open++; break; }
                }
                if (open * 2 < pixels) continue;
                int cells = 0, free = 0;
                for (int fy = fy0; fy <= fy1; fy++) for (int fz = fz0; fz <= fz1; fz++) for (int fx = fx0; fx <= fx1; fx++) { cells++; if (!map.Solid[FineIndex(fx, fy, fz)]) free++; }
                if (free * 2 < cells) continue;
                carved[i] = true;
            }
        }
        // each opening's centre ray through the wall
        foreach (var o in map.Found)
        {
            var (u, y, d0, d1) = o.Runs[o.CentreRun];
            for (int d = d0; d <= d1; d++)
            {
                int fx = o.Axis == 0 ? d : u, fz = o.Axis == 0 ? u : d;
                carved[g.Index(Coarse(fx, g.Nx), Coarse(y, g.Ny), Coarse(fz, g.Nz))] = true;
            }
        }
        for (int i = 0; i < carved.Length; i++) if (carved[i]) g.Solid[i] = false;
        return carved;
    }

    /// <summary>
    /// The openings measured against a fit's boxes (on the fine grid; <paramref name="covered"/> is the boxes' fine cells): each opening's core share whose rays through the wall no box touches, and a wall point beside it
    /// (a pixel 2 to 5 bands outside the opening whose ray over the centre's run is at least 60 % solid and meets a box; the most solid, then the nearest).
    /// </summary>
    static List<AshfallKitOpening> MeasureOpenings(AshfallKitOpeningMap map, bool[] covered)
    {
        var list = new List<AshfallKitOpening>();
        float f = map.Cell;
        int Fi(int axis, int u, int y, int d) => axis == 0 ? d + map.Nx * (u + map.Nz * y) : u + map.Nx * (d + map.Nz * y);
        foreach (var o in map.Found)
        {
            int nu = o.Axis == 0 ? map.Nz : map.Nx;
            int clear = 0;
            foreach (int k in o.Core)
            {
                var (u, y, d0, d1) = o.Runs[k];
                bool blocked = false;
                for (int d = d0; d <= d1 && !blocked; d++) blocked = covered[Fi(o.Axis, u, y, d)];
                if (!blocked) clear++;
            }
            var (cu, cy, cd0, cd1) = o.Runs[o.CentreRun];
            Vector3 Raw(int u, int y, float d) => o.Axis == 0
                ? map.Origin + new Vector3(d * f, (y + 0.5f) * f, (u + 0.5f) * f)
                : map.Origin + new Vector3((u + 0.5f) * f, (y + 0.5f) * f, d * f);
            float depthMin = (o.Axis == 0 ? map.Origin.X : map.Origin.Z) + (cd0 + 0.25f) * f, depthMax = (o.Axis == 0 ? map.Origin.X : map.Origin.Z) + (cd1 + 0.75f) * f;
            // a wall point beside the opening
            Vector3? wall = null; double bestSolid = -1; float bestDist = float.MaxValue;
            // Chebyshev distance to the opening's pixels over a window around it (8-connected breadth-first steps)
            int reach = 5 * BandCells, wu0 = Math.Max(0, o.MinU - reach), wy0 = Math.Max(0, o.MinY - reach);
            int ww = Math.Min(nu - 1, o.MaxU + reach) - wu0 + 1, wh = Math.Min(map.Ny - 1, o.MaxY + reach) - wy0 + 1;
            var dist = new int[ww * wh];
            Array.Fill(dist, int.MaxValue);
            var queue = new Queue<int>();
            foreach (var r in o.Runs) { int k = r.U - wu0 + ww * (r.Y - wy0); if (dist[k] != 0) { dist[k] = 0; queue.Enqueue(k); } }
            while (queue.Count > 0)
            {
                int k = queue.Dequeue(), ku = k % ww, ky = k / ww;
                if (dist[k] >= reach) continue;
                for (int dy = -1; dy <= 1; dy++) for (int du = -1; du <= 1; du++)
                {
                    int nu2 = ku + du, ny2 = ky + dy;
                    if (nu2 < 0 || ny2 < 0 || nu2 >= ww || ny2 >= wh) continue;
                    int j = nu2 + ww * ny2;
                    if (dist[j] != int.MaxValue) continue;
                    dist[j] = dist[k] + 1; queue.Enqueue(j);
                }
            }
            for (int y = wy0; y < wy0 + wh; y++)
                for (int u = wu0; u < wu0 + ww; u++)
                {
                    int dd = dist[u - wu0 + ww * (y - wy0)];
                    if (dd < 2 * BandCells || dd > reach) continue;
                    int solid = 0, n = 0; bool box2 = false;
                    for (int d = cd0; d <= cd1; d++) { n++; int i = Fi(o.Axis, u, y, d); if (map.Solid[i]) solid++; box2 |= covered[i]; }
                    if (!box2 || solid * 10 < n * 6) continue;
                    double share = (double)solid / n;
                    float dc = (u - cu) * (u - cu) + (y - cy) * (y - cy);
                    if (share > bestSolid + 1e-9 || (Math.Abs(share - bestSolid) <= 1e-9 && dc < bestDist))
                    {
                        bestSolid = share; bestDist = dc; wall = Raw(u, y, (cd0 + cd1 + 1) / 2f);
                    }
                }
            list.Add(new AshfallKitOpening(o.Axis, Raw(cu, cy, (cd0 + cd1 + 1) / 2f), depthMin, depthMax, (o.MaxU - o.MinU + 1) * f, (o.MaxY - o.MinY + 1) * f, 100.0 * clear / o.Core.Count) { Wall = wall });
        }
        return list;
    }
}
