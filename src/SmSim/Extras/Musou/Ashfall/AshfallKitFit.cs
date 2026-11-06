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

/// <summary>
/// How well a model's boxes match its mesh, measured on a finer grid than the fit's (draft 6's collision quality gate, <see cref="AshfallKitFit.Measure"/>; thresholds in <see cref="AshfallKitFit.Gate"/>).
/// "Far air": cells a box covers that are empty and farther than one fit cell (<see cref="BandCells"/> fine cells) from the mesh's solid, i.e. air a player would bump into.
/// </summary>
/// <param name="FineResolution">Cells on the longest axis of the measuring grid.</param>
/// <param name="BandCells">Fine cells of air next to the solid that are not counted (one fit cell: a box on the fit's grid cannot do better).</param>
/// <param name="FineSolidCells">Solid fine cells (surface plus enclosed interior).</param>
/// <param name="FineIou">Intersection over union of the boxes' fine cells and the fine solid (no band).</param>
/// <param name="FarAirPercent">Far air in per cent of the boxes' union.</param>
/// <param name="OverhangAirPercent">Far air with solid above it in its column (under a balcony, a lintel, a deck), in per cent of the boxes' union.</param>
/// <param name="WorstBoxAirPercent">The largest share of far air in any one box, in per cent.</param>
/// <param name="MissedPercent">Surface cells of the fine solid farther than the band from every box (collision missing where the model is), in per cent of the surface cells (a hollow fit of a solid model misses nothing a player can reach).</param>
/// <param name="CoveredCells">Fine cells covered by the boxes' union.</param>
public sealed record AshfallKitFitQuality(int FineResolution, int BandCells, int FineSolidCells, double FineIou, double FarAirPercent, double OverhangAirPercent, double WorstBoxAirPercent,
    double MissedPercent, int CoveredCells)
{
    /// <summary>Box silhouette where the model is see-through (more than the band away from the model's silhouette), along the worst of the three axes, in per cent of the model's silhouette.</summary>
    public double PhantomSilhouettePercent { get; init; }

    /// <summary>
    /// The largest connected pocket of deep air inside the boxes (more than twice the band from the model: a chunk of air, not a skin along a surface), as the edge of a cube of the same volume
    /// in per cent of the model's longest side (a 20 m tower: 1 % is 0.2 m).
    /// </summary>
    public double LargestPocketPercent { get; init; }

    /// <summary>
    /// Share of the model's silhouette, along the worst of the three axes, behind which a box holds deep air (more than twice the band from the model): how much of the model, seen from a side, has
    /// collision in empty space somewhere along the line of sight (a filled room, frame or the space under a balcony), in per cent.
    /// </summary>
    public double DeepAirViewPercent { get; init; }

    /// <summary>Model silhouette where the boxes are see-through (a hole through the collision), along the worst of the three axes, in per cent of the model's silhouette.</summary>
    public double HoleSilhouettePercent { get; init; }

    /// <summary>Through-openings found in the model's walls (windows, arches, door-sized holes; the window pass, <see cref="AshfallKitFit.FindOpenings"/>).</summary>
    public int OpeningCount { get; init; }

    /// <summary>Openings whose core the boxes block (less than <see cref="AshfallKitFit.Gate.MinOpeningClearPercent"/> % clear): a shot through that window would stop.</summary>
    public int BlockedOpenings { get; init; }

    /// <summary>The least clear opening's core share, in per cent (100 without openings).</summary>
    public double WorstOpeningClearPercent { get; init; } = 100;

    /// <summary>Each opening as measured (not stored in the generated file; <see cref="AshfallKitCollision.Openings"/> holds the committed ones).</summary>
    public IReadOnlyList<AshfallKitOpening> Openings { get; init; } = [];

    /// <summary>Why the fit fails the gate (empty when it passes).</summary>
    public IReadOnlyList<string> Failures() => AshfallKitFit.Gate.Failures(this);

    public bool Passes => Failures().Count == 0;
}

/// <summary>
/// A walkable ramp found in a model's top heightfield (raw glTF frame, Y up): a run of columns whose tops rise along <see cref="Dir"/> (0 +X, 1 -X, 2 +Z, 3 -Z). <see cref="Min"/>/<see cref="Max"/> bound the footprint;
/// Min.Y is the wedge's bottom, <see cref="YLow"/> and <see cref="YHigh"/> the top plane's heights at the low and the high edge (the plane runs through the middle of the steps, so a riser pokes at most half its height above it).
/// </summary>
public sealed record AshfallRamp(Vector3 Min, Vector3 Max, int Dir, float YLow, float YHigh);

public sealed record AshfallKitFitResult(IReadOnlyList<(Vector3 Min, Vector3 Max)> Boxes, AshfallKitFitMetrics Metrics)
{
    public IReadOnlyList<AshfallRamp> Ramps { get; init; } = [];
}

/// <summary>The settings one fit ran with (draft 6: chosen per model by <see cref="AshfallKitFit.FitGated"/>).</summary>
public sealed record AshfallKitFitSettings(int Resolution, int MaxBoxes, float FillTolerance, int Seal);

/// <summary>A model's gated fit: the fewest boxes among the settings tried that pass the gate (or, when none does, the closest), with its measure.</summary>
public sealed record AshfallKitGatedFit(AshfallKitFitResult Fit, AshfallKitFitQuality Quality, AshfallKitFitSettings Settings, int Tried)
{
    /// <summary>The model's through-openings the fit carved and the gate checked (window pass).</summary>
    public AshfallKitOpeningMap? Openings { get; init; }
}

/// <summary>
/// Offline collision fit (Ashfall kit, draft 4): a model's triangles become a short list of axis-aligned boxes that approximate its solid form. Pure and deterministic: voxelise on a grid anchored
/// to the raw bounds (exact triangle/cell overlap), fill the solid (everything the exterior cannot reach, so doorways, windows and gaps under spans stay open), merge greedily into boxes, then
/// reduce to a cap by the cheapest merges, never past a fill tolerance and never over a vertical opening (2 cells wide, 3 tall, open to the grid's outside).
/// Numbers only: the output is committed as <c>AshfallKitCollision.g.cs</c> by <c>smtool ashfall-kit-collision</c>. Draft 6 adds the collision quality gate (<see cref="Measure"/>, <see cref="Gate"/>, <see cref="FitGated"/>):
/// dust is a loose piece of solid, an opening blocks a merge only beyond the surface's own cell, and an optional seal closes cracks before the fill.
/// </summary>
public static partial class AshfallKitFit
{
    /// <summary>Cells on the longest axis for the boxes. Finer grids (the first try used 40) trace every chip of the Meshy surfaces and leave 50 to 130 boxes that no tolerance merges; 16 keeps the fit smooth.</summary>
    public const int DefaultResolution = 16;
    /// <summary>Cells on the longest axis for the walkable-top search (a stand-on surface needs 4 x 4 cells, which a coarse box grid cannot show).</summary>
    public const int DefaultTopResolution = 40;
    public const int DefaultMaxBoxes = 32;

    /// <summary>Models whose tops or stairs are meant to be walked on (draft 5): fitted finer (<see cref="WalkableResolution"/> cells, at most <see cref="WalkableMaxBoxes"/> boxes, top search at the same resolution) with ramp detection. One list for the generator command and the level pass.</summary>
    /// <remarks>Draft 6 adds the Meshy expansion's traversal pieces (walls with tops, gatehouse, keep, bridge, stair, ramp, the low and medium platforms, the two-storey ruin, the tower, the mound, the dais);
    /// its quarantined models (<see cref="AshfallKitFlags.TraversalQuarantined"/>: AC-043, AC-048) are fitted as solids, not walkable.</remarks>
    public static readonly IReadOnlyList<string> WalkableIds = ["AC-003", "AC-005", "AC-006", "AC-007", "AC-008", "AC-009", "AC-010", "AC-037",
        "AC-041", "AC-042", "AC-044", "AC-045", "AC-046", "AC-047", "AC-049", "AC-050", "AC-051", "AC-052", "AC-053", "AC-076"];

    /// <summary>The step a walker climbs (metres; the level pass's rule, <c>AshfallDraft5.StepHeight</c>).</summary>
    public const float StepHeight = 0.4f;

    /// <summary>The largest uniform scale (metres per raw unit) at which a fit's cell, the height quantum of its boxes, stays within <see cref="StepHeight"/>.</summary>
    public static float MaxWalkScale(float cellSize) => cellSize <= 0f ? float.PositiveInfinity : StepHeight / cellSize;
    public const int WalkableResolution = 48, WalkableMaxBoxes = 64;
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
        int resolution = DefaultResolution, int maxBoxes = DefaultMaxBoxes, float fillTolerance = DefaultFillTolerance, int topResolution = DefaultTopResolution, bool ramps = false, int seal = 0,
        AshfallKitOpeningMap? openings = null)
    {
        var size = rawMax - rawMin;
        float longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (longest <= 0f) return new(Array.Empty<(Vector3, Vector3)>(), new(0, 0, 0, 0, 0, 0, false, false, [], 0, 0, 0, 0f));
        float cell = longest / resolution;
        var g = seal > 0 ? SealedSolidGrid(positions, indices, rawMin, size, cell, seal) : SolidGrid(positions, indices, rawMin, size, cell);
        int solidCells = g.Solid.Count(b => b);
        // Window pass (2026-11-06): the cells in a through-opening are cleared before anything merges, and no merge may cover them again.
        var carved = openings != null ? CarveOpenings(g, cell, openings) : new bool[g.Solid.Length];

        // Dust (draft 6): loose flecks, i.e. connected pieces of solid (26-connected) smaller than 0.2 % of the solid volume, are removed before merging. Draft 5 dropped every
        // one-cell-thick merged box under that size, which at the walkable models' 48 cells cut thin walls into strips and dropped them (AC-010 lost 28 % of its solid: holes through its walls).
        var (dropped, droppedVol) = RemoveDust(g, solidCells * 0.002);
        var kept = GreedyMerge(g);
        if (kept.Count == 0 && solidCells > 0)
        {
            // everything was dust: keep the solid as it was
            g = seal > 0 ? SealedSolidGrid(positions, indices, rawMin, size, cell, seal) : SolidGrid(positions, indices, rawMin, size, cell);
            carved = openings != null ? CarveOpenings(g, cell, openings) : new bool[g.Solid.Length];
            kept = GreedyMerge(g); dropped = 0; droppedVol = 0;
        }

        bool capReached = false;
        if (kept.Count > maxBoxes) { capReached = true; kept = Reduce(g, kept, maxBoxes, fillTolerance, carved); }
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
        return new(outBoxes, m) { Ramps = ramps ? FindRamps(tg, rawMin, topCell) : [] };
    }

    /// <summary>
    /// Stair and rubble-ramp runs of the top heightfield: along each row and column, a maximal stretch of columns whose tops do not fall, trimmed to its first and last change, at least 3 columns long with at least 2 rises,
    /// whose overall slope is at most 50 degrees. Equal runs on neighbouring lines are merged into one footprint (at least 3 cells wide). The top plane runs through the middle of the steps.
    /// </summary>
    static List<AshfallRamp> FindRamps(Grid g, Vector3 rawMin, float cell)
    {
        var top = new float[g.Nx * g.Nz];   // height of the highest solid cell's top, NaN where empty
        for (int z = 0; z < g.Nz; z++)
            for (int x = 0; x < g.Nx; x++)
            {
                int y = g.Ny - 1;
                while (y >= 0 && !g.IsSolid(x, y, z)) y--;
                top[x + g.Nx * z] = y < 0 ? float.NaN : (y + 1) * cell;
            }
        var found = new SortedDictionary<(int Dir, int I0, int I1, int H0, int H1), List<int>>();
        for (int dir = 0; dir < 4; dir++)
        {
            bool alongX = dir < 2, forward = dir % 2 == 0;
            int lines = alongX ? g.Nz : g.Nx, len = alongX ? g.Nx : g.Nz;
            for (int line = 0; line < lines; line++)
            {
                float H(int i) { int k = forward ? i : len - 1 - i; return alongX ? top[k + g.Nx * line] : top[line + g.Nx * k]; }
                int start = 0;
                while (start < len)
                {
                    if (float.IsNaN(H(start))) { start++; continue; }
                    int end = start;
                    while (end + 1 < len && !float.IsNaN(H(end + 1)) && H(end + 1) >= H(end) - 1e-4f) end++;
                    int a = start, b = end;
                    while (a < b && H(a + 1) - H(a) < 1e-4f) a++;
                    while (b > a && H(b) - H(b - 1) < 1e-4f) b--;
                    int rises = 0;
                    for (int i = a; i < b; i++) if (H(i + 1) - H(i) > 1e-4f) rises++;
                    float rise = H(b) - H(a), runLen = (b - a) * cell;
                    if (b - a >= 2 && rises >= 2 && runLen > 0f && MathF.Atan(rise / runLen) <= 50f * MathF.PI / 180f)
                    {
                        var key = (dir, a, b, (int)MathF.Round(H(a) / cell), (int)MathF.Round(H(b) / cell));
                        if (!found.TryGetValue(key, out var l)) found[key] = l = [];
                        l.Add(line);
                    }
                    start = end + 1;
                }
            }
        }
        var result = new List<(int Area, AshfallRamp Ramp)>();
        foreach (var (key, lines) in found)
        {
            lines.Sort();
            int s0 = 0;
            for (int k = 1; k <= lines.Count; k++)
            {
                if (k < lines.Count && lines[k] == lines[k - 1] + 1) continue;
                int l0 = lines[s0], l1 = lines[k - 1];
                s0 = k;
                if (l1 - l0 + 1 < 3) continue;
                var (dir, i0, i1, _, _) = key;
                bool alongX = dir < 2, forward = dir % 2 == 0;
                int len = alongX ? g.Nx : g.Nz;
                int c0 = forward ? i0 : len - 1 - i1, c1 = forward ? i1 : len - 1 - i0;
                float H(int i, int line) { int kk = forward ? i : len - 1 - i; return alongX ? top[kk + g.Nx * line] : top[line + g.Nx * kk]; }
                float h0 = float.MaxValue, h1 = float.MinValue, dMax = float.MinValue, dMin = float.MaxValue;
                for (int line = l0; line <= l1; line++) { h0 = MathF.Min(h0, H(i0, line)); h1 = MathF.Max(h1, H(i1, line)); }
                for (int line = l0; line <= l1; line++)
                    for (int i = i0; i <= i1; i++)
                    {
                        float d = H(i, line) - (h0 + (h1 - h0) * (i - i0) / (i1 - i0));
                        dMax = MathF.Max(dMax, d); dMin = MathF.Min(dMin, d);
                    }
                float off = (dMax + dMin) / 2f;
                float yLow = h0 + off + rawMin.Y, yHigh = h1 + off + rawMin.Y, bottom = MathF.Min(h0 + rawMin.Y, yLow);
                float xa = alongX ? rawMin.X + c0 * cell : rawMin.X + l0 * cell, xb = alongX ? rawMin.X + (c1 + 1) * cell : rawMin.X + (l1 + 1) * cell;
                float za = alongX ? rawMin.Z + l0 * cell : rawMin.Z + c0 * cell, zb = alongX ? rawMin.Z + (l1 + 1) * cell : rawMin.Z + (c1 + 1) * cell;
                result.Add(((l1 - l0 + 1) * (i1 - i0), new AshfallRamp(new Vector3(xa, bottom, za), new Vector3(xb, MathF.Max(yHigh, yLow), zb), dir, yLow, yHigh)));
            }
        }
        return result.OrderByDescending(r => r.Area).ThenBy(r => r.Ramp.Dir).ThenBy(r => r.Ramp.Min.X).ThenBy(r => r.Ramp.Min.Z).Take(12).Select(r => r.Ramp).ToList();
    }

    /// <summary>
    /// <see cref="SolidGrid"/> for a grid whose cell is finer than the mesh's cracks: the surface is grown by <paramref name="seal"/> cells, the exterior flooded around that, then grown back
    /// <paramref name="seal"/> steps (26-connected) through cells that are not surface. Gaps up to about 2 x <paramref name="seal"/> cells stay closed; real openings (a doorway, an open frame) stay open.
    /// The grid is padded by <paramref name="seal"/> + 1 cells while this runs so that nothing near the bounds is sealed against the grid's edge.
    /// </summary>
    static Grid SealedSolidGrid(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, Vector3 rawMin, Vector3 size, float cell, int seal)
    {
        int pad = seal + 1;
        var g = new Grid { Nx = Cells(size.X, cell), Ny = Cells(size.Y, cell), Nz = Cells(size.Z, cell) };
        var p = new Grid { Nx = g.Nx + 2 * pad, Ny = g.Ny + 2 * pad, Nz = g.Nz + 2 * pad };
        p.Solid = new bool[p.Nx * p.Ny * p.Nz];
        var surface = Voxelise(p, positions, indices, rawMin - new Vector3(pad * cell), cell);
        var ext = FloodExterior(p, Dilate(p, surface, seal));
        var frontier = new List<int>();
        for (int i = 0; i < ext.Length; i++) if (ext[i]) frontier.Add(i);
        for (int step = 0; step < seal; step++)
        {
            var next = new List<int>();
            foreach (int i in frontier)
            {
                int x = i % p.Nx, z = i / p.Nx % p.Nz, y = i / (p.Nx * p.Nz);
                for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy, nz = z + dz;
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= p.Nx || ny >= p.Ny || nz >= p.Nz) continue;
                    int j = p.Index(nx, ny, nz);
                    if (ext[j] || surface[j]) continue;
                    ext[j] = true; next.Add(j);
                }
            }
            frontier = next;
        }
        g.Solid = new bool[g.Nx * g.Ny * g.Nz];
        for (int y = 0; y < g.Ny; y++) for (int z = 0; z < g.Nz; z++) for (int x = 0; x < g.Nx; x++)
            g.Solid[g.Index(x, y, z)] = !ext[p.Index(x + pad, y + pad, z + pad)];
        return g;
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

    /// <summary>Clears every 26-connected piece of solid smaller than <paramref name="limit"/> cells; returns how many pieces and cells went.</summary>
    static (int Pieces, long Cells) RemoveDust(Grid g, double limit)
    {
        var seen = new bool[g.Solid.Length];
        int pieces = 0; long cells = 0;
        var stack = new Stack<int>();
        var comp = new List<int>();
        for (int s = 0; s < g.Solid.Length; s++)
        {
            if (!g.Solid[s] || seen[s]) continue;
            comp.Clear(); stack.Push(s); seen[s] = true;
            while (stack.Count > 0)
            {
                int i = stack.Pop(); comp.Add(i);
                int x = i % g.Nx, z = i / g.Nx % g.Nz, y = i / (g.Nx * g.Nz);
                for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy, nz = z + dz;
                    if (!g.IsSolid(nx, ny, nz)) continue;
                    int j = g.Index(nx, ny, nz);
                    if (seen[j]) continue;
                    seen[j] = true; stack.Push(j);
                }
            }
            if (comp.Count < limit) { pieces++; cells += comp.Count; foreach (int i in comp) g.Solid[i] = false; }
        }
        return (pieces, cells);
    }

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

    static List<Box> Reduce(Grid g, List<Box> start, int maxBoxes, float tolerance, bool[] carved)
    {
        // Draft 6: a merge pays only for empty cells more than one cell from the solid (air next to the surface is the fit's own quantisation; the gate does not count it either),
        // and an opening is kept only where it is more than one cell from the solid.
        var near = Dilate(g, g.Solid, 1);
        var solid = new Prefix(g, i => g.Solid[i]);
        var prot = ProtectedOpenings(g);
        var protectedCount = new Prefix(g, i => (prot[i] && !near[i]) || carved[i]);
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

    // ---------------------------------------------------------------- quality gate (draft 6)

    /// <summary>
    /// The settings <see cref="FitGated"/> tries, cheapest first. Walkable models keep <see cref="WalkableResolution"/> (the placement's step rule reads their cell size); the others go from 24 to 48 cells.
    /// Fill tolerances 15, 10 and 5 %, caps of 32 and 64 boxes (64 and 128 for the walkable ones), and the solid either as voxelised or with one-cell cracks sealed.
    /// </summary>
    public static IEnumerable<AshfallKitFitSettings> GateLadder(bool walkable)
    {
        int[] resolutions = walkable ? [WalkableResolution] : [24, 32, 48];
        int[] caps = walkable ? [WalkableMaxBoxes, 2 * WalkableMaxBoxes] : [DefaultMaxBoxes, 2 * DefaultMaxBoxes];
        foreach (int res in resolutions)
            foreach (float fill in new[] { 0.15f, 0.10f, 0.05f })
                foreach (int cap in caps)
                    foreach (int seal in new[] { 0, 1 })
                        yield return new(res, cap, fill, seal);
    }

    /// <summary>
    /// Fits a model with every setting of <see cref="GateLadder"/> and keeps the passing fit with the fewest boxes (ties: the earlier setting). When no setting passes, the one with the fewest gate failures,
    /// then the fewest boxes, is returned and its <see cref="AshfallKitGatedFit.Quality"/> says why it fails. Deterministic.
    /// </summary>
    public static AshfallKitGatedFit FitGated(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, Vector3 rawMin, Vector3 rawMax, bool walkable)
    {
        AshfallKitGatedFit? best = null;
        var openings = FindOpenings(positions, indices, rawMin, rawMax);
        int bestFailures = int.MaxValue, tried = 0;
        foreach (var st in GateLadder(walkable))
        {
            tried++;
            var fit = Fit(positions, indices, rawMin, rawMax, st.Resolution, st.MaxBoxes, st.FillTolerance, walkable ? st.Resolution : DefaultTopResolution, ramps: walkable, seal: st.Seal, openings: openings);
            var q = Measure(positions, indices, rawMin, rawMax, fit.Boxes, fit.Metrics.CellSize, openings);
            int failures = q.Failures().Count;
            if (best == null || failures < bestFailures || (failures == bestFailures && fit.Boxes.Count < best.Fit.Boxes.Count))
            {
                best = new(fit, q, st, 0); bestFailures = failures;
            }
        }
        return best! with { Tried = tried, Openings = openings };
    }

    /// <summary>
    /// The collision quality gate's thresholds (draft 6; ours, an estimate set on the kit of 2026-11-05: 28 of the 40 draft-5 fits fail it, the 16-cell fits for the air they fill and AC-003, AC-005 and AC-010
    /// for holes through their walls, while every gated refit passes). One place for the tool and the tests; see <see cref="AshfallKitFitQuality"/> for what each measure is.
    /// </summary>
    public static class Gate
    {
        public const double MaxFarAirPercent = 8.0;
        public const double MaxOverhangAirPercent = 3.0;
        public const double MaxWorstBoxAirPercent = 35.0;
        public const double MaxMissedPercent = 5.0;
        public const double MaxPhantomSilhouettePercent = 2.0;
        public const double MaxHoleSilhouettePercent = 2.0;
        public const double MaxPocketPercent = 4.0;
        public const double MaxDeepAirViewPercent = 10.0;

        /// <summary>Window pass (2026-11-06; ours, an estimate): every through-opening's core must be at least this clear of boxes (frames and mullions may stay solid; the core is the opening less one band at its edge).</summary>
        public const double MinOpeningClearPercent = 90.0;

        /// <summary>Models the gate only reports: the manifest flags them never-used (AC-015, AC-016, AC-026).</summary>
        public static readonly IReadOnlyList<string> ReportOnlyIds = ["AC-015", "AC-016", "AC-026"];

        public static IReadOnlyList<string> Failures(AshfallKitFitQuality q)
        {
            var r = new List<string>();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (q.FarAirPercent > MaxFarAirPercent) r.Add(string.Format(inv, "far air {0:F1} % over {1:F1} %", q.FarAirPercent, MaxFarAirPercent));
            if (q.OverhangAirPercent > MaxOverhangAirPercent) r.Add(string.Format(inv, "overhang air {0:F1} % over {1:F1} %", q.OverhangAirPercent, MaxOverhangAirPercent));
            if (q.WorstBoxAirPercent > MaxWorstBoxAirPercent) r.Add(string.Format(inv, "a box {0:F1} % far air, over {1:F1} %", q.WorstBoxAirPercent, MaxWorstBoxAirPercent));
            if (q.MissedPercent > MaxMissedPercent) r.Add(string.Format(inv, "missed solid {0:F1} % over {1:F1} %", q.MissedPercent, MaxMissedPercent));
            if (q.PhantomSilhouettePercent > MaxPhantomSilhouettePercent) r.Add(string.Format(inv, "boxes fill {0:F1} % of a see-through view, over {1:F1} %", q.PhantomSilhouettePercent, MaxPhantomSilhouettePercent));
            if (q.HoleSilhouettePercent > MaxHoleSilhouettePercent) r.Add(string.Format(inv, "holes through the boxes {0:F1} % of a view, over {1:F1} %", q.HoleSilhouettePercent, MaxHoleSilhouettePercent));
            if (q.DeepAirViewPercent > MaxDeepAirViewPercent) r.Add(string.Format(inv, "deep air behind {0:F1} % of a view, over {1:F1} %", q.DeepAirViewPercent, MaxDeepAirViewPercent));
            if (q.BlockedOpenings > 0) r.Add(string.Format(inv, "{0} of {1} window opening(s) blocked (least clear {2:F0} %, under {3:F0} %)", q.BlockedOpenings, q.OpeningCount, q.WorstOpeningClearPercent, MinOpeningClearPercent));
            if (q.LargestPocketPercent > MaxPocketPercent) r.Add(string.Format(inv, "an air pocket {0:F1} % of the longest side, over {1:F1} %", q.LargestPocketPercent, MaxPocketPercent));
            return r;
        }
    }

    /// <summary>Cells on the longest axis of the fine grid the gate measures a fit against (a fixed fine cell: 144 is 3 x the walkable fit's 48 and 9 x the default 16).</summary>
    public const int MeasureResolution = 144;

    /// <summary>Fine cells of air next to the solid that the gate does not count (3 of 144: one cell of the walkable fit (48), the finest grid the fit uses; about 2 % of the model's longest side).</summary>
    public const int BandCells = 3;

    /// <summary>Cracks in the mesh up to about twice this many fine cells wide are sealed before the interior is filled (the generated models are not watertight at the fine cell).</summary>
    public const int SealCells = 2;

    /// <summary>
    /// Measures a fit against a finer voxelisation of the same mesh (<see cref="MeasureResolution"/> cells on the longest axis; solid = surface plus what the exterior cannot reach).
    /// Cracks up to 2 x <see cref="SealCells"/> cells are sealed first. Air within <see cref="BandCells"/> fine cells of the solid (one cell of the 48-cell fit, the finest the fit uses) is not counted as air; everything else a box covers and the mesh does not is far air.
    /// <paramref name="fitCell"/> (the fit's cell) is not used by the measure; it stays so callers record which fit was measured.
    /// </summary>
    public static AshfallKitFitQuality Measure(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, Vector3 rawMin, Vector3 rawMax, IReadOnlyList<(Vector3 Min, Vector3 Max)> boxes, float fitCell,
        AshfallKitOpeningMap? openings = null)
    {
        var size = rawMax - rawMin;
        float longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (longest <= 0f || boxes.Count == 0) return new(MeasureResolution, 0, 0, 1.0, 0, 0, 0, 0, 0);
        float cell = longest / MeasureResolution;
        var g = SealedSolidGrid(positions, indices, rawMin, size, cell, SealCells);
        int band = BandCells;
        var near = Dilate(g, g.Solid, band);

        var covered = new bool[g.Solid.Length];
        var farAir = new bool[g.Solid.Length];
        for (int i = 0; i < g.Solid.Length; i++) farAir[i] = !near[i];
        var farPrefix = new Prefix(g, i => farAir[i]);
        double worstBox = 0;
        foreach (var (mn, mx) in boxes)
        {
            var b = CellBox(g, mn - rawMin, mx - rawMin, cell);
            if (b is not { } bb) continue;
            for (int y = bb.Y0; y <= bb.Y1; y++) for (int z = bb.Z0; z <= bb.Z1; z++) for (int x = bb.X0; x <= bb.X1; x++) covered[g.Index(x, y, z)] = true;
            // A box thinner than the band is all band: its air cannot be told from quantisation.
            worstBox = Math.Max(worstBox, (double)farPrefix.Count(bb) / bb.Volume);
        }
        // Solid somewhere above a cell in its column: the cell is under an overhang (a balcony, a lintel, a deck).
        var solidAbove = new bool[g.Solid.Length];
        for (int z = 0; z < g.Nz; z++) for (int x = 0; x < g.Nx; x++)
        {
            bool any = false;
            for (int y = g.Ny - 1; y >= 0; y--) { int i = g.Index(x, y, z); solidAbove[i] = any; any |= g.Solid[i]; }
        }
        // Solid the boxes miss by more than the band (collision that is not there).
        var nearBox = Dilate(g, covered, band);
        long inter = 0, uni = 0, cov = 0, far = 0, over = 0, solid = 0, missed = 0, surface = 0;
        for (int i = 0; i < covered.Length; i++)
        {
            bool c = covered[i], s = g.Solid[i];
            if (s) { solid++; if (OnSurface(g, i)) { surface++; if (!nearBox[i]) missed++; } }
            if (c) cov++;
            if (c && s) inter++;
            if (c || s) uni++;
            if (c && farAir[i]) { far++; if (solidAbove[i]) over++; }
        }
        // The largest connected pocket of deep air inside the boxes (6-connected), as the edge of a cube of the same volume in per cent of the model's longest side.
        long largest = 0;
        var deepNear = Dilate(g, g.Solid, 2 * band);
        {
            var seen = new bool[covered.Length];
            var stack = new Stack<int>();
            for (int s0 = 0; s0 < covered.Length; s0++)
            {
                if (!covered[s0] || deepNear[s0] || seen[s0]) continue;
                long n = 0; stack.Push(s0); seen[s0] = true;
                while (stack.Count > 0)
                {
                    int i = stack.Pop(); n++;
                    int x = i % g.Nx, z = i / g.Nx % g.Nz, y = i / (g.Nx * g.Nz);
                    void Try(int xx, int yy, int zz)
                    {
                        if (xx < 0 || yy < 0 || zz < 0 || xx >= g.Nx || yy >= g.Ny || zz >= g.Nz) return;
                        int j = g.Index(xx, yy, zz);
                        if (seen[j] || !covered[j] || deepNear[j]) return;
                        seen[j] = true; stack.Push(j);
                    }
                    Try(x - 1, y, z); Try(x + 1, y, z); Try(x, y - 1, z); Try(x, y + 1, z); Try(x, y, z - 1); Try(x, y, z + 1);
                }
                largest = Math.Max(largest, n);
            }
        }
        double pocket = 100.0 * Math.Cbrt(largest) / MeasureResolution;
        // Silhouettes along each axis: box where the model is see-through (a filled doorway, frame or the air under a balcony seen from the side), and model where the boxes are see-through (a hole through the collision).
        double phantom = 0, holes = 0, airView = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            var p = new Grid { Nx = axis == 0 ? 1 : g.Nx, Ny = axis == 1 ? 1 : g.Ny, Nz = axis == 2 ? 1 : g.Nz };
            var meshSil = new bool[p.Nx * p.Ny * p.Nz]; var boxSil = new bool[meshSil.Length]; var airSil = new bool[meshSil.Length];
            for (int y = 0; y < g.Ny; y++) for (int z = 0; z < g.Nz; z++) for (int x = 0; x < g.Nx; x++)
            {
                int i = g.Index(x, y, z), j = p.Index(axis == 0 ? 0 : x, axis == 1 ? 0 : y, axis == 2 ? 0 : z);
                meshSil[j] |= g.Solid[i]; boxSil[j] |= covered[i]; airSil[j] |= covered[i] && !deepNear[i];
            }
            var meshNear = Dilate(p, meshSil, band); var boxNear = Dilate(p, boxSil, band);
            int silCount = 0, ph = 0, ho = 0, av = 0;
            for (int j = 0; j < meshSil.Length; j++)
            {
                if (meshSil[j]) { silCount++; if (!boxNear[j]) ho++; }
                if (boxSil[j] && !meshNear[j]) ph++;
                if (airSil[j]) av++;
            }
            if (silCount > 0) { phantom = Math.Max(phantom, 100.0 * ph / silCount); holes = Math.Max(holes, 100.0 * ho / silCount); airView = Math.Max(airView, 100.0 * av / silCount); }
        }
        // Window pass: every through-opening's core must stay clear of the boxes.
        openings ??= FindOpenings(positions, indices, rawMin, rawMax);
        var measured = openings.Nx == g.Nx && openings.Ny == g.Ny && openings.Nz == g.Nz ? MeasureOpenings(openings, covered) : [];
        return new(MeasureResolution, band, (int)solid, uni == 0 ? 1.0 : (double)inter / uni,
            cov == 0 ? 0 : 100.0 * far / cov, cov == 0 ? 0 : 100.0 * over / cov, 100.0 * worstBox, surface == 0 ? 0 : 100.0 * missed / surface, (int)cov)
        {
            PhantomSilhouettePercent = phantom, HoleSilhouettePercent = holes, LargestPocketPercent = pocket, DeepAirViewPercent = airView,
            OpeningCount = measured.Count, BlockedOpenings = measured.Count(o => !o.Open), WorstOpeningClearPercent = measured.Count == 0 ? 100 : measured.Min(o => o.ClearPercent), Openings = measured,
        };
    }

    /// <summary>
    /// A PNG for checking a fit by eye (smtool ashfall-kit-collision --views): top row the front (looking along -Z), bottom row the side (along +X), each at <see cref="MeasureResolution"/> cells;
    /// left the model's solid (grey, darker is nearer), middle the boxes (blue), right the far air inside the boxes (red, darker is more air along the view).
    /// With <paramref name="openings"/> (window pass) each through-opening seen along the view is drawn on the model (left) and the boxes (middle): green where no box blocks its ray through the wall, magenta where one does.
    /// </summary>
    public static byte[] ViewsPng(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices, Vector3 rawMin, Vector3 rawMax, IReadOnlyList<(Vector3 Min, Vector3 Max)> boxes, int scale,
        AshfallKitOpeningMap? openings = null)
    {
        int res = MeasureResolution;
        var size = rawMax - rawMin;
        float longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        float cell = longest / res;
        var g = SealedSolidGrid(positions, indices, rawMin, size, cell, SealCells);
        var near = Dilate(g, g.Solid, BandCells);
        var cov = new bool[g.Solid.Length];
        foreach (var (mn, mx) in boxes) { var b = CellBox(g, mn - rawMin, mx - rawMin, cell); if (b is { } bb) for (int y = bb.Y0; y <= bb.Y1; y++) for (int z = bb.Z0; z <= bb.Z1; z++) for (int x = bb.X0; x <= bb.X1; x++) cov[g.Index(x, y, z)] = true; }
        int pw = g.Nx + g.Nz + 3, W = pw * 3 * scale, H = (g.Ny + 2) * 2 * scale;
        var img = new byte[W * H * 3];
        void Px(int px, int py, byte r, byte gg, byte b) { for (int sy = 0; sy < scale; sy++) for (int sx = 0; sx < scale; sx++) { int o = ((py * scale + sy) * W + px * scale + sx) * 3; img[o] = r; img[o + 1] = gg; img[o + 2] = b; } }
        for (int i = 0; i < img.Length; i++) img[i] = 255;
        // row 0: front view (x across, y up, first hit along +z); row 1: side view (z across, y up, first hit along +x)
        for (int view = 0; view < 2; view++)
        {
            int nu = view == 0 ? g.Nx : g.Nz, nd = view == 0 ? g.Nz : g.Nx;
            for (int y = 0; y < g.Ny; y++) for (int u = 0; u < nu; u++)
            {
                int solidD = -1, boxD = -1, air = 0;
                for (int d = 0; d < nd; d++)
                {
                    int i = view == 0 ? g.Index(u, y, d) : g.Index(d, y, u);
                    if (solidD < 0 && g.Solid[i]) solidD = d;
                    if (boxD < 0 && cov[i]) boxD = d;
                    if (cov[i] && !near[i]) air++;
                }
                int py = view * (g.Ny + 2) + (g.Ny - 1 - y);
                byte Sh(int d) => (byte)(60 + 160 * d / Math.Max(1, nd));
                if (solidD >= 0) Px(u, py, Sh(solidD), Sh(solidD), Sh(solidD));
                if (boxD >= 0) Px(pw + u, py, Sh(boxD), Sh(boxD), (byte)255);
                if (air > 0) { byte a = (byte)Math.Min(255, 80 + air * 12); Px(2 * pw + u, py, 255, (byte)(255 - a), (byte)(255 - a)); }
                else if (solidD >= 0) Px(2 * pw + u, py, 220, 220, 220);
            }
        }
        if (openings != null && openings.Nx == g.Nx && openings.Ny == g.Ny && openings.Nz == g.Nz)
            foreach (var o in openings.Found)
            {
                int view = o.Axis == 2 ? 0 : 1;
                foreach (var (u, y, d0, d1) in o.Runs)
                {
                    bool blocked = false;
                    for (int d = d0; d <= d1 && !blocked; d++) blocked = cov[o.Axis == 2 ? g.Index(u, y, d) : g.Index(d, y, u)];
                    int py = view * (g.Ny + 2) + (g.Ny - 1 - y);
                    var (r, gg, b) = blocked ? ((byte)230, (byte)0, (byte)200) : ((byte)0, (byte)190, (byte)60);
                    Px(u, py, r, gg, b); Px(pw + u, py, r, gg, b);
                }
            }
        // PNG
        using var ms = new MemoryStream();
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); ms.Write(len);
            var t = System.Text.Encoding.ASCII.GetBytes(type); ms.Write(t); ms.Write(data);
            uint crc = 0xFFFFFFFF;
            foreach (var bt in t.Concat(data)) { crc ^= bt; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1; }
            var c = BitConverter.GetBytes(~crc); Array.Reverse(c); ms.Write(c);
        }
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13]; var w = BitConverter.GetBytes(W); Array.Reverse(w); var h = BitConverter.GetBytes(H); Array.Reverse(h);
        w.CopyTo(ihdr, 0); h.CopyTo(ihdr, 4); ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr);
        var raw = new byte[(W * 3 + 1) * H];
        for (int y = 0; y < H; y++) { raw[y * (W * 3 + 1)] = 0; Array.Copy(img, y * W * 3, raw, y * (W * 3 + 1) + 1, W * 3); }
        using (var zs = new MemoryStream()) { using (var z = new System.IO.Compression.ZLibStream(zs, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(raw); Chunk("IDAT", zs.ToArray()); }
        Chunk("IEND", []);
        return ms.ToArray();
    }

    /// <summary>A solid cell with an empty 6-neighbour or on the grid's edge.</summary>
    static bool OnSurface(Grid g, int i)
    {
        int x = i % g.Nx, z = i / g.Nx % g.Nz, y = i / (g.Nx * g.Nz);
        return !g.IsSolid(x - 1, y, z) || !g.IsSolid(x + 1, y, z) || !g.IsSolid(x, y - 1, z) || !g.IsSolid(x, y + 1, z) || !g.IsSolid(x, y, z - 1) || !g.IsSolid(x, y, z + 1);
    }

    /// <summary>The fine cells whose centres lie inside a box given in metres from the grid's origin (null when none do).</summary>
    static Box? CellBox(Grid g, Vector3 mn, Vector3 mx, float cell)
    {
        int Lo(float v) => (int)MathF.Ceiling(v / cell - 0.5f - 1e-4f);
        int Hi(float v) => (int)MathF.Floor(v / cell - 0.5f + 1e-4f);
        int x0 = Math.Max(0, Lo(mn.X)), y0 = Math.Max(0, Lo(mn.Y)), z0 = Math.Max(0, Lo(mn.Z));
        int x1 = Math.Min(g.Nx - 1, Hi(mx.X)), y1 = Math.Min(g.Ny - 1, Hi(mx.Y)), z1 = Math.Min(g.Nz - 1, Hi(mx.Z));
        if (x1 < x0 || y1 < y0 || z1 < z0) return null;
        return new Box(x0, y0, z0, x1, y1, z1);
    }

    /// <summary>Chebyshev dilation of a cell set by <paramref name="r"/> cells (three separable passes).</summary>
    static bool[] Dilate(Grid g, bool[] src, int r)
    {
        var a = (bool[])src.Clone();
        var b = new bool[a.Length];
        void Pass(int axis)
        {
            int n = axis == 0 ? g.Nx : axis == 1 ? g.Ny : g.Nz;
            int stride = axis == 0 ? 1 : axis == 1 ? g.Nx * g.Nz : g.Nx;
            for (int i = 0; i < a.Length; i++)
            {
                int x = i % g.Nx, z = i / g.Nx % g.Nz, y = i / (g.Nx * g.Nz);
                int k = axis == 0 ? x : axis == 1 ? y : z;
                if (k != 0) continue;
                // sliding window count along the line
                int count = 0;
                for (int j = 0; j < Math.Min(n, r + 1); j++) if (a[i + j * stride]) count++;
                for (int j = 0; j < n; j++)
                {
                    b[i + j * stride] = count > 0;
                    int add = j + r + 1, rem = j - r;
                    if (add < n && a[i + add * stride]) count++;
                    if (rem >= 0 && a[i + rem * stride]) count--;
                }
            }
            (a, b) = (b, a);
        }
        Pass(0); Pass(1); Pass(2);
        return a;
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
