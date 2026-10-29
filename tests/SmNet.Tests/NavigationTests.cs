using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Test-bot navigation (<see cref="NavGraph"/>, <see cref="NavAgent"/>; a test aid, not the original AI): the walkable grid
/// over synthetic levels (walls, steps, ledges, ramps, corridors, kill volumes), A* with string pulling, and a capsule
/// following a path through the real motor. Synthetic geometry only.
/// </summary>
public class NavigationTests(ITestOutputHelper output)
{
    static readonly SimConfig Config = new();
    static readonly NavSettings Settings = NavSettings.From(Config);

    internal sealed class Level
    {
        readonly List<Vector3> _v = new();
        readonly List<int> _i = new();

        public Level Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int k = _v.Count;
            _v.AddRange([a, b, c, d]);
            _i.AddRange([k, k + 1, k + 2, k, k + 2, k + 3]);
            return this;
        }

        public Level Floor(float x0, float y0, float x1, float y1, float z = 0f) => Quad(new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z));

        /// <summary>A closed axis-aligned box.</summary>
        public Level Box(Vector3 min, Vector3 max)
        {
            Vector3 P(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
            Quad(P(0, 0, 0), P(1, 0, 0), P(1, 1, 0), P(0, 1, 0));
            Quad(P(0, 0, 1), P(0, 1, 1), P(1, 1, 1), P(1, 0, 1));
            Quad(P(0, 0, 0), P(0, 0, 1), P(1, 0, 1), P(1, 0, 0));
            Quad(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1));
            Quad(P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1));
            return Quad(P(1, 0, 0), P(1, 0, 1), P(1, 1, 1), P(1, 1, 0));
        }

        /// <summary>A thin wall (box 0.2 m thick) from (x0, y0) to (x1, y1), axis-aligned, 4 m tall.</summary>
        public Level Wall(float x0, float y0, float x1, float y1, float z0 = 0f, float height = 4f) =>
            Box(new(MathF.Min(x0, x1) - 0.1f, MathF.Min(y0, y1) - 0.1f, z0), new(MathF.Max(x0, x1) + 0.1f, MathF.Max(y0, y1) + 0.1f, z0 + height));

        public StaticMesh Mesh() => new(_v.ToArray(), _i.ToArray());
    }

    static NavGraph Build(Level level, IReadOnlyList<KillVolume>? kills = null, NavArea? area = null)
    {
        var g = NavGraph.Build(level.Mesh(), kills, Settings, area);
        return g;
    }

    static int Node(NavGraph g, float x, float y, float z = 0f)
    {
        int n = g.Nearest(new Vector3(x, y, z), 0.5f, 0.6f);
        Assert.True(n >= 0, $"no walkable cell at {x},{y},{z}");
        return n;
    }

    static bool HasNode(NavGraph g, float x, float y, float z) => g.Nearest(new Vector3(x, y, z), 0.3f, 0.3f) >= 0;

    /// <summary>Every consecutive pair of walk waypoints is joined by a straight walk on the graph (no shortcut through walls).</summary>
    static void AssertStraightWalks(NavGraph g, NavPath path)
    {
        for (int i = 1; i < path.Waypoints.Count; i++)
            if (path.Waypoints[i].Kind == NavLinkKind.Walk)
                Assert.True(g.StraightWalk(path.Waypoints[i - 1].Node, path.Waypoints[i].Node), $"waypoints {i - 1} -> {i} are not a straight walk");
    }

    [Fact]
    public void An_open_floor_is_walkable_and_a_straight_path_needs_no_turns()
    {
        var g = Build(new Level().Floor(-10, -10, 10, 10));
        output.WriteLine($"{g.NodeCount} cells, {g.LinkCount} links; {g.BuildNote}");
        // 0.5 m cells; the edge cells are walkable too (nothing to bump into at the rim of a floor).
        Assert.InRange(g.NodeCount, 38 * 38, 41 * 41);
        var path = g.FindPath(new Vector3(-8, -8, 0), new Vector3(8, 7, 0));
        Assert.NotNull(path);
        Assert.False(path!.Partial);
        Assert.Equal(2, path.Waypoints.Count);
        Assert.Equal(MathF.Sqrt(16 * 16 + 15 * 15), path.Length, 0.6f);
    }

    [Fact]
    public void A_wall_with_a_door_is_crossed_only_through_the_door()
    {
        // Wall along y = 0 from x = -10 to 10 with a 2 m door at x 4..6.
        var level = new Level().Floor(-10, -10, 10, 10).Wall(-10, 0, 4, 0).Wall(6, 0, 10, 0);
        var g = Build(level);
        Assert.False(HasNode(g, -3, 0, 0));                               // inside the wall
        Assert.False(g.StraightWalk(Node(g, -3, -3), Node(g, -3, 3)));  // straight through the wall
        Assert.True(g.StraightWalk(Node(g, 5, -3), Node(g, 5, 3)));     // straight through the door
        var path = g.FindPath(new Vector3(-6, -5, 0), new Vector3(-6, 5, 0))!;
        Assert.NotNull(path);
        Assert.False(path.Partial);
        AssertStraightWalks(g, path);
        Assert.Contains(path.Waypoints, w => w.Position.X >= 3.5f && w.Position.X <= 6.5f && MathF.Abs(w.Position.Y) < 1.5f);   // via the door
        Assert.True(path.Length > 20f, $"length {path.Length}");
        Assert.True(path.Waypoints.Count <= 5, $"{path.Waypoints.Count} waypoints: not smoothed");
        output.WriteLine($"door path: {path.Length:F1} m via {string.Join(" ", path.Waypoints.Select(w => $"({w.Position.X:F1},{w.Position.Y:F1})"))}");
    }

    [Fact]
    public void A_door_narrower_than_the_capsule_is_closed_and_a_wide_corridor_is_open()
    {
        // Two rooms joined by a 0.6 m slit (too narrow) and, in a second level, a 1.6 m corridor.
        var narrow = new Level().Floor(-10, -10, 10, 10).Wall(-10, 0, -0.3f, 0).Wall(0.3f, 0, 10, 0);
        var g = Build(narrow);
        var path = g.FindPath(new Vector3(0, -5, 0), new Vector3(0, 5, 0))!;
        Assert.True(path.Partial, "a path through a 0.6 m slit");
        Assert.True(path.Waypoints[^1].Position.Y < 0f);

        var corridor = new Level().Floor(-10, -10, 10, 10)
            .Wall(-10, -2, -0.8f, -2).Wall(0.8f, -2, 10, -2).Wall(-0.8f, -2, -0.8f, 2).Wall(0.8f, -2, 0.8f, 2).Wall(-10, 2, -0.8f, 2).Wall(0.8f, 2, 10, 2);
        g = Build(corridor);
        path = g.FindPath(new Vector3(-5, -6, 0), new Vector3(5, 6, 0))!;
        Assert.False(path.Partial);
        AssertStraightWalks(g, path);
        Assert.All(path.Waypoints.Where(w => MathF.Abs(w.Position.Y) < 2f), w => Assert.InRange(w.Position.X, -0.6f, 0.6f));
    }

    [Fact]
    public void Steps_are_climbed_but_a_ledge_is_only_dropped_from()
    {
        // Floor z 0 for y < 0; stairs of 0.3 m rises (1 m treads) at x < 0 up to a platform at z 1.2 (y 4..10);
        // at x >= 0 the platform ends in a 1.2 m ledge at y = 4.
        var level = new Level().Floor(-10, -10, 10, 0);
        for (int k = 0; k < 4; k++) level.Box(new Vector3(-10, k, 0), new Vector3(0, k + 1, 0.3f * (k + 1)));
        level.Floor(0, 0, 10, 4);                                         // floor below the ledge
        level.Box(new Vector3(-10, 4, 0), new Vector3(10, 10, 1.2f));    // the platform (top at 1.2)
        var g = Build(level);
        Assert.True(Node(g, 5, 3, 0) >= 0 && Node(g, 5, 6, 1.2f) >= 0 && Node(g, -5, 2.5f, 0.9f) >= 0);
        // No walk link up (or down) the 1.2 m ledge; the stairs' links rise at most a step.
        for (int n = 0; n < g.NodeCount; n++)
            foreach (var l in g.Links(n).Where(l => l.Kind == NavLinkKind.Walk))
                Assert.True(MathF.Abs(g.Position(l.To).Z - g.Position(n).Z) <= Settings.StepHeight + 0.05f, $"walk link {g.Position(n)} -> {g.Position(l.To)}");
        // Down: a drop link off the ledge; up: the stairs.
        Assert.Contains(Enumerable.Range(0, g.NodeCount).SelectMany(n => g.Links(n)), l => l.Kind == NavLinkKind.Drop);
        var up = g.FindPath(new Vector3(5, 2, 0), new Vector3(5, 7, 1.2f))!;
        Assert.False(up.Partial);
        Assert.Contains(up.Waypoints, w => w.Position.X < 0f);           // round by the stairs
        Assert.DoesNotContain(up.Waypoints, w => w.Kind == NavLinkKind.Drop);
        var down = g.FindPath(new Vector3(5, 7, 1.2f), new Vector3(5, 1, 0))!;
        Assert.False(down.Partial);
        Assert.Contains(down.Waypoints, w => w.Kind == NavLinkKind.Drop);   // straight off the ledge
        Assert.True(down.Length < up.Length, $"down {down.Length:F1} m, up {up.Length:F1} m");
    }

    [Fact]
    public void A_walkable_ramp_joins_levels_and_a_too_steep_one_does_not()
    {
        Level Ramp(float degrees)
        {
            float height = 2f, run = height / MathF.Tan(degrees * MathF.PI / 180f);
            return new Level().Floor(-5, -10, 5, 0)
                .Quad(new(-5, 0, 0), new(5, 0, 0), new(5, run, height), new(-5, run, height))
                .Floor(-5, run, 5, run + 8, height);
        }
        var g = Build(Ramp(30f));
        var path = g.FindPath(new Vector3(0, -5, 0), new Vector3(0, 3.46f + 4f, 2f))!;
        Assert.False(path.Partial);
        AssertStraightWalks(g, path);
        Assert.Equal(2, path.Waypoints.Count);

        g = Build(Ramp(70f));
        path = g.FindPath(new Vector3(0, -5, 0), new Vector3(0, 0.73f + 4f, 2f))!;
        Assert.True(path.Partial, "walked up a 70° ramp");
    }

    [Fact]
    public void Drop_links_avoid_kill_volumes_and_no_cell_lies_in_one()
    {
        // A platform at z 3 (y 0..10) above a floor at z 0 (y -10..0 safe, but x > 0 below the ledge is a kill pit).
        var level = new Level().Floor(-10, -10, 10, 0).Box(new Vector3(-10, 0, 0), new Vector3(10, 10, 3f));
        var pit = new KillVolume(new Vector3(5, -3, 0.5f), new Vector3(5, 3, 1f), 0f);
        var g = Build(level, [pit]);
        Assert.False(HasNode(g, 5, -3, 0));      // inside the kill volume
        Assert.True(HasNode(g, -5, -3, 0));
        for (int n = 0; n < g.NodeCount; n++)
            foreach (var l in g.Links(n).Where(l => l.Kind == NavLinkKind.Drop))
            {
                var from = g.Position(n); var to = g.Position(l.To);
                Assert.False(pit.Crosses(from, to) || pit.Contains(to), $"drop {from} -> {to} into the pit");
                Assert.InRange(from.Z - to.Z, 2.5f, 3.5f);
            }
        Assert.Contains(Enumerable.Range(0, g.NodeCount).SelectMany(n => g.Links(n)), l => l.Kind == NavLinkKind.Drop);
        // Off the platform to the safe floor: over the safe half (x < 0), never through the pit.
        var path = g.FindPath(new Vector3(6, 5, 3), new Vector3(8, -8, 0))!;
        Assert.NotNull(path);
        var drop = path.Waypoints.First(w => w.Kind == NavLinkKind.Drop);
        Assert.True(drop.Position.X < 0.5f, $"dropped at {drop.Position}");
        for (int i = 1; i < path.Waypoints.Count; i++)
            Assert.False(pit.Crosses(path.Waypoints[i - 1].Position + new Vector3(0, 0, 0.1f), path.Waypoints[i].Position + new Vector3(0, 0, 0.1f)), $"leg {i} crosses the pit");
    }

    [Fact]
    public void Goals_off_the_graph_snap_to_the_nearest_walkable_cell()
    {
        var level = new Level().Floor(-10, -10, 10, 10).Box(new Vector3(-2, -2, 0), new Vector3(2, 2, 4f));   // a solid block
        var g = Build(level);
        var path = g.FindPath(new Vector3(-8, 0, 0), new Vector3(0, 0, 0))!;   // the goal is inside the block
        Assert.NotNull(path);
        var end = path.Waypoints[^1].Position;
        Assert.True(MathF.Max(MathF.Abs(end.X), MathF.Abs(end.Y)) is >= 2f and < 3.5f, $"ended at {end}");
        Assert.Null(g.FindPath(new Vector3(-8, 0, 0), new Vector3(60, 0, 0)));   // far off the graph
    }

    [Fact]
    public void Areas_keep_only_what_their_seeds_reach_and_the_cache_round_trips()
    {
        // A floor and, on top of a closed 3 m block, a roof no one can reach.
        var level = new Level().Floor(-10, -10, 10, 10).Box(new Vector3(2, 2, 0), new Vector3(8, 8, 3f));
        var all = Build(level);
        var area = new NavArea(new System.Numerics.Vector2(-10), new System.Numerics.Vector2(10), [new Vector3(-5, -5, 0)]);
        var kept = Build(level, null, area);
        Assert.True(HasNode(all, 5, 5, 3f));
        Assert.False(HasNode(kept, 5, 5, 3f));
        Assert.True(HasNode(kept, -5, 5, 0f));
        Assert.True(kept.NodeCount < all.NodeCount);
        Assert.NotEqual(all.SourceHash, kept.SourceHash);

        using var stream = new MemoryStream();
        kept.Write(stream);
        stream.Position = 0;
        var read = NavGraph.Read(stream, Settings, kept.SourceHash);
        Assert.NotNull(read);
        Assert.Equal(kept.NodeCount, read!.NodeCount);
        Assert.Equal(kept.LinkCount, read.LinkCount);
        var a = kept.FindPath(new Vector3(-8, -8, 0), new Vector3(9, 9, 0))!;
        var b = read.FindPath(new Vector3(-8, -8, 0), new Vector3(9, 9, 0))!;
        Assert.Equal(a.Waypoints, b.Waypoints);
        stream.Position = 0;
        Assert.Null(NavGraph.Read(stream, Settings, kept.SourceHash ^ 1));            // another key
        Assert.Null(NavGraph.Read(new MemoryStream(stream.ToArray()[..40]), Settings, kept.SourceHash));   // truncated
    }

    /// <summary>
    /// A capsule run by the simulation's motor, steered by a <see cref="NavAgent"/>, gets through a maze of walls and up a
    /// flight of steps to a goal it cannot walk to straight, without getting stuck.
    /// </summary>
    [Fact]
    public void A_bot_following_the_path_reaches_a_goal_behind_walls()
    {
        var level = new Level().Floor(-15, -15, 15, 15)
            .Wall(-15, -5, 10, -5)        // doors at the east end
            .Wall(-10, 5, 15, 5);         // ... then at the west end
        for (int k = 0; k < 3; k++) level.Box(new Vector3(-15 + k, 8, 0), new Vector3(-14 + k, 15, 0.3f * (k + 1)));
        level.Box(new Vector3(-12, 8, 0), new Vector3(15, 15, 0.9f));   // a raised area reached by the steps
        var mesh = level.Mesh();
        var world = new CollisionWorld(mesh, mesh);
        var g = NavGraph.Build(mesh, null, Settings);
        var agent = new NavAgent(g);
        var goal = new Vector3(10, 12, 0.9f);
        var s = new PlayerState(1, new Vector3(0, -12, 0), Vector3.Zero, 0f, true);
        double t = 0;
        bool arrived = false;
        for (uint tick = 0; tick < 60 * 30; tick++, t += Config.TickSeconds)
        {
            var steer = agent.Steer(s.Position, s.Grounded, goal, t);
            if (steer.Arrived) { arrived = true; break; }
            float yaw = steer.Direction == System.Numerics.Vector2.Zero ? s.Yaw : MathF.Atan2(-steer.Direction.X, steer.Direction.Y);
            s = Movement.Step(s, new PlayerInput(tick, 0f, steer.Speed, yaw, InputButtons.None), Config, world);
        }
        output.WriteLine($"arrived {arrived} after {t:F1} s at {s.Position}; path {agent.Path?.Length:F1} m, plans {agent.Plans}, stuck {agent.StuckCount}");
        Assert.True(arrived, $"stopped at {s.Position} after {t:F1} s");
        Assert.True(Vector3.Distance(s.Position, goal) < 1.5f);
        Assert.Equal(0, agent.StuckCount);
        Assert.True(t < 25, $"took {t:F1} s");
    }

    // ---------------------------------------------------------------- cache key and files

    static Level Room() => new Level().Floor(-10, -10, 10, 10).Wall(-10, 0, 4, 0);

    [Fact]
    public void The_cache_key_changes_with_the_mesh_the_kill_volumes_the_settings_and_the_area()
    {
        var mesh = Room().Mesh();
        ulong key = NavGraph.KeyFor(mesh, null, Settings);
        Assert.Equal(key, NavGraph.KeyFor(Room().Mesh(), null, Settings));
        Assert.Equal(key, NavGraph.Build(mesh, null, Settings).SourceHash);
        Assert.NotEqual(key, NavGraph.KeyFor(Room().Wall(5, 5, 5, 8).Mesh(), null, Settings));
        Assert.NotEqual(key, NavGraph.KeyFor(mesh, [new KillVolume(Vector3.Zero, Vector3.One, 0f)], Settings));
        Assert.NotEqual(NavGraph.KeyFor(mesh, [new KillVolume(Vector3.Zero, Vector3.One, 0f)], Settings),
                        NavGraph.KeyFor(mesh, [new KillVolume(Vector3.Zero, Vector3.One, 0.5f)], Settings));
        foreach (var changed in new[]
                 {
                     Settings with { CellSize = 0.75f }, Settings with { Radius = 0.3f }, Settings with { Height = 2f }, Settings with { StepHeight = 0.5f },
                     Settings with { MaxSlopeDegrees = 45f }, Settings with { MaxDrop = 4f }, Settings with { MaxDropReach = 2f },
                 })
            Assert.NotEqual(key, NavGraph.KeyFor(mesh, null, changed));
        var area = new NavArea(new System.Numerics.Vector2(-5), new System.Numerics.Vector2(5), [Vector3.Zero]);
        Assert.NotEqual(key, NavGraph.KeyFor(mesh, null, Settings, area));
        Assert.NotEqual(NavGraph.KeyFor(mesh, null, Settings, area), NavGraph.KeyFor(mesh, null, Settings, area with { Seeds = [Vector3.One] }));
    }

    [Fact]
    public void Damaged_cache_data_is_rejected()
    {
        var g = Build(Room());
        using var stream = new MemoryStream();
        g.Write(stream);
        byte[] good = stream.ToArray();
        Assert.NotNull(NavGraph.Read(new MemoryStream(good), Settings, g.SourceHash));
        Assert.Null(NavGraph.Read(new MemoryStream(good), Settings, g.SourceHash + 1));                       // another key
        Assert.Null(NavGraph.Read(new MemoryStream(good), Settings with { CellSize = 1f }, g.SourceHash));     // another cell size
        Assert.Null(NavGraph.Read(new MemoryStream(good[..^1]), Settings, g.SourceHash));                     // truncated
        Assert.Null(NavGraph.Read(new MemoryStream([.. good, 0]), Settings, g.SourceHash));                   // trailing byte
        Assert.Null(NavGraph.Read(new MemoryStream(good[..20]), Settings, g.SourceHash));                     // header cut
        Assert.Null(NavGraph.Read(new MemoryStream([]), Settings, g.SourceHash));
        foreach (int at in new[] { 30, 44, good.Length / 2, good.Length - 9, good.Length - 1 })
        {
            var bad = (byte[])good.Clone();
            bad[at] ^= 0x40;
            Assert.Null(NavGraph.Read(new MemoryStream(bad), Settings, g.SourceHash));
        }
        // Sizes that do not match the length are refused before anything that large is allocated.
        var huge = (byte[])good.Clone();
        BitConverter.GetBytes(40_000_000).CopyTo(huge, 36);   // node count
        Assert.Null(NavGraph.Read(new MemoryStream(huge), Settings, g.SourceHash));
    }

    [Fact]
    public void The_cache_rebuilds_damaged_files_and_keeps_a_few_graphs_per_map()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-reimpl-navcache-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mesh = Room().Mesh();
            ulong key = NavGraph.KeyFor(mesh, null, Settings);
            int builds = 0;
            NavGraph Builder() { builds++; return NavGraph.Build(mesh, null, Settings); }
            var first = NavCache.LoadOrBuild(dir, "map", key, Settings, Builder, out string note);
            Assert.Equal(1, builds);
            Assert.Contains("cached", note);
            string file = NavCache.FileFor(dir, "map", key);
            Assert.True(File.Exists(file));
            var again = NavCache.LoadOrBuild(dir, "map", key, Settings, Builder, out note);
            Assert.Equal(1, builds);
            Assert.StartsWith("read from the cache", note);
            Assert.Equal(first.NodeCount, again.NodeCount);

            // Damaged: one byte flipped, then cut short, then another key's graph under this name: each is deleted and rebuilt.
            var bytes = File.ReadAllBytes(file);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(file, bytes);
            NavCache.LoadOrBuild(dir, "map", key, Settings, Builder, out note);
            Assert.Equal(2, builds);
            Assert.Contains("damaged cache file was deleted", note);
            File.WriteAllBytes(file, File.ReadAllBytes(file)[..100]);
            NavCache.LoadOrBuild(dir, "map", key, Settings, Builder, out _);
            Assert.Equal(3, builds);
            var other = NavGraph.Build(Room().Wall(5, 5, 5, 8).Mesh(), null, Settings);
            using (var f = File.Create(file)) other.Write(f);
            var rebuilt = NavCache.LoadOrBuild(dir, "map", key, Settings, Builder, out _);
            Assert.Equal(4, builds);
            Assert.Equal(first.NodeCount, rebuilt.NodeCount);
            Assert.NotNull(NavCache.LoadOrBuild(dir, "map", key, Settings, Builder, out _));
            Assert.Equal(4, builds);

            // Pruning: old temporary files go; of a map's graphs only the newest KeepPerMap stay (other maps untouched).
            string tmp = Path.Combine(dir, "map-0000000000000001.nav.123.tmp");
            File.WriteAllText(tmp, "x");
            File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow.AddDays(-2));
            for (int i = 0; i < NavCache.KeepPerMap + 2; i++)
            {
                string old = NavCache.FileFor(dir, "map", 0x1000UL + (ulong)i);
                File.WriteAllText(old, "old");
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-20 + i));
            }
            File.WriteAllText(NavCache.FileFor(dir, "othermap", 7), "keep");
            NavCache.LoadOrBuild(dir, "map", key ^ 0xFFFF, Settings, Builder, out _);   // a new key: built, written, pruned
            Assert.False(File.Exists(tmp));
            Assert.Equal(NavCache.KeepPerMap, Directory.GetFiles(dir, "map-*.nav").Length);
            Assert.True(File.Exists(NavCache.FileFor(dir, "map", key ^ 0xFFFF)));
            Assert.True(File.Exists(NavCache.FileFor(dir, "othermap", 7)));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------- agent

    [Fact]
    public void A_bot_that_does_not_move_is_found_stuck_replans_and_then_steps_aside()
    {
        var g = Build(Room());
        var agent = new NavAgent(g);
        var at = new Vector3(-5, -5, 0);
        int plansBefore = 0;
        bool recovering = false;
        for (double t = 0; t < 4.0; t += 1.0 / 30)
        {
            var steer = agent.Steer(at, true, new Vector3(-5, 5, 0), t);   // the bot never moves (held by something)
            if (t < 0.1) plansBefore = agent.Plans;
            recovering |= steer.Recovering;
        }
        Assert.True(agent.StuckCount >= 2, $"stuck {agent.StuckCount}");
        Assert.True(agent.Plans > plansBefore);
        Assert.True(recovering, "no recovery step after two stuck detections");
        // Standing at the goal is not being stuck.
        var resting = new NavAgent(g);
        for (double t = 0; t < 5.0; t += 1.0 / 30) Assert.True(resting.Steer(new Vector3(-5, 5, 0), true, new Vector3(-5, 5.3f, 0), t).Arrived);
        Assert.Equal(0, resting.StuckCount);
    }

    [Fact]
    public void A_plan_that_finds_nothing_keeps_the_path_being_followed_instead_of_dropping_it()
    {
        // M44a: a goal in the air over the room (no floor within the snap distance of 6 m) used to drop the path, and the bot walked straight at it.
        var g = Build(Room());
        var agent = new NavAgent(g);
        var start = new Vector3(-6, -6, 0);
        var goal = new Vector3(-6, 6, 0);
        agent.Steer(start, true, goal, 0.0);
        var path = agent.Path;
        Assert.NotNull(path);
        var failures = agent.Failures;
        // The goal moves up out of reach: the next plans fail (null), the old path stays.
        var air = new Vector3(-6, 6, 30);
        for (double t = 0.6; t < 2.5; t += 0.1) agent.Steer(start, true, air, t);
        Assert.True(agent.Failures > failures, "the plan to a goal in the air should have failed");
        Assert.Same(path, agent.Path);
        Assert.NotNull(agent.Path);
        // ... but not for ever: after MaxKeptFailures failed plans in a row the path is dropped (the straight walk at the goal).
        for (double t = 2.6; t < 20.0; t += 0.1) agent.Steer(start, true, air, t);
        Assert.True(agent.Failures >= agent.MaxKeptFailures);
        Assert.Null(agent.Path);
    }

    [Fact]
    public void A_recovery_step_goes_only_where_the_ground_is_walkable()
    {
        // M44a: a bot held against the wall near the room's west edge: every recovery step has walkable ground ahead (never off the floor or into the wall).
        var g = Build(Room());
        var agent = new NavAgent(g);
        var at = new Vector3(-9.2f, -0.9f, 0f);   // just below the wall (y = 0), near the room's west edge, held there
        var goal = new Vector3(-9.2f, 5f, 0f);    // across the wall: the bot keeps pushing into it
        for (double t = 0; t < 12.0; t += 1.0 / 30)
        {
            var steer = agent.Steer(at, true, goal, t);
            if (steer.Recovering) Assert.True(g.CanWalk(at, steer.Direction * agent.RecoverWalkCheck), $"recovery step {steer.Direction} at t={t:F2} leads off the walkable ground");
        }
        Assert.True(agent.StuckCount >= 3);
    }

    [Fact]
    public void A_respawn_far_away_is_followed_by_a_new_path_from_there()
    {
        var g = Build(Room());
        var agent = new NavAgent(g);
        var goal = new Vector3(-6, 6, 0);
        agent.Steer(new Vector3(-6, -6, 0), true, goal, 0.0);
        Assert.Equal(-6f, agent.Path!.Waypoints[0].Position.X, 0.3f);
        // Teleported (respawn) to the other side of the room; the goal did not move.
        var steer = agent.Steer(new Vector3(8, -8, 0), true, goal, 0.6);
        var start = agent.Path!.Waypoints[0].Position;
        Assert.True(MathF.Abs(start.X - 8f) < 0.6f && MathF.Abs(start.Y + 8f) < 0.6f, $"path starts at {start}");
        Assert.True(steer.Direction.Y > 0.5f, $"steers {steer.Direction}");   // north toward the door, not west through the wall
    }

    [Fact]
    public void An_unreachable_goal_ends_at_the_nearest_cell_and_is_rarely_replanned()
    {
        // The goal is on a 3 m block no one can climb.
        var level = new Level().Floor(-10, -10, 10, 10).Box(new Vector3(2, 2, 0), new Vector3(8, 8, 3f));
        var mesh = level.Mesh();
        var world = new CollisionWorld(mesh, mesh);
        var g = NavGraph.Build(mesh, null, Settings);
        var agent = new NavAgent(g);
        var goal = new Vector3(5, 5, 3f);
        var s = new PlayerState(1, new Vector3(-8, -8, 0), Vector3.Zero, 0f, true);
        bool arrived = false;
        double t = 0, arrivedAt = 0;
        int plansAtArrival = 0;
        for (uint tick = 0; tick < 30 * 30; tick++, t += Config.TickSeconds)
        {
            var steer = agent.Steer(s.Position, s.Grounded, goal, t);
            if (steer.Arrived && !arrived) { arrived = true; arrivedAt = t; plansAtArrival = agent.Plans; }
            float yaw = steer.Direction == System.Numerics.Vector2.Zero ? s.Yaw : MathF.Atan2(-steer.Direction.X, steer.Direction.Y);
            s = Movement.Step(s, new PlayerInput(tick, 0f, steer.Speed, yaw, InputButtons.None), Config, world);
        }
        Assert.True(arrived);
        Assert.True(agent.Path!.Partial);
        Assert.True(agent.Unreachable);
        // Beside the block, as near the goal as the floor goes (3 m half width + clearance + arrival radius).
        Assert.True(s.Position.Z < 0.5f && new System.Numerics.Vector2(s.Position.X - 5, s.Position.Y - 5).Length() < 4.6f, $"ended at {s.Position}");
        // Standing there with the goal unchanged: re-planned every PartialRepathInterval, not every second, and not "stuck".
        double waited = 30 * 30 * Config.TickSeconds - arrivedAt;
        Assert.InRange(agent.Plans - plansAtArrival, 0, (int)(waited / agent.PartialRepathInterval) + 1);
        Assert.Equal(0, agent.StuckCount);
    }

    [Fact]
    public void Background_planning_delivers_the_same_path_without_planning_inside_steer()
    {
        var g = Build(new Level().Floor(-10, -10, 10, 10).Wall(-10, 0, 4, 0).Wall(6, 0, 10, 0));
        var from = new Vector3(-6, -5, 0); var goal = new Vector3(-6, 5, 0);
        var sync = new NavAgent(g);
        sync.Steer(from, true, goal, 0);
        var background = new NavAgent(g) { Background = true };
        var first = background.Steer(from, true, goal, 0);
        Assert.False(first.HasPath);   // nothing yet: a straight walk at the goal until the search lands
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double t = 0;
        while (background.Path is null && watch.Elapsed < TimeSpan.FromSeconds(10)) { Thread.Sleep(5); background.Steer(from, true, goal, t += 0.01); }
        Assert.NotNull(background.Path);
        Assert.Equal(sync.Path!.Waypoints, background.Path!.Waypoints);
        Assert.Equal(1, background.Plans);
    }
}
