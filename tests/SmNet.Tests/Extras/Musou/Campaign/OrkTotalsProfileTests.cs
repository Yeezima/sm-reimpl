using System.Diagnostics;
using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The Orks on the map and the tick's cost (the owner, 2026-11-09: "completely remove those orks ... reduce the total amount of orks on the map to 80% of current"; off unless <c>SM_ARMY_PROFILE=1</c>;
/// bounded: one battle of 180 simulated seconds on the synthetic arena, one thread). A human stands near the landing zone with the patrols and the Scrap Meks on, as a real battle has them, and a wave of
/// 1,000 leaves at once for the base. Prints the most Orks of the pool alive, the most Orks of the armies' aggregates (men no one sees as real units), their sum, and the World's tick (mean, p99, max).
/// Numbers of this PC, not of a server under load.
/// </summary>
public class OrkTotalsProfileTests(ITestOutputHelper output)
{
    static bool On => Environment.GetEnvironmentVariable("SM_ARMY_PROFILE") == "1";

    [Fact]
    [Trait("Speed", "Slow")]
    public void Orks_on_the_map_and_the_tick()
    {
        if (!On) return;
        var w = TfwRig.War();
        w.Tfw!.Patrols = true;
        w.TfwMeksOn = true;
        var battle = TfwRig.Deploy(w);
        var stand = battle.Layout.LandingZone + new Vector3(0f, -10f, 0f);
        TfwRig.Teleport(w, stand);
        battle.SendWaveForTests(1000);
        // SM_ARMY_PROFILE_BOSSES=n (at most 8): n Boss Orks too tough to fall (1e6 health) at the far positions from the start, each with his 20 elites and a twin of each Nob (2026-11-10: up to 8 alive).
        int extraBosses = int.TryParse(Environment.GetEnvironmentVariable("SM_ARMY_PROFILE_BOSSES"), out int nb) ? Math.Clamp(nb, 0, 8) : 0;
        for (int b = 0; b < extraBosses; b++)
        {
            var at = battle.Layout.Positions[b % battle.Layout.Positions.Length] + new Vector3(8f * (b / battle.Layout.Positions.Length), 6f, 0f);
            var kind = MusouBossOrks.Kinds[b % MusouBossOrks.Kinds.Length];
            Assert.NotEqual((ushort)0, w.Musou!.Spawn(kind, at, 0f, 1e6f));
            for (int k = 0; k < TfwBossOrkRules.Retinue; k++)
            {
                var type = k % 4 == 3 ? MusouEnemyType.Nob : MusouEnemyType.ArdBoy;
                var spot = at + new Vector3(MathF.Cos(k * 2.4f) * 4f, MathF.Sin(k * 2.4f) * 4f, 0f);
                for (int n = 0; n < (TfwBattleRules.Big(type) ? TfwBattleRules.BigTwins : 1); n++) w.Musou.Spawn(type, spot + new Vector3(n * 1.5f, 0f, 0f), 0f, w.Musou.Rules.Of(type).Health);
            }
        }
        var ticks = new List<double>();
        int realMost = 0, aggregateMost = 0, totalMost = 0, armyRealMost = 0, bossesMost = 0, bigsMost = 0;
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("SM_ARMY_PROFILE_SECONDS"), out int sec) && sec is > 0 and <= 900 ? sec : 180;   // bounded: 15 min at most
        var watch = new Stopwatch();
        for (int t = 0; t < seconds * 30; t++)
        {
            if (!w.Players[TfwRig.Human].Alive) w.TfwRespawnAt(TfwRig.Human, stand, 0f);
            watch.Restart();
            w.Step(_ => default);
            ticks.Add(watch.Elapsed.TotalMilliseconds);
            int real = w.Musou!.Alive, aggregate = 0;
            foreach (var a in battle.ArmyList) if (a.Side == TfwArmySide.Ork) aggregate += a.FarCount;
            realMost = Math.Max(realMost, real);
            aggregateMost = Math.Max(aggregateMost, aggregate);
            totalMost = Math.Max(totalMost, real + aggregate);
            armyRealMost = Math.Max(armyRealMost, battle.ArmyOrksAlive);
            bossesMost = Math.Max(bossesMost, battle.BossOrks.Count);
            bigsMost = Math.Max(bigsMost, battle.BigsAlive + battle.ArmyBigAlive);
        }
        var sorted = ticks.OrderBy(x => x).ToList();
        output.WriteLine($"ORKTOTALS {seconds} s, Boss Orks at most {bossesMost} ({battle.BossOrksCome} came, {extraBosses} placed), big Orks off the caps at most {bigsMost}, one human at the landing zone, a wave of 1000 at once: real Orks alive at most {realMost} (the armies' real at most {armyRealMost}), aggregate Orks at most {aggregateMost}, " +
            $"both at most {totalMost}, never made for want of room {(battle.OrkAggregates ? 0 : battle.OrksNeverMade)}; tick mean {ticks.Average():F2} ms, p99 {sorted[(int)(sorted.Count * 0.99)]:F2} ms, max {sorted[^1]:F2} ms; waves sent {battle.WavesSent}, Orks killed {battle.OrksKilled}");
        Assert.True(sorted[^1] < 2000, "a tick ran over two seconds");
    }
}
