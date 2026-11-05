using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

// Surfaces and Decoration (kit dressing, draft 1; all ours). Ground tiles are visual only, laid on the flat surface; their box is [surface - raw height, surface] so the tile's top (raw Y = 0) lies exactly on the surface.
internal static partial class AshfallDressPass
{
    /// <summary>The roads' lines: centre, width. Avenues are 24 m, the perimeter road 16 m.</summary>
    static readonly (float C, float W)[] RoadLines = [(-232f, 16f), (-92f, 24f), (92f, 24f), (232f, 16f)];

    static readonly (float Lo, float Hi)[] BlockSpans = [(-224f, -104f), (-80f, 80f), (104f, 224f)];

    /// <summary>A ground tile: footprint centre, surface height it lies on, yaw step, size along the tile's local X and Y (its raw height is kept).</summary>
    /// <summary>Tiles sit this far above the surface they lie on (ours; the ground under them is drawn too, so a shared plane would flicker). Walk-over: under the 0.4 m step.</summary>
    const float Lift = 0.03f;

    static AshfallDressItem Tile(string id, float cx, float cy, float surface, int yawStep, float lx, float ly)
    {
        float h = AshfallKit.Get(id).RawSize.Y;
        return new AshfallDressItem(id, new Vector3(cx, cy, surface - h + Lift), Yaw(yawStep), new Vector3(lx, ly, h), false, AshfallDressRole.Surface, -1);
    }

    /// <summary>
    /// A slab of the Meshy expansion (AC-062..072, draft 6): uniform in plan as the cell asks, its thickness at the model's design scale (the relief keeps its real height), sunk so its walk level
    /// (<see cref="AshfallKitModel.WalkY"/>, the median of its up-facing surface) lies <see cref="Lift"/> over the surface: the walked surface is the ground, the slab's body is under it.
    /// </summary>
    static AshfallDressItem Slab(string id, float cx, float cy, float surface, int yawStep, float lx, float ly)
    {
        var m = AshfallKit.Get(id);
        float h = m.RawSize.Y * m.DesignScale, walk = (m.WalkY - m.RawMin.Y) / m.RawSize.Y * h;
        return new AshfallDressItem(id, new Vector3(cx, cy, surface + Lift - walk), Yaw(yawStep), new Vector3(lx, ly, h), false, AshfallDressRole.Surface, -1);
    }

    /// <summary>
    /// The straight road tile (seeded): AC-068 (avenue paving, the expansion's road slab) for half of them, AC-069 (its shell-damaged variant) and AC-033 (the first library's fractured segment) a quarter each (ours).
    /// AC-040's 1.2 m crater is never used (it would float over the flat collision of a walked road); the variation is also the half turn.
    /// </summary>
    static string Straight(uint seed, int i) => (AshfallBuilder.Mix(seed, (uint)i + 5003u) % 4u) switch { 0u => "AC-033", 3u => "AC-069", _ => "AC-068" };

    static AshfallDressItem Road(string id, float cx, float cy, float surface, int yawStep, float lx, float ly) =>
        id == "AC-033" ? Tile(id, cx, cy, surface, yawStep, lx, ly) : Slab(id, cx, cy, surface, yawStep, lx, ly);

    /// <summary>The expansion's six ash ground slabs (AC-062..067), the seeded variants of the perimeter road's four bare corners (2 x 2 slabs of 8 m each; the blocks themselves stay the ground shader's).</summary>
    static readonly string[] AshSlabs = ["AC-062", "AC-063", "AC-064", "AC-065", "AC-066", "AC-067"];

    /// <summary>The half turn (0 or 12 steps) of a straight tile, from the variation seed (a straight segment keeps its seams at 0 and 180 degrees).</summary>
    static int HalfTurn(uint seed, int i) => AshfallBuilder.Mix(seed, (uint)i + 4001u) % 2u == 0u ? 0 : 12;

    /// <summary>The road and plaza tiles (visual only). Their turns come from <see cref="AshfallMap.VariationSeed"/>: straight tiles 0/180, the crossroads and the plaza's inside tiles 0/90/180/270 (the crossroads only when square),
    /// T-junctions, edge and corner tiles never.</summary>
    public static void SurfaceItems(IReadOnlyList<AshfallPart> parts, List<AshfallDressItem> items) => Surfaces(parts, parts.Count, items);

    static void Surfaces(IReadOnlyList<AshfallPart> parts, int count, List<AshfallDressItem> items)
    {
        uint seed = AshfallBuilder.Mix(AshfallMap.VariationSeed, 777u);
        int n = 0, slab = 0;
        // The avenues and the perimeter road: straight tiles (16 x 8 raw) in one row of about 24 m tiles (three times wider on an avenue, twice on the perimeter: a few hundred tiles in all, the client counts draw calls) between the junctions, one tile
        // fitted to each junction square. Junction squares: crossroads at the avenues' crossings, a T where an avenue meets the perimeter; the four corners stay bare (no L tile in the kit).
        foreach (var (c, w) in RoadLines)
            foreach (bool northSouth in new[] { true, false })
            {
                const int rows = 1;
                foreach (var (lo, hi) in BlockSpans)
                {
                    float len = hi - lo;
                    int tiles = (int)MathF.Round(len / 24f);
                    float lp = len / tiles, rowW = w / rows;
                    for (int r = 0; r < rows; r++)
                        for (int t = 0; t < tiles; t++)
                        {
                            float along = lo + (t + 0.5f) * lp, across = c - w / 2f + (r + 0.5f) * rowW;
                            items.Add(Road(Straight(seed, n), northSouth ? across : along, northSouth ? along : across, 0f, (northSouth ? 6 : 0) + HalfTurn(seed, n), lp, rowW));
                            n++;
                        }
                }
            }
        // Junction squares (each pair of lines once).
        foreach (var (cx, wx) in RoadLines)
            foreach (var (cy, wy) in RoadLines)
            {
                bool perimX = MathF.Abs(cx) == 232f, perimY = MathF.Abs(cy) == 232f;
                if (perimX && perimY)
                {
                    // A corner (no L tile in the kit): ash slabs, 2 x 2 of the 16 m square, the six variants in turn from a seeded start, a seeded quarter turn (draft 6, the expansion's ground slabs).
                    for (int a = 0; a < 2; a++)
                        for (int b = 0; b < 2; b++)
                        {
                            uint h = AshfallBuilder.Mix(seed, 7001u + (uint)n);
                            items.Add(Slab(AshSlabs[(int)((h % 6u + (uint)(slab++)) % (uint)AshSlabs.Length)], cx - wx / 4f + a * wx / 2f, cy - wy / 4f + b * wy / 2f, 0f, 6 * (int)((h >> 8) % 4u), wx / 2f, wy / 2f));
                            n++;
                        }
                    continue;
                }
                if (!perimX && !perimY) { items.Add(Tile("AC-035", cx, cy, 0f, wx == wy ? 6 * (int)(AshfallBuilder.Mix(seed, 9001u + (uint)(n++)) % 4u) : HalfTurn(seed, n++), wx, wy)); continue; }
                // A T: the through road is the perimeter road, the stem is the avenue and points away from the perimeter (towards the map's middle).
                // Assumption (to check in the client): the T tile's stem points along its local +Y at yaw 0.
                if (perimX) { int step = cx > 0f ? 6 : 18; items.Add(Tile("AC-034", cx, cy, 0f, step, wy, wx)); }
                else { int step = cy > 0f ? 12 : 0; items.Add(Tile("AC-034", cx, cy, 0f, step, wx, wy)); }
            }
        // The paved road (kind Road) and the plaza (kind Paving), on their own tops.
        for (int i = 0; i < count; i++)
        {
            var part = parts[i];
            if (part.Kind is not (AshfallKind.Road or AshfallKind.Paving) || AshfallBox.Of(part) is not { } box) continue;
            if (part.Kind == AshfallKind.Road)
            {
                int tiles = Math.Max(1, (int)MathF.Round(box.Length / 14f));
                float lp = box.Length / tiles;
                var ax = box.AxisX;
                for (int t = 0; t < tiles; t++)
                {
                    var p = new Vector2(box.Cx, box.Cy) + ax * (-box.Length / 2f + (t + 0.5f) * lp);
                    items.Add(Road(Straight(seed + 1, n), p.X, p.Y, box.Z1, box.YawStep + HalfTurn(seed + 1, n), lp, box.Depth));
                    n++;
                }
            }
            else Plaza(items, box);
        }
    }

    /// <summary>
    /// The plaza: 15 m cells (the 8 m tiles scaled 1.9 times, uniform). Draft 6: the expansion's plaza set, AC-070 corner tiles in the four corners, AC-071 edge tiles on the rim turned outward, AC-072 (the plain inner tile)
    /// turned by a hash over the inside (draft 5 used AC-038 / AC-036; B's centre stays flat and walkable). AC-071 is drawn with its lay-flat import fix (<see cref="AshfallKitModel.FileToModel"/>, client).
    /// Orientation (measured from the files' up-facing heights, 2026-11-06): AC-070's chamfered border runs along raw -X and +Z, so its corner is at local (-X, -Y) like AC-038's; AC-071's border is not
    /// visible in its heights (ours: taken on raw +Z, local -Y, like AC-036's rim; to check in the screenshots).
    /// </summary>
    static void Plaza(List<AshfallDressItem> items, AshfallBox box)
    {
        int nx = (int)MathF.Round(box.Length / 15f), ny = (int)MathF.Round(box.Depth / 15f);
        float cw = box.Length / nx, cd = box.Depth / ny;
        // Assumptions (to check in the client): the edge tile's rim is on its local -Y side and the corner tile's corner at local (-X, -Y) at yaw 0.
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                float x = box.Cx - box.Length / 2f + (i + 0.5f) * cw, y = box.Cy - box.Depth / 2f + (j + 0.5f) * cd;
                bool west = i == 0, east = i == nx - 1, south = j == 0, north = j == ny - 1;
                string id; int step;
                if ((west || east) && (south || north)) { id = "AC-070"; step = south ? (west ? 0 : 6) : (east ? 12 : 18); }
                else if (south) { id = "AC-071"; step = 0; }
                else if (east) { id = "AC-071"; step = 6; }
                else if (north) { id = "AC-071"; step = 12; }
                else if (west) { id = "AC-071"; step = 18; }
                else { id = "AC-072"; step = 6 * (int)(AshfallBuilder.Mix(AshfallBuilder.Mix(AshfallMap.VariationSeed, 31u), (uint)(i * 64 + j)) % 4); }
                items.Add(Slab(id, x, y, box.Z1, step, cw, cd));
            }
    }
}
