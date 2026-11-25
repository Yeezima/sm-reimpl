using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Chaos;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Chaos.Breach;

/// <summary>Chaos Warfront C4, the Fable review's fixes and the cases it asked for: no muzzle detonation inside a sphere, ring spots that are walkable from their source, no plaza source at the fortress, puppets, a closer who left, two detonations in one tick, determinism.</summary>
public class BreachReviewTests(ITestOutputHelper output)
{
    const int Rate = BreachRig.Rate;
    static readonly NavSettings Settings = NavSettings.From(TfwRig.Config);

    // ---- 3: ring spots on the source's side of a wall, the fortress's plaza sources ------------------------------------------------------------

    /// <summary>A 120 m floor; with <paramref name="wall"/> a 8 m wall across it at y = -10 with a gap only at the east edge (a long way round).</summary>
    static NavGraph Floor(bool wall)
    {
        var level = new NavigationTests.Level().Floor(-60, -60, 60, 60);
        if (wall) level.Wall(-60, -10, 54, -10, height: 8f);
        return NavGraph.Build(level.Mesh(), null, Settings);
    }

    /// <summary>The source 25 m from the landing zone (its own spot fails), so only the 20 m ring's northern spots pass the distances: across the wall when there is one.</summary>
    static WarfrontBreaches PlaceAcross(NavGraph nav, uint seed)
    {
        var layout = new TfwLayout(new Vector3(0f, -45f, 0f), [new(-55f, 55f, 0f), new(-50f, 50f, 0f), new(-45f, 55f, 0f)], new Vector3(-50f, 58f, 0f), [new Vector3(0f, -20f, 0f)]);
        return WarfrontBreaches.Place(layout, nav, BreachRig.Roll(seed), new WarfrontBreachSite(WarfrontBreachBattle.Lane, 1), Rate);
    }

    [Fact]
    public void A_ring_spot_across_a_wall_from_its_source_is_never_taken()
    {
        var open = Floor(wall: false);
        var walled = Floor(wall: true);
        for (uint seed = 1; seed <= 6; seed++)
        {
            var free = PlaceAcross(open, seed);
            Assert.Single(free.All);   // the control: on an open floor a northern ring spot is taken
            Assert.True(free.All[0].Position.Y > -10f);
            var blocked = PlaceAcross(walled, seed);
            output.WriteLine($"seed {seed}: open at {free.All[0].Position}; walled: {blocked.All.Count} placed");
            Assert.Empty(blocked.All);   // every passing spot is across the wall: none is taken
        }
    }

    [Fact]
    public void A_ring_spot_on_another_floor_is_never_taken()
    {
        // a deck 3.5 m up over the north half: the ring spots there snap to the ground floor's nodes only (within 1.5 m of the source's foot), or to nothing
        var level = new NavigationTests.Level().Floor(-60, -60, 60, 60).Floor(-60, -12, 60, 60, z: 3.5f);
        var nav = NavGraph.Build(level.Mesh(), null, Settings);
        for (uint seed = 1; seed <= 4; seed++)
            foreach (var x in PlaceAcross(nav, seed).All) Assert.True(MathF.Abs(x.Position.Z) <= WarfrontBreachRules.RingVertical, $"#{x.Id} at {x.Position}");
    }

    [Fact]
    public void A_fortress_plaza_source_never_takes_a_breach()
    {
        var layout = BreachRig.Layout(new Vector3(-90f, 60f, 0f), new Vector3(90f, 60f, 0f));
        var site = new WarfrontBreachSite(WarfrontBreachBattle.Fortress, 0, default, 0f, [2, 0], 2);
        Assert.False(site.SourceAllowed(0));
        Assert.True(site.SourceAllowed(1));
        for (uint seed = 1; seed <= 10; seed++)
        {
            var b = WarfrontBreaches.Place(layout, null, BreachRig.Roll(seed), site, Rate);
            Assert.Equal(new Vector3(90f, 60f, 0f), Assert.Single(b.All).Position);
        }
        // the stub fortress: no breach on (or ringed round) a source of its last stage
        var (w, battle) = WarfrontChaosOrkPins.Fortress(BreachRig.Chaos());
        TfwRig.Steps(w, 1);
        var plan = w.Tfw!.Warfront!.Fortress!.Plan;
        var plaza = Enumerable.Range(0, plan.Layout.Sources.Length).Where(k => plan.SourceStage[k] >= plan.MaxStage).Select(k => plan.Layout.Sources[k]).ToList();
        foreach (var x in battle.ChaosBreaches!.All)
            foreach (var s in plaza) Assert.True(new Vector2(x.Position.X - s.X, x.Position.Y - s.Y).Length() > WarfrontBreachRules.RingStep * WarfrontBreachRules.Rings + 0.01f, $"#{x.Id} by a plaza source");
    }

    // ---- 2: a shot fired from inside a breach ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_shot_fired_from_inside_a_breach_does_not_go_off_at_the_muzzle()
    {
        var (w, battle) = BreachRig.Lane(BreachRig.Chaos());
        var breach = battle.ChaosBreaches!.All[0];
        var mine = BreachRig.Throw(w, breach.Position + new Vector3(0f, -0.3f, 0f), 0f, 0f, seconds: 0.5f);   // standing in it, the eye inside the sphere
        var health = w.Players[BreachRig.Human].Health;
        foreach (var d in mine) Assert.True(Vector3.Distance(d.Position, breach.Centre) > WarfrontBreachRules.SphereRadius + 1f, $"went off at {d.Position}");
        Assert.True(breach.Standing, "the round flew out of it and on");
        output.WriteLine($"{mine.Count} detonation(s), health {health}");
    }

    // ---- 7: puppets, a closer who left, two at once, determinism --------------------------------------------------------------------------

    [Fact]
    public void A_puppets_round_flies_through_the_breach_and_closes_nothing()
    {
        var (w, battle) = BreachRig.Lane(BreachRig.Chaos());
        var breach = battle.ChaosBreaches!.All[0];
        int puppet = TfwContracts.PuppetIdFirst + 5;
        w.Spawn(puppet, w.AssignClass(0));
        w.Musou!.Frozen = true;
        var from = breach.Position + new Vector3(0f, -20f, 0f);
        Assert.True(w.TfwRespawnAt(puppet, from, 0f));
        bool seen = false;
        var near = new List<Vector3>();
        int k = 0;
        w.Tfw!.PuppetBrain = (id, p) => new PlayerInput(w.Tick, 0f, 0f, 0f, id == puppet && k is >= 1 and < 3 ? InputButtons.Grenade : InputButtons.None);   // the AI Marines' input seam: this puppet throws straight at the breach
        for (; k < 45; k++)
        {
            TfwRig.KillAll(w);
            w.Step(_ => default);
            if (w.Projectiles.Any(p => p.Owner == puppet)) seen = true;
            near.AddRange(w.LastDetonations.Where(d => d.Owner == puppet).Select(d => d.Position));
        }
        Assert.True(seen, "the puppet threw");
        Assert.All(near, p => Assert.True(Vector3.Distance(p, breach.Centre) > WarfrontBreachRules.CloseRadius, $"went off at {p}"));
        Assert.True(breach.Standing);
        Assert.False(new WarfrontBreachFilter().Classify(puppet, 0, true, w.Config).Counts);
    }

    [Fact]
    public void A_closer_who_left_with_a_grenade_in_flight_closes_nothing()
    {
        var (w, battle) = BreachRig.Lane(BreachRig.Chaos());
        const int other = 2;
        w.Spawn(other, w.AssignClass(0));   // a second human keeps the battle
        var breach = battle.ChaosBreaches!.All[0];
        w.Musou!.Frozen = true;
        Assert.True(w.TfwRespawnAt(BreachRig.Human, breach.Position + new Vector3(0f, -20f, 0f), 0f));
        BreachRig.Tick(w);
        for (int k = 0; k < 45 && !w.Projectiles.Any(p => p.Owner == BreachRig.Human); k++) { TfwRig.KillAll(w); BreachRig.Tick(w, k is >= 1 and < 3 ? InputButtons.Grenade : InputButtons.None); }
        Assert.Contains(w.Projectiles, p => p.Owner == BreachRig.Human);   // in flight, about 18 m short of the sphere at 60 m/s
        w.Remove(BreachRig.Human);
        for (int k = 0; k < 30; k++) { TfwRig.KillAll(w); w.Step(_ => default); }
        Assert.True(breach.Standing);
        Assert.Empty(battle.ChaosBreaches.Closures);
    }

    [Fact]
    public void Two_qualifying_detonations_in_one_tick_close_one_breach_once()
    {
        var (w, battle) = BreachRig.Lane(BreachRig.Chaos());
        const int other = 2;
        w.Spawn(other, w.AssignClass(0));
        var breach = battle.ChaosBreaches!.All[0];
        w.Musou!.Frozen = true;
        Assert.True(w.TfwRespawnAt(BreachRig.Human, breach.Position + new Vector3(0f, -2.5f, 0f), 0f));
        Assert.True(w.TfwRespawnAt(other, breach.Position + new Vector3(0f, 2.5f, 0f), MathF.PI));
        TfwRig.KillAll(w);
        var ticks = new List<uint>();
        for (int k = 0; k < 30; k++)
        {
            TfwRig.KillAll(w);
            var buttons = k is >= 1 and < 3 ? InputButtons.Grenade : InputButtons.None;
            w.Step(id => new PlayerInput(w.Tick, 0f, 0f, id == other ? MathF.PI : 0f, id is BreachRig.Human or other ? buttons : InputButtons.None, Pitch: -1.2f));
            int mine = w.LastDetonations.Count(d => d.Owner is BreachRig.Human or other);
            if (mine > 0) ticks.Add(w.Tick);
            if (mine == 2) output.WriteLine($"two at tick {w.Tick}");
        }
        Assert.Equal(WarfrontBreachState.Closed, breach.State);
        var closure = Assert.Single(battle.ChaosBreaches.Closures);
        Assert.True(closure.Closer is BreachRig.Human or other);
    }

    [Fact]
    public void A_chaos_lane_battle_with_breaches_runs_the_same_twice()
    {
        ulong Run(out int sent)
        {
            var (w, battle) = BreachRig.Lane(BreachRig.Chaos());
            TfwRig.Seconds(w, 30f);
            sent = battle.ChaosBreaches!.All.Sum(b => b.Spawned);
            return WarfrontChaosOrkPins.Digest(w);
        }
        ulong a = Run(out int sentA), b = Run(out int sentB);
        output.WriteLine($"0x{a:X16}, {sentA} Daemons sent");
        Assert.True(sentA > 0, "the breaches sent Daemons");
        Assert.Equal(sentA, sentB);
        Assert.Equal(a, b);
    }
}
