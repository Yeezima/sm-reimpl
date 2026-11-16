using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Draft 12's stair-access check (owner, 2026-11-09: "no stairs inaccessible or blocked by walls or other objects"; docs/musou-ashfall-graybox.md "Draft 12 city layout"), on the final collision of any layout: every placed
/// stair (a kit model's ramp, <see cref="AshfallStairs.All"/>) must be reachable at both ends. Its foot and its landing are open (<see cref="AshfallStairs.Measure(AshfallStairs.Ramp, AshfallStairs.Solids, bool, float)"/>
/// on every solid); a foot on the ground is reached from the streets (the grid's walkable ground joined to the Imperial base's assembly area, then the player's capsule walked with the game's movement from the last open
/// ground, clearance 1 m, to the foot); a foot on an upper floor is reached from the landing of a reachable flight on that floor (walked); and the flight itself is walked up to its landing and back down on the whole
/// level's collision (nothing of another item in the way). Ours (placeholder demonstration).
/// </summary>
public static class AshfallStairAccess
{
    /// <summary>The grid clearance the streets' flood keeps (m; the grid's clearance is conservative by half a cell, so 0.3 m passes a 1.1 to 1.6 m opening; the capsule's walk decides at the stair).</summary>
    public const float FloodClearance = 0.3f;

    /// <summary>Ground with this much clearance is open ground (m; where the capsule's walk to a stair's foot starts).</summary>
    public const float OpenClearance = 1.0f;

    /// <summary>The longest capsule walk from open ground to a foot (m; beyond it the grid's way counts).</summary>
    public const float WalkReach = 60f;

    /// <summary>One stair and what the check found: null <see cref="Problem"/> passes. <see cref="Blocker"/> is another item found in the way (-1: none), for the generator's enforcement.</summary>
    public sealed record Stair(AshfallStairs.Ramp Ramp, bool Authored, bool Elevated, string? Problem, int Blocker);

    /// <summary>
    /// Checks every stair of <paramref name="level"/>. <paramref name="authored"/> marks the items whose stairs are the authored exceptions (the stronghold's pieces: the keep AC-044's dead flights stay as authored); they are checked and
    /// reported, never enforced. <paramref name="start"/> is a point of the streets (the Imperial base's assembly area).
    /// </summary>
    public static List<Stair> Check(AshfallLevel level, AshfallDressing dressing, Func<int, bool> authored, Vector2 start)
    {
        var ramps = AshfallStairs.All(level, dressing);
        if (ramps.Count == 0) return [];
        var solids = AshfallStairs.Solids.Of(level);
        var grid = new AshfallGrid(level);
        var corners = level.SolidCorners();
        var world = new CollisionWorld(new StaticMesh(corners, Enumerable.Range(0, corners.Length).ToArray()), new StaticMesh(corners, Enumerable.Range(0, corners.Length).ToArray()));
        var ground = AshfallMap.GroundOf(level.Layout);

        // the items' boxes (who stands where), for naming a blocker
        var owner = new int[level.Parts.Count];
        Array.Fill(owner, -1);
        for (int i = 0; i < dressing.ItemParts.Count; i++) { var (f, n) = dressing.ItemParts[i]; for (int q = f; q < f + n; q++) owner[q] = i; }
        var boxes = new List<(int Item, AshfallBox Box)>();
        for (int q = 0; q < level.Parts.Count; q++)
        {
            var part = level.Parts[q];
            if (!part.Solid || owner[q] < 0 || part.Kind is AshfallKind.Stair) continue;
            if (AshfallBox.Of(part) is { } b) boxes.Add((owner[q], b));
        }
        int BlockerAt(Vector2 p, float z0, float z1, int self)
        {
            foreach (var (item, b) in boxes)
                if (item != self && b.Z0 < z1 && b.Z1 > z0 && MathF.Abs(b.Cx - p.X) < 40f && MathF.Abs(b.Cy - p.Y) < 40f && AshfallAtmosphere.Inside(b, p, 0.1f)) return item;
            return -1;
        }
        int ZoneBlocker(AshfallStairs.Ramp r, bool top)
        {
            var origin = top ? r.High : r.Low; var outward = top ? r.Dir : -r.Dir; float z = top ? r.HighZ : r.LowZ;
            float depth = top ? AshfallStairs.LandingDepth : AshfallStairs.Approach;
            for (float a = 0.15f; a <= depth; a += 0.25f)
                for (float t = -r.Width / 2f; t <= r.Width / 2f + 1e-3f; t += 0.2f)
                {
                    int k = BlockerAt(origin + outward * a + r.Across * t, z + AshfallStairs.Step + 0.05f, z + AshfallStairs.Headroom, r.Item);
                    if (k >= 0) return k;
                }
            return -1;
        }

        // the streets: the flood from the start over the grid's walkable ground
        var reach = Flood(grid, start);

        var cfg = new SimConfig();
        var results = new Stair[ramps.Count];
        var passed = new bool[ramps.Count];
        var pending = new List<int>();
        for (int i = 0; i < ramps.Count; i++)
        {
            var r = ramps[i];
            bool auth = r.Item >= 0 && authored(r.Item);
            var footAt = r.Low - r.Dir * AshfallStairs.Approach;
            float footFloor = solids.FloorUnder(footAt, r.LowZ + AshfallStairs.Step);
            bool elevated = footFloor > ground(footAt.X, footAt.Y) + AshfallStairs.Step + 0.05f;
            var foot = AshfallStairs.Measure(r, solids, top: false, AshfallStairs.Approach);
            var land = AshfallStairs.Measure(r, solids, top: true, AshfallStairs.LandingDepth);
            string? problem = null; int blocker = -1;
            if (!foot.Open) { problem = $"its foot is blocked (clear run {foot.ClearRun:0.0} m of {foot.Need:0.0})"; blocker = ZoneBlocker(r, false); }
            else if (!land.Open) { problem = $"its landing is blocked (clear run {land.ClearRun:0.0} m of {land.Need:0.0})"; blocker = ZoneBlocker(r, true); }
            else
            {
                var flight = WalkFlight(r, footAt, footFloor, cfg, world, out var stall);
                if (flight is not null) { problem = flight; if (stall is { } sp) blocker = BlockerAt(new Vector2(sp.X, sp.Y), sp.Z + AshfallStairs.Step, sp.Z + AshfallStairs.Headroom, r.Item); }
            }
            results[i] = new Stair(r, auth, elevated, problem, blocker);
            if (problem is null) pending.Add(i);
        }
        // the feet: ground feet from the streets; upper feet from a reached flight's landing on their floor (repeated until nothing changes)
        foreach (int i in pending)
        {
            var r = ramps[i];
            var footAt = r.Low - r.Dir * AshfallStairs.Approach;
            if (results[i].Elevated)
            {
                // a raised landing in front of the flight (the kit's two-storey ruin, AC-051: its flight starts about 1.2 m up, over a few box steps the stair finder leaves as steps): climbed from the ground floor
                // behind it, walked from the streets
                foreach (float back in new[] { 2.5f, 3.5f, 5f, 7f })
                {
                    var p = footAt - r.Dir * back;
                    if (solids.FloorUnder(p, r.LowZ) > ground(p.X, p.Y) + AshfallStairs.Step + 0.05f) continue;
                    if (ReachFoot(grid, reach, p, [footAt], cfg, world, out _, st => st.Grounded && MathF.Abs(st.Position.Z - r.LowZ) < AshfallStairs.Step && Vector2.Distance(new(st.Position.X, st.Position.Y), footAt) < 0.8f) is null) { passed[i] = true; break; }
                }
                continue;
            }
            string? why = ReachFoot(grid, reach, footAt, [], cfg, world, out var stall);
            if (why is null) passed[i] = true;
            else results[i] = results[i] with { Problem = why, Blocker = stall is { } sp ? BlockerAt(new Vector2(sp.X, sp.Y), sp.Z + AshfallStairs.Step, sp.Z + AshfallStairs.Headroom, r.Item) : -1 };
        }
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (int i in pending)
            {
                if (passed[i] || !results[i].Elevated || results[i].Problem is not null) continue;
                var r = ramps[i];
                var footAt = r.Low - r.Dir * AshfallStairs.Approach;
                for (int j = 0; j < ramps.Count; j++)
                {
                    if (!passed[j]) continue;
                    var q = ramps[j];
                    if (MathF.Abs(q.HighZ - r.LowZ) > AshfallStairs.Step + 0.05f || Vector2.Distance(q.High, footAt) > 20f) continue;
                    var from = q.High + q.Dir * AshfallStairs.LandingDepth;
                    if (WalkTo(from, q.HighZ, [footAt], cfg, world, out _) is null) { passed[i] = true; changed = true; break; }
                }
            }
        }
        foreach (int i in pending)
            if (!passed[i] && results[i].Problem is null) results[i] = results[i] with { Problem = "its foot stands on an upper floor no reached flight leads to" };
        return [.. results];
    }

    /// <summary>Checks a built layout: the stronghold's pieces, the compound's walls and the bridges are the authored exceptions; the streets start at the Imperial assembly area (draft 10: north of the compound's gate).</summary>
    public static List<Stair> Check(AshfallDraft5 d)
    {
        var auth = new HashSet<int>();
        foreach (var p in d.Placements)
            if (p.Item >= 0 && IsAuthored(p.Group, p.Family, p.Asset)) auth.Add(p.Item);
        var start = d.Level.Layout.PlanOf() is { } plan ? new Vector2((plan.Assembly[0].Rect.X + plan.Assembly[0].Rect.Z) / 2f, (plan.Assembly[0].Rect.Y + plan.Assembly[0].Rect.W) / 2f) : new Vector2(0f, -150f);
        return Check(d.Level, d.Dressing, auth.Contains, start);
    }

    /// <summary>Whether a placed structure is one of the authored exceptions (the stronghold and its pieces, the Imperial compound's walls, the bridges).</summary>
    public static bool IsAuthored(string group, string family, string asset) =>
        family.StartsWith("stronghold", StringComparison.Ordinal) || family is "terrace" or "outerwall" || group == "keep bridge" || AshfallDraft5Pass.IsTransplanted(group) || AshfallDraft5Pass.BridgeIds.Contains(asset);

    /// <summary>The grid cells joined to <paramref name="start"/> over ground with <see cref="FloodClearance"/>.</summary>
    static bool[] Flood(AshfallGrid g, Vector2 start)
    {
        var seen = new bool[g.Blocked.Length];
        int s = g.Snap(start, OpenClearance);
        if (s < 0) return seen;
        var stack = new Stack<int>();
        seen[s] = true; stack.Push(s);
        int n = g.N;
        while (stack.Count > 0)
        {
            int k = stack.Pop(); int x = k % n, y = k / n;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                    int m = ny * n + nx;
                    if (seen[m] || g.Clearance[m] < FloodClearance) continue;
                    seen[m] = true; stack.Push(m);
                }
        }
        return seen;
    }

    /// <summary>Whether a ground foot is reached: the grid's way from the streets to it, then the capsule walked from the last open ground on that way (or <see cref="WalkReach"/> before the foot) to the foot.</summary>
    static string? ReachFoot(AshfallGrid g, bool[] reach, Vector2 footAt, IReadOnlyList<Vector2> tail, SimConfig cfg, CollisionWorld world, out Vector3? stall, Func<PlayerState, bool>? arrive = null)
    {
        stall = null;
        // the nearest reached cell within 3 m of the foot
        int c = g.Index(footAt), n = g.N, cx = c % n, cy = c / n, best = -1, span = (int)(3f / g.Cell);
        float bd = float.MaxValue;
        for (int dy = -span; dy <= span; dy++)
            for (int dx = -span; dx <= span; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= n || y >= n) continue;
                int k = y * n + x; float d = dx * dx + dy * dy;
                if (reach[k] && d < bd) { bd = d; best = k; }
            }
        if (best < 0) return "its foot is not reached from the streets (walled in)";
        // the way back from that cell to open ground (a breadth-first search over reached cells), then walked forward
        var from = new Dictionary<int, int> { [best] = -1 };
        var queue = new Queue<int>();
        queue.Enqueue(best);
        int open = -1;
        while (queue.Count > 0)
        {
            int k = queue.Dequeue();
            if (g.Clearance[k] >= OpenClearance) { open = k; break; }
            int x = k % n, y = k / n;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                    int m = ny * n + nx;
                    if (!reach[m] || from.ContainsKey(m)) continue;
                    if (Vector2.Distance(g.At(m), footAt) > WalkReach) continue;
                    from[m] = k; queue.Enqueue(m);
                }
        }
        if (open < 0) return "its foot is reached only through openings narrower than the capsule";
        var way = new List<Vector2>();
        for (int k = open; k >= 0; k = from[k]) way.Add(g.At(k));
        // thin the cells to a waypoint every 1.5 m, ending at the foot
        var pts = new List<Vector2>();
        foreach (var p in way) if (pts.Count == 0 || Vector2.Distance(pts[^1], p) >= 1.5f) pts.Add(p);
        pts.Add(footAt);
        pts.AddRange(tail);
        string? walk = WalkTo(pts[0], g.GroundAt(pts[0]), pts.Skip(1).ToList(), cfg, world, out stall, arrive);
        return walk is null ? null : "the walk to its foot " + walk;
    }

    /// <summary>The flight walked up from in front of its foot to past its top and back down (null: walked).</summary>
    static string? WalkFlight(AshfallStairs.Ramp r, Vector2 footAt, float footFloor, SimConfig cfg, CollisionWorld world, out Vector3? stall)
    {
        var top = r.High + r.Dir * AshfallStairs.LandingDepth;
        var up = WalkTo(footAt, footFloor, [r.Low, r.High, top], cfg, world, out stall, arrive: st => st.Grounded && Vector2.Dot(new Vector2(st.Position.X, st.Position.Y) - r.High, r.Dir) > 0.5f && MathF.Abs(st.Position.Z - r.HighZ) < AshfallStairs.Step);
        if (up is not null) return "the walk up its flight " + up;
        var down = WalkTo(top, r.HighZ, [r.High, r.Low, footAt], cfg, world, out stall, arrive: st => st.Grounded && Vector2.Dot(new Vector2(st.Position.X, st.Position.Y) - r.Low, -r.Dir) > 0.5f && st.Position.Z < r.LowZ + AshfallStairs.Step);
        return down is null ? null : "the walk down its flight " + down;
    }

    /// <summary>Walks the player's capsule at run speed from <paramref name="from"/> through <paramref name="path"/> (null when it arrives; else where it stalled).</summary>
    static string? WalkTo(Vector2 from, float fromZ, IReadOnlyList<Vector2> path, SimConfig cfg, CollisionWorld world, out Vector3? stall, Func<PlayerState, bool>? arrive = null)
    {
        stall = null;
        float speed = cfg.RunSpeed / cfg.TickRate;
        var st = new PlayerState(1, new Vector3(from.X, from.Y, fromZ + 0.05f), Vector3.Zero, 0f, true);
        float length = 0f; var last = from;
        foreach (var p in path) { length += Vector2.Distance(last, p); last = p; }
        int limit = (int)(length / speed * 1.6f) + 60, next = 0;
        var mark = from; uint markTick = 0;
        for (uint t = 0; t < limit; t++)
        {
            var at = new Vector2(st.Position.X, st.Position.Y);
            while (next < path.Count && Vector2.Distance(at, path[next]) < (next == path.Count - 1 ? 0.6f : 1.0f)) next++;
            if (arrive is not null ? arrive(st) : next >= path.Count) return null;
            var goal = path[Math.Min(next, path.Count - 1)];
            var d = goal - at;
            if (d.LengthSquared() < 1e-6f) d = goal - from;
            float yaw = MathF.Atan2(-d.X, d.Y);
            st = Movement.Step(st, new PlayerInput(t, 0f, 1f, yaw, InputButtons.None), cfg, world);
            if (t - markTick >= (uint)cfg.TickRate)
            {
                var q = new Vector2(st.Position.X, st.Position.Y);
                if (Vector2.Distance(q, mark) < 0.6f) { stall = st.Position; return $"stalls at ({q.X:0.0}, {q.Y:0.0}, {st.Position.Z:0.0})"; }
                mark = q; markTick = t;
            }
        }
        stall = st.Position;
        return $"does not arrive (at ({st.Position.X:0.0}, {st.Position.Y:0.0}, {st.Position.Z:0.0}))";
    }
}
