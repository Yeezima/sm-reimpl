using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall draft 5 (ours, placeholder demonstration; docs/musou-ashfall-graybox.md "Draft 5"): every visible graybox structure of the v5 generator (a tagged group) is replaced by ONE asset of the owner's library,
/// scaled to the structure's visual mass and placed on it; the level's collision is the fitted boxes of those assets (<see cref="AshfallKitCollision"/>) transformed with the placement. The stronghold is rebuilt as a breached ruin
/// enclosure. Deterministic: integer hashes (<see cref="AshfallBuilder.Mix"/>), the 15 degree table for every turn, total-order sorts, no System.Random, no logarithms.
/// </summary>
internal static class AshfallDraft5Pass
{
    /// <summary>A placed box in its item's local frame: centre, half sizes along the turned x, y and up, and the yaw step (x 15 degrees).</summary>
    readonly record struct WBox(Vector3 Centre, Vector3 Half, int YawStep)
    {
        public Vector2 Extent
        {
            get { var (s, c) = AshfallBuilder.Trig(YawStep); return new(MathF.Abs(c) * Half.X + MathF.Abs(s) * Half.Y, MathF.Abs(s) * Half.X + MathF.Abs(c) * Half.Y); }
        }

        public Vector4 Rect { get { var e = Extent; return new(Centre.X - e.X, Centre.Y - e.Y, Centre.X + e.X, Centre.Y + e.Y); } }
    }

    /// <summary>Models with walkable tops or stairs (the finer fit of the collision command): their cell, scaled, must stay under the step height.</summary>
    static readonly HashSet<string> Walkable = [.. AshfallKitFit.WalkableIds];

    const float StepHeight = 0.4f, Grow = 1f, MinBox = 0.15f;

    sealed class Ctx
    {
        public readonly List<AshfallPart> Parts = [];
        public readonly List<AshfallDressItem> Items = [];
        public readonly List<(int First, int Count)> ItemParts = [];
        public readonly List<AshfallStructure> Structures = [];
        public readonly List<string> Flags = [];
        public readonly List<AshfallPlacement> Log = [];
        public readonly List<(string Id, Vector2 At)> Placed = [];
        public readonly List<(int Item, WBox Box)> AllBoxes = [];
        public readonly Dictionary<string, int> Uses = [];
        public readonly Dictionary<string, int> DroppedBoxes = [];
        public readonly List<string> Dropped = [];
        public int RampsKept, RampsDropped;
        public uint Seed;
        public uint Counter;
        public List<Vector4> BannedRects = [];
        public List<Vector3> BannedDiscs = [];
    }

    sealed record Spec(string Group, string Family, Vector3 Min, Vector3 Max, AshfallBox? Box, string[] Pool)
    {
        public float GrowAlong = Grow, GrowAcross = Grow;
        public int[]? Yaws;
        public bool UseNet = true;
        public Vector2? Out;
        public AshfallKind? Kind;
        public bool NoCollision;
        public bool Required;
    }

    // ---------------------------------------------------------------- entry

    public static AshfallDraft5 Run()
    {
        var gb = AshfallMap.BuildGrayboxWithFeatures();
        var g5 = gb.Level;
        var c = new Ctx { Seed = AshfallBuilder.Mix(AshfallMap.VariationSeed, 0xA5F411u) };
        foreach (var p in g5.Parts)
            if (p.Kind is AshfallKind.Ground or AshfallKind.Road or AshfallKind.Paving or AshfallKind.Curb or AshfallKind.Boundary or AshfallKind.Objective) c.Parts.Add(p);
        int groundCount = c.Parts.Count;

        BuildBanned(c, g5);

        var items = new List<AshfallDressItem>();
        AshfallDressPass.SurfaceItems(g5.Parts, items);
        foreach (var it in items) { c.Items.Add(it); c.ItemParts.Add((0, 0)); }

        // Objective A: capture point on the ground, the objective plaza centre tile as a walk-over dais (visual).
        var a = g5.Points[0];
        var points = g5.Points.ToList();
        points[0] = a with { Center = new Vector3(a.Center.X, a.Center.Y, AshfallMap.BlockTop) };
        c.Items.Add(new AshfallDressItem("AC-037", new Vector3(a.Center.X, a.Center.Y, AshfallMap.BlockTop - 0.3f * 0.25f + 0.03f), 0f, new Vector3(16f, 16f, 1.5f * 0.25f), false, AshfallDressRole.Surface, -1));
        c.ItemParts.Add((0, 0));
        c.Flags.Add("A: the plinth with its four stairs is gone; the control point stands on the ground (z 0.25) on AC-037 squashed to a 0.3 m walk-over dais (visual only)");

        // The tagged groups, in creation order.
        var partGroup = gb.PartGroup!;
        var groups = gb.Groups!;
        var byGroup = new List<int>[groups.Count];
        for (int i = 0; i < byGroup.Length; i++) byGroup[i] = [];
        var untagged = new List<int>();
        for (int i = 0; i < g5.Parts.Count; i++)
        {
            var k = g5.Parts[i].Kind;
            if (k is AshfallKind.Ground or AshfallKind.Road or AshfallKind.Paving or AshfallKind.Curb or AshfallKind.Boundary or AshfallKind.Objective) continue;
            if (partGroup[i] >= 0) byGroup[partGroup[i]].Add(i); else untagged.Add(i);
        }
        foreach (int i in untagged)
            c.Flags.Add($"untagged graybox part {i} ({g5.Parts[i].Kind}) has no structure group and is dropped");

        var stronghold = new List<int>();
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            if (byGroup[gi].Count == 0) continue;
            if (g.Family.StartsWith("stronghold") || g.Family is "terrace" or "outerwall" || g.Name == "keep bridge") { stronghold.Add(gi); continue; }
            var (min, max) = Bounds(g5.Parts, byGroup[gi]);
            AshfallBox? box = byGroup[gi].Count == 1 ? AshfallBox.Of(g5.Parts[byGroup[gi][0]]) : null;
            if (g.Family == "bridge") Bridge(c, g.Name, min, max); else Generic(c, g, min, max, box, g5);
        }
        Stronghold(c, groups, byGroup, g5, stronghold);

        if (c.Dropped.Count > 0)
            c.Flags.Add($"{c.Dropped.Count} structures dropped: they stood in a lane, a start area, a capture zone's 6 m ring, on a spawn marker or within 2 m of the boundary (the boundary-foot walls all); first: {string.Join(", ", c.Dropped.Take(6))}");
        c.Flags.Add($"walkable ramps (stairs and rubble runs found in the top heightfields of the walkable models): {c.RampsKept} kept as sloped wedges, {c.RampsDropped} dropped (scaled slope over {MaxRampDegrees:0} degrees)");
        // Review flags for models whose collision wants a manual clean-up.
        foreach (var (id, n) in c.Uses.OrderBy(k => k.Key, StringComparer.Ordinal))
            if (AshfallKitCollision.NeedsCleanup(id)) c.Flags.Add($"{id} used {n}x: collision needs manual clean-up ({AshfallKitCollision.CleanupReason(id)})");
        foreach (var (id, n) in c.DroppedBoxes.OrderBy(k => k.Key, StringComparer.Ordinal))
            c.Flags.Add($"{id}: {n} collision boxes under {MinBox} m dropped as dust across its placements");

        var spawns = g5.Spawns.Select(s => s == g5.Points[0].Center ? points[0].Center : s).ToList();
        var level = new AshfallLevel(c.Parts, points, g5.Areas, spawns, AshfallMap.HashOf(c.Parts, AshfallMap.Version));
        var hidden = Enumerable.Range(groundCount, c.Parts.Count - groundCount).ToList();
        var dressing = new AshfallDressing(c.Items, hidden, AshfallMap.DressVersion, c.Structures) { ItemParts = c.ItemParts, Flags = c.Flags };
        return new AshfallDraft5(level, dressing, gb, c.Log);
    }

    static (Vector3 Min, Vector3 Max) Bounds(IReadOnlyList<AshfallPart> parts, List<int> idx)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (int i in idx) foreach (var v in parts[i].Corners) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return (min, max);
    }

    // ---------------------------------------------------------------- banned places (the checks' list)

    static void BuildBanned(Ctx c, AshfallLevel g5)
    {
        const float lane = 4.2f;
        foreach (float v in new[] { -92f, 92f, -232f, 232f })
        {
            c.BannedRects.Add(new(v - lane, -240f, v + lane, 240f));
            c.BannedRects.Add(new(-240f, v - lane, 240f, v + lane));
        }
        c.BannedRects.Add(new(-6f, -160f, 6f, 80f));
        c.BannedRects.Add(new(-80f, -6f, 80f, 6f));
        foreach (var a in g5.Areas) c.BannedRects.Add(new(a.Center.X - a.Size.X / 2f, a.Center.Y - a.Size.Y / 2f, a.Center.X + a.Size.X / 2f, a.Center.Y + a.Size.Y / 2f));
        foreach (var p in g5.Points) c.BannedDiscs.Add(new(p.Center.X, p.Center.Y, p.Radius + 6f));
        foreach (var s in g5.Spawns) c.BannedDiscs.Add(new(s.X, s.Y, 4f));
    }

    /// <summary>Whether a box breaks a banned place or stands within 2 m of the boundary's inner face.</summary>
    static bool Banned(Ctx c, WBox b)
    {
        var r = b.Rect;
        if (r.X < -238f || r.Y < -238f || r.Z > 238f || r.W > 238f) return true;
        if (b.Centre.Z - b.Half.Z >= 8f) return false;   // above reach (a bridge over an avenue, an upper storey): the places below stay clear
        foreach (var z in c.BannedRects) if (r.X < z.Z && r.Z > z.X && r.Y < z.W && r.W > z.Y) return true;
        foreach (var d in c.BannedDiscs)
        {
            float dx = MathF.Max(MathF.Max(r.X - d.X, d.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - d.Y, d.Y - r.W), 0f);
            if (dx * dx + dy * dy < d.Z * d.Z) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- pools

    static readonly string[] Shell = ["AC-005", "AC-003", "AC-004", "AC-030"], Facade = ["AC-004", "AC-001", "AC-006", "AC-002"], Tower = ["AC-010", "AC-005", "AC-030"],
        Cover = ["AC-011", "AC-017", "AC-014", "AC-018", "AC-012"], Landmark = ["AC-027", "AC-028", "AC-029", "AC-031", "AC-032", "AC-013"],
        RubbleLow = ["AC-020", "AC-024", "AC-025"], RubbleMid = ["AC-019", "AC-021", "AC-018"], RubbleTall = ["AC-023", "AC-021", "AC-019"];

    static string[] PoolOf(string family, string name, float height) => family switch
    {
        "shell" or "shell2" or "corner" => Shell,
        "facade" or "wall" => Facade,
        "tower" => Tower,
        "platform" => ["AC-008"],
        "cover" => Cover,
        "wallrun" => ["AC-001", "AC-017", "AC-014"],
        "wallpiece" => RubbleTall,
        "rubble" => height <= 0.4f ? RubbleLow : height <= 1.6f ? RubbleMid : RubbleTall,
        "monument" => ["AC-027", "AC-029", "AC-028"],
        "arch" => ["AC-002"],
        "obelisk" => ["AC-029", "AC-010"],
        "wreck" => ["AC-030", "AC-028", "AC-027"],
        "bunker" => ["AC-005", "AC-030", "AC-028"],
        "bridge" => ["AC-009"],
        "backdrop" => ["AC-010", "AC-005", "AC-030"],
        _ => Landmark,
    };

    // ---------------------------------------------------------------- generic structures

    static void Generic(Ctx c, AshfallGroup g, Vector3 min, Vector3 max, AshfallBox? box, AshfallLevel g5)
    {
        if (g.Family == "plinth") { c.Log.Add(new(g.Name, g.Family, "AC-037", c.Items.Count - 1, "replaced: control point on the ground, dais tile")); return; }
        if (g.Family == "obelisk")
        {
            // A's obelisk would stand inside the capture zone's 6 m ring: moved 30 m west, 6 m south.
            box = null;
            float tall = max.Z - min.Z;
            min = new Vector3(min.X - 30f, min.Y - 6f, AshfallMap.BlockTop); max = new Vector3(max.X - 30f, max.Y - 6f, AshfallMap.BlockTop + tall);
            c.Flags.Add("A's obelisk moved 30 m west and 6 m south (it stood inside the capture zone's ring)");
        }
        var spec = new Spec(g.Name, g.Family, min, max, box, PoolOf(g.Family, g.Name, max.Z - min.Z))
        {
            NoCollision = g.Family == "backdrop",
            UseNet = g.Family is not ("wallrun" or "monument" or "arch"),   // the compound's walls, the monument and the arch were hand-placed on the avenues and around the start area
            Kind = g.Family is "rubble" or "wallpiece" ? AshfallKind.Rubble : null,
        };
        if (g.Family == "backdrop") spec.GrowAlong = spec.GrowAcross = 0f;
        Place(c, spec);
    }

    // ---------------------------------------------------------------- the placer

    sealed record Cand(string Id, int Yaw, Vector3 Size, float Score, float Mass, bool Risers);

    static float Dev(float a, float b) => a > b ? a / b - 1f : b / a - 1f;

    static int Place(Ctx c, Spec s, int onlyYaw = -1)
    {
        float h = MathF.Max(s.Max.Z - s.Min.Z, 0.05f);
        Vector2 centre = s.Box is { } bx ? new(bx.Cx, bx.Cy) : new((s.Min.X + s.Max.X) / 2f, (s.Min.Y + s.Max.Y) / 2f);
        float wx = s.Max.X - s.Min.X, wy = s.Max.Y - s.Min.Y;
        int[] yaws = onlyYaw >= 0 ? [onlyYaw] : s.Yaws ?? (s.Box is { } b2 ? [b2.YawStep, (b2.YawStep + 12) % 24] : [0, 6, 12, 18]);
        var cands = new List<Cand>();
        foreach (var id in s.Pool)
        {
            var m = AshfallKit.Get(id);
            var r = m.RawSize;
            foreach (int yaw in yaws)
            {
                float tx, ty;
                if (s.Box is { } bb) { tx = bb.Length; ty = bb.Depth; }
                else if (yaw % 12 == 0) { tx = wx; ty = wy; }
                else { tx = wy; ty = wx; }
                float kx = tx / r.X, kh = h / r.Y, kd = ty / r.Z;
                float sc = MathF.Sqrt(kx * kh);
                float Cl(float k) => MathF.Min(MathF.Max(k, 0.8f * sc), 1.25f * sc);
                float lx = MathF.Min(Cl(kx) * r.X, tx + 2f * s.GrowAlong), ly = MathF.Min(Cl(kd) * r.Z, ty + 2f * s.GrowAcross), hh = MathF.Min(Cl(kh) * r.Y, h);
                bool risers = false;
                if (Walkable.Contains(id))
                {
                    float cell = AshfallKitCollision.Metrics(id).CellSize;
                    float limit = StepHeight / cell * r.Y;   // tallest the model may be drawn before one cell exceeds the step
                    if (hh > limit) { hh = limit; risers = hh < 0.8f * h; }
                }
                float score = Dev(lx, tx) + Dev(hh, h) + 0.7f * Dev(ly, ty) + (risers ? 2f : 0f);
                cands.Add(new Cand(id, yaw, new Vector3(lx, ly, hh), score, lx * ly * hh / (tx * ty * h), risers));
            }
        }
        // Best model first; models within 15 % of the best form the pool the hash picks from; a model already placed within 12 m is avoided.
        var perModel = cands.GroupBy(k => k.Id).Select(grp => grp.OrderBy(k => k.Score).ThenBy(k => k.Yaw).First()).OrderBy(k => k.Score).ThenBy(k => k.Id, StringComparer.Ordinal).ToList();
        float best = perModel[0].Score;
        var band = perModel.Where(k => k.Score <= best * 1.15f + 0.03f).ToList();
        var fresh = band.Where(k => !c.Placed.Any(p => p.Id == k.Id && Vector2.Distance(p.At, centre) < 12f)).ToList();
        if (fresh.Count > 0) band = fresh;
        uint pick = AshfallBuilder.Mix(c.Seed, c.Counter++);
        var chosen = band[(int)(pick % (uint)band.Count)];
        // Among the model's turns the score decides; ties go to the front facing away from the map's middle.
        var turns = cands.Where(k => k.Id == chosen.Id).OrderBy(k => k.Score).ThenBy(k => k.Yaw).ToList();
        var outDir = s.Out ?? (centre.LengthSquared() > 1f ? Vector2.Normalize(centre) : Vector2.UnitY);
        var pickTurn = turns[0];
        foreach (var t in turns.Skip(1))
        {
            if (t.Score > turns[0].Score + 0.02f) break;
            var (sn, cs) = AshfallBuilder.Trig(t.Yaw);
            var (sn0, cs0) = AshfallBuilder.Trig(pickTurn.Yaw);
            // the front faces local -Y turned by the yaw: (sin, -cos)
            if (Vector2.Dot(new Vector2(sn, -cs), outDir) > Vector2.Dot(new Vector2(sn0, -cs0), outDir) + 1e-4f) pickTurn = t;
        }
        return Commit(c, s, centre, pickTurn, chosen.Id, h);
    }

    static int Commit(Ctx c, Spec s, Vector2 centre, Cand cand, string id, float h)
    {
        var size = cand.Size;
        float bottom = s.Min.Z;
        AshfallDressItem Item(Vector3 sz) => new(id, new Vector3(centre.X, centre.Y, bottom), cand.Yaw * (MathF.PI / 12f), sz, false, AshfallDressRole.Prop, -1);
        var item = Item(size);
        var boxes = s.NoCollision ? [] : Coarse(s, Boxes(item, cand.Yaw, id));
        string outcome = "placed";
        if (s.UseNet && !s.NoCollision)
        {
            foreach (float f in new[] { 1f, 0.85f, 0.7f })
            {
                size = new Vector3(cand.Size.X * f, cand.Size.Y * f, cand.Size.Z);
                item = Item(size);
                boxes = Coarse(s, Boxes(item, cand.Yaw, id));
                if (!boxes.Any(b => Banned(c, b))) { if (f < 1f) { outcome = "placed, shrunk " + f; c.Flags.Add($"{s.Group}: {id} shrunk to {f:0.##} of its size to stay out of a banned place"); } break; }
                if (f == 0.7f) { c.Log.Add(new(s.Group, s.Family, "", -1, "dropped: inside a banned place (lane, start area, capture zone ring, spawn or the boundary's 2 m)")); c.Dropped.Add(s.Group); return -1; }
            }
        }
        if (Walkable.Contains(id) && cand.Risers) c.Flags.Add($"{s.Group}: {id} drawn lower than the structure ({size.Z:0.#} m of {h:0.#} m) to keep risers under the step height");
        if (cand.Mass < 0.35f || cand.Mass > 3f) c.Flags.Add($"{s.Group} ({s.Family}): {id} mass {cand.Mass:0.##} of the structure's bounds (mismatch)");
        else if (cand.Score > 3f) c.Flags.Add($"{s.Group} ({s.Family}): {id} is the closest fit, off by {cand.Score:0.##} in total");

        int index = c.Items.Count;
        int first = c.Parts.Count;
        c.Items.Add(item);
        c.Uses[id] = c.Uses.GetValueOrDefault(id) + 1;
        c.Placed.Add((id, centre));
        int dust = 0;
        var ids = new List<int>();
        foreach (var b in boxes)
        {
            var e = b.Half * 2f;
            if (e.X < MinBox && e.Y < MinBox && e.Z < MinBox) { dust++; continue; }
            AddPart(c, b, item.Position.Z, s.Kind);
            c.AllBoxes.Add((index, b));
        }
        if (dust > 0) c.DroppedBoxes[id] = c.DroppedBoxes.GetValueOrDefault(id) + dust;
        if (!s.NoCollision)
            foreach (var rp in AshfallKitCollision.Ramps(id))
            {
                var corners = RampCorners(item, cand.Yaw, rp, out bool ok);
                if (ok) { c.Parts.Add(new AshfallPart(AshfallKind.Stair, corners)); c.RampsKept++; } else c.RampsDropped++;
            }
        c.ItemParts.Add((first, c.Parts.Count - first));
        var parts = Enumerable.Range(first, c.Parts.Count - first).ToList();
        c.Structures.Add(new AshfallStructure(parts, s.Min, s.Max));
        c.Log.Add(new(s.Group, s.Family, id, index, outcome));
        return index;
    }

    // ---------------------------------------------------------------- collision transform

    /// <summary>Rubble piles and covers: the fitted boxes are merged into at most <see cref="Slabs"/> boxes (the union of the boxes whose centres fall in the same slab along the item's long axis), which keeps the silhouette along the run and stays near the 69 k triangles of v5.</summary>
    const int Slabs = 6;

    static List<WBox> Coarse(Spec s, List<WBox> boxes)
    {
        if (s.Family is not ("rubble" or "wallpiece" or "cover") || boxes.Count <= Slabs) return boxes;
        var (sn, co) = AshfallBuilder.Trig(boxes[0].YawStep);
        bool alongX = true;
        float lo = float.MaxValue, hi = float.MinValue, lo2 = float.MaxValue, hi2 = float.MinValue;
        foreach (var b in boxes)
        {
            float u = b.Centre.X * co + b.Centre.Y * sn, v = -b.Centre.X * sn + b.Centre.Y * co;
            lo = MathF.Min(lo, u - b.Half.X); hi = MathF.Max(hi, u + b.Half.X); lo2 = MathF.Min(lo2, v - b.Half.Y); hi2 = MathF.Max(hi2, v + b.Half.Y);
        }
        alongX = hi - lo >= hi2 - lo2;
        if (!alongX) { lo = lo2; hi = hi2; }
        var mn = new Vector3[Slabs]; var mx = new Vector3[Slabs]; var used = new bool[Slabs];
        for (int i = 0; i < Slabs; i++) { mn[i] = new Vector3(float.MaxValue); mx[i] = new Vector3(float.MinValue); }
        foreach (var b in boxes)
        {
            float u = b.Centre.X * co + b.Centre.Y * sn, v = -b.Centre.X * sn + b.Centre.Y * co, a = alongX ? u : v;
            int k = Math.Clamp((int)((a - lo) / MathF.Max(hi - lo, 1e-3f) * Slabs), 0, Slabs - 1);
            used[k] = true;
            // corners in the turned frame (u, v, z)
            mn[k] = Vector3.Min(mn[k], new Vector3(u - b.Half.X, v - b.Half.Y, b.Centre.Z - b.Half.Z));
            mx[k] = Vector3.Max(mx[k], new Vector3(u + b.Half.X, v + b.Half.Y, b.Centre.Z + b.Half.Z));
        }
        var list = new List<WBox>();
        for (int k = 0; k < Slabs; k++)
        {
            if (!used[k]) continue;
            var c3 = (mn[k] + mx[k]) / 2f; var h3 = (mx[k] - mn[k]) / 2f;
            list.Add(new WBox(new Vector3(c3.X * co - c3.Y * sn, c3.X * sn + c3.Y * co, c3.Z), h3, boxes[0].YawStep));
        }
        return list;
    }

    /// <summary>
    /// The item's collision boxes in the simulation frame. The mapping is the item contract (<see cref="AshfallDressItem"/>) with SwapXZ false: the model's raw bounds are fitted into the item's box, glTF X is local X, glTF -Z is local Y,
    /// glTF Y is up; the local frame is turned by the yaw about the box's centre.
    /// </summary>
    static List<WBox> Boxes(AshfallDressItem item, int yawStep, string id)
    {
        var m = AshfallKit.Get(id);
        var r = m.RawSize;
        var list = new List<WBox>();
        foreach (var (mn, mx) in AshfallKitCollision.Boxes(id))
        {
            var u0 = (mn - m.RawMin) / r; var u1 = (mx - m.RawMin) / r;
            float lx0 = (u0.X - 0.5f) * item.Size.X, lx1 = (u1.X - 0.5f) * item.Size.X;
            float ly0 = (0.5f - u1.Z) * item.Size.Y, ly1 = (0.5f - u0.Z) * item.Size.Y;
            float z0 = item.Position.Z + u0.Y * item.Size.Z, z1 = item.Position.Z + u1.Y * item.Size.Z;
            list.Add(Local(item.Position, yawStep, (lx0 + lx1) / 2f, (ly0 + ly1) / 2f, z0, z1, (lx1 - lx0) / 2f, (ly1 - ly0) / 2f));
        }
        return list;
    }

    /// <summary>A box given in an item's local frame (offset from the item's centre, half sizes) as a world box.</summary>
    static WBox Local(Vector3 itemPos, int yawStep, float cx, float cy, float z0, float z1, float hx, float hy)
    {
        var (s, co) = AshfallBuilder.Trig(yawStep);
        float wx = itemPos.X + co * cx - s * cy, wy = itemPos.Y + s * cx + co * cy;
        return new WBox(new Vector3(wx, wy, (z0 + z1) / 2f), new Vector3(hx, hy, (z1 - z0) / 2f), yawStep);
    }

    /// <summary>Largest slope a placed ramp may keep after the scale (the sim walks 60 degrees; ours, a margin).</summary>
    const float MaxRampDegrees = 55f;

    /// <summary>
    /// A model's ramp (a wedge: flat bottom, a plane for a top, raw frame) moved by the item's transform. <paramref name="ok"/> is false when the scaled slope of its top is over <see cref="MaxRampDegrees"/>
    /// (non-uniform scale changes slopes) or the top does not rise.
    /// </summary>
    static Vector3[] RampCorners(AshfallDressItem item, int yawStep, AshfallRamp r, out bool ok)
    {
        var m = AshfallKit.Get(item.Asset);
        var (sn, co) = AshfallBuilder.Trig(yawStep);
        Vector3 W(float x, float y, float z)
        {
            var u = (new Vector3(x, y, z) - m.RawMin) / m.RawSize;
            float lx = (u.X - 0.5f) * item.Size.X, ly = (0.5f - u.Z) * item.Size.Y;
            return new Vector3(item.Position.X + co * lx - sn * ly, item.Position.Y + sn * lx + co * ly, item.Position.Z + u.Y * item.Size.Z);
        }
        float TopAt(float x, float z)
        {
            float t = r.Dir switch { 0 => (x - r.Min.X) / (r.Max.X - r.Min.X), 1 => 1f - (x - r.Min.X) / (r.Max.X - r.Min.X), 2 => (z - r.Min.Z) / (r.Max.Z - r.Min.Z), _ => 1f - (z - r.Min.Z) / (r.Max.Z - r.Min.Z) };
            return r.YLow + t * (r.YHigh - r.YLow);
        }
        float mx = (r.Min.X + r.Max.X) / 2f, mz = (r.Min.Z + r.Max.Z) / 2f;
        var lowMid = r.Dir switch { 0 => W(r.Min.X, r.YLow, mz), 1 => W(r.Max.X, r.YLow, mz), 2 => W(mx, r.YLow, r.Min.Z), _ => W(mx, r.YLow, r.Max.Z) };
        var highMid = r.Dir switch { 0 => W(r.Max.X, r.YHigh, mz), 1 => W(r.Min.X, r.YHigh, mz), 2 => W(mx, r.YHigh, r.Max.Z), _ => W(mx, r.YHigh, r.Min.Z) };
        float run = MathF.Sqrt((highMid.X - lowMid.X) * (highMid.X - lowMid.X) + (highMid.Y - lowMid.Y) * (highMid.Y - lowMid.Y)), rise = highMid.Z - lowMid.Z;
        ok = rise > 1e-3f && run > 1e-3f && rise <= run * 1.4281480f;   // tan 55 degrees
        if (!ok) return [];
        // raw vertices in the order Frame uses (x, y up, z right-handed): bottom (-,-,-) (+,-,-) (+,+,-) (-,+,-)... with y the "up" axis
        float[] xs = [r.Min.X, r.Max.X], zs = [r.Min.Z, r.Max.Z];
        Vector3 V(int sx, int sy, int sz)
        {
            float x = xs[sx], z = zs[sz];
            return W(x, sy == 0 ? r.Min.Y : TopAt(x, z), z);
        }
        Vector3[] p = [V(0, 0, 0), V(1, 0, 0), V(1, 1, 0), V(0, 1, 0), V(0, 0, 1), V(1, 0, 1), V(1, 1, 1), V(0, 1, 1)];
        int[][] faces = [[3, 2, 1, 0], [4, 5, 6, 7], [0, 1, 5, 4], [2, 3, 7, 6], [3, 0, 4, 7], [1, 2, 6, 5]];
        var corners = new Vector3[36];
        for (int f = 0; f < 6; f++)
        {
            int[] q = faces[f];
            ReadOnlySpan<int> order = [q[0], q[1], q[2], q[0], q[2], q[3]];
            for (int k = 0; k < 6; k++) corners[f * 6 + k] = p[order[k]];
        }
        return corners;
    }

    static void AddPart(Ctx c, WBox b, float itemBottom, AshfallKind? forced)
    {
        var (s, co) = AshfallBuilder.Trig(b.YawStep);
        float dx = b.Half.X * 2f, dy = b.Half.Y * 2f, dz = b.Half.Z * 2f;
        float top = b.Centre.Z + b.Half.Z - itemBottom, bottom = b.Centre.Z - b.Half.Z - itemBottom;
        var kind = forced ?? (top <= 1.3f ? AshfallKind.Cover : dz <= 0.6f && dx * dy >= 4f && bottom > 0.5f ? AshfallKind.Floor : AshfallKind.Wall);
        var corners = AshfallBuilder.Frame(b.Centre, new Vector3(co, s, 0f), new Vector3(-s, co, 0f), Vector3.UnitZ, b.Half.X, b.Half.Y, b.Half.Z);
        c.Parts.Add(new AshfallPart(kind, corners));
    }

    // ---------------------------------------------------------------- the stronghold

    static void Stronghold(Ctx c, IReadOnlyList<AshfallGroup> groups, List<int>[] byGroup, AshfallLevel g5, List<int> ids)
    {
        (Vector3 Min, Vector3 Max) B(string name)
        {
            int gi = ids.First(i => groups[i].Name == name);
            return Bounds(g5.Parts, byGroup[gi]);
        }

        // The gate: x -12..12 in the south leg, one fortified gate segment.
        {
            var s = new Spec("ring gate", "stronghold-gate", new(-12f, 112f, 0f), new(12f, 116f, 12f), null, ["AC-006"]) { Yaws = [0], GrowAlong = 0f, UseNet = false };
            Place(c, s);
            c.Flags.Add("the gate segment (AC-006) is drawn over the gate; its collision is whatever the fit left open (see the gate width in the report)");
        }
        // The four corners: AC-003, the L's mass turned toward the ring's outer corner.
        foreach (var (name, x, y, h) in new[] { ("ring corner south-west", -72f, 112f, 12f), ("ring corner south-east", 66f, 112f, 18f), ("ring corner north-west", -72f, 214f, 12f), ("ring corner north-east", 66f, 214f, 18f) })
        {
            var s = new Spec(name, "stronghold-corner", new(x, y, 0f), new(x + 6f, y + 6f, h), null, ["AC-003"]) { UseNet = false, GrowAlong = 0f, GrowAcross = 0f };
            var outward = new Vector2(x < 0f ? -1f : 1f, y < 150f ? -1f : 1f);
            int bestYaw = 0; float bestDot = float.MinValue;
            foreach (int yaw in new[] { 0, 6, 12, 18 })
            {
                var item = new AshfallDressItem("AC-003", new Vector3(x + 3f, y + 3f, 0f), yaw * (MathF.PI / 12f), new Vector3(6f, 6f, h), false, AshfallDressRole.Prop, -1);
                var bx = Boxes(item, yaw, "AC-003");
                float vol = 0f; var cm = Vector2.Zero;
                foreach (var b in bx) { float v = b.Half.X * b.Half.Y * b.Half.Z; vol += v; cm += new Vector2(b.Centre.X - (x + 3f), b.Centre.Y - (y + 3f)) * v; }
                float dot = vol > 0f ? Vector2.Dot(cm / vol, outward) : 0f;
                if (dot > bestDot + 1e-4f) { bestDot = dot; bestYaw = yaw; }
            }
            Place(c, s, bestYaw);
        }
        // The legs: lines of standalone wall ruins with breaches.
        uint seed = 7000;
        foreach (var (name, alongX, t0, t1, lo, hi, height, yaw) in new[]
        {
            ("ring south leg west", true, 112f, 116f, -66f, -12f, 12f, 0),
            ("ring south leg east", true, 112f, 116f, 12f, 66f, 18f, 0),
            ("ring west leg", false, -72f, -68f, 118f, 214f, 12f, 18),
            ("ring north leg", true, 216f, 220f, -66f, 66f, 12f, 12),
            ("ring east leg", false, 68f, 72f, 118f, 214f, 18f, 6),
        })
            Leg(c, name, alongX, t0, t1, lo, hi, height, yaw, seed += 101);

        // The keep, its tower, the stair towers, the bridge, the terraces and the outer walls: one asset each.
        {
            var (mn, mx) = B("keep");
            var s = new Spec("keep", "stronghold-keep", mn, mx, null, ["AC-030", "AC-005"]) { Yaws = [0], UseNet = false };
            Place(c, s);
            c.Flags.Add("the keep: no keep asset in the library; the closest mass is used (a 64 x 48 x 18 m graybox keep against a 24 x 12 x 16 m buttress)");
        }
        foreach (var name in new[] { "Keep tower", "Stair tower SW", "Stair tower SE" })
        {
            var (mn, mx) = B(name);
            var pool = name == "Keep tower" ? new[] { "AC-010" } : new[] { "AC-007", "AC-010" };
            Place(c, new Spec(name, name == "Keep tower" ? "stronghold-keeptower" : "stronghold-stairtower", mn, mx, null, pool) { UseNet = false });
        }
        foreach (var name in new[] { "terrace 1", "terrace 2", "terrace 3" })
        {
            var (mn, mx) = B(name);
            Place(c, new Spec(name, "terrace", mn, mx, null, ["AC-008"]) { Yaws = [0, 12], UseNet = false });
        }
        foreach (var name in new[] { "east outer wall 1", "east outer wall 2" })
        {
            var (mn, mx) = B(name);
            Place(c, new Spec(name, "outerwall", mn, mx, null, ["AC-001"]) { Yaws = [0, 12], UseNet = false });
        }
        {
            var (mn, mx) = B("keep bridge");
            Bridge(c, "keep bridge", mn, mx);
        }
    }

    static void Leg(Ctx c, string name, bool alongX, float t0, float t1, float lo, float hi, float height, int yaw, uint seed)
    {
        float pos = lo; uint k = 0; bool first = true;
        var segs = new List<(float A, float B)>(); var breaches = new List<(float A, float B)>();
        while (hi - pos > 1e-3f)
        {
            float rem = hi - pos;
            float lw = 14f + AshfallBuilder.Mix(seed, k++) % 15u, lb = (first ? 8f : 6f) + AshfallBuilder.Mix(seed, k++) % (first ? 7u : 9u);
            if (first) lw = MathF.Min(lw, rem - 22f);
            if (rem - lw < lb + 14f && !first) { segs.Add((pos, hi)); break; }
            if (first) lb = MathF.Min(lb, rem - lw - 14f);
            if (rem - lw - lb < 14f && !first) { segs.Add((pos, hi)); break; }
            segs.Add((pos, pos + lw));
            breaches.Add((pos + lw, pos + lw + lb));
            pos += lw + lb;
            first = false;
        }
        int n = 0;
        foreach (var (a, b) in segs)
        {
            var mn = alongX ? new Vector3(a, t0, 0f) : new Vector3(t0, a, 0f);
            var mx = alongX ? new Vector3(b, t1, height) : new Vector3(t1, b, height);
            Place(c, new Spec($"{name} wall {n++}", "stronghold-wall", mn, mx, null, ["AC-004", "AC-001", "AC-006", "AC-005", "AC-030"]) { Yaws = [yaw], GrowAlong = 0f, UseNet = false });
        }
        n = 0;
        foreach (var (a, b) in breaches)
        {
            bool rubble = n > 0 && b - a >= 8f && AshfallBuilder.Mix(seed, 500u + (uint)n) % 2u == 0;
            if (rubble)
            {
                float off = (b - a) / 2f - 2.2f, mid = (a + b) / 2f + (AshfallBuilder.Mix(seed, 600u + (uint)n) % 2u == 0 ? -off : off);
                float tm = (t0 + t1) / 2f;
                var mn = alongX ? new Vector3(mid - 1.8f, tm - 1.5f, 0f) : new Vector3(tm - 1.5f, mid - 1.8f, 0f);
                var mx = alongX ? new Vector3(mid + 1.8f, tm + 1.5f, 1.8f) : new Vector3(tm + 1.5f, mid + 1.8f, 1.8f);
                Place(c, new Spec($"{name} breach {n} rubble", "rubble", mn, mx, null, ["AC-019", "AC-023"]) { Yaws = [yaw], UseNet = false, Kind = AshfallKind.Rubble, GrowAlong = 0.2f, GrowAcross = 0.2f });
            }
            c.Flags.Add($"{name} breach {n}: {b - a:0.#} m wide{(rubble ? ", rubble pile" : "")}");
            n++;
        }
    }

    /// <summary>The bridge: one AC-009 between its ends. Shortened by 1 m a side up to twice when its collision meets what already stands; dropped (and flagged) when it still does.</summary>
    static void Bridge(Ctx c, string name, Vector3 mn, Vector3 mx)
    {
        for (float cut = 0f; cut <= 2f; cut += 1f)
        {
            var s = new Spec(name, "bridge", mn + new Vector3(cut, 0f, 0f), mx - new Vector3(cut, 0f, 0f), null, ["AC-009"]) { Yaws = [0], GrowAlong = 0f, UseNet = false };
            int before = c.Items.Count, partsBefore = c.Parts.Count, flagsBefore = c.Flags.Count, keptBefore = c.RampsKept, rampsDroppedBefore = c.RampsDropped, droppedBefore = c.Dropped.Count;
            var usesBefore = new Dictionary<string, int>(c.Uses); var dustBefore = new Dictionary<string, int>(c.DroppedBoxes);
            int idx = Place(c, s);
            if (idx < 0) return;
            bool hit = false;
            foreach (var (it, b) in c.AllBoxes.Where(x => x.Item == idx))
                foreach (var (it2, b2) in c.AllBoxes)
                {
                    if (it2 == idx) continue;
                    var e1 = b.Extent; var e2 = b2.Extent;
                    if (MathF.Abs(b.Centre.X - b2.Centre.X) < e1.X + e2.X - 0.3f && MathF.Abs(b.Centre.Y - b2.Centre.Y) < e1.Y + e2.Y - 0.3f && MathF.Abs(b.Centre.Z - b2.Centre.Z) < b.Half.Z + b2.Half.Z - 0.3f) { hit = true; break; }
                }
            if (!hit) return;
            // Undo this try.
            c.Items.RemoveAt(before); c.ItemParts.RemoveAt(before); c.Structures.RemoveAt(c.Structures.Count - 1);
            c.Parts.RemoveRange(partsBefore, c.Parts.Count - partsBefore);
            c.AllBoxes.RemoveAll(x => x.Item == idx);
            c.Placed.RemoveAt(c.Placed.Count - 1);
            c.Log.RemoveAt(c.Log.Count - 1);
            c.Flags.RemoveRange(flagsBefore, c.Flags.Count - flagsBefore);
            c.Dropped.RemoveRange(droppedBefore, c.Dropped.Count - droppedBefore);
            c.RampsKept = keptBefore; c.RampsDropped = rampsDroppedBefore;
            c.Uses.Clear(); foreach (var (k, v) in usesBefore) c.Uses[k] = v;
            c.DroppedBoxes.Clear(); foreach (var (k, v) in dustBefore) c.DroppedBoxes[k] = v;
        }
        c.Log.Add(new(name, "bridge", "", -1, "dropped: AC-009 does not fit between what stands at its ends"));
        c.Flags.Add($"{name}: dropped, AC-009 does not fit between what stands at its ends");
    }
}
