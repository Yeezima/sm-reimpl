using System.Numerics;

namespace SmSim;

/// <summary>
/// The owner's low-health rule for Classic PvP test bots (2026-11-08; test script, **ours**, not the original AI): "they should prioritize taking cover when their hp
/// gets low to regenerate, then rejoin the fight." A retreat starts when health falls to <see cref="StartShare"/> of the class's maximum and ends when health and
/// armour are back to <see cref="EndShare"/> of theirs (the sim's own regeneration: health after its delay, the armour after its own), or after <see cref="Timeout"/> s.
/// </summary>
public sealed class BotRetreat
{
    /// <summary>Share of the health maximum at or below which a retreat starts, and the share of health and armour that ends it (ours).</summary>
    public const float StartShare = 0.6f, EndShare = 0.9f;
    /// <summary>The longest retreat (s; ours): a bot that cannot recover rejoins anyway.</summary>
    public const double Timeout = 35;

    double _since = double.NaN;

    public bool Active => !double.IsNaN(_since);
    public int Retreats { get; private set; }

    /// <summary>Per tick with the bot's health and armour as shares of their maxima (0 to 1); returns whether it is retreating.</summary>
    public bool Step(double now, float health, float shield)
    {
        if (Active)
        {
            if ((health >= EndShare && shield >= EndShare) || now - _since > Timeout) _since = double.NaN;
            return Active;
        }
        if (health > StartShare) return false;
        _since = now; Retreats++;
        return true;
    }

    /// <summary>Ends the retreat (the bot died).</summary>
    public void End() => _since = double.NaN;
}

/// <summary>
/// Where a retreating bot hides (2026-11-08; ours): the walkable cell <see cref="MinDistance"/> to <see cref="MaxDistance"/> m away that none of the threats' eyes
/// sees (<see cref="BotApproach.Seen"/>), scored by the walk to it plus <see cref="TowardWeight"/> times any metres it brings the bot nearer the nearest threat; the
/// best few by that score are walked on the graph and the shortest walk wins. Where the bot already stands hidden it stays.
/// </summary>
public static class BotCover
{
    public const float MinDistance = 2f, MaxDistance = 30f, MaxRise = 5f;
    /// <summary>Metres a metre toward the nearest threat counts extra, and how near a threat a cover cell may be (ours).</summary>
    public const float TowardWeight = 2f, ThreatClearance = 8f;
    /// <summary>How many of the best-scored cells get a walked path (ours).</summary>
    public const int Walked = 6;

    static float Flat(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Whether no threat's eye sees a body standing at <paramref name="feet"/>.</summary>
    public static bool Hidden(CollisionWorld world, IReadOnlyList<Vector3> threatEyes, Vector3 feet)
    {
        foreach (var eye in threatEyes) if (BotApproach.Seen(world, eye, feet)) return false;
        return true;
    }

    /// <summary>
    /// The cover cell for a bot at <paramref name="me"/> against threats whose eyes are at <paramref name="threatEyes"/> (<paramref name="me"/> when it is hidden already
    /// or there is no threat); null when no hidden cell is in reach. <paramref name="excluded"/> drops cells (the enemy's no-loiter zone).
    /// </summary>
    public static Vector3? Find(NavGraph graph, CollisionWorld world, Vector3 me, IReadOnlyList<Vector3> threatEyes, Func<Vector3, bool>? excluded = null, CancellationToken cancel = default)
    {
        if (threatEyes.Count == 0 || Hidden(world, threatEyes, me)) return me;
        Vector3 nearest = threatEyes.MinBy(e => Flat(e, me));
        float threatNow = Flat(me, nearest);
        var scored = new List<(float Score, Vector3 Cell)>();
        for (int n = 0; n < graph.NodeCount; n++)
        {
            if ((n & 8191) == 0) cancel.ThrowIfCancellationRequested();
            var p = graph.Position(n);
            float d = Flat(p, me);
            if (d < MinDistance || d > MaxDistance || MathF.Abs(p.Z - me.Z) > MaxRise) continue;
            float threat = threatEyes.Min(e => Flat(e, p));
            if (threat < ThreatClearance || excluded?.Invoke(p) == true) continue;
            float score = d + TowardWeight * MathF.Max(0f, threatNow - Flat(p, nearest));
            if (scored.Count >= 400 && score >= scored[^1].Score) continue;
            if (!Hidden(world, threatEyes, p)) continue;
            int at = scored.FindIndex(s => s.Score > score);
            scored.Insert(at < 0 ? scored.Count : at, (score, p));
            if (scored.Count > 400) scored.RemoveAt(scored.Count - 1);
        }
        if (scored.Count == 0) return null;
        Vector3? best = null;
        float bestWalk = float.MaxValue;
        foreach (var (score, cell) in scored.Take(Walked))
        {
            cancel.ThrowIfCancellationRequested();
            var path = graph.FindPath(me, cell, 1.5f, 60_000);
            if (path is null || path.Partial) continue;
            float walk = path.Length + (score - Flat(cell, me));
            if (walk < bestWalk) { bestWalk = walk; best = cell; }
        }
        return best ?? scored[0].Cell;
    }
}

/// <summary>A post a bot holds: where it stands and the way it faces while nothing is in sight.</summary>
public sealed record BotPost(string Name, Vector3 Spot, Vector3 Face);

/// <summary>
/// The owner's posts for Classic PvP test bots on Manufactorum (2026-11-08; test script, **ours**): "make one boss play at the top of the side ramps, one devastator on
/// top of the balcony next to the capture points A and C. the rest play around the center of the map and the side lanes." Read as: per side, one bot holds the top of a
/// side ramp and one Devastator (Havoc) the balcony beside A or C; each side takes the ramp and the balcony on its own half (nearer its start area), so the two posts
/// watch opposite flanks. The anchors were read from our navigation graph of <c>mp_tdm_09</c> (the 6 m decks reached by the ramps west of A's half and east of C's
/// half, and the 8 m balconies beside them, between the points and B); each anchor is snapped to the graph cell above its height that sees the most of the points.
/// </summary>
public static class BotPosts
{
    public enum Duty { None, Ramp, Balcony }

    /// <summary>One anchor: its kind, a point on it, the lowest floor height that counts as on it, and how far from the point a cell may be (m; ours).</summary>
    public sealed record Anchor(Duty Kind, string Name, Vector3 At, float MinZ, float Radius);

    /// <summary>The anchors of a map (none but Manufactorum's).</summary>
    public static IReadOnlyList<Anchor> Anchors(string map) => map.Equals("mp_tdm_09", StringComparison.OrdinalIgnoreCase)
        ? [
            new(Duty.Ramp, "ramp top on A's flank", new(151, -840, 6), 5.5f, 5f),
            new(Duty.Ramp, "ramp top on C's flank", new(249, -762, 6), 5.5f, 5f),
            new(Duty.Balcony, "balcony beside A", new(155, -792, 8), 7.5f, 9f),
            new(Duty.Balcony, "balcony beside C", new(245, -812, 8), 7.5f, 9f),
          ]
        : [];

    static float Flat(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>
    /// The side's posts: for each duty the anchor nearer <paramref name="ownStart"/>, snapped to a graph cell (the one on it that sees the most of
    /// <paramref name="watch"/>, the nearer to the anchor on a tie), facing <paramref name="enemyStart"/>. A duty without a cell is left out.
    /// </summary>
    public static IReadOnlyDictionary<Duty, BotPost> Build(string map, NavGraph graph, CollisionWorld? world, Vector3 ownStart, Vector3 enemyStart, IReadOnlyList<Vector3> watch, float eyeHeight, CancellationToken cancel = default)
    {
        var posts = new Dictionary<Duty, BotPost>();
        foreach (var group in Anchors(map).GroupBy(a => a.Kind))
        {
            var anchor = group.MinBy(a => Flat(a.At, ownStart))!;
            Vector3? best = null;
            int bestSeen = -1;
            float bestD = float.MaxValue;
            for (int n = 0; n < graph.NodeCount; n++)
            {
                if ((n & 8191) == 0) cancel.ThrowIfCancellationRequested();
                var p = graph.Position(n);
                float d = Flat(p, anchor.At);
                if (d > anchor.Radius || p.Z < anchor.MinZ || p.Z > anchor.At.Z + 3f) continue;
                int seen = 0;
                if (world is not null)
                    foreach (var w in watch)
                    {
                        var eye = p + new Vector3(0, 0, eyeHeight);
                        if (BotApproach.Seen(world, eye, w)) seen++;
                    }
                if (seen > bestSeen || (seen == bestSeen && d < bestD)) { best = p; bestSeen = seen; bestD = d; }
            }
            if (best is { } spot) posts[anchor.Kind] = new BotPost(anchor.Name, spot, enemyStart);
        }
        return posts;
    }

    /// <summary>
    /// The duty of player <paramref name="me"/> on a side whose players (with their class roles, <see cref="ClassLoadout.Role"/>) are <paramref name="team"/>
    /// (itself included): the balcony is the lowest id with the heavy role (Devastator, Havoc); the ramp the lowest Tactical id, else the lowest id that is neither heavy
    /// nor on the balcony. Everyone else has none (centre and lanes).
    /// </summary>
    public static Duty DutyOf(int me, IEnumerable<(int Id, string Role)> team)
    {
        var list = team.OrderBy(t => t.Id).ToList();
        int First(Func<(int Id, string Role), bool> f) { foreach (var t in list) if (f(t)) return t.Id; return -1; }
        int balcony = First(t => t.Role == "heavy");
        int ramp = First(t => t.Role == "tactical");
        if (ramp < 0) ramp = First(t => t.Role != "heavy" && t.Id != balcony);
        return me == balcony ? Duty.Balcony : me == ramp ? Duty.Ramp : Duty.None;
    }
}
