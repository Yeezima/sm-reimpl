using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Expansion S6, the battle flow (docs/musou-expansion.md "S6 design"): committed wave destinations, the garrison cap and its overflow, redirects, retreats and the leader rule, rallies without loops, the near/far
/// levels (no count or place jumps, every co-op human counted), the Defenders and the puppet ids, and tag 18.
/// </summary>
public class TfwArmyTests
{
    const int Tick = 30;

    static (MusouSim Sim, GuardSim Guards, TfwBattle Battle, TfwLayout Layout) Frozen()
    {
        var sim = new MusouSim(null) { Frozen = true };
        var guards = new GuardSim(sim);
        var layout = TfwTestArena.Layout();
        var battle = new TfwBattle(sim, guards, layout, 100.0, 1, false, default, TfwBattleRules.EliteBase, guardsmen: true)
        { Trickle = false, Patrols = false, BossOrksOn = false, StormboysOn = false, BigShootasOn = false, WeirdboyzOn = false, RokkitsOn = false };
        battle.Start();
        return (sim, guards, battle, layout);
    }

    static void ClearPoint(MusouSim sim, TfwLayout layout, int k)
    {
        var ids = new ushort[MusouSim.Capacity];
        int n = sim.Query(layout.Positions[k], layout.Radius(k) + 2f, ids);
        for (int i = 0; i < n; i++) sim.Remove(ids[i]);
    }

    static void Capture(MusouSim sim, TfwBattle battle, TfwLayout layout, int k)
    {
        Vector3[] at = [layout.Positions[k]];
        for (int t = 0; t < 30 * Tick && battle.Objectives[k].State != TfwPositionState.Secured; t++) { ClearPoint(sim, layout, k); battle.Step(at); }
        Assert.Equal(TfwPositionState.Secured, battle.Objectives[k].State);
    }

    static void Seconds(TfwBattle battle, GuardSim guards, float seconds, Func<Vector3[]> feet)
    {
        for (int t = 0; t < (int)MathF.Round(seconds * Tick); t++) { battle.Step(feet()); guards.BeginTick((uint)t); guards.Step(false); }
    }

    // ---------------------------------------------------------------- waves

    [Fact]
    public void A_wave_commits_to_the_imperial_point_farthest_from_the_players_and_keeps_it_when_they_move()
    {
        var (sim, guards, battle, layout) = Frozen();
        for (int k = 0; k < 3; k++) Capture(sim, battle, layout, k);
        Seconds(battle, guards, 0.2f, () => [layout.Positions[0]]);
        var wave = Assert.IsType<TfwArmy>(battle.SendWaveForTests(200));
        Assert.Equal(TfwArmyOrder.March, wave.Order);
        Assert.Contains(wave.Goal, new[] { 1, 2 });                      // the two farthest from the human on point 0
        int goal = wave.Goal;
        Assert.Equal(200, wave.Count);
        Seconds(battle, guards, 20f, () => [layout.Positions[goal]]);   // the human runs to its destination
        Assert.Equal(goal, wave.Goal);
        Seconds(battle, guards, 20f, () => [layout.Positions[3 - goal]]);   // and away again
        Assert.Equal(goal, wave.Goal);
    }

    [Fact]
    public void With_a_point_held_the_base_is_no_wave_target()
    {
        // The owner's pick at the musou/perf-pass integration (2026-11-08, "no point held"): the base is a target only while the players hold no position (S6's "only once all three are Imperial" dropped).
        var (sim, guards, battle, layout) = Frozen();
        Capture(sim, battle, layout, 0);
        Vector3[] atA = [layout.Positions[0]];
        battle.Step(atA);
        for (int k = 0; k < 8; k++) Assert.NotEqual(TfwBattle.LandingGoal, battle.SendWaveForTests(200)!.Goal);
    }

    [Fact]
    public void A_wave_whose_real_ranks_lag_creeps_on_and_still_reaches_its_point()
    {
        var (sim, guards, battle, layout) = Frozen();   // frozen: its real Orks never walk, so its ranks always lag
        for (int k = 0; k < 3; k++) Capture(sim, battle, layout, k);
        Vector3[] atHq = [TfwTestArena.Sources[3]];
        battle.Step(atHq);
        var wave = battle.SendWaveForTests(100, 2)!;
        for (int t = 0; t < 240 * Tick && wave.Order == TfwArmyOrder.March; t++) battle.Step(atHq);
        Assert.True(wave.Real > 0);
        Assert.Equal(TfwArmyOrder.Attack, wave.Order);
    }

    [Fact]
    public void A_near_armys_remainder_wears_the_real_front_down_and_the_real_men_only_take_its_fire()
    {
        var (sim, guards, battle, layout) = Frozen();
        Capture(sim, battle, layout, 1);
        var point = layout.Positions[1];
        var garrison = battle.AddArmyForTests(TfwArmySide.Guard, TfwArmyOrder.Hold, 1, 100, point)!;
        var wave = battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Attack, 1, 900, point)!;
        Vector3[] near = [point + new Vector3(0f, -40f, 0f)];
        for (int t = 0; t < 3 * Tick; t++) battle.Step(near);
        Assert.Equal(0, garrison.FarCount);                                 // all of it real
        Assert.Equal(TfwArmyRules.NearOrksMost, wave.Real);                 // the wave at the cap, the rest behind the line
        int remainder = wave.FarCount;
        Assert.True(remainder > 0);
        int before = garrison.Count;
        for (int t = 0; t < 30 * Tick; t++) battle.Step(near);
        Assert.True(garrison.Count < before, "the remainder's fire killed real men");
        Assert.Equal(remainder, wave.FarCount);                             // the real men's own fire is real only: the remainder lost nobody here
    }

    [Fact]
    public void Wave_sizes_follow_the_brief_shares()
    {
        int small = 0, medium = 0, large = 0;
        for (int k = 0; k < 10000; k++)
        {
            int size = TfwArmyRules.WaveSize((k + 0.5f) / 10000f, (k * 0.618f) % 1f);
            if (size == 200) small++; else if (size == 500) medium++; else { Assert.InRange(size, 1000, 1200); large++; }
        }
        Assert.Equal((7000, 2500, 500), (small, medium, large));
    }

    [Fact]
    public void With_no_imperial_point_the_muster_waits_and_leaves_once_one_is_taken()
    {
        var (sim, guards, battle, layout) = Frozen();
        battle.BaseTargets = false;   // expansion S6's rule; the owner's "base is a target" (2026-11-08) is the next test
        Seconds(battle, guards, 200f, () => [layout.LandingZone]);
        Assert.Equal(0, battle.WavesSent);                               // nothing Imperial to attack: the muster waits
        Assert.NotNull(battle.Mustering);
        Capture(sim, battle, layout, 0);
        Seconds(battle, guards, TfwArmyRules.WaitRecheck + 0.5f, () => [layout.LandingZone]);
        Assert.Equal(1, battle.WavesSent);
    }

    [Fact]
    public void With_no_imperial_point_the_wave_marches_on_the_base()
    {
        var (_, guards, battle, layout) = Frozen();
        TfwArmy? left = null;
        battle.OnWaveLeft = a => left ??= a;
        Seconds(battle, guards, 200f, () => [layout.LandingZone]);
        Assert.Equal(1, battle.WavesSent);                               // the owner, 2026-11-08: "base is a target"
        Assert.Equal(TfwBattle.LandingGoal, left!.Goal);
    }

    [Fact]
    public void Peaks_come_three_to_five_minutes_apart()
    {
        var (sim, guards, battle, layout) = Frozen();
        for (int k = 0; k < 3; k++) Capture(sim, battle, layout, k);
        Seconds(battle, guards, 800f, () => [layout.LandingZone]);
        Assert.True(battle.WavesSent >= 3, $"{battle.WavesSent} waves");
        for (int k = 1; k < battle.Departures.Count; k++)
            Assert.True((battle.Departures[k] - battle.Departures[k - 1]) / (float)Tick >= TfwArmyRules.PeakMin - 0.1f);
        Assert.InRange((battle.Departures[1] - battle.Departures[0]) / (float)Tick, TfwArmyRules.PeakMin - 0.1f, TfwArmyRules.PeakMax + 0.1f);
    }

    [Fact]
    public void The_first_wave_leaves_after_a_lull_of_90_to_150_s()
    {
        var (sim, guards, battle, layout) = Frozen();
        Capture(sim, battle, layout, 0);
        Seconds(battle, guards, 160f, () => [layout.LandingZone]);
        int first = Assert.Single(battle.Departures.Take(1));
        Assert.InRange(first / (float)Tick, TfwArmyRules.LullMin - 0.1f, TfwArmyRules.LullMax + 0.1f);
    }

    // ---------------------------------------------------------------- garrisons

    [Fact]
    public void A_garrison_never_passes_200_and_the_overflow_moves_on()
    {
        var (sim, guards, battle, layout) = Frozen();
        Capture(sim, battle, layout, 0);
        battle.ReinforceForTests(0, 150);
        battle.ReinforceForTests(0, 150);
        int most = 0;
        bool overflowSeen = false;
        for (int t = 0; t < 60 * Tick; t++)
        {
            battle.Step([]);
            most = Math.Max(most, battle.Stationed(0));
            overflowSeen |= battle.ArmyList.Any(a => a.Side == TfwArmySide.Guard && a.Order == TfwArmyOrder.March && a.Goal == TfwBattle.LandingGoal && a.Count == 100);
        }
        Assert.Equal(TfwArmyRules.GarrisonCap, battle.Stationed(0));
        Assert.Equal(TfwArmyRules.GarrisonCap, most);
        Assert.True(overflowSeen, "the 100 over the cap went on (to the base: no other Imperial point)");
    }

    [Fact]
    public void Reinforcements_leave_every_minute_split_by_need()
    {
        var (sim, guards, battle, layout) = Frozen();
        Capture(sim, battle, layout, 0);
        Capture(sim, battle, layout, 1);
        int before = battle.ReinforcementsSent;
        Seconds(battle, guards, 61f, () => []);
        Assert.True(battle.ReinforcementsSent >= before + 2, "both points lack men: both get a share");
        foreach (var a in battle.ArmyList.Where(a => a.Side == TfwArmySide.Guard && a.Order == TfwArmyOrder.March))
            Assert.Equal(0, a.Count % GuardSim.SquadSize);
    }

    [Fact]
    public void A_destination_lost_in_transit_redirects_to_another_imperial_point_else_the_base()
    {
        var (sim, guards, battle, layout) = Frozen();
        Capture(sim, battle, layout, 0);
        Capture(sim, battle, layout, 1);
        var men = battle.ReinforceForTests(1, 50)!;
        Seconds(battle, guards, 3f, () => []);
        battle.LosePointForTests(1);
        Assert.Equal((0, TfwArmyOrder.March), (men.Goal, men.Order));
        battle.LosePointForTests(0);
        Assert.Equal(TfwBattle.LandingGoal, men.Goal);
        Assert.Equal(50, men.Count);                                      // nobody lost on the way
    }

    [Fact]
    public void A_wave_whose_destination_falls_goes_to_another_imperial_point()
    {
        var (sim, guards, battle, layout) = Frozen();
        for (int k = 0; k < 3; k++) Capture(sim, battle, layout, k);
        var wave = battle.SendWaveForTests(200)!;
        int goal = wave.Goal;
        Seconds(battle, guards, 2f, () => []);
        battle.LosePointForTests(goal);
        Assert.NotEqual(goal, wave.Goal);
        Assert.True(battle.Objectives[wave.Goal].State == TfwPositionState.Secured);
    }

    // ---------------------------------------------------------------- retreats

    [Fact]
    public void Orks_retreat_at_half_losses_only_when_no_leader_is_near_and_killing_the_leaders_breaks_the_charge()
    {
        var (_, _, battle, _) = Frozen();
        var lonely = new Vector3(100f, -100f, 0f);
        var orks = battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Attack, 0, 200, lonely, 0f, eliteShare: 0.1f)!;
        TfwBattle.LoseForTests(orks, 90);
        battle.StepRetreatsForTests();
        Assert.Equal(TfwArmyOrder.Attack, orks.Order);                   // 45 %: not yet
        TfwBattle.LoseForTests(orks, 20);
        battle.StepRetreatsForTests();
        Assert.True(orks.Led);
        Assert.Equal(TfwArmyOrder.Attack, orks.Order);                   // 55 % but its Nobs stand
        TfwBattle.LoseForTests(orks, 20, leadersFirst: true);
        Assert.False(orks.Led);
        battle.StepRetreatsForTests();
        Assert.Equal(TfwArmyOrder.Retreat, orks.Order);                  // the leaders fell: the charge breaks
        Assert.True(orks.Goal >= TfwBattle.StrongholdGoalFirst);
    }

    [Fact]
    public void Guardsmen_retreat_past_half_losses_and_when_their_point_falls()
    {
        var (sim, _, battle, layout) = Frozen();
        var lonely = new Vector3(100f, -100f, 0f);
        var men = battle.AddArmyForTests(TfwArmySide.Guard, TfwArmyOrder.March, 0, 100, lonely)!;
        TfwBattle.LoseForTests(men, 50);
        battle.StepRetreatsForTests();
        Assert.Equal(TfwArmyOrder.March, men.Order);                     // exactly half: not more than half
        TfwBattle.LoseForTests(men, 1);
        battle.StepRetreatsForTests();
        Assert.Equal((TfwArmyOrder.Retreat, TfwBattle.LandingGoal), (men.Order, men.Goal));

        Capture(sim, battle, layout, 1);
        var garrison = battle.AddArmyForTests(TfwArmySide.Guard, TfwArmyOrder.Hold, 1, 80, layout.Positions[1])!;
        battle.LosePointForTests(1);
        Assert.Equal(TfwArmyOrder.Retreat, garrison.Order);
    }

    /// <summary>Losses on an army: its aggregate first, then its real men (the nearest squad's), as a fight would take them.</summary>
    static void Kill(GuardSim guards, TfwArmy a, int men)
    {
        int far = Math.Min(men, a.FarCount);
        TfwBattle.LoseForTests(a, far);
        for (int k = far; k < men; k++)
        {
            int s = a.Squads.FirstOrDefault(q => guards.AliveIn(q) > 0, -1);
            if (s < 0 || !guards.KillOne(s, Vector3.Zero)) break;
        }
    }

    [Fact]
    public void A_rally_comes_once_a_minute_at_most_and_a_rallied_army_holds_for_30_s()
    {
        var (sim, guards, battle, layout) = Frozen();
        Capture(sim, battle, layout, 2);
        var men = battle.AddArmyForTests(TfwArmySide.Guard, TfwArmyOrder.Hold, 2, 100, layout.LandingZone + new Vector3(0f, 20f, 0f))!;   // posted far from its point: the walks are long
        int rallies = 0, retreats = 0;
        battle.OnRally = a => { if (a == men) rallies++; };
        battle.OnRetreat = a => { if (a == men) retreats++; };
        Kill(guards, men, 60);
        battle.StepRetreatsForTests();
        Assert.Equal((TfwArmyOrder.Retreat, 1), (men.Order, retreats));
        Vector3[] Beside() => [men.Middle + new Vector3(6f, 0f, 0f)];
        Seconds(battle, guards, 1f, Beside);
        Assert.Equal((1, TfwArmyOrder.March, 2), (rallies, men.Order, men.Goal));
        Assert.Equal(40, men.Start);                                       // its losses count from the rally
        Kill(guards, men, 25);                                             // 62 % of what rallied
        Seconds(battle, guards, 25f, Beside);
        Assert.Equal((1, TfwArmyOrder.March), (retreats, men.Order));      // the 30 s window holds it
        Seconds(battle, guards, 6f, Beside);
        Assert.Equal((2, TfwArmyOrder.Retreat), (retreats, men.Order));
        Seconds(battle, guards, 20f, Beside);                              // a human still beside it
        Assert.Equal(1, rallies);                                          // the minute's lock: no rally/retreat loop
        Seconds(battle, guards, 10f, Beside);
        Assert.Equal(2, rallies);                                          // a minute after the first
        Assert.True(retreats <= 2);
    }

    // ---------------------------------------------------------------- levels

    [Fact]
    public void Promotion_spawns_in_the_impostors_slots_and_neither_level_change_moves_the_count()
    {
        var (sim, guards, battle, layout) = Frozen();
        var anchor = new Vector3(80f, -60f, 0f);
        var orks = battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Hold, 0, 120, anchor, 0.3f, eliteShare: 0.1f)!;
        Vector3[] far = [new(-100f, 100f, 0f)], near = [anchor + new Vector3(0f, 60f, 0f)];
        battle.Step(far);
        Assert.Equal((0, 120), (orks.Real, orks.Count));
        int ticks = 0;
        while (orks.FarCount > 0 && ticks < 60)
        {
            battle.Step(near);
            ticks++;
            Assert.Equal(120, orks.Count);
            Assert.True(orks.Real <= TfwArmyRules.PromotePerTick * ticks);
        }
        Assert.Equal(0, orks.FarCount);
        Assert.InRange(ticks, 120 / TfwArmyRules.PromotePerTick, 120 / TfwArmyRules.PromotePerTick + 1);
        for (int j = 0; j < orks.Orks.Count; j++)
        {
            Assert.True(sim.TryGet(orks.Orks[j], out var e));
            var slot = TfwArmyLayout.Slot(TfwArmySide.Ork, orks.Width, orks.Anchor, orks.Yaw, orks.OrkSlots[j]);
            Assert.True(Vector3.Distance(e.Position, slot) < 0.01f, $"slot {orks.OrkSlots[j]} at {e.Position}, impostor at {slot}");
        }
        Assert.InRange(orks.RealElites, 10, 14);                            // its 12 leaders came real with it
        Vector3[] between = [anchor + new Vector3(0f, 100f + orks.Radius, 0f)];   // inside the hysteresis band: it stays real
        for (int t = 0; t < 10; t++) battle.Step(between);
        Assert.Equal(120, orks.Real);
        ticks = 0;
        while (orks.Real > 0 && ticks < 60) { battle.Step(far); ticks++; Assert.Equal(120, orks.Count); }
        Assert.Equal((0, 120), (orks.Real, orks.FarCount));
        Assert.InRange(ticks, 120 / TfwArmyRules.DemotePerTick, 120 / TfwArmyRules.DemotePerTick + 1);
    }

    [Fact]
    public void Promote_and_demote_cycles_never_push_the_slots_back()
    {
        var (_, _, battle, _) = Frozen();
        var anchor = new Vector3(80f, -60f, 0f);
        var orks = battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Hold, 0, 50, anchor)!;
        Vector3[] near = [anchor + new Vector3(0f, 40f, 0f)], far = [new(-100f, 100f, 0f)];
        for (int cycle = 0; cycle < 4; cycle++)
        {
            for (int t = 0; t < 8; t++) battle.Step(near);
            Assert.Equal((50, 0), (orks.Real, orks.FarCount));
            for (int t = 0; t < 8; t++) battle.Step(far);
            Assert.Equal((0, 50), (orks.Real, orks.FarCount));
            Assert.Equal(50, orks.Slots);
        }
    }

    [Fact]
    public void At_the_army_limit_no_wave_musters_and_nothing_breaks()
    {
        var (sim, guards, battle, layout) = Frozen();
        Capture(sim, battle, layout, 0);
        while (battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Hold, 1, 10, new Vector3(100f, 100f, 0f)) is not null) { }
        Assert.Equal(TfwArmyRules.MaxArmies, battle.ArmyList.Count);
        Assert.Null(battle.SendWaveForTests(200));
        Seconds(battle, guards, 200f, () => []);
        Assert.True(battle.ArmyList.Count <= TfwArmyRules.MaxArmies);
    }

    [Fact]
    public void Every_coop_human_brings_the_armies_near_him_and_an_ai_marine_brings_none()
    {
        var (_, _, battle, _) = Frozen();
        var a = battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Hold, 0, 30, new Vector3(-90f, -90f, 0f))!;
        var b = battle.AddArmyForTests(TfwArmySide.Guard, TfwArmyOrder.Hold, 1, 30, new Vector3(90f, 90f, 0f))!;
        battle.SetPlayerIds([1, 2, TfwContracts.PuppetIdFirst]);
        Vector3[] feet = [new(-90f, -40f, 0f), new(90f, 40f, 0f), new(-90f, -90f, 0f)];
        for (int t = 0; t < 40; t++) battle.Step(feet);
        Assert.Equal((30, 30), (a.Real, b.Real));
        Assert.Equal(2, battle.Humans.Count);                               // the puppet is no human
        feet = [new(-90f, -40f, 0f), new(-90f, 40f, 0f), new(90f, 90f, 0f)];   // the second human walks off; an AI Marine stands on b
        for (int t = 0; t < 40; t++) battle.Step(feet);
        Assert.Equal((30, 0, 30), (a.Real, b.Real, b.Count));
    }

    [Fact]
    public void Real_army_orks_live_outside_the_crowds_pool_and_cap()
    {
        Assert.Equal(TfwBattleRules.CrowdPool - TfwBattleRules.PoolReserve, TfwBattleRules.CrowdPoolRoom);
        Assert.True(TfwBattleRules.CrowdPool + TfwArmyRules.NearOrksMost <= MusouSim.Capacity);
        Assert.True(TfwArmyRules.NearSquadsMost + TfwBattleRules.FinalFieldSquads <= GuardSim.MaxSquads);
        Assert.True(GuardSim.Capacity <= TfwWire.GuardCapacity);
    }

    // ---------------------------------------------------------------- the far clash on a point

    [Fact]
    public void A_far_wave_and_a_far_garrison_clash_until_one_breaks_and_the_point_falls_only_with_the_imperials_gone()
    {
        var (sim, guards, battle, layout) = Frozen();
        Capture(sim, battle, layout, 1);
        var garrison = battle.AddArmyForTests(TfwArmySide.Guard, TfwArmyOrder.Hold, 1, 40, layout.Positions[1])!;   // with the field's 60 real men, 100 (a Guardsman worth an Ork)
        var wave = battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Attack, 1, 200, layout.Positions[1])!;
        for (int t = 0; t < 240 * Tick && battle.Objectives[1].State == TfwPositionState.Secured; t++) battle.Step([]);
        Assert.Equal(TfwArmyOrder.Retreat, garrison.Order);                 // outweighed two to one, it broke
        Assert.Equal(TfwPositionState.Held, battle.Objectives[1].State);    // then the Orks alone took the point
        Assert.True(wave.Count > 50, $"the wave kept {wave.Count}");
        Assert.Equal(TfwArmyOrder.Hold, wave.Order);                        // and hold it
        Assert.True(battle.OrksKilled > 0);
    }

    // ---------------------------------------------------------------- the Defenders and the ids

    [Fact]
    public void Twelve_puppet_ids_and_the_world_never_passes_16_players()
    {
        Assert.Equal(12, TfwContracts.PuppetIds);
        Assert.Equal(65527, TfwContracts.PuppetIdLast);
        Assert.Equal(TfwContracts.PuppetIdLast + 3, TfwContracts.PointTargetId(0));   // the point targets stay where they were
        Assert.True(DefenderRules.HumansMost + TfwContracts.PuppetIds <= Protocol.MaxPlayers);
        Assert.Equal(6, DefenderRules.For(0));
        Assert.Equal(6, DefenderRules.For(1));
        Assert.Equal(2, DefenderRules.For(3));
        Assert.Equal(TfwContracts.PuppetIdFirst, GameServer.ReservedPlayerIdFirst);
    }

    [Fact]
    public void Six_defenders_land_act_alone_never_capture_and_come_back_30_s_after_a_death()
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        var run = w.Tfw!;
        var defenders = run.Marines.Where(m => m.Role == MarineRole.Defender).ToList();
        Assert.Equal(6, defenders.Count);
        Assert.True(w.Players.Count <= Protocol.MaxPlayers);
        Assert.All(defenders, d => Assert.True(TfwContracts.IsPuppet(d.Id)));
        TfwRig.Seconds(w, 1.1f);
        Assert.All(defenders, d => Assert.NotNull(d.AttackAt));               // a post each, no order needed
        var one = defenders[0];
        Assert.True(w.TfwKillForTests(one.Id));
        TfwRig.Seconds(w, DefenderRules.RespawnSeconds - 0.5f);
        Assert.False(w.Players[one.Id].Alive);
        TfwRig.Seconds(w, 1f);
        Assert.True(w.Players[one.Id].Alive);
        Assert.True(Vector3.Distance(w.Players[one.Id].Position with { Z = 0f }, battle.Layout.LandingZone with { Z = 0f }) < 8f);   // back at the landing zone (half a second on his way)
    }

    [Fact]
    public void A_defender_on_a_held_point_adds_no_capture_progress()
    {
        var (_, _, battle, layout) = Frozen();
        battle.SetPlayerIds([TfwContracts.PuppetIdFirst]);
        battle.SetDefenders([TfwContracts.PuppetIdFirst]);
        for (int t = 0; t < 5 * Tick; t++) battle.Step([layout.Positions[0]]);
        Assert.Equal(0f, battle.Objectives[0].Progress);
    }

    // ---------------------------------------------------------------- tag 18

    [Fact]
    public void Tag_18_round_trips_and_refuses_the_malformed()
    {
        TfwArmyView[] views =
        [
            new(1, TfwArmySide.Ork, TfwArmyOrder.Attack, true, 950, 900, 40, 120, new Vector3(-120.3f, 55.7f, 3.2f), 1.5f, 0.6f, 36),
            new(2, TfwArmySide.Guard, TfwArmyOrder.Hold, false, 200, 0, 200, 0, new Vector3(10f, -10f, 0f), -2f, 1f, 15),
        ];
        var bytes = TfwArmyWire.Write(views);
        Assert.Equal(1 + 2 * TfwArmyWire.RecordBytes, bytes.Length);
        var back = TfwArmyWire.Read(bytes)!;
        Assert.Equal(2, back.Length);
        Assert.Equal((views[0].Id, views[0].Side, views[0].Order, views[0].Led, views[0].Count, views[0].Far, views[0].SlotBase, views[0].Elites, views[0].Width),
            (back[0].Id, back[0].Side, back[0].Order, back[0].Led, back[0].Count, back[0].Far, back[0].SlotBase, back[0].Elites, back[0].Width));
        Assert.True(Vector3.Distance(views[0].Anchor, back[0].Anchor) <= 0.06f);
        Assert.InRange(MathF.Abs(MathF.IEEERemainder(back[1].Yaw - views[1].Yaw, MathF.Tau)), 0f, MathF.Tau / 256f);
        Assert.True(TfwWire.Owns(TfwWire.ArmiesTag));
        Assert.True(1 + TfwArmyWire.MaxArmies * TfwArmyWire.RecordBytes <= Protocol.MaxExtrasPayloadBytes);
        var bad = (byte[])bytes.Clone(); bad[1 + TfwArmyWire.RecordBytes] = 1;   // a repeated id
        Assert.Null(TfwArmyWire.Read(bad));
        bad = (byte[])bytes.Clone(); bad[5] = 0xFF; bad[6] = 0xFF;               // more far than the count
        Assert.Null(TfwArmyWire.Read(bad));
        Assert.Null(TfwArmyWire.Read(bytes.AsSpan(0, bytes.Length - 1)));
    }
}
