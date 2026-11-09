using System.Numerics;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The corpse floor (the owner, 2026-11-09: "ork corpses should never immediately disappear, same as guardsmen and allied space marines. they should always leave a corpse for at least 10
/// seconds before disappearing"; spec "Musou Mode: the corpse floor"). The client's pools are Godot nodes, so <see cref="Pool"/> runs their bookkeeping engine-free with the same rules
/// (<see cref="MusouCorpseRules.MakeRoom"/>, the lying and sinking seconds of <c>CrowdView.AddBodyItems</c>): every body an Ork, a Guardsman or an aggregate's loss leaves, near or far, lies whole
/// at least 10 s, a full pool included; the deaths reach the client at any distance and a reused id does not take a body away. The old rules (<c>--musou-old-corpses</c>) are measured beside them.
/// </summary>
public class MusouCorpseFloorTests(ITestOutputHelper output)
{
    /// <summary>The seconds a body sinks after it lies (<c>MusouCrowd.SinkSeconds</c>, a client constant).</summary>
    const float SinkSeconds = 1.5f;

    /// <summary>A crowd's body pool as <c>CrowdView</c> keeps it (oldest first): ages, room, ageing out after lying and sinking; each body's whole time on show is recorded.</summary>
    sealed class Pool(int soft, int hard, bool old)
    {
        readonly List<(float Age, float Metres)> _bodies = new();
        public readonly List<(float Shown, float Metres)> Gone = new();
        public int Most, FloorBroken;
        public int Count => _bodies.Count;

        public void Add(float metres)
        {
            int drop = MusouCorpseRules.MakeRoom(_bodies.Count, i => _bodies[i].Age, soft, hard, old, out bool broken);
            if (drop >= 0) { Gone.Add((_bodies[drop].Age, _bodies[drop].Metres)); _bodies.RemoveAt(drop); }
            if (broken) FloorBroken++;
            _bodies.Add((0f, metres));
            Most = Math.Max(Most, _bodies.Count);
        }

        public void Step(float dt)
        {
            float lie = MusouCorpseRules.LieSeconds(old);
            for (int i = _bodies.Count - 1; i >= 0; i--)
            {
                var b = _bodies[i] with { Age = _bodies[i].Age + dt };
                if (b.Age > lie + SinkSeconds) { Gone.Add((lie, b.Metres)); _bodies.RemoveAt(i); continue; }   // whole on show until it starts to sink
                _bodies[i] = b;
            }
        }
    }

    /// <summary>A grenade's 280 deaths at once, then 30 a second for 40 s (the death-spike test's worst burst and a heavy war's rate), half of them 150 to 300 m away.</summary>
    static Pool Run(int soft, int hard, bool old)
    {
        var pool = new Pool(soft, hard, old);
        var rng = new Random(7);
        const float dt = 1f / 30f;
        for (int k = 0; k < 280; k++) pool.Add(k % 2 == 0 ? 10f : 200f);
        for (int t = 0; t < 40 * 30; t++)
        {
            pool.Step(dt);
            if (t % 3 == 0) for (int k = 0; k < 3; k++) pool.Add(rng.NextDouble() < 0.5 ? 5f + 40f * (float)rng.NextDouble() : 150f + 150f * (float)rng.NextDouble());
        }
        for (int t = 0; t < 15 * 30; t++) pool.Step(dt);
        return pool;
    }

    [Fact]
    public void Every_body_lies_whole_ten_seconds_near_and_far_through_a_burst_and_a_steady_war()
    {
        foreach (var (name, soft, hard) in new[] { ("Orks", 100, MusouCorpseRules.OrkHardBodies), ("Guardsmen", 40, MusouCorpseRules.GuardHardBodies) })
        {
            var now = Run(soft, hard, old: false);
            var before = Run(soft, hard, old: true);
            output.WriteLine($"CORPSES {name}: floor: least on show {now.Gone.Min(g => g.Shown):F2} s, most kept {now.Most}, dropped under the floor {now.FloorBroken}; " +
                             $"before: least {before.Gone.Min(g => g.Shown):F2} s, {before.Gone.Count(g => g.Shown < MusouCorpseRules.FloorSeconds)} of {before.Gone.Count} under 10 s, most kept {before.Most}");
            Assert.Equal(1480, now.Gone.Count);   // every one of the 280 + 1,200 came and went
            Assert.All(now.Gone, g => Assert.True(g.Shown >= MusouCorpseRules.FloorSeconds - 1e-3f, $"{name}: a body at {g.Metres:F0} m went after {g.Shown:F2} s"));
            Assert.Contains(now.Gone, g => g.Metres >= 150f);
            Assert.Equal(0, now.FloorBroken);
            Assert.True(now.Most <= hard);
            Assert.Contains(before.Gone, g => g.Shown < MusouCorpseRules.FloorSeconds);   // the cause: 3 s lying and the oldest out of a full pool
        }
    }

    [Fact]
    public void A_full_pool_of_young_bodies_grows_and_drops_only_bodies_past_the_floor()
    {
        var pool = new Pool(100, 300, old: false);
        for (int k = 0; k < 100; k++) pool.Add(20f);
        pool.Step(5f);
        for (int k = 0; k < 150; k++) pool.Add(250f);   // the pool is full of 5 s old bodies: it grows
        Assert.Equal(250, pool.Count);
        Assert.Empty(pool.Gone);
        pool.Step(5.1f);   // the first 100 are past the floor now (10.1 s), the 150 at 5.1 s
        pool.Add(250f);
        Assert.Single(pool.Gone);
        Assert.True(pool.Gone[0].Shown >= MusouCorpseRules.FloorSeconds);
        for (int k = 0; k < 200; k++) pool.Add(250f);   // the old 99 go first, then it grows to its ceiling of 300 and only there breaks the floor
        Assert.All(pool.Gone.Take(100), g => Assert.True(g.Shown >= MusouCorpseRules.FloorSeconds));
        Assert.Equal(300, pool.Count);
        Assert.True(pool.FloorBroken > 0);
        // Before the floor: the oldest goes once 100 are kept, whatever its age.
        var old = new Pool(100, 300, old: true);
        for (int k = 0; k < 101; k++) old.Add(20f);
        Assert.Equal(0f, old.Gone.Single().Shown);
    }

    [Fact]
    public void A_death_far_off_and_out_of_the_view_keeps_its_body_and_a_gib_only_when_its_burst_is_drawn()
    {
        Assert.True(MusouCorpseRules.HandlesUnseenFar(old: false));
        Assert.False(MusouCorpseRules.HandlesUnseenFar(old: true));
        Assert.True(MusouCorpseRules.LeavesBody(gibbed: false, burstDrawn: false, farUnseen: true, old: false));
        Assert.False(MusouCorpseRules.LeavesBody(gibbed: false, burstDrawn: false, farUnseen: true, old: true));
        Assert.False(MusouCorpseRules.LeavesBody(gibbed: true, burstDrawn: true, farUnseen: false, old: false));   // torn apart where it is seen: its pieces lie instead (gore rules of 2026-11-02 and -08)
        Assert.True(MusouCorpseRules.LeavesBody(gibbed: true, burstDrawn: false, farUnseen: false, old: false));   // too far for the gore: the body stays (before: nothing at all)
        Assert.False(MusouCorpseRules.LeavesBody(gibbed: true, burstDrawn: false, farUnseen: false, old: true));
    }

    [Fact]
    public void A_lying_ragdoll_is_taken_for_a_new_throw_only_past_the_floor_and_a_marine_corpse_is_held_till_then()
    {
        Assert.False(MusouCorpseRules.MayRetake(9.9f, old: false));
        Assert.True(MusouCorpseRules.MayRetake(10f, old: false));
        Assert.True(MusouCorpseRules.MayRetake(1f, old: true));
        Assert.True(MusouCorpseRules.Hold(0.5, old: false));
        Assert.True(MusouCorpseRules.Hold(9.9, old: false));
        Assert.False(MusouCorpseRules.Hold(10.0, old: false));
        Assert.False(MusouCorpseRules.Hold(0.5, old: true));
        Assert.Equal(10f, MusouCorpseRules.LieSeconds(old: false));
        Assert.Equal(3f, MusouCorpseRules.LieSeconds(old: true));
    }

    [Fact]
    public void An_aggregates_losses_leave_a_body_for_every_slot_ork_and_guard_at_any_distance()
    {
        foreach (var side in new[] { TfwArmySide.Ork, TfwArmySide.Guard })
        {
            var at = new Vector3(280f, 60f, 0f);   // 287 m off: past the old 90 m
            var v = new TfwArmyView(3, side, TfwArmyOrder.Hold, true, 120, 120, 0, 12, at, 0.4f, 1f, 13);
            var now = new TfwArmyImpostors { OrkAggregates = true };   // an Ork aggregate only under the old armies (2026-11-09); OrkArmyAggregateTests of none
            now.Apply([v], 10, 0.0);
            now.Apply([v with { Far = 80 }], 25, 0.5);   // a far clash took 40 in one section
            Assert.Equal(40, now.Deaths.Count);
            Assert.All(now.Deaths, d => Assert.Equal(side, d.Side));
            var before = new TfwArmyImpostors { OldCorpses = true, OrkAggregates = true };
            before.Apply([v], 10, 0.0);
            before.Apply([v with { Far = 80 }], 25, 0.5);
            Assert.Equal(TfwImpostorRules.DeathsPerSection, before.Deaths.Count);
        }
        Assert.True(float.IsPositiveInfinity(MusouCorpseRules.ImpostorBodiesWithin(TfwArmyRules.CoreMetres, old: false)));
        Assert.Equal(TfwArmyRules.CoreMetres, MusouCorpseRules.ImpostorBodiesWithin(TfwArmyRules.CoreMetres, old: true));
    }

    static readonly MusouBox WideBox = new(new Vector3(-400f, -400f, -10f), new Vector3(400f, 400f, 30f));

    [Fact]
    public void A_death_300_m_off_reaches_the_client_as_a_body_and_the_reused_slot_does_not_take_it()
    {
        // The server: a full pool, one Ork killed 300 m from the player; its slot is not taken while it dies (Spawn finds none free), and a freed slot goes to the back of the queue.
        var sim = new MusouSim(null);
        ushort far = sim.Spawn(MusouEnemyType.Boy, new Vector3(300f, 0f, 0f), 0f, 10f);
        for (int k = 1; k < MusouSim.Capacity; k++) Assert.NotEqual((ushort)0, sim.Spawn(MusouEnemyType.Boy, new Vector3(-300f + k % 50, k / 50, 0f), 0f, 10f));
        Assert.True(sim.Damage(far, 1, 100f, Vector3.UnitX, out bool killed) && killed);
        var attacks = new List<MusouAttack>();
        var death = Assert.Single(sim.Events, e => e.Kind == MusouEventKind.Death && e.Enemy == far);
        Assert.Equal((ushort)0, sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, 10f));   // dying: its slot is not free yet
        MusouTarget[] none = [];
        for (int t = 0; t < 120 && sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, 10f) is var id && id == 0; t++) sim.Step(none, null, false, attacks);
        Assert.Equal(MusouSim.Capacity, sim.Count);   // freed after its dying seconds and taken again
        // The client: the far Ork's record, his death, then at once the new Ork on the reused id: the death leaves its body where he fell, and the newcomer is another enemy.
        var s = new MusouClientState();
        s.Apply(new ExtrasSection(1, MusouWire.DirectorTag, MusouWire.WriteDirector(new MusouDirectorView(1, 5, MusouPhase.Wave, 0, 80, 80, 0, 0f, 0f, 3, WideBox))), 0.0);
        s.Apply(new ExtrasSection(death.Tick, MusouWire.EntitiesTag, MusouWire.WriteEntities([new MusouEntity(far, new Vector3(300f, 0f, 0f), 0f, MusouEnemyType.Boy, MusouEnemyState.Seek)], WideBox)), 0.0);
        s.Apply(new ExtrasSection(death.Tick, MusouWire.EventsTag, MusouWire.WriteEvents([death], WideBox)), 0.05);
        s.Apply(new ExtrasSection(death.Tick + 61, MusouWire.EntitiesTag, MusouWire.WriteEntities([new MusouEntity(far, new Vector3(0f, 0f, 0f), 0f, MusouEnemyType.Boy, MusouEnemyState.Spawning)], WideBox)), 2.1);
        var body = Assert.Single(s.NewDeaths);
        Assert.Equal(300f, body.Position.X, 0.1f);
        Assert.Equal(1, s.Count);   // the newcomer is drawn as a living Ork beside the body
    }

    [Fact]
    public void A_guardsmans_death_far_off_reaches_the_client()
    {
        // The Guard crowd lays his body from his last drawn place (GuardCrowd.Fall); the client hears his death 293 m off as near.
        var s = new TfwClientState();
        var at = new Vector3(290f, -40f, 0f);
        s.Apply(new ExtrasSection(10, TfwWire.GuardsTag, TfwWire.WriteGuards([new TfwGuard(7, at, 0f, 0)], WideBox)), WideBox, 0.0);
        Assert.Equal(at.X, Assert.Single(s.Sample(10, 0.0)).Position.X, 0.1f);
        s.Apply(new ExtrasSection(12, TfwWire.GuardEventsTag, TfwWire.WriteGuardEvents([new TfwGuardEvent(TfwGuardEventKind.Death, 12, 7, 0, at)], WideBox)), WideBox, 0.07);
        Assert.Single(s.NewEvents, e => e.Kind == TfwGuardEventKind.Death && e.Guard == 7);
        Assert.Empty(s.Sample(12, 0.07));   // the man is gone from the living: his body is drawn instead
    }

    /// <summary>
    /// The cost, bounded (one thread, 45 simulated seconds): a 200 v 200 clash beside a human (and 1,000 v 1,000 with <c>SM_ARMY_PROFILE=1</c>), its Ork and Guard deaths a tick fed to the two
    /// pools: the bodies kept at most and whether any went under the floor. The server's tick and send budget do not change (the floor is the client's alone: no simulation or wire change).
    /// </summary>
    [Theory]
    [InlineData(200)]
    [InlineData(1000)]
    public void A_clash_fills_the_pools_within_their_ceilings(int size)
    {
        if (size > 200 && Environment.GetEnvironmentVariable("SM_ARMY_PROFILE") != "1") return;
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        TfwRig.Secure(w, battle, 0);
        var point = battle.Layout.Positions[0];
        var stand = point + new Vector3(0f, -45f, 0f);
        TfwRig.Teleport(w, stand);
        battle.AddArmyForTests(TfwArmySide.Guard, TfwArmyOrder.Hold, 0, size, point + new Vector3(0f, 8f, 0f), MathF.PI);
        battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Attack, 0, size, point + new Vector3(0f, 20f, 0f), MathF.PI, eliteShare: 0.1f);
        var orks = new Pool(100, MusouCorpseRules.OrkHardBodies, old: false);
        var guards = new Pool(40, MusouCorpseRules.GuardHardBodies, old: false);
        int orkDeaths = battle.OrksKilled, guardsLost = w.TfwGuards!.Lost + battle.GuardsLostFar, orkTotal = 0, guardTotal = 0, mostInTick = 0;
        for (int t = 0; t < 45 * 30; t++)
        {
            if (!w.Players[TfwRig.Human].Alive) w.TfwRespawnAt(TfwRig.Human, stand, 0f);
            w.Step(_ => default);
            int o = battle.OrksKilled - orkDeaths, g = w.TfwGuards.Lost + battle.GuardsLostFar - guardsLost;
            orkDeaths = battle.OrksKilled; guardsLost = w.TfwGuards.Lost + battle.GuardsLostFar;
            orks.Step(1f / 30f); guards.Step(1f / 30f);
            for (int k = 0; k < o; k++) orks.Add(45f);
            for (int k = 0; k < g; k++) guards.Add(45f);
            orkTotal += o; guardTotal += g; mostInTick = Math.Max(mostInTick, o + g);
        }
        output.WriteLine($"CORPSECOST {size} v {size}: deaths Orks {orkTotal}, Guardsmen {guardTotal} in 45 s (most {mostInTick} a tick); bodies kept at most: Orks {orks.Most} (ceiling {MusouCorpseRules.OrkHardBodies}), " +
                         $"Guardsmen {guards.Most} (ceiling {MusouCorpseRules.GuardHardBodies}); under the floor {orks.FloorBroken + guards.FloorBroken}");
        Assert.True(orkTotal + guardTotal > 0, "the clash killed no one");
        Assert.Equal(0, orks.FloorBroken + guards.FloorBroken);
        Assert.All(orks.Gone.Concat(guards.Gone), g => Assert.True(g.Shown >= MusouCorpseRules.FloorSeconds - 1e-3f));
    }
}
