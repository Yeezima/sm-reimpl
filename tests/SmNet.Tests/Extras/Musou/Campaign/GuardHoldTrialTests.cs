using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The Guardsmen's strength against an Ork counterattack (night batch 2026-11-06, the owner: "scale up the guardsmen to what you believe will half the time win, half the time lose against ork
/// counterattacks (the purpose of the guardsmen is only to defend captured points)"). A bounded, deterministic headless trial with no player: the field's squads stand on a secured point and one
/// spawn point's full crowd (<see cref="TfwBattleRules.CrowdSize"/>, with the run's middle elite share) comes at them from a seeded bearing and distance. The squads win when the last Ork falls with
/// a Guardsman standing, or one stands at the time limit (the point is still held); they lose when every man is down (the point then falls to the Orks alone on it).
/// </summary>
public static class GuardHoldTrial
{
    public const int Squads = TfwBattleRules.FieldSquads;
    public const float PointRadius = 12f, LimitSeconds = 300f;
    public static readonly Vector3 Point = new(20f, 20f, 0f);

    public readonly record struct Outcome(int Seed, bool Held, float Seconds, int GuardsLeft, int OrksLeft, int Shots, int ShotsHit);

    static NavGraph? _nav;
    static StaticMesh? _mesh;

    /// <summary>The run's middle elite share (half way through: 35 %).</summary>
    public static float Elite => TfwBattleRules.EliteShare(0.5f);

    /// <summary>One counterattack, seeded: the crowd's bearing, its distance (35 to 55 m), its disc and the tick it starts on vary; the rest is the simulation's.</summary>
    public static Outcome Run(int seed, GuardRules? rules = null, int crowd = TfwBattleRules.CrowdSize, float elite = -1f)
    {
        if (elite < 0f) elite = Elite;
        const float half = 90f;
        _mesh ??= new NavigationTests.Level().Floor(-half, -half, half, half).Mesh();
        var collision = new CollisionWorld(_mesh, _mesh);
        _nav ??= NavGraph.Build(_mesh, null, NavSettings.From(new SimConfig()));
        var sim = new MusouSim(_nav) { TickRate = 30, Swarm = MusouSwarmRules.Default, War = TfwCombatRules.Default };
        var guards = new GuardSim(sim, rules) { Sight = collision };

        uint s = (uint)seed;
        float bearing = Unit(Mix(s, 0x6265u)) * MathF.Tau;
        float distance = 35f + 20f * Unit(Mix(s, 0x64u));
        var from = Point + new Vector3(MathF.Cos(bearing), MathF.Sin(bearing), 0f) * distance;
        float facing = MathF.Atan2(-(from.X - Point.X), from.Y - Point.Y);
        for (int q = 0; q < Squads; q++)
        {
            float angle = q * 2.399963f, radius = MathF.Min(6f + 1.5f * q, PointRadius * TfwBattleRules.SquadOnPoint);   // TfwBattle.SquadOffset, inside the point
            Assert.Equal(q, guards.SpawnSquad(Point + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f), facing));
        }
        // The crowd as TfwBattle builds it: elites in runs of EliteSquad (75 % 'Ard Boyz, 25 % Nobs), the fodder 70 % Boyz, 30 % Shootas, on a disc of SpawnDisc.
        int elites = (int)MathF.Round(crowd * elite / TfwBattleRules.EliteSquad) * TfwBattleRules.EliteSquad;
        for (int n = 0; n < crowd; n++)
        {
            MusouEnemyType type = n < elites
                ? (n * 0.7548776662466927 % 1.0 < TfwBattleRules.EliteArds ? MusouEnemyType.ArdBoy : MusouEnemyType.Nob)
                : (n * 0.7548776662466927 % 1.0 < TfwBattleRules.FodderBoyz ? MusouEnemyType.Boy : MusouEnemyType.ShootaBoy);
            float a = (n + s) * 2.399963f, r = TfwBattleRules.SpawnDisc * MathF.Sqrt(Unit(Mix((uint)n + s * 7919u, 0x7469646Bu)));
            var at = from + new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0f);
            Assert.NotEqual((ushort)0, sim.Spawn(type, at, facing + MathF.PI, sim.Rules.Of(type).Health));
        }

        var targets = new MusouTarget[GuardSim.MaxSquads + 1];
        var attacks = new List<MusouAttack>();
        var kills = new List<MusouKill>();
        uint start = 1 + s % 97u, tick = start, end = start + (uint)(LimitSeconds * 30f);
        for (; tick < end && guards.Alive > 0 && sim.Alive > 0; tick++)
        {
            sim.BeginTick(tick);
            guards.BeginTick(tick);
            int nt = guards.AppendTargets(targets);
            targets[nt++] = new MusouTarget(TfwContracts.PointTargetId(0), Point, true, 0f, Goal: true);   // the secured point, to retake
            attacks.Clear();
            sim.Step(targets.AsSpan(0, nt), collision, true, attacks);
            foreach (var attack in attacks)
                if (TfwContracts.IsGuardTarget(attack.Player)) guards.Hit(TfwContracts.SquadOf(attack.Player), attack.Damage, attack.From);
            guards.Step(true);
            kills.Clear();
            sim.TakeKills(kills);
        }
        return new Outcome(seed, guards.Alive > 0, (tick - start) / 30f, guards.Alive, sim.Alive, guards.Shots, guards.ShotsHit);
    }

    static uint Mix(uint a, uint b)
    {
        uint h = a * 0x9E3779B1u ^ b * 0x85EBCA6Bu;
        h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
        return h;
    }

    static float Unit(uint h) => (h >> 8) / 16777216f;

    public static Outcome[] RunSeeds(int seeds, GuardRules? rules = null, int crowd = TfwBattleRules.CrowdSize, float elite = -1f)
    {
        var all = new Outcome[seeds];
        all[0] = Run(1, rules, crowd, elite);   // builds the shared floor and graph before the threads read them
        Parallel.For(1, seeds, k => all[k] = Run(k + 1, rules, crowd, elite));
        return all;
    }
}

public class GuardHoldTrialTests(ITestOutputHelper output)
{
    /// <summary>The owner's target: about half (40 to 60 %) of 20 seeded counterattacks held (11 of 20 at <see cref="GuardRules.Strength"/> 4.5; about 1.5 minutes on 8 cores).</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void The_guard_hold_wins_about_half_of_twenty_seeded_counterattacks()
    {
        var all = GuardHoldTrial.RunSeeds(20);
        foreach (var o in all) output.WriteLine(o.ToString());
        int held = all.Count(o => o.Held);
        output.WriteLine($"held {held} of {all.Length}");
        Assert.InRange(held, 8, 12);   // 40 to 60 %
    }

    [Fact]
    public void A_counterattack_is_deterministic()
    {
        Assert.Equal(GuardHoldTrial.Run(3, crowd: 100), GuardHoldTrial.Run(3, crowd: 100));
    }

    /// <summary>Before the night batch (the install's 85 and the design's 5 a shot): the field fell to a counterattack within a minute, as the scripted run of 2026-11-06 saw (70 men in about 60 s).</summary>
    [Fact]
    public void At_the_old_strength_the_field_falls_within_a_minute()
    {
        var o = GuardHoldTrial.Run(1, new GuardRules { Health = 85f, Damage = 5f });
        output.WriteLine(o.ToString());
        Assert.False(o.Held);
        Assert.True(o.Seconds < 60f, o.ToString());
        Assert.True(o.OrksLeft > TfwBattleRules.CrowdSize * 9 / 10, o.ToString());
    }

    /// <summary>The tuning aid: SM_GUARD_SWEEP=4,4.5,5 prints the win rate of 20 seeds at each strength (the install's 85 health and the design's 5 a shot times each).</summary>
    [Fact]
    public void Sweep()
    {
        if (Environment.GetEnvironmentVariable("SM_GUARD_SWEEP") is not { Length: > 0 } list) return;
        var baseRules = new GuardRules { Health = 85f, Damage = 5f };
        foreach (var (h, d) in list.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).Select(x => (x, x)))
        {
            var rules = baseRules with { Health = baseRules.Health * h, Damage = baseRules.Damage * d };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var all = GuardHoldTrial.RunSeeds(20, rules);
            output.WriteLine($"health x{h} damage x{d}: held {all.Count(o => o.Held)}/20 ({all.Count(o => o.Held && o.OrksLeft > 0)} at the limit), mean {all.Average(o => o.Seconds):0} s, guards left {all.Average(o => o.GuardsLeft):0.0}, orks left {all.Average(o => o.OrksLeft):0}, {watch.Elapsed.TotalSeconds:0.0} s wall");
        }
    }
}
