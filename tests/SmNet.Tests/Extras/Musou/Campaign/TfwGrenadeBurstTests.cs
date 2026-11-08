using System.Diagnostics;
using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The grenade mass kill on the server (late batch 2026-11-06, stream D item 1: "a grenade that kills many Orks still hitches the game"): a human's frag (one vanilla frag since 2026-11-08; item 21's three big frags were taken out) thrown into
/// a packed crowd of live Orks on the synthetic arena, and the cost of the ticks around the blasts against the quiet ticks before them. The numbers are printed (the measure); the assertions
/// hold only what the burst must not do on any machine.
/// </summary>
public class TfwGrenadeBurstTests(ITestOutputHelper output)
{
    static readonly WeaponSpec Rifle = new("sm_mp_ranged_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.12f, 30, 2000, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    static readonly RigidSpec Rigid = new(65f, 0.3f, 0.4f, 0.05f, 0.05f, [new(0f, 1f), new(90f, 0.2f)], [new(0f, 1f), new(90f, 0.2f)], 0.1f, 0.1f);
    static readonly DistancePoint[] FragFallOff = [new(0f, 1f), new(2.5f, 1f), new(5f, 0.5f), new(7f, 0.3f), new(10f, 0.05f)];
    static readonly ProjectileSpec FragBall = new("ebps\\test\\frag", 20f, 30f, "rigidbody", 0f, [new(5f, 0f), new(10f, 1f)], false, true, false, 1, 1f, 1.5f, 0, null,
        new SplashSpec(10f, new DamageSpec(2000f, 0f, 1f, 1f, 1f, 1f, false), FragFallOff, false, false, "target_all")) { Rigid = Rigid, StopAtMaxBounce = true };
    static readonly ThrownSpec Frag = new("sm_mp_eq_frag_grenade", 2, 1, 1f, 10f, FragBall);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 1f, 10f, 3.5f);

    const int Human = 1;
    static readonly Vector3 At = new(0f, -60f, 0f);

    internal static World War()
    {
        var c = new ClassLoadout("sm - tactical", 0, -1, [Light]) { PrimaryChoices = [0], EquipmentChoices = [0], Side = Team.SpaceMarines, Role = "tactical" };
        var config = MusouSimTests.Base with
        {
            Weapons = [Rifle], Thrown = [Frag], Classes = [c, c with { Name = "csm - tactical", Side = Team.Chaos }], Extras = ContentExtras.Musou,
            MusouRules = MusouRules.Default with { PlayerDamageScale = 0f }, RespawnTime = 60f,
        };
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.Spawn(Human, 0);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(Human, At, 0f);
        Tick(w);
        return w;
    }

    static void Tick(World w, InputButtons buttons = InputButtons.None) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, buttons));

    /// <summary>The crowd: <paramref name="n"/> Orks on a disc 9 m wide, 24 m in front of the player, leashed as a crowd's are (a hurt one wakes them all).</summary>
    static void Crowd(World w, int n, int group)
    {
        var centre = At + new Vector3(0f, 24f, 0f);
        for (int k = 0; k < n; k++)
        {
            float r = 9f * MathF.Sqrt((k + 0.5f) / n), a = k * 2.39996f;
            var type = k % 10 == 0 ? MusouEnemyType.ShootaBoy : MusouEnemyType.Boy;
            Assert.NotEqual(0, w.Musou!.SpawnLeashed(type, centre + new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0f), MathF.PI, 30f, group, 1f));
        }
    }

    internal readonly record struct Burst(int Killed, double QuietMedian, double BlastWorst, double AfterWorst, int BlastTick);

    /// <summary>One measured burst: <paramref name="n"/> Orks, quiet ticks, the throw, the blasts and a second after.</summary>
    internal static Burst Measure(int n)
    {
        var w = War();
        Crowd(w, n, group: 20);
        var times = new List<(double Ms, int Detonations, int Kills)>();
        int alive0 = w.Musou!.Alive;
        for (int k = 0; k < 60; k++) times.Add(Timed(w, InputButtons.None));
        times.Add(Timed(w, InputButtons.Grenade));
        for (int k = 0; k < 360; k++) times.Add(Timed(w, InputButtons.None));
        var quiet = times.Take(60).Skip(10).Select(t => t.Ms).Order().ToArray();
        int first = times.FindIndex(t => t.Detonations > 0);
        Assert.True(first > 0, $"the frags went off (alive {w.Musou.Alive}: {string.Join("; ", w.Projectiles.Select(q => $"{q.Position} v{q.Velocity} {q.Motion} {q.Bounces} tick {w.Tick}"))})");
        double blast = times.Skip(first).Take(5).Max(t => t.Ms);
        double after = times.Skip(first + 5).Take(30).Max(t => t.Ms);
        return new Burst(alive0 - w.Musou.Alive, quiet[quiet.Length / 2], blast, after, first);

        static (double, int, int) Timed(World w, InputButtons b)
        {
            long t0 = Stopwatch.GetTimestamp();
            Tick(w, b);
            return (Stopwatch.GetElapsedTime(t0).TotalMilliseconds, w.LastDetonations.Count(d => d.Thrown), 0);
        }
    }

    /// <summary>
    /// 2026-11-08 (one vanilla frag): a frag that strikes the front of a crowd in the air (the crowd 12 to 16 m ahead) was seen stalling against it with almost no speed and never going off (reported to the lead), so the crowd starts 24 m ahead of the thrower. Earlier, measured 2026-11-07 (three big frags) on the shared PC (100 % CPU from other sessions), warm runs: 150 Orks, 130 killed, quiet tick median 0.45 ms, the blast ticks' worst 0.6 to 1.3 ms; 400 Orks, 279 killed,
    /// quiet 0.8 ms, blasts 1.1 to 1.2 ms. A first run of a process showed 34 to 73 ms once (the JIT and the other sessions' load), so the bound is on the median of three runs.
    /// </summary>
    [Theory]
    [InlineData(150)]
    [InlineData(100)]
    public void A_frag_into_a_packed_crowd_kills_it_and_its_tick_stays_within_a_frame(int n)
    {
        var runs = new List<Burst>();
        for (int r = 0; r < 3; r++)
        {
            var b = Measure(n);
            runs.Add(b);
            output.WriteLine($"GRENADEBURST n {n} run {r}: killed {b.Killed}, quiet median {b.QuietMedian:F2} ms, blast ticks worst {b.BlastWorst:F2} ms, the second after worst {b.AfterWorst:F2} ms (tick {b.BlastTick})");
        }
        Assert.All(runs, b => Assert.True(b.Killed > 0, $"{b.Killed} of {n}"));
        double median = runs.Select(b => b.BlastWorst).Order().ElementAt(1);
        Assert.True(median < 33.3, $"{median:F1} ms: a blast tick ran over a 30 Hz tick");
    }
}
