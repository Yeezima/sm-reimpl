using System.Diagnostics;
using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmSim;
using SmSim.Extras.Musou;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Chaos.Breach;

/// <summary>Chaos Warfront C4: an Orks battle, Musou Mode and Classic never see a breach; the Orks lane, HQ and fortress battles hash as on the base before C4 (C2's pins); the hooks are in place; the tick cost.</summary>
public class BreachIsolationTests(ITestOutputHelper output)
{
    // C2's pins (WarfrontChaosOrkPins, claude/chaos-base-1125 @ 6f1cfe8c): C4 moves none of them.
    const ulong LanePin = 0x3920808E3E1C112DUL, HqPin = 0xA0BAD633A482EE15UL, FortressPin = 0xB14E5D60A2AF5BE5UL;

    static ulong Run(World w)
    {
        TfwRig.Seconds(w, 20f);
        return WarfrontChaosOrkPins.Digest(w);
    }

    [Fact]
    public void OrkLaneHqFortressHashesUnchanged()
    {
        var (lane, lb) = WarfrontChaosOrkPins.Lane(WarfrontBattleTests.WarfrontConfig, guardsmen: true);
        Assert.Equal(LanePin, Run(lane));
        Assert.Null(lb.ChaosBreaches);
        var (hq, hb) = WarfrontChaosOrkPins.Hq(WarfrontBattleTests.WarfrontConfig);
        Assert.Equal(HqPin, Run(hq));
        Assert.Null(hb.ChaosBreaches);
        var (fortress, fb) = WarfrontChaosOrkPins.Fortress(WarfrontBattleTests.WarfrontConfig);
        Assert.Equal(FortressPin, Run(fortress));
        Assert.Null(fb.ChaosBreaches);
    }

    [Fact]
    public void An_orks_battle_with_thrown_explosives_never_places_or_stops_on_a_breach()
    {
        var (w, battle) = BreachRig.Lane(BreachRig.Orks());
        var src = battle.Layout.Sources[0];
        var mine = BreachRig.Throw(w, src + new Vector3(0f, -20f, 0f), 0f, 0f);
        Assert.Null(battle.ChaosBreaches);
        Assert.Null(w.WarfrontChaosBreaches);
        Assert.All(mine, d => Assert.True(Vector3.Distance(d.Position, src) > 5f));
    }

    [Fact]
    public void MusouUnchanged()
    {
        var w = TfwRig.War();   // Musou Mode's war: no Warfront rules at all
        var battle = TfwRig.Deploy(w);
        Assert.Null(w.Tfw!.Warfront);
        TfwRig.Seconds(w, 8f);
        Assert.Null(battle.ChaosBreaches);
        Assert.Null(w.WarfrontChaosBreaches);
    }

    [Fact]
    public void NoBreachWithoutChaosRoster()
    {
        var (w, battle) = BreachRig.Lane(BreachRig.Orks());
        TfwRig.Seconds(w, 10f);
        Assert.Null(w.Tfw!.Warfront!.ChaosRoster);
        Assert.Null(battle.ChaosBreaches);
        // and the Chaos one has them
        var (c, cb) = BreachRig.Lane(BreachRig.Chaos());
        Assert.NotNull(cb.ChaosBreaches);
        Assert.NotEmpty(cb.ChaosBreaches.All);
    }

    [Fact]
    public void The_hooks_are_in_place()
    {
        string root = BreachFilterTests.Root();
        var step = File.ReadAllLines(Path.Combine(root, "src/SmSim/Extras/Warfront/Battle/World.WarfrontProgression.cs"));
        Assert.Single(step, l => l.Contains("WarfrontChaosStep();") && l.Contains("// WARFRONT-HOOK"));
        var core = File.ReadAllLines(Path.Combine(root, "src/SmSim/Projectiles.cs"));
        Assert.Single(core, l => l.Contains("ExtrasProjectileBreach(p, direction") && l.Contains("// EXTRAS-HOOK"));
        Assert.Single(File.ReadAllLines(Path.Combine(root, "src/SmServer/Extras/Warfront/GameServer.WarfrontSend.cs")), l => l.Trim().StartsWith("WarfrontChaosSend();"));
        Assert.Single(File.ReadAllLines(Path.Combine(root, "src/SmServer/Extras/Warfront/GameServer.Warfront.cs")), l => l.Trim().StartsWith("WarfrontChaosCreated();"));
        // no edit of TfwBattle.cs for the alive cap: Counted() already counts a breach Daemon (BreachSpawnTests.DaemonsCountAgainstAliveCap)
        Assert.DoesNotContain(File.ReadAllLines(Path.Combine(root, "src/SmSim/Extras/Musou/Campaign/TfwBattle.cs")), l => l.Contains("WarfrontCountedExtra"));
    }

    static List<MusouEnemyType> Alive(World w)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        return all.Take(n).Where(e => e.Alive).Select(e => e.Type).ToList();
    }

    static double TickP99(SimConfig config, float seconds)
    {
        var (w, _) = BreachRig.Lane(config);
        TfwRig.Seconds(w, 10f);
        var ms = new List<double>();
        for (int t = 0; t < (int)(seconds * 30f); t++)
        {
            long a = Stopwatch.GetTimestamp();
            w.Step(_ => default);
            ms.Add(Stopwatch.GetElapsedTime(a).TotalMilliseconds);
        }
        ms.Sort();
        return ms[(int)(ms.Count * 0.99)];
    }

    [Fact(Skip = "Wall-clock bench (Fable review 1, as C2's twin): out of the default suite; the live-test queue runs it on a quiet PC (remove the Skip, run with the console logger)")]
    [Trait("Speed", "Slow")]
    public void A_chaos_lane_tick_with_breaches_p99_is_bounded_by_the_orks()
    {
        double orks = double.MaxValue, chaos = double.MaxValue;
        for (int k = 0; k < 2; k++)
        {
            orks = Math.Min(orks, TickP99(BreachRig.Orks(), 10f));
            chaos = Math.Min(chaos, TickP99(BreachRig.Chaos(), 10f));
        }
        output.WriteLine($"tick p99 (breaches on in Chaos): Orks {orks:0.00} ms, Chaos {chaos:0.00} ms");
        Assert.True(chaos <= Math.Max(orks * 1.5, orks + 5.0), $"Chaos p99 {chaos:0.00} ms against the Orks' {orks:0.00} ms");
    }
}
