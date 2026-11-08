using System.Numerics;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The owner's balance decisions of 2026-11-08 (musou/perf-pass): "near matches far" (a real fight near a player comes out as the far clash does: about even for even numbers, a decisive winner, the loser
/// routs), and "base is a target" (with no point held the waves march on the base). The trials (off unless <c>SM_ARMY_BALANCE=1</c>; bounded: 8 trials of 180 simulated seconds a strength) put a Guard garrison
/// and an Ork wave of the same size at a point beside a human, from 8 bearings, and count who held. The far clash's share is <see cref="TfwClash.Resolve"/>'s Monte Carlo (<c>TfwClashTests</c>).
/// </summary>
public class NearFarBalanceTests(ITestOutputHelper output)
{
    static bool On => Environment.GetEnvironmentVariable("SM_ARMY_BALANCE") == "1";

    /// <summary>One near trial: who held, and the shares each side kept.</summary>
    static (bool GuardsHeld, float GuardsKept, float OrksKept, float Seconds) Trial(int size, int bearing)
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        TfwRig.Secure(w, battle, 0);
        var point = battle.Layout.Positions[0];
        float angle = bearing * MathF.Tau / 8f;
        var out1 = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
        var stand = point - out1 * 45f;
        TfwRig.Teleport(w, stand);
        TfwRig.Steps(w, 30);   // the secure settles (its change reaches the armies) before the trial's armies stand there
        var garrison = battle.AddArmyForTests(TfwArmySide.Guard, TfwArmyOrder.Hold, 0, size, point + out1 * 6f, MathF.Atan2(-out1.X, out1.Y))!;
        var wave = battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Attack, 0, size, point + out1 * 22f, MathF.Atan2(out1.X, -out1.Y), eliteShare: 0.1f)!;
        int t = 0;
        for (; t < 180 * 30; t++)
        {
            if (!w.Players[TfwRig.Human].Alive) w.TfwRespawnAt(TfwRig.Human, stand, 0f);
            w.Step(_ => default);
            bool orksDone = wave.Order == TfwArmyOrder.Retreat || wave.Count == 0;
            bool guardsDone = garrison.Order == TfwArmyOrder.Retreat || garrison.Count == 0 || battle.Objectives[0].State != TfwPositionState.Secured;
            if (orksDone || guardsDone) return (orksDone && !guardsDone, garrison.Count / (float)size, wave.Count / (float)size, t / 30f);
        }
        float g = garrison.Count / (float)size, o = wave.Count / (float)size;
        return (g >= o, g, o, t / 30f);
    }

    [Theory]
    [Trait("Speed", "Slow")]
    [InlineData(200)]
    [InlineData(500)]
    [InlineData(1000)]
    public void Near_matches_far(int size)
    {
        if (!On) return;
        int farWins = 0;
        for (uint seed = 0; seed < 1000; seed++) if (TfwClash.Resolve(size, size, seed).AWon) farWins++;
        output.WriteLine($"NEARFAR far clash {size} v {size}: side A won {farWins} of 1000");
        // The near balance is expansion S6's (the owner's pick at the musou/perf-pass integration, 2026-11-08): the Guardsmen at GuardRules.Strength, a Guardsman worth an Ork in the far clash.
        var trials = Enumerable.Range(0, 8).Select(b => Trial(size, b)).ToList();
        output.WriteLine($"NEARFAR near {size} v {size}, Guardsmen at {GuardRules.Strength:0.###}x the install: Guardsmen held {trials.Count(x => x.GuardsHeld)} of 8; " +
                         $"kept Guardsmen {string.Join(" ", trials.Select(x => x.GuardsKept.ToString("0.00")))}; kept Orks {string.Join(" ", trials.Select(x => x.OrksKept.ToString("0.00")))}; seconds {string.Join(" ", trials.Select(x => x.Seconds.ToString("0")))}");
    }

    /// <summary>"Base is a target": with every point the Orks', the first wave marches on the base; the time until it arrives (the players' time to deploy and push out).</summary>
    [Fact]
    public void First_wave_marches_on_the_base_with_no_point_held()
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        TfwArmy? left = null;
        battle.OnWaveLeft = a => left ??= a;
        TfwRig.Teleport(w, battle.Layout.LandingZone + new Vector3(0f, -5f, 0f));
        int t = 0;
        for (; t < 600 * 30 && battle.FirstArrival < 0; t++)
        {
            if (!w.Players[TfwRig.Human].Alive) w.TfwRespawnAt(TfwRig.Human, battle.Layout.LandingZone, 0f);
            w.Step(_ => default);
        }
        Assert.NotNull(left);
        Assert.Equal(TfwBattle.LandingGoal, left!.Goal);
        output.WriteLine($"BASETARGET first wave left at {battle.Departures[0] / 30f:F0} s, reached the base at {battle.FirstArrival / 30f:F0} s ({(battle.FirstArrival >= 0 ? "arrived" : "not within 600 s")}), size {left.Start}");
        Assert.True(battle.FirstArrival < 0 || battle.FirstArrival >= 90 * 30, "the first wave reached the base before the 90 s lull was over");
    }
}
