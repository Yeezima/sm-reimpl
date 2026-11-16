using System.Diagnostics;
using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Squad;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Squad.Classes;

/// <summary>
/// W11's bench (review 1 finding 1): 36 jump-pack Marines in four squads on a real 160 x 160 m graph with buildings, a wall the whole length of the map between the Marines (west) and their four owners (east,
/// walking its length), so no leap lands and every follow asks for a route that does not exist: the worst case for route searches, which must be one a second per squad. 1200 real Orks, 450 ticks. Mean, p95
/// and worst of the squad step and the searches made are printed; budget as W9's (median 3 ms, mean under 9 ms; worst printed, not asserted).
/// </summary>
public class SquadClassBenchTests(ITestOutputHelper output)
{
    const int Ticks = 450, Warmup = 30, Orks = 1200;

    [Fact]
    public void Thirty_six_jump_pack_Marines_with_no_landing_search_for_routes_at_most_once_a_second_per_squad()
    {
        var level = new NavigationTests.Level().Floor(-80f, -80f, 80f, 80f);
        for (int x = -60; x <= 60; x += 24) for (int y = -60; y <= 60; y += 24) if (Math.Abs(x) > 12) level.Box(new Vector3(x - 5, y - 5, 0), new Vector3(x + 5, y + 5, 8));
        level.Box(new Vector3(-1, -80, 0), new Vector3(1, 80, 14));   // the wall: unjumpable, unseeable through, no way round
        var mesh = level.Mesh();
        var nav = NavGraph.Build(mesh, null, new NavSettings());
        var sim = new MusouSim(nav);
        var arena = new MusouSquadArena(sim, MusouSquadArena.SightOf(new CollisionWorld(mesh, mesh)));
        var jumpArena = new MusouSquadJumpArena(nav);
        var jump = new SquadJump(100f, 10f, 50f, 8f, 24f, 2.4f, 10f, 4f, 80f, 0.4f, true);
        var rosters = new SquadRosters();
        for (ulong o = 1; o <= 4; o++) { rosters.AddOwner(o); while (rosters.Recruit(o)) { } rosters.Mirror(o, new SquadLoadout { HealthMax = 400f, ArmourMax = 200f, Jump = jump, Weapon = SquadWeapon.Blade }); }
        var squad = new WarfrontSquadSim(rosters, arena) { JumpArena = jumpArena };
        var landings = new (ulong, int, Vector3)[4];
        for (int o = 0; o < 4; o++) landings[o] = ((ulong)(o + 1), o + 1, new Vector3(-70f, -60f + o * 40f, 0f));
        Assert.Equal(36, squad.BeginBattle(landings));

        var rng = new Random(9);
        for (int i = 0; i < Orks; i++)
        {
            Vector3 at;
            do at = new Vector3(rng.Next(-76, 76), rng.Next(-76, 76), 0f); while (Math.Abs(at.X) < 4 || Math.Abs(((at.X + 60 + 12) % 24) - 12) < 6 && Math.Abs(((at.Y + 60 + 12) % 24) - 12) < 6);
            var type = i % 12 == 0 ? MusouEnemyType.Nob : i % 3 == 0 ? MusouEnemyType.ShootaBoy : MusouEnemyType.Boy;
            Assert.NotEqual((ushort)0, sim.Spawn(type, at, 0f, type == MusouEnemyType.Nob ? 300f : 85f));
        }

        var pos = new Vector3[4]; var dir = new float[4];
        for (int o = 0; o < 4; o++) { pos[o] = new Vector3(72f, -60f + o * 40f, 0f); dir[o] = 1f; }
        var humans = new SquadHuman[4];
        var targets = new MusouTarget[64];
        var attacks = new List<MusouAttack>();
        var squadMs = new List<double>(Ticks);
        uint tick = 0;
        int airTicks = 0;
        for (int k = 0; k < Ticks; k++)
        {
            for (int o = 0; o < 4; o++)
            {
                pos[o].Y += dir[o] * 5f / 30f;
                if (MathF.Abs(pos[o].Y) > 70f) dir[o] = -dir[o];
                humans[o] = new SquadHuman(o + 1, pos[o], true);
            }
            int n = 0;
            foreach (var h in humans) targets[n++] = new MusouTarget(h.Id, h.Position, true);
            n += squad.AppendTargets(targets.AsSpan(n));
            sim.BeginTick(++tick);
            attacks.Clear();
            sim.Step(targets.AsSpan(0, n), null, true, attacks);
            var one = Stopwatch.StartNew();
            foreach (var a in attacks) squad.Hit(a.Player, a.Damage, a.From);
            squad.Step(tick, humans);
            one.Stop();
            if (k >= Warmup) squadMs.Add(one.Elapsed.TotalMilliseconds);
            for (int i = 0; i < 36; i++) if (squad.IsAirborne(i)) airTicks++;
        }
        squadMs.Sort();
        double median = squadMs[squadMs.Count / 2], mean = squadMs.Average(), p95 = squadMs[(int)(squadMs.Count * 0.95)], worst = squadMs[^1];
        output.WriteLine($"class squad step: median {median:F3} ms, mean {mean:F3} ms, p95 {p95:F3} ms, worst {worst:F3} ms over {squadMs.Count} ticks; route searches {squad.RouteSearches} (arena: {jumpArena.RoutesBuilt}), paths {arena.PathsBuilt}, flow goals {arena.FlowGoals}, Marine-ticks airborne {airTicks}");
        Assert.True(median <= 3.0, $"median {median:F3} ms");
        Assert.True(mean <= 9.0, $"mean {mean:F3} ms");
        Assert.True(jumpArena.RoutesBuilt == squad.RouteSearches);
        Assert.True(squad.RouteSearches <= 4 * (Ticks / 30 + 1), $"{squad.RouteSearches} route searches");   // one a second per squad
    }
}
