using System.Diagnostics;
using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Codex review S1, owner "measure first" (2026-11-23): what a piercing shot costs when it sweeps the whole enemy pool once per Ork it strikes (<c>TfwPierceShot</c>'s loop over
/// <see cref="MusouSim.Raycast(Vector3, Vector3, float, out float, out bool, ReadOnlySpan{ushort})"/>). Off unless <c>SM_PIERCE_BENCH=1</c> (a measurement, nothing asserted but that the scenario hit what it should):
/// about 2,000 Orks lined up along four firing lines, four owners firing a lascannon (9 struck) and plasma (64 struck) along them. Single thread, below-normal priority, a fixed 2 x 150 ticks.
/// The owner's bar: change the code only if one shot costs over 1 ms or the p99 tick grows by more than 10 %.
/// Run: <c>$env:SM_PIERCE_BENCH = "1"; dotnet test --filter FullyQualifiedName~TfwPierceBenchTests --logger "console;verbosity=detailed"</c>.
/// </summary>
public class TfwPierceBenchTests(ITestOutputHelper output)
{
    static bool On => Environment.GetEnvironmentVariable("SM_PIERCE_BENCH") == "1";
    const int PerLine = 500, Ticks = 150, Reps = 200, LascannonMost = 9, PlasmaMost = TfwCombatRules.MostPlasmaPierced;
    static readonly Vector3[] Dirs = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY];

    /// <summary>The current loop of <c>TfwPierceShot</c> (without the damage): one sweep of the pool per Ork struck.</summary>
    internal static int NSweeps(MusouSim sim, Vector3 origin, Vector3 dir, float length, int most, Span<ushort> passed, Span<float> at)
    {
        int struck = 0;
        while (struck < most)
        {
            ushort e = sim.Raycast(origin, dir, length, out float d, out _, passed[..struck]);
            if (e == 0) break;
            at[struck] = d;
            passed[struck++] = e;
        }
        return struck;
    }

    /// <summary>The one-pass sweep <c>TfwPierceShot</c> uses now.</summary>
    internal static int OnePass(MusouSim sim, Vector3 origin, Vector3 dir, float length, int most, Span<ushort> ids, Span<float> at, Span<bool> heads) =>
        sim.RaycastThrough(origin, dir, length, ids[..most], at[..most], heads[..most]);

    static MusouSim Horde()
    {
        var sim = new MusouSim(null);
        var rng = new Random(20261123);
        for (int line = 0; line < 4; line++)
            for (int i = 0; i < PerLine; i++)
            {
                float along = 60f + i * 0.5f + (float)rng.NextDouble() * 0.2f, side = ((float)rng.NextDouble() - 0.5f) * 0.3f;
                var at = Dirs[line] * along + new Vector3(Dirs[line].Y, -Dirs[line].X, 0f) * side;
                Assert.NotEqual((ushort)0, sim.Spawn(MusouEnemyType.Boy, at, 0f, 1000f));
            }
        Assert.Equal(4 * PerLine, sim.Alive);
        return sim;
    }

    static double MedianUs(List<long> ticks) { ticks.Sort(); return ticks[ticks.Count / 2] * 1e6 / Stopwatch.Frequency; }

    static string Tick(List<double> ms) { ms.Sort(); return $"typical {ms[ms.Count / 2]:F3} / p95 {ms[(int)(ms.Count * 0.95)]:F3} / p99 {ms[(int)(ms.Count * 0.99)]:F3} ms"; }

    [Fact]
    [Trait("Speed", "Slow")]
    public void Pierce_shot_and_tick_cost_in_a_dense_horde()
    {
        if (!On) return;
        var proc = Process.GetCurrentProcess();
        var old = proc.PriorityClass;
        proc.PriorityClass = ProcessPriorityClass.BelowNormal;
        try
        {
            // 1. one shot, the horde frozen
            var sim = Horde();
            sim.Frozen = true;
            Span<ushort> passed = stackalloc ushort[PlasmaMost];
            Span<float> at = stackalloc float[PlasmaMost];
            var origin = new Vector3(0f, 0f, 1.2f);
            foreach (int most in new[] { LascannonMost, PlasmaMost }) for (int w = 0; w < 20; w++) NSweeps(sim, origin, Dirs[0], 400f, most, passed, at);   // warm up
            var las = new List<long>(); var pla = new List<long>();
            int lasStruck = 0, plaStruck = 0;
            for (int r = 0; r < Reps; r++)
            {
                var dir = Dirs[r % 4];
                long t0 = Stopwatch.GetTimestamp();
                lasStruck = NSweeps(sim, origin, dir, 400f, LascannonMost, passed, at);
                long t1 = Stopwatch.GetTimestamp();
                plaStruck = NSweeps(sim, origin, dir, 400f, PlasmaMost, passed, at);
                long t2 = Stopwatch.GetTimestamp();
                las.Add(t1 - t0); pla.Add(t2 - t1);
            }
            Assert.Equal(LascannonMost, lasStruck);
            Assert.Equal(PlasmaMost, plaStruck);
            Span<bool> heads = stackalloc bool[PlasmaMost];
            var las1 = new List<long>(); var pla1 = new List<long>();
            for (int r = 0; r < Reps; r++)
            {
                var dir = Dirs[r % 4];
                long t0 = Stopwatch.GetTimestamp();
                OnePass(sim, origin, dir, 400f, LascannonMost, passed, at, heads);
                long t1 = Stopwatch.GetTimestamp();
                OnePass(sim, origin, dir, 400f, PlasmaMost, passed, at, heads);
                long t2 = Stopwatch.GetTimestamp();
                las1.Add(t1 - t0); pla1.Add(t2 - t1);
            }
            output.WriteLine($"PIERCE one-pass: lascannon median {MedianUs(las1):F0} us; plasma median {MedianUs(pla1):F0} us");
            output.WriteLine($"PIERCE N-sweep: one full lascannon shot ({LascannonMost} struck): median {MedianUs(las):F0} us; one plasma shot ({PlasmaMost} struck): median {MedianUs(pla):F0} us ({sim.Alive} Orks in the pool)");

            // 2. the server tick: the horde steps (not frozen), with and without four owners firing a lascannon and a plasma shot every tick
            foreach (int mode in new[] { 0, 1, 2 })   // 0: no shooting, 1: N-sweeps, 2: one pass
            {
                var s = Horde();
                var targets = new MusouTarget[4];
                for (int p = 0; p < 4; p++) targets[p] = new MusouTarget(p + 1, new Vector3(0f, 0f, 0f), true);
                var attacks = new List<MusouAttack>();
                var ms = new List<double>(Ticks);
                uint tick = 0;
                for (int k = 0; k < Ticks + 20; k++)
                {
                    var sw = Stopwatch.StartNew();
                    s.BeginTick(++tick);
                    attacks.Clear();
                    s.Step(targets, null, true, attacks);
                    if (mode > 0)
                        for (int p = 0; p < 4; p++)
                        {
                            if (mode == 1) { NSweeps(s, origin, Dirs[p], 400f, LascannonMost, passed, at); NSweeps(s, origin, Dirs[p], 400f, PlasmaMost, passed, at); }
                            else { OnePass(s, origin, Dirs[p], 400f, LascannonMost, passed, at, heads); OnePass(s, origin, Dirs[p], 400f, PlasmaMost, passed, at, heads); }
                        }
                    sw.Stop();
                    if (k >= 20) ms.Add(sw.Elapsed.TotalMilliseconds);
                }
                output.WriteLine($"PIERCE tick {(mode == 0 ? "without shooting" : mode == 1 ? "WITH 4 lascannon + 4 plasma shots per tick, N-sweeps" : "WITH 4 lascannon + 4 plasma shots per tick, one pass")}: {Tick(ms)}");
            }
        }
        finally { proc.PriorityClass = old; }
    }
}
