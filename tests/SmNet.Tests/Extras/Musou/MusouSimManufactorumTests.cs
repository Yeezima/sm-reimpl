using System.Numerics;
using SmContent;
using SmSim;
using SmSim.Extras.Musou;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou" (MU1) on Manufactorum (mp_tdm_09) from the player's install, read-only: the walking graph the server builds (the bots' cache,
/// <see cref="BotNavigation.LoadOrBuildWalk"/> on the Space Marines' collision), 300 enemies released at nodes at least 20 m from four players standing at
/// map spawns. They converge: under 2 % are ever stuck (more than 12 m from every player, a shooter more than 3 m beyond its band, with under 1 m of headway for over 5 s); the Musou part of a tick
/// costs at most 4 ms at the 99th percentile (design section E; measured here and printed; checked in a Release run). A director-driven run of the last wave is measured too. Skipped
/// when no install is found (CI). Nothing of the install is written or kept.
/// </summary>
public class MusouSimManufactorumTests(ITestOutputHelper output)
{
    // The budget is for the server's optimised build: a Debug test run (the default dotnet test) prints the numbers and checks them only in Release
    // (dotnet test -c Release), where the server's code runs.
#if DEBUG
    static readonly bool Optimised = false;
#else
    static readonly bool Optimised = true;
#endif

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    sealed record Arena(SimConfig Config, CollisionWorld Collision, IReadOnlyList<SpawnPoint> Spawns, NavGraph Nav, string Note);

    static readonly Lazy<Arena> Map = new(() =>
    {
        var dir = Game.Value!;
        var content = MapContent.Load(dir, "mp_tdm_09");
        var collision = content.ToCollisionWorld();
        var config = GameplayTuning.LoadForMatch(dir, ContentExtras.Musou).Config;
        var nav = BotNavigation.LoadOrBuildWalk(content, collision, config, Team.SpaceMarines, BotNavigation.CacheDirectory, out string note);
        return new Arena(config, collision, content.SpawnPoints(), nav, note);
    });

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (Manufactorum's graph needs the player's install; never in CI)";
        }
    }

    static World NewWorld(Arena a, MusouRules rules, int players)
    {
        var w = new World(a.Config with { MusouRules = rules }, a.Collision, a.Spawns) { Rules = new MatchRules { CountdownTime = 0f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartMusou(a.Nav));
        int cls = Enumerable.Range(0, a.Config.Classes.Count).First(i => a.Config.Classes[i].Side == Team.SpaceMarines);
        for (int id = 1; id <= players; id++) w.Spawn(id, w.AssignClass(cls));
        return w;
    }

    [InstallFact]
    public void Three_hundred_enemies_converge_on_the_players_and_the_tick_stays_in_budget()
    {
        var a = Map.Value;
        output.WriteLine($"nav: {a.Nav.NodeCount} nodes, {a.Nav.LinkCount} links ({a.Note})");
        var w = NewWorld(a, MusouRules.Default with { PlayerDamageScale = 0f }, players: 4);
        w.MusouDirector!.Hold = true;   // the test releases them itself
        w.Step(_ => default);
        Assert.Equal(MatchState.Playing, w.State);
        var sim = w.Musou!;
        var nav = sim.Walk!;
        var players = w.Players.Values.Select(p => p.Position).ToList();
        output.WriteLine("players at " + string.Join("; ", players.Select(p => $"{p.X:0.#},{p.Y:0.#},{p.Z:0.#}")));
        var sources = players.Select(p => nav.Nearest(p)).Where(n => n >= 0).ToArray();
        sim.Flow!.Complete(sources);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int k = 0; k < 10; k++) sim.Flow.Complete(sources);
        output.WriteLine($"a whole flow field: {clock.Elapsed.TotalMilliseconds / 10:0.###} ms ({nav.NodeCount} nodes, {sim.Flow.LastExpansions / 11} pops)");

        // 300 enemies on reachable nodes at least 20 m from every player, spread over the map (a fixed stride through the nodes).
        var types = new[] { MusouEnemyType.Boy, MusouEnemyType.Boy, MusouEnemyType.ShootaBoy, MusouEnemyType.ArdBoy, MusouEnemyType.Gretchin, MusouEnemyType.ChaosMarine };
        int placed = 0;
        for (long k = 0; placed < 300 && k < nav.NodeCount * 4L; k++)
        {
            int node = (int)(k * 7919 % nav.NodeCount);
            var p = nav.Position(node);
            if (float.IsPositiveInfinity(sim.Flow.Distance(node)) || players.Any(q => Vector3.Distance(q, p) < 20f)) continue;
            var type = types[placed % types.Length];
            Assert.NotEqual(0, sim.Spawn(type, p, 0f, sim.Rules.Of(type).Health, node));
            placed++;
        }
        Assert.Equal(300, placed);

        const int Ticks = 1800, Sample = 15, StuckTicks = 150;
        var anchor = new Dictionary<ushort, (Vector3 At, uint Tick)>();
        var stuck = new HashSet<ushort>();
        var cost = new List<double>();
        var buffer = new MusouEnemy[MusouSim.Capacity];
        for (int t = 0; t < Ticks; t++)
        {
            w.Step(_ => default);
            cost.Add(sim.LastStepMilliseconds);
            if (t % Sample != 0) continue;
            int n = sim.CopyTo(buffer);
            foreach (var e in buffer.AsSpan(0, n))
            {
                if (!e.Alive) continue;
                float near = players.Min(q => new Vector2(q.X - e.Position.X, q.Y - e.Position.Y).Length() + MathF.Abs(q.Z - e.Position.Z));
                var spec = sim.Rules.Of(e.Type);
                float arrived = spec.Ranged ? MathF.Max(12f, spec.RangedMax + 3f) : 12f;   // a shooter holds its band
                if (!anchor.TryGetValue(e.Id, out var was) || near <= arrived || Vector3.Distance(was.At, e.Position) > 1f) { anchor[e.Id] = (e.Position, w.Tick); continue; }
                if (w.Tick - was.Tick > StuckTicks) stuck.Add(e.Id);
            }
        }
        Assert.Equal(300, sim.Alive);
        int n2 = sim.CopyTo(buffer);
        var final = buffer.AsSpan(0, n2).ToArray();
        float Near(MusouEnemy e) => players.Min(q => Vector3.Distance(q, e.Position));
        int within12 = final.Count(e => Near(e) <= 12f), within25 = final.Count(e => Near(e) <= 25f);
        cost.Sort();
        double p50 = cost[cost.Count / 2], p99 = cost[(int)(cost.Count * 0.99)], max = cost[^1];
        output.WriteLine($"after {Ticks / 30} s: {within12}/300 within 12 m of a player, {within25}/300 within 25 m; ever stuck over 5 s: {stuck.Count} " +
            $"({100.0 * stuck.Count / 300:0.##} %); Musou step ms p50 {p50:0.###}, p99 {p99:0.###}, max {max:0.###}; flow generation {sim.Flow.Generation}");
        foreach (var id in stuck.Take(10))
            if (sim.TryGet(id, out var e)) output.WriteLine($"  stuck {id} {e.Type} at {e.Position.X:0.#},{e.Position.Y:0.#},{e.Position.Z:0.#} node {e.Node} flow {(e.Node >= 0 ? sim.Flow.Distance(e.Node) : -1):0.#} state {e.State} v {e.Velocity.Length():0.##} target {e.TargetPlayer} token {e.AttackToken} next {(e.Node >= 0 ? sim.Flow.Next(e.Node) : -2)}" +
                    (e.Node >= 0 && sim.Flow.Next(e.Node) is var nx and >= 0 ? $" at {nav.Position(nx)} adjacent {nav.Adjacent(e.Node, nx)}" : ""));
        Assert.True(stuck.Count < 6, $"{stuck.Count} of 300 stuck (limit: under 2 %)");
        Assert.True(within25 >= 270, $"only {within25} of 300 within 25 m of a player after {Ticks / 30} s");
        if (Optimised) Assert.True(p99 <= 4.0, $"Musou step p99 {p99:0.###} ms over the 4 ms budget");
    }

    [InstallFact]
    public void The_director_runs_the_last_wave_on_manufactorum_within_budget()
    {
        var a = Map.Value;
        // Wave 5 straight away (the first four as empty waves of one Boy each, killed at once below), four players who take no damage.
        var waves = MusouRules.Default.Waves.Select((w, k) => k < 4 ? new MusouWaveSpec(1, 1, [(MusouEnemyType.Boy, 1f)]) : w).ToArray();
        var world = NewWorld(a, MusouRules.Default with { PlayerDamageScale = 0f, BreakSeconds = 0.1f, ScalePerExtraPlayer = 0f, Waves = waves }, players: 4);
        var sim = world.Musou!;
        var director = world.MusouDirector!;
        var cost = new List<double>();
        int peak = 0;
        var gates = director.Gates;
        output.WriteLine("gates: " + string.Join("; ", gates.Select(g => $"{g.X:0.#},{g.Y:0.#},{g.Z:0.#}")));
        var ids = new ushort[8];
        for (int t = 0; t < 2400; t++)
        {
            world.Step(_ => default);
            if (director.Wave < 5 && sim.Alive > 0)
            {
                foreach (var id in ids[..sim.Query(Vector3.Zero, 1e6f, ids, 1e6f)]) sim.Damage(id, 1, 1e4f, Vector3.Zero, out _);
            }
            if (director.Wave == 5) { cost.Add(sim.LastStepMilliseconds); peak = Math.Max(peak, sim.Alive); }
        }
        cost.Sort();
        double p50 = cost[cost.Count / 2], p99 = cost[(int)(cost.Count * 0.99)];
        output.WriteLine($"wave 5: peak {peak} alive (cap {director.MaxAlive}), released {director.Released}/{director.WaveTotal}; ms p50 {p50:0.###}, p99 {p99:0.###}, max {cost[^1]:0.###}");
        Assert.Equal(250, peak);
        if (Optimised) Assert.True(p99 <= 4.0, $"p99 {p99:0.###} ms");
    }
}
