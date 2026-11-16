using System.Diagnostics;
using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Squad;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Squad;

/// <summary>
/// W9's headless bench: the whole squad (4 players x 9 = 36 Marines) against a crowd of 1200 real Orks (<see cref="MusouSim"/>, no graph), a fixed 450 ticks (15 s at 30 Hz), no more.
/// Budget: the squad step alone averages at most 2 ms per tick (6 % of the 33.3 ms tick at 30 Hz) and the 95th-percentile tick stays under 5 ms (the worst tick is printed, not asserted: this PC runs other agents' work, so one tick can be descheduled); the whole tick (Orks + squad) is printed.
/// </summary>
public class SquadBenchTests(ITestOutputHelper output)
{
    const int Ticks = 450, Warmup = 30, Orks = 1200;
    const double SquadBudgetMeanMs = 2.0, SquadBudgetP95Ms = 5.0;

    [Fact]
    public void Thirty_six_Marines_against_twelve_hundred_Orks_stay_within_the_tick_budget()
    {
        var sim = new MusouSim(null);
        var rosters = new SquadRosters();
        for (int o = 1; o <= 4; o++) { rosters.AddOwner(o); while (rosters.Recruit(o)) { } }
        rosters.Mirror(1, new SquadLoadout { HealthMax = 400f, ArmourMax = 200f });
        var squad = new WarfrontSquadSim(rosters, new MusouSquadArena(sim));
        var landings = new List<(int, Vector3)>();
        for (int o = 1; o <= 4; o++) landings.Add((o, new Vector3(o * 6f - 15f, 0f, 0f)));
        Assert.Equal(36, squad.BeginBattle(landings.ToArray()));

        var rng = new Random(9);
        for (int i = 0; i < Orks; i++)
        {
            float angle = (float)(rng.NextDouble() * Math.PI * 2), radius = 40f + (float)rng.NextDouble() * 80f;
            var type = i % 12 == 0 ? MusouEnemyType.Nob : i % 3 == 0 ? MusouEnemyType.ShootaBoy : MusouEnemyType.Boy;
            Assert.NotEqual((ushort)0, sim.Spawn(type, new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f), 0f, type == MusouEnemyType.Nob ? 300f : 85f));
        }
        Assert.Equal(Orks, sim.Alive);

        var humans = new SquadHuman[4];
        for (int o = 0; o < 4; o++) humans[o] = new SquadHuman(o + 1, landings[o].Item2, true);
        var targets = new MusouTarget[64];
        var attacks = new List<MusouAttack>();
        var squadMs = new List<double>(Ticks);
        double totalMs = 0;
        int hits = 0, downs = 0, kills = 0;
        uint tick = 0;
        for (int k = 0; k < Ticks; k++)
        {
            var whole = Stopwatch.StartNew();
            int n = 0;
            foreach (var h in humans) targets[n++] = new MusouTarget(h.Id, h.Position, true);
            n += squad.AppendTargets(targets.AsSpan(n));
            sim.BeginTick(++tick);
            attacks.Clear();
            sim.Step(targets.AsSpan(0, n), null, true, attacks);
            var one = Stopwatch.StartNew();
            foreach (var a in attacks) if (squad.Hit(a.Player, a.Damage, a.From)) hits++;
            squad.Step(tick, humans);
            one.Stop();
            kills += squad.Kills.Count; downs += squad.Downs.Count;
            if (k >= Warmup) { squadMs.Add(one.Elapsed.TotalMilliseconds); totalMs += whole.Elapsed.TotalMilliseconds; }
        }
        squadMs.Sort();
        double mean = squadMs.Average(), worst = squadMs[^1], p95 = squadMs[(int)(squadMs.Count * 0.95)], p99 = squadMs[(int)(squadMs.Count * 0.99)];
        output.WriteLine($"squad step: mean {mean:F3} ms, p95 {p95:F3} ms, p99 {p99:F3} ms, worst {worst:F3} ms over {squadMs.Count} ticks (budget {SquadBudgetMeanMs} mean / {SquadBudgetP95Ms} p95); whole tick (Orks + squad) mean {totalMs / squadMs.Count:F2} ms of {1000.0 / 30:F1} ms");
        output.WriteLine($"fight: {kills} Ork kills by the squad, {hits} Ork hits on Marines, {downs} Marines downed, Orks alive {sim.Alive}, Marines standing {Enumerable.Range(0, 36).Count(i => squad.StateOf(i) == SquadState.Standing)}");

        Assert.True(kills > 0, "the squad fought");
        Assert.True(hits > 0, "the Orks reached the squad");
        Assert.True(mean <= SquadBudgetMeanMs, $"mean {mean:F3} ms");
        Assert.True(p95 <= SquadBudgetP95Ms, $"p95 {p95:F3} ms");
    }

    [Fact]
    public void An_Ork_attack_on_a_squad_target_downs_a_Marine_in_the_real_pool()
    {
        var sim = new MusouSim(null);
        var rosters = new SquadRosters();
        rosters.AddOwner(1);
        rosters.Mirror(1, new SquadLoadout { HealthMax = 30f, ArmourMax = 0f, Weapon = new SquadWeapon(30f, 0.001f, 1f, 0f) });   // cannot hurt back
        var squad = new WarfrontSquadSim(rosters, new MusouSquadArena(sim));
        squad.BeginBattle([(1, Vector3.Zero)]);
        squad.Order(1, new SmSim.Extras.Musou.Campaign.TfwCommandRecord(1, SmSim.Extras.Musou.Campaign.TfwCommand.Hold, 0));
        for (int i = 0; i < 6; i++) sim.Spawn(MusouEnemyType.Boy, new Vector3(6 + i, 0f, 0f), 0f, 85f);
        var targets = new MusouTarget[4];
        var attacks = new List<MusouAttack>();
        uint tick = 0;
        for (int k = 0; k < 30 * 20 && squad.StateOf(0) == SquadState.Standing; k++)
        {
            int n = squad.AppendTargets(targets);
            sim.BeginTick(++tick);
            attacks.Clear();
            sim.Step(targets.AsSpan(0, n), null, true, attacks);
            foreach (var a in attacks) squad.Hit(a.Player, a.Damage, a.From);
            squad.Step(tick, [new SquadHuman(1, new Vector3(0, 0, 0), true)]);
        }
        Assert.Equal(SquadState.Downed, squad.StateOf(0));
        Assert.Equal(1, rosters.Marine(1, 0)!.Record.TimesDowned);
    }
}
