using System.Globalization;
using System.Numerics;
using static SmSim.Extras.Musou.Ashfall.AshfallKind;

namespace SmSim.Extras.Musou.Ashfall;

// The freestanding kit structures of draft 2 (all ours; docs/musou-ashfall-graybox.md "Draft 2 (freestanding kit)"): nothing is attached to a wall of the graybox. The v5 families the kit replaces
// (ruins without a walkable floor, shooting platforms, curb and staggered cover, rubble, version 1's low walls) are removed; here they come back as kit models at a UNIFORM scale near the
// manifest's proposed size, each with its own simple collision proxy, placed by the placer's rules at the removed families' places.
internal static partial class AshfallDressPass
{
    /// <summary>Whether the model's long raw axis is glTF Z (the item then swaps X and Z so the long side runs along the item's length).</summary>
    public static bool Swap(AshfallKitModel m) => m.RawSize.Z > m.RawSize.X * 1.2f;

    static float First(string text) => float.TryParse(text.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

    /// <summary>The model's scale at the manifest's proposed size: the smaller of proposed height / raw height and proposed length / raw length (one number for all three axes).</summary>
    public static float BaseScale(AshfallKitModel m)
    {
        var r = m.RawSize;
        return MathF.Min(First(m.ProposedHeight) / r.Y, First(m.ProposedFootprint) / MathF.Max(r.X, r.Z));
    }

    static readonly float[] Variations = [0.85f, 0.9f, 0.95f, 1f, 1.05f, 1.1f, 1.15f];

    /// <summary>The seven variations 0.85..1.15 in an order that starts at the hash's pick.</summary>
    static IEnumerable<float> VariationOrder(uint h)
    {
        for (int i = 0; i < Variations.Length; i++) yield return Variations[(int)((h + (uint)i) % (uint)Variations.Length)];
    }

    /// <summary>The model's length (along the item's local X), depth (local Y) and height at the uniform scale <paramref name="s"/>.</summary>
    public static (float L, float D, float H) Dims(AshfallKitModel m, float s)
    {
        var r = m.RawSize;
        bool sw = Swap(m);
        return ((sw ? r.Z : r.X) * s, (sw ? r.X : r.Z) * s, r.Y * s);
    }

    static readonly HashSet<string> Arches = ["AC-002", "AC-006"];

    /// <summary>The state of the kit pass: the placer (claims and banned places), the items, the pieces placed so far (for the neighbour rule) and the perches.</summary>
    sealed class Kit(AshfallBuilder b, AshfallPlacer p)
    {
        public readonly AshfallBuilder B = b;
        public readonly AshfallPlacer P = p;
        public readonly List<AshfallDressItem> Items = [];
        public readonly List<(string Id, Vector4 Rect)> Placed = [];

        /// <summary>Whether no piece of the same model stands within 4 m (footprints) of <paramref name="r"/>: never the same model on two neighbouring pieces.</summary>
        public bool Varied(string id, Vector4 r)
        {
            foreach (var (pid, pr) in Placed) if (pid == id && AshfallPlacer.Gap(r, pr) < 4f) return false;
            return true;
        }

        public static Vector4 Aabb(float cx, float cy, float l, float d, int yaw)
        {
            var (s, c) = AshfallBuilder.Trig(yaw);
            float ex = MathF.Abs(c) * l / 2f + MathF.Abs(s) * d / 2f, ey = MathF.Abs(s) * l / 2f + MathF.Abs(c) * d / 2f;
            return new Vector4(cx - ex, cy - ey, cx + ex, cy + ey);
        }

        public static bool Level(Vector4 r)
        {
            float z = AshfallMap.SurfaceAt((r.X + r.Z) / 2f, (r.Y + r.W) / 2f);
            return AshfallPlacer.Flat(r) && AshfallMap.SurfaceAt(r.X, r.Y) == z && AshfallMap.SurfaceAt(r.Z, r.W) == z && AshfallMap.SurfaceAt(r.X, r.W) == z && AshfallMap.SurfaceAt(r.Z, r.Y) == z;
        }

        /// <summary>The proxy kind of a solid mass: low cover, rubble up to 2.5 m (when <paramref name="rubble"/>), else wall.</summary>
        public static AshfallKind KindFor(float height, bool rubble) => height <= 1.3f ? Cover : rubble && height <= 2.5f ? AshfallKind.Rubble : Wall;

        /// <summary>
        /// Places one piece: the model at the uniform scale <paramref name="s"/>, centred (<paramref name="cx"/>, <paramref name="cy"/>) on the ground, turned by <paramref name="yaw"/> x 15 degrees, with its proxy
        /// (one box, or two piers and a lintel for the arches) as parts. False when the placer's rules or the neighbour rule say no.
        /// </summary>
        public bool Place(string id, float s, float cx, float cy, int yaw, AshfallKind kind, float gap)
        {
            var m = AshfallKit.Get(id);
            var (l, d, h) = Dims(m, s);
            var r = Aabb(cx, cy, l, d, yaw);
            if (r.X < -222f || r.Y < -222f || r.Z > 222f || r.W > 222f) return false;
            if (!P.Clear(r, gap) || !Level(r) || !Varied(id, r)) return false;
            float z = AshfallMap.SurfaceAt(cx, cy);
            int first = B.Parts.Count;
            var (sn, cs) = AshfallBuilder.Trig(yaw);
            var ax = new Vector2(cs, sn);
            void Box(AshfallKind k, float u0, float u1, float z0, float z1)
            {
                var c = new Vector2(cx, cy) + ax * ((u0 + u1) / 2f);
                B.Oriented(k, c.X, c.Y, z + z0, z + z1, u1 - u0, d, yaw);
            }
            if (Arches.Contains(id))
            {
                // Two piers (28 % of the length each) and a lintel from 72 % of the height: the opening (44 % of the length) stays free.
                Box(kind, -l / 2f, -l / 2f + 0.28f * l, 0f, h);
                Box(kind, l / 2f - 0.28f * l, l / 2f, 0f, h);
                Box(kind, -l / 2f + 0.28f * l, l / 2f - 0.28f * l, 0.72f * h, h);
            }
            else Box(kind, -l / 2f, l / 2f, 0f, h);
            Items.Add(new AshfallDressItem(id, new Vector3(cx, cy, z), Yaw(yaw), new Vector3(l, d, h), Swap(m), AshfallDressRole.Prop, first));
            P.Claims.Add(r);
            Placed.Add((id, r));
            return true;
        }

        /// <summary>Tries the model at each variation of its base scale (starting at the hash's pick) until one places.</summary>
        public bool PlaceAny(string id, uint h, float cx, float cy, int yaw, bool rubble, float gap, float? height = null)
        {
            var m = AshfallKit.Get(id);
            foreach (float v in VariationOrder(h))
            {
                float s = height is { } ht ? ht * v / m.RawSize.Y : BaseScale(m) * v;
                var (_, _, hh) = Dims(m, s);
                if (Place(id, s, cx, cy, yaw, KindFor(hh, rubble), gap)) return true;
            }
            return false;
        }
    }

    /// <summary>A model picked from a weighted pool by the hash: <c>pool</c> lists the ids, repeated for weight.</summary>
    static string Pick(string[] pool, uint h, int skip = 0) => pool[(int)((h + (uint)skip) % (uint)pool.Length)];

    /// <summary>The direction from (x, y) to the nearest road centre line: cover and wall fronts face the street (ours).</summary>
    static Vector2 Toward(float x, float y)
    {
        float bx = 1e9f, by = 1e9f;
        foreach (float c in new[] { -232f, -92f, 92f, 232f })
        {
            if (MathF.Abs(c - x) < MathF.Abs(bx)) bx = c - x;
            if (MathF.Abs(c - y) < MathF.Abs(by)) by = c - y;
        }
        return MathF.Abs(bx) <= MathF.Abs(by) ? new Vector2(bx >= 0f ? 1f : -1f, 0f) : new Vector2(0f, by >= 0f ? 1f : -1f);
    }

    /// <summary>Turns a yaw half a turn when the model's front (glTF +Z, local -Y) would face away from <paramref name="front"/>.</summary>
    static int Facing(int yaw, Vector2 front)
    {
        var (s, c) = AshfallBuilder.Trig(yaw);
        return Vector2.Dot(new Vector2(s, -c), front) < 0f ? (yaw + 12) % 24 : yaw;
    }

    static AshfallBox? BoxOf(AshfallPart part) => AshfallBox.Of(part);

    // ---------------------------------------------------------------- perches (replace the shooting platforms)

    /// <summary>
    /// AC-008, the elevated firing perch, at the old platform's place: the model at its uniform scale (about 8 x 2.7 x 4.1 m), a walkable deck at 85 % of its height (rounded to 0.5 m, 1.5..5.5 m) as a Floor top on a Wall body, and a
    /// 4 m wide box-step stair up one long side (v5's platform geometry). Turned in quarter turns. Returns the perches' features (the deck's middle, its height).
    /// </summary>
    static List<AshfallPlatform> Perches(Kit k, List<AshfallRemoved> removed, AshfallFeatures features)
    {
        var result = new List<AshfallPlatform>();
        var model = AshfallKit.Get("AC-008");
        foreach (var g in removed.Where(r => r.Family == "plat"))
        {
            var old = features.Platforms[g.Id];
            uint h = AshfallBuilder.Mix(0xB0A7u, (uint)g.Id);
            bool done = false;
            for (int attempt = 0; attempt < 28 && !done; attempt++)
            {
                uint ha = AshfallBuilder.Mix(h, (uint)attempt);
                float v = Variations[(int)(ha % 7)];
                float s = BaseScale(model) * v;
                var (l, d, hh) = Dims(model, s);
                float deck = MathF.Min(5.5f, MathF.Max(1.5f, MathF.Round(0.85f * hh * 2f) / 2f)), run = deck * 2f;
                int rot = (int)((ha >> 4) & 3);
                // Offsets from the old platform's middle: first none, then 4 and 8 m steps in the hash's direction order.
                int ring = attempt / 4;
                var (dx, dy) = ((int)((ha >> 8) & 3)) switch { 0 => (1, 0), 1 => (-1, 0), 2 => (0, 1), _ => (0, -1) };
                float tx = MathF.Round((old.Top.X + dx * 4f * ring) * 2f) / 2f, ty = MathF.Round((old.Top.Y + dy * 4f * ring) * 2f) / 2f;
                var (ox, oy) = rot switch { 0 => (tx - l / 2f, ty - d / 2f), 1 => (tx + d / 2f, ty - l / 2f), 2 => (tx + l / 2f, ty + d / 2f), _ => (tx - d / 2f, ty + l / 2f) };
                var f = new AshfallFrame(k.B, ox, oy, rot);
                var rects = new[] { f.Rect(0f, 0f, l, d), f.Rect(l / 2f - 2f, -run - 0.25f, l / 2f + 2f, 0f) };
                if (rects.Any(r => r.X < -222f || r.Y < -222f || r.Z > 222f || r.W > 222f || !k.P.Clear(r, 3f) || !Kit.Level(r))) continue;
                if (!k.Varied("AC-008", rects[0])) continue;
                int first = k.B.Parts.Count;
                f.Box(Wall, 0f, 0f, 0f, l, d, deck - 0.5f);
                f.Box(Floor, 0f, 0f, deck - 0.5f, l, d, deck);
                f.Stair(l / 2f, -run, 0, 1, 4f, 0f, deck);
                var (ux, uy) = f.Dir(1, 0);
                int yaw = ux == 1 ? 0 : uy == 1 ? 6 : ux == -1 ? 12 : 18;
                var c = f.Map(l / 2f, d / 2f);
                k.Items.Add(new AshfallDressItem("AC-008", new Vector3(c.X, c.Y, 0f), Yaw(yaw), new Vector3(l, d, hh), false, AshfallDressRole.Prop, first));
                foreach (var r in rects) k.P.Claims.Add(r);
                k.Placed.Add(("AC-008", rects[0]));
                result.Add(new AshfallPlatform(new Vector3(c.X, c.Y, deck), deck, 1, false));
                done = true;
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- ruins (replace the shells, facades, walls and corners without a walkable floor)

    static readonly Dictionary<string, string[]> RuinPools = new()
    {
        ["wall"] = ["AC-001", "AC-001", "AC-001", "AC-004", "AC-006", "AC-002"],
        ["facade"] = ["AC-004", "AC-004", "AC-004", "AC-006", "AC-005", "AC-001"],
        ["shell"] = ["AC-001", "AC-006", "AC-003", "AC-004", "AC-002", "AC-001"],
        ["shell2"] = ["AC-005", "AC-005", "AC-004", "AC-006", "AC-001", "AC-010"],
        ["corner"] = ["AC-003", "AC-003", "AC-001", "AC-004", "AC-005"],
    };

    /// <summary>
    /// A small composition of architecture pieces in each removed ruin's footprint: one piece on a long edge (wall, facade), an L on two adjacent edges (corner), a U on three edges (shells), mirrored and shifted by the hash.
    /// The pieces stand on the footprint's edges with their fronts outward and 2.5 m or more between them (a walk-through gap, also at the corners); arches (AC-002, AC-006) have their opening free.
    /// </summary>
    static void Ruins(Kit k, List<AshfallRemoved> removed, AshfallFeatures features)
    {
        foreach (var g in removed.Where(r => r.Family == "ruin"))
        {
            var ruin = features.Ruins[g.Id];
            var R = ruin.Footprint;
            float w = R.Z - R.X, d = R.W - R.Y;
            uint h = AshfallBuilder.Mix(0xC11Au, (uint)g.Id);
            var pool = RuinPools[ruin.Kind];
            // Edges: 0 south, 1 north, 2 west, 3 east. The set is chosen by kind; the long edges (south, north) go first, then the short ones in the room that is left.
            int mask = ruin.Kind switch
            {
                "wall" or "facade" => 1 << (int)(h & 1),
                "corner" => (int)((h >> 3) & 3) switch { 0 => 0b0101, 1 => 0b0110, 2 => 0b1001, _ => 0b1010 },
                _ => 0b1111 & ~(1 << (int)((h >> 3) & 3)),
            };
            if (ruin.Kind is "wall" or "facade" && w < d) mask = mask == 1 ? 4 : 8;   // a footprint that is longer in y takes its long edge on the west or east
            float southDepth = 0f, northDepth = 0f;
            for (int edge = 0; edge < 4; edge++)
            {
                if ((mask & (1 << edge)) == 0) continue;
                bool alongX = edge < 2;
                float edgeLen = alongX ? w : d, crossLen = alongX ? d : w;
                float lo = 0f, hi = edgeLen;
                if (!alongX) { lo = southDepth > 0f ? southDepth + 2.5f : 0f; hi = d - (northDepth > 0f ? northDepth + 2.5f : 0f); }
                float room = hi - lo;
                for (int attempt = 0; attempt < pool.Length * 2; attempt++)
                {
                    uint ha = AshfallBuilder.Mix(h, (uint)(edge * 31 + attempt));
                    string id = Pick(pool, ha, attempt);
                    var m = AshfallKit.Get(id);
                    bool placed = false;
                    foreach (float v in VariationOrder(ha >> 5))
                    {
                        float s = BaseScale(m) * v;
                        var (l, dd, _) = Dims(m, s);
                        if (l > room || dd > MathF.Max(crossLen * 0.55f, 2.5f)) continue;   // a 1 m "wall" footprint still takes a piece up to 2.5 m deep (the placer keeps it clear of everything)
                        float slack = room - l;
                        float along = lo + l / 2f + slack * (((ha >> 9) & 7) / 7f);   // shifted along the edge by the hash
                        float across = dd / 2f;
                        float cx, cy; int yaw;
                        switch (edge)
                        {
                            case 0: cx = R.X + along; cy = R.Y + across; yaw = 0; break;
                            case 1: cx = R.X + along; cy = R.W - across; yaw = 12; break;
                            case 2: cx = R.X + across; cy = R.Y + along; yaw = 18; break;
                            default: cx = R.Z - across; cy = R.Y + along; yaw = 6; break;
                        }
                        var (_, _, hh) = Dims(m, s);
                        if (!k.Place(id, s, cx, cy, yaw, Kit.KindFor(hh, false), 2.5f)) continue;
                        if (edge == 0) southDepth = dd; else if (edge == 1) northDepth = dd;
                        placed = true;
                        break;
                    }
                    if (placed) break;
                }
            }
        }
    }

    // ---------------------------------------------------------------- cover (replaces the curb walls, the staggered avenue walls and version 1's low walls)

    static readonly string[] CoverPool = ["AC-011", "AC-014", "AC-017"];

    /// <summary>
    /// Runs of kit cover along the removed low walls' lines: AC-011, AC-014, AC-017 (AC-018 rarely, AC-012 only in the crater field), each at a uniform scale that makes it 1.0..1.2 m tall, quarter turns along the road,
    /// 1.5 m between pieces; a piece that does not fit is skipped, so a run has openings. Half of the curb segments are used (the kit pieces are bulkier than the v5 boxes).
    /// </summary>
    static void Covers(Kit k, List<AshfallRemoved> removed)
    {
        int n = 0;
        foreach (var g in removed.Where(r => r.Family is "curb" or "stagger" or "v1cover"))
        {
            n++;
            if (g.Family == "curb" && AshfallBuilder.Mix(0xC0B3u, (uint)n) % 2 == 0) continue;
            if (BoxOf(g.Parts[0]) is not { } box) continue;
            uint h = AshfallBuilder.Mix(0xC0DEu, (uint)n);
            var ax = box.AxisX;
            var toward = Toward(box.Cx, box.Cy);
            bool crater = box.Cx < -104f && MathF.Abs(box.Cy) < 80f;
            float cursor = -box.Length / 2f + (h & 3) * 0.5f;
            int pieces = 0;
            for (uint i = 0; cursor < box.Length / 2f - 1f && pieces < 3 && i < 8; i++)
            {
                uint hp = AshfallBuilder.Mix(h, i + 1);
                string id = hp % 16 == 0 ? "AC-018" : crater && hp % 4 == 0 ? "AC-012" : Pick(CoverPool, hp);   // AC-018 rarely (it renders near white), AC-012 only by craters
                var m = AshfallKit.Get(id);
                float height = 1f + 0.1f * (int)((hp >> 6) % 3);   // 1.0, 1.1, 1.2 m
                float s = height / m.RawSize.Y;
                var (l, _, _) = Dims(m, s);
                if (cursor + l > box.Length / 2f + 1f) { cursor += 1.5f; continue; }
                var c = new Vector2(box.Cx, box.Cy) + ax * (cursor + l / 2f);
                int yaw = Facing(box.YawStep, toward);
                if (k.Place(id, s, c.X, c.Y, yaw, Cover, 1.2f)) { cursor += l + 1.5f; pieces++; }
                else cursor += 1.5f;
            }
        }
    }

    // ---------------------------------------------------------------- rubble (replaces the clusters)

    static readonly string[] PilePool = ["AC-019", "AC-021", "AC-023", "AC-023", "AC-021", "AC-019"];

    /// <summary>Kit rubble: per removed cluster one or two blocking piles (AC-019, AC-021, AC-023 at their uniform scale, 15 degree yaws) and walk-over debris around them (Decoration); version 1's collapsed pieces become single piles.</summary>
    static void RubblePiles(Kit k, List<AshfallRemoved> removed)
    {
        int n = 0;
        foreach (var g in removed.Where(r => r.Family is "rubble" or "v1rubble"))
        {
            n++;
            uint h = AshfallBuilder.Mix(0xB0B1u, (uint)n);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var part in g.Parts)
                foreach (var c in part.Corners) { x0 = MathF.Min(x0, c.X); y0 = MathF.Min(y0, c.Y); x1 = MathF.Max(x1, c.X); y1 = MathF.Max(y1, c.Y); }
            float mx = (x0 + x1) / 2f, my = (y0 + y1) / 2f;
            int piles = g.Family == "v1rubble" ? 1 : g.Parts.Count >= 7 ? 2 : 1;
            for (int i = 0; i < piles; i++)
            {
                bool done = false;
                for (int attempt = 0; attempt < 24 && !done; attempt++)
                {
                    uint ha = AshfallBuilder.Mix(h, (uint)(i * 97 + attempt));
                    string id = Pick(PilePool, ha, attempt);
                    float r = attempt == 0 ? 0f : 3f + 2f * (attempt / 4);
                    var (dx, dy) = ((int)((ha >> 8) & 3)) switch { 0 => (1f, 0f), 1 => (-1f, 0f), 2 => (0f, 1f), _ => (0f, -1f) };
                    int yaw = (int)((ha >> 12) % 24);
                    done = k.PlaceAny(id, ha, mx + dx * r, my + dy * r, yaw, true, 2f);
                }
            }
            // Walk-over debris around the cluster, as Decoration (never above 0.35 m).
            if (g.Family == "rubble")
                for (int i = 0; i < 3; i++)
                {
                    uint ha = AshfallBuilder.Mix(h, 500u + (uint)i);
                    float ang = (ha % 24) * (MathF.PI / 12f);
                    AddScatter(k, ha, mx + MathF.Cos(ang) * (6f + (ha >> 8) % 5), my + MathF.Sin(ang) * (6f + (ha >> 8) % 5), null);
                }
        }
    }

    // ---------------------------------------------------------------- landmarks

    sealed record Landmark(string Id, int Count, bool Rubble = false, bool Arch = false);

    static readonly Landmark[] LandmarkList =
    [
        new("AC-027", 4), new("AC-028", 3), new("AC-029", 4), new("AC-030", 3), new("AC-031", 8), new("AC-032", 6, Rubble: true), new("AC-013", 6),
        new("AC-002", 4, Arch: true), new("AC-010", 2), new("AC-007", 2),
    ];

    /// <summary>
    /// Landmarks and cover mixed in between the families above (beyond version 5, to read as the approved bombarded Gothic district): the owner's red zones first, each model by its own hash order, round robin, 30 m
    /// between pieces of one model, standing free (quarter turns for the arches, 15 degree yaws for the rest).
    /// </summary>
    static void Landmarks(Kit k)
    {
        var left = LandmarkList.Select(l => l.Count).ToArray();
        var cands = new List<(uint Key, float X, float Y)>[LandmarkList.Length];
        var next = new int[LandmarkList.Length];
        var own = new List<Vector2>[LandmarkList.Length];
        for (int d = 0; d < LandmarkList.Length; d++)
        {
            var list = new List<(uint Key, float X, float Y)>();
            uint i = 0;
            for (float x = -218f; x <= 218f; x += 4f)
                for (float y = -218f; y <= 218f; y += 4f)
                {
                    uint key = AshfallBuilder.Mix(70000u + (uint)d, i++);
                    list.Add(((AshfallMap.InZone(x, y) ? 0u : 0x80000000u) | (key >> 1), x, y));
                }
            list.Sort((a, c) => a.Key != c.Key ? a.Key.CompareTo(c.Key) : a.X != c.X ? a.X.CompareTo(c.X) : a.Y.CompareTo(c.Y));   // total order: List.Sort is unstable
            cands[d] = list;
            own[d] = [];
        }
        bool progress = true;
        while (progress)
        {
            progress = false;
            for (int d = 0; d < LandmarkList.Length; d++)
            {
                if (left[d] == 0) continue;
                var def = LandmarkList[d];
                while (next[d] < cands[d].Count)
                {
                    var (key, cx, cy) = cands[d][next[d]++];
                    var here = new Vector2(cx, cy);
                    if (own[d].Any(q => Vector2.DistanceSquared(q, here) < 900f)) continue;
                    uint h = AshfallBuilder.Mix(key, 4711u);
                    int yaw = def.Arch ? 6 * (int)(h % 4) : (int)(h % 24);
                    if (!k.PlaceAny(def.Id, h, cx, cy, yaw, def.Rubble, 3f)) continue;
                    own[d].Add(here);
                    left[d]--;
                    progress = true;
                    break;
                }
            }
        }
    }

    // ---------------------------------------------------------------- walk-over scatter (Decoration)

    static readonly string[] ScatterPool = ["AC-020", "AC-022", "AC-024", "AC-025", "AC-020", "AC-024", "AC-025"];

    const int ScatterTarget = 200;

    /// <summary>One walk-over item at (x, y): the model's uniform scale (the manifest's, 0.85..1.15, capped so the height stays under 0.35 m) when the ground there is open. Returns whether it was placed.</summary>
    static bool AddScatter(Kit k, uint h, float x, float y, string? only)
    {
        string id = only ?? Pick(ScatterPool, h >> 3);
        var m = AshfallKit.Get(id);
        float s = MathF.Min(BaseScale(m) * Variations[(int)(h % 7)], 0.33f / m.RawSize.Y);
        var (l, d, ht) = Dims(m, s);
        int yaw = (int)((h >> 4) % 24);
        var r = Kit.Aabb(x, y, l, d, yaw);
        if (r.X < -222f || r.Y < -222f || r.Z > 222f || r.W > 222f || !k.P.Clear(r, 0.8f) || !Kit.Level(r)) return false;
        k.Items.Add(new AshfallDressItem(id, new Vector3(x, y, AshfallMap.SurfaceAt(x, y)), Yaw(yaw), new Vector3(l, d, ht), Swap(m), AshfallDressRole.Decoration, -1));
        return true;
    }

    static void Scatter(Kit k, StartArea[] areas)
    {
        var cands = new List<(uint Key, float X, float Y)>();
        uint i = 0;
        for (float x = -222f; x <= 222f; x += 3f)
            for (float y = -222f; y <= 222f; y += 3f) cands.Add((AshfallBuilder.Mix(60000, i++), x, y));
        cands.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));   // total order
        var placed = new List<Vector2>();
        foreach (var (key, x, y) in cands)
        {
            if (placed.Count >= ScatterTarget) break;
            if (areas.Any(a => MathF.Abs(x - a.Center.X) < a.Size.X / 2f + 3f && MathF.Abs(y - a.Center.Y) < a.Size.Y / 2f + 3f)) continue;
            if (placed.Any(q => Vector2.DistanceSquared(q, new Vector2(x, y)) < 25f)) continue;
            if (AddScatter(k, AshfallBuilder.Mix(key, 91u), x, y, null)) placed.Add(new Vector2(x, y));
        }
    }
}
