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

    /// <summary>
    /// Draft 12 (the city layout only; draft 10 and the field keep <see cref="StairMinRise"/>): a run of steps rising more than the 0.4 m step gets its ramp too. The kit's two-storey ruin (AC-051) stands on a 1.1 m plinth
    /// whose entry steps rise 0.29 and 0.57 m after the scale: without a ramp no player gets in, and its flight to the upper floor starts on that plinth (owner, 2026-11-09: "no stairs inaccessible").
    /// </summary>
    const float CityStairMinRise = 0.4f;

    /// <summary>Set while the city layout's pass runs on this thread (its stair rules: <see cref="CityStairMinRise"/>, the entry ramps).</summary>
    [ThreadStatic] static bool t_city;

    /// <summary>The city's roadway strips while its pass runs (an entry ramp and its foot zone stay off them).</summary>
    [ThreadStatic] static List<AshfallBox>? t_cityRoads;

    /// <summary>
    /// Draft 10 (owner, 2026-11-09: "there are still stairs that cannot be walked up"; ours): a drawn flight narrower than <see cref="StairWalkWidth"/> but at least <see cref="StairWidenFrom"/> wide gets a ramp
    /// <see cref="StairWalkWidth"/> wide about its middle (the capsule's 0.9 m plus 0.3 m), the model's boxes cut under the wider footprint; a narrower run stays a few steps (a kerb, a broken tread).
    /// </summary>
    const float StairWidenFrom = 0.4f, StairWalkWidth = 1.2f;

    /// <summary>Steepest kept stair after the scale (ours: the motor walks 60 degrees; draft 10 keeps a 5 degree margin, draft 9 kept 10).</summary>
    const float StairMaxDegrees = 55f;

    /// <summary>Longest slice a cut box keeps under a ramp (ours: so shots still meet stepped stone under the movement-only ramp, within the triangle budget).</summary>
    const float CutSlice = 2f;

    /// <summary>Most slices one cut box makes (the level's collision-triangle budget, 225 k since draft 10).</summary>
    const int MaxSlices = 3;

    /// <summary>A placed stair, item-local before the translation: its ramp corners, the boxes after the cut, and its foot and landing zones (boxes in the item's yaw).</summary>
    sealed record StairPlan(List<Vector3[]> Ramps, List<WBox> Boxes, List<WBox> Zones, List<string> Notes, int Kept, int Dropped)
    {
        /// <summary>Draft 10: kept stairs whose corridor was carved through the model's own boxes (<see cref="Carve"/>).</summary>
        public int Carved { get; init; }
    }

    static readonly Dictionary<(AshfallDressItem, int, bool), StairPlan> PlanCache = [];
    static readonly Lock PlanLock = new();

    /// <summary>
    /// The stairs of an item where it stands: <paramref name="boxes"/> are its collision boxes there (world). Planned in place (never moved from another position: a translation would round differently),
    /// cached by the exact placement, so every build gives the same bits whatever ran before it.
    /// </summary>
    static StairPlan Stairs(AshfallDressItem item, int yaw, string id, List<WBox> boxes, bool walk = true)
    {
        var key = (item, yaw, walk);
        if (!walk) return PlanStairs(item, yaw, id, boxes, walk);   // a candidate position while an item settles: geometry only, cheap, not kept
        lock (PlanLock) if (PlanCache.TryGetValue(key, out var hit)) return hit;
        var plan = PlanStairs(item, yaw, id, boxes, walk);
        lock (PlanLock) PlanCache[key] = plan;
        return plan;
    }

    /// <summary>The stair zones of an item where it stands (for the placement rules before it is emitted).</summary>
    static List<WBox> StairZones(Spec s, AshfallDressItem item, int yaw, string id, List<WBox> boxes) =>
        s.NoCollision || AshfallKitCollision.Stairs(id).Count == 0 ? [] : Stairs(item, yaw, id, boxes, walk: false).Zones;

    /// <param name="walk">false: the geometric rules only (the placement rules' zones while an item is still being settled: a stair the walk will drop later still keeps its zones clear there)</param>
    static StairPlan PlanStairs(AshfallDressItem item, int yaw, string id, List<WBox> boxes, bool walk)
    {
        // draft 10: once planned, every kept flight is walked again with all the model's other kept ramps present (a later flight may cross an earlier one's landing); one that fails is left out and the
        // model planned again without it, until every kept flight walks (at most a few rounds: each round leaves one out or ends)
        var skip = new HashSet<int>();
        var extra = new List<string>();
        while (true)
        {
            var (plan, idx) = PlanStairsOnce(item, yaw, id, boxes, walk, skip);
            if (!walk || plan.Kept < 2) return extra.Count == 0 ? plan : plan with { Notes = [.. plan.Notes, .. extra], Dropped = plan.Dropped + extra.Count };
            int bad = -1;
            for (int k = 0; k < plan.Ramps.Count && bad < 0; k++)
            {
                if (AshfallStairs.Of(new AshfallPart(AshfallKind.Stair, plan.Ramps[k]), 0, 0) is not { } r) continue;
                var others = plan.Ramps.Where((_, j) => j != k).ToList();
                if (Check(item, plan.Boxes, plan.Ramps[k], r, others) is { } why) { bad = idx[k]; extra.Add($"{why} (with the model's other flights)"); }
            }
            if (bad < 0) return extra.Count == 0 ? plan : plan with { Notes = [.. plan.Notes, .. extra], Dropped = plan.Dropped + extra.Count };
            skip.Add(bad);
        }
    }

    static (StairPlan Plan, List<int> Index) PlanStairsOnce(AshfallDressItem item, int yaw, string id, List<WBox> boxes, bool walk, HashSet<int> skip)
    {
        var index = new List<int>();
        var notes = new List<string>();
        var ramps = new List<Vector3[]>();
        var zones = new List<WBox>();
        var cur = boxes;
        int kept = 0, dropped = 0, carvedCount = 0;
        var taken = new List<Vector4>();   // local footprints of kept stairs (a stair found twice keeps its first copy: the detector lists the widest first)
        var (s, co) = AshfallBuilder.Trig(yaw);   // the exact turn table (the same bits on every machine)
        Vector3 Loc(Vector3 v) { float dx = v.X - item.Position.X, dy = v.Y - item.Position.Y; return new Vector3(co * dx + s * dy, -s * dx + co * dy, v.Z); }
        Vector3 Wor(Vector3 l) => new(item.Position.X + co * l.X - s * l.Y, item.Position.Y + s * l.X + co * l.Y, l.Z);
        var m = AshfallKit.Get(id);
        var found = AshfallKitCollision.Stairs(id);
        for (int si = 0; si < found.Count; si++)
        {
            if (skip.Contains(si)) continue;
            var rp0 = found[si];
            var rp = FootToFloor(item, yaw, id, Widen(item, rp0, m), cur, m);
            var corners = RampCorners(item, yaw, rp, out bool ok);
            if (!ok) { dropped++; notes.Add("not rising after the scale"); continue; }
            var r = AshfallStairs.Of(new AshfallPart(AshfallKind.Stair, corners), 0, 0);
            if (r is not { } ramp) { dropped++; notes.Add("not a ramp"); continue; }
            float rise = ramp.HighZ - ramp.LowZ;
            if (ramp.Width < StairMinWidth || rise < (t_city ? CityStairMinRise : StairMinRise)) { notes.Add($"{(ramp.Width < StairMinWidth ? $"{ramp.Width:0.0} m wide" : $"{rise:0.0} m rise")}: a few steps, no ramp"); continue; }
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
            // zones: foot and landing (local), in the item's yaw; draft 10: the foot's zone is FootApproach deep, both are the stair's width plus SideMargin a side
            float w = ramp.Width + 2f * AshfallStairs.SideMargin;
            float sign = MathF.Sign(a1 - a0);
            WBox Zone(float along, float A, float z)
            {
                float cx = alongX ? along : (fp.X + fp.Z) / 2f, cy = alongX ? (fp.Y + fp.W) / 2f : along;
                var wc = Wor(new Vector3(cx, cy, 0f));
                float z0 = z + AshfallStairs.Step + 0.05f, z1 = z + AshfallStairs.Headroom;
                return new WBox(new Vector3(wc.X, wc.Y, (z0 + z1) / 2f), alongX ? new Vector3(A / 2f, w / 2f, (z1 - z0) / 2f) : new Vector3(w / 2f, A / 2f, (z1 - z0) / 2f), yaw);
            }
            var foot = Zone(a0 - sign * AshfallStairs.FootApproach / 2f, AshfallStairs.FootApproach, ramp.LowZ);
            var land = Zone(a1 + sign * AshfallStairs.Approach / 2f, AshfallStairs.Approach, ramp.HighZ);
            // draft 10: the space at the foot is cleared of the model's own boxes too (Approach deep, the flight's width, from a step over the foot to head height; other items keep out of FootApproach and SideMargin a side)
            {
                float depth = AshfallStairs.Approach;   // the model's own wall standing at the depth or beyond stays (the measure stops 5 cm short of it)
                float f0 = MathF.Min(a0, a0 - sign * depth), f1 = MathF.Max(a0, a0 - sign * depth);
                var rect = alongX ? new Vector4(f0, fp.Y, f1, fp.W) : new Vector4(fp.X, f0, fp.Z, f1);   // the flight's width only (a wall it runs along keeps its face)
                cut = ClearBand(cut, item, yaw, rect, ramp.LowZ + AshfallStairs.Step, ramp.LowZ + AshfallStairs.CorridorHeadroom);
            }
            string? why = walk ? Check(item, cut, corners, ramp, ramps) : null;
            if (why is not null)
            {
                // draft 10: the model's own boxes in the stair's corridor (its width, from the foot's approach to the landing's) carved from the ramp to head height, then walked again
                var carved = Carve(cut, item, yaw, fp, alongX, a0, a1, PlaneAt);
                if (Check(item, carved, corners, ramp, ramps) is null) { cut = carved; why = null; carvedCount++; }
            }
            if (why is not null) { dropped++; notes.Add(why); continue; }
            cur = cut; ramps.Add(corners); zones.Add(foot); zones.Add(land); taken.Add(fp); kept++; index.Add(si);
            // draft 12 (the city only): a flight that starts on a raised floor gets an entry ramp up onto that floor from its door
            if (t_city && walk && ramp.LowZ > item.Position.Z + AshfallStairs.Step + 0.05f)
            {
                if (EntryRamp(item, yaw, rp, ramp, cur, ramps) is { } entry) { cur = entry.Boxes; ramps.Add(entry.Corners); zones.Add(entry.Foot); zones.Add(entry.Land); notes.Add(entry.Note); index.Add(si); }   // the entry goes with its flight (a re-walk that fails leaves both out)
                else notes.Add($"its foot stands {ramp.LowZ - item.Position.Z:0.0} m over the floor in front of it (draft 12: no entry ramp onto its raised floor fits here)");   // the stair-access rule then tries the pool's next model
            }
        }
        if (kept > 0) cur = Merge(cur, item, yaw);
        return (new StairPlan(ramps, cur, zones, notes, kept, dropped) { Carved = carvedCount }, index);
    }

    /// <summary>
    /// Draft 10: the item's boxes after the cuts with exact neighbours fused (the triangle budget): two boxes in the item's yaw that share their extent on two axes and touch or overlap on the third become one.
    /// The union is exact (no volume added or lost). Order-stable: the list is scanned in its order and a merged box takes the first one's place.
    /// </summary>
    static List<WBox> Merge(List<WBox> boxes, AshfallDressItem item, int yaw)
    {
        var (s, co) = AshfallBuilder.Trig(yaw);
        const float eps = 1e-4f;
        var loc = new List<(float X0, float X1, float Y0, float Y1, float Z0, float Z1)?>();
        var other = new List<WBox>();
        foreach (var b in boxes)
        {
            if (b.YawStep != yaw) { other.Add(b); continue; }
            float dx = b.Centre.X - item.Position.X, dy = b.Centre.Y - item.Position.Y;
            float lx = co * dx + s * dy, ly = -s * dx + co * dy;
            loc.Add((lx - b.Half.X, lx + b.Half.X, ly - b.Half.Y, ly + b.Half.Y, b.Centre.Z - b.Half.Z, b.Centre.Z + b.Half.Z));
        }
        static bool Eq(float a, float b) => MathF.Abs(a - b) <= eps;
        static bool Join(float a0, float a1, float b0, float b1) => a0 <= b1 + eps && b0 <= a1 + eps;
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < loc.Count; i++)
            {
                if (loc[i] is not { } a) continue;
                for (int j = i + 1; j < loc.Count; j++)
                {
                    if (loc[j] is not { } b) continue;
                    bool sx = Eq(a.X0, b.X0) && Eq(a.X1, b.X1), sy = Eq(a.Y0, b.Y0) && Eq(a.Y1, b.Y1), sz = Eq(a.Z0, b.Z0) && Eq(a.Z1, b.Z1);
                    bool ok = (sx && sy && Join(a.Z0, a.Z1, b.Z0, b.Z1)) || (sx && sz && Join(a.Y0, a.Y1, b.Y0, b.Y1)) || (sy && sz && Join(a.X0, a.X1, b.X0, b.X1));
                    if (!ok) continue;
                    a = (MathF.Min(a.X0, b.X0), MathF.Max(a.X1, b.X1), MathF.Min(a.Y0, b.Y0), MathF.Max(a.Y1, b.Y1), MathF.Min(a.Z0, b.Z0), MathF.Max(a.Z1, b.Z1));
                    loc[i] = a; loc[j] = null; changed = true;
                }
            }
        }
        var res = new List<WBox>();
        foreach (var l in loc)
        {
            if (l is not { } a) continue;
            float cx = (a.X0 + a.X1) / 2f, cy = (a.Y0 + a.Y1) / 2f;
            res.Add(new WBox(new Vector3(item.Position.X + co * cx - s * cy, item.Position.Y + s * cx + co * cy, (a.Z0 + a.Z1) / 2f), new Vector3((a.X1 - a.X0) / 2f, (a.Y1 - a.Y0) / 2f, (a.Z1 - a.Z0) / 2f), yaw));
        }
        res.AddRange(other);
        return res;
    }

    /// <summary>Draft 10: the item's boxes with the band [<paramref name="zLo"/>, <paramref name="zHi"/>] cleared inside a local plan rectangle (x0, y0, x1, y1); a crossing box keeps its parts outside the rectangle, under and over the band.</summary>
    static List<WBox> ClearBand(List<WBox> boxes, AshfallDressItem item, int yaw, Vector4 rect, float zLo, float zHi)
    {
        var (s, co) = AshfallBuilder.Trig(yaw);   // the exact turn table (the same bits on every machine)
        var res = new List<WBox>();
        foreach (var b in boxes)
        {
            float dx = b.Centre.X - item.Position.X, dy = b.Centre.Y - item.Position.Y;
            float lx = co * dx + s * dy, ly = -s * dx + co * dy;
            float x0 = lx - b.Half.X, x1 = lx + b.Half.X, y0 = ly - b.Half.Y, y1 = ly + b.Half.Y, z0 = b.Centre.Z - b.Half.Z, z1 = b.Centre.Z + b.Half.Z;
            float ix0 = MathF.Max(x0, rect.X), ix1 = MathF.Min(x1, rect.Z), iy0 = MathF.Max(y0, rect.Y), iy1 = MathF.Min(y1, rect.W);
            if (b.YawStep != yaw || ix1 - ix0 < 0.01f || iy1 - iy0 < 0.01f || z1 <= zLo + 0.01f || z0 >= zHi - 0.01f) { res.Add(b); continue; }
            void Add(float ax0, float ax1, float ay0, float ay1, float bz0, float bz1)
            {
                if (ax1 - ax0 < 0.01f || ay1 - ay0 < 0.01f || bz1 - bz0 < 0.01f) return;
                float cx = (ax0 + ax1) / 2f, cy = (ay0 + ay1) / 2f;
                res.Add(new WBox(new Vector3(item.Position.X + co * cx - s * cy, item.Position.Y + s * cx + co * cy, (bz0 + bz1) / 2f), new Vector3((ax1 - ax0) / 2f, (ay1 - ay0) / 2f, (bz1 - bz0) / 2f), yaw));
            }
            const float sliver = 0.05f;
            if (ix0 - x0 > sliver) Add(x0, ix0, y0, y1, z0, z1);
            if (x1 - ix1 > sliver) Add(ix1, x1, y0, y1, z0, z1);
            if (iy0 - y0 > sliver) Add(ix0, ix1, y0, iy0, z0, z1);
            if (y1 - iy1 > sliver) Add(ix0, ix1, iy1, y1, z0, z1);
            if (z0 < zLo) Add(ix0, ix1, iy0, iy1, z0, MathF.Min(z1, zLo));
            if (z1 > zHi) Add(ix0, ix1, iy0, iy1, MathF.Max(z0, zHi), z1);
        }
        return res;
    }

    /// <summary>
    /// Draft 10: whether a stair note names a visible flight a player cannot use where the model stands (no floor at its foot or top, not open, too steep, the walk stalls or lifts): the stair-access rule refuses
    /// such a model. A run of a few steps (under 0.8 m rise or 0.4 m wide) and steps that do not rise after the scale are not flights.
    /// </summary>
    static bool NoAccess(string note) =>
        note.Contains("foot stands") || note.Contains("no landing") || note.Contains("is open") || note.Contains("degrees after") || note.Contains("stalls") || note.Contains("lifts") || note.Contains("does not arrive");

    /// <summary>How far a flight's foot may lie from the ground or from a reached flight's top and still count as reached (ours: a step and a half; a landing between flights is a step or two of floor).</summary>
    const float ReachTolerance = 0.6f;

    /// <summary>A foot this high over the model's base still stands on its ground floor (ours: <see cref="FootDrop"/> plus 0.5 m; the ruins' raised floors, which the walk check stands on in front of the foot).</summary>
    const float GroundBand = FootDrop + 0.5f;

    /// <summary>
    /// Draft 10: the kept flights of a plan that a player on foot cannot reach: reached when the foot stands within <see cref="GroundBand"/> of the model's base (<paramref name="ground"/>) or within <see cref="ReachTolerance"/> of the top of a reached
    /// flight of the same model (in rounds until nothing changes). One note per unreached flight (a stair on a roof the model's stairs never lead to).
    /// </summary>
    static List<string> Unreached(StairPlan plan, float ground)
    {
        var ramps = plan.Ramps.Select(r => AshfallStairs.Of(new AshfallPart(AshfallKind.Stair, r), 0, 0)).Where(r => r is not null).Select(r => r!.Value).ToList();
        var reached = new bool[ramps.Count];
        bool more = true;
        while (more)
        {
            more = false;
            for (int i = 0; i < ramps.Count; i++)
            {
                if (reached[i]) continue;
                bool ok = ramps[i].LowZ <= ground + GroundBand;
                for (int j = 0; j < ramps.Count && !ok; j++) ok = reached[j] && MathF.Abs(ramps[i].LowZ - ramps[j].HighZ) <= ReachTolerance;
                if (ok) { reached[i] = true; more = true; }
            }
        }
        return [.. Enumerable.Range(0, ramps.Count).Where(i => !reached[i]).Select(i => $"a flight from {ramps[i].LowZ - ground:0.0} m that no flight or the ground leads to")];
    }

    /// <summary>Draft 10: a flight narrower than <see cref="StairWalkWidth"/> (and at least <see cref="StairWidenFrom"/>) widened to it about its middle, in the raw frame (raw X: item X; raw Z: item -Y).</summary>
    static AshfallRamp Widen(AshfallDressItem item, AshfallRamp rp, AshfallKitModel m)
    {
        bool alongX = rp.Dir < 2;
        float perRaw = alongX ? item.Size.Y / m.RawSize.Z : item.Size.X / m.RawSize.X;   // metres a raw unit across the climb
        if (perRaw <= 1e-6f) return rp;
        float w = (alongX ? rp.Max.Z - rp.Min.Z : rp.Max.X - rp.Min.X) * perRaw;
        if (w >= StairWalkWidth || w < StairWidenFrom) return rp;
        float e = (StairWalkWidth - w) / 2f / perRaw;
        var mn = rp.Min; var mx = rp.Max;
        if (alongX) { mn.Z -= e; mx.Z += e; } else { mn.X -= e; mx.X += e; }
        return new AshfallRamp(mn, mx, rp.Dir, rp.YLow, rp.YHigh);
    }

    /// <summary>
    /// Draft 10: the stair's corridor cleared through the item's own boxes. In the footprint (the flight's width) lengthened in front of the foot and beyond the landing
    /// (draft 10: <see cref="AshfallStairs.Approach"/> at both ends), nothing stands from the ramp's plane (the foot's height in front, the landing's beyond) to <see cref="AshfallStairs.CorridorHeadroom"/> over it. A box crossing that band
    /// keeps its parts under the plane and over the band (in slices along the climb, at most <see cref="MaxSlices"/>); its parts outside the corridor keep their height.
    /// </summary>
    static List<WBox> Carve(List<WBox> boxes, AshfallDressItem item, int yaw, Vector4 fp, bool alongX, float a0, float a1, Func<float, float> planeAt)
    {
        var (s, co) = AshfallBuilder.Trig(yaw);   // the exact turn table (the same bits on every machine)
        float sign = MathF.Sign(a1 - a0), H = AshfallStairs.CorridorHeadroom;
        float e0 = a0 - sign * AshfallStairs.Approach, e1 = a1 + sign * AshfallStairs.Approach;
        float lo = MathF.Min(e0, e1), hi = MathF.Max(e0, e1);
        var cor = alongX ? new Vector4(lo, fp.Y, hi, fp.W) : new Vector4(fp.X, lo, fp.Z, hi);   // the flight's width only: a wall it runs along keeps its face (no slot through it)
        var res = new List<WBox>();
        foreach (var b in boxes)
        {
            float dx = b.Centre.X - item.Position.X, dy = b.Centre.Y - item.Position.Y;
            float lx = co * dx + s * dy, ly = -s * dx + co * dy;
            float x0 = lx - b.Half.X, x1 = lx + b.Half.X, y0 = ly - b.Half.Y, y1 = ly + b.Half.Y, z0 = b.Centre.Z - b.Half.Z, z1 = b.Centre.Z + b.Half.Z;
            float ix0 = MathF.Max(x0, cor.X), ix1 = MathF.Min(x1, cor.Z), iy0 = MathF.Max(y0, cor.Y), iy1 = MathF.Min(y1, cor.W);
            if (ix1 - ix0 < 0.01f || iy1 - iy0 < 0.01f) { res.Add(b); continue; }
            float blo = alongX ? ix0 : iy0, bhi = alongX ? ix1 : iy1;
            float pMin = MathF.Min(planeAt(blo), planeAt(bhi)), pMax = MathF.Max(planeAt(blo), planeAt(bhi));
            if (z1 <= pMin + 0.01f || z0 >= pMax + H - 0.01f) { res.Add(b); continue; }
            void Add(float ax0, float ax1, float ay0, float ay1, float bz0, float bz1)
            {
                if (ax1 - ax0 < 0.01f || ay1 - ay0 < 0.01f || bz1 - bz0 < 0.01f) return;
                float cx = (ax0 + ax1) / 2f, cy = (ay0 + ay1) / 2f;
                res.Add(new WBox(new Vector3(item.Position.X + co * cx - s * cy, item.Position.Y + s * cx + co * cy, (bz0 + bz1) / 2f), new Vector3((ax1 - ax0) / 2f, (ay1 - ay0) / 2f, (bz1 - bz0) / 2f), yaw));
            }
            const float sliver = 0.05f;   // an outside piece thinner than this is dropped (a fit cell's overlap)
            if (ix0 - x0 > sliver) Add(x0, ix0, y0, y1, z0, z1);
            if (x1 - ix1 > sliver) Add(ix1, x1, y0, y1, z0, z1);
            if (iy0 - y0 > sliver) Add(ix0, ix1, y0, iy0, z0, z1);
            if (y1 - iy1 > sliver) Add(ix0, ix1, iy1, y1, z0, z1);
            // inside: under the band, one piece in front of the foot, one under the flight (its top the plane at the flight's low end: the ramp carries walkers, the triangle budget) and one under the landing;
            // over the band, one piece
            float rLo = MathF.Min(a0, a1), rHi = MathF.Max(a0, a1);
            foreach (var (t0, t1) in new[] { (blo, MathF.Min(bhi, rLo)), (MathF.Max(blo, rLo), MathF.Min(bhi, rHi)), (MathF.Max(blo, rHi), bhi) })
            {
                if (t1 - t0 < 0.01f) continue;
                float under = MathF.Min(planeAt(t0), planeAt(t1));
                if (z0 >= under) continue;
                if (alongX) Add(t0, t1, iy0, iy1, z0, MathF.Min(z1, under)); else Add(ix0, ix1, t0, t1, z0, MathF.Min(z1, under));
            }
            if (z1 > pMax + H) Add(ix0, ix1, iy0, iy1, MathF.Max(z0, pMax + H), z1);
        }
        return res;
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
        // draft 10: the lower of the floor just in front and the floor where the walker starts (a plinth's lip in front of a platform's stair is not its floor)
        float floor = MathF.Min(solids.FloorUnder(r.Low - r.Dir * 0.3f, r.LowZ), solids.FloorUnder(r.Low - r.Dir * AshfallStairs.Approach, r.LowZ));
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
        var (s, co) = AshfallBuilder.Trig(yaw);   // the exact turn table (the same bits on every machine)
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
                if (z0 >= top - 0.01f) top = z1;   // a box over this slice of the ramp (a beam, a deck): left whole, never deleted (the walk check decides)
                if (alongX) Add(t0, t1, iy0, iy1, z0, top); else Add(ix0, ix1, t0, t1, z0, top);
            }
        }
        return res;
    }

    /// <summary>
    /// Draft 10: the keep test of a stair on the item alone: its foot (<see cref="AshfallStairs.Approach"/>) and landing (<see cref="AshfallStairs.LandingDepth"/>) open on the item's own boxes (<see cref="AshfallStairs.Measure"/>,
    /// what the level's check measures; other items keep out of the zones), then the walk up and down (<see cref="WalkCheck"/>). Null when kept, else why not.
    /// </summary>
    static string? Check(AshfallDressItem item, List<WBox> boxes, Vector3[] ramp, AshfallStairs.Ramp r, List<Vector3[]> kept)
    {
        var solids = new AshfallStairs.Solids(boxes.Select(AsBox)) { Ground = (_, _) => item.Position.Z };
        var foot = AshfallStairs.Measure(r, solids, top: false);
        if (!foot.Open) return $"its foot is open {foot.ClearRun:0.0} m of {foot.Need:0.0}";
        var top = AshfallStairs.Measure(r, solids, top: true);
        if (!top.Open) return $"its landing is open {top.ClearRun:0.0} m of {top.Need:0.0}";
        return WalkCheck(item, boxes, ramp, r, kept);
    }

    /// <summary>
    /// Walks the stair on the item alone (its boxes, this ramp, the ground under it): from <see cref="AshfallStairs.Approach"/> in front of the foot up onto the landing, then back down past the foot, at run speed;
    /// null when both walks arrive without a stall, else why not.
    /// </summary>
    static string? WalkCheck(AshfallDressItem item, List<WBox> boxes, Vector3[] ramp, AshfallStairs.Ramp r, List<Vector3[]> kept)
    {
        var tris = new List<Vector3>();
        foreach (var b in boxes)
        {
            var (sn, cs) = AshfallBuilder.Trig(b.YawStep);
            tris.AddRange(AshfallBuilder.Frame(b.Centre, new Vector3(cs, sn, 0f), new Vector3(-sn, cs, 0f), Vector3.UnitZ, b.Half.X, b.Half.Y, b.Half.Z));
        }
        tris.AddRange(ramp);
        foreach (var k in kept) tris.AddRange(k);   // draft 10: the item's stairs kept before this one (a flight walked from the last one's landing meets its ramp, as in the level)
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
                // draft 10: smooth, as the level's check walks it: no lift of more than a step in one tick
                if (st.Position.Z - prev.Z > AshfallStairs.Step + 0.05f) return $"the walk {what} lifts {st.Position.Z - prev.Z:0.00} m in one tick at ({st.Position.X - item.Position.X:0.0}, {st.Position.Y - item.Position.Y:0.0}) on the item";
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

    /// <summary>A ramp's bounding box in its item's yaw (world).</summary>
    static WBox RampBox(AshfallDressItem item, int yaw, Vector3[] corners)
    {
        var (s, co) = AshfallBuilder.Trig(yaw);   // the exact turn table (the same bits on every machine)
        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        foreach (var v in corners)
        {
            float dx = v.X - item.Position.X, dy = v.Y - item.Position.Y, lx = co * dx + s * dy, ly = -s * dx + co * dy;
            x0 = MathF.Min(x0, lx); x1 = MathF.Max(x1, lx); y0 = MathF.Min(y0, ly); y1 = MathF.Max(y1, ly); z0 = MathF.Min(z0, v.Z); z1 = MathF.Max(z1, v.Z);
        }
        float cx = (x0 + x1) / 2f, cy = (y0 + y1) / 2f;
        return new WBox(new Vector3(item.Position.X + co * cx - s * cy, item.Position.Y + s * cx + co * cy, (z0 + z1) / 2f), new Vector3((x1 - x0) / 2f, (y1 - y0) / 2f, (z1 - z0) / 2f), yaw);
    }

    // ---------------------------------------------------------------- the rubble props

    /// <summary>The look lead's separate rubble props (AR-001..004; owner: "rubble as separate small props, not fused into buildings").</summary>
    internal static readonly string[] RubblePropIds = ["AR-001", "AR-002", "AR-003"];   // AR-004 is catalogued but fails the collision gate (report only)

    /// <summary>Fewest metres between two rubble props, and the most placed (ours: a pile every few houses, not a carpet).</summary>
    const float RubbleSpacing = 10f;
    const int RubbleTarget = 60;   // the level's collision-triangle budget, 225 k since draft 10: three slabs a pile

    /// <summary>
    /// Draft 9 (late batch F): scatters the rubble props as low cover and dressing at the feet of the standing walls and buildings (the stronghold's legs included, so beside its breaches) and along the
    /// plaza's edge and the avenues' kerbs. Each candidate (seeded order) stands on the ground, outside every protected place (lanes, the paved road, the plaza's axes, start areas, capture zones plus 6 m, spawns,
    /// the boundary's 2 m, the breaches), off every roadway, meets no placed box, drawn model or stair zone beyond the rules' tolerances, and leaves no gap under 2.4 m (it touches the wall it leans on or stands clear),
    /// at least <see cref="RubbleSpacing"/> m from another rubble prop.
    /// </summary>
    static void RubbleProps(Ctx c)
    {
        if (!c.Draft9) return;
        var cands = new List<(uint Key, Vector2 At, Vector2 Out, int Yaw)>();
        uint n = 0;
        void Add(Vector2 at, Vector2 outward) => cands.Add((AshfallBuilder.Mix(c.Seed ^ 0xA2B1u, n++), at, outward, MathF.Abs(outward.X) > 0.5f ? 6 : 0));
        // wall and building feet: along each standing piece's drawn box, every 6 m, on its four sides
        foreach (var l in c.Log.ToList())
        {
            if (l.Item < 0 || !(IsBuildingFamily(l.Family, c.Items[l.Item].Size.Z) || l.Family is "wall" or "wallrun" or "stronghold-wall" or "stronghold-corner")) continue;
            var it = c.Items[l.Item];
            int ys = ((int)MathF.Round(it.Yaw / (MathF.PI / 12f)) % 24 + 24) % 24;
            if (ys % 6 != 0) continue;
            var (sn, cs) = AshfallBuilder.Trig(ys);
            var ax = new Vector2(cs, sn); var ay = new Vector2(-sn, cs);
            var p = new Vector2(it.Position.X, it.Position.Y);
            foreach (var (normal, along, half, len) in new[] { (ay, ax, it.Size.Y / 2f, it.Size.X), (-ay, ax, it.Size.Y / 2f, it.Size.X), (ax, ay, it.Size.X / 2f, it.Size.Y), (-ax, ay, it.Size.X / 2f, it.Size.Y) })
                for (float t = -len / 2f + 3f; t <= len / 2f - 3f; t += 6f) Add(p + normal * half + along * t, normal);
        }
        // draft 11: the corridors' edges (outside the corridor and its margin), in place of the plaza's edge and the avenues' kerbs
        if (c.Field is { } fl)
            foreach (var k in fl.Corridors)
            {
                var dir = Vector2.Normalize(k.B - k.A); var nrm = new Vector2(-dir.Y, dir.X);
                for (float t = 8f; t <= k.Length - 8f; t += 8f)
                    foreach (float side in new[] { -1f, 1f })
                    {
                        var o = nrm * side;
                        Add(k.A + dir * t + o * (k.Width / 2f + AshfallRoadway.Margin), MathF.Abs(o.X) >= MathF.Abs(o.Y) ? new Vector2(MathF.Sign(o.X), 0f) : new Vector2(0f, MathF.Sign(o.Y)));
                    }
            }
        // the plaza's edge (on the block side of it) and the avenues' kerbs (on the blocks)
        if (c.Field is null)
        for (float t = -56f; t <= 56f; t += 8f)
            foreach (var (pt, o) in new[] { (new Vector2(t, 60f), Vector2.UnitY), (new Vector2(t, -60f), -Vector2.UnitY), (new Vector2(60f, t), Vector2.UnitX), (new Vector2(-60f, t), -Vector2.UnitX) }) Add(pt, o);
        if (c.Field is null)
        foreach (float v in new[] { -92f, 92f })
            for (float t = -220f; t <= 220f; t += 8f)
                foreach (float side in new[] { -1f, 1f })
                {
                    float kerb = v + side * (12f + AshfallRoadway.Margin);
                    Add(new Vector2(kerb, t), new Vector2(side, 0f));
                    Add(new Vector2(t, kerb), new Vector2(0f, side));
                }
        cands.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.At.X != b.At.X ? a.At.X.CompareTo(b.At.X) : a.At.Y.CompareTo(b.At.Y));
        var placed = new List<Vector2>();
        int tried = 0;
        foreach (var (key, at0, outward, yaw) in cands)
        {
            if (placed.Count >= RubbleTarget) break;
            if (placed.Any(q => Vector2.DistanceSquared(q, at0) < RubbleSpacing * RubbleSpacing)) continue;
            tried++;
            string id = RubblePropIds[(int)(key >> 7) % RubblePropIds.Length];
            var m = AshfallKit.Get(id);
            var size = new Vector3(m.RawSize.X, m.RawSize.Z, m.RawSize.Y);
            float halfOut = size.Y / 2f;   // the pile lies with its long side along the edge, its depth across it
            foreach (float push in new[] { -0.25f, 0f, 0.3f, 1.2f, 2.6f })
            {
                var at = at0 + outward * (halfOut + push);
                float ground = AshfallMap.SurfaceAt(at.X, at.Y);
                var item = new AshfallDressItem(id, new Vector3(at.X, at.Y, ground), yaw * (MathF.PI / 12f), size, false, AshfallDressRole.Prop, -1);
                var spec = RubbleSpec with { Group = $"rubble prop {placed.Count}", Min = new Vector3(at.X - size.X / 2f, at.Y - size.Y / 2f, ground), Max = new Vector3(at.X + size.X / 2f, at.Y + size.Y / 2f, ground + size.Z) };
                var boxes = CoarseBoxes(spec, item, yaw, id);
                if (boxes.Count == 0 || !Level(Drawn(item, yaw).Rect, ground)) continue;
                if (boxes.Any(b => Banned(c, b)) || !OffRoad(c, boxes)) continue;
                var r = Drawn(item, yaw).Rect;
                if (c.BreachRects.Any(z => r.X < z.Z && r.Z > z.X && r.Y < z.W && r.W > z.Y)) continue;
                if (Conflict(c, spec, id, item, yaw, boxes).Excess > 0f || CountNarrow(c, boxes) > 0) continue;
                Emit(c, spec, item, yaw, id, boxes, "placed, draft 9 rubble prop (" + (push < 0.5f ? "at a wall's foot or an edge" : "beside an edge") + ")", at);
                placed.Add(at0);
                break;
            }
        }
        c.Flags.Add($"draft 9 rubble props (AR-001..003, separate small piles; AR-004 fails the collision gate): {placed.Count} placed at wall feet, {(c.Field is null ? "the plaza's edge and the avenues' kerbs" : "the army corridors' edges")} ({tried} candidates tried; target {RubbleTarget}, {RubbleSpacing:0} m apart)");
    }

    static readonly Spec RubbleSpec = new("rubble prop", "rubble prop", Vector3.Zero, Vector3.Zero, null, RubblePropIds) { Kind = AshfallKind.Rubble, UseNet = false };

    // ---------------------------------------------------------------- the urban rules

    /// <summary>How far loose debris (rubble, collapsed wall pieces) may lie from a standing building's drawn box (its graybox structure's bounds to the building's) (ours: a storey and a half; debris falls at the foot of what it fell from).</summary>
    const float DebrisReach = 8f;

    /// <summary>A building of the city: a ruin shell or corner, a facade, a tower, the bunker, a wall over cover height.</summary>
    static bool IsBuildingFamily(string family, float height) =>
        family is "shell" or "shell2" or "corner" or "facade" or "tower" or "bunker" || (family == "wall" && height > LowWallHeight);

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
        if (!s.NoCollision && AshfallKitCollision.Stairs(id).Count > 0)
        {
            var plan = Stairs(item, yaw, id, boxes, walk: false);
            foreach (var z in plan.Zones)
                foreach (var (oi, o) in Near(c.Solid, z.Rect)) Try(z, o, oi, zoneTol, "its stair's foot or landing");
            // a ramp carried down past its model must not run into what stands
            foreach (var rc in plan.Ramps)
            {
                var rb = RampBox(item, yaw, rc);
                foreach (var (oi, o) in Near(c.Solid, rb.Rect)) Try(rb, o, oi, AshfallOverlap.Tolerance, "its stair's ramp");
            }
        }
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
