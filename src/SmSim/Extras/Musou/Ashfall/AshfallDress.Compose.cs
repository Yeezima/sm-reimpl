using System.Numerics;
using static SmSim.Extras.Musou.Ashfall.AshfallKind;

namespace SmSim.Extras.Musou.Ashfall;

// Draft 3 (ours, placeholder demonstration; docs/musou-ashfall-graybox.md "Draft 3 (scaled replacements)"): the level is Ashfall v5 untouched; every visible graybox structure is replaced by pack models scaled up to it.
// The pass reads the level's parts as boxes, groups them into structures (touching parts), merges the wall boxes of a run into rectangles and covers each with tiles of the facade models (non-uniform scale, tiles chosen so no
// model is stretched past about 1:2.4), covers stair flights with the stair model, decks and caps with the flat paving model, rubble boxes each with a rubble model, masses (keep, plinths, backdrop) with a shell of facades.
internal static partial class AshfallDressPass
{
    /// <summary>The farthest a Skin item may stick out of its structure's bounds (metres).</summary>
    public const float SkinGrow = 0.5f;

    /// <summary>The widest stretch of a tile against its model's own proportions.</summary>
    const float Stretch = 2.8f;

    /// <summary>Kinds the kit look draws as terrain or boundary (never hidden, never skinned).</summary>
    public static bool IsGround(AshfallKind k) => k is Ground or Road or Paving or Curb or Boundary or Objective;

    sealed class Pt
    {
        public int Index;
        public AshfallKind Kind;
        public AshfallBox? Box;
        public Vector3[] Corners = [];
        public Vector3 Min, Max;
        public int Cluster;
    }

    sealed class Ctx
    {
        public readonly List<Pt> Pts = [];
        public readonly List<AshfallDressItem> Items = [];
        public Vector2[] ClusterCentre = [];
        public Vector3[] ClusterMin = [], ClusterMax = [];
        public List<int>[] ClusterParts = [];
        public readonly HashSet<int> Consumed = [];
        public int Seed;
    }

    /// <summary>Last pass: a hidden part that no item names and whose centre lies in no item's box (grown 0.5 m) would show as a hole once hidden (found: the
    /// triumphal arch's lintel). It gets one fortified segment (AC-006) fitted to its own box, or to its bounds when it is not a box.</summary>
    static void Fallback(Ctx c)
    {
        var named = c.Items.Select(i => i.Part).ToHashSet();
        var boxes = c.Items.Select(ItemBounds).ToList();
        foreach (var pt in c.Pts)
        {
            if (named.Contains(pt.Index)) continue;
            var centre = (pt.Min + pt.Max) / 2f;
            if (boxes.Any(b => centre.X >= b.Min.X - 0.5f && centre.X <= b.Max.X + 0.5f && centre.Y >= b.Min.Y - 0.5f && centre.Y <= b.Max.Y + 0.5f
                && centre.Z >= b.Min.Z - 0.5f && centre.Z <= b.Max.Z + 0.5f)) continue;
            if (pt.Box is { } b0)
                Emit(c, "AC-006", new Vector2(b0.Cx, b0.Cy), b0.Z0, b0.YawStep, b0.Length, b0.Depth, b0.Height, false, pt.Index);
            else
                Emit(c, "AC-006", new Vector2(centre.X, centre.Y), pt.Min.Z, 0, pt.Max.X - pt.Min.X, pt.Max.Y - pt.Min.Y, pt.Max.Z - pt.Min.Z, false, pt.Index);
        }
    }

    /// <summary>An item's axis-aligned bounds (its turned footprint's corners, bottom to top).</summary>
    static (Vector3 Min, Vector3 Max) ItemBounds(AshfallDressItem it)
    {
        float cs = MathF.Abs(MathF.Cos(it.Yaw)), sn = MathF.Abs(MathF.Sin(it.Yaw));
        float hx = (it.Size.X * cs + it.Size.Y * sn) / 2f, hy = (it.Size.X * sn + it.Size.Y * cs) / 2f;
        return (new Vector3(it.Position.X - hx, it.Position.Y - hy, it.Position.Z), new Vector3(it.Position.X + hx, it.Position.Y + hy, it.Position.Z + it.Size.Z));
    }

    public static AshfallDressing Run(AshfallLevel level)
    {
        var c = new Ctx();
        var parts = level.Parts;
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            if (IsGround(p.Kind)) continue;
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            foreach (var v in p.Corners) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            c.Pts.Add(new Pt { Index = i, Kind = p.Kind, Box = AshfallBox.Of(p), Corners = p.Corners, Min = min, Max = max });
        }
        Cluster(c);
        Stairs(c);
        Walls(c);
        Masses(c);
        Flats(c);
        Rubbles(c);
        NonBoxes(c);
        Fallback(c);
        var items = new List<AshfallDressItem>();
        Surfaces(parts, parts.Count, items);
        items.AddRange(c.Items);
        var structures = c.ClusterParts.Select((l, k) => new AshfallStructure(l, c.ClusterMin[k], c.ClusterMax[k])).ToList();
        return new AshfallDressing(items, c.Pts.Select(p => p.Index).ToList(), AshfallMap.DressVersion, structures);
    }

    // ---------------------------------------------------------------- structures

    static void Cluster(Ctx c)
    {
        int n = c.Pts.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        var order = Enumerable.Range(0, n).OrderBy(i => c.Pts[i].Min.X).ThenBy(i => i).ToArray();
        const float eps = 0.02f;
        for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
            {
                var p = c.Pts[order[a]]; var q = c.Pts[order[b]];
                if (q.Min.X > p.Max.X + eps) break;
                if (q.Min.Y > p.Max.Y + eps || p.Min.Y > q.Max.Y + eps || q.Min.Z > p.Max.Z + eps || p.Min.Z > q.Max.Z + eps) continue;
                int ra = Find(order[a]), rb = Find(order[b]);
                if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        var ids = new Dictionary<int, int>();
        var lists = new List<List<int>>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!ids.TryGetValue(r, out int k)) { ids[r] = k = lists.Count; lists.Add([]); }
            c.Pts[i].Cluster = k;
            lists[k].Add(c.Pts[i].Index);
        }
        c.ClusterParts = [.. lists];
        c.ClusterMin = new Vector3[lists.Count]; c.ClusterMax = new Vector3[lists.Count]; c.ClusterCentre = new Vector2[lists.Count];
        for (int k = 0; k < lists.Count; k++) { c.ClusterMin[k] = new(float.MaxValue); c.ClusterMax[k] = new(float.MinValue); }
        foreach (var p in c.Pts) { c.ClusterMin[p.Cluster] = Vector3.Min(c.ClusterMin[p.Cluster], p.Min); c.ClusterMax[p.Cluster] = Vector3.Max(c.ClusterMax[p.Cluster], p.Max); }
        for (int k = 0; k < lists.Count; k++) c.ClusterCentre[k] = new((c.ClusterMin[k].X + c.ClusterMax[k].X) / 2f, (c.ClusterMin[k].Y + c.ClusterMax[k].Y) / 2f);
    }

    // ---------------------------------------------------------------- helpers

    static float Aspect(string id) => AshfallKit.Get(id).RawSize.X / AshfallKit.Get(id).RawSize.Y;

    /// <summary>Splits a length x height panel into n x m tiles whose proportions stay within <see cref="Stretch"/> of the model's natural aspect <paramref name="a"/> (length / height): the fewest tiles, then the closest.</summary>
    static (int N, int M) Split(float length, float height, float a)
    {
        (int N, int M) best = (1, 1); float bestScore = float.MaxValue; float limit = MathF.Log(height < 1.5f ? 3.6f : Stretch);   // low strips (sills, stubs) may stretch longer: they are all length
        for (int m = 1; m <= 8; m++)
            for (int n = 1; n <= 60; n++)
            {
                float asp = length / n / (height / m), dev = MathF.Abs(MathF.Log(asp / a));
                float score = (dev <= limit ? 0f : 1000f + dev * 100f) + n * m + dev * 0.5f;
                if (score < bestScore) { bestScore = score; best = (n, m); }
            }
        return best;
    }

    /// <summary>A tile's model: the panel's own (the one its split was computed for) or another of the pool when that one is not stretched past 3.7 times its proportions either.</summary>
    static string Pick(string own, string[] pool, uint h, float length, float height, bool single)
    {
        if (single) return own;
        string tid = pool[(int)(h % (uint)pool.Length)];
        float stretch = length / height / Aspect(tid);
        return stretch is >= 1f / 3.6f and <= 3.6f ? tid : own;
    }

    static uint H(Ctx c, uint i) => AshfallBuilder.Mix((uint)c.Seed, i);

    static void Emit(Ctx c, string id, Vector2 centre, float z, int yawStep, float l, float d, float h, bool swap, int part) =>
        c.Items.Add(new AshfallDressItem(id, new Vector3(centre.X, centre.Y, z), Yaw(yawStep), new Vector3(l, d, h), swap, AshfallDressRole.Skin, part));

    /// <summary>The direction from (x, y) to the nearest road centre line (a lone wall's front faces the street).</summary>
    static Vector2 Toward(Vector2 p)
    {
        float bx = 1e9f, by = 1e9f;
        foreach (float r in new[] { -232f, -92f, 92f, 232f })
        {
            if (MathF.Abs(r - p.X) < MathF.Abs(bx)) bx = r - p.X;
            if (MathF.Abs(r - p.Y) < MathF.Abs(by)) by = r - p.Y;
        }
        return MathF.Abs(bx) <= MathF.Abs(by) ? new Vector2(bx >= 0f ? 1f : -1f, 0f) : new Vector2(0f, by >= 0f ? 1f : -1f);
    }

    /// <summary>The yaw step that puts the model's front (local -Y) on the outward side of a wall box: away from its structure's middle, or towards the street for a lone wall.</summary>
    static int FrontStep(Ctx c, AshfallBox box, int cluster)
    {
        var ay = box.AxisY;
        float d = Vector2.Dot(new Vector2(box.Cx, box.Cy) - c.ClusterCentre[cluster], ay);
        if (MathF.Abs(d) < 1f) d = Vector2.Dot(Toward(new Vector2(box.Cx, box.Cy)), ay);
        return d > 0f ? (box.YawStep + 12) % 24 : box.YawStep;
    }

    /// <summary>The point at (u along the box's length, v across it) of a box.</summary>
    static Vector2 At(AshfallBox box, float u, float v) => new Vector2(box.Cx, box.Cy) + box.AxisX * u + box.AxisY * v;

    /// <summary>Covers the panel (u0..u1 along the box's length, z0..z1) of a wall-like box with facade tiles: model from the pool by the hash, depth across <paramref name="depth"/> centred at <paramref name="v"/>.</summary>
    static void Panel(Ctx c, AshfallBox box, int cluster, int part, float u0, float u1, float z0, float z1, float v, float depth, string[] pool, uint salt)
    {
        float len = u1 - u0, h = z1 - z0;
        if (len < 0.05f || h < 0.05f) return;
        uint hh = H(c, salt);
        int step = FrontStep(c, box, cluster);
        string id = pool[(int)(hh % (uint)pool.Length)];
        var (n, m) = Split(len, h, Aspect(id));
        float tl = len / n, th = h / m;
        for (int j = 0; j < m; j++)
            for (int i = 0; i < n; i++)
            {
                string tid = Pick(id, pool, hh + (uint)(i + j * 2), tl, th, n * m == 1);
                Emit(c, tid, At(box, u0 + (i + 0.5f) * tl, v), z0 + j * th, step, tl, depth, th, false, part);
            }
    }

    static readonly string[] LowFacade = ["AC-001", "AC-006", "AC-001", "AC-004"];
    static readonly string[] MidFacade = ["AC-004", "AC-001", "AC-005", "AC-006", "AC-004"];
    static readonly string[] TallFacade = ["AC-005", "AC-004", "AC-006", "AC-005", "AC-004"];
    static readonly string[] CoverPool = ["AC-011", "AC-011", "AC-017", "AC-011", "AC-014"];
    static readonly string[] StrongPool = ["AC-006", "AC-001", "AC-004", "AC-006"];

    static readonly string[] StripPool = ["AC-001", "AC-017", "AC-001", "AC-014", "AC-006"];

    static string[] PoolFor(float height, bool stronghold) => height < 1.6f ? StripPool : stronghold ? StrongPool : height <= 2.6f ? LowFacade : height <= 7f ? MidFacade : TallFacade;

    // ---------------------------------------------------------------- walls

    /// <summary>A wall-like box: thin (4.1 m or less), at least half a metre tall, kinds Wall, Cover, Stronghold, Perch (a lintel).</summary>
    static bool WallLike(Pt p) =>
        p.Box is { } b && p.Kind is Wall or Cover or Stronghold or Perch && MathF.Min(b.Length, b.Depth) <= 4.1f && b.Height >= 0.45f
        && !(p.Kind == Perch && b.Height < 3f) && !(p.Kind == Perch && b.Length < 16f) && !(p.Kind == Wall && MathF.Min(b.Length, b.Depth) > 1.3f && b.Height < 0.6f);

    static void Walls(Ctx c)
    {
        var groups = new SortedDictionary<(int Cluster, int Yaw, int Depth, int V, bool Cover), List<Pt>>();
        foreach (var p in c.Pts)
        {
            if (c.Consumed.Contains(p.Index) || !WallLike(p)) continue;
            var b = p.Box!.Value;
            // The long axis is the run: a box as long as deep (a pier) is a mass instead.
            if (b.Length < b.Depth * 1.001f && b.Length > 1.3f) continue;
            int v = (int)MathF.Round(Vector2.Dot(new Vector2(b.Cx, b.Cy), b.AxisY) * 100f);
            var key = (p.Cluster, b.YawStep, (int)MathF.Round(b.Depth * 100f), v, p.Kind == Cover);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(p);
        }
        uint salt = 1;
        foreach (var (key, list) in groups)
        {
            var first = list.OrderBy(p => p.Index).First();
            var fb = first.Box!.Value;
            var ax = fb.AxisX;
            float centreU = Vector2.Dot(new Vector2(fb.Cx, fb.Cy), ax);   // u is measured from the first box's centre
            var bs = list.OrderBy(p => p.Index).Select(p => { var b = p.Box!.Value; float u = Vector2.Dot(new Vector2(b.Cx, b.Cy), ax) - centreU; return (P: p, U0: u - b.Length / 2f, U1: u + b.Length / 2f, b.Z0, b.Z1); }).ToList();
            var zs = bs.SelectMany(x => new[] { x.Z0, x.Z1 }).OrderBy(z => z).ToList();
            var cuts = new List<float>();
            foreach (float z in zs) if (cuts.Count == 0 || z - cuts[^1] > 1e-3f) cuts.Add(z);
            // Layers: in each slab between two cut heights, the boxes that span it, their u ranges joined where they touch; equal ranges in stacked slabs are joined again.
            var rects = new List<(float U0, float U1, float Z0, float Z1, int Part)>();
            for (int k = 0; k + 1 < cuts.Count; k++)
            {
                float za = cuts[k], zb = cuts[k + 1];
                var cover = bs.Where(x => x.Z0 <= za + 1e-3f && x.Z1 >= zb - 1e-3f).OrderBy(x => x.U0).ThenBy(x => x.P.Index).ToList();
                int a = 0;
                while (a < cover.Count)
                {
                    float u0 = cover[a].U0, u1 = cover[a].U1; int part = cover[a].P.Index; int e = a + 1;
                    while (e < cover.Count && cover[e].U0 <= u1 + 0.01f) { u1 = MathF.Max(u1, cover[e].U1); part = Math.Min(part, cover[e].P.Index); e++; }
                    rects.Add((u0, u1, za, zb, part));
                    a = e;
                }
            }
            var merged = new List<(float U0, float U1, float Z0, float Z1, int Part)>();
            foreach (var r in rects.OrderBy(r => MathF.Round(r.U0 * 100f)).ThenBy(r => MathF.Round(r.U1 * 100f)).ThenBy(r => r.Z0))
            {
                if (merged.Count > 0 && MathF.Abs(merged[^1].U0 - r.U0) < 0.011f && MathF.Abs(merged[^1].U1 - r.U1) < 0.011f && MathF.Abs(merged[^1].Z1 - r.Z0) < 1e-3f)
                    merged[^1] = (merged[^1].U0, merged[^1].U1, merged[^1].Z0, r.Z1, Math.Min(merged[^1].Part, r.Part));
                else merged.Add(r);
            }
            var kind = first.Kind;
            float depth0 = fb.Depth;
            foreach (var r in merged)
            {
                float h = r.Z1 - r.Z0, uMid = (r.U0 + r.U1) / 2f;
                var mid = At(fb, uMid, 0f);
                bool stronghold = kind == Stronghold || (mid.Y > 100f && mid.Y < 224f && MathF.Abs(mid.X) <= 74f);
                string[] pool = kind == Cover ? CoverPool : PoolFor(h, stronghold);
                float depth = kind == Cover ? MathF.Max(depth0 + 0.4f, 0.8f) : depth0 + 0.4f;
                bool cap = kind != Cover && depth0 >= 3f && r.U1 - r.U0 >= 3f && kind != Perch;
                float tower = c.ClusterMax[key.Cluster].Z - c.ClusterMin[key.Cluster].Z;
                if (kind != Cover && tower >= 18f && r.Z1 >= 18f && h >= 5f && r.U1 - r.U0 <= 8.1f && H(c, salt + 7) % 3 == 0) pool = ["AC-010"];
                Panel(c, fb, key.Cluster, r.Part, r.U0, r.U1, r.Z0, cap ? r.Z1 - 0.25f : r.Z1, 0f, depth, pool, salt++);
                if (cap) Flat(c, fb with { Length = r.U1 - r.U0, Cx = mid.X, Cy = mid.Y, Z0 = r.Z1 - 0.3f, Z1 = r.Z1 }, r.Part, salt++);
            }
        }
    }

    // ---------------------------------------------------------------- flat slabs and caps

    /// <summary>The flat paving model over a slab (box from Z0 to Z1, a cap of 0.3 m): cells of 16 m at most and never longer than 2.4 times their width.</summary>
    static void Flat(Ctx c, AshfallBox box, int part, uint salt)
    {
        float w = MathF.Max(MathF.Min(box.Length, box.Depth), 0.3f);
        int nx = Math.Max((int)MathF.Ceiling(box.Length / 16f), (int)MathF.Ceiling(box.Length / (Stretch * 1.6f * w)));
        int ny = Math.Max((int)MathF.Ceiling(box.Depth / 16f), (int)MathF.Ceiling(box.Depth / (Stretch * 1.6f * w)));
        float cl = box.Length / nx, cd = box.Depth / ny;
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                string id = H(c, salt * 31u + (uint)(i * 7 + j)) % 5 == 0 ? "AC-024" : "AC-022";
                if (id == "AC-024" && (cl > 3f * 1.6f || cd > 3f * 1.6f)) id = "AC-022";
                Emit(c, id, At(box, -box.Length / 2f + (i + 0.5f) * cl, -box.Depth / 2f + (j + 0.5f) * cd), box.Z0, (box.YawStep + (MathF.Abs(cl - cd) < 0.05f ? 6 * (int)(H(c, salt + (uint)(i * 13 + j)) % 4) : 12 * (int)(H(c, salt + (uint)(i * 13 + j)) % 2))) % 24, cl, cd, box.Height, false, part);
            }
    }

    static void Flats(Ctx c)
    {
        uint salt = 5000;
        foreach (var p in c.Pts)
        {
            if (p.Box is not { } b || p.Kind is not (Floor or Perch or Wall or Stronghold) || b.Height > 0.6f || MathF.Min(b.Length, b.Depth) < 0.6f || c.Consumed.Contains(p.Index) || WallLike(p)) continue;
            Flat(c, b, p.Index, salt++);
        }
    }

    // ---------------------------------------------------------------- masses (keep, plinths, landings, backdrop, perches)

    static bool Inside(Ctx c, Vector3 q, int except)
    {
        foreach (var p in c.Pts)
            if (p.Index != except && q.X > p.Min.X && q.X < p.Max.X && q.Y > p.Min.Y && q.Y < p.Max.Y && q.Z > p.Min.Z && q.Z < p.Max.Z) return true;
        return false;
    }

    static void Masses(Ctx c)
    {
        uint salt = 9000;
        foreach (var p in c.Pts)
        {
            if (p.Box is not { } b || c.Consumed.Contains(p.Index)) continue;
            bool mass = p.Kind switch
            {
                Backdrop or Stronghold => true,
                Stair => MathF.Min(b.Length, b.Depth) >= 2f,
                Floor => b.Height > 0.6f,
                Wall => MathF.Min(b.Length, b.Depth) > 4.1f || (MathF.Min(b.Length, b.Depth) > 1.3f && b.Length < b.Depth * 1.001f && b.Height >= 0.6f),
                Perch => b.Height >= 3f && b.Length < 16f,
                _ => false,
            };
            if (!mass || (p.Kind == Wall && WallLike(p) && b.Length > b.Depth * 1.001f)) continue;
            if (p.Kind == Perch) { Column(c, p, b, salt++); continue; }
            if (p.Kind != Backdrop && p.Kind != Stronghold && MathF.Max(b.Length, b.Depth) <= 8f)
            {
                // A small block (a landing, a stair-tower plinth): one plinth model per 'storey' of it.
                int mm = Math.Max(1, (int)MathF.Round(b.Height / MathF.Max(2f, MathF.Min(b.Length, b.Depth))));
                float hh = b.Height / mm;
                for (int j = 0; j < mm; j++) Emit(c, (H(c, salt++) + (uint)j) % 2 == 0 ? "AC-013" : "AC-028", new(b.Cx, b.Cy), b.Z0 + j * hh, (b.YawStep + (MathF.Abs(b.Length - b.Depth) < 0.05f ? 6 * (int)(H(c, salt) % 4) : 12 * (int)(H(c, salt) % 2))) % 24, b.Length, b.Depth, hh, false, p.Index);
                continue;
            }
            bool backdrop = p.Kind == Backdrop;
            bool cap = !backdrop && MathF.Min(b.Length, b.Depth) >= 3f;
            float zTop = cap ? b.Z1 - 0.25f : b.Z1;
            var pool = backdrop ? new[] { "AC-005", "AC-010", "AC-005", "AC-004" } : p.Kind == Stronghold ? StrongPool : b.Height > 7f ? TallFacade : b.Height > 2.6f ? MidFacade : LowFacade;
            // Four faces: (normal along +-AxisY with length Length, normal along +-AxisX with length Depth).
            for (int f = 0; f < 4; f++)
            {
                bool alongY = f < 2; float sign = f % 2 == 0 ? 1f : -1f;
                var normal = alongY ? b.AxisY * sign : b.AxisX * sign;
                if (backdrop && Vector2.Dot(normal, -new Vector2(b.Cx, b.Cy)) < 0.5f * normal.Length() * new Vector2(b.Cx, b.Cy).Length()) continue;   // only the face towards the map's middle
                float faceLen = alongY ? b.Length : b.Depth, half = (alongY ? b.Depth : b.Length) / 2f;
                float depth = MathF.Min(2f, half * 0.8f);
                int yawStep = alongY ? b.YawStep : (b.YawStep + 6) % 24;   // local X along the face
                // The model's front is local -Y: outward = -localY, so a face whose normal is +localY needs a half turn.
                var faceAxisX = alongY ? b.AxisX : b.AxisY;
                var outward = normal;
                var localY = new Vector2(-faceAxisX.Y, faceAxisX.X);
                int step = Vector2.Dot(localY, outward) > 0f ? (yawStep + 12) % 24 : yawStep;
                string id = pool[(int)(H(c, salt++) % (uint)pool.Length)];
                float h = zTop - b.Z0;
                // Exposed stretches of the face (sampled every 0.5 m at mid height; a stair or another mass against it hides the rest): each is covered by its own tiles.
                int samples = Math.Max(1, (int)MathF.Round(faceLen / 0.5f));
                float cellLen = faceLen / samples;
                int s0 = 0;
                while (s0 < samples)
                {
                    bool Exposed(int k)
                    {
                        float uu = -faceLen / 2f + (k + 0.5f) * cellLen;
                        var q = new Vector2(b.Cx, b.Cy) + faceAxisX * uu + normal * (half + 0.2f);
                        return !Inside(c, new Vector3(q.X, q.Y, b.Z0 + 0.5f * h), p.Index);
                    }
                    if (!Exposed(s0)) { s0++; continue; }
                    int s1 = s0;
                    while (s1 < samples && Exposed(s1)) s1++;
                    float ua = -faceLen / 2f + s0 * cellLen, ub = -faceLen / 2f + s1 * cellLen, runLen = ub - ua;
                    var (n, m) = Split(runLen, h, Aspect(id));
                    float tl = runLen / n, th = h / m;
                    for (int j = 0; j < m; j++)
                        for (int i = 0; i < n; i++)
                        {
                            float u = ua + (i + 0.5f) * tl;
                            var centre = new Vector2(b.Cx, b.Cy) + faceAxisX * u + normal * (half - depth / 2f);
                            string tid = Pick(id, pool, H(c, salt) + (uint)(i + 2 * j), tl, th, n * m == 1);
                            Emit(c, tid, centre, b.Z0 + j * th, step, tl, depth, th, false, p.Index);
                        }
                    s0 = s1;
                }
            }
            if (cap && !Inside(c, new Vector3(b.Cx, b.Cy, b.Z1 + 0.2f), p.Index)) Flat(c, b with { Z0 = b.Z1 - 0.3f }, p.Index, salt++);
        }
    }

    /// <summary>An obelisk or perch pillar (a square perch 3 m tall or more): AC-010 stacked.</summary>
    static void Column(Ctx c, Pt p, AshfallBox b, uint salt)
    {
        float w = MathF.Max(b.Length, b.Depth);
        int m = Math.Max(1, (int)MathF.Round(b.Height / (1.4f * w)));
        float th = b.Height / m;
        for (int j = 0; j < m; j++) Emit(c, j % 2 == 0 ? "AC-010" : "AC-029", new(b.Cx, b.Cy), b.Z0 + j * th, (b.YawStep + 6 * (int)(H(c, salt) % 4)) % 24, b.Length, b.Depth, th, false, p.Index);
    }

    // ---------------------------------------------------------------- rubble

    static void Rubbles(Ctx c)
    {
        uint salt = 12000;
        foreach (var p in c.Pts)
        {
            if (p.Kind != Rubble || p.Box is not { } b) continue;
            uint h = H(c, salt++);
            string id = b.Height >= 1.6f ? (h % 2 == 0 ? "AC-023" : "AC-021") : b.Height <= 0.7f ? (h % 3 == 0 ? "AC-025" : h % 3 == 1 ? "AC-020" : "AC-024") : (h % 5 == 0 ? "AC-018" : h % 2 == 0 ? "AC-019" : "AC-021");
            Emit(c, id, new(b.Cx, b.Cy), b.Z0, (b.YawStep + (MathF.Abs(b.Length - b.Depth) < 0.05f ? 6 * (int)((h >> 8) % 4) : 12 * (int)((h >> 8) % 2))) % 24, b.Length, b.Depth, b.Height, false, p.Index);
        }
    }

    // ---------------------------------------------------------------- flights of stairs

    static void Stairs(Ctx c)
    {
        var steps = c.Pts.Where(p => p.Kind == Stair && p.Box is { } b && MathF.Min(b.Length, b.Depth) < 2f).ToList();
        int n = steps.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
            {
                var p = steps[a]; var q = steps[b];
                const float e = 0.02f;
                if (q.Min.X > p.Max.X + e || p.Min.X > q.Max.X + e || q.Min.Y > p.Max.Y + e || p.Min.Y > q.Max.Y + e) continue;
                // Steps of one flight touch end to end (equal z0, different tops).
                if (MathF.Abs(p.Min.Z - q.Min.Z) > 1e-3f) continue;
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        var groups = Enumerable.Range(0, n).GroupBy(Find).OrderBy(g => g.Min()).ToList();
        uint salt = 20000;
        foreach (var g in groups)
        {
            var list = g.Select(i => steps[i]).OrderBy(p => p.Index).ToList();
            var proto = list.OrderBy(p => MathF.Min(p.Box!.Value.Length, p.Box.Value.Depth)).First().Box!.Value;
            var run = proto.Depth < proto.Length ? proto.AxisY : proto.AxisX;   // across the steps' narrow side
            var wid = new Vector2(-run.Y, run.X);
            float r0 = float.MaxValue, r1 = float.MinValue, w0 = float.MaxValue, w1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            float lowTop = float.MaxValue, highTop = float.MinValue, lowR = float.MaxValue, highR = float.MinValue;
            foreach (var p in list)
            {
                var b = p.Box!.Value;
                float rc = Vector2.Dot(new Vector2(b.Cx, b.Cy), run);
                foreach (var v in Corners(b))
                {
                    float rr = Vector2.Dot(v, run), ww = Vector2.Dot(v, wid);
                    r0 = MathF.Min(r0, rr); r1 = MathF.Max(r1, rr); w0 = MathF.Min(w0, ww); w1 = MathF.Max(w1, ww);
                }
                z0 = MathF.Min(z0, b.Z0); z1 = MathF.Max(z1, b.Z1);
                if (rc < lowR) { lowR = rc; lowTop = b.Z1; }
                if (rc > highR) { highR = rc; highTop = b.Z1; }
            }
            // The flight ascends towards +run when its far end is higher.
            var asc = highTop >= lowTop ? run : -run;
            int step = AngleStep(asc);
            float len = r1 - r0, width = w1 - w0, rise = z1 - z0;
            var centre = run * ((r0 + r1) / 2f) + wid * ((w0 + w1) / 2f);
            // The model's natural width / length is 0.46: wide flights are split across their width.
            int k = Math.Max(1, (int)MathF.Ceiling(width / len / 0.46f / 2.2f));
            for (int i = 0; i < k; i++)
            {
                var cc = centre + wid * (-width / 2f + (i + 0.5f) * width / k);
                Emit(c, "AC-007", cc, z0, (step + StairTurn) % 24, len, width / k, rise, true, list[0].Index);
            }
            salt++;
        }
    }

    /// <summary>Added to a flight's yaw step so the stair model rises towards the flight's top (the model's own climb direction).</summary>
    const int StairTurn = 0;

    static IEnumerable<Vector2> Corners(AshfallBox b)
    {
        foreach (float u in new[] { -0.5f, 0.5f })
            foreach (float v in new[] { -0.5f, 0.5f })
                yield return new Vector2(b.Cx, b.Cy) + b.AxisX * (u * b.Length) + b.AxisY * (v * b.Depth);
    }

    static int AngleStep(Vector2 d)
    {
        int best = 0; float bestDot = -2f;
        for (int k = 0; k < 24; k++)
        {
            var (s, co) = AshfallBuilder.Trig(k);
            float dot = d.X * co + d.Y * s;
            if (dot > bestDot + 1e-6f) { bestDot = dot; best = k; }
        }
        return best;
    }

    // ---------------------------------------------------------------- parts that are no boxes (the barrel, the roofs)

    static void NonBoxes(Ctx c)
    {
        uint salt = 30000;
        foreach (var p in c.Pts)
        {
            if (p.Box is not null) continue;
            var corners = p.Corners;
            var (cx, cy, len, dep, step) = OrientedBounds(corners);
            float z0 = p.Min.Z, z1 = p.Max.Z;
            if (p.Kind == Floor)
            {
                // The gun barrel (a pitched ramp, 20 degrees): four plinth blocks stepping up its length, each as high as the ramp's top at its far end.
                const int k = 4;
                var ax = new Vector2(AshfallBuilder.Trig(step).Cos, AshfallBuilder.Trig(step).Sin);
                for (int i = 0; i < k; i++)
                {
                    float top = 3f + (z1 - 3f) * (i + 1) / k;
                    Emit(c, i % 2 == 0 ? "AC-013" : "AC-028", new Vector2(cx, cy) + ax * (-len / 2f + (i + 0.5f) * len / k), z0, step, len / k, dep, top - z0, false, p.Index);
                }
            }
            else
            {
                bool gable = (p.Max.X - p.Min.X) > (p.Max.Y - p.Min.Y) * 1.2f || (p.Max.Y - p.Min.Y) > (p.Max.X - p.Min.X) * 1.2f;
                Emit(c, gable ? "AC-005" : "AC-010", new(cx, cy), z0, step, len, dep, z1 - z0, false, p.Index);
            }
            salt++;
        }
    }

    /// <summary>The smallest oriented rectangle (yaw in 15 degree steps) around a part's corners: centre, length (longer side), depth and the yaw step of the length.</summary>
    static (float Cx, float Cy, float Length, float Depth, int Step) OrientedBounds(Vector3[] corners)
    {
        float bestArea = float.MaxValue; (float, float, float, float, int) best = default;
        for (int k = 0; k < 12; k++)
        {
            var (s, co) = AshfallBuilder.Trig(k);
            float u0 = float.MaxValue, u1 = float.MinValue, w0 = float.MaxValue, w1 = float.MinValue;
            foreach (var v in corners)
            {
                float u = v.X * co + v.Y * s, w = -v.X * s + v.Y * co;
                u0 = MathF.Min(u0, u); u1 = MathF.Max(u1, u); w0 = MathF.Min(w0, w); w1 = MathF.Max(w1, w);
            }
            float area = (u1 - u0) * (w1 - w0);
            if (area >= bestArea - 1e-3f) continue;
            bestArea = area;
            float cu = (u0 + u1) / 2f, cw = (w0 + w1) / 2f, lu = u1 - u0, lw = w1 - w0;
            float cx = cu * co - cw * s, cy = cu * s + cw * co;
            best = lw > lu ? (cx, cy, lw, lu, (k + 6) % 24) : (cx, cy, lu, lw, k);
        }
        return best;
    }
}
