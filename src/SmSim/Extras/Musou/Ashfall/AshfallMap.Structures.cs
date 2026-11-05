using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

// Pass 2 of the graybox (the owner's playtest notes of 2026-11-05; docs/musou-ashfall-graybox.md): ruins, tall buildings, shooting platforms and low walls placed by rules. Everything is ours.
public static partial class AshfallMap
{
    const float Storey = 6f;

    static uint H(uint seed, uint i) => AshfallBuilder.Mix(seed, i);

    /// <summary>The owner's red-marked zones (2026-11-05; min x, min y, max x, max y): extra ruins, walls, platforms and rubble go here first.</summary>
    internal static readonly Vector4[] PriorityZones =
    [
        new(-236f, -120f, 236f, -60f),   // 1 the south loop (north edge of the south blocks, the south avenue, south edge of the middle row)
        new(-220f, -90f, -160f, 0f),     // 2 the crater field's south-west
        new(-100f, -70f, -60f, -30f),    // 3 the west avenue by the plaza's south-west corner
        new(80f, -70f, 130f, -20f),      // 4 the east avenue south of the arch
        new(190f, -70f, 236f, 0f),       // 5 the east side of the E block
        new(-200f, 80f, -100f, 104f),    // 6a the north avenue south of A's block
        new(60f, 80f, 150f, 104f),       // 6b the north avenue between the stronghold and C's block
    ];

    static bool InZone(float x, float y)
    {
        foreach (var z in PriorityZones) if (x >= z.X && x <= z.Z && y >= z.Y && y <= z.W) return true;
        return false;
    }

    static bool TouchesZone(Vector4 r)
    {
        foreach (var z in PriorityZones) if (r.X < z.Z && r.Z > z.X && r.Y < z.W && r.W > z.Y) return true;
        return false;
    }

    /// <summary>Runs pass 2 (after the hand-placed layout, before the rubble). Returns what it placed.</summary>
    static AshfallFeatures Structures(AshfallBuilder b, ControlPoint[] points)
    {
        var p = new AshfallPlacer(b);
        // Banned places: the roads' 8 m lanes (centrelines, the paved road and the plaza's axes, the stronghold's gate approach), both start areas, the capture zones and a 6 m ring, the spawn markers.
        const float lane = 5.2f;
        foreach (float c in new[] { -92f, 92f, -232f, 232f })
        {
            p.Ban(c - lane, -240f, c + lane, 240f);
            p.Ban(-240f, c - lane, 240f, c + lane);
        }
        p.Ban(-8f, -160f, 8f, 80f);
        p.Ban(-80f, -8f, 80f, 8f);
        p.Ban(-12f, 104f, 12f, 164f);
        p.Ban(-42f, -222f, 42f, -154f);
        p.Ban(-72f, 108f, 72f, 220f);
        foreach (var pt in points) p.BanDisc(pt.Center.X, pt.Center.Y, pt.Radius + 6f);
        foreach (var r in b.Reserved) p.BanDisc(r.X, r.Y, r.Z + 6f);
        // The lanes also keep the rubble out.
        foreach (float c in new[] { -92f, 92f })
        {
            b.Footprints.Add(new Vector4(c - 4f, -240f, c + 4f, 240f));
            b.Footprints.Add(new Vector4(-240f, c - 4f, 240f, c + 4f));
        }
        b.Footprints.Add(new Vector4(-6f, -160f, 6f, 60f));

        StrongholdTowers(p);
        Towers(p);
        Platforms(p);
        Ruins(p);
        Curbs(p);
        Stagger(p);
        return new AshfallFeatures(p.Platforms, p.Towers, p.Ruins, p.WallSegments, p.WallLength, p.StaggerWalls, p.Points);
    }

    static Vector4 Rect(float cx, float cy, float w, float d) => new(cx, cy, cx + w, cy + d);

    // ---------------------------------------------------------------- walls with broken tops

    /// <summary>
    /// A wall run along u (<paramref name="alongU"/>) or v from <paramref name="lo"/> to <paramref name="hi"/>, <paramref name="t0"/>..<paramref name="t1"/> thick: 4 m columns, each with its own broken top
    /// (a storey count and a top height in 0.5 m steps, 1.5..6 m, never in the 0.4..0.9 m band), windows (sill 1 m, opening to 4.5 m) or a doorway (4 m wide, 4.5 m tall).
    /// </summary>
    static void WallRun(AshfallFrame f, bool alongU, float lo, float hi, float t0, float t1, int storeys, uint seed, int doorCol, int fullCol, bool tower, bool low, bool collapseLo, bool collapseHi, int gateCol = -1, int gateStorey = -1, bool noGround = false)
    {
        var edges = new List<float> { lo };
        for (float e = (MathF.Floor(lo / 4f) + 1f) * 4f; e < hi - 0.01f; e += 4f) edges.Add(e);
        edges.Add(hi);
        int n = edges.Count - 1;
        for (int c = 0; c < n; c++)
        {
            float a = edges[c], bb = edges[c + 1];
            bool full4 = bb - a > 3.99f;
            uint h = H(seed, (uint)c * 977u + 13u);
            int ts; float topH;
            if (low) { ts = 0; topH = 1.5f + 0.5f * (int)((h >> 4) % 7); }
            else if (c == fullCol) { ts = storeys - 1; topH = Storey; }
            else
            {
                int roll = (int)(h % 10);
                ts = storeys == 1 ? 0 : roll < 5 ? storeys - 1 : roll < 8 ? storeys - 2 : Math.Max(0, storeys - 3);
                int ti = (int)((h >> 4) % 12);
                topH = ti >= 10 ? Storey : 1.5f + 0.5f * ti;
            }
            if ((c == 0 && collapseLo) || (c == n - 1 && collapseHi))
            {
                ts = 0; topH = 1.5f + 0.5f * (int)(h % 3);
                if ((h >> 9) % 3 == 0) continue;   // gone altogether
            }
            if (c == gateCol) { ts = storeys - 1; topH = Storey; }   // a gateway column (bridge, balcony or stair door) stands full height
            if (low && (h >> 12) % 5 == 0) continue;   // a gap in a broken wall
            float top = Storey * ts + topH;
            var iv = new List<(float A, float B)>();
            void Add(float za, float zb)
            {
                if (zb - za < 0.5f - 1e-3f) return;
                if (iv.Count > 0 && MathF.Abs(iv[^1].B - za) < 1e-3f) iv[^1] = (iv[^1].A, zb); else iv.Add((za, zb));
            }
            for (int s = 0; s <= ts; s++)
            {
                float zs = Storey * s, ze = MathF.Min(zs + Storey, top);
                if (ze - zs < 0.5f) continue;
                if (noGround && s == 0) { Add(zs, ze); continue; }   // a sealed ground storey (pass 3: the twin tower east): no door, no window
                int r = (int)((h >> (8 + s * 3)) & 7);
                bool door = (s == 0 && full4 && (c == doorCol || (!tower && !low && r == 3))) || (c == gateCol && s == gateStorey && full4);
                bool window = !door && !low && (tower ? ((c + s) % 2 == 0 || r < 3) : r < 3 || (r == 3 && s > 0));
                if (door) { if (ze - zs >= 5f) Add(zs + 4.5f, ze); }   // a low column with a doorway is simply gone
                else if (window && ze - zs >= 5f) { Add(zs, zs + 1f); Add(zs + 4.5f, ze); }
                else Add(zs, ze);
            }
            foreach (var (za, zb) in iv)
                if (alongU) f.Box(AshfallKind.Wall, a, t0, za, bb, t1, zb); else f.Box(AshfallKind.Wall, t0, a, za, t1, bb, zb);
        }
    }

    // ---------------------------------------------------------------- ruins

    /// <summary>A ruined shell W x D (local), 1 or 2 storeys, walls by <paramref name="mask"/> (1 front, 2 right, 4 back, 8 left), a collapsed corner, optionally a straight stair to a partial first floor.</summary>
    static Vector3? Shell(AshfallFrame f, int W, int D, int storeys, int mask, bool stair, uint seed)
    {
        int corner = (seed >> 5) % 3 == 0 ? (int)((seed >> 8) & 3) : -1;
        int cols = W / 4;
        if ((mask & 1) != 0) WallRun(f, true, 0f, W, 0f, 1f, storeys, seed + 1, (int)((seed >> 7) % (uint)cols), -1, false, false, corner == 0, corner == 1);
        if ((mask & 2) != 0) WallRun(f, false, 1f, D - 1f, W - 1f, W, storeys, seed + 2, -1, -1, false, false, corner == 1, corner == 2);
        if ((mask & 4) != 0) WallRun(f, true, 0f, W, D - 1f, D, storeys, seed + 3, -1, (int)((seed >> 3) % (uint)cols), false, false, corner == 3, corner == 2);
        if ((mask & 8) != 0) WallRun(f, false, 1f, D - 1f, 0f, 1f, storeys, seed + 4, -1, -1, false, false, corner == 0, corner == 3);
        if (storeys < 2) return null;
        if (stair)
        {
            // A straight stair (4 m wide, 24 steps) along the back wall to a floor over the last third.
            f.Stair(2f, D - 3f, 1, 0, 4f, 0f, Storey);
            f.Box(AshfallKind.Floor, 14f, 1f, 5.5f, W - 1f, D - 1f, 6f);
            var m = f.Map((14f + W - 1f) / 2f, D / 2f);
            return new Vector3(m.X, m.Y, 6f);
        }
        f.Box(AshfallKind.Perch, 1f, D / 2f, 5.5f, W - 1f, D - 1f, 6f);   // a partial floor only a jump reaches
        return null;
    }

    static void Ruins(AshfallPlacer p)
    {
        int blockIndex = 0;
        foreach (var (xl, xh) in Spans)
            foreach (var (yl, yh) in Spans)
            {
                int target = xl == -80f && yl == -80f ? 26 : 24;
                RuinPass(p, (uint)(1000 + blockIndex * 17), xl + 4f, yl + 4f, xh - 4f, yh - 4f, target);
                blockIndex++;
            }
        // The owner's red-marked zones get a second pass of their own.
        blockIndex = 0;
        foreach (var z in PriorityZones)
            foreach (var (xl, xh) in Spans)
                foreach (var (yl, yh) in Spans)
                {
                    float x0 = MathF.Max(xl + 4f, z.X - 8f), y0 = MathF.Max(yl + 4f, z.Y - 8f), x1 = MathF.Min(xh - 4f, z.Z + 8f), y1 = MathF.Min(yh - 4f, z.W + 8f);
                    if (x1 - x0 >= 8f && y1 - y0 >= 8f) RuinPass(p, (uint)(2000 + blockIndex * 31), x0, y0, x1, y1, z.Z - z.X > 300f ? 16 : 8);
                    blockIndex++;
                }
    }

    static void RuinPass(AshfallPlacer p, uint seed, float x0, float y0, float x1, float y1, int target)
    {
        var cands = new List<(uint Key, float X, float Y)>();
        uint i = 0;
        for (float x = MathF.Ceiling(x0 / 4f) * 4f; x <= x1; x += 4f)
            for (float y = MathF.Ceiling(y0 / 4f) * 4f; y <= y1; y += 4f) cands.Add((H(seed, i++), x, y));
        cands.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));   // total order: List.Sort is unstable
        int placed = 0;
        foreach (var (key, cx, cy) in cands)
        {
            if (placed >= target) break;
            uint h = H(key, 77u);
            int roll = (int)(h % 100), rot = (int)((h >> 8) & 3);
            string kind; int W, D, storeys = 1, mask = 15; bool stair = false;
            if (roll < 28) { kind = "shell"; W = 8 + 4 * (int)((h >> 10) % 3); D = 8 + 4 * (int)((h >> 12) % 2); if ((h >> 14) % 3 == 0) mask &= ~(1 << (int)((h >> 16) & 3)); }
            else if (roll < 54) { kind = "shell2"; storeys = 2; W = 12 + 4 * (int)((h >> 10) % 4); D = 12 + 4 * (int)((h >> 12) % 2); stair = W >= 20; if ((h >> 14) % 4 == 0) mask &= ~(1 << (int)((h >> 16) & 3)); }
            else if (roll < 66) { kind = "facade"; storeys = 2; W = 12 + 4 * (int)((h >> 10) % 3); D = 4; }
            else if (roll < 80) { kind = "wall"; W = 8 + 4 * (int)((h >> 10) % 4); D = 1; }
            else { kind = "corner"; storeys = 1 + (int)((h >> 10) & 1); W = 12; D = 12; }
            if (stair) mask |= 4;
            int wx = rot % 2 == 0 ? W : D, wy = rot % 2 == 0 ? D : W;
            var rect = Rect(cx, cy, wx, wy);
            if (rect.Z > x1 || rect.W > y1) continue;
            if (!p.Clear(rect, 8f) || !AshfallPlacer.Flat(rect)) continue;
            var f = AshfallFrame.At(p.B, rot, 0f, 0f, W, D, cx, cy, out var world);
            Vector3? floor = null;
            switch (kind)
            {
                case "shell" or "shell2":
                    floor = Shell(f, W, D, storeys, mask, stair, h);
                    break;
                case "facade":
                    WallRun(f, true, 0f, W, 0f, 1f, 2, h + 1, -1, (int)((h >> 3) % (uint)(W / 4)), false, false, false, false);
                    f.Box(AshfallKind.Wall, 3f, 1f, 0f, 4f, 4f, 6f);
                    f.Box(AshfallKind.Wall, W - 4f, 1f, 0f, W - 3f, 4f, 4.5f);
                    break;
                case "wall":
                    WallRun(f, true, 0f, W, 0f, 1f, 1, h + 1, -1, -1, false, true, false, false);
                    break;
                default:
                    WallRun(f, true, 0f, W, 0f, 1f, storeys, h + 1, -1, 0, false, false, false, true);
                    WallRun(f, false, 1f, W, 0f, 1f, storeys, h + 2, -1, -1, false, false, true, false);
                    break;
            }
            p.Claims.Add(world);
            p.Ruins.Add(new AshfallRuin(kind, world, storeys, floor));
            placed++;
        }
    }

    // ---------------------------------------------------------------- tall buildings

    /// <summary>A hand-listed tall building: footprint corner, local size, quarter turns, storeys, stair core; the doorways and gates (column, storey) of its walls, a jump-only roof slab, the distance kept from the claims (8 m, or less to butt a wall).</summary>
    sealed record TowerDef(string Name, float X, float Y, int W, int D, int Rot, int N, bool Core, int FrontDoor = -1, int LeftDoor = 1, int FrontGateCol = -1, int FrontGateStorey = -1, int RightGateCol = -1, int RightGateStorey = -1, bool Roof = false, float Gap = 8f, bool Sealed = false, int BackGateCol = -1, int BackGateStorey = -1);

    static void Towers(AshfallPlacer p)
    {
        TowerDef[] list =
        [
            new("NW corner tower", -216f, 196f, 16, 16, 0, 5, true),
            new("A tower", -140f, 188f, 16, 16, 3, 4, true),
            new("C tower", 120f, 130f, 16, 16, 1, 3, true),
            new("NE corner tower", 200f, 200f, 16, 16, 2, 5, true),
            // East tower (pass 2b): doorways away from the outside stair (front column 5 = the north end; left column 2), the stair's doorway (front column 1, storey 1), the balcony's doorway (right column 1, storey 3), a roof slab under two pointed roofs.
            new("East tower", 200f, 48f, 24, 16, 1, 4, true, FrontDoor: 5, LeftDoor: 2, FrontGateCol: 1, FrontGateStorey: 1, RightGateCol: 1, RightGateStorey: 3, Roof: true),
            new("SW corner tower", -216f, -176f, 16, 16, 0, 5, true),
            new("South-east of the compound", 48f, -208f, 16, 16, 1, 3, false),
            new("SE corner shell", 200f, -216f, 16, 16, 0, 4, false),
            new("East flank shell", 112f, 44f, 12, 12, 3, 3, false),
            new("Plaza west tower", -76f, 24f, 16, 16, 2, 3, true),
            new("South loop west shell", -148f, -128f, 16, 16, 0, 3, false),
            new("South loop east tower", 132f, -128f, 16, 16, 2, 4, true),
            // Twin towers either side of the west avenue (pass 2b), joined by a bridge at 12 m through a gateway in each facing wall (column 2 = the 4 m at local u 8..12; the east one butts the stronghold's west L wall).
            new("Twin tower west", -124f, 140f, 16, 16, 1, 4, true, FrontGateCol: 2, FrontGateStorey: 2, Roof: true),
            new("Twin tower east", -84f, 144f, 16, 12, 3, 4, true, FrontGateCol: 2, FrontGateStorey: 2, Roof: true, Gap: 0f, Sealed: true, BackGateCol: 1, BackGateStorey: 2),
        ];
        uint seed = 5000;
        foreach (var t in list)
        {
            var f = AshfallFrame.At(p.B, t.Rot, 0f, 0f, t.W, t.D, t.X, t.Y, out var world);
            if (!p.Clear(world, t.Gap) || !AshfallPlacer.Flat(world)) throw new InvalidOperationException($"Ashfall tower '{t.Name}' does not fit at {world}: {p.Why(world, t.Gap)}");
            seed += 101;
            var floors = Tower(f, t, seed);
            float roofTop = 0f;
            switch (t.Name)
            {
                case "East tower": roofTop = EastTowerExtras(p, f, t.W, t.D); break;
                case "Twin tower west": p.Points["bridge west end"] = new Vector3(-111f, 150f, 12f); break;
                case "Twin tower east": Bridge(p); break;
            }
            p.Claims.Add(world);
            p.Towers.Add(new AshfallTower(t.Name, world, t.N, t.Core, floors, roofTop));
        }
    }

    /// <summary>The bridge between the twin towers: a 6 m wide deck at 12 m across the west avenue (the lane below stays open), parapets 1.2 m, through the gateways of both facing walls.</summary>
    static void Bridge(AshfallPlacer p)
    {
        p.B.Box(AshfallKind.Floor, -109f, 147f, 11.5f, -83f, 153f, 12f);
        p.B.Box(AshfallKind.Cover, -108f, 147f, 12f, -84f, 147.5f, 13.2f);
        p.B.Box(AshfallKind.Cover, -108f, 152.5f, 12f, -84f, 153f, 13.2f);
        p.Claims.Add(new Vector4(-108f, 147f, -84f, 153f));
        p.Points["bridge middle"] = new Vector3(-96f, 150f, 12f);
        p.Points["bridge east end"] = new Vector3(-86f, 150f, 12f);
        p.Points["twin east plate"] = new Vector3(-82f, 150f, 12f);
    }

    /// <summary>
    /// The east tower's own features (owner, 2026-11-05: "get those things but vary it"): a railed gallery facing north (towards C) on the third floor, an outside stair that wraps the south-east corner (two flights at right angles with a landing)
    /// to a gateway on the first floor, and two pointed roofs of different heights (a gable 12 m high over the south half, a spire 14 m high over the north half) on a jump-only roof slab. Returns the highest roof point.
    /// </summary>
    static float EastTowerExtras(AshfallPlacer p, AshfallFrame f, int W, int D)
    {
        // Gallery: slab u 23..27 (3 m out from the north wall), v 2..10, at 18 m, parapet 1.2 m on the outer edge and both sides.
        f.Box(AshfallKind.Floor, W - 1f, 2f, 17.5f, W + 3f, 10f, 18f);
        f.Box(AshfallKind.Cover, W + 2.5f, 2f, 18f, W + 3f, 10f, 19.2f);
        f.Box(AshfallKind.Cover, W, 2f, 18f, W + 3f, 2.5f, 19.2f);
        f.Box(AshfallKind.Cover, W, 9.5f, 18f, W + 3f, 10f, 19.2f);
        var g = f.Map(W + 1.5f, 6f);
        p.Points["east balcony"] = new Vector3(g.X, g.Y, 18f);
        p.Claims.Add(f.Rect(W, 2f, W + 3f, 10f));
        // Outside stair at the south-east corner: flight 1 along the south wall (3 m), a landing on the corner, flight 2 along the east wall (3 m more) to a 4 m landing at 6 m in front of the gateway.
        var c = f.Rect(0f, 0f, W, D);   // (200, 48)..(216, 72): the stair's coordinates below are world coordinates of this footprint
        p.B.Stair(210f, 46f, 1, 0, 4f, 0f, 3f);
        p.B.Box(AshfallKind.Stair, 216f, 44f, 0f, 220f, 48f, 3f);
        p.B.Stair(218f, 48f, 0, 1, 4f, 0f, 3f, skip: 12);
        p.B.Box(AshfallKind.Stair, 216f, 54f, 0f, 220f, 58f, 6f);
        p.Claims.Add(new Vector4(209.5f, 43.75f, 220f, 58f));
        p.Points["east stair foot"] = new Vector3(208f, 46f, AshfallMap.BlockTop);
        p.Points["east stair landing"] = new Vector3(218f, 46f, 3f);
        p.Points["east stair top"] = new Vector3(218f, 56f, 6f);
        p.Points["east first floor"] = new Vector3(213f, 54f, 6f);
        // Roof: slab at 24 m (jump-only), a gable over the south half and a spire over the north half; two strips of the slab stay bare beside the spire.
        f.Box(AshfallKind.Perch, 0f, 0f, 23.5f, W, D, 24f);
        var south = f.Rect(0f, 0f, 12f, D);
        p.B.Gable(AshfallKind.Wall, south.X, south.Y, south.Z, south.W, 24f, 12f, true);
        var north = f.Rect(12f, 2f, W, D - 2f);
        p.B.Pyramid(AshfallKind.Wall, north.X, north.Y, north.Z, north.W, 24f, 14f);
        p.Points["east roof strip west"] = new Vector3(c.X + 1f, c.Y + 18f, 24f);
        p.Points["east roof strip east"] = new Vector3(c.Z - 1f, c.Y + 18f, 24f);
        p.Points["east gable slope"] = new Vector3(c.X + 8f, c.Y + 3f, 30f);        // 3 m up the south slope: 24 + 12 x 3/6
        p.Points["east spire slope"] = new Vector3((north.X + north.Z) / 2f, north.Y + 3f, 31f);   // 3 m up a face of the spire: 24 + 14 x 3/6 = 31
        return 38f;
    }

    static List<Vector3> Tower(AshfallFrame f, TowerDef t, uint seed)
    {
        int W = t.W, D = t.D, n = t.N;
        bool core = t.Core;
        // Walls: a doorway in the front wall and one in the left, windows on every storey, broken tops on the last; the front wall's first and the back wall's last column reach the full height.
        WallRun(f, true, 0f, W, 0f, 1f, n, seed + 1, t.FrontDoor >= 0 ? t.FrontDoor : (int)((seed >> 3) % (uint)(W / 4)), 0, true, false, false, false, t.FrontGateCol, t.FrontGateStorey, t.Sealed);
        WallRun(f, false, 1f, D - 1f, W - 1f, W, n, seed + 2, -1, -1, true, false, false, false, t.RightGateCol, t.RightGateStorey, t.Sealed);
        WallRun(f, true, 0f, W, D - 1f, D, n, seed + 3, -1, W / 4 - 1, true, false, false, false, t.BackGateCol, t.BackGateStorey, t.Sealed);
        WallRun(f, false, 1f, D - 1f, 0f, 1f, n, seed + 4, t.LeftDoor, -1, true, false, false, false, -1, -1, t.Sealed);
        var floors = new List<Vector3>();
        if (core)
        {
            // Dogleg core in the back-right corner: flight A (+u) in one lane, a 2 m pad across both lanes, flight B (-u) back in the other lane per storey; the floor plates have a staggered 4 x 4 hole, open over the core.
            float u0 = W - 9f, v0 = D - 9f;
            int holes = D >= 16 ? 3 : 2;   // hole positions along v that leave the plate whole at its far end
            for (int k = 1; k < n; k++)
            {
                float z = Storey * k;
                int hole = 1 + 4 * (int)((k + seed) % (uint)holes);
                f.Box(AshfallKind.Floor, 5f, 1f, z - 0.5f, u0, D - 1f, z);
                f.Box(AshfallKind.Floor, 1f, 1f, z - 0.5f, 5f, hole, z);
                f.Box(AshfallKind.Floor, 1f, hole + 4f, z - 0.5f, 5f, D - 1f, z);
                f.Box(AshfallKind.Floor, u0, 1f, z - 0.5f, W - 1f, v0, z);
                var m = f.Map(u0 - 1f, v0 + 6f);
                floors.Add(new Vector3(m.X, m.Y, z));
            }
            for (int k = 0; k < n - 1; k++)
            {
                float baseZ = Storey * k;
                f.Stair(u0, v0 + 2f, 1, 0, 4f, baseZ, 3f);
                f.Box(AshfallKind.Stair, u0 + 6f, v0, baseZ, u0 + 8f, v0 + 8f, baseZ + 3f);
                f.Stair(u0 + 6f, v0 + 6f, -1, 0, 4f, baseZ + 3f, 3f);
            }
        }
        else
        {
            for (int k = 1; k < n; k++)
            {
                float z = Storey * k;
                f.Box(AshfallKind.Perch, 1f, D / 2f, z - 0.5f, W - 1f, D - 1f, z);
                f.Box(AshfallKind.Perch, 1f, 1f, z - 0.5f, 5f, D / 2f, z);
                var m = f.Map(W / 2f, D - 3f);
                floors.Add(new Vector3(m.X, m.Y, z));
            }
        }
        if (t.Roof && t.Name != "East tower") f.Box(AshfallKind.Perch, 1f, 1f, Storey * n - 0.5f, W - 1f, D - 1f, Storey * n);   // a roof slab only a jump reaches
        return floors;
    }

    // ---------------------------------------------------------------- shooting platforms

    static void Platforms(AshfallPlacer p)
    {
        var cands = new List<(uint Key, float X, float Y)>();
        uint i = 0;
        for (float x = -216f; x <= 216f; x += 4f)
            for (float y = -216f; y <= 216f; y += 4f)
            {
                uint key = H(9000, i++);
                cands.Add(((InZone(x, y) ? 0u : 0x80000000u) | (key >> 1), x, y));
            }
        cands.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));   // total order: List.Sort is unstable
        var centres = new List<Vector2>();
        foreach (var (key, cx, cy) in cands)
        {
            if (p.Platforms.Count >= 26) break;
            uint h = H(key, 4242u);
            int P = 6 + 2 * (int)(h % 4), Q = 6 + 2 * (int)((h >> 3) % 4);
            float top = 1.5f + 0.5f * (int)((h >> 6) % 9), run = top * 2f;
            int rot = (int)((h >> 10) & 3), side2 = (int)((h >> 12) % 4);   // second ramp: 0 none, 1 west, 2 east, 3 north
            bool parapet = (h >> 15) % 2 == 0 && side2 != 3;
            var rects = new List<(float U0, float V0, float U1, float V1)> { (0f, 0f, P, Q), (P / 2f - 2f, -run - 0.25f, P / 2f + 2f, 0f) };
            float um = 0f, vm = -run - 0.25f, uM = P, vM = Q;
            switch (side2)
            {
                case 1: rects.Add((-run - 0.25f, Q / 2f - 2f, 0f, Q / 2f + 2f)); um = -run - 0.25f; break;
                case 2: rects.Add((P, Q / 2f - 2f, P + run + 0.25f, Q / 2f + 2f)); uM = P + run + 0.25f; break;
                case 3: rects.Add((P / 2f - 2f, Q, P / 2f + 2f, Q + run + 0.25f)); vM = Q + run + 0.25f; break;
            }
            var probe = AshfallFrame.At(p.B, rot, um, vm, uM, vM, cx, cy, out var world);
            if (!AshfallPlacer.Inside(world, -222f, -222f, 222f, 222f)) continue;
            var wr = rects.Select(r => probe.Rect(r.U0, r.V0, r.U1, r.V1)).ToList();
            var centre = probe.Map(P / 2f, Q / 2f);
            if (centres.Any(c => Vector2.Distance(c, centre) < 36f)) continue;
            if (!wr.All(r => p.Clear(r, 8f) && AshfallPlacer.Flat(r))) continue;
            var f = probe;
            f.Box(AshfallKind.Wall, 0f, 0f, 0f, P, Q, top - 0.5f);
            f.Box(AshfallKind.Floor, 0f, 0f, top - 0.5f, P, Q, top);
            f.Stair(P / 2f, -run, 0, 1, 4f, 0f, top);
            switch (side2)
            {
                case 1: f.Stair(-run, Q / 2f, 1, 0, 4f, 0f, top); break;
                case 2: f.Stair(P + run, Q / 2f, -1, 0, 4f, 0f, top); break;
                case 3: f.Stair(P / 2f, Q + run, 0, -1, 4f, 0f, top); break;
            }
            if (parapet) f.Box(AshfallKind.Cover, 0f, Q - 0.5f, top, P / 2f + 2f, Q, top + 1f);
            foreach (var r in wr) p.Claims.Add(r);
            centres.Add(centre);
            p.Platforms.Add(new AshfallPlatform(new Vector3(centre.X, centre.Y, top), top, side2 == 0 ? 1 : 2, parapet));
        }
    }

    // ---------------------------------------------------------------- low walls along the roads

    static void Curbs(AshfallPlacer p)
    {
        uint seed = 30000;
        foreach (var (xl, xh) in Spans)
            foreach (var (yl, yh) in Spans)
                for (int side = 0; side < 4; side++)
                {
                    seed += 11;
                    float t = H(seed, 0) % 2 == 0 ? 0.5f : 1f;
                    switch (side)
                    {
                        case 0: Runs(p, true, yl + 1.5f, t, xl + 2f, xh - 2f, BlockTop, seed); break;
                        case 1: Runs(p, true, yh - 1.5f - t, t, xl + 2f, xh - 2f, BlockTop, seed); break;
                        case 2: Runs(p, false, xl + 1.5f, t, yl + 2f, yh - 2f, BlockTop, seed); break;
                        default: Runs(p, false, xh - 1.5f - t, t, yl + 2f, yh - 2f, BlockTop, seed); break;
                    }
                }
        // Along the boundary wall's foot (the perimeter road's outer side).
        // Butted against the boundary (gap 0): a 1 m slot behind them would trap the 0.9 m capsule (review of 2026-11-05).
        Runs(p, true, -240f, 1f, -216f, 216f, 0f, 40001);
        Runs(p, true, 239f, 1f, -216f, 216f, 0f, 40002);
        Runs(p, false, -240f, 1f, -216f, 216f, 0f, 40003);
        Runs(p, false, 239f, 1f, -216f, 216f, 0f, 40004);
    }

    /// <summary>A run of low walls (1.0 or 1.2 m tall, <paramref name="t"/> thick) of 8..16 m with openings of 4..8 m between (so one at least every 24 m); a segment that would touch anything or stand on a slope is left out (a wider opening).</summary>
    static void Runs(AshfallPlacer p, bool alongX, float line, float t, float lo, float hi, float z0, uint seed)
    {
        float cur = lo + 2f * (H(seed, 1) % 3);
        for (uint i = 2; ; i++)
        {
            float L = 8f + 4f * (H(seed, i) % 3), a = cur, b = MathF.Min(a + L, hi);
            if (b - a < 4f) break;
            float h = H(seed, i + 50) % 2 == 0 ? 1f : 1.2f;
            var r = alongX ? new Vector4(a, line, b, line + t) : new Vector4(line, a, line + t, b);
            if (p.Clear(r, 1.2f) && AshfallPlacer.Flat(r))
            {
                p.B.Box(AshfallKind.Cover, r.X, r.Y, z0, r.Z, r.W, z0 + h);
                p.Claims.Add(r);
                p.WallSegments++;
                p.WallLength += b - a;
            }
            cur = b + 4f + 2f * (H(seed, i + 100) % 3);
        }
    }

    /// <summary>Staggered cover across the avenues: short low walls (5.5 m, 0.5 thick) on alternating sides of the 8 m lane, every 24 m, every 12 m inside the owner's red zones.</summary>
    static void Stagger(AshfallPlacer p)
    {
        uint seed = 50000;
        foreach (float c in new[] { -92f, 92f })
            foreach (bool northSouth in new[] { true, false })
                for (int i = 0; i <= 34; i++)
                {
                    float a = -204f + 12f * i;
                    seed++;
                    var probe = northSouth ? new Vector4(c - 12f, a, c + 12f, a + 0.5f) : new Vector4(a, c - 12f, a + 0.5f, c + 12f);
                    if (i % 2 == 1 && !TouchesZone(probe)) continue;
                    if (MathF.Abs(a - 92f) < 20f || MathF.Abs(a + 92f) < 20f) continue;   // the junctions
                    if (northSouth && MathF.Abs(a) < 16f) continue;                       // the monument and the arch
                    float side = (i + i / 2 + (int)(H(seed, 0) & 1)) % 2 == 0 ? 1f : -1f;
                    float d0 = c + side * 5.5f, d1 = c + side * 11f, h = H(seed, 1) % 2 == 0 ? 1f : 1.2f;
                    var r = northSouth ? new Vector4(MathF.Min(d0, d1), a, MathF.Max(d0, d1), a + 0.5f) : new Vector4(a, MathF.Min(d0, d1), a + 0.5f, MathF.Max(d0, d1));
                    if (!p.Clear(r, 1.2f)) continue;
                    p.B.Box(AshfallKind.Cover, r.X, r.Y, 0f, r.Z, r.W, h);
                    p.Claims.Add(r);
                    p.StaggerWalls++;
                }
    }
}
