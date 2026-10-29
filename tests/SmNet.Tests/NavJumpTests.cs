using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Test-bot navigation, round 2 (a test aid, not the original AI): a graph per side over that side's character collision
/// (the other side's spawn-room blockers are walls only for it) and jump-pack links the builder checks by flying the real
/// jump (<see cref="NavJumpFlight"/>) against the same collision, executed by <see cref="NavAgent"/>. Synthetic geometry only.
/// </summary>
public class NavJumpTests(ITestOutputHelper output)
{
    // the assault class (0) has the jump pack; M45: with the release cut, as every pack read from the data
    static readonly SimConfig Config = JumpPackTests.Config with { JumpPacks = [JumpPackTests.Pack with { ReleaseCut = true }] };
    const int Assault = 0, Tactical = 1;
    static readonly NavSettings Settings = NavSettings.From(Config);
    static readonly JumpPackSpec Pack = Config.JumpPacks[0];
    static readonly NavJumpOptions Options = new();

    static NavigationTests.Level Floor(float x0, float y0, float x1, float y1, float z = 0f) => new NavigationTests.Level().Floor(x0, y0, x1, y1, z);

    static (CollisionWorld World, NavGraph Graph) Make(NavigationTests.Level level, Team team = Team.None, IReadOnlyList<SpawnBlocker>? blockers = null, IReadOnlyList<KillVolume>? kills = null)
    {
        var mesh = level.Mesh();
        var world = new CollisionWorld(mesh, mesh, kills, blockers);
        return (world, NavGraph.Build(world.CharacterFor(team), kills, Settings));
    }

    static NavGraph WithJumps(CollisionWorld world, NavGraph graph, Team team, int playerClass = Assault) =>
        graph.WithJumps(NavJumpSet.Build(graph, world, team, Config, playerClass, Options));

    // ---------------------------------------------------------------- a graph per side

    /// <summary>A walled yard with one doorway that the Chaos side's blocker fills: the Space Marines' cannot cross it, the Chaos can.</summary>
    static (CollisionWorld World, SpawnBlocker Blocker) Doorway()
    {
        var level = Floor(-10, -10, 10, 10).Wall(-10, 0, -1.5f, 0).Wall(1.5f, 0, 10, 0);
        var blocker = new SpawnBlocker(Team.Chaos, new Vector3(0, 0, 2), new Vector3(3.4f, 0.6f, 4f), 0f);
        var mesh = level.Mesh();
        return (new CollisionWorld(mesh, mesh, null, [blocker]), blocker);
    }

    [Fact]
    public void Each_side_plans_around_the_blockers_that_stop_it_and_through_the_ones_that_do_not()
    {
        var (world, _) = Doorway();
        var marines = NavGraph.Build(world.CharacterFor(Team.SpaceMarines), null, Settings);
        var chaos = NavGraph.Build(world.CharacterFor(Team.Chaos), null, Settings);
        var neutral = NavGraph.Build(world.Character, null, Settings);
        output.WriteLine($"cells: marines {marines.NodeCount}, chaos {chaos.NodeCount}, neutral {neutral.NodeCount}");
        // The Chaos blocker is a wall for the Space Marines only: their graph has fewer cells at the doorway, the Chaos graph has the neutral one's.
        Assert.True(marines.NodeCount < chaos.NodeCount);
        Assert.Equal(neutral.NodeCount, chaos.NodeCount);
        Assert.NotEqual(marines.SourceHash, chaos.SourceHash);
        Assert.Equal(neutral.SourceHash, NavGraph.KeyFor(world.Character, null, Settings));   // keys follow the mesh each side moves on
        Assert.NotEqual(NavGraph.KeyFor(world.CharacterFor(Team.SpaceMarines), null, Settings), NavGraph.KeyFor(world.CharacterFor(Team.Chaos), null, Settings));
        Assert.True(marines.Nearest(new Vector3(0, 0, 0), 0.3f, 0.3f) < 0, "the doorway is solid for the Space Marines");
        Assert.True(chaos.Nearest(new Vector3(0, 0, 0), 0.3f, 0.3f) >= 0, "the doorway is open for the Chaos");

        var from = new Vector3(0, -6, 0); var to = new Vector3(0, 6, 0);
        var viaMarines = marines.FindPath(from, to)!;
        var viaChaos = chaos.FindPath(from, to)!;
        Assert.True(viaMarines.Partial, "no way through for the Space Marines");
        Assert.False(viaChaos.Partial);
        Assert.InRange(viaChaos.Length, 11.5f, 14f);
        // Nothing of the Space Marines' path (up to where it ends, short of the wall) passes the line the blocker is on.
        Assert.All(viaMarines.Waypoints, w => Assert.True(w.Position.Y < 0.5f, $"waypoint at {w.Position}"));
    }

    [Fact]
    public void A_bot_of_each_side_walking_its_own_graphs_path_gets_through_or_stops_short_in_the_real_motor()
    {
        var (world, _) = Doorway();
        foreach (var team in new[] { Team.SpaceMarines, Team.Chaos })
        {
            var graph = NavGraph.Build(world.CharacterFor(team), null, Settings);
            var agent = new NavAgent(graph);
            var s = new PlayerState(1, new Vector3(0, -6, 0), Vector3.Zero, 0f, true) { Team = team };
            var goal = new Vector3(0, 6, 0);
            double t = 0;
            for (uint tick = 0; tick < 20 * 30; tick++, t += Config.TickSeconds)
            {
                var steer = agent.Steer(s.Position, s.Grounded, goal, t);
                float yaw = steer.Direction == Vector2.Zero ? s.Yaw : MathF.Atan2(-steer.Direction.X, steer.Direction.Y);
                s = Movement.Step(s, new PlayerInput(tick, 0f, steer.Speed, yaw, InputButtons.None), Config, world);
            }
            output.WriteLine($"{team}: ended at {s.Position}, stuck {agent.StuckCount}, unreachable {agent.Unreachable}");
            if (team == Team.Chaos) { Assert.True(s.Position.Y > 5f, $"{s.Position}"); Assert.Equal(0, agent.StuckCount); }
            else
            {
                // Plans only on its side of the wall and stands at the nearest cell: no pushing into the blocker.
                Assert.True(s.Position.Y < 0f && agent.Unreachable, $"{s.Position}");
                Assert.Equal(0, agent.StuckCount);
            }
        }
    }

    // ---------------------------------------------------------------- jump links

    /// <summary>Two floors with 14 m of nothing between them: only a flight crosses.</summary>
    static NavigationTests.Level Gap() => Floor(-12, -8, 0, 8).Quad(new(14, -8, 0), new(60, -8, 0), new(60, 8, 0), new(14, 8, 0));

    /// <summary>A floor with a 5 m tower of solid block in the middle (nothing leads up it).</summary>
    static NavigationTests.Level Tower(float roof = 0f)
    {
        var level = Floor(-20, -20, 40, 20).Box(new Vector3(10, -3, 0), new Vector3(14, 3, 5f));
        if (roof > 0f) level.Box(new Vector3(8, -5, 6.5f), new Vector3(16, 5, 6.7f));   // 1.5 m over the top: no standing room up there
        return level;
    }

    [Fact]
    public void A_jump_link_crosses_a_gap_no_walk_reaches_and_every_link_replays_to_its_landing()
    {
        var (world, graph) = Make(Gap());
        var jumpers = WithJumps(world, graph, Team.None);
        Assert.NotNull(jumpers.Jumps);
        output.WriteLine($"{graph.NodeCount} cells; {jumpers.Jumps.BuildNote}");
        Assert.True(jumpers.Jumps.LinkCount > 0);
        var from = new Vector3(-6, 0, 0); var to = new Vector3(30, 0, 0);
        Assert.True(graph.FindPath(from, to)!.Partial);                      // no jumps attached: walking cannot get there
        var path = jumpers.FindPath(from, to)!;
        Assert.False(path.Partial);
        var flight = Assert.Single(path.Waypoints, w => w.Kind == NavLinkKind.Jump);
        Assert.True(flight.Jump.IsJump && flight.Jump.Power >= Pack.CostAt(0f));
        Assert.True(flight.Position.X >= 14f);
        // The take-off waypoint is the cell the link starts from.
        int index = path.Waypoints.ToList().IndexOf(flight);
        Assert.Equal(flight.Jump.From, path.Waypoints[index - 1].Node);
        // Every stored link is what the simulation gives: replaying it from the take-off cell lands on the landing cell.
        int checkedLinks = 0;
        for (int n = 0; n < graph.NodeCount && checkedLinks < 60; n += 7)
            foreach (var (landing, cost, move) in jumpers.Jumps.From(n))
            {
                var r = NavJumpFlight.Simulate(world, Config, Assault, Team.None, graph.Position(n), move.Direction, move.Hold);
                Assert.True(r.Landed, $"{n}: {r.Note}");
                var cell = graph.Position(landing);
                Assert.True(new Vector2(cell.X - r.Position.X, cell.Y - r.Position.Y).Length() <= Options.LandingReach + 1e-3f && MathF.Abs(cell.Z - r.Position.Z) <= Options.LandingHeight + 1e-3f);
                Assert.True(cost >= Options.Penalty + Options.MinFlat);
                checkedLinks++;
            }
        Assert.True(checkedLinks > 0);
    }

    /// <summary>The simulated body for a test: the agent's steering through the simulation's own player step.</summary>
    (PlayerState State, NavAgent Agent, double Time, bool Arrived) Fly(CollisionWorld world, NavGraph graph, Vector3 start, Vector3 goal, double seconds, Team team = Team.None, int playerClass = Assault)
    {
        var agent = new NavAgent(graph);
        var s = Combat.Respawned(new PlayerState(1, start, Vector3.Zero, 0f, true) { Class = playerClass, Team = team }, Config);
        double t = 0;
        bool arrived = false;
        float dt = Config.TickSeconds;
        var log = new List<string>();
        for (uint tick = 0; tick < (uint)(seconds / dt); tick++, t += dt)
        {
            var body = new NavBody(s.Jump, s.JumpTicks, s.JumpPower, new Vector2(s.Velocity.X, s.Velocity.Y).Length(), Pack, dt,
                s.MeleeStep > 0 || s.Throwing || s.SwitchLeft > 0f || s.JumpHeld);
            var steer = agent.Steer(s.Position, s.Grounded, goal, t, body);
            if (steer.Arrived) { arrived = true; break; }
            float yaw = steer.Direction == Vector2.Zero ? s.Yaw : MathF.Atan2(-steer.Direction.X, steer.Direction.Y);
            s = Combat.StepPlayer(s, new PlayerInput(tick, 0f, steer.Speed, yaw, steer.Jump ? InputButtons.Jump : InputButtons.None), Config, out _, world);
            if (tick % 15 == 0) log.Add($"{t:F1}s {s.Position} {s.Jump} sp {steer.Speed:F2} {(steer.Jump ? "J" : "")}{(steer.Flying ? "F" : "")}");
        }
        output.WriteLine(string.Join("\n", log.TakeLast(25)));
        output.WriteLine($"arrived {arrived} at {s.Position} after {t:F1} s; jumps started {agent.JumpsStarted}, landed {agent.JumpsLanded}, missed {agent.JumpsMissed}, stuck {agent.StuckCount}, plans {agent.Plans}");
        return (s, agent, t, arrived);
    }

    [Fact]
    public void A_simulated_assault_marine_flies_the_link_over_the_gap_and_arrives()
    {
        var (world, graph) = Make(Gap());
        var jumpers = WithJumps(world, graph, Team.None);
        var goal = new Vector3(30, 0, 0);
        var (s, agent, t, arrived) = Fly(world, jumpers, new Vector3(-8, 0, 0), goal, 40);
        Assert.True(arrived, $"stopped at {s.Position} after {t:F1} s");
        Assert.True(Vector3.Distance(s.Position, goal) < 2.5f);
        Assert.Equal(1, agent.JumpsStarted);
        Assert.Equal(1, agent.JumpsLanded);
        Assert.Equal(0, agent.JumpsMissed);
        Assert.Equal(0, agent.StuckCount);
    }

    [Fact]
    public void A_jump_link_climbs_a_ledge_with_no_way_up_and_the_marine_ends_on_top()
    {
        var (world, graph) = Make(Tower());
        var jumpers = WithJumps(world, graph, Team.None);
        output.WriteLine(jumpers.Jumps!.BuildNote);
        var top = new Vector3(12, 0, 5);
        Assert.True(graph.Nearest(top, 0.3f, 0.3f) >= 0);                     // the top is a walkable area
        Assert.True(graph.FindPath(new Vector3(0, 0, 0), top)!.Partial);      // ... that walking cannot reach
        var path = jumpers.FindPath(new Vector3(0, 0, 0), top)!;
        Assert.False(path.Partial);
        Assert.Contains(path.Waypoints, w => w.Kind == NavLinkKind.Jump && w.Position.Z > 4.5f);
        var (s, agent, t, arrived) = Fly(world, jumpers, new Vector3(0, 0, 0), top, 40);
        Assert.True(arrived, $"stopped at {s.Position} after {t:F1} s");
        Assert.True(s.Position.Z > 4.5f && s.Position.X > 9f && s.Position.X < 15f, $"{s.Position}");
        Assert.True(agent.JumpsLanded >= 1 && agent.JumpsMissed == 0);
    }

    [Fact]
    public void No_link_flies_through_a_ceiling_or_lands_where_there_is_no_standing_room()
    {
        // Under a slab 3.5 m up the rise stops at the ceiling: nothing under it reaches the 5 m tower.
        var covered = Tower().Box(new Vector3(-20, -20, 3.5f), new Vector3(8, 20, 3.7f));
        var (cw, cg) = Make(covered);
        var coveredJumps = WithJumps(cw, cg, Team.None);
        int up = 0, upFromUnder = 0;
        for (int n = 0; n < cg.NodeCount; n++)
            foreach (var (to, _, _) in coveredJumps.Jumps!.From(n))
                if (cg.Position(to).Z > 4.5f) { up++; if (cg.Position(n).X < 8f && cg.Position(n).Z < 0.5f) upFromUnder++; }
        output.WriteLine($"covered: {coveredJumps.Jumps!.LinkCount} links, {up} to the tower top, {upFromUnder} of them from under the slab");
        Assert.Equal(0, upFromUnder);
        Assert.True(up > 0, "cells outside the slab still reach the tower");
        // Every link replays under the slab too (the stored landing is what the flight reaches, never past the ceiling).
        for (int n = 0; n < cg.NodeCount; n += 11)
            foreach (var (to, _, move) in coveredJumps.Jumps.From(n))
            {
                var r = NavJumpFlight.Simulate(cw, Config, Assault, Team.None, cg.Position(n), move.Direction, move.Hold);
                Assert.True(r.Landed);
                // A flight that starts and ends under the slab stays under it (a straight line): the rise stops 1.9 m short of the ceiling.
                if (cg.Position(n).X < 8f && cg.Position(to).X < 8f) Assert.True(r.Apex <= 3.5f - 1.9f + 0.1f, $"apex {r.Apex} under the slab");
            }

        // A roof 1.5 m over the tower top leaves no standing room there: no cells, no landing, no link to it.
        var (rw, rg) = Make(Tower(roof: 1f));
        var roofedJumps = WithJumps(rw, rg, Team.None);
        Assert.True(rg.Nearest(new Vector3(12, 0, 5), 0.5f, 0.5f) < 0);
        for (int n = 0; n < rg.NodeCount; n++)
            foreach (var (to, _, _) in roofedJumps.Jumps!.From(n))
                Assert.False(rg.Position(to).Z is > 4.5f and < 6.2f, $"landing at {rg.Position(to)}");   // (the roof's own top, 6.7 m, is a legitimate floor)
    }

    [Fact]
    public void A_flight_through_a_kill_volume_is_not_a_link()
    {
        // The same gap with a kill volume across the middle of it: the arc passes through it, so no link crosses.
        var kill = new KillVolume(new Vector3(7, 0, 8), new Vector3(1, 12, 20), 0f);
        var (world, graph) = Make(Gap(), kills: [kill]);
        var jumpers = WithJumps(world, graph, Team.None);
        output.WriteLine(jumpers.Jumps!.BuildNote);
        for (int n = 0; n < graph.NodeCount; n++)
            foreach (var (to, _, _) in jumpers.Jumps.From(n))
                Assert.False(graph.Position(n).X < 0.5f && graph.Position(to).X > 14f, $"{graph.Position(n)} -> {graph.Position(to)}");
    }

    [Fact]
    public void Jump_links_are_per_side_the_enemys_blocker_is_a_wall_in_the_arc_for_one_side_only()
    {
        // A tall wall (a Chaos spawn-room blocker) across the middle of the gap: Chaos fly over or through it, Marines hit it.
        var blocker = new SpawnBlocker(Team.Chaos, new Vector3(7, 0, 15), new Vector3(1, 20, 30), 0f);
        var level = Gap();
        var mesh = level.Mesh();
        var world = new CollisionWorld(mesh, mesh, null, [blocker]);
        int Crossing(Team team)
        {
            var graph = NavGraph.Build(world.CharacterFor(team), null, Settings);
            var set = NavJumpSet.Build(graph, world, team, Config, team == Team.Chaos ? Assault : Assault, Options);
            int crossing = 0;
            for (int n = 0; n < graph.NodeCount; n++)
                foreach (var (to, _, _) in set.From(n))
                    if (graph.Position(n).X < 0.5f && graph.Position(to).X > 14f) crossing++;
            output.WriteLine($"{team}: {set.LinkCount} links, {crossing} crossing the gap");
            return crossing;
        }
        Assert.True(Crossing(Team.Chaos) > 0);
        Assert.Equal(0, Crossing(Team.SpaceMarines));
    }

    [Fact]
    public void A_low_enemy_blocker_reaches_above_the_level_so_no_jump_link_flies_over_it()
    {
        // M31 (change from the original, owner's decision 2026-10-28): a blocker only 2 m tall in the gap (a full jump rises 18 m)
        // reaches above the level, so the enemy's links do not fly over it; the own side's still cross the gap.
        var blocker = new SpawnBlocker(Team.Chaos, new Vector3(7, 0, -1f), new Vector3(1, 20, 2f), 0f);
        var mesh = Gap().Mesh();
        var world = new CollisionWorld(mesh, mesh, null, [blocker]);
        Assert.True(world.Blockers[0].Center.Z + world.Blockers[0].Size.Z / 2f >= mesh.Max.Z + SpawnBlocker.AboveLevel - 1e-3f);
        int Crossing(Team team)
        {
            var graph = NavGraph.Build(world.CharacterFor(team), null, Settings);
            var set = NavJumpSet.Build(graph, world, team, Config, Assault, Options);
            int crossing = 0;
            for (int n = 0; n < graph.NodeCount; n++)
                foreach (var (to, _, _) in set.From(n))
                    if ((graph.Position(n).X < 7f) != (graph.Position(to).X < 7f)) crossing++;
            output.WriteLine($"{team}: {set.LinkCount} links, {crossing} crossing x 7");
            return crossing;
        }
        Assert.True(Crossing(Team.Chaos) > 0);
        Assert.Equal(0, Crossing(Team.SpaceMarines));
    }

    [Fact]
    public void The_jump_set_round_trips_through_its_file_and_the_key_follows_the_graph_the_pack_and_the_options()
    {
        var (world, graph) = Make(Gap());
        var set = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options);
        ulong key = NavJumpSet.KeyFor(graph, Config, Assault, Options);
        Assert.Equal(key, set.Key);
        using var stream = new MemoryStream();
        set.Write(stream);
        stream.Position = 0;
        var back = NavJumpSet.Read(stream, graph.NodeCount, key)!;
        Assert.NotNull(back);
        Assert.Equal(set.LinkCount, back.LinkCount);
        for (int n = 0; n < graph.NodeCount; n++) Assert.Equal(set.From(n).ToList(), back.From(n).ToList());
        stream.Position = 0;
        Assert.Null(NavJumpSet.Read(stream, graph.NodeCount, key ^ 1));
        stream.Position = 0;
        Assert.Null(NavJumpSet.Read(stream, graph.NodeCount + 1, key));
        var bytes = stream.ToArray();
        bytes[bytes.Length / 2] ^= 0x40;
        Assert.Null(NavJumpSet.Read(new MemoryStream(bytes), graph.NodeCount, key));
        Assert.Null(NavJumpSet.Read(new MemoryStream(bytes[..30]), graph.NodeCount, key));
        // The key follows the graph, the options and every pack number.
        Assert.NotEqual(key, NavJumpSet.KeyFor(graph, Config, Assault, Options with { Stride = 1 }));
        Assert.NotEqual(key, NavJumpSet.KeyFor(graph, Config, Assault, Options with { Holds = [1, 12] }));
        var faster = Config with { JumpPacks = [Pack with { ForwardSpeed = 12f }] };
        Assert.NotEqual(key, NavJumpSet.KeyFor(graph, faster, Assault, Options));
        var (_, other) = Make(Gap().Wall(5, -8, 5, 8));
        Assert.NotEqual(key, NavJumpSet.KeyFor(other, Config, Assault, Options));
        // A set for another graph cannot be attached.
        Assert.Throws<ArgumentException>(() => other.WithJumps(NavJumpSet.Build(Make(Floor(-5, -5, 5, 5)).Graph, world, Team.None, Config, Assault, Options)));
    }

    [Fact]
    public void The_cache_builds_a_jump_set_once_rebuilds_a_damaged_file_and_keeps_a_few_per_map()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-reimpl-navjump-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (world, graph) = Make(Gap());
            ulong key = NavJumpSet.KeyFor(graph, Config, Assault, Options);
            int builds = 0;
            NavJumpSet Builder() { builds++; return NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options); }
            var first = NavCache.LoadOrBuildJumps(dir, "map", key, graph.NodeCount, Builder, out string note);
            Assert.Equal(1, builds);
            Assert.Contains("cached", note);
            var again = NavCache.LoadOrBuildJumps(dir, "map", key, graph.NodeCount, Builder, out note);
            Assert.Equal(1, builds);
            Assert.Contains("read from the cache", note);
            Assert.Equal(first.LinkCount, again.LinkCount);
            string file = NavCache.JumpFileFor(dir, "map", key);
            var bytes = File.ReadAllBytes(file);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(file, bytes);
            NavCache.LoadOrBuildJumps(dir, "map", key, graph.NodeCount, Builder, out _);
            Assert.Equal(2, builds);
            // A graph with another node count never reads this file.
            NavCache.LoadOrBuildJumps(dir, "map", key, graph.NodeCount + 1, Builder, out _);
            Assert.Equal(3, builds);
            for (int i = 0; i < NavCache.KeepPerMap + 2; i++)
            {
                string old = NavCache.JumpFileFor(dir, "map", 0x1000UL + (ulong)i);
                File.WriteAllText(old, "old");
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-20 + i));
            }
            NavCache.LoadOrBuildJumps(dir, "map", key ^ 0xFFFF, graph.NodeCount, Builder, out _);
            Assert.Equal(NavCache.KeepPerMap, Directory.GetFiles(dir, "map-*.navjump").Length);
            Assert.True(File.Exists(NavCache.JumpFileFor(dir, "map", key ^ 0xFFFF)));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------- the agent at a take-off cell

    /// <summary>The gap level with jump links, the goal across it, and the take-off cell of the planned flight.</summary>
    static (NavGraph Graph, Vector3 Take, Vector3 Goal, NavWaypoint Flight) AtTakeOff()
    {
        var (world, graph) = Make(Gap());
        var jumpers = WithJumps(world, graph, Team.None);
        var goal = new Vector3(30, 0, 0);
        var path = jumpers.FindPath(new Vector3(-6, 0, 0), goal)!;
        var flight = Assert.Single(path.Waypoints, w => w.Kind == NavLinkKind.Jump);
        return (jumpers, jumpers.Position(flight.Jump.From), goal, flight);
    }

    static NavBody Body(float power, JumpPhase phase = JumpPhase.None, int ticks = 0, bool busy = false) =>
        new(phase, ticks, power, 0f, Pack, Config.TickSeconds, busy);

    [Fact]
    public void A_bot_off_the_take_off_cell_walks_back_to_it_and_presses_only_on_it()
    {
        var (graph, take, goal, flight) = AtTakeOff();
        var agent = new NavAgent(graph);
        double t = 0;
        // Waiting on the cell (no power yet): standing, facing the heading, no press.
        var wait = agent.Steer(take, true, goal, t, Body(0f));
        Assert.True(agent.OnJumpLink);
        Assert.False(wait.Jump);
        Assert.Equal(0f, wait.Speed);
        // Drifted 0.45 m off (more than the take-off radius, less than twice it): it walks back, it does not stand there.
        foreach (var off in new[] { new Vector3(0.45f, 0, 0), new Vector3(0, -0.45f, 0), new Vector3(-0.31f, 0.31f, 0) })
        {
            t += 0.05;
            var back = agent.Steer(take + off, true, goal, t, Body(100f));
            Assert.False(back.Jump);
            Assert.True(back.Speed > 0f, $"offset {off}");
            Assert.True(Vector2.Dot(back.Direction, -Vector2.Normalize(new Vector2(off.X, off.Y))) > 0.9f, $"offset {off}: {back.Direction}");
        }
        // On the cell and ready: the press.
        t += 0.05;
        var press = agent.Steer(take, true, goal, t, Body(100f));
        Assert.True(press.Jump && press.Flying);
        Assert.True(agent.JumpInProgress);
        Assert.Equal(1, agent.JumpsStarted);
    }

    [Fact]
    public void A_wait_that_goes_on_too_long_is_stuck_drops_the_link_and_plans_without_it()
    {
        var (graph, take, goal, flight) = AtTakeOff();
        var agent = new NavAgent(graph);
        double t = 0;
        double allowed = agent.JumpWaitSeconds + flight.Jump.Power / Pack.RechargeRate;   // the power never comes (the body keeps 0)
        for (; t < allowed - 0.5; t += 0.05) agent.Steer(take, true, goal, t, Body(0f));
        Assert.Equal(0, agent.StuckCount);                                       // the recharge time is allowed for
        Assert.Equal(0, agent.ExcludedJumps);
        for (; t < allowed + 1.5; t += 0.05) agent.Steer(take, true, goal, t, Body(0f));
        Assert.True(agent.StuckCount >= 1);
        Assert.Equal(1, agent.ExcludedJumps);
        // (Other links from the cell may remain; the dropped one is not planned through again.)
        Assert.DoesNotContain(agent.Path!.Waypoints, w => w.Kind == NavLinkKind.Jump && w.Jump.From == flight.Jump.From && w.Node == flight.Node);
    }

    [Fact]
    public void A_wait_does_not_survive_leaving_the_link()
    {
        var (graph, take, goal, flight) = AtTakeOff();
        var agent = new NavAgent(graph);
        double t = 0;
        double allowed = agent.JumpWaitSeconds + flight.Jump.Power / Pack.RechargeRate;
        double half = allowed * 0.7;
        for (double until = half; t < until; t += 0.05) agent.Steer(take, true, goal, t, Body(0f));
        // Something else for a moment (a body the agent cannot jump with: it walks the link as a plain path), then back.
        for (double until = t + 0.6; t < until; t += 0.05) agent.Steer(take, true, goal, t, null);
        Assert.False(agent.OnJumpLink);
        for (double until = t + half; t < until; t += 0.05) agent.Steer(take, true, goal, t, Body(0f));
        // 1.4 x the allowance in all, but never one unbroken wait: not stuck, nothing dropped.
        Assert.Equal(0, agent.StuckCount);
        Assert.Equal(0, agent.ExcludedJumps);
    }

    [Fact]
    public void A_link_that_misses_twice_is_not_planned_through_again_by_that_agent()
    {
        var (graph, take, goal, flight) = AtTakeOff();
        var agent = new NavAgent(graph);
        double t = 0;
        Assert.True(agent.Steer(take, true, goal, t, Body(100f)).Jump);
        // The jump never takes (still grounded, phase None, long after the press): a miss, and the agent tries the link again ...
        t += 0.6;
        agent.Steer(take, true, goal, t, Body(100f));
        Assert.Equal(1, agent.JumpsMissed);
        Assert.Equal(2, agent.JumpsStarted);
        Assert.Equal(0, agent.ExcludedJumps);
        // ... and after the second miss it plans around it.
        t += 0.6;
        agent.Steer(take, true, goal, t, Body(100f));
        Assert.Equal(2, agent.JumpsMissed);
        Assert.Equal(1, agent.ExcludedJumps);
        agent.Steer(take, true, goal, t += 0.6, Body(100f));
        Assert.DoesNotContain(agent.Path!.Waypoints, w => w.Kind == NavLinkKind.Jump && w.Jump.From == flight.Jump.From && w.Node == flight.Node);
        // Another agent on the same graph still plans through it.
        var fresh = new NavAgent(graph);
        fresh.Steer(take, true, goal, 0, Body(100f));
        Assert.Contains(fresh.Path!.Waypoints, w => w.Kind == NavLinkKind.Jump && w.Jump.From == flight.Jump.From && w.Node == flight.Node);
    }

    [Fact]
    public void A_background_plan_that_lands_after_the_lift_off_does_not_replace_the_path_of_the_flight()
    {
        var (graph, take, goal, flight) = AtTakeOff();
        var agent = new NavAgent(graph) { Background = true, SearchDelayMilliseconds = 300 };
        double t = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (agent.Path is null && watch.Elapsed < TimeSpan.FromSeconds(10)) { agent.Steer(take, true, goal, t += 0.02, Body(0f)); Thread.Sleep(20); }
        Assert.NotNull(agent.Path);
        // A re-plan starts while it waits for the power (the search sleeps 300 ms) ...
        t += 1.2;
        agent.Steer(take, true, goal, t, Body(0f));
        // ... and the press comes with that search still out.
        t += 0.05;
        var press = agent.Steer(take, true, goal, t, Body(100f));
        Assert.True(press.Jump && agent.JumpInProgress);
        int plans = agent.Plans;
        var path = agent.Path;
        Thread.Sleep(700);   // the search finishes now
        for (int k = 0; k < 4; k++)
        {
            t += 0.03;
            var up = agent.Steer(take + new Vector3(0, 0, 2f), false, goal, t, Body(65f, k == 0 ? JumpPhase.TakeOff : JumpPhase.Ascent, 3 + k));
            Assert.True(up.Flying);
            Assert.Equal(plans, agent.Plans);
            Assert.Same(path, agent.Path);
        }
        // A re-plan is due after the landing, not before.
        t += 1.5;
        var landing = path!.Waypoints[agent.Waypoint].Position;
        agent.Steer(landing, true, goal, t, Body(65f, JumpPhase.Landed));
        Assert.False(agent.JumpInProgress);
        Assert.Equal(1, agent.JumpsLanded);
    }

    [Fact]
    public void The_cache_prunes_by_exact_extension_and_only_its_own_temporary_files()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-reimpl-navmixed-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Windows matches "map-*.nav" against ".navjump" files too (and "*.tmp" against any scratch file): neither kind may be
            // pruned by the other's count, and foreign files stay.
            var (world, graph) = Make(Gap());
            ulong key = NavGraph.KeyFor(world.CharacterFor(Team.None), null, Settings);
            var set = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options);
            for (int i = 0; i < NavCache.KeepPerMap; i++)
            {
                File.WriteAllText(NavCache.FileFor(dir, "map", 0x2000UL + (ulong)i), "g");
                File.WriteAllText(NavCache.JumpFileFor(dir, "map", 0x3000UL + (ulong)i), "j");
            }
            string foreign = Path.Combine(dir, "notes.tmp"), foreignOld = Path.Combine(dir, "other.tmp");
            File.WriteAllText(foreign, "keep"); File.WriteAllText(foreignOld, "keep");
            File.SetLastWriteTimeUtc(foreignOld, DateTime.UtcNow.AddDays(-3));
            string ownOld = NavCache.FileFor(dir, "map", 1) + ".99.tmp", ownOldJump = NavCache.JumpFileFor(dir, "map", 1) + ".99.tmp";
            File.WriteAllText(ownOld, "x"); File.WriteAllText(ownOldJump, "x");
            File.SetLastWriteTimeUtc(ownOld, DateTime.UtcNow.AddDays(-3)); File.SetLastWriteTimeUtc(ownOldJump, DateTime.UtcNow.AddDays(-3));
            NavCache.LoadOrBuildJumps(dir, "map", key ^ 5, graph.NodeCount, () => set, out _);   // one more jump file: the jump files are pruned ...
            Assert.Equal(NavCache.KeepPerMap, Directory.GetFiles(dir).Count(f => Path.GetExtension(f) == ".navjump"));   // the old ones kept, plus the new
            Assert.Equal(NavCache.KeepPerMap - 1, Directory.GetFiles(dir).Count(f => Path.GetExtension(f) == ".nav"));   // (a write prunes both kinds to the limit; the counts never mix)
            Assert.True(File.Exists(foreign) && File.Exists(foreignOld));
            Assert.False(File.Exists(ownOld) || File.Exists(ownOldJump));
            // And the other way round.
            NavCache.LoadOrBuild(dir, "map", key ^ 7, Settings, () => graph, out _);
            Assert.Equal(NavCache.KeepPerMap, Directory.GetFiles(dir).Count(f => Path.GetExtension(f) == ".nav"));
            Assert.Equal(NavCache.KeepPerMap - 1, Directory.GetFiles(dir).Count(f => Path.GetExtension(f) == ".navjump"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_cancelled_jump_build_stops_with_an_exception()
    {
        var (world, graph) = Make(Gap());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options, cts.Token));
    }

    [Fact]
    public void A_class_without_a_jump_pack_has_no_links_and_paths_never_fly()
    {
        var (world, graph) = Make(Gap());
        Assert.Throws<ArgumentException>(() => NavJumpSet.Build(graph, world, Team.None, Config, Tactical, Options));
        Assert.Null(graph.Jumps);
        var path = graph.FindPath(new Vector3(-6, 0, 0), new Vector3(30, 0, 0))!;
        Assert.True(path.Partial);
        Assert.DoesNotContain(path.Waypoints, w => w.Kind == NavLinkKind.Jump);
    }
}
