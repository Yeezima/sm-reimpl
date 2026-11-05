using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

// Surfaces and Decoration (kit dressing, draft 1; all ours). Ground tiles are visual only, laid on the flat surface; their box is [surface - raw height, surface] so the tile's top (raw Y = 0) lies exactly on the surface.
internal static partial class AshfallDressPass
{
    /// <summary>The roads' lines: centre, width. Avenues are 24 m, the perimeter road 16 m.</summary>
    static readonly (float C, float W)[] RoadLines = [(-232f, 16f), (-92f, 24f), (92f, 24f), (232f, 16f)];

    static readonly (float Lo, float Hi)[] BlockSpans = [(-224f, -104f), (-80f, 80f), (104f, 224f)];

    /// <summary>A ground tile: footprint centre, surface height it lies on, yaw step, size along the tile's local X and Y (its raw height is kept).</summary>
    static AshfallDressItem Tile(string id, float cx, float cy, float surface, int yawStep, float lx, float ly)
    {
        float h = AshfallKit.Get(id).RawSize.Y;
        return new AshfallDressItem(id, new Vector3(cx, cy, surface - h), Yaw(yawStep), new Vector3(lx, ly, h), false, AshfallDressRole.Surface, -1);
    }

    static string Straight(uint seed, int i) => AshfallBuilder.Mix(seed, (uint)i) % 8 == 0 ? "AC-040" : "AC-033";

    static void Surfaces(IReadOnlyList<AshfallPart> parts, int count, List<AshfallDressItem> items)
    {
        uint seed = 777;
        int n = 0;
        // The avenues and the perimeter road: straight tiles (16 x 8 raw, laid 1.5 times wider in two rows on an avenue, twice wider in one row on the perimeter) between the junctions, one tile
        // fitted to each junction square. Junction squares: crossroads at the avenues' crossings, a T where an avenue meets the perimeter; the four corners stay bare (no L tile in the kit).
        foreach (var (c, w) in RoadLines)
            foreach (bool northSouth in new[] { true, false })
            {
                int rows = w > 20f ? 2 : 1;
                foreach (var (lo, hi) in BlockSpans)
                {
                    float len = hi - lo;
                    int tiles = (int)MathF.Round(len / 16f);
                    float lp = len / tiles, rowW = w / rows;
                    for (int r = 0; r < rows; r++)
                        for (int t = 0; t < tiles; t++)
                        {
                            float along = lo + (t + 0.5f) * lp, across = c - w / 2f + (r + 0.5f) * rowW;
                            items.Add(Tile(Straight(seed, n++), northSouth ? across : along, northSouth ? along : across, 0f, northSouth ? 6 : 0, lp, rowW));
                        }
                }
            }
        // Junction squares (each pair of lines once).
        foreach (var (cx, wx) in RoadLines)
            foreach (var (cy, wy) in RoadLines)
            {
                bool perimX = MathF.Abs(cx) == 232f, perimY = MathF.Abs(cy) == 232f;
                if (perimX && perimY) continue;   // a corner: bare
                if (!perimX && !perimY) { items.Add(Tile("AC-035", cx, cy, 0f, 0, wx, wy)); continue; }
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
                    items.Add(Tile(Straight(seed + 1, n++), p.X, p.Y, box.Z1, box.YawStep, lp, box.Depth));
                }
            }
            else Plaza(items, box);
        }
    }

    /// <summary>The plaza: 8 m cells. Corner tiles in the four corners, edge tiles on the rim turned outward, the same edge tile turned by a hash over the inside (the kit has no plain plaza tile; AC-037, the raised centre tile, is not used: B's centre stays flat and walkable).</summary>
    static void Plaza(List<AshfallDressItem> items, AshfallBox box)
    {
        int nx = (int)MathF.Round(box.Length / 8f), ny = (int)MathF.Round(box.Depth / 8f);
        float cw = box.Length / nx, cd = box.Depth / ny;
        // Assumptions (to check in the client): the edge tile's rim is on its local -Y side and the corner tile's corner at local (-X, -Y) at yaw 0.
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                float x = box.Cx - box.Length / 2f + (i + 0.5f) * cw, y = box.Cy - box.Depth / 2f + (j + 0.5f) * cd;
                bool west = i == 0, east = i == nx - 1, south = j == 0, north = j == ny - 1;
                string id; int step;
                if ((west || east) && (south || north)) { id = "AC-038"; step = south ? (west ? 0 : 6) : (east ? 12 : 18); }
                else if (south) { id = "AC-036"; step = 0; }
                else if (east) { id = "AC-036"; step = 6; }
                else if (north) { id = "AC-036"; step = 12; }
                else if (west) { id = "AC-036"; step = 18; }
                else { id = "AC-036"; step = 6 * (int)(AshfallBuilder.Mix(31u, (uint)(i * 64 + j)) % 4); }
                items.Add(Tile(id, x, y, box.Z1, step, cw, cd));
            }
    }

    // ---------------------------------------------------------------- decoration

    /// <summary>The walk-over scatter: model, footprint (length, depth) and height (metres, at most <see cref="DecorationMaxHeight"/> after fitting).</summary>
    static readonly (string Id, float L, float D, float H)[] Scatter =
    [
        ("AC-020", 2.6f, 2.4f, 0.30f), ("AC-022", 3.2f, 3.2f, 0.08f), ("AC-024", 1.8f, 1.8f, 0.20f), ("AC-025", 2.4f, 1.7f, 0.30f),
    ];

    const int DecorationTarget = 280;

    static void Decoration(AshfallPlacer p, StartArea[] areas, List<AshfallDressItem> items)
    {
        var cands = new List<(uint Key, float X, float Y)>();
        uint i = 0;
        for (float x = -222f; x <= 222f; x += 3f)
            for (float y = -222f; y <= 222f; y += 3f) cands.Add((AshfallBuilder.Mix(60000, i++), x, y));
        cands.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));   // total order
        var placed = new List<Vector2>();
        foreach (var (key, x, y) in cands)
        {
            if (placed.Count >= DecorationTarget) break;
            uint h = AshfallBuilder.Mix(key, 91u);
            var (id, l, d, ht) = Scatter[(int)(h % (uint)Scatter.Length)];
            int yaw = (int)((h >> 4) % 24);
            var (s, c) = AshfallBuilder.Trig(yaw);
            float ex = MathF.Abs(c) * l / 2f + MathF.Abs(s) * d / 2f, ey = MathF.Abs(s) * l / 2f + MathF.Abs(c) * d / 2f;
            var r = new Vector4(x - ex, y - ey, x + ex, y + ey);
            if (!p.Clear(r, 0.8f) || !AshfallPlacer.Flat(r)) continue;
            if (areas.Any(a => r.X < a.Center.X + a.Size.X / 2f && r.Z > a.Center.X - a.Size.X / 2f && r.Y < a.Center.Y + a.Size.Y / 2f && r.W > a.Center.Y - a.Size.Y / 2f)) continue;
            if (placed.Any(q => Vector2.DistanceSquared(q, new Vector2(x, y)) < 25f)) continue;
            float z = AshfallMap.SurfaceAt(x, y);
            if (AshfallMap.SurfaceAt(r.X, r.Y) != z || AshfallMap.SurfaceAt(r.Z, r.W) != z || AshfallMap.SurfaceAt(r.X, r.W) != z || AshfallMap.SurfaceAt(r.Z, r.Y) != z) continue;
            placed.Add(new Vector2(x, y));
            var m = AshfallKit.Get(id);
            items.Add(new AshfallDressItem(id, new Vector3(x, y, z), Yaw(yaw), new Vector3(l, d, ht), Swap(m), AshfallDressRole.Decoration, -1));
        }
    }
}
