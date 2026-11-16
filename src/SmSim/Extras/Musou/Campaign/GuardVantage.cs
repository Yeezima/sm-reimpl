using System.Numerics;
using System.Runtime.CompilerServices;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The Guardsmen's vantage points (owner, 2026-11-09: "make small squads of guardsmen position themselves up in buildings randomly at times around the map - they should only position on high ground vantage
/// points with good lines of fire around them within 50m of a friendly captured point. if there is no friendly captured point then they still defend the stronghold/imperial base but also venture out in
/// front of their home base to take up high ground vantage point positions around the imperial base."). Every number is ours (an estimate for the playtest) but the owner's 50 m. A change here raises
/// <see cref="TfwRules.Version"/>. Distances in metres, times in seconds.
/// </summary>
public sealed record GuardVantageRules
{
    /// <summary>The restore switch: false, no squad ever goes up (the Guardsmen as before 2026-11-09).</summary>
    public bool Enabled { get; init; } = true;

    // The finder (GuardVantage.Find), once a level.
    /// <summary>
    /// A vantage stands at least this high (a storey) over the ground round it: the median of the lowest floors of the columns sampled in <see cref="GroundDirections"/> directions at <see cref="GroundRings"/>
    /// (columns with no floor, a solid block's or off the map, left out; at least <see cref="MinGroundSamples"/> needed). The median, so a pit's rim or the middle of a wide plateau is not high ground.
    /// </summary>
    public float MinHeight { get; init; } = 3f;
    public int GroundDirections { get; init; } = 16;
    public float[] GroundRings { get; init; } = [4f, 8f, 12f, 16f];
    public int MinGroundSamples { get; init; } = 16;
    /// <summary>Spots are taken one per bin of this size (and storey) before the costly tests: the highest over its ground, then the one whose distance to the floor's edge is nearest <see cref="EdgePreference"/> (at a roof's or balcony's edge, where the ground below is seen, not in its middle).</summary>
    public float BinSize { get; init; } = 3f;
    public float EdgePreference { get; init; } = 1f;
    /// <summary>The lines of fire: from a man's eye (<see cref="EyeHeight"/>) to the chest (<see cref="ChestHeight"/>) of one standing on the ground in <see cref="Directions"/> directions at <see cref="SightDistances"/>; a spot needs <see cref="MinOpen"/> of them clear.</summary>
    public int Directions { get; init; } = 16;
    public float[] SightDistances { get; init; } = [10f, 20f, 30f];
    public float EyeHeight { get; init; } = 1.5f;
    public float ChestHeight { get; init; } = 1.2f;
    public float MinOpen { get; init; } = 0.3f;
    /// <summary>The height that counts fully in a spot's score (score = open share x (0.5 + 0.5 x min(height, this) / this)).</summary>
    public float ScoreHeight { get; init; } = 12f;
    /// <summary>Room for a squad: at least <see cref="MinPosts"/> posts (of up to <see cref="MaxPosts"/>) on the spot's own floor within <see cref="PostRadius"/>, <see cref="PostSpacing"/> apart, each walkable in a straight line from the spot.</summary>
    public int MinPosts { get; init; } = 3;
    public int MaxPosts { get; init; } = 6;
    public float PostRadius { get; init; } = 2.5f;
    public float PostSpacing { get; init; } = 1.1f;
    /// <summary>Two vantage points stand at least this far apart (flat).</summary>
    public float Spacing { get; init; } = 8f;

    // The squads (TfwBattle.Vantage.cs).
    /// <summary>
    /// The owner's 50 m: a vantage is taken only within this (flat) of a friendly-captured point; with none, within <see cref="BaseRadius"/> of the base, in front of it (towards the Ork HQ, cos at least
    /// <see cref="BaseFrontCos"/>); a level with no such spot that near its base takes them out to <see cref="BaseRadiusFar"/> (Ashfall of 2026-11-09 has no raised floor within 50 m of its base: its nearest in front are 70 to 85 m out).
    /// </summary>
    public float PointRadius { get; init; } = 50f;
    public float BaseRadius { get; init; } = 50f;
    public float BaseRadiusFar { get; init; } = 100f;
    public float BaseFrontCos { get; init; } = 0f;
    /// <summary>A small squad leaves for a vantage every <see cref="IntervalMin"/> to <see cref="IntervalMax"/> seconds (a roll each time), at most <see cref="MostTeams"/> out at once.</summary>
    public float IntervalMin { get; init; } = 20f;
    public float IntervalMax { get; init; } = 45f;
    public int MostTeams { get; init; } = 4;
    /// <summary>A team is <see cref="MenMin"/> to <see cref="MenMax"/> men taken from a field squad standing at its post, which keeps at least <see cref="ParentKeeps"/>.</summary>
    public int MenMin { get; init; } = 3;
    public int MenMax { get; init; } = 6;
    public int ParentKeeps { get; init; } = 4;
    /// <summary>A team holds its vantage <see cref="HoldMin"/> to <see cref="HoldMax"/> seconds, then goes back and rejoins its squad within <see cref="RejoinDistance"/>.</summary>
    public float HoldMin { get; init; } = 60f;
    public float HoldMax { get; init; } = 150f;
    public float RejoinDistance { get; init; } = 6f;

    public static GuardVantageRules Default { get; } = new();
}

/// <summary>One vantage point: where the team's centre stands (a graph node), its height over the ground round it, the share of its lines of fire that are open, its score, the way it looks out (yaw), and its posts.</summary>
public sealed record GuardVantagePoint(Vector3 Position, int Node, float Height, float Open, float Score, float Yaw, Vector3[] Posts);

/// <summary>
/// The finder (owner, 2026-11-09): the vantage points of a level, from the walking graph and the weapons mesh alone, so it works on whatever geometry the level has. A node is a candidate when it stands
/// clearly above the ground round it (<see cref="GuardVantageRules.MinHeight"/> over the median ground of 64 samples out to 16 m: a roof, an upper floor's balcony, the top
/// of a stair) and can be walked to from the base and back (forward and reverse searches over the graph's walk and drop links); one per bin (the highest, then the nearest a floor's edge) goes on to room
/// for a squad (its posts) and the lines of fire (raycasts on the weapons mesh to the ground round it); the best are kept, <see cref="GuardVantageRules.Spacing"/> apart. Deterministic: nodes in order,
/// ties to the lower node. Computed once a level and base, off the tick (<see cref="For"/>).
/// </summary>
public static class GuardVantage
{
    static readonly ConditionalWeakTable<MusouNav, Dictionary<(StaticMesh, int), Task<GuardVantagePoint[]>>> Cache = new();

    /// <summary>
    /// The vantage points of <paramref name="walk"/> and <paramref name="sight"/> reachable from <paramref name="home"/> (and back), worked out once on a thread of its own and kept with the graph; the task
    /// (the caller waits on it when it first needs the points, so the battle is the same whenever it finishes).
    /// </summary>
    public static Task<GuardVantagePoint[]> For(MusouNav walk, StaticMesh sight, Vector3 home, GuardVantageRules rules)
    {
        int node = walk.Nearest(home, 4f, 4f);
        if (!ReferenceEquals(rules, GuardVantageRules.Default)) return Task.FromResult(Find(walk, sight, node, rules));
        var byKey = Cache.GetValue(walk, _ => new());
        lock (byKey)
        {
            if (byKey.TryGetValue((sight, node), out var task)) return task;
            task = walk.NodeCount >= 20_000
                ? Task.Factory.StartNew(() => Find(walk, sight, node, rules), TaskCreationOptions.LongRunning)
                : Task.FromResult(Find(walk, sight, node, rules));
            byKey[(sight, node)] = task;
            return task;
        }
    }

    /// <summary>Spots tested by the last <see cref="Find"/> (bins), and its rays (the cost report).</summary>
    public static int LastCandidates, LastRays;

    /// <summary>The finder itself (see the type). <paramref name="home"/>: the base's node (-1: no reach test).</summary>
    public static GuardVantagePoint[] Find(MusouNav walk, StaticMesh sight, int home, GuardVantageRules rules)
    {
        int n = walk.NodeCount;
        if (n == 0) return [];
        bool[]? reach = null;
        if (home >= 0) reach = Reachable(walk, home);

        // The ground of each column: its lowest floor.
        var ground = new float[walk.Width * walk.Depth];
        Array.Fill(ground, float.PositiveInfinity);
        for (int i = 0; i < n; i++)
        {
            int c = walk.ColumnOf(walk.X(i), walk.Y(i));
            if (c >= 0 && walk.Z(i) < ground[c]) ground[c] = walk.Z(i);
        }

        // Candidates by height, one per bin and storey (the highest over its ground, then the most room).
        var dirs = new Vector2[rules.GroundDirections];
        for (int k = 0; k < dirs.Length; k++) { float a = k * 2f * MathF.PI / dirs.Length; dirs[k] = new Vector2(MathF.Cos(a), MathF.Sin(a)); }
        var samples = new float[dirs.Length * rules.GroundRings.Length];
        var bins = new Dictionary<long, (int Node, float Height, float Edge)>();
        for (int i = 0; i < n; i++)
        {
            if (reach is not null && !reach[i]) continue;
            float z = walk.Z(i), x = walk.X(i), y = walk.Y(i);
            int m = 0;
            foreach (float r in rules.GroundRings)
                foreach (var d in dirs)
                {
                    int c = walk.ColumnOf(x + d.X * r, y + d.Y * r);
                    if (c >= 0 && float.IsFinite(ground[c])) samples[m++] = ground[c];
                }
            if (m < rules.MinGroundSamples) continue;
            Array.Sort(samples, 0, m);
            float height = z - (m % 2 == 1 ? samples[m / 2] : 0.5f * (samples[m / 2 - 1] + samples[m / 2]));
            if (height < rules.MinHeight) continue;
            long key = ((long)MathF.Floor(x / rules.BinSize) & 0xFFFFF) | (((long)MathF.Floor(y / rules.BinSize) & 0xFFFFF) << 20) | (((long)MathF.Floor(z / 2.5f) & 0xFFFFF) << 40);
            float edge = MathF.Abs(walk.EdgeDistance(i) - rules.EdgePreference);
            if (!bins.TryGetValue(key, out var best) || height > best.Height + 0.01f || (MathF.Abs(height - best.Height) <= 0.01f && edge < best.Edge)) bins[key] = (i, height, edge);
        }

        int rays = 0;
        var found = new List<GuardVantagePoint>();
        var reps = bins.Values.OrderBy(b => b.Node).ToArray();
        foreach (var (i, height, _) in reps)
        {
            var p = walk.Position(i);
            var posts = Posts(walk, p, rules);
            if (posts.Length < rules.MinPosts) continue;
            var (open, yaw) = Lines(walk, ground, sight, p, rules, ref rays);
            if (open < rules.MinOpen) continue;
            float score = open * (0.5f + 0.5f * MathF.Min(height, rules.ScoreHeight) / MathF.Max(0.1f, rules.ScoreHeight));
            found.Add(new GuardVantagePoint(p, i, height, open, score, yaw, posts));
        }
        LastCandidates = reps.Length; LastRays = rays;

        // The best first, each at least Spacing from those kept.
        found.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.Node.CompareTo(b.Node));
        var kept = new List<GuardVantagePoint>();
        float s2 = rules.Spacing * rules.Spacing;
        foreach (var v in found)
        {
            bool near = false;
            foreach (var k in kept)
                if (new Vector2(k.Position.X - v.Position.X, k.Position.Y - v.Position.Y).LengthSquared() < s2) { near = true; break; }
            if (!near) kept.Add(v);
        }
        return kept.ToArray();
    }

    /// <summary>Nodes reachable from <paramref name="home"/> and from which <paramref name="home"/> is reachable (walk and drop links).</summary>
    static bool[] Reachable(MusouNav walk, int home)
    {
        int n = walk.NodeCount;
        var there = new bool[n];
        var back = new bool[n];
        var queue = new Queue<int>();
        there[home] = true; queue.Enqueue(home);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            for (int l = walk.ForwardStart(i); l < walk.ForwardEnd(i); l++)
            {
                int t = walk.ForwardTo(l);
                if (!there[t]) { there[t] = true; queue.Enqueue(t); }
            }
        }
        back[home] = true; queue.Enqueue(home);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            for (int l = walk.ReverseStart(i); l < walk.ReverseEnd(i); l++)
            {
                int f = walk.ReverseFrom(l);
                if (!back[f]) { back[f] = true; queue.Enqueue(f); }
            }
        }
        for (int i = 0; i < n; i++) there[i] &= back[i];
        return there;
    }

    /// <summary>
    /// The posts round a spot: of it and 16 places round it (8 at half <see cref="GuardVantageRules.PostRadius"/>, 8 at the whole), those on its own floor (within 0.5 m) walkable in a straight line from it,
    /// <see cref="GuardVantageRules.PostSpacing"/> apart, in that order, at most <see cref="GuardVantageRules.MaxPosts"/>.
    /// </summary>
    static Vector3[] Posts(MusouNav walk, Vector3 p, GuardVantageRules rules)
    {
        var posts = new List<Vector3>(rules.MaxPosts);
        float gap2 = rules.PostSpacing * rules.PostSpacing;
        for (int k = 0; k < 17 && posts.Count < rules.MaxPosts; k++)
        {
            var off = Vector2.Zero;
            if (k > 0)
            {
                float a = (k - 1) % 8 * (MathF.PI / 4f) + (k > 8 ? MathF.PI / 8f : 0f), r = k <= 8 ? rules.PostRadius * 0.5f : rules.PostRadius;
                off = new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            }
            int node = walk.NodeIn(walk.ColumnOf(p.X + off.X, p.Y + off.Y), p.Z, 0.5f);
            if (node < 0) continue;
            if (k > 0 && !walk.Graph.CanWalk(p, off)) continue;
            var q = new Vector3(p.X + off.X, p.Y + off.Y, walk.Z(node));
            bool near = false;
            foreach (var o in posts) if (Vector3.DistanceSquared(o, q) < gap2) { near = true; break; }
            if (!near) posts.Add(q);
        }
        return posts.ToArray();
    }

    /// <summary>The share of the lines of fire from a man's eye at <paramref name="p"/> to a chest on the ground round it that are clear, and the mean way of the clear ones (yaw; 0 with none).</summary>
    static (float Open, float Yaw) Lines(MusouNav walk, float[] ground, StaticMesh sight, Vector3 p, GuardVantageRules rules, ref int rays)
    {
        var eye = p + new Vector3(0f, 0f, rules.EyeHeight);
        int clear = 0, total = 0;
        var sum = Vector2.Zero;
        for (int k = 0; k < rules.Directions; k++)
        {
            float a = k * 2f * MathF.PI / rules.Directions;
            var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
            foreach (float r in rules.SightDistances)
            {
                total++;
                int c = walk.ColumnOf(p.X + d.X * r, p.Y + d.Y * r);
                if (c < 0 || !float.IsFinite(ground[c])) continue;   // no ground there: no man to shoot at
                var target = new Vector3(p.X + d.X * r, p.Y + d.Y * r, ground[c] + rules.ChestHeight);
                var ray = target - eye;
                float length = ray.Length();
                if (length < 1e-3f) continue;
                rays++;
                if (sight.Raycast(eye, ray / length, length) is { } hit && hit.Distance < length - 0.3f) continue;
                clear++;
                sum += d;
            }
        }
        float yaw = sum.LengthSquared() > 1e-6f ? MathF.Atan2(-sum.X, sum.Y) : 0f;   // GuardSim's yaw: forward is (-sin, cos)
        return (total == 0 ? 0f : clear / (float)total, yaw);
    }
}
