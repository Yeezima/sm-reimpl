using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>S1 (Codex review, 2026-11-23): the one-pass <see cref="MusouSim.RaycastThrough"/> gives exactly what asking <see cref="MusouSim.Raycast(Vector3, Vector3, float, out float, out bool, ReadOnlySpan{ushort})"/> again past each enemy struck gave.</summary>
public class TfwPierceOnePassTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void One_pass_equals_the_repeated_sweeps_in_dense_random_hordes(int seed)
    {
        var rng = new Random(seed * 7919);
        var sim = new MusouSim(null);
        var types = new[] { MusouEnemyType.Boy, MusouEnemyType.ShootaBoy, MusouEnemyType.Nob };
        int n = 300 + rng.Next(600);
        for (int i = 0; i < n; i++)
        {
            // a tight cluster, so rays strike many and distances tie (some on the very same spot)
            var at = new Vector3(rng.Next(-12, 13) * 0.5f, rng.Next(-12, 13) * 0.5f, rng.Next(0, 3) * 0.5f);
            sim.Spawn(types[rng.Next(types.Length)], at, 0f, 1000f);
        }
        var target = new[] { new MusouTarget(1, new Vector3(30f, 0f, 0f), true) };
        var attacks = new List<MusouAttack>();
        for (uint t = 1; t <= 40; t++) { sim.BeginTick(t); attacks.Clear(); sim.Step(target, null, true, attacks); }   // they move: the trails are real paths
        Span<ushort> pa = stackalloc ushort[64], pb = stackalloc ushort[64];
        Span<float> da = stackalloc float[64], db = stackalloc float[64];
        Span<bool> hb = stackalloc bool[64];
        int rays = 0, struckTotal = 0;
        for (int r = 0; r < 300; r++)
        {
            var origin = new Vector3((float)rng.NextDouble() * 20f - 10f, (float)rng.NextDouble() * 20f - 10f, (float)rng.NextDouble() * 3f);
            var dir = r % 25 == 0 ? Vector3.UnitZ : Vector3.Normalize(new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, ((float)rng.NextDouble() - 0.5f) * (r % 3 == 0 ? 1f : 0.1f)));
            float length = r % 5 == 0 ? 3f + (float)rng.NextDouble() * 10f : 60f;
            foreach (int most in new[] { 1, 3, 9, 64 })
            {
                // old: sweep after sweep, recording heads too
                var oldIds = new List<ushort>(); var oldD = new List<float>(); var oldH = new List<bool>();
                while (oldIds.Count < most)
                {
                    ushort e = sim.Raycast(origin, dir, length, out float d, out bool h, pa[..oldIds.Count]);
                    if (e == 0) break;
                    oldIds.Add(e); oldD.Add(d); oldH.Add(h); pa[oldIds.Count - 1] = e;
                }
                int got = sim.RaycastThrough(origin, dir, length, pb[..most], db[..most], hb[..most]);
                Assert.Equal(oldIds.Count, got);
                for (int i = 0; i < got; i++)
                {
                    Assert.Equal(oldIds[i], pb[i]);
                    Assert.Equal(oldD[i], db[i]);
                    Assert.Equal(oldH[i], hb[i]);
                }
                struckTotal += got;
            }
            rays++;
        }
        Assert.True(struckTotal > rays, $"the rays must strike something ({struckTotal} hits in {rays} rays)");   // a vacuous comparison proves nothing
    }
}
