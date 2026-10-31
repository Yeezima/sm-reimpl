using System.Numerics;
using SmContent;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou": the enemies' walking graph ignores the spawn-room blockers (lead's decision 2026-10-31, Musou only; <see cref="GameServer.BuildMusouNav"/>),
/// so the Warboss starts inside the Chaos spawn room and walks out of it, while the players keep their side's collision and the bots their own graphs and cache
/// files. A synthetic yard shows the rule without an install; Manufactorum (mp_tdm_09, from the player's install, read-only; skipped without one) shows the
/// Warboss's spawn node inside the Chaos start area with a finite walk to the players in the Space Marines' room, and reports its length.
/// </summary>
[Collection(MusouBench.Name)]
public class MusouChaosRoomTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------ synthetic yard (no install)

    static (Vector3, Vector3, Vector3, Vector3) Floor(float x0, float x1, float y0, float y1) =>
        (new(x0, y0, 0f), new(x1, y0, 0f), new(x1, y1, 0f), new(x0, y1, 0f));

    static (Vector3, Vector3, Vector3, Vector3) Wall(Vector2 a, Vector2 b) =>
        (new(a.X, a.Y, -1f), new(b.X, b.Y, -1f), new(b.X, b.Y, 40f), new(a.X, a.Y, 40f));

    static StaticMesh Mesh(params (Vector3 A, Vector3 B, Vector3 C, Vector3 D)[] quads)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        foreach (var (a, b, c, d) in quads) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        return new StaticMesh(v.ToArray(), idx.ToArray());
    }

    /// <summary>A walled 40 x 24 m yard: the Space Marines' room west of a marine blocker at x -10, the Chaos room east of a Chaos blocker at x 10.</summary>
    static GameMap Yard()
    {
        var mesh = Mesh(Floor(-20, 20, -12, 12), Wall(new(-20, -12), new(20, -12)), Wall(new(20, -12), new(20, 12)),
            Wall(new(20, 12), new(-20, 12)), Wall(new(-20, 12), new(-20, -12)));
        SpawnBlocker[] blockers =
        [
            new(Team.SpaceMarines, new Vector3(-10, 0, 19), new Vector3(1, 24, 40), 0f),
            new(Team.Chaos, new Vector3(10, 0, 19), new Vector3(1, 24, 40), 0f),
        ];
        var collision = new CollisionWorld(mesh, mesh, null, blockers);
        StartArea[] areas =
        [
            new(Team.SpaceMarines, new Vector3(-15, 0, 0), new Vector3(8, 20, 4), 0f),
            new(Team.Chaos, new Vector3(15, 0, 0), new Vector3(8, 20, 4), 0f),
        ];
        return new GameMap("musou_test_yard", collision, [new SpawnPoint(new Vector3(0, 0, 0), 0f)], 0, [], areas, blockers);
    }

    [Fact]
    public void The_enemies_graph_walks_through_the_spawn_room_blockers_and_is_cached_apart()
    {
        var map = Yard();
        var config = EvadeTests.Config;
        string dir = Path.Combine(Path.GetTempPath(), "sm-reimpl-musou-nav-" + Guid.NewGuid().ToString("N"));
        try
        {
            var nav = GameServer.BuildMusouNav(map, config, out string note, dir);
            Assert.NotNull(nav);
            output.WriteLine(note);
            var chaosRoom = new Vector3(15, 0, 0);
            var marineRoom = new Vector3(-15, 0, 0);
            var path = nav!.FindPath(chaosRoom, marineRoom, goalSnap: 1f);
            Assert.NotNull(path);
            Assert.False(path!.Partial);
            Assert.InRange(path.Length, 29f, 34f);   // straight across both blockers

            // The Space Marines' own graph (what the bots and MU0–MU2 used): the Chaos blocker seals the Chaos room.
            var settings = NavSettings.From(config);
            var area = NavArea.Around([new Vector3(0, 0, 0), marineRoom, chaosRoom], BotNavigation.Margin);
            var marines = NavGraph.Build(map.Collision.CharacterFor(Team.SpaceMarines), map.Collision.KillVolumes, settings, area);
            var sealedPath = marines.FindPath(chaosRoom, marineRoom, goalSnap: 1f);
            Assert.True(sealedPath is null || sealedPath.Partial, "the marines' graph should not cross the Chaos blocker");

            // Its own cache file, apart from the bots' (<map>-<key>.nav).
            var files = Directory.GetFiles(dir, "*.nav").Select(Path.GetFileName).ToArray();
            Assert.Single(files);
            Assert.StartsWith("musou_test_yard" + GameServer.MusouNavCacheSuffix + "-", files[0]);
            ulong key = NavGraph.KeyFor(map.Collision.Character, map.Collision.KillVolumes, settings, area);
            foreach (var side in new[] { Team.SpaceMarines, Team.Chaos })
                Assert.NotEqual(key, NavGraph.KeyFor(map.Collision.CharacterFor(side), map.Collision.KillVolumes, settings, area));

            // A second build reads the file.
            var again = GameServer.BuildMusouNav(map, config, out string note2, dir);
            Assert.Contains("read from the cache", note2);
            Assert.Equal(nav.NodeCount, again!.NodeCount);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void The_players_keep_their_sides_collision()
    {
        var map = Yard();
        // The blockers still stop the other side's players (the core collision is not changed by Musou).
        Assert.NotSame(map.Collision.Character, map.Collision.CharacterFor(Team.SpaceMarines));
        Assert.NotSame(map.Collision.Character, map.Collision.CharacterFor(Team.Chaos));
        Assert.True(map.Collision.CharacterFor(Team.SpaceMarines).TriangleCount > map.Collision.Character.TriangleCount);
    }

    // ------------------------------------------------------------------ Manufactorum (install)

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (Manufactorum's graph needs the player's install; never in CI)";
        }
    }

    /// <summary>Whether <paramref name="p"/> is inside <paramref name="area"/>'s footprint (its yaw-rotated box), within its height plus 2 m.</summary>
    static bool Inside(StartArea area, Vector3 p)
    {
        var d = p - area.Center;
        float c = MathF.Cos(area.Yaw), s = MathF.Sin(area.Yaw);
        float u = d.X * c + d.Y * s, v = -d.X * s + d.Y * c;
        var half = area.Size / 2f;
        bool flat = area.Shape == AreaShape.Cylinder ? new Vector2(u, v).Length() <= MathF.Min(half.X, half.Y) : MathF.Abs(u) <= half.X && MathF.Abs(v) <= half.Y;
        return flat && MathF.Abs(d.Z) <= MathF.Max(half.Z, 1f) + 2f;
    }

    [InstallFact]
    public void On_manufactorum_the_warboss_starts_in_the_chaos_room_and_walks_to_the_players()
    {
        var dir = Game.Value!;
        var content = MapContent.Load(dir, "mp_tdm_09");
        var collision = content.ToCollisionWorld();
        var map = new GameMap("mp_tdm_09", collision, content.SpawnPoints(), content.Hash, content.SimControlPoints(), content.SimStartAreas(), content.SimBlockers());
        var config = GameplayTuning.LoadForMatch(dir, ContentExtras.Musou).Config;
        var nav = GameServer.BuildMusouNav(map, config, out string note);
        Assert.NotNull(nav);
        output.WriteLine($"enemies' graph: {nav!.NodeCount} nodes, {nav.LinkCount} links ({note})");
        Assert.True(File.Exists(NavCache.FileFor(BotNavigation.CacheDirectory, "mp_tdm_09" + GameServer.MusouNavCacheSuffix,
            NavGraph.KeyFor(collision.Character, collision.KillVolumes, NavSettings.From(config),
                NavArea.Around(map.Spawns.Select(s => s.Position).Concat(map.ControlPoints!.Select(p => p.Center)).Concat(map.StartAreas!.Select(a => a.Center)).ToList(), BotNavigation.Margin)))),
            "the Musou graph's own cache file");

        var rooms = map.StartAreas!;
        var chaos = rooms.First(r => r.Side == Team.Chaos);
        var marines = rooms.First(r => r.Side == Team.SpaceMarines);
        var one = new MusouWaveSpec(1, 1, [(MusouEnemyType.Boy, 1f)]);
        var w = new World(config with { MusouRules = MusouRules.Default with { PlayerDamageScale = 0f, BreakSeconds = 0.1f, Waves = [one, one, one, one, one] } },
            collision, map.Spawns)
            { Rules = new MatchRules { CountdownTime = 0f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 }, StartAreas = rooms };
        Assert.True(w.StartMusou(nav));
        int cls = Enumerable.Range(0, config.Classes.Count).First(i => config.Classes[i].Side == Team.SpaceMarines);
        for (int id = 1; id <= 4; id++) w.Spawn(id, w.AssignClass(cls));
        Assert.NotNull(w.MusouDirector!.BossSpawn);
        Assert.True(Inside(chaos, w.MusouDirector.BossSpawn!.Value), $"the boss spot {w.MusouDirector.BossSpawn} is outside the Chaos start area");

        var sim = w.Musou!;
        var d = w.MusouDirector!;
        Span<ushort> ids = stackalloc ushort[8];
        for (int t = 0; t < 30 * 60 && d.BossId == 0; t++)
        {
            w.Step(_ => default);
            foreach (var id in ids[..sim.Query(Vector3.Zero, 1e6f, ids, 1e6f)]) if (id != d.BossId) sim.Damage(id, 1, 1e6f, Vector3.Zero, out _);
        }
        Assert.NotEqual(0, d.BossId);
        Assert.True(sim.TryGet(d.BossId, out var boss));
        var node = nav.Position(boss.Node);
        float walk = sim.Flow!.Distance(boss.Node);
        foreach (var p in w.Players.Values) Assert.True(Inside(marines, p.Position), $"a player at {p.Position} is outside the Space Marines' start area");
        output.WriteLine($"the Warboss's spawn node {boss.Node} at {node} ({Vector3.Distance(node, chaos.Center):0.#} m from the Chaos start area's centre {chaos.Center}, " +
            $"size {chaos.Size}, yaw {chaos.Yaw:0.###}); flow distance to the nearest player {walk:0.#} m, straight line {Vector3.Distance(node, marines.Center):0.#} m " +
            $"to the Space Marines' start area's centre (before: about 321 m from a node 19 m outside the room)");
        Assert.True(Inside(chaos, node), $"the Warboss's spawn node {node} is outside the Chaos start area");
        Assert.True(float.IsFinite(walk), "the Warboss cannot walk to the players");
        // The rooms are at opposite ends of the map: the walk out of the room is the old 321 m plus the way from the room to the old spot, no longer.
        Assert.True(walk < 360f, $"the Warboss's walk is {walk:0.#} m");

        float nearest = float.MaxValue;
        int k = 0;
        for (; k < 30 * 120; k++)
        {
            w.Step(_ => default);
            Assert.True(sim.TryGet(d.BossId, out boss));
            nearest = Math.Min(nearest, w.Players.Values.Min(p => Vector3.Distance(p.Position, boss.Position)));
            if (nearest < 6f) break;
        }
        output.WriteLine($"he reached the players (within 6 m) after {k / 30f:0.#} s");
        Assert.True(nearest < 6f, $"the Warboss got no nearer than {nearest:0.#} m");
        Assert.Equal(0, d.Removed);
    }
}
