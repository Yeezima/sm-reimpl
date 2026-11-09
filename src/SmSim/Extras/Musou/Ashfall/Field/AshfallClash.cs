using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// The clash test bench of docs/ashfall-layout-brief.md ("Test the intended 200-vs-200 average clash, 500-vs-500 larger clashes and rare 1,000-vs-1,000"; draft 11). Two armies march from their bases to one point along a
/// route on the final collision, meet, hold the clash and one side retreats; frontage, queue length, stuck units, arrival spread and throughput are measured. A movement model only: no unit is hurt (near and far combat
/// are not modelled or changed), and the wave rules are not used. Every number of the model is the game's where one exists (the army layer's slots and march speeds, <see cref="TfwArmyLayout"/> and <see cref="TfwArmyRules"/>;
/// an Ork's 0.4 m body and 5.2 m/s run, a Nob's 0.6 m; a Guardsman's 0.4 m and 4.5 m/s), the rest ours.
/// <para>The army AI as shipped (<see cref="ClashAi.Shipped"/>): the formation's width is fixed for its life (<see cref="TfwArmyLayout.Width"/>, up to 36 files), its anchor follows the shortest walk a single body can take
/// (the war's route search on the walking graph), and each man walks to his slot, or down the flow to the point when his slot stands in a wall. <see cref="ClashAi.Adaptive"/> is the investigation's alternative, not in
/// the game: the route keeps the army's half width from walls where it can and the files narrow to the ground's width (the slots reflow).</para>
/// </summary>
public static class AshfallClash
{
    public enum ClashAi : byte { Shipped, Adaptive }

    public sealed record Setup(AshfallLayout Layout, string Point, int PerSide, ClashAi Ai, float HoldSeconds = 40f, float Tick = 0.2f, float LimitSeconds = 360f);

    public sealed record Snapshot(float Time, string Phase, Vector2[] Orks, Vector2[] Guards, bool[] OrkEngaged, bool[] GuardEngaged);

    public sealed record Result(Setup Setup, float FirstContact, float FrontageAtContact, float FrontageSteady, int Engaged, float OrkQueue, float GuardQueue, float OrkDepthPlanned, float GuardDepthPlanned,
        int StuckMost, int StuckEver, float OrkArrivalSpread, float GuardArrivalSpread, float OrkThroughputPeak, float GuardThroughputPeak, float OrkThroughputMean, float GuardThroughputMean,
        float RetreatSeconds, float RetreatThroughput, int OrkWidth, int GuardWidth, float OrkRoute, float GuardRoute, float NarrowestOnRoute, int SlotsInWalls, bool SingleFile)
    {
        public string Row => $"| {Setup.Layout} | {Setup.Point} | {Setup.PerSide} | {Setup.Ai} | {FirstContact:0} | {FrontageAtContact:0} | {FrontageSteady:0} | {Engaged} | {OrkQueue:0} / {OrkDepthPlanned:0} | {GuardQueue:0} / {GuardDepthPlanned:0} | {StuckMost} / {StuckEver} | {OrkArrivalSpread:0} / {GuardArrivalSpread:0} | {OrkThroughputPeak:0.0} / {GuardThroughputPeak:0.0} | {OrkThroughputMean:0.0} / {GuardThroughputMean:0.0} | {RetreatSeconds:0} ({RetreatThroughput:0.0}/s) | {OrkWidth} / {GuardWidth} | {NarrowestOnRoute:0.0} | {SlotsInWalls} | {(SingleFile ? "yes" : "no")} |";
        public static string Header => "| Layout | Point | A side | AI | First contact (s) | Frontage at contact (m) | Frontage +20 s (m) | Engaged | Ork queue / planned depth (m) | Guard queue / planned depth (m) | Stuck most / ever | Arrival spread Ork / Guard (s) | Throughput peak Ork / Guard (men/s) | Throughput mean | Retreat (s, men/s) | Files Ork / Guard | Narrowest on route (m) | Slot-ticks in walls | Single file |\n|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|";
    }

    const float OrkRadius = 0.4f, NobRadius = 0.6f, GuardRadius = 0.4f, OrkRun = 5.2f, GuardRun = 4.5f, Contact = 1.6f, StuckMetres = 1f, StuckSeconds = 10f;

    sealed class Unit
    {
        public Vector2 P; public float R, Speed; public int Slot; public bool Engaged, Arrived, Home; public float ArrivedAt = -1f, CrossedAt = -1f;
        public Vector2 Mark; public float MarkT; public bool Stuck, EverStuck, AtSlot; public int WallTicks, Ticks;
    }

    sealed class Army
    {
        public TfwArmySide Side; public List<Unit> Units = []; public TfwWay Way = new(); public float Along, Speed; public int Width; public float WaitT;
        public int[] Flow = []; public int[] HomeFlow = [];
    }

    /// <summary>Runs one clash; <paramref name="frame"/> (optional) gets a snapshot every <paramref name="frameEvery"/> s (the clips).</summary>
    public static Result Run(Setup s, Action<Snapshot>? frame = null, float frameEvery = 0.5f)
    {
        var d = AshfallMap.BuildDraft5(s.Layout);
        var grid = Grids.GetOrAdd(s.Layout, _ => new AshfallGrid(d.Level));
        var pt = d.Level.Points.First(p => p.Name == s.Point);
        var goal = new Vector2(pt.Center.X, pt.Center.Y);
        var plan = s.Layout.PlanOf();
        Vector2 baseNode = plan is null ? new(0f, -150f) : Mid(plan.Assembly[0].Rect), shNode = plan is null ? new(0f, 98f) : Mid(plan.Assembly[1].Rect);
        var orks = MakeArmy(TfwArmySide.Ork, s.PerSide, shNode, goal, grid, s.Ai);
        var guards = MakeArmy(TfwArmySide.Guard, s.PerSide, baseNode, goal, grid, s.Ai);
        orks.HomeFlow = Flow(grid, shNode, OrkRadius); guards.HomeFlow = Flow(grid, baseNode, GuardRadius);
        orks.Flow = Flow(grid, goal, OrkRadius); guards.Flow = Flow(grid, goal, GuardRadius);
        float narrow = MathF.Min(Narrowest(grid, orks.Way), Narrowest(grid, guards.Way));

        float t = 0f, firstContact = -1f, frontContact = 0f, frontSteady = 0f, nextFrame = 0f, retreatStart = -1f, retreatEnd = -1f;
        int stuckMost = 0, slotsInWalls = 0, engagedSteady = 0;
        float orkQueue = 0f, guardQueue = 0f;
        string phase = "assembling";
        var grid2 = new Dictionary<long, List<Unit>>();
        float zone = pt.Radius + (plan?.Params.ObjectiveClear ?? 16f);
        // the throughput gate: 60 m before the point's centre along each army's way
        float orkGate = MathF.Max(0f, orks.Way.Length - 60f), guardGate = MathF.Max(0f, guards.Way.Length - 60f);
        const float assemble = 10f;
        while (t < s.LimitSeconds)
        {
            bool retreating = retreatStart >= 0f;
            if (t >= assemble && !retreating) phase = firstContact < 0f ? "advancing" : "clashing";
            // anchors: march along the way at the army speed, waiting (the army layer's rule) while the men's middle lags
            foreach (var a in new[] { orks, guards })
            {
                if (t < assemble || retreating && a == guards) continue;
                float lag = a.Along - MiddleAlong(a);
                if (lag > TfwArmyRules.WaitBehind && a.WaitT < TfwArmyRules.WaitMostSeconds) { a.WaitT += s.Tick; continue; }
                if (lag > TfwArmyRules.WaitBehind) a.Along += a.Speed * TfwArmyRules.CreepShare * s.Tick; else { a.WaitT = 0f; a.Along += a.Speed * s.Tick; }
                a.Along = MathF.Min(a.Along, a.Way.Length);
            }
            // spatial hash
            grid2.Clear();
            foreach (var u in orks.Units.Concat(guards.Units)) { long key = Key(u.P); if (!grid2.TryGetValue(key, out var l)) grid2[key] = l = []; l.Add(u); }
            // contact
            foreach (var u in orks.Units)
                if (!u.Engaged && !retreating)
                    foreach (var o in Near(grid2, u.P))
                        if (guards.Units.Contains(o) is var _ && o.R == GuardRadius && !o.Home && IsGuard(o, guards) && Vector2.DistanceSquared(u.P, o.P) < Contact * Contact)
                        { u.Engaged = true; o.Engaged = true; if (firstContact < 0f) firstContact = t; break; }
            // move
            foreach (var a in new[] { orks, guards })
            {
                bool home = retreating && a == guards;
                var (at, way) = a.Way.Sample(a.Along);
                float yaw = MathF.Atan2(-way.X, way.Y);
                int width = a.Width;
                if (s.Ai == ClashAi.Adaptive && !home)
                {
                    float w = grid.WidthAcross(new Vector2(at.X, at.Y), way, 40f);
                    width = Math.Clamp((int)((w - 2f * NobRadius - 1f) / TfwArmyLayout.OrkFile) + 1, 4, a.Width);
                }
                foreach (var u in a.Units)
                {
                    if (u.Home) continue;
                    if (u.Engaged && !home) continue;
                    Vector2 target; bool slotFree = false;
                    if (home) target = new Vector2(float.NaN);
                    else
                    {
                        var slot = TfwArmyLayout.Slot(a.Side, width, at, yaw, u.Slot);
                        target = new Vector2(slot.X, slot.Y);
                        slotFree = grid.ClearanceAt(target) >= u.R;
                        if (!slotFree && t >= assemble) slotsInWalls++;
                        u.AtSlot = slotFree && Vector2.Distance(u.P, target) < 1.5f;
                    }
                    Vector2 step;
                    if (!home && slotFree && (Vector2.Distance(u.P, target) < 12f || DirectClear(grid, u.P, target, u.R)))
                    {
                        var dv = target - u.P; float dl = dv.Length();
                        step = dl < 1e-3f ? Vector2.Zero : dv / dl * MathF.Min(u.Speed * s.Tick, dl);
                    }
                    else step = Downhill(grid, home ? a.HomeFlow : a.Flow, u.P, u.Speed * s.Tick);
                    // separation from neighbours
                    var push = Vector2.Zero;
                    foreach (var o in Near(grid2, u.P))
                    {
                        if (o == u || o.Home) continue;
                        var dv = u.P - o.P; float dd = dv.LengthSquared(), min = u.R + o.R + 0.1f;
                        if (dd < min * min && dd > 1e-6f) { float dl = MathF.Sqrt(dd); push += dv / dl * (min - dl) * 0.5f; }
                    }
                    u.Ticks++;
                    if (!Move(grid, u, step + push) && step.LengthSquared() > 1e-4f) u.WallTicks++;
                    if (home && Vector2.Distance(u.P, Mid(a == guards ? baseNode : shNode)) < 25f) { u.Home = true; }
                    if (!u.Arrived && !home && Vector2.Distance(u.P, goal) < zone) { u.Arrived = true; u.ArrivedAt = t; }
                }
            }
            // gates, stuck, queues
            int stuckNow = 0;
            foreach (var a in new[] { orks, guards })
            {
                float gate = a == orks ? orkGate : guardGate;
                foreach (var u in a.Units)
                {
                    if (u.CrossedAt < 0f && Along(a.Way, u.P) >= gate) u.CrossedAt = t;
                    // stuck: no metre of progress in 10 s while the walls (not the men ahead) stopped its own move at least half the time; a man in his slot or in the fight is not stuck
                    bool settled = u.Engaged || u.Home || u.AtSlot || (retreatStart < 0f && Vector2.Distance(u.P, goal) < zone);
                    if (settled || t < assemble) { u.Mark = u.P; u.MarkT = t; u.Stuck = false; u.WallTicks = u.Ticks = 0; continue; }
                    if (Vector2.Distance(u.P, u.Mark) >= StuckMetres) { u.Mark = u.P; u.MarkT = t; u.Stuck = false; u.WallTicks = u.Ticks = 0; }
                    else if (t - u.MarkT >= StuckSeconds && u.WallTicks * 2 >= u.Ticks) { u.Stuck = true; u.EverStuck = true; }
                    if (u.Stuck) stuckNow++;
                }
            }
            stuckMost = Math.Max(stuckMost, stuckNow);
            if (firstContact >= 0f && MathF.Abs(t - firstContact) < s.Tick * 0.5f)
            {
                frontContact = Frontage(orks, guards, goal);
                orkQueue = Queue(orks); guardQueue = Queue(guards);
            }
            if (firstContact >= 0f && MathF.Abs(t - firstContact - 20f) < s.Tick * 0.5f) { frontSteady = Frontage(orks, guards, goal); engagedSteady = orks.Units.Count(u => u.Engaged) + guards.Units.Count(u => u.Engaged); }
            if (firstContact >= 0f && retreatStart < 0f && t >= firstContact + s.HoldSeconds)
            {
                retreatStart = t; phase = "retreating";
                foreach (var u in guards.Units) { u.Engaged = false; u.Mark = u.P; u.MarkT = t; }
                foreach (var u in orks.Units) u.Engaged = false;
            }
            if (retreatStart >= 0f && retreatEnd < 0f && guards.Units.Count(u => u.Home) >= guards.Units.Count * 9 / 10) retreatEnd = t;
            if (frame is not null && t >= nextFrame)
            {
                frame(new Snapshot(t, phase, orks.Units.Select(u => u.P).ToArray(), guards.Units.Where(u => !u.Home).Select(u => u.P).ToArray(), orks.Units.Select(u => u.Engaged).ToArray(), guards.Units.Where(u => !u.Home).Select(u => u.Engaged).ToArray()));
                nextFrame += frameEvery;
            }
            if (retreatEnd >= 0f && t > retreatEnd + 2f) break;
            t += s.Tick;
        }
        var (ot, om) = Throughput(orks); var (gt, gm) = Throughput(guards);
        float retreatS = retreatStart >= 0f && retreatEnd >= 0f ? retreatEnd - retreatStart : -1f;
        float oDepth = TfwArmyLayout.Depth(TfwArmySide.Ork, orks.Width, s.PerSide), gDepth = TfwArmyLayout.Depth(TfwArmySide.Guard, guards.Width, s.PerSide);
        bool single = orkQueue > 3f * MathF.Max(oDepth, 10f) || guardQueue > 3f * MathF.Max(gDepth, 10f);
        return new Result(s, firstContact, frontContact, frontSteady, engagedSteady, orkQueue, guardQueue, oDepth, gDepth, stuckMost, orks.Units.Count(u => u.EverStuck) + guards.Units.Count(u => u.EverStuck),
            Spread(orks), Spread(guards), ot, gt, om, gm, retreatS, retreatS > 0f ? guards.Units.Count * 0.9f / retreatS : 0f, orks.Width, guards.Width, orks.Way.Length, guards.Way.Length, narrow, slotsInWalls, single);
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<AshfallLayout, AshfallGrid> Grids = new();

    static Vector2 Mid(Vector4 r) => new((r.X + r.Z) / 2f, (r.Y + r.W) / 2f);
    static Vector2 Mid(Vector2 v) => v;
    static bool IsGuard(Unit o, Army g) => o.Speed == GuardRun;
    static long Key(Vector2 p) => ((long)MathF.Floor(p.X / 2f) << 32) ^ (uint)(int)MathF.Floor(p.Y / 2f);

    static IEnumerable<Unit> Near(Dictionary<long, List<Unit>> h, Vector2 p)
    {
        int cx = (int)MathF.Floor(p.X / 2f), cy = (int)MathF.Floor(p.Y / 2f);
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (h.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cy + dy), out var l)) foreach (var u in l) yield return u;
    }

    static Army MakeArmy(TfwArmySide side, int n, Vector2 from, Vector2 goal, AshfallGrid grid, ClashAi ai)
    {
        var a = new Army { Side = side, Width = TfwArmyLayout.Width(side, n), Speed = side == TfwArmySide.Ork ? TfwArmyRules.OrkSpeed : TfwArmyRules.GuardSpeed };
        // the way: the shortest walk of one body (as shipped: the war searches its routes on the single-body walking graph); the alternative keeps half the army's width from walls where it can
        float r = side == TfwArmySide.Ork ? OrkRadius : GuardRadius;
        (float Length, List<int> Path)? path = null;
        if (ai == ClashAi.Adaptive)
            foreach (float c in new[] { 12f, 10f, 8f, 6f, 4f, 2f }) { path = grid.Shortest(from, goal, c); if (path is not null) break; }
        path ??= grid.Shortest(from, goal, r) ?? throw new InvalidOperationException($"no walk from ({from.X}, {from.Y}) to ({goal.X}, {goal.Y})");
        var cells = path.Value.Path;
        for (int i = 0; i < cells.Count; i += 8) { var q = grid.At(cells[i]); a.Way.Add(new Vector3(q.X, q.Y, 0f)); }
        var last = grid.At(cells[^1]); a.Way.Add(new Vector3(last.X, last.Y, 0f));
        // assembled at the start: the formation's slots at the way's start facing along it (a muster), each man on the nearest free cell
        var (at, way) = a.Way.Sample(0f);
        float yaw = MathF.Atan2(-way.X, way.Y);
        for (int k = 0; k < n; k++)
        {
            var slot = TfwArmyLayout.Slot(side, a.Width, at, yaw, k);
            var p = new Vector2(slot.X, slot.Y);
            bool nob = side == TfwArmySide.Ork && TfwArmyLayout.OrkType(k, 0.1f) == MusouEnemyType.Nob;
            float rad = side == TfwArmySide.Guard ? GuardRadius : nob ? NobRadius : OrkRadius;
            int c = grid.Snap(p, rad);
            a.Units.Add(new Unit { P = c >= 0 ? grid.At(c) : p, R = side == TfwArmySide.Guard ? GuardRadius : rad, Speed = side == TfwArmySide.Ork ? OrkRun : GuardRun, Slot = k });
        }
        return a;
    }

    /// <summary>Distance to <paramref name="goal"/> in cells over cells a body of <paramref name="radius"/> fits (8-connected; -1 unreachable), as an int field in tenths of a cell.</summary>
    static int[] Flow(AshfallGrid g, Vector2 goal, float radius)
    {
        int n = g.N; var dist = new int[n * n]; Array.Fill(dist, int.MaxValue);
        int s = g.Snap(goal, radius); if (s < 0) return dist;
        var q = new PriorityQueue<int, int>(); dist[s] = 0; q.Enqueue(s, 0);
        while (q.TryDequeue(out int k, out int dk))
        {
            if (dk > dist[k]) continue;
            int x = k % n, y = k / n;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx | dy) == 0) continue;
                    int nx = x + dx, ny = y + dy; if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                    int m = ny * n + nx; if (g.Clearance[m] < radius) continue;
                    int nd = dk + (dx != 0 && dy != 0 ? 14 : 10);
                    if (nd < dist[m]) { dist[m] = nd; q.Enqueue(m, nd); }
                }
        }
        return dist;
    }

    static Vector2 Downhill(AshfallGrid g, int[] flow, Vector2 p, float stepLen)
    {
        int k = g.Index(p), n = g.N, x = k % n, y = k / n, best = k;
        // look two cells out for a smoother heading
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = x + dx, ny = y + dy; if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                int m = ny * n + nx; if (flow[m] < flow[best]) best = m;
            }
        if (best == k) return Vector2.Zero;
        var dv = g.At(best) - p; float l = dv.Length();
        return l < 1e-4f ? Vector2.Zero : dv / l * stepLen;
    }

    static bool DirectClear(AshfallGrid g, Vector2 a, Vector2 b, float r)
    {
        float len = Vector2.Distance(a, b); int steps = Math.Max(1, (int)(len / g.Cell));
        for (int i = 1; i <= steps; i++) if (g.ClearanceAt(Vector2.Lerp(a, b, i / (float)steps)) < r) return false;
        return true;
    }

    /// <summary>Moves a man by <paramref name="d"/> (or along one axis of it); false when the walls stopped every try.</summary>
    static bool Move(AshfallGrid g, Unit u, Vector2 d)
    {
        float l = d.Length(); float most = u.Speed * 0.2f * 1.5f;
        if (l > most) d = d / l * most;
        foreach (var c in new[] { d, new Vector2(d.X, 0f), new Vector2(0f, d.Y) })
        {
            var q = u.P + c;
            if (MathF.Abs(q.X) < AshfallMap.Half - 1f && MathF.Abs(q.Y) < AshfallMap.Half - 1f && g.ClearanceAt(q) >= u.R) { u.P = q; return true; }
        }
        return l < 1e-4f;
    }

    static float Along(TfwWay w, Vector2 p)
    {
        float best = float.MaxValue, at = 0f, run = 0f;
        var pts = w.Points;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = new Vector2(pts[i - 1].X, pts[i - 1].Y); var b = new Vector2(pts[i].X, pts[i].Y); var ab = b - a; float len = ab.Length();
            float t = len > 1e-4f ? Math.Clamp(Vector2.Dot(p - a, ab) / (len * len), 0f, 1f) : 0f;
            float d = Vector2.DistanceSquared(p, a + ab * t);
            if (d < best) { best = d; at = run + t * len; }
            run += len;
        }
        return at;
    }

    static float MiddleAlong(Army a)
    {
        var list = a.Units.Where(u => !u.Engaged).Select(u => Along(a.Way, u.P)).OrderBy(x => x).ToList();
        if (list.Count == 0) return a.Along;
        // the middle of the army against the anchor (the anchor is the front rank's middle: compare the front tenth's middle)
        return list[(int)(list.Count * 0.9f)];
    }

    static float Queue(Army a)
    {
        var list = a.Units.Select(u => Along(a.Way, u.P)).OrderBy(x => x).ToList();
        return list.Count == 0 ? 0f : list[^1] - list[(int)(list.Count * 0.05f)];   // the front to the rearmost 95 %
    }

    static float Frontage(Army orks, Army guards, Vector2 goal)
    {
        var engaged = orks.Units.Where(u => u.Engaged).Select(u => u.P).Concat(guards.Units.Where(u => u.Engaged).Select(u => u.P)).ToList();
        if (engaged.Count < 2) return engaged.Count;
        // across the clash's axis (the two armies' middles)
        var oc = Centroid(orks.Units.Select(u => u.P)); var gc = Centroid(guards.Units.Select(u => u.P));
        var axis = gc - oc; if (axis.LengthSquared() < 1e-3f) axis = Vector2.UnitY;
        axis = Vector2.Normalize(axis); var across = new Vector2(-axis.Y, axis.X);
        var proj = engaged.Select(p => Vector2.Dot(p, across)).OrderBy(x => x).ToList();
        // the 5th to 95th percentile (a lone straggler does not make a front)
        return proj[(int)(proj.Count * 0.95f) - 1 < 0 ? proj.Count - 1 : (int)(proj.Count * 0.95f) - 1] - proj[(int)(proj.Count * 0.05f)];
    }

    static Vector2 Centroid(IEnumerable<Vector2> ps) { var l = ps.ToList(); return l.Count == 0 ? Vector2.Zero : l.Aggregate(Vector2.Zero, (a, b) => a + b) / l.Count; }

    static float Spread(Army a)
    {
        var times = a.Units.Select(u => u.ArrivedAt >= 0f ? u.ArrivedAt : u.Engaged ? -2f : -1f).Where(x => x >= 0f).OrderBy(x => x).ToList();
        if (times.Count < 2) return 0f;
        return times[(int)(times.Count * 0.9f) - 1 < 0 ? 0 : (int)(times.Count * 0.9f) - 1] - times[0];
    }

    static (float Peak, float Mean) Throughput(Army a)
    {
        var times = a.Units.Where(u => u.CrossedAt >= 0f).Select(u => u.CrossedAt).OrderBy(x => x).ToList();
        if (times.Count < 2) return (0f, 0f);
        float peak = 0f; int j = 0;
        for (int i = 0; i < times.Count; i++) { while (times[i] - times[j] > 10f) j++; peak = MathF.Max(peak, (i - j + 1) / 10f); }
        int lo = (int)(times.Count * 0.1f), hi = Math.Max(lo + 1, (int)(times.Count * 0.9f) - 1);
        float span = times[hi] - times[lo];
        return (peak, span > 0.1f ? (hi - lo) / span : peak);
    }

    static float Narrowest(AshfallGrid g, TfwWay w)
    {
        float min = float.MaxValue;
        for (float s = 20f; s < w.Length - 20f; s += 2f)
        {
            var (at, dir) = w.Sample(s);
            min = MathF.Min(min, g.WidthAcross(new Vector2(at.X, at.Y), dir));
        }
        return min == float.MaxValue ? 0f : min;
    }
}
