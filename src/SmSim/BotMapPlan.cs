using System.Numerics;

namespace SmSim;

/// <summary>
/// Where a Classic PvP test bot goes when it has nobody to chase (owner, 2026-11-07; test script, **ours**, not the original AI; spec "Bot map plan"). Built once
/// per side and graph from the bot's own <see cref="NavGraph"/> (cells it reaches from its start area: walks, drops and jump links), the map centre, both start
/// areas and the other side's spawn door (<see cref="SpawnRoomWatch.Door"/>):
/// <list type="bullet">
/// <item><b>no-loiter zone</b>: within <see cref="NoLoiterRadius"/> m (flat) of the enemy's door or start area. No hold, roam or route point is in it, and an
/// enemy standing in it is not chased (the owner: "the bot should never wait directly outside the spawn area of the enemy");</item>
/// <item><b>centre holds</b>: one cell per <see cref="HoldSectors"/>-th of the circle around the centre, <see cref="HoldRingMin"/> to <see cref="HoldRingMax"/> m out,
/// near a wall or prop where there is one (the same cover test as the spawn watch), ordered by angle, so each bot of a side gets its own defensive spot;</item>
/// <item><b>roam points</b>: up to <see cref="MaxRoam"/> cells spread over the reachable floor by farthest-point sampling, at least <see cref="RoamSpacing"/> m apart and
/// <see cref="OwnStartClearance"/> m from the own start;</item>
/// <item><b>lanes</b>: up to three cells across the map (left, middle, right of the line between the start areas) <see cref="LaneFrom"/> to <see cref="LaneTo"/> of
/// the way from the own start toward the enemy's, the first leg of a life, so a side's bots leave the spawn by different routes.</item>
/// </list>
/// </summary>
public sealed class BotMapPlan
{
    /// <summary>Metres (flat) around the enemy's spawn door and start area where a bot never waits, roams or chases (ours, an estimate).</summary>
    public const float NoLoiterRadius = 25f;
    /// <summary>The ring around the centre the defensive holds are taken from (metres, flat; ours).</summary>
    public const float HoldRingMin = 5f, HoldRingMax = 16f;
    /// <summary>Sectors of the circle around the centre, one hold each at most (ours).</summary>
    public const int HoldSectors = 8;
    /// <summary>Height (m) a hold may differ from the centre's floor (ours).</summary>
    public const float HoldMaxRise = 4f;
    /// <summary>Most roam points, their least spacing (m) and their least distance from the own start (m; ours).</summary>
    public const int MaxRoam = 12;
    public const float RoamSpacing = 12f, OwnStartClearance = 20f;
    /// <summary>The lanes' band along the line from the own start to the enemy's (fractions; ours) and their least spacing (m).</summary>
    public const float LaneFrom = 0.25f, LaneTo = 0.45f, LaneSpacing = 8f;
    /// <summary>A chase route bends through a via point only for a target at least this far (m) and only when the detour is at most <see cref="ViaDetour"/> times the direct line (ours).</summary>
    public const float ViaMinDistance = 30f, ViaDetour = 1.4f;

    BotMapPlan(Vector3 centre, Vector3 enemyStart, Vector3? enemyDoor)
    {
        Centre = centre; EnemyStart = enemyStart; EnemyDoor = enemyDoor;
    }

    /// <summary>The centre's floor (the reachable cell nearest the given centre).</summary>
    public Vector3 Centre { get; }
    public Vector3 EnemyStart { get; }
    public Vector3? EnemyDoor { get; }
    /// <summary>The defensive spots around the centre, ordered by angle about it.</summary>
    public IReadOnlyList<Vector3> CentreHolds { get; private set; } = [];
    public IReadOnlyList<Vector3> RoamPoints { get; private set; } = [];
    /// <summary>The spawn-exit lanes, ordered from one side of the map to the other.</summary>
    public IReadOnlyList<Vector3> Lanes { get; private set; } = [];
    public string Note { get; private set; } = "";
    /// <summary>The side's posts on this map (<see cref="BotPosts"/>; empty on maps without anchors). Set by the caller after the build.</summary>
    public IReadOnlyDictionary<BotPosts.Duty, BotPost> Posts { get; set; } = new Dictionary<BotPosts.Duty, BotPost>();

    static float Flat(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Whether <paramref name="p"/> is within <see cref="NoLoiterRadius"/> m (flat) of the enemy's door or start area.</summary>
    public bool InNoLoiterZone(Vector3 p) => Flat(p, EnemyStart) < NoLoiterRadius || EnemyDoor is { } d && Flat(p, d) < NoLoiterRadius;

    /// <summary>
    /// An index for the bot of team rank <paramref name="rank"/> (0, 1, ...) into a list of <paramref name="count"/>: distinct for the first <paramref name="count"/>
    /// ranks and spread over the list (a stride near the golden ratio of the count, co-prime with it), -1 for an empty list.
    /// </summary>
    public static int Spread(int rank, int count)
    {
        if (count <= 0) return -1;
        if (count <= 2) return ((rank % count) + count) % count;
        int stride = Math.Max(1, (int)MathF.Round(count * 0.618f));
        while (Gcd(stride, count) != 1) stride++;
        return (int)(((long)Math.Max(rank, 0) * stride) % count);
    }

    static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    /// <summary>
    /// The plan of a bot on <paramref name="graph"/> whose side starts at <paramref name="ownStart"/>; null when the own start is off the graph. <paramref name="world"/>
    /// (optional) scores the holds for cover. <paramref name="cancel"/> is polled in the loops.
    /// </summary>
    public static BotMapPlan? Build(NavGraph graph, Vector3 ownStart, Vector3 enemyStart, Vector3? enemyDoor, Vector3 centre, CollisionWorld? world = null, CancellationToken cancel = default)
    {
        int start = graph.Nearest(ownStart, 3f, 3f);
        if (start < 0) return null;
        var reach = new bool[graph.NodeCount];
        var queue = new Queue<int>();
        reach[start] = true; queue.Enqueue(start);
        while (queue.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            int n = queue.Dequeue();
            foreach (var (to, _, _) in graph.Links(n)) if (!reach[to]) { reach[to] = true; queue.Enqueue(to); }
            if (graph.Jumps is { } jumps)
                foreach (var (to, _, _) in jumps.From(n)) if (!reach[to]) { reach[to] = true; queue.Enqueue(to); }
        }
        // The centre's floor: the reachable cell nearest the centre (flat, then height).
        int centreNode = -1; float bestCentre = float.MaxValue;
        for (int n = 0; n < reach.Length; n++)
        {
            if (!reach[n]) continue;
            var p = graph.Position(n);
            float d = Flat(p, centre) + 0.25f * MathF.Abs(p.Z - centre.Z);
            if (d < bestCentre) { bestCentre = d; centreNode = n; }
        }
        if (centreNode < 0) return null;
        var plan = new BotMapPlan(graph.Position(centreNode), enemyStart, enemyDoor);
        var c = plan.Centre;

        // Centre holds: the best cell of each sector (cover near a wall, about 10 m out).
        var best = new (Vector3 P, float Score)?[HoldSectors];
        for (int n = 0; n < reach.Length; n++)
        {
            if (!reach[n]) continue;
            if ((n & 4095) == 0) cancel.ThrowIfCancellationRequested();
            var p = graph.Position(n);
            float d = Flat(p, c);
            if (d < HoldRingMin || d > HoldRingMax || MathF.Abs(p.Z - c.Z) > HoldMaxRise || plan.InNoLoiterZone(p)) continue;
            int sector = (int)((MathF.Atan2(p.Y - c.Y, p.X - c.X) + MathF.PI) / (2f * MathF.PI) * HoldSectors) % HoldSectors;
            float score = -MathF.Abs(d - 10f) * 0.25f;
            if (best[sector] is { } b && b.Score >= score + 2f) continue;   // cover can add at most 2: no need to cast
            score += Cover(world, p);
            if (best[sector] is not { } old || score > old.Score) best[sector] = (p, score);
        }
        plan.CentreHolds = best.Where(b => b is not null).Select(b => b!.Value.P).ToList();

        // Roam points: farthest-point sampling over every fourth reachable cell outside the zone and away from the own start, from the centre outward.
        var candidates = new List<Vector3>();
        for (int n = 0; n < reach.Length; n += 4)
        {
            if (!reach[n]) continue;
            var p = graph.Position(n);
            if (plan.InNoLoiterZone(p) || Flat(p, ownStart) < OwnStartClearance) continue;
            candidates.Add(p);
        }
        var roam = new List<Vector3>();
        if (candidates.Count > 0)
        {
            var nearest = new float[candidates.Count];
            int first = 0;
            for (int i = 1; i < candidates.Count; i++) if (Vector3.Distance(candidates[i], c) < Vector3.Distance(candidates[first], c)) first = i;
            roam.Add(candidates[first]);
            for (int i = 0; i < candidates.Count; i++) nearest[i] = Vector3.Distance(candidates[i], candidates[first]);
            while (roam.Count < MaxRoam)
            {
                cancel.ThrowIfCancellationRequested();
                int far = 0;
                for (int i = 1; i < candidates.Count; i++) if (nearest[i] > nearest[far]) far = i;
                if (nearest[far] < RoamSpacing) break;
                roam.Add(candidates[far]);
                for (int i = 0; i < candidates.Count; i++) nearest[i] = MathF.Min(nearest[i], Vector3.Distance(candidates[i], candidates[far]));
            }
        }
        plan.RoamPoints = roam;

        // Lanes: reachable cells in the band, by their offset across the line between the starts; the 10th, 50th and 90th percentiles.
        var axis = new Vector2(enemyStart.X - ownStart.X, enemyStart.Y - ownStart.Y);
        float length = axis.Length();
        var lanes = new List<Vector3>();
        if (length > 1f)
        {
            axis /= length;
            var band = new List<(Vector3 P, float Across)>();
            for (int n = 0; n < reach.Length; n += 2)
            {
                if (!reach[n]) continue;
                var p = graph.Position(n);
                var rel = new Vector2(p.X - ownStart.X, p.Y - ownStart.Y);
                float along = Vector2.Dot(rel, axis) / length;
                if (along < LaneFrom || along > LaneTo || plan.InNoLoiterZone(p)) continue;
                band.Add((p, axis.X * rel.Y - axis.Y * rel.X));
            }
            band.Sort((a, b) => a.Across.CompareTo(b.Across));
            foreach (float q in new[] { 0.1f, 0.5f, 0.9f })
            {
                if (band.Count == 0) break;
                var p = band[Math.Clamp((int)(q * (band.Count - 1)), 0, band.Count - 1)].P;
                if (lanes.All(l => Flat(l, p) >= LaneSpacing)) lanes.Add(p);
            }
        }
        plan.Lanes = lanes;
        plan.Note = $"centre {c.X:F0},{c.Y:F0},{c.Z:F0}; {plan.CentreHolds.Count} centre holds, {roam.Count} roam points, {lanes.Count} lanes; no-loiter {NoLoiterRadius:F0} m around the enemy's "
            + $"{(enemyDoor is { } door ? $"door {door.X:F0},{door.Y:F0} and " : "")}start {enemyStart.X:F0},{enemyStart.Y:F0}";
        return plan;
    }

    /// <summary>Cover at <paramref name="p"/>: 2 near a wall (1 to 4 of 8 directions blocked within 1.5 m), 0 in the open, -1 in a pocket; 0 without a world.</summary>
    static float Cover(CollisionWorld? world, Vector3 p)
    {
        if (world is null) return 0f;
        int cover = 0;
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.PI / 4f;
            if (world.Weapons.Raycast(p + new Vector3(0, 0, 1.0f), new Vector3(MathF.Cos(a), MathF.Sin(a), 0), 1.5f) is not null) cover++;
        }
        return cover is >= 1 and <= 4 ? 2f : cover == 0 ? 0f : -1f;
    }

    /// <summary>
    /// A point to bend a long chase through (<paramref name="me"/> to <paramref name="target"/>), so a side's bots come at the same enemy by different routes: of
    /// the plan's points (roam, holds, lanes) at least 8 m from both ends, outside the zone and with a detour of at most <see cref="ViaDetour"/>, ordered across
    /// the line, the left, middle or right one by <paramref name="rank"/>. Null for a target nearer than <see cref="ViaMinDistance"/> or without such a point.
    /// </summary>
    public Vector3? ViaFor(Vector3 me, Vector3 target, int rank)
    {
        float direct = Flat(me, target);
        if (direct < ViaMinDistance) return null;
        var line = new Vector2(target.X - me.X, target.Y - me.Y) / direct;
        var options = RoamPoints.Concat(CentreHolds).Concat(Lanes)
            .Where(v => Flat(v, me) >= 8f && Flat(v, target) >= 8f && Flat(me, v) + Flat(v, target) <= ViaDetour * direct && !InNoLoiterZone(v))
            .Select(v => (P: v, Across: line.X * (v.Y - me.Y) - line.Y * (v.X - me.X)))
            .OrderBy(v => v.Across).ToList();
        if (options.Count == 0) return null;
        int k = ((rank % 3) + 3) % 3;
        return options[k == 0 ? 0 : k == 1 ? options.Count / 2 : options.Count - 1].P;
    }
}

/// <summary>
/// One bot's idle cycle over a <see cref="BotMapPlan"/> (owner, 2026-11-07; ours): with nobody to chase it walks to its own centre hold (<see cref="BotMapPlan.Spread"/>
/// by team rank) and holds it for <see cref="HoldMin"/> to <see cref="HoldMax"/> s, then walks to a roam point (its own sequence through the list) and back to its
/// hold, and so on. Each life's first leg can be a lane (<see cref="LaneGoal"/>). Seeded (the client uses the player id), so a run repeats for the same inputs.
/// </summary>
public sealed class BotRoamer(int seed)
{
    /// <summary>Seconds a bot holds its centre spot before it roams (uniform; ours).</summary>
    public const double HoldMin = 12, HoldMax = 20;
    /// <summary>Seconds a walk to a hold or roam point may take before the bot gives it up (stuck or unreachable; ours).</summary>
    public const double WalkTimeout = 40;
    /// <summary>Seconds a lane leg may take, and the metres within which a lane or via point counts as reached (ours).</summary>
    public const double LaneTimeout = 25;
    public const float PassRadius = 4f;
    /// <summary>Metres within which a hold or roam point counts as reached (ours).</summary>
    public const float HoldRadius = 1.5f, RoamRadius = 3f;

    public enum Activity { Hold, Roam }

    readonly Random _random = new(seed);
    int _visits;
    double _since = double.NaN, _arrivedAt = double.NaN, _holdFor;
    int _laneLife = -1;
    bool _laneDone = true;
    double _laneSince;

    /// <summary>What the bot is doing now.</summary>
    public Activity Doing { get; private set; } = Activity.Hold;
    /// <summary>Whether the bot stands at its hold (arrived and waiting).</summary>
    public bool Holding => Doing == Activity.Hold && !double.IsNaN(_arrivedAt);
    /// <summary>Roam points visited so far.</summary>
    public int Visits => _visits;

    /// <summary>Back to walking to the hold (a new life, or after a chase or a fight).</summary>
    public void Reset()
    {
        Doing = Activity.Hold; _since = double.NaN; _arrivedAt = double.NaN;
    }

    /// <summary>
    /// The idle goal at <paramref name="now"/> for the bot of team rank <paramref name="rank"/> at <paramref name="me"/>: its hold or its current roam point; null when the
    /// plan has neither. <paramref name="arrived"/> is true while the bot stands at its hold.
    /// </summary>
    public Vector3? IdleGoal(BotMapPlan plan, int rank, Vector3 me, double now, out bool arrived)
    {
        arrived = false;
        var holds = plan.CentreHolds; var roam = plan.RoamPoints;
        if (holds.Count == 0 && roam.Count == 0) return null;
        if (double.IsNaN(_since)) _since = now;
        if (Doing == Activity.Hold && holds.Count == 0) Begin(Activity.Roam, now);
        if (Doing == Activity.Hold)
        {
            var hold = holds[BotMapPlan.Spread(rank, holds.Count)];
            if (double.IsNaN(_arrivedAt))
            {
                if (Dist(me, hold) <= HoldRadius) { _arrivedAt = now; _holdFor = HoldMin + _random.NextDouble() * (HoldMax - HoldMin); }
                else if (now - _since > WalkTimeout && roam.Count > 0) { Begin(Activity.Roam, now); return RoamPoint(roam, rank); }
                else return hold;
            }
            if (now - _arrivedAt >= _holdFor && roam.Count > 0) { Begin(Activity.Roam, now); return RoamPoint(roam, rank); }
            arrived = true;
            return hold;
        }
        var point = RoamPoint(roam, rank);
        if (Dist(me, point) <= RoamRadius || now - _since > WalkTimeout)
        {
            _visits++;
            Begin(Activity.Hold, now);
            if (holds.Count > 0) return holds[BotMapPlan.Spread(rank, holds.Count)];
            return RoamPoint(roam, rank);
        }
        return point;
    }

    Vector3 RoamPoint(IReadOnlyList<Vector3> roam, int rank) => roam[(BotMapPlan.Spread(rank, roam.Count) + _visits) % roam.Count];

    void Begin(Activity what, double now) { Doing = what; _since = now; _arrivedAt = double.NaN; }

    /// <summary>
    /// The first leg of life <paramref name="life"/>: the lane <c>Spread(rank) + life</c> (so a bot changes lane each life and a side's bots take different ones), until
    /// it is within <see cref="PassRadius"/> m, <see cref="LaneTimeout"/> s pass or <see cref="EndLane"/> is called; null after, or without lanes.
    /// </summary>
    public Vector3? LaneGoal(BotMapPlan plan, int rank, int life, Vector3 me, double now)
    {
        if (plan.Lanes.Count == 0) return null;
        if (life != _laneLife) { _laneLife = life; _laneDone = false; _laneSince = now; }
        if (_laneDone) return null;
        var lane = plan.Lanes[(BotMapPlan.Spread(rank, plan.Lanes.Count) + Math.Max(life, 0)) % plan.Lanes.Count];
        if (Dist(me, lane) <= PassRadius || now - _laneSince > LaneTimeout) { _laneDone = true; return null; }
        return lane;
    }

    /// <summary>Ends this life's lane leg (the bot found a fight).</summary>
    public void EndLane() => _laneDone = true;

    static float Dist(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) + MathF.Max(0f, MathF.Abs(a.Z - b.Z) - 1.5f);
}
