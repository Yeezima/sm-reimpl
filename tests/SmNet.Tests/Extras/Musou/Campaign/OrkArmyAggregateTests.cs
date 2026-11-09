using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The owner, 2026-11-09: "there are currently on occasion large rectangular formations of orks that move from their points to imperial captured points. they walk through objects, don't interact with the
/// world or fight. completely remove those orks from musou mode ... reduce the total amount of orks on the map to 80% of current." Those formations were the Ork armies' aggregates (men counted, drawn as
/// impostors in formation slots). Here: no Ork army keeps a man that is not a real Ork, at any distance, under the cap or past it; no Ork aggregate is drawn; the far clash and the point count only real Orks;
/// the old switch restores them; and the Orks' totals are 80 % of what they were.
/// </summary>
public class OrkArmyAggregateTests
{
    const int Tick = 30;

    static (MusouSim Sim, GuardSim Guards, TfwBattle Battle, TfwLayout Layout) Frozen(bool old = false)
    {
        var sim = new MusouSim(null) { Frozen = true };
        var guards = new GuardSim(sim);
        var layout = TfwTestArena.Layout();
        var battle = new TfwBattle(sim, guards, layout, 100.0, 1, false, default, TfwBattleRules.EliteBase, guardsmen: true)
        { Trickle = false, Patrols = false, BossOrksOn = false, StormboysOn = false, BigShootasOn = false, WeirdboyzOn = false, RokkitsOn = false, OrkAggregates = old };
        battle.Start();
        return (sim, guards, battle, layout);
    }

    static int OrkAggregate(TfwBattle battle) => battle.ArmyList.Where(a => a.Side == TfwArmySide.Ork).Sum(a => a.FarCount);

    [Fact]
    public void A_wave_far_from_every_human_is_real_orks_alone_up_to_the_cap_and_the_rest_is_never_made()
    {
        var (sim, _, battle, _) = Frozen();
        var wave = battle.SendWaveForTests(TfwArmyRules.LargeWave)!;   // no human anywhere: before 2026-11-09 all of it stayed an aggregate
        battle.Step([]);
        int queuedCapped = Enumerable.Range(wave.SlotBase, wave.FarCount).Count(s => !TfwBattleRules.Big(TfwArmyLayout.OrkType(s, wave.FarEliteShare)));   // the big Orks need no room (2026-11-10)
        Assert.True(queuedCapped <= TfwArmyRules.NearOrksMost - (wave.Real - battle.ArmyBigAlive), "past the room: never queued");   // only spawns the next ticks make wait   // only spawns the next ticks make wait
        int aggregateMost = 0;
        for (int t = 0; t < 3 * Tick; t++) { battle.Step([]); if (t >= TfwArmyRules.LargeWave / TfwArmyRules.PromotePerTick + 1) aggregateMost = Math.Max(aggregateMost, OrkAggregate(battle)); }
        Assert.Equal(0, OrkAggregate(battle));
        Assert.Equal(0, aggregateMost);                                            // once made, never a count again
        int bigs = battle.ArmyBigAlive;                                            // the Nobs and their twins, off the real cap (2026-11-10)
        Assert.Equal(TfwArmyRules.NearOrksMost, wave.Real - bigs);                 // the cap's worth made real
        Assert.Equal(TfwArmyRules.LargeWave - (wave.Real - wave.Twins), battle.OrksNeverMade);
        Assert.Equal(wave.Real, battle.ArmyOrksAlive);                             // every one of them a unit of the pool
        Assert.All(wave.Orks, id => Assert.True(sim.TryGet(id, out var e) && e.Alive));
        for (int t = 0; t < 20 * Tick; t++) battle.Step([]);                       // still nobody near: nothing folds back
        Assert.Equal((TfwArmyRules.NearOrksMost + bigs, 0, 0), (wave.Real, wave.FarCount, battle.Demoted));
    }

    [Fact]
    public void The_muster_brings_real_orks_at_the_stronghold_and_never_a_count()
    {
        var (_, _, battle, _) = Frozen();
        for (int t = 0; t < 200 * Tick && battle.Mustering is null; t++) battle.Step([]);
        var m = Assert.IsType<TfwArmy>(battle.Mustering);
        int most = 0;
        for (int t = 0; t < 10 * Tick && battle.Mustering is not null; t++) { battle.Step([]); most = Math.Max(most, m.FarCount); }
        Assert.True(m.Real > 0, "the muster made no Ork");
        Assert.True(most <= 1, $"the muster held {most} men as a count");   // at most the one man still being added this tick
    }

    [Fact]
    public void Orks_at_a_point_fight_the_far_clash_as_real_units_and_every_loss_is_a_real_death()
    {
        var (sim, _, battle, layout) = Frozen();
        Vector3[] at = [layout.Positions[1]];
        for (int t = 0; t < 30 * Tick && battle.Objectives[1].State != TfwPositionState.Secured; t++)
        {
            var ids = new ushort[MusouSim.Capacity];
            int n = sim.Query(layout.Positions[1], layout.Radius(1) + 2f, ids);
            for (int i = 0; i < n; i++) sim.Remove(ids[i]);
            battle.Step(at);
        }
        Assert.Equal(TfwPositionState.Secured, battle.Objectives[1].State);
        var garrison = battle.AddArmyForTests(TfwArmySide.Guard, TfwArmyOrder.Hold, 1, 40, layout.Positions[1])!;
        var wave = battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Attack, 1, 200, layout.Positions[1])!;
        int aggregateMost = 0;
        for (int t = 0; t <= 200 / TfwArmyRules.PromotePerTick + 1; t++) battle.Step([]);
        var made = wave.Orks.ToList();
        Assert.Equal(200, made.Count);
        for (int t = 0; t < 60 * Tick; t++) { battle.Step([]); aggregateMost = Math.Max(aggregateMost, wave.FarCount); }
        Assert.Equal(0, aggregateMost);
        Assert.True(wave.Lost > 0, "the real wave lost nobody in the clash");
        Assert.Equal(wave.Lost, made.Count(id => !sim.TryGet(id, out var e) || !e.Alive));   // every Ork the army lost is one of its real Orks dead
        Assert.True(garrison.Lost > 0, "the real Orks did not fight the garrison");
    }

    [Fact]
    public void The_old_switch_keeps_the_aggregate_far_from_every_human()
    {
        var (_, _, battle, _) = Frozen(old: true);
        var wave = battle.SendWaveForTests(TfwArmyRules.LargeWave)!;
        for (int t = 0; t < 3 * Tick; t++) battle.Step([]);
        Assert.Equal((0, TfwArmyRules.LargeWave), (wave.Real, wave.FarCount));
    }

    [Fact]
    public void A_waves_orks_are_real_crowd_orks_that_walk_the_world()
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        Assert.False(battle.OrkAggregates);
        var wave = battle.SendWaveForTests(TfwArmyRules.SmallWave)!;
        TfwRig.Seconds(w, 1f);
        var start = wave.Orks.ToDictionary(id => id, id => w.Musou!.TryGet(id, out var e) ? e.Position : Vector3.Zero);
        TfwRig.Seconds(w, 8f);
        Assert.Equal(0, wave.FarCount);
        Assert.True(wave.Real >= TfwArmyRules.SmallWave * 9 / 10, $"real {wave.Real} of {TfwArmyRules.SmallWave}");
        float moved = 0f;
        int n = 0;
        foreach (ushort id in wave.Orks)
            if (start.TryGetValue(id, out var from) && w.Musou!.TryGet(id, out var e) && e.Alive) { moved += Vector2.Distance(new(e.Position.X, e.Position.Y), new(from.X, from.Y)); n++; }
        Assert.True(n > 0 && moved / n > 5f, $"the wave's Orks moved {moved / Math.Max(1, n):F1} m on average");
    }

    [Fact]
    public void The_client_draws_no_ork_aggregate_and_no_body_for_one_and_still_draws_the_guards()
    {
        var at = new Vector3(60f, 0f, 0f);
        var ork = new TfwArmyView(1, TfwArmySide.Ork, TfwArmyOrder.March, true, 300, 300, 0, 30, at, 0f, 1f, 20);
        var guard = new TfwArmyView(2, TfwArmySide.Guard, TfwArmyOrder.Hold, true, 50, 50, 0, 0, at + new Vector3(0f, 40f, 0f), 0f, 1f, 10);
        var impostors = new TfwArmyImpostors();
        impostors.Apply([ork, guard], 10, 0.0);
        impostors.Apply([ork with { Far = 200 }, guard], 25, 0.5);
        var list = new List<TfwImpostor>();
        impostors.Write(Vector3.Zero, list);
        Assert.DoesNotContain(list, i => i.Side == TfwArmySide.Ork);
        Assert.Equal(50, list.Count(i => i.Side == TfwArmySide.Guard));
        Assert.Empty(impostors.Deaths);
        var old = new TfwArmyImpostors { OrkAggregates = true };
        old.Apply([ork], 10, 0.0);
        old.Write(Vector3.Zero, list);
        Assert.Equal(300, list.Count(i => i.Side == TfwArmySide.Ork));
    }

    /// <summary>
    /// Every bulk count of Orks back at its value before 2026-11-09's 80 % (the owner, 2026-11-10: "roll back the 20% less orks change"; the real Orks alone kept), and twice the boss and elite spawns
    /// ("double boss and elite enemy spawns": the big Orks twice and off the caps, up to 8 Boss Orks): the Weirdboyz as they were.
    /// </summary>
    [Fact]
    public void The_orks_totals_are_back_at_100_percent_with_twice_the_boss_and_elite_spawns()
    {
        Assert.Equal(1000, TfwArmyRules.LargeWave);
        Assert.Equal(500, TfwArmyRules.MediumWave);
        Assert.Equal(200, TfwArmyRules.SmallWave);
        Assert.Equal(200, TfwArmyRules.LargeExtraMost);
        Assert.Equal(480, TfwArmyRules.NearOrksMost);
        Assert.Equal(96, TfwBattleRules.Garrison);
        Assert.Equal(400, TfwBattleRules.CrowdSize);
        Assert.Equal(4f / 30f, TfwBattleRules.CrowdFillSeconds);
        Assert.Equal(1200, TfwBattleRules.AliveCapSolo);
        Assert.Equal(640, TfwBattleRules.AliveCapPerExtraHuman);
        Assert.Equal(1680, TfwBattleRules.AliveCapMost);
        Assert.Equal(1600, TfwBattleRules.TrickleTotalMax);
        Assert.Equal(40, TfwPatrolRules.Size);
        Assert.Equal(20, TfwBossOrkRules.Retinue);
        Assert.Equal(10, TfwBigShootaRules.Squad);
        Assert.Equal(500, VictoryWaveRules.Size);
        Assert.Equal(2, TfwWeirdboyRules.MaxAlive);
        Assert.Equal(2, TfwBattleRules.BigTwins);
        Assert.Equal(8, TfwBossOrkRules.MostAlive);
        Assert.Equal(0.5f, TfwBossOrkRules.Interval);
        Assert.Equal(0.10f, TfwBattleRules.EliteShare(0f), 4);   // the elite share as before: the big Orks come twice beside it
        Assert.Equal(0.60f, TfwBattleRules.EliteShare(1f), 4);
        Assert.True(TfwBattleRules.Big(MusouEnemyType.Nob) && TfwBattleRules.Big(MusouEnemyType.BigShootaNob) && TfwBattleRules.Big(MusouEnemyType.BossNob) && TfwBattleRules.Big(MusouEnemyType.BossShootaBoy));
        Assert.False(TfwBattleRules.Big(MusouEnemyType.ArdBoy) || TfwBattleRules.Big(MusouEnemyType.Warboss) || TfwBattleRules.Big(MusouEnemyType.Weirdboy) || TfwBattleRules.Big(MusouEnemyType.Boy));
    }
}
