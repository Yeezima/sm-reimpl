using System.Numerics;

namespace SmSim;

/// <summary>
/// One jump-pack link of a <see cref="NavJumpSet"/> as a path waypoint carries it: take-off node, heading
/// (<see cref="NavJumpFlight.Heading"/>), hold in ticks and the power it needs. <see cref="Hold"/> 0 means "no jump" (a walk
/// or drop waypoint).
/// </summary>
public readonly record struct NavJumpMove(int From, byte Direction, byte Hold, float Power)
{
    public bool IsJump => Hold > 0;
}

/// <summary>
/// The controls of a test bot's navigation jump (test script, not the original AI), shared by the builder's simulation and
/// the <see cref="NavAgent"/> so that a link is replayed exactly as it was checked: stand still at the take-off cell, press
/// Jump; no stick through the take-off until the lift-off tick, then the stick fully forward along a fixed heading (one of
/// <see cref="Directions"/> yaws) through the ascent, hang and fall, the Jump button held for <c>hold</c> ticks after the
/// press (the pack's hold time raises the jump; M45: a release while rising cuts the rise); landing ends it.
/// </summary>
public static class NavJumpFlight
{
    /// <summary>
    /// Version of the movement, jump and collision rules the flights were checked against; hashed into the jump cache key.
    /// **Bump it with any change to the capsule motor, the jump pack, the movement step or the collision (anything that can
    /// change where a flight lands)**: cached links are keyed by it and would otherwise be reused stale.
    /// </summary>
    public const int SimulationVersion = 9;   // 9: air control doubled, the charge ignoring W, stagger and interrupt not holding, the lunge on the press tick (sim/combat-2); 8: the three jumps by the hold, the M45 cut off (feel/jump-pack-2); 7: movement × 0.9 and input buffering (feel/buffer-speed); 6: jump-pack air control and the slash's own dive (feel/jump-pack); 2: the spawn-room blockers reach above the level (M31); 3: a tap (released before 0.167 s) is the dash, two full jumps fit (M44); 4: a release while rising cuts the rise (M45); 5: the walk-off drop and the slash (protocol 30)

    /// <summary>Headings a link can take off in (a yaw each, 360 / 16 = 22.5 degrees apart).</summary>
    public const int Directions = 16;

    /// <summary>
    /// M44: the shortest hold (ticks after the press, as <see cref="Controls"/> counts it) that is a jump for the class: 1 without a jump-pack dash;
    /// with one, Jump must still be down on the dash's decision tick (the first whose time reaches min(<see cref="EvadeSpec.ToJumpAt"/>, the lift-off)),
    /// i.e. held for that many ticks after the press. Shorter holds dash.
    /// </summary>
    public static int ShortestJumpHold(SimConfig config, int playerClass)
    {
        if (Loadouts.JumpPack(config, playerClass) is not { } pack || Loadouts.Evade(config, playerClass) is not { Dash: true } dash) return 1;
        float decide = MathF.Min(dash.ToJumpAt, pack.TakeOffDelay);
        return Math.Max(1, (int)MathF.Ceiling(decide / config.TickSeconds - 1e-3f));
    }

    /// <summary>Yaw (radians, 0 faces +Y) of heading <paramref name="direction"/>.</summary>
    public static float Yaw(int direction) => direction * (2f * MathF.PI / Directions);

    /// <summary>Unit ground-plane vector of heading <paramref name="direction"/> (forward at its yaw).</summary>
    public static Vector2 Heading(int direction)
    {
        float yaw = Yaw(direction);
        return new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw));
    }

    /// <summary>
    /// The controls for a tick given the jump state at its start: whether the stick is pushed forward and whether the Jump
    /// button is down. Phase None is the press tick; <see cref="JumpPhase.Landed"/> and the rest: nothing.
    /// </summary>
    public static (bool Stick, bool Button) Controls(JumpPhase phase, int jumpTicks, int hold, JumpPackSpec pack, float dt)
    {
        switch (phase)
        {
            case JumpPhase.None: return (false, true);
            case JumpPhase.TakeOff:
                // The lift-off is the tick on which the take-off delay has passed (JumpPack.Begin counts this tick in).
                return ((jumpTicks + 1) * dt + 1e-5f >= pack.TakeOffDelay, jumpTicks < hold);
            case JumpPhase.Ascent: return (true, jumpTicks < hold);
            case JumpPhase.Hang or JumpPhase.Fall: return (true, false);
            default: return (false, false);
        }
    }

    /// <summary>The input of a tick under <see cref="Controls"/>.</summary>
    public static PlayerInput Input(uint tick, PlayerState s, int direction, int hold, JumpPackSpec pack, float dt)
    {
        var (stick, button) = Controls(s.Jump, s.JumpTicks, hold, pack, dt);
        return new PlayerInput(tick, 0f, stick ? 1f : 0f, Yaw(direction), button ? InputButtons.Jump : InputButtons.None, 0f);
    }

    /// <summary>What <see cref="Simulate"/> found.</summary>
    /// <param name="Landed">The jump ended in a landing (not a death, a lost jump or a time-out).</param>
    /// <param name="Position">Where it landed (feet), else where it ended.</param>
    /// <param name="Ticks">Ticks from the press to the end.</param>
    /// <param name="Apex">Highest point above the take-off (metres).</param>
    public readonly record struct Result(bool Landed, Vector3 Position, int Ticks, float Apex, string Note);

    /// <summary>
    /// Runs one jump with the simulation's own rules (<see cref="Combat.StepPlayer"/>, the player's class and side on
    /// <paramref name="world"/>) from rest at <paramref name="start"/>: the controls above, until the landing, a kill
    /// volume's death, the jump not starting (power) or <paramref name="maxSeconds"/>. Pure and thread-safe.
    /// </summary>
    public static Result Simulate(CollisionWorld world, SimConfig config, int playerClass, Team team, Vector3 start, int direction, int hold, float maxSeconds = 8f)
    {
        var pack = Loadouts.JumpPack(config, playerClass);
        if (pack is null) return new Result(false, start, 0, 0f, "no jump pack");
        float dt = config.TickSeconds;
        var s = Combat.Respawned(new PlayerState(1, start, Vector3.Zero, Yaw(direction), true) { Class = playerClass, Team = team }, config);
        int max = (int)(maxSeconds / dt);
        float apex = 0f;
        for (int k = 0; k < max; k++)
        {
            var from = s.Position;
            s = Combat.StepPlayer(s, Input((uint)k, s, direction, hold, pack, dt), config, out _, world);
            apex = MathF.Max(apex, s.Position.Z - start.Z);
            if (!s.Position.X.Equals(s.Position.X) || !s.Position.Y.Equals(s.Position.Y) || !s.Position.Z.Equals(s.Position.Z))
                return new Result(false, s.Position, k, apex, "not finite");
            if (world.KillsPlayer(from, s.Position, config.CapsuleHeight)) return new Result(false, s.Position, k, apex, "killed");
            if (s.Jump == JumpPhase.Landed) return new Result(true, s.Position, k + 1, apex, "landed");
            if (k > 0 && s.Jump == JumpPhase.None) return new Result(false, s.Position, k, apex, "jump lost");
        }
        return new Result(false, s.Position, max, apex, "time-out");
    }
}

/// <summary>What to build (test aid): which take-off cells, headings and holds to try and which landings to keep.</summary>
public sealed record NavJumpOptions
{
    /// <summary>Take-off cells: boundary cells (a missing walk link) on every <c>Stride</c>th column of the grid.</summary>
    public int Stride { get; init; } = 2;
    /// <summary>
    /// Holds (ticks after the press) to try: a tap, a short, a medium and a full hold. M44: for a class with a jump-pack dash a hold under the
    /// decision tick (<see cref="NavJumpFlight.ShortestJumpHold"/>: 5 at 30 Hz with <see cref="EvadeSpec.ToJumpAt"/> 0.167 s) is a dash, not a
    /// jump; the builder raises such holds to the shortest jump. M45: letting go while rising cuts the rise (the middle jump), so the hold now
    /// sets the height over the whole ascent: [1, 14, 19, 60] is flown as [5, 14, 19, 60], about 2, 9, 12 and 17.5 m high on the install's
    /// values (60 holds past the full jump's apex, about 47 ticks after the press). feel/jump-pack-2: with the three jumps by the hold
    /// (<see cref="JumpPackSpec.HoldTiers"/>, the install) 5 flies the small jump (about 7.7 m) and 14, 19 and 60 the full one (17.5 m).
    /// </summary>
    public int[] Holds { get; init; } = [1, 14, 19, 60];
    /// <summary>Shortest flight (metres, horizontally) worth a link.</summary>
    public float MinFlat { get; init; } = 3f;
    /// <summary>A landing must be farther than <c>WalkFactor</c> times the flight plus <c>WalkExtra</c> metres by foot to be kept.</summary>
    public float WalkFactor { get; init; } = 2f;
    public float WalkExtra { get; init; } = 12f;
    /// <summary>Most links kept per take-off cell.</summary>
    public int MaxPerNode { get; init; } = 10;
    /// <summary>Landings of one take-off cell closer together than this (metres) are one.</summary>
    public float DistinctLanding { get; init; } = 2f;
    /// <summary>A landing is the graph cell within this horizontal distance (metres) of where the jump ended and within <c>LandingHeight</c>.</summary>
    public float LandingReach { get; init; } = 0.75f;
    public float LandingHeight { get; init; } = 0.5f;
    /// <summary>A flight must land the same from <c>RobustPoints</c> points on a ring of this radius (metres) around the take-off cell's centre (the agent stops near it, not on it); 0: only from the centre.</summary>
    public float RobustRadius { get; init; } = 0.3f;
    public int RobustPoints { get; init; } = 8;
    /// <summary>Cost (metres of walking) added to a jump link on top of its flight length: stopping, take-off and landing locks.</summary>
    public float Penalty { get; init; } = 20f;

    internal ulong Hash(ulong h)
    {
        h = Geometry.Fnv(h, (uint)Stride);
        foreach (int v in Holds) h = Geometry.Fnv(h, (uint)v);
        foreach (float f in new[] { MinFlat, WalkFactor, WalkExtra, DistinctLanding, LandingReach, LandingHeight, Penalty, RobustRadius })
            h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(f));
        h = Geometry.Fnv(Geometry.Fnv(h, (uint)MaxPerNode), (uint)RobustPoints);
        return h;
    }
}

/// <summary>
/// Jump-pack links over a <see cref="NavGraph"/>'s nodes (test aid): take-off node → landing node, with the heading, the
/// hold and the power the jump needs. Each was found by running the real jump (<see cref="NavJumpFlight.Simulate"/>) from rest
/// at the take-off cell against the same collision the bots move in, so the capsule's sweeps along the arc (walls, ceilings,
/// kill volumes) are part of the check, and the landing is a graph cell (standing room, headroom). Attach with
/// <see cref="NavGraph.WithJumps"/>; only jump classes' graphs carry them.
/// </summary>
public sealed class NavJumpSet
{
    // Bump on ANY change to how links are found or written that the options and the hashed numbers do not capture.
    const int FormatVersion = 2;
    const int Magic = 0x504D554A;   // "JUMP"

    readonly int[] _start;     // per node: first link; length nodes + 1
    readonly int[] _to;
    readonly float[] _cost;
    readonly byte[] _direction;
    readonly byte[] _hold;
    readonly float[] _power;

    public ulong Key { get; }
    public int NodeCount => _start.Length - 1;
    public int LinkCount => _to.Length;
    /// <summary>Take-off cells tried, flights simulated and milliseconds spent (0 when read from a cache).</summary>
    public int TakeOffsTried { get; init; }
    public long Simulated { get; init; }
    public long BuildMilliseconds { get; init; }
    public string BuildNote { get; init; } = "";

    internal NavJumpSet(int nodes, List<(int From, int To, float Cost, byte Direction, byte Hold, float Power)> links, ulong key)
    {
        links.Sort((a, b) => a.From != b.From ? a.From.CompareTo(b.From) : a.To != b.To ? a.To.CompareTo(b.To) : a.Hold.CompareTo(b.Hold));
        _start = new int[nodes + 1];
        _to = new int[links.Count]; _cost = new float[links.Count]; _direction = new byte[links.Count]; _hold = new byte[links.Count]; _power = new float[links.Count];
        int at = 0;
        for (int n = 0; n < nodes; n++)
        {
            _start[n] = at;
            while (at < links.Count && links[at].From == n)
            {
                _to[at] = links[at].To; _cost[at] = links[at].Cost; _direction[at] = links[at].Direction; _hold[at] = links[at].Hold; _power[at] = links[at].Power;
                at++;
            }
        }
        _start[nodes] = at;
        Key = key;
    }

    NavJumpSet(int[] start, int[] to, float[] cost, byte[] direction, byte[] hold, float[] power, ulong key)
    {
        (_start, _to, _cost, _direction, _hold, _power, Key) = (start, to, cost, direction, hold, power, key);
    }

    internal int Start(int node) => _start[node];
    internal int End(int node) => _start[node + 1];
    internal int To(int link) => _to[link];
    internal float Cost(int link) => _cost[link];
    internal NavJumpMove Move(int link, int from) => new(from, _direction[link], _hold[link], _power[link]);

    /// <summary>The links leaving <paramref name="node"/>: (landing node, cost, move).</summary>
    public IEnumerable<(int To, float Cost, NavJumpMove Move)> From(int node)
    {
        for (int l = _start[node]; l < _start[node + 1]; l++) yield return (_to[l], _cost[l], Move(l, node));
    }

    /// <summary>
    /// The cache key of the set <see cref="Build"/> makes for <paramref name="graph"/>, the pack and the movement numbers the
    /// flights depend on: the graph's key (mesh, kill volumes, settings, area), the options and every number of the pack
    /// and of the capsule, step, slope, fall and gravity settings.
    /// </summary>
    public static ulong KeyFor(NavGraph graph, SimConfig config, int playerClass, NavJumpOptions options)
    {
        ulong h = Geometry.Fnv(Geometry.Fnv(Geometry.Fnv(graph.SourceHash, FormatVersion), (uint)NavJumpFlight.Directions), (uint)NavJumpFlight.SimulationVersion);
        h = options.Hash(h);
        if (Loadouts.JumpPack(config, playerClass) is { } pack)
        {
            using var buffer = new MemoryStream();
            using (var w = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true)) pack.Write(w);
            foreach (byte b in buffer.ToArray()) h = Geometry.Fnv(h, b);
        }
        foreach (float f in new[] { config.CapsuleRadius, config.CapsuleHeight, config.StepHeight, config.MaxWalkableSlope, config.MaxFallSpeed, config.Gravity, config.TickRate, config.RunSpeed, config.GroundTimeToMaxSpeed })
            h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(f));
        return h;
    }

    // ------------------------------------------------------------------ build

    /// <summary>
    /// Finds the jump links of <paramref name="graph"/> for a player of <paramref name="playerClass"/> and <paramref name="team"/>
    /// on <paramref name="world"/> (which must be the world the graph was built from, so that <see cref="CollisionWorld.CharacterFor"/>
    /// is the graph's mesh). Take-off cells: boundary cells (fewer than 8 walk links) on every <see cref="NavJumpOptions.Stride"/>th
    /// column. For each, every heading and hold is flown with the real jump rules; a flight that lands within reach of a graph
    /// cell (standing room, headroom, outgoing links) at least <see cref="NavJumpOptions.MinFlat"/> away is a candidate, kept
    /// when walking there is much longer than flying (or impossible) and the take-off cell has no nearer landing like it.
    /// Parallel over the take-off cells (all cores but one); deterministic. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="cancel"/> is cancelled.
    /// </summary>
    public static NavJumpSet Build(NavGraph graph, CollisionWorld world, Team team, SimConfig config, int playerClass, NavJumpOptions? options = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(config);
        var o = options ?? new NavJumpOptions();
        var pack = Loadouts.JumpPack(config, playerClass) ?? throw new ArgumentException("The class has no jump pack.", nameof(playerClass));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        float dt = config.TickSeconds;
        int stride = Math.Max(1, o.Stride);
        // M44: holds under the dash's decision tick would dash; they are flown as the shortest jump instead.
        int shortest = NavJumpFlight.ShortestJumpHold(config, playerClass);
        var holds = o.Holds.Select(h => Math.Max(h, shortest)).Distinct().ToArray();
        var takeOffs = new List<int>();
        for (int n = 0; n < graph.NodeCount; n++)
        {
            var (cx, cy) = graph.CellOf(n);
            if (cx % stride != 0 || cy % stride != 0) continue;
            if (graph.WalkLinkCount(n) < 8) takeOffs.Add(n);
        }
        long simulated = 0;
        var perNode = new List<(int From, int To, float Cost, byte Direction, byte Hold, float Power)>[takeOffs.Count];
        // One core is left to the game; the build stops at the next take-off cell when cancelled.
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1), CancellationToken = cancel };
        Parallel.For(0, takeOffs.Count, parallel, () => new NavGraph.DijkstraScratch(graph.NodeCount), (i, _, scratch) =>
        {
            int a = takeOffs[i];
            var start = graph.Position(a);
            var cands = new List<(int To, float Flat, byte Direction, byte Hold, float Power)>();
            long sims = 0;
            for (int d = 0; d < NavJumpFlight.Directions; d++)
                foreach (int hold in holds)
                {
                    sims++;
                    var r = NavJumpFlight.Simulate(world, config, playerClass, team, start, d, hold);
                    if (!r.Landed) continue;
                    float flat = new Vector2(r.Position.X - start.X, r.Position.Y - start.Y).Length();
                    if (flat < o.MinFlat) continue;
                    int b = graph.Nearest(r.Position, o.LandingReach, o.LandingHeight);
                    if (b < 0 || b == a) continue;
                    var landing = graph.Position(b);
                    if (new Vector2(landing.X - r.Position.X, landing.Y - r.Position.Y).Length() > o.LandingReach || MathF.Abs(landing.Z - r.Position.Z) > o.LandingHeight) continue;
                    if (!graph.Links(b).Any()) continue;
                    float power = pack.CostAt(hold * dt);
                    cands.Add((b, flat, (byte)d, (byte)hold, power));
                }
            System.Threading.Interlocked.Add(ref simulated, sims);
            var kept = new List<(int From, int To, float Cost, byte Direction, byte Hold, float Power)>();
            if (cands.Count > 0)
            {
                // Walking to each landing: a bounded search over the walk and drop links from the take-off cell.
                float limit = cands.Max(c => c.Flat) * o.WalkFactor + o.WalkExtra;
                var targets = cands.Select(c => c.To).ToArray();
                var walk = new float[targets.Length];
                graph.WalkCosts(a, targets, limit, scratch, walk);
                var ranked = Enumerable.Range(0, cands.Count)
                    .Where(k => walk[k] > cands[k].Flat * o.WalkFactor + o.WalkExtra)
                    .OrderByDescending(k => float.IsPositiveInfinity(walk[k]) ? 1e9f : walk[k] - cands[k].Flat)
                    .ThenBy(k => cands[k].To).ThenBy(k => cands[k].Power).ThenBy(k => cands[k].Hold).ThenBy(k => cands[k].Direction)
                    .ToList();
                foreach (int k in ranked)
                {
                    if (kept.Count >= o.MaxPerNode) break;
                    var landing = graph.Position(cands[k].To);
                    if (kept.Any(m => { var p = graph.Position(m.To); return new Vector2(p.X - landing.X, p.Y - landing.Y).Length() < o.DistinctLanding && MathF.Abs(p.Z - landing.Z) < 1f; })) continue;
                    // The agent stops within a few centimetres of the cell's centre, not on it: the flight must land the same from there too.
                    if (!Robust(world, config, playerClass, team, start, cands[k].Direction, cands[k].Hold, graph.Position(cands[k].To), o)) continue;
                    kept.Add((a, cands[k].To, cands[k].Flat + o.Penalty, cands[k].Direction, cands[k].Hold, cands[k].Power));
                }
            }
            perNode[i] = kept;
            return scratch;
        }, _ => { });
        var all = new List<(int From, int To, float Cost, byte Direction, byte Hold, float Power)>();
        foreach (var list in perNode) all.AddRange(list);
        var set = new NavJumpSet(graph.NodeCount, all, KeyFor(graph, config, playerClass, o))
        {
            TakeOffsTried = takeOffs.Count, Simulated = simulated, BuildMilliseconds = watch.ElapsedMilliseconds,
            BuildNote = $"{takeOffs.Count} take-off cells, {simulated:N0} flights simulated, {all.Count} links kept ({all.Select(l => l.From).Distinct().Count()} cells), {watch.ElapsedMilliseconds} ms",
        };
        return set;
    }

    /// <summary>Whether the flight lands on the same cell (within a metre) from every point of a ring of <see cref="NavJumpOptions.RobustRadius"/> around the take-off cell.</summary>
    static bool Robust(CollisionWorld world, SimConfig config, int playerClass, Team team, Vector3 start, int direction, int hold, Vector3 landing, NavJumpOptions o)
    {
        if (o.RobustRadius <= 0f) return true;
        for (int k = 0; k < o.RobustPoints; k++)
        {
            float angle = k * (2f * MathF.PI / o.RobustPoints);
            var from = start + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * o.RobustRadius;
            var r = NavJumpFlight.Simulate(world, config, playerClass, team, from, direction, hold);
            if (!r.Landed || new Vector2(r.Position.X - landing.X, r.Position.Y - landing.Y).Length() > 1f || MathF.Abs(r.Position.Z - landing.Z) > o.LandingHeight) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ cache

    const int HeaderBytes = 4 + 4 + 8 + 4 + 4;

    static ulong Checksum(ReadOnlySpan<byte> data)
    {
        ulong h = Geometry.FnvOffset;
        foreach (byte b in data) h = (h ^ b) * 1099511628211UL;
        return h;
    }

    /// <summary>Writes the set: versioned header with the key and sizes, the arrays, a trailing checksum.</summary>
    public void Write(Stream stream)
    {
        using var buffer = new MemoryStream();
        using (var w = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic); w.Write(FormatVersion); w.Write(Key);
            w.Write(NodeCount); w.Write(LinkCount);
            foreach (int v in _start) w.Write(v);
            foreach (int v in _to) w.Write(v);
            foreach (float v in _cost) w.Write(v);
            w.Write(_direction); w.Write(_hold);
            foreach (float v in _power) w.Write(v);
        }
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        stream.Write(bytes);
        stream.Write(BitConverter.GetBytes(Checksum(bytes)));
    }

    /// <summary>Reads a set written by <see cref="Write"/>; null when the format, the key, the node count or the data differ or are damaged.</summary>
    public static NavJumpSet? Read(Stream stream, int expectedNodes, ulong expectedKey)
    {
        try
        {
            var head = new byte[HeaderBytes];
            if (stream.ReadAtLeast(head, HeaderBytes, throwOnEndOfStream: false) < HeaderBytes) return null;
            int magic = BitConverter.ToInt32(head, 0), version = BitConverter.ToInt32(head, 4);
            ulong key = BitConverter.ToUInt64(head, 8);
            int nodes = BitConverter.ToInt32(head, 16), links = BitConverter.ToInt32(head, 20);
            if (magic != Magic || version != FormatVersion || key != expectedKey || nodes != expectedNodes || links < 0 || links > 200_000_000) return null;
            long body = 4L * (nodes + 1) + 4L * links + 4L * links + links + links + 4L * links;
            if (stream.CanSeek && stream.Length - stream.Position != body + 8) return null;
            if (body + 8 > int.MaxValue) return null;
            var data = new byte[body + 8];
            if (stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false) != data.Length || stream.ReadByte() != -1) return null;
            var all = new byte[HeaderBytes + body];
            head.CopyTo(all, 0);
            Array.Copy(data, 0, all, HeaderBytes, body);
            if (Checksum(all) != BitConverter.ToUInt64(data, (int)body)) return null;
            int at = 0;
            int[] Ints(int count) { var a = new int[count]; Buffer.BlockCopy(data, at, a, 0, count * 4); at += count * 4; return a; }
            float[] Floats(int count) { var a = new float[count]; Buffer.BlockCopy(data, at, a, 0, count * 4); at += count * 4; return a; }
            var start = Ints(nodes + 1); var to = Ints(links); var cost = Floats(links);
            var direction = data.AsSpan(at, links).ToArray(); at += links;
            var hold = data.AsSpan(at, links).ToArray(); at += links;
            var power = Floats(links);
            if (start[0] != 0 || start[^1] != links) return null;
            for (int n = 0; n < nodes; n++) if (start[n] > start[n + 1]) return null;
            for (int l = 0; l < links; l++)
                if (to[l] < 0 || to[l] >= nodes || !float.IsFinite(cost[l]) || cost[l] < 0f || !float.IsFinite(power[l]) || direction[l] >= NavJumpFlight.Directions || hold[l] == 0) return null;
            return new NavJumpSet(start, to, cost, direction, hold, power, expectedKey);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ArgumentException or OverflowException or OutOfMemoryException or NotSupportedException)
        {
            return null;
        }
    }
}
