using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall draft 9 (late batch F, owner 2026-11-06; ours, placeholder demonstration; docs/musou-ashfall-graybox.md "Draft 9 layout"):
/// <list type="bullet">
/// <item><b>Stairs that walk.</b> The draft-5 ramp wedges (read from the fitted voxel grid: side cheeks, broken wall tops, half stairs) are gone. Each stair found in a walkable model's own mesh
/// (<see cref="AshfallKitCollision.Stairs"/>) gets one ramp over its step nosings, from the floor at its foot to its landing; the model's own boxes under the ramp are cut down below it (in slices, so shots still meet
/// stepped stone), so no riser pokes through. A stair is kept only when, on the item alone, a walker (the player's capsule and step, <see cref="Movement.Step"/>) walks from its foot up onto its landing and back down
/// without a stall; otherwise its ramp is dropped and its boxes stay as fitted (named in the stair flag: a stair the model ends in its own wall, one too narrow or too steep after the scale).</item>
/// <item><b>Openings at the foot, landings at the top.</b> Each kept stair keeps a clear approach in front of its foot and a clear landing beyond its top (<see cref="AshfallStairs.Approach"/> deep, the stair's width,
/// a step over the floor to head height): no other item's box may stand in them, and an item whose stair zone meets a placed box yields (the overlap rule's moves, shrinks, next model, else left out).</item>
/// <item><b>No stacked objects.</b> Two placed models' drawn boxes (the whole model as the eye sees it) may meet by at most <see cref="AshfallOverlap.Tolerance"/>: nothing is drawn on top of or inside another model.
/// Where height is wanted one model is scaled (the pass already scales each structure's one model). Exempt: bridges on their supports, and the stronghold's ring (gate, corners, leg walls: one continuous wall line).</item>
/// </list>
/// </summary>
internal static partial class AshfallDraft5Pass
{
    /// <summary>Least width and least rise of a stair after the placement's scale (ours: the capsule's 0.9 m plus 0.1 m; under 0.8 m the steps are a kerb the motor steps anyway).</summary>
    const float StairMinWidth = 1.0f, StairMinRise = 0.8f;

    /// <summary>Steepest kept stair after the scale (ours: the motor walks 60 degrees; a margin).</summary>
    const float StairMaxDegrees = 50f;

    /// <summary>Longest slice a cut box keeps under a ramp (ours: so shots still meet stepped stone under the movement-only ramp, within the triangle budget).</summary>
    const float CutSlice = 2f;

    /// <summary>Most slices one cut box makes (the level's 220 k collision-triangle budget).</summary>
    const int MaxSlices = 3;

    /// <summary>A placed stair, item-local before the translation: its ramp corners, the boxes after the cut, and its foot and landing zones (boxes in the item's yaw).</summary>
    sealed record StairPlan(List<Vector3[]> Ramps, List<WBox> Boxes, List<WBox> Zones, List<string> Notes, int Kept, int Dropped);

    static readonly Dictionary<(string, int, Vector3, float, bool), StairPlan> PlanCache = [];
    static readonly Lock PlanLock = new();

    /// <summary>The stairs of an item where it stands: <paramref name="boxes"/> are its collision boxes there (world).</summary>
    static StairPlan Stairs(AshfallDressItem item, int yaw, string id, List<WBox> boxes, bool walk = true)
    {
        var key = (id, yaw, item.Size, item.Position.Z, walk);
        StairPlan? local;
        lock (PlanLock) PlanCache.TryGetValue(key, out local);
        if (local is null)
        {
            var at0 = item with { Position = new Vector3(0f, 0f, item.Position.Z) };
            var b0 = boxes.Select(b => b with { Centre = b.Centre - new Vector3(item.Position.X, item.Position.Y, 0f) }).ToList();
            local = PlanStairs(at0, yaw, id, b0, walk);
            lock (PlanLock) PlanCache[key] = local;
        }
        var d = new Vector3(item.Position.X, item.Position.Y, 0f);
        return local with
        {
            Ramps = [.. local.Ramps.Select(r => r.Select(v => v + d).ToArray())],
            Boxes = [.. local.Boxes.Select(b => b with { Centre = b.Centre + d })],
            Zones = [.. local.Zones.Select(b => b with { Centre = b.Centre + d })],
        };
    }

    /// <summary>The stair zones of an item where it stands (for the placement rules before it is emitted).</summary>
    static List<WBox> StairZones(Spec s, AshfallDressItem item, int yaw, string id, List<WBox> boxes) =>
        s.NoCollision || AshfallKitCollision.Stairs(id).Count == 0 ? [] : Stairs(item, yaw, id, boxes, walk: false).Zones;

    /// <param name="walk">false: the geometric rules only (the placement rules' zones while an item is still being settled: a stair the walk will drop later still keeps its zones clear there)</param>
    static StairPlan PlanStairs(AshfallDressItem item, int yaw, string id, List<WBox> boxes, bool walk)
    {
        var notes = new List<string>();
        var ramps = new List<Vector3[]>();
        var zones = new List<WBox>();
        var cur = boxes;
        int kept = 0, dropped = 0;
        var taken = new List<Vector4>();   // local footprints of kept stairs (a stair found twice keeps its widest copy)
        float s = MathF.Sin(item.Yaw), co = MathF.Cos(item.Yaw);
        Vector3 Loc(Vector3 v) { float dx = v.X - item.Position.X, dy = v.Y - item.Position.Y; return new Vector3(co * dx + s * dy, -s * dx + co * dy, v.Z); }
        Vector3 Wor(Vector3 l) => new(item.Position.X + co * l.X - s * l.Y, item.Position.Y + s * l.X + co * l.Y, l.Z);
        var m = AshfallKit.Get(id);
        foreach (var rp0 in AshfallKitCollision.Stairs(id))
        {
            var rp = FootToFloor(item, yaw, id, rp0, cur, m);
            var corners = RampCorners(item, yaw, rp, out bool ok);
            if (!ok) { dropped++; notes.Add("not rising after the scale"); continue; }
            var r = AshfallStairs.Of(new AshfallPart(AshfallKind.Stair, corners), 0, 0);
            if (r is not { } ramp) { dropped++; notes.Add("not a ramp"); continue; }
            float rise = ramp.HighZ - ramp.LowZ;
            if (ramp.Width < StairMinWidth || rise < StairMinRise) { notes.Add($"{(ramp.Width < StairMinWidth ? $"{ramp.Width:0.0} m wide" : $"{rise:0.0} m rise")}: a few steps, no ramp"); continue; }
            if (ramp.Degrees > StairMaxDegrees) { dropped++; notes.Add($"{ramp.Degrees:0} degrees after the scale"); continue; }
            // local footprint and plane
            var lc = corners.Select(Loc).ToList();
            var fp = new Vector4(lc.Min(v => v.X), lc.Min(v => v.Y), lc.Max(v => v.X), lc.Max(v => v.Y));
            if (taken.Any(t => t.X < fp.Z && t.Z > fp.X && t.Y < fp.W && t.W > fp.Y)) continue;
            var lLow = Loc(new Vector3(ramp.Low, 0f)); var lHigh = Loc(new Vector3(ramp.High, 0f));
            bool alongX = MathF.Abs(lHigh.X - lLow.X) > MathF.Abs(lHigh.Y - lLow.Y);
            float a0 = alongX ? lLow.X : lLow.Y, a1 = alongX ? lHigh.X : lHigh.Y;
            float PlaneAt(float a) => ramp.LowZ + (ramp.HighZ - ramp.LowZ) * Math.Clamp((a - a0) / (a1 - a0), 0f, 1f);
            var cut = Cut(cur, item, yaw, fp, alongX, a0, a1, PlaneAt);
            // zones: foot and landing (local), in the item's yaw
            float A = AshfallStairs.Approach, w = ramp.Width;
            float sign = MathF.Sign(a1 - a0);
            WBox Zone(float along, float z)
            {
                float cx = alongX ? along : (fp.X + fp.Z) / 2f, cy = alongX ? (fp.Y + fp.W) / 2f : along;
                var wc = Wor(new Vector3(cx, cy, 0f));
                float z0 = z + AshfallStairs.Step + 0.05f, z1 = z + AshfallStairs.Headroom;
                return new WBox(new Vector3(wc.X, wc.Y, (z0 + z1) / 2f), alongX ? new Vector3(A / 2f, w / 2f, (z1 - z0) / 2f) : new Vector3(w / 2f, A / 2f, (z1 - z0) / 2f), yaw);
            }
            var foot = Zone(a0 - sign * A / 2f, ramp.LowZ);
            var land = Zone(a1 + sign * A / 2f, ramp.HighZ);
            string? why = walk ? WalkCheck(item, cut, corners, ramp) : null;
            if (why is not null) { dropped++; notes.Add(why); continue; }
            cur = cut; ramps.Add(corners); zones.Add(foot); zones.Add(land); taken.Add(fp); kept++;
        }
        return new StairPlan(ramps, cur, zones, notes, kept, dropped);
    }

    /// <summary>The farthest a stair's foot is carried down to the floor in front of it (ours: a tall first riser of a ruin's stair, half a metre in the expansion's two-storey ruin).</summary>
    const float FootDrop = 1.5f;

    /// <summary>
    /// A stair whose foot stands over the floor in front of it (a first riser taller than a step) is carried down to that floor at its own slope, its footprint lengthened toward the foot (ours; the walk check still decides).
    /// The floor is the highest of the item's box tops and its foot within the stair's width there.
    /// </summary>
    static AshfallRamp FootToFloor(AshfallDressItem item, int yaw, string id, AshfallRamp rp, List<WBox> boxes, AshfallKitModel m)
    {
        float sy = item.Size.Z / m.RawSize.Y;
        var corners = RampCorners(item, yaw, rp, out bool ok);
        if (!ok || AshfallStairs.Of(new AshfallPart(AshfallKind.Stair, corners), 0, 0) is not { } r) return rp;
        var solids = new AshfallStairs.Solids(boxes.Select(AsBox)) { Ground = (_, _) => item.Position.Z };
        float floor = solids.FloorUnder(r.Low - r.Dir * 0.3f, r.LowZ);
        float drop = r.LowZ - floor;
        if (drop <= 0.1f || drop > FootDrop) return rp;
        float tan = (r.HighZ - r.LowZ) / MathF.Max(r.Run, 1e-3f);
        if (tan <= 1e-3f) return rp;
        float extend = drop / tan;   // metres along the climb
        // the same in the raw frame along the stair's axis (raw X: item X; raw Z: item -Y)
        bool alongX = rp.Dir < 2;
        float perRaw = alongX ? item.Size.X / m.RawSize.X : item.Size.Y / m.RawSize.Z;
        float e = extend / perRaw, yLow = rp.YLow - drop / sy;
        var mn = rp.Min; var mx = rp.Max;
        switch (rp.Dir)
        {
            case 0: mn.X -= e; break;   // rises toward +X: the foot is at Min.X
            case 1: mx.X += e; break;
            case 2: mn.Z -= e; break;
            default: mx.Z += e; break;
        }
        mn.Y = MathF.Min(mn.Y, yLow);
        return new AshfallRamp(mn, mx, rp.Dir, yLow, rp.YHigh);
    }

    /// <summary>
    /// The item's boxes with every box under a ramp's footprint cut below the ramp: the part outside the footprint keeps its height, the part inside is sliced along the climb (at most <see cref="CutSlice"/> long)
    /// with each slice's top at the ramp's height at its low end. A box standing over the ramp (its bottom above the ramp's top) is left alone (the walk check decides).
    /// </summary>
    static List<WBox> Cut(List<WBox> boxes, AshfallDressItem item, int yaw, Vector4 fp, bool alongX, float a0, float a1, Func<float, float> planeAt)
    {
        float s = MathF.Sin(item.Yaw), co = MathF.Cos(item.Yaw);
        var res = new List<WBox>();
        float planeMax = MathF.Max(planeAt(a0), planeAt(a1));
        foreach (var b in boxes)
        {
            float dx = b.Centre.X - item.Position.X, dy = b.Centre.Y - item.Position.Y;
            float lx = co * dx + s * dy, ly = -s * dx + co * dy;
            float x0 = lx - b.Half.X, x1 = lx + b.Half.X, y0 = ly - b.Half.Y, y1 = ly + b.Half.Y, z0 = b.Centre.Z - b.Half.Z, z1 = b.Centre.Z + b.Half.Z;
            float ix0 = MathF.Max(x0, fp.X), ix1 = MathF.Min(x1, fp.Z), iy0 = MathF.Max(y0, fp.Y), iy1 = MathF.Min(y1, fp.W);
            if (ix1 - ix0 < 0.01f || iy1 - iy0 < 0.01f || z0 >= planeMax - 0.01f) { res.Add(b); continue; }
            float lo = alongX ? ix0 : iy0, hi = alongX ? ix1 : iy1;
            float under = MathF.Min(planeAt(lo), planeAt(hi));
            if (z1 <= under + 0.01f) { res.Add(b); continue; }
            void Add(float ax0, float ax1, float ay0, float ay1, float bz0, float bz1)
            {
                if (ax1 - ax0 < 0.01f || ay1 - ay0 < 0.01f || bz1 - bz0 < 0.01f) return;
                float cx = (ax0 + ax1) / 2f, cy = (ay0 + ay1) / 2f;
                res.Add(new WBox(new Vector3(item.Position.X + co * cx - s * cy, item.Position.Y + s * cx + co * cy, (bz0 + bz1) / 2f), new Vector3((ax1 - ax0) / 2f, (ay1 - ay0) / 2f, (bz1 - bz0) / 2f), yaw));
            }
            // outside the footprint: full height
            const float sliver = 0.05f;   // an outside piece thinner than this is dropped (a fit cell's overlap)
            if (ix0 - x0 > sliver) Add(x0, ix0, y0, y1, z0, z1);
            if (x1 - ix1 > sliver) Add(ix1, x1, y0, y1, z0, z1);
            if (iy0 - y0 > sliver) Add(ix0, ix1, y0, iy0, z0, z1);
            if (y1 - iy1 > sliver) Add(ix0, ix1, iy1, y1, z0, z1);
            // inside: slices along the climb, each under the ramp at its lower end
            int n = Math.Clamp((int)MathF.Ceiling((hi - lo) / CutSlice), 1, MaxSlices);
            for (int k = 0; k < n; k++)
            {
                float t0 = lo + (hi - lo) * k / n, t1 = lo + (hi - lo) * (k + 1) / n;
                float top = MathF.Min(z1, MathF.Min(planeAt(t0), planeAt(t1)));
                if (alongX) Add(t0, t1, iy0, iy1, z0, top); else Add(ix0, ix1, t0, t1, z0, top);
            }
        }
        return res;
    }

    /// <summary>
    /// Walks the stair on the item alone (its boxes, this ramp, the ground under it): from <see cref="AshfallStairs.Approach"/> in front of the foot up onto the landing, then back down past the foot, at run speed;
    /// null when both walks arrive without a stall, else why not.
    /// </summary>
    static string? WalkCheck(AshfallDressItem item, List<WBox> boxes, Vector3[] ramp, AshfallStairs.Ramp r)
    {
        var tris = new List<Vector3>();
        foreach (var b in boxes)
        {
            var (sn, cs) = AshfallBuilder.Trig(b.YawStep);
            tris.AddRange(AshfallBuilder.Frame(b.Centre, new Vector3(cs, sn, 0f), new Vector3(-sn, cs, 0f), Vector3.UnitZ, b.Half.X, b.Half.Y, b.Half.Z));
        }
        tris.AddRange(ramp);
        float g = item.Position.Z, ext = MathF.Max(item.Size.X, item.Size.Y) / 2f + 8f;
        tris.AddRange(AshfallBuilder.Frame(new Vector3(item.Position.X, item.Position.Y, g - 0.5f), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, ext, ext, 0.5f));
        var corners = tris.ToArray();
        var mesh = new StaticMesh(corners, Enumerable.Range(0, corners.Length).ToArray());
        var world = new CollisionWorld(mesh, mesh);
        var solids = new AshfallStairs.Solids(boxes.Select(AsBox)) { Ground = (_, _) => g };
        var cfg = WalkConfig;
        float speed = cfg.RunSpeed / cfg.TickRate;
        // a floor to stand on in front of the foot
        var footAt = r.Low - r.Dir * AshfallStairs.Approach;
        float footFloor = MathF.Max(solids.FloorUnder(footAt, r.LowZ + AshfallStairs.Step), g);
        if (r.LowZ - footFloor > AshfallStairs.Step + 0.05f) return $"its foot stands {r.LowZ - footFloor:0.0} m over the floor in front of it";
        var landAt = r.High + r.Dir * 1.0f;
        float landFloor = MathF.Max(solids.FloorUnder(landAt, r.HighZ + AshfallStairs.Step), g);
        if (MathF.Abs(landFloor - r.HighZ) > AshfallStairs.Step + 0.05f) return $"no landing at its top (the floor beyond it is {landFloor - r.HighZ:+0.0;-0.0} m off; it ends in its own wall or in the air)";
        string? Walk(Vector2 from, float fromZ, Vector2 dir, float distance, Func<PlayerState, bool> arrived, string what)
        {
            var st = new PlayerState(1, new Vector3(from.X, from.Y, fromZ + 0.02f), Vector3.Zero, 0f, true);
            float yawr = MathF.Atan2(-dir.X, dir.Y);
            int ticks = (int)(distance / speed) + 40, slow = 0;
            for (uint t = 0; t < ticks; t++)
            {
                var prev = st.Position;
                st = Movement.Step(st, new PlayerInput(t, 0f, 1f, yawr, InputButtons.None), cfg, world);
                if (arrived(st)) return null;
                if (t > 12 && Vector2.Distance(new(prev.X, prev.Y), new(st.Position.X, st.Position.Y)) < 0.4f * speed && ++slow > 2) return $"the walk {what} stalls at ({st.Position.X - item.Position.X:0.0}, {st.Position.Y - item.Position.Y:0.0}, {st.Position.Z - g:0.0}) on the item";
            }
            return $"the walk {what} does not arrive";
        }
        float run = r.Run + AshfallStairs.Approach + 1f;
        var up = Walk(footAt, footFloor, r.Dir, run, st => st.Grounded && Vector2.Dot(new Vector2(st.Position.X, st.Position.Y) - r.High, r.Dir) > 0.5f && MathF.Abs(st.Position.Z - r.HighZ) < AshfallStairs.Step, "up");
        if (up is not null) return up;
        return Walk(landAt, landFloor, -r.Dir, run, st => st.Grounded && Vector2.Dot(new Vector2(st.Position.X, st.Position.Y) - r.Low, -r.Dir) > 0.5f && st.Position.Z < r.LowZ + AshfallStairs.Step, "down");
    }

    /// <summary>The walker of the stair check: the player's capsule, step and slope limit, run speed (<see cref="SimConfig"/> defaults).</summary>
    internal static readonly SimConfig WalkConfig = new();

    // ---------------------------------------------------------------- the urban rules

    /// <summary>How far loose debris (rubble, collapsed wall pieces) may lie from a building's footprint (ours: a storey and a half; debris falls at the foot of what it fell from).</summary>
    const float DebrisReach = 8f;

    /// <summary>A building of the city: a ruin shell or corner, a facade, a tower, the bunker, a wall over cover height.</summary>
    static bool IsBuilding(string family, (Vector3 Min, Vector3 Max) b) =>
        family is "shell" or "shell2" or "corner" or "facade" or "tower" or "bunker" || (family == "wall" && b.Max.Z - b.Min.Z > LowWallHeight);

    static bool NearBuilding(Ctx c, Vector3 min, Vector3 max)
    {
        foreach (var r in c.Buildings)
        {
            float dx = MathF.Max(MathF.Max(r.X - max.X, min.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - max.Y, min.Y - r.W), 0f);
            if (dx * dx + dy * dy <= DebrisReach * DebrisReach) return true;
        }
        return false;
    }

    /// <summary>The way to the nearest street from a point (the avenues and the perimeter road): a building's front faces it.</summary>
    static Vector2 StreetSide(Vector2 p)
    {
        float[] lines = [-232f, -92f, 92f, 232f];
        Vector2 best = Vector2.UnitY; float dist = float.MaxValue;
        foreach (float v in lines)
        {
            if (MathF.Abs(p.X - v) < dist) { dist = MathF.Abs(p.X - v); best = new Vector2(MathF.Sign(v - p.X), 0f); }
            if (MathF.Abs(p.Y - v) < dist) { dist = MathF.Abs(p.Y - v); best = new Vector2(0f, MathF.Sign(v - p.Y)); }
        }
        return best;
    }

    // ---------------------------------------------------------------- the placement rules: drawn boxes and stair zones

    /// <summary>The model's drawn box where it stands (the whole model, as the eye sees it).</summary>
    static WBox Drawn(AshfallDressItem item, int yaw) => new(new Vector3(item.Position.X, item.Position.Y, item.Position.Z + item.Size.Z / 2f), item.Size / 2f, yaw);

    /// <summary>The stronghold's ring pieces (gate, corners, leg walls): one continuous wall line, exempt from the drawn-box rule among themselves.</summary>
    static bool RingPiece(Spec s) => s.Family is "stronghold-wall" or "stronghold-corner" or "stronghold-gate" || s.Group.Contains(" breach ");

    /// <summary>Whether the drawn-box rule applies to this placement (a Prop with collision; not a bridge, not a backdrop block).</summary>
    static bool DrawnRule(Spec s, string id) => !s.NoCollision && !BridgeIds.Contains(id);

    static void AddCell<T>(Dictionary<(int, int), List<T>> grid, Vector4 r, T value)
    {
        for (int gx = (int)MathF.Floor(r.X / OverlapCell); gx <= (int)MathF.Floor(r.Z / OverlapCell); gx++)
            for (int gy = (int)MathF.Floor(r.Y / OverlapCell); gy <= (int)MathF.Floor(r.W / OverlapCell); gy++)
            {
                if (!grid.TryGetValue((gx, gy), out var l)) grid[(gx, gy)] = l = [];
                l.Add(value);
            }
    }

    static IEnumerable<T> Near<T>(Dictionary<(int, int), List<T>> grid, Vector4 r)
    {
        var seen = new HashSet<(int, int)>();
        for (int gx = (int)MathF.Floor(r.X / OverlapCell); gx <= (int)MathF.Floor(r.Z / OverlapCell); gx++)
            for (int gy = (int)MathF.Floor(r.Y / OverlapCell); gy <= (int)MathF.Floor(r.W / OverlapCell); gy++)
                if (grid.TryGetValue((gx, gy), out var l)) foreach (var v in l) yield return v;
    }

    static bool RectsMeet(Vector4 a, Vector4 b) => a.X < b.Z && b.X < a.Z && a.Y < b.W && b.Y < a.W;

    /// <summary>One conflict of a placement: how far it goes past its tolerance, the box of this item and the other's box, the other item, and the tolerance (for the move).</summary>
    readonly record struct Clash(float Excess, WBox Mine, WBox Other, int Item, float Tol, string What);

    /// <summary>
    /// The worst conflict of a placement with what stands: its boxes against placed boxes (draft 7, <see cref="AshfallOverlap.Tolerance"/>), its drawn box against placed drawn boxes (draft 9, the same tolerance),
    /// its boxes against placed stair zones and its stair zones against placed boxes (draft 9, 2 cm). Excess 0 or less: no conflict.
    /// </summary>
    static Clash Conflict(Ctx c, Spec s, string id, AshfallDressItem item, int yaw, List<WBox> boxes)
    {
        var worst = new Clash(0f, default, default, -1, 0f, "");
        void Try(WBox mine, WBox other, int oi, float tol, string what)
        {
            if (!RectsMeet(mine.Rect, other.Rect)) return;
            float d = AshfallOverlap.Depth(AsBox(mine), AsBox(other));
            if (d - tol > worst.Excess) worst = new Clash(d - tol, mine, other, oi, tol, what);
        }
        var (deep, m0, o0, i0) = Deepest(c, boxes);
        if (deep > 0f) worst = new Clash(deep - AshfallOverlap.Tolerance, m0, o0, i0, AshfallOverlap.Tolerance, "boxes");
        if (!c.Draft9) return worst;
        const float zoneTol = 0.02f;
        foreach (var b in boxes)
        {
            if (Dust(b)) continue;
            foreach (var (oi, z) in Near(c.Zones, b.Rect)) Try(b, z, oi, zoneTol, "a stair's foot or landing");
        }
        foreach (var z in StairZones(s, item, yaw, id, boxes))
            foreach (var (oi, o) in Near(c.Solid, z.Rect)) Try(z, o, oi, zoneTol, "its stair's foot or landing");
        if (DrawnRule(s, id))
        {
            var dr = Drawn(item, yaw);
            bool ring = RingPiece(s);
            foreach (var (oi, o, oring) in Near(c.DrawnBoxes, dr.Rect))
                if (!(ring && oring)) Try(dr, o, oi, AshfallOverlap.Tolerance, "drawn models");
        }
        return worst;
    }

    /// <summary>Registers an emitted item's drawn box and stair zones for the rules of the items after it.</summary>
    static void AddDraft9(Ctx c, Spec s, int index, AshfallDressItem item, int yaw, string id, List<WBox> zones)
    {
        if (DrawnRule(s, id)) { var d = Drawn(item, yaw); AddCell(c.DrawnBoxes, d.Rect, (index, d, RingPiece(s))); }
        foreach (var z in zones) AddCell(c.Zones, z.Rect, (index, z));
    }
}
