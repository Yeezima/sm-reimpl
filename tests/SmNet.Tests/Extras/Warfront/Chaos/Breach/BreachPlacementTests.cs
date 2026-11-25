using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmNet.Tests.Extras.Warfront.Fortress;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Chaos;
using SmSim.Extras.Warfront.Fortress;

namespace SmNet.Tests.Extras.Warfront.Chaos.Breach;

/// <summary>Chaos Warfront C4: where the breaches stand (on the enemy sources; 40 m from the landing, 25 m from the capture points, 30 m apart, none in the plaza; 1/2/3 by node, 2 at the fortress, 0 at the HQ).</summary>
public class BreachPlacementTests
{
    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    /// <summary>Twelve sources on a ring round the arena's middle, some too near the places.</summary>
    static readonly Vector3[] Many = Enumerable.Range(0, 12).Select(k => new Vector3(MathF.Cos(k * MathF.Tau / 12) * 90f, MathF.Sin(k * MathF.Tau / 12) * 90f, 0f))
        .Concat([TfwTestArena.LandingZone + new Vector3(10f, 0f, 0f), TfwTestArena.Position2 + new Vector3(5f, 5f, 0f)]).ToArray();

    static void AssertRules(WarfrontBreaches b, TfwLayout layout, bool points = true)
    {
        foreach (var x in b.All)
        {
            Assert.True(Flat(x.Position, layout.LandingZone) >= WarfrontBreachRules.MinFromLanding, $"#{x.Id} from the landing zone");
            if (points) foreach (var p in layout.Positions.Append(layout.Hq)) Assert.True(Flat(x.Position, p) >= WarfrontBreachRules.MinFromPoints, $"#{x.Id} from a point");
            foreach (var y in b.All) if (y != x) Assert.True(Flat(x.Position, y.Position) >= WarfrontBreachRules.MinApart, $"#{x.Id} from #{y.Id}");
        }
    }

    [Fact]
    public void DistancesFromLzPointsAndEachOther()
    {
        for (uint seed = 1; seed <= 20; seed++)
        {
            var layout = BreachRig.Layout(Many);
            var b = WarfrontBreaches.Place(layout, null, BreachRig.Roll(seed), new WarfrontBreachSite(WarfrontBreachBattle.Lane, 3), BreachRig.Rate);
            Assert.Equal(3, b.All.Count);
            AssertRules(b, layout);
        }
        // a source too near the landing zone gives a breach on the ring round it, not on it
        var source = TfwTestArena.LandingZone + new Vector3(-30f, 0f, 0f);
        var near = BreachRig.Layout(source);
        var one = WarfrontBreaches.Place(near, null, BreachRig.Roll(1), new WarfrontBreachSite(WarfrontBreachBattle.Lane, 1), BreachRig.Rate);
        Assert.Single(one.All);
        Assert.InRange(Flat(one.All[0].Position, source), WarfrontBreachRules.RingStep - 0.01f, WarfrontBreachRules.RingStep * WarfrontBreachRules.Rings + 0.01f);
        AssertRules(one, near);
    }

    [Fact]
    public void NoneInPlaza()
    {
        var plaza = new Vector3(0f, 0f, 0f);
        var layout = BreachRig.Layout([new(0f, 10f, 0f), new(10f, -5f, 0f), new(-15f, 0f, 0f), new(0f, 60f, 0f), new(60f, 0f, 0f)]);
        for (uint seed = 1; seed <= 20; seed++)
        {
            var b = WarfrontBreaches.Place(layout, null, BreachRig.Roll(seed), new WarfrontBreachSite(WarfrontBreachBattle.Fortress, 0, plaza, 45f), BreachRig.Rate);
            Assert.Equal(2, b.All.Count);
            Assert.All(b.All, x => Assert.True(Flat(x.Position, plaza) > 45f, $"#{x.Id} at {x.Position} is in the plaza"));
        }
        // the fortress battle itself on the stub arena
        var (w, battle) = WarfrontChaosOrkPins.Fortress(BreachRig.Chaos());
        TfwRig.Steps(w, 1);
        var setup = w.Tfw!.Warfront!.Fortress!;
        Assert.NotNull(battle.ChaosBreaches);
        Assert.Equal(2, battle.ChaosBreaches.Wanted);
        Assert.All(battle.ChaosBreaches.All, x => Assert.False(Flat(x.Position, setup.Plan.BossSpot) <= setup.Plan.ArenaRadius));
    }

    [Fact]
    public void CountsPerBattleKind()
    {
        Assert.Equal(1, WarfrontBreachRules.Count(WarfrontBreachBattle.Lane, 1));
        Assert.Equal(2, WarfrontBreachRules.Count(WarfrontBreachBattle.Lane, 2));
        Assert.Equal(3, WarfrontBreachRules.Count(WarfrontBreachBattle.Lane, 3));
        Assert.Equal(2, WarfrontBreachRules.Count(WarfrontBreachBattle.Fortress, 0));
        Assert.Equal(0, WarfrontBreachRules.Count(WarfrontBreachBattle.Hq, 0));
        for (int node = 1; node <= 3; node++)
            Assert.Equal(node, BreachRig.Placed(node, Many.Take(12).ToArray()).All.Count);
        // in the World: a lane battle of node 1, the HQ defence
        var (lane, lb) = BreachRig.Lane(BreachRig.Chaos());
        int laneNode = lane.Tfw!.Warfront!.Node;
        Assert.Equal(WarfrontBreachRules.Count(WarfrontBreachBattle.Lane, laneNode), lb.ChaosBreaches!.Wanted);
        Assert.Equal(lb.ChaosBreaches.Wanted, lb.ChaosBreaches.All.Count);
        var (hq, hb) = WarfrontChaosOrkPins.Hq(BreachRig.Chaos());
        TfwRig.Steps(hq, 1);
        Assert.NotNull(hb.ChaosBreaches);
        Assert.Empty(hb.ChaosBreaches.All);
        Assert.Equal(0, hb.ChaosBreaches.Wanted);
    }

    [Fact]
    public void SeededSameEveryTime()
    {
        var a = BreachRig.Lane(BreachRig.Chaos()).Battle.ChaosBreaches!.All.Select(x => x.Position).ToArray();
        var b = BreachRig.Lane(BreachRig.Chaos()).Battle.ChaosBreaches!.All.Select(x => x.Position).ToArray();
        Assert.Equal(a, b);
        var layout = BreachRig.Layout(Many);
        var site = new WarfrontBreachSite(WarfrontBreachBattle.Lane, 3);
        var one = WarfrontBreaches.Place(layout, null, BreachRig.Roll(7), site, BreachRig.Rate).All.Select(x => x.Position).ToArray();
        Assert.Equal(one, WarfrontBreaches.Place(layout, null, BreachRig.Roll(7), site, BreachRig.Rate).All.Select(x => x.Position).ToArray());
        var seeds = Enumerable.Range(1, 10).Select(s => string.Join(";", WarfrontBreaches.Place(layout, null, BreachRig.Roll((uint)s), site, BreachRig.Rate).All.Select(x => x.Position))).Distinct().Count();
        Assert.True(seeds > 1, "another battle rolls other places");
    }

    [Fact]
    public void FewerCandidatesPlacesWhatFits()
    {
        var lines = new List<string>();
        var layout = BreachRig.Layout(new Vector3(-90f, 0f, 0f), new Vector3(-95f, 10f, 0f));   // two sources, 11 m apart: one breach (30 m apart) unless a ring spot fits
        var b = WarfrontBreaches.Place(layout, null, BreachRig.Roll(3), new WarfrontBreachSite(WarfrontBreachBattle.Lane, 3), BreachRig.Rate, lines.Add);
        Assert.Equal(3, b.Wanted);
        Assert.InRange(b.All.Count, 1, 2);
        AssertRules(b, layout);
        Assert.Contains(lines, l => l.Contains("no more places fit"));
        var none = WarfrontBreaches.Place(BreachRig.Layout(), null, BreachRig.Roll(3), new WarfrontBreachSite(WarfrontBreachBattle.Lane, 2), BreachRig.Rate);
        Assert.Empty(none.All);
        Assert.False(none.AnyStanding);
    }
}
