using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Warfront.Chaos;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Chaos.Breach;

/// <summary>Chaos Warfront C4: one qualifying explosive of a human within 3 m (horizontally and vertically) of a breach's centre closes the nearest standing one; it never sends again, its Daemons fight on.</summary>
public class BreachCloseTests(ITestOutputHelper output)
{
    const int Rate = BreachRig.Rate;
    static readonly Vector3 Up = new(0f, 0f, WarfrontBreachRules.CentreHeight);

    [Fact]
    public void OneHitClosesNearestOnly()
    {
        var b = WarfrontBreaches.At(Rate, new Vector3(0f, 0f, 0f), new Vector3(4f, 0f, 0f));
        Assert.Equal(1, b.TryClose(new Vector3(1.5f, 0f, 0f) + Up, 1, 100));
        Assert.Equal(WarfrontBreachState.Closed, b.All[0].State);
        Assert.True(b.All[1].Standing, "one detonation closes one breach");
        Assert.Equal(new WarfrontBreachClosure(100, 1, 1), Assert.Single(b.Closures));
        Assert.Equal(2, b.TryClose(new Vector3(1.5f, 0f, 0f) + Up, 2, 101));   // the next closes the other, now the nearest standing
        Assert.Equal(2, b.All[1].ClosedBy);
        Assert.False(b.AnyStanding);
    }

    [Theory]
    [InlineData(2.99f, true)]
    [InlineData(3.0f, true)]
    [InlineData(3.01f, false)]
    public void ThreeMetreBoundary(float distance, bool closes)
    {
        var b = WarfrontBreaches.At(Rate, new Vector3(10f, 20f, 0f));
        var dir = Vector3.Normalize(new Vector3(1f, 2f, 0f));
        Assert.Equal(closes ? 1 : 0, b.TryClose(new Vector3(10f, 20f, 0f) + Up + dir * distance, 1, 5));
    }

    [Theory]
    [InlineData(2.99f, true)]
    [InlineData(-2.99f, true)]
    [InlineData(3.01f, false)]
    [InlineData(-3.01f, false)]
    public void VerticalLimit(float dz, bool closes)
    {
        var b = WarfrontBreaches.At(Rate, new Vector3(0f, 0f, 0f));
        Assert.Equal(closes ? 1 : 0, b.TryClose(Up + new Vector3(0f, 0f, dz), 1, 5));
    }

    [Fact]
    public void ClosedStopsSpawnsKeepsDaemons()
    {
        var b = WarfrontBreaches.At(Rate, new Vector3(0f, 0f, 0f));
        var f = new BreachRig.Field();
        for (int t = 0; t <= WarfrontBreachRules.Ticks(6f, Rate); t++) b.Step(t, f);
        Assert.Equal(5, b.All[0].Alive.Count);
        Assert.Equal(1, b.TryClose(Up, 1, 1));
        for (int t = WarfrontBreachRules.Ticks(6f, Rate) + 1; t <= WarfrontBreachRules.Ticks(120f, Rate); t++) b.Step(t, f);
        Assert.Equal(5, f.Spawns.Count);
        Assert.Equal(5, b.All[0].Alive.Count);   // never despawned
        Assert.Equal(5, b.AliveDaemons);
    }

    [Fact]
    public void ClosedNeverReopens()
    {
        var b = WarfrontBreaches.At(Rate, new Vector3(0f, 0f, 0f));
        Assert.Equal(1, b.TryClose(Up, 1, 1));   // while it is still opening
        var f = new BreachRig.Field();
        for (int t = 0; t <= WarfrontBreachRules.Ticks(200f, Rate); t++) b.Step(t, f);
        Assert.Equal(WarfrontBreachState.Closed, b.All[0].State);
        Assert.Empty(f.Spawns);
        Assert.Equal(0, b.TryClose(Up, 2, 9));   // a second hit finds nothing standing
        Assert.Equal(1, b.All[0].ClosedBy);
        Assert.Single(b.Closures);
        Assert.Equal(float.PositiveInfinity, b.SphereEntry(Up + new Vector3(0f, -5f, 0f), Vector3.UnitY, 10f));
    }

    [Fact]
    public void CloseAllSilentNoEvent()
    {
        var b = WarfrontBreaches.At(Rate, new Vector3(0f, 0f, 0f), new Vector3(40f, 0f, 0f));
        int version = b.StateVersion;
        b.CloseAll(silent: true, 77);
        Assert.All(b.All, x => { Assert.Equal(WarfrontBreachState.Closed, x.State); Assert.Equal(0, x.ClosedBy); Assert.Equal(77u, x.ClosedAt); });
        Assert.Empty(b.Closures);
        Assert.True(b.StateVersion > version, "the list still shows them closed");
        var loud = WarfrontBreaches.At(Rate, new Vector3(0f, 0f, 0f));
        loud.CloseAll(silent: false, 3);
        Assert.Equal(new WarfrontBreachClosure(3, 1, 0), Assert.Single(loud.Closures));
    }

    [Fact]
    public void The_sphere_is_met_from_outside_and_inside()
    {
        var b = WarfrontBreaches.At(Rate, new Vector3(0f, 0f, 0f));
        Assert.Equal(10f - WarfrontBreachRules.SphereRadius, b.SphereEntry(Up + new Vector3(0f, -10f, 0f), Vector3.UnitY, 20f), 3);
        Assert.Equal(float.PositiveInfinity, b.SphereEntry(Up + new Vector3(0f, -10f, 0f), Vector3.UnitY, 5f));   // not within this step
        Assert.Equal(float.PositiveInfinity, b.SphereEntry(Up + new Vector3(0f, -10f, 0f), -Vector3.UnitY, 20f));   // going away
        Assert.Equal(float.PositiveInfinity, b.SphereEntry(Up + new Vector3(2f, -10f, 0f), Vector3.UnitY, 20f));   // passes beside it
        Assert.Equal(float.PositiveInfinity, b.SphereEntry(Up, Vector3.UnitY, 1f));   // fired from inside: no stop at the muzzle (Fable review 2)
        var two = WarfrontBreaches.At(Rate, new Vector3(0f, 0f, 0f), new Vector3(0f, 10f, 0f));
        Assert.Equal(10f - WarfrontBreachRules.SphereRadius, two.SphereEntry(Up, Vector3.UnitY, 20f), 3);   // from inside the first, it still meets the next
    }

    // ---- in the World ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_frag_at_the_base_closes_it_and_the_daemons_fight_on()
    {
        var (w, battle) = BreachRig.Lane(BreachRig.Chaos());
        var breaches = battle.ChaosBreaches!;
        var breach = breaches.All[0];
        TfwRig.Seconds(w, 7f);   // the first squad is out
        Assert.True(breach.Alive.Count > 0);
        var out1 = breach.Alive.ToList();
        w.Musou!.Frozen = true;
        Assert.True(w.TfwRespawnAt(BreachRig.Human, breach.Position + new Vector3(0f, -2.5f, 0f), 0f));
        BreachRig.Tick(w, InputButtons.None, 0f, -1.2f);
        for (int k = 0; k < 30 && breach.Standing; k++) BreachRig.Tick(w, k < 2 ? InputButtons.Grenade : InputButtons.None, 0f, -1.2f);
        Assert.Equal(WarfrontBreachState.Closed, breach.State);
        Assert.Equal(BreachRig.Human, breach.ClosedBy);
        Assert.All(out1, id => Assert.True(w.Musou.TryGet(id, out var e) && e.Alive, "the Daemons out are not despawned"));
        int spawned = breach.Spawned;
        w.Musou.Frozen = false;
        TfwRig.Seconds(w, 45f);
        Assert.Equal(spawned, breach.Spawned);
        output.WriteLine($"closed at tick {breach.ClosedAt}; {breach.Spawned} Daemons sent in all");
    }

    [Fact]
    public void A_blind_grenade_or_one_landing_4m_away_does_nothing()
    {
        var (w, battle) = BreachRig.Lane(BreachRig.Chaos([BreachRig.Blind]));
        var breach = battle.ChaosBreaches!.All[0];
        BreachRig.Throw(w, breach.Position + new Vector3(0f, -2.5f, 0f), 0f, -1.2f);
        Assert.True(breach.Standing, "a blind grenade at the base");
        var (w2, b2) = BreachRig.Lane(BreachRig.Chaos());
        var far = b2.ChaosBreaches!.All[0];
        var detonated = new List<Vector3>();
        w2.Musou!.Frozen = true;
        Assert.True(w2.TfwRespawnAt(BreachRig.Human, far.Position + new Vector3(0f, -4.8f, 0f), 0f));
        for (int k = 0; k < 30; k++)
        {
            TfwRig.KillAll(w2);
            BreachRig.Tick(w2, k is >= 1 and < 3 ? InputButtons.Grenade : InputButtons.None, 0f, -1.2f);
            detonated.AddRange(w2.LastDetonations.Select(d => d.Position));
        }
        var at = Assert.Single(detonated);
        float flat = new Vector2(at.X - far.Centre.X, at.Y - far.Centre.Y).Length();
        output.WriteLine($"landed {flat:0.00} m from the centre");
        Assert.True(flat > WarfrontBreachRules.CloseRadius, $"{flat} m");
        Assert.True(far.Standing, "a frag landing 4 m away");
    }

    [Fact]
    public void A_shot_through_the_breach_detonates_on_it_and_closes_it_and_an_orks_battle_lets_it_fly_on()
    {
        Vector3 Fly(SmSim.SimConfig config, out WarfrontBreach? breach, Vector3? aim)
        {
            var (w, battle) = BreachRig.Lane(config);
            breach = battle.ChaosBreaches?.All[0];
            var target = breach?.Position ?? aim!.Value;
            var from = target + new Vector3(0f, -20f, 0f);
            var all = BreachRig.Throw(w, from, 0f, 0f);
            Assert.True(all.Count <= 1);
            return all.Count == 0 ? new Vector3(float.NaN) : all[0].Position;
        }
        var at = Fly(BreachRig.Chaos(), out var b, null);
        Assert.NotNull(b);
        float d = Vector3.Distance(at, b.Centre);
        output.WriteLine($"Chaos: detonated {d:0.00} m from the centre");
        Assert.InRange(d, 0f, WarfrontBreachRules.SphereRadius + 0.1f);
        Assert.Equal(WarfrontBreachState.Closed, b.State);
        // the same shot in an Orks battle at the same place: no breach, it flies on far past it (a wall, or out of its life)
        var orks = Fly(BreachRig.Orks(), out var none, b.Position);
        Assert.Null(none);
        Assert.True(float.IsNaN(orks.X) || Vector3.Distance(orks, b.Centre) > 5f, $"Orks: {orks}");
    }
}
