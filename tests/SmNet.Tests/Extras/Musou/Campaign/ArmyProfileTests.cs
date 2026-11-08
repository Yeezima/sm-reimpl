using System.Diagnostics;
using System.Numerics;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Expansion S6's profile (off unless <c>SM_ARMY_PROFILE=1</c>; bounded: 90 simulated seconds a size, one thread): a war on the synthetic arena with a human beside position 0, the Guard garrison and an Ork wave of
/// N each clashing there (200, 500, 1,000), so the near layer promotes them while they fight. Prints the World's tick (mean, p99, max), the worst tick of the promotions and of the mass deaths, and what stayed
/// aggregate. A second run with the human far off measures the far clash alone. Numbers of this PC, not of a server under load.
/// </summary>
public class ArmyProfileTests(ITestOutputHelper output)
{
    static bool On => Environment.GetEnvironmentVariable("SM_ARMY_PROFILE") == "1";

    [Theory]
    [Trait("Speed", "Slow")]
    [InlineData(200, true)]
    [InlineData(500, true)]
    [InlineData(1000, true)]
    [InlineData(1000, false)]
    public void Clash_profile(int size, bool humanNear)
    {
        if (!On) return;
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        TfwRig.Secure(w, battle, 0);
        var point = battle.Layout.Positions[0];
        var stand = humanNear ? point + new Vector3(0f, -45f, 0f) : battle.Layout.LandingZone + new Vector3(0f, -10f, 0f);
        TfwRig.Teleport(w, stand);
        var garrison = battle.AddArmyForTests(TfwArmySide.Guard, TfwArmyOrder.Hold, 0, size, point + new Vector3(0f, 8f, 0f), MathF.PI)!;
        var wave = battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Attack, 0, size, point + new Vector3(0f, 20f, 0f), MathF.PI, eliteShare: 0.1f)!;
        var ticks = new List<double>();
        double promoWorst = 0, deathWorst = 0;
        int promoTick = -1, deathTick = -1, deathsMost = 0, promotedBefore = battle.Promoted, orkDeaths = battle.OrksKilled, guardsLost = w.TfwGuards!.Lost + battle.GuardsLostFar;
        int realOrksMost = 0, realSquadsMost = 0;
        var watch = new Stopwatch();
        for (int t = 0; t < 90 * 30; t++)
        {
            if (!w.Players[TfwRig.Human].Alive) w.TfwRespawnAt(TfwRig.Human, stand, 0f);
            watch.Restart();
            w.Step(_ => default);
            double ms = watch.Elapsed.TotalMilliseconds;
            ticks.Add(ms);
            int promoted = battle.Promoted - promotedBefore;
            promotedBefore = battle.Promoted;
            int deaths = battle.OrksKilled - orkDeaths + (w.TfwGuards.Lost + battle.GuardsLostFar) - guardsLost;
            orkDeaths = battle.OrksKilled; guardsLost = w.TfwGuards.Lost + battle.GuardsLostFar;
            if (promoted > 0 && ms > promoWorst) { promoWorst = ms; promoTick = t; }
            if (deaths >= 8 && ms > deathWorst) { deathWorst = ms; deathTick = t; }
            deathsMost = Math.Max(deathsMost, deaths);
            realOrksMost = Math.Max(realOrksMost, battle.ArmyOrksAlive);
            realSquadsMost = Math.Max(realSquadsMost, battle.ArmySquads);
        }
        var sorted = ticks.OrderBy(x => x).ToList();
        double mean = ticks.Average(), p99 = sorted[(int)(sorted.Count * 0.99)], max = sorted[^1];
        output.WriteLine($"ARMYPROFILE {size} v {size} human {(humanNear ? "near" : "far")}: tick mean {mean:F2} ms, p99 {p99:F2} ms, max {max:F2} ms (tick {ticks.IndexOf(max)}); " +
            $"worst promotion tick {promoWorst:F2} ms (tick {promoTick}), worst tick with 8+ deaths {deathWorst:F2} ms (tick {deathTick}), most deaths in a tick {deathsMost}; " +
            $"real army Orks at most {realOrksMost}, army squads at most {realSquadsMost}; after 90 s: wave {wave.Count} ({wave.Order}, real {wave.Real}), garrison {garrison.Count} ({garrison.Order}, real {garrison.Real}); " +
            $"Orks alive {w.Musou!.Alive}, Guardsmen alive {w.TfwGuards.Alive}");
        Assert.True(max < 2000, "a tick ran over two seconds");
    }
}
