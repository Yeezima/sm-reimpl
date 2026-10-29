using System.Numerics;

namespace SmSim;

/// <summary>
/// How a test bot sees the other side's spawn room (M44a; test script, **ours**, not the original AI; spec "Test bots"). The room is sealed by the
/// side's <see cref="SpawnBlocker"/> boxes, which stop the bot's own side, so a path to a player inside ends against a blocker and the old chase made the
/// bot hug the wall outside the door. Built once per side and graph from two <see cref="NavGraph"/>s of the same map (the bot's, in which the enemy blockers
/// are walls, and the enemy side's, in which they are not):
/// <list type="bullet">
/// <item>the cells the bot can get to from its own start area (walks, drops, jump links) are flood-filled;</item>
/// <item>Dijkstra over the <b>enemy's</b> graph from its start area, in order of walking distance, up to <see cref="MaxRoomPath"/> m: the first cell that the bot
/// can reach is the <b>door</b> (the cell just outside the exit; the server's exit rule is the same walk); every cell popped before such cells (the walk does not
/// continue through them) is <b>in the room</b>. So the room is exactly the floor a player can walk from its start area without coming out among floor the bot
/// reaches: a ledge near the room that only a jump reaches is not part of it, and neither is anything above the floor (<see cref="InRoom"/> needs a cell of the
/// enemy graph under the feet);</item>
/// <item>hold points: reachable cells <see cref="HoldMin"/> to <see cref="HoldMax"/> m from the door with a clear weapon-collision line from the eye to the door at
/// chest height, 3 m or more from the blockers, scored for cover and for a middling distance, thinned to points 2.5 m apart (at most <see cref="MaxHolds"/>).</item>
/// </list>
/// </summary>
public sealed class SpawnRoomWatch
{
    /// <summary>Metres from the door a waiting bot stands (owner's request, 2026-10-28).</summary>
    public const float HoldMin = 8f, HoldMax = 15f;
    /// <summary>The farthest the room is searched (walking distance from the enemy's start area, metres).</summary>
    public const float MaxRoomPath = 80f;
    /// <summary>Most hold points kept.</summary>
    public const int MaxHolds = 24;

    readonly NavGraph _enemyGraph;
    readonly bool[] _room;

    SpawnRoomWatch(Team enemy, NavGraph enemyGraph, bool[] room)
    {
        Enemy = enemy;
        _enemyGraph = enemyGraph;
        _room = room;
    }

    /// <summary>The side whose spawn room this is.</summary>
    public Team Enemy { get; }
    /// <summary>The cell just outside the exit (null when the walk from the enemy's start never comes out among cells the bot reaches).</summary>
    public Vector3? Door { get; private set; }
    /// <summary>Where a bot may wait (see the class), best first.</summary>
    public IReadOnlyList<Vector3> Holds { get; private set; } = [];
    /// <summary>How many cells are in the room.</summary>
    public int RoomCells { get; private set; }
    /// <summary>Cells the bot can reach from its own start area.</summary>
    public int ReachableCells { get; private set; }
    /// <summary>Cells with a clear line to the door at 8 to 15 m, before thinning.</summary>
    public int Candidates { get; private set; }
    /// <summary>What was found, for a log line.</summary>
    public string Note { get; private set; } = "";

    /// <summary>
    /// Whether a player at <paramref name="position"/> (feet) stands in the room: on a cell of the room, which needs floor of the enemy's graph under the feet
    /// (within 1.5 m below or above); false in the air, outside, or when no room was found.
    /// </summary>
    public bool InRoom(Vector3 position)
    {
        if (RoomCells == 0) return false;
        int node = _enemyGraph.Nearest(position, 1.0f, 1.5f);
        return node >= 0 && node < _room.Length && _room[node];
    }

    static float Flat(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>
    /// The watch of the bot of side <paramref name="me"/> on <paramref name="graph"/> over <paramref name="enemyGraph"/> (the other side's walking graph),
    /// with both sides' start areas' centres and <paramref name="world"/> for the blockers and the lines of sight. Null when <paramref name="me"/> has no
    /// opponent side or a start is off its graph. <paramref name="cancel"/> is polled in the loops.
    /// </summary>
    public static SpawnRoomWatch? Build(NavGraph graph, NavGraph enemyGraph, Team me, Vector3 ownStart, Vector3 enemyStart, CollisionWorld world, float eyeHeight, CancellationToken cancel = default)
    {
        var enemy = MatchRules.Other(me);
        if (enemy == Team.None) return null;
        int start = graph.Nearest(ownStart, 3f, 3f);
        int enemyNode = enemyGraph.Nearest(enemyStart, 3f, 3f);
        if (start < 0 || enemyNode < 0) return null;

        // What the bot can get to from its own start area (walks, drops and, with a pack, jump links).
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

        // The room and the door: Dijkstra over the enemy's graph. A popped cell that maps onto a cell the bot reaches is outside: the first such is the door, and
        // the walk does not go on through it; every other popped cell is in the room.
        var room = new bool[enemyGraph.NodeCount];
        var best = new float[enemyGraph.NodeCount];
        Array.Fill(best, float.MaxValue);
        var open = new PriorityQueue<int, float>();
        best[enemyNode] = 0f; open.Enqueue(enemyNode, 0f);
        Vector3? door = null;
        int cells = 0, pops = 0;
        while (open.TryDequeue(out int node, out float cost))
        {
            if (cost > best[node]) continue;
            if ((++pops & 4095) == 0) cancel.ThrowIfCancellationRequested();
            var pos = enemyGraph.Position(node);
            if (graph.Nearest(pos, 0.3f, 0.6f) is var m and >= 0 && reach[m])
            {
                door ??= graph.Position(m);
                continue;
            }
            room[node] = true; cells++;
            foreach (var (to, linkCost, _) in enemyGraph.Links(node))
                if (cost + linkCost <= MaxRoomPath && cost + linkCost < best[to]) { best[to] = cost + linkCost; open.Enqueue(to, best[to]); }
        }
        var watch = new SpawnRoomWatch(enemy, enemyGraph, door is null ? new bool[enemyGraph.NodeCount] : room)
        {
            Door = door, RoomCells = door is null ? 0 : cells, ReachableCells = reach.Count(r => r),
        };
        if (door is null)
        {
            watch.Note = $"no door: the {enemy} side's walk from its start area never comes out among floor the bot can reach within {MaxRoomPath:F0} m; {watch.ReachableCells:N0} of {reach.Length:N0} cells reachable";
            return watch;
        }

        // Hold points: reachable cells 8 to 15 m from the door with a clear line to it; cover = walls and props within 1.5 m (eight directions).
        var doorChest = door.Value + new Vector3(0, 0, 1.2f);
        var weapons = world.Weapons;
        var enemyBlockers = world.Blockers.Where(b => b.Side == enemy).ToArray();
        var scored = new List<(Vector3 P, float Score)>();
        for (int n = 0; n < reach.Length; n++)
        {
            if (!reach[n]) continue;
            var p = graph.Position(n);
            float d = Flat(p, door.Value);
            if (d < HoldMin || d > HoldMax || MathF.Abs(p.Z - door.Value.Z) > 2.5f) continue;
            if (enemyBlockers.Any(b => b.HorizontalDistance(p) < 3f)) continue;
            var eye = p + new Vector3(0, 0, eyeHeight);
            var line = doorChest - eye;
            float length = line.Length();
            if (weapons.Raycast(eye, line / length, length - 0.15f) is not null) continue;
            int cover = 0;
            for (int k = 0; k < 8; k++)
            {
                float a = k * MathF.PI / 4f;
                if (weapons.Raycast(p + new Vector3(0, 0, 1.0f), new Vector3(MathF.Cos(a), MathF.Sin(a), 0), 1.5f) is not null) cover++;
            }
            // Near a wall (cover 1 to 4) beats open floor and a corner pocket (more than 4); a middling distance (11 m) beats the ends.
            scored.Add((p, (cover is >= 1 and <= 4 ? 2f : cover == 0 ? 0f : -1f) - MathF.Abs(d - 11f) * 0.25f));
        }
        var picked = new List<Vector3>();
        foreach (var (p, _) in scored.OrderByDescending(s => s.Score))
        {
            if (picked.All(q => Flat(p, q) >= 2.5f)) picked.Add(p);
            if (picked.Count >= MaxHolds) break;
        }
        watch.Holds = picked;
        watch.Candidates = scored.Count;
        watch.Note = $"door {door.Value.X:F0},{door.Value.Y:F0},{door.Value.Z:F0}, {cells:N0} room cells, {scored.Count} cells with a clear line to the door at {HoldMin:F0} to {HoldMax:F0} m, {picked.Count} hold points, {watch.ReachableCells:N0} of {reach.Length:N0} cells reachable";
        return watch;
    }
}
