using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Draft 12, the city layout (owner, 2026-11-09: "redo the layout of the ashfall draft 10 map. no stairs inaccessible or blocked by walls or other objects. lined up buildings and walls. a logical layout that allows the flow of
/// the battle, with roads placed properly to facilitate movement of armies"; docs/musou-ashfall-graybox.md "Draft 12 city layout"). Draft 10's street grid redone as a plan: its 24 m avenues at x and y = +-92 kept and cleared,
/// the central cross widened to an avenue, edge avenues at x = +-160 and a north loop added, so every point has three or four formation-capable approaches; every block is filled with rows of buildings standing on one
/// frontage line along its roads (alleys between them, walls in the wider gaps, a yard behind). Every place and size ours (placeholder demonstration).
/// </summary>
public static class AshfallCity
{
    /// <summary>The city layout's version (salts its level hash; raised with any change of its geometry, points, areas or spawns).</summary>
    public const uint Version = 1;

    /// <summary>The version term the city level's hash is salted with ("C" in the high byte, so it never equals a draft's or the field's version).</summary>
    public const uint HashVersion = 0x43000000u | Version;

    /// <summary>The city's parameters (the field's set; the values that differ are the city's: dense blocks, more platforms, its own layout seed).</summary>
    public static readonly AshfallFieldParams Defaults = new()
    {
        LayoutSeed = 12,
        CorridorWidth = 22f, RouteBuffer = 2.5f,   // a street: building fronts 28 m apart (an entry ramp of 2 m fits in front of a door), the roadway rule keeps every item 0.5 m off the 22 m corridor (the army's 20 m and a metre a side)
        DensityCathedral = 0.34f, DensityManufactorum = 0.34f, DensityHab = 0.38f, DensityNoMansLand = 0.24f,
        PlatformTarget = 40,
        ClusterGap = 6f,
    };

    static readonly Lazy<AshfallFieldPlan> DefaultPlan = new(() => Plan(Defaults), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The plan of the default parameters (built once; the level the map <c>tfw_war</c> loads).</summary>
    public static AshfallFieldPlan Default => DefaultPlan.Value;

    // The fixed places: the Imperial base (draft 10's compound and start area) and the stronghold (draft 10's fortress, unchanged).
    public static readonly Vector2 BaseCentre = new(0f, -188f), BaseGate = new(0f, -160f), StrongholdCentre = new(0f, 138f), StrongholdGate = new(0f, 112f);
    /// <summary>The points: B at the central crossing (draft 10's plaza), A and C where the north avenue meets the edge avenues (draft 10 had them in the north blocks, 70 m further north).</summary>
    public static readonly Vector2 PointA = new(-160f, 92f), PointB = new(0f, 0f), PointC = new(160f, 92f);

    /// <summary>The avenues' centre lines (x of the north-south avenues, y of the east-west ones).</summary>
    public const float Edge = 160f, Inner = 92f, South = -92f, North = 92f, Loop = 200f;

    /// <summary>One block of the city: its outer rectangle (between the roads' centre lines or the boundary) and its district.</summary>
    public sealed record Block(string Name, Vector4 Outer, string District, bool EdgeRow);

    /// <summary>The plan for <paramref name="p"/> (deterministic: integer hashes, exact arithmetic, total-order sorts).</summary>
    public static AshfallFieldPlan Plan(AshfallFieldParams p)
    {
        var notes = new List<string>();
        const float top = AshfallMap.BlockTop;
        ControlPoint[] points = [new("A", new(PointA.X, PointA.Y, top), 14f, 8f), new("B", new(PointB.X, PointB.Y, top), 16f, 8f), new("C", new(PointC.X, PointC.Y, top), 14f, 8f)];
        StartArea[] areas =
        [
            new(Team.SpaceMarines, new(BaseCentre.X, BaseCentre.Y, top), new(64f, 48f, 8f), 0f),
            new(Team.Chaos, new(StrongholdCentre.X, StrongholdCentre.Y, top), new(40f, 36f, 8f), 0f),
        ];
        float w = p.CorridorWidth;
        AshfallCorridor[] corridors =
        [
            new("central avenue south", new Vector2(0f, -140f), PointB, w),   // from north of the compound's gate wall (its 16 m gate is the base's authored exit)
            new("central avenue north", PointB, new Vector2(0f, 94f), w),      // to the stronghold's apron (its gate is the stronghold's authored exception)
            new("south avenue", new Vector2(-Edge, South), new Vector2(Edge, South), w),
            new("middle avenue west", new Vector2(-Edge, 0f), PointB, w),
            new("middle avenue east", PointB, new Vector2(Edge, 0f), w),
            new("north avenue", PointA, PointC, w),
            new("west avenue", new Vector2(-Edge, South), new Vector2(-Edge, Loop), w),
            new("east avenue", new Vector2(Edge, South), new Vector2(Edge, Loop), w),
            new("inner avenue west", new Vector2(-Inner, South), new Vector2(-Inner, Loop), w),
            new("inner avenue east", new Vector2(Inner, South), new Vector2(Inner, Loop), w),
            new("north loop west", new Vector2(-Edge, Loop), new Vector2(-Inner, Loop), w),
            new("north loop east", new Vector2(Inner, Loop), new Vector2(Edge, Loop), w),
        ];
        var a0 = p.AssemblySize;
        (string, Vector4)[] assembly =
        [
            ("Imperial assembly", new(-a0.X / 2f, -150f, a0.X / 2f, -150f + a0.Y)),   // 10 m north of the compound's gate wall, up to the south avenue
            ("Ork assembly", new(-a0.X / 2f, 92f - a0.Y * 0.8f, a0.X / 2f, 92f)),        // on the north avenue in front of the stronghold's gate
        ];
        // The fortresses' aprons (draft 11's: draft 10's roadway strips round the stronghold and the Imperial compound) where no avenue covers them: the north avenue and the inner avenues stand on the south, west and east
        // aprons (the fortress itself is taken from draft 10 as it stands, so the aprons no longer settle it).
        (string, Vector4)[] aprons =
        [
            ("stronghold apron north", new(-110f, 224f, 110f, 240f)),
            ("stronghold apron west", new(-104f, 211f, -80f, 240f)),
            ("stronghold apron east", new(80f, 211f, 104f, 240f)),
            ("stronghold gate lane", new(-6f, 103f, 6f, 110f)),
            ("compound road", new(-6f, -160f, 6f, -104f)),
            ("compound apron south", new(-60f, -240f, 60f, -224f)),
        ];
        Vector2 base0 = BaseCentre, sh = StrongholdCentre, sj = new(0f, South), nj = new(0f, North);
        Vector2 swj = new(-Edge, South), sej = new(Edge, South), wmj = new(-Edge, 0f), emj = new(Edge, 0f), wij = new(-Inner, South), eij = new(Inner, South), wim = new(-Inner, 0f), eim = new(Inner, 0f), win = new(-Inner, North), ein = new(Inner, North);
        Vector2 wl = new(-Edge, Loop), wil = new(-Inner, Loop), el = new(Edge, Loop), eil = new(Inner, Loop);
        AshfallRoute[] routes =
        [
            new("base to A", "base", "A", [base0, BaseGate, sj, swj, wmj, PointA]),
            new("base to B", "base", "B", [base0, BaseGate, PointB]),
            new("base to C", "base", "C", [base0, BaseGate, sj, sej, emj, PointC]),
            new("stronghold to A", "stronghold", "A", [sh, StrongholdGate, nj, PointA]),
            new("stronghold to B", "stronghold", "B", [sh, StrongholdGate, nj, PointB]),
            new("stronghold to C", "stronghold", "C", [sh, StrongholdGate, nj, PointC]),
            new("A to B", "A", "B", [PointA, wmj, PointB]),
            new("B to C", "B", "C", [PointB, emj, PointC]),
            new("A to C", "A", "C", [PointA, nj, PointC]),
            new("base to A (inner avenue)", "base", "A", [base0, BaseGate, sj, wij, wim, win, PointA]),
            new("base to C (inner avenue)", "base", "C", [base0, BaseGate, sj, eij, eim, ein, PointC]),
            new("A to B (inner avenue)", "A", "B", [PointA, win, wim, PointB]),
            new("B to C (inner avenue)", "B", "C", [PointB, eim, ein, PointC]),
            new("A north loop", "A", "A", [PointA, wl, wil, win, PointA]),
            new("C north loop", "C", "C", [PointC, el, eil, ein, PointC]),
            new("stronghold to B (central avenue)", "stronghold", "B", [sh, StrongholdGate, PointB]),
        ];

        var prot = new List<(string, Vector4)>
        {
            ("Imperial base", new(-44f, -224f, 44f, -156f)),
            ("stronghold", new(-84f, 96f, 84f, 226f)),
            ("stronghold twin towers", new(-142f, 126f, -70f, 166f)),
        };
        prot.AddRange(assembly);
        prot.AddRange(aprons);

        // The blocks (outer rectangles between the roads' centre lines, the side streets' lines or the boundary; ours). Edge strips split by side streets in line with the avenues' blocks.
        const float H = AshfallMap.Half;
        var blocks = new List<Block>();
        void Col(string name, float x0, float x1, float[] ys, Func<float, string> district, bool edge)
        {
            for (int i = 0; i + 1 < ys.Length; i++) blocks.Add(new($"{name} {i}", new(x0, ys[i], x1, ys[i + 1]), district((ys[i] + ys[i + 1]) / 2f), edge));
        }
        Col("west edge", -H, -Edge, [-H, -150f, -46f, 46f, 146f, H], y => y > 46f ? "cathedral quarter" : "hab-block ruins", true);
        Col("east edge", Edge, H, [-H, -150f, -46f, 46f, 146f, H], y => y > 46f ? "manufactorum district" : "hab-block ruins", true);
        Col("west", -Edge, -Inner, [-H, -166f, South, 0f, North, Loop, H], y => y > 0f ? "cathedral quarter" : "hab-block ruins", false);
        Col("east", Inner, Edge, [-H, -166f, South, 0f, North, Loop, H], y => y > 0f ? "manufactorum district" : "hab-block ruins", false);
        Col("centre west", -Inner, 0f, [-H, South, 0f, North], y => y < South ? "hab-block ruins" : "no-man's-land", false);
        Col("centre east", 0f, Inner, [-H, South, 0f, North], y => y < South ? "hab-block ruins" : "no-man's-land", false);

        // The inset of a block's side: a road's half width plus the keep-out where an avenue runs along it, the boundary band at the edge, half a side street elsewhere.
        float Inset(bool vertical, float at, float s0, float s1)
        {
            if (MathF.Abs(MathF.Abs(at) - H) < 1e-3f) return H - 226f + 0.5f;
            foreach (var k in corridors)
            {
                bool kv = MathF.Abs(k.A.X - k.B.X) < 1e-3f, kh = MathF.Abs(k.A.Y - k.B.Y) < 1e-3f;
                if (vertical && kv && MathF.Abs(k.A.X - at) < 1e-3f && MathF.Min(k.A.Y, k.B.Y) - k.Width / 2f < s1 - 1f && MathF.Max(k.A.Y, k.B.Y) + k.Width / 2f > s0 + 1f) return k.Width / 2f + p.RouteBuffer + 0.5f;
                if (!vertical && kh && MathF.Abs(k.A.Y - at) < 1e-3f && MathF.Min(k.A.X, k.B.X) - k.Width / 2f < s1 - 1f && MathF.Max(k.A.X, k.B.X) + k.Width / 2f > s0 + 1f) return k.Width / 2f + p.RouteBuffer + 0.5f;
            }
            return SideStreet / 2f;
        }

        var districts = new[] { "cathedral quarter", "manufactorum district", "no-man's-land", "hab-block ruins" }
            .Select(d => new AshfallDistrict(d, [.. blocks.Where(b => b.District == d).Select(b => Buildable(b, Inset))], d switch { "cathedral quarter" => p.DensityCathedral, "manufactorum district" => p.DensityManufactorum, "hab-block ruins" => p.DensityHab, _ => p.DensityNoMansLand }, 0f))
            .ToList();

        var structures = new List<AshfallFieldStructure>();
        var placed = new List<Vector4>();

        var why = new Dictionary<string, int>();
        bool Free(Vector4 r, float gap)
        {
            string? w = FreeWhy(r, gap);
            if (w is not null) why[w] = why.GetValueOrDefault(w) + 1;
            return w is null;
        }
        string? FreeWhy(Vector4 r, float gap)
        {
            if (r.X < -226f || r.Y < -226f || r.Z > 226f || r.W > 226f) return "boundary band";
            foreach (var (zn, z) in prot) if (r.X < z.Z + p.RouteBuffer && r.Z > z.X - p.RouteBuffer && r.Y < z.W + p.RouteBuffer && r.W > z.Y - p.RouteBuffer) return "protected (" + zn + ")";
            foreach (var pt in points)
            {
                float rr = pt.Radius + p.ObjectiveClear + p.RouteBuffer;
                float dx = MathF.Max(MathF.Max(r.X - pt.Center.X, pt.Center.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - pt.Center.Y, pt.Center.Y - r.W), 0f);
                if (dx * dx + dy * dy < rr * rr) return "a point's ring";
            }
            foreach (var k in corridors) if (AshfallField.RectMeetsBox(r, k.Box(p.RouteBuffer))) return "a road";
            foreach (var o in placed) if (AshfallPlacer.Gap(r, o) < gap) return "a neighbour";
            return null;
        }

        // 1. Landmarks at the ends of the avenues' vistas and in the corners (wayfinding): seen down the roads.
        (string District, string Name, string Family, Vector2 At, Vector3 Size)[] landmarks =
        [
            ("cathedral quarter", "cathedral spire", "spire", new(-206f, 124f), new(16f, 16f, 34f)),       // beyond the north avenue's west end, over A
            ("manufactorum district", "manufactorum chimney tower", "spire", new(206f, 124f), new(16f, 16f, 34f)),
            ("hab-block ruins", "hab tower", "spire", new(-200f, 0f), new(14f, 14f, 26f)),                  // the middle avenue's west vista from B
            ("hab-block ruins", "hab tower east", "spire", new(200f, 0f), new(14f, 14f, 26f)),
        ];
        foreach (var (d, name, fam, at, size) in landmarks)
        {
            var r = AshfallField.Footprint(at, size, 0);
            if (!Free(r, p.ClusterGap)) { notes.Add($"landmark {name} does not fit at ({at.X}, {at.Y}) and is left out"); continue; }
            structures.Add(new(name, fam, at, size, 0, d, Landmark: true));
            placed.Add(r);
        }
        // Landmark sightlines (each tall landmark to its two nearest points): a structure in the band is refused only when it would rise into the line from the point's eye to the landmark's top
        // (the city: rows of 10-16 m buildings stand under a 26-34 m landmark's line beyond the first third of the way; a flat 4 m cap would cut every row the line crosses).
        var bands = new List<(Vector2 A, Vector2 B, float Top)>();
        foreach (var s in structures.Where(s => s.Landmark && s.Size.Z >= 12f))
            foreach (var pt in points.OrderBy(q => Vector2.DistanceSquared(new Vector2(q.Center.X, q.Center.Y), s.Centre)).Take(2))
            {
                var a = new Vector2(pt.Center.X, pt.Center.Y); var dir = Vector2.Normalize(s.Centre - a);
                bands.Add((a, s.Centre - dir * MathF.Max(s.Size.X, s.Size.Y), top + s.Size.Z * 0.85f));
            }
        bool InSight(Vector4 r, float height) => bands.Any(b => SightBlocked(b.A, b.B, b.Top, r, height, p.SightBand / 2f));

        // 2. The blocks: rows of buildings on the frontage line of every side, alleys between them, a wall in each wide gap, a yard behind with at most one structure.
        uint n = 0;
        int rows = 0, walls = 0, yards = 0, skipped = 0;
        foreach (var b in blocks)
        {
            var r = Buildable(b, Inset);
            if (r.Z - r.X < 10f || r.W - r.Y < 10f) continue;
            var st = Style(b.District);
            uint key = AshfallBuilder.Mix(p.LayoutSeed * 0x9E37u + 23u, n++);
            float d = st.Depth;
            // The rows on the block's long sides run its whole length (the frontage along the avenue stays one line); the short sides' rows fit between them, a corner alley at each end.
            bool longX = r.Z - r.X >= r.W - r.Y;
            float u0 = longX ? r.X : r.Y, u1 = longX ? r.Z : r.W, v0 = longX ? r.Y : r.X, v1 = longX ? r.W : r.Z;
            bool twoV = v1 - v0 >= 2f * d + MinYard, twoU = u1 - u0 >= 2f * d + MinYard;
            var sides = new List<(bool AlongX, float Line, float S0, float S1, int Outward)>();
            if (twoV) { sides.Add((longX, v0, u0, u1, -1)); sides.Add((longX, v1, u0, u1, 1)); }
            else if (v1 - v0 >= d)
            {
                // one row: on the side nearer an avenue
                Vector2 At(float v) => longX ? new Vector2((u0 + u1) / 2f, v) : new Vector2(v, (u0 + u1) / 2f);
                bool low = NearRoad(corridors, At(v0)) <= NearRoad(corridors, At(v1));
                sides.Add((longX, low ? v0 : v1, u0, u1, low ? -1 : 1));
            }
            float w0 = v0, w1 = v1;
            foreach (var sd in sides) { if (sd.Outward < 0) w0 = v0 + d + st.Corner; else w1 = v1 - d - st.Corner; }
            if (w1 - w0 >= 10f && twoU) { sides.Add((!longX, u0, w0, w1, -1)); sides.Add((!longX, u1, w0, w1, 1)); }
            bool twoX = longX ? twoU : twoV, twoY = longX ? twoV : twoU;
            int bi = 0;
            foreach (var (alongX, line, s0, s1, outward) in sides)
            {
                rows++;
                bool boundarySide = MathF.Abs(MathF.Abs(line) - 226f) < 1e-3f;
                float t = s0;
                int k = 0;
                int yaw = alongX ? 0 : 6;
                var taken = new List<(float A, float B)>();   // the stretches of the frontage line the row's buildings stand on
                while (t < s1 - 6f)
                {
                    uint h = AshfallBuilder.Mix(key, (uint)(bi * 64 + k++));
                    float wdt = st.Widths[(int)(h % (uint)st.Widths.Length)];
                    if (t + wdt > s1) { wdt = st.Widths.Where(x => t + x <= s1).DefaultIfEmpty(0f).Max(); if (wdt < st.Widths.Min()) break; }
                    float height = st.Height + (h >> 8) % 3u * 1f + (boundarySide && b.EdgeRow ? st.EdgeLift : 0f);
                    string fam = st.Families[(int)((h >> 12) % (uint)st.Families.Length)];
                    // the front stands on the frontage line; the depth varies behind it (into the yard)
                    float depth = fam == "facade" ? 3f : d + 2f * ((h >> 24) % 3u) - 2f;
                    float mid = t + wdt / 2f, across = line - outward * depth / 2f;
                    var centre = alongX ? new Vector2(mid, across) : new Vector2(across, mid);
                    var size = new Vector3(wdt, depth, fam == "bunker" ? MathF.Min(height, 7f) : height);
                    var fp = AshfallField.Footprint(centre, size, yaw);
                    if (size.Z > p.TallHeight && EdgeDistance(centre) > p.EdgeBand) size = size with { Z = p.TallHeight };
                    bool sight = size.Z > p.SightBlockHeight && InSight(fp, size.Z);
                    if (sight) why["a landmark's sightline"] = why.GetValueOrDefault("a landmark's sightline") + 1;
                    if (!sight && Free(fp, p.ClusterGap))
                    {
                        structures.Add(new($"{b.Name} row {bi} {fam} {k}", fam, centre, size, yaw, b.District));
                        placed.Add(fp);
                        taken.Add((t, t + wdt));
                        t += wdt;
                    }
                    else { skipped++; t += 4f; continue; }
                    // the gap after it: an alley, or now and then a wide gap (a kerb wall stands in it, below)
                    bool wide = (h >> 20) % 4u == 0u;
                    t += wide ? st.WideGap : st.Alleys[(int)((h >> 16) % (uint)st.Alleys.Length)];
                }
                // The kerb walls (draft 10's dashed block walls, lined up): on the frontage line wherever no building stands and the stretch is long enough, an opening kept at each end and a break
                // every few metres (each break over the 2.4 m narrow-gap rule), so the yard behind is always reached through the alleys and the openings.
                var free = new List<(float A, float B)>();
                float at = s0;
                foreach (var (ta, tb) in taken) { if (ta - at > 0f) free.Add((at, ta)); at = tb; }
                if (s1 - at > 0f) free.Add((at, s1));
                foreach (var (fa, fb) in free)
                {
                    float wa = fa + WallOpening, wb = fb - WallOpening;
                    if (wb - wa < MinKerb) continue;
                    int segs = Math.Max(1, (int)MathF.Ceiling((wb - wa + KerbBreak) / (KerbLength + KerbBreak)));
                    float len = (wb - wa - (segs - 1) * KerbBreak) / segs;
                    if (len < MinKerb) { segs = 1; len = wb - wa; }
                    for (int q = 0; q < segs; q++)
                    {
                        float c0 = wa + q * (len + KerbBreak) + len / 2f;
                        var wc = alongX ? new Vector2(c0, line - outward * 0.75f) : new Vector2(line - outward * 0.75f, c0);
                        var wsize = new Vector3(len, 1.5f, 2.2f);
                        var wfp = AshfallField.Footprint(wc, wsize, yaw);
                        if (Free(wfp, KerbBreak - 0.05f)) { structures.Add(new($"{b.Name} row {bi} kerb wall {walls}", "wall", wc, wsize, yaw, b.District)); placed.Add(wfp); walls++; }
                    }
                }
                bi++;
            }
            // the yard: what is left inside the rows
            float yd = d + st.Yard;
            var yard = new Vector4(r.X + (twoX ? yd : 0f), r.Y + (twoY ? yd : 0f), r.Z - (twoX ? yd : 0f), r.W - (twoY ? yd : 0f));
            if (yard.Z - yard.X >= 16f && yard.W - yard.Y >= 12f)
            {
                var yc = new Vector2((yard.X + yard.Z) / 2f, (yard.Y + yard.W) / 2f);
                uint h = AshfallBuilder.Mix(key, 0xDA7Du);
                var pick = st.Yards[(int)(h % (uint)st.Yards.Length)];
                // a yard's platform stands only where not all three points lie within the high ground's view range (no one platform overlooks every objective)
                if (pick.Family == "platform" && points.All(q => Vector2.Distance(new Vector2(q.Center.X, q.Center.Y), yc) <= p.HighGroundViewRange)) pick = ("", Vector3.Zero);
                if (pick.Family.Length > 0)
                {
                    int yaw = yard.Z - yard.X >= yard.W - yard.Y ? 0 : 6;
                    var fp = AshfallField.Footprint(yc, pick.Size, yaw);
                    if (Free(fp, st.Yard) && !(pick.Size.Z > p.SightBlockHeight && InSight(fp, pick.Size.Z))) { structures.Add(new($"{b.Name} yard {pick.Family}", pick.Family, yc, pick.Size, yaw, b.District)); placed.Add(fp); yards++; }
                }
            }
        }
        foreach (var d in districts)
        {
            float land = d.Rects.Sum(r => (r.Z - r.X) * (r.W - r.Y)), cover = structures.Where(s => s.District == d.Name).Sum(s => s.Size.X * s.Size.Y);
            notes.Add($"{d.Name}: {structures.Count(s => s.District == d.Name)} structures, drawn cover {cover / land:P0} of {land / 10000f:0.0} ha of block land (target {d.Density:P0})");
        }
        notes.Add($"city blocks: {blocks.Count} blocks, {rows} frontage rows, {walls} kerb wall segments, {yards} yard structures, {skipped} row places skipped ({string.Join(", ", why.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}"))})");

        Vector3 P(float x, float y) => new(x, y, top);
        Vector3[] spawns =
        [
            areas[0].Center, areas[1].Center, .. points.Select(q => q.Center),
            P(sj.X, sj.Y), P(swj.X, swj.Y), P(sej.X, sej.Y), P(wmj.X, wmj.Y), P(emj.X, emj.Y), P(wij.X, wij.Y), P(eij.X, eij.Y), P(wim.X, wim.Y), P(eim.X, eim.Y),
            P(win.X, win.Y), P(ein.X, ein.Y), P(wl.X, wl.Y), P(wil.X, wil.Y), P(el.X, el.Y), P(eil.X, eil.Y), P(0f, -160f), P(0f, -60f), P(0f, 50f),
            P(-232f, -232f), P(232f, -232f), P(-232f, 232f), P(232f, 232f),
        ];
        return new AshfallFieldPlan(p, points, areas, assembly, corridors, routes, districts, structures, prot, spawns, notes, aprons) { Layout = AshfallLayout.City, HashVersion = HashVersion, Blocks = blocks.Select(b => (b.Name, Buildable(b, Inset))).ToList() };
    }

    /// <summary>A side street's width between two blocks where no avenue runs (m; ours: two Marines abreast with room, not an army route).</summary>
    public const float SideStreet = 12f;

    /// <summary>The least yard between two facing rows of one block (m; ours: an alley, so both rows stand where the block is too narrow for a yard).</summary>
    const float MinYard = 6f;

    /// <summary>A kerb wall's longest segment, the break between two segments (over the 2.4 m narrow-gap rule) and the shortest segment kept (m; ours: draft 10's dashed block walls).</summary>
    public const float KerbLength = 10f, KerbBreak = 2.5f, MinKerb = 4f;

    /// <summary>The opening kept at each end of a frontage wall in a wide gap (m; ours: a doorway for a squad, over the 2.4 m narrow-gap rule).</summary>
    public const float WallOpening = 4f;

    static Vector4 Buildable(Block b, Func<bool, float, float, float, float> inset)
    {
        var o = b.Outer;
        return new Vector4(o.X + inset(true, o.X, o.Y, o.W), o.Y + inset(false, o.Y, o.X, o.Z), o.Z - inset(true, o.Z, o.Y, o.W), o.W - inset(false, o.W, o.X, o.Z));
    }

    /// <summary>A district's row style: depth of the rows, widths, alleys, the wide gap with its wall, the corner gap, the yard's least depth, heights and families, and the yards' pieces (ours).</summary>
    sealed record RowStyle(float Depth, float[] Widths, float[] Alleys, float WideGap, float Corner, float Yard, float Height, float EdgeLift, string[] Families, (string Family, Vector3 Size)[] Yards);

    static RowStyle Style(string district) => district switch
    {
        "cathedral quarter" => new(14f, [16f, 20f, 24f], [6f, 6f, 8f], 18f, 8f, 6f, 12f, 8f, ["shell", "shell", "shell", "shell", "facade"],
            [("monument", new(7f, 7f, 10f)), ("platform", new(16f, 7f, 5f)), ("", Vector3.Zero), ("shell", new(16f, 12f, 11f))]),
        "manufactorum district" => new(15f, [18f, 22f, 26f], [6f, 6f, 8f], 18f, 8f, 6f, 10f, 8f, ["shell", "shell", "bunker", "shell", "bunker"],
            [("platform", new(20f, 7f, 5f)), ("bunker", new(14f, 12f, 6f)), ("platform", new(16f, 7f, 5f)), ("", Vector3.Zero)]),
        "hab-block ruins" => new(14f, [16f, 18f, 22f], [6f, 6f, 8f], 16f, 8f, 6f, 12f, 8f, ["shell", "shell", "shell", "shell", "facade"],
            [("platform", new(16f, 7f, 5f)), ("", Vector3.Zero), ("shell", new(14f, 12f, 10f)), ("monument", new(7f, 7f, 10f))]),
        _ => new(12f, [12f, 14f, 16f], [6f, 8f, 10f], 18f, 10f, 12f, 7f, 0f, ["shell", "facade", "shell", "bunker"],
            [("platform", new(16f, 7f, 5f)), ("rubble", new(6f, 4f, 1.4f)), ("", Vector3.Zero), ("cover", new(5f, 1.5f, 1.2f))]),
    };

    static float NearRoad(IEnumerable<AshfallCorridor> corridors, Vector2 p) => corridors.Min(k => k.DistanceTo(p));

    static float EdgeDistance(Vector2 p) => AshfallMap.Half - MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y));

    /// <summary>
    /// Whether a structure <paramref name="height"/> tall on <paramref name="r"/> rises into the sightline from a point's eye at <paramref name="a"/> (1.6 m over the ground) to a landmark's top <paramref name="topZ"/> over
    /// <paramref name="b"/>, within <paramref name="half"/> of the line (sampled every metre; 1 m of margin under the line).
    /// </summary>
    static bool SightBlocked(Vector2 a, Vector2 b, float topZ, Vector4 r, float height, float half)
    {
        float len = Vector2.Distance(a, b);
        int n = Math.Max(1, (int)len);
        float eye = AshfallMap.BlockTop + 1.6f, roof = AshfallMap.BlockTop + height + 1f;
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            var q = Vector2.Lerp(a, b, t);
            float dx = MathF.Max(MathF.Max(r.X - q.X, q.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - q.Y, q.Y - r.W), 0f);
            if (dx * dx + dy * dy < half * half && roof > eye + (topZ - eye) * t) return true;
        }
        return false;
    }
}
