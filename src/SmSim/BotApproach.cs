using System.Numerics;

namespace SmSim;

/// <summary>
/// The owner's melee push for Classic PvP test bots (2026-11-08; test script, **ours**, not the original AI): "periodically choose to close in and attempt melee
/// combat. but taking a route that minimizes the enemy's ability to see the bot on the approach. raptor and assault marines in particular try to stay low but
/// approach the enemy with hops and dashes, then jump on them and shoot/melee." This class decides <b>when</b> a bot pushes: every <see cref="RollMin"/> to
/// <see cref="RollMax"/> s it rolls a chance by role (<see cref="ChanceFor"/>); a push lasts until the target dies, the bot dies, it has been in melee for
/// <see cref="MeleeLinger"/> s, or <see cref="Timeout"/> s pass. Seeded (the client uses the player id).
/// </summary>
public sealed class BotPush(int seed)
{
    /// <summary>Seconds between push rolls (uniform; ours).</summary>
    public const double RollMin = 15, RollMax = 30;
    /// <summary>The longest push (s), and how long it goes on once the bot is in melee reach (ours).</summary>
    public const double Timeout = 30, MeleeLinger = 4;
    /// <summary>Chance per roll by role: jump-pack classes most ("in particular"), Tactical sometimes, heavy weapons rarely (ours).</summary>
    public const double AssaultChance = 0.75, TacticalChance = 0.35, HeavyChance = 0.15;
    /// <summary>The farthest target (m, flat) a push starts on (ours).</summary>
    public const float StartRange = 70f;
    /// <summary>
    /// Owner, 2026-11-08: "they should also not push when they are being actively shot at". No push starts within <see cref="UnderFireStart"/> s of the bot's last damage,
    /// and damage within <see cref="UnderFireAbort"/> s ends one that has not closed in yet (ours).
    /// </summary>
    public const double UnderFireStart = 4, UnderFireAbort = 1;

    readonly Random _random = new(seed);
    double _nextRoll = double.NaN, _since, _meleeSince = double.NaN;

    /// <summary>The player the push goes for (-1: no push).</summary>
    public int Target { get; private set; } = -1;
    public bool Active => Target >= 0;
    public int Pushes { get; private set; }

    /// <summary>The roll's chance for a class role (<see cref="ClassLoadout.Role"/>).</summary>
    public static double ChanceFor(string role) => role switch { "assault" => AssaultChance, "tactical" => TacticalChance, "heavy" => HeavyChance, _ => TacticalChance };

    /// <summary>Why the last push ended ("" before any).</summary>
    public string Ended { get; private set; } = "";

    /// <summary>
    /// Per tick: <paramref name="target"/> is the enemy the bot would go for now (-1 none) at <paramref name="distance"/> m; <paramref name="targetAlive"/> whether the
    /// push's own target still lives; <paramref name="inMelee"/> whether the bot is within melee reach of it; <paramref name="sinceDamage"/> seconds since the bot was
    /// last hurt and <paramref name="closedIn"/> whether it is already within its jump or charge range of the target. Returns whether a push is on.
    /// </summary>
    public bool Step(double now, string role, int target, float distance, bool targetAlive, bool inMelee, double sinceDamage = double.MaxValue, bool closedIn = false)
    {
        if (double.IsNaN(_nextRoll)) _nextRoll = now + RollMin + _random.NextDouble() * (RollMax - RollMin);
        if (Active)
        {
            if (!targetAlive) { End(now, "target dead or back in its spawn"); return false; }
            if (now - _since > Timeout) { End(now, "timeout"); return false; }
            if (sinceDamage < UnderFireAbort && !closedIn && !inMelee) { End(now, "under fire"); return false; }
            if (inMelee && double.IsNaN(_meleeSince)) _meleeSince = now;
            if (!double.IsNaN(_meleeSince) && now - _meleeSince > MeleeLinger) { End(now, "melee done"); return false; }
            return true;
        }
        if (now < _nextRoll) return false;
        _nextRoll = now + RollMin + _random.NextDouble() * (RollMax - RollMin);
        if (target < 0 || distance > StartRange || sinceDamage < UnderFireStart || _random.NextDouble() >= ChanceFor(role)) return false;
        Target = target; _since = now; _meleeSince = double.NaN; Pushes++;
        return true;
    }

    /// <summary>Ends the push (the bot died, or the caller gave it up: no safe approach).</summary>
    public void End(double now, string why = "ended")
    {
        if (Active) Ended = why;
        Target = -1; _meleeSince = double.NaN;
        if (_nextRoll < now + RollMin) _nextRoll = now + RollMin + _random.NextDouble() * (RollMax - RollMin);
    }
}

/// <summary>
/// A concealed approach: up to two points to walk through and how much of it the enemy can see (0 to 1), against the straight line's; <see cref="Lead"/> is the seen
/// share up to the last point (the walk before the final rush, jump or charge). <see cref="Safe"/> (owner, 2026-11-08: "only approach for melee if there is safe way to
/// do it out of sight"): there is a hidden point and at most <see cref="BotApproach.SafeExposure"/> of the walk to it is seen.
/// </summary>
public sealed record ApproachPlan(IReadOnlyList<Vector3> Waypoints, float Exposure, float DirectExposure, float Lead = 1f)
{
    public bool Safe => Waypoints.Count > 0 && Lead <= BotApproach.SafeExposure;
}

/// <summary>
/// The route of a melee push (owner, 2026-11-08; ours): the walkable cells near the enemy are scored for how much of the leg to them the enemy sees from its eye
/// (a weapon-collision ray to each sample at chest height), and the bot goes through the best <b>hidden</b> cells: a staging cell <see cref="StageMin"/> to
/// <see cref="StageMax"/> m from the enemy, and before it, for a far enemy, a cover cell <see cref="CoverMin"/> to <see cref="CoverMax"/> m out. Score: walking
/// distance (straight) plus <see cref="ExposureWeight"/> times the seen share of each leg times its length. Cells within <see cref="SampleStride"/> of the graph
/// are sampled to keep it cheap (the client runs it off the frame loop).
/// </summary>
public static class BotApproach
{
    public const float StageMin = 4f, StageMax = 11f, CoverMin = 14f, CoverMax = 28f;
    /// <summary>Metres a seen metre of the leg counts extra (ours).</summary>
    public const float ExposureWeight = 3f;
    /// <summary>The most of the walk to the last hidden point the enemy may see for the push to go ahead (<see cref="ApproachPlan.Safe"/>; ours).</summary>
    public const float SafeExposure = 0.25f;
    /// <summary>Metres between the visibility samples on a leg, the chest height of a sample and the most a cell may differ in height from the enemy (ours).</summary>
    public const float SampleStep = 3f, ChestHeight = 1.2f, MaxRise = 4f;
    /// <summary>Every n-th graph cell is a candidate; the best this many by distance get their legs scored (ours).</summary>
    public const int SampleStride = 3, Scored = 40;

    /// <summary>Whether the enemy's eye sees a body standing at <paramref name="feet"/> (a clear weapon-collision line to its chest).</summary>
    public static bool Seen(CollisionWorld world, Vector3 enemyEye, Vector3 feet)
    {
        var chest = feet + new Vector3(0, 0, ChestHeight);
        var line = chest - enemyEye;
        float length = line.Length();
        return length < 1e-3f || world.Weapons.Raycast(enemyEye, line / length, length - 0.1f) is null;
    }

    /// <summary>The share (0 to 1) of samples every <see cref="SampleStep"/> m from <paramref name="a"/> to <paramref name="b"/> (ends included) the enemy sees.</summary>
    public static float Exposure(CollisionWorld world, Vector3 enemyEye, Vector3 a, Vector3 b)
    {
        float length = Vector3.Distance(a, b);
        int n = Math.Max(1, (int)MathF.Ceiling(length / SampleStep));
        int seen = 0;
        for (int i = 0; i <= n; i++) if (Seen(world, enemyEye, Vector3.Lerp(a, b, i / (float)n))) seen++;
        return seen / (float)(n + 1);
    }

    static float Flat(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    static float LegCost(CollisionWorld world, Vector3 eye, Vector3 a, Vector3 b) => Flat(a, b) * (1f + ExposureWeight * Exposure(world, eye, a, b));

    /// <summary>
    /// The approach from <paramref name="me"/> to an enemy standing at <paramref name="enemy"/> (feet) over <paramref name="graph"/>; with no hidden cell in reach the
    /// waypoints are empty (go straight). <paramref name="cancel"/> is polled.
    /// </summary>
    public static ApproachPlan Plan(NavGraph graph, CollisionWorld world, Vector3 me, Vector3 enemy, float eyeHeight, CancellationToken cancel = default)
    {
        var eye = enemy + new Vector3(0, 0, eyeHeight);
        float direct = Exposure(world, eye, me, enemy);
        float toEnemy = Flat(me, enemy);
        var stage = new List<Vector3>();
        var cover = new List<Vector3>();
        for (int n = 0; n < graph.NodeCount; n += SampleStride)
        {
            if ((n & 8191) == 0) cancel.ThrowIfCancellationRequested();
            var p = graph.Position(n);
            if (MathF.Abs(p.Z - enemy.Z) > MaxRise) continue;
            float d = Flat(p, enemy);
            bool isStage = d >= StageMin && d <= StageMax, isCover = toEnemy > CoverMax && d >= CoverMin && d <= CoverMax && Flat(p, me) < toEnemy;
            if ((!isStage && !isCover) || Seen(world, eye, p)) continue;
            (isStage ? stage : cover).Add(p);
        }
        if (stage.Count == 0) return new ApproachPlan([], direct, direct, direct);
        // Stage cells: the nearest by the straight walk, scored on their legs.
        var stages = stage.OrderBy(p => Flat(me, p) + Flat(p, enemy)).Take(Scored).ToList();
        Vector3? bestCover = null;
        if (cover.Count > 0)
        {
            var covers = cover.OrderBy(p => Flat(me, p) + Flat(p, enemy)).Take(Scored).ToList();
            float best = float.MaxValue;
            foreach (var c in covers)
            {
                cancel.ThrowIfCancellationRequested();
                float cost = LegCost(world, eye, me, c) + Flat(c, enemy);
                if (cost < best) { best = cost; bestCover = c; }
            }
        }
        var from = bestCover ?? me;
        Vector3 bestStage = stages[0];
        float bestCost = float.MaxValue;
        foreach (var s in stages)
        {
            cancel.ThrowIfCancellationRequested();
            float cost = LegCost(world, eye, from, s) + Flat(s, enemy);
            if (cost < bestCost) { bestCost = cost; bestStage = s; }
        }
        var points = bestCover is { } bc ? new List<Vector3> { bc, bestStage } : new List<Vector3> { bestStage };
        float seen = 0f, total = 0f, leadSeen = 0f, leadTotal = 0f;
        var prev = me;
        foreach (var p in points.Append(enemy))
        {
            float len = Flat(prev, p), part = len * Exposure(world, eye, prev, p);
            seen += part; total += len; prev = p;
            if (p != enemy) { leadSeen += part; leadTotal += len; }
        }
        return new ApproachPlan(points, total > 0f ? seen / total : 0f, direct, leadTotal > 0f ? leadSeen / leadTotal : 0f);
    }
}

/// <summary>
/// How a jump-pack bot closes in low (owner, 2026-11-08: "stay low but approach the enemy with hops and dashes, then jump on them"; ours): on the ground, moving,
/// farther than <see cref="JumpInRange"/> m, it alternates forward dashes (a one-tick Jump tap: no power, the evade's own cooldown) and small hops (the shortest
/// jump hold) every <see cref="SpacingMin"/> to <see cref="SpacingMax"/> s, a hop only while the pack keeps enough power for the jump onto the target afterwards. Within
/// <see cref="JumpInRange"/> m it jumps onto the target (the existing jump-and-pound script, limited to that range during a push).
/// </summary>
public sealed class BotHopper(int seed)
{
    /// <summary>Metres within which a pushing jump-pack bot jumps onto the target instead of hopping (ours; the script's own range is 8 to 30 m).</summary>
    public const float JumpInRange = 16f;
    /// <summary>Seconds between the approach's dashes and hops (uniform; ours).</summary>
    public const double SpacingMin = 0.9, SpacingMax = 1.6;

    public enum Move { None, Dash, Hop }

    readonly Random _random = new(seed);
    double _next;
    bool _hopNext;

    public int Dashes { get; private set; }
    public int Hops { get; private set; }

    /// <summary>
    /// The move for this tick: <paramref name="canDash"/> (grounded, the evade not cooling, nothing else under way), <paramref name="power"/> left in the pack and
    /// <paramref name="reserve"/> it must keep (the jump onto the target plus a hop), <paramref name="distance"/> to the target.
    /// </summary>
    public Move Next(double now, bool canDash, bool grounded, float power, float hopCost, float reserve, float distance)
    {
        if (now < _next || !grounded || distance <= JumpInRange) return Move.None;
        bool hop = _hopNext && power >= hopCost + reserve;
        if (!hop && !canDash) return Move.None;
        _next = now + SpacingMin + _random.NextDouble() * (SpacingMax - SpacingMin);
        _hopNext = !_hopNext;
        if (hop) { Hops++; return Move.Hop; }
        Dashes++;
        return Move.Dash;
    }
}
