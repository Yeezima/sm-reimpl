using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Chaos;

namespace SmNet.Tests.Extras.Warfront.Chaos.Breach;

/// <summary>Chaos Warfront C4: a breach opens 1 s after the landing, sends 5 Daemons 5 s later and every 20 s, at most 10 alive a breach and 30 over all, refilled as they die, within the battle's alive cap and the pool.</summary>
public class BreachSpawnTests
{
    const int Rate = BreachRig.Rate;
    static int T(float s) => WarfrontBreachRules.Ticks(s, Rate);

    static WarfrontBreaches One() => WarfrontBreaches.At(Rate, new Vector3(50f, 50f, 0f));

    static void Run(WarfrontBreaches b, BreachRig.Field f, int from, int to) { for (int t = from; t <= to; t++) b.Step(t, f); }

    [Fact]
    public void FirstSquadAtOpenPlusBuildUp()
    {
        var b = One();
        var f = new BreachRig.Field();
        Run(b, f, 0, T(1f) - 1);
        Assert.Equal(WarfrontBreachState.Opening, b.All[0].State);
        b.Step(T(1f), f);
        Assert.Equal(WarfrontBreachState.Open, b.All[0].State);
        Run(b, f, T(1f) + 1, T(6f) - 1);
        Assert.Empty(f.Spawns);
        b.Step(T(6f), f);
        Assert.Equal(WarfrontBreachRules.SquadSize, f.Spawns.Count);
        Assert.All(f.Spawns, s => Assert.Equal(WarfrontBreachRules.RingRadius, new Vector2(s.Spot.X - 50f, s.Spot.Y - 50f).Length(), 3));
    }

    [Fact]
    public void SquadEvery20s()
    {
        var b = One();
        var f = new BreachRig.Field();
        Run(b, f, 0, T(6f) + T(20f) - 1);
        Assert.Equal(5, f.Spawns.Count);
        b.Step(T(6f) + T(20f), f);
        Assert.Equal(10, f.Spawns.Count);
    }

    [Fact]
    public void CapTenPerBreach()
    {
        var b = One();
        var f = new BreachRig.Field();
        Run(b, f, 0, T(6f) + 4 * T(20f));
        Assert.Equal(WarfrontBreachRules.BreachAliveCap, f.Spawns.Count);
        Assert.Equal(10, b.All[0].Alive.Count);
        Assert.Equal(10, b.AliveDaemons);
    }

    [Fact]
    public void CapThirtyOverall()
    {
        var b = WarfrontBreaches.At(Rate, Enumerable.Range(0, 5).Select(k => new Vector3(k * 40f, 0f, 0f)).ToArray());
        var f = new BreachRig.Field();
        Run(b, f, 0, T(6f) + 6 * T(20f));
        Assert.Equal(WarfrontBreachRules.AllAliveCap, f.Spawns.Count);
        Assert.Equal(30, b.AliveDaemons);
        Assert.All(b.All, x => Assert.True(x.Alive.Count <= 10));
    }

    [Fact]
    public void RefillWhenSlotsFree()
    {
        var b = One();
        var f = new BreachRig.Field();
        int t = T(6f) + 2 * T(20f);
        Run(b, f, 0, t);
        Assert.Equal(10, b.All[0].Alive.Count);
        Assert.Equal(10, f.Spawns.Count);
        Assert.True(b.All[0].NextSquadAt <= t, "the third squad is due and waits for room");
        foreach (ushort id in b.All[0].Alive.Take(3).ToList()) f.Kill(id);
        b.Step(t + 1, f);
        Assert.Equal(10, b.All[0].Alive.Count);   // the slots that freed are filled at once by the waiting squad
        Assert.Equal(13, f.Spawns.Count);
        Assert.Equal(t + 1 + T(20f), b.All[0].NextSquadAt);   // and the cadence starts again from it
        // the battle's own room limits it too
        foreach (ushort id in b.All[0].Alive.ToList()) f.Kill(id);
        f.RoomNow = 2;
        Run(b, f, t + 2, b.All[0].NextSquadAt);
        Assert.Equal(2, b.All[0].Alive.Count);
    }

    [Fact]
    public void PoolFullSpawnsNothingNoThrow()
    {
        var b = One();
        var f = new BreachRig.Field { Full = true };
        Run(b, f, 0, T(60f));
        Assert.Empty(b.All[0].Alive);
        f.Full = false;
        f.RoomNow = 0;
        Run(b, f, T(60f) + 1, T(90f));
        Assert.Empty(f.Spawns);
        // the real pool, full: the battle's field gives no Daemon and nothing throws
        var (w, battle) = BreachRig.Lane(BreachRig.Chaos());
        var musou = w.Musou!;
        while (musou.Spawn(MusouEnemyType.Boy, new Vector3(0f, 0f, 0f), 0f, 100f) != 0) { }
        Assert.Equal(0, battle.BreachSpawns.Spawn(new Vector3(10f, 10f, 0f), new Vector3(10f, 10f, 0f)));
        Assert.Equal(0, battle.BreachRoom);
        TfwRig.Seconds(w, 8f);
        Assert.Empty(battle.ChaosBreaches!.All[0].Alive);
    }

    [Fact]
    public void DaemonsCountAgainstAliveCap()
    {
        var (w, battle) = BreachRig.Lane(BreachRig.Chaos());
        int counted = battle.AliveCounted, room = battle.BreachRoom;
        var field = battle.BreachSpawns;
        var at = battle.ChaosBreaches!.All[0].Position;
        var ids = Enumerable.Range(0, 4).Select(_ => field.Spawn(at, at)).ToList();
        Assert.All(ids, id => Assert.NotEqual(0, id));
        Assert.Equal(counted + 4, battle.AliveCounted);   // Counted() already takes a breach Daemon: no hook in TfwBattle.cs
        Assert.Equal(room - 4, battle.BreachRoom);
        Assert.All(ids, id => { Assert.True(w.Musou!.TryGet(id, out var e)); Assert.Equal(MusouEnemyType.Gretchin, e.Type); Assert.True(field.IsDaemon(id)); });
    }

    [Fact]
    public void DaemonsHuntHumans()
    {
        var (w, battle) = BreachRig.Lane(BreachRig.Chaos());
        var breach = battle.ChaosBreaches!.All[0];
        var human = breach.Position + new Vector3(0f, -18f, 0f);
        Assert.True(w.TfwRespawnAt(BreachRig.Human, human, 0f));
        TfwRig.Seconds(w, 6.5f);   // open, built up, the first squad out
        Assert.True(breach.Alive.Count >= 1, "the first squad came");
        Assert.True(breach.Alive.Count <= WarfrontBreachRules.SquadSize);
        var musou = w.Musou!;
        float Nearest() => breach.Alive.Select(id => musou.TryGet(id, out var e) ? Vector3.Distance(e.Position, w.Players[BreachRig.Human].Position) : float.MaxValue).Min();
        Assert.All(breach.Alive, id => Assert.False(musou.Leashed(id)));
        float before = Nearest();
        Assert.True(w.TfwRespawnAt(BreachRig.Human, human, 0f));
        TfwRig.Seconds(w, 2f);
        Assert.True(Nearest() < before - 2f, $"they close in: {before:0.0} m, then {Nearest():0.0} m");
        // their health is the Chaos row's Daemon (200 hp; one human, level 1)
        Assert.All(breach.Alive, id => { musou.TryGet(id, out var e); Assert.True(e.Health <= musou.Rules.Of(MusouEnemyType.Gretchin).Health + 1e-3f); });
    }

    [Fact]
    public void A_daemon_is_shot_over_its_whole_height_and_falls_like_the_chaos_fodder()
    {
        var chaos = WarfrontChaosRosterTests.ChaosConfig.MusouRules!;
        Assert.Equal(chaos.Of(MusouEnemyType.Gretchin).Height, MusouSim.ShotBodyOf(chaos, MusouEnemyType.Gretchin).Height);   // 2.2 m, the row's
        Assert.Equal(1.5f, MusouSim.ShotBodyOf(MusouRules.Default, MusouEnemyType.Gretchin).Height);   // the Orks' Gretchin as before
        Assert.True(MusouSim.Fodder(MusouEnemyType.Gretchin), "decided (C4): a breach Daemon falls to melee as the Cultists do");
        Assert.False(WarfrontChaosRoster.BlastFodder(MusouEnemyType.Gretchin), "the autocannon's one-blast rule is the Cultists' and Traitors' only");
    }
}
